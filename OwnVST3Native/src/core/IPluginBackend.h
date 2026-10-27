#pragma once

#include "../../include/ownvst3_exports.h"

#include <cstdint>
#include <string>
#include <vector>

struct ParamChange
{
    int32_t index;
    double  value;
};

struct BlockContext
{
    double  sampleRate = 44100.0;
    double  bpm        = 120.0;
    bool    playing    = false;
    int64_t samplePos  = 0;
};

struct PluginMeta
{
    std::string name, vendor, version, category;
    int32_t     uniqueId = 0;
};

// one plugin of one format. process() is audio thread: no alloc, no locks
class IPluginBackend
{
public:
    virtual ~IPluginBackend() = default;

    virtual const char*        formatName() const noexcept = 0;
    virtual const std::string& identifier() const noexcept = 0;
    virtual const PluginMeta&  meta() const noexcept = 0;

    virtual bool acceptsMidi() const = 0;
    virtual int  inputChannels() const = 0;
    virtual int  outputChannels() const = 0;
    virtual int  latencySamples() const = 0;

    virtual bool prepare(double sampleRate, int maxBlockSize) = 0;

    virtual int    parameterCount() const = 0;
    virtual bool   parameterInfo(int index, std::string& name, double& defaultValue) = 0;
    virtual double parameterValue(int index) const = 0;

    virtual void setParameterCache(int index, double value) = 0;

    virtual int bypassParameter() const { return -1; }

    // out may be null. returns channels written, -1 on failure
    virtual int process(float* const* in, float* const* out, int numChannels, int numSamples,
                        const ParamChange* params, int numParams,
                        const MidiEventC* midi, int numMidi,
                        const BlockContext& ctx) = 0;

    virtual bool hasEditor() const = 0;
    virtual bool openEditor(void* ownerWindow) = 0;
    virtual void closeEditor() = 0;
    virtual void resizeEditor(int width, int height) = 0;
    virtual bool editorSize(int& width, int& height) = 0;
    virtual bool isEditorOpen() const = 0;

    // the plugin's view inside a view the app owns (UIView / NSView). false where unsupported
    virtual bool attachEditor(void* parentView) { (void)parentView; return false; }
    virtual void detachEditor() {}

    virtual bool getState(std::vector<uint8_t>& out) = 0;
    virtual bool setState(const uint8_t* data, size_t size) = 0;
};
