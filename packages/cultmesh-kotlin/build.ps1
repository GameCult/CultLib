param(
  [string] $KotlinHome = $(if ($env:KOTLIN_HOME) { $env:KOTLIN_HOME } else { "C:\Program Files\Android\Android Studio\plugins\Kotlin\kotlinc" }),
  [string] $JavaHome = $(if ($env:KOTLIN_JAVA_HOME) { $env:KOTLIN_JAVA_HOME } elseif ($env:JAVA_HOME) { $env:JAVA_HOME } else { "C:\Program Files\Android\Android Studio\jbr" })
)

$ErrorActionPreference = "Stop"
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$pathSeparator = [IO.Path]::PathSeparator
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$src = Join-Path $PSScriptRoot (Join-Path "src" (Join-Path "main" "kotlin"))
$out = Join-Path (Join-Path $repoRoot "artifacts") "cultmesh-kotlin"
$jarPath = Join-Path $out "cultmesh-kotlin.jar"
$kotlinc = Join-Path (Join-Path $KotlinHome "bin") $(if ($onWindows) { "kotlinc.bat" } else { "kotlinc" })
$kotlinStdlib = Join-Path (Join-Path $KotlinHome "lib") "kotlin-stdlib.jar"
$java = Join-Path (Join-Path $JavaHome "bin") $(if ($onWindows) { "java.exe" } else { "java" })

if (-not (Test-Path $kotlinc)) { throw "kotlinc not found: $kotlinc" }
if (-not (Test-Path $kotlinStdlib)) { throw "kotlin stdlib not found: $kotlinStdlib" }
if (-not (Test-Path $java)) { throw "java not found: $java" }

$env:JAVA_HOME = $JavaHome
$env:PATH = "$(Join-Path $JavaHome 'bin')$pathSeparator$env:PATH"

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $out
New-Item -ItemType Directory -Force $out | Out-Null
$sources = Get-ChildItem $src -Recurse -Filter *.kt | Select-Object -ExpandProperty FullName
& $kotlinc @sources -jvm-target 1.8 -classpath $kotlinStdlib -d $jarPath
if ($LASTEXITCODE -ne 0) { throw "kotlinc failed with exit code $LASTEXITCODE" }
& $java -cp "$jarPath$pathSeparator$kotlinStdlib" org.gamecult.cultmesh.CultMeshKt
if ($LASTEXITCODE -ne 0) { throw "cultmesh-kotlin self-test failed with exit code $LASTEXITCODE" }
Write-Host "Built $jarPath"
