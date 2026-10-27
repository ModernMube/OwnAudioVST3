using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// Behaviour of the public wrapper with the SDK sample plugins: loading, parameters, audio,
    /// bypass, MIDI mapping, transport, state formats.
    /// </summary>
    [TestClass]
    public sealed class Vst3PluginTests
    {
        private const int Block = 256;

        private static float[][] _stereo(float value)
        {
            var a = new float[Block];
            Array.Fill(a, value);
            return new[] { a, (float[])a.Clone() };
        }

        private static float[][] _process(OwnVst3Wrapper p, float[][] input, int blocks = 1)
        {
            var _out = new[] { new float[Block], new float[Block] };
            for (int b = 0; b < blocks; b++) Assert.IsTrue(p.ProcessAudio(input, _out, 2, Block), "ProcessAudio");
            return _out;
        }

        private static int _indexOf(OwnVst3Wrapper p, string name) =>
            p.GetAllParameters().Single(x => x.Name == name).Id;

        [TestMethod]
        public void LoadReportsFormatAndIdentifier()
        {
            string _path = TestEnv.Plugin("adelay");
            using (var p = TestEnv.Load("adelay"))
            {
                Assert.AreEqual(PluginFormat.Vst3, p.Format);
                Assert.AreEqual(_path, p.Identifier);
                Assert.AreEqual("ADelay | Steinberg Media Technologies | 3.8.1.0", p.PluginInfo);
                Assert.IsFalse(p.HasEditor);
                Assert.AreEqual(0, p.LatencySamples);
            }
        }

        [TestMethod]
        public void SubIndexPicksTheClassInsideTheBundle()
        {
            using (var p = TestEnv.Load("mda-vst3", 14))
            {
                Assert.AreEqual("mda JX10", p.Name);
                Assert.IsTrue(p.IsInstrument);
                Assert.AreEqual(0, p.ActualInputChannels);
                Assert.AreEqual(2, p.ActualOutputChannels);
            }

            // Past the end falls back to the first class, as the 1.7.0 build did.
            using (var p = TestEnv.Load("mda-vst3", 999))
                Assert.AreEqual("mda Ambience", p.Name);
        }

        [TestMethod]
        public void ReloadingReplacesThePlugin()
        {
            using (var p = TestEnv.Load("adelay"))
            {
                Assert.IsTrue(p.LoadPlugin(TestEnv.Plugin("sync-delay")));
                Assert.AreEqual("SyncDelay", p.Name);
                Assert.IsFalse(p.ProcessAudio(_stereo(0.1f), new[] { new float[Block], new float[Block] }, 2, Block), "not initialised yet");
                Assert.IsTrue(p.Initialize(44100, Block));
                _process(p, _stereo(0.1f));
            }
        }

        [TestMethod]
        public void Utf16NamesArriveAsUtf8()
        {
            using (var p = TestEnv.Load("utf16-name"))
                Assert.AreEqual("UTF16Name öüäéèê-やあ-مرحبًا", p.Name);
        }

        [TestMethod]
        public void ParametersAreIndexedAndNormalised()
        {
            using (var p = TestEnv.Load("again-sample-accurate"))
            {
                var _all = p.GetAllParameters();
                Assert.HasCount(2, _all);
                for (int i = 0; i < _all.Count; i++)
                {
                    Assert.AreEqual(i, _all[i].Id, "paramId is the index");
                    Assert.AreEqual(0.0, _all[i].MinValue);
                    Assert.AreEqual(1.0, _all[i].MaxValue);
                }

                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => p.GetParameterAt(2));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => p.GetParameterAt(-1));

                Assert.IsTrue(p.SetParameter(1, 0.25));
                Assert.AreEqual(0.25, p.GetParameter(1), 1e-9, "visible right away, before any block");
                Assert.AreEqual(0.25, p.GetParameterAt(1).CurrentValue, 1e-9);

                p.SetParameter(1, 7.0);
                Assert.AreEqual(1.0, p.GetParameter(1), "clamped into [0,1]");

                Assert.AreEqual(0.0, p.GetParameter(99));
            }
        }

        [TestMethod]
        public void GainParameterReachesTheProcessor()
        {
            using (var p = TestEnv.Load("again-sample-accurate", 0, 44100, Block))
            {
                p.SetParameter(_indexOf(p, "Gain"), 0.5);
                var _out = _process(p, _stereo(0.4f), 2);

                foreach (var ch in _out)
                    Assert.AreEqual(0.2, ch[Block - 1], 1e-4);
            }
        }

        [TestMethod]
        public void BypassUsesThePluginsBypassParameter()
        {
            using (var p = TestEnv.Load("again-sample-accurate", 0, 44100, Block))
            {
                int _bypass = _indexOf(p, "Bypass");
                p.SetParameter(_indexOf(p, "Gain"), 0.5);
                _process(p, _stereo(0.4f));

                p.SetBypass(true);
                var _out = _process(p, _stereo(0.4f), 2);
                Assert.AreEqual(0.4f, _out[0][Block - 1], 1e-5f, "dry while bypassed");
                Assert.AreEqual(1.0, p.GetParameter(_bypass), "the bypass parameter follows SetBypass");

                p.SetBypass(false);
                _out = _process(p, _stereo(0.4f), 2);
                Assert.AreEqual(0.2f, _out[0][Block - 1], 1e-4f);
                Assert.AreEqual(0.0, p.GetParameter(_bypass));
            }
        }

        [TestMethod]
        public void BypassWithoutParameterPassesInputThrough()
        {
            // "Advanced Tutorial" has a gain and nothing else.
            using (var p = TestEnv.Load("again-sample-accurate", 1, 44100, Block))
            {
                Assert.HasCount(1, p.GetAllParameters());
                p.SetParameter(0, 0.25);
                Assert.AreEqual(0.1f, _process(p, _stereo(0.4f), 2)[0][Block - 1], 1e-4f);

                p.SetBypass(true);
                var _in = new[] { Signals.Sine(Block), Signals.Sine(Block, 300) };
                var _out = new[] { new float[Block], new float[Block] };
                Assert.IsTrue(p.ProcessAudio(_in, _out, 2, Block));
                CollectionAssert.AreEqual(_in[0], _out[0]);
                CollectionAssert.AreEqual(_in[1], _out[1]);
            }
        }

        [TestMethod]
        public void InstrumentPlaysNotesAndStaysSilentOtherwise()
        {
            using (var p = TestEnv.Load("mda-vst3", 14, 44100, Block))
            {
                var _silence = _stereo(0f);
                var _out = _process(p, _silence, 4);
                Assert.AreEqual(0.0, Signals.Rms(_out[0]), 1e-6, "no note, no sound");

                Assert.IsTrue(p.SendMidiEvent(0x90, 60, 110));
                _out = _process(p, _silence, 4);
                Assert.IsTrue(Signals.Rms(_out[0]) > 1e-3, "note on is heard");

                Assert.IsTrue(p.ProcessMidi(new[] { new MidiEvent { Status = 0x80, Data1 = 60, Data2 = 0, SampleOffset = 10 } }));
                Assert.IsFalse(p.ProcessMidi(Array.Empty<MidiEvent>()));
            }
        }

        [TestMethod]
        public void MidiControllersGoThroughIMidiMapping()
        {
            using (var p = TestEnv.Load("mda-vst3", 14, 44100, Block))
            {
                int _mod = _indexOf(p, "Mod Wheel");
                int _bend = _indexOf(p, "Pitch Bend");
                Assert.AreEqual(0.0, p.GetParameter(_mod));

                p.SendMidiEvent(0xB0, 1, 127);
                p.SendMidiEvent(0xE0, 0x7F, 0x7F);
                _process(p, _stereo(0f));
                Assert.AreEqual(1.0, p.GetParameter(_mod), 1e-9, "CC1 → Mod Wheel");
                Assert.AreEqual(1.0, p.GetParameter(_bend), 1e-9, "pitch bend → Pitch Bend");

                p.SendMidiEvent(0xE0, 0x00, 0x40);
                _process(p, _stereo(0f));
                Assert.AreEqual(8192 / 16383.0, p.GetParameter(_bend), 1e-9);
            }
        }

        [TestMethod]
        public void TempoReachesTheProcessContext()
        {
            int _echoAt(double bpm)
            {
                using (var p = TestEnv.Load("sync-delay", 0, 44100, Block))
                {
                    p.SetParameter(_indexOf(p, "Delay"), 5 / 17.0);
                    p.SetTempo(bpm);
                    p.SetTransportState(true);

                    var _in = new[] { new float[Block], new float[Block] };
                    var _out = new[] { new float[Block], new float[Block] };
                    for (int b = 0; b < 16; b++)
                    {
                        _in[0][0] = _in[1][0] = b == 0 ? 1f : 0f;
                        Assert.IsTrue(p.ProcessAudio(_in, _out, 2, Block));
                        int i = Array.FindIndex(_out[0], x => x != 0f);
                        if (i >= 0) return b * Block + i;
                    }
                    return -1;
                }
            }

            // 1/32 note = an eighth of a beat.
            Assert.AreEqual(2756, _echoAt(120));
            Assert.AreEqual(1378, _echoAt(240));
        }

        [TestMethod]
        public void ReinitialiseGrowsTheBlock()
        {
            using (var p = TestEnv.Load("again-sample-accurate", 0, 44100, 128))
            {
                var _in = new[] { new float[1024], new float[1024] };
                var _out = new[] { new float[1024], new float[1024] };
                Assert.IsFalse(p.ProcessAudio(_in, _out, 2, 1024));

                Assert.IsTrue(p.Initialize(48000, 1024));
                Assert.IsTrue(p.ProcessAudio(_in, _out, 2, 1024));
                Assert.IsFalse(p.Initialize(48000, 0));
            }
        }

        [TestMethod]
        public void MonoHostBufferOnStereoPlugin()
        {
            using (var p = TestEnv.Load("again-sample-accurate", 0, 44100, Block))
            {
                p.SetParameter(_indexOf(p, "Gain"), 0.5);
                var _in = new[] { new float[Block] };
                var _out = new[] { new float[Block] };
                Array.Fill(_in[0], 0.4f);
                Assert.IsTrue(p.ProcessAudio(_in, _out, 1, Block));
                Assert.IsTrue(p.ProcessAudio(_in, _out, 1, Block));
                Assert.AreEqual(0.2f, _out[0][Block - 1], 1e-4f);
            }
        }

        [TestMethod]
        public void StateIsWrittenInTheVc2Container()
        {
            using (var p = TestEnv.Load("adelay"))
            {
                byte[] _blob = p.GetState()!;
                var _xml = StateBlob.Xml(_blob);

                Assert.AreEqual("VST3PluginState", _xml.Name.LocalName);
                Assert.IsNotNull(_xml.Element("IComponent"));
                Assert.IsTrue(StateBlob.Child(_blob, "IComponent")!.Length > 0);
            }
        }

        [TestMethod]
        public void StateRoundTripsBetweenInstances()
        {
            byte[] _blob;
            double _delay;

            using (var a = TestEnv.Load("adelay", 0, 44100, Block))
            {
                a.SetParameter(_indexOf(a, "Delay"), 0.37);
                _process(a, _stereo(0.1f));
                _delay = a.GetParameter(_indexOf(a, "Delay"));
                _blob = a.GetState()!;
            }

            using (var b = TestEnv.Load("adelay", 0, 44100, Block))
            {
                Assert.AreNotEqual(_delay, b.GetParameter(_indexOf(b, "Delay")), 1e-6);
                Assert.IsTrue(b.SetState(_blob));
                Assert.AreEqual(_delay, b.GetParameter(_indexOf(b, "Delay")), 1e-6);
                CollectionAssert.AreEqual(_blob, b.GetState());
            }
        }

        [TestMethod]
        public void LegacyRawStateIsStillAccepted()
        {
            // ≤ 1.5.21 stored the bare component stream; we cut it out of a current blob.
            byte[] _raw;
            double _delay;
            using (var a = TestEnv.Load("adelay", 0, 44100, Block))
            {
                a.SetParameter(_indexOf(a, "Delay"), 0.61);
                _process(a, _stereo(0.1f));
                _delay = a.GetParameter(_indexOf(a, "Delay"));
                _raw = StateBlob.Child(a.GetState()!, "IComponent")!;
            }

            Assert.IsFalse(StateBlob.IsVst3Container(_raw));
            using (var b = TestEnv.Load("adelay", 0, 44100, Block))
            {
                Assert.IsTrue(b.SetState(_raw));
                Assert.AreEqual(_delay, b.GetParameter(_indexOf(b, "Delay")), 1e-6);
            }
        }

        [TestMethod]
        public void DamagedContainerIsRejected()
        {
            using (var p = TestEnv.Load("adelay"))
            {
                byte[] _blob = p.GetState()!;
                var _broken = _blob.Take(20).ToArray();
                Assert.IsFalse(p.SetState(_broken));
                Assert.IsFalse(p.SetState(Array.Empty<byte>()));
            }
        }

        [TestMethod]
        public void ParameterWritesFromManyThreadsWhileProcessing()
        {
            using (var p = TestEnv.Load("again-sample-accurate", 0, 44100, Block))
            {
                int _gain = _indexOf(p, "Gain");
                using (var _stop = new CancellationTokenSource())
                {
                    var _writers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
                    {
                        var r = new Random(t);
                        while (!_stop.IsCancellationRequested) p.SetParameter(_gain, r.NextDouble());
                    })).ToArray();

                    var _in = _stereo(0.4f);
                    var _out = new[] { new float[Block], new float[Block] };
                    for (int b = 0; b < 400; b++) Assert.IsTrue(p.ProcessAudio(_in, _out, 2, Block));

                    _stop.Cancel();
                    Task.WaitAll(_writers);
                }

                p.SetParameter(_gain, 0.5);
                Assert.AreEqual(0.2f, _process(p, _stereo(0.4f), 3)[0][Block - 1], 1e-4f);
            }
        }

        [TestMethod]
        public void EditorCallsAreSafeWithoutAnEditor()
        {
            using (var p = TestEnv.Load("adelay"))
            {
                Assert.IsFalse(p.HasEditor);
                Assert.IsFalse(p.CreateEditor(IntPtr.Zero));
                Assert.IsFalse(p.IsEditorOpen);
                Assert.IsNull(p.GetEditorSize());
                p.ResizeEditor(300, 200);
                p.CloseEditor();
                p.ProcessIdle();

                //No editor means the view is never touched, so any handle is fine here
                Assert.IsFalse(p.AttachEditorToView(IntPtr.Zero));
                Assert.IsFalse(p.AttachEditorToView(new IntPtr(0x1000)));
                p.DetachEditor();
                Assert.IsFalse(p.IsEditorOpen);
            }
        }

        [TestMethod]
        public async Task ThreadedWrapperDrivesThePlugin()
        {
            using (var t = new ThreadedVst3Wrapper(TestEnv.NativeLib))
            {
                Assert.IsTrue(await t.LoadPluginAsync(TestEnv.Plugin("again-sample-accurate")));
                Assert.IsTrue(await t.InitializeAsync(44100, Block));
                Assert.AreEqual("AGain Sample Accurate", await t.GetNameAsync());
                Assert.AreEqual(PluginFormat.Vst3, await t.GetFormatAsync());

                int _gain = (await t.GetAllParametersAsync()).Single(x => x.Name == "Gain").Id;
                t.SetParameter(_gain, 0.5);

                var _in = _stereo(0.4f);
                var _out = new[] { new float[Block], new float[Block] };
                for (int b = 0; b < 3; b++) t.ProcessAudio(_in, _out, 2, Block);
                Assert.AreEqual(0.2f, _out[0][Block - 1], 1e-4f);

                byte[]? _state = await t.GetStateAsync();
                Assert.IsNotNull(_state);
                Assert.IsTrue(await t.SetStateAsync(_state));
            }
        }
    }
}
