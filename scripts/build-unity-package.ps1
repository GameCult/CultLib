param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.cultlib",
  [switch] $UpdateTemplate
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\GameCult.Mesh\GameCult.Mesh.csproj"
$quicProjectPath = Join-Path $repoRoot "src\GameCult.Mesh.Quic.Native\GameCult.Mesh.Quic.Native.csproj"
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.cultlib"
$publishRoot = Join-Path $repoRoot "artifacts\unity-publish"
$quicPublishRoot = Join-Path $repoRoot "artifacts\unity-quic-publish"
$quicNativeRoot = Join-Path $repoRoot "artifacts\unity-quic-native"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}
$pluginRoot = Join-Path $outputRoot "Runtime\Plugins"
$manifest = Get-Content -LiteralPath (Join-Path $templateRoot "package.json") -Raw | ConvertFrom-Json

$expectedAssemblies = @(
  "ConcurrentCollections.dll",
  "GameCult.Caching.dll",
  "GameCult.Caching.MessagePack.dll",
  "GameCult.Logging.dll",
  "GameCult.Mesh.dll",
  "GameCult.Mesh.Quic.Native.dll",
  "GameCult.Networking.dll",
  "Isopoh.Cryptography.Argon2.dll",
  "Isopoh.Cryptography.Blake2b.dll",
  "Isopoh.Cryptography.SecureArray.dll",
  "LiteNetLib.dll",
  "MessagePack.Annotations.dll",
  "MessagePack.dll",
  "Microsoft.Bcl.AsyncInterfaces.dll",
  "Microsoft.Bcl.TimeProvider.dll",
  "Microsoft.NET.StringTools.dll",
  "R3.dll",
  "System.Collections.Immutable.dll",
  "System.ComponentModel.Annotations.dll",
  "System.IO.Pipelines.dll",
  "System.Runtime.CompilerServices.Unsafe.dll",
  "System.Text.Encodings.Web.dll",
  "System.Text.Json.dll",
  "System.Threading.Channels.dll"
)
$ownedAssemblies = @(
  "GameCult.Caching.dll",
  "GameCult.Caching.MessagePack.dll",
  "GameCult.Logging.dll",
  "GameCult.Mesh.dll",
  "GameCult.Mesh.Quic.Native.dll",
  "GameCult.Networking.dll"
)
$expectedPdbs = $ownedAssemblies | ForEach-Object { [System.IO.Path]::ChangeExtension($_, ".pdb") }

function Assert-SameNames([string[]] $Actual, [string[]] $Expected, [string] $Description) {
  $difference = Compare-Object ($Expected | Sort-Object) ($Actual | Sort-Object)
  if ($difference) {
    throw "$Description differs from the expected package contract:`n$($difference | Out-String)"
  }
}

function Assert-Meta([string] $Path) {
  if (-not (Test-Path -LiteralPath "$Path.meta")) {
    throw "Unity metadata is missing for: $Path"
  }
}

if ($manifest.name -ne "org.gamecult.cultlib" -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') {
  throw "CultLib package manifest has an invalid name or semantic version."
}

$templatePluginRoot = Join-Path $templateRoot "Runtime\Plugins"
Assert-SameNames `
  (Get-ChildItem -LiteralPath $templatePluginRoot -Filter "*.dll" | Select-Object -ExpandProperty Name) `
  $expectedAssemblies `
  "Tracked Unity DLL set"
Assert-SameNames `
  (Get-ChildItem -LiteralPath $templatePluginRoot -Filter "*.pdb" | Select-Object -ExpandProperty Name) `
  $expectedPdbs `
  "Tracked Unity PDB set"

@(
  (Join-Path $templateRoot "package.json"),
  (Join-Path $templateRoot "README.md"),
  (Join-Path $templateRoot "Runtime"),
  (Join-Path $templateRoot "Runtime\GameCult.CultLib.asmdef"),
  $templatePluginRoot
) + ($expectedAssemblies | ForEach-Object { Join-Path $templatePluginRoot $_ }) +
  ($expectedPdbs | ForEach-Object { Join-Path $templatePluginRoot $_ }) |
  ForEach-Object { Assert-Meta $_ }

$asmdef = Get-Content -LiteralPath (Join-Path $templateRoot "Runtime\GameCult.CultLib.asmdef") -Raw | ConvertFrom-Json
Assert-SameNames $asmdef.precompiledReferences $expectedAssemblies "asmdef precompiled reference set"

