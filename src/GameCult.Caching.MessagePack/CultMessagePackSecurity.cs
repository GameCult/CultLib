using System;
using System.Collections.Generic;
using MessagePack;

namespace GameCult.Caching.MessagePack;

// UntrustedData, plus a collision-resistant comparer for CultRecordRef<T> dictionary keys over the key string.
public sealed class CultMessagePackSecurity : MessagePackSecurity
{
    /// <summary>
    /// The deepest a document payload may nest, on read and on write, in every CultCache runtime: every MessagePack array and
    /// map counts, read by a formatter or skipped past it, and the outermost array is depth 1. A payload of this depth is
    /// accepted and one container deeper is refused. MessagePack's own object-graph counter is set to the same value as a
    /// backstop; the rule itself is <c>CultPayloadDepth.Check</c>, run by the document serialization entry points.
    /// </summary>
    public const int MaximumPayloadDepth = 256;

    public static readonly CultMessagePackSecurity Instance = new(UntrustedData.WithMaximumObjectGraphDepth(MaximumPayloadDepth));

    private CultMessagePackSecurity(MessagePackSecurity copyFrom) : base(copyFrom) { }

    protected override IEqualityComparer<T> GetHashCollisionResistantEqualityComparer<T>()
    {
        var type = typeof(T);
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(CultRecordRef<>))
            return base.GetHashCollisionResistantEqualityComparer<T>();
        var comparerType = typeof(RecordRefComparer<>).MakeGenericType(type.GetGenericArguments()[0]);
        return (IEqualityComparer<T>)Activator.CreateInstance(comparerType, GetEqualityComparer<string>())!;
    }

    protected override MessagePackSecurity Clone() => new CultMessagePackSecurity(this);

    private sealed class RecordRefComparer<TDocument> : IEqualityComparer<CultRecordRef<TDocument>>
    {
        private readonly IEqualityComparer<string> _keys;

        public RecordRefComparer(IEqualityComparer<string> keys) => _keys = keys;

        public bool Equals(CultRecordRef<TDocument> x, CultRecordRef<TDocument> y) => _keys.Equals(x.Key.Value, y.Key.Value);

        public int GetHashCode(CultRecordRef<TDocument> value) => _keys.GetHashCode(value.Key.Value);
    }
}
