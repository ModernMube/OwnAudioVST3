using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows native window management using Win32 API.
    /// The window runs on a dedicated STA thread with its own message pump (message loop)
    /// to prevent deadlocks from VST3 plugin cross-thread SendMessage calls.
    /// Invoke processing happens in the message loop body (not in WndProc),
    /// so the system can process "sent messages" during the attached() call.
    /// </summary>
    internal class NativeWindowWindows : INativeWindow
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private Thread? _windowThread;
        private readonly ManualResetEventSlim _windowCreated = new(false);
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();
        private bool _disposed = false;
        private WndProcDelegate? _wndProcDelegate;
        private string? _windowClassName;

        public bool IsOpen => _hwnd != IntPtr.Zero;

        public bool IsActive
        {
            get
            {
                if (_hwnd == IntPtr.Zero)
                    return false;
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

        #region Win32 API Declarations

        // Window styles - custom combination without WS_SYSMENU (no close button)
        private const int WS_OVERLAPPED = 0x00000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_VISIBLE = 0x10000000;

        // Custom window style: Titled, resizable, with min/max buttons, but NO close button
        private const int WS_VST_WINDOW = WS_OVERLAPPED | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        private const uint WM_SIZE = 0x0005;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_USER_WAKE = 0x0400 + 100;

        private const uint PM_REMOVE = 0x0001;

        private const int CS_VREDRAW = 0x0001;
        private const int CS_HREDRAW = 0x0002;
        private const int COLOR_WINDOW = 5;
        private const int IDC_ARROW = 32512;

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
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x, int y,
            int nWidth, int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

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
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern bool WaitMessage();

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        public void Open(string title, int width, int height)
        {
            if (_hwnd != IntPtr.Zero)
            {
                throw new InvalidOperationException("Window is already open!");
            }

            _windowCreated.Reset();

            _windowThread = new Thread(() => WindowThreadProc(title, width, height));
            _windowThread.SetApartmentState(ApartmentState.STA);
            _windowThread.IsBackground = true;
            _windowThread.Start();

            _windowCreated.Wait();

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create window on dedicated thread!");
            }
        }

        private void WindowThreadProc(string title, int width, int height)
        {
            try
            {
                IntPtr hInstance = GetModuleHandle(null);

                _windowClassName = $"OwnVST3HostWindow_{Environment.TickCount}_{GetHashCode()}";
                _wndProcDelegate = new WndProcDelegate(WndProc);

                WNDCLASS wc = new WNDCLASS
                {
                    style = (uint)(CS_HREDRAW | CS_VREDRAW),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                    hbrBackground = new IntPtr(COLOR_WINDOW + 1),
                    lpszMenuName = null,
                    lpszClassName = _windowClassName
                };

                if (RegisterClass(ref wc) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[VST Window] Failed to register window class. Win32 error: {error}");
                    _windowCreated.Set();
                    return;
                }

                // Create window WITHOUT close button (WS_VST_WINDOW excludes WS_SYSMENU)
                // The window lifecycle is managed by code, not by user interaction
                _hwnd = CreateWindowEx(
                    0,
                    _windowClassName,
                    title,
                    WS_VST_WINDOW | WS_VISIBLE,
                    CW_USEDEFAULT, CW_USEDEFAULT,
                    width, height,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[VST Window] Failed to create window. Win32 error: {error}");
                    _windowCreated.Set();
                    return;
                }

                ShowWindow(_hwnd, 1); // SW_SHOWNORMAL
                UpdateWindow(_hwnd);

                _windowCreated.Set();

                // Message pump - invoke processing in loop body (NOT in WndProc!)
                // This ensures that during attached() the Win32 system
                // can process cross-thread "sent messages",
                // since we're not inside WndProc.
                RunMessageLoop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Window] Window thread error: {ex.Message}");
                _windowCreated.Set();
            }
        }

        private void RunMessageLoop()
        {
            while (true)
            {
                // 1) Process invoke queue in message loop body
                //    (NOT in WndProc, so it doesn't block sent messages processing)
                while (_invokeQueue.TryDequeue(out var item))
                {
                    try
                    {
                        item.Action();
                    }
                    catch (Exception ex)
                    {
                        item.Exception = ex;
                        Console.WriteLine($"[VST Window] Invoke error: {ex.Message}");
                    }
                    finally
                    {
                        item.SyncSignal?.Set();
                    }
                }

                // 2) Process Windows messages
                if (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == 0x0012) // WM_QUIT
                        break;

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else if (_invokeQueue.IsEmpty)
                {
                    // No messages and no invokes - wait efficiently
                    WaitMessage();
                }
            }
        }

        public void Invoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            if (Thread.CurrentThread == _windowThread)
            {
                action();
                return;
            }

            // Use ManualResetEvent (not Slim!),
            // because WaitOne() on STA thread also pumps COM messages
            using var signal = new ManualResetEvent(false);
            var item = new InvokeItem(action, signal);
            _invokeQueue.Enqueue(item);
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);

            // On STA thread this also processes COM messages while waiting
            signal.WaitOne();

            if (item.Exception != null)
                throw new InvalidOperationException("Error on window thread", item.Exception);
        }

        public void BeginInvoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            _invokeQueue.Enqueue(new InvokeItem(action));
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);
        }

        public void Close()
        {
            if (_hwnd == IntPtr.Zero) return;

            if (Thread.CurrentThread == _windowThread)
            {
                DestroyWindow(_hwnd);
            }
            else
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        public IntPtr GetHandle()
        {
            return _hwnd;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_USER_WAKE:
                    // Just wakes up WaitMessage(),
                    // actual processing happens in message loop body
                    return IntPtr.Zero;

                case WM_SIZE:
                    int width = (int)(lParam.ToInt64() & 0xFFFF);
                    int height = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                    OnResize?.Invoke(width, height);
                    break;

                case WM_CLOSE:
                    OnClosed?.Invoke();
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
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
                }

                _windowThread?.Join(5000);
                _windowCreated.Dispose();

                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowWindows()
        {
            Dispose();
        }
    }
}
