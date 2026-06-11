param(
    [Parameter(Mandatory = $true)]
    [string]$SpirvPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [int]$MslVersion = 23000,
    [string]$SpirvCrossPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $SpirvCrossPath) {
    $SpirvCrossPath = Join-Path $repoRoot ".tools\spirv-cross\build\spirv-cross.exe"
}

if (-not (Test-Path $SpirvCrossPath)) {
    throw "SPIRV-Cross not found at '$SpirvCrossPath'. Run tools\get-spirv-cross.ps1 first or pass -SpirvCrossPath."
}

$resolvedSpirv = Resolve-Path $SpirvPath
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

& $SpirvCrossPath $resolvedSpirv --msl --msl-version $MslVersion --output $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "spirv-cross failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $OutputPath
