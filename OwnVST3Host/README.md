# OwnVst3 C# Wrapper

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

1. Download or build the `ownvst3.dll` or `libownvst3.dylib` or `libownvst3.so` file
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