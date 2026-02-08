using System;
using System.Threading;
using System.Threading.Tasks;
using OwnVST3Host;

namespace OwnVST3EditorDemo
{
    /// <summary>
    /// Simple white noise generator and VST processor for testing
    /// Generates white noise and processes it through a VST plugin in real-time
    /// </summary>
    public class WhiteNoiseProcessor : IDisposable
    {
        private readonly OwnVst3Wrapper _plugin;
        private readonly Random _random = new Random();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _processingTask;
        private bool _disposed;

        public bool IsPlaying => _processingTask != null && !_processingTask.IsCompleted;

        public WhiteNoiseProcessor(OwnVst3Wrapper plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        }

        public void Start()
        {
            if (IsPlaying || _disposed) return;

            _cancellationTokenSource = new CancellationTokenSource();
            _processingTask = Task.Run(() => ProcessAudioLoop(_cancellationTokenSource.Token));
        }

        public void Stop()
        {
            if (!IsPlaying || _disposed) return;

            _cancellationTokenSource?.Cancel();
            _processingTask?.Wait(1000);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _processingTask = null;
        }

        private void ProcessAudioLoop(CancellationToken cancellationToken)
        {
            const int sampleRate = 44100;
            const int bufferSize = 512;
            const int channels = 2;
            const double duration = 60.0; // 60 seconds

            int totalSamples = (int)(sampleRate * duration);
            int samplesProcessed = 0;

            // Allocate buffers
            float[][] inputBuffers = new float[channels][];
            float[][] outputBuffers = new float[channels][];

            for (int i = 0; i < channels; i++)
            {
                inputBuffers[i] = new float[bufferSize];
                outputBuffers[i] = new float[bufferSize];
            }

            while (!cancellationToken.IsCancellationRequested && samplesProcessed < totalSamples)
            {
                int samplesToProcess = Math.Min(bufferSize, totalSamples - samplesProcessed);

                // Generate white noise
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int i = 0; i < samplesToProcess; i++)
                    {
                        // Generate white noise: random values between -0.5 and 0.5
                        inputBuffers[ch][i] = (float)(_random.NextDouble() - 0.5) * 0.3f; // Reduced amplitude
                    }
                }

                // Process through VST plugin
                try
                {
                    _plugin.ProcessAudio(inputBuffers, outputBuffers, channels, samplesToProcess);
                }
                catch
                {
                    // Ignore processing errors
                }

                samplesProcessed += samplesToProcess;

                // Simulate real-time playback timing
                Thread.Sleep((int)(samplesToProcess * 1000.0 / sampleRate));
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
