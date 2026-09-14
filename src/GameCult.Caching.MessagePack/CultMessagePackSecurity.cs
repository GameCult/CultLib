using System;
using System.Collections.Generic;
using MessagePack;

namespace GameCult.Caching.MessagePack;

// UntrustedData, plus a collision-resistant comparer for CultRecordRef<T> dictionary keys over the key string.
public sealed class CultMessagePackSecurity : MessagePackSecurity
{
    public static readonly CultMessagePackSecurity Instance = new(UntrustedData);

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
