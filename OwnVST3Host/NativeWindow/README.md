# VST Editor Windows

## Overview

`VstEditorController` opens and closes a plugin's editor. The window itself is created and
owned by the native library, never by C#:

| Platform | Who owns the window | Thread |
|---|---|---|
| Windows | native top-level window (+ child container the plugin attaches to), owned by the HWND you pass, if any | a native STA thread with its own `GetMessage` loop, per-monitor DPI aware |
| macOS | `NSWindow` + content `NSView` (AU: the unit's view controller, or Apple's generic view) | the main thread; the native side marshals there with GCD and never blocks it |
| Linux | X11 top-level window, `Linux::IRunLoop` offered to the plugin | a native thread running the X11 / fd / timer loop |

Plugin idle, timers and resizing are all handled on that thread. `ProcessIdle()` is kept for
compatibility and does nothing.

## Usage

```csharp
using OwnVST3Host;
using OwnVST3Host.NativeWindow;

await using var plugin = new ThreadedVst3Wrapper();
await plugin.LoadPluginAsync("/path/to/plugin.vst3");
await plugin.InitializeAsync(44100, 512);

if (await plugin.HasEditorAsync())
{
    var editor = new VstEditorController(plugin);
    await editor.OpenEditorAsync("My Plugin");

    // ... later
    editor.CloseEditor();
    editor.Dispose();
}
```

The synchronous `OwnVst3Wrapper` works the same way through `new VstEditorController(vst)` and
`OpenEditor(title)`.

## API

**`OpenEditorAsync(string title)` / `OpenEditor(string title)`**
Opens the editor. Throws `InvalidOperationException` if the plugin has no editor or the window
could not be created. Calling it while open does nothing.

**`CloseEditor()`**
Closes the window and releases the plugin view. Safe to call repeatedly.

**`IsEditorOpen` / `IsOpen`**
Whether this controller opened the editor. `OwnVst3Wrapper.IsEditorOpen` asks the native side
and also turns false when the user closes the window with its title-bar button.

## Rules the native side follows

1. The native code creates and owns the window; the handle passed to `CreateEditor` is at most an
   owner to stay in front of, never an embedding target.
2. One UI thread owns everything belonging to the plugin and its window.
3. On macOS the main thread is never made to wait for another thread, and every timer runs in
   the common run-loop modes, so menus tracked by a plugin never freeze it.
4. On Windows the content scale is set after `attached()`, and the window is shown only after
   the view is attached.

Without a running main loop on macOS (a console app or a test runner calling from a worker
thread) editors are not available and `CreateEditor` returns false; everything else works.

## Legacy classes

`INativeWindow`, `NativeWindowFactory` and the `NativeWindowWindows` / `Mac` / `Linux`
implementations created editor windows from C# before 1.6. Nothing uses them any more; they are
marked `[Obsolete]` and will be removed in a later major version.
