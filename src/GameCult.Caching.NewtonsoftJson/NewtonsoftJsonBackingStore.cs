using System;
using GameCult.Caching.MessagePack;
using Newtonsoft.Json;

namespace GameCult.Caching.NewtonsoftJson;

public class SingleFileNewtonsoftJsonBackingStore : SingleFileBackingStore
{
    public SingleFileNewtonsoftJsonBackingStore(string filePath) : base(filePath)
    {
    }

    protected override byte[] SerializeSnapshot(CultPersistedStoreSnapshot snapshot)
    {
        return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(snapshot, Formatting.Indented));
    }

    protected override CultPersistedStoreSnapshot DeserializeSnapshot(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonConvert.DeserializeObject<CultPersistedStoreSnapshot>(json)
               ?? new CultPersistedStoreSnapshot();
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

public class MultiFileNewtonsoftJsonBackingStore : MultiFileBackingStore
{
    public MultiFileNewtonsoftJsonBackingStore(string path) : base(path)
    {
    }

    protected override byte[] SerializeRecord(CultPersistedRecord record)
    {
        return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(record, Formatting.Indented));
    }

    protected override CultPersistedRecord DeserializeRecord(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonConvert.DeserializeObject<CultPersistedRecord>(json)
               ?? new CultPersistedRecord();
    }

    protected override byte[] SerializeCatalog(CultSchemaCatalogEntry[] catalog)
    {
        return System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(catalog, Formatting.Indented));
    }

    protected override CultSchemaCatalogEntry[] DeserializeCatalog(byte[] data)
    {
        var json = System.Text.Encoding.UTF8.GetString(data);
        return JsonConvert.DeserializeObject<CultSchemaCatalogEntry[]>(json)
               ?? Array.Empty<CultSchemaCatalogEntry>();
    }

    protected override byte[] SerializePayload(object document)
    {
        return CultDocumentMessagePackSerialization.SerializeUntyped(document, document.GetType());
    }

    protected override object DeserializePayload(Type documentType, byte[] payload)
    {
        return CultDocumentMessagePackSerialization.DeserializeUntyped(documentType, payload);
    }

    public override string Extension => "json";
}
