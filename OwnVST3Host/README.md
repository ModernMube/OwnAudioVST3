# OwnVst3Host

A powerful, cross-platform C# wrapper for VST3 plugins with built-in visual editor support using Avalonia UI.

[![NuGet](https://img.shields.io/nuget/v/OwnVst3Host.svg)](https://www.nuget.org/packages/OwnVst3Host/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Features

- **🎹 Full VST3 Support** - Load and control VST3 instruments and effects with complete API coverage
- **🖥️ Cross-Platform Editors** - Display plugin UIs seamlessly on Windows, macOS, and Linux using Avalonia
- **🎛️ Parameter Control** - Get, set, and monitor plugin parameters programmatically
- **🎵 MIDI Support** - Send MIDI events to VST3 instruments
- **🔊 Real-Time Audio Processing** - Process audio buffers with native performance
- **🚀 Native Performance** - Uses optimized C++ library for minimal overhead
- **📦 Zero Configuration** - Automatic platform detection and native library loading

## Installation

Install via NuGet Package Manager:

```bash
dotnet add package OwnVst3Host
```

Or via Package Manager Console:

```powershell
Install-Package OwnVst3Host
```

## Quick Start

### Basic Plugin Loading

```csharp
using OwnVST3Host;

// Create wrapper instance (automatic platform detection)
var plugin = new OwnVst3Wrapper();

// Load a VST3 plugin
plugin.LoadPlugin("/path/to/plugin.vst3");

// Initialize audio engine
plugin.Initialize(sampleRate: 44100, bufferSize: 512);

// Get plugin information
Console.WriteLine($"Name: {plugin.Name}");
Console.WriteLine($"Vendor: {plugin.Vendor}");
Console.WriteLine($"Type: {(plugin.IsInstrument ? "Instrument" : "Effect")}");

// Clean up
plugin.Dispose();
```

### Opening Plugin Editor

```csharp
using OwnVST3Host.Extensions;

// Check if plugin has a visual editor
if (plugin.HasEditor())
{
    // Open editor in a new window
    var editorWindow = plugin.ShowEditor();
    
    // Check if editor is currently open
    bool isOpen = editorWindow.IsEditorActive;
}
```

### Working with Parameters

```csharp
// Get parameter count
int paramCount = plugin.GetParameterCount();

// Get parameter information
var paramInfo = plugin.GetParameterInfo(0);
Console.WriteLine($"{paramInfo.Name}: {paramInfo.CurrentValue}");

// Set parameter value (normalized 0.0 - 1.0)
plugin.SetParameterValue(0, 0.5);

// Get current parameter value
double value = plugin.GetParameterValue(0);
```

### Processing Audio

```csharp
// Prepare audio buffers
float[] inputBuffer = new float[512];
float[] outputBuffer = new float[512];

// Process audio
plugin.ProcessAudio(inputBuffer, outputBuffer, sampleCount: 512);
```

### Sending MIDI Events

```csharp
// Send a MIDI note on message
plugin.SendMidiEvent(
    status: 0x90,  // Note On
    data1: 60,     // Middle C
    data2: 100     // Velocity
);

// Send a MIDI note off message
plugin.SendMidiEvent(
    status: 0x80,  // Note Off
    data1: 60,     // Middle C
    data2: 0       // Velocity
);
```

### Finding VST3 Plugins

```csharp
// Get default VST3 directories for current platform
string[] directories = OwnVst3Wrapper.GetDefaultVst3Directories();

// Find all VST3 plugins in default locations
List<string> plugins = OwnVst3Wrapper.FindVst3Plugins();

foreach (string pluginPath in plugins)
{
    Console.WriteLine($"Found: {Path.GetFileName(pluginPath)}");
}

// Get diagnostic information
string info = OwnVst3Wrapper.GetVst3DirectoriesInfo();
Console.WriteLine(info);
```

## Platform Support

| Platform | Supported | Architectures |
|----------|-----------|---------------|
| Windows  | ✅ | x64, x86 |
| macOS    | ✅ | x64, ARM64 (Apple Silicon) |
| Linux    | ✅ | x64 |

Native libraries are automatically selected based on your runtime platform and architecture.

## API Reference

### Core Methods

| Method | Description |
|--------|-------------|
| `LoadPlugin(path)` | Load a VST3 plugin from the specified path |
| `Initialize(sampleRate, bufferSize)` | Initialize the audio processing engine |
| `ProcessAudio(input, output, samples)` | Process audio buffers |
| `Dispose()` | Release all resources and clean up |

### Editor Support

| Method | Description |
|--------|-------------|
| `HasEditor()` | Check if the plugin has a visual editor |
| `ShowEditor()` | Open the editor in a new window |
| `ShowEditor(owner)` | Open the editor as a child of the owner window |
| `IsEditorActive` | Check if the editor is currently open |

### Parameters

| Method | Description |
|--------|-------------|
| `GetParameterCount()` | Get the total number of parameters |
| `GetParameterValue(index)` | Get the current value of a parameter (0.0 - 1.0) |
| `SetParameterValue(index, value)` | Set a parameter value (0.0 - 1.0) |
| `GetParameterInfo(index)` | Get parameter metadata (name, units, etc.) |

### MIDI

| Method | Description |
|--------|-------------|
| `SendMidiEvent(status, data1, data2)` | Send a MIDI message to the plugin |
| `IsInstrument` | Check if the plugin is a MIDI instrument |

### Plugin Information

| Property | Description |
|----------|-------------|
| `Name` | Plugin name |
| `Vendor` | Plugin vendor/manufacturer |
| `Version` | Plugin version string |
| `IsInstrument` | Whether the plugin is an instrument |
| `IsEffect` | Whether the plugin is an effect |

## Requirements

- **.NET 9.0** or higher
- **Avalonia 11.2.3** (automatically included)

## License

This project is licensed under the MIT License. See the [LICENSE.txt](https://github.com/ModernMube/OwnVST3Sharp/blob/main/LICENSE.txt) file for details.

## Support

- **Issues & Questions**: [GitHub Issues](https://github.com/ModernMube/OwnVST3Sharp/issues)
- **Source Code**: [GitHub Repository](https://github.com/ModernMube/OwnVST3Sharp)
- **Support the Project**: [Buy Me a Coffee](https://www.buymeacoffee.com/ModernMube)

## Acknowledgments

- Built on top of the [VST3 SDK](https://github.com/steinbergmedia/vst3sdk)
- UI rendering powered by [Avalonia UI](https://avaloniaui.net/)