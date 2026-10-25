using System;
using System.Threading.Tasks;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Opens / closes a plugin editor. The native library creates and owns the window
    /// (CreateEditor with IntPtr.Zero) and drives it from its own UI thread, so there is
    /// no C# window and no idle thread here.
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private readonly Func<Task<EditorSize?>> _getEditorSizeAsync;
        private readonly ThreadedVst3Wrapper? _threadedWrapper;

        private bool _nativeOwnsWindow;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance using the synchronous low-level wrapper.
        /// </summary>
        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
            _getEditorSizeAsync = () => Task.FromResult(vst3Wrapper.GetEditorSize());
        }

        /// <summary>
        /// Initializes a new instance using the threaded wrapper (preferred).
        /// </summary>
        public VstEditorController(ThreadedVst3Wrapper threaded)
        {
            if (threaded == null) throw new ArgumentNullException(nameof(threaded));
            _threadedWrapper = threaded;
            _vst3Wrapper = threaded.InnerWrapper;
            _getEditorSizeAsync = () => Task.FromResult(_vst3Wrapper.GetEditorSize());
        }

        /// <summary>Whether we opened the editor and have not closed it since.</summary>
        public bool IsOpen => _nativeOwnsWindow;

        /// <summary>Gets a value indicating whether the editor is open.</summary>
        public bool IsEditorOpen => IsOpen;

        /// <summary>
        /// Opens the VST editor synchronously; the native library shows its own window.
        /// </summary>
        public void OpenEditor(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            var editorSize = _vst3Wrapper.GetEditorSize() ?? new EditorSize(800, 600);
            OpenEditorCore(editorSize);
        }

        /// <summary>
        /// Opens the VST editor without blocking the UI thread.
        /// Size is retrieved on the plugin thread before the native window is created.
        /// </summary>
        public async Task OpenEditorAsync(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            EditorSize? size = await _getEditorSizeAsync().ConfigureAwait(true);
            OpenEditorCore(size ?? new EditorSize(800, 600));
        }

        private void OpenEditorCore(EditorSize editorSize)
        {
            try
            {
                //We let the native side create its own top-level window; a handle here would only set the owner, not embed
                if (!_vst3Wrapper.CreateEditor(IntPtr.Zero))
                    throw new InvalidOperationException("Failed to create VST editor.");

                _nativeOwnsWindow = true;
            }
            catch
            {
                CloseEditor();
                throw;
            }
        }

        /// <summary>
        /// Closes the VST editor window and releases related resources.
        /// </summary>
        public void CloseEditor()
        {
            if (!_nativeOwnsWindow) return;

            _nativeOwnsWindow = false;
            try { _vst3Wrapper.CloseEditor(); }
            catch (Exception ex)
            { Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}"); }
        }

        /// <summary>Disposes of the editor controller and closes the editor if open.</summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                CloseEditor();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~VstEditorController() => Dispose();
    }
}
