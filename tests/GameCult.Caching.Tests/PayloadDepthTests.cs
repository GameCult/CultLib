#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The payload walk at its own seam: every MessagePack header and scalar width must be skipped by its length, so that a
    // container after it is counted at its true depth. Each case is a two-slot document [probe, chain] whose chain brings
    // the whole payload to exactly the limit (accepted) or one past it (refused); a header or length read wrongly loses the
    // chain and flips one of the two. The probes' filler bytes look like container headers on purpose.
    public class PayloadDepthTests
    {
        private const int Limit = CultMessagePackSecurity.MaximumPayloadDepth;

        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

        private static byte[] Filler(int n) => Enumerable.Range(0, n).Select(i => (byte)(i % 2 == 0 ? 0x91 : 0xdd)).ToArray();

        // `depth` containers nested in one another, the innermost empty; a fixarray header nests by one element.
        private static byte[] Chain(int depth, byte[]? leaf = null) =>
            Cat(Enumerable.Repeat((byte)0x91, depth - 1).ToArray(), leaf ?? Bytes(0x90));

        private static byte[] Doc(byte[] probe, byte[] second) => Cat(Bytes(0x92), probe, second);

        private static byte[] Len16(int n) => Bytes(n >> 8, n & 0xff);

        private static byte[] Len32(int n) => Bytes(n >> 24, (n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff);

        private static readonly (string Name, byte[] Bytes)[] Scalars =
        {
            ("positive fixint", Bytes(0x7f)), ("negative fixint", Bytes(0xe0)), ("max negative fixint", Bytes(0xff)),
            ("nil", Bytes(0xc0)), ("false", Bytes(0xc2)), ("true", Bytes(0xc3)),
            ("uint8", Bytes(0xcc, 0x91)), ("uint16", Bytes(0xcd, 0x91, 0x91)), ("uint32", Bytes(0xce, 0x91, 0x91, 0x91, 0x91)),
            ("uint64", Cat(Bytes(0xcf), Filler(8))), ("int8", Bytes(0xd0, 0x91)), ("int16", Bytes(0xd1, 0x91, 0x91)),
            ("int32", Bytes(0xd2, 0x91, 0x91, 0x91, 0x91)), ("int64", Cat(Bytes(0xd3), Filler(8))),
            ("float32", Cat(Bytes(0xca), Filler(4))), ("float64", Cat(Bytes(0xcb), Filler(8))),
            ("empty fixstr", Bytes(0xa0)), ("fixstr", Cat(Bytes(0xa3), Filler(3))), ("longest fixstr", Cat(Bytes(0xbf), Filler(31))),
            ("str8", Cat(Bytes(0xd9, 5), Filler(5))), ("empty str8", Bytes(0xd9, 0)),
            ("str16", Cat(Bytes(0xda), Len16(256), Filler(256))), ("str32", Cat(Bytes(0xdb), Len32(65536), Filler(65536))),
            ("bin8", Cat(Bytes(0xc4, 3), Filler(3))), ("bin16", Cat(Bytes(0xc5), Len16(256), Filler(256))),
            ("bin32", Cat(Bytes(0xc6), Len32(65536), Filler(65536))),
            ("fixext1", Cat(Bytes(0xd4, 1), Filler(1))), ("fixext2", Cat(Bytes(0xd5, 1), Filler(2))),
            ("fixext4", Cat(Bytes(0xd6, 1), Filler(4))), ("fixext8", Cat(Bytes(0xd7, 1), Filler(8))),
            ("fixext16", Cat(Bytes(0xd8, 1), Filler(16))),
            ("ext8", Cat(Bytes(0xc7, 3, 1), Filler(3))), ("ext16", Cat(Bytes(0xc8), Len16(256), Bytes(1), Filler(256))),
            ("ext32", Cat(Bytes(0xc9), Len32(65536), Bytes(1), Filler(65536))),
        };

        // Containers holding scalar children, so that the child count (a map's is twice its pair count) is what keeps the walk in step.
        private static readonly (string Name, byte[] Bytes)[] Containers =
        {
            ("fixarray", Cat(Bytes(0x9f), Enumerable.Repeat((byte)1, 15).ToArray())),
            ("array16", Cat(Bytes(0xdc), Len16(3), Bytes(1, 1, 1))),
            ("array32", Cat(Bytes(0xdd), Len32(2), Bytes(1, 1))),
            ("fixmap", Cat(Bytes(0x82), Bytes(1, 1, 1, 1))),
            ("largest fixmap", Cat(Bytes(0x8f), Enumerable.Repeat((byte)1, 30).ToArray())),
            ("map16", Cat(Bytes(0xde), Len16(2), Bytes(1, 1, 1, 1))),
            ("map32", Cat(Bytes(0xdf), Len32(1), Bytes(1, 1))),
            ("empty fixarray", Bytes(0x90)), ("empty array16", Bytes(0xdc, 0, 0)), ("empty array32", Bytes(0xdd, 0, 0, 0, 0)),
            ("empty fixmap", Bytes(0x80)), ("empty map16", Bytes(0xde, 0, 0)), ("empty map32", Bytes(0xdf, 0, 0, 0, 0)),
        };

        // A single-child carrier of each container kind, for nesting through every header.
        private static readonly (string Name, Func<byte[], byte[]> Wrap)[] Carriers =
        {
            ("fixarray", inner => Cat(Bytes(0x91), inner)),
            ("array16", inner => Cat(Bytes(0xdc, 0, 1), inner)),
            ("array32", inner => Cat(Bytes(0xdd, 0, 0, 0, 1), inner)),
            ("fixmap", inner => Cat(Bytes(0x81, 1), inner)),
            ("map16", inner => Cat(Bytes(0xde, 0, 1, 1), inner)),
            ("map32", inner => Cat(Bytes(0xdf, 0, 0, 0, 1, 1), inner)),
        };

        private static void Accepts(byte[] payload, string what) =>
            Assert.That(() => CultPayloadDepth.Check(payload, typeof(object), PayloadDirection.Read), Throws.Nothing, what);

        private static void Refuses(byte[] payload, string what) =>
            Assert.That(() => CultPayloadDepth.Check(payload, typeof(object), PayloadDirection.Read),
                Throws.TypeOf<MessagePackSerializationException>().With.Message.Contains($"the limit is {Limit}"), what);

        [Test]
        public void EveryScalarIsSkippedByItsLength()
        {
            foreach (var (name, scalar) in Scalars)
            {
                Accepts(Doc(scalar, Chain(Limit - 1)), $"{name} then a chain of depth {Limit}");
                Refuses(Doc(scalar, Chain(Limit)), $"{name} then a chain of depth {Limit + 1}");
            }
        }

        [Test]
        public void EveryContainerKnowsItsChildCount()
        {
            foreach (var (name, container) in Containers)
            {
                Accepts(Doc(container, Chain(Limit - 1)), $"{name} then a chain of depth {Limit}");
                Refuses(Doc(container, Chain(Limit)), $"{name} then a chain of depth {Limit + 1}");
            }
        }

        [Test]
        public void NestingThroughEveryHeaderIsCountedAndAnEmptyLeafCounts()
        {
            foreach (var (name, wrap) in Carriers)
            {
                Accepts(wrap(Chain(Limit - 1)), $"{name} carrying a chain, depth {Limit}");
                Refuses(wrap(Chain(Limit)), $"{name} carrying a chain, depth {Limit + 1}");
            }

            // The deepest container is empty and the payload ends with its header: it still counts.
            foreach (var (name, leaf) in Containers.Where(c => c.Name.StartsWith("empty")))
            {
                Accepts(Chain(Limit, leaf), $"{name} as the leaf at depth {Limit}");
                Refuses(Chain(Limit + 1, leaf), $"{name} as the leaf at depth {Limit + 1}");
            }
        }

        [Test]
        public void ARefusalNamesTheTypeTheDepthTheLimitAndTheDirection()
        {
            var deep = Chain(Limit + 5, Cat(Bytes(0xa5), System.Text.Encoding.ASCII.GetBytes("CANRY")));
            var read = Assert.Throws<MessagePackSerializationException>(() => CultPayloadDepth.Check(deep, typeof(PayloadDepthTests), PayloadDirection.Read))!;
            var write = Assert.Throws<MessagePackSerializationException>(() => CultPayloadDepth.Check(deep, typeof(PayloadDepthTests), PayloadDirection.Write))!;
            Assert.That(read.Message, Is.EqualTo($"{typeof(PayloadDepthTests).FullName} payload nests {Limit + 1} MessagePack containers deep; the limit is {Limit} (refused on read)."));
            Assert.That(write.Message, Is.EqualTo($"{typeof(PayloadDepthTests).FullName} payload nests {Limit + 1} MessagePack containers deep; the limit is {Limit} (refused on write)."));
            Assert.That(read.Message + write.Message, Does.Not.Contain("CANRY"));
        }

        [Test]
        public void ATruncatedPayloadIsTheDeserializersToRefuse()
        {
            foreach (var truncated in new[] { Bytes(), Bytes(0xdc), Bytes(0xdc, 0), Bytes(0xdd, 0, 0, 0), Bytes(0xd9), Bytes(0xda, 1), Bytes(0xc6, 0, 0, 1) })
                Accepts(truncated, $"{Convert.ToHexString(truncated)} ends inside a header");

            // A header that fits exactly is read, not treated as cut short.
            Refuses(Chain(Limit + 1, Bytes(0xdc, 0, 0)), "an array16 header ending the payload is read");
            Accepts(Cat(Bytes(0x91, 0xd9, 200), Filler(10)), "a string cut short is not walked past its end");

            // A container header cut short at the limit is not yet a container: the deserializer refuses it, not the walk.
            foreach (var cut in new[] { Bytes(0xdc, 0), Bytes(0xdd, 0, 0, 0), Bytes(0xde, 0), Bytes(0xdf, 0, 0, 0) })
                Accepts(Chain(Limit + 1, cut), $"{Convert.ToHexString(cut)} cut short at depth {Limit + 1}");
        }

        [Test]
        public void OnlyTheFirstValueIsThePayload()
        {
            Accepts(Cat(Bytes(0x90), Chain(Limit + 10)), "bytes after the first value are not walked");
            Accepts(Cat(Bytes(0x01), Chain(Limit + 10)), "nor after a scalar");
        }
    }
}
