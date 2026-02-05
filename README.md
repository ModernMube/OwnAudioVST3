# OwnAudioVST3

<p align="center">
  <img src="Ownaudiologo.png" alt="OwnAudio Logo" width="600"/>
</p>

A cross-platform C# wrapper for VST3 plugins with built-in visual editor support using Avalonia UI.

[![Build](https://github.com/ModernMube/OwnVST3Sharp/actions/workflows/build.yml/badge.svg)](https://github.com/ModernMube/OwnVST3Sharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/OwnVst3Host.svg)](https://www.nuget.org/packages/OwnVst3Host/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## Features

- **VST3 Plugin Loading** - Load and control VST3 instruments and effects
- **Cross-Platform Editors** - Display plugin UIs on Windows, macOS, and Linux
- **Parameter Control** - Get/set plugin parameters programmatically
- **MIDI Support** - Send MIDI events to instruments
- **Audio Processing** - Process audio buffers in real-time
- **Native Performance** - Uses native C++ library for optimal performance

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

```csharp
using OwnVST3Host;
using OwnVST3Host.Extensions;

// Load a VST3 plugin
var plugin = new OwnVst3Wrapper();
plugin.LoadPlugin("/path/to/plugin.vst3");
plugin.Initialize(44100, 512);

// Check if plugin has a visual editor
if (plugin.HasEditor())
{
    // Open the editor window
    var editorWindow = plugin.ShowEditor();
    
    // Check if editor is currently open
    bool isOpen = editorWindow.IsEditorActive;
}

// Get plugin information
Console.WriteLine($"Name: {plugin.Name}");
Console.WriteLine($"Vendor: {plugin.Vendor}");
Console.WriteLine($"Type: {(plugin.IsInstrument ? "Instrument" : "Effect")}");

// Process audio
plugin.ProcessAudio(inputBuffer, outputBuffer, sampleCount);

// Clean up
plugin.Dispose();
```

## API Overview

### Core Methods
- `LoadPlugin(path)` - Load a VST3 plugin
- `Initialize(sampleRate, bufferSize)` - Initialize the audio engine
- `ProcessAudio(input, output, samples)` - Process audio buffers
- `Dispose()` - Clean up resources

### Editor Support
- `HasEditor()` - Check if plugin has a visual editor
- `ShowEditor()` - Open editor in a new window
- `ShowEditor(owner)` - Open editor as child of owner window
- `IsEditorActive` - Check if editor is currently open

### Parameters
- `GetParameterCount()` - Get number of parameters
- `GetParameterValue(index)` - Get parameter value
- `SetParameterValue(index, value)` - Set parameter value
- `GetParameterInfo(index)` - Get parameter metadata

### MIDI
- `SendMidiEvent(status, data1, data2)` - Send MIDI message
- `IsInstrument` - Check if plugin is a MIDI instrument

## Platform Support

| Platform | Supported | Architecture |
|----------|-----------|--------------|
| Windows  | ✅ | x64, x86, ARM64 |
| macOS    | ✅ | x64, ARM64 (Universal) |
| Linux    | ✅ | x64, ARM64 |

## Building from Source

```bash
# Clone the repository with submodules
git clone --recursive https://github.com/ModernMube/OwnVST3Sharp.git
cd OwnVST3Sharp

# Build the solution
dotnet build OwnVST3Sharp.sln --configuration Release

# Run the demo
dotnet run --project OwnVST3EditorDemo
```

## Project Structure

- **OwnVST3Host** - Main library with VST3 wrapper and editor support
- **OwnVST3EditorDemo** - Example application demonstrating usage
- **OwnVST3** - Native C++ library (submodule)

## Support My Work

If you find this project helpful, consider buying me a coffee!

<a href="https://www.buymeacoffee.com/ModernMube" 
    target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png" 
    alt="Buy Me A Coffee" 
    style="height: 60px !important;width: 217px !important;" >
 </a>

## License

This project is licensed under the MIT License - see the [LICENSE.txt](LICENSE.txt) file for details.

## Acknowledgments

- Built on top of the VST3 SDK
- UI rendering powered by [Avalonia UI](https://avaloniaui.net/)

## Support

For issues, questions, or contributions, please open an issue on [GitHub](https://github.com/ModernMube/OwnVST3Sharp/issues).
