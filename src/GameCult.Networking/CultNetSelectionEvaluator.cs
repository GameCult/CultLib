using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GameCult.Caching;

namespace GameCult.Networking
{
    /// <summary>
    /// The one evaluator for CultNet typed selections (docs/cultnet-selection-cut.md, section 6/8).
    /// Owns matching, order, snapshot, hop and cursor. Reflects over nothing: every declared value it
    /// reads comes through <see cref="CultDocumentDescriptor"/>'s public read surface (section 1's cache
    /// commit). Replaces the three selector engines this cut deletes from
    /// <c>CultNetDocumentRegistry</c>/<c>CultNetDatabaseServer</c>/<c>CultNetDatabaseSubscriptionServer</c>.
    /// </summary>
    public static class CultNetSelectionEvaluator
    {
        /// <summary>The lowest and highest limit a page may carry (section 2).</summary>
        public const uint LimitMin = 1;
        public const uint LimitMax = 200;

        /// <summary>One row available to the evaluator: what the cache already knows about it.</summary>
        public readonly struct Row
        {
            public Row(CultDocumentDescriptor descriptor, CultRecordKey key, object document, long ordinal, string storedAt = "")
            {
                Descriptor = descriptor;
                Key = key;
                Document = document;
                Ordinal = ordinal;
                StoredAt = storedAt;
            }

            public CultDocumentDescriptor Descriptor { get; }
            public CultRecordKey Key { get; }
            public object Document { get; }
            public long Ordinal { get; }
            public string StoredAt { get; }
        }

        /// <summary>One edge a hop traversed, resolved against the row set in hand.</summary>
        public readonly struct EdgeMatch
        {
            public EdgeMatch(Row from, string role, Row to, object? payload)
            {
                From = from;
                Role = role;
                To = to;
                Payload = payload;
            }

            public Row From { get; }
            public string Role { get; }
            public Row To { get; }
            public object? Payload { get; }
        }

        /// <summary>Evaluated, ordered, paged rows plus the edges a hop traversed.</summary>
        public sealed class Evaluation
        {
            public IReadOnlyList<Row> Rows { get; set; } = Array.Empty<Row>();
            public IReadOnlyList<EdgeMatch> Edges { get; set; } = Array.Empty<EdgeMatch>();
            public string? NextCursor { get; set; }
            /// <summary>The selection's whole matching count, not this page's row count (R-G/C5).</summary>
            public int TotalMatched { get; set; }
        }

        /// <summary>
        /// The single-row fast path (D6): schemas, keys and fields only. A hop-bearing selection
        /// (<see cref="CultNetSelection.HasHop"/>) is set-dependent and must not use this path - the
        /// caller reconciles the full set instead.
        /// </summary>
        public static bool Matches(CultDocumentDescriptor descriptor, CultRecordKey key, object? document, CultNetSelection selection)
        {
            if (selection.HasHop)
                throw new InvalidOperationException("A hop-bearing selection (cites/cited) is set-dependent and cannot use the single-row Matches fast path; reconcile the full row set instead.");
            return MatchesSchemaKeysFields(descriptor, key, document, selection);
        }

        private static bool MatchesSchemaKeysFields(CultDocumentDescriptor descriptor, CultRecordKey key, object? document, CultNetSelection selection)
        {
            // The door (CultNetSelectionValidation.Validate) refuses an empty schemas/keys list before
            // a selection ever reaches evaluation, so a non-null array here is always non-empty; the
            // evaluator does not special-case [] as "no filter" (docs/cultnet-selection-cut.md,
            // Self's rulings 2026-09-22, S2-3).
            if (selection.Schemas != null && !CultNetSchemaAliasMatching.MatchesAny(selection.Schemas, descriptor))
                return false;
            if (selection.Keys != null && !selection.Keys.Contains(key.Value, StringComparer.Ordinal))
                return false;
            if (selection.Fields == null)
                return true;
            // R-T(a): a removed change is evaluated without reading a document (there is none to read -
            // the row is gone). It matches on the selection's keys and schemas alone; a field predicate
            // cannot be tested against a value that no longer exists, so it counts as unknown rather
            // than excluding the row (or throwing TryGetIndexValue(null)).
            if (document == null)
                return true;

            foreach (var field in selection.Fields)
            {
                if (!CultNetSelectionOperators.TryParse(field.Op, out var op))
                    return false;
                if (op == CultNetSelectionOperator.AnyOf)
                {
                    // R-J: on a numeric alias, any_of compares the canonical rendering
                    // (TryGetIndexNumber), not the string getter's culture-dependent ToString() - the
                    // same rendering a comparison operator uses, so a numeric member's any_of and its
                    // comparisons agree on what the row's value spells.
                    var isNumeric = descriptor.DeclaredMembers.Any(member => member.IndexAlias == field.Index && member.IsNumeric);
                    var value = isNumeric
                        ? (descriptor.TryGetIndexNumber(document, field.Index, out var numeric) ? numeric : null)
                        : (descriptor.TryGetIndexValue(document, field.Index, out var raw) ? raw : null);
                    if (value == null || field.Values == null || !field.Values.Contains(value, StringComparer.Ordinal))
                        return false;
                }
                else
                {
                    if (!descriptor.TryGetIndexNumber(document, field.Index, out var number) || field.Number == null)
                        return false;
                    if (!CompareNumber(number, op, field.Number))
                        return false;
                }
            }

            return true;
        }

