#pragma once

/*
 * ownvst3 C API. Names and struct layouts are frozen, the C# P/Invoke side binds to them.
 * ProcessAudio / ProcessMidi: audio thread only. Everything else: any thread.
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

typedef void* VST3PluginHandle;

typedef struct {
    int32_t      id;
    const char*  name;
    double       minValue;
    double       maxValue;
    double       defaultValue;
    double       currentValue;
} VST3ParameterC;

typedef struct {
    float** inputs;
    float** outputs;
    int32_t numChannels;
    int32_t numSamples;
} AudioBufferC;

typedef struct {
    uint8_t  status;
    uint8_t  data1;
    uint8_t  data2;
    uint8_t  _reserved;
    int32_t  sampleOffset;
} MidiEventC;

#define OWNPLUGIN_FORMAT_VST3       0x01
#define OWNPLUGIN_FORMAT_AUDIOUNIT  0x02
#define OWNPLUGIN_FORMAT_ALL        0x7fffffff

#define OWNPLUGIN_SCAN_FAST  0
#define OWNPLUGIN_SCAN_FULL  1

/* strings belong to the scanner until the next scan, copy them */
typedef struct {
    const char* name;
    const char* vendor;
    const char* version;
    const char* category;
    const char* identifier;
    const char* formatName;
    const char* fileOrPath;
    int32_t     isInstrument;
    int32_t     numInputs;
    int32_t     numOutputs;
    int32_t     uniqueId;
    int32_t     _reserved;
} PluginDescriptorC;

OWNVST3_API bool OwnPlugin_ScanStart(int formatMask);
OWNVST3_API bool OwnPlugin_ScanStartMode(int formatMask, int mode);

/* loads the plugin, blocks - not from the UI thread */
OWNVST3_API bool OwnPlugin_ResolveDescriptor(const char* identifier, PluginDescriptorC* out);

OWNVST3_API bool OwnPlugin_ScanIsRunning();
OWNVST3_API float OwnPlugin_ScanProgress();
OWNVST3_API const char* OwnPlugin_ScanCurrentItem();
OWNVST3_API void OwnPlugin_ScanCancel();
OWNVST3_API int OwnPlugin_GetScannedCount();
OWNVST3_API bool OwnPlugin_GetScannedAt(int index, PluginDescriptorC* out);
OWNVST3_API const char* OwnPlugin_GetScanCacheXml();
OWNVST3_API bool OwnPlugin_RestoreScanCacheXml(const char* xml);

OWNVST3_API VST3PluginHandle VST3Plugin_Create();
OWNVST3_API void VST3Plugin_Destroy(VST3PluginHandle handle);

OWNVST3_API bool VST3Plugin_LoadPlugin(VST3PluginHandle handle, const char* pluginPath);
OWNVST3_API bool OwnPlugin_LoadPluginAt(VST3PluginHandle handle, const char* pluginPath, int subIndex);
OWNVST3_API const char* OwnPlugin_GetFormatName(VST3PluginHandle handle);
OWNVST3_API const char* OwnPlugin_GetIdentifier(VST3PluginHandle handle);

/* can be called again to grow maxBlockSize */
OWNVST3_API bool VST3Plugin_Initialize(VST3PluginHandle handle, double sampleRate, int maxBlockSize);

OWNVST3_API const char* VST3Plugin_GetName(VST3PluginHandle handle);
OWNVST3_API const char* VST3Plugin_GetVendor(VST3PluginHandle handle);
OWNVST3_API const char* VST3Plugin_GetVersion(VST3PluginHandle handle);
OWNVST3_API const char* VST3Plugin_GetPluginInfo(VST3PluginHandle handle);
OWNVST3_API bool VST3Plugin_IsInstrument(VST3PluginHandle handle);
OWNVST3_API bool VST3Plugin_IsEffect(VST3PluginHandle handle);
OWNVST3_API bool VST3Plugin_IsMidiOnly(VST3PluginHandle handle);
OWNVST3_API int VST3Plugin_GetActualInputChannels(VST3PluginHandle handle);
OWNVST3_API int VST3Plugin_GetActualOutputChannels(VST3PluginHandle handle);
OWNVST3_API int VST3Plugin_GetLatencySamples(VST3PluginHandle handle);

OWNVST3_API int VST3Plugin_GetParameterCount(VST3PluginHandle handle);
OWNVST3_API bool VST3Plugin_GetParameterAt(VST3PluginHandle handle, int index, VST3ParameterC* outParam);
OWNVST3_API bool VST3Plugin_SetParameter(VST3PluginHandle handle, int paramId, double value);
OWNVST3_API double VST3Plugin_GetParameter(VST3PluginHandle handle, int paramId);

/* false when numSamples > maxBlockSize, buffers untouched then */
OWNVST3_API bool VST3Plugin_ProcessAudio(VST3PluginHandle handle, AudioBufferC* buffer);
OWNVST3_API bool VST3Plugin_ProcessMidi(VST3PluginHandle handle, const MidiEventC* events, int count);

OWNVST3_API void VST3Plugin_SetTempo(VST3PluginHandle handle, double bpm);
OWNVST3_API void VST3Plugin_SetTransportState(VST3PluginHandle handle, bool isPlaying);
OWNVST3_API void VST3Plugin_ResetTransportPosition(VST3PluginHandle handle);
OWNVST3_API void VST3Plugin_SetBypass(VST3PluginHandle handle, bool bypassed);

OWNVST3_API bool VST3Plugin_HasEditor(VST3PluginHandle handle);
OWNVST3_API bool VST3Plugin_CreateEditor(VST3PluginHandle handle, void* windowHandle);
OWNVST3_API void VST3Plugin_CloseEditor(VST3PluginHandle handle);
OWNVST3_API void VST3Plugin_ResizeEditor(VST3PluginHandle handle, int width, int height);
OWNVST3_API bool VST3Plugin_GetEditorSize(VST3PluginHandle handle, int* width, int* height);
OWNVST3_API bool VST3Plugin_IsEditorOpen(VST3PluginHandle handle);
OWNVST3_API void VST3Plugin_ProcessIdle(VST3PluginHandle handle);

/* free the buffer with VST3Plugin_FreeStateData */
OWNVST3_API bool VST3Plugin_GetState(VST3PluginHandle handle, uint8_t** outData, int* outLength);
OWNVST3_API bool VST3Plugin_SetState(VST3PluginHandle handle, const uint8_t* data, int length);
OWNVST3_API void VST3Plugin_FreeStateData(uint8_t* data);

OWNVST3_API void VST3Plugin_ClearStringCache();
OWNVST3_API void VST3Plugin_SafeDispatchMessage(void* msg);

#ifdef __cplusplus
}
#endif
