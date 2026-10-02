using System;
using System.IO;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameCult.Caching;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace GameCult.Caching.MessagePack;

public sealed class CultDocumentResolver : IFormatterResolver
{
    public static readonly CultDocumentResolver Instance = new();
    private CultDocumentResolver() { }

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

public static class CultDocumentMessagePackSerialization
{
    private const int PersistedRecordFieldCount = 4;
    private const int VariantRecordFieldCount = 5;
    private const int SchemaCatalogEntryFieldCount = 7;
    private const int SchemaCatalogMemberFieldCount = 8;
    private const int StoreSnapshotFieldCount = 3;

    public static readonly MessagePackSerializerOptions Options = Compose(Array.Empty<IFormatterResolver>());

    private static readonly ConcurrentDictionary<Assembly, MessagePackSerializerOptions> AssemblyOptions = new();

    public static MessagePackSerializerOptions OptionsFor(Assembly documentAssembly)
    {
        if (documentAssembly == null) throw new ArgumentNullException(nameof(documentAssembly));
        return AssemblyOptions.GetOrAdd(documentAssembly, static assembly =>
        {
            var resolvers = assembly.GetCustomAttributes<CultCacheFormatterResolverAttribute>()
                .Select(attribute => CreateResolver(attribute.ResolverType))
                .ToArray();
            return resolvers.Length == 0 ? Options : Compose(resolvers);
        });
    }

