using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace OwnVST3Host.Platform
{
    /// <summary>
    /// Provides cross-platform native window handle retrieval for VST3 editor embedding
    /// </summary>
    public static class NativeWindowHandle
    {
        /// <summary>
        /// Gets the native window handle from an Avalonia TopLevel (Window)
        /// </summary>
        /// <param name="topLevel">The Avalonia TopLevel/Window</param>
        /// <returns>Native window handle (HWND on Windows, X11 Window on Linux, NSView on macOS)</returns>
        public static IntPtr GetHandle(TopLevel topLevel)
        {
            if (topLevel == null)
                throw new ArgumentNullException(nameof(topLevel));

            var platformHandle = topLevel.TryGetPlatformHandle();
            if (platformHandle == null)
                throw new InvalidOperationException("Failed to get platform handle from window");

            return platformHandle.Handle;
        }

        /// <summary>
        /// Gets the native window handle from a Control's parent window
        /// </summary>
        /// <param name="control">The Avalonia Control</param>
        /// <returns>Native window handle</returns>
        public static IntPtr GetHandle(Control control)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));

            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel == null)
                throw new InvalidOperationException("Control is not attached to a window");

            return GetHandle(topLevel);
        }

        /// <summary>
        /// Checks if the current platform supports VST3 editor embedding
        /// </summary>
        public static bool IsSupported =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// Gets the current platform type
        /// </summary>
        public static PlatformType CurrentPlatform
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return PlatformType.Windows;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return PlatformType.Linux;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return PlatformType.MacOS;
                return PlatformType.Unknown;
            }
        }
    }

    /// <summary>
    /// Platform types for VST3 editor
    /// </summary>
    public enum PlatformType
    {
        Unknown,
        Windows,
        Linux,
        MacOS
    }
}
