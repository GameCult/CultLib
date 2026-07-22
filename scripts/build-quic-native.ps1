param(
  [string] $Configuration = "Release",
  [string] $Architecture = "x64",
  [string] $OutputDirectory = "artifacts\quic-native"
)

$ErrorActionPreference = "Stop"
if ($Architecture -ne "x64") {
  throw "The first CultMesh native QUIC package supports x64 Windows desktop only."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = "2.5.9"
$expectedPackageSha256 = "9877A919E1AA73AA4800F1E8A06B6539021F376228910138EB04540CD956617B"
$cacheRoot = Join-Path $repoRoot "artifacts\dependencies\msquic-schannel-$version"
$packagePath = Join-Path $cacheRoot "Microsoft.Native.Quic.MsQuic.Schannel.$version.zip"
$extractRoot = Join-Path $cacheRoot "package"
$nativeRoot = Join-Path $extractRoot "build\native"
$buildRoot = Join-Path $repoRoot "artifacts\quic-native-build\$Architecture\$Configuration"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}

New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
if (-not (Test-Path -LiteralPath $packagePath)) {
  & curl.exe -L --fail --silent --show-error `
    -o $packagePath `
    "https://www.nuget.org/api/v2/package/Microsoft.Native.Quic.MsQuic.Schannel/$version"
  if ($LASTEXITCODE -ne 0) { throw "MsQuic package download failed with exit code $LASTEXITCODE" }
}
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $expectedPackageSha256) {
  throw "MsQuic package digest does not match the pinned release."
}
if (-not (Test-Path -LiteralPath (Join-Path $nativeRoot "include\msquic.h"))) {
  if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
  Expand-Archive -LiteralPath $packagePath -DestinationPath $extractRoot
}

cmake -S (Join-Path $repoRoot "native\GameCult.Mesh.Quic.Native") -B $buildRoot -A x64 `
  "-DMSQUIC_ROOT=$($nativeRoot.Replace('\', '/'))"
if ($LASTEXITCODE -ne 0) { throw "CultMesh native QUIC configure failed with exit code $LASTEXITCODE" }
cmake --build $buildRoot --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CultMesh native QUIC build failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot "bin\$Configuration\gamecult_mesh_quic_native.dll") `
  -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $nativeRoot "bin\x64\msquic.dll") -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $extractRoot "LICENSE") -Destination (Join-Path $outputRoot "MSQUIC-LICENSE.txt") -Force

Write-Host "CultMesh native QUIC runtime: $outputRoot"
Get-ChildItem -LiteralPath $outputRoot -File | Select-Object Name,Length