if (Test-Path -LiteralPath $publishRoot) {
  Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $quicPublishRoot) {
  Remove-Item -LiteralPath $quicPublishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

dotnet publish $projectPath -c $Configuration -o $publishRoot --disable-build-servers `
  -p:UseSharedCompilation=false -m:1 -p:Version=$($manifest.version)
if ($LASTEXITCODE -ne 0) {
  throw "CultLib publish failed with exit code $LASTEXITCODE"
}
dotnet publish $quicProjectPath -c $Configuration -o $quicPublishRoot --disable-build-servers `
  -p:UseSharedCompilation=false -m:1 -p:Version=$($manifest.version)
if ($LASTEXITCODE -ne 0) {
  throw "CultLib native QUIC managed publish failed with exit code $LASTEXITCODE"
}
& (Join-Path $PSScriptRoot "build-quic-native.ps1") `
  -Configuration $Configuration `
  -OutputDirectory $quicNativeRoot

$publishedByName = @{}
foreach ($assembly in Get-ChildItem -LiteralPath $publishRoot -Filter "*.dll") {
  $publishedByName[$assembly.Name] = $assembly
}
foreach ($assembly in Get-ChildItem -LiteralPath $quicPublishRoot -Filter "*.dll") {
  $publishedByName[$assembly.Name] = $assembly
}
foreach ($assemblyName in $expectedAssemblies) {
  if (-not $publishedByName.ContainsKey($assemblyName)) {
    throw "CultLib Unity package is missing expected assembly: $assemblyName"
  }
}

Copy-Item -LiteralPath $templateRoot -Destination $outputRoot -Recurse
foreach ($assemblyName in $expectedAssemblies) {
  Copy-Item -LiteralPath $publishedByName[$assemblyName].FullName -Destination $pluginRoot -Force
}
foreach ($pdbName in $expectedPdbs) {
  $pdbPath = @($publishRoot, $quicPublishRoot) |
    ForEach-Object { Join-Path $_ $pdbName } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
  if (-not (Test-Path -LiteralPath $pdbPath)) {
    throw "CultLib Unity package is missing expected symbols: $pdbName"
  }
  Copy-Item -LiteralPath $pdbPath -Destination $pluginRoot -Force
}

$nativePluginRoot = Join-Path $pluginRoot "x86_64"
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "gamecult_mesh_quic_native.dll") -Destination $nativePluginRoot -Force
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "msquic.dll") -Destination $nativePluginRoot -Force
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "MSQUIC-LICENSE.txt") `
  -Destination (Join-Path $outputRoot "Third Party Notices\MSQUIC-LICENSE.txt") -Force

Assert-SameNames `
  (Get-ChildItem -LiteralPath $pluginRoot -Filter "*.dll" | Select-Object -ExpandProperty Name) `
  $expectedAssemblies `
  "Staged Unity DLL set"
Assert-SameNames `
  (Get-ChildItem -LiteralPath $pluginRoot -Filter "*.pdb" | Select-Object -ExpandProperty Name) `
  $expectedPdbs `
  "Staged Unity PDB set"

$expectedFileVersion = "$($manifest.version).0"
foreach ($assemblyName in $ownedAssemblies) {
  $assemblyPath = Join-Path $pluginRoot $assemblyName
  $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath).FileVersion
  if ($fileVersion -ne $expectedFileVersion) {
    throw "$assemblyName has file version $fileVersion; expected $expectedFileVersion."
  }
  $freshHash = (Get-FileHash -LiteralPath $publishedByName[$assemblyName].FullName -Algorithm SHA256).Hash
  $stagedHash = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
  if ($freshHash -ne $stagedHash) {
    throw "$assemblyName differs between the fresh publish and staged package."
  }
}

$resolverAssemblyPath = Join-Path $pluginRoot "GameCult.Caching.MessagePack.dll"
$resolverAssemblyMetadata = [System.Text.Encoding]::UTF8.GetString(
  [System.IO.File]::ReadAllBytes($resolverAssemblyPath))
if (-not $resolverAssemblyMetadata.Contains("CultDocumentMessagePackResolversAttribute")) {
  throw "GameCult.Caching.MessagePack.dll lacks CultDocumentMessagePackResolversAttribute."
}
$meshAssemblyMetadata = [System.Text.Encoding]::UTF8.GetString(
  [System.IO.File]::ReadAllBytes((Join-Path $pluginRoot "GameCult.Mesh.dll")))
if (-not $meshAssemblyMetadata.Contains("ICultMeshBodyReadLease")) {
  throw "GameCult.Mesh.dll lacks ICultMeshBodyReadLease."
}

if ($UpdateTemplate) {
  foreach ($name in $expectedAssemblies + $expectedPdbs) {
    Copy-Item -LiteralPath (Join-Path $pluginRoot $name) -Destination $templatePluginRoot -Force
  }
  Copy-Item -LiteralPath (Join-Path $nativePluginRoot "gamecult_mesh_quic_native.dll") `
    -Destination (Join-Path $templatePluginRoot "x86_64") -Force
  Copy-Item -LiteralPath (Join-Path $nativePluginRoot "msquic.dll") `
    -Destination (Join-Path $templatePluginRoot "x86_64") -Force
  Copy-Item -LiteralPath (Join-Path $outputRoot "Third Party Notices\MSQUIC-LICENSE.txt") `
    -Destination (Join-Path $templateRoot "Third Party Notices") -Force
  Write-Host "Updated tracked Unity package assemblies: $templatePluginRoot"
}

Write-Host "CultLib Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($expectedAssemblies.Count)"
Write-Host "Verified owned assembly version: $expectedFileVersion"
Write-Host "Verified Stage 1 MessagePack resolver attribute."
Write-Host "Verified ICultMeshBodyReadLease."
Write-Host "Native realtime: MsQuic Schannel 2.5.9 (Windows x64)"
