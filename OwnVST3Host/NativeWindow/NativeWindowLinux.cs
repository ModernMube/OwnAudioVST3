using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Linux native window management using X11
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

        [DllImport("libX11.so.6")]
        private static extern int XChangeProperty(
            IntPtr display,
            IntPtr window,
            IntPtr property,
            IntPtr type,
            int format,
            int mode,
            IntPtr data,
            int nelements);

        // Event masks
        private const long StructureNotifyMask = 1L << 17;
        private const long ExposureMask = 1L << 15;

        // Motif WM Hints structure for controlling window decorations
        [StructLayout(LayoutKind.Sequential)]
        private struct MotifWmHints
        {
            public uint flags;
            public uint functions;
            public uint decorations;
            public int input_mode;
            public uint status;
        }

        // Motif WM Hints flags
        private const uint MWM_HINTS_FUNCTIONS = 1 << 0;
        private const uint MWM_HINTS_DECORATIONS = 1 << 1;

        // Motif WM functions - exclude MWM_FUNC_CLOSE to remove close button
        private const uint MWM_FUNC_RESIZE = 1 << 1;
        private const uint MWM_FUNC_MINIMIZE = 1 << 3;
        private const uint MWM_FUNC_MAXIMIZE = 1 << 4;

        // Motif WM decorations - all decorations enabled
        private const uint MWM_DECOR_ALL = 1 << 0;

        #endregion

        public void Open(string title, int width, int height)
        {
            if (_window != IntPtr.Zero)
            {
                throw new InvalidOperationException("Window is already open!");
            }

            // Open X11 display
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to connect to X server!");
            }

            IntPtr rootWindow = XDefaultRootWindow(_display);

            // Create window
            _window = XCreateSimpleWindow(
                _display,
                rootWindow,
                0, 0,
                (uint)width, (uint)height,
                1,
                0x000000, // black border
                0xFFFFFF  // white background
            );

            if (_window == IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
                throw new InvalidOperationException("Failed to create X11 window!");
            }

            // Set title
            XStoreName(_display, _window, title);

            // Set Motif WM Hints to remove close button
            // The window lifecycle is managed by code, not by user interaction
            IntPtr motifHintsAtom = XInternAtom(_display, "_MOTIF_WM_HINTS", false);
            MotifWmHints hints = new MotifWmHints
            {
                flags = MWM_HINTS_FUNCTIONS | MWM_HINTS_DECORATIONS,
                functions = MWM_FUNC_RESIZE | MWM_FUNC_MINIMIZE | MWM_FUNC_MAXIMIZE, // No MWM_FUNC_CLOSE
                decorations = MWM_DECOR_ALL, // All decorations visible
                input_mode = 0,
                status = 0
            };

            IntPtr hintsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(hints));
            try
            {
                Marshal.StructureToPtr(hints, hintsPtr, false);
                XChangeProperty(_display, _window, motifHintsAtom, motifHintsAtom,
                    32, 0, hintsPtr, 5); // PropModeReplace = 0, 5 elements in struct
            }
            finally
            {
                Marshal.FreeHGlobal(hintsPtr);
            }

            // Setup WM_DELETE_WINDOW protocol (though close button is removed)
            IntPtr wmDeleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", false);
            XSetWMProtocols(_display, _window, ref wmDeleteWindow, 1);

            // Select events
            XSelectInput(_display, _window, StructureNotifyMask | ExposureMask);

            // Show window
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
