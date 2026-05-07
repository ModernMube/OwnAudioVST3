using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows native window management using Win32 API.
    /// The window runs on a dedicated STA thread with its own message pump.
    /// Processing occurs in the message loop body to prevent deadlocks.
    /// </summary>
    internal class NativeWindowWindows : INativeWindow
    {
        /// <summary>
        /// The native window handle (HWND).
        /// </summary>
        private IntPtr _hwnd = IntPtr.Zero;

        /// <summary>
        /// The thread hosting the window.
        /// </summary>
        private Thread? _windowThread;

        /// <summary>
        /// Synchronization event signaled when the window is created.
        /// </summary>
        private readonly ManualResetEventSlim _windowCreated = new(false);

        /// <summary>
        /// Queue of actions to be invoked on the window thread.
        /// </summary>
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();

        /// <summary>
        /// Indicates whether the object has been disposed.
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// Delegate for the window procedure.
        /// </summary>
        private WndProcDelegate? _wndProcDelegate;

        /// <summary>
        /// The registered window class name.
        /// </summary>
        private string? _windowClassName;

        /// <summary>
        /// Gets a value indicating whether the window is open.
        /// </summary>
        public bool IsOpen => _hwnd != IntPtr.Zero;

        /// <summary>
        /// Gets a value indicating whether the window is active and foreground.
        /// </summary>
        public bool IsActive
        {
            get
            {
                if (_hwnd == IntPtr.Zero)
                    return false;
                return GetForegroundWindow() == _hwnd;
            }
        }

        /// <summary>
        /// Event triggered when the window is resized.
        /// </summary>
        public event Action<int, int>? OnResize;

        /// <summary>
        /// Event triggered when the window is closed.
        /// </summary>
        public event Action? OnClosed;

        /// <summary>
        /// Represents an action to be invoked on the window thread.
        /// </summary>
        private sealed class InvokeItem
        {
            /// <summary>
            /// Gets the action to execute.
            /// </summary>
            public Action Action { get; }

            /// <summary>
            /// Gets the optional synchronization signal.
            /// </summary>
            public ManualResetEvent? SyncSignal { get; }

            /// <summary>
            /// Gets or sets the exception that occurred during execution.
            /// </summary>
            public Exception? Exception { get; set; }

            /// <summary>
            /// Initializes a new instance of the InvokeItem class.
            /// </summary>
            /// <param name="action">The action to execute.</param>
            /// <param name="syncSignal">The optional synchronization signal.</param>
            public InvokeItem(Action action, ManualResetEvent? syncSignal = null)
            {
                Action = action;
                SyncSignal = syncSignal;
            }
        }

        #region Win32 API Declarations

        /// <summary>
        /// Window style flag for overlapped windows.
        /// </summary>
        private const int WS_OVERLAPPED = 0x00000000;

        /// <summary>
        /// Window style flag for caption bar.
        /// </summary>
        private const int WS_CAPTION = 0x00C00000;

        /// <summary>
        /// Window style flag for system menu.
        /// </summary>
        private const int WS_SYSMENU = 0x00080000;

        /// <summary>
        /// Window style flag for thick frame (resizable).
        /// </summary>
        private const int WS_THICKFRAME = 0x00040000;

        /// <summary>
        /// Window style flag for minimize box.
        /// </summary>
        private const int WS_MINIMIZEBOX = 0x00020000;

        /// <summary>
        /// Window style flag for maximize box.
        /// </summary>
        private const int WS_MAXIMIZEBOX = 0x00010000;

        /// <summary>
        /// Window style flag for initial visibility.
        /// </summary>
        private const int WS_VISIBLE = 0x10000000;

        /// <summary>
        /// Combined window style for VST windows.
        /// </summary>
        private const int WS_VST_WINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

        /// <summary>
        /// Constant indicating default position or size.
        /// </summary>
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        /// <summary>
        /// Window message: Size changed.
        /// </summary>
        private const uint WM_SIZE = 0x0005;

        /// <summary>
        /// Window message: Close requested.
        /// </summary>
        private const uint WM_CLOSE = 0x0010;

        /// <summary>
        /// Window message: Destroying.
        /// </summary>
        private const uint WM_DESTROY = 0x0002;

        /// <summary>
        /// Custom user message for waking the message loop.
        /// </summary>
        private const uint WM_USER_WAKE = 0x0400 + 100;

        /// <summary>
        /// PeekMessage flag to remove message from queue.
        /// </summary>
        private const uint PM_REMOVE = 0x0001;

        /// <summary>
        /// Class style flag for vertical redraw.
        /// </summary>
        private const int CS_VREDRAW = 0x0001;

        /// <summary>
        /// Class style flag for horizontal redraw.
        /// </summary>
        private const int CS_HREDRAW = 0x0002;

        /// <summary>
        /// Standard window background color.
        /// </summary>
        private const int COLOR_WINDOW = 5;

        /// <summary>
        /// Standard arrow cursor identifier.
        /// </summary>
        private const int IDC_ARROW = 32512;

        /// <summary>
        /// Structure representing a window class.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            /// <summary>
            /// Class style.
            /// </summary>
            public uint style;
            /// <summary>
            /// Window procedure pointer.
            /// </summary>
            public IntPtr lpfnWndProc;
            /// <summary>
            /// Extra class bytes.
            /// </summary>
            public int cbClsExtra;
            /// <summary>
            /// Extra window bytes.
            /// </summary>
            public int cbWndExtra;
            /// <summary>
            /// Instance handle.
            /// </summary>
            public IntPtr hInstance;
            /// <summary>
            /// Icon handle.
            /// </summary>
            public IntPtr hIcon;
            /// <summary>
            /// Cursor handle.
            /// </summary>
            public IntPtr hCursor;
            /// <summary>
            /// Background brush handle.
            /// </summary>
            public IntPtr hbrBackground;
            /// <summary>
            /// Menu name.
            /// </summary>
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            /// <summary>
            /// Class name.
            /// </summary>
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
        }

        /// <summary>
        /// Structure representing a window message.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            /// <summary>
            /// Window handle.
            /// </summary>
            public IntPtr hwnd;
            /// <summary>
            /// Message identifier.
            /// </summary>
            public uint message;
            /// <summary>
            /// Additional message information.
            /// </summary>
            public IntPtr wParam;
            /// <summary>
            /// Additional message information.
            /// </summary>
            public IntPtr lParam;
            /// <summary>
            /// Time when message was posted.
            /// </summary>
            public uint time;
            /// <summary>
            /// Cursor x-coordinate.
            /// </summary>
            public int pt_x;
            /// <summary>
            /// Cursor y-coordinate.
            /// </summary>
            public int pt_y;
        }

        /// <summary>
        /// Registers a window class for subsequent use in calls to the CreateWindowEx function.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        /// <summary>
        /// Creates an overlapped, pop-up, or child window with an extended window style.
        /// </summary>
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

        /// <summary>
        /// Destroys the specified window.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        /// <summary>
        /// Sets the specified window's show state.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        /// <summary>
        /// Updates the client area of the specified window by sending a WM_PAINT message.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        /// <summary>
        /// Calls the default window procedure to provide default processing for any window messages.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Retrieves a module handle for the specified module.
        /// </summary>
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>
        /// Loads the specified cursor resource from the executable (.EXE) file associated with an application instance.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        /// <summary>
        /// Retrieves a handle to the foreground window.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Dispatches incoming sent messages, checks the thread message queue for a posted message.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        /// <summary>
        /// Translates virtual-key messages into character messages.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        /// <summary>
        /// Dispatches a message to a window procedure.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        /// <summary>
        /// Places (posts) a message in the message queue associated with the thread that created the specified window.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Indicates to the system that a thread has made a request to terminate (quit).
        /// </summary>
        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        /// <summary>
        /// Yields control to other threads when a thread has no other messages in its message queue.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern bool WaitMessage();

        /// <summary>
        /// Unregisters a window class, freeing the memory required for the class.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        /// <summary>
        /// Delegate type for the window procedure.
        /// </summary>
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        /// <summary>
        /// Creates and displays the native window.
        /// </summary>
        /// <param name="title">The window title.</param>
        /// <param name="width">The width of the window.</param>
        /// <param name="height">The height of the window.</param>
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

        /// <summary>
        /// The procedure executed by the dedicated window thread.
        /// </summary>
        /// <param name="title">The window title.</param>
        /// <param name="width">The width of the window.</param>
        /// <param name="height">The height of the window.</param>
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

                ShowWindow(_hwnd, 1);
                UpdateWindow(_hwnd);

                _windowCreated.Set();

                RunMessageLoop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST Window] Window thread error: {ex.Message}");
                _windowCreated.Set();
            }
        }

        /// <summary>
        /// Runs the message loop for the window thread.
        /// </summary>
        private void RunMessageLoop()
        {
            while (true)
            {
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

                if (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == 0x0012)
                        break;

                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else if (_invokeQueue.IsEmpty)
                {
                    WaitMessage();
                }
            }
        }

        /// <summary>
        /// Synchronously invokes an action on the window thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void Invoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            if (Thread.CurrentThread == _windowThread)
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
                throw new InvalidOperationException("Error on window thread", item.Exception);
        }

        /// <summary>
        /// Asynchronously invokes an action on the window thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void BeginInvoke(Action action)
        {
            if (_hwnd == IntPtr.Zero) return;

            _invokeQueue.Enqueue(new InvokeItem(action));
            PostMessage(_hwnd, WM_USER_WAKE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Closes the native window.
        /// </summary>
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

        /// <summary>
        /// Gets the handle to the native window.
        /// </summary>
        /// <returns>The window handle.</returns>
        public IntPtr GetHandle()
        {
            return _hwnd;
        }

        /// <summary>
        /// The window procedure handling messages sent to the window.
        /// </summary>
        /// <param name="hWnd">Window handle.</param>
        /// <param name="msg">Message identifier.</param>
        /// <param name="wParam">Additional message information.</param>
        /// <param name="lParam">Additional message information.</param>
        /// <returns>The result of the message processing.</returns>
        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_USER_WAKE:
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

        /// <summary>
        /// Disposes of the native window resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_hwnd != IntPtr.Zero)
                    Close();

                _windowThread?.Join(5000);

                if (_windowClassName != null)
                {
                    UnregisterClass(_windowClassName, GetModuleHandle(null));
                    _windowClassName = null;
                }

                _windowCreated.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Finalizes an instance of the NativeWindowWindows class.
        /// </summary>
        ~NativeWindowWindows()
        {
            Dispose();
        }
    }
}
