using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public partial class OwnVst3Wrapper
    {
        #region Native Function Delegates

        private delegate IntPtr VST3Plugin_CreateDelegate();
        private delegate void VST3Plugin_DestroyDelegate(IntPtr handle);
        private delegate bool VST3Plugin_LoadPluginDelegate(IntPtr handle, string pluginPath);
        private delegate bool VST3Plugin_CreateEditorDelegate(IntPtr handle, IntPtr windowHandle);
        private delegate void VST3Plugin_CloseEditorDelegate(IntPtr handle);
        private delegate void VST3Plugin_ResizeEditorDelegate(IntPtr handle, int width, int height);
        private delegate bool VST3Plugin_GetEditorSizeDelegate(IntPtr handle, out int width, out int height);
        private delegate int VST3Plugin_GetParameterCountDelegate(IntPtr handle);
        private delegate bool VST3Plugin_GetParameterAtDelegate(IntPtr handle, int index, ref VST3ParameterC parameter);
        private delegate bool VST3Plugin_SetParameterDelegate(IntPtr handle, int paramId, double value);
        private delegate double VST3Plugin_GetParameterDelegate(IntPtr handle, int paramId);
        private delegate bool VST3Plugin_InitializeDelegate(IntPtr handle, double sampleRate, int maxBlockSize);
        private delegate bool VST3Plugin_ProcessAudioDelegate(IntPtr handle, ref AudioBufferC buffer);
        private delegate bool VST3Plugin_ProcessMidiDelegate(IntPtr handle, [In] MidiEventC[] events, int eventCount);
        private delegate bool VST3Plugin_IsInstrumentDelegate(IntPtr handle);
        private delegate bool VST3Plugin_IsEffectDelegate(IntPtr handle);
        private delegate IntPtr VST3Plugin_GetNameDelegate(IntPtr handle);
        private delegate IntPtr VST3Plugin_GetVendorDelegate(IntPtr handle);
        private delegate IntPtr VST3Plugin_GetVersionDelegate(IntPtr handle);
        private delegate IntPtr VST3Plugin_GetPluginInfoDelegate(IntPtr handle);
        private delegate void VST3Plugin_ClearStringCacheDelegate();

        #endregion

        #region Private helper methods

        private void InitializeDelegates()
        {
            _createFunc = GetDelegate<VST3Plugin_CreateDelegate>("VST3Plugin_Create");
            _destroyFunc = GetDelegate<VST3Plugin_DestroyDelegate>("VST3Plugin_Destroy");
            _loadPluginFunc = GetDelegate<VST3Plugin_LoadPluginDelegate>("VST3Plugin_LoadPlugin");
            _createEditorFunc = GetDelegate<VST3Plugin_CreateEditorDelegate>("VST3Plugin_CreateEditor");
            _closeEditorFunc = GetDelegate<VST3Plugin_CloseEditorDelegate>("VST3Plugin_CloseEditor");
            _resizeEditorFunc = GetDelegate<VST3Plugin_ResizeEditorDelegate>("VST3Plugin_ResizeEditor");
            _getEditorSizeFunc = GetDelegate<VST3Plugin_GetEditorSizeDelegate>("VST3Plugin_GetEditorSize");
            _getParameterCountFunc = GetDelegate<VST3Plugin_GetParameterCountDelegate>("VST3Plugin_GetParameterCount");
            _getParameterAtFunc = GetDelegate<VST3Plugin_GetParameterAtDelegate>("VST3Plugin_GetParameterAt");
            _setParameterFunc = GetDelegate<VST3Plugin_SetParameterDelegate>("VST3Plugin_SetParameter");
            _getParameterFunc = GetDelegate<VST3Plugin_GetParameterDelegate>("VST3Plugin_GetParameter");
            _initializeFunc = GetDelegate<VST3Plugin_InitializeDelegate>("VST3Plugin_Initialize");
            _processAudioFunc = GetDelegate<VST3Plugin_ProcessAudioDelegate>("VST3Plugin_ProcessAudio");
            _processMidiFunc = GetDelegate<VST3Plugin_ProcessMidiDelegate>("VST3Plugin_ProcessMidi");
            _isInstrumentFunc = GetDelegate<VST3Plugin_IsInstrumentDelegate>("VST3Plugin_IsInstrument");
            _isEffectFunc = GetDelegate<VST3Plugin_IsEffectDelegate>("VST3Plugin_IsEffect");
            _getNameFunc = GetDelegate<VST3Plugin_GetNameDelegate>("VST3Plugin_GetName");
            _getVendorFunc = GetDelegate<VST3Plugin_GetVendorDelegate>("VST3Plugin_GetVendor");
            _getVersionFunc = GetDelegate<VST3Plugin_GetVersionDelegate>("VST3Plugin_GetVersion");
            _getPluginInfoFunc = GetDelegate<VST3Plugin_GetPluginInfoDelegate>("VST3Plugin_GetPluginInfo");
            _clearStringCacheFunc = GetDelegate<VST3Plugin_ClearStringCacheDelegate>("VST3Plugin_ClearStringCache");
        }

        private T GetDelegate<T>(string functionName) where T : Delegate
        {
            IntPtr funcPtr = NativeLibrary.GetExport(_libraryHandle, functionName);
            if (funcPtr == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException($"Function not found: {functionName}");
            }
            return Marshal.GetDelegateForFunctionPointer<T>(funcPtr);
        }
        
        #endregion
    }
}
