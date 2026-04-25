using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Linux native window management using X11.
    /// A dedicated event thread processes X11 events so that WM_DELETE_WINDOW
    /// (user close button) is handled without blocking the audio thread.
    /// </summary>
    internal class NativeWindowLinux : INativeWindow
    {
        private IntPtr _display = IntPtr.Zero;
        private IntPtr _window  = IntPtr.Zero;
        private bool   _disposed = false;

        // X11 atoms set in Open(), read in event thread
        private IntPtr _wmDeleteWindow = IntPtr.Zero;

        // Event loop thread
        private Thread?                    _eventThread;
        private CancellationTokenSource?   _eventCts;
        private volatile bool              _onClosedFired;

        public bool IsOpen   => _window != IntPtr.Zero;
        public bool IsActive => _window != IntPtr.Zero;

        public event Action<int, int>? OnResize;
        public event Action?           OnClosed;

        #region X11 Declarations

        // Thread-safety: must call XInitThreads() before any multithreaded X11 use
        [DllImport("libX11.so.6")]
        private static extern int XInitThreads();

        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6")]
        private static extern IntPtr XCreateSimpleWindow(
            IntPtr display, IntPtr parent,
            int x, int y, uint width, uint height,
            uint border_width, ulong border, ulong background);

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
            IntPtr display, IntPtr window, IntPtr property, IntPtr type,
            int format, int mode, IntPtr data, int nelements);

        [DllImport("libX11.so.6")]
        private static extern int XNextEvent(IntPtr display, ref XEvent event_return);

        [DllImport("libX11.so.6")]
        private static extern int XPending(IntPtr display);

        // XEvent union – large enough for all event types (192 bytes on 64-bit Linux).
        // Only the fields we actually use are declared; ExplicitLayout lets them overlap safely.
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct XEvent
        {
            [FieldOffset(0)]  public int    type;

            // XConfigureEvent: x@48, y@52, width@56, height@60
            [FieldOffset(48)] public int    configure_x;
            [FieldOffset(52)] public int    configure_y;
            [FieldOffset(56)] public int    configure_width;
            [FieldOffset(60)] public int    configure_height;

            // XClientMessageEvent: message_type@40, format@48, data.l[0]@56
            [FieldOffset(40)] public IntPtr clientMessage_type;   // Atom
            // format field overlaps configure_x – read by type check
            [FieldOffset(48)] public int    clientMessage_format;
            [FieldOffset(56)] public IntPtr clientMessage_data0;  // data.l[0]
        }

        // Relevant X11 event type codes
        private const int ConfigureNotify = 22;
        private const int ClientMessage   = 33;

        // Event masks
        private const long StructureNotifyMask = 1L << 17;
        private const long ExposureMask        = 1L << 15;

        // Motif WM Hints for window decoration control
        [StructLayout(LayoutKind.Sequential)]
        private struct MotifWmHints
        {
            public uint flags;
            public uint functions;
            public uint decorations;
            public int  input_mode;
            public uint status;
        }

        private const uint MWM_HINTS_FUNCTIONS   = 1u << 0;
        private const uint MWM_HINTS_DECORATIONS = 1u << 1;
        private const uint MWM_FUNC_RESIZE        = 1u << 1;
        private const uint MWM_FUNC_CLOSE         = 1u << 5;
        private const uint MWM_FUNC_MINIMIZE      = 1u << 3;
        private const uint MWM_FUNC_MAXIMIZE      = 1u << 4;
        private const uint MWM_DECOR_ALL          = 1u << 0;

        #endregion

        static NativeWindowLinux()
        {
            // Required for multi-threaded X11 access (event thread + caller thread)
            XInitThreads();
        }

        public void Open(string title, int width, int height)
        {
            if (_window != IntPtr.Zero)
                throw new InvalidOperationException("Window is already open!");

            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero)
                throw new InvalidOperationException("Failed to connect to X server!");

            IntPtr rootWindow = XDefaultRootWindow(_display);

            _window = XCreateSimpleWindow(
                _display, rootWindow,
                0, 0, (uint)width, (uint)height,
                1, 0x000000, 0xFFFFFF);

            if (_window == IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
                throw new InvalidOperationException("Failed to create X11 window!");
            }

            XStoreName(_display, _window, title);

            // Enable close button via Motif WM Hints
            IntPtr motifHintsAtom = XInternAtom(_display, "_MOTIF_WM_HINTS", false);
            MotifWmHints hints = new MotifWmHints
            {
                flags       = MWM_HINTS_FUNCTIONS | MWM_HINTS_DECORATIONS,
                functions   = MWM_FUNC_RESIZE | MWM_FUNC_MINIMIZE | MWM_FUNC_MAXIMIZE | MWM_FUNC_CLOSE,
                decorations = MWM_DECOR_ALL,
                input_mode  = 0,
                status      = 0
            };

            IntPtr hintsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(hints));
            try
            {
                Marshal.StructureToPtr(hints, hintsPtr, false);
                XChangeProperty(_display, _window, motifHintsAtom, motifHintsAtom,
                    32, 0, hintsPtr, 5);
            }
            finally
            {
                Marshal.FreeHGlobal(hintsPtr);
            }

            // Register WM_DELETE_WINDOW protocol so the WM sends us a ClientMessage
            // instead of killing the process when the user clicks the X button.
            _wmDeleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", false);
            XSetWMProtocols(_display, _window, ref _wmDeleteWindow, 1);

            XSelectInput(_display, _window, StructureNotifyMask | ExposureMask);

            XMapWindow(_display, _window);
            XFlush(_display);

            // Start event loop thread
            _onClosedFired = false;
            _eventCts      = new CancellationTokenSource();
            _eventThread   = new Thread(EventThreadProc)
            {
                IsBackground = true,
                Name         = "X11 Event Thread"
            };
            _eventThread.Start(_eventCts.Token);
        }

        /// <summary>
        /// X11 event loop – runs on a dedicated background thread.
        /// Handles ConfigureNotify (resize) and ClientMessage/WM_DELETE_WINDOW (user close).
        /// </summary>
        private void EventThreadProc(object? state)
        {
            var token = (CancellationToken)state!;
            XEvent xevent = default;

            try
            {
                while (!token.IsCancellationRequested && _window != IntPtr.Zero)
                {
                    if (XPending(_display) > 0)
                    {
                        XNextEvent(_display, ref xevent);

                        switch (xevent.type)
                        {
                            case ConfigureNotify:
                                int w = xevent.configure_width;
                                int h = xevent.configure_height;
                                if (w > 0 && h > 0)
                                    OnResize?.Invoke(w, h);
                                break;

                            case ClientMessage:
                                if (xevent.clientMessage_data0 == _wmDeleteWindow)
                                {
                                    // User clicked close button – fire event and exit thread.
                                    // VstEditorController.OnWindowClosed will call Close() which
                                    // will destroy the window and display after we exit.
                                    _onClosedFired = true;
                                    OnClosed?.Invoke();
                                    return;
                                }
                                break;
                        }
                    }
                    else
                    {
                        // No pending events – sleep briefly to avoid busy-wait
                        token.WaitHandle.WaitOne(50);
                    }
                }
            }
            catch
            {
                // Display was closed while we were reading – normal during shutdown
            }
        }

        public void Close()
        {
            // Stop event thread before touching the display
            if (_eventCts != null)
            {
                _eventCts.Cancel();
                _eventCts.Dispose();
                _eventCts = null;
            }

            _eventThread?.Join(500);
            _eventThread = null;

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

            // Fire OnClosed only if not already fired by the event thread (user clicked X)
            if (!_onClosedFired)
                OnClosed?.Invoke();
        }

        public IntPtr GetHandle() => _window;

        // Linux: Invoke/BeginInvoke execute directly on the calling thread.
        // The idle action (ProcessIdle) does not use X11, so no thread safety issue.
        public void Invoke(Action action)      => action();
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
