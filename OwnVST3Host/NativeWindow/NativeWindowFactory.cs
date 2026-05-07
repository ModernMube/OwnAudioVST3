using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Factory class for instantiating platform-specific native windows.
    /// </summary>
    public static class NativeWindowFactory
    {
        /// <summary>
        /// Creates a native window appropriate for the current operating system.
        /// </summary>
        /// <returns>Platform-specific INativeWindow implementation</returns>
        /// <exception cref="PlatformNotSupportedException">If the OS is not supported</exception>
        public static INativeWindow Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new NativeWindowWindows();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return new NativeWindowMac();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new NativeWindowLinux();
            }

            throw new PlatformNotSupportedException(
                $"Native window management is not supported on this OS: {RuntimeInformation.OSDescription}");
        }
    }
}
