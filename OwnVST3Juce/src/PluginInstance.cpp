#include "PluginInstance.h"

#include <thread>

#if defined(_WIN32)
    #define NOMINMAX
    #include <windows.h>
#endif

/* ────────────────────────────────────────────────────────────────────────────
 * JUCE one-time initialisation
 *
 * Windows / Linux  – a dedicated JuceMessageThread calls initialiseJuce_GUI()
 *   inside its run() so that thread becomes the JUCE message thread.  The
 *   Win32 message pump runs there to satisfy COM STA and plugin WndProc
 *   requirements without touching the .NET host process main thread.
 *
 * macOS – initialiseJuce_GUI() is called on the calling thread.  JUCE on
 *   macOS dispatches all Cocoa operations through GCD to the NSApplication
 *   main run loop, which IS the Avalonia UI thread.  No separate JUCE message
 *   thread is required.
 * ────────────────────────────────────────────────────────────────────────────*/

#if !defined(__APPLE__)

class JuceMessageThread final : public juce::Thread
{
public:
    JuceMessageThread() : juce::Thread("JUCE Message Thread") {}

    void run() override
    {
#if defined(_WIN32)
        OleInitialize(nullptr);
#endif
        juce::initialiseJuce_GUI();
        _ready.store(true, std::memory_order_release);

        juce::MessageManager::getInstance()->runDispatchLoop();

#if defined(_WIN32)
        OleUninitialize();
#endif
        juce::shutdownJuce_GUI();
    }

    /** Blocks until JUCE initialisation is complete on the message thread. */
    void waitUntilReady() const noexcept
    {
        while (!_ready.load(std::memory_order_acquire))
            juce::Thread::sleep(1);
    }

private:
    std::atomic<bool> _ready { false };
};

static std::unique_ptr<JuceMessageThread> s_messageThread;
#endif // !__APPLE__

static std::once_flag s_juceInitFlag;

static void initJuceOnce()
{
#if defined(__APPLE__)
    juce::initialiseJuce_GUI();
    // shutdownJuce_GUI() is intentionally not called via atexit: during .NET
    // process teardown the runtime has already freed Objective-C objects that
    // JUCE's DeletedAtShutdown list still holds, causing SIGABRT in deleteAll().
    // The OS reclaims all resources on process exit.
#else
#if defined(_WIN32)
    // JUCE derives its Win32 window class name from the module HINSTANCE it
    // receives via Process::getCurrentModuleInstanceHandle(), which defaults to
    // GetModuleHandle(nullptr) — the host executable's HINSTANCE.  JUCE-based
    // plugins (e.g. TDR Nova) compiled with their own JUCE copy do the same,
    // producing an identical class name.  The second RegisterClassEx call gets
    // ERROR_CLASS_ALREADY_EXISTS and the plugin ends up using the host's WndProc,
    // leading to an immediate crash.
    //
    // Fix: point our JUCE at ownvst3.dll's own HINSTANCE so the host and each
    // plugin get distinct class names and independent WndProcs.
    {
        HMODULE ownModule = nullptr;
        if (GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&initJuceOnce),
                &ownModule) && ownModule != nullptr)
        {
            juce::Process::setCurrentModuleInstanceHandle(ownModule);
        }
    }
#endif
    s_messageThread = std::make_unique<JuceMessageThread>();
    s_messageThread->startThread(juce::Thread::Priority::high);
    s_messageThread->waitUntilReady();

    std::atexit([]()
    {
        if (s_messageThread)
        {
            juce::MessageManager::getInstance()->stopDispatchLoop();
            s_messageThread->stopThread(3000);
            s_messageThread.reset();
        }
    });
#endif
}

void ownvst3_ensureJuceInitialised()
{
    std::call_once(s_juceInitFlag, initJuceOnce);
}

/* ────────────────────────────────────────────────────────────────────────────
 * PluginPlayHead – feeds transport info to the plugin on the audio thread
 * ────────────────────────────────────────────────────────────────────────────*/

juce::Optional<juce::AudioPlayHead::PositionInfo>
PluginInstance::PluginPlayHead::getPosition() const
{
    juce::AudioPlayHead::PositionInfo info;

    info.setBpm        (_owner._bpm.load(std::memory_order_relaxed));
    info.setIsPlaying  (_owner._playing.load(std::memory_order_relaxed));
    info.setIsLooping  (false);
    info.setIsRecording(false);

    const int64_t pos = _owner._samplePos.load(std::memory_order_relaxed);
    info.setTimeInSamples(pos);
    info.setTimeInSeconds(static_cast<double>(pos) / _owner._sampleRate);
    info.setTimeSignature(juce::AudioPlayHead::TimeSignature{ 4, 4 });

    return info;
}

