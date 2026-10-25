using System.Runtime.InteropServices;
using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// The C ABI itself: every export present, struct layouts as the header documents them,
    /// and sane answers from a handle with nothing loaded (or no handle at all).
    /// </summary>
    [TestClass]
    public sealed unsafe class ExportTests
    {
        private static readonly string[] _allExports =
        {
            "VST3Plugin_Create", "VST3Plugin_Destroy", "VST3Plugin_LoadPlugin", "VST3Plugin_Initialize",
            "VST3Plugin_GetName", "VST3Plugin_GetVendor", "VST3Plugin_GetVersion", "VST3Plugin_GetPluginInfo",
            "VST3Plugin_IsInstrument", "VST3Plugin_IsEffect", "VST3Plugin_IsMidiOnly",
            "VST3Plugin_GetActualInputChannels", "VST3Plugin_GetActualOutputChannels", "VST3Plugin_GetLatencySamples",
            "VST3Plugin_GetParameterCount", "VST3Plugin_GetParameterAt", "VST3Plugin_SetParameter", "VST3Plugin_GetParameter",
            "VST3Plugin_ProcessAudio", "VST3Plugin_ProcessMidi", "VST3Plugin_SetTempo", "VST3Plugin_SetTransportState",
            "VST3Plugin_ResetTransportPosition", "VST3Plugin_SetBypass", "VST3Plugin_HasEditor", "VST3Plugin_CreateEditor",
            "VST3Plugin_CloseEditor", "VST3Plugin_ResizeEditor", "VST3Plugin_GetEditorSize", "VST3Plugin_IsEditorOpen",
            "VST3Plugin_ProcessIdle", "VST3Plugin_GetState", "VST3Plugin_SetState", "VST3Plugin_FreeStateData",
            "VST3Plugin_ClearStringCache", "VST3Plugin_SafeDispatchMessage",
            "OwnPlugin_ScanStart", "OwnPlugin_ScanStartMode", "OwnPlugin_ResolveDescriptor", "OwnPlugin_ScanIsRunning",
            "OwnPlugin_ScanProgress", "OwnPlugin_ScanCurrentItem", "OwnPlugin_ScanCancel", "OwnPlugin_GetScannedCount",
            "OwnPlugin_GetScannedAt", "OwnPlugin_GetScanCacheXml", "OwnPlugin_RestoreScanCacheXml",
            "OwnPlugin_LoadPluginAt", "OwnPlugin_GetFormatName", "OwnPlugin_GetIdentifier"
        };

        private static IntPtr _lib;

        [ClassInitialize]
        public static void Load(TestContext _) =>
            _lib = NativeLibrary.Load(TestEnv.NativeLib ?? OwnVst3Wrapper.GetNativeLibraryPath());

        [ClassCleanup]
        public static void Unload() => NativeLibrary.Free(_lib);

        private static void* _fn(string name) => (void*)NativeLibrary.GetExport(_lib, name);

        private static string _str(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "<null>";

        [TestMethod]
        public void AllFiftyExportsResolve()
        {
            Assert.HasCount(50, _allExports);
            foreach (string name in _allExports)
                Assert.IsTrue(NativeLibrary.TryGetExport(_lib, name, out _), "missing export " + name);
        }

        [TestMethod]
        public void StructLayoutsMatchTheHeader()
        {
            Assert.AreEqual(IntPtr.Size == 8 ? 48 : 40, Marshal.SizeOf<OwnVst3Wrapper.VST3ParameterC>());
            Assert.AreEqual(IntPtr.Size == 8 ? 8 : 4, (int)Marshal.OffsetOf<OwnVst3Wrapper.VST3ParameterC>("name"));
            Assert.AreEqual(8, Marshal.SizeOf<OwnVst3Wrapper.MidiEventC>());
            Assert.AreEqual(4, (int)Marshal.OffsetOf<OwnVst3Wrapper.MidiEventC>("sampleOffset"));
            Assert.AreEqual(IntPtr.Size * 2 + 8, Marshal.SizeOf<OwnVst3Wrapper.AudioBufferC>());
        }

        [TestMethod]
        public void NullHandleIsHarmless()
        {
            var name = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_fn("VST3Plugin_GetName");
            var count = (delegate* unmanaged[Cdecl]<IntPtr, int>)_fn("VST3Plugin_GetParameterCount");
            var load = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)_fn("VST3Plugin_LoadPlugin");
            var init = (delegate* unmanaged[Cdecl]<IntPtr, double, int, byte>)_fn("VST3Plugin_Initialize");
            var process = (delegate* unmanaged[Cdecl]<IntPtr, OwnVst3Wrapper.AudioBufferC*, byte>)_fn("VST3Plugin_ProcessAudio");
            var bypass = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)_fn("VST3Plugin_SetBypass");
            var destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)_fn("VST3Plugin_Destroy");
            var free = (delegate* unmanaged[Cdecl]<IntPtr, void>)_fn("VST3Plugin_FreeStateData");
            var fmt = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_fn("OwnPlugin_GetFormatName");
            var idle = (delegate* unmanaged[Cdecl]<IntPtr, void>)_fn("VST3Plugin_ProcessIdle");
            var dispatch = (delegate* unmanaged[Cdecl]<IntPtr, void>)_fn("VST3Plugin_SafeDispatchMessage");

            Assert.AreEqual("", _str(name(IntPtr.Zero)));
            Assert.AreEqual("", _str(fmt(IntPtr.Zero)));
            Assert.AreEqual(0, count(IntPtr.Zero));
            Assert.AreEqual(0, load(IntPtr.Zero, null));
            Assert.AreEqual(0, init(IntPtr.Zero, 44100, 512));
            Assert.AreEqual(0, process(IntPtr.Zero, null));
            bypass(IntPtr.Zero, 1);
            idle(IntPtr.Zero);
            dispatch(IntPtr.Zero);
            free(IntPtr.Zero);
            destroy(IntPtr.Zero);
        }

        [TestMethod]
        public void EmptyInstanceAnswersNoPlugin()
        {
            using (var w = TestEnv.NewWrapper())
            {
                Assert.AreEqual("", w.Name);
                Assert.AreEqual("", w.Vendor);
                Assert.AreEqual("", w.Version);
                Assert.AreEqual("", w.PluginInfo);
                Assert.IsNull(w.Format);
                Assert.AreEqual("", w.Identifier);
                Assert.IsFalse(w.IsInstrument);
                Assert.IsFalse(w.IsEffect);
                Assert.IsFalse(w.IsMidiOnly);
                Assert.AreEqual(0, w.ActualInputChannels);
                Assert.AreEqual(0, w.ActualOutputChannels);
                Assert.AreEqual(0, w.LatencySamples);
                Assert.AreEqual(0, w.GetParameterCount());
                Assert.AreEqual(0.0, w.GetParameter(0));
                Assert.IsFalse(w.Initialize(44100, 512));
                Assert.IsFalse(w.HasEditor);
                Assert.IsFalse(w.IsEditorOpen);
                Assert.IsFalse(w.CreateEditor(IntPtr.Zero));
                Assert.IsNull(w.GetEditorSize());
                Assert.IsNull(w.GetState());
                Assert.IsFalse(w.SetState(new byte[] { 1, 2, 3 }));
                Assert.IsFalse(w.SendMidiEvent(0x90, 60, 100));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => w.GetParameterAt(0));

                w.SetTempo(140);
                w.SetTransportState(true);
                w.ResetTransportPosition();
                w.SetBypass(true);
                w.CloseEditor();
                w.ResizeEditor(100, 100);
                w.ProcessIdle();
                w.ClearStringCache();

                var _in = new[] { Signals.Sine(64), Signals.Sine(64) };
                var _out = new[] { new float[64], new float[64] };
                Assert.IsFalse(w.ProcessAudio(_in, _out, 2, 64));
            }
        }

        [TestMethod]
        public void LoadRejectsWhatIsNotAPlugin()
        {
            using (var w = TestEnv.NewWrapper())
            {
                Assert.IsFalse(w.LoadPlugin("/definitely/not/here.vst3"));
                Assert.IsFalse(w.LoadPlugin(""));
                Assert.IsFalse(w.LoadPlugin(TestEnv.RepoRoot));
                Assert.IsFalse(w.LoadPlugin("AudioUnit:Effects/xxxx,yyyy,zzzz"));
                Assert.IsFalse(w.LoadPlugin("AudioUnit:garbage"));
                Assert.AreEqual("", w.Name);
            }
        }

        [TestMethod]
        public void ProcessAudioRefusesOversizedBlockWithoutTouchingBuffers()
        {
            using (var w = TestEnv.Load("again-sample-accurate", 0, 44100, 128))
            {
                var process = (delegate* unmanaged[Cdecl]<IntPtr, OwnVst3Wrapper.AudioBufferC*, byte>)_fn("VST3Plugin_ProcessAudio");

                const int n = 256;
                float* inL = stackalloc float[n], inR = stackalloc float[n], outL = stackalloc float[n], outR = stackalloc float[n];
                for (int i = 0; i < n; i++) { inL[i] = inR[i] = 0.5f; outL[i] = outR[i] = 7f; }

                float** ins = stackalloc float*[2];
                float** outs = stackalloc float*[2];
                ins[0] = inL; ins[1] = inR; outs[0] = outL; outs[1] = outR;

                var buf = new OwnVst3Wrapper.AudioBufferC { inputs = (IntPtr)ins, outputs = (IntPtr)outs, numChannels = 2, numSamples = n };
                Assert.AreEqual(0, process(w.PluginHandle, &buf), "a block wider than Initialize() promised is refused");
                for (int i = 0; i < n; i++) Assert.AreEqual(7f, outL[i]);

                buf.numSamples = 128;
                Assert.AreEqual(1, process(w.PluginHandle, &buf));

                buf.numSamples = 0;
                Assert.AreEqual(0, process(w.PluginHandle, &buf));
            }
        }

        [TestMethod]
        public void StateBufferIsFreedThroughTheExport()
        {
            using (var w = TestEnv.Load("adelay"))
            {
                var get = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int*, byte>)_fn("VST3Plugin_GetState");
                var free = (delegate* unmanaged[Cdecl]<IntPtr, void>)_fn("VST3Plugin_FreeStateData");

                IntPtr data;
                int len;
                Assert.AreEqual(1, get(w.PluginHandle, &data, &len));
                Assert.IsTrue(len > 9, "a VC2! container is never this short");
                Assert.AreNotEqual(IntPtr.Zero, data);
                free(data);

                Assert.AreEqual(0, get(w.PluginHandle, null, &len));
            }
        }
    }
}
