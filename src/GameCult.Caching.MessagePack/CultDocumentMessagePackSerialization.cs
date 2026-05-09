using System;
using System.Buffers;
using GameCult.Caching;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// MessagePack resolver for CultCache-specific value types.
/// </summary>
public sealed class CultDocumentResolver : IFormatterResolver
{
    /// <summary>
    /// Gets the shared resolver instance.
    /// </summary>
    public static readonly CultDocumentResolver Instance = new();
    private CultDocumentResolver() { }

    /// <summary>
    /// Gets a formatter for the requested type, when this resolver owns it.
    /// </summary>
    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        var type = typeof(T);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CultRecordRef<>))
        {
            var formatterType = typeof(global::GameCult.Caching.CultRecordRefFormatter<>).MakeGenericType(type.GetGenericArguments()[0]);
            return (IMessagePackFormatter<T>)Activator.CreateInstance(formatterType)!;
        }

        return null;
    }
}

/// <summary>
/// MessagePack serialization helpers for CultCache documents and backing stores.
/// </summary>
public static class CultDocumentMessagePackSerialization
{
    private const int PersistedRecordFieldCount = 4;
    private const int SchemaCatalogEntryFieldCount = 7;
    private const int SchemaCatalogMemberFieldCount = 8;
    private const int StoreSnapshotFieldCount = 3;

