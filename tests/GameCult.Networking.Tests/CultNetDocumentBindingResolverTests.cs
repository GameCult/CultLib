using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using MessagePack;
using MessagePack.Formatters;
using NUnit.Framework;

[assembly: CultCacheFormatterResolver(typeof(GameCult.Networking.Tests.BindingPairResolver))]

namespace GameCult.Networking.Tests
{
    public class CultNetDocumentBindingResolverTests
    {
        [Test]
        public void TypedBindingUsesTheDocumentAssemblyResolver()
        {
            var registry = CultDocumentRegistry.ForTypes(new[] { typeof(BindingPairDocument) });
            var binding = CultNetDocumentBinding.ForDocument<BindingPairDocument>(registry);

            var payload = binding.PayloadSerializer(new BindingPairDocument { Name = "p", Value = new BindingPair { A = 1.5f, B = -2f } });
            var decoded = (BindingPairDocument)binding.PayloadDeserializer(payload);

            Assert.That(decoded.Name, Is.EqualTo("p"));
            Assert.That(decoded.Value.A, Is.EqualTo(1.5f));
            Assert.That(decoded.Value.B, Is.EqualTo(-2f));
        }
    }

    public struct BindingPair
    {
        public float A;
        public float B;
    }

    [CultDocument("tests.binding_pair", "tests.binding_pair.v1")]
    [MessagePackObject]
    public sealed class BindingPairDocument
    {
        [Key(0)] [CultName] public string Name = string.Empty;
        [Key(1)] public BindingPair Value;
    }

    public sealed class BindingPairResolver : IFormatterResolver
    {
        public static readonly IFormatterResolver Instance = new BindingPairResolver();

        public IMessagePackFormatter<T>? GetFormatter<T>() =>
            typeof(T) == typeof(BindingPair) ? (IMessagePackFormatter<T>)(object)PairFormatter.Instance : null;

        private sealed class PairFormatter : IMessagePackFormatter<BindingPair>
        {
            public static readonly PairFormatter Instance = new();

            public void Serialize(ref MessagePackWriter writer, BindingPair value, MessagePackSerializerOptions options)
            {
                writer.WriteArrayHeader(2);
                writer.Write(value.A);
                writer.Write(value.B);
            }

            public BindingPair Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            {
                reader.ReadArrayHeader();
                return new BindingPair { A = reader.ReadSingle(), B = reader.ReadSingle() };
            }
        }
    }
}
