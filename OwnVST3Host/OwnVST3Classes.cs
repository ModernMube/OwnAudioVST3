using System;

namespace OwnVST3Host
{
    /// <summary>
    /// Lifecycle state of a VST plugin managed by ThreadedVst3Wrapper.
    /// Transitions are always forward except Error, which is terminal within one wrapper instance.
    /// All reads are safe from any thread via Volatile.Read / Interlocked inside the wrapper.
    /// </summary>
    public enum VstPluginState
    {
        /// <summary>No plugin loaded. Initial state and state after Dispose.</summary>
        NotLoaded = 0,

        /// <summary>LoadPlugin succeeded. Plugin factory created but audio engine not yet started.</summary>
        Loaded,

        /// <summary>Initialize succeeded. Plugin is ready to process audio.</summary>
        Ready,

        /// <summary>ProcessAudio is currently executing on the audio thread.</summary>
        Processing,

        /// <summary>LoadPlugin or Initialize returned false. The wrapper instance must be replaced.</summary>
        Error
    }

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
