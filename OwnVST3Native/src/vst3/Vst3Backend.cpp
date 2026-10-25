#include "Vst3Backend.h"
#include "core/StateCodec.h"
#include "scan/CacheXml.h"

#include "pluginterfaces/gui/iplugviewcontentscalesupport.h"
#include "pluginterfaces/vst/ivstmidicontrollers.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <algorithm>
#include <cmath>
#include <cstring>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace
{
#if defined(_WIN32)
    const FIDString kPlatformType = kPlatformTypeHWND;
#elif defined(__APPLE__)
    const FIDString kPlatformType = kPlatformTypeNSView;
#else
    const FIDString kPlatformType = kPlatformTypeX11EmbedWindowID;
#endif

    constexpr int kIdleIntervalMs = 30;

    template <typename T>
    IPtr<T> queryAs(FUnknown* obj)
    {
        T* out = nullptr;
        if (obj && obj->queryInterface(T::iid, reinterpret_cast<void**>(&out)) == kResultOk && out)
            return owned(out);
        return nullptr;
    }

    int busChannels(IComponent* c, BusDirection dir, int32 index)
    {
        BusInfo info {};
        return c->getBusInfo(kAudio, dir, index, info) == kResultOk ? info.channelCount : 0;
    }
}

std::unique_ptr<Vst3Backend> Vst3Backend::load(const std::string& path, int subIndex)
{
    std::unique_ptr<Vst3Backend> self(new Vst3Backend());
    bool ok = false;

    struct Ctx { Vst3Backend* self; const std::string* path; int subIndex; bool* ok; };
    Ctx ctx { self.get(), &path, subIndex, &ok };

    UiThread::call([&] {
        const bool survived = UiThread::guarded([](void* raw) {
            auto* c = static_cast<Ctx*>(raw);
            try { *c->ok = c->self->loadOnUi(*c->path, c->subIndex); }
            catch (...) { *c->ok = false; }
        }, &ctx);

        if (!survived) ok = false;
        if (!ok) self->unloadOnUi();
    });

    if (!ok) return nullptr;
    return self;
}

Vst3Backend::~Vst3Backend()
{
    UiThread::call([this] { unloadOnUi(); });
}

// createView() probe stays on the UI thread (IK plugins)
bool Vst3Backend::loadOnUi(const std::string& path, int subIndex)
{
    std::string error;
    _module = VST3::Hosting::Module::create(path, error);
    if (!_module) return false;

    const auto& factory = _module->getFactory();
    factory.setHostContext(ownvst3_hostApplication());

    std::vector<VST3::Hosting::ClassInfo> audioClasses;
    for (auto& info : factory.classInfos())
        if (info.category() == kVstAudioEffectClass)
            audioClasses.push_back(info);
    if (audioClasses.empty()) return false;

    const auto& cls = audioClasses[subIndex >= 0 && subIndex < static_cast<int>(audioClasses.size()) ? subIndex : 0];

    _identifier    = path;
    _meta.name     = cls.name();
    _meta.vendor   = cls.vendor().empty() ? factory.info().vendor() : cls.vendor();
    _meta.version  = cls.version();
    _meta.category = cls.subCategoriesString();
    _meta.uniqueId = ownvst3_hash31(reinterpret_cast<const uint8_t*>(cls.ID().data()), 16);

    _component = factory.createInstance<IComponent>(cls.ID());
    if (!_component || _component->initialize(ownvst3_hostApplication()) != kResultOk)
    {
        _component = nullptr;
        return false;
    }

    _processor = queryAs<IAudioProcessor>(_component);
    if (!_processor) return false;

    _controller = queryAs<IEditController>(_component);
    _singleComponent = _controller != nullptr;

    if (!_controller)
    {
        TUID cid;
        if (_component->getControllerClassId(cid) == kResultOk)
        {
            _controller = factory.createInstance<IEditController>(VST3::UID::fromTUID(cid));
            if (_controller && _controller->initialize(ownvst3_hostApplication()) != kResultOk)
                _controller = nullptr;
        }
    }

    if (_controller)
    {
        _handler = new ComponentHandler(this);
        _controller->setComponentHandler(_handler);

        if (!_singleComponent)
        {
            _componentCP  = queryAs<IConnectionPoint>(_component);
            _controllerCP = queryAs<IConnectionPoint>(_controller);
            if (_componentCP && _controllerCP)
            {
                _componentCP->connect(_controllerCP);
                _controllerCP->connect(_componentCP);
            }

            VectorStream state;
            if (_component->getState(&state) == kResultOk)
            {
                state.rewind();
                _controller->setComponentState(&state);
            }
        }
    }

    if (!setupBuses()) return false;

    readParameters();
    buildMidiMap();

    if (_controller)
    {
        if (IPtr<IPlugView> probe = owned(_controller->createView(ViewType::kEditor)))
            _hasEditor = probe->isPlatformTypeSupported(kPlatformType) == kResultTrue;
    }

    _timer = UiThread::startTimer(kIdleIntervalMs, [this] { onIdle(); });
    return true;
}

