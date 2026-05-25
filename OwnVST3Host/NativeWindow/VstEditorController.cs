using System;
using System.Threading;
using System.Threading.Tasks;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Controller for managing a VST plugin editor window.
    /// Manages native window thread, caller thread synchronization, and idle processing.
    /// Uses dedicated threads for UI and idle operations to prevent deadlocks.
    /// </summary>
    public class VstEditorController : IDisposable
    {
        /// <summary>
        /// The inner VST3 wrapper instance.
        /// </summary>
        private readonly OwnVst3Wrapper _vst3Wrapper;

        /// <summary>
        /// Function to asynchronously retrieve the editor size.
        /// </summary>
        private readonly Func<Task<EditorSize?>> _getEditorSizeAsync;

        /// <summary>
        /// The native window instance.
        /// </summary>
        private INativeWindow? _nativeWindow;

        /// <summary>
        /// The dedicated idle processing thread.
        /// </summary>
        private Thread? _idleThread;

        /// <summary>
        /// Token source for canceling the idle thread.
        /// </summary>
        private CancellationTokenSource? _idleCancellation;

        /// <summary>
        /// Indicates whether the object has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Throttle flag for pending idle processing invocations.
        /// Value is 0 if free, 1 if a pending invocation exists.
        /// </summary>
        private volatile int _idlePending;

        /// <summary>
        /// Interval in milliseconds for the idle thread (50 Hz).
        /// </summary>
        private const int IdleIntervalMs = 20;

        /// <summary>
        /// Initializes a new instance of the VstEditorController class.
        /// Wraps the synchronous size retrieval in a completed Task.
        /// </summary>
        /// <param name="vst3Wrapper">The VST3 wrapper instance.</param>
        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
            _getEditorSizeAsync = () => Task.FromResult(vst3Wrapper.GetEditorSize());
        }

        /// <summary>
        /// Initializes a new instance of the VstEditorController class for threaded wrappers.
        /// Allows asynchronous editor size retrieval without blocking UI.
        /// </summary>
        /// <param name="threaded">The threaded VST3 wrapper instance.</param>
        public VstEditorController(ThreadedVst3Wrapper threaded)
        {
            if (threaded == null) throw new ArgumentNullException(nameof(threaded));
            _vst3Wrapper = threaded.InnerWrapper;
            _getEditorSizeAsync = () => Task.FromResult(_vst3Wrapper.GetEditorSize());
        }

        /// <summary>
        /// Gets a value indicating whether the editor window is currently open.
        /// </summary>
        public bool IsOpen => _nativeWindow?.IsOpen ?? false;

        /// <summary>
        /// Gets a value indicating whether the editor is open.
        /// </summary>
        public bool IsEditorOpen => IsOpen;

        /// <summary>
        /// Opens the VST editor window synchronously.
        /// Creates the window on a dedicated native-window thread.
        /// </summary>
        /// <param name="windowTitle">The title of the editor window.</param>
        public void OpenEditor(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            var editorSize = _vst3Wrapper.GetEditorSize() ?? new EditorSize(800, 600);
            OpenEditorCore(windowTitle, editorSize);
        }

        /// <summary>
        /// Opens the VST editor window without blocking the UI thread.
        /// Awaits size retrieval on the plugin thread.
        /// </summary>
        /// <param name="windowTitle">The title of the editor window.</param>
        public async Task OpenEditorAsync(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            EditorSize? size = await _getEditorSizeAsync().ConfigureAwait(true);

            var editorSize = size ?? new EditorSize(800, 600);

            if (OperatingSystem.IsWindows())
                await OpenEditorCoreWindowsAsync(windowTitle, editorSize);
            else
                OpenEditorCore(windowTitle, editorSize);
        }

        /// <summary>
        /// Closes the VST editor window and releases related resources.
        /// Also stops the background idle processing thread.
        /// </summary>
        public void CloseEditor()
        {
            try
            {
                StopIdleThread();

                if (_nativeWindow != null)
                {
                    var win = _nativeWindow;
                    _nativeWindow = null;

                    Action doClose = () =>
                    {
                        try { _vst3Wrapper.CloseEditor(); }
                        catch (Exception ex)
                        { Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}"); }
                    };
                    if (OperatingSystem.IsMacOS())
                        win.Invoke(doClose);
                    else
                        doClose();

                    win.OnClosed -= OnWindowClosed;
                    win.OnResize -= OnWindowResized;
                    win.Close();
                    win.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] Error in CloseEditor: {ex.Message}");
            }
        }

        /// <summary>
        /// Windows-specific editor creation. Mirrors the macOS strategy exactly:
        /// on macOS everything (NSWindow, NSView, VST subviews) lives on the main thread.
        /// Here the HWND is created on the Avalonia UI thread (creator thread) so that
        /// the parent window and all VST child windows created during CreateEditor share
        /// the same message thread. Avalonia's message loop services all of them.
        ///
        /// This eliminates cross-thread SendMessage between parent and children that
        /// caused deadlocks and the frozen white window with JUCE-based plugins and others
        /// that call SendMessage on the parent HWND during IPlugView::attached().
        ///
        /// The Task.Delay gives DWM time to finish compositing the window before the
        /// plugin attaches its child HWND — without this, some plugins (e.g. TDR Nova)
        /// only see a white window because the compositor hasn't finished initializing.
        /// </summary>
        private async Task OpenEditorCoreWindowsAsync(string windowTitle, EditorSize editorSize)
        {
            try
            {
                _nativeWindow = NativeWindowFactory.Create();
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                _nativeWindow.Open(windowTitle, editorSize.Width, editorSize.Height);

                await Task.Delay(50).ConfigureAwait(true);

                IntPtr windowHandle = _nativeWindow.GetHandle();

                bool success = _vst3Wrapper.CreateEditor(windowHandle);

                if (!success)
                {
                    CloseEditor();
                    throw new InvalidOperationException("Failed to create VST editor.");
                }

                StartIdleThread();
            }
            catch
            {
                CloseEditor();
                throw;
            }
        }

        /// <summary>
        /// Core window and editor setup executed from the UI thread.
        /// Initializes the native window and attaches the VST editor.
        /// </summary>
        /// <param name="windowTitle">The title of the window.</param>
        /// <param name="editorSize">The initial size of the editor.</param>
        private void OpenEditorCore(string windowTitle, EditorSize editorSize)
        {
            try
            {
                _nativeWindow = NativeWindowFactory.Create();
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                _nativeWindow.Open(windowTitle, editorSize.Width, editorSize.Height);

                IntPtr windowHandle = _nativeWindow.GetHandle();

                bool success = false;
                _nativeWindow.Invoke(() => success = _vst3Wrapper.CreateEditor(windowHandle));

                if (!success)
                {
                    CloseEditor();
                    throw new InvalidOperationException("Failed to create VST editor.");
                }

                StartIdleThread();
            }
            catch
            {
                CloseEditor();
                throw;
            }
        }

        /// <summary>
        /// Starts the dedicated background idle processing thread.
        /// </summary>
        private void StartIdleThread()
        {
            _idleCancellation = new CancellationTokenSource();
            _idleThread = new Thread(IdleThreadProc)
            {
                Name = "VST Editor Idle Thread",
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true
            };
            _idleThread.Start(_idleCancellation.Token);
        }

        /// <summary>
        /// Stops the background idle processing thread and cleans up resources.
        /// </summary>
        private void StopIdleThread()
        {
            if (_idleCancellation != null)
            {
                _idleCancellation.Cancel();
                _idleCancellation.Dispose();
                _idleCancellation = null;
            }

            if (_idleThread != null)
            {
                if (!_idleThread.Join(1000))
                    Console.WriteLine("[VST Editor] Warning: Idle thread did not stop within timeout.");
                _idleThread = null;
            }
        }

        /// <summary>
        /// Procedure for the dedicated idle processing thread.
        /// Periodically invokes ProcessIdle callbacks using the native window message loop.
        /// </summary>
        /// <param name="state">The cancellation token state.</param>
        private void IdleThreadProc(object? state)
        {
            var ct = (CancellationToken)state!;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (!OperatingSystem.IsMacOS() &&
                            !_disposed && _nativeWindow != null &&
                            Interlocked.CompareExchange(ref _idlePending, 1, 0) == 0)
                        {
                            var win = _nativeWindow;
                            win?.BeginInvoke(() =>
                            {
                                try
                                {
                                    if (_nativeWindow != null)
                                        _vst3Wrapper.ProcessIdle();
                                }
                                catch (Exception ex)
                                { Console.WriteLine($"[VST Editor] ProcessIdle error: {ex.Message}"); }
                                finally { Interlocked.Exchange(ref _idlePending, 0); }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VST Editor] IdleThreadProc error: {ex.Message}");
                    }

                    ct.WaitHandle.WaitOne(IdleIntervalMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] Fatal IdleThreadProc error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the native window closed event.
        /// Cleans up editor resources.
        /// </summary>
        private void OnWindowClosed()
        {
            try
            {
                StopIdleThread();
                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] OnWindowClosed error: {ex.Message}");
            }

            if (_nativeWindow != null)
            {
                _nativeWindow.OnClosed -= OnWindowClosed;
                _nativeWindow.OnResize -= OnWindowResized;
                _nativeWindow = null;
            }
        }

        /// <summary>
        /// Handles the native window resized event.
        /// Notifies the VST editor wrapper of the new dimensions.
        /// </summary>
        /// <param name="width">The new width.</param>
        /// <param name="height">The new height.</param>
        private void OnWindowResized(int width, int height)
        {
            try { _vst3Wrapper.ResizeEditor(width, height); }
            catch (Exception ex)
            { Console.WriteLine($"[VST Editor] Resize error: {ex.Message}"); }
        }

        /// <summary>
        /// Disposes of the native window resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                CloseEditor();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Finalizes an instance of the VstEditorController class.
        /// </summary>
        ~VstEditorController() => Dispose();
    }
}