/* ────────────────────────────────────────────────────────────────────────────
 * PluginInstance – construction / destruction
 * ────────────────────────────────────────────────────────────────────────────*/

PluginInstance::PluginInstance()
    : _playHead(*this)
{
    // JUCE is initialised at most once per process lifetime.
    std::call_once(s_juceInitFlag, initJuceOnce);

    _formatManager.addDefaultFormats();
}

PluginInstance::~PluginInstance()
{
    _disposed.store(true, std::memory_order_release);

    if (_editorWindow)
    {
        juce::MessageManager::getInstance()->callFunctionOnMessageThread(
            [](void* ctx) -> void*
            {
                static_cast<PluginInstance*>(ctx)->_editorWindow.reset();
                return nullptr;
            },
            this);
    }
}

/* ── Loading ─────────────────────────────────────────────────────────────── */

// Actual scan + instantiation logic, separated so the __try/__except wrapper
// below does not share a scope with C++ objects that have non-trivial dtors.
bool PluginInstance::loadPluginBody(PluginInstance* self, const char* path)
{
    const juce::String pluginPath = juce::String::fromUTF8(path);
    juce::KnownPluginList pluginList;

    for (int i = 0; i < self->_formatManager.getNumFormats(); ++i)
    {
        auto* fmt = self->_formatManager.getFormat(i);
        if (!fmt->fileMightContainThisPluginType(pluginPath))
            continue;

        juce::OwnedArray<juce::PluginDescription> found;
        pluginList.scanAndAddFile(pluginPath, false, found, *fmt);

        if (!found.isEmpty())
        {
            // _subIndex is 0 unless loadPluginAt() set it, so the VST3 path is unchanged.
            const int idx = juce::isPositiveAndBelow(self->_subIndex, found.size())
                          ? self->_subIndex : 0;

            juce::String errorMsg;
            self->_plugin = self->_formatManager.createPluginInstance(
                *found[idx], self->_sampleRate, self->_blockSize, errorMsg);

            self->_identifier = found[idx]->fileOrIdentifier.toStdString();
            break;
        }
    }

    if (!self->_plugin)
        return false;

    self->_plugin->setPlayHead(&self->_playHead);
    self->buildParameterMap();
    return true;
}

bool PluginInstance::loadPlugin(const char* path)
{
    if (!path || _disposed.load(std::memory_order_relaxed))
        return false;

#if defined(_WIN32)
    // Windows: run entirely on the JUCE message thread.
    // JUCE-based plugins (e.g. TDR Nova) register a Win32 window class via
    // RegisterClassEx during DLL load.  Both the host JUCE and the plugin JUCE
    // derive the class name from their HINSTANCE; if both use the same HINSTANCE
    // the second RegisterClassEx returns ERROR_CLASS_ALREADY_EXISTS and the plugin
    // ends up with the host's WndProc — causing an immediate crash.
    // Running on the JUCE message thread ensures the host JUCE infrastructure is
    // already up and the plugin shares the existing message loop safely.
    // The SEH guard catches access violations from misbehaving plugin DllMains.
    struct Ctx { PluginInstance* self; const char* path; bool result; };
    Ctx ctx{ this, path, false };

    juce::MessageManager::getInstance()->callFunctionOnMessageThread(
        [](void* raw) -> void*
        {
            auto& c = *static_cast<Ctx*>(raw);
            __try
            {
                c.result = loadPluginBody(c.self, c.path);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                c.result = false;
            }
            // hasEditor() calls IEditController::createView() internally; must run
            // on the JUCE message thread — already satisfied here on Windows.
            if (c.result && c.self->_plugin)
                c.self->_hasEditor = c.self->_plugin->hasEditor();
            return nullptr;
        },
        &ctx);

    return ctx.result;
#else
    // macOS / Linux: run scan + instantiation on the calling thread.
    // callFunctionOnMessageThread is intentionally NOT used for loadPluginBody:
    //   - On macOS the JUCE message thread is the Avalonia UI thread.  Blocking
    //     the message thread for the full scan + instantiation duration degrades
    //     UI responsiveness and risks deadlock if the host awaits the result from
    //     the same thread.
    //   - On Linux the JuceMessageThread is always running, but loadPluginBody
    //     must not run there to avoid thread-affinity distortion with processBlock.
    if (!loadPluginBody(this, path))
        return false;

    if (_plugin)
    {
#if defined(__APPLE__)
        // hasEditor() calls IEditController::createView() + releaseView() internally
        // to probe whether the plugin provides an editor.  IK Multimedia products
        // (and other JUCE-based VST3 plugins) register and unregister CFRunLoop
        // Source0 callbacks during this sequence.  If hasEditor() runs on a
        // background thread, the AppKit RunLoop on the main thread can service those
        // Source0 callbacks while the plugin's internal editor object is mid-teardown,
        // calling a pure virtual function on a partially-freed vtable and crashing
        // with SIGABRT (__cxa_pure_virtual).
        // Fix: dispatch hasEditor() to the JUCE message thread (= NSApplication main
        // thread on macOS), matching the Windows path above.
        juce::MessageManager::getInstance()->callFunctionOnMessageThread(
            [](void* raw) -> void*
            {
                auto* self = static_cast<PluginInstance*>(raw);
                self->_hasEditor = self->_plugin->hasEditor();
                return nullptr;
            },
            this);
#else
        _hasEditor = _plugin->hasEditor();
#endif
    }
    return true;
#endif
}

