## OwnAudioVst 2.0.0 – JUCE-free native host

### Why
JUCE's license does not allow this project to be published under the MIT license while JUCE code is built into it. 2.0.0 removes JUCE completely, so the whole repository is MIT again.

### What changed
- **New native library (`OwnVST3Native`)** replaces the JUCE backend on every platform. It sits behind the unchanged C ABI (all 50 exports, same struct layouts and threading contract), so **the C# API is not changed**.
- **VST3**: built directly on the MIT-licensed VST3 SDK 3.8.1 (bus negotiation, RT-safe parameter changes, IComponentHandler2, IMidiMapping, ProcessContext, latency reporting, bypass).
- **AudioUnit (macOS)**: AUAudioUnit-based backend for v2 and v3 units.
- **State compatibility**: reads and writes the `VC2!` state container of 1.6/1.7 and the raw blobs of 1.5.x, so saved sessions keep loading.
- **Scanner**: `moduleinfo.json` fast scans for VST3; `plugin-cache.xml` stays compatible with the previous cache.
- **Editor windows** are owned by the native library on a dedicated UI thread (Win32 STA thread, macOS main thread, X11 loop on Linux).
- The C#-side `NativeWindow` classes are no longer used and are marked `[Obsolete]`.
- The shipped native binaries are much smaller (e.g. win-x64 DLL ~3.1 MB → ~0.4 MB, macOS dylib ~18.9 MB → ~1.3 MB).

### Fixes
- macOS: AudioUnits are created on the main thread (fixes popup menus that stayed open in some plugins); safer editor teardown; mouse-moved events in the editor.
- The native library is now found in single-file publishes and macOS app bundles.

### Other
- `THIRD_PARTY_NOTICES.md` (VST3 SDK, pugixml) is packed into the NuGet package.
- CI builds the JUCE-free library, runs native unit tests and C# API tests (against the VST3 SDK sample plugins), and fails if a shipped binary contains JUCE.
- The JUCE-based 1.7.0 tree is preserved on the `legacy/1.7.0` branch (not MIT-compatible).
