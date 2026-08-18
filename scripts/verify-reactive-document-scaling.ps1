param(
    [string] $PythonPath = "",
    [string] $NodePath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction Stop
$nodeExecutable = if ([string]::IsNullOrWhiteSpace($NodePath)) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) { $node.Source } else { Join-Path $env:ProgramFiles "nodejs\node.exe" }
}
else {
    [IO.Path]::GetFullPath($NodePath)
}
if (-not (Test-Path -LiteralPath $nodeExecutable -PathType Leaf)) {
    throw "Node.js executable not found: $nodeExecutable"
}
$pythonExecutable = if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    (Get-Command python -ErrorAction Stop).Source
}
else {
    [IO.Path]::GetFullPath($PythonPath)
}
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw "Python executable not found: $pythonExecutable"
}
$typescript = Join-Path $root "node_modules\typescript\bin\tsc"
if (-not (Test-Path -LiteralPath $typescript)) {
    throw "TypeScript compiler not found: $typescript"
}

foreach ($dependency in @("cultcache-ts", "cultnet-ts")) {
    $dependencyRoot = Join-Path $root "packages\$dependency"
    Push-Location $dependencyRoot
    try {
        if ($dependency -eq "cultnet-ts") {
            & $nodeExecutable (Join-Path $dependencyRoot "tools\generate-swarm-contracts.mjs")
            if ($LASTEXITCODE -ne 0) {
                throw "CultNet TypeScript contract generation failed."
            }
        }
        & $nodeExecutable $typescript -p tsconfig.json --pretty false
        if ($LASTEXITCODE -ne 0) {
            throw "TypeScript dependency build failed: $dependency"
        }
    }
    finally {
        Pop-Location
    }
}

& $dotnet.Source test (Join-Path $root "tests\GameCult.Mesh.Tests\GameCult.Mesh.Tests.csproj") `
    --filter "FullyQualifiedName~ReactiveDocument_SchedulingScalesWithChangedDocumentsOnly" `
    --nologo `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "CultMesh C# reactive scaling verification failed."
}

Push-Location (Join-Path $root "packages\cultmesh-ts")
try {
    & $nodeExecutable $typescript -p tsconfig.json --pretty false
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh TypeScript source build failed."
    }
    & $nodeExecutable $typescript -p tsconfig.test.json --pretty false
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh TypeScript test build failed."
    }
    $testFiles = Get-ChildItem -LiteralPath .\dist-test\test -Filter "*.test.js" |
        Sort-Object Name |
        ForEach-Object FullName
    & $nodeExecutable --test @testFiles
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh TypeScript reactive scaling verification failed."
    }
}
finally {
    Pop-Location
}

$pythonPath = Join-Path $root "packages\cultcache-py\src"
$previousPythonPath = $env:PYTHONPATH
$env:PYTHONPATH = if ([string]::IsNullOrWhiteSpace($previousPythonPath)) {
    $pythonPath
}
else {
    $pythonPath + [IO.Path]::PathSeparator + $previousPythonPath
}
try {
    & $pythonExecutable -m unittest discover `
        -s (Join-Path $root "packages\cultcache-py\tests") `
        -p "test_cultcache.py" `
        -k "reactive_scheduling_scales_with_changed_documents_only"
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh Python reactive scaling verification failed."
    }
}
finally {
    $env:PYTHONPATH = $previousPythonPath
}

Write-Output '{"runtimes":["csharp","typescript","python"],"documentCounts":[1,100,1000],"changedFraction":0.01,"idleSchedules":0,"invariant":"scheduling is proportional to explicitly changed documents"}'
