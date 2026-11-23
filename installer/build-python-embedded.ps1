<#
.SYNOPSIS
  Builds ai-service\dist\python-embedded: the Python 3.12 runtime + every AI service package that
  GSE-Tech-Pro-Setup.iss copies to {app}\Python. Nothing is installed with pip on the tablet.

.DESCRIPTION
  1. Unpacks Python's official Windows embeddable package (python-3.12.*-embed-amd64.zip).
     Put the zip in Reference\python\. If Reference\python\PINNED.txt holds a line
     "sha256 <hash>", the zip must match it; without that file the hash is printed and the
     build stops (pass -AcceptUnpinned to build anyway, then record the hash in PINNED.txt).
  2. Rewrites python312._pth so the runtime imports the stdlib zip, Lib\site-packages and
     ..\AiService (the installed service code), and nothing else.
  3. Installs installer\python-requirements.lock.txt (every ai-service\pyproject.toml dependency,
     pinned with hashes, win_amd64 / CPython 3.12 wheels only) into Lib\site-packages with uv.
     -UpdateLock re-resolves the lock from pyproject.toml first.
  4. Checks the result the way the tablet runs it: copies Python + the AI service into a temp
     folder in the installed layout and imports the service, the OCR bindings, sqlite-vec and
     the prompts with the embedded python.exe.

  Needs uv (https://docs.astral.sh/uv/) and network access to PyPI for steps 1 and 3 only.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-python-embedded.ps1
#>
[CmdletBinding()]
param(
    [string]$PythonZip,
    [switch]$UpdateLock,
    [switch]$AcceptUnpinned,
    [switch]$SkipCheck
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$aiService = Join-Path $repo 'ai-service'
$out = Join-Path $aiService 'dist\python-embedded'
$lock = Join-Path $PSScriptRoot 'python-requirements.lock.txt'
$pinned = Join-Path $repo 'Reference\python\PINNED.txt'
# Wheels for the tablet, whatever Python the build machine has.
$target = @('--python-version', '3.12', '--python-platform', 'x86_64-pc-windows-msvc')

function Invoke-Native([string]$exe, [string[]]$argv) {
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

$uv = (Get-Command uv -ErrorAction SilentlyContinue).Source
if (-not $uv) { throw 'uv is not on PATH. Install it from https://docs.astral.sh/uv/.' }

# ---- 1. Embeddable zip ----------------------------------------------------------------------
if (-not $PythonZip) {
    $found = @(Get-ChildItem (Join-Path $repo 'Reference\python') -Filter 'python-3.12.*-embed-amd64.zip' -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) {
        throw "Put exactly one python-3.12.*-embed-amd64.zip (python.org, Windows embeddable package 64-bit) in Reference\python\, or pass -PythonZip. Found $($found.Count)."
    }
    $PythonZip = $found[0].FullName
}
$hash = (Get-FileHash $PythonZip -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = $null
if (Test-Path $pinned) {
    $line = Select-String -Path $pinned -Pattern '^\s*sha256\s+([0-9a-fA-F]{64})\s*$' | Select-Object -First 1
    if ($line) { $expected = $line.Matches[0].Groups[1].Value.ToLowerInvariant() }
}
if ($expected) {
    if ($hash -ne $expected) { throw "$PythonZip sha256 $hash does not match Reference\python\PINNED.txt ($expected)." }
    Write-Host "Python zip matches the pinned sha256."
} elseif (-not $AcceptUnpinned) {
    throw "No pinned hash. $([IO.Path]::GetFileName($PythonZip)) sha256 is $hash. Check it against python.org, write 'sha256 $hash' to Reference\python\PINNED.txt, and run again."
} else {
    Write-Warning "Building from an unpinned zip (sha256 $hash)."
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Expand-Archive -Path $PythonZip -DestinationPath $out
$pth = @(Get-ChildItem $out -Filter 'python3*._pth')
if ($pth.Count -ne 1 -or $pth[0].Name -ne 'python312._pth') {
    throw "Expected python312._pth in the embeddable zip; found: $($pth.Name -join ', '). Is this a 3.12 zip?"
}

# ---- 2. Import path -------------------------------------------------------------------------
# Paths are relative to {app}\Python. "import site" lets packages' .pth files run.
@(
    'python312.zip',
    '.',
    'Lib\site-packages',
    '..\AiService',
    'import site'
) | Set-Content -Path $pth[0].FullName -Encoding ascii

# ---- 3. Packages ----------------------------------------------------------------------------
if ($UpdateLock -or -not (Test-Path $lock)) {
    Write-Host "Resolving $lock from ai-service\pyproject.toml ..."
    Invoke-Native $uv (@('pip', 'compile', (Join-Path $aiService 'pyproject.toml'), '--generate-hashes',
        '--no-header', '--output-file', $lock) + $target)
}
$site = Join-Path $out 'Lib\site-packages'
Invoke-Native $uv (@('pip', 'install', '--target', $site, '--require-hashes', '--only-binary', ':all:',
    '--no-deps', '--requirement', $lock) + $target)
Get-ChildItem $site -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

# ---- 4. Check in the installed layout -------------------------------------------------------
if (-not $SkipCheck) {
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('gse-embedded-check-' + [guid]::NewGuid().ToString('N'))
    try {
        Copy-Item $out (Join-Path $stage 'Python') -Recurse
        $svc = Join-Path $stage 'AiService'
        # Same folders as the [Files] section of GSE-Tech-Pro-Setup.iss.
        foreach ($dir in 'app', 'tools', 'rag', 'ingest', 'recommender', 'voice', 'vision', 'prompts') {
            Copy-Item (Join-Path $aiService $dir) (Join-Path $svc $dir) -Recurse
        }
        Remove-Item (Join-Path $svc 'app\config.yaml') -ErrorAction SilentlyContinue
        New-Item -ItemType Directory (Join-Path $svc 'migrations') | Out-Null
        Copy-Item (Join-Path $repo 'src\GseTechPro.Data\Migrations\*.sql') (Join-Path $svc 'migrations')
        Copy-Item (Join-Path $PSScriptRoot 'check_embedded_python.py') $stage

        $py = Join-Path $stage 'Python\python.exe'
        Push-Location $stage
        try { Invoke-Native $py @((Join-Path $stage 'check_embedded_python.py')) }
        finally { Pop-Location }
    }
    finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Built {0} ({1:N0} MB)." -f $out, $size)
