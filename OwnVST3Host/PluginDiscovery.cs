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

        // Raw Cdecl function pointers; every native bool is a one-byte C99 _Bool.
        private static readonly object _initLock = new object();
        private static bool _loaded;
        private static IntPtr _libraryHandle;

        private static unsafe delegate* unmanaged[Cdecl]<int, int, byte> _scanStartFunc;
        private static unsafe delegate* unmanaged[Cdecl]<byte> _scanIsRunningFunc;
        private static unsafe delegate* unmanaged[Cdecl]<float> _scanProgressFunc;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr> _scanCurrentItemFunc;
        private static unsafe delegate* unmanaged[Cdecl]<void> _scanCancelFunc;
        private static unsafe delegate* unmanaged[Cdecl]<int> _getScannedCountFunc;
        private static unsafe delegate* unmanaged[Cdecl]<int, PluginDescriptorC*, byte> _getScannedAtFunc;
        private static unsafe delegate* unmanaged[Cdecl]<byte*, PluginDescriptorC*, byte> _resolveFunc;
        private static unsafe delegate* unmanaged[Cdecl]<IntPtr> _getScanCacheXmlFunc;
        private static unsafe delegate* unmanaged[Cdecl]<byte*, byte> _restoreScanCacheXmlFunc;

        private static unsafe void _ensureLoaded()
        {
            if (_loaded) return;

            lock (_initLock)
            {
                if (_loaded) return;

                _libraryHandle = NativeLibrary.Load(OwnVst3Wrapper.GetNativeLibraryPath());

                _scanStartFunc = (delegate* unmanaged[Cdecl]<int, int, byte>)_bind("OwnPlugin_ScanStartMode");
                _scanIsRunningFunc = (delegate* unmanaged[Cdecl]<byte>)_bind("OwnPlugin_ScanIsRunning");
                _scanProgressFunc = (delegate* unmanaged[Cdecl]<float>)_bind("OwnPlugin_ScanProgress");
                _scanCurrentItemFunc = (delegate* unmanaged[Cdecl]<IntPtr>)_bind("OwnPlugin_ScanCurrentItem");
                _scanCancelFunc = (delegate* unmanaged[Cdecl]<void>)_bind("OwnPlugin_ScanCancel");
                _getScannedCountFunc = (delegate* unmanaged[Cdecl]<int>)_bind("OwnPlugin_GetScannedCount");
                _getScannedAtFunc = (delegate* unmanaged[Cdecl]<int, PluginDescriptorC*, byte>)_bind("OwnPlugin_GetScannedAt");
                _resolveFunc = (delegate* unmanaged[Cdecl]<byte*, PluginDescriptorC*, byte>)_bind("OwnPlugin_ResolveDescriptor");
                _getScanCacheXmlFunc = (delegate* unmanaged[Cdecl]<IntPtr>)_bind("OwnPlugin_GetScanCacheXml");
                _restoreScanCacheXmlFunc = (delegate* unmanaged[Cdecl]<byte*, byte>)_bind("OwnPlugin_RestoreScanCacheXml");

                _loaded = true;
            }
        }

        private static unsafe void* _bind(string name)
        {
            if (NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr ptr))
                return (void*)ptr;
            return null;
        }

        private static string _utf8(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

        // ScanAsync awaits, and an async method cannot be unsafe, so every pointer call it
        // needs goes through one of these.
        private static unsafe bool _supported => _scanStartFunc != null;
        private static unsafe bool _start(int formats, int mode) => _scanStartFunc(formats, mode) != 0;
        private static unsafe bool _running() => _scanIsRunningFunc != null && _scanIsRunningFunc() != 0;
        private static unsafe float _progress() => _scanProgressFunc == null ? 1f : _scanProgressFunc();

        #endregion

        #region Public API

        /// <summary>False against a native library older than 1.7.0.</summary>
        public static bool IsSupported
        {
            get
            {
                _ensureLoaded();
                return _supported;
            }
        }

        public static bool AudioUnitSupported => OperatingSystem.IsMacOS();

        public static bool IsScanning
        {
            get
            {
                _ensureLoaded();
                return _running();
            }
        }

        public static unsafe void Cancel()
        {
            _ensureLoaded();
            if (_scanCancelFunc != null) _scanCancelFunc();
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

            if (!_supported)
                throw new NotSupportedException("This native library predates 1.7.0 and has no plugin scanner.");

            if (!_start((int)formats, (int)mode))
                throw new InvalidOperationException("A plugin scan is already running.");

            using (cancellationToken.Register(Cancel))
            {
                while (_running())
                {
                    progress?.Report(new ScanProgress(_progress(), CurrentItem, _count()));
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
        public static unsafe Task<PluginDescriptor?> ResolveAsync(PluginDescriptor plugin,
                                                           CancellationToken cancellationToken = default)
        {
            _ensureLoaded();

            if (_resolveFunc == null)
                throw new NotSupportedException("This native library predates 1.7.0.");

            string identifier = plugin.Identifier;

            return Task.Run(() =>
            {
                PluginDescriptorC raw = new PluginDescriptorC();
                byte* id = (byte*)Marshal.StringToCoTaskMemUTF8(identifier);

                try { return _resolveFunc(id, &raw) != 0 ? _toDescriptor(ref raw) : null; }
                finally { Marshal.FreeCoTaskMem((IntPtr)id); }
            }, cancellationToken);
        }

        public static unsafe string CurrentItem
        {
            get
            {
                _ensureLoaded();
                return _scanCurrentItemFunc == null ? "" : _utf8(_scanCurrentItemFunc());
            }
        }

        /// <summary>Result of the last scan or cache restore. Safe to call mid-scan.</summary>
        public static unsafe IReadOnlyList<PluginDescriptor> GetResults()
        {
            _ensureLoaded();
            if (_getScannedAtFunc == null) return Array.Empty<PluginDescriptor>();

            int count = _count();
            var found = new List<PluginDescriptor>(count);

            for (int i = 0; i < count; i++)
            {
                PluginDescriptorC raw = new PluginDescriptorC();
                if (_getScannedAtFunc(i, &raw) != 0) { found.Add(_toDescriptor(ref raw)); }
            }

            return found;
        }

        /// <summary>Persist this and feed it back to <see cref="RestoreCache"/> to skip a rescan.</summary>
        public static unsafe string GetCacheXml()
        {
            _ensureLoaded();
            return _getScanCacheXmlFunc == null ? "" : _utf8(_getScanCacheXmlFunc());
        }

        /// <returns>False on malformed XML, or if a scan is in flight.</returns>
        public static unsafe bool RestoreCache(string xml)
        {
            _ensureLoaded();
            if (_restoreScanCacheXmlFunc == null || string.IsNullOrWhiteSpace(xml))
                return false;

            byte* raw = (byte*)Marshal.StringToCoTaskMemUTF8(xml);
            try { return _restoreScanCacheXmlFunc(raw) != 0; }
            finally { Marshal.FreeCoTaskMem((IntPtr)raw); }
        }

        #endregion

        private static unsafe int _count() => _getScannedCountFunc == null ? 0 : _getScannedCountFunc();

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
