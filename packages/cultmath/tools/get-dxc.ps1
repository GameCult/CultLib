param(
    [string]$InstallRoot = ".tools\dxc"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installPath = Join-Path $repoRoot $InstallRoot
New-Item -ItemType Directory -Force -Path $installPath | Out-Null

# Pinned: parity means this compiler's lowering (dxcompiler 1.9.0.5402). The hash is the SHA256
# of dxc_2026_07_29.zip downloaded from Microsoft's GitHub release v1.9.2607, and matches the
# digest GitHub publishes for that asset.
$zipName = "dxc_2026_07_29.zip"
$zipUrl = "https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/$zipName"
$zipSha256 = "A1DFB116BA3EEAE6A1582291B53A8E7BF65AD760676BD3194685C8F7367CD241"

$zipPath = Join-Path $installPath $zipName
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

& curl.exe -L --retry 5 --retry-delay 2 --fail -o $zipPath $zipUrl
if ($LASTEXITCODE -ne 0) {
    throw "curl.exe failed with exit code $LASTEXITCODE."
}

$actualSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
if ($actualSha256 -ne $zipSha256) {
    Remove-Item -LiteralPath $zipPath -Force
    throw "$zipName SHA256 is $actualSha256, expected $zipSha256."
}

$currentPath = Join-Path $installPath "current"
Remove-Item -LiteralPath $currentPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $currentPath | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $currentPath -Force

$dxcPath = Join-Path $currentPath "bin\x64\dxc.exe"
if (-not (Test-Path $dxcPath)) {
    throw "DXC x64 executable was not found after extraction."
}

& $dxcPath --version
Write-Host "DXC installed at $dxcPath"
