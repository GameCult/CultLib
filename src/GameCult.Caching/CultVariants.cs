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

        /// <summary>
        /// Decodes a document payload read from a store file, as a cache load decodes it: the ids a load mints are minted from the key
        /// and their path, so every reader of one store mints the same ids, and a payload a cache write would refuse is refused here too.
        /// It reads no file and holds no state.
        /// </summary>
        /// <typeparam name="TDocument">The registered document type.</typeparam>
        /// <param name="key">The record key the payload was stored under.</param>
        /// <param name="payload">The record's payload.</param>
        /// <exception cref="CultElementIdException">The payload holds an element id or list a cache load refuses.</exception>
        public TDocument DecodeDocument<TDocument>(CultRecordKey key, byte[] payload)
            where TDocument : class
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var document = (TDocument)Deserialize(typeof(TDocument), typeof(TDocument), payload)!;
            CultElementIds.Plan(typeof(TDocument), document, key.Value, deterministic: true).Apply();
            return document;
        }
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
    }
}
