using System.Text.Json;
using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// One plugin as the reference (JUCE-era) build saw it. Written in record mode, compared otherwise.
    /// </summary>
    public sealed class GoldenRecord
    {
        public string Bundle { get; set; } = "";
        public int SubIndex { get; set; }
        public string Os { get; set; } = "";
        public string Arch { get; set; } = "";
        public string RecordedWith { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Version { get; set; } = "";
        public string Format { get; set; } = "";
        public bool IsInstrument { get; set; }
        public bool IsEffect { get; set; }
        public bool IsMidiOnly { get; set; }
        public int Inputs { get; set; }
        public int Outputs { get; set; }
        public int Latency { get; set; }
        public bool HasEditor { get; set; }
        public List<GoldenParam> Params { get; set; } = new List<GoldenParam>();
        public string Render { get; set; } = "";
        public string State { get; set; } = "";
        public List<double> ValuesAfterTweak { get; set; } = new List<double>();

        /// <summary>What the reference build read back after loading State into a fresh instance.</summary>
        public List<double> ValuesAfterRestore { get; set; } = new List<double>();
    }

    public sealed class GoldenParam
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public double Default { get; set; }
    }

    /// <summary>
    /// Phase 0 of the JUCE removal plan: everything the C# side can observe about a plugin
    /// must match what the JUCE build reported. Render tolerance is -90 dBFS.
    /// </summary>
    [TestClass]
    public sealed class GoldenTests
    {
        private const float RenderTolerance = 3.2e-5f;

        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions { WriteIndented = true };

        public static IEnumerable<object[]> Plugins => new List<object[]>
        {
            new object[] { "adelay", 0 },
            new object[] { "again-sample-accurate", 0 },
            new object[] { "again-sample-accurate", 1 },
            new object[] { "legacy-midicc-out", 0 },
            new object[] { "sync-delay", 0 },
            new object[] { "mda-vst3", 0 },
            new object[] { "mda-vst3", 6 },
            new object[] { "mda-vst3", 10 },
            new object[] { "mda-vst3", 12 },
            new object[] { "mda-vst3", 14 },
            new object[] { "mda-vst3", 16 },
            new object[] { "mda-vst3", 20 },
            new object[] { "mda-vst3", 30 },
            new object[] { "AudioUnit:Effects/aufx,dely,appl", 0 },
            new object[] { "AudioUnit:Effects/aufx,mrev,appl", 0 },
            new object[] { "AudioUnit:Effects/aufx,lmtr,appl", 0 },
            new object[] { "AudioUnit:Synths/aumu,dls ,appl", 0 },
        };

        [TestMethod]
        [DynamicData(nameof(Plugins))]
        public void MatchesReferenceBuild(string bundle, int subIndex)
        {
            string _safe = string.Concat(bundle.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
            string _file = Path.Combine(TestEnv.GoldenDir, $"{_safe}_{subIndex}.json");

            if (TestEnv.Recording)
            {
                Directory.CreateDirectory(TestEnv.GoldenDir);
                File.WriteAllText(_file, JsonSerializer.Serialize(_capture(bundle, subIndex, null), _json));
                return;
            }

            if (!File.Exists(_file)) Assert.Inconclusive("No golden file: " + _file);
            var _gold = JsonSerializer.Deserialize<GoldenRecord>(File.ReadAllText(_file))!;
            var _now = _capture(bundle, subIndex, _gold);

            Assert.AreEqual(_gold.Name, _now.Name, "name");
            Assert.AreEqual(_gold.Vendor, _now.Vendor, "vendor");
            Assert.AreEqual(_gold.Version, _now.Version, "version");
            Assert.AreEqual(_gold.Format, _now.Format, "format");
            Assert.AreEqual(_gold.IsInstrument, _now.IsInstrument, "isInstrument");
            Assert.AreEqual(_gold.IsEffect, _now.IsEffect, "isEffect");
            Assert.AreEqual(_gold.IsMidiOnly, _now.IsMidiOnly, "isMidiOnly");
            Assert.AreEqual(_gold.Inputs, _now.Inputs, "inputs");
            Assert.AreEqual(_gold.Outputs, _now.Outputs, "outputs");
            Assert.AreEqual(_gold.Latency, _now.Latency, "latency");
            Assert.AreEqual(_gold.HasEditor, _now.HasEditor, "hasEditor");

            Assert.AreEqual(_gold.Params.Count, _now.Params.Count, "parameter count");
            for (int i = 0; i < _gold.Params.Count; i++)
            {
                Assert.AreEqual(_gold.Params[i].Name, _now.Params[i].Name, $"param {i} name");
                Assert.AreEqual(_gold.Params[i].Default, _now.Params[i].Default, 1e-6, $"param {i} default");
            }

            if (_gold.Os == TestEnv.OsName && _gold.Arch == TestEnv.Arch)
            {
                float _diff = Signals.MaxAbsDiff(Signals.FromBase64(_gold.Render), Signals.FromBase64(_now.Render));
                Assert.IsTrue(_diff <= RenderTolerance, $"render differs by {_diff} (> -90 dBFS)");
            }

            //We restore the state the reference build saved and expect what it read back itself
            if (_gold.State.Length > 0)
            {
                using (var p = TestEnv.Load(bundle, subIndex, Signals.SampleRate, Signals.BlockSize))
                {
                    Assert.IsTrue(p.SetState(Convert.FromBase64String(_gold.State)), "SetState(reference blob)");
                    for (int i = 0; i < _gold.ValuesAfterRestore.Count; i++)
                        Assert.AreEqual(_gold.ValuesAfterRestore[i], p.GetParameter(i), 1e-4, $"param {i} after restore");
                }
            }
        }

        /// <summary>Index 0 is the bypass switch more often than not, so we leave it alone.</summary>
        public static IEnumerable<int> TweakedParams(int count)
        {
            if (count == 1) { yield return 0; yield break; }
            for (int i = 1; i < Math.Min(3, count); i++) yield return i;
        }

        private static GoldenRecord _capture(string bundle, int subIndex, GoldenRecord? gold)
        {
            var r = new GoldenRecord
            {
                Bundle = bundle,
                SubIndex = subIndex,
                Os = TestEnv.OsName,
                Arch = TestEnv.Arch,
                RecordedWith = Environment.GetEnvironmentVariable("OWNVST3_RECORD_LABEL") ?? "default"
            };

            using (var p = TestEnv.Load(bundle, subIndex, Signals.SampleRate, Signals.BlockSize))
            {
                r.Name = p.Name;
                r.Vendor = p.Vendor;
                r.Version = p.Version ?? "";
                r.Format = p.Format?.ToString() ?? "";
                r.IsInstrument = p.IsInstrument;
                r.IsEffect = p.IsEffect;
                r.IsMidiOnly = p.IsMidiOnly;
                r.Inputs = p.ActualInputChannels;
                r.Outputs = p.ActualOutputChannels;
                r.Latency = p.LatencySamples;
                r.HasEditor = p.HasEditor;

                foreach (var _param in p.GetAllParameters())
                    r.Params.Add(new GoldenParam { Index = _param.Id, Name = _param.Name, Default = _param.DefaultValue });

                r.Render = Signals.ToBase64(Signals.Render(p, gold?.IsInstrument ?? r.IsInstrument));
            }

            if (gold == null)
            {
                using (var p = TestEnv.Load(bundle, subIndex, Signals.SampleRate, Signals.BlockSize))
                {
                    foreach (int i in TweakedParams(p.GetParameterCount()))
                        p.SetParameter(i, i % 2 == 1 ? 0.3 : 0.7);

                    Signals.Render(p, false);

                    for (int i = 0; i < p.GetParameterCount(); i++) r.ValuesAfterTweak.Add(p.GetParameter(i));
                    byte[]? _state = p.GetState();
                    r.State = _state == null ? "" : Convert.ToBase64String(_state);
                }

                if (r.State.Length > 0)
                {
                    using (var p = TestEnv.Load(bundle, subIndex, Signals.SampleRate, Signals.BlockSize))
                    {
                        p.SetState(Convert.FromBase64String(r.State));
                        for (int i = 0; i < p.GetParameterCount(); i++) r.ValuesAfterRestore.Add(p.GetParameter(i));
                    }
                }
            }

            return r;
        }
    }
}
