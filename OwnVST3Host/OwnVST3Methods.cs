using System.Runtime.InteropServices;

namespace OwnVST3Host
{
    /// <summary>
    /// C# wrapper for the OwnVst3 native library
    /// </summary>
    public partial class OwnVst3Wrapper
    {
#nullable disable
        #region Public API methods

        /// <summary>
        /// Loads a VST3 plugin from the specified path
        /// </summary>
        /// <param name="pluginPath">Path to the VST3 plugin</param>
        /// <returns>True if successful</returns>
        public bool LoadPlugin(string pluginPath)
        {
            CheckDisposed();
            return _loadPluginFunc(_pluginHandle, pluginPath);
        }

        /// <summary>
        /// Creates an editor view for the plugin
        /// </summary>
        /// <param name="windowHandle">Window handle where the editor should appear</param>
        /// <returns>True if successful</returns>
        public bool CreateEditor(IntPtr windowHandle)
        {
            CheckDisposed();
            return _createEditorFunc(_pluginHandle, windowHandle);
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
            return _getEditorSizeFunc(_pluginHandle, out width, out height);
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
            bool success = _getParameterAtFunc(_pluginHandle, index, ref paramC);

            if (!success)
                throw new ArgumentOutOfRangeException(nameof(index), "Invalid parameter index");

            return new VST3Parameter
            {
                Id = paramC.id,
                Name = Marshal.PtrToStringAnsi(paramC.name),
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
            return _setParameterFunc(_pluginHandle, paramId, value);
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
        /// Initializes the plugin and pre-allocates audio buffer handles to eliminate
        /// per-callback heap allocations in ProcessAudio.
        /// </summary>
        public bool Initialize(double sampleRate, int maxBlockSize)
        {
            CheckDisposed();
            bool ok = _initializeFunc(_pluginHandle, sampleRate, maxBlockSize);
            if (ok)
            {
                int channels = Math.Max(ActualInputChannels, ActualOutputChannels);
                if (channels != _preallocChannels)
                {
                    // Release previous pinned arrays before reallocating.
                    if (_inputPtrsHandle.IsAllocated) _inputPtrsHandle.Free();
                    if (_outputPtrsHandle.IsAllocated) _outputPtrsHandle.Free();

                    _inputHandles = new GCHandle[channels];
                    _outputHandles = new GCHandle[channels];
                    _inputPtrs = new IntPtr[channels];
                    _outputPtrs = new IntPtr[channels];

                    // Pin the IntPtr arrays permanently — they are owned by this object
                    // and their address is passed to native code on every ProcessAudio call.
                    _inputPtrsHandle = GCHandle.Alloc(_inputPtrs, GCHandleType.Pinned);
                    _outputPtrsHandle = GCHandle.Alloc(_outputPtrs, GCHandleType.Pinned);

                    _preallocChannels = channels;
                }
            }
            return ok;
        }

        /// <summary>
        /// Processes audio data through the plugin
        /// </summary>
        /// <param name="inputs">Input audio data</param>
        /// <param name="outputs">Output audio data</param>
        /// <param name="numChannels">Number of channels</param>
        /// <param name="numSamples">Number of samples per channel</param>
        /// <returns>True if successful</returns>
        public bool ProcessAudio(float[][] inputs, float[][] outputs, int numChannels, int numSamples)
        {
            CheckDisposed();

            int actualIn = ActualInputChannels;
            int actualOut = ActualOutputChannels;

            int pluginChannels = Math.Min(numChannels, Math.Min(actualIn, actualOut));
            pluginChannels = Math.Min(pluginChannels, _preallocChannels);

            if (pluginChannels <= 0)
            {
                // Plugin has no audio buses at all – simple pass-through.
                int copyChannels = Math.Min(inputs.Length, outputs.Length);
                for (int ch = 0; ch < copyChannels; ch++)
                    inputs[ch].AsSpan(0, numSamples).CopyTo(outputs[ch]);
                return false;
            }

            var inputHandles = _inputHandles;
            var outputHandles = _outputHandles;

            for (int i = 0; i < pluginChannels; i++)
            {
                inputHandles[i] = GCHandle.Alloc(inputs[i], GCHandleType.Pinned);
                outputHandles[i] = GCHandle.Alloc(outputs[i], GCHandleType.Pinned);
                _inputPtrs[i] = inputHandles[i].AddrOfPinnedObject();
                _outputPtrs[i] = outputHandles[i].AddrOfPinnedObject();
            }

            AudioBufferC buffer = new AudioBufferC
            {
                inputs = _inputPtrsHandle.AddrOfPinnedObject(),
                outputs = _outputPtrsHandle.AddrOfPinnedObject(),
                numChannels = pluginChannels,
                numSamples = numSamples
            };

            bool result = _processAudioFunc(_pluginHandle, ref buffer);

            for (int i = 0; i < pluginChannels; i++)
            {
                inputHandles[i].Free();
                outputHandles[i].Free();
            }

            // Pass-through any extra channels that the plugin did not process.
            for (int ch = pluginChannels; ch < Math.Min(numChannels, outputs.Length); ch++)
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
        public bool SendMidiEvent(byte status, byte data1, byte data2)
        {
            return ProcessMidi(new[] { new MidiEvent { Status = status, Data1 = data1, Data2 = data2 } });
        }

        /// <summary>
        /// Processes MIDI events
        /// </summary>
        /// <param name="events">MIDI events</param>
        /// <returns>True if successful</returns>
        public bool ProcessMidi(MidiEvent[] events)
        {
            CheckDisposed();

            if (events == null || events.Length == 0)
                return false;

            MidiEventC[] eventsC = new MidiEventC[events.Length];

            for (int i = 0; i < events.Length; i++)
            {
                eventsC[i] = new MidiEventC
                {
                    status = events[i].Status,
                    data1 = events[i].Data1,
                    data2 = events[i].Data2,
                    sampleOffset = events[i].SampleOffset
                };
            }

            return _processMidiFunc(_pluginHandle, eventsC, events.Length);
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
                return _isMidiOnlyFunc?.Invoke(_pluginHandle) ?? false;
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
                return _isInstrumentFunc(_pluginHandle);
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
                return _isEffectFunc(_pluginHandle);
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
                return Marshal.PtrToStringAnsi(namePtr);
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
                return Marshal.PtrToStringAnsi(vendorPtr);
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
                return Marshal.PtrToStringAnsi(versionPtr);
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
                return Marshal.PtrToStringAnsi(infoPtr);
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
                return _getActualInputChannelsFunc?.Invoke(_pluginHandle) ?? 2;
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
                return _getActualOutputChannelsFunc?.Invoke(_pluginHandle) ?? 2;
            }
        }

        /// <summary>
        /// Sets the playback tempo forwarded to the plugin via ProcessContext
        /// </summary>
        public void SetTempo(double bpm)
        {
            CheckDisposed();
            _setTempoFunc?.Invoke(_pluginHandle, bpm);
        }

        /// <summary>
        /// Sets the transport playing state forwarded to the plugin via ProcessContext
        /// </summary>
        public void SetTransportState(bool isPlaying)
        {
            CheckDisposed();
            _setTransportStateFunc?.Invoke(_pluginHandle, isPlaying);
        }

        /// <summary>
        /// Resets the transport sample position counter (e.g. on Stop)
        /// </summary>
        public void ResetTransportPosition()
        {
            CheckDisposed();
            _resetTransportPositionFunc?.Invoke(_pluginHandle);
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
            _processIdleFunc?.Invoke(_pluginHandle);
        }

        /// <summary>
        /// Check if the editor window is currently open
        /// </summary>
        public bool IsEditorOpen
        {
            get
            {
                CheckDisposed();
                return _isEditorOpenFunc?.Invoke(_pluginHandle) ?? false;
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
#nullable restore
    }
}
