using System;
using System.Collections.Generic;

namespace GameCult.Caching
{
    // Which members group a listed document type's records, outermost first.
    public sealed class CultInspectorGrouping
    {
        internal CultInspectorGrouping(Type listedType, IReadOnlyList<CultInspectorMember> members, string? notice)
        {
            ListedType = listedType;
            Members = members;
            Notice = notice;
        }

        public Type ListedType { get; }

        // Empty when nothing groups: no declaration, an opt-out, or a refused declaration (then Notice says why).
        public IReadOnlyList<CultInspectorMember> Members { get; }

        public string? Notice { get; }
    }

    // One node of a grouped record list. Interior nodes hold Children; leaves hold Records.
    public sealed class CultInspectorRecordGroup
    {
        internal CultInspectorRecordGroup(
            string id,
            string label,
            int depth,
            IReadOnlyList<object?> values,
            IReadOnlyList<CultInspectorRecordGroup> children,
            IReadOnlyList<CultStoredDocument> records,
            int count)
        {
            Id = id;
            Label = label;
            Depth = depth;
            Values = values;
            Children = children;
            Records = records;
            Count = count;
        }

        // Value identity along the path, not the label: stable across renames, so a lowering keys its foldout state on it.
        public string Id { get; }
        public string Label { get; }

        // 0 for the outermost level.
        public int Depth { get; }

        // The grouped member values on the path to this node, outermost first.
        public IReadOnlyList<object?> Values { get; }
        public IReadOnlyList<CultInspectorRecordGroup> Children { get; }
        public IReadOnlyList<CultStoredDocument> Records { get; }

        // The records under this node, at any depth.
        public int Count { get; }
    }
}
