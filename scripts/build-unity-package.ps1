param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.cultlib",
  [string] $NuGetConfig = "",
  [switch] $NoRestore,
  [switch] $UpdateTemplate
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\GameCult.Mesh\GameCult.Mesh.csproj"
$webSocketProjectPath = Join-Path $repoRoot "src\GameCult.Networking.WebSockets\GameCult.Networking.WebSockets.csproj"
$quicProjectPath = Join-Path $repoRoot "src\GameCult.Mesh.Quic.Native\GameCult.Mesh.Quic.Native.csproj"
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.cultlib"
$publishRoot = Join-Path $repoRoot "artifacts\unity-publish"
$webSocketPublishRoot = Join-Path $repoRoot "artifacts\unity-websocket-publish"
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
if (Test-Path -LiteralPath $webSocketPublishRoot) {
  Remove-Item -LiteralPath $webSocketPublishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

$unityPackageVersion = (Get-Content -LiteralPath (Join-Path $templateRoot "package.json") -Raw | ConvertFrom-Json).version

# Release gate: cultlib's Unity package resolves CultMath through the org.gamecult.cultmath
# dependency, so a cultlib release must not run ahead of the CultMath package it names.
# (1) The declared dependency version must not exceed the CultMath package's own version.
# (2) The CultMath.dll that package tracks must define every type CultMathResolver formats.
#     The names come from the resolver source (its Shape<T> entries); scripts\check-cultmath-types.cs
#     looks them up in the assembly's type-definition table, which Windows PowerShell cannot load
#     the assembly to do. It checks type names, not signatures.
$cultMathPackageRoot = Join-Path $repoRoot "packages\cultmath\unity\org.gamecult.cultmath"
$cultMathDeclared = (Get-Content -LiteralPath (Join-Path $templateRoot "package.json") -Raw | ConvertFrom-Json).dependencies.'org.gamecult.cultmath'
$cultMathAvailable = (Get-Content -LiteralPath (Join-Path $cultMathPackageRoot "package.json") -Raw | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($cultMathDeclared)) {
  throw "package.json must declare org.gamecult.cultmath, which supplies CultMath.dll."
}
foreach ($declared in @(@("org.gamecult.cultlib's org.gamecult.cultmath dependency", $cultMathDeclared), @("the CultMath package version", $cultMathAvailable))) {
  if ($declared[1] -notmatch '^\d+\.\d+\.\d+$') {
    throw "$($declared[0]) is '$($declared[1])'. The release gate compares exact release versions (major.minor.patch); prerelease and range versions are not supported."
  }
}
if ([version]$cultMathDeclared -gt [version]$cultMathAvailable) {
  throw "Release order: org.gamecult.cultlib depends on org.gamecult.cultmath $cultMathDeclared, but the CultMath package is at $cultMathAvailable. Release CultMath first."
}
$resolverSource = Get-Content -LiteralPath (Join-Path $repoRoot "src\GameCult.Caching.MessagePack\CultMathResolver.cs") -Raw
$resolverTypes = @([regex]::Matches($resolverSource, '\bShape<(?!T>)(\w+)>\(') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($resolverTypes.Count -eq 0) {
  throw "Found no CultMath types in CultMathResolver.cs; the release gate cannot check the CultMath package."
}
$missingTypes = & dotnet run (Join-Path $PSScriptRoot "check-cultmath-types.cs") -- (Join-Path $cultMathPackageRoot "Runtime\Plugins\CultMath.dll") @resolverTypes
if ($LASTEXITCODE -eq 1) {
  throw "The CultMath.dll in org.gamecult.cultmath $cultMathAvailable lacks types CultMathResolver formats: $missingTypes. Release CultMath first."
}
if ($LASTEXITCODE -ne 0) {
  throw "scripts\check-cultmath-types.cs failed with exit code $LASTEXITCODE."
}
# The tracked DLLs and pdbs are committed beside their source, so none may name a commit or a worktree:
# Source Link writes the commit SHA into the pdb, the informational version carries it too, and each DLL
# carries its pdb's content id. ContinuousIntegrationBuild maps the local source path to /_/.
# dotnet publish has no --no-incremental, so clear the configuration's intermediates instead: a stale obj
# directory must not decide what the byte check compares.
Get-ChildItem -LiteralPath (Join-Path $repoRoot "obj\src") -Directory -ErrorAction SilentlyContinue |
  ForEach-Object { Join-Path $_.FullName $Configuration } |
  Where-Object { Test-Path -LiteralPath $_ } |
  ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force }
$deterministicArguments = @("--disable-build-servers", "-p:UseSharedCompilation=false",
  "-p:ContinuousIntegrationBuild=true", "-p:EnableSourceLink=false",
  "-p:IncludeSourceRevisionInInformationalVersion=false", "-m:1")
$publishArguments = @("publish", $projectPath, "-c", $Configuration, "-o", $publishRoot, "-p:CultLibPackageVersion=$unityPackageVersion") + $deterministicArguments
if ($NoRestore) { $publishArguments += "--no-restore" }
if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
  $publishArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))"
}
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
  throw "CultLib publish failed with exit code $LASTEXITCODE"
}
$webSocketPublishArguments = @("publish", $webSocketProjectPath, "-c", $Configuration, "-f", "netstandard2.1", "-o", $webSocketPublishRoot, "-p:CultLibPackageVersion=$unityPackageVersion") + $deterministicArguments
if ($NoRestore) { $webSocketPublishArguments += "--no-restore" }
if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
  $webSocketPublishArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))"
}
& dotnet @webSocketPublishArguments
if ($LASTEXITCODE -ne 0) {
  throw "CultLib WebSocket publish failed with exit code $LASTEXITCODE"
}
$quicPublishArguments = @("publish", $quicProjectPath, "-c", $Configuration, "-o", $quicPublishRoot, "-p:CultLibPackageVersion=$unityPackageVersion") + $deterministicArguments
if ($NoRestore) { $quicPublishArguments += "--no-restore" }
if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
  $quicPublishArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))"
}
& dotnet @quicPublishArguments
if ($LASTEXITCODE -ne 0) {
  throw "CultLib native QUIC managed publish failed with exit code $LASTEXITCODE"
}
& (Join-Path $PSScriptRoot "build-quic-native.ps1") `
  -Configuration $Configuration `
  -OutputDirectory $quicNativeRoot

