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

New-Item -ItemType Directory -Force -Path $pluginRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $templateRoot "package.json") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $templateRoot "README.md") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\GameCult.Geometry.asmdef") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\GameCult.Geometry.Unity.asmdef") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\PlanetaryPatchMeshAdapter.cs") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\PlanetaryPatchMeshAdapter.cs.meta") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\PlanetaryPageUpload.cs") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Runtime\PlanetaryPageUpload.cs.meta") -Destination (Join-Path $outputRoot "Runtime")
Copy-Item -LiteralPath (Join-Path $templateRoot "Samples~") -Destination $outputRoot -Recurse
$shaderOutputRoot = Join-Path $outputRoot "Shaders"
New-Item -ItemType Directory -Force -Path $shaderOutputRoot | Out-Null
Copy-Item -Path (Join-Path $repoRoot "src\GameCult.Geometry\Shaders\*.hlsl") -Destination $shaderOutputRoot
Copy-Item -Path (Join-Path $repoRoot "src\GameCult.Geometry\Shaders\*.hlsl.meta") -Destination $shaderOutputRoot -ErrorAction SilentlyContinue
Copy-Item -LiteralPath (Join-Path $repoRoot "src\GameCult.Geometry\LICENSES.md") -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $repoRoot "src\GameCult.Geometry\THIRD-PARTY-NOTICES.md") -Destination $outputRoot

$geometryAssembly = Join-Path $publishRoot "GameCult.Geometry.dll"
if (-not (Test-Path -LiteralPath $geometryAssembly)) {
  throw "Geometry Unity package is missing GameCult.Geometry.dll"
}
Copy-Item -LiteralPath $geometryAssembly -Destination $pluginRoot
$geometrySymbols = [System.IO.Path]::ChangeExtension($geometryAssembly, ".pdb")
if (Test-Path -LiteralPath $geometrySymbols) {
  Copy-Item -LiteralPath $geometrySymbols -Destination $pluginRoot
}

$manifest = Get-Content -LiteralPath (Join-Path $outputRoot "package.json") -Raw | ConvertFrom-Json
$stagedAssemblies = @(Get-ChildItem -LiteralPath $pluginRoot -Filter "*.dll")
if ($stagedAssemblies.Count -ne 1 -or $stagedAssemblies[0].Name -ne "GameCult.Geometry.dll") {
  throw "Geometry package must stage only its owned assembly; found: $($stagedAssemblies.Name -join ', ')"
}

Write-Host "GameCult.Geometry Unity package: $outputRoot"
Write-Host "Package: $($manifest.name)@$($manifest.version)"
Write-Host "Dependencies: $($manifest.dependencies.PSObject.Properties.Name -join ', ')"
Write-Host "Managed assemblies: $($stagedAssemblies.Count)"
