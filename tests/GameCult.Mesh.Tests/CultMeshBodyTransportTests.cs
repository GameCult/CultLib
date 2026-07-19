using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;
using System.IO.MemoryMappedFiles;
using GameCult.Networking;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshBodyTransportTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cultmesh-body-tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void MappedBody_IsReadOnlyAndPreservesLogicalIdentityAcrossTransportMaterial()
    {
        var publisher = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        var firstBytes = BitConverter.GetBytes(12.5f);
        var secondBytes = BitConverter.GetBytes(13.5f);

        var first = publisher.Publish("aetheria:pilot-entities", "eve.entity_soa.v1", 1, 128, 7, 40, firstBytes, now);
        var second = publisher.Publish("aetheria:pilot-entities", "eve.entity_soa.v1", 1, 128, 7, 41, secondBytes, now);

        first.BodyId.Should().Be(second.BodyId);
        first.CapabilityToken.Should().NotBe(second.CapabilityToken);
        first.AccessMode.Should().Be(CultMeshBodyAccessMode.ReadOnly);
        using var lease = new CultMeshMappedBodyAdapter(_root).OpenReadOnly(first, Request(first, now));
        lease.ReadSingle(0).Should().Be(12.5f);
        lease.Descriptor.Sequence.Should().Be(40);
    }

    [Test]
    public void Validation_RejectsExpiredEpochLayoutAndWritableConsumption()
    {
        var publisher = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromSeconds(1));
        var now = DateTimeOffset.UtcNow;
        var descriptor = publisher.Publish("body", "schema", 2, 4, 9, 1, new byte[4], now);
        var adapter = new CultMeshMappedBodyAdapter(_root);

        Action expired = () => adapter.OpenReadOnly(descriptor, Request(descriptor, now.AddSeconds(2)));
        expired.Should().Throw<InvalidOperationException>().WithMessage("*expired*");

        var wrongEpoch = Request(descriptor, now);
        wrongEpoch.ProducerEpoch++;
        Action epoch = () => adapter.OpenReadOnly(descriptor, wrongEpoch);
        epoch.Should().Throw<InvalidOperationException>().WithMessage("*epoch*");

        var writable = Request(descriptor, now);
        writable.AccessMode = CultMeshBodyAccessMode.ReadWrite;
        Action write = () => adapter.OpenReadOnly(descriptor, writable);
        write.Should().Throw<UnauthorizedAccessException>();
    }

    [Test]
    public void CapabilityToken_CannotEscapeBrokerRoot()
    {
        var descriptor = new CultMeshBodyDescriptor
        {
            BodyId = "body",
            SchemaId = "schema",
            LayoutVersion = 1,
            ByteSize = 4,
            Capacity = 4,
            ProducerEpoch = 1,
            Sequence = 1,
            LeaseExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            TransportKind = CultMeshBodyTransportKind.SharedFileMapping,
            CapabilityToken = ".." + Path.DirectorySeparatorChar + "outside"
        };
        Action open = () => new CultMeshMappedBodyAdapter(_root).OpenReadOnly(
            descriptor,
            Request(descriptor, DateTimeOffset.UtcNow));
        open.Should().Throw<UnauthorizedAccessException>();
    }

    [Test]
    public void Negotiation_FallsBackToEquivalentNetworkBody_WhenLocalMappingFails()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = BitConverter.GetBytes(42.25f);
        var publisher = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1));
        var local = publisher.Publish("body", "schema", 1, bytes.Length, 3, 8, bytes, now);
        publisher.Revoke(local);
        var network = new CultMeshBodyDescriptor
        {
            BodyId = local.BodyId,
            SchemaId = local.SchemaId,
            LayoutVersion = local.LayoutVersion,
            ByteSize = local.ByteSize,
            Capacity = local.Capacity,
            ProducerEpoch = local.ProducerEpoch,
            Sequence = local.Sequence,
            AccessMode = CultMeshBodyAccessMode.ReadOnly,
            Synchronization = local.Synchronization,
            LeaseExpiresAtUnixMs = local.LeaseExpiresAtUnixMs,
            TransportKind = CultMeshBodyTransportKind.Network,
            SemanticHash = local.SemanticHash
        };
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_root),
                new CultMeshNetworkBodyAdapter(_ => bytes)
            },
            _ => true);

        var result = service.NegotiateReadOnly(local, network, Request(local, now));
        using var lease = result.Lease;

        result.PreferredFailure.Should().BeOfType<InvalidDataException>();
        result.UsedFallback.Should().BeTrue();
        result.PreferredTransport.Should().Be(CultMeshBodyTransportKind.SharedFileMapping);
        result.SelectedTransport.Should().Be(CultMeshBodyTransportKind.Network);
        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
        lease.Descriptor.BodyId.Should().Be(local.BodyId);
        lease.ReadSingle(0).Should().Be(42.25f);
    }

    [Test]
    public void LocalAndNetworkRepresentationsExposeTheSameLogicalContractAndSemanticBytes()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 1, 3, 5, 7, 9, 11 };
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("body", "schema.v2", 2, 64, 5, 12, bytes, now);
        var network = NetworkRepresentation(local);
        var request = Request(local, now);

        using var localLease = new CultMeshMappedBodyAdapter(_root).OpenReadOnly(local, request);
        using var networkLease = new CultMeshNetworkBodyAdapter(_ => bytes).OpenReadOnly(network, request);

        networkLease.Descriptor.Should().BeEquivalentTo(localLease.Descriptor, options => options
            .Excluding(value => value.TransportKind)
            .Excluding(value => value.CapabilityToken));
        ReadAll(localLease).Should().Equal(bytes);
        ReadAll(networkLease).Should().Equal(bytes);
    }

    [Test]
    public void MissingPreferredAdapterProducesObservableFallbackReason()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 4, 8, 15, 16, 23, 42 };
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("body", "schema", 1, bytes.Length, 3, 8, bytes, now);
        var network = NetworkRepresentation(local);
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[] { new CultMeshNetworkBodyAdapter(_ => bytes) },
            _ => true);

        var result = service.NegotiateReadOnly(local, network, Request(local, now));
        using var lease = result.Lease;

        result.UsedFallback.Should().BeTrue();
        result.PreferredFailure.Should().BeOfType<NotSupportedException>();
        result.PreferredFailure!.Message.Should().Contain("adapter is available");
        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
    }

    [TestCase("schema")]
    [TestCase("epoch")]
    [TestCase("sequence")]
    public void NegotiationRejectsStaleLogicalGenerationBeforeFetchingNetworkBytes(string staleField)
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 2, 4, 6, 8 };
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("body", "schema", 1, 4, 3, 8, bytes, now);
        var network = NetworkRepresentation(local);
        var request = Request(local, now);
        if (staleField == "schema") request.SchemaId = "stale";
        if (staleField == "epoch") request.ProducerEpoch--;
        if (staleField == "sequence") request.Sequence--;
        var fetched = false;
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_root),
                new CultMeshNetworkBodyAdapter(_ => { fetched = true; return bytes; })
            },
            _ => true);

        Action open = () => service.NegotiateReadOnly(local, network, request);

        open.Should().Throw<InvalidOperationException>();
        fetched.Should().BeFalse();
    }

    [Test]
    public void NetworkRepresentationRejectsCorruptBytesBeforeCreatingAReadableLease()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 10, 20, 30, 40 };
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("body", "schema", 1, 4, 3, 8, bytes, now);
        var network = NetworkRepresentation(local);
        var corrupt = new byte[] { 10, 20, 30, 41 };

        Action open = () => new CultMeshNetworkBodyAdapter(_ => corrupt)
            .OpenReadOnly(network, Request(network, now));

        open.Should().Throw<InvalidDataException>().WithMessage("*semantic digest*");
    }

    [Test]
    public void Negotiation_RejectsUnauthorizedProducerBeforeOpeningAnyRepresentation()
    {
        var now = DateTimeOffset.UtcNow;
        var bytes = new byte[4];
        var local = new CultMeshMappedBodyPublisher(_root, TimeSpan.FromMinutes(1))
            .Publish("body", "schema", 1, 4, 3, 8, bytes, now);
        var network = new CultMeshBodyDescriptor
        {
            BodyId = local.BodyId, SchemaId = local.SchemaId, LayoutVersion = 1,
            ByteSize = 4, Capacity = 4, ProducerEpoch = 3, Sequence = 8,
            LeaseExpiresAtUnixMs = local.LeaseExpiresAtUnixMs,
            TransportKind = CultMeshBodyTransportKind.Network,
            SemanticHash = local.SemanticHash
        };
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_root),
                new CultMeshNetworkBodyAdapter(_ => bytes)
            },
            _ => false);
        Action open = () => service.OpenReadOnly(local, network, Request(local, now), out _);
        open.Should().Throw<UnauthorizedAccessException>();
    }

    [Test]
    [Platform("Win")]
    public void SharedMemoryAdapter_OpensOpaqueCapabilityReadOnly()
    {
        var token = "CultMesh.Body.Test." + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(token, 8, MemoryMappedFileAccess.ReadWrite);
        using (var writer = mapping.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Write)) writer.Write(0, 99L);
        var now = DateTimeOffset.UtcNow;
        var descriptor = new CultMeshBodyDescriptor
        {
            BodyId = "body", SchemaId = "schema", LayoutVersion = 1,
            ByteSize = 8, Capacity = 8, ProducerEpoch = 4, Sequence = 2,
            LeaseExpiresAtUnixMs = now.AddMinutes(1).ToUnixTimeMilliseconds(),
            TransportKind = CultMeshBodyTransportKind.SharedMemory,
            CapabilityToken = token
        };

        using var lease = new CultMeshSharedMemoryBodyAdapter().OpenReadOnly(descriptor, Request(descriptor, now));

        lease.ReadInt64(0).Should().Be(99);
        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.SharedMemory);
    }

    [Test]
    public void FramePublisher_DirectWriteLeaseCommitsMappedBytesWithoutCompatibilityCopy()
    {
        using var publisher = new CultMeshFrameBodyPublisher(
            "aetheria:entities",
            "gamecult.eve.entity_soa.body.v2",
            layoutVersion: 2,
            capacity: 128,
            producerEpoch: 7,
            slotByteLength: 64,
            leaseDuration: TimeSpan.FromMinutes(1));
        publisher.TryAcquireWrite(out var write).Should().BeTrue();
        using (write)
        {
            BitConverter.TryWriteBytes(write.Span, 42.5f).Should().BeTrue();
            var descriptor = write.Commit(sizeof(float), DateTimeOffset.UtcNow);
            using var read = new CultMeshSharedMemoryBodyAdapter().OpenReadOnly(
                descriptor,
                Request(descriptor, DateTimeOffset.UtcNow));

            descriptor.Sequence.Should().Be(0);
            descriptor.SemanticHash.Should().NotBeNullOrWhiteSpace();
            read.ReadSingle(0).Should().Be(42.5f);
        }
    }

    [Test]
    public void FramePublisher_AbandonedWriteDoesNotAdvanceGenerationOrHoldSlot()
    {
        using var publisher = new CultMeshFrameBodyPublisher(
            "aetheria:entities",
            "gamecult.eve.entity_soa.body.v2",
            layoutVersion: 2,
            capacity: 128,
            producerEpoch: 7,
            slotByteLength: 64);
        publisher.TryAcquireWrite(out var abandoned).Should().BeTrue();
        abandoned.Dispose();

        publisher.TryAcquireWrite(out var replacement).Should().BeTrue();
        using (replacement)
        {
            replacement.Span[0] = 23;
            var descriptor = replacement.Commit(1, DateTimeOffset.UtcNow);
            descriptor.Sequence.Should().Be(0);
        }
    }

    [Test]
    public void BodyDemand_SelectsSharedMemoryLocallyAndNetworkRemotelyWithoutDuplicateFallbackWork()
    {
        using var tracker = new CultMeshBodyDemandTracker();
        tracker.Observe(Demand("unity-local", "local", sameMachine: true));
        tracker.Observe(Demand("unity-remote", "remote", sameMachine: false));

        var mixed = tracker.Plan("eve:entity-soa:aetheria.daemon:pilot");

        mixed.HasConsumers.Should().BeTrue();
        mixed.RequiresSharedMemory.Should().BeTrue();
        mixed.RequiresNetwork.Should().BeTrue();
        mixed.Consumers.Should().ContainSingle(value =>
            value.ConsumerRuntimeId == "unity-local" &&
            value.Transport == CultMeshBodyTransportKind.SharedMemory);
        mixed.Consumers.Should().ContainSingle(value =>
            value.ConsumerRuntimeId == "unity-remote" &&
            value.Transport == CultMeshBodyTransportKind.Network);

        tracker.Observe(Demand("unity-remote", "remote", sameMachine: false, active: false));
        var localOnly = tracker.Plan("eve:entity-soa:aetheria.daemon:pilot");
        localOnly.RequiresSharedMemory.Should().BeTrue();
        localOnly.RequiresNetwork.Should().BeFalse();
    }

    [Test]
    public void BodyDemand_UnknownLocalityFailsTowardNetworkAndUnsupportedDemandCreatesNoRoute()
    {
        using var tracker = new CultMeshBodyDemandTracker();
        tracker.Observe(Demand("unknown", "network", sameMachine: false));
        tracker.Observe(new CultNetDatabaseSubscriptionDemand(
            "unsupported",
            "unsupported",
            new[] { "eve:entity-soa:aetheria.daemon:pilot" },
            new[] { CultMeshBodyTransportKind.SharedMemory.ToString() },
            sameMachine: false,
            active: true));

        var plan = tracker.Plan("eve:entity-soa:aetheria.daemon:pilot");

        plan.RequiresSharedMemory.Should().BeFalse();
        plan.RequiresNetwork.Should().BeTrue();
        plan.Consumers.Should().ContainSingle().Which.ConsumerRuntimeId.Should().Be("unknown");
    }

    private static CultMeshBodyValidationRequest Request(CultMeshBodyDescriptor descriptor, DateTimeOffset now) => new()
    {
        BodyId = descriptor.BodyId,
        SchemaId = descriptor.SchemaId,
        LayoutVersion = descriptor.LayoutVersion,
        ProducerEpoch = descriptor.ProducerEpoch,
        Sequence = descriptor.Sequence,
        Capacity = descriptor.Capacity,
        AccessMode = CultMeshBodyAccessMode.ReadOnly,
        NowUtc = now
    };

    private static CultNetDatabaseSubscriptionDemand Demand(
        string runtimeId,
        string subscriptionId,
        bool sameMachine,
        bool active = true) => new(
        runtimeId,
        subscriptionId,
        new[] { "eve:entity-soa:aetheria.daemon:pilot" },
        new[]
        {
            CultMeshBodyTransportKind.SharedMemory.ToString(),
            CultMeshBodyTransportKind.Network.ToString()
        },
        sameMachine,
        active);

    private static CultMeshBodyDescriptor NetworkRepresentation(CultMeshBodyDescriptor descriptor) => new()
    {
        BodyId = descriptor.BodyId,
        SchemaId = descriptor.SchemaId,
        LayoutVersion = descriptor.LayoutVersion,
        ByteSize = descriptor.ByteSize,
        Capacity = descriptor.Capacity,
        ProducerEpoch = descriptor.ProducerEpoch,
        Sequence = descriptor.Sequence,
        AccessMode = descriptor.AccessMode,
        Synchronization = descriptor.Synchronization,
        LeaseExpiresAtUnixMs = descriptor.LeaseExpiresAtUnixMs,
        TransportKind = CultMeshBodyTransportKind.Network,
        SemanticHash = descriptor.SemanticHash
    };

    private static byte[] ReadAll(ICultMeshBodyReadLease lease)
    {
        var bytes = new byte[lease.Descriptor.ByteSize];
        lease.CopyTo(0, bytes, 0, bytes.Length);
        return bytes;
    }
}