New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $templateRoot "package.json") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $templateRoot "README.md") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $templateRoot "CHANGELOG.md") -Destination $outputRoot

$expectedAssemblies = @(
  "ConcurrentCollections.dll",
  "GameCult.Caching.dll",
  "GameCult.Caching.MessagePack.dll",
  "GameCult.Logging.dll",
  "GameCult.Mesh.dll",
  "GameCult.Mesh.Quic.Native.dll",
  "GameCult.Networking.dll",
  "GameCult.Networking.WebSockets.dll",
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
# Assemblies the package references but never ships: org.gamecult.cultmath owns CultMath.dll, and the
# package.json dependency delivers it. A second copy in Runtime\Plugins would be a duplicate assembly.
$externalAssemblies = @("CultMath.dll")
$asmdef = Get-Content -LiteralPath (Join-Path $templateRoot "Runtime\GameCult.CultLib.asmdef") -Raw | ConvertFrom-Json
$referencedAssemblies = @($asmdef.precompiledReferences | Sort-Object)
$declaredAssemblies = @($expectedAssemblies + $externalAssemblies | Sort-Object)
if (($referencedAssemblies -join "|") -ne ($declaredAssemblies -join "|")) {
  throw "GameCult.CultLib.asmdef precompiledReferences differ from the shipped plus external assembly lists."
}
$publishedByName = @{}
foreach ($assembly in Get-ChildItem -LiteralPath $publishRoot -Filter "*.dll") {
  $publishedByName[$assembly.Name] = $assembly
}
foreach ($assembly in Get-ChildItem -LiteralPath $quicPublishRoot -Filter "*.dll") {
  $publishedByName[$assembly.Name] = $assembly
}
foreach ($assembly in Get-ChildItem -LiteralPath $webSocketPublishRoot -Filter "*.dll") {
  $publishedByName[$assembly.Name] = $assembly
}
foreach ($assemblyName in $expectedAssemblies) {
  if (-not $publishedByName.ContainsKey($assemblyName)) {
    throw "CultLib Unity package is missing expected assembly: $assemblyName"
  }
}

# docs/semver-policy.md: refuse a release whose version does not tell the truth. The check measures
# the public API of every shipped assembly against the DLLs the previous cultlib-unity tag
# tracked (read from git), so it runs here, once the assemblies under judgement exist. Every
# tracked assembly the build does not hand over counts as removed, so all of them are handed over.
# The package's tag prefix, changelog and assemblies directory are declared in
# scripts/release-packages.mjs; this call passes only the version and the built assemblies.
$measuredArguments = @()
foreach ($assemblyName in $expectedAssemblies) {
  $measuredArguments += @("--api-built", $publishedByName[$assemblyName].FullName)
}
foreach ($refRoot in @($publishRoot, $quicPublishRoot, $webSocketPublishRoot)) {
  $measuredArguments += @("--api-refs", $refRoot)
}
& node (Join-Path $repoRoot "scripts\check-changelog-semver.mjs") `
  --package "org.gamecult.cultlib" `
  --version $unityPackageVersion `
  --cwd $repoRoot `
  @measuredArguments
if ($LASTEXITCODE -ne 0) {
  throw "CultLib Unity package failed the semver policy check (see docs/semver-policy.md)."
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
Copy-Item -LiteralPath (Join-Path $quicNativeRoot "OPENSSL-NOTICE.txt") `
  -Destination (Join-Path $outputRoot "Third Party Notices\OPENSSL-NOTICE.txt")

$meshPackagePath = Join-Path $pluginRoot "GameCult.Mesh.dll"
$meshPackageVersion = [Reflection.AssemblyName]::GetAssemblyName($meshPackagePath).Version.ToString()
if ($meshPackageVersion -ne "$unityPackageVersion.0") {
  throw "Packaged GameCult.Mesh.dll version '$meshPackageVersion' does not match package '$unityPackageVersion'."
}
$meshPackageMetadata = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($meshPackagePath))
foreach ($requiredType in @(
  "GameCult.Mesh.CultMeshAuthorityTrustPolicy",
  "GameCult.Mesh.CultMeshRouteCertificate",
  "GameCult.Mesh.CultMeshHttpsContentTransportConnector")) {
  if (-not $meshPackageMetadata.Contains($requiredType.Split('.')[-1])) {
    throw "Packaged GameCult.Mesh.dll is missing required API '$requiredType'."
  }
}
$webSocketPackageMetadata = [Text.Encoding]::UTF8.GetString(
  [IO.File]::ReadAllBytes((Join-Path $pluginRoot "GameCult.Networking.WebSockets.dll")))
if (-not $webSocketPackageMetadata.Contains("CultNetWebSocketSchemaClient")) {
  throw "Packaged WebSocket assembly is missing the Unity-compatible CultNet client."
}

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
  Copy-Item -LiteralPath (Join-Path $outputRoot "Third Party Notices\OPENSSL-NOTICE.txt") `
    -Destination $templateNoticesRoot -Force
  Write-Host "Updated tracked Unity package assemblies: $templatePluginRoot"
}

$manifest = Get-Content -LiteralPath (Join-Path $outputRoot "package.json") -Raw | ConvertFrom-Json
Write-Host "CultLib Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($expectedAssemblies.Count)"
Write-Host "Native realtime: MsQuic OpenSSL 2.5.9 (Windows x64)"
