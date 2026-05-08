using System;
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
    public static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                CultDocumentResolver.Instance,
                StandardResolver.Instance))
            .WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Serialize<T>(T value)
    {
        return MessagePackSerializer.Serialize(value, Options);
    }

    public static T Deserialize<T>(byte[] payload)
    {
        return MessagePackSerializer.Deserialize<T>(payload, Options);
    }

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

    public static object DeserializeUntyped(Type type, byte[] payload)
    {
        var descriptor = CultDocumentRegistry.Shared.GetRequired(type);
        if (descriptor.GeneratedPayloadDeserializer != null)
        {
            return descriptor.GeneratedPayloadDeserializer(payload);
        }

        return MessagePackSerializer.Deserialize(type, payload, Options);
    }
}

public class SingleFileMessagePackBackingStore : SingleFileBackingStore
{
    public SingleFileMessagePackBackingStore(string filePath) : base(filePath)
    {
    }

    protected override byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        return CultDocumentMessagePackSerialization.Serialize(snapshot);
    }

    protected override CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data)
    {
        return CultDocumentMessagePackSerialization.Deserialize<CultPersistedStoreSnapshot>(data);
    }

    protected override byte[] SerializePayload(object document)
    {
        return CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
    }

    protected override object DeserializePayload(Type documentType, byte[] payload)
    {
        return CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload);
    }
}

public class MultiFileMessagePackBackingStore : MultiFileBackingStore
{
    public MultiFileMessagePackBackingStore(string path) : base(path)
    {
    }

    protected override byte[] SerializeRecord(CultPersistedRecord record)
    {
        return CultDocumentMessagePackSerialization.Serialize(record);
    }

    protected override CultPersistedRecord DeserializeRecord(byte[] data)
    {
        return CultDocumentMessagePackSerialization.Deserialize<CultPersistedRecord>(data);
    }

    protected override byte[] SerializeCatalog(CultSchemaCatalogEntry[] catalog)
    {
        return CultDocumentMessagePackSerialization.Serialize(catalog);
    }

    protected override CultSchemaCatalogEntry[] DeserializeCatalog(byte[] data)
    {
        return CultDocumentMessagePackSerialization.Deserialize<CultSchemaCatalogEntry[]>(data);
    }

    protected override byte[] SerializePayload(object document)
    {
        return CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
    }

    protected override object DeserializePayload(Type documentType, byte[] payload)
    {
        return CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload);
    }

    public override string Extension => "msgpack";
}
