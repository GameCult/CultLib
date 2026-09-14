#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Faults come from real file locks, not from probes in the store.
    public class DirectoryStoreDurabilityTests
    {
        private const string FixedStoredAt = "2026-09-13T00:00:00.0000000+00:00";
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(Page) });
        private string _directory = string.Empty;
        private string _manifest = string.Empty;
        private string _records = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-durability-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _manifest = Path.Combine(_directory, "store.cc");
            _records = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(_manifest);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public async Task FailedManifestReplaceKeepsPreviousGeneration()
        {
            using var cache = Open(out _);
            var key = await cache.UpsertAsync(typeof(Page), new Page { Name = "a", Text = "one" });
            cache.FlushAllBackingStores();
            await cache.UpsertAsync(typeof(Page), new Page { Name = "a", Text = "two" }, key);

            using (new FileStream(_manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(() => cache.FlushAllBackingStores(),
                    Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
            }

            Assert.That(cache.IsDirty, Is.True);
            using (var reader = Open(out _))
                Assert.That(reader.Get<Page>(key)?.Text, Is.EqualTo("one"));

            cache.FlushAllBackingStores();
            using var reopened = Open(out _);
            Assert.That(reopened.Get<Page>(key)?.Text, Is.EqualTo("two"));
        }

        [Test]
        public void SameStoredAtDifferentPayloadWritesADistinctPage()
        {
            using var cache = Open(out var store);
            var key = new CultRecordKey("page");
            var descriptor = Registry.GetRequired<Page>();

            store.Push(new CultStoredDocument(key, FixedStoredAt, descriptor, new Page { Name = "p", Text = "first" }));
            store.PushAll();
            var firstIdentity = ManifestRecord(key).Payload;
            store.Push(new CultStoredDocument(key, FixedStoredAt, descriptor, new Page { Name = "p", Text = "second" }));
            store.PushAll();
            var secondIdentity = ManifestRecord(key).Payload;

            Assert.That(secondIdentity, Is.Not.EqualTo(firstIdentity));
            Assert.That(File.Exists(PagePath(secondIdentity)), Is.True);
            using var reader = Open(out _);
            Assert.That(reader.Get<Page>(key)?.Text, Is.EqualTo("second"));
        }

        [Test]
        public void FlushWritesPagesBeforeManifest()
        {
            using var cache = Open(out var store);
            var key = new CultRecordKey("page");
            var descriptor = Registry.GetRequired<Page>();
            store.Push(new CultStoredDocument(key, FixedStoredAt, descriptor, new Page { Name = "p", Text = "first" }));
            store.PushAll();
            var before = File.ReadAllBytes(_manifest);

            const string nextStoredAt = "2026-09-13T00:00:01.0000000+00:00";
            var next = new Page { Name = "p", Text = "second" };
            var page = CultDocumentMessagePackSerialization.SerializePersistedRecord(new CultPersistedRecord
            {
                Key = key.Value,
                SchemaId = descriptor.SchemaId,
                StoredAt = nextStoredAt,
                Payload = CultDocumentMessagePackSerialization.SerializeUntyped(next, typeof(Page), Registry)
            });
            using (var sha = SHA256.Create())
                Directory.CreateDirectory(PagePath(sha.ComputeHash(page)));

            store.Push(new CultStoredDocument(key, nextStoredAt, descriptor, next));
            Assert.That(() => store.PushAll(), Throws.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());

            Assert.That(File.ReadAllBytes(_manifest), Is.EqualTo(before), "the manifest was written although its page was not");
            using var reader = Open(out _);
            Assert.That(reader.Get<Page>(key)?.Text, Is.EqualTo("first"));
        }

        [Test]
        public void PullAllWaitsForHeldCommitLease()
        {
            using var cache = Open(out var store);
            Task pull;
            using (HoldLease())
            {
                pull = Task.Run(() => store.PullAll());
                Assert.That(pull.Wait(100), Is.False, "PullAll did not wait for the commit lease");
            }

            Assert.That(pull.Wait(TimeSpan.FromSeconds(10)), Is.True);
        }

        [Test]
        public async Task MutationBlockedBehindFlushStaysDirty()
        {
            using var cache = Open(out var store);
            var first = await cache.UpsertAsync(typeof(Page), new Page { Name = "a" });
            Task flush;
            Task<CultRecordKey> mutation;
            using (HoldLease())
            {
                flush = Task.Run(() => cache.FlushAllBackingStores());
                Assert.That(flush.Wait(100), Is.False);
                mutation = Task.Run(() => cache.UpsertAsync(typeof(Page), new Page { Name = "b" }));
                Assert.That(mutation.Wait(100), Is.False, "a mutation ran while the flush held the store");
            }

            Assert.That(Task.WaitAll(new Task[] { flush, mutation }, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(store.IsDirty, Is.True, "the flush pardoned a mutation it did not write");
            using (var reader = Open(out _))
            {
                Assert.That(reader.Get(first), Is.Not.Null);
                Assert.That(reader.Get(mutation.Result), Is.Null);
            }

            cache.FlushAllBackingStores();
            using var reopened = Open(out _);
            Assert.That(reopened.Get(mutation.Result), Is.Not.Null);
        }

        [Test]
        public async Task FlushNeverOpensUnchangedPages()
        {
            using var cache = Open(out var store);
            var first = await cache.UpsertAsync(typeof(Page), new Page { Name = "a" });
            cache.FlushAllBackingStores();
            var page = Directory.GetFiles(_records, "*.msgpack").Single();

            using (new FileStream(page, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await cache.UpsertAsync(typeof(Page), new Page { Name = "b" });
                Assert.That(() => cache.FlushAllBackingStores(), Throws.Nothing);
            }

            Assert.That(store.IsDirty, Is.False);
            using var reader = Open(out _);
            Assert.That(reader.GetAll<Page>().Count(), Is.EqualTo(2));
            Assert.That(reader.Get(first), Is.Not.Null);
        }

        [Test]
        public async Task MissingManifestIgnoresOrphanPagesAndFlushDeletesThem()
        {
            Directory.CreateDirectory(_records);
            var orphan = Path.Combine(_records, new string('a', 64) + ".msgpack");
            File.WriteAllBytes(orphan, new byte[] { 0x91, 0xA1, 0x78 });

            using var cache = Open(out _);
            Assert.That(cache.AllEntries, Is.Empty);
            await cache.UpsertAsync(typeof(Page), new Page { Name = "a" });
            cache.FlushAllBackingStores();

            Assert.That(File.Exists(orphan), Is.False);
            Assert.That(Directory.GetFiles(_records, "*.msgpack"), Has.Length.EqualTo(1));
        }

        [Test]
        public async Task TamperedPageFailsLoadNamingPage()
        {
            using (var cache = Open(out _))
            {
                await cache.UpsertAsync(typeof(Page), new Page { Name = "a", Text = "honest" });
                cache.FlushAllBackingStores();
            }

            var page = Directory.GetFiles(_records, "*.msgpack").Single();
            var bytes = File.ReadAllBytes(page);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(page, bytes);

            using var reader = new CultCache(Registry);
            Assert.That(() => reader.AddBackingStore(new DirectoryMessagePackBackingStore(_manifest)),
                Throws.Exception.With.Message.Contains(page));
            Assert.That(reader.AllEntries, Is.Empty);
        }

        private CultCache Open(out DirectoryMessagePackBackingStore store)
        {
            var cache = new CultCache(Registry);
            store = new DirectoryMessagePackBackingStore(_manifest);
            cache.AddBackingStore(store);
            return cache;
        }

        private FileStream HoldLease()
        {
            Directory.CreateDirectory(_records);
            return new FileStream(Path.Combine(_records, ".commit.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private CultPersistedRecord ManifestRecord(CultRecordKey key) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(_manifest)).Records.Single(record => record.Key == key.Value);

        private string PagePath(byte[] identity) =>
            Path.Combine(_records, BitConverter.ToString(identity).Replace("-", string.Empty).ToLowerInvariant() + ".msgpack");

        [CultDocument("tests.durability_page", "tests.durability_page.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class Page
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Text = string.Empty;
        }
    }
}
