/**
 * C export bridge for the JUCE-based ownvst3 shared library.
 *
 * This translation unit contains ONLY thin forwarding functions – no business
 * logic.  Every exported symbol declared in ownvst3_exports.h must appear here
 * exactly once so that the Windows DEF file, the macOS visibility attributes,
 * and the Linux symbol table all agree with what the C# P/Invoke layer expects.
 *
 * Threading notes are documented in ownvst3_exports.h.
 */

#include "PluginInstance.h"
#include "../include/ownvst3_exports.h"

/* ── Lifecycle ─────────────────────────────────────────────────────────────── */

OWNVST3_API VST3PluginHandle VST3Plugin_Create()
{
    return new (std::nothrow) PluginInstance();
}

OWNVST3_API void VST3Plugin_Destroy(VST3PluginHandle handle)
{
    delete static_cast<PluginInstance*>(handle);
}

/* ── Loading ────────────────────────────────────────────────────────────────── */

OWNVST3_API bool VST3Plugin_LoadPlugin(VST3PluginHandle handle, const char* pluginPath)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->loadPlugin(pluginPath);
}

OWNVST3_API bool VST3Plugin_Initialize(VST3PluginHandle handle,
                                        double sampleRate, int maxBlockSize)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->initialize(sampleRate, maxBlockSize);
}

/* ── Metadata ───────────────────────────────────────────────────────────────── */

OWNVST3_API const char* VST3Plugin_GetName(VST3PluginHandle handle)
{
    if (!handle) return "";
    return static_cast<PluginInstance*>(handle)->getName();
}

OWNVST3_API const char* VST3Plugin_GetVendor(VST3PluginHandle handle)
{
    if (!handle) return "";
    return static_cast<PluginInstance*>(handle)->getVendor();
}

OWNVST3_API const char* VST3Plugin_GetVersion(VST3PluginHandle handle)
{
    if (!handle) return "";
    return static_cast<PluginInstance*>(handle)->getVersion();
}

OWNVST3_API const char* VST3Plugin_GetPluginInfo(VST3PluginHandle handle)
{
    if (!handle) return "";
    return static_cast<PluginInstance*>(handle)->getPluginInfo();
}

OWNVST3_API bool VST3Plugin_IsInstrument(VST3PluginHandle handle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->isInstrument();
}

OWNVST3_API bool VST3Plugin_IsEffect(VST3PluginHandle handle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->isEffect();
}

OWNVST3_API bool VST3Plugin_IsMidiOnly(VST3PluginHandle handle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->isMidiOnly();
}

OWNVST3_API int VST3Plugin_GetActualInputChannels(VST3PluginHandle handle)
{
    if (!handle) return 0;
    return static_cast<PluginInstance*>(handle)->getActualInputChannels();
}

OWNVST3_API int VST3Plugin_GetActualOutputChannels(VST3PluginHandle handle)
{
    if (!handle) return 0;
    return static_cast<PluginInstance*>(handle)->getActualOutputChannels();
}

/* ── Parameters ─────────────────────────────────────────────────────────────── */

OWNVST3_API int VST3Plugin_GetParameterCount(VST3PluginHandle handle)
{
    if (!handle) return 0;
    return static_cast<PluginInstance*>(handle)->getParameterCount();
}

OWNVST3_API bool VST3Plugin_GetParameterAt(VST3PluginHandle handle,
                                            int index,
                                            VST3ParameterC* outParam)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->getParameterAt(index, outParam);
}

OWNVST3_API bool VST3Plugin_SetParameter(VST3PluginHandle handle,
                                          int paramId, double value)
{
    if (!handle) return false;
    static_cast<PluginInstance*>(handle)->setParameter(paramId, value);
    return true;
}

OWNVST3_API double VST3Plugin_GetParameter(VST3PluginHandle handle, int paramId)
{
    if (!handle) return 0.0;
    return static_cast<PluginInstance*>(handle)->getParameter(paramId);
}

/* ── Audio ──────────────────────────────────────────────────────────────────── */

OWNVST3_API bool VST3Plugin_ProcessAudio(VST3PluginHandle handle, AudioBufferC* buffer)
{
    if (!handle || !buffer) return false;
    auto* inst = static_cast<PluginInstance*>(handle);
    return inst->processAudio(
        reinterpret_cast<float**>(buffer->inputs),
        buffer->numChannels,
        reinterpret_cast<float**>(buffer->outputs),
        buffer->numChannels,
        buffer->numSamples);
}

/* ── MIDI ───────────────────────────────────────────────────────────────────── */

OWNVST3_API bool VST3Plugin_ProcessMidi(VST3PluginHandle handle,
                                         const MidiEventC* events, int count)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->processMidi(events, count);
}

/* ── Transport ──────────────────────────────────────────────────────────────── */

OWNVST3_API void VST3Plugin_SetTempo(VST3PluginHandle handle, double bpm)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->setTempo(bpm);
}

OWNVST3_API void VST3Plugin_SetTransportState(VST3PluginHandle handle, bool isPlaying)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->setTransportState(isPlaying);
}

OWNVST3_API void VST3Plugin_ResetTransportPosition(VST3PluginHandle handle)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->resetTransportPosition();
}

/* ── Editor ─────────────────────────────────────────────────────────────────── */

OWNVST3_API bool VST3Plugin_HasEditor(VST3PluginHandle handle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->hasEditor();
}

OWNVST3_API bool VST3Plugin_CreateEditor(VST3PluginHandle handle, void* windowHandle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->createEditor(windowHandle);
}

OWNVST3_API void VST3Plugin_CloseEditor(VST3PluginHandle handle)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->closeEditor();
}

OWNVST3_API void VST3Plugin_ResizeEditor(VST3PluginHandle handle, int width, int height)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->resizeEditor(width, height);
}

OWNVST3_API bool VST3Plugin_GetEditorSize(VST3PluginHandle handle,
                                           int* width, int* height)
{
    if (!handle || !width || !height) return false;
    return static_cast<PluginInstance*>(handle)->getEditorSize(*width, *height);
}

OWNVST3_API bool VST3Plugin_IsEditorOpen(VST3PluginHandle handle)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->isEditorOpen();
}

OWNVST3_API void VST3Plugin_ProcessIdle(VST3PluginHandle handle)
{
    if (handle)
        static_cast<PluginInstance*>(handle)->processIdle();
}

/* ── State ──────────────────────────────────────────────────────────────────── */

OWNVST3_API bool VST3Plugin_GetState(VST3PluginHandle handle,
                                      uint8_t** outData, int* outLength)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->getState(outData, outLength);
}

OWNVST3_API bool VST3Plugin_SetState(VST3PluginHandle handle,
                                      const uint8_t* data, int length)
{
    if (!handle) return false;
    return static_cast<PluginInstance*>(handle)->setState(data, length);
}

OWNVST3_API void VST3Plugin_FreeStateData(uint8_t* data)
{
    delete[] data;
}

/* ── String cache ───────────────────────────────────────────────────────────── */

OWNVST3_API void VST3Plugin_ClearStringCache()
{
    // Instance-level caches are cleared when the instance is destroyed.
    // This global no-op export satisfies the binary contract with older callers
    // that resolved a process-wide string cache in the previous implementation.
}

/* ── SEH-safe dispatch (no-op) ──────────────────────────────────────────────── */

OWNVST3_API void VST3Plugin_SafeDispatchMessage(void* msg)
{
    // JUCE's MessageManager provides SEH protection internally on Windows.
    // This export is intentionally empty and exists solely so that the C#
    // NativeLibrary.TryGetExport() call in ThreadedVst3Wrapper succeeds.
    (void)msg;
}
