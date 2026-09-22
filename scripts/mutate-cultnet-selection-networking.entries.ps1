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
        Old    = 'CultNetSelectionOperator.Le => left <= right,'
        New    = 'CultNetSelectionOperator.Le => left < right,'
    },
    @{
        Id     = 'NET-S16-Ge-Revert'
        Rule   = 'S16: ge includes the equal boundary.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionEvaluatorTests.EvaluatorComparesNumbersAtTheBoundaryForEachOfTheFourOperators'
        Old    = 'CultNetSelectionOperator.Ge => left >= right,'
        New    = 'CultNetSelectionOperator.Ge => left > right,'
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
)
