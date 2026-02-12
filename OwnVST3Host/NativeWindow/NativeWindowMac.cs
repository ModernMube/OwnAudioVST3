using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// macOS native window management using Cocoa/Objective-C Runtime
    /// </summary>
    internal class NativeWindowMac : INativeWindow
    {
        private IntPtr _nsWindow = IntPtr.Zero;
        private IntPtr _nsView = IntPtr.Zero;
        private bool _disposed = false;

        // Main thread reference - on macOS all Cocoa/AppKit operations
        // must run on the main thread, otherwise undefined behavior
        // Note: dispatch_get_main_queue() is an inline function, so we load _dispatch_main_q directly
        private static readonly IntPtr _mainQueue;

        public bool IsOpen => _nsWindow != IntPtr.Zero;

        public bool IsActive
        {
            get
            {
                if (_nsWindow == IntPtr.Zero)
                    return false;

                // Check if this window is the key window (has keyboard focus)
                IntPtr result = objc_msgSend(_nsWindow, sel_registerName("isKeyWindow"));
                return result != IntPtr.Zero;
            }
        }

        public event Action<int, int>? OnResize;
        public event Action? OnClosed;

        #region Objective-C Runtime Declarations

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_getClass(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, bool arg1);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_initWithContentRect(IntPtr receiver, IntPtr selector, NSRect rect, IntPtr styleMask, IntPtr backing, IntPtr defer);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend_initWithFrame(IntPtr receiver, IntPtr selector, NSRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double X;
            public double Y;
            public double Width;
            public double Height;

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

        // Note: dispatch_get_main_queue() is inline, not a real function
        // We use dlsym to load _dispatch_main_q symbol directly

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern void dispatch_async_f(IntPtr queue, IntPtr context, dispatch_function_t work);

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern void dispatch_sync_f(IntPtr queue, IntPtr context, dispatch_function_t work);

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern int pthread_main_np();

        private delegate void dispatch_function_t(IntPtr context);

        // CFRunLoop API - supports explicit RunLoop modes
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern IntPtr CFRunLoopGetMain();

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRunLoopTimerContext
        {
            public IntPtr version;
            public IntPtr info;
            public IntPtr retain;
            public IntPtr release;
            public IntPtr copyDescription;
        }

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern IntPtr CFRunLoopTimerCreate(
            IntPtr allocator,
            double fireDate,
            double interval,
            ulong flags,
            long order,
            CFRunLoopTimerCallBack callout,
            ref CFRunLoopTimerContext context);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRunLoopAddTimer(IntPtr rl, IntPtr timer, IntPtr mode);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern double CFAbsoluteTimeGetCurrent();

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRelease(IntPtr cf);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRunLoopWakeUp(IntPtr rl);

        private delegate void CFRunLoopTimerCallBack(IntPtr timer, IntPtr info);

        // Static delegate reference to prevent GC collection
        private static readonly CFRunLoopTimerCallBack TimerCallbackDelegate = TimerCallback;

        // kCFRunLoopCommonModes constant (CFStringRef)
        // This mode set contains both NSDefaultRunLoopMode AND NSEventTrackingRunLoopMode
        // So our callback runs even when a dropdown menu is open (tracking mode)
        private static readonly IntPtr kCFRunLoopCommonModes;

        // dlopen/dlsym for loading framework constants and dispatch queue
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlopen(string path, int mode);

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private const int RTLD_LAZY = 1;
        private const int RTLD_DEFAULT = -2; // Special handle for default search

        static NativeWindowMac()
        {
            try
            {
                // Load _dispatch_main_q symbol (dispatch_get_main_queue is inline, not a real function)
                // Use RTLD_DEFAULT to search in already loaded libraries
                IntPtr mainQueueSymbol = dlsym(new IntPtr(RTLD_DEFAULT), "_dispatch_main_q");
                if (mainQueueSymbol != IntPtr.Zero)
                {
                    // _dispatch_main_q is a struct, not a pointer, so we use its address directly
                    _mainQueue = mainQueueSymbol;
                }
                else
                {
                    throw new InvalidOperationException("Failed to load _dispatch_main_q symbol");
                }

                // Load kCFRunLoopCommonModes constant directly from CoreFoundation framework
                // This is the correct way to access built-in CFString constants
                IntPtr cfHandle = dlopen("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", RTLD_LAZY);
                if (cfHandle != IntPtr.Zero)
                {
                    IntPtr symbolPtr = dlsym(cfHandle, "kCFRunLoopCommonModes");
                    if (symbolPtr != IntPtr.Zero)
                    {
                        // kCFRunLoopCommonModes is a CFStringRef*, so we need to dereference it
                        kCFRunLoopCommonModes = Marshal.ReadIntPtr(symbolPtr);
                    }
                }

                if (kCFRunLoopCommonModes == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Failed to load kCFRunLoopCommonModes constant from CoreFoundation framework");
                }
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private static bool IsMainThread() => pthread_main_np() != 0;

        /// <summary>
        /// Asynchronously executes an operation on the macOS main thread.
        /// Uses CFRunLoopTimer with kCFRunLoopCommonModes, which ensures
        /// that the callback runs even when a dropdown menu is in tracking mode.
        /// This solves VST plugin editor freezing on macOS.
        /// </summary>
        private static void DispatchToMainAsync(Action action)
        {
            if (IsMainThread())
            {
                action();
                return;
            }

            // Create GCHandle - ensures the Action is not garbage collected
            var handle = GCHandle.Alloc(action);

            // Create CFRunLoopTimerContext with the GCHandle as info
            var context = new CFRunLoopTimerContext
            {
                version = IntPtr.Zero,          // version 0
                info = GCHandle.ToIntPtr(handle), // our GCHandle
                retain = IntPtr.Zero,           // no retain callback
                release = IntPtr.Zero,          // no release callback
                copyDescription = IntPtr.Zero   // no description callback
            };

            // Create one-shot timer that fires immediately on the main RunLoop
            // fireDate: current time (immediately)
            // interval: 0 (one-shot, does not repeat)
            double fireDate = CFAbsoluteTimeGetCurrent();
            IntPtr timer = CFRunLoopTimerCreate(
                IntPtr.Zero,                    // allocator (default)
                fireDate,                       // fireDate (now, immediately)
                0,                              // interval (0 = one-shot)
                0,                              // flags
                0,                              // order
                TimerCallbackDelegate,          // callback (use static delegate to prevent GC)
                ref context);                   // context struct

            if (timer == IntPtr.Zero)
            {
                handle.Free();
                throw new InvalidOperationException("Failed to create CFRunLoopTimer");
            }

            // Add timer to main RunLoop with kCFRunLoopCommonModes
            // This guarantees that the timer fires even when:
            // - NSDefaultRunLoopMode is active (normal operation)
            // - NSEventTrackingRunLoopMode is active (dropdown menu, scrolling, etc.)
            IntPtr mainRunLoop = CFRunLoopGetMain();
            CFRunLoopAddTimer(mainRunLoop, timer, kCFRunLoopCommonModes);

            // Wake up RunLoop if it's waiting
            CFRunLoopWakeUp(mainRunLoop);

            // Release timer - the RunLoop owns it until it fires
            CFRelease(timer);
        }

        /// <summary>
        /// Synchronously executes an operation on the macOS main thread (dispatch_sync).
        /// WARNING: If called from the main thread, executes directly (avoids deadlock).
        /// </summary>
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
        /// CFRunLoopTimer callback - runs on the main thread (RunLoop context).
        /// Unwraps the Action from the GCHandle and executes it.
        /// The 'timer' parameter is the CFRunLoopTimer reference, 'info' is the context (GCHandle pointer).
        /// </summary>
        private static void TimerCallback(IntPtr timer, IntPtr info)
        {
            var handle = GCHandle.FromIntPtr(info);
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

        /// <summary>
        /// GCD callback (used for dispatch_sync) - runs on the main thread.
        /// Unwraps the Action from the GCHandle and executes it.
        /// </summary>
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

        public void Open(string title, int width, int height)
        {
            if (_nsWindow != IntPtr.Zero)
            {
                throw new InvalidOperationException("Window is already open!");
            }

            // Get NSWindow class
            IntPtr nsWindowClass = objc_getClass("NSWindow");
            if (nsWindowClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to load NSWindow class!");
            }

            // Create NSString for the title
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

            // Create NSWindow
            // macOS coordinate system: bottom-left corner is (0,0), so we use 0,0
            NSRect contentRect = new NSRect(0, 0, width, height);

            // NSWindowStyleMask: Titled | Miniaturizable | Resizable (NO Closable)
            // The window lifecycle is managed by code, not by user interaction
            // Titled = 1, Closable = 2 (excluded), Miniaturizable = 4, Resizable = 8
            IntPtr styleMask = new IntPtr(1 | 4 | 8);

            _nsWindow = objc_msgSend(nsWindowClass, sel_registerName("alloc"));
            _nsWindow = objc_msgSend_initWithContentRect(_nsWindow, sel_registerName("initWithContentRect:styleMask:backing:defer:"),
                contentRect, styleMask, new IntPtr(2), IntPtr.Zero); // NSBackingStoreBuffered = 2

            if (_nsWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create NSWindow!");
            }

            // Set title
            objc_msgSend(_nsWindow, sel_registerName("setTitle:"), titleString);

            // Release the title string (setTitle: retains it)
            if (titleString != IntPtr.Zero)
                objc_msgSend(titleString, sel_registerName("release"));

            // CRITICAL: Set window to not quit the application when closed
            objc_msgSend(_nsWindow, sel_registerName("setReleasedWhenClosed:"), false);

            // CRITICAL: Set window to not disappear when it loses focus
            // This is essential for proper VST plugin dropdown menu behavior
            objc_msgSend(_nsWindow, sel_registerName("setHidesOnDeactivate:"), false);

            // Enable mouse events - required for proper dropdown menu tracking
            objc_msgSend(_nsWindow, sel_registerName("setAcceptsMouseMovedEvents:"), true);

            // Center window (must be called BEFORE showing)
            objc_msgSend(_nsWindow, sel_registerName("center"));

            // Create NSView (this is what we return to the VST plugin)
            // View size starts at 0,0 because it's relative to contentRect
            NSRect viewRect = new NSRect(0, 0, width, height);
            IntPtr nsViewClass = objc_getClass("NSView");
            _nsView = objc_msgSend(nsViewClass, sel_registerName("alloc"));
            _nsView = objc_msgSend_initWithFrame(_nsView, sel_registerName("initWithFrame:"), viewRect);

            if (_nsView == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create NSView!");
            }

            // Add NSView to window
            IntPtr contentView = objc_msgSend(_nsWindow, sel_registerName("contentView"));
            objc_msgSend(contentView, sel_registerName("addSubview:"), _nsView);

            // Show window
            objc_msgSend(_nsWindow, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
        }

        public void Close()
        {
            if (_nsWindow != IntPtr.Zero)
            {
                objc_msgSend(_nsWindow, sel_registerName("close"));

                // Release
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

        public IntPtr GetHandle()
        {
            // VST3 plugins on macOS expect an NSView
            return _nsView;
        }

        /// <summary>
        /// Synchronous execution on the main thread.
        /// On macOS, all AppKit/Cocoa operations must run on the main thread.
        /// </summary>
        public void Invoke(Action action) => DispatchToMainSync(action);

        /// <summary>
        /// Asynchronous execution on the main thread.
        /// On macOS, all AppKit/Cocoa operations must run on the main thread.
        /// Uses CFRunLoopTimer with kCFRunLoopCommonModes, which ensures
        /// that the callback runs even when a dropdown menu is in tracking mode.
        /// This solves VST plugin editor freezing on macOS under heavy background load.
        /// </summary>
        public void BeginInvoke(Action action) => DispatchToMainAsync(action);

        public void Dispose()
        {
            if (!_disposed)
            {
                Close();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        ~NativeWindowMac()
        {
            Dispose();
        }
    }
}
