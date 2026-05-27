using System;
using System.Threading.Tasks;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Controller for managing a VST plugin editor window.
    ///
    /// On all platforms the JUCE native backend creates and owns its own window
    /// (juce::DocumentWindow) when CreateEditor is called with IntPtr.Zero.
    /// No C# native window wrapper is created — that would produce a second,
    /// empty window alongside the plugin editor.
    ///
    /// JUCE's message pump (JuceMessageThread on Windows/Linux, GCD on macOS)
    /// handles all UI dispatching internally, so no additional idle thread is needed.
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private readonly Func<Task<EditorSize?>> _getEditorSizeAsync;
        private readonly ThreadedVst3Wrapper? _threadedWrapper;

        private bool _juceOwnsWindow;
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

        /// <summary>Gets a value indicating whether the JUCE editor window is currently open.</summary>
        public bool IsOpen => _juceOwnsWindow;

        /// <summary>Gets a value indicating whether the editor is open.</summary>
        public bool IsEditorOpen => IsOpen;

        /// <summary>
        /// Opens the VST editor synchronously.
        /// JUCE creates and shows its own native window.
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
        /// Size is retrieved on the plugin thread before asking JUCE to create the window.
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
                // Pass IntPtr.Zero on all platforms so JUCE creates and owns its own
                // DocumentWindow. Passing a real handle here would still open a second
                // window because JUCE always creates its own HWND/NSWindow/X11 window —
                // the handle is only used on Windows to set the owner via SetWindowLongPtr,
                // which does NOT embed the JUCE window; it just changes window ordering.
                if (!_vst3Wrapper.CreateEditor(IntPtr.Zero))
                    throw new InvalidOperationException("Failed to create VST editor.");

                _juceOwnsWindow = true;
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
            if (!_juceOwnsWindow) return;

            _juceOwnsWindow = false;
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
