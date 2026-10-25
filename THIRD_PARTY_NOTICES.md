# Third-party notices

The OwnAudioVst package (the `OwnVST3Host` assembly and the native `ownvst3` library in
`runtimes/`) is MIT licensed. The native library contains code from the projects below, all
under MIT-compatible terms. Nothing in the package is under the AGPL or the JUCE licence:
since 2.0 the native host no longer uses JUCE.

| Component | Version | Licence | Used for |
|---|---|---|---|
| Steinberg VST 3 SDK (`pluginterfaces`, `base`, `public.sdk` hosting and moduleinfo sources) | 3.8.1 | MIT | loading and running VST3 plugins, `moduleinfo.json` |
| pugixml | 1.15 | MIT | plugin cache XML, VST3 state container XML |
| OwnVST3 host (ModernMube/OwnVST3, 6a838e3) | – | Unlicense (public domain) | starting point of the VST3 backend |
| Apple AudioToolbox, AVFoundation, CoreAudioKit, AppKit | system | Apple system frameworks, linked, not redistributed | AudioUnit hosting on macOS |

The VST3 SDK sample plugins (`adelay`, `again-sample-accurate`, `mda-vst3`, …) are built in CI
for the tests only. They are not part of the package.

---

## Steinberg VST 3 SDK

VST is a registered trademark of Steinberg Media Technologies GmbH.

```
MIT License

Copyright (c) 2026, Steinberg Media Technologies GmbH

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## pugixml

```
MIT License

Copyright (c) 2006-2025 Arseny Kapoulkine

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.
```

## OwnVST3 host (public domain)

The VST3 backend started from the earlier OwnVST3 host, released by its author under the
Unlicense (public domain dedication), with an MIT licence as an alternative choice. No notice
is required; it is listed here for provenance.
