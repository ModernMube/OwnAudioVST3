#include "PluginInstance.h"

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
            juce::String errorMsg;
            self->_plugin = self->_formatManager.createPluginInstance(
                *found[0], self->_sampleRate, self->_blockSize, errorMsg);
            break;
        }
    }

    if (!self->_plugin)
        return false;

    self->_plugin->setPlayHead(&self->_playHead);
    self->buildParameterMap();
    // Cache hasEditor on the message thread — calling _plugin->hasEditor() from
    // the plugin thread crashes JUCE-based plugins (e.g. TDR Nova) because
    // VST3PluginInstance::hasEditor() internally calls IEditController::createView(),
    // which those plugins expect to run on the JUCE message thread.
    self->_hasEditor = self->_plugin->hasEditor();
    return true;
}

bool PluginInstance::loadPlugin(const char* path)
{
    if (!path || _disposed.load(std::memory_order_relaxed))
        return false;

    // Scan and instantiate on the JUCE message thread so that JUCE-based plugins
    // that call initialiseJuce_GUI() during DLL load do so on the correct thread.
    // An SEH guard inside the lambda prevents a misbehaving plugin from crashing
    // the host process; instead loadPlugin() returns false and the caller can
    // report a graceful error.
    struct Ctx { PluginInstance* self; const char* path; bool result; };
    Ctx ctx{ this, path, false };

    juce::MessageManager::getInstance()->callFunctionOnMessageThread(
        [](void* raw) -> void*
        {
            auto& c = *static_cast<Ctx*>(raw);
#if defined(_WIN32)
            __try
            {
                c.result = loadPluginBody(c.self, c.path);
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                c.result = false;
            }
#else
            c.result = loadPluginBody(c.self, c.path);
#endif
            return nullptr;
        },
        &ctx);

    return ctx.result;
}

bool PluginInstance::initialize(double sampleRate, int blockSize)
{
    if (!_plugin) return false;

    _sampleRate = sampleRate;
    _blockSize  = blockSize;

    _plugin->prepareToPlay(sampleRate, blockSize);

    const int channels = std::max(
        _plugin->getTotalNumInputChannels(),
        _plugin->getTotalNumOutputChannels());

    // Pre-allocate once so processAudio() never heap-allocates.
    _juceBuffer.setSize(std::max(channels, 1), blockSize, false, true, false);
    _midiBuffer.ensureSize(static_cast<size_t>(blockSize));

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
    StateChange c;
    c.kind      = StateChangeKind::Parameter;
    c.intArg    = paramId;
    c.doubleArg = value;
    _stateQueue.tryEnqueue(c);
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

    drainStateQueue();

    const int pluginIn  = _plugin->getTotalNumInputChannels();
    const int pluginOut = _plugin->getTotalNumOutputChannels();
    const int channels  = std::min({ numIn, numOut, pluginIn, pluginOut,
                                     _juceBuffer.getNumChannels() });

    // Copy C#-pinned input into the pre-allocated JUCE buffer.
    for (int ch = 0; ch < channels; ++ch)
    {
        if (ch < numIn && inputs[ch])
            std::memcpy(_juceBuffer.getWritePointer(ch),
                        inputs[ch],
                        static_cast<size_t>(numSamples) * sizeof(float));
        else
            _juceBuffer.clear(ch, 0, numSamples);
    }
    for (int ch = channels; ch < _juceBuffer.getNumChannels(); ++ch)
        _juceBuffer.clear(ch, 0, numSamples);

    _plugin->processBlock(_juceBuffer, _midiBuffer);

    // Copy processed output back to the C# buffers.
    for (int ch = 0; ch < channels; ++ch)
    {
        if (ch < numOut && outputs[ch])
            std::memcpy(outputs[ch],
                        _juceBuffer.getReadPointer(ch),
                        static_cast<size_t>(numSamples) * sizeof(float));
    }

    // Pass-through for channels beyond what the plugin processed.
    for (int ch = channels; ch < std::min(numIn, numOut); ++ch)
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

/* ── State queue drain ───────────────────────────────────────────────────── */

void PluginInstance::drainStateQueue()
{
    StateChange c;
    while (_stateQueue.tryDequeue(c))
    {
        switch (c.kind)
        {
        case StateChangeKind::Parameter:
        {
            const auto idx = static_cast<size_t>(c.intArg);
            if (c.intArg >= 0 && idx < _paramPtrs.size() && _paramPtrs[idx])
                _paramPtrs[idx]->setValue(static_cast<float>(c.doubleArg));
            break;
        }
        case StateChangeKind::Tempo:
            _bpm.store(c.doubleArg, std::memory_order_relaxed);
            break;

        case StateChangeKind::TransportState:
            _playing.store(c.intArg != 0, std::memory_order_relaxed);
            break;

        case StateChangeKind::ResetTransport:
            _samplePos.store(0, std::memory_order_relaxed);
            break;
        }
    }
}

/* ── Transport ───────────────────────────────────────────────────────────── */

void PluginInstance::setTempo(double bpm)
{
    StateChange c{ StateChangeKind::Tempo, 0, bpm };
    _stateQueue.tryEnqueue(c);
}

void PluginInstance::setTransportState(bool playing)
{
    StateChange c{ StateChangeKind::TransportState, playing ? 1 : 0, 0.0 };
    _stateQueue.tryEnqueue(c);
}

void PluginInstance::resetTransportPosition()
{
    StateChange c{ StateChangeKind::ResetTransport, 0, 0.0 };
    _stateQueue.tryEnqueue(c);
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
