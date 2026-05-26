#pragma once

#include "JuceHeader.h"
#include "EditorWindow.h"
#include "SpscQueue.h"
#include "StringCache.h"
#include "../include/ownvst3_exports.h"

#include <atomic>
#include <memory>
#include <vector>
#include <unordered_map>

/** Tag for the kind of state change enqueued from the UI thread. */
enum class StateChangeKind : uint8_t
{
    Parameter,
    Tempo,
    TransportState,
    ResetTransport
};

/**
 * Payload for one enqueued state change.
 * Delivered lock-free from the UI thread to the audio thread via SpscQueue.
 */
struct StateChange
{
    StateChangeKind kind;
    int32_t         intArg;
    double          doubleArg;
};

/**
 * JUCE-based VST3 plugin instance.
 *
 * Owns one juce::AudioPluginInstance and its optional EditorWindow.
 * Methods are partitioned by calling thread:
 *   - loadPlugin / initialize / editor operations : UI thread
 *   - processAudio / processMidi                 : audio thread (lock-free)
 *   - setParameter / setTempo / setTransport      : any thread (SPSC queue)
 */
class PluginInstance
{
public:
    PluginInstance();
    ~PluginInstance();

    /* ── Loading ─────────────────────────────────────────────────────────── */

    /** Loads the VST3 bundle and builds the internal parameter map. */
    bool loadPlugin(const char* path);

    /** Prepares the plugin for audio processing. */
    bool initialize(double sampleRate, int blockSize);

    /* ── Metadata ────────────────────────────────────────────────────────── */

    const char* getName();
    const char* getVendor();
    const char* getVersion();
    const char* getPluginInfo();
    bool        isInstrument()  const;
    bool        isEffect()      const;
    bool        isMidiOnly()    const;
    int         getActualInputChannels()  const;
    int         getActualOutputChannels() const;

    /* ── Parameters ──────────────────────────────────────────────────────── */

    int  getParameterCount() const;
    bool getParameterAt(int index, VST3ParameterC* outParam);
    void setParameter(int paramId, double value);
    double getParameter(int paramId) const;

    /* ── Audio / MIDI  (audio thread only) ───────────────────────────────── */

    bool processAudio(float** inputs, int numIn,
                      float** outputs, int numOut,
                      int numSamples);

    bool processMidi(const MidiEventC* events, int count);

    /* ── Transport ───────────────────────────────────────────────────────── */

    void setTempo(double bpm);
    void setTransportState(bool playing);
    void resetTransportPosition();

    /* ── Editor (UI thread) ──────────────────────────────────────────────── */

    bool createEditor(void* ownerWindowHandle);
    void closeEditor();
    void resizeEditor(int width, int height);
    bool getEditorSize(int& width, int& height);
    bool isEditorOpen() const;
    void processIdle();

    /* ── State ───────────────────────────────────────────────────────────── */

    bool getState(uint8_t** outData, int* outLength);
    bool setState(const uint8_t* data, int length);

    /* ── String cache ────────────────────────────────────────────────────── */

    void clearStringCache();

private:
    /**
     * Drains the SPSC queue and applies all pending state changes.
     * Called at the top of processAudio() before processBlock().
     */
    void drainStateQueue();

    /**
     * Builds _paramPtrs and _indexToParamId after loadPlugin() succeeds.
     * JUCE uses sequential indices; this mapping makes the index the stable
     * identifier that the C# side uses as "paramId".
     */
    void buildParameterMap();

    /* JUCE plugin management */
    juce::AudioPluginFormatManager              _formatManager;
    std::unique_ptr<juce::AudioPluginInstance>  _plugin;
    std::unique_ptr<EditorWindow>               _editorWindow;

    /* Audio processing state – pre-allocated in initialize(), never resized on audio thread */
    juce::AudioBuffer<float>  _juceBuffer;
    juce::MidiBuffer          _midiBuffer;
    double                    _sampleRate { 44100.0 };
    int                       _blockSize  { 512 };

    /* Parameter map – built once in loadPlugin(), read-only thereafter */
    std::vector<juce::AudioProcessorParameter*> _paramPtrs;
    std::vector<int32_t>                        _indexToParamId;
    std::unordered_map<int32_t, int32_t>        _paramIdToIndex;

    /* Transport state – written via SPSC from UI thread, read on audio thread */
    std::atomic<double>   _bpm       { 120.0 };
    std::atomic<bool>     _playing   { false  };
    std::atomic<int64_t>  _samplePos { 0      };

    /* Lock-free queue: UI thread enqueues, audio thread drains */
    SpscQueue<StateChange, 512> _stateQueue;

    /* Shared string storage for pointers returned across the P/Invoke boundary */
    StringCache _strings;

    /* Lifecycle */
    std::atomic<bool> _disposed { false };

    /* PlayHead inner class that feeds transport info to the plugin */
    class PluginPlayHead final : public juce::AudioPlayHead
    {
    public:
        explicit PluginPlayHead(PluginInstance& owner) noexcept : _owner(owner) {}

        std::optional<PositionInfo> getPosition() const override;

    private:
        PluginInstance& _owner;
    };

    PluginPlayHead _playHead;
};
