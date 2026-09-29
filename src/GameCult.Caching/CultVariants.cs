using System;
using System.Collections.Generic;
using System.Linq;

namespace GameCult.Caching
{
    // The store's encoding, as the cache needs it: the one pair CultInspectorModel and variant resolution share.
    // Serialize takes any value, not only documents, under its owning document type's options; Deserialize is its
    // inverse (a document type is checked against the registry, a member type is not); Overlay replaces slots of a
    // serialized document payload with already-encoded member values and touches nothing else.
    public sealed class CultCodec
    {
        public CultCodec(
            Func<object, Type, Type, byte[]> serialize,
            Func<Type, Type, byte[], object?> deserialize,
            Func<byte[], IReadOnlyList<KeyValuePair<int, byte[]>>, byte[]> overlay)
        {
            Serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            Deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
            Overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        }

        // (value, value type, owning document type)
        public Func<object, Type, Type, byte[]> Serialize { get; }

        // (value type, owning document type, bytes)
        public Func<Type, Type, byte[], object?> Deserialize { get; }

        // (document payload, slot and encoded value pairs)
        public Func<byte[], IReadOnlyList<KeyValuePair<int, byte[]>>, byte[]> Overlay { get; }
    }

    public enum CultOverrideOp
    {
        Set = 0,
        Insert = 1,
        Remove = 2
    }

    // One step of a member path: the MessagePack slot, and the id of the object-list element the step enters ("" when it
    // does not enter one).
    public sealed class CultPathStep
    {
        public CultPathStep(int slot, string elementId = "")
        {
            Slot = slot;
            ElementId = elementId ?? string.Empty;
        }

        public int Slot { get; }
        public string ElementId { get; }

        public override string ToString() => ElementId.Length == 0 ? $"[{Slot}]" : $"[{Slot}:{ElementId}]";
    }

    // The wire shape [op, path[], id, value]; value is the override's already-encoded MessagePack value.
    public sealed class CultVariantOverride
    {
        public CultVariantOverride(CultOverrideOp op, IReadOnlyList<CultPathStep> path, string id, byte[] value)
        {
            Op = op;
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Id = id ?? string.Empty;
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public CultOverrideOp Op { get; }
        public IReadOnlyList<CultPathStep> Path { get; }
        public string Id { get; }
        public byte[] Value { get; }

        // A top-level Set: the whole member at slot takes the encoded value.
        public static CultVariantOverride Set(int slot, byte[] value) =>
            new(CultOverrideOp.Set, new[] { new CultPathStep(slot) }, string.Empty, value);

        internal string PathText => string.Concat(Path.Select(step => step.ToString()));
    }

    // What a variant record stores: its base and its overrides. Never the resolved document.
    public sealed class CultVariantDelta
    {
        public CultVariantDelta(string baseKey, IReadOnlyList<CultVariantOverride> overrides)
        {
            BaseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            Overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        }

        public string BaseKey { get; }
        public IReadOnlyList<CultVariantOverride> Overrides { get; }

        // Member names by slot from the catalog the delta was loaded under, for warnings about slots the type dropped.
        // Not persisted.
        internal IReadOnlyDictionary<int, string>? SlotNames { get; set; }
    }
}
