#nullable enable
using System;
using System.Buffers;
using GameCult.Caching.MessagePack;
using MessagePack;
using MessagePack.Formatters;
using NUnit.Framework;

[assembly: CultDocumentMessagePackResolvers(typeof(GameCult.Caching.Tests.FirstOwnerResolver), typeof(GameCult.Caching.Tests.SecondOwnerResolver))]

namespace GameCult.Caching.Tests;

public sealed class DocumentAssemblyResolverTests
{
    [Test]
    public void OptionsFor_CachesOptionsByDocumentAssembly()
    {
        var first = CultDocumentMessagePackSerialization.OptionsFor(typeof(ResolverDocument));
        var second = CultDocumentMessagePackSerialization.OptionsFor(typeof(ResolverDocument).Assembly);

        Assert.That(second, Is.SameAs(first));
        Assert.That(first, Is.Not.SameAs(CultDocumentMessagePackSerialization.Options));
    }

    [Test]
    public void GeneratedDocumentSerialization_UsesOwnerResolversInDeclaredOrder()
    {
        FirstOwnerResolver.SerializeCount = 0;
        SecondOwnerResolver.FormatterRequestCount = 0;
        var original = new ResolverDocument { Name = "probe", Value = new ExternalValue(41) };

        var payload = CultDocumentMessagePackSerialization.SerializeUntyped(original, typeof(ResolverDocument));
        var decoded = (ResolverDocument)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(ResolverDocument), payload);

        Assert.That(decoded.Name, Is.EqualTo("probe"));
        Assert.That(decoded.Value.Number, Is.EqualTo(42));
        Assert.That(FirstOwnerResolver.SerializeCount, Is.EqualTo(1));
        Assert.That(SecondOwnerResolver.FormatterRequestCount, Is.Zero);
    }
}

[CultDocument("tests.assembly_resolver", "tests.assembly_resolver.v1")]
public sealed class ResolverDocument
{
    [Key(0)] public string Name { get; set; } = string.Empty;
    [Key(1)] public ExternalValue Value { get; set; }
}

public readonly struct ExternalValue
{
    public ExternalValue(int number) => Number = number;
    public int Number { get; }
}

public sealed class FirstOwnerResolver : IFormatterResolver
{
    public static int SerializeCount;

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        return typeof(T) == typeof(ExternalValue)
            ? (IMessagePackFormatter<T>)(object)new FirstExternalValueFormatter()
            : null;
    }

    public sealed class FirstExternalValueFormatter : IMessagePackFormatter<ExternalValue>
    {
        public void Serialize(ref MessagePackWriter writer, ExternalValue value, MessagePackSerializerOptions options)
        {
            SerializeCount++;
            writer.Write(value.Number + 1);
        }

        public ExternalValue Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
            => new(reader.ReadInt32());
    }
}

public sealed class SecondOwnerResolver : IFormatterResolver
{
    public static int FormatterRequestCount;

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(ExternalValue)) FormatterRequestCount++;
        return null;
    }
}
