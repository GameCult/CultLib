using GameCult.Caching.MessagePack;
using GameCult.Caching.Tests;
using MessagePack;
using MessagePack.Formatters;

[assembly: CultCacheFormatterResolver(typeof(PairResolver))]

namespace GameCult.Caching.Tests
{
    public struct Pair
    {
        public float A;
        public float B;
    }

    public sealed class PairResolver : IFormatterResolver
    {
        public static readonly IFormatterResolver Instance = new PairResolver();

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            return typeof(T) == typeof(Pair) ? (IMessagePackFormatter<T>)(object)PairFormatter.Instance : null;
        }

        private sealed class PairFormatter : IMessagePackFormatter<Pair>
        {
            public static readonly PairFormatter Instance = new();

            public void Serialize(ref MessagePackWriter writer, Pair value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(2);
                writer.Write(value.A);
                writer.Write(value.B);
            }

            public Pair Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                if (reader.ReadArrayHeader() != 2) throw new MessagePackSerializationException("Pair is a two-element array.");
                return new Pair { A = reader.ReadSingle(), B = reader.ReadSingle() };
            }
        }
    }
}
