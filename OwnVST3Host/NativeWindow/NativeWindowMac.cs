using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// macOS natív ablakkezelés Cocoa/Objective-C Runtime használatával
    /// </summary>
    internal class NativeWindowMac : INativeWindow
    {
        private IntPtr _nsWindow = IntPtr.Zero;
        private IntPtr _nsView = IntPtr.Zero;
        private bool _disposed = false;

        public bool IsOpen => _nsWindow != IntPtr.Zero;

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

        public void Open(string title, int width, int height)
        {
            if (_nsWindow != IntPtr.Zero)
            {
                throw new InvalidOperationException("Az ablak már nyitva van!");
            }

            // NSWindow osztály lekérése
            IntPtr nsWindowClass = objc_getClass("NSWindow");
            if (nsWindowClass == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nem sikerült betölteni az NSWindow osztályt!");
            }

            // NSString létrehozása a címhez
            IntPtr nsStringClass = objc_getClass("NSString");
            IntPtr titleString = objc_msgSend(nsStringClass, sel_registerName("alloc"));
            titleString = objc_msgSend(titleString, sel_registerName("initWithUTF8String:"),
                Marshal.StringToHGlobalAnsi(title));

            // NSWindow létrehozása
            // macOS koordináta rendszer: bal alsó sarok (0,0), ezért 0,0-t használunk
            NSRect contentRect = new NSRect(0, 0, width, height);

            // NSWindowStyleMask: Titled | Closable | Miniaturizable | Resizable
            IntPtr styleMask = new IntPtr(1 | 2 | 4 | 8);

            _nsWindow = objc_msgSend(nsWindowClass, sel_registerName("alloc"));
            _nsWindow = objc_msgSend_initWithContentRect(_nsWindow, sel_registerName("initWithContentRect:styleMask:backing:defer:"),
                contentRect, styleMask, new IntPtr(2), IntPtr.Zero); // NSBackingStoreBuffered = 2

            if (_nsWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nem sikerült létrehozni az NSWindow-t!");
            }

            // Cím beállítása
            objc_msgSend(_nsWindow, sel_registerName("setTitle:"), titleString);

            // KRITIKUS: Beállítjuk, hogy az ablak bezárásakor ne lépjen ki az alkalmazásból
            objc_msgSend(_nsWindow, sel_registerName("setReleasedWhenClosed:"), false);

            // KRITIKUS: Beállítjuk, hogy az ablak ne tűnjön el, amikor elveszíti a fókuszt
            // Ez elengedhetetlen a VST plugin dropdown menük helyes működéséhez
            objc_msgSend(_nsWindow, sel_registerName("setHidesOnDeactivate:"), false);

            // Ablak középre igazítása (ezt a megjelenítés ELŐTT kell meghívni)
            objc_msgSend(_nsWindow, sel_registerName("center"));

            // NSView létrehozása (ezt adjuk vissza a VST plugin számára)
            // A view mérete 0,0-tól indul, mert a contentRect-hez relatív
            NSRect viewRect = new NSRect(0, 0, width, height);
            IntPtr nsViewClass = objc_getClass("NSView");
            _nsView = objc_msgSend(nsViewClass, sel_registerName("alloc"));
            _nsView = objc_msgSend_initWithFrame(_nsView, sel_registerName("initWithFrame:"), viewRect);

            if (_nsView == IntPtr.Zero)
            {
                throw new InvalidOperationException("Nem sikerült létrehozni az NSView-t!");
            }

            // NSView hozzáadása az ablakhoz
            IntPtr contentView = objc_msgSend(_nsWindow, sel_registerName("contentView"));
            objc_msgSend(contentView, sel_registerName("addSubview:"), _nsView);

            // Ablak megjelenítése
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
            // VST3 pluginok macOS-en NSView-t várnak
            return _nsView;
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

        ~NativeWindowMac()
        {
            Dispose();
        }
    }
}
