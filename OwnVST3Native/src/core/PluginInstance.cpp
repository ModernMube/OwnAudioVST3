#include "PluginInstance.h"
#include "BackendFactory.h"
#include "ui/UiThread.h"

#include <algorithm>
#include <cstring>
#include <thread>

namespace
{
    constexpr int kMaxBypassChannels = 8;
}

PluginInstance::PluginInstance()
{
    UiThread::pinLibrary();
}

PluginInstance::~PluginInstance()
{
    _disposed.store(true, std::memory_order_release);
    parkAudioThread();
    _backend.reset();
}

void PluginInstance::parkAudioThread()
{
    _reconfiguring.store(true, std::memory_order_release);
    while (_inProcess.load(std::memory_order_acquire) != 0)
        std::this_thread::yield();
}

void PluginInstance::unparkAudioThread()
{
    _reconfiguring.store(false, std::memory_order_release);
}

bool PluginInstance::loadPlugin(const char* path)
{
    return loadPluginAt(path, 0);
}

bool PluginInstance::loadPluginAt(const char* path, int subIndex)
{
    if (!path || _disposed.load(std::memory_order_relaxed)) return false;

    parkAudioThread();
    _backend.reset();
    _blockSize = 0;

    _backend = ownvst3_createBackend(path, std::max(0, subIndex));
    unparkAudioThread();

    return _backend != nullptr;
}

bool PluginInstance::initialize(double sampleRate, int blockSize)
{
    if (!_backend || blockSize <= 0 || sampleRate <= 0) return false;

    parkAudioThread();

    const bool ok = _backend->prepare(sampleRate, blockSize);
    if (ok)
    {
        _sampleRate = sampleRate;
        _blockSize  = blockSize;

        const int channels = std::min(kMaxBypassChannels,
            std::max({ 2, _backend->inputChannels(), _backend->outputChannels() }));
        _bypassDelay.prepare(channels, _backend->latencySamples());
        _bypassScratch.assign(static_cast<size_t>(channels) * static_cast<size_t>(blockSize), 0.0f);
        _bypassOut.resize(static_cast<size_t>(channels));
        for (int c = 0; c < channels; ++c)
            _bypassOut[static_cast<size_t>(c)] = _bypassScratch.data() + static_cast<size_t>(c) * static_cast<size_t>(blockSize);
    }
    else
    {
        _blockSize = 0;
    }

    unparkAudioThread();
    return ok;
}

const char* PluginInstance::getName()
{
    return _backend ? _strings.store("name", _backend->meta().name) : "";
}

const char* PluginInstance::getVendor()
{
    return _backend ? _strings.store("vendor", _backend->meta().vendor) : "";
}

const char* PluginInstance::getVersion()
{
    return _backend ? _strings.store("version", _backend->meta().version) : "";
}

const char* PluginInstance::getPluginInfo()
{
    if (!_backend) return "";
    const auto& m = _backend->meta();
    return _strings.store("info", m.name + " | " + m.vendor + " | " + m.version);
}

const char* PluginInstance::getFormatName()
{
    return _backend ? _backend->formatName() : "";
}

const char* PluginInstance::getIdentifier()
{
    return _backend ? _strings.store("identifier", _backend->identifier()) : "";
}

bool PluginInstance::isInstrument() const
{
    return _backend && _backend->acceptsMidi() && _backend->inputChannels() == 0 && _backend->outputChannels() > 0;
}

bool PluginInstance::isEffect() const
{
    return _backend && _backend->inputChannels() > 0 && _backend->outputChannels() > 0;
}

bool PluginInstance::isMidiOnly() const
{
    return _backend && _backend->acceptsMidi() && _backend->outputChannels() == 0;
}

int PluginInstance::getActualInputChannels() const  { return _backend ? _backend->inputChannels() : 0; }
int PluginInstance::getActualOutputChannels() const { return _backend ? _backend->outputChannels() : 0; }
int PluginInstance::getLatencySamples() const       { return _backend && _blockSize > 0 ? _backend->latencySamples() : 0; }

int PluginInstance::getParameterCount() const
{
    return _backend ? _backend->parameterCount() : 0;
}

bool PluginInstance::getParameterAt(int index, VST3ParameterC* outParam)
{
    if (!_backend || !outParam) return false;

    std::string name;
    double def = 0.0;
    if (!_backend->parameterInfo(index, name, def)) return false;

    outParam->id           = index;
    outParam->name         = _strings.store("param_" + std::to_string(index), name);
    outParam->minValue     = 0.0;
    outParam->maxValue     = 1.0;
    outParam->defaultValue = def;
    outParam->currentValue = _backend->parameterValue(index);
    return true;
}

void PluginInstance::setParameter(int paramId, double value)
{
    if (!_backend || paramId < 0 || paramId >= _backend->parameterCount()) return;

    value = std::clamp(value, 0.0, 1.0);
    _backend->setParameterCache(paramId, value);
    _paramQueue.tryEnqueue(ParamChange { paramId, value });
}

double PluginInstance::getParameter(int paramId) const
{
    return _backend ? _backend->parameterValue(paramId) : 0.0;
}