void Vst3Backend::unloadOnUi()
{
    if (_timer) { UiThread::stopTimer(_timer); _timer = 0; }

    closeEditorOnUi();
    stopProcessing();

    if (_componentCP && _controllerCP)
    {
        _componentCP->disconnect(_controllerCP);
        _controllerCP->disconnect(_componentCP);
    }
    _componentCP = nullptr;
    _controllerCP = nullptr;

    if (_handler) _handler->detach();

    if (_controller)
    {
        _controller->setComponentHandler(nullptr);
        if (!_singleComponent) _controller->terminate();
    }
    if (_component) _component->terminate();

    _controller = nullptr;
    _processor  = nullptr;
    _component  = nullptr;

    if (_handler) { _handler->release(); _handler = nullptr; }
    _module = nullptr;
}

bool Vst3Backend::setupBuses()
{
    const int32 numIn  = _component->getBusCount(kAudio, kInput);
    const int32 numOut = _component->getBusCount(kAudio, kOutput);

    if (numIn > 0 || numOut > 0)
    {
        std::vector<SpeakerArrangement> inArr(static_cast<size_t>(numIn)), outArr(static_cast<size_t>(numOut));
        for (int32 i = 0; i < numIn; ++i)  _processor->getBusArrangement(kInput, i, inArr[static_cast<size_t>(i)]);
        for (int32 i = 0; i < numOut; ++i) _processor->getBusArrangement(kOutput, i, outArr[static_cast<size_t>(i)]);

        auto wanted = [](std::vector<SpeakerArrangement> v) {
            if (!v.empty()) v[0] = SpeakerArr::kStereo;
            return v;
        };
        auto in2 = wanted(inArr), out2 = wanted(outArr);

        if (_processor->setBusArrangements(in2.data(), numIn, out2.data(), numOut) != kResultTrue)
            _processor->setBusArrangements(inArr.data(), numIn, outArr.data(), numOut);
    }

    for (int32 i = 0; i < numIn; ++i)  _component->activateBus(kAudio, kInput, i, i == 0);
    for (int32 i = 0; i < numOut; ++i) _component->activateBus(kAudio, kOutput, i, i == 0);

    const int32 numEvents = _component->getBusCount(kEvent, kInput);
    for (int32 i = 0; i < numEvents; ++i) _component->activateBus(kEvent, kInput, i, i == 0);

    _hasEventInput = numEvents > 0;
    _mainIn  = numIn  > 0 ? busChannels(_component, kInput, 0)  : 0;
    _mainOut = numOut > 0 ? busChannels(_component, kOutput, 0) : 0;
    return true;
}

void Vst3Backend::readParameters()
{
    _params.clear();
    _idToIndex.clear();
    _bypassIndex = -1;

    const int32 count = _controller ? _controller->getParameterCount() : 0;
    for (int32 i = 0; i < count; ++i)
    {
        ParameterInfo info {};
        if (_controller->getParameterInfo(i, info) != kResultOk) continue;

        const int index = static_cast<int>(_params.size());
        _params.push_back({ info.id, StringConvert::convert(info.title), info.defaultNormalizedValue, info.flags });
        _idToIndex.emplace(info.id, index);

        if (_bypassIndex < 0 && (info.flags & ParameterInfo::kIsBypass)) _bypassIndex = index;
    }

    _values.reset(new std::atomic<double>[_params.size()]);
    _controllerDirty.reset(new std::atomic<bool>[_params.size()]);
    for (size_t i = 0; i < _params.size(); ++i)
    {
        _values[i].store(_controller->getParamNormalized(_params[i].id), std::memory_order_relaxed);
        _controllerDirty[i].store(false, std::memory_order_relaxed);
    }
}

