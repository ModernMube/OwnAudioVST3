using System;
using System.Threading;
using System.Threading.Tasks;
using OwnVST3Host;

namespace OwnVST3EditorDemo
{
    /// <summary>
    /// Generates white noise and processes it through a VST plugin in real-time.
    /// Audio processing runs on its own dedicated thread, completely independent of the UI thread.
    /// Accepts both OwnVst3Wrapper (legacy) and ThreadedVst3Wrapper (recommended).
    /// </summary>
    public class WhiteNoiseProcessor : IDisposable
    {
        // Delegate-based call surface so the audio loop is agnostic to wrapper type.
        private readonly Func<float[][], float[][], int, int, bool> _processAudio;
        private readonly Action<bool> _setTransportState;
        private readonly Action _resetTransportPosition;

        private readonly Random _random = new();
        private CancellationTokenSource? _cts;
        private Thread? _audioThread;
        private bool _disposed;

        public bool IsPlaying => _audioThread?.IsAlive ?? false;

        /// <summary>
        /// Legacy constructor – uses OwnVst3Wrapper directly.
        /// </summary>
        public WhiteNoiseProcessor(OwnVst3Wrapper plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            _processAudio = plugin.ProcessAudio;
            _setTransportState = plugin.SetTransportState;
            _resetTransportPosition = plugin.ResetTransportPosition;
        }

        /// <summary>
        /// Recommended constructor – uses ThreadedVst3Wrapper.
        /// State changes (transport start/stop) are posted through the lock-free SPSC queue
        /// and applied by the audio thread before the next block, so the UI thread never waits.
        /// </summary>
        public WhiteNoiseProcessor(ThreadedVst3Wrapper plugin)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            _processAudio = plugin.ProcessAudio;
            _setTransportState = plugin.SetTransportState;
            _resetTransportPosition = plugin.ResetTransportPosition;
        }

        public void Start()
        {
            if (IsPlaying || _disposed) return;

            // SetTransportState goes through the SPSC queue to the audio thread.
            _setTransportState(true);

            _cts = new CancellationTokenSource();
            _audioThread = new Thread(() => AudioThreadProc(_cts.Token))
            {
                Name = "VST Audio Thread",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioThread.Start();
        }

        public void Stop()
        {
            if (!IsPlaying || _disposed) return;

            _cts?.Cancel();
            _audioThread?.Join(1000);
            _cts?.Dispose();
            _cts = null;
            _audioThread = null;

            _setTransportState(false);
            _resetTransportPosition();
        }

        /// <summary>
        /// Audio processing loop – runs on its own dedicated thread, never on the UI thread.
        /// Buffers are pre-allocated here so there are no heap allocations inside the loop.
        /// </summary>
        private void AudioThreadProc(CancellationToken ct)
        {
            const int sampleRate = 44100;
            const int bufferSize = 512;
            const int channels = 2;
            const double duration = 60.0;

            int totalSamples = (int)(sampleRate * duration);
            int processed = 0;

            // Pre-allocate buffers once – zero allocations inside the loop.
            float[][] inputs = new float[channels][];
            float[][] outputs = new float[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                inputs[ch] = new float[bufferSize];
                outputs[ch] = new float[bufferSize];
            }

            while (!ct.IsCancellationRequested && processed < totalSamples)
            {
                int count = Math.Min(bufferSize, totalSamples - processed);

                // Generate white noise in-place (no allocation).
                for (int ch = 0; ch < channels; ch++)
                    for (int i = 0; i < count; i++)
                        inputs[ch][i] = (float)(_random.NextDouble() - 0.5) * 0.3f;

                try { _processAudio(inputs, outputs, channels, count); }
                catch { }

                processed += count;

                // Simulate real-time pacing (sleep is acceptable here – this is a demo).
                Thread.Sleep((int)(count * 1000.0 / sampleRate));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
