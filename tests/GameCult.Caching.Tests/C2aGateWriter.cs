#nullable enable
using System;
using System.IO;
using System.Linq;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    // The writer half of the C2a format gate. It writes a record that holds element ids through CultCacheMessagePack.Create, once
    // as a single-file store and once as a directory store, and asserts the headers C2a stamps. old-reader-refusal.sh gate runs it
    // with CULTLIB_GATE_OUT set, so the 2.0.1 reader probes the very bytes this tree writes; the committed vectors are one such run.
    public class C2aGateWriter
    {
        private const string SingleName = "c2a-written-ids.msgpack";
        private const string DirectoryName = "c2a-written-ids-directory";
        private const string ManifestName = "store.cc";
        private const string DirectoryHeader = "cultcache.store.v5.directory-content-addressed-pages";

        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(ElementIdTests.IdDeck) });

        // Every id is the author's, so the written bytes and the decoded ids do not depend on minting.
        private static readonly string[] Ids = { "00000000c2a1", "00000000c2a2", "00000000c2a3", "00000000c2a4" };

        private static ElementIdTests.IdDeck Deck() => new()
        {
            Name = "gate",
            Reels =
            {
                new ElementIdTests.IdReel
                {
                    Label = "reel",
                    Id = Ids[0],
                    Marks = { new ElementIdTests.IdMark { Text = "m", Id = Ids[1] }, new ElementIdTests.IdMark { Text = "m", Id = Ids[2] } }
                }
            },
            Moves = { new ElementIdTests.IdSlash { Reach = 1, Id = Ids[3] } }
        };

        private static string[] IdsOf(ElementIdTests.IdDeck deck) =>
            deck.Reels.Select(reel => reel.Id)
                .Concat(deck.Reels.SelectMany(reel => reel.Marks.Select(mark => mark.Id)))
                .Concat(deck.Moves.Select(move => move.Id))
                .ToArray();

        // The one instant every record is stamped with, so the bytes this writes are the same on every run and the committed vectors
        // can be compared to them byte for byte. Regenerate the vectors with CULTLIB_GATE_OUT (see C2aStamps...) if C2a's format moves.
        private static readonly DateTimeOffset StoredAt = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

        private static void Write(string path, bool directory)
        {
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory });
            cache.Clock = () => StoredAt;
            cache.Commit(batch => batch.Upsert(typeof(ElementIdTests.IdDeck), Deck(), new CultRecordKey("gate")));
        }

        private static string HeaderOf(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path)).FormatVersion;

        private static string[] ReadIds(string path)
        {
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry });
            return IdsOf(cache.Get<ElementIdTests.IdDeck>(new CultRecordKey("gate"))!);
        }

        private static string Vectors()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var vectors = Path.Combine(dir.FullName, "tests", "vectors", "document-variants-c2a");
                if (Directory.Exists(vectors)) return vectors;
            }

            throw new DirectoryNotFoundException($"tests/vectors/document-variants-c2a not found above {TestContext.CurrentContext.TestDirectory}.");
        }

        // Every file under root by relative path and bytes; a store's lock files hold no state and are not compared.
        private static (string Path, byte[] Bytes)[] Tree(string root) =>
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => !file.EndsWith(".lock", StringComparison.Ordinal))
                .Select(file => (Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(file)))
                .OrderBy(entry => entry.Item1, StringComparer.Ordinal)
                .ToArray();

        private string _temp = string.Empty;

        [SetUp]
        public void CreateTemp()
        {
            _temp = Path.Combine(Path.GetTempPath(), $"cultlib-c2a-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void DeleteTemp()
        {
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, recursive: true);
        }

        [Test]
        public void C2aStampsV3OnASingleFileStoreAndV5OnADirectoryStoreThatHoldIds()
        {
            var output = Environment.GetEnvironmentVariable("CULTLIB_GATE_OUT");
            var root = string.IsNullOrEmpty(output) ? _temp : output;
            Directory.CreateDirectory(root);
            var single = Path.Combine(root, SingleName);
            var directory = Path.Combine(root, DirectoryName);
            File.Delete(single);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);

            Write(single, directory: false);
            Write(Path.Combine(directory, ManifestName), directory: true);

            Assert.That(HeaderOf(single), Is.EqualTo(CultPersistedStoreSnapshot.FormatV3));
            Assert.That(HeaderOf(Path.Combine(directory, ManifestName)), Is.EqualTo(DirectoryHeader));
            Assert.That(ReadIds(single), Is.EqualTo(Ids));
        }

        [Test]
        public void TheCommittedVectorsAreWhatC2aWrites()
        {
            var live = Path.Combine(_temp, "live");
            var liveDirectory = Path.Combine(live, DirectoryName);
            Directory.CreateDirectory(liveDirectory);
            Write(Path.Combine(live, SingleName), directory: false);
            Write(Path.Combine(liveDirectory, ManifestName), directory: true);

            Assert.That(File.ReadAllBytes(Path.Combine(Vectors(), SingleName)), Is.EqualTo(File.ReadAllBytes(Path.Combine(live, SingleName))), SingleName);

            var committed = Tree(Path.Combine(Vectors(), DirectoryName));
            var written = Tree(liveDirectory);
            Assert.That(committed.Select(entry => entry.Path), Is.EqualTo(written.Select(entry => entry.Path)), DirectoryName);
            for (var i = 0; i < committed.Length; i++)
                Assert.That(committed[i].Bytes, Is.EqualTo(written[i].Bytes), committed[i].Path);
        }
    }
}
