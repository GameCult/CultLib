# Entries for scripts/mutate-dotnet.ps1: CultNet typed selection, Cut 1, commits 1-2 - the evaluator
# and D4's schema-identity collapse. Targets:
# src/GameCult.Networking/CultNetSelectionEvaluator.cs (S3/S5/S7/S16/S18) and
# src/GameCult.Networking/CultNetDatabase.cs (D4's fourth matcher). Every entry below carries its own
# File now, so this runs as one invocation with both -Target paths. Test project:
# tests/GameCult.Networking.Tests.
#
# Not exhaustive of S1-S23: this covers order, cursor freshness, the one negation, numeric boundaries
# and the reference_outside_target refusal. S1/S2/S6/S23 are pinned by tests but not yet mutation-run
# here; S9/S10-S15 (subscription reconcile, v0 lowering, cross-runtime) and S19/S21/S22 (cache layer)
# are covered elsewhere or not yet (see the Hands report for commit 1).

@(
    @{
        Id     = 'NET-S3-Revert'
        Rule   = 'S3: order reverses under descending.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorOrdersByOrdinalThenIdentityAndReverses'
        Old    = 'var ordered = (selection.Descending'
        New    = 'var ordered = (false'
    },
    @{
        Id     = 'NET-S5-Stale-Revert'
        Rule   = 'S5: a cursor from a moved asOf is refused cursor_stale.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorRefusesAStaleCursorAndAMismatchedSelection'
        Old    = 'if (cursor.AsOf != asOf)'
        New    = 'if (false)'
    },
    @{
        Id     = 'NET-S5-Stale-Loosening'
        Rule   = 'S5: staleness compares equality, not only "cursor is newer than the current asOf".'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorRefusesAStaleCursorAndAMismatchedSelection'
        Old    = 'if (cursor.AsOf != asOf)'
        New    = 'if (cursor.AsOf > asOf)'
    },
    @{
        Id     = 'NET-S5-Invalid-Revert'
        Rule   = 'S5: a cursor whose digest does not match this selection is refused cursor_invalid.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorRefusesAStaleCursorAndAMismatchedSelection'
        Old    = "if (cursor.Digest != CultNetSelectionCursor.ComputeDigest(selection))`n                    throw new CultNetSelectionCursorException(`"cursor_invalid`", `"The cursor's selection digest does not match this selection.`");"
        New    = ''
    },
    @{
        Id     = 'NET-S7-Revert'
        Rule   = 'S7: cited { exists } is honoured, not ignored.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorNegatedIncomingEdgeIsTheOnlyNegation'
        Old    = 'candidates = candidates.Where(row => incoming.Contains(row.Key.Value) == exists);'
        New    = 'candidates = candidates.Where(row => incoming.Contains(row.Key.Value) || true);'
    },
    @{
        Id     = 'NET-S7-Loosening'
        Rule   = 'S7: exists:true and exists:false must answer opposite sets, not the same test inverted twice.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorNegatedIncomingEdgeIsTheOnlyNegation'
        Old    = 'candidates = candidates.Where(row => incoming.Contains(row.Key.Value) == exists);'
        New    = 'candidates = candidates.Where(row => incoming.Contains(row.Key.Value) != exists);'
    },
    @{
        Id     = 'NET-S16-Le-Revert'
        Rule   = 'S16: le includes the equal boundary.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorComparesNumbersAtTheBoundaryForEachOfTheFourOperators'
        Old    = 'CultNetSelectionOperator.Le => cmp <= 0,'
        New    = 'CultNetSelectionOperator.Le => cmp < 0,'
    },
    @{
        Id     = 'NET-S16-Ge-Revert'
        Rule   = 'S16: ge includes the equal boundary.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorComparesNumbersAtTheBoundaryForEachOfTheFourOperators'
        Old    = 'CultNetSelectionOperator.Ge => cmp >= 0,'
        New    = 'CultNetSelectionOperator.Ge => cmp > 0,'
    },
    @{
        Id     = 'NET-S18-Revert'
        Rule   = 'D9/S18: an edge naming a row outside its reference''s declared target refuses the selection.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorRefusesAnEdgeOutsideItsDeclaredTarget'
        Old    = 'throw new CultNetSelectionReferenceOutsideTargetException(from.Descriptor.SchemaId, from.Key.Value, role, to.Descriptor.SchemaId, to.Key.Value);'
        New    = 'return;'
    },
    @{
        Id     = 'NET-D4-FourthMatcher-Revert'
        Rule   = 'D4/Self''s ruling 2026-09-22: CreateShardSnapshotResponse''s schema filter is the one alias matcher (CultNetSchemaAliasMatching.MatchesAny), not a private fourth copy.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetDatabase.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.CultNetDatabase_Creates_ShardSnapshotResponse_ForCompatibleSchemaAlias'
        Old    = '(requestedSchemaIds != null && !CultNetSchemaAliasMatching.MatchesAny(requestedSchemaIds, descriptor)) ||'
        New    = '(requestedSchemaIds != null) ||'
    }
    # A loosening mutant here ('(requestedSchemaIds != null && requestedSchemaIds.Length == 0) ||',
    # i.e. reject only an empty-but-non-null list rather than "no requested id matches") was run by
    # hand against CultNetDatabase_Creates_ShardSnapshotResponse_ForCompatibleSchemaAlias and
    # SURVIVED: the fixture puts exactly one document in the whole cache, so an over-permissive
    # filter still returns the same single row. This is a finding about the fixture, not a defended
    # rule - a second document of a different schema in the same shard is needed to make the
    # loosening fail, and is not added here (no easier mutant substituted per the Hands brief).
    #
    # Q-J (docs/cultnet-selection-cut.md section 2 "Numbers", 2026-09-22): the door's canonical-decimal
    # grammar (CultNetSelection.cs) and the comparator that reads it (CultNetSelectionEvaluator.cs's
    # CompareNumber, via CultNetCanonicalNumber.Compare). CultNetCanonicalNumberTests pins Compare
    # directly with more cases than the entries below exercise; every entry here is one the map named.
    @{
        Id     = 'NET-QJ-Door-Regex-Revert'
        Rule   = 'Q-J: the door refuses a non-canonical number spelling (+1, 1.0, 01, 1e3) rather than accepting it.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName~CultNetSelectionEvaluatorTests.ValidationRefusesNonCanonicalNumberSpellings'
        Old    = 'if (!CultNetCanonicalNumber.IsCanonical(field.Number))'
        New    = 'if (false)'
    },
    @{
        Id     = 'NET-QJ-Door-NegativeZero-Revert'
        Rule   = 'Q-J: "-0" is excluded even though the regex alone matches it - IsCanonical checks it explicitly.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName~CultNetSelectionEvaluatorTests.ValidationRefusesNonCanonicalNumberSpellings'
        Old    = 'value != null && value != "-0" && CanonicalRegex.IsMatch(value);'
        New    = 'value != null && CanonicalRegex.IsMatch(value);'
    },
    @{
        Id     = 'NET-QJ-Compare-IntegerLength-Revert'
        Rule   = 'Q-J: the comparator checks the integer part''s length before its digits - "100" > "99" even though ''1'' < ''9'' as a bare character.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetCanonicalNumberTests.CompareOrdersByIntegerPartLengthBeforeDigits'
        Old    = "            if (integerLeft.Length != integerRight.Length)`n                return sign * (integerLeft.Length < integerRight.Length ? -1 : 1);`n`n"
        New    = ''
    },
    @{
        Id     = 'NET-QJ-Compare-Lexicographic-Revert'
        Rule   = 'Q-J: the comparator is not a lexicographic compare of the whole string ("10" must not be less than "9").'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetCanonicalNumberTests.CompareOrdersByMagnitudeNotLexicographically'
        Old    = 'var (negativeLeft, integerLeft, fractionLeft) = Decompose(left);'
        New    = 'return string.CompareOrdinal(left, right); var (negativeLeft, integerLeft, fractionLeft) = Decompose(left);'
    },
    @{
        Id     = 'NET-QJ-Compare-Sign-Revert'
        Rule   = 'Q-J: the comparator orders a negative value below a positive one of the same magnitude.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetCanonicalNumberTests.CompareOrdersNegativeBelowPositive'
        Old    = 'if (negativeLeft != negativeRight)'
        New    = 'if (false)'
    }
)
