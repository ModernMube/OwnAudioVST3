#pragma once

#include "../../include/ownvst3_exports.h"

#include <cstdint>
#include <string>
#include <vector>

/** One pending parameter write, delivered lock-free to the audio thread. */
struct ParamChange
{
    int32_t index;
    double  value;
};

/** What the host knows about time when a block starts. */
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

/**
 * One loaded plugin of one format. PluginInstance owns it and does everything that
 * is format-neutral (queues, transport, bypass, the reconfigure guard), so a backend
 * only talks to its plugin.
 *
 * Threads: load / prepare / parameter info / state / editor run on the UI thread
 * (backends marshal there themselves); process() runs on the audio thread and must not
 * allocate, lock or call into the OS; setParameterCache() and parameterValue() are any thread.
 */
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

    /** (Re)allocates everything process() touches. The audio thread is parked meanwhile. */
    virtual bool prepare(double sampleRate, int maxBlockSize) = 0;

    virtual int    parameterCount() const = 0;
    virtual bool   parameterInfo(int index, std::string& name, double& defaultValue) = 0;
    virtual double parameterValue(int index) const = 0;

    /** Host-side write: updates the cache and whatever must follow on the UI thread. */
    virtual void setParameterCache(int index, double value) = 0;

    /** Index of the plugin's own bypass parameter, or -1. */
    virtual int bypassParameter() const { return -1; }

    /**
     * Renders one block. in / out are numChannels host planes; out may be null (render,
     * then discard — used while bypassed through a delay line). Returns how many of the
     * host's output channels were written, or -1 if the plugin failed.
     */
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

    virtual bool getState(std::vector<uint8_t>& out) = 0;
    virtual bool setState(const uint8_t* data, size_t size) = 0;
};
