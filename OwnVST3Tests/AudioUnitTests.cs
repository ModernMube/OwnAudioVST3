using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// AudioUnit backend on macOS with Apple's own units, which every Mac has.
    /// </summary>
    [TestClass]
    public sealed class AudioUnitTests
    {
        private const string Delay   = "AudioUnit:Effects/aufx,dely,appl";
        private const string Limiter = "AudioUnit:Effects/aufx,lmtr,appl";
        private const string Dls     = "AudioUnit:Synths/aumu,dls ,appl";
        private const int Block = 256;

        [TestInitialize]
        public void MacOnly()
        {
            if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("AudioUnits are macOS only");
        }

        [TestMethod]
        public void IdentifierRoundTrips()
        {
            using (var p = TestEnv.Load(Delay))
            {
                Assert.AreEqual(PluginFormat.AudioUnit, p.Format);
                Assert.AreEqual(Delay, p.Identifier);
                Assert.AreEqual("AUDelay", p.Name);
                Assert.AreEqual("Apple", p.Vendor);
                Assert.IsTrue(p.IsEffect);
            }
        }

        [TestMethod]
        public void ComponentBundlePathLoads()
        {
            const string _bundle = "/System/Library/Components/CoreAudio.component";
            if (!Directory.Exists(_bundle)) Assert.Inconclusive(_bundle + " not present");

            using (var w = TestEnv.NewWrapper())
            {
                Assert.IsTrue(w.LoadPlugin(_bundle));
                Assert.AreEqual(PluginFormat.AudioUnit, w.Format);
                StringAssert.StartsWith(w.Identifier, "AudioUnit:");
            }
        }

        [TestMethod]
        public void DryMixPassesInputThrough()
        {
            using (var p = TestEnv.Load(Delay, 0, 44100, Block))
            {
                p.SetParameter(p.GetAllParameters().Single(x => x.Name == "Dry/Wet Mix").Id, 0.0);

                var _in = new[] { Signals.Sine(Block), Signals.Sine(Block) };
                var _out = new[] { new float[Block], new float[Block] };
                for (int b = 0; b < 3; b++) Assert.IsTrue(p.ProcessAudio(_in, _out, 2, Block));

                Assert.IsTrue(Signals.MaxAbsDiff(_in[0], _out[0]) < 1e-4f, "0 % wet is the dry signal");
            }
        }

        [TestMethod]
        public void BypassIsDelayedByTheReportedLatency()
        {
            using (var p = TestEnv.Load(Limiter, 0, 44100, Block))
            {
                int _lat = p.LatencySamples;
                Assert.AreEqual(88, _lat);

                p.SetBypass(true);
                var _in = new[] { new float[Block], new float[Block] };
                var _out = new[] { new float[Block], new float[Block] };
                for (int i = 0; i < Block; i++) _in[0][i] = _in[1][i] = (i + 1) / 1000f;

                Assert.IsTrue(p.ProcessAudio(_in, _out, 2, Block));
                for (int i = 0; i < Block; i++)
                    Assert.AreEqual(i < _lat ? 0f : _in[0][i - _lat], _out[0][i], "sample " + i);
            }
        }

        [TestMethod]
        public void StateIsABinaryPlistAndRoundTrips()
        {
            byte[] _state;
            double _fb;
            using (var a = TestEnv.Load(Delay, 0, 44100, Block))
            {
                int _i = a.GetAllParameters().Single(x => x.Name == "Feedback").Id;
                a.SetParameter(_i, 0.2);
                a.ProcessAudio(new[] { new float[Block], new float[Block] }, new[] { new float[Block], new float[Block] }, 2, Block);
                _fb = a.GetParameter(_i);
                _state = a.GetState()!;
            }

            CollectionAssert.AreEqual("bplist00"u8.ToArray(), _state.Take(8).ToArray());

            using (var b = TestEnv.Load(Delay, 0, 44100, Block))
            {
                int _i = b.GetAllParameters().Single(x => x.Name == "Feedback").Id;
                Assert.IsTrue(b.SetState(_state));
                Assert.AreEqual(_fb, b.GetParameter(_i), 1e-4);

                Assert.IsFalse(b.SetState(new byte[] { 0x56, 0x43, 0x32, 0x21, 1, 0, 0, 0, 0 }), "a VST3 blob is not an AU state");
            }
        }

        [TestMethod]
        public void InstrumentReportsAllOutputBuses()
        {
            using (var p = TestEnv.Load(Dls, 0, 44100, Block))
            {
                Assert.IsTrue(p.IsInstrument);
                Assert.AreEqual(0, p.ActualInputChannels);
                Assert.AreEqual(4, p.ActualOutputChannels);
                Assert.IsTrue(p.SendMidiEvent(0x90, 60, 100));
            }
        }

        [TestMethod]
        public async Task ResolveFillsChannels()
        {
            var d = await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = Delay });
            Assert.IsNotNull(d);
            Assert.AreEqual(PluginFormat.AudioUnit, d.Format);
            Assert.AreEqual("AUDelay", d.Name);
            Assert.AreEqual(2, d.InputChannels);
            Assert.AreEqual(2, d.OutputChannels);
            Assert.AreEqual("", d.FilePath);
        }
    }
}
