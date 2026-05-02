using System;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Controller for managing a VST plugin editor window.
    ///
    /// Threading model:
    ///   Native window thread – HWND/NSWindow lives here with its own OS message loop.
    ///   Caller (UI) thread   – CreateEditor / CloseEditor run here (VST3 requirement: the
    ///                          plugin's attached()/removed() callbacks must be on the same
    ///                          thread that created the host view).
    ///   Idle thread          – dedicated high-priority thread that calls ProcessIdle at 50 Hz.
    ///
    /// When constructed from a ThreadedVst3Wrapper, GetEditorSize is fetched asynchronously
    /// on the plugin thread so that OpenEditorAsync never blocks the UI thread.
    /// </summary>
    public class VstEditorController : IDisposable
    {
        private readonly OwnVst3Wrapper _vst3Wrapper;
        private readonly Func<Task<EditorSize?>> _getEditorSizeAsync;

        private INativeWindow? _nativeWindow;
        private Thread? _idleThread;
        private CancellationTokenSource? _idleCancellation;
        private bool _disposed;

        // Throttle flag: 0 = idle slot free, 1 = a BeginInvoke(ProcessIdle) is already
        // queued on the main RunLoop but has not executed yet.
        // This prevents callback accumulation during macOS modal tracking loops
        // (e.g. dropdown menus), which would cause a UI flood/freeze the moment
        // the menu closes.
        private volatile int _idlePending; // 0 or 1

        private const int IdleIntervalMs = 20; // 50 Hz

        // ------------------------------------------------------------------
        // Constructors
        // ------------------------------------------------------------------

        /// <summary>
        /// Backward-compatible constructor. GetEditorSize is fetched synchronously
        /// (wraps the blocking call in a completed Task).
        /// </summary>
        public VstEditorController(OwnVst3Wrapper vst3Wrapper)
        {
            _vst3Wrapper = vst3Wrapper ?? throw new ArgumentNullException(nameof(vst3Wrapper));
            _getEditorSizeAsync = () => Task.FromResult(vst3Wrapper.GetEditorSize());
        }

        /// <summary>
        /// Constructor for ThreadedVst3Wrapper. GetEditorSize is fetched asynchronously
        /// on the dedicated plugin thread, so the UI thread is never blocked.
        /// </summary>
        public VstEditorController(ThreadedVst3Wrapper threaded)
        {
            if (threaded == null) throw new ArgumentNullException(nameof(threaded));
            _vst3Wrapper = threaded.InnerWrapper;
            _getEditorSizeAsync = () => Task.FromResult(_vst3Wrapper.GetEditorSize());
        }

        // ------------------------------------------------------------------
        // Public state
        // ------------------------------------------------------------------

        public bool IsOpen => _nativeWindow?.IsOpen ?? false;
        public bool IsEditorOpen => IsOpen;

        // ------------------------------------------------------------------
        // OpenEditor (synchronous – backward compatible)
        // ------------------------------------------------------------------

        /// <summary>
        /// Opens the VST editor window synchronously.
        /// The HWND/NSWindow is created on a dedicated native-window thread; CreateEditor
        /// runs on the caller (UI) thread to satisfy the VST3/macOS threading requirement.
        /// </summary>
        public void OpenEditor(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            // Synchronously fetch size (fast – just reads a cached value from the native lib).
            var editorSize = _vst3Wrapper.GetEditorSize() ?? new EditorSize(800, 600);
            OpenEditorCore(windowTitle, editorSize);
        }

        // ------------------------------------------------------------------
        // OpenEditorAsync (non-blocking – preferred when using ThreadedVst3Wrapper)
        // ------------------------------------------------------------------

        /// <summary>
        /// Opens the VST editor window without blocking the UI thread.
        /// GetEditorSize is awaited on the plugin thread; window creation and CreateEditor
        /// still run on the caller (UI) thread to satisfy the VST3/macOS requirement.
        /// </summary>
        public async Task OpenEditorAsync(string windowTitle)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VstEditorController));
            if (IsOpen) return;

            // Fetch editor size on the plugin thread – does not block the UI thread.
            EditorSize? size = await _getEditorSizeAsync().ConfigureAwait(true);
            // ConfigureAwait(true) → resumes on the original (UI) thread, which is required
            // because the rest of this method calls CreateEditor on the UI thread.

            var editorSize = size ?? new EditorSize(800, 600);
            OpenEditorCore(windowTitle, editorSize);
        }

        // ------------------------------------------------------------------
        // CloseEditor
        // ------------------------------------------------------------------

        public void CloseEditor()
        {
            try
            {
                StopIdleThread();

                if (_nativeWindow != null)
                {
                    var win = _nativeWindow;
                    _nativeWindow = null;

                    // CloseEditor (VST removed()) runs on the caller (UI) thread.
                    try { _vst3Wrapper.CloseEditor(); }
                    catch (Exception ex)
                    { Console.WriteLine($"[VST Editor] Error closing editor: {ex.Message}"); }

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

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Core window + editor setup. Must be called from the UI thread.
        /// </summary>
        private void OpenEditorCore(string windowTitle, EditorSize editorSize)
        {
            try
            {
                _nativeWindow = NativeWindowFactory.Create();
                _nativeWindow.OnClosed += OnWindowClosed;
                _nativeWindow.OnResize += OnWindowResized;

                // The native OS window is created on a dedicated thread (or the macOS main
                // thread via dispatch_sync). After Open() returns the handle is ready.
                _nativeWindow.Open(windowTitle, editorSize.Width, editorSize.Height);

                IntPtr windowHandle = _nativeWindow.GetHandle();

                // CRITICAL: CreateEditor runs on the CALLER (UI) thread.
                // The native window thread pumps messages so plugin SendMessage calls are
                // handled there, preventing cross-thread deadlocks.
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
        /// Dedicated idle thread: calls ProcessIdle at 50 Hz, independent of the ThreadPool.
        /// On macOS, BeginInvoke marshals to the main run loop via CFRunLoopTimer so that
        /// plugin dropdown menus work even under heavy background load.
        ///
        /// Throttle: at most ONE ProcessIdle callback is pending on the RunLoop at any
        /// time (_idlePending flag). During macOS modal tracking (dropdown menu) the
        /// main thread is blocked in NSEventTrackingRunLoopMode and cannot drain the
        /// queued callbacks. Without the flag, the idle thread would keep adding new
        /// CFRunLoopTimers every 20 ms; when the menu closes they all fire at once,
        /// flooding the JUCE timer loop and causing the apparent UI freeze.
        /// </summary>
        private void IdleThreadProc(object? state)
        {
            var ct = (CancellationToken)state!;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Only post a new idle callback if the previous one has already run.
                        if (!_disposed && _nativeWindow != null &&
                            Interlocked.CompareExchange(ref _idlePending, 1, 0) == 0)
                        {
                            var win = _nativeWindow; // capture before lambda
                            win?.BeginInvoke(() =>
                            {
                                try { _vst3Wrapper.ProcessIdle(); }
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

        private void OnWindowResized(int width, int height)
        {
            try { _vst3Wrapper.ResizeEditor(width, height); }
            catch (Exception ex)
            { Console.WriteLine($"[VST Editor] Resize error: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

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
