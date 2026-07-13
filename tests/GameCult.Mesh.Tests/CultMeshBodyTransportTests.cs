using System;
using System.IO;
using FluentAssertions;
using NUnit.Framework;

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
            TransportKind = CultMeshBodyTransportKind.Network
        };
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_root),
                new CultMeshNetworkBodyAdapter(_ => bytes)
            },
            _ => true);

        using var lease = service.OpenReadOnly(local, network, Request(local, now), out var localFailure);

        localFailure.Should().BeOfType<InvalidDataException>();
        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
        lease.Descriptor.BodyId.Should().Be(local.BodyId);
        lease.ReadSingle(0).Should().Be(42.25f);
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
            TransportKind = CultMeshBodyTransportKind.Network
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

    private static CultMeshBodyValidationRequest Request(CultMeshBodyDescriptor descriptor, DateTimeOffset now) => new()
    {
        BodyId = descriptor.BodyId,
        SchemaId = descriptor.SchemaId,
        LayoutVersion = descriptor.LayoutVersion,
        ProducerEpoch = descriptor.ProducerEpoch,
        AccessMode = CultMeshBodyAccessMode.ReadOnly,
        NowUtc = now
    };
}