bool PluginInstance::loadPluginAt(const char* path, int subIndex)
{
    _subIndex = subIndex > 0 ? subIndex : 0;
    return loadPlugin(path);
}

bool PluginInstance::initialize(double sampleRate, int blockSize)
{
    if (!_plugin || blockSize <= 0) return false;

    // setSize() below frees the buffer processAudio() reads from, so park the audio
    // thread on the flag and wait out whatever block is already in flight.
    _reconfiguring.store(true, std::memory_order_release);
    while (_inProcess.load(std::memory_order_acquire) != 0)
        std::this_thread::yield();

    _sampleRate = sampleRate;
    _blockSize  = blockSize;

    _plugin->prepareToPlay(sampleRate, blockSize);

    const int channels = std::max(
        _plugin->getTotalNumInputChannels(),
        _plugin->getTotalNumOutputChannels());

    // Pre-allocate once so processAudio() never heap-allocates.
    _juceBuffer.setSize(std::max(channels, 1), blockSize, false, true, false);
    _midiBuffer.ensureSize(static_cast<size_t>(blockSize));

    _reconfiguring.store(false, std::memory_order_release);
    return true;
}

/* ── Parameter map ───────────────────────────────────────────────────────── */

void PluginInstance::buildParameterMap()
{
    _paramPtrs.clear();
    _indexToParamId.clear();
    _paramIdToIndex.clear();

    if (!_plugin) return;

    const auto& params = _plugin->getParameters();
    const int   count  = static_cast<int>(params.size());

    _paramPtrs.resize(static_cast<size_t>(count));
    _indexToParamId.resize(static_cast<size_t>(count));

    for (int i = 0; i < count; ++i)
    {
        _paramPtrs[static_cast<size_t>(i)]      = params[i];
        _indexToParamId[static_cast<size_t>(i)] = i;
        _paramIdToIndex[i]                      = i;
    }
}

/* ── Metadata ────────────────────────────────────────────────────────────── */

const char* PluginInstance::getName()
{
    if (!_plugin) return "";
    return _strings.store("name", _plugin->getName().toStdString());
}

const char* PluginInstance::getVendor()
{
    if (!_plugin) return "";
    juce::PluginDescription desc;
    _plugin->fillInPluginDescription(desc);
    return _strings.store("vendor", desc.manufacturerName.toStdString());
}

const char* PluginInstance::getVersion()
{
    if (!_plugin) return "";
    juce::PluginDescription desc;
    _plugin->fillInPluginDescription(desc);
    return _strings.store("version", desc.version.toStdString());
}

const char* PluginInstance::getPluginInfo()
{
    if (!_plugin) return "";
    juce::PluginDescription desc;
    _plugin->fillInPluginDescription(desc);
    const std::string info =
        _plugin->getName().toStdString() + " | " +
        desc.manufacturerName.toStdString()  + " | " +
        desc.version.toStdString();
    return _strings.store("info", info);
}

