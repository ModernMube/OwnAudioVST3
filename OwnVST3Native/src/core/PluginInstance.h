#pragma once

#include "IPluginBackend.h"
#include "DelayLine.h"
#include "MpscQueue.h"
#include "StringCache.h"
#include "../../include/ownvst3_exports.h"

#include <atomic>
#include <memory>
#include <string>
#include <vector>

// what a VST3PluginHandle points at
class PluginInstance
{
public:
    PluginInstance();
    ~PluginInstance();

    bool loadPlugin(const char* path);
    bool loadPluginAt(const char* path, int subIndex);
    bool initialize(double sampleRate, int blockSize);

    const char* getName();
    const char* getVendor();
    const char* getVersion();
    const char* getPluginInfo();
    const char* getFormatName();
    const char* getIdentifier();
    bool        isInstrument() const;
    bool        isEffect() const;
    bool        isMidiOnly() const;
    int         getActualInputChannels() const;
    int         getActualOutputChannels() const;
    int         getLatencySamples() const;

    int    getParameterCount() const;
    bool   getParameterAt(int index, VST3ParameterC* outParam);
    void   setParameter(int paramId, double value);
    double getParameter(int paramId) const;

    bool processAudio(float** inputs, float** outputs, int numChannels, int numSamples);
    bool processMidi(const MidiEventC* events, int count);

    void setTempo(double bpm);
    void setTransportState(bool playing);
    void resetTransportPosition();
    void setBypass(bool bypassed);

    bool createEditor(void* ownerWindowHandle);
    void closeEditor();
    void resizeEditor(int width, int height);
    bool hasEditor() const;
    bool getEditorSize(int& width, int& height);
    bool isEditorOpen() const;
    bool attachEditor(void* parentView);
    void detachEditor();

    bool getState(uint8_t** outData, int* outLength);
    bool setState(const uint8_t* data, int length);

private:
    void parkAudioThread();
    void unparkAudioThread();
    bool processBody(float** inputs, float** outputs, int numChannels, int numSamples);

    std::unique_ptr<IPluginBackend> _backend;

    MpscQueue<ParamChange, 1024> _paramQueue;
    MpscQueue<MidiEventC, 1024>  _midiQueue;

    ParamChange _pendingParams[1024 + 1];
    MidiEventC  _pendingMidi[1024];
    DelayLine   _bypassDelay;
    std::vector<float>  _bypassScratch;
    std::vector<float*> _bypassOut;

    double _sampleRate = 44100.0;
    int    _blockSize  = 0;

    std::atomic<double>  _bpm       { 120.0 };
    std::atomic<bool>    _playing   { false };
    std::atomic<int64_t> _samplePos { 0 };

    std::atomic<bool> _bypassed     { false };
    bool              _bypassActive = false;

    std::atomic<bool> _reconfiguring { false };
    std::atomic<int>  _inProcess     { 0 };
    std::atomic<bool> _disposed      { false };

    StringCache _strings;
};
