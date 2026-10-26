# Third-party notices

Vesper's code is its own, except as noted here.

## ard-rs

`src/Vesper.Core/Rfb/Decoders/MvsDecoder.cs` (Apple's adaptive codec, encoding 1011) is a C#
port of `crates/ard-core/src/mvs.rs` from ard-rs, and `tests/Vesper.Core.Tests/MvsTests.cs`
ports that project's MVS tests. ard-rs (<https://github.com/weidix/ard-rs>) is licensed
"MIT OR Apache-2.0"; Vesper uses it under the MIT license:

```
MIT License

Copyright (c) the ard-rs contributors

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

## remotex

Much of Vesper's understanding of Apple's protocol comes from remotex's measured description,
`docs/apple-vnc-889.md` (<https://github.com/andrewtheguy/remotex>), used under its MIT license:

```
MIT License

Copyright (c) 2026 Andrew Chen

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

## Packages

Vesper's NuGet dependencies (Windows App SDK, Win2D, Microsoft.DevTunnels.Ssh) are Microsoft's,
each under its own license, and are restored from nuget.org at build time.

The Linux app (`src/Vesper.Linux`) is built with Avalonia (<https://avaloniaui.net>, MIT license,
Copyright (c) AvaloniaUI OÜ), which brings SkiaSharp and HarfBuzzSharp (MIT, Copyright (c)
Microsoft Corporation; with Skia under the BSD 3-Clause license and HarfBuzz under its "Old MIT"
license), Tmds.DBus.Protocol (MIT) and MicroCom.Runtime (MIT). Its interface text is set in Inter
(<https://rsms.me/inter>), Copyright (c) 2016 The Inter Project Authors, used under the SIL Open
Font License 1.1 (<https://openfontlicense.org>), which travels inside the font's own metadata.
