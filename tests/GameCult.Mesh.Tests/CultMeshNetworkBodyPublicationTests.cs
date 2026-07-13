using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshNetworkBodyPublicationTests
{
    private CultCache _cache = null!;
    private string _mappedRoot = null!;
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _cache = new CultCache();
        _mappedRoot = Path.Combine(Path.GetTempPath(), "cultmesh-network-body-tests", Guid.NewGuid().ToString("N"));
        _now = DateTimeOffset.UtcNow;
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        if (Directory.Exists(_mappedRoot)) Directory.Delete(_mappedRoot, true);
    }

    [Test]
    public async Task PublishAndFetch_RoundTripsThroughTypedCdnDocuments()
    {
        var bytes = Enumerable.Range(0, 41).Select(value => (byte)value).ToArray();
        var descriptor = await Publisher(chunkSizeBytes: 8).PublishAsync(Generation(), bytes);
        var resolver = new CultMeshNetworkBodyResolver(_cache);

        var fetched = resolver.Fetch(descriptor);

        descriptor.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
        descriptor.CapabilityToken.Should().NotBeNullOrWhiteSpace();
        fetched.Should().Equal(bytes);
        _cache.Get<CultMeshNetworkBodyDocument>(CultMeshNetworkBodyDocument.CreateRecordKey(descriptor.CapabilityToken))
            .Should().NotBeNull();
    }

    [Test]
    public async Task LocalAndNetworkDescriptors_AreAcceptedAsOneLogicalGeneration()
    {
        var bytes = BitConverter.GetBytes(42.25f);
        var generation = Generation();
        var local = new CultMeshMappedBodyPublisher(_mappedRoot, TimeSpan.FromMinutes(1)).Publish(
            generation.BodyId, generation.SchemaId, generation.LayoutVersion, generation.Capacity,
            generation.ProducerEpoch, generation.Sequence, bytes, _now);
        generation.LeaseExpiresAtUnixMs = local.LeaseExpiresAtUnixMs;
        var network = await Publisher().PublishAsync(generation, bytes);
        var service = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                new CultMeshMappedBodyAdapter(_mappedRoot),
                new CultMeshNetworkBodyAdapter(new CultMeshNetworkBodyResolver(_cache).CreateFetchDelegate())
            },
            _ => true);

        using var lease = service.NegotiateReadOnly(local, network, Request(network)).Lease;

        lease.TransportKind.Should().Be(CultMeshBodyTransportKind.SharedFileMapping);
        lease.ReadSingle(0).Should().Be(42.25f);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Fetch_RejectsMissingOrCorruptChunk(bool corrupt)
    {
        var descriptor = await Publisher(chunkSizeBytes: 2).PublishAsync(Generation(), new byte[] { 1, 2, 3, 4 });
        var binding = _cache.Get<CultMeshNetworkBodyDocument>(CultMeshNetworkBodyDocument.CreateRecordKey(descriptor.CapabilityToken))!;
        var manifest = _cache.Get<CultMeshCdnArtifactManifest>(new CultRecordKey(binding.ManifestRecordKey))!;
        var chunkKey = new CultRecordKey(manifest.Chunks[0].RecordKey);
        if (corrupt)
        {
            var chunk = _cache.Get<CultMeshCdnArtifactChunk>(chunkKey)!;
            chunk.Payload[0] ^= 0xff;
            await _cache.UpsertAsync(chunk, new CultRecordHandle<CultMeshCdnArtifactChunk>(chunkKey));
        }
        else
        {
            _cache.Remove(chunkKey);
        }

        Action fetch = () => new CultMeshNetworkBodyResolver(_cache).Fetch(descriptor);

        if (corrupt) fetch.Should().Throw<InvalidDataException>();
        else fetch.Should().Throw<FileNotFoundException>();
    }

    [Test]
    public async Task Fetch_RejectsStaleDescriptorGeneration()
    {
        var descriptor = await Publisher().PublishAsync(Generation(), new byte[] { 1, 2, 3, 4 });
        descriptor.Sequence--;

        Action fetch = () => new CultMeshNetworkBodyResolver(_cache).Fetch(descriptor);

        fetch.Should().Throw<InvalidDataException>().WithMessage("*capability binding*");
    }

    [Test]
    public async Task Publish_RejectsUnauthorizedProducerBeforeWritingDocuments()
    {
        var generation = Generation();
        var publisher = new CultMeshNetworkBodyPublisher(_cache, _ => false);

        Func<Task> publish = () => publisher.PublishAsync(generation, new byte[] { 1, 2, 3, 4 });

        await publish.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Test]
    public async Task Publish_RejectsMutationOfAnExistingGeneration()
    {
        var publisher = Publisher();
        var generation = Generation();
        await publisher.PublishAsync(generation, new byte[] { 1, 2, 3, 4 });

        Func<Task> republish = () => publisher.PublishAsync(generation, new byte[] { 1, 2, 3, 5 });

        await republish.Should().ThrowAsync<InvalidOperationException>().WithMessage("*immutable*");
    }

    private CultMeshNetworkBodyPublisher Publisher(int chunkSizeBytes = CultMeshCdnPackOptions.DefaultChunkSizeBytes) =>
        new(_cache, generation => generation.ProducerId == "aetheria", chunkSizeBytes);

    private CultMeshBodyGeneration Generation() => new()
    {
        BodyId = "aetheria:entities",
        ProducerId = "aetheria",
        SchemaId = "eve.entity_soa.v1",
        LayoutVersion = 2,
        Capacity = 128,
        ProducerEpoch = 7,
        Sequence = 42,
        Synchronization = CultMeshBodySynchronization.ImmutableSequence,
        LeaseExpiresAtUnixMs = _now.AddMinutes(1).ToUnixTimeMilliseconds()
    };

    private CultMeshBodyValidationRequest Request(CultMeshBodyDescriptor descriptor) => new()
    {
        BodyId = descriptor.BodyId,
        SchemaId = descriptor.SchemaId,
        LayoutVersion = descriptor.LayoutVersion,
        ProducerEpoch = descriptor.ProducerEpoch,
        Sequence = descriptor.Sequence,
        Capacity = descriptor.Capacity,
        AccessMode = CultMeshBodyAccessMode.ReadOnly,
        NowUtc = _now
    };
}
