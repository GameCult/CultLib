using System;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshStreamingTests
{
    [Test]
    public void NegotiatesGpuTextureStreamsWithoutForcingCopies()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        var stream = new CultMeshStreamDescriptor(
            "mimir:kiyo-pro:rgba",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Video,
            new CultMeshStreamClock("mimir:clock", "kiyo-pro", sampleRate: 90_000, confidence: 0.92d),
            new[]
            {
                CultMeshStreamBodyTransport.SharedD3D12Texture,
                CultMeshStreamBodyTransport.SharedMemory,
                CultMeshStreamBodyTransport.CultCachePage
            },
            video: new CultMeshVideoStreamFormat(1920, 1080, "rgba8", framesPerSecond: 60),
            maxInFlightFrames: 4);

        catalog.Declare(stream);

        var negotiation = catalog.Negotiate(
            stream.StreamId,
            new CultMeshStreamConsumerProfile(
                "fensalir",
                "mimir-live",
                new[] { CultMeshStreamBodyTransport.SharedD3D12Texture, CultMeshStreamBodyTransport.CultCachePage },
                acceptedKinds: new[] { CultMeshStreamKind.Video },
                canImportGpuHandles: true,
                maxInFlightFrames: 2));

        negotiation.Transport.Should().Be(CultMeshStreamBodyTransport.SharedD3D12Texture);
        negotiation.CopyBudget.Should().Be(CultMeshStreamCopyBudget.ZeroCopyTarget);
        negotiation.MaxInFlightFrames.Should().Be(2);

        var handle = new CultMeshStreamFrameHandle(
            stream.StreamId,
            sequence: 42,
            timestampNs: 123_456_789,
            CultMeshStreamBodyTransport.SharedD3D12Texture,
            nativeHandle: "shared-handle:0xfeedbeef",
            fenceHandle: "fence:0x1234",
            fenceValue: 7);

        catalog.PublishFrame(handle);

        catalog.LatestFrame(stream.StreamId).Should().BeSameAs(handle);
    }

    [Test]
    public void NegotiationRejectsStreamsWithoutACommonTransport()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        catalog.Declare(new CultMeshStreamDescriptor(
            "mimir:kiyo-pro:gpu-only",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Video,
            new CultMeshStreamClock("mimir:clock", "kiyo-pro"),
            new[] { CultMeshStreamBodyTransport.SharedD3D12Texture }));

        var consumer = new CultMeshStreamConsumerProfile(
            "cpu-recorder",
            "mimir-live",
            new[] { CultMeshStreamBodyTransport.SharedMemory },
            acceptedKinds: new[] { CultMeshStreamKind.Video });

        Action act = () => catalog.Negotiate("mimir:kiyo-pro:gpu-only", consumer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Stream and consumer have no compatible body transport.");
    }

    [Test]
    public void SharedMemoryRingPublishesWritableSlotsWithoutInternalCopies()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 2, slotByteLength: 16);

        ring.TryAcquireWriteSlot(out var write).Should().BeTrue();
        ReadOnlySpan<byte> seed = stackalloc byte[] { 1, 2, 3, 4 };
        seed.CopyTo(write.Span);

        var handle = ring.CommitWriteSlot(write, timestampNs: 99, byteLength: 4);
        catalog.PublishFrame(handle);

        ring.TryAcquireLatestRead(out var read).Should().BeTrue();
        using (read)
        {
            read.Handle.Sequence.Should().Be(0);
            read.Handle.UnavoidableCopyCount.Should().Be(0);
            read.Span.ToArray().Should().Equal(1, 2, 3, 4);
        }

        var stats = ring.Stats();
        stats.PublishedFrames.Should().Be(1);
        stats.UnavoidableCopyCount.Should().Be(0);
        catalog.LatestFrame("mimir:leap:depth")!.ResourceKey.Should().Be("mimir:leap:depth:slot:0");
    }

    [Test]
    public void SharedMemoryRingDoesNotOverwriteSlotsHeldByReaders()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 1, slotByteLength: 8);

        ring.TryAcquireWriteSlot(out var firstWrite).Should().BeTrue();
        firstWrite.Span[0] = 11;
        ring.CommitWriteSlot(firstWrite, timestampNs: 1, byteLength: 1);

        ring.TryAcquireLatestRead(out var read).Should().BeTrue();

        ring.TryAcquireWriteSlot(out _).Should().BeFalse();
        ring.Stats().BlockedWrites.Should().Be(1);

        read.Dispose();

        ring.TryAcquireWriteSlot(out var secondWrite).Should().BeTrue();
        secondWrite.Span[0] = 12;
        ring.CommitWriteSlot(secondWrite, timestampNs: 2, byteLength: 1);

        var stats = ring.Stats();
        stats.PublishedFrames.Should().Be(2);
        stats.DroppedFrames.Should().Be(1);
        stats.LatestSequence.Should().Be(1);
    }

    [Test]
    public void CopyPublishMarksFallbackCopiesExplicitly()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 2, slotByteLength: 8);

        ring.TryPublishCopy(stackalloc byte[] { 5, 6, 7 }, timestampNs: 10, durationNs: 2, out var handle)
            .Should()
            .BeTrue();

        handle.UnavoidableCopyCount.Should().Be(1);
        ring.Stats().UnavoidableCopyCount.Should().Be(1);
    }

    private static CultMeshStreamCatalog CatalogWithByteStream()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        catalog.Declare(new CultMeshStreamDescriptor(
            "mimir:leap:depth",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Tensor,
            new CultMeshStreamClock("mimir:clock", "leap", confidence: 0.8d),
            new[] { CultMeshStreamBodyTransport.SharedMemory, CultMeshStreamBodyTransport.CultCachePage }));
        return catalog;
    }
}
