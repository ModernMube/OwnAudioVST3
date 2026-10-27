namespace OwnVST3Host
{
    /// <summary>
    /// Editor inside a view the app owns instead of a window of its own - the only kind
    /// there is on iOS. AudioUnits only for now.
    /// </summary>
    public unsafe partial class OwnVst3Wrapper
    {
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte> _attachEditorFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, void> _detachEditorFunc;

        private void _initEmbedDelegates()
        {
            _attachEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)_tryExport("OwnPlugin_AttachEditorToView");
            _detachEditorFunc = (delegate* unmanaged[Cdecl]<IntPtr, void>)_tryExport("OwnPlugin_DetachEditor");
        }

        /// <summary>
        /// Puts the plugin's own view into view (a UIView on iOS, an NSView on macOS), filling it.
        /// UI thread. The view shows up a moment later, the plugin hands it over asynchronously.
        /// </summary>
        /// <returns>False with no editor, no view, a VST3, or a native library older than 2.1.</returns>
        public bool AttachEditorToView(IntPtr view)
        {
            CheckDisposed();
            if (_attachEditorFunc == null || view == IntPtr.Zero) return false;
            return _attachEditorFunc(_pluginHandle, view) != 0;
        }

        /// <summary>Takes the editor back out of the view. UI thread, safe to repeat.</summary>
        public void DetachEditor()
        {
            CheckDisposed();
            if (_detachEditorFunc != null) _detachEditorFunc(_pluginHandle);
        }
    }
}
