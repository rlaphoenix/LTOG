<#
.SYNOPSIS
    One-shot clean build of LTOG: the WinLtfs native engine, the WinUI 3 GUI,
    and the Windows installer.

.DESCRIPTION
    Wipes previous build output (so nothing stale survives), then assembles, in order:

      1. WinLtfs native engine      -> dist\winltfs\  (downloaded, pinned release zip)
      2. Self-contained WinUI 3 GUI -> dist\          (dotnet build, .NET baked in)
      3. Windows installer          -> installer\Output\  (installer\build-installer.ps1)

    The licenses shipped in the WinLtfs release are kept at dist\licenses.

    The LTFS engine + WinFsp port (ltfs.exe, backends, winfsp-x64.dll) is built by
    the WinLtfs project (https://github.com/rlaphoenix/WinLtfs); LTOG bundles a
    pinned, checksum-verified release of it. No MSYS2/WinFsp toolchain needed here.

    Run from a normal PowerShell prompt (no elevation needed):

        pwsh -File build.ps1

    Build prerequisites (the script checks for these and fails clearly if missing):
      * Internet access to fetch the pinned WinLtfs release - unless -SkipNative
      * .NET 8 SDK
      * Inno Setup 6.3+ (build-installer.ps1 can install it via winget)

.PARAMETER SkipNative
    Reuse the WinLtfs binaries already in dist\winltfs\ and skip the download. Use
    when iterating on the GUI/installer. Fails if dist\winltfs\ltfs.exe is absent.

.PARAMETER NoInstaller
    Build the engine and GUI but stop before the installer.

.PARAMETER Version
    Version embedded into the GUI assembly and installer.

.EXAMPLE
    pwsh -File build.ps1                 # full clean build of everything

.EXAMPLE
    pwsh -File build.ps1 -SkipNative     # rebuild only the GUI + installer (reuse native)
#>
[CmdletBinding()]
param(
    [switch]$SkipNative,
    [switch]$NoInstaller,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.1.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root         = $PSScriptRoot
$Dist         = Join-Path $Root 'dist'
$DistWinLtfs  = Join-Path $Dist 'winltfs'   # native engine lives in a subfolder
$DistLicenses = Join-Path $Dist 'licenses'
$GuiDir       = Join-Path $Root 'gui'
$InstallerDir = Join-Path $Root 'installer'
$GuiOutSub    = 'bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'   # self-contained output

# Pinned WinLtfs native release that LTOG bundles. Bump both together when
# updating (get the SHA256 from the release's "digest" or `Get-FileHash`).
$WinLtfsVersion    = '1.2.0'
$WinLtfsDistSha256 = '08932B950310E8938137A463C3656CF14501E0D366A613792394A926C5D39ECE'

function Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Info([string]$m) { Write-Host "    $m" }

# Delete a path, but only if it lives inside the repo (never touch anything outside).
function Remove-RepoPath([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $full -eq $rootFull) {
        throw "refusing to delete '$full' (outside the repo)"
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------- 1. clean ---
Step 'Cleaning previous build output'
Remove-RepoPath (Join-Path $GuiDir 'bin')
Remove-RepoPath (Join-Path $GuiDir 'obj')
Remove-RepoPath (Join-Path $InstallerDir 'Output')
if ($SkipNative) {
    # Keep the native engine (dist\winltfs) and licenses; wipe the GUI files that
    # live directly in dist\ so nothing stale survives.
    if (Test-Path -LiteralPath $Dist) {
        Get-ChildItem -LiteralPath $Dist -Force |
            Where-Object { $_.Name -notin @('winltfs', 'licenses') } |
            ForEach-Object { Remove-RepoPath $_.FullName }
    }
    Info 'kept existing native binaries in dist\winltfs (-SkipNative)'
} else {
    Remove-RepoPath $Dist
}
Info 'clean'

# -------------------------------------------- 2. native (WinLtfs release) ---
if ($SkipNative) {
    Step 'Skipping WinLtfs download (-SkipNative)'
    if (-not (Test-Path (Join-Path $DistWinLtfs 'ltfs.exe'))) {
        throw "dist\winltfs\ltfs.exe is missing - cannot use -SkipNative without a prior download."
    }
    Info 'reusing existing dist\winltfs native binaries'
} else {
    Step "Fetching WinLtfs $WinLtfsVersion native engine"
    $zip = Join-Path ([IO.Path]::GetTempPath()) "WinLtfs-v$WinLtfsVersion-portable.zip"
    $url = "https://github.com/rlaphoenix/WinLtfs/releases/download/" +
           "v$WinLtfsVersion/WinLtfs-v$WinLtfsVersion-portable.zip"
    Invoke-WebRequest -Uri $url -OutFile $zip
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $WinLtfsDistSha256) {
        throw "WinLtfs zip SHA256 mismatch: got $actual, expected $WinLtfsDistSha256."
    }
    New-Item -ItemType Directory -Force -Path $DistWinLtfs | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $DistWinLtfs -Force
    if (-not (Test-Path (Join-Path $DistWinLtfs 'ltfs.exe'))) {
        throw "WinLtfs zip did not contain ltfs.exe."
    }
    # The release ships its license texts under licenses\; keep those at dist\licenses.
    $winltfsLicenses = Join-Path $DistWinLtfs 'licenses'
    if (Test-Path -LiteralPath $winltfsLicenses) {
        Remove-RepoPath $DistLicenses
        Move-Item -LiteralPath $winltfsLicenses -Destination $DistLicenses
    }
    Info 'WinLtfs engine + WinFsp DLL staged into dist\winltfs (licenses -> dist\licenses)'
}

# ------------------------------------------------------------------ 3. GUI ---
Step 'Building self-contained WinUI 3 GUI (dotnet)'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8)."
}
$assemblyVersion = if (($Version.Split('.')).Count -eq 3) { "$Version.0" } else { $Version }
Push-Location $GuiDir
try {
    & dotnet build 'LTOG.Gui.csproj' -c Release -p:Platform=x64 `
        -p:Version=$Version -p:AssemblyVersion=$assemblyVersion -p:FileVersion=$assemblyVersion `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
} finally {
    Pop-Location
}
$guiOut = Join-Path $GuiDir $GuiOutSub
if (-not (Test-Path (Join-Path $guiOut 'LTOG.exe'))) {
    throw "GUI build output not found at $guiOut."
}
New-Item -ItemType Directory -Force -Path $Dist | Out-Null
Copy-Item (Join-Path $guiOut '*') -Destination $Dist -Recurse -Force
Info 'self-contained GUI staged into dist\'

# ------------------------------------------------------------ 4. installer ---
if ($NoInstaller) {
    Step 'Skipping installer (-NoInstaller)'
} else {
    Step 'Building Windows installer'
    & pwsh -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $InstallerDir 'build-installer.ps1') -AutoInstallInnoSetup -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "installer build failed (exit $LASTEXITCODE)." }
}

# ----------------------------------------------------------------- summary ---
$sw.Stop()
Step ('Build complete in {0:n0}s' -f $sw.Elapsed.TotalSeconds)
$distMB = [math]::Round(((Get-ChildItem $Dist -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ("    dist\        {0} MB (install footprint)" -f $distMB) -ForegroundColor Green
$setup = Get-ChildItem (Join-Path $InstallerDir 'Output') -Filter '*.exe' -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setup) {
    $setupMB = [math]::Round($setup.Length / 1MB, 1)
    Write-Host ("    installer    {0} ({1} MB)" -f $setup.FullName, $setupMB) -ForegroundColor Green
}