        // Q-J: both operands are canonical decimal strings by the time they reach here (the row side
        // renders through RenderCanonicalNumber, the wire side is validated at the door), so the
        // comparison is CultNetCanonicalNumber.Compare's pure string function - no double anywhere on
        // this path (docs/cultnet-selection-cut.md section 2 "Numbers").
        private static bool CompareNumber(string left, CultNetSelectionOperator op, string right)
        {
            var cmp = CultNetCanonicalNumber.Compare(left, right);
            return op switch
            {
                CultNetSelectionOperator.Lt => cmp < 0,
                CultNetSelectionOperator.Le => cmp <= 0,
                CultNetSelectionOperator.Ge => cmp >= 0,
                CultNetSelectionOperator.Gt => cmp > 0,
                _ => throw new ArgumentOutOfRangeException(nameof(op))
            };
        }

        /// <summary>One evaluation's full matching, ordered row set, with the edges its hop traversed anchored to the row that carries each one.</summary>
        public sealed class FullEvaluation
        {
            /// <summary>Every matching row, in order, with no cursor or limit applied.</summary>
            public IReadOnlyList<Row> Ordered { get; set; } = Array.Empty<Row>();
            /// <summary>Every edge the hop traversed, each paired with the page-eligible row it is reported against: the citer under <c>cites</c>, the cited row under <c>cited</c> (R-B).</summary>
            public IReadOnlyList<(EdgeMatch Edge, Row Anchor)> Edges { get; set; } = Array.Empty<(EdgeMatch, Row)>();
        }

        /// <summary>
        /// Evaluates a selection's full matching, ordered row set and the edges its hop traversed, with
        /// no cursor or limit applied - the door (R-F), schemas/keys/fields, the hop (cites/cited, with
        /// the reference_outside_target refusal) and order all run exactly once here. This is the one
        /// evaluation both a single v1 page (<see cref="Select"/>) and a v0/shard full walk page over,
        /// instead of each page re-filtering and re-sorting the whole row set (R-G).
        /// </summary>
        public static FullEvaluation EvaluateAll(
            CultDocumentRegistry registry,
            IReadOnlyList<Row> allRows,
            CultNetSelection selection)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (allRows == null) throw new ArgumentNullException(nameof(allRows));
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            // R-F: the door is inside select. This is the one public entry point that evaluates a
            // selection over a full row set, so validation cannot be skipped by a caller that forgets
            // to gate it - a public entry point that answers an unvalidated selection does not exist.
            selection.Validate(registry.AllDescriptors.ToArray());

            // R-V: a row's identity is (schemaId, recordKey), never the record key alone. A bare-key
            // dictionary here silently let one schema's row displace another's at the same key (last
            // writer wins), so a hop resolving "the row at this key" picked whichever schema happened
            // to insert last instead of the schema the reference/citation actually declares. Every key
            // that appears on more than one schema's row is kept, not collapsed, so hop resolution can
            // still tell them apart.
            var byKey = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            foreach (var row in allRows)
            {
                if (!byKey.TryGetValue(row.Key.Value, out var rowsAtKey))
                    byKey[row.Key.Value] = rowsAtKey = new List<Row>();
                rowsAtKey.Add(row);
            }

            var candidates = allRows.Where(row => MatchesSchemaKeysFields(row.Descriptor, row.Key, row.Document, selection));

            // R-B: cites and cited are opposite hop directions, and each keeps its own edge sink so an
            // edge is always anchored to the row it will be reported against - the citer under cites,
            // the cited row under cited - never filtered by "From is on the page" regardless of which
            // direction the hop ran.
            var citedEdges = new List<EdgeMatch>();
            if (selection.Cited != null)
            {
                var incoming = BuildIncomingIndex(registry, allRows, byKey, selection.Cited.Role, citedEdges);
                var exists = selection.Cited.Exists;
                candidates = candidates.Where(row => incoming.Contains((row.Descriptor.SchemaId, row.Key.Value)) == exists);
            }

