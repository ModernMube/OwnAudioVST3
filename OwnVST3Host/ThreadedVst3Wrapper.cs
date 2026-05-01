using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OwnVST3Host;

/// <summary>
/// Thread-safe async façade over OwnVst3Wrapper.
///
/// Threading model:
///   Plugin thread  – dedicated Thread that runs all native VST operations (load, init,
///                    parameter reads, etc.). UI code posts commands here via PostCommand.
///   Audio thread   – ProcessAudio() is called directly by the audio engine. Before each
///                    block it drains the lock-free SPSC queue and applies any pending
///                    state changes (SetParameter, SetTempo, …).
///   UI thread      – calls the async public methods; SetParameter/SetTempo/SetTransportState
///                    enqueue to the SPSC queue and return immediately.
///   Editor thread  – CreateEditor / CloseEditor MUST still be called on the caller (UI) thread
///                    as required by VST3 + macOS Cocoa. Use InnerWrapper for those via
///                    VstEditorController.
///
/// State machine (VstPluginState):
///   NotLoaded → Loaded    : LoadPluginAsync succeeds
///   NotLoaded → Error     : LoadPluginAsync fails
///   Loaded    → Ready     : InitializeAsync succeeds
///   Loaded    → Error     : InitializeAsync fails
///   Ready     ↔ Processing: ProcessAudio entry/exit (audio thread only)
///   Any       → NotLoaded : Dispose
/// </summary>
public sealed class ThreadedVst3Wrapper : IDisposable
{
    private readonly OwnVst3Wrapper _inner;
    private readonly Thread _pluginThread;
    private readonly BlockingCollection<Action> _cmdQueue;
    private readonly LockFreeQueue<VstStateChange> _stateQueue;

    // _disposed: written once (true) by Dispose, read from any thread.
    private volatile bool _disposed;

    // _state: only modified via Interlocked, read via Volatile.Read. Backing field for State.
    private int _state = (int)VstPluginState.NotLoaded;

    // Cached after InitializeAsync so the audio thread can read channel counts
    // without posting a command. Written on plugin thread, read on audio thread (volatile).
    private volatile int _actualIn = 2;
    private volatile int _actualOut = 2;

    // -------------------------------------------------------------------------
    // Public state API – safe to read from any thread, zero allocation.
    // -------------------------------------------------------------------------

    /// <summary>Current lifecycle state. Safe to read from any thread at any time.</summary>
    public VstPluginState State => (VstPluginState)Volatile.Read(ref _state);

    /// <summary>True when the plugin is in Ready or Processing state (can process audio).</summary>
    public bool IsReady
    {
        get
        {
            var s = State;
            return s == VstPluginState.Ready || s == VstPluginState.Processing;
        }
    }

    /// <summary>
    /// Exposes the inner low-level wrapper. Required by VstEditorController because
    /// CreateEditor and CloseEditor must run on the caller (UI) thread.
    /// Do NOT call any blocking operations on this from the UI thread; use the async
    /// methods on ThreadedVst3Wrapper instead.
    /// </summary>
    public OwnVst3Wrapper InnerWrapper => _inner;

