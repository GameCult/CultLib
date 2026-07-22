param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.cultlib"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\GameCult.Mesh\GameCult.Mesh.csproj"
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.cultlib"
$publishRoot = Join-Path $repoRoot "artifacts\unity-publish"
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
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

dotnet publish $projectPath -c $Configuration -o $publishRoot --disable-build-servers `
  -p:UseSharedCompilation=false -m:1 -p:Version=$($manifest.version)
if ($LASTEXITCODE -ne 0) {
  throw "CultLib publish failed with exit code $LASTEXITCODE"
}

$publishedByName = @{}
foreach ($assembly in Get-ChildItem -LiteralPath $publishRoot -Filter "*.dll") {
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
  $pdbPath = Join-Path $publishRoot $pdbName
  if (-not (Test-Path -LiteralPath $pdbPath)) {
    throw "CultLib Unity package is missing expected symbols: $pdbName"
  }
  Copy-Item -LiteralPath $pdbPath -Destination $pluginRoot -Force
}

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
  $freshHash = (Get-FileHash -LiteralPath (Join-Path $publishRoot $assemblyName) -Algorithm SHA256).Hash
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

Write-Host "CultLib Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($expectedAssemblies.Count)"
Write-Host "Verified owned assembly version: $expectedFileVersion"
Write-Host "Verified Stage 1 MessagePack resolver attribute."
