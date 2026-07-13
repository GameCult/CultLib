using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshFrameBodyTests
{
    [Test]
    public void CrossProcessPublisherOpensReadOnlyBytesAndProtectsLeasedSlot()
    {
        using var publisher = Publisher();
        publisher.TryPublish(new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow, out var first).Should().BeTrue();
        var adapter = new CultMeshSharedMemoryBodyAdapter();
        using var lease = adapter.OpenReadOnly(first, Request(first));
        var bytes = new byte[3];
        lease.CopyTo(0, bytes, 0, bytes.Length).Should().Be(3);
        bytes.Should().Equal(1, 2, 3);

        publisher.TryPublish(new byte[] { 4 }, DateTimeOffset.UtcNow, out var second).Should().BeTrue();
        publisher.TryPublish(new byte[] { 5 }, DateTimeOffset.UtcNow, out var third).Should().BeTrue();
        publisher.TryPublish(new byte[] { 6 }, DateTimeOffset.UtcNow, out var fourth).Should().BeTrue();
        fourth.CapabilityToken.Should().NotBe(first.CapabilityToken);
        lease.ReadByte(0).Should().Be(1);
    }

    [TestCase("wrong", 2, 9)]
    [TestCase("tests.frame.v2", 1, 9)]
    [TestCase("tests.frame.v2", 2, 8)]
    public void CrossProcessOpenRejectsStaleSchemaLayoutOrEpoch(string schema, int layout, long epoch)
    {
        using var publisher = Publisher();
        publisher.TryPublish(new byte[] { 42 }, DateTimeOffset.UtcNow, out var descriptor).Should().BeTrue();
        var request = Request(descriptor);
        request.SchemaId = schema;
        request.LayoutVersion = layout;
        request.ProducerEpoch = epoch;

        Action open = () => new CultMeshSharedMemoryBodyAdapter().OpenReadOnly(descriptor, request);
        open.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ReusedSlotRejectsItsStaleGenerationDescriptor()
    {
        using var publisher = Publisher();
        publisher.TryPublish(new byte[] { 1 }, DateTimeOffset.UtcNow, out var stale).Should().BeTrue();
        publisher.TryPublish(new byte[] { 2 }, DateTimeOffset.UtcNow, out _).Should().BeTrue();
        publisher.TryPublish(new byte[] { 3 }, DateTimeOffset.UtcNow, out _).Should().BeTrue();
        publisher.TryPublish(new byte[] { 4 }, DateTimeOffset.UtcNow, out _).Should().BeTrue();

        Action open = () => new CultMeshSharedMemoryBodyAdapter().OpenReadOnly(stale, Request(stale));
        open.Should().Throw<InvalidOperationException>().WithMessage("*stale*");
    }

    [Test]
    public void CleanupRevokesNewOpensAndDisposedLeaseIsInvalid()
    {
        var publisher = Publisher();
        publisher.TryPublish(new byte[] { 42 }, DateTimeOffset.UtcNow, out var descriptor).Should().BeTrue();
        var adapter = new CultMeshSharedMemoryBodyAdapter();
        var lease = adapter.OpenReadOnly(descriptor, Request(descriptor));
        lease.Dispose();
        Action readDisposed = () => lease.ReadByte(0);
        readDisposed.Should().Throw<ObjectDisposedException>();

        publisher.Dispose();
        Action reopen = () => adapter.OpenReadOnly(descriptor, Request(descriptor));
        reopen.Should().Throw<InvalidDataException>();
    }

    [Test]
    public void PublishesOneAtomicSchemaVersionedGenerationPerCommit()
    {
        using var region = Region();
        region.TryAcquireWrite(out var write).Should().BeTrue();
        write.Sequence.Should().Be(0);
        write.Span[..4].Fill(7);

        var generation = region.Commit(
            write,
            4,
            timestampNs: 12,
            metadata: new Dictionary<string, string> { ["plane"] = "positions" });

        generation.Descriptor.Sequence.Should().Be(0);
        generation.Descriptor.SchemaId.Should().Be("tests.frame.v2");
        generation.Descriptor.LayoutVersion.Should().Be(2);
        generation.Descriptor.Capacity.Should().Be(64);
        generation.Descriptor.ProducerEpoch.Should().Be(9);
        generation.Descriptor.Synchronization.Should().Be(CultMeshBodySynchronization.TripleBuffer);
        generation.Descriptor.AccessMode.Should().Be(CultMeshBodyAccessMode.ReadOnly);
        region.Stats().PublishedFrames.Should().Be(1);

        Action duplicate = () => region.Commit(write, 4, timestampNs: 13);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*stale or was already committed*");
        region.Stats().PublishedFrames.Should().Be(1);
    }

    [Test]
    public void RefusesASecondOutstandingWriteAndRecoversAfterCommit()
    {
        using var region = Region();

        region.TryAcquireWrite(out var first).Should().BeTrue();
        first.Sequence.Should().Be(0);
        region.TryAcquireWrite(out _).Should().BeFalse();

        first.Span[0] = 10;
        region.Commit(first, 1, timestampNs: 1).Descriptor.Sequence.Should().Be(0);

        region.TryAcquireWrite(out var second).Should().BeTrue();
        second.Sequence.Should().Be(1);
        second.Span[0] = 11;
        region.Commit(second, 1, timestampNs: 2).Descriptor.Sequence.Should().Be(1);

        region.TryAcquireWrite(out var third).Should().BeTrue();
        third.Sequence.Should().Be(2);
        region.Stats().BlockedWrites.Should().Be(1);
    }

    [Test]
    public void RefusesWritesWhenEverySlotHasAReaderLease()
    {
        using var region = Region();
        var reads = new CultMeshFrameRegionReadLease[3];
        for (var sequence = 0; sequence < 3; sequence++)
        {
            region.TryAcquireWrite(out var write).Should().BeTrue();
            write.Span[0] = (byte)sequence;
            region.Commit(write, 1, sequence);
            region.TryAcquireLatestRead(Request(sequence), out reads[sequence]).Should().BeTrue();
        }

        region.TryAcquireWrite(out _).Should().BeFalse();
        region.Stats().BlockedWrites.Should().Be(1);

        reads[1].Dispose();
        region.TryAcquireWrite(out var available).Should().BeTrue();
        available.SlotIndex.Should().Be(1);
        reads[0].Dispose();
        reads[2].Dispose();
    }

    [TestCase("wrong", "tests.frame.v2", 2, 9, 64, 0)]
    [TestCase("body", "tests.frame.v1", 2, 9, 64, 0)]
    [TestCase("body", "tests.frame.v2", 1, 9, 64, 0)]
    [TestCase("body", "tests.frame.v2", 2, 8, 64, 0)]
    [TestCase("body", "tests.frame.v2", 2, 9, 63, 0)]
    [TestCase("body", "tests.frame.v2", 2, 9, 64, 1)]
    public void RejectsMismatchedGenerationBeforeExposingBytes(
        string bodyId,
        string schemaId,
        int layoutVersion,
        long epoch,
        int capacity,
        long sequence)
    {
        using var region = Region();
        region.TryAcquireWrite(out var write).Should().BeTrue();
        write.Span[0] = 42;
        region.Commit(write, 1, 0);
        var request = new CultMeshBodyValidationRequest
        {
            BodyId = bodyId,
            SchemaId = schemaId,
            LayoutVersion = layoutVersion,
            ProducerEpoch = epoch,
            Capacity = capacity,
            Sequence = sequence,
            AccessMode = CultMeshBodyAccessMode.ReadOnly
        };

        Action read = () => region.TryAcquireLatestRead(request, out _);

        read.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ReadLeaseIsReadOnlyAndProtectsItsBytesAcrossLaterCommits()
    {
        using var region = Region();
        region.TryAcquireWrite(out var first).Should().BeTrue();
        first.Span[..4].Fill(11);
        region.Commit(first, 4, 0);
        region.TryAcquireLatestRead(Request(0), out var protectedRead).Should().BeTrue();

        for (var value = 12; value <= 13; value++)
        {
            region.TryAcquireWrite(out var write).Should().BeTrue();
            write.Span[..4].Fill((byte)value);
            region.Commit(write, 4, value);
        }

        protectedRead.Span.ToArray().Should().OnlyContain(value => value == 11);
        protectedRead.Dispose();
    }

    [Test]
    public async Task ConcurrentReadersNeverObserveTornBytesOrMetadata()
    {
        using var region = Region();
        var failures = new ConcurrentQueue<string>();

        for (var sequence = 0; sequence < 200; sequence++)
        {
            CultMeshFrameRegionWriteLease write;
            while (!region.TryAcquireWrite(out write))
                await Task.Yield();
            var marker = (byte)(sequence % 251 + 1);
            write.Span[..64].Fill(marker);
            region.Commit(
                write,
                64,
                sequence,
                metadata: new Dictionary<string, string> { ["marker"] = marker.ToString() });

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                if (!region.TryAcquireLatestRead(Request(), out var read)) return;
                using (read)
                {
                    var expected = byte.Parse(read.Generation.Metadata["marker"]);
                    if (read.Span.ToArray().Any(value => value != expected))
                        failures.Enqueue($"Torn generation {read.Generation.Descriptor.Sequence}");
                }
            }));
            await Task.WhenAll(readers);
        }

        failures.Should().BeEmpty();
        region.Stats().PublishedFrames.Should().Be(200);
        region.Stats().LatestSequence.Should().Be(199);
    }

    private static CultMeshFrameRegion Region() =>
        new("body", "tests.frame.v2", layoutVersion: 2, capacity: 64, producerEpoch: 9, slotByteLength: 64);

    private static CultMeshFrameBodyPublisher Publisher() =>
        new("body", "tests.frame.v2", layoutVersion: 2, capacity: 64, producerEpoch: 9, slotByteLength: 64);

    private static CultMeshBodyValidationRequest Request(CultMeshBodyDescriptor descriptor) => new()
    {
        BodyId = descriptor.BodyId,
        SchemaId = descriptor.SchemaId,
        LayoutVersion = descriptor.LayoutVersion,
        ProducerEpoch = descriptor.ProducerEpoch,
        Capacity = descriptor.Capacity,
        Sequence = descriptor.Sequence,
        AccessMode = CultMeshBodyAccessMode.ReadOnly
    };

    private static CultMeshBodyValidationRequest Request(long? sequence = null) => new()
    {
        BodyId = "body",
        SchemaId = "tests.frame.v2",
        LayoutVersion = 2,
        ProducerEpoch = 9,
        Capacity = 64,
        Sequence = sequence,
        AccessMode = CultMeshBodyAccessMode.ReadOnly
    };
}
