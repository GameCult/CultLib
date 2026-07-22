#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public sealed class SoaValueColumnTests
    {
        [Test]
        public async Task Soa_Preserves_Unmanaged_Value_Members_As_Intact_Columns()
        {
            var cache = new CultCache();
            var first = new CultRecordHandle<VectorDocument>(new CultRecordKey("vector:first"));
            var second = new CultRecordHandle<VectorDocument>(new CultRecordKey("vector:second"));

            await cache.UpsertAsync(new VectorDocument
            {
                Name = "first",
                Position = new TestFloat3(1, 2, 3),
                Health = 10,
            }, first);
            await cache.UpsertAsync(new VectorDocument
            {
                Name = "second",
                Position = new TestFloat3(4, 5, 6),
                Health = 20,
            }, second);
            await cache.UpsertAsync(new VectorDocument
            {
                Name = "first-updated",
                Position = new TestFloat3(7, 8, 9),
                Health = 30,
            }, first);

            var table = cache.Soa<VectorDocument>();

            Assert.That(table.Column<TestFloat3>(nameof(VectorDocument.Position)).Span.ToArray(), Is.EqualTo(new[]
            {
                new TestFloat3(7, 8, 9),
                new TestFloat3(4, 5, 6),
            }));
            Assert.That(table.Column<int>(nameof(VectorDocument.Health)).Span.ToArray(), Is.EqualTo(new[] { 30, 20 }));
            Assert.That(() => table.Column<float>("Position.x"), Throws.TypeOf<KeyNotFoundException>());
            Assert.That(() => table.Column<float>("x"), Throws.TypeOf<KeyNotFoundException>());

            Assert.That(cache.Remove(first.Key), Is.True);
            var afterRemoval = cache.Soa<VectorDocument>();
            Assert.That(afterRemoval.Keys.Select(key => key.Value).ToArray(), Is.EqualTo(new[] { "vector:second" }));
            Assert.That(
                afterRemoval.Column<TestFloat3>(nameof(VectorDocument.Position)).Span.ToArray(),
                Is.EqualTo(new[] { new TestFloat3(4, 5, 6) }));
        }

        [Test]
        public async Task Soa_Rejects_Value_Members_Containing_Managed_References()
        {
            var cache = new CultCache();
            await cache.UpsertAsync(new VectorDocument
            {
                Name = "managed",
                Position = new TestFloat3(1, 2, 3),
                ManagedValue = new ManagedValue(42, "not a column"),
            });

            var table = cache.Soa<VectorDocument>();

            Assert.That(
                () => table.Column<int>(nameof(VectorDocument.ManagedValue)),
                Throws.TypeOf<KeyNotFoundException>());
        }

        [MessagePackObject]
        public readonly record struct TestFloat3(
            [property: Key(0)] float x,
            [property: Key(1)] float y,
            [property: Key(2)] float z);

        [MessagePackObject]
        public readonly record struct ManagedValue(
            [property: Key(0)] int Code,
            [property: Key(1)] string Label);

        [CultDocument("tests.soa_value_column", "tests.soa_value_column.v1")]
        internal sealed class VectorDocument
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public TestFloat3 Position;

            [Key(2)]
            public int Health;

            [Key(3)]
            public ManagedValue ManagedValue;
        }
    }
}
