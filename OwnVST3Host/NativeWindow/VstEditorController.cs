using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Controller for managing a VST plugin editor window.
    ///
    /// Threading model:
    /// - The native window (HWND) runs on a dedicated thread with its own Win32 message loop
    /// - CreateEditor/CloseEditor calls run on the CALLER thread (where the plugin was loaded)
    /// - During attached(), the HWND thread freely pumps messages
    /// - This prevents cross-thread SendMessage deadlocks
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private INativeWindow? _nativeWindow;
        private Thread? _idleThread;
        private CancellationTokenSource? _idleCancellation;
        private bool _disposed = false;

        private const int IdleIntervalMs = 20; // 50Hz for GUI updates

        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
        }

        public bool IsOpen => _nativeWindow?.IsOpen ?? false;

        public bool IsEditorOpen => IsOpen;

        /// <summary>
        /// Opens the VST editor window.
        /// The HWND is created on a dedicated thread (with its own message loop),
        /// but CreateEditor runs on the caller thread (where the controller was created).
        /// </summary>
        public void OpenEditor(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));

            if (IsOpen) return;

            try
            {
                var editorSize = _vst3Wrapper.GetEditorSize() ?? new EditorSize(800, 600);

                _nativeWindow = NativeWindowFactory.Create();
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                // Create window on dedicated thread (on Windows with its own message loop)
                _nativeWindow.Open(windowTitle, editorSize.Width, editorSize.Height);

                IntPtr windowHandle = _nativeWindow.GetHandle();

                // CRITICAL: CreateEditor runs on the CALLER (Avalonia UI) thread!
                // The controller and view were created on this thread.
                // The HWND, however, is on the dedicated window thread, which is running and pumping
                // messages. Thus during attached():
                // - If the plugin sends SendMessage to the HWND → the window thread processes it
                // - If the plugin makes a COM call → it runs on the Avalonia thread (direct call)
                // - No deadlock!
                bool success = _vst3Wrapper.CreateEditor(windowHandle);

                if (!success)
                {
                    CloseEditor();
                    throw new InvalidOperationException("Failed to create VST editor!");
                }

                // Start dedicated idle thread with high priority
                // This guarantees that ProcessIdle runs even when ThreadPool is under load
                _idleCancellation = new CancellationTokenSource();
                _idleThread = new Thread(IdleThreadProc)
                {
                    Name = "VST Editor Idle Thread",
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = true
                };
                _idleThread.Start(_idleCancellation.Token);
            }
            catch
            {
                CloseEditor();
                throw;
            }
        }

        public void CloseEditor()
        {
            try
            {
                // Stop idle thread
                if (_idleCancellation != null)
                {
                    _idleCancellation.Cancel();
                    _idleCancellation.Dispose();
                    _idleCancellation = null;
                }

                if (_idleThread != null)
                {
                    // Wait maximum 1 second for thread to finish
                    if (!_idleThread.Join(1000))
                    {
                        Console.WriteLine("[VST Editor] Warning: Idle thread did not stop within timeout");
                    }
                    _idleThread = null;
                }

                if (_nativeWindow != null)
                {
                    var win = _nativeWindow;
                    _nativeWindow = null;

                    // Close VST editor on caller thread
                    try
                    {
                        _vst3Wrapper.CloseEditor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}");
                    }

                    win.OnClosed -= OnWindowClosed;
                    win.OnResize -= OnWindowResized;
                    win.Close();
                    win.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing VST editor: {ex.Message}");
            }
        }

        private void OnWindowClosed()
        {
            // The window was closed with the close button (X) - called from window thread
            try
            {
                // Stop idle thread
                _idleCancellation?.Cancel();
                _idleCancellation?.Dispose();
                _idleCancellation = null;

                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] Error in OnWindowClosed: {ex.Message}");
            }

            if (_nativeWindow != null)
            {
                _nativeWindow.OnClosed -= OnWindowClosed;
                _nativeWindow.OnResize -= OnWindowResized;
                _nativeWindow = null;
            }
        }

        /// <summary>
        /// Dedicated idle thread, independent of the ThreadPool.
        /// Runs with high priority to ensure ProcessIdle calls even under heavy background load
        /// (supports macOS dropdown menus).
        /// </summary>
        private void IdleThreadProc(object? state)
        {
            var cancellationToken = (CancellationToken)state!;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Only work if the window is still open
                        if (!_disposed && _nativeWindow != null)
                        {
                            // ProcessIdle runs on the native window thread
                            // On macOS this is marshaled to the main thread
                            _nativeWindow.BeginInvoke(() => _vst3Wrapper.ProcessIdle());
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VST Editor] Error in IdleThreadProc: {ex.Message}");
                    }

                    // 50Hz refresh rate (20ms)
                    // Use WaitHandle.WaitOne for fast shutdown
                    cancellationToken.WaitHandle.WaitOne(IdleIntervalMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] Fatal error in IdleThreadProc: {ex.Message}");
            }
        }

        private void OnWindowResized(int width, int height)
        {
            try
            {
                _vst3Wrapper.ResizeEditor(width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resizing VST editor: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CloseEditor();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~VstEditorController()
        {
            Dispose();
        }
    }
}
