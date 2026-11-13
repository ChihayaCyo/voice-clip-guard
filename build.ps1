# Build VoiceClipGuard using the built-in csc.exe and .NET Framework reference assemblies.
# No Visual Studio / dotnet SDK required.
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$src    = Join-Path $root 'src\VoiceClipGuard.cs'
$outDir = Join-Path $root 'dist'
$out    = Join-Path $outDir 'VoiceClipGuard.exe'

if (-not (Test-Path $src)) { throw "source not found: $src" }

# --- locate csc.exe ---
$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found (.NET Framework 4.x required)" }

# --- locate reference assemblies (Reference Assemblies first, then GAC) ---
$refRoots = @(
    'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8',
    'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2',
    'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'
) | Where-Object { Test-Path $_ }

function Resolve-Ref([string]$name) {
    foreach ($r in $refRoots) {
        $p = Join-Path $r $name
        if (Test-Path $p) { return $p }
    }
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($name)
    $gacDir = Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL' -Directory -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -eq $baseName } |
              Select-Object -First 1
    if ($gacDir) {
        $dll = Get-ChildItem $gacDir.FullName -Recurse -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($dll) { return $dll.FullName }
    }
    throw "assembly not found: $name"
}

$refs = @('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll')
$resolvedRefs = @()
foreach ($r in $refs) { $resolvedRefs += (Resolve-Ref $r) }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- compile ---
# Only reference the UIA assemblies explicitly; others come from csc defaults (avoids CS1703).
$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    ('/out:' + $out)
)
foreach ($r in $resolvedRefs) { $cscArgs += ('/r:' + $r) }
$cscArgs += $src

Write-Host "csc: $csc"
Write-Host "src: $src"
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

Write-Host "OK -> $out" -ForegroundColor Green
