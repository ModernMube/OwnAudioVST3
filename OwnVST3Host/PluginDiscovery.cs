using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// Plugin formats the native library can host. AudioUnit is macOS only.
    /// </summary>
    [Flags]
    public enum PluginFormat
    {
        Vst3      = 0x01,
        AudioUnit = 0x02,
        All       = 0x7fffffff
    }

    /// <summary>
    /// Fast reads only what is free — the AudioComponent registry for AU, bundle
    /// names for VST3 — and leaves channel counts unknown. Full loads every plugin.
    /// </summary>
    public enum ScanMode
    {
        Fast = 0,
        Full = 1
    }

    /// <summary>
    /// One plugin found by <see cref="PluginScanner"/>.
    /// </summary>
    public sealed class PluginDescriptor
    {
        public string Name { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string Version { get; init; } = "";
        public string Category { get; init; } = "";

        /// <summary>A bundle path for VST3, an "AudioUnit:..." token for AU. Hand it to LoadPlugin as is.</summary>
        public string Identifier { get; init; } = "";

        public PluginFormat Format { get; init; }

        /// <summary>Empty for AUs that only exist in the component registry.</summary>
        public string FilePath { get; init; } = "";

        public bool IsInstrument { get; init; }

        /// <summary>-1 until a full scan or <see cref="PluginScanner.ResolveAsync"/> fills it in.</summary>
        public int InputChannels { get; init; } = -1;
        public int OutputChannels { get; init; } = -1;

        public int UniqueId { get; init; }

        public bool IsResolved => InputChannels >= 0;

        public override string ToString() => $"{Name} — {Vendor} ({Format})";
    }

    public readonly struct ScanProgress
    {
        public float Fraction { get; }
        public string CurrentItem { get; }
        public int FoundSoFar { get; }

        public ScanProgress(float fraction, string currentItem, int foundSoFar)
        {
            Fraction = fraction;
            CurrentItem = currentItem;
            FoundSoFar = foundSoFar;
        }
    }

    /// <summary>
    /// Format-neutral plugin discovery. AudioUnits have no directory to walk, so
    /// OwnVst3Wrapper.FindVst3Plugins() cannot see them — that method is unchanged,
    /// this is the way in when you want AUs too.
    /// </summary>
    public static class PluginScanner
    {
        #region Native interop

        [StructLayout(LayoutKind.Sequential)]
        private struct PluginDescriptorC
        {
            public IntPtr name;
            public IntPtr vendor;
            public IntPtr version;
            public IntPtr category;
            public IntPtr identifier;
            public IntPtr formatName;
            public IntPtr fileOrPath;
            public int isInstrument;
            public int numInputs;
            public int numOutputs;
            public int uniqueId;
            public int reserved;
        }

        // I1 because the native side returns C99 _Bool, not a 4-byte Win32 BOOL.
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_ScanStartModeDelegate(int formatMask, int mode);

        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_ScanIsRunningDelegate();

        private delegate float OwnPlugin_ScanProgressDelegate();
        private delegate IntPtr OwnPlugin_ScanCurrentItemDelegate();
        private delegate void OwnPlugin_ScanCancelDelegate();
        private delegate int OwnPlugin_GetScannedCountDelegate();

        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_GetScannedAtDelegate(int index, ref PluginDescriptorC descriptor);

        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_ResolveDescriptorDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, ref PluginDescriptorC descriptor);

        private delegate IntPtr OwnPlugin_GetScanCacheXmlDelegate();

        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_RestoreScanCacheXmlDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string xml);

        private static readonly object _initLock = new object();
        private static bool _loaded;
        private static IntPtr _libraryHandle;

        private static OwnPlugin_ScanStartModeDelegate? _scanStartFunc;
        private static OwnPlugin_ScanIsRunningDelegate? _scanIsRunningFunc;
        private static OwnPlugin_ScanProgressDelegate? _scanProgressFunc;
        private static OwnPlugin_ScanCurrentItemDelegate? _scanCurrentItemFunc;
        private static OwnPlugin_ScanCancelDelegate? _scanCancelFunc;
        private static OwnPlugin_GetScannedCountDelegate? _getScannedCountFunc;
        private static OwnPlugin_GetScannedAtDelegate? _getScannedAtFunc;
        private static OwnPlugin_ResolveDescriptorDelegate? _resolveFunc;
        private static OwnPlugin_GetScanCacheXmlDelegate? _getScanCacheXmlFunc;
        private static OwnPlugin_RestoreScanCacheXmlDelegate? _restoreScanCacheXmlFunc;

        private static void _ensureLoaded()
        {
            if (_loaded) return;

            lock (_initLock)
            {
                if (_loaded) return;

                _libraryHandle = NativeLibrary.Load(OwnVst3Wrapper.GetNativeLibraryPath());

                _scanStartFunc = _bind<OwnPlugin_ScanStartModeDelegate>("OwnPlugin_ScanStartMode");
                _scanIsRunningFunc = _bind<OwnPlugin_ScanIsRunningDelegate>("OwnPlugin_ScanIsRunning");
                _scanProgressFunc = _bind<OwnPlugin_ScanProgressDelegate>("OwnPlugin_ScanProgress");
                _scanCurrentItemFunc = _bind<OwnPlugin_ScanCurrentItemDelegate>("OwnPlugin_ScanCurrentItem");
                _scanCancelFunc = _bind<OwnPlugin_ScanCancelDelegate>("OwnPlugin_ScanCancel");
                _getScannedCountFunc = _bind<OwnPlugin_GetScannedCountDelegate>("OwnPlugin_GetScannedCount");
                _getScannedAtFunc = _bind<OwnPlugin_GetScannedAtDelegate>("OwnPlugin_GetScannedAt");
                _resolveFunc = _bind<OwnPlugin_ResolveDescriptorDelegate>("OwnPlugin_ResolveDescriptor");
                _getScanCacheXmlFunc = _bind<OwnPlugin_GetScanCacheXmlDelegate>("OwnPlugin_GetScanCacheXml");
                _restoreScanCacheXmlFunc = _bind<OwnPlugin_RestoreScanCacheXmlDelegate>("OwnPlugin_RestoreScanCacheXml");

                _loaded = true;
            }
        }

        private static T? _bind<T>(string name) where T : Delegate
        {
            if (NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr ptr) && ptr != IntPtr.Zero)
                return Marshal.GetDelegateForFunctionPointer<T>(ptr);
            return null;
        }

        private static string _utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

        #endregion

        #region Public API

        /// <summary>False against a native library older than 1.7.0.</summary>
        public static bool IsSupported
        {
            get
            {
                _ensureLoaded();
                return _scanStartFunc != null;
            }
        }

        public static bool AudioUnitSupported => OperatingSystem.IsMacOS();

        public static bool IsScanning
        {
            get
            {
                _ensureLoaded();
                return _scanIsRunningFunc != null && _scanIsRunningFunc();
            }
        }

        public static void Cancel()
        {
            _ensureLoaded();
            _scanCancelFunc?.Invoke();
        }

        /// <summary>
        /// Lists installed plugins. Fast mode is near-instant and leaves channel counts
        /// at -1; Full loads every plugin to fill them in and takes minutes.
        /// </summary>
        public static async Task<IReadOnlyList<PluginDescriptor>> ScanAsync(
            PluginFormat formats = PluginFormat.All,
            ScanMode mode = ScanMode.Fast,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _ensureLoaded();

            if (_scanStartFunc == null)
                throw new NotSupportedException("This native library predates 1.7.0 and has no plugin scanner.");

            if (!_scanStartFunc((int)formats, (int)mode))
                throw new InvalidOperationException("A plugin scan is already running.");

            using (cancellationToken.Register(Cancel))
            {
                while (_scanIsRunningFunc!())
                {
                    progress?.Report(new ScanProgress(_scanProgressFunc!(), CurrentItem, _count()));
                    await Task.Delay(120, CancellationToken.None).ConfigureAwait(false);
                }
            }

            progress?.Report(new ScanProgress(1f, "", _count()));
            cancellationToken.ThrowIfCancellationRequested();

            return GetResults();
        }

        /// <summary>
        /// Loads one plugin to fill in what a fast scan left out, and updates the cached
        /// entry. Takes as long as loading that plugin; returns null if it cannot be loaded.
        /// </summary>
        public static Task<PluginDescriptor?> ResolveAsync(PluginDescriptor plugin,
                                                           CancellationToken cancellationToken = default)
        {
            _ensureLoaded();

            if (_resolveFunc == null)
                throw new NotSupportedException("This native library predates 1.7.0.");

            return Task.Run(() =>
            {
                var raw = new PluginDescriptorC();
                return _resolveFunc(plugin.Identifier, ref raw) ? _toDescriptor(ref raw) : null;
            }, cancellationToken);
        }

        public static string CurrentItem
        {
            get
            {
                _ensureLoaded();
                return _scanCurrentItemFunc == null ? "" : _utf8(_scanCurrentItemFunc());
            }
        }

        /// <summary>Result of the last scan or cache restore. Safe to call mid-scan.</summary>
        public static IReadOnlyList<PluginDescriptor> GetResults()
        {
            _ensureLoaded();
            if (_getScannedAtFunc == null) return Array.Empty<PluginDescriptor>();

            int count = _count();
            var found = new List<PluginDescriptor>(count);

            for (int i = 0; i < count; i++)
            {
                var raw = new PluginDescriptorC();
                if (_getScannedAtFunc(i, ref raw)) { found.Add(_toDescriptor(ref raw)); }
            }

            return found;
        }

        /// <summary>Persist this and feed it back to <see cref="RestoreCache"/> to skip a rescan.</summary>
        public static string GetCacheXml()
        {
            _ensureLoaded();
            return _getScanCacheXmlFunc == null ? "" : _utf8(_getScanCacheXmlFunc());
        }

        /// <returns>False on malformed XML, or if a scan is in flight.</returns>
        public static bool RestoreCache(string xml)
        {
            _ensureLoaded();
            if (_restoreScanCacheXmlFunc == null || string.IsNullOrWhiteSpace(xml))
                return false;

            return _restoreScanCacheXmlFunc(xml);
        }

        #endregion

        private static int _count() => _getScannedCountFunc == null ? 0 : _getScannedCountFunc();

        private static PluginDescriptor _toDescriptor(ref PluginDescriptorC raw)
        {
            string format = _utf8(raw.formatName);

            return new PluginDescriptor
            {
                Name = _utf8(raw.name),
                Vendor = _utf8(raw.vendor),
                Version = _utf8(raw.version),
                Category = _utf8(raw.category),
                Identifier = _utf8(raw.identifier),
                FilePath = _utf8(raw.fileOrPath),
                Format = format == "AudioUnit" ? PluginFormat.AudioUnit : PluginFormat.Vst3,
                IsInstrument = raw.isInstrument != 0,
                InputChannels = raw.numInputs,
                OutputChannels = raw.numOutputs,
                UniqueId = raw.uniqueId
            };
        }
    }
}
