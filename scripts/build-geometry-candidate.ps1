param(
  [string] $Version = "0.1.0-geometry-migration.2",
  [string] $OutputDirectory = "artifacts\candidate-feed",
  [string] $CultMathCandidateFeed = "..\CultMath\.tools\local-feed"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\GameCult.Geometry\GameCult.Geometry.csproj"
$output = Join-Path $root $OutputDirectory
$feed = [IO.Path]::GetFullPath((Join-Path $root $CultMathCandidateFeed))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$supportProjects = @(
  "src\GameCult.Logging\GameCult.Logging.csproj",
  "src\GameCult.Caching\GameCult.Caching.csproj",
  "src\GameCult.Caching.MessagePack.Analyzers\GameCult.Caching.MessagePack.Analyzers.csproj",
  "src\GameCult.Caching.MessagePack\GameCult.Caching.MessagePack.csproj",
  "src\GameCult.Networking\GameCult.Networking.csproj",
  "src\GameCult.Mesh\GameCult.Mesh.csproj"
)
foreach ($relativeProject in $supportProjects) {
  $supportProject = Join-Path $root $relativeProject
  dotnet pack $supportProject -c Release -o $output --disable-build-servers -p:PackageVersion=$Version -p:UseSharedCompilation=false -m:1
  if ($LASTEXITCODE -ne 0) { throw "Candidate support pack failed: $relativeProject" }
}
dotnet restore $project --disable-build-servers -p:CultMathCandidateFeed=$feed -p:UseSharedCompilation=false -m:1
if ($LASTEXITCODE -ne 0) { throw "Geometry candidate restore failed." }
dotnet pack $project -c Release -o $output --no-restore --disable-build-servers -p:PackageVersion=$Version -p:CultMathCandidateFeed=$feed -p:UseSharedCompilation=false -m:1
if ($LASTEXITCODE -ne 0) { throw "Geometry candidate pack failed." }
$package = Join-Path $output "GameCult.Geometry.$Version.nupkg"
if (-not (Test-Path -LiteralPath $package)) { throw "Geometry candidate package was not produced." }
Write-Host $package
