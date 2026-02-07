using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace OwnVST3Host.Platform
{
    /// <summary>
    /// Windows-specific helper for creating child windows for VST3 editor embedding.
    /// This solves the issue where Avalonia's NativeControlHost doesn't automatically
    /// create a child HWND on Windows, resulting in blank VST editor displays.
    /// </summary>
    internal static class WindowsChildWindowHelper
    {
        #region Win32 API Declarations

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        #endregion

        #region Win32 Constants

        private const uint WS_CHILD = 0x40000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_CLIPCHILDREN = 0x02000000;
        private const uint WS_CLIPSIBLINGS = 0x04000000;
        private const uint WS_EX_CONTROLPARENT = 0x00010000;

        private const int SW_SHOW = 5;

        #endregion

        /// <summary>
        /// Creates a Windows child window suitable for VST3 editor embedding.
        /// </summary>
        /// <param name="parent">Parent window handle (HWND)</param>
        /// <param name="width">Initial width of child window</param>
        /// <param name="height">Initial height of child window</param>
        /// <returns>Handle to the created child window, or IntPtr.Zero on failure</returns>
        public static IntPtr CreateChildWindow(IPlatformHandle parent, int width, int height)
        {
            if (parent == null || parent.Handle == IntPtr.Zero)
            {
                throw new ArgumentException("Parent window handle is invalid", nameof(parent));
            }

            return CreateChildWindow(parent.Handle, width, height);
        }

        /// <summary>
        /// Creates a Windows child window suitable for VST3 editor embedding.
        /// </summary>
        /// <param name="parentHwnd">Parent window handle (HWND)</param>
        /// <param name="width">Initial width of child window</param>
        /// <param name="height">Initial height of child window</param>
        /// <returns>Handle to the created child window, or IntPtr.Zero on failure</returns>
        public static IntPtr CreateChildWindow(IntPtr parentHwnd, int width, int height)
        {
            if (parentHwnd == IntPtr.Zero || !IsWindow(parentHwnd))
            {
                throw new ArgumentException("Parent window handle is invalid", nameof(parentHwnd));
            }

            // Get module handle for the current process
            IntPtr hInstance = GetModuleHandle(null);

            // Create child window with appropriate styles for VST embedding
            // WS_CHILD: This is a child window
            // WS_VISIBLE: Window is initially visible
            // WS_CLIPCHILDREN: Excludes child windows when drawing
            // WS_CLIPSIBLINGS: Clips child windows relative to each other
            uint style = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
            uint exStyle = WS_EX_CONTROLPARENT;

            IntPtr childHwnd = CreateWindowEx(
                exStyle,
                "Static", // Use "Static" window class (simple container)
                "VstEditorContainer",
                style,
                0, 0, // Position (x, y)
                width, height,
                parentHwnd,
                IntPtr.Zero, // No menu
                hInstance,
                IntPtr.Zero);

            if (childHwnd == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to create child window. Win32 error: {error}");
            }

            // Make sure window is visible and updated
            ShowWindow(childHwnd, SW_SHOW);
            UpdateWindow(childHwnd);

            return childHwnd;
        }

        /// <summary>
        /// Destroys a previously created child window.
        /// </summary>
        /// <param name="hwnd">Handle to the window to destroy</param>
        /// <returns>True if successful, false otherwise</returns>
        public static bool DestroyChildWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            return DestroyWindow(hwnd);
        }

        /// <summary>
        /// Resizes a child window.
        /// </summary>
        /// <param name="hwnd">Handle to the window to resize</param>
        /// <param name="width">New width</param>
        /// <param name="height">New height</param>
        /// <returns>True if successful, false otherwise</returns>
        public static bool ResizeChildWindow(IntPtr hwnd, int width, int height)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return false;

            return MoveWindow(hwnd, 0, 0, width, height, true);
        }

        /// <summary>
        /// Checks if a handle is a valid window.
        /// </summary>
        /// <param name="hwnd">Window handle to check</param>
        /// <returns>True if the handle is a valid window</returns>
        public static bool IsValidWindow(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && IsWindow(hwnd);
        }
    }
}
