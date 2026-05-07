using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Linux native window management using X11.
    /// Manages the X11 event loop thread, window creation, and event processing.
    /// Provides cross-thread invocation capabilities for UI operations.
    /// X11 resources are exclusively managed on the event loop thread.
    /// </summary>
    internal class NativeWindowLinux : INativeWindow
    {
        /// <summary>
        /// The X11 display pointer.
        /// </summary>
        private IntPtr _display = IntPtr.Zero;

        /// <summary>
        /// The X11 window pointer.
        /// </summary>
        private IntPtr _window = IntPtr.Zero;

        /// <summary>
        /// Atom for WM_PROTOCOLS.
        /// </summary>
        private IntPtr _wmProtocols = IntPtr.Zero;

        /// <summary>
        /// Atom for WM_DELETE_WINDOW.
        /// </summary>
        private IntPtr _wmDeleteWindow = IntPtr.Zero;

        /// <summary>
        /// The thread running the X11 event loop.
        /// </summary>
        private Thread? _eventThread;

        /// <summary>
        /// Flag indicating whether the event loop should terminate.
        /// </summary>
        private volatile bool _shouldStop;

        /// <summary>
        /// Event used to signal when the window has been successfully created.
        /// </summary>
        private readonly ManualResetEventSlim _windowReady = new(false);

        /// <summary>
        /// Queue for cross-thread invocations.
        /// </summary>
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();

        /// <summary>
        /// The last known width of the window.
        /// </summary>
        private int _lastWidth;

        /// <summary>
        /// The last known height of the window.
        /// </summary>
        private int _lastHeight;

        /// <summary>
        /// Flag indicating whether the instance has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Gets a value indicating whether the window is open.
        /// </summary>
        public bool IsOpen => _window != IntPtr.Zero;

        /// <summary>
        /// Gets a value indicating whether the window is active.
        /// </summary>
        public bool IsActive => _window != IntPtr.Zero;

        /// <summary>
        /// Occurs when the window is resized.
        /// </summary>
        public event Action<int, int>? OnResize;

        /// <summary>
        /// Occurs when the window is closed.
        /// </summary>
        public event Action? OnClosed;

        /// <summary>
        /// Represents a queued action for the event thread.
        /// </summary>
        private sealed class InvokeItem
        {
            /// <summary>
            /// Gets the action to execute.
            /// </summary>
            public Action Action { get; }

            /// <summary>
            /// Gets the optional sync signal.
            /// </summary>
            public ManualResetEventSlim? SyncSignal { get; }

            /// <summary>
            /// Gets or sets the exception that occurred during execution.
            /// </summary>
            public Exception? Exception { get; set; }

            /// <summary>
            /// Initializes a new instance of the InvokeItem class.
            /// </summary>
            /// <param name="action">The action.</param>
            /// <param name="syncSignal">The optional sync signal.</param>
            public InvokeItem(Action action, ManualResetEventSlim? syncSignal = null)
            {
                Action = action;
                SyncSignal = syncSignal;
            }
        }

        /// <summary>
        /// X11 ConfigureNotify event constant.
        /// </summary>
        private const int ConfigureNotify = 22;

        /// <summary>
        /// X11 ClientMessage event constant.
        /// </summary>
        private const int ClientMessage = 33;

        /// <summary>
        /// Union-style XEvent struct for X11 events.
        /// Offsets are mapped for 64-bit Linux.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct XEvent
        {
            /// <summary>
            /// Event type.
            /// </summary>
            [FieldOffset(0)] public int Type;

            /// <summary>
            /// ConfigureNotify width.
            /// </summary>
            [FieldOffset(56)] public int CfgWidth;

            /// <summary>
            /// ConfigureNotify height.
            /// </summary>
            [FieldOffset(60)] public int CfgHeight;

            /// <summary>
            /// ClientMessage type atom.
            /// </summary>
            [FieldOffset(40)] public IntPtr CmMessageType;

            /// <summary>
            /// ClientMessage data.
            /// </summary>
            [FieldOffset(56)] public IntPtr CmDataL0;
        }

        /// <summary>
        /// Opens an X11 display connection.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern IntPtr XOpenDisplay(IntPtr display);

        /// <summary>
        /// Closes the X11 display connection.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XCloseDisplay(IntPtr display);

        /// <summary>
        /// Gets the default root window.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        /// <summary>
        /// Creates a simple window.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern IntPtr XCreateSimpleWindow(
            IntPtr display, IntPtr parent,
            int x, int y, uint width, uint height,
            uint border_width, ulong border, ulong background);

        /// <summary>
        /// Maps the window to the screen.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XMapWindow(IntPtr display, IntPtr window);

        /// <summary>
        /// Destroys the window.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XDestroyWindow(IntPtr display, IntPtr window);

        /// <summary>
        /// Stores the window name.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XStoreName(IntPtr display, IntPtr window, string window_name);

        /// <summary>
        /// Selects input events for the window.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XSelectInput(IntPtr display, IntPtr window, long event_mask);

        /// <summary>
        /// Flushes the X11 output buffer.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XFlush(IntPtr display);

        /// <summary>
        /// Gets the atom ID for the given name.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern IntPtr XInternAtom(IntPtr display, string atom_name, bool only_if_exists);

        /// <summary>
        /// Sets window manager protocols.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XSetWMProtocols(IntPtr display, IntPtr window, ref IntPtr protocols, int count);

        /// <summary>
        /// Changes a property of the window.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XChangeProperty(
            IntPtr display, IntPtr window,
            IntPtr property, IntPtr type,
            int format, int mode, IntPtr data, int nelements);

        /// <summary>
        /// Gets the next event from the queue.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XNextEvent(IntPtr display, out XEvent xevent);

        /// <summary>
        /// Gets the number of pending events.
        /// </summary>
        [DllImport("libX11.so.6")]
        private static extern int XPending(IntPtr display);

        /// <summary>
        /// Mask for StructureNotify events.
        /// </summary>
        private const long StructureNotifyMask = 1L << 17;

        /// <summary>
        /// Mask for Exposure events.
        /// </summary>
        private const long ExposureMask = 1L << 15;

        /// <summary>
        /// Motif window manager hints struct.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MotifWmHints
        {
            /// <summary>
            /// Flags.
            /// </summary>
            public uint flags;
            /// <summary>
            /// Functions.
            /// </summary>
            public uint functions;
            /// <summary>
            /// Decorations.
            /// </summary>
            public uint decorations;
            /// <summary>
            /// Input mode.
            /// </summary>
            public int input_mode;
            /// <summary>
            /// Status.
            /// </summary>
            public uint status;
        }

        /// <summary>
        /// Motif functions hint flag.
        /// </summary>
        private const uint MWM_HINTS_FUNCTIONS = 1u << 0;

        /// <summary>
        /// Motif decorations hint flag.
        /// </summary>
        private const uint MWM_HINTS_DECORATIONS = 1u << 1;

        /// <summary>
        /// Motif resize function flag.
        /// </summary>
        private const uint MWM_FUNC_RESIZE = 1u << 1;

        /// <summary>
        /// Motif close function flag.
        /// </summary>
        private const uint MWM_FUNC_CLOSE = 1u << 5;

        /// <summary>
        /// Motif minimize function flag.
        /// </summary>
        private const uint MWM_FUNC_MINIMIZE = 1u << 3;

        /// <summary>
        /// Motif maximize function flag.
        /// </summary>
        private const uint MWM_FUNC_MAXIMIZE = 1u << 4;

        /// <summary>
        /// Motif all decorations flag.
        /// </summary>
        private const uint MWM_DECOR_ALL = 1u << 0;

        /// <summary>
        /// Opens a native window and starts the event loop thread.
        /// Blocks until the window is successfully created.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="width">Window width.</param>
        /// <param name="height">Window height.</param>
        public void Open(string title, int width, int height)
        {
            if (_window != IntPtr.Zero)
                throw new InvalidOperationException("Window is already open!");

            _shouldStop = false;
            _windowReady.Reset();
            _lastWidth = width;
            _lastHeight = height;

            _eventThread = new Thread(() => EventThreadProc(title, width, height))
            {
                Name = "VST X11 Event Thread",
                IsBackground = true
            };
            _eventThread.Start();

            _windowReady.Wait();

            if (_window == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create X11 window on event thread.");
        }

        /// <summary>
        /// Main procedure for the X11 event thread.
        /// Initializes the display, window, and enters the event loop.
        /// </summary>
        /// <param name="title">Window title.</param>
        /// <param name="width">Window width.</param>
        /// <param name="height">Window height.</param>
        private void EventThreadProc(string title, int width, int height)
        {
            try
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    Console.WriteLine("[VST X11] XOpenDisplay failed");
                    _windowReady.Set();
                    return;
                }

                IntPtr root = XDefaultRootWindow(_display);
                _window = XCreateSimpleWindow(
                    _display, root,
                    0, 0, (uint)width, (uint)height,
                    1, 0x000000ul, 0xFFFFFFul);

                if (_window == IntPtr.Zero)
                {
                    Console.WriteLine("[VST X11] XCreateSimpleWindow failed.");
                    XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                    _windowReady.Set();
                    return;
                }

                XStoreName(_display, _window, title);
                SetMotifHints();

                _wmProtocols = XInternAtom(_display, "WM_PROTOCOLS", false);
                _wmDeleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", false);
                XSetWMProtocols(_display, _window, ref _wmDeleteWindow, 1);

                XSelectInput(_display, _window, StructureNotifyMask | ExposureMask);
                XMapWindow(_display, _window);
                XFlush(_display);

                _windowReady.Set();

                RunEventLoop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VST X11] Event thread error: {ex.Message}");
                _windowReady.Set();
            }
            finally
            {
                CleanupX11Resources();
            }
        }

        /// <summary>
        /// Sets window hints to enable standard manager functions.
        /// </summary>
        private void SetMotifHints()
        {
            IntPtr atom = XInternAtom(_display, "_MOTIF_WM_HINTS", false);
            MotifWmHints hints = new()
            {
                flags = MWM_HINTS_FUNCTIONS | MWM_HINTS_DECORATIONS,
                functions = MWM_FUNC_RESIZE | MWM_FUNC_MINIMIZE | MWM_FUNC_MAXIMIZE | MWM_FUNC_CLOSE,
                decorations = MWM_DECOR_ALL
            };
            IntPtr hintsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(hints));
            try
            {
                Marshal.StructureToPtr(hints, hintsPtr, false);
                XChangeProperty(_display, _window, atom, atom, 32, 0, hintsPtr, 5);
            }
            finally { Marshal.FreeHGlobal(hintsPtr); }
        }

        /// <summary>
        /// Runs the X11 event loop until termination is requested.
        /// Drains the invoke queue and processes X11 events.
        /// </summary>
        private void RunEventLoop()
        {
            while (!_shouldStop)
            {
                DrainInvokeQueue();

                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent xev);
                    HandleXEvent(ref xev);
                    if (_shouldStop) return;
                }

                Thread.Sleep(1);
            }
        }

        /// <summary>
        /// Processes all pending actions in the invoke queue.
        /// </summary>
        private void DrainInvokeQueue()
        {
            while (_invokeQueue.TryDequeue(out var item))
            {
                try { item.Action(); }
                catch (Exception ex) { item.Exception = ex; }
                finally { item.SyncSignal?.Set(); }
            }
        }

        /// <summary>
        /// Handles a single X11 event.
        /// Processes resize notifications and close requests.
        /// </summary>
        /// <param name="xev">The X11 event to handle.</param>
        private void HandleXEvent(ref XEvent xev)
        {
            switch (xev.Type)
            {
                case ConfigureNotify:
                    if (xev.CfgWidth > 0 && xev.CfgHeight > 0
                        && (xev.CfgWidth != _lastWidth || xev.CfgHeight != _lastHeight))
                    {
                        _lastWidth = xev.CfgWidth;
                        _lastHeight = xev.CfgHeight;
                        OnResize?.Invoke(_lastWidth, _lastHeight);
                    }
                    break;

                case ClientMessage:
                    if (xev.CmMessageType == _wmProtocols && xev.CmDataL0 == _wmDeleteWindow)
                    {
                        _shouldStop = true;
                        OnClosed?.Invoke();
                    }
                    break;
            }
        }

        /// <summary>
        /// Releases all X11 resources.
        /// </summary>
        private void CleanupX11Resources()
        {
            if (_window != IntPtr.Zero)
            {
                XDestroyWindow(_display, _window);
                _window = IntPtr.Zero;
            }
            if (_display != IntPtr.Zero)
            {
                XFlush(_display);
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Initiates the closing of the window.
        /// Ensures the event loop thread shuts down safely.
        /// </summary>
        public void Close()
        {
            _shouldStop = true;

            var thread = _eventThread;
            if (thread != null && Thread.CurrentThread != thread)
            {
                thread.Join(2000);
                _eventThread = null;
            }
        }

        /// <summary>
        /// Gets the platform-specific window handle.
        /// </summary>
        /// <returns>The X11 window pointer.</returns>
        public IntPtr GetHandle() => _window;

        /// <summary>
        /// Synchronously executes an action on the X11 event thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void Invoke(Action action)
        {
            if (_window == IntPtr.Zero) return;

            if (Thread.CurrentThread == _eventThread)
            {
                action();
                return;
            }

            using var signal = new ManualResetEventSlim(false);
            var item = new InvokeItem(action, signal);
            _invokeQueue.Enqueue(item);
            signal.Wait();

            if (item.Exception != null)
                throw new InvalidOperationException("Error on X11 event thread.", item.Exception);
        }

        /// <summary>
        /// Asynchronously posts an action to the X11 event thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void BeginInvoke(Action action)
        {
            if (_window == IntPtr.Zero) return;
            _invokeQueue.Enqueue(new InvokeItem(action));
        }

        /// <summary>
        /// Disposes of the native window resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _windowReady.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Finalizes an instance of the NativeWindowLinux class.
        /// </summary>
        ~NativeWindowLinux() => Dispose();
    }
}