void Vst3Backend::refreshValues()
{
    if (!_controller) return;
    for (size_t i = 0; i < _params.size(); ++i)
        _values[i].store(_controller->getParamNormalized(_params[i].id), std::memory_order_relaxed);
}

void Vst3Backend::buildMidiMap()
{
    _midiMap.clear();

    auto mapping = queryAs<IMidiMapping>(_controller);
    if (!mapping) return;

    for (int ch = 0; ch < 16; ++ch)
    {
        for (int ctrl = 0; ctrl < MidiMapTable::kControllers; ++ctrl)
        {
            ParamID id = kNoParamId;
            if (mapping->getMidiControllerAssignment(0, static_cast<int16>(ch), static_cast<CtrlNumber>(ctrl), id) == kResultTrue)
                _midiMap.ids[static_cast<size_t>(ch)][static_cast<size_t>(ctrl)] = id;
        }
    }
}

void Vst3Backend::stopProcessing()
{
    if (_processing && _processor) _processor->setProcessing(false);
    if (_active && _component)     _component->setActive(false);
    _processing = false;
    _active     = false;
}

bool Vst3Backend::prepare(double sampleRate, int maxBlockSize)
{
    bool ok = false;

    UiThread::call([&] {
        stopProcessing();

        ProcessSetup setup {};
        setup.processMode        = kRealtime;
        setup.symbolicSampleSize = kSample32;
        setup.maxSamplesPerBlock = maxBlockSize;
        setup.sampleRate         = sampleRate;
        if (_processor->setupProcessing(setup) != kResultOk) return;

        const int32 numIn  = _component->getBusCount(kAudio, kInput);
        const int32 numOut = _component->getBusCount(kAudio, kOutput);

        size_t channels = 0;
        _inBuses.assign(static_cast<size_t>(numIn), AudioBusBuffers {});
        _outBuses.assign(static_cast<size_t>(numOut), AudioBusBuffers {});
        for (int32 i = 0; i < numIn; ++i)  { _inBuses[static_cast<size_t>(i)].numChannels  = busChannels(_component, kInput, i);  channels += static_cast<size_t>(_inBuses[static_cast<size_t>(i)].numChannels); }
        for (int32 i = 0; i < numOut; ++i) { _outBuses[static_cast<size_t>(i)].numChannels = busChannels(_component, kOutput, i); channels += static_cast<size_t>(_outBuses[static_cast<size_t>(i)].numChannels); }

        _scratch.assign(channels * static_cast<size_t>(maxBlockSize), 0.0f);
        _channelPtrs.assign(channels, nullptr);

        size_t next = 0;
        auto bind = [&](AudioBusBuffers& bus) {
            bus.channelBuffers32 = _channelPtrs.data() + next;
            for (int32 c = 0; c < bus.numChannels; ++c, ++next)
                _channelPtrs[next] = _scratch.data() + next * static_cast<size_t>(maxBlockSize);
        };
        for (auto& b : _inBuses)  bind(b);
        for (auto& b : _outBuses) bind(b);

        const int capacity = static_cast<int>(_params.size()) + 16;
        _inChanges.resize(capacity);
        _outChanges.resize(capacity);
        _events.setMaxSize(1024);
        _maxBlock = maxBlockSize;

        if (_component->setActive(true) != kResultOk) return;
        _active = true;

        _processor->setProcessing(true);
        _processing = true;

        _latency.store(static_cast<int>(_processor->getLatencySamples()), std::memory_order_relaxed);
        ok = true;
    });

    return ok;
}

bool Vst3Backend::parameterInfo(int index, std::string& name, double& defaultValue)
{
    if (index < 0 || index >= static_cast<int>(_params.size())) return false;
    name         = _params[static_cast<size_t>(index)].name;
    defaultValue = _params[static_cast<size_t>(index)].defaultValue;
    return true;
}

