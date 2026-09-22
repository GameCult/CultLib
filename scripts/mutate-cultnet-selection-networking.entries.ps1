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
        # S2-3 (docs/cultnet-selection-cut.md, Self's rulings 2026-09-22, commit 2 fix batch):
        # CreateShardSnapshotResponse no longer carries its own schema/key filter at all - it went
        # through CultNetSelectionEvaluator.SelectPage, with the shard's own row filter as the only
        # thing this method still owns. NET-D4-FourthMatcher-Revert's original anchor
        # ("requestedSchemaIds != null && !CultNetSchemaAliasMatching.MatchesAny(...)") was the private
        # loop this fix batch deleted, not merely refactored; the schema-matching mutants it proved are
        # now covered by this file's CultNetSelectionEvaluator.cs entries (NET-S1/S2 etc.) plus
        # tests/GameCult.Networking.Tests.NetworkingTests.CultNetDatabase_ShardAndNonShardSnapshot_*.
        # This entry now proves the one thing CreateShardSnapshotResponse still owns: the shard
        # membership check.
        Id     = 'NET-D4-ShardMembership-Revert'
        Rule   = 'CreateShardSnapshotResponse''s row filter still enforces shard membership (shard.Matches), even though schema/key filtering is now entirely the evaluator''s.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetDatabase.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.CultNetDatabase_ShardAndNonShardSnapshot_AgreeOnUnfilteredRequest'
        Old    = '!string.IsNullOrWhiteSpace(key.Value) && shard.Matches(descriptor.SchemaId, key);'
        New    = '!string.IsNullOrWhiteSpace(key.Value);'
    }
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
    },
    #
    # Cut 1 fix batch, second Hands pass (docs/cultnet-selection-cut.md, "Not done, sent to a second
    # Hands"): mutation entries for the C# rules R-A/B/C/D/E/F/G/H/J/L the first pass (7903853..3d32c67)
    # fixed but proved only by regression tests, plus S9 (D6) which had no test at all. Killers below are
    # tests/GameCult.Networking.Tests.CultNetSelectionFixBatchTests.cs (new file, this pass),
    # NetworkingTests.cs (two new RUDP round-trip tests, this pass) and the pre-existing
    # SelectionParityVectorTests.cs / CultNetSelectionPageTests.cs where those already pin the rule.
    #
    @{
        # R-A: both C# servers register v1 listeners. Revert = unregister the subscription server's,
        # which S9's own test also depends on - if this listener is gone, that test's subscribe message
        # is never answered at all and the test times out.
        Id     = 'NET-RA-SubscribeListener-Revert'
        Rule   = 'R-A: CultNetDatabaseSubscriptionServer registers a v1 subscribe listener, not only a v0 one.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.DatabaseSubscriptionServer_SubscribesV1AndReconcilesAHopBearingSelectionWhenTheCiterChanges'
        Old    = '_server.OnCultNet(_subscribeV1);'
        New    = ''
    },
    # NET-RA-SnapshotListener-Revert (unregistering CultNetDatabaseServer's v1 snapshot listener) is not
    # here: CultNetDatabaseServer pairs only with Server (the LiteNetLib/TCP-secured production
    # construction site), and no existing test in this suite drives Server's real client/listener
    # dispatch for CultNetDatabaseServer at all - every existing test (this pass's included) calls
    # CreateSelectionResponse/CreateSnapshotResponse directly. A revert of that OnCultNet registration
    # line is therefore not yet reached by this suite - a real gap in wire-level coverage for the
    # snapshot path, not a fixture-tunable one; the subscription server's equivalent registration is
    # covered below (NET-RA-SubscribeListener-Revert) because CultNetDatabaseSubscriptionServer does
    # accept RudpCultNetSchemaServer and this pass's S9 test drives it over the real wire.
    @{
        # Loosening: a v1 request is answered, but through a selection quietly lowered to v0's two
        # allowlists - fields/cites/cited/projection silently dropped instead of honoured, the exact
        # "answers v1 through v0 lowering" shortcut R-A rules out.
        Id     = 'NET-RA-Snapshot-V0Lowering-Loosening'
        Rule   = 'R-A: a v1 snapshot request is answered against its own full Selection, not a v0-shaped lowering that drops fields/cites/cited/projection.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetDatabaseServer.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.CultNetDatabaseServer_AnswersV1SnapshotRequestRespectingItsFields'
        Old    = '                request.Selection,
                ordinalOf: (schemaId, key) => _database.LastWriteSequence(schemaId, key) ?? 0,'
        New    = '                new CultNetSelection { Schemas = request.Selection.Schemas, Keys = request.Selection.Keys },
                ordinalOf: (schemaId, key) => _database.LastWriteSequence(schemaId, key) ?? 0,'
    },
    @{
        # S9/D6: a hop-bearing selection must run the full reconcile diff on every change, not the
        # single-row fast path - a change to the citer (not the page row itself) must still surface.
        Id     = 'NET-S9-HopFastPath-Revert'
        Rule   = 'D6/S9: Watch routes a hop-bearing selection to Reconcile on every change; it must not fall through to the single-row fast path.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.DatabaseSubscriptionServer_SubscribesV1AndReconcilesAHopBearingSelectionWhenTheCiterChanges'
        Old    = '                    // D6: cites/cited are set-dependent - a change to row B can change whether row A
                    // matches - so a hop-bearing selection runs the full diff-against-delivered
                    // reconcile on every change instead of the single-row fast path.
                    if (request.Selection.HasHop)
                    {
                        Reconcile(key, request);
                        return;
                    }
'
        New    = ''
    },
    @{
        # R-B: edges are anchored to the row the hop direction actually reports against, not always to
        # the citer - the pre-fix bug filtered every edge by "is the citer (From) on the page", even
        # under `cited`, where the page rows are the cited (To) rows.
        Id     = 'NET-RB-CitedAnchor-Revert'
        Rule   = 'R-B: a cited edge is anchored to the cited row (To), not the citer (From) - the direction-blind pre-fix anchor.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_AnchorsCitedEdgesToTheCitedRowAndOrdersThemByFromThenRole'
        Old    = 'foreach (var edge in citedEdges) edges.Add((edge, edge.To));'
        New    = 'foreach (var edge in citedEdges) edges.Add((edge, edge.From));'
    },
    @{
        # R-B loosening: the order rule is page-row order then (from, role, to) in code-point order -
        # dropping the (from, role, to) tie-break still groups edges by their anchor row correctly (a
        # plausible partial fix) but no longer orders two edges anchored to the same row.
        Id     = 'NET-RB-EdgeOrder-Loosening'
        Rule   = 'R-B: two edges anchored to the same page row are ordered by (from, role, to) in code-point order, not left in row-supply order.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_AnchorsCitedEdgesToTheCitedRowAndOrdersThemByFromThenRole'
        Old    = '            return full.Edges
                .Where(pair => pageRowIndex.ContainsKey(pair.Anchor.Key.Value))
                .OrderBy(pair => pageRowIndex[pair.Anchor.Key.Value])
                .ThenBy(pair => pair.Edge.From.Descriptor.SchemaId, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.From.Key.Value, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.Role, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.To.Descriptor.SchemaId, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.To.Key.Value, CultNetCodePointComparer.Instance)
                .Select(pair => pair.Edge)
                .ToArray();'
        New    = '            return full.Edges
                .Where(pair => pageRowIndex.ContainsKey(pair.Anchor.Key.Value))
                .OrderBy(pair => pageRowIndex[pair.Anchor.Key.Value])
                .Select(pair => pair.Edge)
                .ToArray();'
    },
    @{
        # R-C: the row tiebreak (schemaId, then key) uses code-point order everywhere, not UTF-16
        # code-unit ("ordinal") order - the two disagree on astral-vs-BMP key comparisons.
        Id     = 'NET-RC-RowTiebreak-Revert'
        Rule   = 'R-C: the ascending row tiebreak compares the record key by code-point order (CultNetCodePointComparer), not UTF-16 code-unit order.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_OrdersAstralKeysByCodePointNotUtf16CodeUnit'
        Old    = 'matched.OrderBy(row => row.Ordinal).ThenBy(row => row.Descriptor.SchemaId, CultNetCodePointComparer.Instance).ThenBy(row => row.Key.Value, CultNetCodePointComparer.Instance))'
        New    = 'matched.OrderBy(row => row.Ordinal).ThenBy(row => row.Descriptor.SchemaId, CultNetCodePointComparer.Instance).ThenBy(row => row.Key.Value, StringComparer.Ordinal))'
    },
    @{
        # R-C loosening: the row order itself is fixed (kills the revert above), but the cursor position
        # search still compares by UTF-16 code-unit order - a plausible partial fix that forgets the
        # cursor half, which desyncs a page walk over an astral/BMP key boundary.
        Id     = 'NET-RC-CursorPosition-Loosening'
        Rule   = 'R-C: the cursor position compare uses code-point order matching the row tiebreak, not UTF-16 code-unit order.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_CursorPositionUsesCodePointOrderMatchingTheRowTiebreak'
        Old    = 'var schema = CultNetCodePointComparer.Instance.Compare(row.Descriptor.SchemaId, cursor.SchemaId);
            return schema != 0 ? schema : CultNetCodePointComparer.Instance.Compare(row.Key.Value, cursor.RecordKey);'
        New    = 'var schema = string.CompareOrdinal(row.Descriptor.SchemaId, cursor.SchemaId);
            return schema != 0 ? schema : string.CompareOrdinal(row.Key.Value, cursor.RecordKey);'
    },
    @{
        # R-E: cites.target.schemaId is matched through the alias matcher, same as `schemas` - not an
        # exact SchemaId string compare a schema alias could never satisfy.
        Id     = 'NET-RE-AliasMatcher-Revert'
        Rule   = 'R-E: MatchesCitation compares a cites target through CultNetSchemaAliasMatching, not an exact SchemaId string compare.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_MatchesCitesTargetThroughTheAliasMatcher'
        Old    = 'if (!CultNetSchemaAliasMatching.Matches(citation.Target.SchemaId, resolved.Descriptor))'
        New    = 'if (resolved.Descriptor.SchemaId != citation.Target.SchemaId)'
    },
    @{
        # R-E: an unmatched cites.target.schemaId is refused at the door (this pass added the check -
        # docs/cultnet-selection-cut.md named it but the first fix-batch pass never wrote it), never
        # answered with a silently empty page by MatchesCitation's own "not found" continue.
        Id     = 'NET-RE-DoorRefusesUnmatchedTarget-Revert'
        Rule   = 'R-E: Validate refuses a cites.target.schemaId that matches no declared schema.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Validation_RefusesACitesTargetSchemaThatMatchesNoDeclaredSchema'
        Old    = 'if (!allDescriptors.Any(descriptor => CultNetSchemaAliasMatching.Matches(selection.Cites.Target!.SchemaId, descriptor)))
                    throw new CultNetSelectionInvalidException("cites.target.schemaId", selection.Cites.Target!.SchemaId, $"selection.cites.target.schemaId \"{selection.Cites.Target!.SchemaId}\" does not match any declared schema.");'
        New    = ''
    },
    @{
        # R-E loosening: the door reuses `reachable` (Fields validation's own Schemas-filtered set)
        # instead of the full declared-schema set - a cites target is a separate structural reference,
        # not itself constrained by the selection's own Schemas allowlist, so this wrongly refuses a
        # valid target whose schema sits outside that filter.
        Id     = 'NET-RE-DoorTargetReachableOnly-Loosening'
        Rule   = 'R-E: the door resolves cites.target.schemaId against every declared schema, not only the selection''s own Schemas-filtered reachable set.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelection.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Validation_ResolvesACitesTargetSchemaEvenWhenNotInTheSelectionsOwnSchemasFilter'
        Old    = 'if (!allDescriptors.Any(descriptor => CultNetSchemaAliasMatching.Matches(selection.Cites.Target!.SchemaId, descriptor)))'
        New    = 'if (!reachable.Any(descriptor => CultNetSchemaAliasMatching.Matches(selection.Cites.Target!.SchemaId, descriptor)))'
    },
    @{
        # R-F: the door is inside Select/EvaluateAll unconditionally - the pre-fix code took a
        # validate:true/false flag that let a caller skip it.
        Id     = 'NET-RF-DoorInsideSelect-Revert'
        Rule   = 'R-F: EvaluateAll validates the selection before matching anything - no public evaluation path can skip the door.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_SelectValidatesFirstAndNeverAnswersAnInvalidSelection'
        Old    = 'selection.Validate(registry.AllDescriptors.ToArray());'
        New    = ''
    },
    @{
        # R-G: SelectPage/Page reports the selection''s whole matching count (TotalMatched), not the
        # page''s own row count (C5). Already regression-tested by CultNetSelectionPageTests; this entry
        # is that rule''s mutation coverage.
        Id     = 'NET-RG-TotalMatched-Revert'
        Rule   = 'R-G/C5: Evaluation.TotalMatched is the selection''s whole matching count, not this page''s row count.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionPageTests.CreateSelectionResponse_ReportsTotalMatchedNotPageLength'
        Old    = 'return new Evaluation { Rows = page, Edges = pageEdges, NextCursor = nextCursor, TotalMatched = ordered.Count };'
        New    = 'return new Evaluation { Rows = page, Edges = pageEdges, NextCursor = nextCursor, TotalMatched = page.Length };'
    },
    @{
        # R-H: every string and list in the cursor digest is length-prefixed, closing the delimiter-
        # ambiguous collisions Soul named (Keys=["a,b"] vs ["a","b"]; a field's Values=["a|b"] vs
        # ["a","b"]). Revert reproduces a single-separator-joined digest (comma), which the keys half of
        # the killer test catches; the loosening below reproduces the exact pipe-joined case Soul named,
        # which only the field-values half catches.
        Id     = 'NET-RH-Digest-CommaJoin-Revert'
        Rule   = 'R-H: AppendList length-prefixes each entry and the list itself, rather than joining entries with one separator that can appear inside an entry.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.ComputeDigest_DoesNotCollideOnDelimiterAmbiguousKeysOrFieldValues'
        Old    = '            var ordered = (values ?? Array.Empty<string>()).OrderBy(v => v, CultNetCodePointComparer.Instance).ToArray();
            sb.Append(ordered.Length).Append('':'');
            foreach (var value in ordered)
                AppendString(sb, value);'
        New    = '            sb.Append(string.Join(",", (values ?? Array.Empty<string>()).OrderBy(v => v, CultNetCodePointComparer.Instance)));'
    },
    @{
        Id     = 'NET-RH-Digest-PipeJoin-Loosening'
        Rule   = 'R-H: the exact collision Soul named - a separator-joined digest using "|" still collides Values=["a|b"] with Values=["a","b"].'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.ComputeDigest_DoesNotCollideOnDelimiterAmbiguousKeysOrFieldValues'
        Old    = '            var ordered = (values ?? Array.Empty<string>()).OrderBy(v => v, CultNetCodePointComparer.Instance).ToArray();
            sb.Append(ordered.Length).Append('':'');
            foreach (var value in ordered)
                AppendString(sb, value);'
        New    = '            sb.Append(string.Join("|", (values ?? Array.Empty<string>()).OrderBy(v => v, CultNetCodePointComparer.Instance)));'
    },
    @{
        # R-J: any_of on a numeric alias compares TryGetIndexNumber's canonical rendering, not the
        # string getter's culture-dependent ToString() (which renders exponent notation for a large
        # float, never matching the canonical decimal form a caller would send).
        Id     = 'NET-RJ-NumericAnyOf-Revert'
        Rule   = 'R-J: any_of on a numeric alias compares the canonical rendering (TryGetIndexNumber), not the raw string getter.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetSelectionEvaluator.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.CultNetSelectionFixBatchTests.Evaluator_AnyOfOnANumericAliasComparesTheCanonicalRenderingNotToString'
        Old    = '                    var isNumeric = descriptor.DeclaredMembers.Any(member => member.IndexAlias == field.Index && member.IsNumeric);
                    var value = isNumeric
                        ? (descriptor.TryGetIndexNumber(document, field.Index, out var numeric) ? numeric : null)
                        : (descriptor.TryGetIndexValue(document, field.Index, out var raw) ? raw : null);'
        New    = '                    var value = descriptor.TryGetIndexValue(document, field.Index, out var raw) ? raw : null;'
    }
)
