using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public unsafe partial class OwnVst3Wrapper
    {
        #region Native entry points

        // Raw function pointers rather than Marshal.GetDelegateForFunctionPointer delegates:
        // no marshalling stub per call on the audio path, and nothing for the AOT compiler to
        // generate at runtime. Every export is Cdecl and every native bool is one byte, so
        // they come back as byte here and the callers compare against zero.

        private delegate* unmanaged[Cdecl]<IntPtr> _createFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _destroyFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte*, byte> _loadPluginFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte> _hasEditorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte> _createEditorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _closeEditorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int, int, void> _resizeEditorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int*, int*, byte> _getEditorSizeFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int> _getParameterCountFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int, VST3ParameterC*, byte> _getParameterAtFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int, double, byte> _setParameterFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int, double> _getParameterFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, double, int, byte> _initializeFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, AudioBufferC*, byte> _processAudioFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, MidiEventC*, int, byte> _processMidiFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte> _isInstrumentFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte> _isEffectFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte> _isMidiOnlyFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getNameFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getVendorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getVersionFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getPluginInfoFunc;
        private delegate* unmanaged[Cdecl]<void> _clearStringCacheFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _processIdleFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte> _isEditorOpenFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int> _getActualInputChannelsFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int> _getActualOutputChannelsFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, int> _getLatencySamplesFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, double, void> _setTempoFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte, void> _setTransportStateFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte, void> _setBypassFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _resetTransportPositionFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, byte> _getStateFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, byte*, int, byte> _setStateFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _freeStateDataFunc;

        #endregion

        #region Private helper methods

        private void InitializeDelegates()
        {
            _createFunc = (delegate* unmanaged[Cdecl]<IntPtr>)_export("VST3Plugin_Create");
            _destroyFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_export("VST3Plugin_Destroy");
            _loadPluginFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)_export("VST3Plugin_LoadPlugin");
            _hasEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte>)_tryExport("VST3Plugin_HasEditor");
            _createEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)_export("VST3Plugin_CreateEditor");
            _closeEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_export("VST3Plugin_CloseEditor");
            _resizeEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, int, int, void>)_export("VST3Plugin_ResizeEditor");
            _getEditorSizeFunc = (delegate* unmanaged[Cdecl]<IntPtr, int*, int*, byte>)_export("VST3Plugin_GetEditorSize");
            _getParameterCountFunc = (delegate* unmanaged[Cdecl]<IntPtr, int>)_export("VST3Plugin_GetParameterCount");
            _getParameterAtFunc = (delegate* unmanaged[Cdecl]<IntPtr, int, VST3ParameterC*, byte>)_export("VST3Plugin_GetParameterAt");
            _setParameterFunc = (delegate* unmanaged[Cdecl]<IntPtr, int, double, byte>)_export("VST3Plugin_SetParameter");
            _getParameterFunc = (delegate* unmanaged[Cdecl]<IntPtr, int, double>)_export("VST3Plugin_GetParameter");
            _initializeFunc = (delegate* unmanaged[Cdecl]<IntPtr, double, int, byte>)_export("VST3Plugin_Initialize");
            _processAudioFunc = (delegate* unmanaged[Cdecl]<IntPtr, AudioBufferC*, byte>)_export("VST3Plugin_ProcessAudio");
            _processMidiFunc = (delegate* unmanaged[Cdecl]<IntPtr, MidiEventC*, int, byte>)_export("VST3Plugin_ProcessMidi");
            _isInstrumentFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte>)_export("VST3Plugin_IsInstrument");
            _isEffectFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte>)_export("VST3Plugin_IsEffect");
            _isMidiOnlyFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte>)_tryExport("VST3Plugin_IsMidiOnly");
            _getNameFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_export("VST3Plugin_GetName");
            _getVendorFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_export("VST3Plugin_GetVendor");
            _getVersionFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_tryExport("VST3Plugin_GetVersion");
            _getPluginInfoFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_export("VST3Plugin_GetPluginInfo");
            _clearStringCacheFunc = (delegate* unmanaged[Cdecl]<void>)_export("VST3Plugin_ClearStringCache");
            _processIdleFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_tryExport("VST3Plugin_ProcessIdle");
            _isEditorOpenFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte>)_tryExport("VST3Plugin_IsEditorOpen");
            _getActualInputChannelsFunc = (delegate* unmanaged[Cdecl]<IntPtr, int>)_tryExport("VST3Plugin_GetActualInputChannels");
            _getActualOutputChannelsFunc = (delegate* unmanaged[Cdecl]<IntPtr, int>)_tryExport("VST3Plugin_GetActualOutputChannels");
            _getLatencySamplesFunc = (delegate* unmanaged[Cdecl]<IntPtr, int>)_tryExport("VST3Plugin_GetLatencySamples");
            _setTempoFunc = (delegate* unmanaged[Cdecl]<IntPtr, double, void>)_tryExport("VST3Plugin_SetTempo");
            _setTransportStateFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)_tryExport("VST3Plugin_SetTransportState");
            _setBypassFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)_tryExport("VST3Plugin_SetBypass");
            _resetTransportPositionFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_tryExport("VST3Plugin_ResetTransportPosition");
            _getStateFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, byte>)_tryExport("VST3Plugin_GetState");
            _setStateFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, byte>)_tryExport("VST3Plugin_SetState");
            _freeStateDataFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_tryExport("VST3Plugin_FreeStateData");

            _initFormatDelegates();
        }

        private void* _export(string functionName)
        {
            IntPtr funcPtr = NativeLibrary.GetExport(_libraryHandle, functionName);
            if (funcPtr == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"Function not found: {functionName}");
            }
            return (void*)funcPtr;
        }

        private void* _tryExport(string functionName)
        {
            if (NativeLibrary.TryGetExport(_libraryHandle, functionName, out IntPtr funcPtr))
                return (void*)funcPtr;
            return null;
        }

        /// <summary>
        /// Native UTF-8 copy of s. The caller frees it with Marshal.FreeCoTaskMem.
        /// </summary>
        private static byte* _toUtf8(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);

        #endregion
    }
}