double Vst3Backend::parameterValue(int index) const
{
    if (index < 0 || index >= static_cast<int>(_params.size())) return 0.0;
    return _values[static_cast<size_t>(index)].load(std::memory_order_relaxed);
}

void Vst3Backend::setParameterCache(int index, double value)
{
    if (index < 0 || index >= static_cast<int>(_params.size())) return;
    _values[static_cast<size_t>(index)].store(value, std::memory_order_relaxed);
    _controllerDirty[static_cast<size_t>(index)].store(true, std::memory_order_release);
    _anyDirty.store(true, std::memory_order_release);
}

int Vst3Backend::indexOf(ParamID id) const
{
    const auto it = _idToIndex.find(id);
    return it == _idToIndex.end() ? -1 : it->second;
}

int Vst3Backend::process(float* const* in, float* const* out, int numChannels, int numSamples,
                         const ParamChange* params, int numParams, const MidiEventC* midi, int numMidi,
                         const BlockContext& block)
{
    if (!_processing || numSamples > _maxBlock) return -1;

    const size_t bytes = sizeof(float) * static_cast<size_t>(numSamples);

    for (size_t b = 0; b < _inBuses.size(); ++b)
    {
        auto& bus = _inBuses[b];
        bus.silenceFlags = 0;
        for (int32 c = 0; c < bus.numChannels; ++c)
        {
            float* dst = bus.channelBuffers32[c];
            if (b == 0 && c < numChannels && in && in[c]) std::memcpy(dst, in[c], bytes);
            else                                          std::memset(dst, 0, bytes);
        }
        if (b > 0) bus.silenceFlags = ~uint64(0);
    }
    for (auto& bus : _outBuses)
    {
        bus.silenceFlags = 0;
        for (int32 c = 0; c < bus.numChannels; ++c) std::memset(bus.channelBuffers32[c], 0, bytes);
    }

    _inChanges.clear();
    _outChanges.clear();
    _events.clear();

    for (int i = 0; i < numParams; ++i)
    {
        const int idx = params[i].index;
        if (idx >= 0 && idx < static_cast<int>(_params.size()))
            _inChanges.add(_params[static_cast<size_t>(idx)].id, 0, params[i].value);
    }

    ParamChange edit;
    while (_editQueue.tryDequeue(edit))
        _inChanges.add(_params[static_cast<size_t>(edit.index)].id, 0, edit.value);

    for (int i = 0; i < numMidi; ++i)
    {
        Event ev;
        ParamID pid;
        double  value;
        switch (ownvst3_convertMidi(midi[i], numSamples, _midiMap, ev, pid, value))
        {
            case MidiConversion::Event:
                if (_hasEventInput) _events.addEvent(ev);
                break;
            case MidiConversion::Parameter:
            {
                _inChanges.add(pid, ev.sampleOffset, value);
                const int idx = indexOf(pid);
                if (idx >= 0) _values[static_cast<size_t>(idx)].store(value, std::memory_order_relaxed);
                break;
            }
            default:
                break;
        }
    }

    ProcessContext ctx {};
    ctx.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid
              | ProcessContext::kProjectTimeMusicValid | ProcessContext::kBarPositionValid
              | ProcessContext::kContTimeValid;
    if (block.playing) ctx.state |= ProcessContext::kPlaying;
    ctx.sampleRate             = block.sampleRate;
    ctx.projectTimeSamples     = block.samplePos;
    ctx.continousTimeSamples   = block.samplePos;
    ctx.tempo                  = block.bpm;
    ctx.timeSigNumerator       = 4;
    ctx.timeSigDenominator     = 4;
    ctx.projectTimeMusic       = block.sampleRate > 0 ? (block.samplePos / block.sampleRate) * (block.bpm / 60.0) : 0.0;
    ctx.barPositionMusic       = std::floor(ctx.projectTimeMusic / 4.0) * 4.0;

    ProcessData data {};
    data.processMode            = kRealtime;
    data.symbolicSampleSize     = kSample32;
    data.numSamples             = numSamples;
    data.numInputs              = static_cast<int32>(_inBuses.size());
    data.numOutputs             = static_cast<int32>(_outBuses.size());
    data.inputs                 = _inBuses.empty() ? nullptr : _inBuses.data();
    data.outputs                = _outBuses.empty() ? nullptr : _outBuses.data();
    data.inputParameterChanges  = &_inChanges;
    data.outputParameterChanges = &_outChanges;
    data.inputEvents            = _hasEventInput ? &_events : nullptr;
    data.processContext         = &ctx;

    _processor->process(data);

    for (int32 i = 0; i < _outChanges.getParameterCount(); ++i)
    {
        auto* q = _outChanges.getParameterData(i);
        const int idx = q ? indexOf(q->getParameterId()) : -1;
        int32 offset;
        ParamValue value;
        if (idx >= 0 && q->getPoint(q->getPointCount() - 1, offset, value) == kResultTrue)
        {
            _values[static_cast<size_t>(idx)].store(value, std::memory_order_relaxed);
            _controllerDirty[static_cast<size_t>(idx)].store(true, std::memory_order_release);
            _anyDirty.store(true, std::memory_order_release);
        }
    }

    if (_outBuses.empty()) return 0;

    const int written = std::min(numChannels, static_cast<int>(_outBuses[0].numChannels));
    if (out)
        for (int c = 0; c < written; ++c)
            if (out[c]) std::memcpy(out[c], _outBuses[0].channelBuffers32[c], bytes);

    return written;
}

