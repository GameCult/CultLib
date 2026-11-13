#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using static GameCult.Caching.Tests.ElementIdTests;

namespace GameCult.Caching.Tests
{
    // A write decides from the file it holds the lock over, not from the file as it was before it waited: whatever another
    // writer left while this one waited for <path>.lock is what the write applies onto, or refuses.
    public class StoreWriteReadsUnderTheLockTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(IdDeck) });
        private static readonly CultSchemaCatalogEntry One = new() { SchemaId = "tests.under-lock.one", SchemaName = "tests.under-lock.one" };
        private string _directory = "";
        private string _path = "";

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cultlib-under-lock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "store.cc");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, true);
        }

        private void Seed()
        {
            using var cache = CultCacheMessagePack.Create(_path, new CultCacheOpenOptions { Registry = Registry });
            cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "seed" }, new CultRecordKey("seed")));
        }

        // The write starts and waits on the held lock; while it waits, the file is replaced by what `swap` makes of it.
        private Exception? WriteWhileTheFileChangesUnderIt(Action write, Action swap)
        {
            using var held = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var task = Task.Run(write);
            Assert.That(task.Wait(TimeSpan.FromMilliseconds(600)), Is.False, "the lock is held, so the write must wait");
            swap();
            held.Dispose();
            try
            {
                Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True);
                return null;
            }
            catch (AggregateException ex)
            {
                return ex.GetBaseException();
            }
        }

        private static void ZeroBytes(string path) => File.WriteAllBytes(path, Array.Empty<byte>());

        private static CultPersistedStoreSnapshot Snapshot(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        private void MarkV3()
        {
            var snapshot = Snapshot(_path);
            snapshot.FormatVersion = CultPersistedStoreSnapshot.FormatV3;
            File.WriteAllBytes(_path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
        }

        private void CommitWrite(Action swap, out Exception? error)
        {
            using var cache = CultCacheMessagePack.Create(_path, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });
            error = WriteWhileTheFileChangesUnderIt(
                () => cache.Commit(batch => batch.Upsert(typeof(IdDeck), new IdDeck { Name = "committed" }, new CultRecordKey("committed"))), swap);
        }

        private void FlushWriteWhile(Action swap, out Exception? error)
        {
            using var cache = CultCacheMessagePack.Create(_path, new CultCacheOpenOptions { Registry = Registry, FlushOnDispose = false, StoreFlushOnDispose = false });
            cache.UpsertAsync(typeof(IdDeck), new IdDeck { Name = "flushed" }, new CultRecordKey("flushed")).GetAwaiter().GetResult();
            error = WriteWhileTheFileChangesUnderIt(() => cache.FlushAllBackingStores(), swap);
        }

        private void CachelessWriteWhile(Action swap, out Exception? error) =>
            error = WriteWhileTheFileChangesUnderIt(
                () => new SingleFileMessagePackBackingStore(_path).WriteRecord(
                    new CultPersistedRecord { Key = "written", SchemaId = One.SchemaId, StoredAt = "2026-10-22T00:00:00.0000000+00:00", Payload = new byte[] { 1 } }, One),
                swap);

        private delegate void WriteWhile(Action swap, out Exception? error);

        [Test]
        public void AFlushDecidesFromTheFileHeldUnderTheLock() => RefusesWhatItFindsUnderTheLock(FlushWriteWhile);

        [Test]
        public void ACommitDecidesFromTheFileHeldUnderTheLock() => RefusesWhatItFindsUnderTheLock(CommitWrite);

        [Test]
        public void ACachelessWriteDecidesFromTheFileHeldUnderTheLock() => RefusesWhatItFindsUnderTheLock(CachelessWriteWhile);

        [Test]
        public void AFlushKeepsTheIdHeaderOfTheFileHeldUnderTheLock() => KeepsTheV3HeaderItFindsUnderTheLock(FlushWriteWhile);

        [Test]
        public void ACommitKeepsTheIdHeaderOfTheFileHeldUnderTheLock() => KeepsTheV3HeaderItFindsUnderTheLock(CommitWrite);

        [Test]
        public void ACachelessWriteKeepsTheIdHeaderOfTheFileHeldUnderTheLock() => KeepsTheV3HeaderItFindsUnderTheLock(CachelessWriteWhile);

        private void RefusesWhatItFindsUnderTheLock(WriteWhile write)
        {
            Seed();

            write(() => ZeroBytes(_path), out var error);

            Assert.That(error, Is.InstanceOf<CultStoreUnreadableException>());
            Assert.That(new FileInfo(_path).Length, Is.Zero, "the refused file was written over");
        }

        private void KeepsTheV3HeaderItFindsUnderTheLock(WriteWhile write)
        {
            Seed();

            write(MarkV3, out var error);

            Assert.That(error, Is.Null, error?.ToString());
            Assert.That(Snapshot(_path).FormatVersion, Is.EqualTo(CultPersistedStoreSnapshot.FormatV3), "the write read the file as it was before the lock");
        }
    }
}