            var citesEdges = new List<EdgeMatch>();
            if (selection.Cites != null)
            {
                candidates = candidates.Where(row => MatchesCitation(registry, byKey, row, selection.Cites, citesEdges));
            }

            var matched = candidates.ToArray();
            var ordered = (selection.Descending
                    ? matched.OrderByDescending(row => row.Ordinal).ThenByDescending(row => row.Descriptor.SchemaId, CultNetCodePointComparer.Instance).ThenByDescending(row => row.Key.Value, CultNetCodePointComparer.Instance)
                    : matched.OrderBy(row => row.Ordinal).ThenBy(row => row.Descriptor.SchemaId, CultNetCodePointComparer.Instance).ThenBy(row => row.Key.Value, CultNetCodePointComparer.Instance))
                .ToArray();

            var edges = new List<(EdgeMatch, Row)>(citesEdges.Count + citedEdges.Count);
            foreach (var edge in citesEdges) edges.Add((edge, edge.From));
            foreach (var edge in citedEdges) edges.Add((edge, edge.To));

            return new FullEvaluation { Ordered = ordered, Edges = edges };
        }

        /// <summary>
        /// Evaluates a selection over the full row set and returns one cursor/limit page of it
        /// (docs/cultnet-selection-cut.md, section 2/6). <see cref="Evaluation.TotalMatched"/> is the
        /// selection's whole matching count, not this page's length (R-G/C5). The caller projects the
        /// result to wire records (<see cref="CultNetDocumentRegistry"/> owns that).
        /// </summary>
        public static Evaluation Select(
            CultDocumentRegistry registry,
            IReadOnlyList<Row> allRows,
            CultNetSelection selection,
            ulong asOf,
            CultNetSelectionCursorKey? cursorKey = null)
        {
            var full = EvaluateAll(registry, allRows, selection);
            return Page(full, selection, asOf, cursorKey);
        }

        /// <summary>
        /// Slices one cursor/limit page out of an already-evaluated, already-ordered row set (R-G): the
        /// v0/shard full walk calls this once per 200-row chunk of a single <see cref="EvaluateAll"/>
        /// result instead of re-evaluating per page.
        /// </summary>
        /// <param name="cursorKey">
        /// The answering process's cursor key (R-O). Defaults to a key generated once per process the
        /// first time this method runs, so a caller that never sets up its own <see cref="CultNetDatabase"/>
        /// still gets a keyed digest; <see cref="CultNetDatabase"/> holds and passes its own instance so a
        /// fresh database (a simulated restart in tests, a real process restart in production) mints
        /// under a fresh key and a prior cursor's digest stops verifying.
        /// </param>
        public static Evaluation Page(FullEvaluation full, CultNetSelection selection, ulong asOf, CultNetSelectionCursorKey? cursorKey = null)
        {
            if (full == null) throw new ArgumentNullException(nameof(full));
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            var key = cursorKey ?? CultNetSelectionCursorKey.ProcessDefault;
            var ordered = full.Ordered;
            var startIndex = 0;
            if (!string.IsNullOrEmpty(selection.Cursor))
            {
                var cursor = CultNetSelectionCursor.Parse(selection.Cursor!);
                if (!cursor.VerifyDigest(selection, key))
                    throw new CultNetSelectionCursorException("cursor_invalid", "The cursor's selection digest does not match this selection.");
                if (cursor.AsOf != asOf)
                    throw new CultNetSelectionCursorException(
                        "cursor_stale",
                        $"The cursor was minted at asOf {cursor.AsOf}; this server answers as of {asOf}.",
                        asOf: cursor.AsOf,
                        current: asOf);
                startIndex = FindCursorPosition(ordered, cursor, selection.Descending);
            }

            var limit = (int)Math.Clamp(selection.Limit ?? LimitMax, LimitMin, LimitMax);
            var page = ordered.Skip(startIndex).Take(limit).ToArray();
            var hasNext = startIndex + page.Length < ordered.Count;
            var nextCursor = hasNext && page.Length > 0
                ? CultNetSelectionCursor.Mint(asOf, page[^1], selection, key)
                : null;

            var pageEdges = selection.HasHop
                ? EdgesFor(page, full)
                : Array.Empty<EdgeMatch>();

            return new Evaluation { Rows = page, Edges = pageEdges, NextCursor = nextCursor, TotalMatched = ordered.Count };
        }

