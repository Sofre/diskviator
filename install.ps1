# =============================================================================
# DiskViator — Silent Installer
# Installs the .NET SDK if it's missing, publishes DiskViator, and adds the
# "diskviator" command to the current user's PATH.
#
# Works two ways:
#   - Local dev mode:  .\install.ps1          (run from inside the cloned repo)
#   - Remote one-liner: irm https://raw.githubusercontent.com/Sofre/diskviator/main/install.ps1 | iex
#                        (downloads the source itself, no manual clone/download needed)
# =============================================================================

param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$RepoZipUrl = 'https://github.com/Sofre/diskviator/archive/refs/heads/main.zip'
$LogDir = Join-Path $env:TEMP 'diskviator-install-logs'
$InstallRoot = Join-Path $env:LOCALAPPDATA 'DiskViator'
$InstallDir = Join-Path $InstallRoot 'app'
$BinDir = Join-Path $InstallRoot 'bin'
$CommandPath = Join-Path $BinDir 'diskviator.cmd'

if ($Uninstall) {
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $CommandPath) {
        Remove-Item -LiteralPath $CommandPath -Force
    }
    $removeBinPath = -not (Test-Path -LiteralPath $BinDir)
    if (Test-Path -LiteralPath $BinDir) {
        if (-not (Get-ChildItem -LiteralPath $BinDir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $BinDir -Force
            $removeBinPath = $true
        }
    }

    if ($removeBinPath) {
        $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
        $remainingPath = @($userPath -split ';' | Where-Object {
            $_ -and [string]::Compare($_.TrimEnd('\'), $BinDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -ne 0
        })
        $newUserPath = $remainingPath -join ';'
        [Environment]::SetEnvironmentVariable('PATH', $(if ($newUserPath) { $newUserPath } else { $null }), 'User')
    }

    Write-Host "DiskViator was uninstalled. Your database was preserved at $InstallRoot."
    exit 0
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

function Write-Step {
    param([string]$Message)
    Write-Host "  > $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message, [string]$LogPath)
    Write-Host ""
    Write-Host "  [FAILED] $Message" -ForegroundColor Red
    if ($LogPath -and (Test-Path $LogPath)) {
        Write-Host "  See log: $LogPath" -ForegroundColor DarkGray
    }
}

function Test-DotNetAvailable {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    return $null -ne $cmd
}

Write-Host ""
Write-Host "  ===============================" -ForegroundColor Yellow
Write-Host "   DiskViator Installer" -ForegroundColor Yellow
Write-Host "  ===============================" -ForegroundColor Yellow
Write-Host ""

# -----------------------------------------------------------------------------
# Step 0: Locate the source. If this script is sitting next to the project
# (local dev mode), use it as-is. Otherwise (piped remotely via iex, so there
# is no $PSScriptRoot and no files on disk), download the repo as a zip.
# -----------------------------------------------------------------------------
$ProjectDir = $null
$CleanupDir = $null

if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot 'DiskHistorian.csproj'))) {
    $ProjectDir = $PSScriptRoot
}
else {
    Write-Step "Downloading DiskViator source..."
    $downloadLog = Join-Path $LogDir 'download.log'
    $tempRoot = Join-Path $env:TEMP ("diskviator-src-" + [Guid]::NewGuid().ToString('N'))
    $zipPath = "$tempRoot.zip"

    try {
        New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
        Invoke-WebRequest -Uri $RepoZipUrl -OutFile $zipPath -UseBasicParsing *> $downloadLog
        Expand-Archive -Path $zipPath -DestinationPath $tempRoot -Force *>> $downloadLog

        # GitHub zips extract into a single "<repo>-<branch>" subfolder.
        $extracted = Get-ChildItem -Path $tempRoot -Directory | Select-Object -First 1
        if (-not $extracted) { throw "Extracted archive did not contain a project folder." }

        $ProjectDir = $extracted.FullName
        $CleanupDir = $tempRoot
        Write-Ok "Source downloaded."
    }
    catch {
        Write-Fail "Could not download DiskViator source ($($_.Exception.Message))." $downloadLog
        exit 1
    }
}