void Vst3Backend::performEdit(ParamID id, ParamValue value)
{
    const int idx = indexOf(id);
    if (idx < 0) return;

    _values[static_cast<size_t>(idx)].store(value, std::memory_order_relaxed);
    _editQueue.tryEnqueue(ParamChange { idx, value });
}

void Vst3Backend::restartComponent(int32 flags)
{
    _restartFlags.fetch_or(flags, std::memory_order_acq_rel);
}

void Vst3Backend::onIdle()
{
    if (_anyDirty.exchange(false, std::memory_order_acq_rel) && _controller)
    {
        for (size_t i = 0; i < _params.size(); ++i)
            if (_controllerDirty[i].exchange(false, std::memory_order_acq_rel))
                _controller->setParamNormalized(_params[i].id, _values[i].load(std::memory_order_relaxed));
    }

    const int32 flags = _restartFlags.exchange(0, std::memory_order_acq_rel);
    if (flags & kLatencyChanged && _processor)
        _latency.store(static_cast<int>(_processor->getLatencySamples()), std::memory_order_relaxed);
    if (flags & (kParamValuesChanged | kReloadComponent))
        refreshValues();

    if (_closePending.exchange(false, std::memory_order_acq_rel))
        closeEditorOnUi();
}

bool Vst3Backend::openEditor(void* ownerWindow)
{
    if (!UiThread::available()) return false;

    bool ok = false;
    UiThread::call([&] { openEditorOnUi(ownerWindow, ok); });
    return ok;
}

// content scale only after attached(), TDR Nova crashes otherwise
void Vst3Backend::openEditorOnUi(void* owner, bool& ok)
{
    if (_window) { _window->show(); ok = true; return; }
    if (!_controller || !_hasEditor) return;

    _view = owned(_controller->createView(ViewType::kEditor));
    if (!_view || _view->isPlatformTypeSupported(kPlatformType) != kResultTrue) { _view = nullptr; return; }

    _frame = new PlugFrame(this);
    _view->setFrame(_frame);

    ViewRect rect {};
    if (_view->getSize(&rect) != kResultOk || rect.getWidth() <= 0 || rect.getHeight() <= 0)
        rect = ViewRect(0, 0, 400, 300);

    EditorWindow::Callbacks cb;
    cb.resized        = [this](int w, int h) { onUserResize(w, h); };
    cb.closeRequested = [this] {
        _editorOpen.store(false, std::memory_order_release);
        _closePending.store(true, std::memory_order_release);
    };

    _window = EditorWindow::create(_meta.name, rect.getWidth(), rect.getHeight(),
                                   _view->canResize() == kResultTrue, owner, std::move(cb));

    if (!_window || _view->attached(_window->parentHandle(), kPlatformType) != kResultOk)
    {
        closeEditorOnUi();
        return;
    }
    _attached = true;

#if !defined(__APPLE__)
    if (auto scale = queryAs<IPlugViewContentScaleSupport>(_view))
        scale->setContentScaleFactor(static_cast<IPlugViewContentScaleSupport::ScaleFactor>(_window->scaleFactor()));
#endif

    if (_view->getSize(&rect) == kResultOk && rect.getWidth() > 0 && rect.getHeight() > 0)
    {
        int w = 0, h = 0;
        _window->contentSize(w, h);
        if (w != rect.getWidth() || h != rect.getHeight())
            _window->setContentSize(rect.getWidth(), rect.getHeight());
    }

    _window->show();
    _editorOpen.store(true, std::memory_order_release);
    ok = true;
}

