<div align="center">
  <img src="Ownaudiologo.png" alt="Logó" width="600"/>
</div>

<a href="https://www.buymeacoffee.com/ModernMube">
  <img src="https://img.shields.io/badge/Support-Buy%20Me%20A%20Coffe-orange" alt="Buy Me a Coffe">
</a>

# OwnVst3 CSharp Wrapper

This library enables loading and managing VST3 plugins in C# applications using the native OwnVst3 wrapper DLL.

## Features

- Fully managed C# code
- Cross-platform compatibility (Windows, Linux, macOS) using NativeLibrary.Load()
- Automatic platform detection and native library loading
- Complete VST3 plugin functionality support:
  - Plugin loading and initialization
  - Editor view management with size querying
  - Parameter querying and modification
  - Audio processing
  - MIDI event handling
  - Plugin information retrieval (name, vendor, version, type)
- Built-in VST3 plugin discovery for platform-specific default directories
- Automatic memory management and resource disposal (IDisposable)

## Installation

1. [Download or build the `ownvst3.dll` or `libownvst3.dylib` or `libownvst3.so` file.](https://github.com/ModernMube/OwnVST3/releases)
2. Add the `OwnVST3Host` project or reference its DLL in your project
3. Place the native library in one of the following locations:
   - `runtimes/{rid}/native/` folder (recommended for NuGet-style deployment)
   - Same directory as your application executable
   - Or provide its full path when using the constructor

## Quick Start

```csharp
using OwnVST3Host;

// Create VST3 plugin wrapper instance (auto-detects platform and library path)
using (OwnVst3Wrapper vst = new OwnVst3Wrapper())
{
    // Load a VST3 plugin
    if (vst.LoadPlugin("C:\\Plugins\\MyVst3Plugin.vst3"))
    {
        Console.WriteLine($"Plugin name: {vst.Name}");
        Console.WriteLine($"Vendor: {vst.Vendor}");
        Console.WriteLine($"Version: {vst.Version}");
        Console.WriteLine($"Is Instrument: {vst.IsInstrument}");

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
using OwnVST3Host;

// Option 1: Auto-detect native library (recommended)
OwnVst3Wrapper vst = new OwnVst3Wrapper();

// Option 2: Specify custom path
OwnVst3Wrapper vst = new OwnVst3Wrapper("path/to/ownvst3.dll");

bool success = vst.LoadPlugin("path/to/plugin.vst3");
```

### Finding VST3 Plugins

```csharp
// Get default VST3 directories for current platform
string[] directories = OwnVst3Wrapper.GetDefaultVst3Directories();

// Find all VST3 plugins in default directories
List<string> plugins = OwnVst3Wrapper.FindVst3Plugins();

// Find plugins in specific directories
List<string> plugins = OwnVst3Wrapper.FindVst3Plugins(new[] { "C:\\MyPlugins" }, includeSubdirectories: true);

// Get diagnostic info about VST3 directories
Console.WriteLine(OwnVst3Wrapper.GetVst3DirectoriesInfo());
```

### Initializing a Plugin

```csharp
bool success = vst.Initialize(44100.0, 512); // 44.1kHz, 512 sample block size
```

### Working with Parameters

```csharp
// Get parameter count
int count = vst.GetParameterCount();

// Get a specific parameter by index
VST3Parameter param = vst.GetParameterAt(0);
Console.WriteLine($"ID: {param.Id}, Name: {param.Name}");
Console.WriteLine($"Range: {param.MinValue} - {param.MaxValue}");
Console.WriteLine($"Default: {param.DefaultValue}, Current: {param.CurrentValue}");

// Query all parameters
List<VST3Parameter> parameters = vst.GetAllParameters();

// Modify parameter by ID
vst.SetParameter(parameterId, 0.75);

// Query parameter value by ID
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
using (OwnVst3Wrapper vst = new OwnVst3Wrapper())
{
    // ... use plugin
}

// Or manual disposal
OwnVst3Wrapper vst = new OwnVst3Wrapper();
// ... use plugin
vst.Dispose();
```

## Editor Management

```csharp
// Get the plugin's preferred editor size
EditorSize? size = vst.GetEditorSize();
if (size.HasValue)
{
    Console.WriteLine($"Preferred size: {size.Value.Width}x{size.Value.Height}");
}

// Or using out parameters
if (vst.GetEditorSize(out int width, out int height))
{
    Console.WriteLine($"Preferred size: {width}x{height}");
}

// Create editor in a window
IntPtr windowHandle = /* obtain window handle */;
bool success = vst.CreateEditor(windowHandle);

// Resize editor
vst.ResizeEditor(800, 600);

// Close editor
vst.CloseEditor();
```

## Plugin Information

```csharp
// Get plugin metadata
Console.WriteLine($"Name: {vst.Name}");
Console.WriteLine($"Vendor: {vst.Vendor}");
Console.WriteLine($"Version: {vst.Version}");
Console.WriteLine($"Full Info: {vst.PluginInfo}");

// Check plugin type
if (vst.IsInstrument)
    Console.WriteLine("This is a virtual instrument (VSTi)");
if (vst.IsEffect)
    Console.WriteLine("This is an audio effect");

// Clear cached strings (for memory management)
vst.ClearStringCache();
```

## Platform Utilities

```csharp
// Get runtime identifier (e.g., "win-x64", "linux-x64", "osx-arm64")
string rid = OwnVst3Wrapper.GetRuntimeIdentifier();

// Get native library name for current platform
string libName = OwnVst3Wrapper.GetNativeLibraryName();
// Returns: "ownvst3.dll" (Windows), "libownvst3.so" (Linux), "libownvst3.dylib" (macOS)

// Get full path to native library (with automatic search)
string libPath = OwnVst3Wrapper.GetNativeLibraryPath();
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
- InvalidOperationException: Failed to create VST3 plugin instance
- PlatformNotSupportedException: Unsupported operating system or architecture
- ArgumentOutOfRangeException: Invalid parameter index
- ObjectDisposedException: Attempt to use an already disposed wrapper instance

## Support My Work

If you find this project helpful, consider buying me a coffee!

<a href="https://www.buymeacoffee.com/ModernMube" 
    target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" 
    alt="Buy Me A Coffee" 
    style="height: 60px !important;width: 217px !important;" >
 </a>
