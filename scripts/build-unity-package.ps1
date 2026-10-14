param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.cultlib",
  [string] $NuGetConfig = "",
  [switch] $NoRestore,
  [switch] $UpdateTemplate
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
# The CultMath byte gate below needs a git checkout: ContinuousIntegrationBuild maps the repo root to /_/
# through the SDK's git source root, so in a tree that is not the top of a git checkout (git archive, a
# source zip, an empty .git) the DLL names the absolute obj path and its bytes change with the directory.
# Git itself answers, so a worktree, where .git is a file, passes.
$gitErrorAction = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$gitTop = & git -C $repoRoot rev-parse --show-toplevel 2>$null
$gitExit = $LASTEXITCODE
$ErrorActionPreference = $gitErrorAction
$isGitTop = $gitExit -eq 0 -and $gitTop -and
  ([System.IO.Path]::GetFullPath("$gitTop").TrimEnd('\', '/') -eq [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/'))
if (-not $isGitTop) {
  throw "$repoRoot is not the top of a git checkout (git rev-parse --show-toplevel: '$gitTop', exit $gitExit), so CultMath.dll would carry this directory's path and fail the CultMath byte gate. Build from a git clone or worktree, not an archive or source zip."
}
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.cultlib"
# Every managed publish the package is assembled from, in the order $publishedByName fills (last wins).
# This list is the one owner of publish-root identity: the clean step, the publish loop, the assembly
# lookup, the CultMath byte gate and the semver check's --api-refs all read it.
$managedPublishes = @(
  @{ Project = "src\GameCult.Mesh\GameCult.Mesh.csproj"; Root = "artifacts\unity-publish"; Extra = @(); Label = "CultLib" },
  @{ Project = "src\GameCult.Mesh.Quic.Native\GameCult.Mesh.Quic.Native.csproj"; Root = "artifacts\unity-quic-publish"; Extra = @(); Label = "CultLib native QUIC managed" },
  @{ Project = "src\GameCult.Networking.WebSockets\GameCult.Networking.WebSockets.csproj"; Root = "artifacts\unity-websocket-publish"; Extra = @("-f", "netstandard2.1"); Label = "CultLib WebSocket" }
)
foreach ($publish in $managedPublishes) {
  $publish.Root = Join-Path $repoRoot $publish.Root
}
$quicNativeRoot = Join-Path $repoRoot "artifacts\unity-quic-native"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}
$pluginRoot = Join-Path $outputRoot "Runtime\Plugins"

foreach ($root in @($managedPublishes | ForEach-Object { $_.Root }) + $outputRoot) {
  if (Test-Path -LiteralPath $root) {
    Remove-Item -LiteralPath $root -Recurse -Force
  }
}

$unityPackageVersion = (Get-Content -LiteralPath (Join-Path $templateRoot "package.json") -Raw | ConvertFrom-Json).version

# Release gate: cultlib compiles CultMath from source and ships beside org.gamecult.cultmath, which owns
# CultMath.dll and is resolved through the package.json dependency. Two gates bind the two:
# (1) The declared dependency version must equal the CultMath package's own version. Checked here,
#     before anything is built.
# (2) Every CultMath.dll the publishes below produce must equal, byte for byte, the one that package
#     tracks. Checked once they exist. The builds are deterministic for one toolchain and checkout, so a
#     mismatch means the CultMath source changed since that package was released, its checkout's line
#     endings differ from the CRLF that .gitattributes pins, or this SDK, compiler or host builds
#     different bytes from the one that built the tracked DLL (no global.json pins the SDK).
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
if ([version]$cultMathDeclared -ne [version]$cultMathAvailable) {
  throw "Release order: org.gamecult.cultlib declares org.gamecult.cultmath $cultMathDeclared but the CultMath package is at $cultMathAvailable; cultlib compiles against that package's source, so they must be equal. Release CultMath first, then declare its version."
}
# The tracked DLLs and pdbs are committed beside their source, so none may name a commit or a worktree:
# Source Link writes the commit SHA into the pdb, the informational version carries it too, and each DLL
# carries its pdb's content id. ContinuousIntegrationBuild maps the local source path to /_/.
# dotnet publish has no --no-incremental, so clear the configuration's intermediates instead: a stale obj
# directory must not decide what the byte check compares. Directory.Build.props puts every project's
# intermediates under obj\<project dir>, CultMath's at obj\packages\cultmath\src\CultMath, so clear the
# configuration directory beside every restored project's project.assets.json, not only those under obj\src.
@(Get-ChildItem -LiteralPath (Join-Path $repoRoot "obj") -Recurse -File -Filter "project.assets.json" -ErrorAction SilentlyContinue) |
  ForEach-Object { Join-Path $_.DirectoryName $Configuration } |
  Where-Object { Test-Path -LiteralPath $_ } |
  ForEach-Object { Remove-Item -LiteralPath $_ -Recurse -Force }
