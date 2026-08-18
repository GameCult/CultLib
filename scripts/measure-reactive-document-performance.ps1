param(
    [switch] $Quick
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tools\GameCult.Mesh.PerformanceProbe\GameCult.Mesh.PerformanceProbe.csproj"
$buildArguments = @(
    "build",
    $project,
    "--configuration", "Release",
    "--verbosity", "quiet",
    "/p:NoWarn=1591%3BCS8632"
)
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "CultMesh reactive document performance probe build failed."
}

$arguments = @("run", "--project", $project, "--configuration", "Release", "--no-build")
if ($Quick) {
    $arguments += @("--", "--quick")
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "CultMesh reactive document performance probe failed."
}
