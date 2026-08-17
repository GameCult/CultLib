param(
    [string] $PythonPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet -ErrorAction Stop
$node = Get-Command node -ErrorAction Stop
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

& $dotnet.Source test (Join-Path $root "tests\GameCult.Mesh.Tests\GameCult.Mesh.Tests.csproj") `
    --filter "FullyQualifiedName~ReactiveDocument_SchedulingScalesWithChangedDocumentsOnly" `
    --nologo `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "CultMesh C# reactive scaling verification failed."
}

Push-Location (Join-Path $root "packages\cultmesh-ts")
try {
    & $node.Source $typescript -p tsconfig.json --pretty false
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh TypeScript source build failed."
    }
    & $node.Source $typescript -p tsconfig.test.json --pretty false
    if ($LASTEXITCODE -ne 0) {
        throw "CultMesh TypeScript test build failed."
    }
    $testFiles = Get-ChildItem -LiteralPath .\dist-test\test -Filter "*.test.js" |
        Sort-Object Name |
        ForEach-Object FullName
    & $node.Source --test @testFiles
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
