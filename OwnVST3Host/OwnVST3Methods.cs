using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public unsafe partial class OwnVst3Wrapper
    {
#nullable disable warnings
        #region Public API methods

        /// <summary>
        /// Loads a VST3 plugin from the specified path
        /// </summary>
        /// <param name="pluginPath">Path to the VST3 plugin</param>
        /// <returns>True if successful</returns>
        public bool LoadPlugin(string pluginPath)
        {
            CheckDisposed();

            byte* path = _toUtf8(pluginPath);
            try { return _loadPluginFunc(_pluginHandle, path) != 0; }
            finally { Marshal.FreeCoTaskMem((IntPtr)path); }
        }

        /// <summary>
        /// Creates an editor view for the plugin
        /// </summary>
        /// <param name="windowHandle">Window handle where the editor should appear</param>
        /// <returns>True if successful</returns>
        public bool CreateEditor(IntPtr windowHandle)
        {
            CheckDisposed();
            return _createEditorFunc(_pluginHandle, windowHandle) != 0;
        }

        /// <summary>
        /// Closes the plugin editor
        /// </summary>
        public void CloseEditor()
        {
            CheckDisposed();
            _closeEditorFunc(_pluginHandle);
        }

        /// <summary>
        /// Resizes the plugin editor
        /// </summary>
        /// <param name="width">Width</param>
        /// <param name="height">Height</param>
        public void ResizeEditor(int width, int height)
        {
            CheckDisposed();
            _resizeEditorFunc(_pluginHandle, width, height);
        }

        /// <summary>
        /// Gets the plugin editor's preferred size
        /// </summary>
        /// <param name="width">Output: editor width</param>
        /// <param name="height">Output: editor height</param>
        /// <returns>True if successful</returns>
        public bool GetEditorSize(out int width, out int height)
        {
            CheckDisposed();

            int w = 0, h = 0;
            bool ok = _getEditorSizeFunc(_pluginHandle, &w, &h) != 0;
            width = w;
            height = h;
            return ok;
        }

        /// <summary>
        /// Gets the plugin editor's preferred size as an EditorSize struct
        /// </summary>
        /// <returns>EditorSize struct with Width and Height, or null if failed</returns>
        public EditorSize? GetEditorSize()
        {
            if (GetEditorSize(out int width, out int height))
            {
                return new EditorSize(width, height);
            }
            return null;
        }

        /// <summary>
        /// Returns the number of parameters in the plugin
        /// </summary>
        /// <returns>Parameter count</returns>
        public int GetParameterCount()
        {
            CheckDisposed();
            return _getParameterCountFunc(_pluginHandle);
        }

        /// <summary>
        /// Gets a parameter at the specified index
        /// </summary>
        /// <param name="index">Parameter index</param>
        /// <returns>Parameter data</returns>
        public VST3Parameter GetParameterAt(int index)
        {
            CheckDisposed();

            VST3ParameterC paramC = new VST3ParameterC();
            bool success = _getParameterAtFunc(_pluginHandle, index, &paramC) != 0;

            if (!success)
                throw new ArgumentOutOfRangeException(nameof(index), "Invalid parameter index");

            return new VST3Parameter
            {
                Id = paramC.id,
                Name = Marshal.PtrToStringUTF8(paramC.name),
                MinValue = paramC.minValue,
                MaxValue = paramC.maxValue,
                DefaultValue = paramC.defaultValue,
                CurrentValue = paramC.currentValue
            };
        }

        /// <summary>
        /// Sets a parameter value
        /// </summary>
        /// <param name="paramId">Parameter ID</param>
        /// <param name="value">New value</param>
        /// <returns>True if successful</returns>
        public bool SetParameter(int paramId, double value)
        {
            CheckDisposed();
            return _setParameterFunc(_pluginHandle, paramId, value) != 0;
        }

        /// <summary>
        /// Gets a parameter's current value
        /// </summary>
        /// <param name="paramId">Parameter ID</param>
        /// <returns>Parameter value</returns>
        public double GetParameter(int paramId)
        {
            CheckDisposed();
            return _getParameterFunc(_pluginHandle, paramId);
        }

        /// <summary>
        /// Initializes the plugin and lays out the planar scratch ProcessAudio hands it,
        /// so the audio thread never allocates or pins. Safe to call again to grow the block.
        /// </summary>
        public bool Initialize(double sampleRate, int maxBlockSize)
        {
            CheckDisposed();

            if (_initializeFunc(_pluginHandle, sampleRate, maxBlockSize) == 0)
                return false;

            _actualIn = ActualInputChannels;
            _actualOut = ActualOutputChannels;

            int channels = Math.Max(1, Math.Max(_actualIn, _actualOut));
            if (channels != _preallocChannels || maxBlockSize != _preallocBlock)
                _allocScratch(channels, maxBlockSize);

            return true;
        }

        private unsafe void _allocScratch(int channels, int block)
        {
            _freeScratch();

            nuint _ptrBytes = (nuint)channels * (nuint)sizeof(float*);
            nuint _dataBytes = (nuint)channels * (nuint)block * sizeof(float);

            _inPlanes = (float**)NativeMemory.Alloc(_ptrBytes);
            _outPlanes = (float**)NativeMemory.Alloc(_ptrBytes);
            _inData = (float*)NativeMemory.AlignedAlloc(_dataBytes, 64);
            _outData = (float*)NativeMemory.AlignedAlloc(_dataBytes, 64);

            NativeMemory.Clear(_inData, _dataBytes);
            NativeMemory.Clear(_outData, _dataBytes);

            for (int c = 0; c < channels; c++)
            {
                _inPlanes[c] = _inData + (nint)c * block;
                _outPlanes[c] = _outData + (nint)c * block;
            }

            _preallocChannels = channels;
            _preallocBlock = block;
        }

        private unsafe void _freeScratch()
        {
            if (_inPlanes != null) { NativeMemory.Free(_inPlanes); _inPlanes = null; }
            if (_outPlanes != null) { NativeMemory.Free(_outPlanes); _outPlanes = null; }
            if (_inData != null) { NativeMemory.AlignedFree(_inData); _inData = null; }
            if (_outData != null) { NativeMemory.AlignedFree(_outData); _outData = null; }

            _preallocChannels = 0;
            _preallocBlock = 0;
        }

        /// <summary>
        /// Processes audio data through the plugin
        /// </summary>
        /// <param name="inputs">Input audio data</param>
        /// <param name="outputs">Output audio data</param>
        /// <param name="numChannels">Number of channels</param>
        /// <param name="numSamples">Number of samples per channel</param>
        /// <returns>True if successful</returns>
        public unsafe bool ProcessAudio(float[][] inputs, float[][] outputs, int numChannels, int numSamples)
        {
            CheckDisposed();

            // The wider of the two buses, the same way Initialize preallocated. An instrument
            // has no input bus at all, and taking the narrower one made that a zero here - the
            // plugin was never called and every block came back silent.
            int _pluginChannels = Math.Min(numChannels, Math.Max(_actualIn, _actualOut));
            _pluginChannels = Math.Min(_pluginChannels, _preallocChannels);

            // No audio bus, or a block the scratch was not sized for: dry through, no rebuild
            // here. Initialize() is where the buffers grow, and it is not audio-thread work.
            if (_pluginChannels <= 0 || numSamples > _preallocBlock)
            {
                int _copyChannels = Math.Min(inputs.Length, outputs.Length);
                for (int ch = 0; ch < _copyChannels; ch++)
                    inputs[ch].AsSpan(0, numSamples).CopyTo(outputs[ch]);
                return false;
            }

            for (int ch = 0; ch < _pluginChannels; ch++)
                inputs[ch].AsSpan(0, numSamples).CopyTo(new Span<float>(_inPlanes[ch], numSamples));

            AudioBufferC buffer = new AudioBufferC
            {
                inputs = (IntPtr)_inPlanes,
                outputs = (IntPtr)_outPlanes,
                numChannels = _pluginChannels,
                numSamples = numSamples
            };

            bool result = _processAudioFunc(_pluginHandle, &buffer) != 0;

            for (int ch = 0; ch < _pluginChannels; ch++)
            {
                if (result)
                    new Span<float>(_outPlanes[ch], numSamples).CopyTo(outputs[ch].AsSpan(0, numSamples));
                else
                    inputs[ch].AsSpan(0, numSamples).CopyTo(outputs[ch]);
            }

            // Pass-through any extra channels that the plugin did not process.
            for (int ch = _pluginChannels; ch < Math.Min(numChannels, outputs.Length); ch++)
                inputs[ch].AsSpan(0, numSamples).CopyTo(outputs[ch]);

            return result;
        }

        /// <summary>
        /// Sends a single MIDI message to the plugin
        /// </summary>
        /// <param name="status">Status byte (e.g. 0x90 = Note On, 0x80 = Note Off)</param>
        /// <param name="data1">First data byte (e.g. note number)</param>
        /// <param name="data2">Second data byte (e.g. velocity)</param>
        /// <returns>True if successful</returns>
        public unsafe bool SendMidiEvent(byte status, byte data1, byte data2)
        {
            CheckDisposed();

            MidiEventC ev = new MidiEventC { status = status, data1 = data1, data2 = data2 };
            return _processMidiFunc(_pluginHandle, &ev, 1) != 0;
        }

        /// <summary>
        /// Processes MIDI events. Marshalled through stack scratch in chunks — this runs on
        /// the audio thread and a per-call array was straight gen0 churn.
        /// </summary>
        public unsafe bool ProcessMidi(MidiEvent[] events)
        {
            CheckDisposed();

            if (events == null || events.Length == 0)
                return false;

            const int _chunk = 128;
            MidiEventC* scratch = stackalloc MidiEventC[_chunk];
            bool ok = true;

            for (int start = 0; start < events.Length; start += _chunk)
            {
                int n = Math.Min(_chunk, events.Length - start);

                for (int i = 0; i < n; i++)
                {
                    MidiEvent e = events[start + i];
                    scratch[i].status = e.Status;
                    scratch[i].data1 = e.Data1;
                    scratch[i].data2 = e.Data2;
                    scratch[i].sampleOffset = e.SampleOffset;
                }

                ok &= _processMidiFunc(_pluginHandle, scratch, n) != 0;
            }

            return ok;
        }

        /// <summary>
        /// Checks if the plugin accepts MIDI events but has no audio output (e.g. MIDI effect, arpeggiator).
        /// Returns false if the native library does not support this query (older DLL versions).
        /// </summary>
        public bool IsMidiOnly
        {
            get
            {
                CheckDisposed();
                return _isMidiOnlyFunc != null && _isMidiOnlyFunc(_pluginHandle) != 0;
            }
        }

        /// <summary>
        /// Checks if the plugin is an instrument (MIDI input + audio output)
        /// </summary>
        public bool IsInstrument
        {
            get
            {
                CheckDisposed();
                return _isInstrumentFunc(_pluginHandle) != 0;
            }
        }

        /// <summary>
        /// Checks if the plugin is an effect
        /// </summary>
        public bool IsEffect
        {
            get
            {
                CheckDisposed();
                return _isEffectFunc(_pluginHandle) != 0;
            }
        }

        /// <summary>
        /// Returns the plugin name
        /// </summary>
        public string Name
        {
            get
            {
                CheckDisposed();
                IntPtr namePtr = _getNameFunc(_pluginHandle);
                return Marshal.PtrToStringUTF8(namePtr);
            }
        }

        /// <summary>
        /// Returns the plugin vendor
        /// </summary>
        public string Vendor
        {
            get
            {
                CheckDisposed();
                IntPtr vendorPtr = _getVendorFunc(_pluginHandle);
                return Marshal.PtrToStringUTF8(vendorPtr);
            }
        }

        /// <summary>
        /// Returns the plugin version
        /// </summary>
        public string? Version
        {
            get
            {
                CheckDisposed();
                if (_getVersionFunc == null)
                    return null; // Function not available in this version of the native library
                IntPtr versionPtr = _getVersionFunc(_pluginHandle);
                return Marshal.PtrToStringUTF8(versionPtr);
            }
        }

        /// <summary>
        /// Returns the plugin information
        /// </summary>
        public string PluginInfo
        {
            get
            {
                CheckDisposed();
                IntPtr infoPtr = _getPluginInfoFunc(_pluginHandle);
                return Marshal.PtrToStringUTF8(infoPtr);
            }
        }

        /// <summary>
        /// Returns the actual input channel count accepted by the plugin after setBusArrangement
        /// </summary>
        public int ActualInputChannels
        {
            get
            {
                CheckDisposed();
                return _getActualInputChannelsFunc != null ? _getActualInputChannelsFunc(_pluginHandle) : 2;
            }
        }

        /// <summary>
        /// Returns the actual output channel count accepted by the plugin after setBusArrangement
        /// </summary>
        public int ActualOutputChannels
        {
            get
            {
                CheckDisposed();
                return _getActualOutputChannelsFunc != null ? _getActualOutputChannelsFunc(_pluginHandle) : 2;
            }
        }

        /// <summary>
        /// Returns the plugin's processing latency in samples (per channel), reported after
        /// Initialize(). Zero before initialization or for a zero-latency plugin. A native audio host
        /// uses this to delay-compensate other tracks so the plugin's output stays sample-accurate.
        /// </summary>
        public int LatencySamples
        {
            get
            {
                CheckDisposed();
                return _getLatencySamplesFunc != null ? _getLatencySamplesFunc(_pluginHandle) : 0;
            }
        }

        /// <summary>
        /// Sets the playback tempo forwarded to the plugin via ProcessContext
        /// </summary>
        public void SetTempo(double bpm)
        {
            CheckDisposed();
            if (_setTempoFunc != null) _setTempoFunc(_pluginHandle, bpm);
        }

        /// <summary>
        /// Sets the transport playing state forwarded to the plugin via ProcessContext
        /// </summary>
        public void SetTransportState(bool isPlaying)
        {
            CheckDisposed();
            if (_setTransportStateFunc != null) _setTransportStateFunc(_pluginHandle, isPlaying ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Enables or disables plugin bypass. When bypassed, the plugin is still driven every block
        /// (so it never goes cold) but runs through JUCE's processBlockBypassed(), which passes the
        /// input through delayed by the plugin's own latency — so toggling bypass introduces no time
        /// shift relative to the processed output. Safe to call from any thread; applied on the next
        /// audio block.
        /// </summary>
        public void SetBypass(bool bypassed)
        {
            CheckDisposed();
            if (_setBypassFunc != null) _setBypassFunc(_pluginHandle, bypassed ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Resets the transport sample position counter (e.g. on Stop)
        /// </summary>
        public void ResetTransportPosition()
        {
            CheckDisposed();
            if (_resetTransportPositionFunc != null) _resetTransportPositionFunc(_pluginHandle);
        }

        /// <summary>
        /// Returns the complete processor state as a byte array, or null on failure.
        /// </summary>
        public byte[]? GetState()
        {
            CheckDisposed();
            if (_getStateFunc == null || _freeStateDataFunc == null) return null;

            IntPtr ptr = IntPtr.Zero;
            int len = 0;
            if (_getStateFunc(_pluginHandle, &ptr, &len) == 0 || ptr == IntPtr.Zero || len <= 0)
                return null;
            try
            {
                byte[] result = new byte[len];
                Marshal.Copy(ptr, result, 0, len);
                return result;
            }
            finally
            {
                _freeStateDataFunc(ptr);
            }
        }

        /// <summary>
        /// Restores the processor state from a byte array and syncs the controller.
        /// </summary>
        public bool SetState(byte[] data)
        {
            CheckDisposed();
            if (_setStateFunc == null || data == null || data.Length == 0) return false;

            fixed (byte* p = data)
                return _setStateFunc(_pluginHandle, p, data.Length) != 0;
        }

        /// <summary>
        /// Clears the string cache
        /// </summary>
        public void ClearStringCache()
        {
            CheckDisposed();
            _clearStringCacheFunc();
        }

        /// <summary>
        /// Process idle events - should be called periodically from UI thread.
        /// This is essential for proper popup menu handling on all platforms,
        /// especially when running with a separate audio thread.
        /// </summary>
        public void ProcessIdle()
        {
            CheckDisposed();
            if (_processIdleFunc != null) _processIdleFunc(_pluginHandle);
        }

        /// <summary>
        /// Returns true if the plugin has an editor UI.
        /// Safe to call from any thread; does NOT create a temporary editor component.
        /// Falls back to checking GetEditorSize on older DLL versions.
        /// </summary>
        public bool HasEditor
        {
            get
            {
                CheckDisposed();
                if (_hasEditorFunc != null)
                    return _hasEditorFunc(_pluginHandle) != 0;
                // Fallback for DLLs that pre-date VST3Plugin_HasEditor
                return GetEditorSize(out _, out _);
            }
        }

        /// <summary>
        /// Check if the editor window is currently open
        /// </summary>
        public bool IsEditorOpen
        {
            get
            {
                CheckDisposed();
                return _isEditorOpenFunc != null && _isEditorOpenFunc(_pluginHandle) != 0;
            }
        }

        /// <summary>
        /// Gets all parameters
        /// </summary>
        /// <returns>List of parameters</returns>
        public List<VST3Parameter> GetAllParameters()
        {
            CheckDisposed();

            int count = GetParameterCount();
            List<VST3Parameter> parameters = new List<VST3Parameter>(count);

            for (int i = 0; i < count; i++)
            {
                parameters.Add(GetParameterAt(i));
            }

            return parameters;
        }

        private void CheckDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OwnVst3Wrapper));
        }

        #endregion
#nullable restore warnings
    }
}