const char* PluginInstance::getFormatName()
{
    if (!_plugin) return "";
    juce::PluginDescription desc;
    _plugin->fillInPluginDescription(desc);
    return _strings.store("format", desc.pluginFormatName.toStdString());
}

const char* PluginInstance::getIdentifier()
{
    return _identifier.c_str();
}

bool PluginInstance::isInstrument() const
{
    if (!_plugin) return false;
    return _plugin->acceptsMidi()
        && _plugin->getTotalNumInputChannels()  == 0
        && _plugin->getTotalNumOutputChannels()  > 0;
}

bool PluginInstance::isEffect() const
{
    if (!_plugin) return false;
    return _plugin->getTotalNumInputChannels()  > 0
        && _plugin->getTotalNumOutputChannels() > 0;
}

bool PluginInstance::isMidiOnly() const
{
    if (!_plugin) return false;
    return _plugin->acceptsMidi()
        && _plugin->getTotalNumOutputChannels() == 0;
}

int PluginInstance::getActualInputChannels() const
{
    return _plugin ? _plugin->getTotalNumInputChannels() : 0;
}

int PluginInstance::getActualOutputChannels() const
{
    return _plugin ? _plugin->getTotalNumOutputChannels() : 0;
}

int PluginInstance::getLatencySamples() const
{
    // The plugin reports its processing latency (per channel, in samples) after
    // prepareToPlay(). Zero until initialized or for a zero-latency plugin.
    return _plugin ? _plugin->getLatencySamples() : 0;
}

/* ── Parameters ──────────────────────────────────────────────────────────── */

int PluginInstance::getParameterCount() const
{
    return static_cast<int>(_paramPtrs.size());
}

bool PluginInstance::getParameterAt(int index, VST3ParameterC* outParam)
{
    if (!outParam || index < 0 || index >= static_cast<int>(_paramPtrs.size()))
        return false;

    auto* p = _paramPtrs[static_cast<size_t>(index)];
    if (!p) return false;

    outParam->id           = index;
    outParam->name         = _strings.store("param_" + std::to_string(index),
                                            p->getName(128).toStdString());
    outParam->minValue     = 0.0;
    outParam->maxValue     = 1.0;
    outParam->defaultValue = static_cast<double>(p->getDefaultValue());
    outParam->currentValue = static_cast<double>(p->getValue());

    return true;
}

void PluginInstance::setParameter(int paramId, double value)
{
    _paramQueue.tryEnqueue(ParamChange{ paramId, static_cast<float>(value) });
}

double PluginInstance::getParameter(int paramId) const
{
    if (paramId < 0 || paramId >= static_cast<int>(_paramPtrs.size()))
        return 0.0;
    const auto* p = _paramPtrs[static_cast<size_t>(paramId)];
    return p ? static_cast<double>(p->getValue()) : 0.0;
}

/* ── Audio processing (audio thread – no heap allocation) ───────────────── */

bool PluginInstance::processAudio(float** inputs,  int numIn,
                                  float** outputs, int numOut,
                                  int numSamples)
{
    if (_disposed.load(std::memory_order_relaxed)) return false;
    if (!_plugin)                                   return false;

    if (_reconfiguring.load(std::memory_order_acquire))
        return false;

    _inProcess.fetch_add(1, std::memory_order_acq_rel);

    const bool ok = _reconfiguring.load(std::memory_order_acquire)
                  ? false
                  : processAudioBody(inputs, numIn, outputs, numOut, numSamples);

    _inProcess.fetch_sub(1, std::memory_order_release);
    return ok;
}

