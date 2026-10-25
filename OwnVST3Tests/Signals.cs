using OwnVST3Host;

namespace OwnVST3Tests
{
    /// <summary>
    /// Deterministic stimulus and block rendering shared by the golden and behaviour tests.
    /// </summary>
    public static class Signals
    {
        public const int Blocks = 4;
        public const int BlockSize = 256;
        public const double SampleRate = 44100;

        /// <summary>
        /// 1 kHz sine + white noise for effects, a C4 note for instruments. Returns
        /// planar output, channel after channel, Blocks*BlockSize frames each.
        /// </summary>
        public static float[] Render(OwnVst3Wrapper plugin, bool instrument, int channels = 2)
        {
            var _out = new float[channels * Blocks * BlockSize];
            var _in = new float[channels][];
            var _blockOut = new float[channels][];
            for (int c = 0; c < channels; c++) { _in[c] = new float[BlockSize]; _blockOut[c] = new float[BlockSize]; }

            uint _seed = 0x1234567;
            long _pos = 0;

            for (int b = 0; b < Blocks; b++)
            {
                for (int i = 0; i < BlockSize; i++, _pos++)
                {
                    float _s = 0;
                    if (!instrument)
                    {
                        _seed = _seed * 1664525u + 1013904223u;
                        float _noise = ((_seed >> 8) / 16777216f) * 2f - 1f;
                        _s = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * _pos / SampleRate)) + 0.1f * _noise;
                    }
                    for (int c = 0; c < channels; c++) _in[c][i] = _s;
                }

                if (instrument && b == 0) plugin.SendMidiEvent(0x90, 60, 100);
                if (instrument && b == 2) plugin.SendMidiEvent(0x80, 60, 0);

                plugin.ProcessAudio(_in, _blockOut, channels, BlockSize);

                for (int c = 0; c < channels; c++)
                    Array.Copy(_blockOut[c], 0, _out, c * Blocks * BlockSize + b * BlockSize, BlockSize);
            }

            return _out;
        }

        public static float[] Sine(int frames, double freq = 1000, double amp = 0.5)
        {
            var s = new float[frames];
            for (int i = 0; i < frames; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / SampleRate));
            return s;
        }

        public static double Rms(ReadOnlySpan<float> x)
        {
            double _sum = 0;
            foreach (float v in x) _sum += v * v;
            return x.Length == 0 ? 0 : Math.Sqrt(_sum / x.Length);
        }

        public static float MaxAbsDiff(float[] a, float[] b)
        {
            Assert.AreEqual(a.Length, b.Length, "render length");
            float _max = 0;
            for (int i = 0; i < a.Length; i++) _max = Math.Max(_max, Math.Abs(a[i] - b[i]));
            return _max;
        }

        public static string ToBase64(float[] x)
        {
            var _bytes = new byte[x.Length * 4];
            Buffer.BlockCopy(x, 0, _bytes, 0, _bytes.Length);
            return Convert.ToBase64String(_bytes);
        }

        public static float[] FromBase64(string s)
        {
            byte[] _bytes = Convert.FromBase64String(s);
            var _x = new float[_bytes.Length / 4];
            Buffer.BlockCopy(_bytes, 0, _x, 0, _bytes.Length);
            return _x;
        }
    }
}