bool PluginInstance::processAudio(float** inputs, float** outputs, int numChannels, int numSamples)
{
    if (_disposed.load(std::memory_order_relaxed) || !_backend) return false;
    if (_reconfiguring.load(std::memory_order_acquire)) return false;

    _inProcess.fetch_add(1, std::memory_order_acq_rel);

    const bool ok = !_reconfiguring.load(std::memory_order_acquire)
                 && processBody(inputs, outputs, numChannels, numSamples);

    _inProcess.fetch_sub(1, std::memory_order_release);
    return ok;
}

bool PluginInstance::processBody(float** inputs, float** outputs, int numChannels, int numSamples)
{
    if (!_backend || _blockSize <= 0 || numSamples <= 0 || numSamples > _blockSize || numChannels < 0)
        return false;

    int numParams = 0;
    ParamChange change;
    while (numParams < 1024 && _paramQueue.tryDequeue(change))
        _pendingParams[numParams++] = change;

    int numMidi = 0;
    MidiEventC ev;
    while (numMidi < 1024 && _midiQueue.tryDequeue(ev))
        _pendingMidi[numMidi++] = ev;

    const bool bypass    = _bypassed.load(std::memory_order_relaxed);
    const int  bypassIdx = _backend->bypassParameter();
    const int  delayed   = std::min(numChannels, static_cast<int>(_bypassOut.size()));

    if (bypassIdx >= 0)
    {
        if (bypass != _bypassActive)
        {
            _pendingParams[numParams++] = ParamChange { bypassIdx, bypass ? 1.0 : 0.0 };
            _backend->setParameterCache(bypassIdx, bypass ? 1.0 : 0.0);
        }
    }
    else if (inputs)
    {
        _bypassDelay.process(inputs, _bypassOut.data(), delayed, numSamples);
    }
    _bypassActive = bypass;

    const BlockContext ctx { _sampleRate, _bpm.load(std::memory_order_relaxed),
                             _playing.load(std::memory_order_relaxed), _samplePos.load(std::memory_order_relaxed) };

    const bool viaDelay = bypass && bypassIdx < 0;
    const int written = _backend->process(inputs, viaDelay ? nullptr : outputs, numChannels, numSamples,
                                          _pendingParams, numParams, _pendingMidi, numMidi, ctx);
    if (written < 0) return false;

    if (outputs)
    {
        const size_t bytes = sizeof(float) * static_cast<size_t>(numSamples);
        int from = written;

        if (viaDelay)
        {
            for (int c = 0; c < delayed; ++c)
                if (outputs[c]) std::memcpy(outputs[c], _bypassOut[static_cast<size_t>(c)], bytes);
            from = delayed;
        }

        for (int c = from; c < numChannels; ++c)
            if (outputs[c] && inputs && inputs[c] && outputs[c] != inputs[c])
                std::memcpy(outputs[c], inputs[c], bytes);
    }

    if (ctx.playing)
        _samplePos.fetch_add(numSamples, std::memory_order_relaxed);

    return true;
}

bool PluginInstance::processMidi(const MidiEventC* events, int count)
{
    if (!_backend || !events || count <= 0) return false;

    for (int i = 0; i < count; ++i)
        _midiQueue.tryEnqueue(events[i]);
    return true;
}

void PluginInstance::setTempo(double bpm)            { _bpm.store(bpm, std::memory_order_relaxed); }
void PluginInstance::setTransportState(bool playing) { _playing.store(playing, std::memory_order_relaxed); }
void PluginInstance::resetTransportPosition()        { _samplePos.store(0, std::memory_order_relaxed); }
void PluginInstance::setBypass(bool bypassed)        { _bypassed.store(bypassed, std::memory_order_relaxed); }

bool PluginInstance::createEditor(void* ownerWindowHandle)
{
    if (_disposed.load(std::memory_order_relaxed) || !_backend || !_backend->hasEditor()) return false;
    return _backend->openEditor(ownerWindowHandle);
}

void PluginInstance::closeEditor()
{
    if (_backend) _backend->closeEditor();
}

void PluginInstance::resizeEditor(int width, int height)
{
    if (_backend && width > 0 && height > 0) _backend->resizeEditor(width, height);
}

bool PluginInstance::hasEditor() const
{
    return _backend && _backend->hasEditor();
}

bool PluginInstance::getEditorSize(int& width, int& height)
{
    return _backend && _backend->editorSize(width, height);
}

bool PluginInstance::isEditorOpen() const
{
    return _backend && _backend->isEditorOpen();
}

bool PluginInstance::getState(uint8_t** outData, int* outLength)
{
    if (!_backend || !outData || !outLength) return false;

    std::vector<uint8_t> state;
    if (!_backend->getState(state) || state.empty()) return false;

    *outLength = static_cast<int>(state.size());
    *outData   = new uint8_t[state.size()];
    std::memcpy(*outData, state.data(), state.size());
    return true;
}

bool PluginInstance::setState(const uint8_t* data, int length)
{
    if (!_backend || !data || length <= 0) return false;
    return _backend->setState(data, static_cast<size_t>(length));
}
