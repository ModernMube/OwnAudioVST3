using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows native window management.
    ///
    /// Design: the HWND is created on the CALLING thread (the Avalonia UI / STA thread)
    /// so that the parent window and the VST plugin's child windows all share the same
    /// message thread.  Avalonia's own Win32 message loop dispatches messages for this
    /// HWND automatically — no dedicated window thread or RunMessageLoop is needed.
    ///
    /// This mirrors NativeWindowMac exactly: on macOS every Cocoa object lives on the
    /// main thread and Invoke() runs actions directly when already on that thread.
    /// Here Invoke() does the same: direct call when on the creator thread, otherwise
    /// PostMessage + WaitOne so Avalonia's loop runs the action inside WM_USER_WAKE.
    ///
    /// Keeping all windows on one thread eliminates cross-thread SendMessage paths
    /// between the parent HWND and VST child windows that caused deadlocks with
    /// JUCE-based and other plugins that call SendMessage during IPlugView::attached().
    /// </summary>
    internal class NativeWindowWindows : INativeWindow
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private Thread? _creatorThread;
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();
        private readonly ManualResetEventSlim _windowDestroyed = new(false);
        private bool _disposed;
        private WndProcDelegate? _wndProcDelegate;
        private string? _windowClassName;

        public bool IsOpen => _hwnd != IntPtr.Zero;

        public bool IsActive
        {
            get
            {
                if (_hwnd == IntPtr.Zero) return false;
                return GetForegroundWindow() == _hwnd;
            }
        }

        public event Action<int, int>? OnResize;
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
        private const int WS_VISIBLE      = 0x10000000;

        private const int WS_CLIPCHILDREN  = 0x02000000;
        private const int WS_CLIPSIBLINGS  = 0x04000000;

        private const int WS_VST_WINDOW =
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU |
            WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX |
            WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_SIZE       = 0x0005;
        private const uint WM_CLOSE      = 0x0010;
        private const uint WM_DESTROY    = 0x0002;
        private const uint WM_USER_WAKE  = 0x0400 + 100;

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
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        /// <summary>
        /// Creates the native window synchronously on the calling thread.
        /// Must be called from the UI/STA thread whose message loop will service this window.
        /// </summary>
        public void Open(string title, int width, int height)
        {
            if (_hwnd != IntPtr.Zero)
                throw new InvalidOperationException("Window is already open!");

            _creatorThread = Thread.CurrentThread;

            IntPtr hInstance = GetModuleHandle(null);
            _windowClassName = $"OwnVST3HostWindow_{Environment.TickCount}_{GetHashCode()}";
            _wndProcDelegate = WndProc;

            WNDCLASS wc = new WNDCLASS
            {
                style         = (uint)(CS_DBLCLKS),
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance     = hInstance,
                hCursor       = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = IntPtr.Zero,   // No brush; plugin paints its own content
                lpszClassName = _windowClassName
            };

            if (RegisterClass(ref wc) == 0)
                throw new InvalidOperationException(
                    $"[VST Window] Failed to register window class. Error: {Marshal.GetLastWin32Error()}");

            _hwnd = CreateWindowEx(0, _windowClassName, title,
                WS_VST_WINDOW | WS_VISIBLE,
                CW_USEDEFAULT, CW_USEDEFAULT, width, height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"[VST Window] Failed to create window. Error: {Marshal.GetLastWin32Error()}");

            ShowWindow(_hwnd, 1);
            UpdateWindow(_hwnd);
            // Avalonia's message loop on the calling thread services this HWND from here on.
        }

        public IntPtr GetHandle() => _hwnd;

        /// <summary>
        /// Synchronously invokes an action on the creator thread.
        /// If already on the creator thread, executes directly (same as macOS Invoke behaviour).
        /// </summary>
        public void Invoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            if (Thread.CurrentThread == _creatorThread)
            {
                action();
                return;
            }

            using var signal = new ManualResetEvent(false);
            var item = new InvokeItem(action, signal);
            _invokeQueue.Enqueue(item);
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);

            signal.WaitOne();

            if (item.Exception != null)
                throw new InvalidOperationException("Error on creator thread", item.Exception);
        }

        /// <summary>
        /// Asynchronously invokes an action on the creator thread via the message loop.
        /// </summary>
        public void BeginInvoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;
            _invokeQueue.Enqueue(new InvokeItem(action));
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Closes the window.  Synchronous when called from the creator thread,
        /// asynchronous (PostMessage) otherwise.
        /// </summary>
        public void Close()
        {
            if (_hwnd == IntPtr.Zero) return;

            if (Thread.CurrentThread == _creatorThread)
                SendMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_USER_WAKE:
                    // Drain the invoke queue — called from Avalonia's DispatchMessage.
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

                case WM_ERASEBKGND:
                    // Suppress background erase so no white flash appears before the
                    // plugin paints its content.  WS_CLIPCHILDREN prevents the parent
                    // from repainting over child windows on subsequent WM_PAINT events.
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
                    _windowDestroyed.Set();
                    // Do NOT call PostQuitMessage — this HWND lives on Avalonia's UI thread;
                    // PostQuitMessage would terminate Avalonia's entire message loop.
                    return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_hwnd != IntPtr.Zero)
                {
                    Close();

                    // When Close() is async (PostMessage from a non-creator thread) we must
                    // wait for WM_DESTROY before unregistering the window class.
                    if (_creatorThread != null && Thread.CurrentThread != _creatorThread)
                        _windowDestroyed.Wait(5000);
                }

                if (_windowClassName != null)
                {
                    UnregisterClass(_windowClassName, GetModuleHandle(null));
                    _windowClassName = null;
                }

                _windowDestroyed.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowWindows() => Dispose();
    }
}
