using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Linux native window management using X11.
    ///
    /// Threading model:
    ///   Event loop thread – dedicated thread that owns the X11 display connection,
    ///                       processes ConfigureNotify (resize) and ClientMessage
    ///                       (WM_DELETE_WINDOW) events, and drains the invoke queue.
    ///   Caller thread     – Open() blocks until the window is ready, then returns.
    ///   Any thread        – Invoke() (sync) and BeginInvoke() (async) marshal work
    ///                       to the event loop thread.
    ///
    /// X11 resources are always created and destroyed on the event loop thread.
    /// </summary>
    internal class NativeWindowLinux : INativeWindow
    {
        private IntPtr _display = IntPtr.Zero;
        private IntPtr _window  = IntPtr.Zero;
        private IntPtr _wmProtocols   = IntPtr.Zero;
        private IntPtr _wmDeleteWindow = IntPtr.Zero;

        private Thread? _eventThread;
        private volatile bool _shouldStop;
        private readonly ManualResetEventSlim _windowReady = new(false);
        private readonly ConcurrentQueue<InvokeItem> _invokeQueue = new();
        private int _lastWidth;
        private int _lastHeight;
        private bool _disposed;

        public bool IsOpen   => _window != IntPtr.Zero;
        public bool IsActive => _window != IntPtr.Zero;

        public event Action<int, int>? OnResize;
        public event Action? OnClosed;

        // -------------------------------------------------------------------------
        // Invoke queue item
        // -------------------------------------------------------------------------

        private sealed class InvokeItem
        {
            public Action Action { get; }
            public ManualResetEventSlim? SyncSignal { get; }
            public Exception? Exception { get; set; }

            public InvokeItem(Action action, ManualResetEventSlim? syncSignal = null)
            {
                Action = action;
                SyncSignal = syncSignal;
            }
        }

        // -------------------------------------------------------------------------
        // X11 event type constants
        // -------------------------------------------------------------------------

        private const int ConfigureNotify = 22;
        private const int ClientMessage   = 33;

        /// <summary>
        /// Union-style XEvent struct. All X11 events start with 'int type' at offset 0.
        /// Fields at other offsets are only valid for specific event types.
        ///
        /// Offsets verified against X11 headers on 64-bit Linux (long=8, ptr=8):
        ///   ConfigureNotify.width   → offset 56  (int)
        ///   ConfigureNotify.height  → offset 60  (int)
        ///   ClientMessage.message_type → offset 40 (Atom = unsigned long → IntPtr)
        ///   ClientMessage.data.l[0]    → offset 56 (long → IntPtr)
        ///
        /// XEvent is a union of 'long pad[24]' = 192 bytes on 64-bit.
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct XEvent
        {
            [FieldOffset(0)]  public int    Type;
            // ConfigureNotify (type = 22)
            [FieldOffset(56)] public int    CfgWidth;
            [FieldOffset(60)] public int    CfgHeight;
            // ClientMessage (type = 33)
            [FieldOffset(40)] public IntPtr CmMessageType;   // Atom (unsigned long)
            [FieldOffset(56)] public IntPtr CmDataL0;        // data.l[0] (long)
        }

        // -------------------------------------------------------------------------
        // X11 P/Invoke declarations
        // -------------------------------------------------------------------------

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
            IntPtr display, IntPtr window,
            IntPtr property, IntPtr type,
            int format, int mode, IntPtr data, int nelements);

        [DllImport("libX11.so.6")]
        private static extern int XNextEvent(IntPtr display, out XEvent xevent);

        [DllImport("libX11.so.6")]
        private static extern int XPending(IntPtr display);

        private const long StructureNotifyMask = 1L << 17;
        private const long ExposureMask        = 1L << 15;

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
        private const uint MWM_FUNC_RESIZE       = 1u << 1;
        private const uint MWM_FUNC_CLOSE        = 1u << 5;
        private const uint MWM_FUNC_MINIMIZE     = 1u << 3;
        private const uint MWM_FUNC_MAXIMIZE     = 1u << 4;
        private const uint MWM_DECOR_ALL         = 1u << 0;

        // -------------------------------------------------------------------------
        // Open / event thread
        // -------------------------------------------------------------------------

        public void Open(string title, int width, int height)
        {
            if (_window != IntPtr.Zero)
                throw new InvalidOperationException("Window is already open!");

            _shouldStop = false;
            _windowReady.Reset();
            _lastWidth  = width;
            _lastHeight = height;

            _eventThread = new Thread(() => EventThreadProc(title, width, height))
            {
                Name         = "VST X11 Event Thread",
                IsBackground = true
            };
            _eventThread.Start();

            _windowReady.Wait();

            if (_window == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create X11 window on event thread.");
        }

        private void EventThreadProc(string title, int width, int height)
        {
            try
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    Console.WriteLine("[VST X11] XOpenDisplay failed — DISPLAY not set?");
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

                // Register WM_DELETE_WINDOW so the user can close the window.
                _wmProtocols    = XInternAtom(_display, "WM_PROTOCOLS",    false);
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
                // X11 resources are always released on the event thread.
                CleanupX11Resources();
            }
        }

        private void SetMotifHints()
        {
            IntPtr atom = XInternAtom(_display, "_MOTIF_WM_HINTS", false);
            MotifWmHints hints = new()
            {
                flags       = MWM_HINTS_FUNCTIONS | MWM_HINTS_DECORATIONS,
                functions   = MWM_FUNC_RESIZE | MWM_FUNC_MINIMIZE | MWM_FUNC_MAXIMIZE | MWM_FUNC_CLOSE,
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

        // -------------------------------------------------------------------------
        // Event loop
        // -------------------------------------------------------------------------

        private void RunEventLoop()
        {
            while (!_shouldStop)
            {
                // Drain the invoke queue first.
                DrainInvokeQueue();

                // Process all pending X11 events without blocking.
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent xev);
                    HandleXEvent(ref xev);
                    if (_shouldStop) return;
                }

                // Brief sleep to avoid busy-waiting. 1 ms gives acceptable latency for
                // Invoke() and resize/close detection while using negligible CPU.
                Thread.Sleep(1);
            }
        }

        private void DrainInvokeQueue()
        {
            while (_invokeQueue.TryDequeue(out var item))
            {
                try   { item.Action(); }
                catch (Exception ex) { item.Exception = ex; }
                finally { item.SyncSignal?.Set(); }
            }
        }

        private void HandleXEvent(ref XEvent xev)
        {
            switch (xev.Type)
            {
                case ConfigureNotify:
                    // ConfigureNotify fires for position changes too; only propagate
                    // when the size actually changed to avoid redundant ResizeEditor calls.
                    if (xev.CfgWidth > 0 && xev.CfgHeight > 0
                        && (xev.CfgWidth != _lastWidth || xev.CfgHeight != _lastHeight))
                    {
                        _lastWidth  = xev.CfgWidth;
                        _lastHeight = xev.CfgHeight;
                        OnResize?.Invoke(_lastWidth, _lastHeight);
                    }
                    break;

                case ClientMessage:
                    // WM_DELETE_WINDOW: user clicked the close button.
                    if (xev.CmMessageType == _wmProtocols && xev.CmDataL0 == _wmDeleteWindow)
                    {
                        _shouldStop = true;
                        OnClosed?.Invoke();
                    }
                    break;
            }
        }

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

        // -------------------------------------------------------------------------
        // Close
        // -------------------------------------------------------------------------

        public void Close()
        {
            _shouldStop = true;

            // Do not Join from the event thread itself — it would deadlock.
            var thread = _eventThread;
            if (thread != null && Thread.CurrentThread != thread)
            {
                thread.Join(2000);
                _eventThread = null;
            }
            // X11 resources are cleaned up by EventThreadProc's finally block.
        }

        // -------------------------------------------------------------------------
        // GetHandle / Invoke / BeginInvoke
        // -------------------------------------------------------------------------

        public IntPtr GetHandle() => _window;

        /// <summary>
        /// Synchronously executes an action on the X11 event loop thread.
        /// If called from the event thread itself, executes directly (no deadlock).
        /// </summary>
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
        /// Asynchronously posts an action to the X11 event loop thread.
        /// Returns immediately; the action runs within ~1 ms.
        /// </summary>
        public void BeginInvoke(Action action)
        {
            if (_window == IntPtr.Zero) return;
            _invokeQueue.Enqueue(new InvokeItem(action));
        }

        // -------------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------------

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

        ~NativeWindowLinux() => Dispose();
    }
}
