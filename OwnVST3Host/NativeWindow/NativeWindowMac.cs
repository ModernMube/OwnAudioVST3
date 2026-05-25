using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// macOS native window management using Cocoa and Objective-C Runtime.
    /// Manages window creation, rendering, and thread synchronization.
    /// </summary>
    internal class NativeWindowMac : INativeWindow
    {
        /// <summary>
        /// Pointer to the NSWindow instance.
        /// </summary>
        private IntPtr _nsWindow = IntPtr.Zero;

        /// <summary>
        /// Pointer to the NSView instance.
        /// </summary>
        private IntPtr _nsView = IntPtr.Zero;

        /// <summary>
        /// Indicates whether the window resources have been disposed.
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// Reference to the main GCD dispatch queue.
        /// </summary>
        private static readonly IntPtr _mainQueue;

        /// <summary>
        /// Gets a value indicating whether the window is open.
        /// </summary>
        public bool IsOpen => _nsWindow != IntPtr.Zero;

        /// <summary>
        /// Gets a value indicating whether the window is active and has keyboard focus.
        /// </summary>
        public bool IsActive
        {
            get
            {
                if (_nsWindow == IntPtr.Zero)
                    return false;

                IntPtr result = objc_msgSend(_nsWindow, sel_registerName("isKeyWindow"));
                return result != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Occurs when the window is resized.
        /// </summary>
        public event Action<int, int>? OnResize;

        /// <summary>
        /// Occurs when the window is closed.
        /// </summary>
        public event Action? OnClosed;

        #region Objective-C Runtime Declarations

        /// <summary>
        /// Gets an Objective-C class by name.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_getClass(string name);

        /// <summary>
        /// Registers a method selector.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr sel_registerName(string name);

        /// <summary>
        /// Sends a message to an Objective-C object.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        /// <summary>
        /// Sends a message to an Objective-C object with one argument.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

        /// <summary>
        /// Sends a message to an Objective-C object with a boolean argument.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        /// <summary>
        /// Sends a message to an Objective-C object with a long argument.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, long arg1);

        /// <summary>
        /// Initializes a window with a content rectangle.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_initWithContentRect(IntPtr receiver, IntPtr selector, NSRect rect, IntPtr styleMask, IntPtr backing, IntPtr defer);

        /// <summary>
        /// Initializes a view with a frame rectangle.
        /// </summary>
        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_initWithFrame(IntPtr receiver, IntPtr selector, NSRect rect);

        /// <summary>
        /// Represents a rectangle in the macOS coordinate system.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            /// <summary>
            /// The x-coordinate.
            /// </summary>
            public double X;
            /// <summary>
            /// The y-coordinate.
            /// </summary>
            public double Y;
            /// <summary>
            /// The width.
            /// </summary>
            public double Width;
            /// <summary>
            /// The height.
            /// </summary>
            public double Height;

            /// <summary>
            /// Initializes a new instance of the NSRect struct.
            /// </summary>
            /// <param name="x">The x-coordinate.</param>
            /// <param name="y">The y-coordinate.</param>
            /// <param name="width">The width.</param>
            /// <param name="height">The height.</param>
            public NSRect(double x, double y, double width, double height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        #endregion

        #region GCD (Grand Central Dispatch) and CFRunLoop for main thread marshaling

        /// <summary>
        /// Submits a block for asynchronous execution on a dispatch queue.
        /// </summary>
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern void dispatch_async_f(IntPtr queue, IntPtr context, dispatch_function_t work);

        /// <summary>
        /// Submits a block for synchronous execution on a dispatch queue.
        /// </summary>
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern void dispatch_sync_f(IntPtr queue, IntPtr context, dispatch_function_t work);

        /// <summary>
        /// Identifies the main thread.
        /// </summary>
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern int pthread_main_np();

        /// <summary>
        /// Delegate for dispatch functions.
        /// </summary>
        private delegate void dispatch_function_t(IntPtr context);

        /// <summary>
        /// Loads a dynamic library.
        /// </summary>
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        /// <summary>
        /// Obtains the address of a symbol in a dynamic library.
        /// </summary>
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        /// <summary>
        /// Constant for default symbol resolution search.
        /// </summary>
        private const int RTLD_DEFAULT = -2;

        /// <summary>
        /// Initializes static members of the NativeWindowMac class.
        /// </summary>
        static NativeWindowMac()
        {
            IntPtr mainQueueSymbol = dlsym(new IntPtr(RTLD_DEFAULT), "_dispatch_main_q");
            if (mainQueueSymbol == IntPtr.Zero)
                throw new InvalidOperationException("Failed to load _dispatch_main_q symbol.");
            _mainQueue = mainQueueSymbol;
        }

        /// <summary>
        /// Determines whether the current thread is the macOS main thread.
        /// </summary>
        /// <returns>True if the current thread is the main thread; otherwise, false.</returns>
        private static bool IsMainThread() => pthread_main_np() != 0;

        /// <summary>
        /// Asynchronously executes an action on the macOS main thread.
        /// Avoids deadlocks during UI tracking modes.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        private static void DispatchToMainAsync(Action action)
        {
            if (IsMainThread())
            {
                action();
                return;
            }

            var handle = GCHandle.Alloc(action);
            dispatch_async_f(_mainQueue, GCHandle.ToIntPtr(handle), DispatchCallback);
        }

        /// <summary>
        /// Synchronously executes an action on the macOS main thread.
        /// If already on the main thread, executes directly.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        private static void DispatchToMainSync(Action action)
        {
            if (IsMainThread())
            {
                action();
                return;
            }

            var handle = GCHandle.Alloc(action);
            dispatch_sync_f(_mainQueue, GCHandle.ToIntPtr(handle), DispatchCallback);
        }

        /// <summary>
        /// Callback executed by Grand Central Dispatch.
        /// </summary>
        /// <param name="context">The context containing the action handle.</param>
        private static void DispatchCallback(IntPtr context)
        {
            var handle = GCHandle.FromIntPtr(context);
            try
            {
                var action = (Action)handle.Target!;
                action();
            }
            finally
            {
                handle.Free();
            }
        }

        #endregion

        /// <summary>
        /// Opens a native macOS window.
        /// </summary>
        /// <param name="title">The window title.</param>
        /// <param name="width">The window width.</param>
        /// <param name="height">The window height.</param>
        public void Open(string title, int width, int height)
        {
            if (_nsWindow != IntPtr.Zero)
            {
                throw new InvalidOperationException("Window is already open!");
            }

            if (!IsMainThread())
            {
                DispatchToMainSync(() => OpenOnMainThread(title, width, height));
                return;
            }

            OpenOnMainThread(title, width, height);
        }

        /// <summary>
        /// Internal implementation to open the window on the main thread.
        /// </summary>
        /// <param name="title">The window title.</param>
        /// <param name="width">The window width.</param>
        /// <param name="height">The window height.</param>
        private void OpenOnMainThread(string title, int width, int height)
        {
            IntPtr nsWindowClass = objc_getClass("NSWindow");
            if (nsWindowClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to load NSWindow class!");
            }

            IntPtr titleString = IntPtr.Zero;
            IntPtr utf8Ptr = IntPtr.Zero;
            try
            {
                utf8Ptr = Marshal.StringToHGlobalAnsi(title);
                IntPtr nsStringClass = objc_getClass("NSString");
                titleString = objc_msgSend(nsStringClass, sel_registerName("alloc"));
                titleString = objc_msgSend(titleString, sel_registerName("initWithUTF8String:"), utf8Ptr);
            }
            finally
            {
                if (utf8Ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(utf8Ptr);
            }

            NSRect contentRect = new NSRect(0, 0, width, height);
            IntPtr styleMask = new IntPtr(1 | 2 | 4 | 8);

            _nsWindow = objc_msgSend(nsWindowClass, sel_registerName("alloc"));
            _nsWindow = objc_msgSend_initWithContentRect(_nsWindow, sel_registerName("initWithContentRect:styleMask:backing:defer:"),
                contentRect, styleMask, new IntPtr(2), IntPtr.Zero);

            if (_nsWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create NSWindow!");
            }

            objc_msgSend(_nsWindow, sel_registerName("setTitle:"), titleString);

            if (titleString != IntPtr.Zero)
                objc_msgSend(titleString, sel_registerName("release"));

            objc_msgSend(_nsWindow, sel_registerName("setReleasedWhenClosed:"), false);
            objc_msgSend(_nsWindow, sel_registerName("setHidesOnDeactivate:"), false);
            objc_msgSend(_nsWindow, sel_registerName("setAcceptsMouseMovedEvents:"), true);
            objc_msgSend(_nsWindow, sel_registerName("setIgnoresMouseEvents:"), false);
            objc_msgSend(_nsWindow, sel_registerName("setLevel:"), 0L);
            objc_msgSend(_nsWindow, sel_registerName("center"));

            NSRect viewRect = new NSRect(0, 0, width, height);
            IntPtr nsViewClass = objc_getClass("NSView");
            _nsView = objc_msgSend(nsViewClass, sel_registerName("alloc"));
            _nsView = objc_msgSend_initWithFrame(_nsView, sel_registerName("initWithFrame:"), viewRect);

            if (_nsView == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create NSView!");
            }

            objc_msgSend(_nsWindow, sel_registerName("setContentView:"), _nsView);
            objc_msgSend(_nsWindow, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
        }

        /// <summary>
        /// No-op on macOS: the window is already visible after Open().
        /// </summary>
        public void Show() { }

        /// <summary>
        /// Closes the native macOS window.
        /// </summary>
        public void Close()
        {
            if (!IsMainThread())
            {
                DispatchToMainSync(CloseOnMainThread);
                return;
            }

            CloseOnMainThread();
        }

        /// <summary>
        /// Internal implementation to close the window on the main thread.
        /// </summary>
        private void CloseOnMainThread()
        {
            if (_nsWindow != IntPtr.Zero)
            {
                objc_msgSend(_nsWindow, sel_registerName("close"));
                objc_msgSend(_nsWindow, sel_registerName("release"));
                _nsWindow = IntPtr.Zero;
            }

            if (_nsView != IntPtr.Zero)
            {
                objc_msgSend(_nsView, sel_registerName("release"));
                _nsView = IntPtr.Zero;
            }

            OnClosed?.Invoke();
        }

        /// <summary>
        /// Gets the handle to the macOS native view.
        /// </summary>
        /// <returns>The pointer to the NSView.</returns>
        public IntPtr GetHandle()
        {
            return _nsView;
        }

        /// <summary>
        /// Synchronously executes an action on the macOS main thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void Invoke(Action action) => DispatchToMainSync(action);

        /// <summary>
        /// Asynchronously executes an action on the macOS main thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void BeginInvoke(Action action) => DispatchToMainAsync(action);

        /// <summary>
        /// Disposes of the native window resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Finalizes an instance of the NativeWindowMac class.
        /// </summary>
        ~NativeWindowMac()
        {
            Dispose();
        }
    }
}
