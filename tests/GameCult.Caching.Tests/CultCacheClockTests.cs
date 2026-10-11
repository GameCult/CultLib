#nullable enable
using System;
using System.Globalization;
using System.IO;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The clock behind every storedAt: the wall clock by default, and a pinned one when a golden store has to reproduce.
    public class CultCacheClockTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(UnionParityVectorTests.UnionSurface) });
        private static readonly CultRecordKey Key = new("surface");

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-clock-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private CultCache Open() => CultCacheMessagePack.Create(Path.Combine(_directory, "clock.cc"), new CultCacheOpenOptions { Registry = Registry });

        private static string Upsert(CultCache cache)
        {
            cache.UpsertAsync(typeof(UnionParityVectorTests.UnionSurface), new UnionParityVectorTests.UnionSurface(), Key).GetAwaiter().GetResult();
            return cache.GetStored(Key)!.StoredAt;
        }

        [Test]
        public void ADefaultCacheStampsTheWallClock()
        {
            using var cache = Open();
            var before = DateTimeOffset.UtcNow;
            var stamp = DateTimeOffset.Parse(Upsert(cache), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var after = DateTimeOffset.UtcNow;
            Assert.That(stamp, Is.InRange(before, after));
        }

        [Test]
        public void APinnedClockIsTheStoredAt()
        {
            var pinned = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var cache = Open();
            cache.Clock = () => pinned;
            Assert.That(Upsert(cache), Is.EqualTo("2026-01-01T00:00:00.0000000+00:00"));
        }

        [Test]
        public void APinnedClockStillIncreasesStoredAtPerKey()
        {
            var pinned = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var cache = Open();
            cache.Clock = () => pinned;
            Upsert(cache);
            Assert.That(Upsert(cache), Is.EqualTo("2026-01-01T00:00:00.0000001+00:00"), "the same instant again is one tick later");
        }
    }
}
