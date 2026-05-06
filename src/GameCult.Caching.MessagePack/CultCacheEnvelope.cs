using System;
using System.Reflection;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// Represents a canonical CultCache payload envelope for cross-runtime exchange.
/// </summary>
public sealed class CultCacheEnvelope
{
    /// <summary>
    /// Gets or sets the logical document key for the serialized entry.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the shared document type identifier for the serialized entry.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw MessagePack payload bytes for the entry.
    /// </summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the RFC3339 timestamp associated with the serialized payload.
    /// </summary>
    public string StoredAt { get; set; } = string.Empty;
}

/// <summary>
/// Exposes canonical MessagePack payload helpers for <see cref="DatabaseEntry"/> values.
/// </summary>
public static class CultCacheEnvelopeSerialization
{
    /// <summary>
    /// Serializes a concrete entry to the canonical MessagePack payload bytes used by CultCache.
    /// </summary>
    public static byte[] SerializePayload(DatabaseEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var method = typeof(CultCacheEnvelopeSerialization)
            .GetMethod(nameof(SerializeGeneric), BindingFlags.NonPublic | BindingFlags.Static)
            ?.MakeGenericMethod(entry.GetType())
            ?? throw new InvalidOperationException("Failed to locate typed CultCache envelope serializer.");
        return (byte[])method.Invoke(null, [entry])!;
    }

    /// <summary>
    /// Deserializes canonical MessagePack payload bytes into a concrete entry type.
    /// </summary>
    public static T DeserializePayload<T>(byte[] payload) where T : DatabaseEntry
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        return DatabaseEntrySerialization.Deserialize<T>(payload);
    }

    /// <summary>
    /// Deserializes canonical MessagePack payload bytes into an abstract <see cref="DatabaseEntry"/>.
    /// </summary>
    public static DatabaseEntry DeserializePayload(byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        return DatabaseEntrySerialization.Deserialize<DatabaseEntry>(payload);
    }

    /// <summary>
    /// Creates a full exchange envelope for a concrete entry using the supplied metadata.
    /// </summary>
    public static CultCacheEnvelope CreateEnvelope(
        DatabaseEntry entry,
        string key,
        string type,
        string storedAt)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Envelope key must be non-empty.", nameof(key));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Envelope type must be non-empty.", nameof(type));
        if (string.IsNullOrWhiteSpace(storedAt)) throw new ArgumentException("Envelope storedAt must be non-empty.", nameof(storedAt));

        return new CultCacheEnvelope
        {
            Key = key,
            Type = type,
            Payload = SerializePayload(entry),
            StoredAt = storedAt
        };
    }

    private static byte[] SerializeGeneric<T>(T entry) where T : DatabaseEntry
    {
        return DatabaseEntrySerialization.Serialize(entry);
    }
}
