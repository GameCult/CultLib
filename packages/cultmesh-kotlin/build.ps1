param(
  [string] $KotlinHome = "C:\Program Files\Android\Android Studio\plugins\Kotlin\kotlinc",
  [string] $JavaHome = "C:\Program Files\Android\Android Studio\jbr"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = Join-Path $PSScriptRoot "src\main\kotlin"
$out = Join-Path $repoRoot "artifacts\cultmesh-kotlin"
$jarPath = Join-Path $out "cultmesh-kotlin.jar"
$kotlinc = Join-Path $KotlinHome "bin\kotlinc.bat"
$kotlinStdlib = Join-Path $KotlinHome "lib\kotlin-stdlib.jar"
$java = Join-Path $JavaHome "bin\java.exe"

if (-not (Test-Path $kotlinc)) { throw "kotlinc not found: $kotlinc" }
if (-not (Test-Path $kotlinStdlib)) { throw "kotlin stdlib not found: $kotlinStdlib" }
if (-not (Test-Path $java)) { throw "java not found: $java" }

$env:JAVA_HOME = $JavaHome
$env:PATH = "$(Join-Path $JavaHome 'bin');$env:PATH"

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $out
New-Item -ItemType Directory -Force $out | Out-Null
$sources = Get-ChildItem $src -Recurse -Filter *.kt | Select-Object -ExpandProperty FullName
& $kotlinc @sources -jvm-target 1.8 -classpath $kotlinStdlib -d $jarPath
if ($LASTEXITCODE -ne 0) { throw "kotlinc failed with exit code $LASTEXITCODE" }
& $java -cp "$jarPath;$kotlinStdlib" org.gamecult.cultmesh.CultMeshKt
if ($LASTEXITCODE -ne 0) { throw "cultmesh-kotlin self-test failed with exit code $LASTEXITCODE" }
Write-Host "Built $jarPath"
