# Third-party notices

Optimus-Vibe (`OptimusVibe.exe`, `optimus-vibe.exe`) is MIT licensed (see [LICENSE](LICENSE)). The published exes are
self-contained builds and therefore include the components below. Each is used under the MIT License, whose text follows
the list. **This file should ship next to the exes in every release.**

| Component | Version | Copyright | License |
|---|---|---|---|
| .NET runtime, base class libraries and WPF (bundled by self-contained publish) | 8.0.x | (c) .NET Foundation and Contributors; Microsoft Corporation | MIT |
| YamlDotNet | 18.1.0 | (c) Antoine Aubry and contributors | MIT |
| System.CommandLine (CLI exe only) | 2.0.0 | (c) .NET Foundation and Contributors | MIT |
| System.Management | 8.0.0 | (c) .NET Foundation and Contributors; Microsoft Corporation | MIT |
| System.CodeDom (dependency of System.Management) | 8.0.0 | (c) .NET Foundation and Contributors; Microsoft Corporation | MIT |
| Sodium.Core (game lab: sealed-box encryption of the Actions secret) | 1.4.1 | (c) Adam Caudill, Tom Hotchkiss, Christian Weyer and contributors | MIT |
| libsodium (native library behind Sodium.Core, win-x64) | 1.0.22 | (c) Frank Denis and contributors | ISC |

Sources: https://github.com/dotnet/runtime, https://github.com/aaubry/YamlDotNet, https://github.com/dotnet/command-line-api.

## Not redistributed

- **NVIDIA driver / NVAPI.** Optimus-Vibe ships no NVIDIA code. When NVIDIA driver profiles are used, `nvapi64.dll` is loaded
  at run time from the user's own installed NVIDIA driver. The NVAPI function ids and driver-setting ids used to call it are
  taken from NVIDIA's NVAPI SDK headers (`nvapi_interface.h`, `NvApiDriverSettings.h`, https://github.com/NVIDIA/nvapi),
  Copyright (c) NVIDIA CORPORATION & AFFILIATES, MIT License. NVIDIA and NVAPI are trademarks of NVIDIA Corporation.
- **AMD and Windows APIs** are called from the user's installed drivers and OS; nothing is bundled.
- **Test and build-only packages** (xunit and xunit.*, Microsoft.NET.Test.Sdk, coverlet.collector, Newtonsoft.Json pulled in
  by the test SDK, Microsoft.NET.ILLink.Tasks, and their transitive packages) are used only to build and test. They are
  **not** in the published exes.

## MIT License text

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## ISC License text (libsodium)

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted,
provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
