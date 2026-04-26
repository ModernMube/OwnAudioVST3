using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public partial class OwnVst3Wrapper
    {
        #region Native Structures

        [StructLayout(LayoutKind.Sequential)]
        public struct VST3ParameterC
        {
            public int id;
            public IntPtr name;
            public double minValue;
            public double maxValue;
            public double defaultValue;
            public double currentValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioBufferC
        {
            public IntPtr inputs;
            public IntPtr outputs;
            public int numChannels;
            public int numSamples;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MidiEventC
        {
            public byte status;
            public byte data1;
            public byte data2;
            public int sampleOffset;
        }

        #endregion
    }

    /// <summary>
    /// Represents the size of the plugin editor
    /// </summary>
    public struct EditorSize
    {
        public int Width { get; set; }
        public int Height { get; set; }

        public EditorSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }
}
