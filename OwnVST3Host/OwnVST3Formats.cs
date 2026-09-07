using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// Format-aware extras: which format an instance ended up hosting, and loading
    /// by scanner identifier or bundle sub-index.
    /// </summary>
    public partial class OwnVst3Wrapper
    {
        // I1 because the native side returns C99 _Bool, not a 4-byte Win32 BOOL.
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OwnPlugin_LoadPluginAtDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string pluginPath, int subIndex);

        private delegate IntPtr OwnPlugin_GetFormatNameDelegate(IntPtr handle);
        private delegate IntPtr OwnPlugin_GetIdentifierDelegate(IntPtr handle);

        private OwnPlugin_LoadPluginAtDelegate? _loadPluginAtFunc;
        private OwnPlugin_GetFormatNameDelegate? _getFormatNameFunc;
        private OwnPlugin_GetIdentifierDelegate? _getIdentifierFunc;

        private void _initFormatDelegates()
        {
            _loadPluginAtFunc = TryGetDelegate<OwnPlugin_LoadPluginAtDelegate>("OwnPlugin_LoadPluginAt");
            _getFormatNameFunc = TryGetDelegate<OwnPlugin_GetFormatNameDelegate>("OwnPlugin_GetFormatName");
            _getIdentifierFunc = TryGetDelegate<OwnPlugin_GetIdentifierDelegate>("OwnPlugin_GetIdentifier");
        }

        public bool LoadPlugin(PluginDescriptor plugin) => LoadPlugin(plugin.Identifier);

        /// <summary>Picks one plugin out of a bundle holding several; subIndex 0 is plain LoadPlugin.</summary>
        public bool LoadPluginAt(string pluginPath, int subIndex)
        {
            CheckDisposed();

            if (_loadPluginAtFunc == null)
                throw new NotSupportedException("This native library predates 1.7.0 and cannot address plugins inside a bundle.");

            return _loadPluginAtFunc(_pluginHandle, pluginPath, subIndex);
        }

        /// <summary>Null on an older native library.</summary>
        public PluginFormat? Format
        {
            get
            {
                CheckDisposed();
                if (_getFormatNameFunc == null) return null;

                string name = Marshal.PtrToStringUTF8(_getFormatNameFunc(_pluginHandle)) ?? "";
                if (name.Length == 0) return null;

                return name == "AudioUnit" ? PluginFormat.AudioUnit : PluginFormat.Vst3;
            }
        }

        /// <summary>What the instance was loaded from — round-trips back through LoadPlugin.</summary>
        public string? Identifier
        {
            get
            {
                CheckDisposed();
                if (_getIdentifierFunc == null) return null;
                return Marshal.PtrToStringUTF8(_getIdentifierFunc(_pluginHandle));
            }
        }
    }
}