    private static MessagePackSerializerOptions Compose(IFormatterResolver[] consumerResolvers)
    {
        return MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                consumerResolvers.Append(CultDocumentResolver.Instance).Append(CultMathResolver.Instance).Append(StandardResolver.Instance).ToArray()))
            .WithSecurity(CultMessagePackSecurity.Instance);
    }

    private static IFormatterResolver CreateResolver(Type resolverType)
    {
        var instance = resolverType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                       ?? resolverType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                       ?? (resolverType.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(resolverType) : null);
        return instance as IFormatterResolver
               ?? throw new InvalidOperationException(
                   $"Formatter resolver {resolverType.FullName} needs a public static Instance or a public parameterless constructor and must implement {nameof(IFormatterResolver)}.");
    }

    public static byte[] SerializeUntyped(object value, Type type)
    {
        return SerializeUntyped(value, type, CultDocumentRegistry.Shared);
    }

    public static byte[] SerializeUntyped(object value, Type type, CultDocumentRegistry registry)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        if (value != null) registry.GetRequired(type);
        return MessagePackSerializer.Serialize(type, value, OptionsFor(type.Assembly));
    }

    public static object DeserializeUntyped(Type type, byte[] payload)
    {
        return DeserializeUntyped(type, payload, CultDocumentRegistry.Shared);
    }

    public static object DeserializeUntyped(Type type, byte[] payload, CultDocumentRegistry registry)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));
        registry.GetRequired(type);
        return MessagePackSerializer.Deserialize(type, payload, OptionsFor(type.Assembly))
            ?? throw new InvalidOperationException($"MessagePack returned null for Cult document type {type.FullName}.");
    }

    /// <summary>
    /// Replaces slots of a serialized document payload (a MessagePack array indexed by slot) with already-encoded member
    /// values, leaving every other slot byte for byte as it was. A slot past the end extends the array with nil.
    /// </summary>
    public static byte[] OverlaySlots(byte[] payload, IReadOnlyList<KeyValuePair<int, byte[]>> slots)
    {
        var reader = new MessagePackReader(payload);
        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            throw new InvalidOperationException("A document payload is a MessagePack array; this one is not, so its slots cannot be overridden.");
        }

        var count = reader.ReadArrayHeader();
        var elements = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var start = reader.Position;
            reader.Skip();
            elements.Add(reader.Sequence.Slice(start, reader.Position).ToArray());
        }

        foreach (var (slot, value) in slots)
        {
            while (elements.Count <= slot)
            {
                elements.Add(new byte[] { 0xc0 });
            }

            elements[slot] = value;
        }

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(elements.Count);
        foreach (var element in elements)
        {
            writer.WriteRaw(element);
        }

        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] SerializePersistedRecord(CultPersistedRecord record)
    {
        var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        WritePersistedRecord(ref writer, record);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static CultPersistedRecord DeserializePersistedRecord(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        return ReadPersistedRecord(ref reader);
    }

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
    /// Reads a store file: exactly one complete MessagePack array of three slots (header, schema catalog, records), or an empty
    /// array, which is an empty store. A truncated file, bytes after the array, a value that is not an array, a first slot that
    /// is not a header string, a store with fewer or more slots, a record whose schema the catalog does not publish, or a body
    /// that does not decode are refused with
    /// <see cref="CultStoreUnreadableException"/>, so a reader never takes part of a file for the whole and never skips a slot
    /// it does not understand. The rewriting paths use this same reader as their verdict on whether a file may be replaced.
    /// </summary>
    public static CultPersistedStoreSnapshot DeserializeSnapshot(byte[] payload) => ReadStore(payload, ReadSchemaCatalogEntry);

    internal delegate CultSchemaCatalogEntry CatalogEntryReader(ref MessagePackReader reader);

    // The one store reader. A caller with an older catalog layout supplies its own entry reader; the framing, the slot count
    // and the records are judged here, once.
    internal static CultPersistedStoreSnapshot ReadStore(byte[] payload, CatalogEntryReader readCatalogEntry)
    {
        try
        {
            RequireOneArray(payload);
            var reader = new MessagePackReader(payload);
            var fieldCount = reader.ReadArrayHeader();
            if (fieldCount != 0 && fieldCount != StoreSnapshotFieldCount)
            {
                throw new CultStoreUnreadableException(
                    $"The store has {fieldCount} top-level slots; this runtime reads {StoreSnapshotFieldCount} (header, schema catalog, records) or none.");
            }

            var snapshot = new CultPersistedStoreSnapshot();
            if (fieldCount == 0)
                return snapshot;

            snapshot.FormatVersion = reader.ReadString()
                ?? throw new CultStoreUnreadableException("Store snapshot declares no format version; this runtime reads " + CultPersistedStoreSnapshot.FormatV1 + ", " + CultPersistedStoreSnapshot.FormatV2 + " and " + CultPersistedStoreSnapshot.FormatV3 + ".");

            var catalogCount = reader.ReadArrayHeader();
            snapshot.SchemaCatalog = new CultSchemaCatalogEntry[catalogCount];
            for (var index = 0; index < catalogCount; index++)
            {
                snapshot.SchemaCatalog[index] = readCatalogEntry(ref reader);
            }

            var recordCount = reader.ReadArrayHeader();
            snapshot.Records = new CultPersistedRecord[recordCount];
            for (var index = 0; index < recordCount; index++)
            {
                snapshot.Records[index] = ReadPersistedRecord(ref reader);
            }

            RequirePublishedSchemas(snapshot);
            return snapshot;
        }
        catch (CultStoreUnreadableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new CultStoreUnreadableException(ex.Message, innerException: ex);
        }
    }

    // A record whose schema the store's catalog does not publish cannot be read by any runtime that has only the file.
    private static void RequirePublishedSchemas(CultPersistedStoreSnapshot snapshot)
    {
        var published = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.SchemaCatalog)
        {
            published.Add(entry.SchemaId);
            foreach (var compatible in entry.CompatibleSchemaIds)
                published.Add(compatible);
        }

        var unpublished = snapshot.Records.FirstOrDefault(record => !published.Contains(record.SchemaId));
        if (unpublished != null)
        {
            throw new CultStoreUnreadableException(
                $"Record '{unpublished.Key}' names schema '{unpublished.SchemaId}', which the store's schema catalog does not publish.");
        }
    }

    private static void RequireOneArray(byte[] bytes)
    {
        var whole = new MessagePackReader(bytes);
        try
        {
            if (whole.NextMessagePackType != MessagePackType.Array)
                throw new CultStoreUnreadableException($"The store is a MessagePack {whole.NextMessagePackType}, not an array.");
            whole.Skip();
        }
        catch (Exception ex) when (ex is EndOfStreamException or MessagePackSerializationException or InvalidOperationException)
        {
            throw new CultStoreUnreadableException("The store is not one complete MessagePack array.", innerException: ex);
        }

        if (!whole.End)
            throw new CultStoreUnreadableException("The store has bytes after its MessagePack array.");
    }

    /// <summary>Refuses a single-file snapshot this runtime cannot read, naming the format or record it found.</summary>
    public static void RequireSingleFileFormat(CultPersistedStoreSnapshot snapshot)
    {
        var version = snapshot.FormatVersion;
        if (!string.Equals(version, CultPersistedStoreSnapshot.FormatV1, StringComparison.Ordinal) &&
            !string.Equals(version, CultPersistedStoreSnapshot.FormatV2, StringComparison.Ordinal) &&
            !string.Equals(version, CultPersistedStoreSnapshot.FormatV3, StringComparison.Ordinal))
        {
            throw new CultStoreUnreadableException(
                $"Store format {DescribeHeader(version)} is not readable; this runtime reads {CultPersistedStoreSnapshot.FormatV1}, {CultPersistedStoreSnapshot.FormatV2} and {CultPersistedStoreSnapshot.FormatV3}.");
        }

        // Only a v2 or v3 store may hold a variant: a v1 header over a variant record is a store no reader could trust.
        if (string.Equals(version, CultPersistedStoreSnapshot.FormatV1, StringComparison.Ordinal))
        {
            var variant = snapshot.Records.FirstOrDefault(record => record.Variant != null);
            if (variant != null)
            {
                throw new CultStoreUnreadableException(
                    $"Record '{variant.Key}' (schema '{variant.SchemaId}') is a variant but the store declares {version}; variants need {CultPersistedStoreSnapshot.FormatV2} or {CultPersistedStoreSnapshot.FormatV3}.");
            }
        }
    }

    // A store header as a refusal may show it: echoed in the shape cultcache.store.v<digits>, and otherwise described
    // only by its length, since the bytes are the store's.
    private static string DescribeHeader(string header)
    {
        const string prefix = "cultcache.store.v";
        var version = header.StartsWith(prefix, StringComparison.Ordinal) ? header.Substring(prefix.Length) : "";
        return version.Length > 0 && version.All(character => character >= '0' && character <= '9')
            ? header
            : $"an unrecognised cultcache.store.* header of {System.Text.Encoding.UTF8.GetByteCount(header)} bytes";
    }

    private static void WritePersistedRecord(ref MessagePackWriter writer, CultPersistedRecord record)
    {
        writer.WriteArrayHeader(record.Variant == null ? PersistedRecordFieldCount : VariantRecordFieldCount);
        writer.Write(record.Key);
        writer.Write(record.SchemaId);
        writer.Write(record.StoredAt);
        writer.Write(record.Payload);
        if (record.Variant != null)
        {
            // Slot 4: [baseKey, overrides[]]; an override is [op, path[], id, value], a path step [slot, elementId],
            // and the value is the member's own MessagePack encoding, inline.
            writer.WriteArrayHeader(2);
            writer.Write(record.Variant.BaseKey);
            writer.WriteArrayHeader(record.Variant.Overrides.Count);
            foreach (var entry in record.Variant.Overrides)
            {
                writer.WriteArrayHeader(4);
                writer.Write((int)entry.Op);
                writer.WriteArrayHeader(entry.Path.Count);
                foreach (var step in entry.Path)
                {
                    writer.WriteArrayHeader(2);
                    writer.Write(step.Slot);
                    writer.Write(step.ElementId);
                }

                writer.Write(entry.Id);
                writer.WriteRaw(entry.Value);
            }
        }
    }

    private static CultVariantDelta ReadVariant(ref MessagePackReader reader, string key, string schemaId)
    {
        var slots = reader.ReadArrayHeader();
        if (slots != 2)
        {
            throw new NotSupportedException(
                $"Record '{key}' (schema '{schemaId}') has a variant slot of {slots} entries; this runtime reads 2 (base key, overrides).");
        }

        var baseKey = reader.ReadString() ?? string.Empty;
        var count = reader.ReadArrayHeader();
        var overrides = new CultVariantOverride[count];
        for (var index = 0; index < count; index++)
        {
            var fields = reader.ReadArrayHeader();
            if (fields != 4)
            {
                throw new NotSupportedException(
                    $"Record '{key}' (schema '{schemaId}') has an override of {fields} entries; this runtime reads 4 (op, path, id, value).");
            }

            var op = reader.ReadInt32();
            if (op is < 0 or > 2)
            {
                throw new NotSupportedException($"Record '{key}' (schema '{schemaId}') has an override with unknown op {op}.");
            }

            var stepCount = reader.ReadArrayHeader();
            var path = new CultPathStep[stepCount];
            for (var step = 0; step < stepCount; step++)
            {
                var stepFields = reader.ReadArrayHeader();
                if (stepFields != 2)
                {
                    throw new NotSupportedException(
                        $"Record '{key}' (schema '{schemaId}') has a path step of {stepFields} entries; this runtime reads 2 (slot, elementId).");
                }

                path[step] = new CultPathStep(reader.ReadInt32(), reader.ReadString() ?? string.Empty);
            }

            var id = reader.ReadString() ?? string.Empty;
            var start = reader.Position;
            reader.Skip();
            var value = reader.Sequence.Slice(start, reader.Position).ToArray();
            overrides[index] = new CultVariantOverride((CultOverrideOp)op, path, id, value);
        }

        return new CultVariantDelta(baseKey, overrides);
    }

    public static CultPersistedRecord ReadPersistedRecord(ref MessagePackReader reader)
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

        if (fieldCount > VariantRecordFieldCount)
        {
            throw new NotSupportedException(
                $"Record '{record.Key}' (schema '{record.SchemaId}') has {fieldCount} slots; this runtime reads {PersistedRecordFieldCount} " +
                $"(or {VariantRecordFieldCount} for a variant).");
        }

        if (fieldCount == VariantRecordFieldCount)
        {
            record.Variant = ReadVariant(ref reader, record.Key, record.SchemaId);
            if (record.Payload.Length != 0)
            {
                throw new NotSupportedException(
                    $"Record '{record.Key}' (schema '{record.SchemaId}') is a variant and must carry an empty payload.");
            }
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

public class SingleFileMessagePackBackingStore : SingleFileBackingStore
{
    public SingleFileMessagePackBackingStore(string filePath, bool readOnly = false) : base(filePath, readOnly)
    {
    }

    protected override byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        return CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
    }

    protected override CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data)
    {
        var snapshot = CultDocumentMessagePackSerialization.DeserializeSnapshot(data);
        CultDocumentMessagePackSerialization.RequireSingleFileFormat(snapshot);
        return snapshot;
    }

    protected override byte[] SerializePayload(object document)
    {
        return CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType(), Registry);
    }

    protected override object DeserializePayload(Type documentType, byte[] payload)
    {
        return CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload, Registry);
    }
}
