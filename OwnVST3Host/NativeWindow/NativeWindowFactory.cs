using System;
using System.Runtime.InteropServices;

namespace OwnVST3Host.NativeWindow
{
    /// <summary>
    /// Factory osztály a platform-specifikus natív ablak példányosításához
    /// </summary>
    public static class NativeWindowFactory
    {
        /// <summary>
        /// Létrehoz egy natív ablakot az aktuális operációs rendszernek megfelelően
        /// </summary>
        /// <returns>Platform-specifikus INativeWindow implementáció</returns>
        /// <exception cref="PlatformNotSupportedException">Ha az operációs rendszer nem támogatott</exception>
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
                $"A natív ablakkezelés nem támogatott ezen az operációs rendszeren: {RuntimeInformation.OSDescription}");
        }
    }
}
