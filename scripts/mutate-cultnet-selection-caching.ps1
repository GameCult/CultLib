# Mutation check for CultNet typed selection, Cut 1, commit 0: the cache's
# public read surface (docs/cultnet-selection-cut.md, section 6, D8/D10/D11).
#
# This is a dedicated runner for src/GameCult.Caching, not the shared
# tools/eureka-mutations.ps1 harness: that harness's Invoke-Cargo parses
# `cargo test` output and appends `-- --exact <test>`, and is not usable
# against `dotnet test`/NUnit. It is also not scripts/mutate-cultmesh.mjs -
# that file is the QUIC campaign's, live and dirty in this tree, and this
# campaign does not edit it (docs/cultnet-selection-cut.md, section 10: "this
# campaign adds its entries in its own file ... and does not edit the QUIC
# campaign's targets - Self decides the exact split at landing").
#
# Same discipline as both: every entry mutates CultCache.cs, is applied as a
# byte-exact splice against one anchor that must match exactly once, restored
# from the original bytes (never from git) with a SHA-256 check, and a
# sidecar (<target>.mutation-original) protects a hard kill mid-mutation. M0
# is a no-op control: the target round-trips through the read/write path
# unchanged and the unmutated suite is green, before any entry runs.
#
#   powershell -File scripts/mutate-cultnet-selection-caching.ps1
#
# Exit code: 0 when every entry's revert and loosening (where one is named)
# killed the test named in Killer, non-zero otherwise. Output for a run that
# did not go green names which entry and which mutant survived.

