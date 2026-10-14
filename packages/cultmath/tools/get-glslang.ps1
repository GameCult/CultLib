param(
    [string]$InstallRoot = ".tools\glslang"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installPath = Join-Path $repoRoot $InstallRoot
New-Item -ItemType Directory -Force -Path $installPath | Out-Null

# Pinned: the GLSL compile check means this front end (Khronos glslang 16.6.0). The hash is the
# SHA256 digest GitHub publishes for this asset of KhronosGroup/glslang release 16.6.0.
$zipName = "glslang-16.6.0-windows-x86_64-release.zip"
$zipUrl = "https://github.com/KhronosGroup/glslang/releases/download/16.6.0/$zipName"
$zipSha256 = "82BF434E69B9BB4829DE7E2B4BC2C5E7A7861E53D66CF75E5CC70F5F694A8D9B"

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

$glslangPath = Get-ChildItem -LiteralPath $currentPath -Recurse -File -Include "glslang.exe", "glslangValidator.exe" | Select-Object -First 1
if (-not $glslangPath) {
    throw "glslang executable was not found after extraction."
}

& $glslangPath.FullName --version
Write-Host "glslang installed at $($glslangPath.FullName)"