bool PluginInstance::processAudioBody(float** inputs,  int numIn,
                                      float** outputs, int numOut,
                                      int numSamples)
{
    // Wider than initialize() prepared for would malloc below and overrun what
    // prepareToPlay() promised the plugin. Refusing it is the only RT-safe answer.
    if (numSamples <= 0 || numSamples > _blockSize)
        return false;

    drainParamQueue();

    const int pluginIn  = _plugin->getTotalNumInputChannels();
    const int pluginOut = _plugin->getTotalNumOutputChannels();
    const int buffered  = _juceBuffer.getNumChannels();

    // The two sides are counted apart, the same way initialize() sized the buffer to the
    // wider bus. An instrument has no input bus at all, and folding both into one minimum
    // made that a zero: processBlock still rendered, but nothing was copied back out and
    // the silent input was passed over the top of it. Every instrument came back mute.
    const int inChannels  = std::min({ numIn,  pluginIn,  buffered });
    const int outChannels = std::min({ numOut, pluginOut, buffered });

    // Clamp _juceBuffer to exactly numSamples without heap allocation.
    // _juceBuffer was pre-allocated for maxBlockSize in initialize(), but the host
    // may send smaller blocks (e.g. maxBlockSize=4096, actual=1024). Without this,
    // processBlock sees the full 4096-sample buffer: the tail [numSamples..maxBlockSize-1]
    // contains leftover output from the previous call (not real input), which drives
    // filter state and delay lines to near-zero. On the next block the DSP restarts
    // from wrong state, causing a discontinuity every block (~43 Hz) that sounds like
    // buzzing or distortion — only audible after parameters activate the DSP.
    _juceBuffer.setSize(_juceBuffer.getNumChannels(), numSamples,
                        /*keepExistingContent=*/false,
                        /*clearExtraSpace=*/false,
                        /*avoidReallocating=*/true);

    // Copy C#-pinned input into the pre-allocated JUCE buffer. Whatever the plugin does
    // not take input for is cleared, so an instrument starts every block from silence.
    for (int ch = 0; ch < inChannels; ++ch)
    {
        if (inputs[ch])
            std::memcpy(_juceBuffer.getWritePointer(ch),
                        inputs[ch],
                        static_cast<size_t>(numSamples) * sizeof(float));
        else
            _juceBuffer.clear(ch, 0, numSamples);
    }
    for (int ch = inChannels; ch < buffered; ++ch)
        _juceBuffer.clear(ch, 0, numSamples);

    // When bypassed, JUCE's processBlockBypassed() passes the input through
    // delayed by the plugin's own latency, so toggling bypass never shifts the
    // output in time relative to the processed (active) path. The plugin is still
    // driven every block, so it never goes cold.
    if (_bypassed.load(std::memory_order_relaxed))
        _plugin->processBlockBypassed(_juceBuffer, _midiBuffer);
    else
        _plugin->processBlock(_juceBuffer, _midiBuffer);

    // Copy processed output back to the C# buffers.
    for (int ch = 0; ch < outChannels; ++ch)
    {
        if (outputs[ch])
            std::memcpy(outputs[ch],
                        _juceBuffer.getReadPointer(ch),
                        static_cast<size_t>(numSamples) * sizeof(float));
    }

    // Pass-through for channels beyond what the plugin processed.
    for (int ch = outChannels; ch < std::min(numIn, numOut); ++ch)
    {
        if (inputs[ch] && outputs[ch])
            std::memcpy(outputs[ch], inputs[ch],
                        static_cast<size_t>(numSamples) * sizeof(float));
    }

    if (_playing.load(std::memory_order_relaxed))
        _samplePos.fetch_add(numSamples, std::memory_order_relaxed);

    _midiBuffer.clear();
    return true;
}

bool PluginInstance::processMidi(const MidiEventC* events, int count)
{
    if (!_plugin || !events || count <= 0) return false;

    for (int i = 0; i < count; ++i)
    {
        const auto& ev = events[i];
        juce::MidiMessage msg(
            static_cast<int>(ev.status),
            static_cast<int>(ev.data1),
            static_cast<int>(ev.data2));
        _midiBuffer.addEvent(msg, ev.sampleOffset);
    }

    return true;
}

/* ── Parameter queue drain ───────────────────────────────────────────────── */

void PluginInstance::drainParamQueue()
{
    ParamChange c;
    while (_paramQueue.tryDequeue(c))
    {
        const auto idx = static_cast<size_t>(c.index);
        if (c.index >= 0 && idx < _paramPtrs.size() && _paramPtrs[idx])
            _paramPtrs[idx]->setValue(c.value);
    }
}

/* ── Transport ───────────────────────────────────────────────────────────── */

void PluginInstance::setTempo(double bpm)
{
    _bpm.store(bpm, std::memory_order_relaxed);
}

void PluginInstance::setTransportState(bool playing)
{
    _playing.store(playing, std::memory_order_relaxed);
}

void PluginInstance::resetTransportPosition()
{
    _samplePos.store(0, std::memory_order_relaxed);
}

void PluginInstance::setBypass(bool bypassed)
{
    // A plain atomic store — the audio thread reads it at the top of the next
    // processAudio() block. No SPSC round-trip is needed for a bypass toggle.
    _bypassed.store(bypassed, std::memory_order_relaxed);
}

