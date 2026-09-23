using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// The four comparison and one any-of operator a field predicate may carry. Serialised on the wire
    /// as the <c>op</c> string (<see cref="CultNetSelectionOperators"/>), never as a union: the C#
    /// enum exists for evaluator and validation ergonomics only.
    /// </summary>
    public enum CultNetSelectionOperator
    {
        /// <summary>Matches when the row's declared value is one of <c>values</c>.</summary>
        AnyOf,
        /// <summary>Matches when the row's declared numeric value is less than <c>number</c>.</summary>
        Lt,
        /// <summary>Matches when the row's declared numeric value is less than or equal to <c>number</c>.</summary>
        Le,
        /// <summary>Matches when the row's declared numeric value is greater than or equal to <c>number</c>.</summary>
        Ge,
        /// <summary>Matches when the row's declared numeric value is greater than <c>number</c>.</summary>
        Gt
    }

    /// <summary>Converts <see cref="CultNetSelectionOperator"/> to and from its wire spelling.</summary>
    public static class CultNetSelectionOperators
    {
        /// <summary>Returns the wire spelling of an operator.</summary>
        public static string ToWireString(this CultNetSelectionOperator op) => op switch
        {
            CultNetSelectionOperator.AnyOf => "any_of",
            CultNetSelectionOperator.Lt => "lt",
            CultNetSelectionOperator.Le => "le",
            CultNetSelectionOperator.Ge => "ge",
            CultNetSelectionOperator.Gt => "gt",
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };

        /// <summary>Parses a wire operator string; returns false for anything else.</summary>
        public static bool TryParse(string? wire, out CultNetSelectionOperator op)
        {
            switch (wire)
            {
                case "any_of": op = CultNetSelectionOperator.AnyOf; return true;
                case "lt": op = CultNetSelectionOperator.Lt; return true;
                case "le": op = CultNetSelectionOperator.Le; return true;
                case "ge": op = CultNetSelectionOperator.Ge; return true;
                case "gt": op = CultNetSelectionOperator.Gt; return true;
                default: op = default; return false;
            }
        }

        /// <summary>True for the four comparison operators (everything but any_of).</summary>
        public static bool IsComparison(this CultNetSelectionOperator op) => op != CultNetSelectionOperator.AnyOf;
    }

    /// <summary>
    /// The wire's canonical decimal form (Q-J, docs/cultnet-selection-cut.md section 2 "Numbers"): one
    /// spelling per value, so the cursor digest and the cross-runtime parity vectors need no numeric
    /// parser and no precision limit. A comparison number that does not match <see cref="Pattern"/>, or
    /// that spells zero with a leading minus, is refused at the door rather than normalised - a
    /// non-canonical spelling of an equal value never reaches the evaluator.
    /// </summary>
    public static class CultNetCanonicalNumber
    {
        /// <summary>
        /// Forbids leading zeros, a trailing fractional zero or bare point, an explicit '+', exponent
        /// notation, and whitespace. Does not by itself forbid "-0"; <see cref="IsCanonical"/> does.
        /// Anchored with <c>\z</c>, not <c>$</c>: .NET's <c>$</c> matches immediately before a single
        /// trailing newline even without <see cref="System.Text.RegularExpressions.RegexOptions.Multiline"/>,
        /// so a naive <c>$</c> here would accept "5\n" as canonical (R-D/Q-J; the JSON schema's ECMA-262
        /// <c>$</c> has no such exception and already refused it).
        /// </summary>
        public const string Pattern = @"\A-?(0|[1-9][0-9]*)(\.[0-9]*[1-9])?\z";

        private static readonly Regex CanonicalRegex = new(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>True for a canonical decimal string; false for null, "-0", or any other spelling.</summary>
        public static bool IsCanonical(string? value) =>
            value != null && value != "-0" && CanonicalRegex.IsMatch(value);

        /// <summary>
        /// Compares two canonical decimal strings by sign, then the integer part's length, then its
        /// digits, then the fraction digits padded on the right to equal length - a pure string
        /// comparison, never a numeric parse, so no CLR type's range limits what a comparison can hold.
        /// Callers must validate both operands with <see cref="IsCanonical"/> first; this does not
        /// re-validate.
        /// </summary>
        public static int Compare(string left, string right)
        {
            var (negativeLeft, integerLeft, fractionLeft) = Decompose(left);
            var (negativeRight, integerRight, fractionRight) = Decompose(right);

            if (negativeLeft != negativeRight)
                return negativeLeft ? -1 : 1;
            var sign = negativeLeft ? -1 : 1;

            if (integerLeft.Length != integerRight.Length)
                return sign * (integerLeft.Length < integerRight.Length ? -1 : 1);

            var integerCompare = string.CompareOrdinal(integerLeft, integerRight);
            if (integerCompare != 0)
                return sign * Math.Sign(integerCompare);

            var fractionLength = Math.Max(fractionLeft.Length, fractionRight.Length);
            var fractionCompare = string.CompareOrdinal(
                fractionLeft.PadRight(fractionLength, '0'),
                fractionRight.PadRight(fractionLength, '0'));
            return sign * Math.Sign(fractionCompare);
        }

        private static (bool Negative, string Integer, string Fraction) Decompose(string canonical)
        {
            var negative = canonical.Length > 0 && canonical[0] == '-';
            var unsigned = negative ? canonical.Substring(1) : canonical;
            var dot = unsigned.IndexOf('.');
            return dot < 0
                ? (negative, unsigned, string.Empty)
                : (negative, unsigned.Substring(0, dot), unsigned.Substring(dot + 1));
        }
    }

    /// <summary>One predicate over a single declared index alias (docs/cultnet-selection-cut.md, section 2).</summary>
    [MessagePackObject]
    public sealed class CultNetFieldPredicate
    {
        /// <summary>A declared index alias, reachable on some schema the selection can reach.</summary>
        [Key("index")] public string Index { get; set; } = string.Empty;
        /// <summary>"any_of", "lt", "le", "ge", or "gt".</summary>
        [Key("op")] public string Op { get; set; } = string.Empty;
        /// <summary>Present iff op = any_of; non-empty.</summary>
        [Key("values")] public string[]? Values { get; set; }
        /// <summary>Present iff op is a comparison; a canonical decimal string (Q-J, <see cref="CultNetCanonicalNumber"/>).</summary>
        [Key("number")] public string? Number { get; set; }
    }

    /// <summary>Identifies one row on the wire by schema and record key.</summary>
    [MessagePackObject]
    public sealed class CultNetRecordRef
    {
        /// <summary>Gets or sets the schema id.</summary>
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        /// <summary>Gets or sets the record key.</summary>
        [Key("recordKey")] public string RecordKey { get; set; } = string.Empty;
    }

    /// <summary>Selects rows whose declared reference names a target row (docs/cultnet-selection-cut.md, section 2).</summary>
    [MessagePackObject]
    public sealed class CultNetCitation
    {
        /// <summary>The row the citer must name.</summary>
        [Key("target")] public CultNetRecordRef Target { get; set; } = new();
        /// <summary>The reference's declared alias; absent matches any declared reference.</summary>
        [Key("role")] public string? Role { get; set; }
    }

    /// <summary>Selects rows some other row does or does not name in <c>role</c> - the one negation.</summary>
    [MessagePackObject]
    public sealed class CultNetIncoming
    {
        /// <summary>The declared reference alias to check for an incoming edge.</summary>
        [Key("role")] public string Role { get; set; } = string.Empty;
        /// <summary>True to select rows with an incoming edge, false to select rows without one.</summary>
        [Key("exists")] public bool Exists { get; set; }
    }

    /// <summary>
    /// One typed selection, carried by <c>cultnet.snapshot_request.v1</c> and
    /// <c>cultnet.database_subscribe.v1</c>. docs/cultnet-selection-cut.md, section 2.
    /// </summary>
    [MessagePackObject]
    public sealed class CultNetSelection
    {
        /// <summary>Kinds: schema ids or aliases, matched by <see cref="CultNetSchemaAliasMatching"/>. Absent = every schema.</summary>
        [Key("schemas")] public string[]? Schemas { get; set; }
        /// <summary>Record-key allowlist. Absent = every key.</summary>
        [Key("keys")] public string[]? Keys { get; set; }
        /// <summary>Conjunction; each is any-of over one declared index alias.</summary>
        [Key("fields")] public CultNetFieldPredicate[]? Fields { get; set; }
        /// <summary>Rows whose declared reference names <c>target</c>.</summary>
        [Key("cites")] public CultNetCitation? Cites { get; set; }
        /// <summary>Rows some row does or does not name in <c>role</c>.</summary>
        [Key("cited")] public CultNetIncoming? Cited { get; set; }
        /// <summary>"header" or "document"; default "header".</summary>
        [Key("projection")] public string Projection { get; set; } = CultNetSelectionProjections.Header;
        /// <summary>Reverses the one order.</summary>
        [Key("descending")] public bool Descending { get; set; }
        /// <summary>Clamped 1..=200.</summary>
        [Key("limit")] public uint? Limit { get; set; }
        /// <summary>Opaque; minted by the answering server.</summary>
        [Key("cursor")] public string? Cursor { get; set; }

        /// <summary>True when this selection follows an edge (section 2: "Edges in the answer").</summary>
        [IgnoreMember]
        public bool HasHop => Cites != null || Cited != null;
    }

    /// <summary>
    /// Lowers a v0 <c>schemaIds</c>/<c>recordKeys</c> filter into cultnet.selection.v1's null-means-
    /// every vocabulary (docs/cultnet-selection-cut.md, Self's rulings 2026-09-22): v0's old cleaning
    /// carried over as the one place that decides it. An absent list, an empty one, or one made only
    /// of blank entries all lower to null; a mixed list keeps its non-blank entries, deduplicated.
    /// This is a v0-compatibility rule, not a meaning <see cref="CultNetSelectionEvaluator"/> or
    /// <see cref="CultNetSelectionValidation"/> carries - v1 selections are refused at the door
    /// instead (S1).
    /// </summary>
    public static class CultNetV0SelectionLowering
    {
        /// <summary>
        /// Lowers one v0 filter list; see the type summary. R-R (2026-09-22): a <c>null</c> list means
        /// "no filter" and stays <c>null</c>; a non-null list, even one that filters down to nothing,
        /// stays a real (possibly empty) array. Collapsing an explicit empty list to "no filter" was
        /// wrong - the pre-cut reference (<c>CultNetDatabase.CreateShardSnapshotResponse</c> at
        /// <c>b3d9cf7</c>) tests <c>filter?.RecordKeys != null</c>, not a length, so
        /// <c>recordKeys: []</c> always answered empty. The evaluator already honours that distinction
        /// (<c>CultNetSelectionEvaluator</c>: a non-null <c>Keys</c> array excludes every row when it
        /// is empty); only this lowering used to erase it before the selection ever reached the
        /// evaluator.
        /// </summary>
        public static string[]? Lower(IReadOnlyList<string>? values)
        {
            if (values == null)
                return null;

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>The two wire values <see cref="CultNetSelection.Projection"/> may carry.</summary>
    public static class CultNetSelectionProjections
    {
        /// <summary>The record without its payload.</summary>
        public const string Header = "header";
        /// <summary>The full record.</summary>
        public const string Document = "document";
    }

    /// <summary>One edge a hop traversed, carried on <see cref="CultNetSelectionPage"/>.</summary>
    [MessagePackObject]
    public sealed class CultNetEdge
    {
        /// <summary>The citing row.</summary>
        [Key("from")] public CultNetRecordRef From { get; set; } = new();
        /// <summary>The declared reference alias the citer used.</summary>
        [Key("role")] public string Role { get; set; } = string.Empty;
        /// <summary>The cited row, its schema resolved to the leaf actually stored.</summary>
        [Key("to")] public CultNetRecordRef To { get; set; } = new();
        /// <summary>"messagepack" when <see cref="Payload"/> is present.</summary>
        [Key("payloadEncoding")] public string? PayloadEncoding { get; set; }
        /// <summary>The value attached to the edge (a dictionary reference's value); present under document projection only.</summary>
        [Key("payload")] public byte[]? Payload { get; set; }
    }

    /// <summary>
    /// A header record: <see cref="CultNetRawDocumentRecord"/> minus its payload and payloadEncoding.
    /// </summary>
    [MessagePackObject]
    public sealed class CultNetRawDocumentHeader
    {
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        [Key("schemaName")] public string? SchemaName { get; set; }
        [Key("schemaVersion")] public string? SchemaVersion { get; set; }
        [Key("schemaContentHash")] public string? SchemaContentHash { get; set; }
        [Key("recordKey")] public string RecordKey { get; set; } = string.Empty;
        [Key("storedAt")] public string StoredAt { get; set; } = string.Empty;
        [Key("sourceRuntimeId")] public string? SourceRuntimeId { get; set; }
        [Key("sourceAgentId")] public string? SourceAgentId { get; set; }
        [Key("sourceRole")] public string? SourceRole { get; set; }
        [Key("tags")] public string[]? Tags { get; set; }

        /// <summary>Builds a header by dropping a raw record's payload.</summary>
        public static CultNetRawDocumentHeader FromRecord(CultNetRawDocumentRecord record) => new()
        {
            SchemaId = record.SchemaId,
            SchemaName = record.SchemaName,
            SchemaVersion = record.SchemaVersion,
            SchemaContentHash = record.SchemaContentHash,
            RecordKey = record.RecordKey,
            StoredAt = record.StoredAt,
            SourceRuntimeId = record.SourceRuntimeId,
            SourceAgentId = record.SourceAgentId,
            SourceRole = record.SourceRole,
            Tags = record.Tags
        };
    }

    /// <summary>
    /// The result of evaluating a selection, before it is carried by a wire message. Exactly one of
    /// <see cref="Headers"/> / <see cref="Documents"/> is populated, by projection.
    /// </summary>
    public sealed class CultNetSelectionPage
    {
        public uint Matched { get; set; }
        public ulong AsOf { get; set; }
        public string? Next { get; set; }
        public CultNetRawDocumentHeader[]? Headers { get; set; }
        public CultNetRawDocumentRecord[]? Documents { get; set; }
        public CultNetEdge[]? Edges { get; set; }
    }

    /// <summary>
    /// Refused typed at the door (section 2/3): a name, list, or comparison the declared shape cannot
    /// support. Carried as <c>cultnet.error.v0 { code: "selection_invalid", details: { field, value } }</c>.
    /// </summary>
    public sealed class CultNetSelectionInvalidException : Exception
    {
        public CultNetSelectionInvalidException(string field, string? value, string message)
            : base(message)
        {
            Field = field;
            Value = value;
        }

        /// <summary>The selection field that failed ("schemas", "keys", "fields[i].index", "cites.role", "cited.role", ...).</summary>
        public string Field { get; }

        /// <summary>The offending value, when there is one string worth naming.</summary>
        public string? Value { get; }
    }

    /// <summary>
    /// Validates a <see cref="CultNetSelection"/> against the declared shape of the schemas it can
    /// reach - the door refusals of docs/cultnet-selection-cut.md section 2/3 (S1).
    /// </summary>
    public static class CultNetSelectionValidation
    {
        /// <summary>
        /// The declaration-independent half of the door: empty/blank <c>schemas</c>/<c>keys</c> and an
        /// unrecognised <c>projection</c>. Runs first inside <see cref="Validate"/>, and is also the
        /// whole door for a caller with no descriptor list in hand - CultMesh's
        /// <c>EnsureV0Compatible</c> (docs/cultnet-selection-cut.md, R-F) runs this before its own v0
        /// transport refusals, so <c>Keys=[]</c>/<c>[""]</c> is refused there too, not only in the
        /// reference runtime's full evaluator path.
        /// </summary>
        public static void ValidateShape(CultNetSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            if (selection.Schemas != null && selection.Schemas.Length == 0)
                throw new CultNetSelectionInvalidException("schemas", null, "selection.schemas is present and empty; omit it to reach every schema.");
            if (selection.Keys != null && selection.Keys.Length == 0)
                throw new CultNetSelectionInvalidException("keys", null, "selection.keys is present and empty; omit it to reach every key.");
            if (selection.Schemas != null)
            {
                foreach (var schema in selection.Schemas)
                {
                    if (string.IsNullOrWhiteSpace(schema))
                        throw new CultNetSelectionInvalidException("schemas", schema, "selection.schemas carries an empty or whitespace entry.");
                }
            }
            if (selection.Keys != null)
            {
                foreach (var key in selection.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        throw new CultNetSelectionInvalidException("keys", key, "selection.keys carries an empty or whitespace entry.");
                }
            }
            if (selection.Projection is not (CultNetSelectionProjections.Header or CultNetSelectionProjections.Document))
                throw new CultNetSelectionInvalidException("projection", selection.Projection, $"selection.projection \"{selection.Projection}\" is neither \"header\" nor \"document\".");
        }

        public static void Validate(this CultNetSelection selection, IReadOnlyList<CultDocumentDescriptor> allDescriptors)
        {
            if (allDescriptors == null) throw new ArgumentNullException(nameof(allDescriptors));

            ValidateShape(selection);

            var reachable = ReachableSchemas(selection, allDescriptors);
            if (selection.Fields != null)
            {
                for (var i = 0; i < selection.Fields.Length; i++)
                {
                    ValidateField(selection.Fields[i], i, reachable);
                }
            }

            if (selection.Cites != null)
            {
                if (string.IsNullOrEmpty(selection.Cites.Target?.SchemaId) || string.IsNullOrEmpty(selection.Cites.Target?.RecordKey))
                    throw new CultNetSelectionInvalidException("cites.target", null, "selection.cites.target requires schemaId and recordKey.");
                // R-E (docs/cultnet-selection-cut.md): a cites target names a schema through the same
                // alias matcher `schemas` uses. One that matches no declared schema is refused here,
                // never answered with an empty page by MatchesCitation silently treating an unresolved
                // target as "not found".
                if (!allDescriptors.Any(descriptor => CultNetSchemaAliasMatching.Matches(selection.Cites.Target!.SchemaId, descriptor)))
                    throw new CultNetSelectionInvalidException("cites.target.schemaId", selection.Cites.Target!.SchemaId, $"selection.cites.target.schemaId \"{selection.Cites.Target!.SchemaId}\" does not match any declared schema.");
                if (selection.Cites.Role != null && !AnySchemaDeclaresRole(allDescriptors, selection.Cites.Role))
                    throw new CultNetSelectionInvalidException("cites.role", selection.Cites.Role, $"selection.cites.role \"{selection.Cites.Role}\" is not declared by any schema.");
            }

            if (selection.Cited != null)
            {
                if (string.IsNullOrEmpty(selection.Cited.Role))
                    throw new CultNetSelectionInvalidException("cited.role", null, "selection.cited.role must be non-empty.");
                if (!AnySchemaDeclaresRole(allDescriptors, selection.Cited.Role))
                    throw new CultNetSelectionInvalidException("cited.role", selection.Cited.Role, $"selection.cited.role \"{selection.Cited.Role}\" is not declared by any schema.");
            }
        }

        private static void ValidateField(CultNetFieldPredicate field, int index, IReadOnlyList<CultDocumentDescriptor> reachable)
        {
            var prefix = $"fields[{index}]";
            if (string.IsNullOrEmpty(field.Index))
                throw new CultNetSelectionInvalidException($"{prefix}.index", null, $"{prefix}.index must be non-empty.");
            if (!CultNetSelectionOperators.TryParse(field.Op, out var op))
                throw new CultNetSelectionInvalidException($"{prefix}.op", field.Op, $"{prefix}.op \"{field.Op}\" is not any_of/lt/le/ge/gt.");

            if (op == CultNetSelectionOperator.AnyOf)
            {
                if (field.Values == null || field.Values.Length == 0)
                    throw new CultNetSelectionInvalidException($"{prefix}.values", null, $"{prefix}.values must be non-empty for op any_of.");
                if (field.Number != null)
                    throw new CultNetSelectionInvalidException($"{prefix}.number", null, $"{prefix} carries both values and number; any_of takes only values.");
            }
            else
            {
                if (field.Number == null)
                    throw new CultNetSelectionInvalidException($"{prefix}.number", null, $"{prefix}.number is required for op {field.Op}.");
                if (!CultNetCanonicalNumber.IsCanonical(field.Number))
                    throw new CultNetSelectionInvalidException($"{prefix}.number", field.Number, $"{prefix}.number \"{field.Number}\" is not a canonical decimal string (Q-J): it must match {CultNetCanonicalNumber.Pattern} and not be \"-0\".");
                if (field.Values != null)
                    throw new CultNetSelectionInvalidException($"{prefix}.values", null, $"{prefix} carries both values and number; a comparison takes only number.");
            }

            var declaring = reachable
                .Select(descriptor => descriptor.DeclaredMembers.FirstOrDefault(member => member.IndexAlias == field.Index))
                .Where(member => member.MemberName != null)
                .ToArray();
            if (declaring.Length == 0)
                throw new CultNetSelectionInvalidException($"{prefix}.index", field.Index, $"{prefix}.index \"{field.Index}\" is not declared by any schema this selection can reach.");
            if (op.IsComparison() && declaring.Any(member => !member.IsNumeric))
                throw new CultNetSelectionInvalidException($"{prefix}.index", field.Index, $"{prefix}.index \"{field.Index}\" is declared non-numeric on a schema this selection can reach; comparisons need a numeric declaration everywhere the alias is reachable.");
        }

        private static bool AnySchemaDeclaresRole(IReadOnlyList<CultDocumentDescriptor> allDescriptors, string role) =>
            allDescriptors.Any(descriptor => descriptor.DeclaredMembers.Any(member =>
                member.IsReference && (member.IndexAlias ?? member.MemberName) == role));

        /// <summary>The schemas <see cref="CultNetSelection.Schemas"/> reaches - every schema when absent.</summary>
        public static IReadOnlyList<CultDocumentDescriptor> ReachableSchemas(
            CultNetSelection selection, IReadOnlyList<CultDocumentDescriptor> allDescriptors)
        {
            if (selection.Schemas == null || selection.Schemas.Length == 0)
                return allDescriptors;
            return allDescriptors.Where(descriptor => CultNetSchemaAliasMatching.MatchesAny(selection.Schemas, descriptor)).ToArray();
        }
    }
}
