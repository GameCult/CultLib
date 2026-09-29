#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // Cut 1 of docs/cultcache-publication-cut.md: an admission reads every landing record's name and index values once,
    // before land and before any sequence is minted. A getter that throws refuses the whole change set.
    [NonParallelizable]
    public class CultCacheProjectionTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(ProjectedRecord) });

        private string _directory = string.Empty;

        [SetUp]
        public void Prepare()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-projection-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            ProjectedRecord.Armed = false;
            ProjectedRecord.Reads = 0;
        }

        [TearDown]
        public void Cleanup()
        {
            ProjectedRecord.Armed = false;
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [CultDocument("tests.projected_record", "tests.projected_record.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class ProjectedRecord
        {
            public static long Reads;
            public static volatile bool Armed;
            [IgnoreMember] private string _name = string.Empty;
            [IgnoreMember] private string _code = string.Empty;

            [Key(0)]
            [CultName]
            public string Name
            {
                get
                {
                    Interlocked.Increment(ref Reads);
                    if (Armed && _name == "boom")
                        throw new InvalidOperationException("name getter boom");
                    return _name;
                }
                set => _name = value;
            }

            [Key(1)]
            [CultIndex("code")]
            public string Code
            {
                get
                {
                    Interlocked.Increment(ref Reads);
                    if (Armed && _code == "boom")
                        throw new InvalidOperationException("index getter boom");
                    return _code;
                }
                set => _code = value;
            }
        }

        private static readonly CultRecordKey OkKey = new("projected:ok");
        private static readonly CultRecordKey BoomKey = new("projected:boom");

        private static ProjectedRecord Ok() => new() { Name = "ok", Code = "ok-code" };

        private static ProjectedRecord Boom(bool name) => name ? new ProjectedRecord { Name = "boom", Code = "boom-code" } : new ProjectedRecord { Name = "boom-name", Code = "boom" };

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path) => CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });

        private static void Put(CultCache cache, ProjectedRecord document, CultRecordKey key) =>
            cache.Commit(batch => batch.Upsert(typeof(ProjectedRecord), document, key));

        private static long SequenceOf(CultCache cache) => cache.GetWithSequence(OkKey).Sequence;

        private static void StageBoth(CultCacheBatch batch, ProjectedRecord boom)
        {
            batch.Upsert(typeof(ProjectedRecord), Ok(), OkKey);
            batch.Upsert(typeof(ProjectedRecord), boom, BoomKey);
        }

        [TestCase(true, "[CultName] member Name")]
        [TestCase(false, "index 'code' member Code")]
        public void ARefusedGetterMintsNothingAndChangesNothing(bool nameThrows, string member)
        {
            using var cache = new CultCache(Registry);
            var seen = new List<long>();
            using var watch = cache.Watch<ProjectedRecord>().Subscribe(change => seen.Add(change.Sequence));
            Put(cache, new ProjectedRecord { Name = "seed", Code = "seed-code" }, new CultRecordKey("projected:seed"));
            var before = SequenceOf(cache);
            seen.Clear();
            ProjectedRecord.Armed = true;

            var refusal = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch => StageBoth(batch, Boom(nameThrows))))!;

            ProjectedRecord.Armed = false;
            Assert.Multiple(() =>
            {
                Assert.That(refusal.Message, Does.Contain(BoomKey.Value).And.Contain(member).And.Contain("getter boom"));
                Assert.That(SequenceOf(cache), Is.EqualTo(before), "a refused admission mints nothing");
                Assert.That(cache.Get<ProjectedRecord>(OkKey), Is.Null, "the good record did not land");
                Assert.That(cache.Get<ProjectedRecord>(BoomKey), Is.Null);
                Assert.That(cache.GetByName<ProjectedRecord>("ok"), Is.Null, "the name index holds nothing of the refused set");
                Assert.That(cache.GetByIndex<ProjectedRecord>("code", "ok-code"), Is.Null);
                Assert.That(seen, Is.Empty, "nothing was published");
            });

            Put(cache, Ok(), OkKey);
            Assert.That(SequenceOf(cache), Is.EqualTo(before + 1), "the next write is previous + 1");
            Assert.That(seen, Is.EqualTo(new[] { before + 1 }));
        }

        [Test]
        public void ARefusedSingleWriteMintsNothing()
        {
            using var cache = new CultCache(Registry);
            ProjectedRecord.Armed = true;

            var refusal = Assert.Throws<InvalidOperationException>(() => Put(cache, Boom(true), BoomKey))!;

            ProjectedRecord.Armed = false;
            Assert.That(refusal.Message, Does.Contain(BoomKey.Value));
            Assert.That(SequenceOf(cache), Is.EqualTo(0));
            Assert.That(cache.Get<ProjectedRecord>(BoomKey), Is.Null);
        }

        [Test]
        public void ARefusedBatchLeavesAStoreByteIdenticalAndReopensWithNeitherKey()
        {
            var path = PathOf("projected.cc");
            using (var seed = Open(path))
                Put(seed, new ProjectedRecord { Name = "seed", Code = "seed-code" }, new CultRecordKey("projected:seed"));
            var before = File.ReadAllBytes(path);

            using (var cache = Open(path))
            {
                ProjectedRecord.Armed = true;
                Assert.Throws<InvalidOperationException>(() => cache.Commit(batch => StageBoth(batch, Boom(true))));
                ProjectedRecord.Armed = false;
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            }

            using var reopened = Open(path);
            Assert.Multiple(() =>
            {
                Assert.That(reopened.Get<ProjectedRecord>(OkKey), Is.Null);
                Assert.That(reopened.Get<ProjectedRecord>(BoomKey), Is.Null);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            });
        }

        [Test]
        public void ALoadWhoseGetterThrowsRefusesTheOpenAndLeavesTheFileByteIdentical()
        {
            var path = PathOf("poisoned.cc");
            using (var seed = Open(path))
                Put(seed, Boom(true), BoomKey);
            var before = File.ReadAllBytes(path);

            ProjectedRecord.Armed = true;
            var refusal = Assert.Throws<InvalidOperationException>(() => Open(path))!;
            ProjectedRecord.Armed = false;

            Assert.That(refusal.Message, Does.Contain(BoomKey.Value).And.Contain("[CultName] member Name"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        // A counting getter: each landing record is read once for its name and once for its index, and never again by the
        // variant rules or the indexer. A variant re-resolved by a base edit lands too, and is read once.
        [Test]
        public void EachLandedRecordIsReadOncePerGetterIncludingAVariantDependent()
        {
            using var cache = new CultCache(Registry, CultCacheMessagePack.CreateCodec(Registry));
            var baseKey = new CultRecordKey("projected:base");
            Put(cache, new ProjectedRecord { Name = "base", Code = "base-code" }, baseKey);
            cache.Commit(batch => batch.UpsertVariant(new CultRecordKey("projected:variant"), baseKey, new[]
            {
                cache.Override<ProjectedRecord>(nameof(ProjectedRecord.Name), "variant"),
                cache.Override<ProjectedRecord>(nameof(ProjectedRecord.Code), "variant-code")
            }));

            ProjectedRecord.Reads = 0;
            Put(cache, new ProjectedRecord { Name = "base", Code = "base-code-2" }, baseKey);

            // Two landing records (the base, and the variant re-resolved against it), two getters each.
            Assert.That(Interlocked.Read(ref ProjectedRecord.Reads), Is.EqualTo(4));
            Assert.That(cache.GetByIndex<ProjectedRecord>("code", "base-code-2"), Is.Not.Null);
            Assert.That(cache.GetByName<ProjectedRecord>("variant"), Is.Not.Null);
        }
    }
}
