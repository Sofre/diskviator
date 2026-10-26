# =============================================================================
# DiskViator — Silent Installer
# Installs the .NET SDK if it's missing, then packs and installs DiskViator
# as a global CLI tool so you can run it from anywhere as "diskviator".
#
# Works two ways:
#   - Local dev mode:  .\install.ps1          (run from inside the cloned repo)
#   - Remote one-liner: irm https://raw.githubusercontent.com/Sofre/diskviator/main/install.ps1 | iex
#                        (downloads the source itself, no manual clone/download needed)
# =============================================================================

$ErrorActionPreference = 'Stop'
$RepoZipUrl = 'https://github.com/Sofre/diskviator/archive/refs/heads/main.zip'
$LogDir = Join-Path $env:TEMP 'diskviator-install-logs'
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
        exit 1
    }
}

# -----------------------------------------------------------------------------
# Step 2: Pack DiskViator
# -----------------------------------------------------------------------------
Write-Step "Building DiskViator..."
$packLog = Join-Path $LogDir 'pack.log'
Push-Location $ProjectDir
try {
    & dotnet pack -c Release *> $packLog
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack exited with code $LASTEXITCODE"
    }
    Write-Ok "Build complete."
}
catch {
    Pop-Location
    Write-Fail "Build failed." $packLog
    if ($CleanupDir) { Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}

# -----------------------------------------------------------------------------
# Step 3: Install (or update) the global "diskviator" command
# -----------------------------------------------------------------------------
Write-Step "Installing the 'diskviator' command..."
$toolLog = Join-Path $LogDir 'tool-install.log'
$alreadyInstalled = (& dotnet tool list -g 2>$null) -match 'diskviator'

try {
    if ($alreadyInstalled) {
        & dotnet tool update -g DiskViator --add-source "$ProjectDir\nupkg" *> $toolLog
    }
    else {
        & dotnet tool install -g DiskViator --add-source "$ProjectDir\nupkg" *> $toolLog
    }
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool install/update exited with code $LASTEXITCODE"
    }
    Write-Ok "DiskViator installed as a global command."
}
catch {
    Pop-Location
    Write-Fail "Could not install the global command." $toolLog
    if ($CleanupDir) { Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
Pop-Location

if ($CleanupDir) {
    Remove-Item $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "  ===============================" -ForegroundColor Green
Write-Host "   Done! Open a NEW terminal and run:  diskviator" -ForegroundColor Green
Write-Host "  ===============================" -ForegroundColor Green
Write-Host ""