        // R-B: the order is deterministic - the page-row order, then (from, role, to) in code-point
        // order. Grouping by the row an edge is anchored to (LINQ's OrderBy is stable, so the given
        // rows' own order survives as the primary key) and breaking ties by the edge's own identity
        // gives both without a second pass. Public because a v0/shard full walk (R-G, one evaluation
        // answering every page) reports edges against its whole matched set, not one 200-row page.
        public static EdgeMatch[] EdgesFor(IReadOnlyList<Row> rows, FullEvaluation full)
        {
            // R-V: the anchor lookup is keyed by (schemaId, recordKey), not the bare key - two schemas'
            // rows sharing one record key can both legitimately be on the same page now that the hop
            // resolves rows by identity, and a bare-key index would let one silently steal the other's
            // edges.
            var pageRowIndex = new Dictionary<(string SchemaId, string RecordKey), int>();
            for (var i = 0; i < rows.Count; i++)
                pageRowIndex[(rows[i].Descriptor.SchemaId, rows[i].Key.Value)] = i;

            return full.Edges
                .Where(pair => pageRowIndex.ContainsKey((pair.Anchor.Descriptor.SchemaId, pair.Anchor.Key.Value)))
                .OrderBy(pair => pageRowIndex[(pair.Anchor.Descriptor.SchemaId, pair.Anchor.Key.Value)])
                .ThenBy(pair => pair.Edge.From.Descriptor.SchemaId, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.From.Key.Value, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.Role, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.To.Descriptor.SchemaId, CultNetCodePointComparer.Instance)
                .ThenBy(pair => pair.Edge.To.Key.Value, CultNetCodePointComparer.Instance)
                .Select(pair => pair.Edge)
                .ToArray();
        }