void Vst3Backend::closeEditor()
{
    UiThread::call([this] { closeEditorOnUi(); });
}

void Vst3Backend::closeEditorOnUi()
{
    _editorOpen.store(false, std::memory_order_release);
    _closePending.store(false, std::memory_order_release);

    if (_window) _window->prepareForViewRemoval();

    if (_view)
    {
        if (_attached) _view->removed();
        _view->setFrame(nullptr);
        _view = nullptr;
    }
    if (_frame)
    {
        _frame->detach();
        _frame->release();
        _frame = nullptr;
    }
    _window.reset();
    _attached = false;
}

void Vst3Backend::resizeEditor(int width, int height)
{
    UiThread::call([&] {
        if (!_window || !_view) return;

        ViewRect r(0, 0, width, height);
        if (_view->canResize() == kResultTrue) _view->checkSizeConstraint(&r);

        _inResize = true;
        _window->setContentSize(r.getWidth(), r.getHeight());
        _view->onSize(&r);
        _inResize = false;
    });
}

bool Vst3Backend::editorSize(int& width, int& height)
{
    bool ok = false;
    UiThread::call([&] {
        if (!_window) return;
        _window->contentSize(width, height);
        ok = width > 0 && height > 0;
    });
    return ok;
}

bool Vst3Backend::resizeView(IPlugView* view, ViewRect* rect)
{
    if (!_window || view != _view.get()) return false;
    if (_inResize) return true;

    _inResize = true;
    _window->setContentSize(rect->getWidth(), rect->getHeight());
    view->onSize(rect);
    _inResize = false;
    return true;
}

void Vst3Backend::onUserResize(int width, int height)
{
    if (!_view || _inResize) return;

    ViewRect r(0, 0, width, height);
    if (_view->canResize() == kResultTrue)
    {
        _view->checkSizeConstraint(&r);
    }
    else
    {
        _view->getSize(&r);
    }

    _inResize = true;
    if (r.getWidth() != width || r.getHeight() != height)
        _window->setContentSize(r.getWidth(), r.getHeight());
    _view->onSize(&r);
    _inResize = false;
}

bool Vst3Backend::getState(std::vector<uint8_t>& out)
{
    bool ok = false;

    UiThread::call([&] {
        if (!_component) return;

        StateCodec::Vst3State state;

        VectorStream cs;
        if (_component->getState(&cs) == kResultOk)
        {
            state.hasComponent = true;
            state.component    = std::move(cs.data());
        }

        if (_controller)
        {
            VectorStream es;
            if (_controller->getState(&es) == kResultOk)
            {
                state.hasController = true;
                state.controller    = std::move(es.data());
            }
        }

        out = StateCodec::writeVst3(state);
        ok  = true;
    });

    return ok;
}

bool Vst3Backend::setState(const uint8_t* data, size_t size)
{
    bool ok = false;

    UiThread::call([&] {
        if (!_component) return;

        StateCodec::Vst3State state;
        if (StateCodec::readVst3(data, size, state))
        {
            if (state.hasComponent)
            {
                VectorStream cs(state.component.data(), state.component.size());
                _component->setState(&cs);
                if (_controller)
                {
                    cs.rewind();
                    _controller->setComponentState(&cs);
                }
            }
            if (state.hasController && _controller)
            {
                VectorStream es(state.controller.data(), state.controller.size());
                _controller->setState(&es);
            }
            ok = true;
        }
        else if (!StateCodec::hasVst3Header(data, size))
        {
            VectorStream cs(data, size);
            _component->setState(&cs);
            if (_controller)
            {
                cs.rewind();
                _controller->setComponentState(&cs);
                cs.rewind();
                _controller->setState(&cs);
            }
            ok = true;
        }

        if (ok) refreshValues();
    });

    return ok;
}
