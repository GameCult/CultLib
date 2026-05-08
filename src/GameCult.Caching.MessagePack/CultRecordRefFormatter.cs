using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching;

public sealed class CultRecordRefFormatter<T> : IMessagePackFormatter<CultRecordRef<T>>
{
    public void Serialize(ref MessagePackWriter writer, CultRecordRef<T> value, MessagePackSerializerOptions options)
    {
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Key.Value, options);
    }

    public CultRecordRef<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var key = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options) ?? string.Empty;
        return new CultRecordRef<T>(new CultRecordKey(key));
    }
}
