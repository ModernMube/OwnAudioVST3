using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Windows natív ablakkezelés Win32 API használatával
    /// </summary>
    internal class NativeWindowWindows : INativeWindow
    {
        private IntPtr _hwnd = IntPtr.Zero;
        private bool _disposed = false;
        private WndProcDelegate? _wndProcDelegate;
        private IntPtr _oldWndProc = IntPtr.Zero;

        public bool IsOpen => _hwnd != IntPtr.Zero;

        public event Action<int, int>? OnResize;
        public event Action? OnClosed;

        #region Win32 API Declarations

        private const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const int WS_VISIBLE = 0x10000000;
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);

        private const int WM_SIZE = 0x0005;
        private const int WM_CLOSE = 0x0010;
        private const int WM_DESTROY = 0x0002;

        private const int GWL_WNDPROC = -4;
        private const int CS_VREDRAW = 0x0001;
        private const int CS_HREDRAW = 0x0002;
        private const int COLOR_WINDOW = 5;

        private const string VST_WINDOW_CLASS = "OwnVST3HostWindow";
        private static bool _classRegistered = false;

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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        private const int IDC_ARROW = 32512;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        #endregion

        public void Open(string title, int width, int height)
        {
            if (_hwnd != IntPtr.Zero)
            {
                throw new InvalidOperationException("Az ablak már nyitva van!");
            }

            IntPtr hInstance = GetModuleHandle(null);

            // Regisztráljuk a saját ablak osztályunkat, ha még nem történt meg
            if (!_classRegistered)
            {
                _wndProcDelegate = new WndProcDelegate(WndProc);

                WNDCLASS wc = new WNDCLASS
                {
                    style = CS_HREDRAW | CS_VREDRAW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInstance,
                    hIcon = IntPtr.Zero,
                    hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                    hbrBackground = new IntPtr(COLOR_WINDOW + 1),
                    lpszMenuName = null,
                    lpszClassName = VST_WINDOW_CLASS
                };

                if (RegisterClass(ref wc) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"Nem sikerült regisztrálni az ablak osztályt. Win32 hiba: {error}");
                }

                _classRegistered = true;
            }

            // Létrehozunk egy ablakot a regisztrált osztállyal
            _hwnd = CreateWindowEx(
                0,
                VST_WINDOW_CLASS,
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
                throw new InvalidOperationException($"Nem sikerült létrehozni az ablakot. Win32 hiba: {error}");
            }

            ShowWindow(_hwnd, 1); // SW_SHOWNORMAL
            UpdateWindow(_hwnd);
        }

        public void Close()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
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
                case WM_SIZE:
                    int width = lParam.ToInt32() & 0xFFFF;
                    int height = (lParam.ToInt32() >> 16) & 0xFFFF;
                    OnResize?.Invoke(width, height);
                    break;

                case WM_CLOSE:
                    OnClosed?.Invoke();
                    Close();
                    return IntPtr.Zero;

                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    return IntPtr.Zero;
            }

            // Mivel saját ablak osztályt használunk, közvetlenül DefWindowProc-ot hívunk
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowWindows()
        {
            Dispose();
        }
    }
}
