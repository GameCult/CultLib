using System;
using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching.MessagePack
{
    public sealed class DatabaseLinkFormatter<T> : IMessagePackFormatter<DatabaseLink<T>> where T : DatabaseEntry
    {
        public void Serialize(ref MessagePackWriter writer, DatabaseLink<T>? value, MessagePackSerializerOptions options)
        {
            if (value == null) { writer.WriteNil(); return; }
            writer.WriteArrayHeader(1);
            var fmt = options.Resolver.GetFormatterWithVerify<Guid>();
            fmt.Serialize(ref writer, value.LinkID, options);
        }

        public DatabaseLink<T>? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            var c = reader.ReadArrayHeader();
            if (c != 1) throw new MessagePackSerializationException($"Invalid DatabaseLink<{typeof(T).Name}> array length {c}, expected 1");
            var id = options.Resolver.GetFormatterWithVerify<Guid>().Deserialize(ref reader, options);
            var link = new DatabaseLink<T> { LinkID = id };
            return link;
        }
    }
}
