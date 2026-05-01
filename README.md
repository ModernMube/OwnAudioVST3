# OwnAudioVST3

<p align="center">
  <img src="Ownaudiologo.png" alt="OwnAudio Logo" width="600"/>
</p>

A thread-safe, cross-platform C# library for hosting VST3 plugins. Built for audio applications where the UI thread, the audio thread, and the plugin's native runtime must never block each other.

[![Build](https://github.com/ModernMube/OwnVST3Sharp/actions/workflows/build.yml/badge.svg)](https://github.com/ModernMube/OwnVST3Sharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/OwnVst3Host.svg)](https://www.nuget.org/packages/OwnVst3Host/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

---

## Features

- **Thread-safe by design** — dedicated plugin thread, lock-free UI→audio parameter queue, atomic state machine
- **Full VST3 support** — instruments, effects, MIDI-only plugins, parameter automation, transport context
- **Cross-platform native editors** — Win32 (STA), Cocoa (GCD main thread), X11 (dedicated event loop)
- **Zero allocation audio path** — pre-pinned buffers, no heap allocation inside `ProcessAudio`
- **Queryable plugin state** — `NotLoaded → Loaded → Ready ↔ Processing | Error`
- **Automatic platform detection** — native library resolved at runtime for `win-x64`, `osx-arm64`, `linux-x64` and more

---

## Installation

```bash
dotnet add package OwnVst3Host
```

---

## Quick Start

```csharp
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

// 1. Create the wrapper — starts the dedicated plugin thread immediately.
await using var plugin = new ThreadedVst3Wrapper();

// 2. Load and initialize on the plugin thread; the UI thread is never blocked.
bool loaded = await plugin.LoadPluginAsync("/Library/Audio/Plug-Ins/VST3/MyPlugin.vst3");
bool ready  = await plugin.InitializeAsync(sampleRate: 44100, maxBlockSize: 512);

if (!ready)
{
    Console.WriteLine($"State: {plugin.State}"); // VstPluginState.Error
    return;
}

// 3. Query plugin info (all async, all on plugin thread).
string name   = await plugin.GetNameAsync();
string vendor = await plugin.GetVendorAsync();
Console.WriteLine($"Loaded: {name} by {vendor}");

// 4. Real-time audio — call ProcessAudio directly from your audio thread.
//    It drains the UI→audio parameter queue before every block.
bool ok = plugin.ProcessAudio(inputs, outputs, numChannels: 2, numSamples: 512);

// 5. Change parameters from the UI thread — lock-free, applied on next audio block.
plugin.SetParameter(paramId: 0, value: 0.75);
plugin.SetTempo(bpm: 120.0);
plugin.SetTransportState(isPlaying: true);

// 6. Open the plugin editor on the UI thread (VST3 + OS requirement).
var editor = new VstEditorController(plugin);
await editor.OpenEditorAsync(name);
```

---

## Threading Model

```
UI Thread       ──── async/await ────▶  Plugin Thread   (Load, Init, GetParameter…)
UI Thread       ──── SPSC queue  ────▶  Audio Thread    (SetParameter, SetTempo…)
Audio Thread    ──── direct call ────▶  ProcessAudio    (no marshalling, no allocation)
UI Thread       ──── UI thread   ────▶  VstEditorController  (CreateEditor, CloseEditor)
```

All state transitions are atomic. `plugin.State` and `plugin.IsReady` can be read safely from **any thread** at any time.

| State | Meaning |
|---|---|
| `NotLoaded` | Initial / after `Dispose` |
| `Loaded` | `LoadPluginAsync` succeeded |
| `Ready` | `InitializeAsync` succeeded — audio processing possible |
| `Processing` | Inside `ProcessAudio` (audio thread) |
| `Error` | Fatal failure — replace the instance |

```csharp
// In your audio callback:
if (!plugin.IsReady) return;
plugin.ProcessAudio(inputs, outputs, channels, samples);
```

---

## Platform Support

| Platform | Architecture | Window backend |
|---|---|---|
| Windows | x64, x86 | Win32 STA thread + message loop |
| macOS | x64, ARM64 | Cocoa via GCD (`dispatch_sync` to main thread) |
| Linux | x64 | X11 dedicated event loop thread |

Native libraries are resolved automatically from `runtimes/{rid}/native/` at runtime.

---

## Project Structure

```
OwnAudioVST3/
├── OwnVST3Host/          # Main library
│   ├── ThreadedVst3Wrapper.cs      # Primary API — thread-safe VST3 façade
│   ├── OwnVst3Wrapper.cs           # Low-level native wrapper + platform detection
│   ├── LockFreeQueue.cs            # SPSC ring buffer (UI → audio thread)
│   └── NativeWindow/
│       ├── INativeWindow.cs        # Platform-agnostic window interface
│       ├── NativeWindowWindows.cs  # Win32 STA window with message loop
│       ├── NativeWindowMac.cs      # Cocoa/GCD main-thread marshalling
│       ├── NativeWindowLinux.cs    # X11 event loop thread
│       ├── NativeWindowFactory.cs  # Runtime platform selector
│       └── VstEditorController.cs  # Editor lifecycle manager
└── OwnVST3EditorDemo/    # Avalonia demo application
```

---

## Building from Source

```bash
# Clone the repository
git clone --recursive https://github.com/ModernMube/OwnVST3Sharp.git
cd OwnVST3Sharp

# Build the library
dotnet build OwnVST3Host/OwnVST3Host.csproj --configuration Release

# Run the demo
dotnet run --project OwnVST3EditorDemo
```

---

## Documentation

Full developer guide with API reference, threading rules, and code examples:

**[OwnVST3Host/README.md](OwnVST3Host/README.md)**

Topics covered:
- Architecture overview and threading diagram
- Plugin state machine reference
- Audio processing patterns (allocation-free path, channel handling)
- Parameter control: reading on plugin thread vs. writing via SPSC queue
- Transport and tempo integration
- MIDI scheduling with `SampleOffset`
- Editor lifecycle with Avalonia examples
- Plugin discovery and platform detection
- Best practices and common pitfalls

---

## Support My Work

If you find this project helpful, consider buying me a coffee!

<a href="https://www.buymeacoffee.com/ModernMube"
    target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/v2/arial-yellow.png"
    alt="Buy Me A Coffee"
    style="height: 60px !important;width: 217px !important;" >
</a>

---

## License

MIT License — see [LICENSE.txt](LICENSE.txt) for details.

## Acknowledgments

- Built on the [VST3 SDK](https://github.com/steinbergmedia/vst3sdk)
- UI rendering powered by [Avalonia UI](https://avaloniaui.net/)
- Issues and contributions: [GitHub Issues](https://github.com/ModernMube/OwnVST3Sharp/issues)
