#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Caching.Tests
{
    // CultCache.Update: read the content version, apply a pure function to a private copy, commit conditionally, re-run on a lost
    // race (variants:ruling:write-set-content-version-declared-ownership; variants:question:update-apply-copy).
    public class UpdateTests
    {
        private static readonly CultRecordKey Key = new("counter");
        private static readonly CultDocumentRegistry Registry =
            CultDocumentRegistry.ForTypes(new[] { typeof(UpdateCounter), typeof(UpdateTally), typeof(UpdateGear) });

        private string _directory = string.Empty;

        [SetUp]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), $"cultlib-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private string PathOf(string name) => Path.Combine(_directory, name);

        private static CultCache Open(string path, bool directory, bool codec = true)
        {
            var cache = codec ? new CultCache(Registry, CultCacheMessagePack.CreateCodec(Registry)) : new CultCache(Registry);
            cache.AddBackingStore(directory ? new DirectoryMessagePackBackingStore(path) : new SingleFileMessagePackBackingStore(path));
            return cache;
        }

        private static void Seed(string path, bool directory, int value)
        {
            using var cache = Open(path, directory);
            Assert.That(cache.Commit(batch => batch.Upsert(new UpdateCounter { Name = "counter", Value = value }, new CultRecordHandle<UpdateCounter>(Key))), Is.True);
        }

        private static void Rival(CultCache cache, int value) =>
            Assert.That(cache.Commit(batch => batch.Upsert(new UpdateCounter { Name = "counter", Value = value }, new CultRecordHandle<UpdateCounter>(Key))), Is.True);

        private static int StoredValue(string path, bool directory)
        {
            using var reader = Open(path, directory);
            return reader.Get<UpdateCounter>(Key)!.Value;
        }

        private static UpdateCounter Plus(UpdateCounter? counter, int by)
        {
            counter ??= new UpdateCounter { Name = "counter" };
            counter.Value += by;
            return counter;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TwoWritersUpdatingLoseNoIncrement(bool directory)
        {
            const int n = 100;
            var path = PathOf("race.cc");
            Seed(path, directory, 0);
            using var a = Open(path, directory);
            using var b = Open(path, directory);
            var applied = 0;

            var racers = new[] { a, b }.Select(cache => Task.Run(() =>
            {
                var results = new List<CultUpdateOutcome>();
                for (var i = 0; i < n; i++)
                    results.Add(cache.Update<UpdateCounter>(Key, counter =>
                    {
                        Interlocked.Increment(ref applied);
                        return Plus(counter, 1);
                    }, maxAttempts: 200).Outcome);
                return results;
            })).ToArray();
            Assert.That(Task.WaitAll(racers, TimeSpan.FromMinutes(2)), Is.True, "the racers did not finish");

            Assert.That(racers.SelectMany(racer => racer.Result), Is.All.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(StoredValue(path, directory), Is.EqualTo(2 * n));
            Assert.That(applied, Is.GreaterThan(2 * n), "the writers never actually raced");
        }

        [Test]
        public void LostAttemptRerunsApplyOnTheFreshValue()
        {
            var path = PathOf("rerun.cc");
            Seed(path, directory: false, 1);
            using var cache = Open(path, false);
            using var rival = Open(path, false);
            var seen = new List<int>();

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                seen.Add(counter!.Value);
                if (seen.Count == 1)
                    Rival(rival, 100);
                return Plus(counter, 1);
            });

            Assert.That(seen, Is.EqualTo(new[] { 1, 100 }));
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(result.Document!.Value, Is.EqualTo(101));
            Assert.That(StoredValue(path, false), Is.EqualTo(101));
        }

        [Test]
        public void ExhaustedAttemptsReturnLostRace()
        {
            var path = PathOf("lost.cc");
            Seed(path, false, 1);
            using var cache = Open(path, false);
            using var rival = Open(path, false);
            var calls = 0;

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                Rival(rival, 100 * ++calls);
                return Plus(counter, 1);
            }, maxAttempts: 3);

            Assert.That(calls, Is.EqualTo(3));
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.LostRace));
            Assert.That(result.Document!.Value, Is.EqualTo(300));
            Assert.That(StoredValue(path, false), Is.EqualTo(300), "the store holds the rival's last write");
        }

        [Test]
        public void ApplyGetsAPrivateCopy()
        {
            var path = PathOf("copy.cc");
            Seed(path, false, 1);
            using var cache = Open(path, false);
            using var rival = Open(path, false);
            var published = new List<int>();
            using var subscription = cache.Watch<UpdateCounter>().Subscribe(change => published.Add(change.Document!.Value));
            var arguments = new List<int>();
            var calls = 0;

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                calls++;
                arguments.Add(counter!.Value);
                Assert.That(counter, Is.Not.SameAs(cache.Get<UpdateCounter>(Key)), "apply was handed the held instance");
                counter.Value += 1000;
                Assert.That(cache.Get<UpdateCounter>(Key)!.Value, Is.Not.EqualTo(counter.Value), "the change reached the cache before any commit");
                if (calls == 1)
                    Rival(rival, 5);
                return counter;
            });

            Assert.That(arguments, Is.EqualTo(new[] { 1, 5 }), "the second call saw the first call's change");
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(published, Does.Not.Contain(1001), "an uncommitted value was published");
            Assert.That(published.Count(value => value == 1005), Is.EqualTo(1));
            Assert.That(StoredValue(path, false), Is.EqualTo(1005));
        }

        [Test]
        public void NullFromApplyDeclines()
        {
            var path = PathOf("decline.cc");
            Seed(path, false, 7);
            var before = File.ReadAllBytes(path);
            using var cache = Open(path, false);
            var changes = 0;
            using var subscription = cache.Watch<UpdateCounter>().Subscribe(_ => changes++);

            var result = cache.Update<UpdateCounter>(Key, _ => null);

            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Declined));
            Assert.That(result.Document!.Value, Is.EqualTo(7));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(changes, Is.EqualTo(0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AbsentKeyCreatesOnceUnderRace(bool directory)
        {
            var path = PathOf("create.cc");
            using var a = Open(path, directory);
            using var b = Open(path, directory);
            var started = new Barrier(2);

            var racers = new[] { a, b }.Select(cache => Task.Run(() =>
            {
                started.SignalAndWait();
                return cache.Update<UpdateCounter>(Key, counter => Plus(counter, 1)).Outcome;
            })).ToArray();
            Assert.That(Task.WaitAll(racers, TimeSpan.FromMinutes(1)), Is.True);

            Assert.That(racers.Select(racer => racer.Result), Is.All.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(StoredValue(path, directory), Is.EqualTo(2));
        }

        [Test]
        public void RefusalsAreNotRetried()
        {
            var path = PathOf("refuse.cc");
            Seed(path, false, 1);
            var before = File.ReadAllBytes(path);
            var calls = 0;
            Func<UpdateCounter?, UpdateCounter?> apply = counter => { calls++; return Plus(counter, 1); };

            using (var codecLess = Open(path, false, codec: false))
                Assert.That(() => codecLess.Update(Key, apply), Throws.InvalidOperationException);
            using (var cache = Open(path, false))
            {
                Assert.That(() => cache.Update(Key, apply, maxAttempts: 0), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => cache.Update<UpdateCounter>(Key, null!), Throws.TypeOf<ArgumentNullException>());
                Assert.That(() => cache.Update<UpdateTally>(Key, _ => new UpdateTally { Name = "counter" }), Throws.InvalidOperationException);
            }

            using (var cache = Open(path, false))
            {
                cache.Commit(batch =>
                {
                    batch.Upsert(new UpdateGear { Name = "gear", Power = 1 }, new CultRecordHandle<UpdateGear>(new CultRecordKey("gear")));
                });
                cache.Commit(batch => batch.UpsertVariant(new CultRecordKey("gear-big"), new CultRecordKey("gear"),
                    new[] { cache.Override<UpdateGear>(nameof(UpdateGear.Name), "gear big"), cache.Override<UpdateGear>(nameof(UpdateGear.Power), 9) }));
            }

            before = File.ReadAllBytes(path);
            var gearCalls = 0;
            using (var cache = Open(path, false))
                Assert.That(() => cache.Update<UpdateGear>(new CultRecordKey("gear-big"), gear => { gearCalls++; return gear; }), Throws.InvalidOperationException);
            Assert.That(gearCalls, Is.EqualTo(0), "apply ran for a variant");
            Assert.That(calls, Is.EqualTo(0), "apply ran for a refused call");
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));

            using (var cache = Open(path, false))
            {
                cache.UpsertAsync(new UpdateTally { Name = "staged" }).GetAwaiter().GetResult();
                Assert.That(() => cache.Update(Key, apply), Throws.InvalidOperationException.With.Message.Contains("flush before a conditional commit"));
                Assert.That(calls, Is.LessThanOrEqualTo(1));
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "a refusal wrote");
        }

        [Test]
        public void VariantStoreConflictIsALostAttempt()
        {
            var path = PathOf("variant.cc");
            Seed(path, false, 1);
            using (var seed = Open(path, false))
            {
                seed.Commit(batch => batch.Upsert(new UpdateGear { Name = "gear", Power = 1 }, new CultRecordHandle<UpdateGear>(new CultRecordKey("gear"))));
                seed.Commit(batch => batch.UpsertVariant(new CultRecordKey("gear-big"), new CultRecordKey("gear"),
                    new[] { seed.Override<UpdateGear>(nameof(UpdateGear.Name), "gear big"), seed.Override<UpdateGear>(nameof(UpdateGear.Power), 9) }));
            }

            using var cache = Open(path, false);
            using (var rival = Open(path, false))
                Assert.That(rival.Commit(batch => batch.Upsert(new UpdateTally { Name = "other" }, new CultRecordHandle<UpdateTally>(new CultRecordKey("other")))), Is.True);
            var calls = 0;

            var result = cache.Update<UpdateCounter>(Key, counter => { calls++; return Plus(counter, 1); });

            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(calls, Is.EqualTo(2), "the conflict was not a lost attempt that re-ran apply");
            Assert.That(StoredValue(path, false), Is.EqualTo(2));
        }

        private static CultPersistedStoreSnapshot ReadSnapshot(string path) =>
            CultDocumentMessagePackSerialization.DeserializeSnapshot(File.ReadAllBytes(path));

        // A reload that fails inside a conflict is a read failure, not a lost race: Update lets it propagate and never re-runs apply.
        // Here the gear a variant is based on has gone foreign in the file, so the reload the conflict owes cannot drop it.
        [Test]
        public void ConflictWhoseReloadFailedPropagates()
        {
            var path = PathOf("reload-failed.cc");
            Seed(path, false, 1);
            using var cache = Open(path, false);
            cache.Commit(batch => batch.Upsert(new UpdateGear { Name = "gear", Power = 1 }, new CultRecordHandle<UpdateGear>(new CultRecordKey("gear"))));
            cache.Commit(batch => batch.UpsertVariant(new CultRecordKey("gear-big"), new CultRecordKey("gear"),
                new[] { cache.Override<UpdateGear>(nameof(UpdateGear.Name), "gear big"), cache.Override<UpdateGear>(nameof(UpdateGear.Power), 9) }));
            const string foreignId = "sha256:00000000000000000000000000000000000000000000000000000000000050de";
            var snapshot = ReadSnapshot(path);
            snapshot.Records.Single(record => record.Key == "gear").SchemaId = foreignId;
            snapshot.SchemaCatalog = snapshot.SchemaCatalog.Append(new CultSchemaCatalogEntry
            {
                SchemaId = foreignId, SchemaName = "tests.update_foreign", SchemaVersion = "tests.update_foreign.v1",
                ContentHash = "foreign", CanonicalSchemaJson = "{}"
            }).ToArray();
            File.WriteAllBytes(path, CultDocumentMessagePackSerialization.SerializeSnapshot(snapshot));
            var calls = 0;

            var conflict = Assert.Throws<CultWriteConflictException>(() => cache.Update<UpdateCounter>(Key, counter => { calls++; return Plus(counter, 1); }))!;

            Assert.That(conflict.InnerException, Is.Not.Null, "the reload that failed is the cause");
            Assert.That(calls, Is.EqualTo(1), "a failed reload was retried");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RivalWritingAndRestoringTheValueStillRerunsApply(bool directory)
        {
            var path = PathOf("aba.cc");
            Seed(path, directory, 1);
            using var cache = Open(path, directory);
            using var rival = Open(path, directory);
            var seen = new List<int>();

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                seen.Add(counter!.Value);
                if (seen.Count == 1)
                {
                    Rival(rival, 5);
                    Rival(rival, 1);
                }
                return Plus(counter, 1);
            });

            Assert.That(seen, Is.EqualTo(new[] { 1, 1 }), "the content version missed A-B-A");
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(StoredValue(path, directory), Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RivalRemovalIsSeenAsNull(bool directory)
        {
            var path = PathOf("removal.cc");
            Seed(path, directory, 1);
            using var cache = Open(path, directory);
            using var rival = Open(path, directory);
            var seen = new List<int?>();

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                seen.Add(counter?.Value);
                if (seen.Count == 1)
                    rival.Commit(batch => batch.Remove(Key));
                return Plus(counter, 1);
            });

            Assert.That(seen, Is.EqualTo(new int?[] { 1, null }));
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            Assert.That(StoredValue(path, directory), Is.EqualTo(1));
        }

        [Test]
        public void ExhaustedAttemptsReturnLostRaceInADirectoryStore()
        {
            var path = PathOf("lost-dir.cc");
            Seed(path, true, 1);
            using var cache = Open(path, true);
            using var rival = Open(path, true);
            var calls = 0;

            var result = cache.Update<UpdateCounter>(Key, counter =>
            {
                Rival(rival, 100 * ++calls);
                return Plus(counter, 1);
            }, maxAttempts: 2);

            Assert.That(calls, Is.EqualTo(2));
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.LostRace));
            Assert.That(result.Document!.Value, Is.EqualTo(200));
            Assert.That(StoredValue(path, true), Is.EqualTo(200));
        }

        [Test]
        public void OneAttemptMeansOneApplyAndNonPositiveThrows()
        {
            var path = PathOf("one.cc");
            Seed(path, false, 1);
            using var cache = Open(path, false);
            using var rival = Open(path, false);
            var calls = 0;

            var result = cache.Update<UpdateCounter>(Key, counter => { calls++; Rival(rival, 50); return Plus(counter, 1); }, maxAttempts: 1);

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Outcome, Is.EqualTo(CultUpdateOutcome.LostRace));
            Assert.That(() => cache.Update<UpdateCounter>(Key, counter => counter, maxAttempts: -1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UpdatedDocumentIsTheHeldInstanceAndOwnWritesCauseNoRetry(bool directory)
        {
            var path = PathOf("own.cc");
            Seed(path, directory, 1);
            using var cache = Open(path, directory);
            cache.UpsertAsync(new UpdateCounter { Name = "counter", Value = 10 }, new CultRecordHandle<UpdateCounter>(Key)).GetAwaiter().GetResult();
            cache.FlushAsync().GetAwaiter().GetResult();
            var calls = 0;

            var first = cache.Update<UpdateCounter>(Key, counter => { calls++; return Plus(counter, 1); });
            var second = cache.Update<UpdateCounter>(Key, counter => { calls++; return Plus(counter, 1); });

            Assert.That(calls, Is.EqualTo(2), "the cache's own writes made a later Update retry");
            Assert.That(first.Document, Is.Not.Null);
            Assert.That(second.Document, Is.SameAs(cache.Get<UpdateCounter>(Key)), "Updated returned a copy, not the held instance");
            Assert.That(StoredValue(path, directory), Is.EqualTo(12));
        }

        [Test]
        public void ApplyRunsOutsideTheCacheGate()
        {
            var path = PathOf("gate.cc");
            Seed(path, false, 1);
            using var cache = Open(path, false);
            var reached = false;

            cache.Update<UpdateCounter>(Key, counter =>
            {
                reached = Task.Run(() => cache.Get<UpdateCounter>(Key)).Wait(TimeSpan.FromSeconds(10));
                return Plus(counter, 1);
            });

            Assert.That(reached, Is.True, "another thread could not read the cache while apply ran");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ContendedLedgerStoredValueEqualsUpdatedCount(bool directory)
        {
            var path = PathOf("ledger.cc");
            Seed(path, directory, 0);
            var caches = Enumerable.Range(0, 3).Select(_ => Open(path, directory)).ToArray();
            try
            {
                var tasks = caches.Select(cache => Task.Run(() =>
                {
                    var updated = 0;
                    for (var i = 0; i < 25; i++)
                        if (cache.Update<UpdateCounter>(Key, counter => Plus(counter, 1)).Outcome == CultUpdateOutcome.Updated)
                            updated++;
                    return updated;
                })).ToArray();
                Assert.That(Task.WaitAll(tasks, TimeSpan.FromMinutes(2)), Is.True);

                Assert.That(StoredValue(path, directory), Is.EqualTo(tasks.Sum(task => task.Result)), "an Updated outcome was lost or a LostRace wrote");
            }
            finally
            {
                foreach (var cache in caches)
                    cache.Dispose();
            }
        }

        [Test]
        public void InMemoryCacheUpdatesUnderThreadRace()
        {
            using var cache = new CultCache(Registry, CultCacheMessagePack.CreateCodec(Registry));
            cache.Commit(batch => batch.Upsert(new UpdateCounter { Name = "counter" }, new CultRecordHandle<UpdateCounter>(Key)));

            var threads = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                    Assert.That(cache.Update<UpdateCounter>(Key, counter => Plus(counter, 1), maxAttempts: 200).Outcome, Is.EqualTo(CultUpdateOutcome.Updated));
            })).ToArray();
            Assert.That(Task.WaitAll(threads, TimeSpan.FromMinutes(1)), Is.True);

            Assert.That(cache.Get<UpdateCounter>(Key)!.Value, Is.EqualTo(200));
        }

        [CultDocument("tests.update_counter", "tests.update_counter.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class UpdateCounter
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Value;
        }

        [CultDocument("tests.update_tally", "tests.update_tally.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class UpdateTally
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;
        }

        [CultDocument("tests.update_gear", "tests.update_gear.v1")]
        [MessagePackObject(AllowPrivate = true)]
        internal sealed class UpdateGear
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;
        }
    }
}
