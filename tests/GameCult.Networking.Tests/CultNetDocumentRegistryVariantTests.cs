#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // Q6 (docs/document-variants-cut.md C1): CultNet carries no variant deltas yet, so building a row for one is refused
    // loudly, on the v1 selection path and on the v0 snapshot path alike.
    public sealed class CultNetDocumentRegistryVariantTests
    {
        private static readonly CultRecordKey BaseKey = new("variant-fixture-base");
        private static readonly CultRecordKey VariantKey = new("variant-fixture-big");

        private static async Task<(CultCache Cache, CultNetDocumentRegistry Documents)> BuildFixtureAsync(bool withVariant)
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry));
            await cache.UpsertAsync(new VariantWireFixture { Name = "base", Power = 1 }, new CultRecordHandle<VariantWireFixture>(BaseKey));
            if (withVariant)
                await cache.UpsertVariantAsync(VariantKey, BaseKey, new[]
                {
                    cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big"),
                    cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Power), 9)
                });
            return (cache, documents);
        }

        [Test]
        public async Task SelectionResponseRefusesAVariantRowNamingIt()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);
            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };

            var error = Assert.Throws<NotSupportedException>(() =>
                documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1))!;

            Assert.That(error.Message, Does.Contain(VariantKey.Value).And.Contain(BaseKey.Value));
        }

        [Test]
        public async Task RawSnapshotResponseRefusesAVariantRowNamingIt()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);

            var error = Assert.Throws<NotSupportedException>(() => documents.CreateRawSnapshotResponse(cache, "m1"))!;

            Assert.That(error.Message, Does.Contain(VariantKey.Value));
        }

        [Test]
        public async Task ACacheWithoutVariantsStillAnswersBothPaths()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: false);
            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };

            Assert.That(documents.CreateSelectionResponse(cache, "m1", selection, ordinalOf: (_, _) => 1, asOf: 1).Documents, Has.Length.EqualTo(1));
            Assert.That(documents.CreateRawSnapshotResponse(cache, "m2").Documents, Has.Length.EqualTo(1));
        }

        // The live-change funnel: a load that lands a variant reaches a subscriber as neither a record nor silence.
        [Test]
        public async Task ALoadThatLandsAVariantNeverReachesASubscriberAsARecordAndTheSubscriberIsToldByKey()
        {
            var registry = CultDocumentRegistry.Shared;
            var directory = Path.Combine(Path.GetTempPath(), $"cultlib-variant-live-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "wire.cc");
                using (var writer = CultCacheMessagePack.Create(path, new CultCacheOpenOptions { Registry = registry }))
                {
                    writer.Commit(batch => batch.Upsert(typeof(VariantWireFixture), new VariantWireFixture { Name = "base", Power = 1 }, BaseKey));
                    writer.Commit(batch => batch.UpsertVariant(VariantKey, BaseKey, new[]
                    {
                        writer.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big"),
                        writer.Override<VariantWireFixture>(nameof(VariantWireFixture.Power), 9)
                    }));
                }

                var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
                var documents = new CultNetDocumentRegistry(registry)
                    .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry));
                var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.ReceiveTimeout = 20;
                using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions
                {
                    RuntimeId = "variant-live-server",
                    Socket = socket
                });
                using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
                using var cancellation = new CancellationTokenSource();
                var serverThread = new Thread(() =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        _ = server.PollOnceAsync().GetAwaiter().GetResult();
                        Thread.Sleep(1);
                    }
                }) { IsBackground = true };
                serverThread.Start();
                try
                {
                    using var client = CultNetSchemaClients.CreateRudp("variant-live-client");
                    var snapshot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var changes = new ConcurrentQueue<CultNetDatabaseChangeRawMessage>();
                    var errors = new ConcurrentQueue<CultNetErrorMessage>();
                    client.OnCultNet<CultNetSnapshotResponseRawMessage>(_ => snapshot.TrySetResult(true));
                    client.OnCultNet<CultNetDatabaseChangeRawMessage>(changes.Enqueue);
                    client.OnCultNet<CultNetErrorMessage>(errors.Enqueue);
                    client.Connect("127.0.0.1", server.LocalEndPoint.Port);
                    await WaitUntilAsync(() => client.Connected);
                    client.SendCultNet(new CultNetDatabaseSubscribeMessage
                    {
                        MessageId = "subscribe-all",
                        SubscriptionId = "all",
                        RecordKeys = new[] { BaseKey.Value, VariantKey.Value },
                        IncludeSnapshot = true
                    });
                    await WaitUntilAsync(() => snapshot.Task.IsCompleted);

                    cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));

                    await WaitUntilAsync(() => !errors.IsEmpty || changes.Any(change => change.Document?.RecordKey == VariantKey.Value),
                        () => $"changes={string.Join(",", changes.Select(c => c.ChangeKind + ":" + c.Document?.RecordKey))} errors={string.Join(",", errors.Select(e => e.Error))} cacheKeys={cache.GetStored(BaseKey) != null}/{cache.GetStored(VariantKey) != null}");
                    await Task.Delay(200); // let anything else already in flight arrive

                    Assert.That(changes.Select(change => change.Document?.RecordKey), Does.Not.Contain(VariantKey.Value),
                        "a variant must not reach a subscriber as a plain record");
                    Assert.That(errors.Select(error => error.Error), Has.Some.Contains(VariantKey.Value),
                        "the subscriber is told, naming the variant");
                }
                finally
                {
                    cancellation.Cancel();
                    serverThread.Join();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public async Task TheDatabaseServersLiveChangeForAVariantIsRefusedNamingIt()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
            using var server = new Server(cache, ServerSecurityOptions.Development());
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var schemaId = cache.Registry.GetRequired<VariantWireFixture>().SchemaId;
            var change = new CultNetDatabaseChange<VariantWireFixture>(
                CultNetDatabaseChangeKind.Added,
                VariantKey,
                schemaId,
                database.Shards[0],
                cache.Get<VariantWireFixture>(VariantKey)!,
                previousDocument: null);
            var method = typeof(CultNetDatabaseServer).GetMethod("CreateChangeMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var refused = Assert.Throws<TargetInvocationException>(() => method.Invoke(databaseServer, new object[]
            {
                change, "sub-1", new CultNetSelection { Projection = CultNetSelectionProjections.Document }
            }))!;

            Assert.That(refused.InnerException, Is.TypeOf<NotSupportedException>());
            Assert.That(refused.InnerException!.Message, Does.Contain(VariantKey.Value).And.Contain(BaseKey.Value));
        }


        // A hop-bearing selection reconciles the whole store on every change. The refusal follows the selection: a subscriber
        // whose selection never selects the variant keeps receiving; one that does is told, as a typed error, not a silent stop.
        [Test]
        public async Task AHopBearingSubscriptionReconcilesPastAVariantItDoesNotSelectAndIsToldWhenItDoes()
        {
            var registry = CultDocumentRegistry.Shared;
            var cache = new CultCache(registry, CultCacheMessagePack.CreateCodec(registry));
            var documents = new CultNetDocumentRegistry(registry)
                .Register(CultNetDocumentBinding.ForDocument<VariantWireFixture>(registry))
                .Register(CultNetDocumentBinding.ForDocument<VariantCiterFixture>(registry));
            var wireSchemaId = registry.GetRequired<VariantWireFixture>().SchemaId;
            cache.Commit(batch =>
            {
                batch.Upsert(typeof(VariantWireFixture), new VariantWireFixture { Name = "base", Power = 1 }, BaseKey);
                batch.Upsert(typeof(VariantCiterFixture), Citer("citer-one", BaseKey), new CultRecordKey("citer-one"));
            });
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.ReceiveTimeout = 20;
            using var server = new RudpCultNetSchemaServer(new RudpCultNetSchemaServerOptions { RuntimeId = "variant-hop-server", Socket = socket });
            using var subscriptions = new CultNetDatabaseSubscriptionServer(server, database);
            using var cancellation = new CancellationTokenSource();
            var serverThread = new Thread(() =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    _ = server.PollOnceAsync().GetAwaiter().GetResult();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            serverThread.Start();
            try
            {
                using var client = CultNetSchemaClients.CreateRudp("variant-hop-client");
                var snapshots = new ConcurrentQueue<CultNetSnapshotResponseRawV1Message>();
                var changes = new ConcurrentQueue<CultNetDatabaseChangeRawMessage>();
                var errors = new ConcurrentQueue<CultNetErrorMessage>();
                client.OnCultNet<CultNetSnapshotResponseRawV1Message>(snapshots.Enqueue);
                client.OnCultNet<CultNetDatabaseChangeRawMessage>(changes.Enqueue);
                client.OnCultNet<CultNetErrorMessage>(errors.Enqueue);
                client.Connect("127.0.0.1", server.LocalEndPoint.Port);
                await WaitUntilAsync(() => client.Connected);
                string Diagnostic() =>
                    $"changes={string.Join(",", changes.Select(c => c.SubscriptionId + ":" + c.ChangeKind + ":" + (c.Document?.RecordKey ?? c.RecordKey)))} " +
                    $"errors={string.Join(",", errors.Select(e => e.Error))}";

                // "citers": rows citing the base. It never selects the variant.
                client.SendCultNet(new CultNetDatabaseSubscribeV1Message
                {
                    MessageId = "citers",
                    SubscriptionId = "citers",
                    IncludeSnapshot = true,
                    Selection = new CultNetSelection
                    {
                        Cites = new CultNetCitation { Target = new CultNetRecordRef { SchemaId = wireSchemaId, RecordKey = BaseKey.Value }, Role = "Ref" }
                    }
                });
                await WaitUntilAsync(() => snapshots.Count == 1, Diagnostic);

                await database.PutAsync(new CultRecordKey("citer-two"), Citer("citer-two", BaseKey));
                await WaitUntilAsync(() => changes.Any(change => change.SubscriptionId == "citers" && change.Document?.RecordKey == "citer-two"), Diagnostic);

                // With a variant in the store, the same subscriber keeps receiving.
                await cache.UpsertVariantAsync(VariantKey, BaseKey, new[]
                {
                    cache.Override<VariantWireFixture>(nameof(VariantWireFixture.Name), "big")
                });
                await database.PutAsync(new CultRecordKey("citer-three"), Citer("citer-three", BaseKey));
                await WaitUntilAsync(() => changes.Any(change => change.SubscriptionId == "citers" && change.Document?.RecordKey == "citer-three"), Diagnostic);
                Assert.That(errors, Is.Empty, "a subscriber that never selects the variant is never told about it");

                // "cited": wire rows some citer cites. Once a citer cites the variant, it selects it.
                client.SendCultNet(new CultNetDatabaseSubscribeV1Message
                {
                    MessageId = "cited",
                    SubscriptionId = "cited",
                    IncludeSnapshot = true,
                    Selection = new CultNetSelection
                    {
                        Schemas = new[] { wireSchemaId },
                        Cited = new CultNetIncoming { Role = "Ref", Exists = true }
                    }
                });
                await WaitUntilAsync(() => snapshots.Count == 2, Diagnostic);
                Assert.That(errors, Is.Empty, "only the base is cited yet");
                await database.PutAsync(new CultRecordKey("citer-four"), Citer("citer-four", VariantKey));

                await WaitUntilAsync(() => errors.Any(error => error.Error.Contains(VariantKey.Value)), Diagnostic);
                Assert.That(changes.Where(change => change.SubscriptionId == "cited").Select(change => change.Document?.RecordKey),
                    Does.Not.Contain(VariantKey.Value), "a variant never reaches a subscriber as a record");

                // The first subscriber is still alive after the refusal.
                await database.PutAsync(new CultRecordKey("citer-five"), Citer("citer-five", BaseKey));
                await WaitUntilAsync(() => changes.Any(change => change.SubscriptionId == "citers" && change.Document?.RecordKey == "citer-five"), Diagnostic);
            }
            finally
            {
                cancellation.Cancel();
                serverThread.Join();
            }
        }

        private static CultNetDatabaseChangeRawMessage? Deliver(
            CultNetDatabaseServer server,
            object change,
            CultNetSelection selection,
            System.Collections.Generic.HashSet<string> refused,
            System.Collections.Generic.List<CultNetErrorMessage> errors)
        {
            var method = typeof(CultNetDatabaseServer).GetMethod("DeliverChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (CultNetDatabaseChangeRawMessage?)method.Invoke(server, new object[]
            {
                change, "sub", selection, refused, (Action<CultNetErrorMessage>)errors.Add
            });
        }

        private static VariantCiterFixture Citer(string name, CultRecordKey target) =>
            new() { Name = name, Ref = new CultRecordRef<VariantWireFixture>(target) };

        // A subscriber told a variant only as an error is not later told it was removed: it never held it.
        [Test]
        public async Task RemovingAVariantTheSubscriberWasOnlyToldAboutAsAnErrorSendsNoRemoval()
        {
            var (cache, documents) = await BuildFixtureAsync(withVariant: true);
            var database = new CultNetDatabase(cache, new CultNetDatabaseOptions { DocumentRegistry = documents });
            using var server = new Server(cache, ServerSecurityOptions.Development());
            using var databaseServer = new CultNetDatabaseServer(server, database);
            var schemaId = cache.Registry.GetRequired<VariantWireFixture>().SchemaId;
            var selection = new CultNetSelection { Projection = CultNetSelectionProjections.Document };
            var refused = new System.Collections.Generic.HashSet<string>();
            var errors = new System.Collections.Generic.List<CultNetErrorMessage>();

            var added = new CultNetDatabaseChange<VariantWireFixture>(
                CultNetDatabaseChangeKind.Added, VariantKey, schemaId, database.Shards[0], cache.Get<VariantWireFixture>(VariantKey)!, previousDocument: null);
            Assert.That(Deliver(databaseServer, added, selection, refused, errors), Is.Null);
            Assert.That(errors, Has.Count.EqualTo(1));

            var removed = new CultNetDatabaseChange<VariantWireFixture>(
                CultNetDatabaseChangeKind.Removed, VariantKey, schemaId, database.Shards[0], document: null, previousDocument: cache.Get<VariantWireFixture>(VariantKey)!);
            Assert.That(Deliver(databaseServer, removed, selection, refused, errors), Is.Null,
                "no removal for a key the subscriber never held as a record");

            // A record it did hold is still told about.
            var baseRemoved = new CultNetDatabaseChange<VariantWireFixture>(
                CultNetDatabaseChangeKind.Removed, BaseKey, schemaId, database.Shards[0], document: null, previousDocument: cache.Get<VariantWireFixture>(BaseKey)!);
            Assert.That(Deliver(databaseServer, baseRemoved, selection, refused, errors)?.ChangeKind, Is.EqualTo("removed"));
        }

        private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? diagnostic = null)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                    Assert.Fail("Condition was not satisfied before timeout. " + diagnostic?.Invoke());
                await Task.Delay(5);
            }
        }


        [CultDocument("variants.wire-fixture", "variants.wire-fixture.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class VariantWireFixture
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            public int Power;
        }

        [CultDocument("variants.wire-citer-fixture", "variants.wire-citer-fixture.v1")]
        [MessagePackObject(AllowPrivate = true)]
        public sealed class VariantCiterFixture
        {
            [Key(0)]
            [CultName]
            public string Name = string.Empty;

            [Key(1)]
            [CultReference(typeof(VariantWireFixture))]
            public CultRecordRef<VariantWireFixture> Ref;
        }
    }
}
