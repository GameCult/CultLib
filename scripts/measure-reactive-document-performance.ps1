param(
    [switch] $Quick,
    [switch] $SkipRuntimeBuild,
    [string] $PythonPath = "",
    [string] $NodePath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$nodeExecutable = if ([string]::IsNullOrWhiteSpace($NodePath)) {
    (Get-Command node -ErrorAction Stop).Source
}
else {
    [IO.Path]::GetFullPath($NodePath)
}
$pythonExecutable = if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    (Get-Command python -ErrorAction Stop).Source
}
else {
    [IO.Path]::GetFullPath($PythonPath)
}
foreach ($executable in @($nodeExecutable, $pythonExecutable)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Performance probe runtime not found: $executable"
    }
}

if (-not $SkipRuntimeBuild) {
    $typescript = Join-Path $root "node_modules\typescript\bin\tsc"
    foreach ($dependency in @("cultcache-ts", "cultnet-ts", "cultmesh-ts")) {
        $dependencyRoot = Join-Path $root "packages\$dependency"
        if ($dependency -eq "cultnet-ts") {
            & $nodeExecutable (Join-Path $dependencyRoot "tools\generate-swarm-contracts.mjs")
            if ($LASTEXITCODE -ne 0) { throw "CultNet TypeScript contract generation failed." }
        }
        & $nodeExecutable $typescript -p (Join-Path $dependencyRoot "tsconfig.json") --pretty false
        if ($LASTEXITCODE -ne 0) { throw "TypeScript performance dependency build failed: $dependency" }
    }
}

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
    throw "CultMesh C# reactive document performance probe failed."
}

$runtimeArguments = @()
if ($Quick) { $runtimeArguments += "--quick" }

& $nodeExecutable --expose-gc (Join-Path $root "scripts\measure-reactive-document-performance.mjs") @runtimeArguments
if ($LASTEXITCODE -ne 0) {
    throw "CultMesh TypeScript reactive document performance probe failed."
}

$pythonSource = Join-Path $root "packages\cultcache-py\src"
$previousPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = if ([string]::IsNullOrWhiteSpace($previousPythonPath)) {
    $pythonSource
}
else {
    $pythonSource + [IO.Path]::PathSeparator + $previousPythonPath
}
try {
    & $pythonExecutable (Join-Path $root "packages\cultcache-py\tools\reactive_performance_probe.py") @runtimeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh Python reactive document performance probe failed."
    }
}
finally {
    $env:PYTHONPATH = $previousPythonPath
}
