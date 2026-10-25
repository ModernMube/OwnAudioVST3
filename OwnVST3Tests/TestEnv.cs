using System.Runtime.InteropServices;
using OwnVST3Host;

[assembly: DoNotParallelize]

namespace OwnVST3Tests
{
    /// <summary>
    /// Where the tests find the native library, the SDK sample plugins and the golden files.
    /// OWNVST3_NATIVE_LIB points at a freshly built library, OWNVST3_TEST_PLUGINS at a folder of .vst3 bundles.
    /// </summary>
    [TestClass]
    public static class TestEnv
    {
        public static string RepoRoot { get; } = _findRepoRoot();

        public static string? NativeLib => _env("OWNVST3_NATIVE_LIB");

        /// <summary>Set OWNVST3_RECORD_GOLDEN=1 to (re)write the golden files instead of comparing.</summary>
        public static bool Recording => _env("OWNVST3_RECORD_GOLDEN") == "1";

        public static string GoldenDir => Path.Combine(RepoRoot, "OwnVST3Tests", "Golden");

        public static string PluginsDir
        {
            get
            {
                string? _dir = _env("OWNVST3_TEST_PLUGINS");
                if (_dir == null)
                    _dir = Path.Combine(RepoRoot, "OwnVST3Native", "build-plugins", "VST3", "Release");
                return _dir;
            }
        }

        [AssemblyInitialize]
        public static void Init(TestContext _)
        {
            //We put the library under test where GetNativeLibraryPath() looks first, so PluginScanner picks it up too
            if (NativeLib == null) return;

            string _target = Path.Combine(AppContext.BaseDirectory, "runtimes",
                OwnVst3Wrapper.GetRuntimeIdentifier(), "native", OwnVst3Wrapper.GetNativeLibraryName());
            Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
            File.Copy(NativeLib, _target, true);
        }

        public static OwnVst3Wrapper NewWrapper() => new OwnVst3Wrapper(NativeLib ?? OwnVst3Wrapper.GetNativeLibraryPath());

        /// <summary>Full path of a sample plugin, or Inconclusive when the plugin set was not built.</summary>
        public static string Plugin(string name)
        {
            string _path = Path.Combine(PluginsDir, name + ".vst3");
            if (!Directory.Exists(_path) && !File.Exists(_path))
                Assert.Inconclusive($"Test plugin not found: {_path} (build the SDK samples or set OWNVST3_TEST_PLUGINS)");
            return _path;
        }

        /// <summary>Name is a sample bundle name, or an "AudioUnit:..." identifier (macOS only).</summary>
        public static OwnVst3Wrapper Load(string name, int subIndex = 0, double sampleRate = 44100, int block = 512)
        {
            bool _au = name.StartsWith("AudioUnit:");
            if (_au && !OperatingSystem.IsMacOS()) Assert.Inconclusive("AudioUnits are macOS only");
            string _path = _au ? name : Plugin(name);
            var _w = NewWrapper();

            bool _ok = subIndex == 0 ? _w.LoadPlugin(_path) : _w.LoadPluginAt(_path, subIndex);
            if (!_ok) { _w.Dispose(); Assert.Fail($"LoadPlugin failed: {_path} #{subIndex}"); }
            if (!_w.Initialize(sampleRate, block)) { _w.Dispose(); Assert.Fail($"Initialize failed: {_path}"); }

            return _w;
        }

        public static string OsName =>
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

        public static string Arch => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        private static string? _env(string name)
        {
            string? v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static string _findRepoRoot()
        {
            var _dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (_dir != null && !File.Exists(Path.Combine(_dir.FullName, "OwnVST3Sharp.sln")))
                _dir = _dir.Parent;
            return _dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
