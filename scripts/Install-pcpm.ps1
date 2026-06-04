<#
.SYNOPSIS
    Builds pcpm in Release mode, sets PCPM_HOME and adds it to the user PATH.

.DESCRIPTION
    Mirrors the way pnpm manages PNPM_HOME:

      1. Resolves PCPM_HOME (default: %LOCALAPPDATA%\pcpm).
      2. Publishes pcpm.Cli as a self-contained single-file executable.
      3. Copies pcpm.exe directly into PCPM_HOME (no bin\ subfolder, same layout as pnpm).
      4. Sets the PCPM_HOME user environment variable persistently.
      5. Adds PCPM_HOME to the user PATH persistently (no admin required).

    After running, open a new terminal and pcpm is available everywhere.

.PARAMETER PcpmHome
    Root directory for pcpm. Equivalent to PNPM_HOME in pnpm.
    Defaults to %LOCALAPPDATA%\pcpm

.EXAMPLE
    .\Install-Pcpm.ps1
    .\Install-Pcpm.ps1 -PcpmHome "C:\tools\pcpm"
#>
[CmdletBinding()]
param(
    [string] $PcpmHome = (Join-Path $env:LOCALAPPDATA 'pcpm')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 1. Locate the repo root (folder that contains pcpm.slnx)
# ---------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

if (-not (Test-Path (Join-Path $repoRoot 'pcpm.slnx'))) {
    Write-Error "Could not find pcpm.slnx in '$repoRoot'. Run this script from the scripts\ folder."
    exit 1
}

$cliProject = Join-Path $repoRoot 'src\pcpm.Cli\pcpm.Cli.csproj'
if (-not (Test-Path $cliProject)) {
    Write-Error "Could not find pcpm.Cli.csproj at '$cliProject'."
    exit 1
}

# ---------------------------------------------------------------------------
# 2. Publish -- self-contained single-file, no SDK needed to run
# ---------------------------------------------------------------------------
Write-Host "Building pcpm..." -ForegroundColor Cyan

$publishDir = Join-Path $repoRoot 'publish'

$dotnetArgs = @(
    'publish', $cliProject
    '--configuration', 'Release'
    '--runtime', 'win-x64'
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:EnableCompressionInSingleFile=true'
    '--output', $publishDir
    '--nologo'
    '-v', 'q'
)

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$binary = Join-Path $publishDir 'pcpm.exe'
if (-not (Test-Path $binary)) {
    Write-Error "Expected binary not found at '$binary' after publish."
    exit 1
}

# ---------------------------------------------------------------------------
# 3. Create PCPM_HOME and copy the binary into it (flat layout, like pnpm)
# ---------------------------------------------------------------------------
if (-not (Test-Path $PcpmHome)) {
    New-Item -ItemType Directory -Path $PcpmHome | Out-Null
}
# Pre-create the store subdirectory so it is visible from day one.
$storeDir = Join-Path $PcpmHome 'store'
if (-not (Test-Path $storeDir)) {
    New-Item -ItemType Directory -Path $storeDir | Out-Null
}

Copy-Item -Path $binary -Destination $PcpmHome -Force
Write-Host "  pcpm.exe  ->  $PcpmHome" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. Set PCPM_HOME user environment variable (persistent, no admin needed)
# ---------------------------------------------------------------------------
$existingHome = [Environment]::GetEnvironmentVariable('PCPM_HOME', 'User')
if ($existingHome -eq $PcpmHome) {
    Write-Host "  PCPM_HOME already set to '$PcpmHome'" -ForegroundColor Gray
} else {
    [Environment]::SetEnvironmentVariable('PCPM_HOME', $PcpmHome, 'User')
    $env:PCPM_HOME = $PcpmHome   # update current session too
    Write-Host "  PCPM_HOME  =  $PcpmHome" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 5. Add PCPM_HOME to the user PATH (persistent, no admin needed)
# ---------------------------------------------------------------------------
$currentPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if (-not $currentPath) { $currentPath = '' }
$entries  = $currentPath -split ';' | Where-Object { $_ -ne '' }

# Normalise for comparison (trim trailing separators, case-insensitive on Windows).
$normalised = $entries | ForEach-Object { $_.TrimEnd('\', '/') }
$target     = $PcpmHome.TrimEnd('\', '/')

if ($normalised -icontains $target) {
    Write-Host "  PATH already contains PCPM_HOME -- no change needed." -ForegroundColor Gray
} else {
    $newPath = ($entries + $PcpmHome) -join ';'
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    $env:PATH = "$($env:PATH);$PcpmHome"   # update current session too
    Write-Host "  PATH  +=  $PcpmHome" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 6. Quick smoke-test in the current session
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Verifying..." -ForegroundColor Cyan
& pcpm --version
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "pcpm is ready. Open a new terminal to pick up the updated PATH." -ForegroundColor Green
    Write-Host ""
    Write-Host "  PCPM_HOME : $PcpmHome" -ForegroundColor DarkCyan
    Write-Host "  Store     : $storeDir" -ForegroundColor DarkCyan
    Write-Host ""
    Write-Host "Try: pcpm --help" -ForegroundColor Yellow
}
