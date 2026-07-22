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

dotnet build $projectPath -c $Configuration -f netstandard2.1 -o $buildRoot `
  --disable-build-servers -p:UseSharedCompilation=false -p:ContinuousIntegrationBuild=true -m:1
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
  "README.md",
  "THIRD-PARTY-NOTICES.md",
  "package.json",
  "Runtime\CultMath.asmdef",
  "Runtime\CultMath.asmdef.meta",
  "Runtime\Plugins\CultMath.dll",
  "Runtime\Plugins\CultMath.dll.meta",
  "Runtime\Plugins\CultMath.pdb",
  "Runtime\Plugins\CultMath.pdb.meta",
  "Shaders\CultMath.hlsl",
  "Shaders\CultMath.hlsl.meta"
)
foreach ($relativePath in $requiredTemplateFiles) {
  if (-not (Test-Path -LiteralPath (Join-Path $templateRoot $relativePath) -PathType Leaf)) {
    throw "Tracked CultMath Unity package is incomplete: missing $relativePath"
  }
}

$unityAssetFiles = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File | Where-Object {
  $_.Extension -in @(".asmdef", ".dll", ".pdb", ".hlsl")
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
if ($trackedSourceFiles.Count -ne 0) {
  throw "CultMath Unity package must not compile repository C# sources: $($trackedSourceFiles.FullName -join ', ')"
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -Path (Join-Path $templateRoot "*") -Destination $outputRoot -Recurse -Force

$stagedAssemblies = @(Get-ChildItem -LiteralPath (Join-Path $outputRoot "Runtime\Plugins") -Filter "*.dll")
$stagedSources = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter "*.cs")
if ($stagedAssemblies.Count -ne 1 -or $stagedAssemblies[0].Name -ne "CultMath.dll" -or $stagedSources.Count -ne 0) {
  throw "CultMath package must contain one owned DLL and no C# source files"
}

Write-Host "CultMath Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Managed assemblies: $($stagedAssemblies.Count)"
Write-Host "C# source files: $($stagedSources.Count)"