$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$target = Join-Path $repo 'src\GameCult.Caching\CultCache.cs'
$sidecar = "$target.mutation-original"
$testProject = Join-Path $repo 'tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Read-Bytes([string]$path) { [System.IO.File]::ReadAllBytes($path) }
function Get-BytesHash([byte[]]$bytes) {
    ([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join ''
}
function Write-Text([string]$path, [string]$text) {
    [System.IO.File]::WriteAllBytes($path, [byte[]]($utf8.GetPreamble() + $utf8.GetBytes($text)))
}

function Run-Tests([string]$filter) {
    $args = @('test', $testProject, '-c', 'Debug', '--filter', $filter, '--nologo')
    $output = & dotnet @args 2>&1 | Out-String
    Write-Host $output
    $passedMatch = [regex]::Match($output, 'Passed:\s+(\d+)')
    $failedMatch = [regex]::Match($output, 'Failed:\s+(\d+)')
    $compiled = -not ($output -match 'error CS\d+')
    $passedLine = $output -match 'Passed!'
    $failedLine = $output -match 'Failed!'
    [pscustomobject]@{
        Compiled = $compiled
        Green    = $compiled -and $passedLine -and -not $failedLine -and $passedMatch.Success -and [int]$passedMatch.Groups[1].Value -ge 1
        Passed   = if ($passedMatch.Success) { [int]$passedMatch.Groups[1].Value } else { 0 }
        Failed   = if ($failedMatch.Success) { [int]$failedMatch.Groups[1].Value } else { 0 }
    }
}

# --- Repair a run that died mid-mutation ------------------------------------

if (Test-Path -LiteralPath $sidecar) {
    $original = Read-Bytes $sidecar
    [System.IO.File]::WriteAllBytes($target, $original)
    Remove-Item -LiteralPath $sidecar -Force
    Write-Host "Repaired: a previous run left $sidecar; $target restored from it and the sidecar removed."
}

# --- M0: no-op control -------------------------------------------------------

Write-Host '--- M0: no-op control'
$originalBytes = Read-Bytes $target
$originalHash = Get-BytesHash $originalBytes
$originalText = $utf8.GetString($originalBytes)
$reencoded = [byte[]]($utf8.GetPreamble() + $utf8.GetBytes($originalText))
if ((Get-BytesHash $reencoded) -ne $originalHash) {
    throw "harness broken: $target does not round-trip through the UTF-8 read/write path byte for byte. Nothing was written and no entry ran."
}
[System.IO.File]::WriteAllBytes($sidecar, $originalBytes)
try {
    Write-Text $target $originalText
    if ((Get-BytesHash (Read-Bytes $target)) -ne $originalHash) {
        throw "harness broken: writing $target back through the harness path changed its bytes."
    }
}
finally {
    [System.IO.File]::WriteAllBytes($target, $originalBytes)
    if ((Get-BytesHash (Read-Bytes $target)) -ne $originalHash) { throw "RESTORE FAILED after M0; original SHA-256 $originalHash is kept in $sidecar." }
    Remove-Item -LiteralPath $sidecar -Force
}
$control = Run-Tests 'FullyQualifiedName~CultDocumentSelectionSurfaceTests'
if (-not $control.Green) {
    throw "harness broken: the caching suite is not green unmutated (compiled=$($control.Compiled) passed=$($control.Passed) failed=$($control.Failed)). No entry ran."
}
Write-Host "M0: green, $($control.Passed) passed"

# --- Entries -----------------------------------------------------------------

$entries = @(
    [pscustomobject]@{
        Id     = 'CACHE-D8-Revert'
        Rule   = 'D8/S22: IsNumeric is derived from the closed CLR numeric set; a decimal-typed member must be numeric.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.IsNumericAgreesForANullableNumericMemberAndADecimalMember'
        Old    = "typeof(int), typeof(uint), typeof(long), typeof(ulong),`n            typeof(float), typeof(double), typeof(decimal)"
        New    = "typeof(int), typeof(uint), typeof(long), typeof(ulong),`n            typeof(float), typeof(double)"
    },
    [pscustomobject]@{
        Id     = 'CACHE-D8-Loosening'
        Rule   = 'D8/S22: a string-typed member must not be treated as numeric.'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.IsNumericIsFalseForAStringAlias'
        Old    = "var underlying = Nullable.GetUnderlyingType(type) ?? type;`n            return NumericClrTypes.Contains(underlying);"
        New    = "return true;"
    },
    [pscustomobject]@{
        Id     = 'CACHE-D10-Revert'
        Rule   = 'D10: two differently-named members sharing one index alias are refused by name at registration.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.DuplicateIndexAliasOnTwoDifferentlyNamedMembersIsRefusedByName'
        Old    = "            // D10: two differently-named members that independently declare the same index alias. A`n            // hiding member sharing one slot is already caught above; this is two distinct members whose`n            // alias map entry (BuildDescriptor's indexAccessors) would otherwise collide silently.`n            foreach (var group in keyed`n                         .Select(entry => (Entry: entry, Alias: ResolveIndexAlias(entry.Member)))`n                         .Where(pair => pair.Alias != null)`n                         .GroupBy(pair => pair.Alias, StringComparer.Ordinal)`n                         .Where(group => group.Select(pair => pair.Entry.Member.Name).Distinct().Count() > 1)`n                         .OrderBy(group => group.Key, StringComparer.Ordinal))`n            {`n                var pair = group.Take(2).ToArray();`n                rejections.Add(DuplicateIndexAliasMessage(type.Name, Qualified(pair[0].Entry.Member), Qualified(pair[1].Entry.Member), group.Key!));`n            }`n"
        New    = ""
    },
    [pscustomobject]@{
        Id     = 'CACHE-D10-Loosening'
        Rule   = 'D10: the collision groups by IndexAlias, not by Member.Name (which would refuse nothing HiddenMemberMessage does not already refuse).'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.DuplicateIndexAliasOnTwoDifferentlyNamedMembersIsRefusedByName'
        Old    = '.GroupBy(pair => pair.Alias, StringComparer.Ordinal)'
        New    = '.GroupBy(pair => pair.Entry.Member.Name, StringComparer.Ordinal)'
    },
    [pscustomobject]@{
        Id     = 'CACHE-D11-Revert'
        Rule   = 'D11: a dictionary reference is walked as edges keyed by CultRecordRef, not skipped as an unrecognised many-shape.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.ReferencesOfEnumeratesADictionaryReferenceAsEdgesCarryingItsValues'
        Old    = 'if (TryGetDictionaryRefTypes(memberType, out var keyProperty, out var valueProperty))'
        New    = 'if (false && TryGetDictionaryRefTypes(memberType, out var keyProperty, out var valueProperty))'
    },
    [pscustomobject]@{
        Id     = 'CACHE-D11-Loosening'
        Rule   = 'D11/S19: a dictionary reference edge carries its value as payload, not null (two entries with different payload bytes must compare unequal).'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.ReferencesOfEnumeratesADictionaryReferenceAsEdgesCarryingItsValues'
        Old    = 'results.Add((reference.Key, valueProperty.GetValue(entry)));'
        New    = 'results.Add((reference.Key, (object?)null));'
    }
)

$exitCode = 0
foreach ($entry in $entries) {
    Write-Host "--- $($entry.Id) ($($entry.Mutant)): $($entry.Rule)"
    $text = $utf8.GetString((Read-Bytes $target))
    $old = $entry.Old -replace "`r`n", "`n"
    $normalized = $text -replace "`r`n", "`n"
    $count = 0
    $at = $normalized.IndexOf($old, [System.StringComparison]::Ordinal)
    while ($at -ge 0) { $count++; $at = $normalized.IndexOf($old, $at + 1, [System.StringComparison]::Ordinal) }
    if ($count -ne 1) {
        throw "$($entry.Id): anchor matches $count times, expected exactly 1."
    }
    $mutatedNormalized = $normalized.Replace($old, ($entry.New -replace "`r`n", "`n"))
    $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $mutatedText = $mutatedNormalized -replace "`n", $eol

    $originalBytes = Read-Bytes $target
    [System.IO.File]::WriteAllBytes($sidecar, $originalBytes)
    $verdict = 'NO VERDICT'
    try {
        Write-Text $target $mutatedText
        $run = Run-Tests $entry.Killer
        if (-not $run.Compiled) {
            $verdict = 'NO VERDICT (did not compile)'
        }
        elseif ($run.Failed -ge 1) {
            $verdict = 'KILLED'
        }
        else {
            $verdict = 'SURVIVED'
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($target, $originalBytes)
        if ((Get-BytesHash (Read-Bytes $target)) -ne $originalHash) {
            Write-Host "RESTORE FAILED: $target not restored; original SHA-256 $originalHash kept in $sidecar."
            throw "restore failed for $($entry.Id)"
        }
        Remove-Item -LiteralPath $sidecar -Force
    }
    Write-Host "$($entry.Id): $verdict"
    if ($verdict -ne 'KILLED') { $exitCode = 1 }
}

# Restore the suite to green once more as a final sanity check.
$final = Run-Tests 'FullyQualifiedName~CultDocumentSelectionSurfaceTests'
if (-not $final.Green) { throw "post-run sanity check: the caching suite is not green after restore (passed=$($final.Passed) failed=$($final.Failed))." }
Write-Host "Final: green, $($final.Passed) passed"

exit $exitCode
