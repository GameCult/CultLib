param(
  [string] $Configuration = "Release",
  [string] $OutputDirectory = "artifacts\nuget",
  [string] $PackageVersion = "",
  [switch] $SkipConsumerSmoke
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
  $OutputDirectory
} else {
  Join-Path $repoRoot $OutputDirectory
}

# The public packages a consumer references directly. Everything they reach
# through ProjectReference is part of the published closure and is packed too,
# so a new internal dependency cannot leave the feed short of a package.
$rootProjects = @(
  "src\GameCult.Mesh\GameCult.Mesh.csproj",
  "src\GameCult.Networking.WebSockets\GameCult.Networking.WebSockets.csproj"
)

# MSBuild paths use backslashes; normalize them so the closure walk also
# resolves on Linux and macOS.
function Resolve-ProjectPath([string] $path) {
  return [IO.Path]::GetFullPath($path.Replace('\', [string][IO.Path]::DirectorySeparatorChar))
}

function Get-ProjectReferenceClosure([string[]] $roots) {
  $seen = [System.Collections.Generic.List[string]]::new()
  $pending = [System.Collections.Generic.Queue[string]]::new()
  foreach ($root in $roots) { $pending.Enqueue((Resolve-ProjectPath $root)) }
  while ($pending.Count -gt 0) {
    $project = $pending.Dequeue()
    if ($seen.Contains($project)) { continue }
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
      throw "Project in the NuGet closure was not found: $project"
    }
    $seen.Add($project)
    # Every ProjectReference counts, whatever its Condition: a reference
    # present for any target framework ships in the package's dependencies.
    foreach ($reference in ([xml](Get-Content -LiteralPath $project -Raw)).SelectNodes("//*[local-name()='ProjectReference']")) {
      $include = $reference.GetAttribute("Include").Trim()
      if ($include -match '\$\(') {
        throw "Cannot resolve the MSBuild property in ProjectReference '$include' from $project"
      }
      $pending.Enqueue((Resolve-ProjectPath (Join-Path (Split-Path -Parent $project) $include)))
    }
  }
  return $seen
}

$projects = Get-ProjectReferenceClosure ($rootProjects | ForEach-Object { Join-Path $repoRoot $_ })

if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$versionArguments = @()
if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
  $versionArguments += "-p:CultLibPackageVersion=$PackageVersion"
}

$expectedPackages = @()
foreach ($project in $projects) {
  # The package identity comes from MSBuild itself: GameCult.Math, for one,
  # carries its own PackageId and version rather than the CultLib version.
  $identity = (& dotnet msbuild $project -nologo "-getProperty:PackageId" "-getProperty:Version" @versionArguments) |
    Out-String | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) {
    throw "Could not read the package identity of $project"
  }
  $expectedPackages += "$($identity.Properties.PackageId).$($identity.Properties.Version).nupkg"

  $packArguments = @(
    "pack", $project, "-c", $Configuration, "-o", $outputRoot,
    "--nologo", "--verbosity", "quiet", "-p:NoWarn=1591%3BCS8632"
  ) + $versionArguments
  & dotnet @packArguments
  if ($LASTEXITCODE -ne 0) {
    throw "NuGet pack failed for $project with exit code $LASTEXITCODE"
  }
}

foreach ($package in $expectedPackages) {
  if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $package))) {
    throw "NuGet dependency closure is missing $package"
  }
}

$meshPackage = Get-ChildItem -LiteralPath $outputRoot -Filter "GameCult.Mesh.*.nupkg" |
  Where-Object { $_.Name -notlike "*.snupkg" } |
  Select-Object -First 1
if (-not $meshPackage) {
  throw "GameCult.Mesh package was not produced."
}
$version = [regex]::Match($meshPackage.Name, '^GameCult\.Mesh\.(.+)\.nupkg$').Groups[1].Value

if (-not $SkipConsumerSmoke) {
  $smokeRoot = Join-Path $repoRoot "artifacts\nuget-consumer-smoke"
  if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
  $consumerPackages = Join-Path $smokeRoot "packages"
  $projectDocument = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreSources>$outputRoot;https://api.nuget.org/v3/index.json</RestoreSources>
    <RestorePackagesPath>$consumerPackages</RestorePackagesPath>
    <RestoreNoCache>true</RestoreNoCache>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GameCult.Mesh" Version="$version" />
  </ItemGroup>
</Project>
"@
  $program = @"
using GameCult.Mesh;

var verse = CultMesh.Verse("nuget-smoke", "cultlib.pack");
Console.WriteLine(verse.Context.VerseId);
"@
  [IO.File]::WriteAllText((Join-Path $smokeRoot "Consumer.csproj"), $projectDocument, [Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText((Join-Path $smokeRoot "Program.cs"), $program, [Text.UTF8Encoding]::new($false))
  dotnet run --project (Join-Path $smokeRoot "Consumer.csproj") -c Release `
    --nologo --verbosity quiet
  if ($LASTEXITCODE -ne 0) {
    throw "GameCult.Mesh NuGet consumer smoke failed with exit code $LASTEXITCODE"
  }
}

Write-Host "CultLib NuGet feed: $outputRoot"
Write-Host "GameCult.Mesh: $version"
Write-Host "Public package closure: $($expectedPackages.Count) packages"
