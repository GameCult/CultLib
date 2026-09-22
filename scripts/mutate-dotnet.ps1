# Generic mutation runner for dotnet/NUnit suites - the CultNet selection campaign's counterpart to
# tools/eureka-mutations.ps1, whose Invoke-Cargo parses `cargo test` output and appends `-- --exact
# <test>`, and is therefore not usable against `dotnet test`. This is not scripts/mutate-cultmesh.mjs
# either - that file is the QUIC campaign's, live and dirty in this tree, and per
# docs/cultnet-selection-cut.md section 10 this campaign adds its entries in its own file rather than
# editing the QUIC campaign's targets.
#
# One copy serves every dotnet target in this campaign (and, once proven out, any future one): pass
# -Target, -TestProject and -Entries. Same discipline as the cargo harness: every entry is a byte-exact
# splice against one anchor that must match exactly once, restored from the original bytes (never from
# git) with a SHA-256 check per target, a sidecar (<target>.mutation-original) protects a hard kill
# mid-mutation, and M0 proves the read/write path round-trips and the unmutated suite is green before
# any entry runs.
#
#   powershell -File scripts/mutate-dotnet.ps1 `
#       -Target src\GameCult.Caching\CultCache.cs `
#       -TestProject tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj `
#       -Entries scripts\mutate-cultnet-selection-caching.entries.ps1 `
#       -ControlFilter 'FullyQualifiedName~CultDocumentSelectionSurfaceTests'
#
# -Target        one or more files entries mutate, relative to the repo root. An entry with no `File`
#                uses the sole target; an entry with more than one target names each edit's `File`.
# -TestProject   the .csproj `dotnet test` runs. One project per invocation; entries needing another
#                project are a separate run (as networking and caching already are).
# -Entries       a .ps1 that returns an array of `@{ Id; Rule; Mutant; Killer; Old; New; File? }` (or
#                `Edits = @(@{ File; Old; New }, ...)` for a multi-file entry). `Killer` is the
#                `dotnet test --filter` value for the one test that must fail under this mutant.
# -ControlFilter the `dotnet test --filter` M0 runs to prove the suite is green unmutated. Defaults to
#                running the whole project.
# -Repo          repo root -Target/-TestProject/-Entries are relative to. Defaults to this script's
#                parent's parent (scripts/..).
#
# Exit code: 0 when every entry's mutant was KILLED, non-zero otherwise (SURVIVED or NO VERDICT).

param(
    [Parameter(Mandatory = $true)] [string[]] $Target,
    [Parameter(Mandatory = $true)] [string] $TestProject,
    [Parameter(Mandatory = $true)] [string] $Entries,
    [string] $ControlFilter,
    [string] $Repo
)

$ErrorActionPreference = 'Stop'

