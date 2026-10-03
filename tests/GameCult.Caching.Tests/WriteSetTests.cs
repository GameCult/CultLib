#nullable enable
using System;
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

        // The condition is the only difference between a conditional and an unconditional commit: one staged set, one apply.
        [Test]
        public void AConditionalAndAnUnconditionalCommitOfOneStagedSetOntoOneStoreWriteTheSameBytes()
        {
            var first = PathOf("unconditional.cc");
            var second = PathOf("conditional.cc");
            Seed(first, false, "a", "x");
            File.Copy(first, second);

            foreach (var (path, conditional) in new[] { (first, false), (second, true) })
            {
                using var cache = Open(path, false);
                cache.UpsertAsync(typeof(WsItem), new WsItem { Name = "name-s", Note = "staged" }, new CultRecordKey("s")).GetAwaiter().GetResult();
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
                foreach (var record in snapshot.Records.Where(record => record.Key is "s" or "z"))
                    record.StoredAt = string.Empty;
                return CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot);
            }

            Assert.That(Keys(first), Is.EqualTo(new[] { "a", "s", "z" }));
            Assert.That(Normalized(second), Is.EqualTo(Normalized(first)));
        }

        private static void MarkAs(string path, string header)
        {
            var snapshot = Read(path);
            snapshot.FormatVersion = header;
            Write(path, snapshot);
        }

        // A marked header says the store holds an element id. A writer that copies only records it knows hold none leaves it
        // unmarked (marked-by-content); one that copies a record it cannot show holds none keeps the mark.
        [Test]
        public void AMarkedHeaderIsDroppedOnlyWhenEveryCopiedRecordIsKnownToHoldNoId([Values] bool anotherWriterAddedARecord)
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

            Assert.That(Read(path).FormatVersion, Is.EqualTo(anotherWriterAddedARecord ? CultPersistedStoreSnapshot.FormatV3 : CultPersistedStoreSnapshot.FormatV1));
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
    }
}
