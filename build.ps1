<#
.SYNOPSIS
    One-shot clean build of LTOG: the LTFS engine + WinFsp port, the WinUI 3 GUI,
    and the Windows installer.

.DESCRIPTION
    Wipes previous build output (so nothing stale survives), then builds, in order:

      1. LTFS engine + WinFsp port  -> dist\        (via MSYS2: scripts/setup.sh + build.sh)
      2. Self-contained WinUI 3 GUI -> dist\gui     (dotnet build, .NET baked in)
      3. Windows installer          -> installer\Output\  (installer\build-installer.ps1)

    Run from a normal PowerShell prompt (no elevation needed):

        pwsh -File build.ps1

    Build prerequisites (the script checks for these and fails clearly if missing):
      * MSYS2 with the LTFS/WinFsp build deps (see scripts/setup.sh) - unless -SkipNative
      * WinFsp installed with the "Developer" feature                - first native build only
      * .NET 8 SDK
      * Inno Setup 6.3+ (build-installer.ps1 can install it via winget)

.PARAMETER SkipNative
    Reuse the native LTFS/WinFsp binaries already in dist\ and skip the (slow) MSYS2
    build. Use when iterating on the GUI/installer. Fails if dist\ltfs.exe is absent.

.PARAMETER NoInstaller
    Build the engine and GUI but stop before the installer.

.PARAMETER Msys2Root
    Path to the MSYS2 installation. Default: C:\msys64.

.PARAMETER Version
    Version embedded into the GUI assembly and installer. Default: 1.0.0.

.EXAMPLE
    pwsh -File build.ps1                 # full clean build of everything

.EXAMPLE
    pwsh -File build.ps1 -SkipNative     # rebuild only the GUI + installer (reuse native)
#>
[CmdletBinding()]
param(
    [switch]$SkipNative,
    [switch]$NoInstaller,
    [string]$Msys2Root = 'C:\msys64',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root         = $PSScriptRoot
$Dist         = Join-Path $Root 'dist'
$GuiDir       = Join-Path $Root 'gui'
$InstallerDir = Join-Path $Root 'installer'
$GuiOutSub    = 'bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'   # self-contained output

function Step([string]$m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Info([string]$m) { Write-Host "    $m" }

# Convert a Windows path to an MSYS2/Unix path: C:\a\b -> /c/a/b
function ConvertTo-MsysPath([string]$p) {
    $p = $p -replace '\\', '/'
    if ($p -match '^([A-Za-z]):(.*)$') { return '/' + $Matches[1].ToLower() + $Matches[2] }
    return $p
}

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
    Remove-RepoPath (Join-Path $Dist 'gui')   # keep native binaries, refresh GUI only
    Info 'kept existing native binaries in dist\ (-SkipNative)'
} else {
    Remove-RepoPath $Dist
}
Info 'clean'

# ------------------------------------------------- 2. native (LTFS + WinFsp) ---
if ($SkipNative) {
    Step 'Skipping native LTFS/WinFsp build (-SkipNative)'
    if (-not (Test-Path (Join-Path $Dist 'ltfs.exe'))) {
        throw "dist\ltfs.exe is missing - cannot use -SkipNative without a prior native build."
    }
    Info 'reusing existing dist\ native binaries'
} else {
    Step 'Building LTFS engine + WinFsp port (MSYS2 MINGW64)'
    $bash = Join-Path $Msys2Root 'usr\bin\bash.exe'
    if (-not (Test-Path $bash)) {
        throw "MSYS2 bash not found at '$bash'. Install MSYS2 (https://www.msys2.org) or pass -Msys2Root. " +
              "Or use -SkipNative to reuse existing native binaries."
    }
    $repoUnix = ConvertTo-MsysPath $Root
    # setup.sh only on a fresh tree (no Makefile yet); always make-clean for a clean compile.
    $bashScript = @"
set -e
cd '$repoUnix'
if [ ! -f third_party/ltfs/ltfs/Makefile ]; then
    echo '--- one-time setup.sh (deps, WinFsp staging, patches, configure) ---'
    ./scripts/setup.sh
fi
echo '--- make clean ---'
./scripts/build.sh clean 2>/dev/null || true
echo '--- compile + stage dist ---'
./scripts/build.sh all
"@
    $env:MSYSTEM = 'MINGW64'
    $env:CHERE_INVOKING = '1'
    & $bash '-l' '-c' $bashScript
    if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)." }
    if (-not (Test-Path (Join-Path $Dist 'ltfs.exe'))) {
        throw "native build did not produce dist\ltfs.exe."
    }
    Info 'LTFS engine + WinFsp DLL staged into dist\'
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
$distGui = Join-Path $Dist 'gui'
New-Item -ItemType Directory -Force -Path $distGui | Out-Null
Copy-Item (Join-Path $guiOut '*') -Destination $distGui -Recurse -Force
Info 'self-contained GUI staged into dist\gui'

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
