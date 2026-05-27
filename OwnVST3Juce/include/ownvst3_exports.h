#pragma once

/**
 * Public C API for the JUCE-based ownvst3 shared library.
 *
 * All function names, parameter types, and struct layouts are binary-compatible
 * with the previous VST3 SDK-based implementation so that the C# P/Invoke layer
 * (OwnVst3Wrapper, ThreadedVst3Wrapper, VstEditorController) requires zero changes.
 *
 * Struct layout rules
 * -------------------
 * Every struct here matches its [StructLayout(LayoutKind.Sequential)] counterpart
 * in the C# project.  Natural alignment is used on both sides (no #pragma pack).
 *
 * Threading contract
 * ------------------
 *  - VST3Plugin_ProcessAudio / VST3Plugin_ProcessMidi : audio thread only, lock-free
 *  - VST3Plugin_CreateEditor / VST3Plugin_CloseEditor : UI thread only
 *  - All other exports                                 : any thread (internally synchronised)
 */

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
    #define OWNVST3_API __declspec(dllexport)
#else
    #define OWNVST3_API __attribute__((visibility("default")))
#endif

/** Opaque pointer to a plugin instance created by VST3Plugin_Create(). */
typedef void* VST3PluginHandle;

/**
 * Parameter descriptor returned by VST3Plugin_GetParameterAt().
 * The 'name' pointer is owned by the plugin instance and remains valid
 * until VST3Plugin_Destroy() is called on the same handle.
 *
 * C# counterpart: OwnVst3Wrapper.VST3ParameterC  (LayoutKind.Sequential)
 *   int id           – 4 bytes at offset  0
 *   IntPtr name      – 8 bytes at offset  8  (4 bytes natural padding after id on 64-bit)
 *   double minValue  – 8 bytes at offset 16
 *   double maxValue  – 8 bytes at offset 24
 *   double default   – 8 bytes at offset 32
 *   double current   – 8 bytes at offset 40
 */
typedef struct {
    int32_t      id;
    const char*  name;
    double       minValue;
    double       maxValue;
    double       defaultValue;
    double       currentValue;
} VST3ParameterC;

/**
 * Audio buffer passed to VST3Plugin_ProcessAudio().
 * 'inputs' and 'outputs' point to GC-pinned float* arrays allocated by the C# caller.
 *
 * C# counterpart: OwnVst3Wrapper.AudioBufferC  (LayoutKind.Sequential)
 */
typedef struct {
    float** inputs;
    float** outputs;
    int32_t numChannels;
    int32_t numSamples;
} AudioBufferC;

/**
 * Single MIDI event for VST3Plugin_ProcessMidi().
 *
 * C# counterpart: OwnVst3Wrapper.MidiEventC  (LayoutKind.Sequential)
 *   byte status      – offset 0
 *   byte data1       – offset 1
 *   byte data2       – offset 2
 *   [1 byte padding] – offset 3
 *   int sampleOffset – offset 4
 *   Total size: 8 bytes
 */
typedef struct {
    uint8_t  status;
    uint8_t  data1;
    uint8_t  data2;
    uint8_t  _reserved;    /* explicit padding – do not use */
    int32_t  sampleOffset;
} MidiEventC;

/* ── Lifecycle ─────────────────────────────────────────────────────────────── */

/** Creates and returns a new plugin instance. Returns NULL on failure. */
OWNVST3_API VST3PluginHandle VST3Plugin_Create();

/** Destroys the instance and releases all native resources. */
OWNVST3_API void VST3Plugin_Destroy(VST3PluginHandle handle);

/* ── Plugin loading ────────────────────────────────────────────────────────── */

/** Loads the VST3 bundle at pluginPath. Must be called before Initialize(). */
OWNVST3_API bool VST3Plugin_LoadPlugin(VST3PluginHandle handle, const char* pluginPath);

/**
 * Prepares the plugin for audio processing.
 * Must be called after LoadPlugin() and before ProcessAudio()/ProcessMidi().
 */
OWNVST3_API bool VST3Plugin_Initialize(VST3PluginHandle handle, double sampleRate, int maxBlockSize);

/* ── Metadata ──────────────────────────────────────────────────────────────── */

/** Returns the plugin display name. Pointer valid until Destroy(). */
OWNVST3_API const char* VST3Plugin_GetName(VST3PluginHandle handle);

/** Returns the manufacturer/vendor name. Pointer valid until Destroy(). */
OWNVST3_API const char* VST3Plugin_GetVendor(VST3PluginHandle handle);

/** Returns the plugin version string. Pointer valid until Destroy(). */
OWNVST3_API const char* VST3Plugin_GetVersion(VST3PluginHandle handle);

/** Returns a combined info string (name + vendor + version). Pointer valid until Destroy(). */
OWNVST3_API const char* VST3Plugin_GetPluginInfo(VST3PluginHandle handle);

/** Returns true if the plugin accepts MIDI input and produces audio output (synth). */
OWNVST3_API bool VST3Plugin_IsInstrument(VST3PluginHandle handle);

/** Returns true if the plugin processes audio (effect / insert). */
OWNVST3_API bool VST3Plugin_IsEffect(VST3PluginHandle handle);

/** Returns true if the plugin accepts MIDI but produces no audio output. */
OWNVST3_API bool VST3Plugin_IsMidiOnly(VST3PluginHandle handle);

/** Returns the number of input channels negotiated with the plugin. */
OWNVST3_API int VST3Plugin_GetActualInputChannels(VST3PluginHandle handle);

/** Returns the number of output channels negotiated with the plugin. */
OWNVST3_API int VST3Plugin_GetActualOutputChannels(VST3PluginHandle handle);

