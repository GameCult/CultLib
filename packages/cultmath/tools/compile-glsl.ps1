param(
    # A GLSL source to append after the generated wrapper's library instead of the call-every-function
    # body; tools\compile-glsl.ps1 -Body 'float x = lerp(0.0, 1.0, 0.5);' is the negative control.
    [string]$Body = "",
    # Also concatenate shaders\CultMath.Phacelle.glsl (MPL-2.0) after CultMath.glsl, as a consumer that
    # calls cultmath_phacelle does. Without it the wrapper is the MIT library alone.
    [switch]$Phacelle,
    # Also concatenate shaders\CultMath.Cycles.glsl (Apache-2.0), as a consumer that calls a cultmath_cycles_
    # function does.
    [switch]$Cycles,
    [string]$GlslangPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (-not $GlslangPath) {
    $found = Get-ChildItem -LiteralPath (Join-Path $repoRoot ".tools\glslang\current") -Recurse -File -Include "glslang.exe", "glslangValidator.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $GlslangPath = $found.FullName }
}

# The wrapper is what a WebGL2 consumer writes: its own #version line and precision, then the library
# by string concatenation (GLSL has no #include), then a fragment shader that calls every public
# function once with zero arguments of its declared types.
$library = Get-Content -LiteralPath (Join-Path $repoRoot "shaders\CultMath.glsl") -Raw
if ($Phacelle) {
    $library += Get-Content -LiteralPath (Join-Path $repoRoot "shaders\CultMath.Phacelle.glsl") -Raw
}
if ($Cycles) {
    $library += Get-Content -LiteralPath (Join-Path $repoRoot "shaders\CultMath.Cycles.glsl") -Raw
}
# A parameter of a struct type the library declares is passed as that struct's constructor with each
# field's zero, read from its declaration (a field may be a struct itself); every other type is its zero.
$structs = @{}
foreach ($declaration in [regex]::Matches($library, '(?s)struct (\w+)\s*\{(.*?)\};')) {
    $structs[$declaration.Groups[1].Value] = @([regex]::Matches($declaration.Groups[2].Value, '(?m)^\s*(\w+)\s+\w+\s*;') | ForEach-Object { $_.Groups[1].Value })
}
function Get-Zero([string]$type) {
    if ($structs.ContainsKey($type)) { return "$type($((@($structs[$type] | ForEach-Object { Get-Zero $_ })) -join ', '))" }
    if ($type -eq 'int' -or $type -like 'ivec*') { return "$type(0)" }
    if ($type -eq 'uint' -or $type -like 'uvec*') { return "$type(0u)" }
    return "$type(0.0)"
}
$calls = foreach ($match in [regex]::Matches($library, '(?m)^\w+\s+(cultmath_\w+)\s*\(([^)]*)\)')) {
    $arguments = foreach ($parameter in ($match.Groups[2].Value -split ',' | Where-Object { $_.Trim() })) {
        Get-Zero (($parameter.Trim() -split '\s+')[0])
    }
    "    $($match.Groups[1].Value)($($arguments -join ', '));"
}
if (-not $Body) {
    $Body = $calls -join "`n"
}

$wrapper = "#version 300 es`nprecision highp float;`nprecision highp int;`n$library`nout vec4 cultmathColor;`nvoid main()`n{`n$Body`n    cultmathColor = vec4(0.0);`n}`n"
$wrapperPath = Join-Path $repoRoot ".tools\glsl\cultmath-wrapper.frag"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $wrapperPath) | Out-Null
[System.IO.File]::WriteAllText($wrapperPath, $wrapper)
Write-Host "wrapper: $wrapperPath ($(@($calls).Count) functions)"

if (-not $GlslangPath -or -not (Test-Path $GlslangPath)) {
    throw "glslang not found. Run tools\get-glslang.ps1 first or pass -GlslangPath."
}

& $GlslangPath -S frag $wrapperPath
if ($LASTEXITCODE -ne 0) {
    throw "glslang failed with exit code $LASTEXITCODE."
}
