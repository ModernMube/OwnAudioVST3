using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows native window management.
    ///
    /// Design: the window is created synchronously on the thread that calls Open().
    /// That thread is expected to be an STA thread that runs a Win32 message loop
    /// (e.g. ThreadedVst3Wrapper's plugin thread). This guarantees that JUCE's
    /// MessageManager and the parent HWND live on the exact same thread, preventing
    /// cross-thread deadlocks and crashes.
    ///
    /// Cross-thread Invoke/BeginInvoke calls post a WM_USER_INVOKE message to the
    /// window thread; the WndProc drains the invoke queue and executes the actions.
    /// </summary>
    internal class NativeWindowWindows : INativeWindow
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();
        private bool _disposed;
        private WndProcDelegate? _wndProcDelegate;
        private string? _windowClassName;
        private int _creatorThreadId;

        /// <summary>Gets a value indicating whether the window is currently open.</summary>
        public bool IsOpen => _hwnd != IntPtr.Zero;

        /// <summary>Gets a value indicating whether the window is the current foreground window.</summary>
        public bool IsActive
        {
            get
            {
                if (_hwnd == IntPtr.Zero) return false;
                return GetForegroundWindow() == _hwnd;
            }
        }

        /// <summary>Raised when the window is resized.</summary>
        public event Action<int, int>? OnResize;

        /// <summary>Raised when the window is closed by the user or programmatically.</summary>
        public event Action? OnClosed;

        private sealed class InvokeItem
        {
            public Action Action { get; }
            public ManualResetEvent? SyncSignal { get; }
            public Exception? Exception { get; set; }

            public InvokeItem(Action action, ManualResetEvent? syncSignal = null)
            {
                Action = action;
                SyncSignal = syncSignal;
            }
        }

        #region Win32 constants

        private const int WS_OVERLAPPED   = 0x00000000;
        private const int WS_CAPTION      = 0x00C00000;
        private const int WS_SYSMENU      = 0x00080000;
        private const int WS_THICKFRAME   = 0x00040000;
        private const int WS_MINIMIZEBOX  = 0x00020000;
        private const int WS_MAXIMIZEBOX  = 0x00010000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;

        private const int WS_VST_WINDOW =
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU |
            WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
            WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

        private const int CW_USEDEFAULT = unchecked((int)0x80000000);
        private const int SW_SHOW       = 5;

        private const uint WM_ERASEBKGND   = 0x0014;
        private const uint WM_SIZE         = 0x0005;
        private const uint WM_CLOSE        = 0x0010;
        private const uint WM_DESTROY      = 0x0002;
        private const uint WM_USER_INVOKE  = 0x0400 + 103;
        private const uint WM_USER_SHOW    = 0x0400 + 102;

        private const int CS_DBLCLKS = 0x0008;
        private const int IDC_ARROW  = 32512;

        #endregion

        #region Win32 structs

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        #endregion

        #region P/Invoke

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        /// <summary>
        /// Creates the HWND on the calling thread. The caller must be an STA thread
        /// with a message pump (e.g. ThreadedVst3Wrapper.PluginThreadProc).
        /// </summary>
        public void Open(string title, int width, int height)
        {
            if (_hwnd != IntPtr.Zero)
                throw new InvalidOperationException("Window is already open!");

            _creatorThreadId = (int)GetCurrentThreadId();

            IntPtr hInstance = GetModuleHandle(null);
            _windowClassName = $"OwnVST3HostWindow_{Environment.TickCount}_{GetHashCode()}";
            _wndProcDelegate = WndProc;

            WNDCLASS wc = new WNDCLASS
            {
                style         = (uint)CS_DBLCLKS,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance     = hInstance,
                hCursor       = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszClassName = _windowClassName
            };

            if (RegisterClass(ref wc) == 0)
                throw new InvalidOperationException($"[VST Window] RegisterClass failed. Error: {Marshal.GetLastWin32Error()}");

            _hwnd = CreateWindowEx(0, _windowClassName, title,
                WS_VST_WINDOW,
                CW_USEDEFAULT, CW_USEDEFAULT, width, height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"[VST Window] CreateWindowEx failed. Error: {Marshal.GetLastWin32Error()}");
        }

        /// <summary>Returns the HWND. Valid after a successful Open() call.</summary>
        public IntPtr GetHandle() => _hwnd;

        /// <summary>
        /// Makes the window visible after the plugin editor has been attached.
        /// Posts WM_USER_SHOW so that ShowWindow runs on the window owner thread.
        /// </summary>
        public void Show()
        {
            if (_hwnd == IntPtr.Zero) return;
            PostMessage(_hwnd, WM_USER_SHOW, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Synchronously executes an action on the window thread.
        /// If already on the window thread, executes directly.
        /// Otherwise enqueues the action, posts WM_USER_INVOKE, and waits for completion.
        /// </summary>
        public void Invoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            if (GetCurrentThreadId() == _creatorThreadId)
            {
                action();
                return;
            }

            using var signal = new ManualResetEvent(false);
            var item = new InvokeItem(action, signal);
            _invokeQueue.Enqueue(item);
            PostMessage(_hwnd, WM_USER_INVOKE, IntPtr.Zero, IntPtr.Zero);
            signal.WaitOne();

            if (item.Exception != null)
                throw new InvalidOperationException("Error on window thread", item.Exception);
        }

        /// <summary>
        /// Asynchronously posts an action to the window thread.
        /// </summary>
        public void BeginInvoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;
            _invokeQueue.Enqueue(new InvokeItem(action));
            PostMessage(_hwnd, WM_USER_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Posts WM_CLOSE to the window thread's message queue.</summary>
        public void Close()
        {
            if (_hwnd == IntPtr.Zero) return;
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_USER_INVOKE:
                    while (_invokeQueue.TryDequeue(out var item))
                    {
                        try { item.Action(); }
                        catch (Exception ex)
                        {
                            item.Exception = ex;
                            Console.WriteLine($"[VST Window] Invoke error: {ex.Message}");
                        }
                        finally { item.SyncSignal?.Set(); }
                    }
                    return IntPtr.Zero;

                case WM_USER_SHOW:
                    ShowWindow(hWnd, SW_SHOW);
                    return IntPtr.Zero;

                case WM_ERASEBKGND:
                    return new IntPtr(1);

                case WM_SIZE:
                    int w = (int)(lParam.ToInt64() & 0xFFFF);
                    int h = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    OnResize?.Invoke(w, h);
                    break;

                case WM_CLOSE:
                    OnClosed?.Invoke();
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    while (_invokeQueue.TryDequeue(out var pending))
                    {
                        pending.Exception = new ObjectDisposedException("Window destroyed");
                        pending.SyncSignal?.Set();
                    }
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Releases all native resources. Posts WM_CLOSE if the window is still open.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_hwnd != IntPtr.Zero)
                {
                    if (GetCurrentThreadId() == _creatorThreadId)
                        DestroyWindow(_hwnd);
                    else
                        PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                if (_windowClassName != null)
                {
                    UnregisterClass(_windowClassName, GetModuleHandle(null));
                    _windowClassName = null;
                }

                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowWindows() => Dispose();
    }
}

