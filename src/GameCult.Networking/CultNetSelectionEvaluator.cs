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

            foreach (var field in selection.Fields)
            {
                if (!CultNetSelectionOperators.TryParse(field.Op, out var op))
                    return false;
                if (op == CultNetSelectionOperator.AnyOf)
                {
                    if (!descriptor.TryGetIndexValue(document, field.Index, out var value) ||
                        value == null ||
                        field.Values == null ||
                        !field.Values.Contains(value, StringComparer.Ordinal))
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

        /// <summary>
        /// Evaluates a selection over the full row set: schemas/keys/fields, the hop (cites/cited,
        /// with its edges and the reference_outside_target refusal), order, cursor and paging. The
        /// caller projects the result to wire records (<see cref="CultNetDocumentRegistry"/> owns that).
        /// </summary>
        public static Evaluation Select(
            CultDocumentRegistry registry,
            IReadOnlyList<Row> allRows,
            CultNetSelection selection,
            ulong asOf)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (allRows == null) throw new ArgumentNullException(nameof(allRows));
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            var byKey = new Dictionary<string, Row>(StringComparer.Ordinal);
            foreach (var row in allRows)
                byKey[row.Key.Value] = row;

            var candidates = allRows.Where(row => MatchesSchemaKeysFields(row.Descriptor, row.Key, row.Document, selection));

            var edges = new List<EdgeMatch>();
            if (selection.Cited != null)
            {
                var incoming = BuildIncomingIndex(registry, byKey, selection.Cited.Role, edges);
                var exists = selection.Cited.Exists;
                candidates = candidates.Where(row => incoming.Contains(row.Key.Value) == exists);
            }

            if (selection.Cites != null)
            {
                candidates = candidates.Where(row => MatchesCitation(registry, byKey, row, selection.Cites, edges));
            }

            var matched = candidates.ToArray();
            var ordered = (selection.Descending
                    ? matched.OrderByDescending(row => row.Ordinal).ThenByDescending(row => row.Descriptor.SchemaId, StringComparer.Ordinal).ThenByDescending(row => row.Key.Value, StringComparer.Ordinal)
                    : matched.OrderBy(row => row.Ordinal).ThenBy(row => row.Descriptor.SchemaId, StringComparer.Ordinal).ThenBy(row => row.Key.Value, StringComparer.Ordinal))
                .ToArray();

            var startIndex = 0;
            if (!string.IsNullOrEmpty(selection.Cursor))
            {
                var cursor = CultNetSelectionCursor.Parse(selection.Cursor!);
                if (cursor.Digest != CultNetSelectionCursor.ComputeDigest(selection))
                    throw new CultNetSelectionCursorException("cursor_invalid", "The cursor's selection digest does not match this selection.");
                if (cursor.AsOf != asOf)
                    throw new CultNetSelectionCursorException("cursor_stale", $"The cursor was minted at asOf {cursor.AsOf}; this server answers as of {asOf}.");
                startIndex = FindCursorPosition(ordered, cursor, selection.Descending);
            }

            var limit = (int)Math.Clamp(selection.Limit ?? LimitMax, LimitMin, LimitMax);
            var page = ordered.Skip(startIndex).Take(limit).ToArray();
            var hasNext = startIndex + page.Length < ordered.Length;
            var nextCursor = hasNext && page.Length > 0
                ? CultNetSelectionCursor.Mint(asOf, page[^1], selection)
                : null;

            var pageKeys = new HashSet<string>(page.Select(row => row.Key.Value), StringComparer.Ordinal);
            var pageEdges = selection.HasHop
                ? edges.Where(edge => pageKeys.Contains(edge.From.Key.Value)).ToArray()
                : Array.Empty<EdgeMatch>();

            return new Evaluation { Rows = page, Edges = pageEdges, NextCursor = nextCursor };
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
            var schema = string.CompareOrdinal(row.Descriptor.SchemaId, cursor.SchemaId);
            return schema != 0 ? schema : string.CompareOrdinal(row.Key.Value, cursor.RecordKey);
        }

        // D9: a reference's target set is every registered leaf assignable to its declared target type.
        // A stored edge naming a row whose schema is outside that set refuses the selection (S18).
        private static void EnsureWithinDeclaredTarget(CultDocumentRegistry registry, Type? targetType, Row from, string role, Row to)
        {
            if (targetType == null) return;
            var leaves = registry.ResolveTargetLeaves(targetType);
            if (leaves.Any(leaf => leaf.SchemaId == to.Descriptor.SchemaId)) return;
            throw new CultNetSelectionReferenceOutsideTargetException(from.Descriptor.SchemaId, from.Key.Value, role, to.Descriptor.SchemaId, to.Key.Value);
        }

        private static bool MatchesCitation(
            CultDocumentRegistry registry,
            IReadOnlyDictionary<string, Row> byKey,
            Row citer,
            CultNetCitation citation,
            List<EdgeMatch> edgeSink)
        {
            if (!byKey.TryGetValue(citation.Target.RecordKey, out var target))
                return false;

            var found = false;
            foreach (var member in ReferenceMembers(citer.Descriptor, citation.Role))
            {
                var role = member.IndexAlias ?? member.MemberName;
                foreach (var (targetKey, payload) in citer.Descriptor.ReferencesOf(citer.Document, role))
                {
                    if (!string.Equals(targetKey.Value, citation.Target.RecordKey, StringComparison.Ordinal))
                        continue;
                    if (!byKey.TryGetValue(targetKey.Value, out var resolved))
                        continue;
                    EnsureWithinDeclaredTarget(registry, member.TargetType, citer, role, resolved);
                    if (resolved.Descriptor.SchemaId != citation.Target.SchemaId)
                        continue;
                    edgeSink.Add(new EdgeMatch(citer, role, resolved, payload));
                    found = true;
                }
            }

            return found;
        }

        private static HashSet<string> BuildIncomingIndex(
            CultDocumentRegistry registry,
            IReadOnlyDictionary<string, Row> byKey,
            string role,
            List<EdgeMatch> edgeSink)
        {
            var incoming = new HashSet<string>(StringComparer.Ordinal);
            foreach (var citer in byKey.Values)
            {
                foreach (var member in ReferenceMembers(citer.Descriptor, role))
                {
                    var memberRole = member.IndexAlias ?? member.MemberName;
                    foreach (var (targetKey, payload) in citer.Descriptor.ReferencesOf(citer.Document, memberRole))
                    {
                        if (!byKey.TryGetValue(targetKey.Value, out var resolved))
                            continue;
                        EnsureWithinDeclaredTarget(registry, member.TargetType, citer, memberRole, resolved);
                        incoming.Add(targetKey.Value);
                        edgeSink.Add(new EdgeMatch(citer, memberRole, resolved, payload));
                    }
                }
            }

            return incoming;
        }

        private static IEnumerable<CultDocumentMemberView> ReferenceMembers(CultDocumentDescriptor descriptor, string? role)
        {
            foreach (var member in descriptor.DeclaredMembers)
            {
                if (!member.IsReference) continue;
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
        public CultNetSelectionCursorException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// The opaque cursor: asOf, the last (ordinal, schemaId, recordKey), and a digest of the selection
    /// with cursor and limit cleared (section 2). Minted only by the answering server.
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

        public static string Mint(ulong asOf, CultNetSelectionEvaluator.Row lastRow, CultNetSelection selection)
        {
            var digest = ComputeDigest(selection);
            var raw = string.Join(
                "",
                asOf.ToString(CultureInfo.InvariantCulture),
                lastRow.Ordinal.ToString(CultureInfo.InvariantCulture),
                lastRow.Descriptor.SchemaId,
                lastRow.Key.Value,
                digest);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        public static CultNetSelectionCursor Parse(string cursor)
        {
            string raw;
            try
            {
                raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            }
            catch (FormatException)
            {
                throw new CultNetSelectionCursorException("cursor_invalid", "The cursor does not decode.");
            }

            var parts = raw.Split('');
            if (parts.Length != 5 ||
                !ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asOf) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            {
                throw new CultNetSelectionCursorException("cursor_invalid", "The cursor does not decode.");
            }

            return new CultNetSelectionCursor(asOf, ordinal, parts[2], parts[3], parts[4]);
        }

        /// <summary>A digest of the selection with cursor and limit cleared, so a cursor is bound to the selection that minted it.</summary>
        public static string ComputeDigest(CultNetSelection selection)
        {
            var canonical = string.Join(
                "",
                string.Join(",", (selection.Schemas ?? Array.Empty<string>()).OrderBy(v => v, StringComparer.Ordinal)),
                string.Join(",", (selection.Keys ?? Array.Empty<string>()).OrderBy(v => v, StringComparer.Ordinal)),
                string.Join(";", (selection.Fields ?? Array.Empty<CultNetFieldPredicate>())
                    .Select(f => $"{f.Index}:{f.Op}:{string.Join("|", f.Values ?? Array.Empty<string>())}:{f.Number}")),
                selection.Cites == null ? "" : $"{selection.Cites.Target.SchemaId}/{selection.Cites.Target.RecordKey}:{selection.Cites.Role}",
                selection.Cited == null ? "" : $"{selection.Cited.Role}:{selection.Cited.Exists}",
                selection.Projection,
                selection.Descending.ToString());
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }
}