/* ── Editor (UI / message thread) ───────────────────────────────────────── */

bool PluginInstance::createEditor(void* ownerWindowHandle)
{
    if (_disposed.load(std::memory_order_relaxed)) return false;
    if (!_plugin || !_hasEditor)                   return false;
    if (_editorWindow)                             return true;

    struct Ctx { PluginInstance* self; void* owner; bool result; };
    Ctx ctx{ this, ownerWindowHandle, false };

    juce::MessageManager::getInstance()->callFunctionOnMessageThread(
        [](void* raw) -> void*
        {
            auto& c = *static_cast<Ctx*>(raw);
            auto* editor = c.self->_plugin->createEditorIfNeeded();
            if (!editor) return nullptr;

            c.self->_editorWindow = std::make_unique<EditorWindow>(
                editor,
                juce::String::fromUTF8(
                    c.self->_plugin->getName().toStdString().c_str()),
                [ptr = c.self]() { /* user closed – window becomes invisible */ (void)ptr; });

#if defined(_WIN32)
            if (c.owner)
            {
                auto* juceHwnd = static_cast<HWND>(
                    c.self->_editorWindow->getWindowHandle());
                if (juceHwnd)
                    ::SetWindowLongPtr(juceHwnd, GWLP_HWNDPARENT,
                                       reinterpret_cast<LONG_PTR>(c.owner));
            }
#endif
            c.result = true;
            return nullptr;
        },
        &ctx);

    return ctx.result;
}

void PluginInstance::closeEditor()
{
    if (!_editorWindow) return;

    juce::MessageManager::getInstance()->callFunctionOnMessageThread(
        [](void* raw) -> void*
        {
            static_cast<PluginInstance*>(raw)->_editorWindow.reset();
            return nullptr;
        },
        this);
}

void PluginInstance::resizeEditor(int width, int height)
{
    if (!_editorWindow) return;

    struct Ctx { PluginInstance* self; int w; int h; };
    Ctx ctx{ this, width, height };

    juce::MessageManager::getInstance()->callFunctionOnMessageThread(
        [](void* raw) -> void*
        {
            const auto& c = *static_cast<Ctx*>(raw);
            if (c.self->_editorWindow)
                c.self->_editorWindow->resizeTo(c.w, c.h);
            return nullptr;
        },
        &ctx);
}

bool PluginInstance::hasEditor() const
{
    return _hasEditor;
}

bool PluginInstance::getEditorSize(int& width, int& height)
{
    if (!_plugin) return false;

    if (_editorWindow)
        return _editorWindow->getContentSize(width, height);

    // Do NOT create a temporary editor here: plugins that use OpenGL, background
    // threads, or native window attachment during construction (e.g. TDR Nova)
    // crash or deadlock when the component is created without a real native peer
    // and then immediately deleted.  The caller should use hasEditor() to decide
    // whether to show an open-editor action, and read the actual size from this
    // function once the editor is open.
    return false;
}

bool PluginInstance::isEditorOpen() const
{
    return _editorWindow && _editorWindow->isWindowOpen();
}

void PluginInstance::processIdle()
{
    if (_editorWindow)
        _editorWindow->runIdle();

#if defined(__APPLE__)
    // Pump a zero-duration slice of the JUCE message queue.
    // The C# caller invokes this from the Avalonia UI thread, which IS the
    // NSApplication main thread – the only thread allowed to service NSRunLoop.
    juce::MessageManager::getInstance()->runDispatchLoopUntil(0);
#endif
}

/* ── State serialisation ─────────────────────────────────────────────────── */

bool PluginInstance::getState(uint8_t** outData, int* outLength)
{
    if (!_plugin || !outData || !outLength) return false;

    juce::MemoryBlock state;
    _plugin->getStateInformation(state);

    if (state.isEmpty()) return false;

    *outLength = static_cast<int>(state.getSize());
    *outData   = new uint8_t[static_cast<size_t>(*outLength)];
    std::memcpy(*outData, state.getData(), state.getSize());

    return true;
}

bool PluginInstance::setState(const uint8_t* data, int length)
{
    if (!_plugin || !data || length <= 0) return false;
    _plugin->setStateInformation(data, length);
    return true;
}

void PluginInstance::clearStringCache()
{
    _strings.clear();
}
