# Entries for scripts/mutate-dotnet.ps1: CultNet typed selection, Cut 1, commit 0 - the cache's public
# read surface (docs/cultnet-selection-cut.md, section 6, D8/D10/D11). Target:
# src/GameCult.Caching/CultCache.cs. Test project: tests/GameCult.Caching.Tests.

@(
    @{
        Id     = 'CACHE-D8-Revert'
        Rule   = 'D8/S22: IsNumeric is derived from the closed CLR numeric set; a decimal-typed member must be numeric.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.IsNumericAgreesForANullableNumericMemberAndADecimalMember'
        Old    = "typeof(int), typeof(uint), typeof(long), typeof(ulong),`n            typeof(float), typeof(double), typeof(decimal)"
        New    = "typeof(int), typeof(uint), typeof(long), typeof(ulong),`n            typeof(float), typeof(double)"
    },
    @{
        Id     = 'CACHE-D8-Loosening'
        Rule   = 'D8/S22: a string-typed member must not be treated as numeric.'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.IsNumericIsFalseForAStringAlias'
        Old    = "var underlying = Nullable.GetUnderlyingType(type) ?? type;`n            return NumericClrTypes.Contains(underlying);"
        New    = 'return true;'
    },
    @{
        Id     = 'CACHE-D10-Revert'
        Rule   = 'D10: two differently-named members sharing one index alias are refused by name at registration.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.DuplicateIndexAliasOnTwoDifferentlyNamedMembersIsRefusedByName'
        Old    = "            // D10: two differently-named members that independently declare the same index alias. A`n            // hiding member sharing one slot is already caught above; this is two distinct members whose`n            // alias map entry (BuildDescriptor's indexAccessors) would otherwise collide silently.`n            foreach (var group in keyed`n                         .Select(entry => (Entry: entry, Alias: ResolveIndexAlias(entry.Member)))`n                         .Where(pair => pair.Alias != null)`n                         .GroupBy(pair => pair.Alias, StringComparer.Ordinal)`n                         .Where(group => group.Select(pair => pair.Entry.Member.Name).Distinct().Count() > 1)`n                         .OrderBy(group => group.Key, StringComparer.Ordinal))`n            {`n                var pair = group.Take(2).ToArray();`n                rejections.Add(DuplicateIndexAliasMessage(type.Name, Qualified(pair[0].Entry.Member), Qualified(pair[1].Entry.Member), group.Key!));`n            }`n"
        New    = ''
    },
    @{
        Id     = 'CACHE-D10-Loosening'
        Rule   = 'D10: the collision groups by IndexAlias, not by Member.Name (which would refuse nothing HiddenMemberMessage does not already refuse).'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.DuplicateIndexAliasOnTwoDifferentlyNamedMembersIsRefusedByName'
        Old    = '.GroupBy(pair => pair.Alias, StringComparer.Ordinal)'
        New    = '.GroupBy(pair => pair.Entry.Member.Name, StringComparer.Ordinal)'
    },
    @{
        Id     = 'CACHE-D11-Revert'
        Rule   = 'D11: a dictionary reference is walked as edges keyed by CultRecordRef, not skipped as an unrecognised many-shape.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.ReferencesOfEnumeratesADictionaryReferenceAsEdgesCarryingItsValues'
        Old    = 'if (TryGetDictionaryRefTypes(memberType, out var keyProperty, out var valueProperty))'
        New    = 'if (false && TryGetDictionaryRefTypes(memberType, out var keyProperty, out var valueProperty))'
    },
    @{
        Id     = 'CACHE-D11-Loosening'
        Rule   = 'D11/S19: a dictionary reference edge carries its value as payload, not null (two entries with different payload bytes must compare unequal).'
        Mutant = 'loosening'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.ReferencesOfEnumeratesADictionaryReferenceAsEdgesCarryingItsValues'
        Old    = 'results.Add((reference.Key, valueProperty.GetValue(entry)));'
        New    = 'results.Add((reference.Key, (object?)null));'
    },
    # Q-J (docs/cultnet-selection-cut.md section 2 "Numbers", 2026-09-22): the row side's canonical
    # decimal rendering. The map named these five mutants explicitly; the fifth ("a row rendered
    # through float64") is this campaign's, the other four are the door's (networking entries file).
    @{
        Id     = 'CACHE-QJ-IntegerFloat64-Revert'
        Rule   = 'Q-J: an integer renders its exact decimal, never through double - a long past 2^53 must not round.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.TryGetIndexNumberRendersALongPast2Pow53ExactlyNotThroughFloat64'
        Old    = '_ => CanonicalizeDecimalDigits(Convert.ToString(raw, CultureInfo.InvariantCulture)!)'
        New    = '_ => CanonicalizeDecimalDigits(Convert.ToDouble(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))'
    },
    @{
        Id     = 'CACHE-QJ-DecimalTrailingZeros-Revert'
        Rule   = 'Q-J: a decimal renders with its trailing fractional zeros stripped.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.TryGetIndexNumberStripsTrailingZerosFromADecimal'
        Old    = "fractionPart = fractionPart.TrimEnd('0');"
        New    = ''
    },
    @{
        Id     = 'CACHE-QJ-ExponentExpansion-Revert'
        Rule   = 'Q-J: a double/float that .NET would render in exponent notation is rewritten to positional digits before canonicalizing.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.TryGetIndexNumberRewritesADoublesExponentNotationToPositionalDigits'
        Old    = 'double d => double.IsNaN(d) || double.IsInfinity(d) ? null : CanonicalizeDecimalDigits(ExpandScientificNotation(d.ToString(CultureInfo.InvariantCulture))),'
        New    = 'double d => double.IsNaN(d) || double.IsInfinity(d) ? null : CanonicalizeDecimalDigits(d.ToString(CultureInfo.InvariantCulture)),'
    },
    @{
        Id     = 'CACHE-QJ-NaNInfinity-Revert'
        Rule   = 'Q-J: a NaN or infinite member matches no comparison - TryGetIndexNumber must answer false, not a garbage rendering of the value.'
        Mutant = 'revert'
        Killer = 'FullyQualifiedName=GameCult.Caching.Tests.CultDocumentSelectionSurfaceTests.TryGetIndexNumberReturnsFalseForNaNAndInfinity'
        Old    = 'double d => double.IsNaN(d) || double.IsInfinity(d) ? null : CanonicalizeDecimalDigits(ExpandScientificNotation(d.ToString(CultureInfo.InvariantCulture))),'
        New    = 'double d => CanonicalizeDecimalDigits(ExpandScientificNotation(d.ToString(CultureInfo.InvariantCulture))),'
    }
)
