param(
    [string]$InstallRoot = ".tools\dxc"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installPath = Join-Path $repoRoot $InstallRoot
New-Item -ItemType Directory -Force -Path $installPath | Out-Null

$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/microsoft/DirectXShaderCompiler/releases/latest" `
    -Headers @{ "User-Agent" = "CultMath-DXC-Bootstrap" }

$asset = $release.assets |
    Where-Object { $_.name -like "dxc_*.zip" } |
    Select-Object -First 1

if (-not $asset) {
    throw "No Windows dxc_*.zip asset found on the latest DirectXShaderCompiler release."
}

$zipPath = Join-Path $installPath $asset.name
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue

& curl.exe -L --retry 5 --retry-delay 2 --fail -o $zipPath $asset.browser_download_url
if ($LASTEXITCODE -ne 0) {
    throw "curl.exe failed with exit code $LASTEXITCODE."
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