        private static int FindCursorPosition(IReadOnlyList<Row> ordered, CultNetSelectionCursor cursor, bool descending)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                var comparesAfter = descending
                    ? ComparePosition(row, cursor) < 0
                    : ComparePosition(row, cursor) > 0;
                if (comparesAfter)
                    return i;
            }

            return ordered.Count;
        }

        private static int ComparePosition(Row row, CultNetSelectionCursor cursor)
        {
            var ordinal = row.Ordinal.CompareTo(cursor.Ordinal);
            if (ordinal != 0) return ordinal;
            var schema = CultNetCodePointComparer.Instance.Compare(row.Descriptor.SchemaId, cursor.SchemaId);
            return schema != 0 ? schema : CultNetCodePointComparer.Instance.Compare(row.Key.Value, cursor.RecordKey);
        }

        // D9: a reference's target set is every registered leaf assignable to its declared target type.
        // A stored edge naming a row whose schema is outside that set refuses the selection (S18).
        // S-8: targetType is never null here - D11 (CultCache's DiscoverMembers) refuses every reference
        // shape whose target the cache could not infer (a bare ICultRecordRef, scalar or many) at
        // registration, before a document carrying one can ever reach the evaluator. The old
        // `targetType ?? typeof(object)` fallback had no fixture that could reach it and is deleted
        // rather than kept as an unreachable branch.
        //
        // R-V: a reference's stored target is a bare record key - the wire carries no schema for it -
        // so resolving "the row this edge names" means searching every row sharing that key (byKey) for
        // the one whose schema falls inside the reference's declared leaf set, not grabbing whichever
        // row a bare-key dictionary happened to keep. Two schemas legitimately sharing one record key
        // resolve to two different rows depending on which reference declared which target (Soul's P2).
        //
        // R-W: this is called for every stored edge a reference member carries, unconditionally, before
        // any citation- or role-specific filtering decides whether that edge is the one the caller
        // asked about - a many-reference edge into a schema outside its declared target refuses the
        // selection even when the caller's own key/role filter would otherwise have skipped past it
        // without ever looking at it (Soul's P3).
        private static bool TryResolveReferenceTarget(
            CultDocumentRegistry registry,
            IReadOnlyDictionary<string, List<Row>> byKey,
            Type targetType,
            Row from,
            string role,
            string recordKey,
            out Row resolved)
        {
            resolved = default;
            if (!byKey.TryGetValue(recordKey, out var candidates) || candidates.Count == 0)
                return false; // a dangling reference - no row at all exists at this key (R-T(b) style: not an error).

            var leaves = registry.ResolveTargetLeaves(targetType);
            foreach (var candidate in candidates)
            {
                if (leaves.Any(leaf => leaf.SchemaId == candidate.Descriptor.SchemaId))
                {
                    resolved = candidate;
                    return true;
                }
            }

            // Every row sharing this record key falls outside the reference's declared target: refuse
            // deterministically (by schema id, code-point order) rather than let insertion order decide
            // which of them the refusal names (R-V: "both row orders give the same answer").
            var outside = candidates.OrderBy(c => c.Descriptor.SchemaId, CultNetCodePointComparer.Instance).First();
            throw new CultNetSelectionReferenceOutsideTargetException(from.Descriptor.SchemaId, from.Key.Value, role, outside.Descriptor.SchemaId, outside.Key.Value);
        }

        private static bool MatchesCitation(
            CultDocumentRegistry registry,
            IReadOnlyDictionary<string, List<Row>> byKey,
            Row citer,
            CultNetCitation citation,
            List<EdgeMatch> edgeSink)
        {
            var found = false;
            foreach (var member in ReferenceMembers(citer.Descriptor, citation.Role))
            {
                var role = member.IndexAlias ?? member.MemberName;
                foreach (var (targetKey, payload) in citer.Descriptor.ReferencesOf(citer.Document, role))
                {
                    // R-W: resolve and check the edge's own declared target first, for every edge this
                    // reference carries - before the citation's key/schema decides whether this is the
                    // edge the caller asked about.
                    if (!TryResolveReferenceTarget(registry, byKey, member.TargetType!, citer, role, targetKey.Value, out var resolved))
                        continue;

                    if (!string.Equals(targetKey.Value, citation.Target.RecordKey, StringComparison.Ordinal))
                        continue;
                    // R-E: one schema-identity rule everywhere - a cites target goes through the alias
                    // matcher, the same as `schemas`, instead of an exact CultDocumentDescriptor.SchemaId
                    // compare that a schema alias or version string could never satisfy.
                    if (!CultNetSchemaAliasMatching.Matches(citation.Target.SchemaId, resolved.Descriptor))
                        continue;
                    edgeSink.Add(new EdgeMatch(citer, role, resolved, payload));
                    found = true;
                }
            }

            return found;
        }

        private static HashSet<(string SchemaId, string RecordKey)> BuildIncomingIndex(
            CultDocumentRegistry registry,
            IReadOnlyList<Row> allRows,
            IReadOnlyDictionary<string, List<Row>> byKey,
            string role,
            List<EdgeMatch> edgeSink)
        {
            // R-V: iterate every row directly, not byKey.Values - a bare-key index collapses two
            // schemas' rows sharing a key into one slot, which used to silently drop one of them from
            // consideration as a citer entirely.
            var incoming = new HashSet<(string, string)>();
            foreach (var citer in allRows)
            {
                foreach (var member in ReferenceMembers(citer.Descriptor, role))
                {
                    var memberRole = member.IndexAlias ?? member.MemberName;
                    foreach (var (targetKey, payload) in citer.Descriptor.ReferencesOf(citer.Document, memberRole))
                    {
                        if (!TryResolveReferenceTarget(registry, byKey, member.TargetType!, citer, memberRole, targetKey.Value, out var resolved))
                            continue;
                        incoming.Add((resolved.Descriptor.SchemaId, resolved.Key.Value));
                        edgeSink.Add(new EdgeMatch(citer, memberRole, resolved, payload));
                    }
                }
            }

            return incoming;
        }

        // R-AK: no `!member.IsReference` guard here - CultCache.ReferencesOf (CultCache.cs:262-278)
        // already searches only its own RichMembers' reference members for a role match and
        // returns empty for anything else, including a data member's own alias (index aliases are
        // unique per descriptor, so a data member's role string can never coincide with a
        // reference member's). A second copy of the same exclusion here was duplicate authority,
        // not a second decision - CultCache owns "is this member a reference" once.
        private static IEnumerable<CultDocumentMemberView> ReferenceMembers(CultDocumentDescriptor descriptor, string? role)
        {
            foreach (var member in descriptor.DeclaredMembers)
            {
                if (role != null && (member.IndexAlias ?? member.MemberName) != role) continue;
                yield return member;
            }
        }
    }

    /// <summary>The typed refusal for an edge naming a row outside its reference's declared target (S18).</summary>
    public sealed class CultNetSelectionReferenceOutsideTargetException : Exception
    {
        public CultNetSelectionReferenceOutsideTargetException(string fromSchemaId, string fromKey, string role, string toSchemaId, string toKey)
            : base($"reference_outside_target: {fromSchemaId}/{fromKey} names {toSchemaId}/{toKey} through role \"{role}\", which is outside the reference's declared target.")
        {
            FromSchemaId = fromSchemaId;
            FromKey = fromKey;
            Role = role;
            ToSchemaId = toSchemaId;
            ToKey = toKey;
        }

        public string FromSchemaId { get; }
        public string FromKey { get; }
        public string Role { get; }
        public string ToSchemaId { get; }
        public string ToKey { get; }
    }

    /// <summary>The typed refusal for a cursor the server cannot answer (<c>cursor_stale</c>/<c>cursor_invalid</c>).</summary>
    public sealed class CultNetSelectionCursorException : Exception
    {
        public CultNetSelectionCursorException(string code, string message, ulong? asOf = null, ulong? current = null)
            : base(message)
        {
            Code = code;
            AsOf = asOf;
            Current = current;
        }

        public string Code { get; }

        /// <summary>The cursor's own minted <c>asOf</c>. Carried on the wire only for <c>cursor_stale</c> (R-N).</summary>
        public ulong? AsOf { get; }

        /// <summary>The answering server's current <c>asOf</c>. Carried on the wire only for <c>cursor_stale</c> (R-N).</summary>
        public ulong? Current { get; }
    }

    /// <summary>
    /// The answering process's key for cursor digests (R-O, F6: "cursors are keyed"). A digest is an
    /// HMAC-SHA256 under this key, so a cursor cannot be forged without it and cannot be verified by
    /// a different key. <see cref="Random"/> is never persisted or serialized; the answering server
    /// holds one instance for its process lifetime (<see cref="CultNetDatabase.CursorKey"/>), so a
    /// cursor minted before a restart carries a digest the new process's key does not produce, and
    /// the restart's first page for it answers <c>cursor_invalid</c> - acceptable, because cursors are
    /// short-lived and section 2 already calls them "minted by the answering server".
    /// </summary>
    public sealed class CultNetSelectionCursorKey
    {
        /// <summary>
        /// The key <see cref="CultNetSelectionEvaluator.Page"/>/<see cref="CultNetSelectionEvaluator.Select"/>
        /// fall back to when no caller-owned key is supplied - generated once, the first time this type
        /// is touched in the process, so a caller that evaluates selections without wiring up its own
        /// <see cref="CultNetDatabase"/> (most unit tests) still gets a keyed, per-process digest instead
        /// of an unkeyed one.
        /// </summary>
        internal static readonly CultNetSelectionCursorKey ProcessDefault = Random();

        private readonly byte[] _key;

        public CultNetSelectionCursorKey(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length == 0) throw new ArgumentException("Value must be non-empty.", nameof(key));
            _key = key;
        }

        /// <summary>A fresh 32-byte key from the OS RNG.</summary>
        public static CultNetSelectionCursorKey Random()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return new CultNetSelectionCursorKey(bytes);
        }

        internal byte[] KeyBytes => _key;
    }

    /// <summary>
    /// The opaque cursor: asOf, the last (ordinal, schemaId, recordKey), and an HMAC digest that covers
    /// that same body as well as the selection (with cursor and limit cleared), keyed under the
    /// answering process's <see cref="CultNetSelectionCursorKey"/> (R-O/R-Y). Minted only by the
    /// answering server.
    /// </summary>
    public readonly struct CultNetSelectionCursor
    {
        private CultNetSelectionCursor(ulong asOf, long ordinal, string schemaId, string recordKey, string digest)
        {
            AsOf = asOf;
            Ordinal = ordinal;
            SchemaId = schemaId;
            RecordKey = recordKey;
            Digest = digest;
        }

        public ulong AsOf { get; }
        public long Ordinal { get; }
        public string SchemaId { get; }
        public string RecordKey { get; }
        public string Digest { get; }

        public static string Mint(ulong asOf, CultNetSelectionEvaluator.Row lastRow, CultNetSelection selection, CultNetSelectionCursorKey key)
        {
            var digest = ComputeDigest(asOf, lastRow.Ordinal, lastRow.Descriptor.SchemaId, lastRow.Key.Value, selection, key);
            // R-O: the cursor body is length-prefixed fields, not a delimiter-joined string - a record
            // key carrying any character at all, including whatever delimiter an earlier scheme would
            // have chosen (Soul found a key containing U+241F broke a delimiter split), round-trips.
            var body = new StringBuilder();
            AppendString(body, asOf.ToString(CultureInfo.InvariantCulture));
            AppendString(body, lastRow.Ordinal.ToString(CultureInfo.InvariantCulture));
            AppendString(body, lastRow.Descriptor.SchemaId);
            AppendString(body, lastRow.Key.Value);
            AppendString(body, digest);
            return Base64UrlEncode(Encoding.UTF8.GetBytes(body.ToString()));
        }

        public static CultNetSelectionCursor Parse(string cursor)
        {
            byte[] bytes;
            try
            {
                bytes = Base64UrlDecode(cursor);
            }
            catch (FormatException)
            {
                throw new CultNetSelectionCursorException("cursor_invalid", "The cursor does not decode.");
            }

            var fields = ReadLengthPrefixedFields(bytes, 5);
            if (fields == null ||
                !ulong.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asOf) ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            {
                throw new CultNetSelectionCursorException("cursor_invalid", "The cursor does not decode.");
            }

            return new CultNetSelectionCursor(asOf, ordinal, fields[2], fields[3], fields[4]);
        }

        /// <summary>
        /// Verifies this cursor's digest against <paramref name="selection"/> under <paramref name="key"/>
        /// in fixed time (R-O): a cursor forged without the key, or minted under a different process's
        /// key (a restart), does not verify. R-Y: the digest is recomputed over this cursor's own body
        /// (<see cref="AsOf"/>, <see cref="Ordinal"/>, <see cref="SchemaId"/>, <see cref="RecordKey"/>)
        /// as well as the selection, so a cursor whose body was rewritten after minting - the ordinal or
        /// record key edited underneath a digest that was still legitimately minted for this selection -
        /// no longer verifies either (Soul's P4: "forged cursor position, reused digest").
        /// </summary>
        public bool VerifyDigest(CultNetSelection selection, CultNetSelectionCursorKey key)
        {
            var expected = ComputeDigest(AsOf, Ordinal, SchemaId, RecordKey, selection, key);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Digest),
                Encoding.UTF8.GetBytes(expected));
        }

        /// <summary>
        /// An HMAC-SHA256 digest, keyed under <paramref name="key"/>, of the cursor's own body
        /// (<paramref name="asOf"/>, <paramref name="ordinal"/>, <paramref name="schemaId"/>,
        /// <paramref name="recordKey"/>) plus the selection with cursor and limit cleared, so a cursor is
        /// bound to the exact position it was minted at as well as to the selection and the process that
        /// minted it (R-O/R-Y). R-H: every string and list is length-prefixed, so no delimiter choice
        /// can make two different selections collide - a delimiter-joined form used to digest
        /// values ["a|b"] the same as ["a","b"], and keys ["a,b"] the same as ["a","b"].
        /// </summary>
        public static string ComputeDigest(
            ulong asOf,
            long ordinal,
            string schemaId,
            string recordKey,
            CultNetSelection selection,
            CultNetSelectionCursorKey? key = null)
        {
            key ??= CultNetSelectionCursorKey.ProcessDefault;
            var sb = new StringBuilder();
            // R-Y: the body is part of what the HMAC signs - without this, a forged cursor could keep a
            // legitimately minted digest and rewrite the ordinal or record key underneath it, since
            // nothing about the body itself was ever authenticated.
            AppendString(sb, asOf.ToString(CultureInfo.InvariantCulture));
            AppendString(sb, ordinal.ToString(CultureInfo.InvariantCulture));
            AppendString(sb, schemaId);
            AppendString(sb, recordKey);
            AppendList(sb, selection.Schemas);
            AppendList(sb, selection.Keys);

            var fields = selection.Fields ?? Array.Empty<CultNetFieldPredicate>();
            sb.Append(fields.Length).Append(':');
            foreach (var field in fields)
            {
                AppendString(sb, field.Index);
                AppendString(sb, field.Op);
                AppendList(sb, field.Values);
                AppendString(sb, field.Number ?? string.Empty);
                sb.Append(field.Number == null ? '0' : '1');
            }

            if (selection.Cites != null)
            {
                sb.Append('1');
                AppendString(sb, selection.Cites.Target.SchemaId);
                AppendString(sb, selection.Cites.Target.RecordKey);
                AppendString(sb, selection.Cites.Role ?? string.Empty);
                sb.Append(selection.Cites.Role == null ? '0' : '1');
            }
            else
            {
                sb.Append('0');
            }

            if (selection.Cited != null)
            {
                sb.Append('1');
                AppendString(sb, selection.Cited.Role);
                sb.Append(selection.Cited.Exists ? '1' : '0');
            }
            else
            {
                sb.Append('0');
            }

            AppendString(sb, selection.Projection);
            sb.Append(selection.Descending ? '1' : '0');

            using var hmac = new HMACSHA256(key.KeyBytes);
            var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }

        /// <summary>Appends one length-prefixed string: unambiguous regardless of what the string itself contains.</summary>
        // The length prefix is a UTF-8 BYTE count, not string.Length's UTF-16 code-unit count - the
        // cursor body's prefix is read back out of UTF-8 bytes (ReadLengthPrefixedFields), and an
        // astral character (a surrogate pair: 2 UTF-16 code units, 4 UTF-8 bytes) would otherwise
        // slice the wrong number of bytes on the way back. The digest's canonical string never
        // round-trips through this byte-sliced reader - it is hashed whole - but keeping one counting
        // rule for both uses is what makes AppendString unambiguous rather than merely usually right.
        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        }

        /// <summary>Appends a count-prefixed list of length-prefixed strings, sorted by code point so the digest does not depend on wire order.</summary>
        private static void AppendList(StringBuilder sb, IReadOnlyList<string>? values)
        {
            var ordered = (values ?? Array.Empty<string>()).OrderBy(v => v, CultNetCodePointComparer.Instance).ToArray();
            sb.Append(ordered.Length).Append(':');
            foreach (var value in ordered)
                AppendString(sb, value);
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 0: break;
                default: throw new FormatException("Invalid base64url string length.");
            }

            return Convert.FromBase64String(padded);
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> length-prefixed fields written by
        /// <see cref="AppendString"/> back out of <paramref name="bytes"/>, in order. Null on any
        /// malformed length, a truncated value, a non-UTF-8 value, or leftover bytes after the last
        /// field - the cursor body has no field this reader may skip.
        /// </summary>
        private static List<string>? ReadLengthPrefixedFields(byte[] bytes, int count)
        {
            var fields = new List<string>(count);
            var pos = 0;
            for (var i = 0; i < count; i++)
            {
                var colon = Array.IndexOf(bytes, (byte)':', pos);
                if (colon < 0) return null;
                var lengthText = Encoding.UTF8.GetString(bytes, pos, colon - pos);
                if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                    return null;
                pos = colon + 1;
                // R-AG: checked arithmetic - an attacker-supplied prefix near int.MaxValue makes
                // `pos + length` overflow in unchecked int math (C#'s default), wrapping negative and
                // slipping past this guard; GetString then throws ArgumentOutOfRangeException instead
                // of this method returning null the way every other malformed shape does. `(long)`
                // promotes before the add so the sum can never wrap - both operands are bounded well
                // under long's range, so this can never itself overflow.
                if (length < 0 || (long)pos + length > bytes.Length) return null;
                fields.Add(Encoding.UTF8.GetString(bytes, pos, length));
                pos += length;
            }

            return pos == bytes.Length ? fields : null;
        }
    }

    /// <summary>
    /// Compares two strings by Unicode code point, not UTF-16 code unit (R-C, docs/cultnet-selection-cut.md).
    /// .NET's ordinal comparers (StringComparer.Ordinal, string.CompareOrdinal) compare UTF-16 code
    /// units, which sorts every astral character (encoded as a surrogate pair whose high half is in
    /// U+D800-U+DBFF) before every BMP character at or above U+E000 - the opposite of code-point order,
    /// and the opposite of what Rust gets by comparing UTF-8 bytes. Every ordered comparison the
    /// selection vocabulary makes - the row tiebreak, the cursor position, and edge order - uses this
    /// instead, so an astral key sorts the same way in both runtimes.
    /// </summary>
    public sealed class CultNetCodePointComparer : IComparer<string>
    {
        public static readonly CultNetCodePointComparer Instance = new();

        private CultNetCodePointComparer() { }

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var xi = 0;
            var yi = 0;
            while (xi < x.Length && yi < y.Length)
            {
                var xCodePoint = CodePointAt(x, xi, out var xSize);
                var yCodePoint = CodePointAt(y, yi, out var ySize);
                if (xCodePoint != yCodePoint)
                    return xCodePoint < yCodePoint ? -1 : 1;
                xi += xSize;
                yi += ySize;
            }

            return (x.Length - xi).CompareTo(y.Length - yi);
        }

        // A lone (unpaired) surrogate is not valid UTF-16, but a comparer must not throw on it - it is
        // treated as its own code unit value. It never round-trips to the wire as anything but
        // well-formed text, so this only has to not crash.
        private static int CodePointAt(string value, int index, out int size)
        {
            var unit = value[index];
            if (char.IsHighSurrogate(unit) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                size = 2;
                return char.ConvertToUtf32(unit, value[index + 1]);
            }

            size = 1;
            return unit;
        }
    }
}
