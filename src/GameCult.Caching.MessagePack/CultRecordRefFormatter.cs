using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching;

// An unset reference (the empty key) is "" on the wire in every position, map keys included (other runtimes refuse nil
// map keys), and default(CultRecordRef<T>) in memory. Nil, written by 1.0.58 stores, reads as unset and saves as "".
public sealed class CultRecordRefFormatter<T> : IMessagePackFormatter<CultRecordRef<T>>
{
    public void Serialize(ref MessagePackWriter writer, CultRecordRef<T> value, MessagePackSerializerOptions options) =>
        writer.Write(value.Key.Value);

    public CultRecordRef<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var key = reader.TryReadNil() ? null : reader.ReadString();
        return string.IsNullOrEmpty(key) ? default : new CultRecordRef<T>(new CultRecordKey(key!));
    }
}
