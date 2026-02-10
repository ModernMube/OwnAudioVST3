using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows natív ablakkezelés Win32 API használatával.
    /// Az ablak dedikált STA szálon fut saját üzenetpumpával (message loop),
    /// hogy a VST3 pluginok cross-thread SendMessage hívásai ne okozzanak deadlockot.
    /// Az invoke feldolgozás a message loop törzsében történik (nem WndProc-ban),
    /// így az attached() hívás közben a rendszer képes feldolgozni a "sent messages"-eket.
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

        private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int WS_VISIBLE = 0x10000000;
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
                throw new InvalidOperationException("Az ablak már nyitva van!");
            }

            _windowCreated.Reset();

            _windowThread = new Thread(() => WindowThreadProc(title, width, height));
            _windowThread.SetApartmentState(ApartmentState.STA);
            _windowThread.IsBackground = true;
            _windowThread.Start();

            _windowCreated.Wait();

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nem sikerült létrehozni az ablakot a dedikált szálon!");
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

                _hwnd = CreateWindowEx(
                    0,
                    _windowClassName,
                    title,
                    WS_OVERLAPPEDWINDOW | WS_VISIBLE,
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

                // Üzenetpumpa - invoke feldolgozás a loop törzsében (NEM WndProc-ban!)
                // Ez biztosítja, hogy az attached() hívás közben a Win32 rendszer
                // képes legyen feldolgozni a cross-thread "sent messages"-eket,
                // mivel nem vagyunk WndProc-on belül.
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
                // 1) Invoke queue feldolgozása a message loop törzsében
                //    (NEM WndProc-ban, így nem blokkolja a sent messages feldolgozást)
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

                // 2) Windows üzenetek feldolgozása
                if (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == 0x0012) // WM_QUIT
                        break;

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else if (_invokeQueue.IsEmpty)
                {
                    // Nincs üzenet és nincs invoke - hatékonyan várakozunk
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

            // ManualResetEvent-et használunk (nem Slim-et!),
            // mert az STA szálon a WaitOne() COM üzeneteket is pumpál
            using var signal = new ManualResetEvent(false);
            var item = new InvokeItem(action, signal);
            _invokeQueue.Enqueue(item);
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);

            // STA szálon ez COM üzeneteket is feldolgoz várakozás közben
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
                    // Csak felébreszti a WaitMessage()-et,
                    // a tényleges feldolgozás a message loop törzsében történik
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
