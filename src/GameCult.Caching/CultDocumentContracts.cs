using System;

namespace GameCult.Caching
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultDocumentAttribute : Attribute
    {
        public CultDocumentAttribute(string schemaName, string schemaVersion)
        {
            SchemaName = string.IsNullOrWhiteSpace(schemaName)
                ? throw new ArgumentException("SchemaName must be non-empty.", nameof(schemaName))
                : schemaName;
            SchemaVersion = string.IsNullOrWhiteSpace(schemaVersion)
                ? throw new ArgumentException("SchemaVersion must be non-empty.", nameof(schemaVersion))
                : schemaVersion;
        }

        public string SchemaName { get; }
        public string SchemaVersion { get; }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultGlobalAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultNameAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class CultIndexAttribute : Attribute
    {
        public CultIndexAttribute(string? alias = null)
        {
            Alias = alias;
        }

        public string? Alias { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultReferenceAttribute : Attribute
    {
        public CultReferenceAttribute(Type? targetType = null, bool many = false)
        {
            TargetType = targetType;
            Many = many;
        }

        public Type? TargetType { get; }
        public bool Many { get; }
    }

    public readonly struct CultRecordKey : IEquatable<CultRecordKey>
    {
        public CultRecordKey(string value)
        {
            Value = value ?? string.Empty;
        }

        public string Value { get; }

        public override string ToString() => Value;

        public bool Equals(CultRecordKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is CultRecordKey other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    }

    public readonly struct CultRecordHandle<T>
    {
        public CultRecordHandle(CultRecordKey key)
        {
            Key = key;
        }

        public CultRecordKey Key { get; }

        public override string ToString() => Key.ToString();
    }

    public readonly struct CultRecordRef<T>
    {
        public CultRecordRef(CultRecordKey key)
        {
            Key = key;
        }

        public CultRecordKey Key { get; }

        public T? Resolve(CultCache cache)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            return cache.Get(Key) is T typed ? typed : default;
        }

        public override string ToString() => Key.ToString();
    }
}
