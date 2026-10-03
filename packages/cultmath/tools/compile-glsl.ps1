param(
    # A GLSL source to append after the generated wrapper's library instead of the call-every-function
    # body; tools\compile-glsl.ps1 -Body 'float x = lerp(0.0, 1.0, 0.5);' is the negative control.
    [string]$Body = "",
    # Also concatenate shaders\CultMath.Phacelle.glsl (MPL-2.0) after CultMath.glsl, as a consumer that
    # calls cultmath_phacelle does. Without it the wrapper is the MIT library alone.
    [switch]$Phacelle,
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
$calls = foreach ($match in [regex]::Matches($library, '(?m)^\w+\s+(cultmath_\w+)\s*\(([^)]*)\)')) {
    $arguments = foreach ($parameter in ($match.Groups[2].Value -split ',')) {
        $type = ($parameter.Trim() -split '\s+')[0]
        if ($type -eq 'int' -or $type -like 'ivec*') { "$type(0)" }
        elseif ($type -eq 'uint' -or $type -like 'uvec*') { "$type(0u)" }
        else { "$type(0.0)" }
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
