# Third-Party Notices

LTOG as a whole is licensed under the **GNU General Public License v3.0** (see
[LICENSE](LICENSE)). The distributed binaries — both the `dist/` folder and the
installer produced from `installer/` — additionally include third-party
components under their own licenses, listed below. Full license texts are in the
[`licenses/`](licenses/) folder (and are shipped alongside the binaries by the
installer).

Nothing here restricts your rights under the GPL for the LTOG-original portions;
each third-party component is governed solely by its own license.

---

## LTFS engine — LGPL-2.1

`ltfs.exe`, `mkltfs.exe`, `unltfs.exe`, `ltfsck.exe`, `libltfs.dll`,
`libdriver-*.dll`, `libiosched-*.dll`, `libkmi-*.dll`

- **License:** GNU Lesser General Public License v2.1 — [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt)
- **Copyright:** © IBM Corporation; © Hewlett-Packard / HPE; © OSR Open Systems Resources, Inc.
- **Corresponding source:** these binaries are built by the
  [WinLtfs](https://github.com/rlaphoenix/WinLtfs) project, which holds the
  pristine upstream tree and the WinFsp-port patch series — both offered under
  LGPL-2.1 so they remain upstreamable. (LGPL-2.1 code may be conveyed as part of
  a GPLv3 work via LGPL §3.) Upstream origin:
  <https://github.com/leavelet/ltfs-hp>.

## WinFsp — GPL-3.0 with FLOSS exception

`winfsp-x64.dll` (in `dist/`) and `winfsp-2.1.25156.msi` (bundled by the
installer and run to install the signed kernel driver)

- **License:** GNU GPL v3.0 with the WinFsp FLOSS exception. The GPLv3 text is
  [LICENSE](LICENSE); the FLOSS exception (which permits combining WinFsp with
  software under other approved free-software licenses — this is what allows the
  LGPL-2.1 LTFS binaries to link the WinFsp FUSE layer) is published with the
  project.
- **Copyright:** © Bill Zissimopoulos.
- **Corresponding source:** <https://github.com/winfsp/winfsp> (release tag
  `v2.1`, version 2.1.25156 — the same version as the bundled DLL/MSI).

## GNU libiconv — LGPL-2.1-or-later

`libiconv-2.dll`

- **License:** GNU Lesser General Public License v2.1 or later — [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt)
- **Copyright:** © Free Software Foundation, Inc.
- **Source:** <https://www.gnu.org/software/libiconv/>

## GCC runtime libraries — GPL-3.0-or-later WITH GCC Runtime Library Exception

`libgcc_s_seh-1.dll`, `libstdc++-6.dll`

- **License:** GNU GPL v3.0 ([LICENSE](LICENSE)) **with** the GCC Runtime Library
  Exception v3.1 — [`licenses/GCC-Runtime-Library-Exception-3.1.txt`](licenses/GCC-Runtime-Library-Exception-3.1.txt).
  The exception grants permission to distribute these runtime libraries with
  independent (non-GPL) programs compiled by GCC, which is the case here.
- **Copyright:** © Free Software Foundation, Inc.
- **Source:** <https://gcc.gnu.org/>

## MinGW-w64 winpthreads — permissive (MIT / BSD / Zope-style)

`libwinpthread-1.dll`

- **License:** MinGW-w64 runtime license (permissive; portions MIT, portions
  BSD/Zope-style). The full text ships with the MinGW-w64 distribution and is
  reproduced in the project COPYING files.
- **Copyright:** © 2011-present, the MinGW-w64 project and contributors.
- **Source:** <https://www.mingw-w64.org/>

## libxml2 — MIT

`libxml2-16.dll`

- **License:** MIT License — [`licenses/libxml2-Copyright.txt`](licenses/libxml2-Copyright.txt)
- **Copyright:** © 1998-2012 Daniel Veillard. All Rights Reserved.
- **Source:** <https://gitlab.gnome.org/GNOME/libxml2>

## ICU — Unicode License v3

`libicuuc78.dll`, `libicudt78.dll`

- **License:** Unicode License v3 — [`licenses/ICU-LICENSE.txt`](licenses/ICU-LICENSE.txt)
- **Copyright:** © 1991-present Unicode, Inc. and others.
- **Source:** <https://github.com/unicode-org/icu>

## zlib — Zlib license

`zlib1.dll`

- **License:** zlib License — [`licenses/zlib-LICENSE.txt`](licenses/zlib-LICENSE.txt)
- **Copyright:** © 1995-present Jean-loup Gailly and Mark Adler.
- **Source:** <https://zlib.net/>

## Oxanium — SIL Open Font License 1.1

`Assets\Oxanium.ttf` (title-bar font)

- **License:** SIL OFL 1.1 — [`licenses/Oxanium-OFL.txt`](licenses/Oxanium-OFL.txt)
- **Copyright:** © 2019 The Oxanium Project Authors.
- **Source:** <https://github.com/sevmeyer/oxanium>

---

## GUI runtime — Microsoft components

The GUI (`dist/gui/`) is a self-contained Windows App SDK / WinUI 3 build. The
following Microsoft runtime files are redistributed as part of the application,
under the **Microsoft Software License Terms** that accompany each package
(redistribution with an application is permitted by those terms):

- **Windows App SDK / WinUI 3** — `Microsoft.WinUI.dll`, `Microsoft.UI.Xaml.*`,
  `Microsoft.ui.xaml.*`, `Microsoft.Windows.*.dll`, `Microsoft.WindowsAppRuntime.*`,
  `CoreMessagingXP.dll`, `DWriteCore.dll`, `DwmSceneI.dll`, `MRM.dll`, and the
  associated `.winmd`/`.pri` files. © Microsoft Corporation. License:
  <https://github.com/microsoft/WindowsAppSDK> / the package license terms.
- **Microsoft Edge WebView2** — `Microsoft.Web.WebView2.*`, `WebView2Loader.dll`.
  © Microsoft Corporation. Redistributable runtime/SDK.

(The Windows App SDK's AI/ML inference stack — ONNX Runtime, DirectML, and the
WinML helpers — is **trimmed out of the build**; LTOG uses no AI APIs, so those
components are not redistributed.)

### MIT-licensed Microsoft / .NET Foundation components

- **.NET 8 runtime (self-contained)** — the GUI is published self-contained, so
  the .NET runtime is bundled: `coreclr.dll`, `clrjit.dll`, `hostfxr.dll`,
  `hostpolicy.dll`, `System.Private.CoreLib.dll`, the `System.*` /
  `Microsoft.*` framework assemblies, etc. (`Microsoft.NETCore.App` 8.0). MIT
  License, © .NET Foundation and Contributors. Source:
  <https://github.com/dotnet/runtime>.
- **Other .NET libraries** — `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll`,
  `System.CodeDom.dll`, `System.Management.dll`, `System.Numerics.Tensors.dll`.
  MIT License, © .NET Foundation and Contributors.

---

## Runtime requirements (not bundled, not separate downloads)

The GUI is fully self-contained (the .NET and Windows App SDK runtimes are baked
in). The WinUI 3 binaries statically link the Microsoft C/C++ runtime and import
only the **Universal CRT** (`api-ms-win-crt-*`), which is part of Windows 10
1809+ — so **no Visual C++ Redistributable is bundled or required**. The only
runtime requirement is the operating system itself (Windows 10 1809 / build
17763 or newer), plus WinFsp for mounting (installed by the installer).