    /// <summary>
    /// Gets the shared MessagePack serializer options for CultCache payloads.
    /// </summary>
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                CultDocumentResolver.Instance,
                StandardResolver.Instance))
            .WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>
    /// Serializes a typed value with the CultCache MessagePack options.
    /// </summary>
    public static byte[] Serialize<T>(T value)
    {
        return MessagePackSerializer.Serialize(value, Options);
    }

    /// <summary>
    /// Deserializes a typed value with the CultCache MessagePack options.
    /// </summary>
    public static T Deserialize<T>(byte[] payload)
    {
        return MessagePackSerializer.Deserialize<T>(payload, Options);
    }

    /// <summary>
    /// Serializes a value whose document type is known at runtime.
    /// </summary>
    public static byte[] SerializeUntyped(object value, Type type)
    {
        if (value != null)
        {
            var descriptor = CultDocumentRegistry.Shared.GetRequired(type);
            if (descriptor.GeneratedPayloadSerializer != null)
            {
                return descriptor.GeneratedPayloadSerializer(value);
            }
        }

        return MessagePackSerializer.Serialize(type, value, Options);
    }

    /// <summary>
    /// Deserializes a value whose document type is known at runtime.
    /// </summary>
    public static object DeserializeUntyped(Type type, byte[] payload)
    {
        var descriptor = CultDocumentRegistry.Shared.GetRequired(type);
        if (descriptor.GeneratedPayloadDeserializer != null)
        {
            return descriptor.GeneratedPayloadDeserializer(payload);
        }

        return MessagePackSerializer.Deserialize(type, payload, Options)
            ?? throw new InvalidOperationException($"MessagePack returned null for Cult document type {type.FullName}.");
    }

    /// <summary>
    /// Serializes one persisted store record.
    /// </summary>
    public static byte[] SerializePersistedRecord(CultPersistedRecord record)
    {
        var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        WritePersistedRecord(ref writer, record);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserializes one persisted store record.
    /// </summary>
    public static CultPersistedRecord DeserializePersistedRecord(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        return ReadPersistedRecord(ref reader);
    }

    /// <summary>
    /// Serializes a schema catalog.
    /// </summary>
    public static byte[] SerializeSchemaCatalog(CultSchemaCatalogEntry[] catalog)
    {
        var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(catalog.Length);
        foreach (var entry in catalog)
        {
            WriteSchemaCatalogEntry(ref writer, entry);
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserializes a schema catalog.
    /// </summary>
    public static CultSchemaCatalogEntry[] DeserializeSchemaCatalog(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        var count = reader.ReadArrayHeader();
        var catalog = new CultSchemaCatalogEntry[count];
        for (var index = 0; index < count; index++)
        {
            catalog[index] = ReadSchemaCatalogEntry(ref reader);
        }

        return catalog;
    }

    /// <summary>
    /// Serializes a complete persisted store snapshot.
    /// </summary>
    public static byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(StoreSnapshotFieldCount);
        writer.Write(snapshot.FormatVersion);
        writer.WriteArrayHeader(snapshot.SchemaCatalog.Length);
        foreach (var entry in snapshot.SchemaCatalog)
        {
            WriteSchemaCatalogEntry(ref writer, entry);
        }

        writer.WriteArrayHeader(snapshot.Records.Length);
        foreach (var record in snapshot.Records)
        {
            WritePersistedRecord(ref writer, record);
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserializes a complete persisted store snapshot.
    /// </summary>
    public static CultPersistedStoreSnapshot DeserializeSnapshot(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        var fieldCount = reader.ReadArrayHeader();
        var snapshot = new CultPersistedStoreSnapshot();

        if (fieldCount > 0)
        {
            snapshot.FormatVersion = reader.ReadString() ?? "cultcache.store.v1";
        }

        if (fieldCount > 1)
        {
            var catalogCount = reader.ReadArrayHeader();
            snapshot.SchemaCatalog = new CultSchemaCatalogEntry[catalogCount];
            for (var index = 0; index < catalogCount; index++)
            {
                snapshot.SchemaCatalog[index] = ReadSchemaCatalogEntry(ref reader);
            }
        }

        if (fieldCount > 2)
        {
            var recordCount = reader.ReadArrayHeader();
            snapshot.Records = new CultPersistedRecord[recordCount];
            for (var index = 0; index < recordCount; index++)
            {
                snapshot.Records[index] = ReadPersistedRecord(ref reader);
            }
        }

        for (var index = StoreSnapshotFieldCount; index < fieldCount; index++)
        {
            reader.Skip();
        }

        return snapshot;
    }

    private static void WritePersistedRecord(ref MessagePackWriter writer, CultPersistedRecord record)
    {
        writer.WriteArrayHeader(PersistedRecordFieldCount);
        writer.Write(record.Key);
        writer.Write(record.SchemaId);
        writer.Write(record.StoredAt);
        writer.Write(record.Payload);
    }

    private static CultPersistedRecord ReadPersistedRecord(ref MessagePackReader reader)
    {
        var fieldCount = reader.ReadArrayHeader();
        var record = new CultPersistedRecord();

        if (fieldCount > 0)
        {
            record.Key = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 1)
        {
            record.SchemaId = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 2)
        {
            record.StoredAt = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 3)
        {
            record.Payload = reader.ReadBytes()?.ToArray() ?? Array.Empty<byte>();
        }

        for (var index = PersistedRecordFieldCount; index < fieldCount; index++)
        {
            reader.Skip();
        }

        return record;
    }

    private static void WriteSchemaCatalogEntry(ref MessagePackWriter writer, CultSchemaCatalogEntry entry)
    {
        writer.WriteArrayHeader(SchemaCatalogEntryFieldCount);
        writer.Write(entry.SchemaId);
        writer.Write(entry.SchemaName);
        writer.Write(entry.SchemaVersion);
        writer.Write(entry.ContentHash);
        writer.Write(entry.CanonicalSchemaJson);
        writer.WriteArrayHeader(entry.CompatibleSchemaIds.Length);
        foreach (var schemaId in entry.CompatibleSchemaIds)
        {
            writer.Write(schemaId);
        }

        writer.WriteArrayHeader(entry.Members.Length);
        foreach (var member in entry.Members)
        {
            WriteSchemaCatalogMember(ref writer, member);
        }
    }

    private static CultSchemaCatalogEntry ReadSchemaCatalogEntry(ref MessagePackReader reader)
    {
        var fieldCount = reader.ReadArrayHeader();
        var entry = new CultSchemaCatalogEntry();

        if (fieldCount > 0)
        {
            entry.SchemaId = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 1)
        {
            entry.SchemaName = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 2)
        {
            entry.SchemaVersion = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 3)
        {
            entry.ContentHash = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 4)
        {
            entry.CanonicalSchemaJson = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 5)
        {
            var compatibleCount = reader.ReadArrayHeader();
            entry.CompatibleSchemaIds = new string[compatibleCount];
            for (var index = 0; index < compatibleCount; index++)
            {
                entry.CompatibleSchemaIds[index] = reader.ReadString() ?? string.Empty;
            }
        }

        if (fieldCount > 6)
        {
            var memberCount = reader.ReadArrayHeader();
            entry.Members = new CultSchemaMemberCatalogEntry[memberCount];
            for (var index = 0; index < memberCount; index++)
            {
                entry.Members[index] = ReadSchemaCatalogMember(ref reader);
            }
        }

        for (var index = SchemaCatalogEntryFieldCount; index < fieldCount; index++)
        {
            reader.Skip();
        }

        return entry;
    }

    private static void WriteSchemaCatalogMember(ref MessagePackWriter writer, CultSchemaMemberCatalogEntry member)
    {
        writer.WriteArrayHeader(SchemaCatalogMemberFieldCount);
        writer.Write(member.Slot);
        writer.Write(member.MemberName);
        writer.Write(member.TypeName);
        writer.Write(member.IsReference);
        writer.Write(member.IsMany);
        writer.Write(member.TargetSchemaName);
        writer.Write(member.IsName);
        writer.Write(member.IndexAlias);
    }

    private static CultSchemaMemberCatalogEntry ReadSchemaCatalogMember(ref MessagePackReader reader)
    {
        var fieldCount = reader.ReadArrayHeader();
        var member = new CultSchemaMemberCatalogEntry();

        if (fieldCount > 0)
        {
            member.Slot = reader.ReadInt32();
        }

        if (fieldCount > 1)
        {
            member.MemberName = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 2)
        {
            member.TypeName = reader.ReadString() ?? string.Empty;
        }

        if (fieldCount > 3)
        {
            member.IsReference = reader.ReadBoolean();
        }

        if (fieldCount > 4)
        {
            member.IsMany = reader.ReadBoolean();
        }

        if (fieldCount > 5)
        {
            member.TargetSchemaName = reader.ReadString();
        }

        if (fieldCount > 6)
        {
            member.IsName = reader.ReadBoolean();
        }

        if (fieldCount > 7)
        {
            member.IndexAlias = reader.ReadString();
        }

        for (var index = SchemaCatalogMemberFieldCount; index < fieldCount; index++)
        {
            reader.Skip();
        }

        return member;
    }
}

/// <summary>
/// Single-file CultCache backing store that persists snapshots as MessagePack.
/// </summary>
public class SingleFileMessagePackBackingStore : SingleFileBackingStore
{
    /// <summary>
    /// Creates a MessagePack single-file backing store.
    /// </summary>
    public SingleFileMessagePackBackingStore(string filePath) : base(filePath)
    {
    }

    /// <summary>
    /// Serializes a store snapshot.
    /// </summary>
    protected override byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        return CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
    }

    /// <summary>
    /// Deserializes a store snapshot.
    /// </summary>
    protected override CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data)
    {
        return CultDocumentMessagePackSerialization.DeserializeSnapshot(data);
    }

    /// <summary>
    /// Serializes one document payload.
    /// </summary>
    protected override byte[] SerializePayload(object document)
    {
        return CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
    }

    /// <summary>
    /// Deserializes one document payload.
    /// </summary>
    protected override object DeserializePayload(Type documentType, byte[] payload)
    {
        return CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload);
    }
}
