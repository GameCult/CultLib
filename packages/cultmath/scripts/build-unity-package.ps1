param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.cultmath"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\CultMath\CultMath.csproj"
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.cultmath"
$buildRoot = Join-Path $repoRoot "artifacts\cultmath-unity-build"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}
$freshAssembly = Join-Path $buildRoot "CultMath.dll"
$freshSymbols = Join-Path $buildRoot "CultMath.pdb"
$trackedPluginRoot = Join-Path $templateRoot "Runtime\Plugins"
$trackedAssembly = Join-Path $trackedPluginRoot "CultMath.dll"
$trackedSymbols = Join-Path $trackedPluginRoot "CultMath.pdb"

if (Test-Path -LiteralPath $buildRoot) {
  Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

# The tracked DLL and pdb are committed beside their source, so neither may name a commit: Source Link
# writes the commit SHA into the pdb, and the DLL carries that pdb's content id.
dotnet build $projectPath -c $Configuration -f netstandard2.1 -o $buildRoot `
  --disable-build-servers -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true -p:EnableSourceLink=false -m:1
if ($LASTEXITCODE -ne 0) {
  throw "CultMath Unity assembly build failed with exit code $LASTEXITCODE"
}

foreach ($path in @($freshAssembly, $freshSymbols)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "CultMath Unity build is missing $path"
  }
}

$requiredTemplateFiles = @(
  "LICENSE.md",
  "LICENSE.md.meta",
  "README.md",
  "README.md.meta",
  "THIRD-PARTY-NOTICES.md",
  "THIRD-PARTY-NOTICES.md.meta",
  "package.json",
  "package.json.meta",
  "Runtime\Plugins\CultMath.dll",
  "Runtime\Plugins\CultMath.dll.meta",
  "Runtime\Plugins\CultMath.pdb",
  "Runtime\Plugins\CultMath.pdb.meta",
  "Runtime\UnityBridge\CultMath.UnityBridge.asmdef",
  "Runtime\UnityBridge\CultMath.UnityBridge.asmdef.meta",
  "Runtime\UnityBridge\UnityConversions.cs",
  "Runtime\UnityBridge\UnityConversions.cs.meta",
  "Shaders\CultMath.hlsl",
  "Shaders\CultMath.hlsl.meta"
)
foreach ($relativePath in $requiredTemplateFiles) {
  if (-not (Test-Path -LiteralPath (Join-Path $templateRoot $relativePath) -PathType Leaf)) {
    throw "Tracked CultMath Unity package is incomplete: missing $relativePath"
  }
}

$unityAssetFiles = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File | Where-Object {
  $_.Extension -in @(".asmdef", ".cs", ".dll", ".pdb", ".hlsl")
})
$missingAssetMetas = @($unityAssetFiles | Where-Object { -not (Test-Path -LiteralPath ($_.FullName + ".meta")) })
$missingDirectoryMetas = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -Directory | Where-Object {
  -not (Test-Path -LiteralPath ($_.FullName + ".meta"))
})
if ($missingAssetMetas.Count -ne 0 -or $missingDirectoryMetas.Count -ne 0) {
  $missing = @($missingAssetMetas.FullName) + @($missingDirectoryMetas.FullName)
  throw "Tracked CultMath Unity package has assets without stable .meta files: $($missing -join ', ')"
}

$manifest = Get-Content -LiteralPath (Join-Path $templateRoot "package.json") -Raw | ConvertFrom-Json
$pluginMeta = Get-Content -LiteralPath ($trackedAssembly + ".meta") -Raw
if ($pluginMeta -notmatch '(?m)^\s*isExplicitlyReferenced:\s*0\s*$') {
  throw "CultMath.dll must be auto-referenced; explicit-only plugins do not reach dependent Unity asmdefs"
}
# The precompiled DLL is the only authority for CultMath's numeric types. The one
# compiled assembly allowed beside it is the UnityEngine conversion bridge, which
# references that DLL and must not re-declare core sources.
$bridgeRoot = Join-Path $templateRoot "Runtime\UnityBridge"
$bridgeDefinition = Join-Path $bridgeRoot "CultMath.UnityBridge.asmdef"
$definitions = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File -Filter "*.asmdef")
if ($definitions.Count -ne 1 -or $definitions[0].FullName -ne $bridgeDefinition) {
  throw "CultMath's Unity package may define only the Runtime\UnityBridge assembly; found: $($definitions.FullName -join ', ')"
}
$bridge = Get-Content -LiteralPath $bridgeDefinition -Raw | ConvertFrom-Json
if ($bridge.noEngineReferences -or -not $bridge.overrideReferences -or @($bridge.precompiledReferences) -notcontains "CultMath.dll") {
  throw "CultMath.UnityBridge must reference UnityEngine and the precompiled CultMath.dll"
}
$expectedFileVersion = "$($manifest.version).0"
$trackedFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($trackedAssembly).FileVersion
$freshFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($freshAssembly).FileVersion
if ($trackedFileVersion -ne $expectedFileVersion -or $freshFileVersion -ne $expectedFileVersion) {
  throw "CultMath Unity assemblies must match package version $expectedFileVersion; tracked=$trackedFileVersion fresh=$freshFileVersion"
}

foreach ($pair in @(@($freshAssembly, $trackedAssembly), @($freshSymbols, $trackedSymbols))) {
  $freshHash = (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash
  $trackedHash = (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash
  if ($freshHash -ne $trackedHash) {
    throw "Tracked Unity artifact is stale: $($pair[1]) differs from a fresh deterministic build"
  }
}

$trackedSourceFiles = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File -Filter "*.cs")
$straySources = @($trackedSourceFiles | Where-Object { $_.DirectoryName -ne $bridgeRoot })
if ($trackedSourceFiles.Count -eq 0 -or $straySources.Count -ne 0) {
  throw "CultMath Unity package may compile only bridge sources under Runtime\UnityBridge: $($straySources.FullName -join ', ')"
}

# Check what the bridge declares, not what its files are called: it may declare types only in
# CultMath.UnityBridge and never a type name the core CultMath sources declare.
$declarationPattern = '\b(?:struct|class|enum|interface|record|delegate\s+[\w<>.,\s]+?)\s+(\w+)'
function Get-CodeWithoutComments([string]$path) {
  $text = Get-Content -LiteralPath $path -Raw
  return [regex]::Replace($text, '(?s)/\*.*?\*/|//[^\r\n]*', '')
}
$coreTypeNames = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src\CultMath") -File -Filter "*.cs" | ForEach-Object {
  [regex]::Matches((Get-CodeWithoutComments $_.FullName), $declarationPattern) | ForEach-Object { $_.Groups[1].Value }
} | Sort-Object -Unique)
if ($coreTypeNames -notcontains "float3" -or $coreTypeNames -notcontains "math") {
  throw "Could not read CultMath core type declarations; the bridge check would be vacuous"
}
foreach ($source in $trackedSourceFiles) {
  $code = Get-CodeWithoutComments $source.FullName
  $redeclared = @([regex]::Matches($code, $declarationPattern) | ForEach-Object { $_.Groups[1].Value } | Where-Object { $coreTypeNames -contains $_ })
  $foreignNamespaces = @([regex]::Matches($code, '\bnamespace\s+([\w.]+)') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne "CultMath.UnityBridge" })
  if ($redeclared.Count -ne 0 -or $foreignNamespaces.Count -ne 0) {
    throw "CultMath.UnityBridge source $($source.Name) re-declares core types [$($redeclared -join ', ')] or uses namespaces [$($foreignNamespaces -join ', ')]; the precompiled DLL owns CultMath's types"
  }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -Path (Join-Path $templateRoot "*") -Destination $outputRoot -Recurse -Force

$stagedAssemblies = @(Get-ChildItem -LiteralPath (Join-Path $outputRoot "Runtime\Plugins") -Filter "*.dll")
$stagedBridgeRoot = Join-Path $outputRoot "Runtime\UnityBridge"
$stagedSources = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter "*.cs")
$stagedDefinitions = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter "*.asmdef")
$stagedStraySources = @($stagedSources | Where-Object { $_.DirectoryName -ne $stagedBridgeRoot })
if ($stagedAssemblies.Count -ne 1 -or $stagedAssemblies[0].Name -ne "CultMath.dll" -or $stagedStraySources.Count -ne 0 -or $stagedDefinitions.Count -ne 1) {
  throw "CultMath package must contain one owned auto-referenced DLL and only the UnityBridge assembly as source"
}

Write-Host "CultMath Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($stagedAssemblies.Count)"
Write-Host "Bridge C# source files: $($stagedSources.Count)"
Write-Host "Asmdef definitions: $($stagedDefinitions.Count)"