    public ThreadedVst3Wrapper(string? dllPath = null)
    {
        _inner = dllPath != null ? new OwnVst3Wrapper(dllPath) : new OwnVst3Wrapper();
        _cmdQueue = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), boundedCapacity: 128);
        _stateQueue = new LockFreeQueue<VstStateChange>(512);

        _pluginThread = new Thread(PluginThreadProc)
        {
            Name = "VST Plugin Thread",
            IsBackground = true,
            Priority = ThreadPriority.Normal
        };
        _pluginThread.Start();
    }

    // -------------------------------------------------------------------------
    // Async plugin operations – all execute on the dedicated plugin thread.
    // -------------------------------------------------------------------------

    public Task<bool> LoadPluginAsync(string pluginPath) =>
        PostCommand(() =>
        {
            bool ok = _inner.LoadPlugin(pluginPath);
            Interlocked.Exchange(ref _state, ok
                ? (int)VstPluginState.Loaded
                : (int)VstPluginState.Error);
            return ok;
        });

    public Task<bool> InitializeAsync(double sampleRate, int maxBlockSize) =>
        PostCommand(() =>
        {
            bool ok = _inner.Initialize(sampleRate, maxBlockSize);
            if (ok)
            {
                _actualIn  = _inner.ActualInputChannels;
                _actualOut = _inner.ActualOutputChannels;
                Interlocked.Exchange(ref _state, (int)VstPluginState.Ready);
            }
            else
            {
                Interlocked.Exchange(ref _state, (int)VstPluginState.Error);
            }
            return ok;
        });

    public Task<EditorSize?> GetEditorSizeAsync() =>
        PostCommand(() => _inner.GetEditorSize());

    public Task<int> GetParameterCountAsync() =>
        PostCommand(() => _inner.GetParameterCount());

    public Task<List<VST3Parameter>> GetAllParametersAsync() =>
        PostCommand(() => _inner.GetAllParameters());

    public Task<VST3Parameter> GetParameterAtAsync(int index) =>
        PostCommand(() => _inner.GetParameterAt(index));

    public Task<double> GetParameterAsync(int paramId) =>
        PostCommand(() => _inner.GetParameter(paramId));

    public Task<string> GetNameAsync() =>
        PostCommand(() => _inner.Name);

    public Task<string> GetVendorAsync() =>
        PostCommand(() => _inner.Vendor);

    public Task<string?> GetVersionAsync() =>
        PostCommand(() => _inner.Version);

    public Task<bool> GetIsInstrumentAsync() =>
        PostCommand(() => _inner.IsInstrument);

    public Task<bool> GetIsEffectAsync() =>
        PostCommand(() => _inner.IsEffect);

    public Task<bool> GetIsMidiOnlyAsync() =>
        PostCommand(() => _inner.IsMidiOnly);

    public Task<string> GetPluginInfoAsync() =>
        PostCommand(() => _inner.PluginInfo);

    // -------------------------------------------------------------------------
    // UI → Audio lock-free state changes.
    // Enqueue to the SPSC queue; the audio thread drains it before each block.
    // If the queue is full the change is logged and dropped — the queue has 512
    // slots and is drained every ~11 ms at 44100/512, so a full queue indicates
    // a bug in the caller (sending thousands of changes per second).
    // The previous fallback (route to plugin thread) was removed because it caused
    // a data race: _inner.SetParameter on the plugin thread is concurrent with
    // _inner.ProcessAudio on the audio thread, which is unsafe.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets a parameter value from the UI thread.
    /// Applied on the audio thread before the next block (lock-free, ~11 ms latency at 44100/512).
    /// </summary>
    public void SetParameter(int paramId, double value)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForParameter(paramId, value)))
            Console.WriteLine($"[ThreadedVst3Wrapper] SPSC queue full — SetParameter({paramId}) dropped.");
    }

    /// <summary>Sets the playback tempo from the UI thread.</summary>
    public void SetTempo(double bpm)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForTempo(bpm)))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — SetTempo dropped.");
    }

    /// <summary>Sets the transport playing state from the UI thread.</summary>
    public void SetTransportState(bool isPlaying)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForTransport(isPlaying)))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — SetTransportState dropped.");
    }

    /// <summary>Resets the transport position from the UI thread.</summary>
    public void ResetTransportPosition()
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForResetTransport()))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — ResetTransportPosition dropped.");
    }

    // -------------------------------------------------------------------------
    // Audio-thread direct calls (zero-overhead after first block).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes audio. Call directly from the audio thread.
    ///
    /// Thread safety:
    ///   • Atomically transitions Ready → Processing before touching any native state,
    ///     and restores Ready on exit. Concurrent Dispose() is safe: if the native
    ///     library is freed while this method is still running, Dispose() spins
    ///     briefly until the state leaves Processing before releasing resources.
    ///   • Returns false immediately if the plugin is not in Ready state (e.g. not
    ///     yet initialized, error, or already disposed).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessAudio(float[][] inputs, float[][] outputs, int numChannels, int numSamples)
    {
        if (_disposed) return false;

        // Atomically claim the Processing slot. Only one audio thread should ever call
        // this, so contention here means a caller bug.
        if (Interlocked.CompareExchange(ref _state, (int)VstPluginState.Processing, (int)VstPluginState.Ready)
            != (int)VstPluginState.Ready)
            return false;

        // Second disposed check: Dispose() could have run between the first check and the CAS.
        if (_disposed)
        {
            Interlocked.Exchange(ref _state, (int)VstPluginState.NotLoaded);
            return false;
        }

        try
        {
            DrainStateQueue();
            return _inner.ProcessAudio(inputs, outputs, numChannels, numSamples);
        }
        finally
        {
            // Restore Ready. If Dispose() ran concurrently it may have already set
            // NotLoaded — CompareExchange leaves that intact.
            Interlocked.CompareExchange(ref _state, (int)VstPluginState.Ready, (int)VstPluginState.Processing);
        }
    }

    /// <summary>Sends a MIDI event from the audio thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendMidiEvent(byte status, byte data1, byte data2) =>
        _inner.SendMidiEvent(status, data1, data2);

    /// <summary>Processes MIDI events from the audio thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessMidi(MidiEvent[] events) => _inner.ProcessMidi(events);

    // -------------------------------------------------------------------------
    // Editor / idle – these must stay on the caller thread (see VstEditorController).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls VST ProcessIdle. Invoked from the dedicated idle thread in VstEditorController.
    /// Safe to call from any thread (no cross-thread marshalling needed here).
    /// </summary>
    public void ProcessIdle() => _inner.ProcessIdle();

    public bool IsEditorOpen => _inner.IsEditorOpen;

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DrainStateQueue()
    {
        while (_stateQueue.TryDequeue(out var change))
        {
            switch (change.Type)
            {
                case VstChangeType.Parameter:
                    _inner.SetParameter(change.IntArg, change.DoubleArg);
                    break;
                case VstChangeType.Tempo:
                    _inner.SetTempo(change.DoubleArg);
                    break;
                case VstChangeType.TransportState:
                    _inner.SetTransportState(change.IntArg != 0);
                    break;
                case VstChangeType.ResetTransport:
                    _inner.ResetTransportPosition();
                    break;
            }
        }
    }

    private void PluginThreadProc()
    {
        foreach (var cmd in _cmdQueue.GetConsumingEnumerable())
        {
            try { cmd(); }
            catch { /* exceptions are captured inside each command via TaskCompletionSource */ }
        }
    }

    private Task<T> PostCommand<T>(Func<T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThreadedVst3Wrapper));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cmdQueue.Add(() =>
        {
            try { tcs.SetResult(operation()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // If the audio thread is inside ProcessAudio, wait up to 100 ms for it to exit
        // before freeing native resources. At 44100/512 (~11 ms/block) this covers
        // several full callbacks. If it takes longer, we proceed anyway — the audio
        // engine should have been stopped before Dispose() is called.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _state) == (int)VstPluginState.Processing
               && sw.ElapsedMilliseconds < 100)
            Thread.SpinWait(20);

        Interlocked.Exchange(ref _state, (int)VstPluginState.NotLoaded);

        _cmdQueue.CompleteAdding();
        if (!_pluginThread.Join(2000))
            Console.WriteLine("[ThreadedVst3Wrapper] Plugin thread did not stop within 2 s.");

        _inner.Dispose();
        _cmdQueue.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ---------------------------------------------------------------------------
// SPSC queue item types
// ---------------------------------------------------------------------------

internal enum VstChangeType : byte
{
    Parameter,
    Tempo,
    TransportState,
    ResetTransport
}

internal readonly struct VstStateChange
{
    public readonly VstChangeType Type;
    public readonly int IntArg;
    public readonly double DoubleArg;

    private VstStateChange(VstChangeType type, int intArg, double doubleArg)
    {
        Type = type;
        IntArg = intArg;
        DoubleArg = doubleArg;
    }

    public static VstStateChange ForParameter(int paramId, double value) =>
        new(VstChangeType.Parameter, paramId, value);

    public static VstStateChange ForTempo(double bpm) =>
        new(VstChangeType.Tempo, 0, bpm);

    public static VstStateChange ForTransport(bool playing) =>
        new(VstChangeType.TransportState, playing ? 1 : 0, 0.0);

    public static VstStateChange ForResetTransport() =>
        new(VstChangeType.ResetTransport, 0, 0.0);
}
