#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // Rule 1 of variants:ruling:write-set-content-version-declared-ownership: a flush, a commit and a single-document write apply
    // only what the writer staged to what the store durably holds now, under its lock. Every other record and catalog entry is
    // copied forward as stored. Every input goes through the cache's own write API and the real stores.
    public class WriteSetTests
    {
        [CultDocument("tests.write_set_item", "tests.write_set_item.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WsItem
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public string Note = string.Empty;
        }

        [CultDocument("tests.write_set_other", "tests.write_set_other.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class WsOther
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(WsItem), typeof(WsOther) });
        private static readonly string ItemId = Registry.GetRequired(typeof(WsItem)).SchemaId;
        private static readonly CultRecordKey A = new("a");
        private static readonly CultRecordKey X = new("x");

        private string _directory = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-write-set-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path, bool directory) =>
            CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory });

        private static CultPersistedStoreSnapshot Read(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        private static void Write(string path, CultPersistedStoreSnapshot snapshot) =>
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));

        // What a record is on disk: its id, its storedAt and, in a single file, its payload (a directory manifest indexes pages).
        private static string Stored(string path, string key, bool directory)
        {
            var record = Read(path).Records.Single(candidate => candidate.Key == key);
            return $"{record.SchemaId}|{record.StoredAt}|{(directory ? string.Empty : BitConverter.ToString(record.Payload))}";
        }

        private static byte[] EntryBytes(string path, string schemaId) =>
            Read(path).SchemaCatalog.Single(entry => entry.SchemaId == schemaId).RawBytes!;

        private static string[] Keys(string path) => Read(path).Records.Select(record => record.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        private static void Seed(string path, bool directory, params string[] keys)
        {
            using var cache = Open(path, directory);
            cache.Commit(batch =>
            {
                foreach (var key in keys)
                    batch.Upsert(typeof(WsItem), new WsItem { Name = "name-" + key, Note = "seeded" }, new CultRecordKey(key));
            });
        }

        // One staged write landed by a plain flush or by an unconditional commit.
        private static void Land(CultCache cache, bool commit, Type type, object document, CultRecordKey key)
        {
            if (commit)
            {
                cache.Commit(batch => batch.Upsert(type, document, key));
                return;
            }

            cache.UpsertAsync(type, document, key).GetAwaiter().GetResult();
            cache.FlushAllBackingStores();
        }

        // A loads, B writes x, A stages y and flushes or commits: x keeps B's bytes, id and storedAt. A record B removed stays removed.
        [Test]
        public void ALoadThenAWriteAfterAnotherWritersWriteKeepsThatWrite([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("keeps.cc");
            Seed(path, directory, "a");
            using var a = Open(path, directory);
            using (var b = Open(path, directory))
                b.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x", Note = "by-b" }, X));
            var written = Stored(path, "x", directory);

            Land(a, commit, typeof(WsItem), new WsItem { Name = "name-y", Note = "by-a" }, new CultRecordKey("y"));

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "x", "y" }));
            Assert.That(Stored(path, "x", directory), Is.EqualTo(written));
        }

        [Test]
        public void ARecordAnotherWriterRemovedStaysRemovedAfterALaterWrite([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("removed.cc");
            Seed(path, directory, "a", "x");
            using var a = Open(path, directory);
            Assert.That(a.Get<WsItem>(X), Is.Not.Null, "A loaded x");
            using (var b = Open(path, directory))
                b.Commit(batch => batch.Remove(X));

            Land(a, commit, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "y" }), "A's copy of x is not written back");
        }

        // A path with nothing at it when the cache opened, and a store there by the time it writes: the write lands on that store.
        [Test]
        public void AWriteOverAStoreThatAppearedAfterTheOpenKeepsItsRecords([Values] bool commit)
        {
            var path = PathOf("appeared.cc");
            using var a = Open(path, false);
            Assert.That(File.Exists(path), Is.False);
            using (var b = Open(path, false))
                b.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X));

            Land(a, commit, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));

            Assert.That(Keys(path), Is.EqualTo(new[] { "x", "y" }));
        }

        // An in-place change to a loaded document is not a write: it is written when the document is staged.
        [Test]
        public void AnInPlaceChangeThatWasNeverStagedIsNotWrittenAndAStagedOneIs([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("in-place.cc");
            Seed(path, directory, "a");
            using var cache = Open(path, directory);
            var item = cache.Get<WsItem>(A)!;
            item.Note = "edited";

            cache.FlushAllBackingStores();
            using (var check = Open(path, directory))
                Assert.That(check.Get<WsItem>(A)!.Note, Is.EqualTo("seeded"), "an unstaged change is not written");

            Land(cache, commit, typeof(WsItem), item, A);
            using (var check = Open(path, directory))
                Assert.That(check.Get<WsItem>(A)!.Note, Is.EqualTo("edited"));
        }

        // B changes the durable entry for the id of a record A carries. A writes a record under another id: the entry for the id it
        // did not write is B's, byte for byte, not the one A registered or loaded.
        [Test]
        public void ARecordCopiedForwardKeepsTheCatalogEntryTheStoreHoldsNow([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("entry.cc");
            Seed(path, directory, "a");
            using var cache = Open(path, directory);
            var snapshot = Read(path);
            snapshot.SchemaCatalog.Single(entry => entry.SchemaId == ItemId).ContentHash = "changed-by-b";
            Write(path, snapshot);
            var theirs = EntryBytes(path, ItemId);

            Land(cache, commit, typeof(WsOther), new WsOther { Name = "name-z" }, new CultRecordKey("z"));

            Assert.That(EntryBytes(path, ItemId), Is.EqualTo(theirs));
            Assert.That(Read(path).SchemaCatalog.Single(entry => entry.SchemaId == ItemId).ContentHash, Is.EqualTo("changed-by-b"));
        }

        // The runtime decodes seven fields of an entry. Fields after them, and the width a value was written in, are not its to drop.
        [Test]
        public void ACarriedCatalogEntryKeepsItsExactBytesIncludingWhatThisRuntimeDoesNotDecode([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("raw.cc");
            Seed(path, directory, "a");
            var original = EntryBytes(path, ItemId);
            Assert.That(original[0], Is.EqualTo(0x97), "a fixarray of the seven fields the runtime knows");
            var crafted = new byte[] { 0x99 }
                .Concat(original.Skip(1))
                .Concat(new byte[] { 0xd3, 0, 0, 0, 0, 0, 0, 0, 1 })
                .Concat(new byte[] { 0xc0 })
                .ToArray();
            var bytes = File.ReadAllBytes(path);
            var at = IndexOf(bytes, original);
            Assert.That(at, Is.GreaterThanOrEqualTo(0));
            File.WriteAllBytes(path, bytes.Take(at).Concat(crafted).Concat(bytes.Skip(at + original.Length)).ToArray());

            using var cache = Open(path, directory);
            Assert.That(cache.Get<WsItem>(A), Is.Not.Null, "the entry with two unknown trailing fields reads");
            Land(cache, commit, typeof(WsOther), new WsOther { Name = "name-z" }, new CultRecordKey("z"));

            Assert.That(IndexOf(File.ReadAllBytes(path), crafted), Is.GreaterThanOrEqualTo(0), "the entry is in the written store byte for byte");
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var start = 0; start + needle.Length <= haystack.Length; start++)
            {
                var same = true;
                for (var index = 0; same && index < needle.Length; index++)
                    same = haystack[start + index] == needle[index];
                if (same)
                    return start;
            }

            return -1;
        }

        // The condition is the only difference between a conditional and an unconditional commit: one batch, one apply.
        [Test]
        public void AConditionalAndAnUnconditionalCommitOfOneBatchOntoOneStoreWriteTheSameBytes()
        {
            var first = PathOf("unconditional.cc");
            var second = PathOf("conditional.cc");
            Seed(first, false, "a", "x");
            File.Copy(first, second);

            foreach (var (path, conditional) in new[] { (first, false), (second, true) })
            {
                using var cache = Open(path, false);
                var current = cache.Get<WsItem>(A)!;
                Assert.That(cache.Commit(batch =>
                {
                    if (conditional)
                        batch.Expect(A, current);
                    batch.Upsert(typeof(WsOther), new WsOther { Name = "name-z" }, new CultRecordKey("z"));
                    batch.Remove(X);
                }), Is.True);
            }

            byte[] Normalized(string path)
            {
                var snapshot = Read(path);
                foreach (var record in snapshot.Records.Where(record => record.Key == "z"))
                    record.StoredAt = string.Empty;
                return CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
            }

            Assert.That(Keys(first), Is.EqualTo(new[] { "a", "z" }));
            Assert.That(Normalized(second), Is.EqualTo(Normalized(first)));
        }

        // An unconditional commit also lands what was staged before it, on the store as it is now.
        [Test]
        public void AnUnconditionalCommitLandsEarlierStagedWritesWithItsBatchOntoWhatAnotherWriterWrote([Values] bool directory)
        {
            var path = PathOf("staged.cc");
            Seed(path, directory, "a");
            using var cache = Open(path, directory);
            using (var other = Open(path, directory))
                other.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X));
            cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-s" }, new CultRecordKey("s")).GetAwaiter().GetResult();

            Assert.That(cache.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"))), Is.True);

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "s", "x", "y" }));
            Assert.That(cache.IsDirty, Is.False);
        }

        // The operator's check on a real store: CULTLIB_REAL_STORE names a copy of one. A cache that registers none of its types
        // stages one record and flushes or commits: every other record and catalog entry, and the header, are byte for byte as they were.
        [Test]
        public void ARealStoreSurvivesOneStagedWriteByteForByte([Values] bool commit)
        {
            var source = Environment.GetEnvironmentVariable("CULTLIB_REAL_STORE");
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                Assert.Ignore("CULTLIB_REAL_STORE does not name a copy of a real store.");
            var path = PathOf("real.cc");
            File.Copy(source!, path);
            var before = Read(path);

            using (var cache = Open(path, false))
                Land(cache, commit, typeof(WsItem), new WsItem { Name = "name-real-write" }, new CultRecordKey("real-write"));

            var after = Read(path);
            TestContext.Out.WriteLine($"real store: {before.Records.Length} records, {before.SchemaCatalog.Length} catalog entries, header {before.FormatVersion}");
            Assert.That(after.Records.Length, Is.EqualTo(before.Records.Length + 1));
            Assert.That(after.FormatVersion, Is.EqualTo(before.FormatVersion));
            byte[] Bytes(CultPersistedRecord record) => CultDocumentMessagePackSerialization.SerializeSnapshot(new CultPersistedStoreSnapshot { Records = new[] { record } });
            var changed = before.Records.Where(record => !Bytes(record).SequenceEqual(Bytes(after.Records.Single(kept => kept.Key == record.Key)))).Select(record => record.Key).ToArray();
            Assert.That(changed, Is.Empty, "records that are not byte-identical");
            var afterEntries = after.SchemaCatalog.Select(entry => entry.RawBytes!).ToList();
            var missing = before.SchemaCatalog.Where(entry => !afterEntries.Any(raw => raw.SequenceEqual(entry.RawBytes!))).Select(entry => entry.SchemaId).ToArray();
            Assert.That(missing, Is.Empty, "catalog entries that are not byte-identical");
        }

        private static void MarkAs(string path, string header)
        {
            var snapshot = Read(path);
            snapshot.FormatVersion = header;
            Write(path, snapshot);
        }

        // A marked header says the store holds an element id, and a record's bytes cannot say whether it does. A write that copies
        // any record forward therefore keeps the mark, whatever this cache wrote or read of that record.
        [Test]
        public void AMarkedHeaderIsKeptWhileTheWriteCopiesAnyRecord([Values] bool anotherWriterAddedARecord)
        {
            var path = PathOf("mark.cc");
            Seed(path, false, "a");
            MarkAs(path, CultPersistedStoreSnapshot.FormatV3);
            using var cache = Open(path, false);
            if (anotherWriterAddedARecord)
            {
                using (var other = Open(path, false))
                    other.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X));
                MarkAs(path, CultPersistedStoreSnapshot.FormatV3);
            }

            Land(cache, commit: false, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
        }

        // Only a write that encodes every record the store will hold can say none holds an id.
        [Test]
        public void AMarkedHeaderIsDroppedOnlyByAWriteThatEncodesEveryRecord([Values] bool commit)
        {
            var path = PathOf("mark-all.cc");
            Seed(path, false, "a");
            MarkAs(path, CultPersistedStoreSnapshot.FormatV3);
            using var cache = Open(path, false);

            Land(cache, commit, typeof(WsItem), new WsItem { Name = "name-a", Note = "again" }, A);

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
        }

        // Another writer changed a copied record after this cache read it, and the mark stays: a copied record is not read to decide it.
        [Test]
        public void AMarkedHeaderIsKeptWhenAnotherWriterChangedACopiedRecordAfterTheLoad()
        {
            var path = PathOf("mark-changed.cc");
            Seed(path, false, "a");
            MarkAs(path, CultPersistedStoreSnapshot.FormatV3);
            using var cache = Open(path, false);
            using (var other = Open(path, false))
                other.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-a", Note = "changed" }, A));
            MarkAs(path, CultPersistedStoreSnapshot.FormatV3);

            Land(cache, commit: false, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
        }

        // A copied record stays copied whoever wrote it: what this cache wrote earlier does not make the next write one that decides by content.
        [Test]
        public void AMarkedHeaderIsKeptWhenEveryCopiedRecordWasWrittenByThisCache()
        {
            var path = PathOf("mark-wrote.cc");
            Seed(path, false, "a");
            using var cache = Open(path, false);
            Land(cache, commit: false, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));
            MarkAs(path, CultPersistedStoreSnapshot.FormatV3);

            Land(cache, commit: false, typeof(WsItem), new WsItem { Name = "name-z" }, new CultRecordKey("z"));

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
        }

        // A batch is the write that lands last: it replaces a staged write of the same key.
        [Test]
        public void ABatchWinsOverAStagedWriteOfTheSameKey()
        {
            var path = PathOf("batch-wins.cc");
            Seed(path, false, "a");
            using (var cache = Open(path, false))
            {
                cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-a", Note = "staged" }, A).GetAwaiter().GetResult();
                Assert.That(cache.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-a", Note = "batch" }, A)), Is.True);
            }

            using var check = Open(path, false);
            Assert.That(check.Get<WsItem>(A)!.Note, Is.EqualTo("batch"));
        }

        // v2 exactly when the written store holds a variant: a copied variant keeps it, and the last one removed drops it.
        [Test]
        public void TheV2HeaderIsWrittenExactlyWhileTheStoreHoldsAVariant()
        {
            var path = PathOf("variant.cc");
            Seed(path, false, "a");
            var variant = new CultRecordKey("v");
            using (var cache = Open(path, false))
            {
                cache.Commit(batch => batch.UpsertVariant(variant, A, new[]
                {
                    cache.Override<WsItem>(nameof(WsItem.Name), "name-v"),
                    cache.Override<WsItem>(nameof(WsItem.Note), "variant")
                }));
            }

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV2));

            using (var cache = Open(path, false))
            {
                Land(cache, commit: true, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));
                Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV2), "the variant was copied");
                cache.Commit(batch => batch.Remove(variant));
            }

            Assert.That(Read(path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV1));
        }

        // Another writer adds a record under this cache's schema id whose payload this cache's type cannot decode.
        private static void AddUndecodableRecord(string path, string key)
        {
            var snapshot = Read(path);
            snapshot.Records = snapshot.Records.Concat(new[]
            {
                new CultPersistedRecord { Key = key, SchemaId = ItemId, StoredAt = DateTimeOffset.UtcNow.ToString("O"), Payload = new byte[] { 0xC1 } }
            }).OrderBy(record => record.Key, StringComparer.Ordinal).ToArray();
            Write(path, snapshot);
        }

        // A flush decodes nothing it did not stage: a copied record this cache's type cannot decode is carried byte for byte.
        [Test]
        public void AFlushCopiesARecordThisCacheCannotDecode([Values] bool commit)
        {
            var path = PathOf("undecodable.cc");
            Seed(path, false, "a");
            using var a = Open(path, false);
            AddUndecodableRecord(path, "r");
            var carried = Stored(path, "r", false);

            Land(a, commit, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y"));

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "r", "y" }));
            Assert.That(Stored(path, "r", false), Is.EqualTo(carried));
        }

        // The same record in a store holding a variant cannot be judged without decoding it: the write is refused with the decode
        // failure as its cause, and the disk, the cache and its staged change are as they were.
        [Test]
        public void AWriteIntoAVariantStoreWithARecordThisCacheCannotLoadChangesNothing([Values] bool commit)
        {
            var path = PathOf("undecodable-variant.cc");
            Seed(path, false, "a");
            using (var seed = Open(path, false))
                seed.Commit(batch => batch.UpsertVariant(new CultRecordKey("v"), A, new[] { seed.Override<WsItem>(nameof(WsItem.Name), "name-v"), seed.Override<WsItem>(nameof(WsItem.Note), "variant") }));
            var a = Open(path, false);
            AddUndecodableRecord(path, "r");
            var before = File.ReadAllBytes(path);
            var y = new CultRecordKey("y");

            var conflict = Assert.Throws<CultWriteConflictException>(() =>
            {
                if (commit)
                    a.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-y" }, y));
                else
                    Land(a, false, typeof(WsItem), new WsItem { Name = "name-y" }, y);
            })!;

            Assert.That(conflict.InnerException, Is.Not.Null, "the decode failure is the cause");
            Assert.That(conflict.ChangedKeys, Is.EqualTo(new[] { "r" }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(a.Get(new CultRecordKey("r")), Is.Null);
            if (!commit)
            {
                Assert.That(a.IsDirty, Is.True, "a reload that fails changes nothing, the staged write included");
                Assert.That(a.Get<WsItem>(y), Is.Not.Null);
            }
        }

        // A stages k, B writes k later, A flushes: A's bytes land at a storedAt later than B's in either store kind, so a writer holding B's k is told it moved.
        [Test]
        public void AWriteOverAnotherWritersLaterRecordStoresALaterStoredAt([Values] bool directory)
        {
            var path = PathOf("later.cc");
            Seed(path, directory, "a");
            var k = new CultRecordKey("k");
            using var a = Open(path, directory);
            a.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-a" }, k).GetAwaiter().GetResult();
            System.Threading.Thread.Sleep(30);
            using var b = Open(path, directory);
            b.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-b" }, k));
            var seenByB = b.Get<WsItem>(k)!;
            var bAt = Read(path).Records.Single(record => record.Key == "k").StoredAt;

            a.FlushAllBackingStores();

            var onDisk = Read(path).Records.Single(record => record.Key == "k");
            Assert.That(DateTimeOffset.Parse(onDisk.StoredAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                Is.GreaterThan(DateTimeOffset.Parse(bAt, null, System.Globalization.DateTimeStyles.RoundtripKind)));
            Assert.That(a.Commit(batch => { batch.Expect(k, a.Get<WsItem>(k)!); batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "again" }, k); }),
                Is.True, "A's own condition holds without a pull");
            Assert.That(b.Commit(batch => { batch.Expect(k, seenByB); batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "stale" }, k); }),
                Is.False, "B's copy of k is no longer what the store holds");
        }

        // Rule 2: a record's version is the SHA-256 of its stored bytes. Another writer rewrites the record to other bytes yet leaves
        // its storedAt as it was (a peer whose clock equals, or a tool): the bytes are all that tells the two records apart.
        private void RewriteAtItsOwnStoredAt(string path, bool directory, string key, string note)
        {
            var storedAt = StoredAtOf(path, key);
            using (var writer = Open(path, directory))
                writer.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-" + key, Note = note }, new CultRecordKey(key)));
            var snapshot = Read(path);
            var record = snapshot.Records.Single(candidate => candidate.Key == key);
            if (!directory)
            {
                record.StoredAt = storedAt;
                Write(path, snapshot);
                return;
            }

            var pages = DirectoryMessagePackBackingStore.DefaultRecordDirectoryPath(path);
            var page = CultDocumentMessagePackSerialization.DeserializePersistedRecord(
                File.ReadAllBytes(Path.Combine(pages, Convert.ToHexString(record.Payload).ToLowerInvariant() + ".msgpack")));
            page.StoredAt = storedAt;
            var bytes = CultDocumentMessagePackSerialization.SerializePersistedRecord(page);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            File.WriteAllBytes(Path.Combine(pages, Convert.ToHexString(hash).ToLowerInvariant() + ".msgpack"), bytes);
            record.StoredAt = storedAt;
            record.Payload = hash;
            Write(path, snapshot);
        }

        private static string NoteOnDisk(string path, bool directory)
        {
            using var fresh = Open(path, directory);
            return fresh.Get<WsItem>(K)!.Note;
        }

        private static readonly CultRecordKey K = new("k");

        // A reads k; another writer rewrites it at its own storedAt; A's Expect on what it read fails and the disk keeps the rewrite.
        [Test]
        public void AStaleExpectFailsWhenAnotherWriterRewroteTheRecordAtItsOwnStoredAt([Values] bool directory)
        {
            var path = PathOf("t1.cc");
            Seed(path, directory, "k");
            using var a = Open(path, directory);
            var observed = a.Get<WsItem>(K)!;
            RewriteAtItsOwnStoredAt(path, directory, "k", "by-c");

            var committed = a.Commit(batch =>
            {
                batch.Expect(K, observed);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "stale" }, K);
            });

            Assert.That(committed, Is.False);
            Assert.That(NoteOnDisk(path, directory), Is.EqualTo("by-c"));
        }

        // A pull serves the rewritten record although nothing about it but its bytes changed.
        [Test]
        public void APullServesARecordRewrittenAtItsOwnStoredAt([Values] bool directory)
        {
            var path = PathOf("t2.cc");
            Seed(path, directory, "k");
            using var a = Open(path, directory);
            RewriteAtItsOwnStoredAt(path, directory, "k", "by-c");

            a.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(a.Get<WsItem>(K)!.Note, Is.EqualTo("by-c"));
        }

        // ExpectUnchanged compares every record's version: a rewrite at the same storedAt fails it for an unrelated key.
        [Test]
        public void ExpectUnchangedFailsWhenARecordWasRewrittenAtItsOwnStoredAt([Values] bool directory)
        {
            var path = PathOf("t3.cc");
            Seed(path, directory, "k");
            using var a = Open(path, directory);
            RewriteAtItsOwnStoredAt(path, directory, "k", "by-c");

            var committed = a.Commit(batch =>
            {
                batch.ExpectUnchanged();
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X);
            });

            Assert.That(committed, Is.False);
            Assert.That(Keys(path), Is.EqualTo(new[] { "k" }));
        }

        // The staleness check of a store holding a variant compares versions: a rewrite at the record's own storedAt refuses the
        // write, names the record, leaves the file as it was, and the cache then holds the rewrite so a retry lands.
        [Test]
        public void AWriteIntoAVariantStoreIsRefusedWhenARecordWasRewrittenAtItsOwnStoredAt()
        {
            var path = PathOf("t4.cc");
            Seed(path, false, "a");
            using var a = Open(path, false);
            AddVariant(a, "v", A);
            using (var peer = Open(path, false))
                Assert.That(peer.Get<WsItem>(A), Is.Not.Null);
            RewriteAtItsOwnStoredAt(path, false, "a", "by-c");
            var before = File.ReadAllBytes(path);
            var y = new CultRecordKey("y");

            var conflict = Assert.Throws<CultWriteConflictException>(() =>
                Land(a, true, typeof(WsItem), new WsItem { Name = "name-y" }, y))!;

            Assert.That(conflict.ChangedKeys, Is.EqualTo(new[] { "a" }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(a.Get<WsItem>(A)!.Note, Is.EqualTo("by-c"), "the refusal reloaded the record by version");
            Land(a, true, typeof(WsItem), new WsItem { Name = "name-y" }, y);
            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "v", "y" }));
        }

        // A write teaches the writer's cache the version of the bytes it wrote, by flush or by commit, in either store kind: the
        // writer's own Expect holds without a pull, and fails once anyone else writes the record.
        [Test]
        public void AnOwnWriteTeachesTheCacheItsVersion([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("t5.cc");
            Seed(path, directory, "a");
            using var a = Open(path, directory);
            Land(a, commit, typeof(WsItem), new WsItem { Name = "name-k", Note = "by-a" }, K);

            Assert.That(a.Commit(batch =>
            {
                batch.Expect(K, a.Get<WsItem>(K)!);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "again" }, K);
            }), Is.True, "A's own condition holds without a pull");

            var seen = a.Get<WsItem>(K)!;
            using (var b = Open(path, directory))
                b.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-b" }, K));
            Assert.That(a.Commit(batch =>
            {
                batch.Expect(K, seen);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "stale" }, K);
            }), Is.False);
        }

        // A version is the SHA-256 of the bytes as the file holds them, not of a re-encoding. A writer may legally encode a record
        // wider than needed (a str16 for a short string, a bin32, an array16); the file below holds every record that way. Each
        // held record's version is the hash of its slice of the file; a pull over the unchanged file replaces nothing; and a write
        // copies the records it did not stage byte for byte, so they keep their version and the next write is not refused.
        [Test]
        public void AVersionIsTheHashOfTheBytesTheFileHoldsWhateverTheirEncoding()
        {
            var path = PathOf("wide.cc");
            Seed(path, false, "a", "k");
            using (var seed = Open(path, false))
                AddVariant(seed, "v", A);
            File.WriteAllBytes(path, WidenEveryRecord(File.ReadAllBytes(path), out var slices));
            Assert.That(slices["k"], Is.Not.EqualTo(CultDocumentMessagePackSerialization.SerializePersistedRecord(
                Read(path).Records.Single(record => record.Key == "k"))), "the stored bytes are not the canonical encoding of the record");

            using var cache = Open(path, false);
            foreach (var (key, slice) in slices)
                Assert.That(cache.GetStored(new CultRecordKey(key))!.StoredVersion, Is.EqualTo(HexOfSha256(slice)), key);

            var before = cache.GetStored(K);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();
            Assert.That(ReferenceEquals(cache.GetStored(K), before), Is.True, "an unchanged file is not read as changed");

            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(K, cache.Get<WsItem>(K)!);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X);
            }), Is.True);
            var file = File.ReadAllBytes(path);
            foreach (var key in new[] { "a", "v" })
                Assert.That(Contains(file, slices[key]), Is.True, $"{key} was copied as the file held it");
            Land(cache, false, typeof(WsItem), new WsItem { Name = "name-x", Note = "again" }, X);
            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "k", "v", "x" }), "a second write into the variant store is not refused");
        }

        private static string HexOfSha256(byte[] bytes) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

        // The same store with every array, string and byte string written in its widest MessagePack form, and each record's slice.
        private static byte[] WidenEveryRecord(byte[] bytes, out Dictionary<string, byte[]> slices)
        {
            slices = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var output = new List<byte>();
            var reader = new MessagePackReader(bytes);
            var top = reader.ReadArrayHeader();
            output.AddRange(new byte[] { 0xdc, (byte)(top >> 8), (byte)top });
            for (var slot = 0; slot < top; slot++)
            {
                if (slot != 2)
                {
                    var from = (int)reader.Consumed;
                    reader.Skip();
                    output.AddRange(bytes.Skip(from).Take((int)reader.Consumed - from));
                    continue;
                }

                var count = reader.ReadArrayHeader();
                output.AddRange(new byte[] { 0xdc, (byte)(count >> 8), (byte)count });
                for (var index = 0; index < count; index++)
                {
                    var start = output.Count;
                    var fields = reader.ReadArrayHeader();
                    output.AddRange(new byte[] { 0xdc, (byte)(fields >> 8), (byte)fields });
                    string? key = null;
                    for (var field = 0; field < fields; field++)
                    {
                        if (reader.NextMessagePackType == MessagePackType.String)
                        {
                            var text = reader.ReadString()!;
                            key ??= text;
                            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
                            output.AddRange(new byte[] { 0xda, (byte)(utf8.Length >> 8), (byte)utf8.Length });
                            output.AddRange(utf8);
                        }
                        else if (reader.NextMessagePackType == MessagePackType.Binary)
                        {
                            var blob = reader.ReadBytes()!.Value.ToArray();
                            output.AddRange(new byte[] { 0xc6, (byte)(blob.Length >> 24), (byte)(blob.Length >> 16), (byte)(blob.Length >> 8), (byte)blob.Length });
                            output.AddRange(blob);
                        }
                        else
                        {
                            var from = (int)reader.Consumed;
                            reader.Skip();
                            output.AddRange(bytes.Skip(from).Take((int)reader.Consumed - from));
                        }
                    }

                    slices[key!] = output.Skip(start).ToArray();
                }
            }

            return output.ToArray();
        }

        // A condition on a record fails when another writer removed it: the removal stands and the writer does not bring it back.
        [Test]
        public void AnExpectFailsWhenAnotherWriterRemovedTheRecord([Values] bool directory)
        {
            var path = PathOf("gone.cc");
            Seed(path, directory, "k");
            using var a = Open(path, directory);
            var observed = a.Get<WsItem>(K)!;
            using (var b = Open(path, directory))
                Assert.That(b.Commit(batch => batch.Remove(K)), Is.True);

            var committed = a.Commit(batch =>
            {
                batch.Expect(K, observed);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "resurrected" }, K);
            });

            Assert.That(committed, Is.False);
            Assert.That(Keys(path), Is.Empty);
        }

        // The cache's own held record, not only the store's, learns the version of the bytes written. A variant staged and then
        // re-resolved (its base edited before the flush) is held by the cache as a new object the store never saw, so only the
        // cache's own line gives it the version of the bytes it was written as.
        [Test]
        public void AVariantReResolvedBeforeItsFlushIsKnownByItsWrittenVersion()
        {
            var path = PathOf("reresolved.cc");
            Seed(path, false, "a");
            using var cache = Open(path, false);
            var v = new CultRecordKey("v");
            cache.UpsertVariantAsync(v, A, new[] { cache.Override<WsItem>(nameof(WsItem.Name), "name-v") }).GetAwaiter().GetResult();
            var staged = cache.GetStored(v);
            cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-a", Note = "edited" }, A).GetAwaiter().GetResult();
            Assert.That(ReferenceEquals(cache.GetStored(v), staged), Is.False, "the base edit re-resolved the variant into a new held copy");
            cache.FlushAllBackingStores();

            var held = cache.GetStored(v)!;
            Assert.That(held.StoredVersion, Is.EqualTo(HexOfSha256(CultDocumentMessagePackSerialization.SerializePersistedRecord(
                Read(path).Records.Single(record => record.Key == "v")))));
            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(v, cache.Get<WsItem>(v)!);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X);
            }), Is.True, "the writer's own condition on the variant holds");
        }

        private sealed class CountingStore : SingleFileMessagePackBackingStore
        {
            public CountingStore(string path) : base(path) { }

            public int Encoded;

            protected override byte[] SerializeRecord(CultPersistedRecord record)
            {
                Encoded++;
                return base.SerializeRecord(record);
            }
        }

        // A commit with no condition decides nothing about the other records, so it hashes none of them: only the record it writes
        // is encoded to be named. A commit with a condition compares every durable record's version.
        [Test]
        public void AnUnconditionalCommitHashesOnlyTheRecordsItWrites()
        {
            var path = PathOf("cost.cc");
            Seed(path, false, "a", "b", "c", "d", "e");
            var store = new CountingStore(path);
            using var cache = new CultCache(Registry, CultCacheMessagePack.CreateCodec(Registry));
            cache.AddBackingStore(store);
            cache.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            store.Encoded = 0;
            Assert.That(cache.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x" }, X)), Is.True);
            Assert.That(store.Encoded, Is.EqualTo(1));

            store.Encoded = 0;
            Assert.That(cache.Commit(batch =>
            {
                batch.Expect(X, cache.Get<WsItem>(X)!);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-x", Note = "again" }, X);
            }), Is.True);
            Assert.That(store.Encoded, Is.GreaterThanOrEqualTo(6), "a condition hashes every durable record, which is how the count proves itself");
        }

        // Records no store has read or written are told apart however many are made in one clock tick.
        [Test]
        public void UnstoredRecordsMadeBackToBackHaveDistinctVersions()
        {
            using var cache = new CultCache(Registry);
            var keys = Enumerable.Range(0, 2000).Select(index => new CultRecordKey("r" + index)).ToArray();
            foreach (var key in keys)
                cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "same" }, key).GetAwaiter().GetResult();

            Assert.That(keys.Select(key => cache.GetStored(key)!.StoredVersion).Distinct().Count(), Is.EqualTo(keys.Length));
        }

        // A write into a store holding a variant is refused when any record it read has moved, including one the writer is itself
        // writing: it must not overwrite another writer's edit with a copy it read before that edit.
        [Test]
        public void AWriteIntoAVariantStoreIsRefusedWhenARecordItWritesMovedAfterItRead()
        {
            var path = PathOf("own-upsert.cc");
            Seed(path, false, "a");
            using var a = Open(path, false);
            AddVariant(a, "v", A);
            using (var peer = Open(path, false))
                Assert.That(peer.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-a", Note = "by-peer" }, A)), Is.True);
            var before = File.ReadAllBytes(path);

            var conflict = Assert.Throws<CultWriteConflictException>(() =>
                Land(a, true, typeof(WsItem), new WsItem { Name = "name-a", Note = "stale" }, A))!;

            Assert.That(conflict.ChangedKeys, Is.EqualTo(new[] { "a" }));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        // A commit that removes a key this cache staged removes it everywhere: the staged write does not survive the batch.
        [Test]
        public void ACommitThatRemovesAStagedKeyRemovesIt()
        {
            var path = PathOf("staged-removed.cc");
            Seed(path, false, "a");
            var k = new CultRecordKey("k");
            using (var cache = Open(path, false))
            {
                cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-k" }, k).GetAwaiter().GetResult();
                Assert.That(cache.Commit(batch => batch.Remove(k)), Is.True);
                Assert.That(Keys(path), Is.EqualTo(new[] { "a" }));
                Assert.That(cache.Get(k), Is.Null);
            }

            using var reopened = Open(path, false);
            Assert.That(reopened.Get(k), Is.Null);
        }

        private static DateTimeOffset At(string storedAt) => DateTimeOffset.Parse(storedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        private static string StoredAtOf(string path, string key) => Read(path).Records.Single(record => record.Key == key).StoredAt;

        private static void AddVariant(CultCache cache, string key, CultRecordKey baseKey) =>
            cache.Commit(batch => batch.UpsertVariant(new CultRecordKey(key), baseKey, new[]
            {
                cache.Override<WsItem>(nameof(WsItem.Name), "name-" + key),
                cache.Override<WsItem>(nameof(WsItem.Note), "variant-" + key)
            }));

        // The record under a key now belongs to a schema no registered type claims, as if a build that knows more types wrote it.
        private static void MakeForeign(string path, string key)
        {
            const string foreignId = "sha256:00000000000000000000000000000000000000000000000000000000000050de";
            var snapshot = Read(path);
            snapshot.Records.Single(record => record.Key == key).SchemaId = foreignId;
            snapshot.SchemaCatalog = snapshot.SchemaCatalog.Append(new CultSchemaCatalogEntry
            {
                SchemaId = foreignId,
                SchemaName = "tests.write_set_foreign",
                SchemaVersion = "tests.write_set_foreign.v1",
                ContentHash = "foreign",
                CanonicalSchemaJson = "{}"
            }).ToArray();
            Write(path, snapshot);
        }

        // Two writers whose clocks put a record's storedAt ahead of both mint the same storedAt over it. The second to write
        // replaces the first's different bytes, so it is stored later than the first's, and the first's copy is told it moved.
        // The equal storedAt is the case, and the mint is from the stored one, not from the writer's clock (which is behind it). Either store kind.
        [Test]
        public void AWriteOverARecordAtItsOwnStoredAtIsStoredLaterWhateverTheClockSays([Values] bool directory, [Values] bool commit)
        {
            var path = PathOf("skew.cc");
            Seed(path, directory, "k");
            var k = new CultRecordKey("k");
            var ahead = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
            // A single file holds the record at that storedAt. A directory store loads a record from its page, which the manifest
            // does not own, so there both writers are made to hold it ahead: a stage then mints from it, as a skewed clock does.
            if (!directory)
            {
                var snapshot = Read(path);
                snapshot.Records.Single(record => record.Key == "k").StoredAt = ahead;
                Write(path, snapshot);
            }

            using var a = Open(path, directory);
            using var b = Open(path, directory);
            if (directory)
            {
                a.GetStored(k)!.StoredAt = ahead;
                b.GetStored(k)!.StoredAt = ahead;
            }

            if (commit)
                b.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-b" }, k));
            else
            {
                a.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-a" }, k).GetAwaiter().GetResult();
                b.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-b" }, k).GetAwaiter().GetResult();
                b.FlushAllBackingStores();
            }

            var bAt = StoredAtOf(path, "k");
            var seenByB = b.Get<WsItem>(k)!;
            if (!commit)
                Assert.That(a.GetStored(k)!.StoredAt, Is.EqualTo(bAt), "both writers minted the same storedAt over the record");

            if (commit)
                a.Commit(batch => batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "by-a" }, k));
            else
                a.FlushAllBackingStores();

            Assert.That(At(StoredAtOf(path, "k")), Is.GreaterThan(At(bAt)), "A's different bytes are stored later than B's");
            Assert.That(b.Commit(batch =>
            {
                batch.Expect(k, seenByB);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "stale" }, k);
            }), Is.False, "B's copy of k is not what the store holds");
            Assert.That(a.Commit(batch =>
            {
                batch.Expect(k, a.Get<WsItem>(k)!);
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-k", Note = "again" }, k);
            }), Is.True, "A's own condition holds without a pull: the cache took the storedAt the write gave it");
        }

        // Another writer removes a base and its variant together; this cache, still holding both, adds a variant of that base.
        // The base and the variant disappeared from the store, which moved it: the write is refused and the cache reloads.
        [Test]
        public void AVariantWrittenOverABaseAnotherWriterRemovedIsRefusedAndReloaded()
        {
            var path = PathOf("gone-base.cc");
            Seed(path, false, "x");
            using var a = Open(path, false);
            AddVariant(a, "v", X);
            using (var b = Open(path, false))
                Assert.That(b.Commit(batch => { batch.Remove(new CultRecordKey("v")); batch.Remove(X); }), Is.True);

            var conflict = Assert.Throws<CultWriteConflictException>(() => AddVariant(a, "w", X))!;

            Assert.That(conflict.ChangedKeys, Is.EqualTo(new[] { "v", "x" }));
            Assert.That(Keys(path), Is.Empty, "nothing was written");
            Assert.That(a.Get(X), Is.Null, "the cache reloaded what the other writer removed");
            using var fresh = Open(path, false);
            Assert.That(fresh.Get(X), Is.Null);
        }

        // A foreign refusal whose own reload is refused (dropping x, which a held variant is based on) carries that refusal as its cause.
        [Test]
        public void AForeignRefusalWhoseReloadFailsCarriesThatCause([Values] bool commit)
        {
            var path = PathOf("foreign-reload.cc");
            Seed(path, false, "x");
            var a = Open(path, false);
            AddVariant(a, "v", X);
            MakeForeign(path, "x");
            var before = File.ReadAllBytes(path);

            var refusal = Assert.Throws<CultSchemaConflictException>(() =>
                Land(a, commit, typeof(WsItem), new WsItem { Name = "name-x", Note = "edited" }, X))!;

            Assert.That(refusal.RecordKey, Is.EqualTo("x"));
            Assert.That(refusal.InnerException, Is.TypeOf<InvalidOperationException>().And.Message.Contains("variant v is based on it"), "the reload that failed is the cause");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(a.Get<WsItem>(X), Is.Not.Null, "the reload failed, so the cache is unchanged");
        }

        // A write that finds a record it holds gone foreign is refused as a conflict, and when the reload that refusal owes fails
        // (x cannot go while variant v is based on it) the conflict carries that failure, and nothing changes.
        [Test]
        public void AWriteConflictWhoseReloadFailsCarriesThatCause([Values] bool commit)
        {
            var path = PathOf("conflict-reload.cc");
            Seed(path, false, "x");
            var a = Open(path, false);
            AddVariant(a, "v", X);
            MakeForeign(path, "x");
            var before = File.ReadAllBytes(path);

            var conflict = Assert.Throws<CultWriteConflictException>(() =>
                Land(a, commit, typeof(WsItem), new WsItem { Name = "name-y" }, new CultRecordKey("y")))!;

            Assert.That(conflict.InnerException, Is.TypeOf<InvalidOperationException>().And.Message.Contains("variant v is based on it"), "the reload that failed is the cause");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(a.Get<WsItem>(X), Is.Not.Null, "the reload failed, so the cache is unchanged");
        }

        // A refused foreign record is what the store holds, so the next write into a variant store is not told that it moved.
        [Test]
        public void AForeignRecordARefusalReloadedIsNotSeenAsMovedByTheNextWrite([Values] bool commit)
        {
            var path = PathOf("foreign-read.cc");
            Seed(path, false, "a", "x");
            using var a = Open(path, false);
            AddVariant(a, "v", A);
            MakeForeign(path, "x");
            Assert.Throws<CultSchemaConflictException>(() =>
                Land(a, commit, typeof(WsItem), new WsItem { Name = "name-x", Note = "edited" }, X));

            Assert.DoesNotThrow(() => Land(a, commit, typeof(WsItem), new WsItem { Name = "name-n" }, new CultRecordKey("n")));

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "n", "v", "x" }));
        }

        // A writer that finds the store file gone has read nothing there: a write into the store it makes anew is not refused as
        // moved because of what the writer read from the file that was.
        [Test]
        public void AWriterThatFindsTheStoreFileGoneWritesItAnewWithoutBeingToldItMoved()
        {
            var path = PathOf("gone-store.cc");
            Seed(path, false, "a");
            using var a = Open(path, false);
            AddVariant(a, "v", A);
            File.Delete(path);
            a.PullAllBackingStoresAsync().GetAwaiter().GetResult();

            Assert.That(a.Commit(batch =>
            {
                batch.Upsert(typeof(WsItem), new WsItem { Name = "name-a", Note = "again" }, A);
                batch.UpsertVariant(new CultRecordKey("v"), A, new[]
                {
                    a.Override<WsItem>(nameof(WsItem.Name), "name-v"),
                    a.Override<WsItem>(nameof(WsItem.Note), "variant-again")
                });
            }), Is.True);

            Assert.That(Keys(path), Is.EqualTo(new[] { "a", "v" }));
        }
    }
}
