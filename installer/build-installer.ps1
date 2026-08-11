<#
.SYNOPSIS
    Build the LTOG Inno Setup installer.

.DESCRIPTION
    1. Verifies the application payload (..\dist) is present.
    2. Downloads the pinned prerequisites (WinFsp MSI, VC++ Redistributable)
       into redist\ if they are missing, verifying their integrity.
    3. Locates Inno Setup's compiler (ISCC.exe); offers to install it via winget
       if it is not found.
    4. Compiles LTOG.iss -> Output\LTOG-<version>-setup.exe

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File .\build-installer.ps1

.PARAMETER Version
    Version embedded into the installer metadata and output filename. Default: 1.0.0.
#>
[CmdletBinding()]
param(
    # Compile even if ISCC has to be installed via winget without prompting.
    [switch]$AutoInstallInnoSetup,

    # Installer/application version. Passed through to LTOG.iss.
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root      = $PSScriptRoot
$DistDir   = Join-Path (Split-Path $Root -Parent) 'dist'
$RedistDir = Join-Path $Root 'redist'
$IssFile   = Join-Path $Root 'LTOG.iss'

# --- Pinned prerequisite --------------------------------------------------
# WinFsp is the only bundled prerequisite. (The GUI is self-contained — .NET and
# the Windows App SDK are baked in — and the WinUI 3 binaries statically link the
# MSVC runtime, needing only the OS-provided UCRT, so no VC++ redist is shipped.)
$WinFsp = @{
    File   = 'winfsp-2.1.25156.msi'
    Url    = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
    Sha256 = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
    MinSize = 2000000
}

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Resolve-Redist($spec) {
    $path = Join-Path $RedistDir $spec.File
    if (Test-Path $path) {
        $len = (Get-Item $path).Length
        $ok = $len -ge $spec.MinSize
        if ($ok -and $spec.Sha256) {
            $ok = (Get-FileHash $path -Algorithm SHA256).Hash -eq $spec.Sha256
        }
        if ($ok) { Write-Host "    have $($spec.File)"; return }
        Write-Host "    $($spec.File) is incomplete/mismatched - re-downloading" -ForegroundColor Yellow
        Remove-Item $path -Force
    }
    Write-Host "    downloading $($spec.File)"
    Invoke-WebRequest -Uri $spec.Url -OutFile $path -UseBasicParsing
    if ((Get-Item $path).Length -lt $spec.MinSize) {
        throw "Downloaded $($spec.File) is smaller than expected ($($spec.MinSize) bytes)."
    }
    if ($spec.Sha256) {
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash
        if ($actual -ne $spec.Sha256) {
            throw "SHA256 mismatch for $($spec.File): got $actual, expected $($spec.Sha256)."
        }
    }
}

function Find-Iscc {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")   # winget per-user install
    # Inno Setup records its install location in the uninstall registry.
    foreach ($hive in 'HKLM:', 'HKCU:') {
        foreach ($view in 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                          'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
            $key = "$hive\$view\Inno Setup 6_is1"
            if (Test-Path $key) {
                $loc = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation
                if ($loc) { $candidates += (Join-Path $loc 'ISCC.exe') }
            }
        }
    }
    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    return $null
}

# --- 1. payload check -----------------------------------------------------
Write-Step 'Checking application payload (..\dist)'
if (-not (Test-Path (Join-Path $DistDir 'ltfs.exe')) -or
    -not (Test-Path (Join-Path $DistDir 'gui\LTOG.exe'))) {
    throw "dist\ is incomplete - expected ltfs.exe and gui\LTOG.exe under '$DistDir'. " +
          "Build the LTFS engine and GUI first (see the repo README)."
}
Write-Host "    payload OK"

# --- 2. prerequisite ------------------------------------------------------
Write-Step 'Ensuring bundled prerequisite (WinFsp)'
New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null
Resolve-Redist $WinFsp

# --- 3. locate / install Inno Setup --------------------------------------
Write-Step 'Locating Inno Setup compiler (ISCC.exe)'
$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host "    ISCC.exe not found." -ForegroundColor Yellow
    $doInstall = $AutoInstallInnoSetup
    if (-not $doInstall) {
        $ans = Read-Host "    Install Inno Setup 6 via winget now? [y/N]"
        $doInstall = ($ans -match '^[Yy]')
    }
    if ($doInstall) {
        if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
            throw "winget is not available. Install Inno Setup 6.3+ manually from https://jrsoftware.org/isdl.php"
        }
        Write-Host "    installing JRSoftware.InnoSetup via winget..."
        winget install --id JRSoftware.InnoSetup --exact --silent `
            --accept-package-agreements --accept-source-agreements
        $iscc = Find-Iscc
    }
    if (-not $iscc) {
        throw "Inno Setup compiler still not found. Install it from https://jrsoftware.org/isdl.php (6.3 or newer) and re-run."
    }
}
Write-Host "    using $iscc"

# --- 4. compile -----------------------------------------------------------
Write-Step 'Compiling installer'
& $iscc "/DMyAppVersion=$Version" $IssFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$out = Get-ChildItem (Join-Path $Root 'Output') -Filter '*.exe' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Step "Done"
if ($out) { Write-Host "    installer: $($out.FullName)" -ForegroundColor Green }
