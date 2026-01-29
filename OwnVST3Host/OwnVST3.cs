using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public partial class OwnVst3Wrapper : IDisposable
    {
#nullable disable
        #region Private fields

        private IntPtr _pluginHandle;
        private IntPtr _libraryHandle;
        private bool _disposed = false;

        // Library name constants
        private const string WindowsLibraryName = "ownvst3.dll";
        private const string LinuxLibraryName = "libownvst3.so";
        private const string MacOSLibraryName = "libownvst3.dylib";

        // Function delegate instances
        private VST3Plugin_CreateDelegate _createFunc;
        private VST3Plugin_DestroyDelegate _destroyFunc;
        private VST3Plugin_LoadPluginDelegate _loadPluginFunc;
        private VST3Plugin_CreateEditorDelegate _createEditorFunc;
        private VST3Plugin_CloseEditorDelegate _closeEditorFunc;
        private VST3Plugin_ResizeEditorDelegate _resizeEditorFunc;
        private VST3Plugin_GetEditorSizeDelegate _getEditorSizeFunc;
        private VST3Plugin_GetParameterCountDelegate _getParameterCountFunc;
        private VST3Plugin_GetParameterAtDelegate _getParameterAtFunc;
        private VST3Plugin_SetParameterDelegate _setParameterFunc;
        private VST3Plugin_GetParameterDelegate _getParameterFunc;
        private VST3Plugin_InitializeDelegate _initializeFunc;
        private VST3Plugin_ProcessAudioDelegate _processAudioFunc;
        private VST3Plugin_ProcessMidiDelegate _processMidiFunc;
        private VST3Plugin_IsInstrumentDelegate _isInstrumentFunc;
        private VST3Plugin_IsEffectDelegate _isEffectFunc;
        private VST3Plugin_GetNameDelegate _getNameFunc;
        private VST3Plugin_GetVendorDelegate _getVendorFunc;
        //private VST3Plugin_GetVersionDelegate _getVersionFunc;
        private VST3Plugin_GetPluginInfoDelegate _getPluginInfoFunc;
        private VST3Plugin_ClearStringCacheDelegate _clearStringCacheFunc;

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
            // Load the DLL using NativeLibrary.Load
            _libraryHandle = NativeLibrary.Load(dllPath);
            if (_libraryHandle == IntPtr.Zero)
            {
                throw new DllNotFoundException($"Failed to load library: {dllPath}");
            }

            // Initialize delegates from the loaded DLL
            InitializeDelegates();

            // Create the plugin instance
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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
        /// Gets the full path to the native library for the current platform.
        /// Searches in the following locations:
        /// 1. runtimes/{rid}/native/ relative to the assembly location
        /// 2. runtimes/{rid}/native/ relative to the current directory
        /// 3. Current directory
        /// </summary>
        /// <returns>Full path to the native library</returns>
        public static string GetNativeLibraryPath()
        {
            string rid = GetRuntimeIdentifier();
            string libraryName = GetNativeLibraryName();

            // Get assembly location
            string assemblyLocation = Path.GetDirectoryName(typeof(OwnVst3Wrapper).Assembly.Location) ?? "";

            // Search paths in order of priority
            string[] searchPaths = new[]
            {
                // Relative to assembly location
                Path.Combine(assemblyLocation, "runtimes", rid, "native", libraryName),
                // Relative to current directory
                Path.Combine(Directory.GetCurrentDirectory(), "runtimes", rid, "native", libraryName),
                // Direct in assembly location
                Path.Combine(assemblyLocation, libraryName),
                // Direct in current directory
                Path.Combine(Directory.GetCurrentDirectory(), libraryName)
            };

            foreach (string path in searchPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // If not found, return the expected path for a better error message
            string expectedPath = Path.Combine(assemblyLocation, "runtimes", rid, "native", libraryName);
            throw new DllNotFoundException(
                $"Native library not found. Expected location: {expectedPath}\n" +
                $"Runtime identifier: {rid}\n" +
                $"Searched paths:\n - {string.Join("\n - ", searchPaths)}");
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
                if (_pluginHandle != IntPtr.Zero)
                {
                    _pluginHandle = IntPtr.Zero;
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
#nullable restore
    }
}
