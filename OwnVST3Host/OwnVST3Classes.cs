using System;

namespace OwnVST3Host
{
#nullable disable
    #region Helper classes

    /// <summary>
    /// C# representation of a VST3 parameter
    /// </summary>
    public class VST3Parameter
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public double DefaultValue { get; set; }
        public double CurrentValue { get; set; }
    }

    /// <summary>
    /// C# representation of a MIDI event
    /// </summary>
    public class MidiEvent
    {
        public byte Status { get; set; }
        public byte Data1 { get; set; }
        public byte Data2 { get; set; }
        public int SampleOffset { get; set; }
    }

    #endregion
#nullable restore
}