$repo = if ($Repo) { (Resolve-Path -LiteralPath $Repo).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$testProjectPath = Join-Path $repo $TestProject
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Read-Bytes([string]$path) { [System.IO.File]::ReadAllBytes($path) }
function Get-BytesHash([byte[]]$bytes) {
    ([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
}
function Write-Text([string]$path, [string]$text) {
    [System.IO.File]::WriteAllBytes($path, [byte[]]($utf8.GetPreamble() + $utf8.GetBytes($text)))
}
function Get-SidecarPath([string]$path) { "$path.mutation-original" }

function Run-Tests([string]$filter) {
    $args = @('test', $testProjectPath, '-c', 'Debug', '--nologo')
    if ($filter) { $args += @('--filter', $filter) }
    # A test-host crash (e.g. S2-1's disposed-socket race on a background poll thread) can write to
    # stderr; under PowerShell 5.1, `2>&1` on a native command wraps each stderr line in a
    # NativeCommandError, and with the script's own $ErrorActionPreference = 'Stop' that becomes a
    # terminating error that would otherwise throw the whole run away. Run this one call under
    # 'Continue' and catch anything that still throws, so a host crash is captured as output and
    # classified as NO VERDICT by the caller instead of aborting every remaining entry.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $crashed = $false
    try {
        $output = & dotnet @args 2>&1 | Out-String
    }
    catch {
        $output = "$_"
        $crashed = $true
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    Write-Host $output
    $passedMatch = [regex]::Match($output, 'Passed:\s+(\d+)')
    $failedMatch = [regex]::Match($output, 'Failed:\s+(\d+)')
    $compiled = -not ($output -match 'error CS\d+')
    $passedLine = $output -match 'Passed!'
    $failedLine = $output -match 'Failed!'
    [pscustomobject]@{
        Compiled = $compiled
        Crashed  = $crashed
        Green    = (-not $crashed) -and $compiled -and $passedLine -and -not $failedLine -and $passedMatch.Success -and [int]$passedMatch.Groups[1].Value -ge 1
        Passed   = if ($passedMatch.Success) { [int]$passedMatch.Groups[1].Value } else { 0 }
        Failed   = if ($failedMatch.Success) { [int]$failedMatch.Groups[1].Value } else { 0 }
    }
}

$targets = @{}
foreach ($file in $Target) {
    $path = Join-Path $repo $file
    if (-not (Test-Path -LiteralPath $path)) { throw "Target $file does not exist." }
    $targets[$file] = $path
}

# --- Repair a run that died mid-mutation, for every target -------------------

foreach ($file in $Target) {
    $sidecar = Get-SidecarPath $targets[$file]
    if (Test-Path -LiteralPath $sidecar) {
        $original = Read-Bytes $sidecar
        [System.IO.File]::WriteAllBytes($targets[$file], $original)
        Remove-Item -LiteralPath $sidecar -Force
        Write-Host "Repaired: a previous run left $sidecar; $($targets[$file]) restored from it and the sidecar removed."
    }
}

# --- M0: no-op control, every target -----------------------------------------

Write-Host '--- M0: no-op control'
$originalBytes = @{}
$originalHash = @{}
foreach ($file in $Target) {
    $path = $targets[$file]
    $bytes = Read-Bytes $path
    $hash = Get-BytesHash $bytes
    $text = $utf8.GetString($bytes)
    $reencoded = [byte[]]($utf8.GetPreamble() + $utf8.GetBytes($text))
    if ((Get-BytesHash $reencoded) -ne $hash) {
        throw "harness broken: $file does not round-trip through the UTF-8 read/write path byte for byte. Nothing was written and no entry ran."
    }
    $sidecar = Get-SidecarPath $path
    [System.IO.File]::WriteAllBytes($sidecar, $bytes)
    try {
        Write-Text $path $text
        if ((Get-BytesHash (Read-Bytes $path)) -ne $hash) {
            throw "harness broken: writing $file back through the harness path changed its bytes."
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($path, $bytes)
        if ((Get-BytesHash (Read-Bytes $path)) -ne $hash) { throw "RESTORE FAILED after M0 for $file; original SHA-256 $hash is kept in $sidecar." }
        Remove-Item -LiteralPath $sidecar -Force
    }
    $originalBytes[$file] = $bytes
    $originalHash[$file] = $hash
    Write-Host "M0: $file decoded, re-encoded and rewritten, bytes unchanged (SHA-256 $hash)"
}
$control = Run-Tests $ControlFilter
if (-not $control.Green) {
    throw "harness broken: the suite is not green unmutated (compiled=$($control.Compiled) passed=$($control.Passed) failed=$($control.Failed)). No entry ran."
}
Write-Host "M0: green, $($control.Passed) passed"

# --- Load entries --------------------------------------------------------------

$entriesPath = Join-Path $repo $Entries
$mutations = & $entriesPath
if (-not $mutations -or @($mutations).Count -lt 1) { throw "$Entries`: no mutations." }

# --- Entries -----------------------------------------------------------------

$exitCode = 0
foreach ($mutation in $mutations) {
    if (-not $mutation.Id) { throw 'An entry has no Id.' }
    if (-not $mutation.Killer) { throw "$($mutation.Id): no Killer." }
    $edits = if ($mutation.ContainsKey('Edits')) { @($mutation.Edits) } else { @(@{ File = $mutation.File; Old = $mutation.Old; New = $mutation.New }) }
    if ($edits.Count -lt 1) { throw "$($mutation.Id): no edits." }

    Write-Host "--- $($mutation.Id) ($($mutation.Mutant)): $($mutation.Rule)"
    $texts = @{}
    $files = @($edits | ForEach-Object { if ($_.File) { $_.File } elseif ($Target.Count -eq 1) { $Target[0] } else { throw "$($mutation.Id): an edit names no File and there is more than one target." } } | Select-Object -Unique)
    foreach ($file in $files) { $texts[$file] = $utf8.GetString($originalBytes[$file]) }

    foreach ($edit in $edits) {
        $file = if ($edit.File) { $edit.File } else { $Target[0] }
        $text = $texts[$file]
        $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $old = ($edit.Old -replace "`r`n", "`n")
        $normalized = $text -replace "`r`n", "`n"
        $count = 0
        $at = $normalized.IndexOf($old, [System.StringComparison]::Ordinal)
        while ($at -ge 0) { $count++; $at = $normalized.IndexOf($old, $at + 1, [System.StringComparison]::Ordinal) }
        if ($count -ne 1) { throw "$($mutation.Id): anchor matches $count times in $file, expected exactly 1." }
        $newValue = if ($null -ne $edit.New) { $edit.New } else { '' }
        $mutatedNormalized = $normalized.Replace($old, ($newValue -replace "`r`n", "`n"))
        $texts[$file] = $mutatedNormalized -replace "`n", $eol
    }

    foreach ($file in $files) { [System.IO.File]::WriteAllBytes((Get-SidecarPath $targets[$file]), $originalBytes[$file]) }
    $verdict = 'NO VERDICT'
    try {
        foreach ($file in $files) { Write-Text $targets[$file] $texts[$file] }
        $run = Run-Tests $mutation.Killer
        if ($run.Crashed) { $verdict = 'NO VERDICT (test host crashed)' }
        elseif (-not $run.Compiled) { $verdict = 'NO VERDICT (did not compile)' }
        elseif ($run.Failed -ge 1) { $verdict = 'KILLED' }
        else { $verdict = 'SURVIVED' }
    }
    catch {
        # Not yet reached by Run-Tests itself (it no longer throws on a host crash), but a defensive
        # backstop: any other exception during this entry's run is recorded and the campaign continues
        # rather than aborting every remaining entry, never labeled "unreachable".
        Write-Host "NO VERDICT: $($mutation.Id) - test run threw: $_"
        $verdict = 'NO VERDICT (harness exception)'
    }
    finally {
        foreach ($file in $files) {
            [System.IO.File]::WriteAllBytes($targets[$file], $originalBytes[$file])
            if ((Get-BytesHash (Read-Bytes $targets[$file])) -ne $originalHash[$file]) {
                Write-Host "RESTORE FAILED: $($targets[$file]) not restored; original SHA-256 $($originalHash[$file]) kept in $(Get-SidecarPath $targets[$file])."
                throw "restore failed for $($mutation.Id)"
            }
            Remove-Item -LiteralPath (Get-SidecarPath $targets[$file]) -Force
        }
    }
    Write-Host "$($mutation.Id): $verdict"
    if ($verdict -ne 'KILLED') { $exitCode = 1 }
}

$final = Run-Tests $ControlFilter
if (-not $final.Green) { throw "post-run sanity check: the suite is not green after restore (passed=$($final.Passed) failed=$($final.Failed))." }
Write-Host "Final: green, $($final.Passed) passed"

exit $exitCode
