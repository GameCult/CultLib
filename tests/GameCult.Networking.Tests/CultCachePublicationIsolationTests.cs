#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using GameCult.Caching;
using NUnit.Framework;
using R3;
using static GameCult.Networking.Tests.NetworkingTests;

namespace GameCult.Networking.Tests
{
    [CultDocument("tests.publication_throwing_name", "tests.publication_throwing_name.v1")]
    [MessagePack.MessagePackObject]
    public sealed class PublicationThrowingName
    {
        [MessagePack.Key(0)]
        public string Text { get; set; } = string.Empty;

        [MessagePack.Key(1)]
        [CultName]
        public string Name
        {
            get => Text == "boom" ? throw new InvalidOperationException("name getter boom") : Text;
            set { }
        }
    }

    // Cache publication cuts 1 and 2 seen through CultNetDatabase: a refused or failing change never stalls the
    // publication and log of the changes after it. (Soul pass 3 probes P4 and P3.)
    [NonParallelizable]
    public sealed class CultCachePublicationIsolationTests
    {
        private static NetworkSchemaNote Note(string text) => new() { Schema = "tests.networking_note.v1", Text = text };
        private static string SchemaId => CultDocumentRegistry.Shared.GetRequired<NetworkSchemaNote>().SchemaId;

        // An admission that a [CultName] getter refuses publishes nothing, leaves nothing in memory, and stalls nothing after it.
        [Test]
        public async Task ARefusedAdmissionLeavesNothingAndLaterChangesStillPublishAndLog()
        {
            var cache = new CultCache();
            var database = new CultNetDatabase(cache);
            var seen = new ConcurrentQueue<string?>();
            using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue(change.Document?.Text));
            var okKey = new CultRecordKey("pub:ok");
            var refusal = Assert.Throws<InvalidOperationException>(() => cache.Commit(batch =>
            {
                batch.Upsert(new PublicationThrowingName { Text = "ok" }, new CultRecordHandle<PublicationThrowingName>(okKey));
                batch.Upsert(new PublicationThrowingName { Text = "boom" }, new CultRecordHandle<PublicationThrowingName>(new CultRecordKey("pub:boomname")));
            }))!;

            var after = new CultRecordKey("pub:after");
            await database.PutAsync(after, Note("after"));
            Assert.Multiple(() =>
            {
                Assert.That(refusal.Message, Does.Contain("pub:boomname").And.Contain("name getter boom"));
                Assert.That(cache.Get<PublicationThrowingName>(okKey), Is.Null, "the good record of a refused batch did not land");
                Assert.That(seen, Is.EqualTo(new[] { "after" }));
                Assert.That(database.LastWriteSequence(SchemaId, after), Is.Not.Null, "a later commit was never logged");
            });
        }

        // One observer's escaped exception (a fail-fast R3 handler) does not stop the database observer after it, and the writer gets it.
        [Test]
        public async Task AnEscapedObserverExceptionDoesNotStopTheDatabaseOrLaterChanges()
        {
            var previous = ObservableSystem.GetUnhandledExceptionHandler();
            ObservableSystem.RegisterUnhandledExceptionHandler(exception => throw exception);
            try
            {
                var cache = new CultCache();
                using var thrower = cache.Watch<NetworkSchemaNote>().Subscribe(change =>
                {
                    if (change.Document?.Text == "boom") throw new InvalidOperationException("observer boom");
                });
                var database = new CultNetDatabase(cache);
                var seen = new ConcurrentQueue<string?>();
                using var watch = database.Watch<NetworkSchemaNote>().Subscribe(change => seen.Enqueue(change.Document?.Text));
                var boom = new CultRecordKey("pub:boom");
                var escaped = Assert.ThrowsAsync<InvalidOperationException>(() => cache.UpsertAsync(Note("boom"), new CultRecordHandle<NetworkSchemaNote>(boom)));

                ObservableSystem.RegisterUnhandledExceptionHandler(previous);
                var after = new CultRecordKey("pub:after");
                await database.PutAsync(after, Note("after"));
                Assert.Multiple(() =>
                {
                    Assert.That(escaped!.Message, Is.EqualTo("observer boom"));
                    Assert.That(seen, Is.EqualTo(new[] { "boom", "after" }), "the database observer after the thrower saw both changes");
                    Assert.That(database.LastWriteSequence(SchemaId, after), Is.Not.Null, "a later commit was never logged");
                });
            }
            finally
            {
                ObservableSystem.RegisterUnhandledExceptionHandler(previous);
            }
        }
    }
}
