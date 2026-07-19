using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Networking;
using NUnit.Framework;

#nullable enable

namespace GameCult.Mesh.Tests;

public sealed class CultMeshBodyPublicationTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() =>
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-body-publications", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Test]
    public void TypedDocument_RoundTripsAndRegistersWithGenerationKey()
    {
        var now = DateTimeOffset.UtcNow;
        var publication = Publication(new byte[] { 1, 2, 3, 4 }, now);
        var path = Path.Combine(_root, "publication.cc");
        var handle = Handle(publication);

        CultMesh.WriteSingleFileDocument(path, handle.RecordKey, publication);
        var restored = CultMesh.ReadSingleFileDocument<CultMeshBodyPublicationDocument>(path, handle.RecordKey);
        var registry = CultMesh.CreateBodyPublicationDocumentRegistry();

        handle.Validate(restored);
        restored.Should().BeEquivalentTo(publication);
        registry.GetByDocumentType(typeof(CultMeshBodyPublicationDocument)).Should().NotBeNull();
        handle.RecordKey.Should().Be(CultMeshBodyPublicationDocument.CreateRecordKey(
            publication.BodyId, publication.ProducerEpoch, publication.Sequence));
        handle.RecordKey.Should().NotBe(CultMeshBodyPublicationDocument.CreateLatestRecordKey(publication.BodyId));
    }

    [Test]
    public async System.Threading.Tasks.Task GenerationRecords_PreserveNWhenNPlusOneIsPublished()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Publication(new byte[] { 1, 2, 3, 4 }, now);
        var second = Publication(new byte[] { 5, 6, 7, 8 }, now);
        second.Sequence++;
        foreach (var representation in second.Representations) representation.Sequence++;
        var cache = new CultCache();

        await cache.UpsertAsync(first, new CultRecordHandle<CultMeshBodyPublicationDocument>(first.RecordKey));
        await cache.UpsertAsync(second, new CultRecordHandle<CultMeshBodyPublicationDocument>(second.RecordKey));

        var restoredFirst = cache.Get<CultMeshBodyPublicationDocument>(first.RecordKey);
        restoredFirst.Should().NotBeNull();
        Handle(first).Validate(restoredFirst!);
        restoredFirst!.Sequence.Should().Be(42);
        cache.Get<CultMeshBodyPublicationDocument>(second.RecordKey)!.Sequence.Should().Be(43);
    }

    [Test]
    public void GenerationHandle_RejectsLatestOrDifferentGenerationEnvelope()
    {
        var publication = Publication(new byte[4], DateTimeOffset.UtcNow);
        var staleLatestKey = CultMeshBodyPublicationDocument.CreateLatestRecordKey(publication.BodyId);
        var next = Publication(new byte[4], DateTimeOffset.UtcNow);
        next.Sequence++;
        foreach (var representation in next.Representations) representation.Sequence++;

        Action staleLatest = () => CultMeshBodyPublicationValidator.Validate(
            publication, publication.BodyId, publication.ProducerEpoch, publication.Sequence, staleLatestKey);
        Action substituted = () => Handle(publication).Validate(next);

        staleLatest.Should().Throw<InvalidOperationException>().WithMessage("*record key*");
        substituted.Should().Throw<InvalidOperationException>().WithMessage("*sequence*");
    }

    [Test]
    public void Resolver_UsesPreferredLocalRepresentation()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = BitConverter.GetBytes(12.5f);
        var publication = Publication(bytes, now);
        var resolver = Resolver(bytes, (producer, _) => producer == "aetheria");

        using var lease = resolver.ResolveReadOnly(publication, Request(publication, now));

        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.SharedFileMapping);
        lease.ReadSingle(0).Should().Be(12.5f);
    }

    [Test]
    public void Resolver_AcceptsLocalOnlyPublicationWithoutInventingNetworkWork()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = BitConverter.GetBytes(21.25f);
        var publication = Publication(bytes, now);
        publication.Representations = new[] { publication.Representations[0] };
        var fetched = false;
        var resolver = Resolver(bytes, (_, _) => true, () => fetched = true);

        using var lease = resolver.ResolveReadOnly(publication, Request(publication, now));

        lease.TransportKind.Should().Be(publication.Representations[0].TransportKind);
        lease.ReadSingle(0).Should().Be(21.25f);
        fetched.Should().BeFalse();
    }

    [Test]
    public void Resolver_FallsBackToEquivalentNetworkRepresentation()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 4, 8, 15, 16 };
        var publication = Publication(bytes, now);
        new CultMeshMappedBodyPublisher(_root).Revoke(publication.Representations[0]);

        var result = Resolver(bytes, (_, _) => true).NegotiateReadOnly(publication, Request(publication, now));
        using var lease = result.Lease;

        result.UsedFallback.Should().BeTrue();
        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
    }

    [TestCase("body")]
    [TestCase("schema")]
    [TestCase("capacity")]
    [TestCase("epoch")]
    [TestCase("sequence")]
    [TestCase("liveness")]
    public void Resolver_RejectsDescriptorThatDisagreesWithEnvelope(string field)
    {
        var now = DateTimeOffset.UtcNow;
        var publication = Publication(new byte[4], now);
        var descriptor = publication.Representations[1];
        if (field == "body") descriptor.BodyId += ":wrong";
        if (field == "schema") descriptor.SchemaId += ".wrong";
        if (field == "capacity") descriptor.Capacity++;
        if (field == "epoch") descriptor.ProducerEpoch++;
        if (field == "sequence") descriptor.Sequence++;
        if (field == "liveness") descriptor.LeaseExpiresAtUnixMs++;

        Action resolve = () => Resolver(new byte[4], (_, _) => true)
            .ResolveReadOnly(publication, Request(publication, now));

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*envelope*");
    }

    [Test]
    public void Resolver_RejectsUnauthorizedProducerBeforeOpeningTransport()
    {
        var now = DateTimeOffset.UtcNow;
        var publication = Publication(new byte[4], now);
        var fetched = false;
        var resolver = Resolver(new byte[4], (producer, _) =>
        {
            producer.Should().Be("aetheria");
            return false;
        }, () => fetched = true);

        Action resolve = () => resolver.ResolveReadOnly(publication, Request(publication, now));

        resolve.Should().Throw<UnauthorizedAccessException>();
        fetched.Should().BeFalse();
    }

    [Test]
    public void Resolver_RejectsExpiredPublicationAndStaleRequestedGeneration()
    {
        var now = DateTimeOffset.UtcNow;
        var publication = Publication(new byte[4], now);
        var resolver = Resolver(new byte[4], (_, _) => true);

        Action expired = () => resolver.ResolveReadOnly(publication, Request(publication, now.AddMinutes(2)));
        expired.Should().Throw<InvalidOperationException>().WithMessage("*live*");

        var stale = Request(publication, now);
        stale.ProducerEpoch--;
        Action wrongGeneration = () => resolver.ResolveReadOnly(publication, stale);
        wrongGeneration.Should().Throw<InvalidOperationException>().WithMessage("*epoch*");
    }

    [Test]
    public async Task LiveBody_SubscriptionKeepsBytesOffTheSnapshotAndOpensTheBestPlane()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 3, 1, 4, 1, 5, 9 };
        var publication = Publication(bytes, now);
        var cache = new CultCache();
        var documents = CultMesh.CreateBodyPublicationDocumentRegistry(cache.Registry);
        var transport = new BodyPublicationSchemaClient(publication, documents);
        using var subscriptions = new CultNetDatabaseSubscriptionClient(transport, cache, documents);

        using var body = await CultMesh.SubscribeLiveBodyAsync(
            subscriptions,
            Resolver(bytes, (_, _) => true),
            new CultMeshLiveBodySubscription("pilot-body", "eve-unity", publication.BodyId));

        transport.Subscribe.Should().NotBeNull();
        transport.Subscribe!.RecordKeys.Should().Equal(
            CultMeshBodyPublicationDocument.CreateLatestRecordKey(publication.BodyId).Value);
        transport.Subscribe.SchemaIds.Should().Equal(CultMeshBodyPublicationSchemaVersions.Publication);
        transport.Subscribe.BodyIds.Should().Equal(publication.BodyId);
        transport.Subscribe.ConsumerRuntimeId.Should().Be("eve-unity");
        cache.Get<CultMeshBodyPublicationDocument>(
            CultMeshBodyPublicationDocument.CreateLatestRecordKey(publication.BodyId)).Should().BeNull(
            "live descriptor control state must not become a renderer-owned replica");

        using var opened = body.OpenCurrentReadOnly(now).Lease;
        opened.TransportKind.Should().Be(CultMeshBodyTransportKind.SharedFileMapping);
        var restored = new byte[bytes.Length];
        opened.CopyTo(0, restored, 0, restored.Length).Should().Be(bytes.Length);
        restored.Should().Equal(bytes);
    }

    private CultMeshBodyPublicationDocument Publication(byte[] bytes, DateTimeOffset now)
    {
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("aetheria:entities", "eve.entity_soa.v1", 2, 128, 7, 42, bytes, now);
        return new CultMeshBodyPublicationDocument
        {
            BodyId = local.BodyId,
            ProducerId = "aetheria",
            SchemaId = local.SchemaId,
            LayoutVersion = local.LayoutVersion,
            ByteSize = local.ByteSize,
            Capacity = local.Capacity,
            ProducerEpoch = local.ProducerEpoch,
            Sequence = local.Sequence,
            Synchronization = local.Synchronization,
            LivenessExpiresAtUnixMs = local.LeaseExpiresAtUnixMs,
            Representations = new[] { local, Network(local) }
        };
    }

    private CultMeshBodyPublicationResolver Resolver(
        byte[] bytes,
        Func<string, CultMeshBodyDescriptor, bool> authorize,
        Action? fetched = null) =>
        new(new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_root),
                new CultMeshNetworkBodyAdapter(_ => { fetched?.Invoke(); return bytes; })
            },
            authorize));

    private static CultMeshBodyPublicationHandle Handle(CultMeshBodyPublicationDocument publication) =>
        new(publication.BodyId, publication.ProducerEpoch, publication.Sequence);

    private static CultMeshBodyValidationRequest Request(
        CultMeshBodyPublicationDocument publication,
        DateTimeOffset now) => new()
    {
        BodyId = publication.BodyId,
        SchemaId = publication.SchemaId,
        LayoutVersion = publication.LayoutVersion,
        ProducerEpoch = publication.ProducerEpoch,
        Sequence = publication.Sequence,
        Capacity = publication.Capacity,
        AccessMode = CultMeshBodyAccessMode.ReadOnly,
        NowUtc = now
    };

    private static CultMeshBodyDescriptor Network(CultMeshBodyDescriptor local) => new()
    {
        BodyId = local.BodyId,
        SchemaId = local.SchemaId,
        LayoutVersion = local.LayoutVersion,
        ByteSize = local.ByteSize,
        Capacity = local.Capacity,
        ProducerEpoch = local.ProducerEpoch,
        Sequence = local.Sequence,
        AccessMode = local.AccessMode,
        Synchronization = local.Synchronization,
        LeaseExpiresAtUnixMs = local.LeaseExpiresAtUnixMs,
        TransportKind = CultMeshBodyTransportKind.Network,
        CapabilityToken = "cultnet:body:aetheria:entities:7:42",
        SemanticHash = local.SemanticHash
    };

    private sealed class BodyPublicationSchemaClient : ICultNetSchemaClient
    {
        private readonly CultMeshBodyPublicationDocument _publication;
        private readonly CultNetDocumentRegistry _documents;
        private readonly List<Action<CultNetSnapshotResponseRawMessage>> _snapshots = new();

        public BodyPublicationSchemaClient(
            CultMeshBodyPublicationDocument publication,
            CultNetDocumentRegistry documents)
        {
            _publication = publication;
            _documents = documents;
        }

        public bool Connected => true;
        public CultNetDatabaseSubscribeMessage? Subscribe { get; private set; }
        public void Connect(string host, int port) { }

        public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
        {
            if (message is not CultNetDatabaseSubscribeMessage subscribe)
                return;
            Subscribe = subscribe;
            var put = _documents.CreateRawDocumentPutMessage(
                "body-publication",
                new CultRecordHandle<CultMeshBodyPublicationDocument>(
                    CultMeshBodyPublicationDocument.CreateLatestRecordKey(_publication.BodyId)),
                _publication);
            var response = new CultNetSnapshotResponseRawMessage
            {
                MessageId = subscribe.MessageId,
                Documents = new[] { put.Document }
            };
            foreach (var snapshot in _snapshots.ToArray()) snapshot(response);
        }

        public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
        {
            if (typeof(T) == typeof(CultNetSnapshotResponseRawMessage))
                _snapshots.Add(response => callback((T)(object)response));
        }

        public void Dispose() { }
    }
}
