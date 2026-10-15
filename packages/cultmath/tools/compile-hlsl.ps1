param(
    # The HLSL files to compile, each as a lib_6_3 library with its own directory as the include path.
    # Default: both copies of CultMath.hlsl (the canonical one and the Unity package's byte copy).
    # tools\compile-hlsl.ps1 -ShaderPath <a file holding 'float f( { return 0; }'> is the negative control.
    [string[]]$ShaderPath = @(),
    # The dxc to run. Default: the x64 install get-dxc.ps1 makes; it is never searched for, so a dxc
    # that cannot launch (one built for another architecture) is an error, not a skipped check.
    [string]$DxcPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $DxcPath) {
    $DxcPath = Join-Path $repoRoot ".tools\dxc\current\bin\x64\dxc.exe"
}
if (-not $ShaderPath) {
    $ShaderPath = @(
        (Join-Path $repoRoot "shaders\CultMath.hlsl"),
        (Join-Path $repoRoot "unity\org.gamecult.cultmath\Shaders\CultMath.hlsl"))
}
if (-not (Test-Path -LiteralPath $DxcPath)) {
    throw "dxc not found (parameter DxcPath). Run tools\get-dxc.ps1 first or pass -DxcPath."
}

$outputDirectory = Join-Path $repoRoot ".tools\hlsl"
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$index = 0
foreach ($shader in $ShaderPath) {
    $index++
    $shaderFile = Get-Item -LiteralPath $shader
    $output = Join-Path $outputDirectory "$index-$($shaderFile.BaseName).dxil"
    & $DxcPath -T lib_6_3 -HV 2021 -I $shaderFile.DirectoryName $shaderFile.FullName -Fo $output
    if ($LASTEXITCODE -ne 0) {
        throw "dxc failed with exit code $LASTEXITCODE (parameter ShaderPath, entry $index)."
    }
    Write-Host "compiled shader $index of $(@($ShaderPath).Count) (lib_6_3, -HV 2021)"
}
