# OwnAudioVST3

<p align="center">
  <img src="Ownaudiologo.png" alt="OwnAudio Logo" width="600"/>
</p>

A thread-safe, cross-platform C# library for hosting VST3 plugins — and, on macOS, Audio Units as well. Built for audio applications where the UI thread, the audio thread, and the plugin's native runtime must never block each other. The native backend is a thin host built directly on the MIT-licensed [Steinberg VST 3 SDK](https://github.com/steinbergmedia/vst3sdk) and Apple's AudioToolbox — no JUCE, every dependency MIT-compatible (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).

[![NuGet Downloads](https://img.shields.io/nuget/dt/OwnAudioVst)](https://www.nuget.org/packages/OwnAudioVst)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

---

## Features

- **Thread-safe by design** — dedicated plugin thread, lock-free UI→audio parameter queue, atomic state machine
- **Full VST3 support** — instruments, effects, MIDI-only plugins, parameter automation, transport context
- **Audio Unit support on macOS and iOS** — AUv2 `.component` bundles and AUv3 extensions alongside VST3, through the same API; on iOS the plugin view goes into a `UIView` you own
- **Sub-second plugin scanner** — finds VST3 bundles *and* registry-only AudioUnits without loading them; opt into a full scan when you need channel counts
- **Cross-platform native editors** — the native library creates and owns the editor window: a dedicated STA thread with its own message loop on Windows, the main thread on macOS, an X11 event-loop thread on Linux
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

## Audio Units (macOS)

VST3 works exactly as it always has — passing a `.vst3` path to `LoadPluginAsync` is unchanged. On macOS the same call also accepts a `.component` path or an AudioUnit identifier, so both formats share one API.

AudioUnits cannot be found by walking directories: they live in the system's AudioComponent registry and are addressed by an identifier rather than a file path. That is what `PluginScanner` is for.

```csharp
// Fast scan — reads the AudioComponent registry and VST3 bundle names.
// Nothing is loaded, so this returns in a fraction of a second.
IReadOnlyList<PluginDescriptor> found = await PluginScanner.ScanAsync();

foreach (var p in found.Where(p => p.Format == PluginFormat.AudioUnit))
    Console.WriteLine($"{p.Name} — {p.Vendor} — {p.Identifier}");

// Load one — Identifier is a path for VST3, an "AudioUnit:..." token for AU.
await plugin.LoadPluginAsync(found[0]);
Console.WriteLine(await plugin.GetFormatAsync()); // PluginFormat.AudioUnit

// Channel counts are -1 after a fast scan; fill them in for one plugin on demand.
PluginDescriptor? full = await PluginScanner.ResolveAsync(found[0]);
```

A fast scan skips channel counts because getting them means loading the plugin. If you really need them for the whole list up front, ask for `ScanMode.Full` — that loads every plugin and takes minutes, so report progress and cache the result:

```csharp
var progress = new Progress<ScanProgress>(p =>
    Console.WriteLine($"{p.Fraction:P0} — {p.CurrentItem}"));

await PluginScanner.ScanAsync(PluginFormat.All, ScanMode.Full, progress);

File.WriteAllText(cacheFile, PluginScanner.GetCacheXml());
PluginScanner.RestoreCache(File.ReadAllText(cacheFile));
```

`OwnVst3Wrapper.FindVst3Plugins()` is untouched and still does the plain VST3 directory walk — use it when you only care about VST3.

> Note: AUv2 plugins are loaded in-process, so an Intel-only AudioUnit cannot be hosted by an ARM64 process. Under the hardened runtime the host needs the `com.apple.security.cs.disable-library-validation` entitlement, same as for third-party VST3 bundles.

---

## iOS (AUv3)

The package carries `ownvst3.xcframework` (device and simulator) and a `buildTransitive` targets file that embeds it into any `net10.0-ios` app — nothing to configure. VST3 does not exist on iOS; AudioUnits, including third-party AUv3 extensions, work exactly as on macOS:

```csharp
IReadOnlyList<PluginDescriptor> units = await PluginScanner.ScanAsync(PluginFormat.AudioUnit);

using var plugin = new ThreadedVst3Wrapper();
await plugin.LoadPluginAsync(units.First(u => u.Name == "AUDelay"));
await plugin.InitializeAsync(48000, 256);
```

There are no top-level editor windows on iOS, so `CreateEditor` returns false. Hand the plugin a view of your own instead (for example the native view behind an Avalonia `NativeControlHost`), on the UI thread:

```csharp
if (await plugin.HasEditorAsync())
    plugin.AttachEditorToView(myUIView.Handle);   // the plugin view fills it, a moment later
// ...
plugin.DetachEditor();
```

`AttachEditorToView` / `DetachEditor` work on macOS with an `NSView` too. Apple's built-in units have no view on iOS (`HasEditor` is false for them); third-party AUv3 run out of process, as iOS requires.

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

| Platform | Architecture | Formats | Window backend |
|---|---|---|---|
| Windows | x64, x86, ARM64 | VST3 | native STA thread + message loop, per-monitor DPI |
| macOS | x64, ARM64 | VST3, AudioUnit | main thread (GCD), never blocked |
| Linux | x64, ARM64 | VST3 | native X11 event-loop thread with `Linux::IRunLoop` |
| iOS / iPadOS 15+ | device ARM64, simulator ARM64 / x64 | AudioUnit (AUv3) | main thread; the editor is embedded in your own `UIView` |

Native libraries are resolved automatically from `runtimes/{rid}/native/` at runtime.

---

## Project Structure

```
OwnAudioVST3/
├── OwnVST3Host/          # Main library (C#)
│   ├── ThreadedVst3Wrapper.cs      # Primary API — thread-safe VST3 façade
│   ├── OwnVst3Wrapper.cs           # Low-level native wrapper + platform detection
│   ├── OwnVST3Formats.cs           # Format-aware extras (AU identifier / bundle sub-index)
│   ├── PluginDiscovery.cs          # PluginScanner, PluginDescriptor, PluginFormat
│   ├── LockFreeQueue.cs            # SPSC ring buffer (UI → audio thread)
│   └── NativeWindow/
│       ├── VstEditorController.cs  # Editor lifecycle manager
│       └── NativeWindow*.cs        # Legacy C#-side windows (unused since the native library owns the window)
├── OwnVST3Native/        # Native backend (C++, VST3 SDK + AudioToolbox, no JUCE)
│   ├── CMakeLists.txt              # Fetches VST3 SDK 3.8.1 + pugixml, builds libownvst3
│   ├── include/ownvst3_exports.h   # C ABI exported by the native library (frozen)
│   ├── src/
│   │   ├── Exports.cpp             # P/Invoke entry points
│   │   ├── core/                   # PluginInstance, IPluginBackend, StateCodec, queues, delay line
│   │   ├── vst3/                   # VST3 backend, host context, MIDI → VST3 / IMidiMapping
│   │   ├── au/                     # AudioUnit backend (macOS), component registry
│   │   ├── scan/                   # Scanner, moduleinfo.json, plugin-cache.xml
│   │   └── ui/                     # UI thread + editor window per platform (win32 / mac / linux)
│   └── tests/NativeTests.cpp       # Native unit tests
├── OwnVST3Tests/         # MSTest API tests + golden files recorded with the 1.7.0 build
├── runtimes/             # Pre-built native libraries per RID
│   ├── win-x64/native/ownvst3.dll
│   ├── win-x86/native/ownvst3.dll
│   ├── win-arm64/native/ownvst3.dll
│   ├── osx-arm64/native/libownvst3.dylib
│   ├── osx-x64/native/libownvst3.dylib
│   ├── linux-x64/native/libownvst3.so
│   └── linux-arm64/native/libownvst3.so
└── OwnVST3EditorDemo/    # Avalonia demo application
```

---

## Building from Source

```bash
# Clone the repository
git clone https://github.com/ModernMube/OwnVST3Sharp.git
cd OwnVST3Sharp

# Build the library
dotnet build OwnVST3Host/OwnVST3Host.csproj --configuration Release

# Run the demo
dotnet run --project OwnVST3EditorDemo
```

> **Native library**: Pre-built binaries for all platforms are included under `runtimes/`. To rebuild the native backend you need CMake 3.22+ and a C++17 compiler; the VST3 SDK and pugixml are fetched at configure time:
>
> ```bash
> cmake -S OwnVST3Native -B OwnVST3Native/build -DCMAKE_BUILD_TYPE=Release
> cmake --build OwnVST3Native/build --config Release
> ```
>
> Linux needs `libx11-dev`, nothing else.

---

## Testing

```bash
# Native unit tests (state codec, cache XML, MIDI mapping, delay line, queues)
cmake -S OwnVST3Native -B OwnVST3Native/build -DOWNVST3_BUILD_TESTS=ON
cmake --build OwnVST3Native/build --config Release
ctest --test-dir OwnVST3Native/build -C Release

# The VST3 SDK sample plugins the API tests load (MIT, built locally, VSTGUI off)
cmake -S OwnVST3Native/build/_deps/vst3sdk-src -B OwnVST3Native/build-plugins -DCMAKE_BUILD_TYPE=Release \
      -DSMTG_ENABLE_VSTGUI_SUPPORT=OFF -DSMTG_ENABLE_VST3_HOSTING_EXAMPLES=OFF -DSMTG_RUN_VST_VALIDATOR=OFF
cmake --build OwnVST3Native/build-plugins --config Release \
      --target adelay again-sample-accurate mda-vst3 sync-delay legacy-midicc-out utf16-name

# C# API tests against the library you just built
OWNVST3_NATIVE_LIB=$PWD/OwnVST3Native/build/libownvst3.dylib dotnet test OwnVST3Tests
```

`OwnVST3Tests/Golden/` holds what the JUCE-based 1.7.0 build reported for every sample plugin and a few Apple AudioUnits — names, parameter order and defaults, channels, latency, a rendered block, a saved state. The tests hold the current build to it. `OWNVST3_RECORD_GOLDEN=1` rewrites the files. CI runs all of this on Windows x64, macOS and Linux x64.

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
- Plugin discovery (VST3 directories, AudioUnit scanning) and platform detection
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

- Plugin hosting built on the [Steinberg VST 3 SDK](https://github.com/steinbergmedia/vst3sdk) (MIT) and [pugixml](https://pugixml.org/) (MIT)
- UI rendering powered by [Avalonia UI](https://avaloniaui.net/)
- Issues and contributions: [GitHub Issues](https://github.com/ModernMube/OwnVST3Sharp/issues)
