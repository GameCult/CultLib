param(
    [Parameter(Mandatory = $true)]
    [string]$ShaderPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$EntryPoint = "main",
    [string]$Profile = "cs_6_0",
    [string[]]$IncludePath = @(),
    [string]$TargetEnv = "",
    [int]$VulkanCBufferShift = 0,
    [int]$VulkanTextureShift = 10,
    [int]$VulkanUavShift = 20,
    [int]$VulkanSamplerShift = 30,
    [string]$DxcPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $DxcPath) {
    $DxcPath = Join-Path $repoRoot ".tools\dxc\current\bin\x64\dxc.exe"
}

if (-not (Test-Path $DxcPath)) {
    throw "DXC not found at '$DxcPath'. Run tools\get-dxc.ps1 first or pass -DxcPath."
}

$resolvedShader = Resolve-Path $ShaderPath
$resolvedIncludes = @((Join-Path $repoRoot "shaders"))
foreach ($path in $IncludePath) {
    $resolvedIncludes += (Resolve-Path $path)
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$arguments = @(
    "-T", $Profile,
    "-E", $EntryPoint,
    "-spirv"
)

if ($TargetEnv) {
    $arguments += "-fspv-target-env=$TargetEnv"
}

$arguments += @(
    "-fvk-b-shift", $VulkanCBufferShift.ToString(), "0",
    "-fvk-t-shift", $VulkanTextureShift.ToString(), "0",
    "-fvk-u-shift", $VulkanUavShift.ToString(), "0",
    "-fvk-s-shift", $VulkanSamplerShift.ToString(), "0"
)

foreach ($include in $resolvedIncludes) {
    $arguments += @("-I", $include)
}

$arguments += @($resolvedShader, "-Fo", $OutputPath)

& $DxcPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dxc.exe failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $OutputPath