$deterministicArguments = @("--disable-build-servers", "-p:UseSharedCompilation=false",
  "-p:ContinuousIntegrationBuild=true", "-p:EnableSourceLink=false",
  "-p:IncludeSourceRevisionInInformationalVersion=false", "-m:1")
foreach ($publish in $managedPublishes) {
  $publishArguments = @("publish", (Join-Path $repoRoot $publish.Project), "-c", $Configuration) + $publish.Extra +
    @("-o", $publish.Root, "-p:CultLibPackageVersion=$unityPackageVersion") + $deterministicArguments
  if ($NoRestore) { $publishArguments += "--no-restore" }
  if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
    $publishArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))"
  }
  & dotnet @publishArguments
  if ($LASTEXITCODE -ne 0) {
    throw "$($publish.Label) publish failed with exit code $LASTEXITCODE"
  }
}
# Gate (2): the CultMath bytes cultlib was built against are the bytes org.gamecult.cultmath ships.
$cultMathTrackedHash = (Get-FileHash -LiteralPath (Join-Path $cultMathPackageRoot "Runtime\Plugins\CultMath.dll") -Algorithm SHA256).Hash
$cultMathComparedRoots = @()
foreach ($publish in $managedPublishes) {
  $builtCultMath = Join-Path $publish.Root "CultMath.dll"
  if (-not (Test-Path -LiteralPath $builtCultMath)) { continue }
  if ((Get-FileHash -LiteralPath $builtCultMath -Algorithm SHA256).Hash -ne $cultMathTrackedHash) {
    throw "Release order: the CultMath.dll built into $($publish.Root) differs from the one org.gamecult.cultmath $cultMathAvailable tracks. Either CultMath source changed since that release, its checkout has other line endings, or this toolchain builds different bytes from the one that built the tracked DLL. Check first: git log cultmath-unity-v$cultMathAvailable.. -- packages/cultmath/src for a source change; git ls-files --eol packages/cultmath/src, where every file must read w/crlf (.gitattributes pins it; a clone checked out before that rule needs the files checked out again); then dotnet --version and the host against the build that produced the tracked DLL. A source change means release CultMath first, then declare its version; a toolchain difference means build with the toolchain that produced the tracked DLL."
  }
  $cultMathComparedRoots += $publish.Root
}
if ($cultMathComparedRoots.Count -eq 0) {
  throw "No managed publish produced CultMath.dll, so the CultMath byte gate measured nothing."
}
Write-Host "CultMath byte gate: CultMath.dll in $($cultMathComparedRoots -join ', ') equals org.gamecult.cultmath $cultMathAvailable's tracked DLL."
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
foreach ($publish in $managedPublishes) {
  foreach ($assembly in Get-ChildItem -LiteralPath $publish.Root -Filter "*.dll") {
    $publishedByName[$assembly.Name] = $assembly
  }
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
foreach ($publish in $managedPublishes) {
  $measuredArguments += @("--api-refs", $publish.Root)
}
# The suite pins the checker's rules and the declared tag prefixes, so it runs first.
& node --test (Join-Path $repoRoot "scripts\check-changelog-semver.test.mjs")
if ($LASTEXITCODE -ne 0) {
  throw "The release check suite failed (scripts/check-changelog-semver.test.mjs); not releasing."
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