# -----------------------------------------------------------------------------
# Step 1: .NET SDK (installed silently + hidden if missing)
# -----------------------------------------------------------------------------
if (Test-DotNetAvailable) {
    Write-Ok ".NET SDK already present."
}
else {
    Write-Step "No .NET SDK found. Installing .NET 8 SDK silently (this can take a minute)..."

    $installerScript = Join-Path $env:TEMP 'dotnet-install.ps1'
    $sdkLog = Join-Path $LogDir 'dotnet-sdk-install.log'
    $dotnetInstallDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerScript -UseBasicParsing *> $sdkLog

        & $installerScript -Channel 8.0 -InstallDir $dotnetInstallDir *>> $sdkLog

        # Make dotnet available for the rest of THIS script...
        $env:PATH = "$dotnetInstallDir;$env:PATH"
        $env:DOTNET_ROOT = $dotnetInstallDir

        # ...and persist it for every future terminal session (user scope, no admin needed).
        $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
        if ($userPath -notlike "*$dotnetInstallDir*") {
            [Environment]::SetEnvironmentVariable('PATH', "$dotnetInstallDir;$userPath", 'User')
        }

        if (-not (Test-DotNetAvailable)) {
            throw "dotnet command still not found after install."
        }
        Write-Ok ".NET 8 SDK installed."
    }
    catch {
        Write-Fail "Could not install the .NET SDK automatically ($($_.Exception.Message))." $sdkLog
        Write-Host "  Install it manually from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run this script." -ForegroundColor Yellow
        if ($CleanupDir) { Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue }
        exit 1
    }
}

# -----------------------------------------------------------------------------
# Step 2: Publish DiskViator
# -----------------------------------------------------------------------------
Write-Step "Publishing DiskViator..."
$publishLog = Join-Path $LogDir 'publish.log'
$PublishDir = Join-Path $env:TEMP ("diskviator-publish-" + [Guid]::NewGuid().ToString('N'))
$RuntimeIdentifier = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    'X64' { 'win-x64' }
    'Arm64' { 'win-arm64' }
    'X86' { 'win-x86' }
    default { throw "Unsupported Windows architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
}

try {
    & dotnet publish (Join-Path $ProjectDir 'DiskHistorian.csproj') -c Release -r $RuntimeIdentifier --self-contained true -o $PublishDir *> $publishLog
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish exited with code $LASTEXITCODE"
    }
    Write-Ok "Publish complete."
}
catch {
    Write-Fail "Publish failed." $publishLog
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($CleanupDir) { Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}

# -----------------------------------------------------------------------------
# Step 3: Install or update the app and command shim
# -----------------------------------------------------------------------------
Write-Step "Installing the DiskViator app and 'diskviator' command..."
try {
    New-Item -ItemType Directory -Path $InstallDir, $BinDir -Force | Out-Null
    Copy-Item -Path (Join-Path $PublishDir '*') -Destination $InstallDir -Recurse -Force
    Set-Content -LiteralPath $CommandPath -Encoding ASCII -Value @(
        '@echo off',
        '"%LOCALAPPDATA%\DiskViator\app\DiskViator.exe" %*'
    )

    $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
    $pathEntries = @($userPath -split ';' | Where-Object { $_ })
    $hasBinPath = $pathEntries | Where-Object {
        [string]::Compare($_.TrimEnd('\'), $BinDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -eq 0
    }
    if (-not $hasBinPath) {
        $pathEntries += $BinDir
        [Environment]::SetEnvironmentVariable('PATH', ($pathEntries -join ';'), 'User')
    }
    $processPathEntries = @($env:PATH -split ';' | Where-Object { $_ })
    $hasProcessBinPath = $processPathEntries | Where-Object {
        [string]::Compare($_.TrimEnd('\'), $BinDir.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -eq 0
    }
    if (-not $hasProcessBinPath) {
        $env:PATH = "$BinDir;$env:PATH"
    }

    Write-Ok "DiskViator installed."
}
catch {
    Write-Fail "Could not install DiskViator." $publishLog
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item $PublishDir -Recurse -Force -ErrorAction SilentlyContinue }
    if ($CleanupDir) { Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}

Remove-Item -LiteralPath $PublishDir -Recurse -Force
if ($CleanupDir) {
    Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  ===============================" -ForegroundColor Green
Write-Host "   Done! Open a NEW terminal and run:  diskviator" -ForegroundColor Green
Write-Host "  ===============================" -ForegroundColor Green
Write-Host ""
