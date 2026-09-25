<h1 align="center"><img src="gui/Assets/icon.png" alt="" width="24" /> LTOG</h1>

<p align="center">
  <a href="https://github.com/rlaphoenix/LTOG/blob/main/LICENSE">
    <img src="https://img.shields.io/:license-GPL%203.0-blue.svg?style=for-the-badge" alt="License">
  </a>
  <img src="https://img.shields.io/badge/platform-Windows%207%2B-informational?style=for-the-badge" alt="Platform">
  <a href="https://github.com/microsoft/microsoft-ui-xaml">
    <img src="https://img.shields.io/badge/WinUI-3-blue?style=for-the-badge" alt="WinUI 3">
  </a>
  <a href="https://github.com/rlaphoenix/WinLtfs">
    <img src="https://img.shields.io/badge/WinLtfs-1.2.0-informational?style=for-the-badge" alt="WinLtfs 1.2.0">
  </a>
</p>

![Screenshot](screenshot.png)

LTOG is a modern LTO tape manager for Windows that lets you use tape like any other drive. Format a
cartridge with [LTFS 2.4](https://www.lto.org/ltfs/), mount it as a drive letter, and drag, drop, and
browse files in File Explorer without proprietary backup software. Tapes follow the open LTFS standard,
so they stay readable on any LTFS-compatible system. Tape mounting is powered by
[WinLtfs](https://github.com/rlaphoenix/WinLtfs).

> [!WARNING]
> It is highly recommended to Copy files instead of Moving files or risk losing your data. While every
> reasonable measure has been taken to prevent data loss, it can still occur in rare cases or with aging
> hardware. Tape drives have an internal memory buffer that stores written data before flushing it to the
> tape. When Moving files to the mount point LTOG creates, it gets written to this memory buffer that we
> cannot control. Once moved to the memory buffer, Windows marks the original file for deletion. If the
> Tape drive has an unexpected or intermittent error or failure while its still in the internal memory
> buffer, you may lose your data. Copying instead of moving prevents this issue as you will retain the
> original file on your computer. Only once you unmount the tape should you delete any original files.

## Features

- 🖥️ Native WinUI 3 GUI
- 📼 LTO-5+ Support
- 🗂️ LTFS 2.4.0 Support
- 💽 Virtual Drive Mounting
- 💾 Automatic Index Backup
- 🔒 Honors Write-Protection and Read-Only
- 🛡️ WHQL-signed Drivers
- ❤️ Forever FLOSS (GPLv3)

## Requirements

- Windows 10/11, or Windows Server 2019 or newer (64-bit only)
- An LTO-5 or newer cartridge with a compatible LTO Tape Drive

See [Supported Tape Drives](https://github.com/rlaphoenix/WinLtfs#supported-tape-drives)
for a list of tape drives that are known to be compatible.

## Building

First clone and enter the repository:

```shell
git clone https://github.com/rlaphoenix/LTOG
cd LTOG
```

Then build the LTFS+WinFsp engine, the GUI, and the Installer with `.\build`.
Instructions below show how to individually build each part of the project.

### 1. WinLtfs (LTFS + WinFsp)

The LTFS executables, tape backends, and `winfsp-x64.dll` are built by the
[WinLtfs](https://github.com/rlaphoenix/WinLtfs) project and consumed here as a
pinned release.

To build the engine from source yourself (MSYS2 + WinFsp toolchain), follow the
build instructions in the [WinLtfs](https://github.com/rlaphoenix/WinLtfs) repo,
then copy its `dist/` output into LTOG's `dist/winltfs/`.

### 2. GUI

Install the .NET 8 SDK:

```shell
winget install -e Microsoft.DotNet.SDK.8
```

In PowerShell:

```powershell
cd gui  # enter gui folder
dotnet build LTOG.Gui.csproj -c Release -p:Platform=x64  # build (self-contained)
robocopy "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64" "..\dist" /E  # self-contained output -> ..\dist
```

### 3. Installer

With `dist/` fully populated (LTFS engine + GUI), build the Windows installer:

```powershell
pwsh -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

It fetches what it needs and writes `setup.exe` to `installer\Output\`.

## Licensing

LTOG as a whole is distributed under the **GNU General Public License v3.0**
(see [LICENSE](LICENSE)).

A full per-component inventory — every redistributed binary, its license,
copyright, and corresponding source — is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), with the license texts in
[`licenses/`](licenses/). The installer ships these alongside the binaries.

---

© rlaphoenix 2026
