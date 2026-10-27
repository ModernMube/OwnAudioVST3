using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public unsafe partial class OwnVst3Wrapper : IDisposable
    {
#nullable disable warnings
        #region Private fields

        private IntPtr _pluginHandle;
        private IntPtr _libraryHandle;
        private bool _disposed = false;

        // Library name constants
        private const string WindowsLibraryName = "ownvst3.dll";
        private const string LinuxLibraryName = "libownvst3.so";
        private const string MacOSLibraryName = "libownvst3.dylib";
        private const string IOSLibraryName = "ownvst3.framework/ownvst3";

        // Planar scratch the plugin reads and writes, off the GC heap entirely. Pinning
        // the caller's float[] per block used to cost two GC handles per channel per
        // block and fragmented gen0 for as long as playback ran.
        private unsafe float** _inPlanes;
        private unsafe float** _outPlanes;
        private unsafe float*  _inData;
        private unsafe float*  _outData;
        private int _preallocChannels;
        private int _preallocBlock;

        private int _actualIn  = 2;
        private int _actualOut = 2;

        #endregion

        #region Constructor and Destructor

        /// <summary>
        /// Creates a new OwnVst3Wrapper instance with automatic platform detection.
        /// Loads the native library from the runtimes folder.
        /// </summary>
        public OwnVst3Wrapper() : this(GetNativeLibraryPath())
        {
        }

        /// <summary>
        /// Creates a new OwnVst3Wrapper instance
        /// </summary>
        /// <param name="dllPath">Path to the ownvst3 native library</param>
        public OwnVst3Wrapper(string dllPath)
        {
            _libraryHandle = NativeLibrary.Load(dllPath);
            if (_libraryHandle == IntPtr.Zero)
            {
                throw new DllNotFoundException($"Failed to load library: {dllPath}");
            }

            InitializeDelegates();

            _pluginHandle = _createFunc();
            if (_pluginHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create VST3 plugin instance");
            }
        }

        #endregion

        #region Platform Detection

        /// <summary>
        /// Gets the runtime identifier for the current platform
        /// </summary>
        /// <returns>Runtime identifier string (e.g., "win-x64", "linux-x64", "osx-arm64")</returns>
        public static string GetRuntimeIdentifier()
        {
            string os;
            if (OperatingSystem.IsIOS())
                os = _onSimulator ? "iossimulator" : "ios";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                os = "win";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                os = "linux";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                os = "osx";
            else
                throw new PlatformNotSupportedException("Unsupported operating system");

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
            };

            return $"{os}-{arch}";
        }

        /// <summary>
        /// Gets the native library filename for the current platform
        /// </summary>
        /// <returns>Library filename</returns>
        public static string GetNativeLibraryName()
        {
            if (OperatingSystem.IsIOS())
                return IOSLibraryName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsLibraryName;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return LinuxLibraryName;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacOSLibraryName;
            else
                throw new PlatformNotSupportedException("Unsupported operating system");
        }

        /// <summary>
        /// Full path to the native library. Looks next to the assembly, then the app's base
        /// directory (single-file builds leave Assembly.Location empty), the .app bundle's
        /// Frameworks folder and finally the current directory.
        /// </summary>
        public static string GetNativeLibraryPath()
        {
            if (OperatingSystem.IsIOS()) return _iosLibraryPath();

            string rid = GetRuntimeIdentifier();
            string libraryName = GetNativeLibraryName();

            var _paths = new List<string>();
            foreach (string _dir in _libraryFolders())
            {
                _paths.Add(Path.Combine(_dir, "runtimes", rid, "native", libraryName));
                if (rid.StartsWith("osx")) _paths.Add(Path.Combine(_dir, "runtimes", "osx", "native", libraryName));
                _paths.Add(Path.Combine(_dir, libraryName));
            }

            foreach (string path in _paths)
                if (File.Exists(path)) return path;

            throw new DllNotFoundException(
                $"Native library not found: {libraryName}\n" +
                $"Runtime identifier: {rid}\n" +
                $"Searched paths:\n - {string.Join("\n - ", _paths)}");
        }

        // The OwnAudioVst targets embed ownvst3.xcframework, so it sits in the app's Frameworks folder
        private static string _iosLibraryPath()
        {
            string _embedded = Path.Combine(AppContext.BaseDirectory, "Frameworks", "ownvst3.framework", "ownvst3");
            if (File.Exists(_embedded)) return _embedded;

            //dyld resolves this through the app's own rpath
            return "@rpath/" + IOSLibraryName;
        }

        private static bool _onSimulator => Environment.GetEnvironmentVariable("SIMULATOR_DEVICE_NAME") != null;

        private static IEnumerable<string> _libraryFolders()
        {
            string baseDir = AppContext.BaseDirectory;

            var folders = new List<string>
            {
                Path.GetDirectoryName(typeof(OwnVst3Wrapper).Assembly.Location) ?? "",
                baseDir
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                folders.Add(Path.Combine(baseDir, "..", "Frameworks"));
            folders.Add(Directory.GetCurrentDirectory());

            return folders.Where(d => d.Length > 0)
                          .Select(Path.GetFullPath)
                          .Distinct();
        }

        /// <summary>
        /// Gets the default VST3 plugin directories for the current platform.
        /// Returns both system-wide and user-specific directories.
        /// </summary>
        /// <returns>Array of directory paths where VST3 plugins are typically installed</returns>
        public static string[] GetDefaultVst3Directories()
        {
            var directories = new List<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows standard VST3 locations
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                directories.Add(Path.Combine(programFiles, "Common Files", "VST3"));
                if (!string.IsNullOrEmpty(programFilesX86) && programFilesX86 != programFiles)
                {
                    directories.Add(Path.Combine(programFilesX86, "Common Files", "VST3"));
                }
                directories.Add(Path.Combine(localAppData, "Programs", "Common", "VST3"));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS standard VST3 locations
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                directories.Add("/Library/Audio/Plug-Ins/VST3");
                directories.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3"));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux standard VST3 locations
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                directories.Add("/usr/lib/vst3");
                directories.Add("/usr/local/lib/vst3");
                directories.Add(Path.Combine(home, ".vst3"));

                // Also check for architecture-specific paths
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    directories.Add("/usr/lib/x86_64-linux-gnu/vst3");
                    directories.Add("/usr/lib64/vst3");
                }
                else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    directories.Add("/usr/lib/aarch64-linux-gnu/vst3");
                }
            }

            return directories.ToArray();
        }

        /// <summary>
        /// Finds all VST3 plugins in the default directories for the current platform.
        /// </summary>
        /// <param name="includeSubdirectories">Whether to search subdirectories recursively</param>
        /// <returns>List of full paths to found VST3 plugin bundles</returns>
        public static List<string> FindVst3Plugins(bool includeSubdirectories = true)
        {
            return FindVst3Plugins(GetDefaultVst3Directories(), includeSubdirectories);
        }

        /// <summary>
        /// Finds all VST3 plugins in the specified directories.
        /// </summary>
        /// <param name="searchDirectories">Directories to search for plugins</param>
        /// <param name="includeSubdirectories">Whether to search subdirectories recursively</param>
        /// <returns>List of full paths to found VST3 plugin bundles</returns>
        public static List<string> FindVst3Plugins(string[] searchDirectories, bool includeSubdirectories = true)
        {
            var plugins = new List<string>();
            var searchOption = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            foreach (string directory in searchDirectories)
            {
                if (!Directory.Exists(directory))
                    continue;

                try
                {
                    // Search for .vst3 bundles (directories or files)
                    var vst3Items = Directory.GetFileSystemEntries(directory, "*.vst3", searchOption);

                    foreach (string item in vst3Items)
                    {
                        // VST3 plugins can be either directories (bundles) or single files
                        if (Directory.Exists(item) || File.Exists(item))
                        {
                            plugins.Add(item);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we don't have access to
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    // Directory was removed during search
                    continue;
                }
            }

            return plugins.Distinct().OrderBy(p => Path.GetFileName(p)).ToList();
        }

        /// <summary>
        /// Gets a formatted string showing the default VST3 directories and which ones exist.
        /// Useful for debugging and user feedback.
        /// </summary>
        /// <returns>Formatted string with directory information</returns>
        public static string GetVst3DirectoriesInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Platform: {GetRuntimeIdentifier()}");
            sb.AppendLine("VST3 Plugin Directories:");

            foreach (string dir in GetDefaultVst3Directories())
            {
                bool exists = Directory.Exists(dir);
                sb.AppendLine($"  [{(exists ? "OK" : "  ")}] {dir}");
            }

            return sb.ToString();
        }

        #endregion

        /// <summary>
        /// No-op on current versions. Kept for source compatibility.
        /// macOS plugin destruction now uses dispatch_async_f as a drain signal only;
        /// _destroyFunc is called on the background thread so the main thread stays free
        /// for any dispatch_sync(main_queue,...) calls the plugin makes during teardown.
        /// </summary>
        public static SynchronizationContext? MainThreadSyncContext { get; set; }

    /// <summary>
    /// Handle of the loaded native library. Used by ThreadedVst3Wrapper to resolve
    /// additional exports (e.g. VST3Plugin_SafeDispatchMessage) without loading the
    /// DLL a second time.
    /// </summary>
    public IntPtr LibraryHandle => _libraryHandle;

    /// <summary>
    /// Opaque native plugin instance handle (the value returned by <c>VST3Plugin_Create</c>).
    /// Exposed so a native audio host — for example the Rust effect chain in OwnAudioSharp —
    /// can forward audio blocks straight to <c>VST3Plugin_ProcessAudio</c> on the audio thread
    /// without routing them back through managed code. The handle stays valid until this wrapper
    /// is disposed; callers must not use it afterwards.
    /// </summary>
    public IntPtr PluginHandle => _pluginHandle;

        #region macOS GCD drain-signal helpers

        private static readonly IntPtr s_macOsMainQueue = GetMacOsMainQueue();

        private static IntPtr GetMacOsMainQueue()
        {
            if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
            try
            {
                var lib = NativeLibrary.Load("libdispatch.dylib");
                return NativeLibrary.GetExport(lib, "_dispatch_main_q");
            }
            catch { return IntPtr.Zero; }
        }

        [DllImport("libdispatch.dylib")]
        private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);

        // Returns 1 when called from the main thread.
        [DllImport("libSystem.B.dylib")]
        private static extern int pthread_main_np();

        // A plain function pointer, not a marshalled delegate: iOS runs AOT-only and cannot
        // build the native-to-managed thunk a delegate needs at run time.
        private static readonly IntPtr s_drainSignalPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&DrainSignalCallback;

        private sealed class DrainContext
        {
            public readonly SemaphoreSlim Done = new SemaphoreSlim(0, 1);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void DrainSignalCallback(IntPtr ctxPtr)
        {
            var gcHandle = GCHandle.FromIntPtr(ctxPtr);
            var ctx = (DrainContext)gcHandle.Target!;
            gcHandle.Free();
            try { ctx.Done.Release(); } catch { }
        }

        #endregion

        #region IDisposable implementation

        ~OwnVst3Wrapper()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _freeScratch();

                if (_pluginHandle != IntPtr.Zero)
                {
                    if (OperatingSystem.IsMacOS() && pthread_main_np() == 0 && s_macOsMainQueue != IntPtr.Zero)
                    {
                        var ctx = new DrainContext();
                        var gcHandle = GCHandle.Alloc(ctx);
                        dispatch_async_f(s_macOsMainQueue,
                                         GCHandle.ToIntPtr(gcHandle),
                                         s_drainSignalPtr);
                        ctx.Done.Wait(5000);

                        _destroyFunc(_pluginHandle);
                        _pluginHandle = IntPtr.Zero;
                    }
                    else
                    {
                        _destroyFunc(_pluginHandle);
                        _pluginHandle = IntPtr.Zero;
                    }
                }

                if (_libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                }

                _disposed = true;
            }
        }
        #endregion
#nullable restore warnings
    }
}
