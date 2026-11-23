<#
.SYNOPSIS
  Builds GSE-Tech-Pro-Setup-<version>.exe with Inno Setup 7.

.DESCRIPTION
  1. Publishes the WinUI app (win-x64) to installer\build\app and the 32-bit device host
     (win-x86) to installer\build\host32, both Release and self-contained.
  2. Needs ai-service\dist\python-embedded (installer\build-python-embedded.ps1); builds it
     when missing, or always with -RebuildPython.
  3. Runs ISCC on GSE-Tech-Pro-Setup.iss into installer\Output (or -OutputDir).
     -NoModels passes /DNO_MODELS: a test setup without the GGUF models.
     When SIGNTOOL_CERT and SIGNTOOL_PASS are set, every binary and the setup are signed with
     signtool (DigiCert timestamp). The certificate and password are never written to disk here.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -NoModels
#>
[CmdletBinding()]
param(
    [switch]$NoModels,
    [switch]$RebuildPython,
    [switch]$SkipPublish,
    [string]$OutputDir,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $PSScriptRoot 'build'
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot 'Output' }

function Invoke-Native([string]$exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE" }
}

if (-not $Iscc) {
    $candidates = @(
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $Iscc) { throw 'Inno Setup 7 (ISCC.exe) not found. Install it or pass -Iscc.' }
}

# ---- 1. Publish ----------------------------------------------------------------------------
if (-not $SkipPublish) {
    foreach ($dir in 'app', 'host32') {
        $path = Join-Path $build $dir
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Invoke-Native dotnet @('publish', (Join-Path $repo 'src\GseTechPro.App\GseTechPro.App.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:Platform=x64',
        '-o', (Join-Path $build 'app'))
    Invoke-Native dotnet @('publish', (Join-Path $repo 'src\GseTechPro.Devices.Host32\GseTechPro.Devices.Host32.csproj'),
        '-c', 'Release', '-r', 'win-x86', '--self-contained', 'true',
        '-o', (Join-Path $build 'host32'))
}
foreach ($exe in 'app\GseTechPro.App.exe', 'host32\GseTechPro.Devices.Host32.exe') {
    if (-not (Test-Path (Join-Path $build $exe))) { throw "Missing $exe in installer\build (run without -SkipPublish)." }
}

# ---- 2. Embedded Python --------------------------------------------------------------------
$python = Join-Path $repo 'ai-service\dist\python-embedded\python.exe'
if ($RebuildPython -or -not (Test-Path $python)) {
    & (Join-Path $PSScriptRoot 'build-python-embedded.ps1')
}

# ---- 3. Compile ----------------------------------------------------------------------------
$argv = @("/O$OutputDir")
if ($NoModels) { $argv += '/DNO_MODELS' }
if ($env:SIGNTOOL_CERT -and $env:SIGNTOOL_PASS) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) { throw 'SIGNTOOL_CERT is set but signtool.exe is not on PATH (Windows SDK).' }
    # $q is Inno's quote, $f the file being signed.
    $argv += "/Sgsesign=`"$signtool`" sign /f `$q$($env:SIGNTOOL_CERT)`$q /p `$q$($env:SIGNTOOL_PASS)`$q /tr http://timestamp.digicert.com /td sha256 /fd sha256 `$f"
    $argv += '/DSIGN'
    Write-Host 'Signing with the certificate in SIGNTOOL_CERT.'
} else {
    Write-Warning 'SIGNTOOL_CERT / SIGNTOOL_PASS not set: building an UNSIGNED setup.'
}
$argv += (Join-Path $PSScriptRoot 'GSE-Tech-Pro-Setup.iss')
Invoke-Native $Iscc $argv
Get-ChildItem $OutputDir -Filter 'GSE-Tech-Pro-Setup-*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
    ForEach-Object { Write-Host ("Setup: {0} ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)) }
