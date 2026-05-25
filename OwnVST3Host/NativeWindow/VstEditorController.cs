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

        private readonly ThreadedVst3Wrapper? _threadedWrapper;

        /// <summary>
        /// Dedicated STA thread that owns the editor window and runs the Win32 message loop.
        /// Windows-only; null on other platforms.
        /// </summary>
        private Thread? _editorThread;

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
            _threadedWrapper = threaded;
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
                await OpenEditorOnDedicatedThread(windowTitle, editorSize);
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

                    win.Invoke(() =>
                    {
                        try { _vst3Wrapper.CloseEditor(); }
                        catch (Exception ex)
                        { Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}"); }
                    });

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
        /// Windows: starts a fresh STA editor thread, calls OpenEditorCore on it,
        /// then keeps the thread alive running a standard Win32 GetMessage loop.
        /// This matches the threading model that JUCE-based plugins expect: the thread
        /// that calls attached() also owns the message pump for the editor's lifetime.
        /// </summary>
        private Task OpenEditorOnDedicatedThread(string windowTitle, EditorSize editorSize)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _editorThread = new Thread(() =>
            {
                try
                {
                    OpenEditorCore(windowTitle, editorSize);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    return;
                }
                tcs.TrySetResult();
                _nativeWindow?.RunMessageLoop();
            });
            _editorThread.Name = "VST Editor Thread";
            _editorThread.SetApartmentState(ApartmentState.STA);
            _editorThread.IsBackground = true;
            _editorThread.Start();
            return tcs.Task;
        }

        /// <summary>
        /// Creates the native window and attaches the VST editor. Platform-agnostic.
        /// On Windows, Invoke routes to the dedicated plugin STA thread (same as macOS/Linux).
        /// </summary>
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
                _nativeWindow.Invoke(() =>
                {
                    success = _vst3Wrapper.CreateEditor(windowHandle);
                });

                if (!success)
                {
                    CloseEditor();
                    throw new InvalidOperationException("Failed to create VST editor.");
                }

                _nativeWindow.BeginInvoke(() => _vst3Wrapper.ResizeEditor(editorSize.Width, editorSize.Height));
                
                _nativeWindow.Show();
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
            Interlocked.Exchange(ref _idlePending, 0);
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
                            Action idleAction = () =>
                            {
                                try
                                {
                                    if (_nativeWindow != null)
                                        _vst3Wrapper.ProcessIdle();
                                }
                                catch (Exception ex)
                                { Console.WriteLine($"[VST Editor] ProcessIdle error: {ex.Message}"); }
                                finally { Interlocked.Exchange(ref _idlePending, 0); }
                            };
                            win?.BeginInvoke(idleAction);
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
                
                if (_nativeWindow != null)
                {
                    _nativeWindow.OnClosed -= OnWindowClosed;
                    _nativeWindow.OnResize -= OnWindowResized;
                    _nativeWindow = null;
                }

                _vst3Wrapper.CloseEditor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Editor] OnWindowClosed error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the native window resized event.
        /// Dispatches ResizeEditor to the plugin thread so it runs on the same thread as attached().
        /// </summary>
        private void OnWindowResized(int width, int height)
        {
            var win = _nativeWindow;
            if (win == null) return;
            win.BeginInvoke(() =>
            {
                try { _vst3Wrapper.ResizeEditor(width, height); }
                catch (Exception ex)
                { Console.WriteLine($"[VST Editor] Resize error: {ex.Message}"); }
            });
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
