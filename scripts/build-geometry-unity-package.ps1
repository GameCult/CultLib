param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\unity\org.gamecult.geometry",
  [string] $CultMathCandidateFeed = "..\CultMath\.tools\local-feed"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\GameCult.Geometry\GameCult.Geometry.csproj"
$templateRoot = Join-Path $repoRoot "unity\org.gamecult.geometry"
$publishRoot = Join-Path $repoRoot "artifacts\geometry-unity-publish"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}
$candidateFeed = if ([System.IO.Path]::IsPathRooted($CultMathCandidateFeed)) {
  $CultMathCandidateFeed
} else {
  Join-Path $repoRoot $CultMathCandidateFeed
}
$pluginRoot = Join-Path $outputRoot "Runtime\Plugins"
$templatePluginRoot = Join-Path $templateRoot "Runtime\Plugins"

if (-not (Test-Path -LiteralPath (Join-Path $candidateFeed "CultMath.0.1.0-geometry-migration.2.nupkg"))) {
  throw "CultMath migration candidate is missing from $candidateFeed. Pack CultMath before staging Geometry."
}

if (Test-Path -LiteralPath $publishRoot) {
  Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

dotnet restore $projectPath --disable-build-servers -p:CultMathCandidateFeed=$candidateFeed -p:UseSharedCompilation=false -m:1
if ($LASTEXITCODE -ne 0) {
  throw "GameCult.Geometry restore failed with exit code $LASTEXITCODE"
}

dotnet publish $projectPath -c $Configuration -o $publishRoot --no-restore --disable-build-servers -p:UseSharedCompilation=false -m:1
if ($LASTEXITCODE -ne 0) {
  throw "GameCult.Geometry publish failed with exit code $LASTEXITCODE"
}

$geometryAssembly = Join-Path $publishRoot "GameCult.Geometry.dll"
if (-not (Test-Path -LiteralPath $geometryAssembly)) {
  throw "Geometry Unity package is missing GameCult.Geometry.dll"
}
$geometrySymbols = [System.IO.Path]::ChangeExtension($geometryAssembly, ".pdb")

$requiredTemplateFiles = @(
  "LICENSES.md",
  "THIRD-PARTY-NOTICES.md",
  "Runtime\Plugins\GameCult.Geometry.dll",
  "Runtime\Plugins\GameCult.Geometry.dll.meta",
  "Runtime\Plugins\GameCult.Geometry.pdb",
  "Runtime\Plugins\GameCult.Geometry.pdb.meta",
  "Shaders\AdvancedErosionFilter.hlsl",
  "Shaders\GameCult.Geometry.hlsl",
  "Shaders\Planetary.hlsl",
  "Shaders\PlanetaryRadialRefinement.hlsl",
  "Shaders\SphericalErosion.hlsl"
)
foreach ($relativePath in $requiredTemplateFiles) {
  if (-not (Test-Path -LiteralPath (Join-Path $templateRoot $relativePath))) {
    throw "Tracked Geometry Unity package is incomplete: missing $relativePath"
  }
}

$unityAssetFiles = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -File | Where-Object {
  $_.Extension -in @(".asmdef", ".cs", ".dll", ".pdb", ".shader", ".hlsl")
})
$missingAssetMetas = @($unityAssetFiles | Where-Object { -not (Test-Path -LiteralPath ($_.FullName + ".meta")) })
$missingDirectoryMetas = @(Get-ChildItem -LiteralPath $templateRoot -Recurse -Directory | Where-Object {
  -not (Test-Path -LiteralPath ($_.FullName + ".meta"))
})
if ($missingAssetMetas.Count -ne 0 -or $missingDirectoryMetas.Count -ne 0) {
  $missing = @($missingAssetMetas.FullName) + @($missingDirectoryMetas.FullName)
  throw "Tracked Geometry Unity package has assets without stable .meta files: $($missing -join ', ')"
}

$trackedAssembly = Join-Path $templatePluginRoot "GameCult.Geometry.dll"
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $trackedAssembly).Hash -ne
    (Get-FileHash -Algorithm SHA256 -LiteralPath $geometryAssembly).Hash) {
  throw "Tracked GameCult.Geometry.dll does not match the current Release publish output. Sync the tracked Git-UPM package before tagging."
}
if (Test-Path -LiteralPath $geometrySymbols) {
  $trackedSymbols = Join-Path $templatePluginRoot "GameCult.Geometry.pdb"
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $trackedSymbols).Hash -ne
      (Get-FileHash -Algorithm SHA256 -LiteralPath $geometrySymbols).Hash) {
    throw "Tracked GameCult.Geometry.pdb does not match the current Release publish output. Sync the tracked Git-UPM package before tagging."
  }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Copy-Item -Path (Join-Path $templateRoot "*") -Destination $outputRoot -Recurse -Force

$manifest = Get-Content -LiteralPath (Join-Path $outputRoot "package.json") -Raw | ConvertFrom-Json
$stagedAssemblies = @(Get-ChildItem -LiteralPath $pluginRoot -Filter "*.dll")
if ($stagedAssemblies.Count -ne 1 -or $stagedAssemblies[0].Name -ne "GameCult.Geometry.dll") {
  throw "Geometry package must stage only its owned assembly; found: $($stagedAssemblies.Name -join ', ')"
}

Write-Host "GameCult.Geometry Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Dependencies: $($manifest.dependencies.PSObject.Properties.Name -join ', ')"
Write-Host "Managed assemblies: $($stagedAssemblies.Count)"
