# OwnVST3Host — Developer Guide

A thread-safe, cross-platform C# library for hosting VST3 plugins. Designed to integrate cleanly into audio applications where the UI thread, the audio thread, and the plugin's native runtime must never interfere with each other.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Threading Model](#threading-model)
3. [Plugin State Machine](#plugin-state-machine)
4. [Quick Start](#quick-start)
5. [Loading and Initializing a Plugin](#loading-and-initializing-a-plugin)
6. [Audio Processing](#audio-processing)
7. [Parameter Control](#parameter-control)
8. [Transport and Tempo](#transport-and-tempo)
9. [MIDI](#midi)
10. [Plugin Editor UI](#plugin-editor-ui)
11. [Plugin Discovery](#plugin-discovery)
12. [Platform Support](#platform-support)
13. [API Reference](#api-reference)
14. [Best Practices](#best-practices)
15. [Common Pitfalls](#common-pitfalls)

---

## Architecture Overview

The library exposes two layers:

| Class | Role |
|---|---|
| `ThreadedVst3Wrapper` | **Primary API.** Thread-safe façade. All native VST3 operations run on a dedicated plugin thread. Audio callbacks run directly on the audio thread without marshalling. |
| `VstEditorController` | Manages the native OS window and the plugin's visual editor. Editor lifecycle (open/close) must stay on the UI thread. |
| `OwnVst3Wrapper` | Low-level native wrapper. Used internally. Exposes platform detection and plugin discovery as static helpers. Do not call from multiple threads directly. |

```
┌──────────────┐    async/await     ┌─────────────────────┐    native calls    ┌─────────────────┐
│   UI Thread  │ ────────────────▶  │ ThreadedVst3Wrapper │ ────────────────▶  │  Plugin Thread  │
│              │                    │                     │                    │ (LoadPlugin,    │
│ OpenEditor() │ ─── UI thread ───▶ │  VstEditorController│                    │  Initialize,    │
└──────────────┘                    └─────────────────────┘                    │  GetParameter…) │
                                             │                                 └─────────────────┘
┌──────────────┐   ProcessAudio()            │  SPSC queue
│ Audio Thread │ ──── direct ──────────────▶ │  (SetParameter,
│              │                             │   SetTempo…)
└──────────────┘                             ▼
                                    ┌─────────────────┐
                                    │   Audio Thread  │
                                    │  (DrainQueue,   │
                                    │   ProcessAudio) │
                                    └─────────────────┘
```

---

## Threading Model

This is the most important concept to understand before integrating the library.

### Four threads, four rules

| Thread | Responsibility | What you call here |
|---|---|---|
| **Plugin thread** | All native VST3 operations except audio | `LoadPluginAsync`, `InitializeAsync`, `GetNameAsync`, `GetParameterAsync`, … |
| **Audio thread** | Real-time audio and MIDI processing | `ProcessAudio`, `SendMidiEvent`, `ProcessMidi` |
| **UI thread** | Editor window lifecycle (VST3 + OS requirement) | `OpenEditorAsync`, `CloseEditor` |
| **Any thread** | Lock-free state changes and state queries | `SetParameter`, `SetTempo`, `SetTransportState`, `State`, `IsReady` |

### Key rule

> **Never call `ProcessAudio` from the UI or plugin thread. Never call `LoadPluginAsync` or `InitializeAsync` from the audio thread.**

The library enforces this through the state machine — `ProcessAudio` only executes when the plugin is in the `Ready` state, and it atomically transitions to `Processing` for the duration of each callback.

---

## Plugin State Machine

`ThreadedVst3Wrapper.State` is safe to read from **any thread** at any time. It uses `Volatile.Read` / `Interlocked` internally — no locks required.

```
NotLoaded ──▶ Loaded ──▶ Ready ◀──▶ Processing
    │             │          │
    └─────────────┴──────────┴──▶ Error
    ▲
  Dispose()
```

| State | Meaning |
|---|---|
| `NotLoaded` | No plugin loaded. Initial state and state after `Dispose()`. |
| `Loaded` | `LoadPluginAsync` succeeded. Plugin factory created, not yet initialized for audio. |
| `Ready` | `InitializeAsync` succeeded. Plugin is ready to process audio. |
| `Processing` | `ProcessAudio` is currently executing on the audio thread. |
| `Error` | `LoadPluginAsync` or `InitializeAsync` returned `false`. Create a new instance to retry. |

```csharp
// Reading state from any thread — safe and allocation-free
VstPluginState state = plugin.State;

// Convenience shortcut: Ready || Processing
bool canProcess = plugin.IsReady;

// React to state changes in the UI
if (plugin.State == VstPluginState.Error)
{
    ShowError("Plugin failed to initialize.");
    return;
}
```

---

## Quick Start

```csharp
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

// 1. Create the wrapper — starts the plugin thread immediately.
await using var plugin = new ThreadedVst3Wrapper();

// 2. Load and initialize — runs on the plugin thread, does not block the UI.
bool loaded = await plugin.LoadPluginAsync("/Library/Audio/Plug-Ins/VST3/MyPlugin.vst3");
if (!loaded) return;

bool initialized = await plugin.InitializeAsync(sampleRate: 44100, maxBlockSize: 512);
if (!initialized) return;

// 3. Query info — all async, all on plugin thread.
string name    = await plugin.GetNameAsync();
string vendor  = await plugin.GetVendorAsync();
bool isEffect  = await plugin.GetIsEffectAsync();

Console.WriteLine($"{name} by {vendor} — {(isEffect ? "Effect" : "Instrument")}");

// 4. Hand off to the audio engine. From here on, the audio thread calls ProcessAudio().
audioEngine.Start(plugin);
```

---

## Loading and Initializing a Plugin

### Basic load sequence

```csharp
var plugin = new ThreadedVst3Wrapper();

// LoadPlugin runs on the plugin thread. Await it — the UI thread is never blocked.
bool loaded = await plugin.LoadPluginAsync(pluginPath);

if (!loaded || plugin.State == VstPluginState.Error)
{
    Console.WriteLine("Failed to load plugin.");
    plugin.Dispose();
    return;
}

// State is now Loaded. Initialize the audio engine.
bool ready = await plugin.InitializeAsync(sampleRate: 48000, maxBlockSize: 256);

if (!ready || plugin.State != VstPluginState.Ready)
{
    Console.WriteLine("Failed to initialize plugin.");
    plugin.Dispose();
    return;
}

// Safe to hand off to the audio thread.
```

### Loading a specific native library

If you need to load from a custom path:

```csharp
string nativePath = "/opt/myapp/runtimes/osx-arm64/native/libownvst3.dylib";
var plugin = new ThreadedVst3Wrapper(nativePath);
```

### Re-loading a different plugin

`ThreadedVst3Wrapper` is single-use: one instance = one plugin. To load a different plugin, dispose and create a new instance.

```csharp
// Wrong — LoadPlugin on an already-loaded wrapper is not defined behavior.
// Correct:
await currentPlugin.DisposeAsync(); // or plugin.Dispose()
currentPlugin = new ThreadedVst3Wrapper();
await currentPlugin.LoadPluginAsync(newPluginPath);
```

---

## Audio Processing

`ProcessAudio` is the only method called directly from the **audio thread**. It is designed to be allocation-free and lock-free after the first call.

### Calling from the audio thread

```csharp
// In your audio engine callback — this runs on the audio thread.
void AudioCallback(float[][] inputs, float[][] outputs, int numSamples)
{
    // Guard: plugin might not be ready yet (loading in background).
    if (!plugin.IsReady)
    {
        // Pass-through silence
        foreach (var ch in outputs) Array.Clear(ch, 0, numSamples);
        return;
    }

    // ProcessAudio:
    //   1. Atomically transitions Ready → Processing.
    //   2. Drains the UI→Audio SPSC queue (SetParameter, SetTempo, …).
    //   3. Calls the native plugin's processing function.
    //   4. Transitions back to Ready.
    bool success = plugin.ProcessAudio(inputs, outputs, numChannels: 2, numSamples);

    if (!success)
    {
        // Plugin reported an error or was in wrong state.
        foreach (var ch in outputs) Array.Clear(ch, 0, numSamples);
    }
}
```

### What happens inside ProcessAudio

```
Audio thread enters ProcessAudio()
    │
    ├─ Check _disposed (volatile read) → return false if disposed
    ├─ CAS: Ready → Processing         → return false if not Ready
    ├─ Check _disposed again           → guard against concurrent Dispose
    ├─ DrainStateQueue()               → apply SetParameter / SetTempo / … from UI
    ├─ _inner.ProcessAudio(...)        → native plugin call (allocation-free)
    └─ CAS: Processing → Ready         → restore state
```

### Handling channel counts

The plugin declares its own channel layout after `Initialize`. The wrapper automatically clamps the call to the plugin's declared channel count and passes through any extra channels unchanged:

```csharp
// If your audio engine uses 8 channels but the plugin only handles stereo,
// channels 0-1 go through the plugin and channels 2-7 are passed through.
plugin.ProcessAudio(inputs, outputs, numChannels: 8, numSamples: 512);
```

---

## Parameter Control

### Reading parameters (plugin thread)

Parameter reads must go through the plugin thread because they call native code:

```csharp
// Get the full parameter list once after loading.
List<VST3Parameter> parameters = await plugin.GetAllParametersAsync();

foreach (var p in parameters)
{
    Console.WriteLine($"[{p.Id}] {p.Name}: {p.CurrentValue:F3} (range {p.MinValue}–{p.MaxValue})");
}

// Read a single parameter value.
double gain = await plugin.GetParameterAsync(paramId: 0);
```

### Setting parameters (any thread, lock-free)

`SetParameter` enqueues a change into a lock-free SPSC queue. The audio thread drains this queue at the start of every `ProcessAudio` call, applying the change with **~11 ms latency** at 44100/512.

```csharp
// Safe to call from the UI thread, a timer callback, or anywhere else.
// Returns immediately — does not wait for the audio thread.
plugin.SetParameter(paramId: 0, value: 0.75);
plugin.SetParameter(paramId: 1, value: 0.5);
```

> **Note:** The queue holds 512 pending changes. For typical UI interaction (sliders, knobs) this is never an issue. If the queue is ever full, the change is logged and dropped — this indicates a bug in the caller.

### Automating parameters at audio rate

For sample-accurate automation, send changes before each `ProcessAudio` call in the audio callback:

```csharp
void AudioCallback(float[][] inputs, float[][] outputs, int numSamples)
{
    // Apply automation value for this block.
    plugin.SetParameter(gainParamId, automationEnvelope.NextValue());

    // ProcessAudio drains the queue first, so the value above is applied
    // before the native plugin processes this block.
    plugin.ProcessAudio(inputs, outputs, 2, numSamples);
}
```

---

## Transport and Tempo

These are also lock-free SPSC queue operations — safe from any thread, applied on the audio thread.

```csharp
// Set BPM (forwarded to the plugin via ProcessContext on every block).
plugin.SetTempo(bpm: 120.0);

// Start / stop transport.
plugin.SetTransportState(isPlaying: true);
plugin.SetTransportState(isPlaying: false);

// Reset playhead to zero (e.g. on Stop).
plugin.ResetTransportPosition();
```

Typical transport integration with a play/stop UI:

```csharp
playButton.Click += (_, _) =>
{
    plugin.SetTransportState(true);
    plugin.SetTempo(currentBpm);
};

stopButton.Click += (_, _) =>
{
    plugin.SetTransportState(false);
    plugin.ResetTransportPosition();
};
```

---

## MIDI

MIDI methods are called **directly from the audio thread** — they do not go through the SPSC queue and have zero latency.

```csharp
// In your audio callback:

// Note On — Middle C, velocity 100
plugin.SendMidiEvent(status: 0x90, data1: 60, data2: 100);

// Note Off
plugin.SendMidiEvent(status: 0x80, data1: 60, data2: 0);

// Control Change — Sustain pedal on
plugin.SendMidiEvent(status: 0xB0, data1: 64, data2: 127);

// Program Change — bank 0, patch 5
plugin.SendMidiEvent(status: 0xC0, data1: 5, data2: 0);
```

### Sending multiple events in one block

```csharp
var events = new MidiEvent[]
{
    new() { Status = 0x90, Data1 = 60, Data2 = 80, SampleOffset = 0   },
    new() { Status = 0x90, Data1 = 64, Data2 = 80, SampleOffset = 128 },
    new() { Status = 0x90, Data1 = 67, Data2 = 80, SampleOffset = 256 },
};

plugin.ProcessMidi(events);
```

`SampleOffset` places each event at a precise sample position within the current block, enabling sample-accurate MIDI scheduling.

### MIDI-only plugins

Some plugins (arpeggiators, chord generators, MIDI effects) accept MIDI but produce no audio output.

```csharp
bool isMidiOnly = await plugin.GetIsMidiOnlyAsync();

if (isMidiOnly)
{
    // No audio processing needed.
    // Just drive the plugin with MIDI from the audio callback.
    plugin.SendMidiEvent(0x90, 60, 100);
}
```

---

## Plugin Editor UI

The editor must be opened and closed on the **UI thread**. This is a hard requirement imposed by VST3 and enforced by macOS (Cocoa) and Windows (STA COM).

### Opening the editor (async, preferred)

```csharp
// Runs entirely on the UI thread.
// GetEditorSize() is fetched asynchronously on the plugin thread;
// the native window is created and CreateEditor() is called on the UI thread.
var editorController = new VstEditorController(plugin);
await editorController.OpenEditorAsync("My Plugin");
```

### Opening the editor (synchronous, for non-async contexts)

```csharp
var editorController = new VstEditorController(plugin);
editorController.OpenEditor("My Plugin");
```

### Closing the editor

```csharp
if (editorController.IsOpen)
    editorController.CloseEditor();
```

### Full editor lifecycle example (Avalonia)

```csharp
private VstEditorController? _editor;

private async void OnOpenEditorClick(object? sender, RoutedEventArgs e)
{
    if (_editor?.IsOpen == true)
    {
        _editor.CloseEditor();
        openButton.Content = "Open Editor";
        return;
    }

    if (_editor == null)
        _editor = new VstEditorController(_plugin);

    string name = await _plugin.GetNameAsync();
    await _editor.OpenEditorAsync(name);

    openButton.Content = "Close Editor";
}

protected override void OnClosed(EventArgs e)
{
    _editor?.CloseEditor();
    _editor?.Dispose();
    _plugin?.Dispose();
    base.OnClosed(e);
}
```

### How the editor thread model works

```
UI Thread                  Plugin Thread            Native Window Thread (Win) / Main Thread (Mac)
    │                           │                              │
    ├─ await GetEditorSizeAsync()─▶ GetEditorSize()            │
    │◀──────────────── size ────────┤                          │
    ├─ NativeWindowFactory.Create() ──────────────────────────▶│
    │                               Open(title, w, h)          │  (Win: STA message loop)
    │◀───────────────────────────────────── handle ────────────┤  (Mac: dispatch_sync to main)
    ├─ CreateEditor(handle)  ← runs on UI thread (VST3 requirement)
    │
    └─ StartIdleThread() → ProcessIdle at 50 Hz (via BeginInvoke on window thread)
```

---

## Plugin Discovery

```csharp
// List all standard VST3 directories for the current OS.
string[] dirs = OwnVst3Wrapper.GetDefaultVst3Directories();

// Scan and return all found .vst3 bundles.
List<string> plugins = OwnVst3Wrapper.FindVst3Plugins();

// Scan specific directories only.
List<string> plugins = OwnVst3Wrapper.FindVst3Plugins(
    new[] { "/usr/lib/vst3", "~/.vst3" });

// Human-readable diagnostic output (useful for debugging).
Console.WriteLine(OwnVst3Wrapper.GetVst3DirectoriesInfo());
// Output:
//   Platform: osx-arm64
//   VST3 Plugin Directories:
//     [OK] /Library/Audio/Plug-Ins/VST3
//     [OK] /Users/yourname/Library/Audio/Plug-Ins/VST3
```

### Runtime / architecture detection

```csharp
string rid  = OwnVst3Wrapper.GetRuntimeIdentifier(); // "osx-arm64", "win-x64", "linux-x64"
string lib  = OwnVst3Wrapper.GetNativeLibraryName();  // "libownvst3.dylib", "ownvst3.dll", …
string path = OwnVst3Wrapper.GetNativeLibraryPath();  // full path to the loaded native library
```

---

## Platform Support

| Platform | Architecture | Window API | Notes |
|---|---|---|---|
| Windows | x64, x86 | Win32 (STA thread) | Full editor and audio support |
| macOS | x64, ARM64 | Cocoa (main thread via GCD) | Requires `[STAThread]` or Avalonia dispatcher |
| Linux | x64 | X11 (dedicated event thread) | Requires running X server; Wayland via XWayland |

### Native library search order

The library is searched in this order at runtime:

1. `runtimes/{rid}/native/` relative to the assembly location
2. `runtimes/{rid}/native/` relative to the current directory
3. Assembly directory (flat layout)
4. Current directory (flat layout)

---

## API Reference

### `ThreadedVst3Wrapper`

#### State

| Member | Thread | Description |
|---|---|---|
| `State` | Any | Current `VstPluginState`. Volatile read, zero allocation. |
| `IsReady` | Any | `true` when `State` is `Ready` or `Processing`. |
| `IsEditorOpen` | Any | `true` when the plugin's editor view is attached. |

#### Lifecycle (plugin thread)

| Method | Returns | Description |
|---|---|---|
| `LoadPluginAsync(path)` | `Task<bool>` | Load the plugin binary and create the factory. Sets state to `Loaded` or `Error`. |
| `InitializeAsync(sampleRate, maxBlockSize)` | `Task<bool>` | Start the audio engine. Sets state to `Ready` or `Error`. |
| `Dispose()` | — | Stop the plugin thread, free all native resources. |

#### Info queries (plugin thread)

| Method | Returns |
|---|---|
| `GetNameAsync()` | `Task<string>` |
| `GetVendorAsync()` | `Task<string>` |
| `GetVersionAsync()` | `Task<string?>` |
| `GetIsInstrumentAsync()` | `Task<bool>` |
| `GetIsEffectAsync()` | `Task<bool>` |
| `GetIsMidiOnlyAsync()` | `Task<bool>` |
| `GetPluginInfoAsync()` | `Task<string>` |
| `GetParameterCountAsync()` | `Task<int>` |
| `GetAllParametersAsync()` | `Task<List<VST3Parameter>>` |
| `GetParameterAtAsync(index)` | `Task<VST3Parameter>` |
| `GetParameterAsync(paramId)` | `Task<double>` |
| `GetEditorSizeAsync()` | `Task<EditorSize?>` |

#### Lock-free state changes (any thread → audio thread)

| Method | Latency |
|---|---|
| `SetParameter(paramId, value)` | ~1 block (~11 ms at 44100/512) |
| `SetTempo(bpm)` | ~1 block |
| `SetTransportState(isPlaying)` | ~1 block |
| `ResetTransportPosition()` | ~1 block |

#### Audio thread direct calls

| Method | Returns | Notes |
|---|---|---|
| `ProcessAudio(inputs, outputs, numChannels, numSamples)` | `bool` | Only valid when `IsReady`. Transitions state Ready↔Processing. |
| `SendMidiEvent(status, data1, data2)` | `bool` | Zero latency, no queue. |
| `ProcessMidi(MidiEvent[])` | `bool` | Zero latency, no queue. |

---

### `VstEditorController`

| Member | Thread | Description |
|---|---|---|
| `OpenEditorAsync(title)` | UI | Preferred. Async size fetch + sync editor attach. |
| `OpenEditor(title)` | UI | Synchronous variant. |
| `CloseEditor()` | UI | Detach editor and close native window. |
| `IsOpen` | Any | `true` if the native window is open. |
| `Dispose()` | UI | Closes editor and releases all OS window resources. |

---

### `VstPluginState` enum

```csharp
public enum VstPluginState
{
    NotLoaded,   // Initial / after Dispose
    Loaded,      // LoadPlugin succeeded, not yet initialized
    Ready,       // Initialize succeeded, audio processing possible
    Processing,  // Inside ProcessAudio (set/cleared by audio thread)
    Error        // LoadPlugin or Initialize failed — replace the instance
}
```

---

## Best Practices

### 1. Always check state before processing

```csharp
// In your audio callback:
if (!plugin.IsReady) return;
plugin.ProcessAudio(inputs, outputs, channels, samples);
```

### 2. Stop audio before Dispose

The `Dispose` method waits up to 100 ms for a running `ProcessAudio` call to complete before freeing native memory. To guarantee a clean shutdown, always stop the audio engine before calling `Dispose`:

```csharp
audioEngine.Stop();              // ensures no more ProcessAudio calls
await Task.Delay(50);            // optional: let any in-flight callback finish
plugin.Dispose();
```

### 3. One instance per plugin

`ThreadedVst3Wrapper` is not reusable. Create a new instance for each plugin, or when reloading the same plugin with different settings:

```csharp
plugin?.Dispose();
plugin = new ThreadedVst3Wrapper();
await plugin.LoadPluginAsync(newPath);
await plugin.InitializeAsync(sampleRate, blockSize);
```

### 4. Use `await using` for automatic cleanup

```csharp
await using var plugin = new ThreadedVst3Wrapper();
// ... plugin is disposed automatically when the scope exits
```

### 5. Check `Error` state after every async operation

```csharp
await plugin.LoadPluginAsync(path);
if (plugin.State == VstPluginState.Error) { /* handle */ return; }

await plugin.InitializeAsync(44100, 512);
if (plugin.State == VstPluginState.Error) { /* handle */ return; }
```

### 6. Match MIDI calls to the audio block

Send MIDI events **inside** the audio callback, not from the UI thread. The `SendMidiEvent` / `ProcessMidi` methods are audio-thread-only:

```csharp
// Wrong — calling from UI thread is not safe:
button.Click += (_, _) => plugin.SendMidiEvent(0x90, 60, 100);

// Correct — enqueue the note, send it in the audio callback:
button.Click += (_, _) => pendingNotes.Enqueue(new MidiEvent(0x90, 60, 100));

void AudioCallback(...)
{
    while (pendingNotes.TryDequeue(out var evt))
        plugin.SendMidiEvent(evt.Status, evt.Data1, evt.Data2);
    plugin.ProcessAudio(...);
}
```

---

## Common Pitfalls

### Calling `ProcessAudio` before `InitializeAsync`

`ProcessAudio` returns `false` silently when the plugin is not in `Ready` state. Always await `InitializeAsync` before starting the audio engine.

### Calling plugin thread methods from the audio callback

```csharp
// WRONG — GetParameterAsync posts to the plugin thread and blocks!
void AudioCallback(...)
{
    double val = await plugin.GetParameterAsync(0); // ← blocks audio thread!
}

// CORRECT — read parameters asynchronously from the UI and cache the result.
double cachedGain = 1.0;
// On plugin thread (before audio starts):
cachedGain = await plugin.GetParameterAsync(0);
// In audio callback:
ApplyGain(outputs, cachedGain);
```

### Opening the editor from a background thread

```csharp
// WRONG — CreateEditor must run on the UI thread (VST3 + OS requirement).
Task.Run(() => editorController.OpenEditor("Plugin")); // ← undefined behavior

// CORRECT — dispatch to the UI thread.
Dispatcher.UIThread.InvokeAsync(() => editorController.OpenEditor("Plugin"));
```

### Disposing while the audio thread is still running

```csharp
// WRONG — Dispose during active audio processing risks a crash.
plugin.Dispose(); // audio thread might still be inside ProcessAudio!

// CORRECT — stop audio first.
audioEngine.Stop();
plugin.Dispose();
```

### Expecting SetParameter changes to be instant

`SetParameter` targets the audio thread via the SPSC queue. The change takes effect on the **next audio block** (~11 ms at 44100/512). For UI feedback, maintain your own cached value:

```csharp
private double _gainValue;

void OnSliderChanged(double value)
{
    _gainValue = value;          // UI cache, instant
    plugin.SetParameter(0, value); // audio queue, ~11 ms
    gainLabel.Text = $"{value:F2}"; // update label from UI cache
}
```
