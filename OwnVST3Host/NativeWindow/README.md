# Native VST Editor Window Management

## Overview

This module provides native, Avalonia-free window management for displaying VST3 plugin editor interfaces on Windows, macOS, and Linux platforms.

## Main Components

### 1. INativeWindow Interface
Platform-independent abstraction for native window management.

### 2. Platform-Specific Implementations
- **NativeWindowWindows**: Uses Win32 API (User32.dll)
- **NativeWindowMac**: Uses Cocoa/Objective-C Runtime (libobjc.dylib)
- **NativeWindowLinux**: Uses X11 (libX11.so.6)

### 3. NativeWindowFactory
Factory class that automatically instantiates the appropriate platform-specific implementation.

### 4. VstEditorController
High-level API for managing VST editor windows.

## Usage

### Simple Example

```csharp
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

// Load VST3 plugin
var vst = new OwnVst3Wrapper();
vst.LoadPlugin("/path/to/plugin.vst3");
vst.Initialize(44100, 512);

// Create editor controller
var editorController = new VstEditorController(vst);

// Open editor
editorController.OpenEditor("My VST Plugin");

// ... user edits plugin parameters ...

// Close editor
editorController.CloseEditor();

// Cleanup
editorController.Dispose();
vst.Dispose();
```

### Detailed Example with Event Handlers

```csharp
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

var vst = new OwnVst3Wrapper();
vst.LoadPlugin("/path/to/plugin.vst3");
vst.Initialize(44100, 512);

var editorController = new VstEditorController(vst);

try
{
    // Open editor
    editorController.OpenEditor();
    
    Console.WriteLine("Editor opened. Press Enter to close...");
    Console.ReadLine();
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    // Always close the editor
    editorController.CloseEditor();
    editorController.Dispose();
    vst.Dispose();
}
```

### Low-Level Usage (Advanced)

If you want to use native windows directly:

```csharp
using OwnVST3Host.NativeWindow;

// Create native window
INativeWindow window = NativeWindowFactory.Create();

// Event handlers
window.OnResize += (width, height) => 
{
    Console.WriteLine($"Window resized: {width}x{height}");
};

window.OnClosed += () => 
{
    Console.WriteLine("Window closed");
};

// Open window
window.Open("Test Window", 800, 600);

// Get native handle
IntPtr handle = window.GetHandle();
Console.WriteLine($"Native handle: {handle}");

// Close
window.Close();
window.Dispose();
```

## API Reference

### VstEditorController

#### Constructor
```csharp
public VstEditorController(OwnVst3Wrapper vst3Wrapper)
```

#### Methods

**OpenEditor(string? title = null)**
- Opens the VST plugin editor window
- `title`: Window title (optional, defaults to plugin name)
- Throws: `InvalidOperationException` if the window is already open or the plugin doesn't support an editor

**CloseEditor()**
- Closes the VST plugin editor window
- Safe to call multiple times

#### Properties

**IsEditorOpen** (bool, readonly)
- Returns whether the editor window is open

### INativeWindow

#### Methods

**Open(string title, int width, int height)**
- Creates and displays the window

**Close()**
- Closes the window

**GetHandle()** → IntPtr
- Returns the platform-specific window handle
  - Windows: HWND
  - macOS: NSView*
  - Linux: Window ID

#### Properties

**IsOpen** (bool, readonly)
- Returns whether the window is open

#### Events

**OnResize** (Action<int, int>)
- Called when the window is resized
- Parameters: new width and height

**OnClosed** (Action)
- Called when the window is closed

## Platform-Specific Notes

### Windows
- Uses Win32 API
- Uses the "Static" window class (built-in, no registration needed)
- Applies subclassing for message handling

### macOS
- Uses Objective-C Runtime API via P/Invoke
- Creates NSWindow and NSView objects dynamically
- Returns NSView pointer for the VST plugin

### Linux
- Uses X11 library
- Opens its own X11 connection
- Provides basic event handling (resize, close)

## Advantages Over Avalonia-Based Solution

1. **No Conflicts**: Completely independent from Avalonia, doesn't clash with user application's Avalonia windows
2. **Native Performance**: Directly uses OS APIs
3. **Simple API**: Just two methods: `OpenEditor()` and `CloseEditor()`
4. **Small Footprint**: Minimal dependencies, only .NET runtime required
5. **VST3 Compatibility**: Returns exactly the handle type that VST3 plugins expect

## Known Limitations

1. **Event Handling**: Linux and macOS implementations currently don't handle all window events (e.g., resize event is not fully implemented)
2. **Message Pump**: On Windows, relies on Avalonia's main message loop
3. **Styling**: Windows have basic styling, no customization options

## Future Development Possibilities

- Full event handling implementation on all platforms
- Window style customization options
- Multiple window management simultaneously
- Window position save/restore
