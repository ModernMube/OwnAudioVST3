using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// Format-aware extras: which format an instance ended up hosting, and loading
    /// by scanner identifier or bundle sub-index.
    /// </summary>
    public unsafe partial class OwnVst3Wrapper
    {
        private delegate* unmanaged[Cdecl]<IntPtr, byte*, int, byte> _loadPluginAtFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getFormatNameFunc;
        private delegate* unmanaged[Cdecl]<IntPtr, IntPtr> _getIdentifierFunc;

        private void _initFormatDelegates()
        {
            _loadPluginAtFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, byte>)_tryExport("OwnPlugin_LoadPluginAt");
            _getFormatNameFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_tryExport("OwnPlugin_GetFormatName");
            _getIdentifierFunc = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_tryExport("OwnPlugin_GetIdentifier");
        }

        public bool LoadPlugin(PluginDescriptor plugin) => LoadPlugin(plugin.Identifier);

        /// <summary>Picks one plugin out of a bundle holding several; subIndex 0 is plain LoadPlugin.</summary>
        public bool LoadPluginAt(string pluginPath, int subIndex)
        {
            CheckDisposed();

            if (_loadPluginAtFunc == null)
                throw new NotSupportedException("This native library predates 1.7.0 and cannot address plugins inside a bundle.");

            byte* path = _toUtf8(pluginPath);
            try { return _loadPluginAtFunc(_pluginHandle, path, subIndex) != 0; }
            finally { Marshal.FreeCoTaskMem((IntPtr)path); }
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
