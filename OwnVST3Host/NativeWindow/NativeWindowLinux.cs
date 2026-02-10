using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Linux natív ablakkezelés X11 használatával
    /// </summary>
    internal class NativeWindowLinux : INativeWindow
    {
        private IntPtr _display = IntPtr.Zero;
        private IntPtr _window = IntPtr.Zero;
        private bool _disposed = false;

        public bool IsOpen => _window != IntPtr.Zero;

        // Note: X11 focus detection is complex and requires additional event handling
        // For now, we always return true on Linux to avoid breaking the interface
        // VST dropdown menu issues are less common on Linux anyway
        public bool IsActive => _window != IntPtr.Zero;

        public event Action<int, int>? OnResize;
        public event Action? OnClosed;

        #region X11 Declarations

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XDefaultScreen(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XCreateSimpleWindow(
            IntPtr display,
            IntPtr parent,
            int x, int y,
            uint width, uint height,
            uint border_width,
            ulong border,
            ulong background);

        [DllImport("libX11.so.6")]
        private static extern int XMapWindow(IntPtr display, IntPtr window);

        [DllImport("libX11.so.6")]
        private static extern int XDestroyWindow(IntPtr display, IntPtr window);

        [DllImport("libX11.so.6")]
        private static extern int XStoreName(IntPtr display, IntPtr window, string window_name);

        [DllImport("libX11.so.6")]
        private static extern int XSelectInput(IntPtr display, IntPtr window, long event_mask);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XInternAtom(IntPtr display, string atom_name, bool only_if_exists);

        [DllImport("libX11.so.6")]
        private static extern int XSetWMProtocols(IntPtr display, IntPtr window, ref IntPtr protocols, int count);

        // Event masks
        private const long StructureNotifyMask = 1L << 17;
        private const long ExposureMask = 1L << 15;

        #endregion

        public void Open(string title, int width, int height)
        {
            if (_window != IntPtr.Zero)
            {
                throw new InvalidOperationException("Az ablak már nyitva van!");
            }

            // X11 display megnyitása
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nem sikerült kapcsolódni az X szerverhez!");
            }

            IntPtr rootWindow = XDefaultRootWindow(_display);

            // Ablak létrehozása
            _window = XCreateSimpleWindow(
                _display,
                rootWindow,
                0, 0,
                (uint)width, (uint)height,
                1,
                0x000000, // fekete keret
                0xFFFFFF  // fehér háttér
            );

            if (_window == IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
                throw new InvalidOperationException("Nem sikerült létrehozni az X11 ablakot!");
            }

            // Cím beállítása
            XStoreName(_display, _window, title);

            // WM_DELETE_WINDOW protokoll beállítása (bezárás kezelése)
            IntPtr wmDeleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", false);
            XSetWMProtocols(_display, _window, ref wmDeleteWindow, 1);

            // Események figyelése
            XSelectInput(_display, _window, StructureNotifyMask | ExposureMask);

            // Ablak megjelenítése
            XMapWindow(_display, _window);
            XFlush(_display);
        }

        public void Close()
        {
            if (_window != IntPtr.Zero)
            {
                XDestroyWindow(_display, _window);
                _window = IntPtr.Zero;
            }

            if (_display != IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }

            OnClosed?.Invoke();
        }

        public IntPtr GetHandle()
        {
            return _window;
        }

        public void Invoke(Action action) => action();

        public void BeginInvoke(Action action) => action();

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowLinux()
        {
            Dispose();
        }
    }
}
