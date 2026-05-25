using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OwnVST3Host;

#region Win32 helpers (plugin-thread message pump)

file static class Win32Pump
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX, ptY;
    }

    internal const uint QS_ALLINPUT = 0x04FF;
    internal const uint PM_REMOVE   = 0x0001;
    internal const uint WAIT_FAILED = 0xFFFFFFFF;

    [DllImport("user32.dll")]
    internal static extern uint MsgWaitForMultipleObjects(
        uint nCount, IntPtr[] pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG lpmsg);

    // OLE inicializáció – szükséges a JUCE és egyéb COM/UI-alapú pluginoknál
    // (vágólap, Drag & Drop, ActiveX, OLE objektumok).
    // Az STA + CoInitialize önmagában nem elegendő; OleInitialize hívása szükséges.
    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();
}

#endregion

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
    private readonly ConcurrentQueue<Action> _cmdQueue = new();
    private readonly AutoResetEvent _cmdReady = new(false);
    private readonly LockFreeQueue<VstStateChange> _stateQueue;

    private volatile bool _disposed;

    // Windows-only: delegate to VST3Plugin_SafeDispatchMessage in the native DLL.
    // That function wraps Win32 DispatchMessage() with a C++ __try/__except block
    // that catches access violations (0xC0000005) from plugin WndProcs.
    // The .NET runtime cannot catch these "corrupted state" SEH exceptions itself.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SafeDispatchDelegate(ref Win32Pump.MSG msg);
    private SafeDispatchDelegate? _safeDispatch;

    private int _state = (int)VstPluginState.NotLoaded;

    private volatile int _actualIn = 2;
    private volatile int _actualOut = 2;

    #region Public state API – safe to read from any thread, zero allocation.

    public VstPluginState State => (VstPluginState)Volatile.Read(ref _state);
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

    /// <summary>
    /// Handle of the loaded native library. Exposed so that callers
    /// (e.g. ThreadedVst3Wrapper) can resolve additional exports from the same
    /// DLL via NativeLibrary.TryGetExport without loading the DLL a second time.
    /// </summary>
    public IntPtr LibraryHandle => _inner.LibraryHandle;

    public ThreadedVst3Wrapper(string? dllPath = null)
    {
        _inner = dllPath != null ? new OwnVst3Wrapper(dllPath) : new OwnVst3Wrapper();
        _stateQueue = new LockFreeQueue<VstStateChange>(512);

        // On Windows, try to resolve the SEH-safe DispatchMessage wrapper from the
        // native DLL.  NativeLibrary.TryGetExport works on the already-loaded handle
        // so no extra LoadLibrary call is needed. Falls back to the direct P/Invoke
        // if the export is absent (e.g. older native binary).
        if (OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryGetExport(_inner.LibraryHandle,
                    "VST3Plugin_SafeDispatchMessage", out IntPtr fnPtr))
                _safeDispatch = Marshal.GetDelegateForFunctionPointer<SafeDispatchDelegate>(fnPtr);
        }

        _pluginThread = new Thread(PluginThreadProc)
        {
            Name = "VST Plugin Thread",
            IsBackground = true,
            Priority = ThreadPriority.Normal
        };
        _pluginThread.Start();
    }

    #endregion

    #region Async plugin operations – all execute on the dedicated plugin thread.

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
                _actualIn = _inner.ActualInputChannels;
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
        Task.FromResult(_inner.GetEditorSize());

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

    #endregion

    /// <summary>
    /// Sets a parameter value from the UI thread.
    /// Applied on the audio thread before the next block (lock-free, ~11 ms latency at 44100/512).
    /// </summary>
    public void SetParameter(int paramId, double value)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForParameter(paramId, value)))
            Console.WriteLine($"[ThreadedVst3Wrapper] SPSC queue full — SetParameter({paramId}) dropped.");
    }

    /// <summary>
    /// Sets a parameter value synchronously on the dedicated plugin thread.
    /// Unlike SetParameter (which enqueues to the audio-thread SPSC queue), this call
    /// executes on the same thread used for plugin initialization and GetAllParametersAsync,
    /// ensuring that the native controller state is updated immediately and is visible
    /// to subsequent GetParameterAt reads without requiring a processAudio cycle.
    /// Use this for non-realtime operations such as project state restoration.
    /// </summary>
    public Task<bool> SetParameterAsync(int paramId, double value) =>
        PostCommand(() => _inner.SetParameter(paramId, value));

    public Task<byte[]?> GetStateAsync() =>
        PostCommand(() => _inner.GetState());

    public Task<bool> SetStateAsync(byte[] data) =>
        PostCommand(() => _inner.SetState(data));

    /// <summary>
    /// Sets the playback tempo from the UI thread.
    /// </summary>
    public void SetTempo(double bpm)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForTempo(bpm)))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — SetTempo dropped.");
    }

    /// <summary>
    /// Sets the transport playing state from the UI thread.
    /// </summary>
    public void SetTransportState(bool isPlaying)
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForTransport(isPlaying)))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — SetTransportState dropped.");
    }

    /// <summary>
    /// Resets the transport position from the UI thread.
    /// </summary>
    public void ResetTransportPosition()
    {
        if (!_stateQueue.TryEnqueue(VstStateChange.ForResetTransport()))
            Console.WriteLine("[ThreadedVst3Wrapper] SPSC queue full — ResetTransportPosition dropped.");
    }

    #region Audio-thread direct calls (zero-overhead after first block).

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

        if (Interlocked.CompareExchange(ref _state, (int)VstPluginState.Processing, (int)VstPluginState.Ready)
            != (int)VstPluginState.Ready)
            return false;

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
            Interlocked.CompareExchange(ref _state, (int)VstPluginState.Ready, (int)VstPluginState.Processing);
        }
    }

    /// <summary>
    /// Sends a MIDI event from the audio thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SendMidiEvent(byte status, byte data1, byte data2) =>
        _inner.SendMidiEvent(status, data1, data2);

    /// <summary>
    /// Processes MIDI events from the audio thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ProcessMidi(MidiEvent[] events) => _inner.ProcessMidi(events);

    #endregion

    #region Editor / idle these must stay on the caller thread (see VstEditorController).

    /// <summary>
    /// Calls VST ProcessIdle. Invoked from the dedicated idle thread in VstEditorController.
    /// Safe to call from any thread (no cross-thread marshalling needed here).
    /// </summary>
    public void ProcessIdle() => _inner.ProcessIdle();

    /// <summary>
    /// Returns true if the plugin editor is open
    /// </summary>
    public bool IsEditorOpen => _inner.IsEditorOpen;

    #endregion

    #region Internal

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
        PluginThreadProcGeneric();
    }

    private void PluginThreadProcWindows()
    {
        // Az OleInitialize elvégzi a teljes COM + OLE inicializációt az STA szálra.
        // Ez tartalmaz mindent, amit a CoInitialize(Ex) nyújt, PLUSZ a szükséges
        // OLE alrendszereket (vágólap, Drag & Drop, OLE objektumok) — amelyekre a
        // JUCE alapú pluginok (pl. TDR Nova) támaszkodnak a belső ablakaik WndProc-jaiban.
        Win32Pump.OleInitialize(IntPtr.Zero);
        try
        {
            // Force Win32 to create a message queue for this thread before anything else.
            Win32Pump.PeekMessage(out Win32Pump.MSG _, IntPtr.Zero, 0, 0, 0);

            var handles = new IntPtr[] { _cmdReady.SafeWaitHandle.DangerousGetHandle() };

            while (!_disposed)
            {
                uint r = Win32Pump.MsgWaitForMultipleObjects(1, handles, false, uint.MaxValue, Win32Pump.QS_ALLINPUT);

                if (r == Win32Pump.WAIT_FAILED)
                    break;

                DrainCommandQueue();

                if (_disposed) break;

                while (Win32Pump.PeekMessage(out Win32Pump.MSG msg, IntPtr.Zero, 0, 0, Win32Pump.PM_REMOVE))
                {
                    Win32Pump.TranslateMessage(ref msg);
                    // Use the SEH-safe wrapper from the native DLL when available.
                    // This prevents 0xC0000005 access violations inside plugin WndProcs
                    // (e.g. TDR Nova / JUCE) from terminating the host process.
                    if (_safeDispatch != null)
                        _safeDispatch(ref msg);
                    else
                        Win32Pump.DispatchMessage(ref msg);
                }
            }

            DrainCommandQueue();
        }
        finally
        {
            // Mindig párosítani kell az OleInitialize-t az OleUninitialize-vel,
            // még akkor is, ha a szál kivétellel zárult le.
            Win32Pump.OleUninitialize();
        }
    }

    private void PluginThreadProcGeneric()
    {
        while (!_disposed)
        {
            _cmdReady.WaitOne();
            DrainCommandQueue();
        }
        DrainCommandQueue();
    }

    private void DrainCommandQueue()
    {
        while (_cmdQueue.TryDequeue(out var cmd))
        {
            try { cmd(); }
            catch { }
        }
    }

    private Task<T> PostCommand<T>(Func<T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThreadedVst3Wrapper));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cmdQueue.Enqueue(() =>
        {
            try { tcs.SetResult(operation()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _cmdReady.Set();
        return tcs.Task;
    }

    /// <summary>
    /// Executes an arbitrary action synchronously on the dedicated plugin thread.
    /// This is used to ensure UI creation (CreateEditor) runs on the same thread as LoadPlugin.
    /// </summary>
    public Task RunOnPluginThreadAsync(Action action) =>
        PostCommand(() => { action(); return true; });

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _state) == (int)VstPluginState.Processing
               && sw.ElapsedMilliseconds < 100)
            Thread.SpinWait(20);

        Interlocked.Exchange(ref _state, (int)VstPluginState.NotLoaded);

        _cmdReady.Set(); // wake the plugin thread so it can observe _disposed = true
        if (!_pluginThread.Join(2000))
            Console.WriteLine("[ThreadedVst3Wrapper] Plugin thread did not stop within 2 s.");

        _inner.Dispose();
        _cmdReady.Dispose();
        GC.SuppressFinalize(this);
    }
}

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
