using System;

namespace GameCult.Caching
{
    /// <summary>
    /// Marks a type as a CultCache document with a stable schema name and version.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultDocumentAttribute : Attribute
    {
        /// <summary>
        /// Creates a document marker for the supplied schema identity.
        /// </summary>
        public CultDocumentAttribute(string schemaName, string schemaVersion)
        {
            SchemaName = string.IsNullOrWhiteSpace(schemaName)
                ? throw new ArgumentException("SchemaName must be non-empty.", nameof(schemaName))
                : schemaName;
            SchemaVersion = string.IsNullOrWhiteSpace(schemaVersion)
                ? throw new ArgumentException("SchemaVersion must be non-empty.", nameof(schemaVersion))
                : schemaVersion;
        }

        /// <summary>
        /// Gets the stable schema name used by CultCache and CultNet.
        /// </summary>
        public string SchemaName { get; }

        /// <summary>
        /// Gets the semantic schema version string for this document shape.
        /// </summary>
        public string SchemaVersion { get; }
    }

    /// <summary>
    /// Marks a document type as having one global record in a cache.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class CultGlobalAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks the member that supplies the human-readable document name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultNameAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a member as an indexed lookup value.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class CultIndexAttribute : Attribute
    {
        /// <summary>
        /// Creates an index marker with an optional alias.
        /// </summary>
        public CultIndexAttribute(string? alias = null)
        {
            Alias = alias;
        }

        /// <summary>
        /// Gets the external index alias, or null to use the member name.
        /// </summary>
        public string? Alias { get; }
    }

    /// <summary>
    /// Marks a member as a reference to another CultCache document.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class CultReferenceAttribute : Attribute
    {
        /// <summary>
        /// Creates a document reference marker.
        /// </summary>
        public CultReferenceAttribute(Type? targetType = null, bool many = false)
        {
            TargetType = targetType;
            Many = many;
        }

        /// <summary>
        /// Gets the explicit referenced document type, when one is required.
        /// </summary>
        public Type? TargetType { get; }

        /// <summary>
        /// Gets whether this member stores many references rather than one reference.
        /// </summary>
        public bool Many { get; }
    }

    /// <summary>
    /// Identifies a record inside a CultCache backing store.
    /// </summary>
    public readonly struct CultRecordKey : IEquatable<CultRecordKey>
    {
        /// <summary>
        /// Creates a record key from its persisted string value.
        /// </summary>
        public CultRecordKey(string value)
        {
            Value = value ?? string.Empty;
        }

        /// <summary>
        /// Gets the persisted key value.
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Returns the persisted key value.
        /// </summary>
        public override string ToString() => Value;

        /// <summary>
        /// Returns whether this key and another key contain the same persisted value.
        /// </summary>
        public bool Equals(CultRecordKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <summary>
        /// Returns whether this key and another object contain the same persisted value.
        /// </summary>
        public override bool Equals(object? obj) => obj is CultRecordKey other && Equals(other);

        /// <summary>
        /// Returns a hash code for the persisted key value.
        /// </summary>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>
    /// Carries the persisted record key for a typed document.
    /// </summary>
    public readonly struct CultRecordHandle<T>
    {
        /// <summary>
        /// Creates a typed document handle.
        /// </summary>
        public CultRecordHandle(CultRecordKey key)
        {
            Key = key;
        }

        /// <summary>
        /// Gets the persisted record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Returns the persisted key value.
        /// </summary>
        public override string ToString() => Key.ToString();
    }

    /// <summary>
    /// Stores a typed reference to a CultCache document.
    /// </summary>
    public readonly struct CultRecordRef<T>
    {
        /// <summary>
        /// Creates a typed document reference.
        /// </summary>
        public CultRecordRef(CultRecordKey key)
        {
            Key = key;
        }

        /// <summary>
        /// Gets the referenced record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Resolves the referenced record against the provided cache.
        /// </summary>
        public T? Resolve(CultCache cache)
        {
            if (cache == null) throw new ArgumentNullException(nameof(cache));
            return cache.Get(Key) is T typed ? typed : default;
        }

        /// <summary>
        /// Returns the referenced key value.
        /// </summary>
        public override string ToString() => Key.ToString();
    }
}
