using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching;

// An unset reference (the empty key) is nil on the wire and default(CultRecordRef<T>) in memory; a "" read from older
// stores is the same unset reference, so load then save is byte-stable.
public sealed class CultRecordRefFormatter<T> : IMessagePackFormatter<CultRecordRef<T>>
{
    public void Serialize(ref MessagePackWriter writer, CultRecordRef<T> value, MessagePackSerializerOptions options)
    {
        if (value.Key.Value.Length == 0) writer.WriteNil();
        else writer.Write(value.Key.Value);
    }

    public CultRecordRef<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var key = reader.TryReadNil() ? null : reader.ReadString();
        return string.IsNullOrEmpty(key) ? default : new CultRecordRef<T>(new CultRecordKey(key!));
    }
}
