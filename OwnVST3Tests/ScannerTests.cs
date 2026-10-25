using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// PluginScanner: fast / full scan, resolve, and the plugin-cache.xml format (KnownPluginList compatible).
    /// </summary>
    [TestClass]
    public sealed class ScannerTests
    {
        private static string _fixture(string name) => Path.Combine(TestEnv.RepoRoot, "OwnVST3Tests", "Fixtures", name);

        [TestMethod]
        public void IsSupported() => Assert.IsTrue(PluginScanner.IsSupported);

        [TestMethod]
        public async Task ResolveFillsChannelCounts()
        {
            string _path = TestEnv.Plugin("adelay");

            var d = await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = _path });

            Assert.IsNotNull(d);
            Assert.AreEqual("ADelay", d.Name);
            Assert.AreEqual(PluginFormat.Vst3, d.Format);
            Assert.AreEqual(_path, d.Identifier);
            Assert.AreEqual(_path, d.FilePath);
            Assert.IsFalse(d.IsInstrument);
            Assert.IsTrue(d.IsResolved);
            Assert.AreEqual(2, d.InputChannels);
            Assert.AreEqual(2, d.OutputChannels);
            Assert.AreEqual("Steinberg Media Technologies", d.Vendor);

            Assert.IsTrue(PluginScanner.GetResults().Any(x => x.Identifier == _path));
        }

        [TestMethod]
        public async Task ResolveInstrument()
        {
            string _path = TestEnv.Plugin("mda-vst3");
            var d = await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = _path });

            Assert.IsNotNull(d);
            Assert.AreEqual(_path, d.Identifier);
        }

        [TestMethod]
        public async Task ResolveUnknownReturnsNull()
        {
            var d = await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = "/does/not/exist.vst3" });
            Assert.IsNull(d);
        }

        [TestMethod]
        public async Task CacheXmlRoundTrips()
        {
            await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = TestEnv.Plugin("adelay") });
            await PluginScanner.ResolveAsync(new PluginDescriptor { Identifier = TestEnv.Plugin("sync-delay") });

            var _before = PluginScanner.GetResults();
            string _xml = PluginScanner.GetCacheXml();

            if (TestEnv.Recording)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_fixture("x"))!);
                File.WriteAllText(_fixture("reference-plugin-cache.xml"), _xml);
            }

            StringAssert.Contains(_xml, "<KNOWNPLUGINS");
            StringAssert.Contains(_xml, "format=\"VST3\"");

            Assert.IsTrue(PluginScanner.RestoreCache(_xml));
            var _after = PluginScanner.GetResults();

            Assert.AreEqual(_before.Count, _after.Count);
            foreach (var b in _before)
            {
                var a = _after.Single(x => x.Identifier == b.Identifier);
                Assert.AreEqual(b.Name, a.Name);
                Assert.AreEqual(b.Vendor, a.Vendor);
                Assert.AreEqual(b.Version, a.Version);
                Assert.AreEqual(b.Category, a.Category);
                Assert.AreEqual(b.Format, a.Format);
                Assert.AreEqual(b.IsInstrument, a.IsInstrument);
                Assert.AreEqual(b.InputChannels, a.InputChannels);
                Assert.AreEqual(b.OutputChannels, a.OutputChannels);
                Assert.AreEqual(b.UniqueId, a.UniqueId);
            }
        }

        [TestMethod]
        public void RestoresReferenceCache()
        {
            //A plugin-cache.xml written by the JUCE build must read back without a rescan
            string _file = _fixture("reference-plugin-cache.xml");
            if (!File.Exists(_file)) Assert.Inconclusive("fixture missing");

            Assert.IsTrue(PluginScanner.RestoreCache(File.ReadAllText(_file)));
            var _list = PluginScanner.GetResults();

            var d = _list.Single(x => x.Name == "ADelay");
            Assert.AreEqual(PluginFormat.Vst3, d.Format);
            Assert.AreEqual("Steinberg Media Technologies", d.Vendor);
            Assert.AreEqual("3.8.1.0", d.Version);
            Assert.AreEqual("Fx|Delay", d.Category);
            Assert.AreEqual(0, d.InputChannels);
            Assert.AreEqual(unchecked((int)0xfed6ff1a), d.UniqueId);
            Assert.AreEqual("/Library/Audio/Plug-Ins/VST3/adelay.vst3", d.Identifier);
            Assert.AreEqual(d.Identifier, d.FilePath);
        }

        [TestMethod]
        public void RestoreToleratesUnknownAttributes()
        {
            string _xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<KNOWNPLUGINS>" +
                "<PLUGIN name=\"Foo\" format=\"VST3\" category=\"Fx\" manufacturer=\"Bar\" version=\"1.2.3\" " +
                "file=\"/x/Foo.vst3\" uniqueId=\"1234abcd\" isInstrument=\"0\" numInputs=\"2\" numOutputs=\"2\" " +
                "isShell=\"0\" hasARAExtension=\"0\" somethingNew=\"42\"/>" +
                "<PLUGIN name=\"Synth\" format=\"AudioUnit\" category=\"Synths\" manufacturer=\"Apple\" " +
                "file=\"AudioUnit:Synths/aumu,dls ,appl\" isInstrument=\"1\" numInputs=\"0\" numOutputs=\"2\"/>" +
                "</KNOWNPLUGINS>";

            Assert.IsTrue(PluginScanner.RestoreCache(_xml));
            var _list = PluginScanner.GetResults();
            Assert.AreEqual(2, _list.Count);

            var foo = _list.Single(x => x.Name == "Foo");
            Assert.AreEqual("Bar", foo.Vendor);
            Assert.AreEqual("1.2.3", foo.Version);
            Assert.AreEqual("Fx", foo.Category);
            Assert.AreEqual("/x/Foo.vst3", foo.Identifier);
            Assert.AreEqual("/x/Foo.vst3", foo.FilePath);
            Assert.AreEqual(unchecked((int)0x1234abcd), foo.UniqueId);

            var syn = _list.Single(x => x.Name == "Synth");
            Assert.AreEqual(PluginFormat.AudioUnit, syn.Format);
            Assert.IsTrue(syn.IsInstrument);
            Assert.AreEqual("", syn.FilePath);
            Assert.AreEqual(0, syn.InputChannels);
        }

        [TestMethod]
        public void RestoreRejectsGarbage()
        {
            Assert.IsFalse(PluginScanner.RestoreCache("<not xml"));
            Assert.IsFalse(PluginScanner.RestoreCache(""));
        }

        [TestMethod]
        public async Task FastScanCompletes()
        {
            var _progress = new List<ScanProgress>();
            var _list = await PluginScanner.ScanAsync(PluginFormat.All, ScanMode.Fast, new Progress<ScanProgress>(_progress.Add));

            Assert.IsFalse(PluginScanner.IsScanning);
            Assert.AreEqual("", PluginScanner.CurrentItem);
            foreach (var d in _list)
            {
                Assert.IsFalse(string.IsNullOrEmpty(d.Name), "every entry has a name");
                Assert.IsFalse(string.IsNullOrEmpty(d.Identifier));
                if (d.Format == PluginFormat.Vst3) Assert.AreEqual(d.Identifier, d.FilePath);
            }

            var _sorted = _list.Select(x => x.Name).ToList();
            CollectionAssert.AreEqual(_sorted.OrderBy(x => x, StringComparer.Ordinal).ToList(), _sorted, "results come sorted by name");

            if (OperatingSystem.IsMacOS())
            {
                var _dly = _list.FirstOrDefault(x => x.Identifier == "AudioUnit:Effects/aufx,dely,appl");
                Assert.IsNotNull(_dly, "AUDelay is listed in the fast scan");
                Assert.AreEqual("Apple", _dly.Vendor);
                Assert.AreEqual("", _dly.FilePath);
                Assert.AreEqual(-1, _dly.InputChannels);
            }
        }

        [TestMethod]
        public async Task SecondScanWhileRunningIsRejected()
        {
            // Only a full AU scan (it instantiates every Apple unit) is slow enough to be caught in the act;
            // a fast scan on an empty CI machine is over before the next line runs.
            if (!OperatingSystem.IsMacOS()) Assert.Inconclusive("needs a scan that takes a while");

            var _first = PluginScanner.ScanAsync(PluginFormat.AudioUnit, ScanMode.Full);
            if (!PluginScanner.IsScanning) Assert.Inconclusive("the scan was over immediately");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PluginScanner.ScanAsync(PluginFormat.Vst3, ScanMode.Fast));

            PluginScanner.Cancel();
            await _first;
            Assert.IsFalse(PluginScanner.IsScanning);
        }
    }
}
