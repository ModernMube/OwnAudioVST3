# OwnVst3 CSharp Wrapper

This library enables loading and managing VST3 plugins in C# applications using the native OwnVst3 wrapper DLL.

## Features

- Fully managed C# code
- Cross-platform compatibility using NativeLibrary.Load()
- Complete VST3 plugin functionality support:
  - Plugin loading and initialization
  - Editor view management
  - Parameter querying and modification
  - Audio processing
  - MIDI event handling
  - Plugin information retrieval
- Automatic memory management and resource disposal (IDisposable)

## Installation

1. [Download or build the `ownvst3.dll` or `libownvst3.dylib` or `libownvst3.so` file.](https://github.com/ModernMube/OwnVST3/releases)
2. Add the `OwnVst3Wrapper.cs` file to your project
3. Place the DLL in the same directory as your application executable, or provide its full path when using

## Quick Start

```csharp
// Create VST3 plugin wrapper instance
using (OwnVst3Wrapper vst = new OwnVst3Wrapper("ownvst3.dll"))
{
    // Load a VST3 plugin 
    if (vst.LoadPlugin("C:\\Plugins\\MyVst3Plugin.vst3"))
    {
        Console.WriteLine($"Plugin name: {vst.Name}");
        
        // Initialize the plugin
        vst.Initialize(44100.0, 512);
        
        // Query parameters
        var parameters = vst.GetAllParameters();
        foreach (var param in parameters)
        {
            Console.WriteLine($"{param.Name}: {param.CurrentValue}");
        }
        
        // Audio processing...
    }
}
```

## Usage Guide

### Loading a Plugin

```csharp
OwnVst3Wrapper vst = new OwnVst3Wrapper("path/to/ownvst3.dll");
bool success = vst.LoadPlugin("path/to/plugin.vst3");
```

### Initializing a Plugin

```csharp
bool success = vst.Initialize(44100.0, 512); // 44.1kHz, 512 sample block size
```

### Working with Parameters

```csharp
// Query all parameters
List<VST3Parameter> parameters = vst.GetAllParameters();

// Modify parameter by ID
vst.SetParameter(parameterId, 0.75);

// Query parameter value
double value = vst.GetParameter(parameterId);
```

### Audio Processing

```csharp
// Create 2-channel, 512-sample buffers
int numChannels = 2;
int numSamples = 512;
float[][] inputs = new float[numChannels][];
float[][] outputs = new float[numChannels][];

for (int c = 0; c < numChannels; c++)
{
    inputs[c] = new float[numSamples];
    outputs[c] = new float[numSamples];
    
    // Fill input data...
}

// Process audio
bool success = vst.ProcessAudio(inputs, outputs, numChannels, numSamples);
```

### Sending MIDI Events

```csharp
MidiEvent[] midiEvents = new MidiEvent[]
{
    new MidiEvent 
    { 
        Status = 0x90, // MIDI Note On, channel 1 
        Data1 = 60,    // C4 note
        Data2 = 100,   // Velocity
        SampleOffset = 0 
    }
};

bool success = vst.ProcessMidi(midiEvents);
```

### Disposing Resources

```csharp
// Automatic disposal in using block
using (OwnVst3Wrapper vst = new OwnVst3Wrapper("ownvst3.dll"))
{
    // ... use plugin
}

// Or manual disposal
vst.Dispose();
```

## Editor Management

```csharp
// Create editor in a window
IntPtr windowHandle = /* obtain window handle */;
bool success = vst.CreateEditor(windowHandle);

// Resize editor
vst.ResizeEditor(800, 600);

// Close editor
vst.CloseEditor();
```

## System Requirements

- .NET 6.0 or newer (required for NativeLibrary.Load API)
- The `ownvst3.dll` or `libownvst3.dylib` or `libownvst3.so` native library
- VST3 standard plugin files

## Troubleshooting

### DLL Not Found
- Verify that the DLL path is correct
- Place the DLL in the application directory
- Check that any required dependencies of the DLL are also installed

### Plugin Cannot Be Loaded
- Verify that the plugin path is correct
- Check that the plugin is in VST3 format
- Verify that the plugin is compatible with the OwnVst3 library

### Missing Functions
- Make sure the native DLL is the correct version that contains all the required exported functions

## Debugging Tips

The OwnVst3Wrapper throws detailed exceptions in the following cases:
- DllNotFoundException: The native DLL was not found
- EntryPointNotFoundException: A required function was not found in the DLL
- ArgumentOutOfRangeException: Invalid parameter index
- ObjectDisposedException: Attempt to use an already disposed wrapper instance
