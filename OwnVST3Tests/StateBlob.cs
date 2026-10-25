using System.Text;
using System.Xml.Linq;

namespace OwnVST3Tests
{
    /// <summary>
    /// Independent reader for the VST3 state container ("VC2!" + length + XML + 0, children in the
    /// 6-bit low-bit-first base64), so the tests check the format, not the native code against itself.
    /// </summary>
    public static class StateBlob
    {
        private const string Alphabet = ".ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+";

        public static bool IsVst3Container(byte[] blob) =>
            blob.Length >= 8 && blob[0] == (byte)'V' && blob[1] == (byte)'C' && blob[2] == (byte)'2' && blob[3] == (byte)'!';

        public static XElement Xml(byte[] blob)
        {
            Assert.IsTrue(IsVst3Container(blob), "VC2! header");
            int _len = BitConverter.ToInt32(blob, 4);
            Assert.AreEqual(blob.Length - 9, _len, "length field = XML bytes");
            Assert.AreEqual(0, blob[^1], "trailing zero");
            return XElement.Parse(Encoding.UTF8.GetString(blob, 8, _len));
        }

        /// <summary>Decoded IComponent / IEditController child, null when absent.</summary>
        public static byte[]? Child(byte[] blob, string name)
        {
            var e = Xml(blob).Element(name);
            return e == null ? null : Decode(e.Value);
        }

        public static byte[] Decode(string text)
        {
            int _dot = text.IndexOf('.');
            int _size = int.Parse(text.Substring(0, _dot));
            var _out = new byte[_size];
            int _bit = 0;

            foreach (char c in text.Substring(_dot + 1))
            {
                int v = Alphabet.IndexOf(c);
                Assert.IsTrue(v >= 0, "bad char " + c);
                for (int b = 0; b < 6; b++, _bit++)
                    if (_bit < _size * 8 && ((v >> b) & 1) != 0) _out[_bit >> 3] |= (byte)(1 << (_bit & 7));
            }
            return _out;
        }
    }
}
