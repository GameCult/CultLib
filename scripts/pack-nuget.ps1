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

$projects = @(
  "src\GameCult.Logging\GameCult.Logging.csproj",
  "src\GameCult.Caching\GameCult.Caching.csproj",
  "src\GameCult.Caching.MessagePack.Analyzers\GameCult.Caching.MessagePack.Analyzers.csproj",
  "src\GameCult.Caching.MessagePack\GameCult.Caching.MessagePack.csproj",
  "src\GameCult.Networking\GameCult.Networking.csproj",
  "src\GameCult.Networking.WebSockets\GameCult.Networking.WebSockets.csproj",
  "src\GameCult.Mesh\GameCult.Mesh.csproj"
)

if (Test-Path -LiteralPath $outputRoot) {
  Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

foreach ($project in $projects) {
  $packArguments = @(
    "pack", (Join-Path $repoRoot $project), "-c", $Configuration, "-o", $outputRoot,
    "--nologo", "--verbosity", "quiet", "-p:NoWarn=1591%3BCS8632"
  )
  if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $packArguments += "-p:CultLibPackageVersion=$PackageVersion"
  }
  & dotnet @packArguments
  if ($LASTEXITCODE -ne 0) {
    throw "NuGet pack failed for $project with exit code $LASTEXITCODE"
  }
}

$meshPackage = Get-ChildItem -LiteralPath $outputRoot -Filter "GameCult.Mesh.*.nupkg" |
  Where-Object { $_.Name -notlike "*.snupkg" } |
  Select-Object -First 1
if (-not $meshPackage) {
  throw "GameCult.Mesh package was not produced."
}

$version = [regex]::Match($meshPackage.Name, '^GameCult\.Mesh\.(.+)\.nupkg$').Groups[1].Value
$expectedPackages = @(
  "GameCult.Logging.$version.nupkg",
  "GameCult.Caching.$version.nupkg",
  "GameCult.Caching.MessagePack.Analyzers.$version.nupkg",
  "GameCult.Caching.MessagePack.$version.nupkg",
  "GameCult.Networking.$version.nupkg",
  "GameCult.Networking.WebSockets.$version.nupkg",
  "GameCult.Mesh.$version.nupkg"
)
foreach ($package in $expectedPackages) {
  if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $package))) {
    throw "NuGet dependency closure is missing $package"
  }
}

if (-not $SkipConsumerSmoke) {
  $smokeRoot = Join-Path $repoRoot "artifacts\nuget-consumer-smoke"
  if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
  $projectDocument = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreSources>$outputRoot;https://api.nuget.org/v3/index.json</RestoreSources>
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
