param(
  [string] $Configuration = "Release",
  [string] $Architecture = "x64",
  # Per-platform, so the Linux script's output sits beside this one's rather
  # than on top of it. build-unity-package.ps1 and the Native.Tests csproj both
  # pass an explicit -OutputDirectory, so neither is affected by this moving.
  [string] $OutputDirectory = "artifacts\quic-native\win32-x64"
)

$ErrorActionPreference = "Stop"
if ($Architecture -ne "x64") {
  throw "The first CultMesh native QUIC package supports x64 Windows desktop only."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$version = "2.5.9"
# OpenSSL, not Schannel (Q9 ruled A). The Schannel build of MsQuic refuses
# QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12 with QUIC_STATUS_NOT_SUPPORTED, which
# is the only credential type a provider has on both platforms, so a Windows
# provider could not open a listener at all. The identical bridge binary passes
# the whole v2 smoke and the v1 gate against this runtime.
#
# Two digests, because they answer different questions. The zip is what the
# download is pinned to; the DLL is what actually ships beside the bridge, and
# pinning only the archive leaves the file nobody checked.
$expectedPackageSha256 = "FEE9A664C0052DEEE66D40CED2E19BFE20BC423DE5AE5234CFC0CEBC22CA3F5E"
$expectedPackageBytes = 19179817
$expectedRuntimeSha256 = "C17C65813FE007EB486A5270D244F3659261355795B27ABB99BD19FF77539263"
$expectedRuntimeBytes = 4181856
$cacheRoot = Join-Path $repoRoot "artifacts\dependencies\msquic-openssl-$version"
$packagePath = Join-Path $cacheRoot "Microsoft.Native.Quic.MsQuic.OpenSSL.$version.zip"
$extractRoot = Join-Path $cacheRoot "package"
$nativeRoot = Join-Path $extractRoot "build\native"
$buildRoot = Join-Path $repoRoot "artifacts\quic-native-build\$Architecture\$Configuration"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}

function Assert-Pinned {
  param([string] $Path, [string] $ExpectedSha256, [long] $ExpectedBytes, [string] $What)
  $actualBytes = (Get-Item -LiteralPath $Path).Length
  $sha256 = [Security.Cryptography.SHA256]::Create()
  try {
    $stream = [IO.File]::OpenRead($Path)
    try { $actualSha256 = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace("-", "") }
    finally { $stream.Dispose() }
  } finally { $sha256.Dispose() }
  if ($actualSha256 -ne $ExpectedSha256 -or $actualBytes -ne $ExpectedBytes) {
    throw ("$What does not match the pinned release: " +
      "expected $ExpectedSha256 ($ExpectedBytes bytes), got $actualSha256 ($actualBytes bytes).")
  }
}

New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
if (-not (Test-Path -LiteralPath $packagePath)) {
  & curl.exe -L --fail --silent --show-error `
    -o $packagePath `
    "https://www.nuget.org/api/v2/package/Microsoft.Native.Quic.MsQuic.OpenSSL/$version"
  if ($LASTEXITCODE -ne 0) { throw "MsQuic package download failed with exit code $LASTEXITCODE" }
}
Assert-Pinned -Path $packagePath -ExpectedSha256 $expectedPackageSha256 `
  -ExpectedBytes $expectedPackageBytes -What "The MsQuic NuGet package"
if (-not (Test-Path -LiteralPath (Join-Path $nativeRoot "include\msquic.h"))) {
  if (Test-Path -LiteralPath $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force }
  Expand-Archive -LiteralPath $packagePath -DestinationPath $extractRoot
}
Assert-Pinned -Path (Join-Path $nativeRoot "bin\x64\msquic.dll") `
  -ExpectedSha256 $expectedRuntimeSha256 -ExpectedBytes $expectedRuntimeBytes -What "The MsQuic runtime DLL"

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
