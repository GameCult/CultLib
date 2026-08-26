param(
    [string]$InstallRoot = ".tools\spirv-cross"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$installPath = Join-Path $repoRoot $InstallRoot
$sourcePath = Join-Path $installPath "src"
$buildPath = Join-Path $installPath "build"

New-Item -ItemType Directory -Force -Path $installPath | Out-Null

if (-not (Test-Path $sourcePath)) {
    git clone --depth 1 https://github.com/KhronosGroup/SPIRV-Cross.git $sourcePath
}

cmake `
    -S $sourcePath `
    -B $buildPath `
    -G Ninja `
    -DSPIRV_CROSS_CLI=ON `
    -DSPIRV_CROSS_ENABLE_TESTS=OFF `
    -DSPIRV_CROSS_ENABLE_C_API=OFF

cmake --build $buildPath --target spirv-cross --config Release

$spirvCrossPath = Join-Path $buildPath "spirv-cross.exe"
if (-not (Test-Path $spirvCrossPath)) {
    throw "SPIRV-Cross executable was not found after build."
}

& $spirvCrossPath --version
Write-Host "SPIRV-Cross installed at $spirvCrossPath"
