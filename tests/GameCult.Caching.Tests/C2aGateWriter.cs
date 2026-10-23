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

        private static void Write(string path, bool directory)
        {
            using var cache = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry, UseDirectoryStore = directory });
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

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(from))
                CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
        }

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
            Directory.CreateDirectory(live);
            Write(Path.Combine(live, SingleName), directory: false);
            Write(Path.Combine(live, ManifestName), directory: true);

            var committedSingle = Path.Combine(Vectors(), SingleName);
            var committedManifest = Path.Combine(Vectors(), DirectoryName, ManifestName);
            Assert.That(HeaderOf(committedSingle), Is.EqualTo(HeaderOf(Path.Combine(live, SingleName))));
            Assert.That(HeaderOf(committedManifest), Is.EqualTo(HeaderOf(Path.Combine(live, ManifestName))));

            // The committed bytes are read through a copy: opening a store may take its lock beside it.
            var copy = Path.Combine(_temp, "copy");
            CopyTree(Vectors(), copy);
            Assert.That(ReadIds(Path.Combine(copy, SingleName)), Is.EqualTo(ReadIds(Path.Combine(live, SingleName))));
            Assert.That(ReadIds(Path.Combine(copy, DirectoryName, ManifestName)), Is.EqualTo(Ids));
        }
    }
}