/* ── Parameters ────────────────────────────────────────────────────────────── */

/** Returns the total parameter count. */
OWNVST3_API int VST3Plugin_GetParameterCount(VST3PluginHandle handle);

/**
 * Fills *outParam with the descriptor of the parameter at the given index.
 * Returns false if index is out of range or outParam is NULL.
 */
OWNVST3_API bool VST3Plugin_GetParameterAt(VST3PluginHandle handle, int index, VST3ParameterC* outParam);

/**
 * Enqueues a parameter change for the next audio block.
 * Safe to call from any thread; delivered lock-free to the audio thread.
 */
OWNVST3_API bool VST3Plugin_SetParameter(VST3PluginHandle handle, int paramId, double value);

/** Returns the current normalised [0,1] value of the parameter at index paramId. */
OWNVST3_API double VST3Plugin_GetParameter(VST3PluginHandle handle, int paramId);

/* ── Audio processing ──────────────────────────────────────────────────────── */

/**
 * Processes one block of audio.  Must be called exclusively from the audio thread.
 * MIDI events accumulated via ProcessMidi() since the last call are consumed here.
 */
OWNVST3_API bool VST3Plugin_ProcessAudio(VST3PluginHandle handle, AudioBufferC* buffer);

/* ── MIDI processing ───────────────────────────────────────────────────────── */

/**
 * Accumulates MIDI events for the next ProcessAudio() call.
 * Must be called exclusively from the audio thread.
 */
OWNVST3_API bool VST3Plugin_ProcessMidi(VST3PluginHandle handle, const MidiEventC* events, int count);

/* ── Transport ─────────────────────────────────────────────────────────────── */

/** Enqueues a tempo change (BPM).  Safe to call from any thread. */
OWNVST3_API void VST3Plugin_SetTempo(VST3PluginHandle handle, double bpm);

/** Enqueues a transport play/stop state change.  Safe to call from any thread. */
OWNVST3_API void VST3Plugin_SetTransportState(VST3PluginHandle handle, bool isPlaying);

/** Resets the internal sample-position counter to zero.  Safe to call from any thread. */
OWNVST3_API void VST3Plugin_ResetTransportPosition(VST3PluginHandle handle);

/* ── Editor window ─────────────────────────────────────────────────────────── */

/**
 * Returns true if the plugin has an editor UI.
 * Safe to call from any thread; does NOT create a temporary editor component.
 * Use this to decide whether to show an open-editor action.  The actual pixel
 * dimensions are only available via VST3Plugin_GetEditorSize() once the editor
 * is open.
 */
OWNVST3_API bool VST3Plugin_HasEditor(VST3PluginHandle handle);

/**
 * Opens the plugin editor in a JUCE-managed top-level window.
 * windowHandle is used as the Win32 owner window on Windows (not as a parent/embed target).
 * Must be called from the UI thread.
 */
OWNVST3_API bool VST3Plugin_CreateEditor(VST3PluginHandle handle, void* windowHandle);

/** Closes and destroys the editor window.  Must be called from the UI thread. */
OWNVST3_API void VST3Plugin_CloseEditor(VST3PluginHandle handle);

/** Requests that the editor window be resized to the given pixel dimensions. */
OWNVST3_API void VST3Plugin_ResizeEditor(VST3PluginHandle handle, int width, int height);

/**
 * Retrieves the preferred editor size in pixels.
 * Returns false if no editor is available or the plugin has no UI.
 */
OWNVST3_API bool VST3Plugin_GetEditorSize(VST3PluginHandle handle, int* width, int* height);

/** Returns true if the editor window is currently open and visible. */
OWNVST3_API bool VST3Plugin_IsEditorOpen(VST3PluginHandle handle);

/**
 * Drives any pending plugin idle processing.
 * Call this from the UI thread at approximately 50 Hz.
 * On macOS it also pumps the JUCE message queue.
 */
OWNVST3_API void VST3Plugin_ProcessIdle(VST3PluginHandle handle);

/* ── State serialisation ───────────────────────────────────────────────────── */

/**
 * Serialises the complete plugin state into a newly allocated buffer.
 * On success, *outData points to a heap buffer of *outLength bytes.
 * The caller MUST release the buffer via VST3Plugin_FreeStateData().
 */
OWNVST3_API bool VST3Plugin_GetState(VST3PluginHandle handle, uint8_t** outData, int* outLength);

/**
 * Restores the plugin state from a previously obtained buffer.
 * data must remain valid for the duration of the call.
 */
OWNVST3_API bool VST3Plugin_SetState(VST3PluginHandle handle, const uint8_t* data, int length);

/** Releases a buffer previously returned by VST3Plugin_GetState(). */
OWNVST3_API void VST3Plugin_FreeStateData(uint8_t* data);

/* ── String cache ──────────────────────────────────────────────────────────── */

/** Clears all internally cached name/vendor/version strings.  Safe to call from any thread. */
OWNVST3_API void VST3Plugin_ClearStringCache();

/* ── Windows SEH-safe message dispatch ────────────────────────────────────── */

/**
 * No-op on JUCE builds.
 *
 * The previous VST3 SDK implementation wrapped Win32 DispatchMessage() in an
 * SEH __try/__except block to swallow access violations raised inside plugin
 * WndProcs.  JUCE's MessageManager provides equivalent protection internally,
 * so this export exists solely for binary compatibility with the C# caller that
 * resolves it via NativeLibrary.TryGetExport().
 */
OWNVST3_API void VST3Plugin_SafeDispatchMessage(void* msg);

#ifdef __cplusplus
}
#endif
