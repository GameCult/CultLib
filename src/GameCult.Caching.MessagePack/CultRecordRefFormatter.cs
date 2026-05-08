using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching;

/// <summary>
/// Serializes typed CultCache record references as their persisted key strings.
/// </summary>
public sealed class CultRecordRefFormatter<T> : IMessagePackFormatter<CultRecordRef<T>>
{
    /// <summary>
    /// Writes a typed record reference to MessagePack.
    /// </summary>
    public void Serialize(ref MessagePackWriter writer, CultRecordRef<T> value, MessagePackSerializerOptions options)
    {
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Key.Value, options);
    }

    /// <summary>
    /// Reads a typed record reference from MessagePack.
    /// </summary>
    public CultRecordRef<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var key = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options) ?? string.Empty;
        return new CultRecordRef<T>(new CultRecordKey(key));
    }
}
