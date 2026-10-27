## OwnAudioVst 2.1.0 – iOS (AUv3) support

### What's new
- **iOS / iPadOS 15+**: AudioUnits – Apple's built-in units and third-party AUv3 extensions (running out of process, as iOS requires) – through the same API as on macOS. VST3 stays desktop-only.
- **`ownvst3.xcframework`** (device arm64, simulator arm64 + x86_64) is packed into the NuGet package. A `buildTransitive` targets file embeds it into any `net10.0-ios` app, so there is nothing to configure.
- **Plugin editor inside your own view**: new `AttachEditorToView(view)` / `DetachEditor()` on `OwnVst3Wrapper` and `ThreadedVst3Wrapper` (native exports `OwnPlugin_AttachEditorToView` / `OwnPlugin_DetachEditor`). It takes a `UIView` on iOS – for example the one behind an Avalonia `NativeControlHost` – and an `NSView` on macOS. AudioUnits only for now.
- The 50 existing exports and the C# API are unchanged; everything new is additive.

### Fixes
- The `OwnVst3Wrapper` type initializer no longer marshals a delegate at run time, so it works where code runs AOT-only (iOS).
- `PluginScanner.AudioUnitSupported` is `true` on iOS.
- Apple's built-in units report no editor on iOS, because they offer none there.

### Other
- The NuGet readme covers iOS, the embedded-editor API and the current native library search order.
- CI builds the xcframework, checks it is JUCE-free and runs a smoke test in the iOS simulator; the API tests cover the two new exports.
- Verified in the iPad simulator with Apple's AUDelay and a third-party AUv3 whose SwiftUI editor was embedded in an app view.
