#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // Cut 3 of docs/cultcache-publication-cut.md: a journal is called under the cache gate, right after an admission is
    // applied, once per admission, with that admission's changes in Sequence order. It is the one place cache order is a fact.
    [NonParallelizable]
    public class CultCacheJournalTests
    {
        private static readonly CultDocumentRegistry Registry = CultDocumentRegistry.ForTypes(new[] { typeof(JournalNote) });

        private string _directory = string.Empty;

        [SetUp]
        public void Prepare()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-journal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            JournalNote.Armed = false;
        }

        [TearDown]
        public void Cleanup()
        {
            JournalNote.Armed = false;
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [CultDocument("tests.journal_note", "tests.journal_note.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class JournalNote
        {
            public static volatile bool Armed;
            [IgnoreMember] private string _name = string.Empty;

            [Key(0)]
            [CultName]
            public string Name
            {
                get
                {
                    if (Armed && _name == "boom")
                        throw new InvalidOperationException("name getter boom");
                    return _name;
                }
                set => _name = value;
            }

            [Key(1)]
            public int Power;
        }

        private static CultRecordKey KeyOf(string text) => new($"journal:{text}");

        private static CultCache Memory() => new(Registry, CultCacheMessagePack.CreateCodec(Registry));

        private static void Put(CultCache cache, string name, int power = 1) =>
            cache.Commit(batch => batch.Upsert(typeof(JournalNote), new JournalNote { Name = name, Power = power }, KeyOf(name)));

        [Test]
        public void EveryAdmissionIsJournaledInSequenceOrderAndItsChangesArriveTogether()
        {
            using var cache = Memory();
            var calls = new List<long[]>();
            using var journal = cache.AddJournal(changes => calls.Add(changes.Select(change => change.Sequence).ToArray()));
            const int threads = 8;
            const int writes = 1000;

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, thread =>
            {
                for (var i = 0; i < writes; i++)
                    cache.Commit(batch =>
                    {
                        batch.Upsert(typeof(JournalNote), new JournalNote { Name = $"{thread}-{i}-a" }, KeyOf($"{thread}-{i}-a"));
                        batch.Upsert(typeof(JournalNote), new JournalNote { Name = $"{thread}-{i}-b" }, KeyOf($"{thread}-{i}-b"));
                    });
            });

            Assert.That(calls, Has.Count.EqualTo(threads * writes), "one call per admission");
            var expected = 1L;
            foreach (var call in calls)
            {
                Assert.That(call, Is.EqualTo(new[] { expected, expected + 1 }), "contiguous, and one call's changes together");
                expected += 2;
            }
        }

        [Test]
        public void AJournalRunsAfterTheAdmissionIsAppliedAndBeforeAnyObserver()
        {
            using var cache = Memory();
            var order = new List<string>();
            using var observer = cache.Watch<JournalNote>().Subscribe(change => order.Add($"observer:{change.Sequence}"));
            var seenInside = new List<string?>();
            using var journal = cache.AddJournal(changes =>
            {
                order.Add($"journal:{changes[0].Sequence}");
                seenInside.Add(cache.Get<JournalNote>(KeyOf("one"))?.Name);
                Assert.That(changes[0].Kind, Is.EqualTo(order.Count == 1 ? CultCacheDocumentChangeKind.Added : CultCacheDocumentChangeKind.Updated));
                Assert.That(((JournalNote?)changes[0].Document)?.Power, Is.EqualTo(order.Count == 1 ? 1 : 2));
                Assert.That(((JournalNote?)changes[0].PreviousDocument)?.Power, Is.EqualTo(order.Count == 1 ? null : 1));
            });

            Put(cache, "one", 1);
            Put(cache, "one", 2);

            Assert.That(order, Is.EqualTo(new[] { "journal:1", "observer:1", "journal:2", "observer:2" }));
            Assert.That(seenInside, Is.EqualTo(new[] { "one", "one" }), "the journal reads the new state");
        }

        [Test]
        public void AJournalIsCalledForAnAdmissionThatChangedSomethingAndNeverForOneThatDidNot()
        {
            using var cache = Memory();
            var calls = 0;
            using var journal = cache.AddJournal(_ => calls++);

            // Attaching a store with nothing in it is an admission with no change.
            cache.AddBackingStore(new SingleFileMessagePackBackingStore(Path.Combine(_directory, "empty.cc")));
            Assert.That(cache.Remove(KeyOf("absent")), Is.False);

            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void JournalsRunInRegistrationOrderAndADisposedJournalStopsRunning()
        {
            using var cache = Memory();
            var order = new List<string>();
            using var first = cache.AddJournal(_ => order.Add("first"));
            var second = cache.AddJournal(_ => order.Add("second"));
            using var third = cache.AddJournal(_ => order.Add("third"));
            Put(cache, "one");

            second.Dispose();
            order.Add("|");
            Put(cache, "two");

            Assert.That(order, Is.EqualTo(new[] { "first", "second", "third", "|", "first", "third" }));
        }

        [Test]
        public void AJournalThatWritesTheCacheThrowsAndTheAdmissionStands()
        {
            using var cache = Memory();
            var seen = new List<long>();
            using var observer = cache.Watch<JournalNote>().Subscribe(change => seen.Add(change.Sequence));
            using var journal = cache.AddJournal(_ => Put(cache, "nested"));

            var thrown = Assert.Throws<InvalidOperationException>(() => Put(cache, "outer"))!;

            Assert.Multiple(() =>
            {
                Assert.That(thrown.Message, Does.Contain("journal"));
                Assert.That(cache.Get<JournalNote>(KeyOf("outer")), Is.Not.Null, "the admission stands");
                Assert.That(cache.Get<JournalNote>(KeyOf("nested")), Is.Null, "the journal's own write did not land");
                Assert.That(seen, Is.EqualTo(new[] { 1L }), "and it was published");
            });
        }

        [Test]
        public void AJournalThatRegistersAnotherJournalThrows()
        {
            using var cache = Memory();
            using var journal = cache.AddJournal(_ => cache.AddJournal(_ => { }));

            var thrown = Assert.Throws<InvalidOperationException>(() => Put(cache, "outer"))!;

            Assert.That(thrown.Message, Does.Contain("journal"));
            Assert.That(cache.Get<JournalNote>(KeyOf("outer")), Is.Not.Null);
        }

        [Test]
        public void TheCacheIsWritableAgainOnceAJournalHasReturned()
        {
            using var cache = Memory();
            using var journal = cache.AddJournal(_ => { });

            Put(cache, "one");
            cache.Commit(batch => batch.Upsert(typeof(JournalNote), new JournalNote { Name = "two" }, KeyOf("two")));
            using var late = cache.AddJournal(_ => { });

            Assert.That(cache.Get<JournalNote>(KeyOf("two")), Is.Not.Null);
        }

        [Test]
        public void ARefusedAdmissionIsNotJournaled()
        {
            using var cache = Memory();
            var calls = 0;
            using var journal = cache.AddJournal(_ => calls++);
            JournalNote.Armed = true;

            Assert.Throws<InvalidOperationException>(() => Put(cache, "boom"));

            JournalNote.Armed = false;
            Assert.That(calls, Is.Zero);
        }

        [Test]
        public void AnAdmissionTheStoreDeclinesIsNotJournaled()
        {
            using var cache = Memory();
            Put(cache, "seed");
            var calls = 0;
            using var journal = cache.AddJournal(_ => calls++);

            var outcome = cache.TryCommit(batch =>
            {
                batch.Expect(KeyOf("seed"), null);
                batch.Upsert(typeof(JournalNote), new JournalNote { Name = "two" }, KeyOf("two"));
            });

            Assert.That(outcome, Is.EqualTo(CultCommitOutcome.Mismatch));
            Assert.That(calls, Is.Zero);
            Assert.That(cache.Get<JournalNote>(KeyOf("two")), Is.Null);
        }

        [Test]
        public void AVariantDependentIsJournaledInTheSameCallAsItsBaseEdit()
        {
            using var cache = Memory();
            Put(cache, "base", 10);
            cache.Commit(batch => batch.UpsertVariant(KeyOf("big"), KeyOf("base"), new[]
            {
                cache.Override<JournalNote>(nameof(JournalNote.Name), "big"),
            }));
            var calls = new List<string[]>();
            using var journal = cache.AddJournal(changes => calls.Add(changes.Select(change => change.Key.Value).ToArray()));

            Put(cache, "base", 20);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Is.EquivalentTo(new[] { KeyOf("base").Value, KeyOf("big").Value }));
        }

        [Test]
        public void AThrowingJournalDoesNotStopTheOthersOrTheAdmissionAndTheWriterHearsOfItAfterPublication()
        {
            using var cache = Memory();
            var order = new List<string>();
            using var observer = cache.Watch<JournalNote>().Subscribe(_ => order.Add("observer"));
            using var a = cache.AddJournal(_ =>
            {
                order.Add("a");
                throw new InvalidOperationException("a");
            });
            using var b = cache.AddJournal(_ => order.Add("b"));
            using var c = cache.AddJournal(_ =>
            {
                order.Add("c");
                throw new InvalidOperationException("c");
            });

            var thrown = Assert.Throws<AggregateException>(() => Put(cache, "one"))!;

            Assert.Multiple(() =>
            {
                Assert.That(thrown.InnerExceptions.Select(exception => exception.Message), Is.EqualTo(new[] { "a", "c" }));
                Assert.That(order, Is.EqualTo(new[] { "a", "b", "c", "observer" }), "every journal ran, then the change published");
                Assert.That(cache.Get<JournalNote>(KeyOf("one")), Is.Not.Null, "the admission stands");
            });

            using var quiet = cache.Watch<JournalNote>().Subscribe(_ => { });
            Assert.Throws<AggregateException>(() => Put(cache, "two"), "a throwing journal stays registered");
        }

        [Test]
        public void AJournalThatThrowsDuringALoadLeavesTheStoreAttachedAndTheLoadAdopted()
        {
            var path = Path.Combine(_directory, "notes.cc");
            using (var writer = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = Registry }))
                Put(writer, "stored");

            var cache = Memory();
            var seen = new List<long>();
            using var observer = cache.Watch<JournalNote>().Subscribe(change => seen.Add(change.Sequence));
            using var journal = cache.AddJournal(_ => throw new InvalidOperationException("journal boom"));

            var thrown = Assert.Throws<InvalidOperationException>(() => cache.AddBackingStore(new SingleFileMessagePackBackingStore(path)))!;

            Assert.Multiple(() =>
            {
                Assert.That(thrown.Message, Is.EqualTo("journal boom"));
                Assert.That(cache.BackingStores, Has.Count.EqualTo(1), "the store stays attached: it and the cache agree");
                Assert.That(cache.Get<JournalNote>(KeyOf("stored")), Is.Not.Null);
                Assert.That(seen, Is.EqualTo(new[] { 1L }));
            });
        }
    }
}
