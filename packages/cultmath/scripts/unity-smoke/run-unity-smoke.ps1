<#
.SYNOPSIS
  The release smoke for the tracked org.gamecult.cultmath Unity package: imports it into a
  scratch project, compiles and dispatches a compute shader that includes the package's
  CultMath.Interval.hlsl and CultMath.Affine.hlsl on a real graphics device, and proves a
  deliberately broken shader is rejected.

.DESCRIPTION
  Run it on a Windows machine with a GPU, alone, before tagging cultmath-unity-v*. The scratch
  project is built outside the repository under -WorkRoot, references the package by a file: path,
  and is deleted afterwards (-Keep leaves it). Exit 0 only when all three tests of SmokeTests.cs
  passed: the C# calls, the real shader dispatched with the right output, and the broken control
  shader detected.

  Never add -nographics. Without a device nothing compiles, and
  ShaderUtil.GetComputeShaderMessageCount reports 0 messages for a compute shader that calls an
  undeclared function until FindKernel or Dispatch compiles it, so a message-count check passes a
  broken shader. SmokeTests.cs compiles by FindKernel, dispatches, and reads the output back.

  For a long run start it detached and poll the log, for example:
    Start-Process powershell -WindowStyle Hidden -ArgumentList '-File', '<this script>', '-UnityExe', '<Unity.exe>', '-WorkRoot', '<dir>' 
  The script writes unity.pid and unity.log under the scratch root's logs folder (kept after the run).

.PARAMETER UnityExe
  Path to Unity.exe of the editor to test with (the cut that released 0.4.0 used 6000.3.24f1).
.PARAMETER WorkRoot
  Directory outside the repository for the scratch project and logs. Default: a new folder under TEMP.
.PARAMETER Keep
  Keep the scratch project instead of deleting it.
#>
param(
  [Parameter(Mandatory)] [string] $UnityExe,
  [string] $WorkRoot = (Join-Path $env:TEMP ("cultmath-unity-smoke-" + [guid]::NewGuid().ToString('N').Substring(0, 8))),
  [switch] $Keep
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf)) { throw "UnityExe not found: $UnityExe" }

$smoke = $PSScriptRoot
$package = (Resolve-Path (Join-Path $smoke '..\..\unity\org.gamecult.cultmath')).Path
$repoRoot = (git -C $smoke rev-parse --show-toplevel).Trim()
$work = [IO.Path]::GetFullPath($WorkRoot)
if ($work.StartsWith([IO.Path]::GetFullPath($repoRoot), [StringComparison]::OrdinalIgnoreCase)) {
  throw "WorkRoot must be outside the repository: $work"
}
$project = Join-Path $work 'project'
$logs = Join-Path $work 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
foreach ($d in 'Packages', 'Assets\Shaders', 'Assets\Tests') { New-Item -ItemType Directory -Force -Path (Join-Path $project $d) | Out-Null }

Copy-Item -LiteralPath (Join-Path $smoke 'Good.compute'), (Join-Path $smoke 'Broken.compute') -Destination (Join-Path $project 'Assets\Shaders')
Copy-Item -LiteralPath (Join-Path $smoke 'SmokeTests.cs'), (Join-Path $smoke 'CultMath.Smoke.Tests.asmdef') -Destination (Join-Path $project 'Assets\Tests')

$packageUri = 'file:' + $package.Replace([string][char]92, '/')
$manifest = [ordered]@{
  dependencies = [ordered]@{
    'com.unity.test-framework' = '1.6.0'
    'org.gamecult.cultmath'    = $packageUri
  }
}
[IO.File]::WriteAllText((Join-Path $project 'Packages\manifest.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))

$log = Join-Path $logs 'unity.log'
$results = Join-Path $logs 'results.xml'
$unityArgs = @('-batchmode', '-projectPath', $project, '-runTests', '-testPlatform', 'EditMode',
  '-testResults', $results, '-logFile', $log)
$p = Start-Process -PassThru -FilePath $UnityExe -ArgumentList $unityArgs -WindowStyle Hidden
$p.Id | Out-File (Join-Path $logs 'unity.pid') -Encoding ascii
"unity pid=$($p.Id) log=$log"
$p.WaitForExit()
"unity exit=$($p.ExitCode)"

$ok = $false
if (Test-Path -LiteralPath $results) {
  $cases = @(([xml](Get-Content -LiteralPath $results -Raw)).SelectNodes('//test-case'))
  $cases | ForEach-Object { "{0} {1}" -f $_.result, $_.name }
  $want = 'CSharpIntervalAndAffineWork', 'RealShaderCompilesAndDispatches', 'BrokenShaderIsDetected'
  $passed = @($cases | Where-Object { $_.result -eq 'Passed' } | ForEach-Object { ($_.name -split '\.')[-1] })
  $ok = (@($want | Where-Object { $passed -contains $_ }).Count -eq $want.Count)
} else { 'no results file' }
Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Select-String -CaseSensitive -Pattern '^SMOKE ' | ForEach-Object { $_.Line }

if (-not $Keep) { Remove-Item -LiteralPath $project -Recurse -Force }
if ($ok) { 'SMOKE PASSED'; exit 0 } else { 'SMOKE FAILED'; exit 1 }
