using System;
using System.Collections.Generic;
using System.IO;
using OwnVST3Host;

namespace OwnVst3SampleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("OwnVst3 C# Sample Program");
            Console.WriteLine("------------------------");

            try
            {
                // Display detected platform info
                Console.WriteLine($"Runtime Identifier: {OwnVst3Wrapper.GetRuntimeIdentifier()}");
                Console.WriteLine($"Native Library: {OwnVst3Wrapper.GetNativeLibraryName()}");
                Console.WriteLine();

                // Display VST3 directory information
                Console.WriteLine(OwnVst3Wrapper.GetVst3DirectoriesInfo());
                Console.WriteLine();

                // Create VST3 plugin wrapper instance with automatic platform detection
                // The library is loaded from: runtimes/{rid}/native/
                using (OwnVst3Wrapper vst = new OwnVst3Wrapper())
                {
                    Console.WriteLine("VST3 wrapper successfully initialized.");

                    // Get plugin path - either from command line, auto-discovery, or user input
                    string? pluginPath = GetPluginPath(args);

                    if (string.IsNullOrEmpty(pluginPath))
                    {
                        Console.WriteLine("No VST3 plugin specified or found. Exiting.");
                        return;
                    }

                    Console.WriteLine($"Loading VST3 plugin: {pluginPath}");

                    if (vst.LoadPlugin(pluginPath))
                    {
                        Console.WriteLine("Plugin loaded successfully!");
                        Console.WriteLine($"Name: {vst.Name}");
                        Console.WriteLine($"Is Instrument: {vst.IsInstrument}");
                        Console.WriteLine($"Is Effect: {vst.IsEffect}");

                        // Initialize the plugin
                        double sampleRate = 44100.0;
                        int blockSize = 512;
                        if (vst.Initialize(sampleRate, blockSize))
                        {
                            Console.WriteLine($"Plugin initialized: {sampleRate} Hz, {blockSize} samples/block");

                            // Query parameters
                            var parameters = vst.GetAllParameters();
                            Console.WriteLine($"\nNumber of parameters: {parameters.Count}");

                            if (parameters.Count > 0)
                            {
                                Console.WriteLine("\nParameters:");
                                Console.WriteLine("ID\tName\t\tCurrent\tMin\tMax\tDefault");
                                Console.WriteLine("------------------------------------------------------------------");

                                foreach (var param in parameters)
                                {
                                    Console.WriteLine($"{param.Id}\t{param.Name}\t\t{param.CurrentValue:F2}\t{param.MinValue:F2}\t{param.MaxValue:F2}\t{param.DefaultValue:F2}");
                                }

                                // Modify parameter example (on first parameter)
                                if (parameters.Count > 0)
                                {
                                    var firstParam = parameters[0];
                                    double newValue = (firstParam.MaxValue + firstParam.MinValue) / 2; // Mid value

                                    Console.WriteLine($"\nModifying parameter '{firstParam.Name}': {firstParam.CurrentValue:F2} -> {newValue:F2}");

                                    if (vst.SetParameter(firstParam.Id, newValue))
                                    {
                                        double actualValue = vst.GetParameter(firstParam.Id);
                                        Console.WriteLine($"Parameter modified successfully. Current value: {actualValue:F2}");
                                    }
                                    else
                                    {
                                        Console.WriteLine("Failed to modify parameter.");
                                    }
                                }
                            }

                            // E.g., 2 channels, 512 samples
                            int numChannels = 2;
                            int numSamples = blockSize;

                            // Create input/output buffers
                            float[][] inputs = new float[numChannels][];
                            float[][] outputs = new float[numChannels][];

                            if(vst.IsEffect)
                            {
                                // Audio processing example
                                Console.WriteLine("\nSimulating audio processing...");

                                for (int c = 0; c < numChannels; c++)
                                {
                                    inputs[c] = new float[numSamples];
                                    outputs[c] = new float[numSamples];

                                    // Generate test input data (sine wave)
                                    for (int i = 0; i < numSamples; i++)
                                    {
                                        inputs[c][i] = (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 0.5f;
                                    }
                                }

                                // Process audio
                                bool processResult = vst.ProcessAudio(inputs, outputs, numChannels, numSamples);
                                Console.WriteLine($"Audio processing result: {(processResult ? "Success" : "Failed")}");

                            }
                            else
                            {
                                // MIDI example - play C4 note
                                Console.WriteLine("\nSending MIDI event (C4 note)...");

                                byte noteOn = 0x90;  // MIDI Note On, channel 1
                                byte noteC4 = 60;    // C4 note (middle C)
                                byte velocity = 100; // Velocity

                                MidiEvent[] midiEvents = new MidiEvent[]
                                {
                                new MidiEvent { Status = noteOn, Data1 = noteC4, Data2 = velocity, SampleOffset = 0 }
                                };

                                bool midiResult = vst.ProcessMidi(midiEvents);
                                Console.WriteLine($"MIDI processing result: {(midiResult ? "Success" : "Failed")}");

                                // Process audio after MIDI
                                vst.ProcessAudio(inputs, outputs, numChannels, numSamples);

                                // Turn off the MIDI note
                                Console.WriteLine("Turning off MIDI note...");

                                byte noteOff = 0x80;  // MIDI Note Off, channel 1
                                byte releaseVel = 0;  // Release velocity

                                midiEvents = new MidiEvent[]
                                {
                                new MidiEvent { Status = noteOff, Data1 = noteC4, Data2 = releaseVel, SampleOffset = 0 }
                                };

                                vst.ProcessMidi(midiEvents);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Failed to initialize plugin.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("Failed to load plugin!");
                    }
                }
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"ERROR: Failed to load DLL: {ex.Message}");
            }
            catch (EntryPointNotFoundException ex)
            {
                Console.WriteLine($"ERROR: Function not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.GetType().Name} - {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        /// <summary>
        /// Gets the VST3 plugin path from command line args, auto-discovery, or user input.
        /// </summary>
        static string? GetPluginPath(string[] args)
        {
            // 1. Check command line arguments
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
            {
                string argPath = args[0];
                if (File.Exists(argPath) || Directory.Exists(argPath))
                {
                    return argPath;
                }
                Console.WriteLine($"Warning: Specified path not found: {argPath}");
            }

            // 2. Try auto-discovery
            Console.WriteLine("Searching for VST3 plugins...");
            var plugins = OwnVst3Wrapper.FindVst3Plugins();

            if (plugins.Count > 0)
            {
                Console.WriteLine($"\nFound {plugins.Count} VST3 plugin(s):");
                for (int i = 0; i < Math.Min(plugins.Count, 20); i++)
                {
                    Console.WriteLine($"  [{i + 1}] {Path.GetFileName(plugins[i])}");
                    Console.WriteLine($"      {plugins[i]}");
                }

                if (plugins.Count > 20)
                {
                    Console.WriteLine($"  ... and {plugins.Count - 20} more");
                }

                Console.WriteLine();
                Console.Write("Enter plugin number to load (or 'q' to quit): ");
                string input = Console.ReadLine() ?? "";

                if (input.ToLower() == "q" || string.IsNullOrWhiteSpace(input))
                {
                    return null;
                }

                if (int.TryParse(input, out int selection) && selection >= 1 && selection <= plugins.Count)
                {
                    return plugins[selection - 1];
                }

                Console.WriteLine("Invalid selection.");
                return null;
            }

            // 3. No plugins found - ask for manual path
            Console.WriteLine("No VST3 plugins found in default directories.");
            Console.Write("Enter full path to a VST3 plugin (or press Enter to quit): ");
            string manualPath = Console.ReadLine() ?? "";

            if (!string.IsNullOrWhiteSpace(manualPath) && (File.Exists(manualPath) || Directory.Exists(manualPath)))
            {
                return manualPath;
            }

            return null;
        }
    }
}