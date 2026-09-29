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
    }
}
