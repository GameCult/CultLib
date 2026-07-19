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

if (Test-Path -LiteralPath $publishRoot) {
  Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $quicPublishRoot) {
  Remove-Item -LiteralPath $quicPublishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

dotnet publish $projectPath -c $Configuration -o $publishRoot
if ($LASTEXITCODE -ne 0) {
  throw "CultLib publish failed with exit code $LASTEXITCODE"
}
dotnet publish $quicProjectPath -c $Configuration -o $quicPublishRoot
if ($LASTEXITCODE -ne 0) {
  throw "CultLib native QUIC managed publish failed with exit code $LASTEXITCODE"
}
& (Join-Path $PSScriptRoot "build-quic-native.ps1") `
  -Configuration $Configuration `
  -OutputDirectory $quicNativeRoot

New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $templateRoot "package.json") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $templateRoot "README.md") -Destination $outputRoot

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

Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\GameCult.CultLib.asmdef") `
  -Destination (Join-Path $outputRoot "Runtime")
foreach ($assemblyName in $expectedAssemblies) {
  $assembly = $publishedByName[$assemblyName]
  Copy-Item -LiteralPath $assembly.FullName -Destination $pluginRoot
  $pdb = [System.IO.Path]::ChangeExtension($assembly.FullName, ".pdb")
  if (Test-Path -LiteralPath $pdb) {
    Copy-Item -LiteralPath $pdb -Destination $pluginRoot
  }
}
$nativePluginRoot = Join-Path $pluginRoot "x86_64"
New-Item -ItemType Directory -Force -Path $nativePluginRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "gamecult_mesh_quic_native.dll") -Destination $nativePluginRoot
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "msquic.dll") -Destination $nativePluginRoot
New-Item -ItemType Directory -Force -Path (Join-Path $outputRoot "Third Party Notices") | Out-Null
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "MSQUIC-LICENSE.txt") `
  -Destination (Join-Path $outputRoot "Third Party Notices\MSQUIC-LICENSE.txt")

if ($UpdateTemplate) {
  $templatePluginRoot = Join-Path $templateRoot "Runtime\Plugins"
  foreach ($assemblyName in $expectedAssemblies) {
    Copy-Item -LiteralPath (Join-Path $pluginRoot $assemblyName) -Destination $templatePluginRoot -Force
    $pdbName = [System.IO.Path]::ChangeExtension($assemblyName, ".pdb")
    $pdb = Join-Path $pluginRoot $pdbName
    if (Test-Path -LiteralPath $pdb) {
      Copy-Item -LiteralPath $pdb -Destination $templatePluginRoot -Force
    }
  }
  $templateNativePluginRoot = Join-Path $templatePluginRoot "x86_64"
  New-Item -ItemType Directory -Force -Path $templateNativePluginRoot | Out-Null
  Copy-Item -LiteralPath (Join-Path $nativePluginRoot "gamecult_mesh_quic_native.dll") `
    -Destination $templateNativePluginRoot -Force
  Copy-Item -LiteralPath (Join-Path $nativePluginRoot "msquic.dll") `
    -Destination $templateNativePluginRoot -Force
  $templateNoticesRoot = Join-Path $templateRoot "Third Party Notices"
  New-Item -ItemType Directory -Force -Path $templateNoticesRoot | Out-Null
  Copy-Item -LiteralPath (Join-Path $outputRoot "Third Party Notices\MSQUIC-LICENSE.txt") `
    -Destination $templateNoticesRoot -Force
  Write-Host "Updated tracked Unity package assemblies: $templatePluginRoot"
}

$manifest = Get-Content -LiteralPath (Join-Path $outputRoot "package.json") -Raw | ConvertFrom-Json
Write-Host "CultLib Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($expectedAssemblies.Count)"
Write-Host "Native realtime: MsQuic Schannel 2.5.9 (Windows x64)"
