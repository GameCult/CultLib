using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshContentTransferTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cultmesh-transfer-tests", Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Test]
    public async Task FetchAsync_VerifiesChunksAndAtomicallyPublishesFinalBody()
    {
        var artifact = Artifact("verified", 4);
        using var cache = Cache();
        var provider = new FakeProvider("source", artifact.Chunks);
        var service = Service(cache, provider);

        var path = await service.FetchAsync(artifact.Manifest);

        File.ReadAllBytes(path).Should().Equal(Payload());
        Directory.GetFiles(_directory, "*.partial").Should().BeEmpty();
        cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest)).Should().BeNull();
    }

    [Test]
    public async Task FetchAsync_FailsOverAfterProviderCorruption()
    {
        var artifact = Artifact("failover", 4);
        using var cache = Cache();
        var corrupt = artifact.Chunks.Select(Clone).ToArray();
        corrupt[0].Payload[0] ^= 0xff;
        var first = new FakeProvider("corrupt", corrupt);
        var second = new FakeProvider("good", artifact.Chunks);

        var path = await Service(cache, first, second).FetchAsync(artifact.Manifest);

        File.ReadAllBytes(path).Should().Equal(Payload());
        first.Requests.Should().ContainKey(artifact.Manifest.Chunks[0].ChunkHash);
        second.Requests.Should().ContainKey(artifact.Manifest.Chunks[0].ChunkHash);
    }

    [Test]
    public async Task FetchAsync_CancellationLeavesVerifiedCheckpointAndResumeSkipsIt()
    {
        var artifact = Artifact("resume", 4);
        var statePath = Path.Combine(_directory, "transfer-state.cc");
        using var cache = DurableCache(statePath);
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider("source", artifact.Chunks) { BlockFromRequest = 2 };
        var service = Service(cache, provider);
        var fetch = service.FetchAsync(artifact.Manifest, cancellation.Token);

        await WaitUntilAsync(() => cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest))?.VerifiedChunkIndexes.Length == 1);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => fetch).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body")).Should().BeFalse();
        cache.Dispose();

        using var restoredCache = DurableCache(statePath);
        await restoredCache.PullAllBackingStoresAsync();
        restoredCache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest))
            .Should().NotBeNull("the verified checkpoint must survive a cache restart");
        var resumedProvider = new FakeProvider("resume", artifact.Chunks);
        var path = await Service(restoredCache, resumedProvider).FetchAsync(artifact.Manifest);

        File.ReadAllBytes(path).Should().Equal(Payload());
        resumedProvider.Requests.Should().NotContainKey(artifact.Manifest.Chunks[0].ChunkHash);
    }

    [Test]
    public async Task FetchAsync_RejectsForgedCheckpointAfterRehashingClaimedChunks()
    {
        var artifact = Artifact("forged", 4);
        using var cache = Cache();
        using var cancellation = new CancellationTokenSource();
        var firstProvider = new FakeProvider("first", artifact.Chunks) { BlockFromRequest = 2 };
        var firstFetch = Service(cache, firstProvider).FetchAsync(artifact.Manifest, cancellation.Token);
        await WaitUntilAsync(() => cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest))?.VerifiedChunkIndexes.Length == 1);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => firstFetch).Should().ThrowAsync<OperationCanceledException>();

        var partial = Path.Combine(_directory, "." + artifact.Manifest.ContentHash + ".partial");
        var bytes = File.ReadAllBytes(partial);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(partial, bytes);

        var provider = new FakeProvider("source", artifact.Chunks);
        await Service(cache, provider).FetchAsync(artifact.Manifest);

        provider.Requests.Values.Sum().Should().Be(artifact.Chunks.Count);
        File.ReadAllBytes(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body")).Should().Equal(Payload());
    }

    [Test]
    public async Task FetchAsync_FinalHashFailurePublishesNoBodyOrTrustedCheckpoint()
    {
        var artifact = Artifact("wrong-final", 4);
        artifact.Manifest.ContentHash = new string('0', 64);
        using var cache = Cache();

        await FluentActions.Awaiting(() => Service(cache, new FakeProvider("source", artifact.Chunks)).FetchAsync(artifact.Manifest))
            .Should().ThrowAsync<InvalidDataException>();

        File.Exists(Path.Combine(_directory, new string('0', 64) + ".body")).Should().BeFalse();
        cache.Get<CultMeshContentTransferStateDocument>(new CultRecordKey("mesh:content-transfer:" + new string('0', 64))).Should().BeNull();
    }

    [Test]
    public async Task FetchAsync_ConcurrentRequestsShareVerifiedWork()
    {
        var artifact = Artifact("shared", 4);
        using var cache = Cache();
        var provider = new FakeProvider("source", artifact.Chunks) { DelayMilliseconds = 20 };
        var service = Service(cache, provider);

        var paths = await Task.WhenAll(service.FetchAsync(artifact.Manifest), service.FetchAsync(artifact.Manifest));

        paths[0].Should().Be(paths[1]);
        provider.Requests.Values.Sum().Should().Be(artifact.Chunks.Count);
    }

    [Test]
    public async Task FetchAsync_PipelinesRequestsWithinConfiguredBoundAndCommitsInManifestOrder()
    {
        var artifact = Artifact("pipelined", 2);
        using var cache = Cache();
        var provider = new FakeProvider("source", artifact.Chunks)
        {
            DelayMilliseconds = 40,
            BlockedChunkHash = artifact.Chunks[0].ChunkHash
        };
        var service = new CultMeshContentTransferService(
            cache,
            new[] { provider },
            new CultMeshContentTransferOptions(_directory) { MaxConcurrentChunkRequests = 3 });

        var fetch = service.FetchAsync(artifact.Manifest);
        try
        {
            await provider.BlockedChunkRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => provider.Requests.Values.Sum() >= 3 && provider.ActiveRequests == 1);

            var pendingState = cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest));
            (pendingState?.VerifiedChunkIndexes ?? Array.Empty<int>()).Should().BeEmpty(
                "later chunks may finish fetching but cannot be checkpointed ahead of the first manifest chunk");
        }
        finally
        {
            provider.ReleaseBlockedChunk();
        }
        var path = await fetch;

        File.ReadAllBytes(path).Should().Equal(Payload());
        provider.MaxConcurrentRequests.Should().Be(3);
        provider.Requests.Values.Sum().Should().Be(artifact.Chunks.Count);
        cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest)).Should().BeNull();
    }

    [Test]
    public void Constructor_RejectsNonPositiveConcurrentChunkBound()
    {
        using var cache = Cache();
        var options = new CultMeshContentTransferOptions(_directory) { MaxConcurrentChunkRequests = 0 };

        FluentActions.Invoking(() => new CultMeshContentTransferService(
                cache,
                new[] { new FakeProvider("source", Array.Empty<CultMeshCdnArtifactChunk>()) },
                options))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FetchAsync_CheckpointsVerifiedWindowPeersWhenEarlierFetchFails()
    {
        var artifact = Artifact("failed-window", 2);
        using var cache = Cache();
        var provider = new FakeProvider("source", artifact.Chunks)
        {
            DelayMilliseconds = 80,
            FailedChunkHash = artifact.Chunks[0].ChunkHash
        };
        var service = new CultMeshContentTransferService(
            cache,
            new[] { provider },
            new CultMeshContentTransferOptions(_directory) { MaxConcurrentChunkRequests = 3 });

        await FluentActions.Awaiting(() => service.FetchAsync(artifact.Manifest))
            .Should().ThrowAsync<InvalidDataException>();

        provider.ActiveRequests.Should().Be(0, "the failed window must observe every task before returning");
        cache.Get<CultMeshContentTransferStateDocument>(StateKey(artifact.Manifest))!
            .VerifiedChunkIndexes.Should().Equal(1, 2);
        File.Exists(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body")).Should().BeFalse();
    }

    [Test]
    public async Task FetchMappedBodyAsync_MapsOnlyCommittedBodyForMultipleConsumersAndFallsBackToNetwork()
    {
        var artifact = Artifact("mapped", 4);
        using var cache = Cache();
        var broker = new CultMeshVerifiedBodyMappingBroker(_directory);
        var service = new CultMeshContentTransferService(
            cache,
            new[] { new FakeProvider("source", artifact.Chunks) },
            new CultMeshContentTransferOptions(_directory),
            broker);
        var now = DateTimeOffset.UtcNow;
        var network = NetworkDescriptor(artifact, now);

        var firstContent = await service.FetchMappedContentAsync(
            artifact.Manifest, network, now, TimeSpan.FromMinutes(1));
        var first = firstContent.Descriptor;
        var second = await service.FetchMappedBodyAsync(artifact.Manifest, network, now, TimeSpan.FromMinutes(1));

        first.Should().BeEquivalentTo(second, options => options.Excluding(value => value.CapabilityToken));
        firstContent.VerifiedPath.Should().Be(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body"));
        first.CapabilityToken.Should().NotBe(artifact.Manifest.ContentHash);
        first.CapabilityToken.Should().NotContain(Path.DirectorySeparatorChar.ToString());
        first.CapabilityToken.Should().NotContain(Path.AltDirectorySeparatorChar.ToString());
        Directory.GetFiles(_directory, "*.body").Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body"));
        Directory.GetFiles(_directory, "*.partial").Should().BeEmpty();

        var adapter = new CultMeshMappedBodyAdapter(broker);
        using (var firstLease = adapter.OpenReadOnly(first, Request(first, now)))
        using (var secondLease = adapter.OpenReadOnly(second, Request(second, now)))
        {
            var firstBytes = new byte[first.ByteSize];
            var secondBytes = new byte[second.ByteSize];
            firstLease.CopyTo(0, firstBytes, 0, firstBytes.Length);
            secondLease.CopyTo(0, secondBytes, 0, secondBytes.Length);
            firstBytes.Should().Equal(Payload());
            secondBytes.Should().Equal(Payload());
        }

        File.Delete(Path.Combine(_directory, artifact.Manifest.ContentHash + ".body"));
        var transport = new CultMeshBodyTransportService(
            new ICultMeshBodyTransportAdapter[]
            {
                adapter,
                new CultMeshNetworkBodyAdapter(_ => Payload())
            },
            _ => true);

        using var fallback = transport.OpenReadOnly(first, network, Request(first, now), out var localFailure);

        localFailure.Should().BeOfType<InvalidDataException>();
        fallback.TransportKind.Should().Be(CultMeshBodyTransportKind.Network);
        fallback.Descriptor.BodyId.Should().Be(first.BodyId);
    }

    private CultMeshContentTransferService Service(CultCache cache, params ICultMeshContentProvider[] providers) =>
        new(cache, providers, new CultMeshContentTransferOptions(_directory));

    private static CultCache Cache()
    {
        var registry = CultMesh.CreateCultCacheDocumentRegistry(typeof(CultMeshContentTransferStateDocument));
        return new CultCache(registry);
    }

    private static CultCache DurableCache(string path)
    {
        var cache = Cache();
        cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
        return cache;
    }

    private static CultMeshCdnArtifact Artifact(string id, int chunkSize) =>
        CultMesh.PackCdnArtifact(id, Payload(), new CultMeshCdnPackOptions { ChunkSizeBytes = chunkSize });

    private static byte[] Payload() => Enumerable.Range(0, 19).Select(value => (byte)(value + 1)).ToArray();

    private static CultRecordKey StateKey(CultMeshCdnArtifactManifest manifest) =>
        new("mesh:content-transfer:" + manifest.ContentHash);

    private static CultMeshBodyDescriptor NetworkDescriptor(CultMeshCdnArtifact artifact, DateTimeOffset now) => new()
    {
        BodyId = artifact.Manifest.ArtifactId,
        SchemaId = "gamecult.mesh.cdn-artifact.v1",
        LayoutVersion = 1,
        ByteSize = artifact.Manifest.SizeBytes,
        Capacity = checked((int)artifact.Manifest.SizeBytes),
        ProducerEpoch = 4,
        Sequence = 7,
        AccessMode = CultMeshBodyAccessMode.ReadOnly,
        Synchronization = CultMeshBodySynchronization.ImmutableSequence,
        LeaseExpiresAtUnixMs = now.AddMinutes(2).ToUnixTimeMilliseconds(),
        TransportKind = CultMeshBodyTransportKind.Network,
        SemanticHash = artifact.Manifest.ContentHash
    };

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

    private static CultMeshCdnArtifactChunk Clone(CultMeshCdnArtifactChunk chunk) => new()
    {
        ChunkHash = chunk.ChunkHash,
        SizeBytes = chunk.SizeBytes,
        Payload = chunk.Payload.ToArray()
    };

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not observed.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeProvider : ICultMeshContentProvider
    {
        private readonly Dictionary<string, CultMeshCdnArtifactChunk> _chunks;
        private int _requestCount;
        private int _activeRequests;
        private int _maxConcurrentRequests;
        private readonly TaskCompletionSource<bool> _releaseBlockedChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProvider(string providerId, IEnumerable<CultMeshCdnArtifactChunk> chunks)
        {
            ProviderId = providerId;
            _chunks = chunks.ToDictionary(chunk => chunk.ChunkHash, Clone, StringComparer.Ordinal);
        }

        public string ProviderId { get; }
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        public int BlockFromRequest { get; set; } = int.MaxValue;
        public int DelayMilliseconds { get; set; }
        public string? BlockedChunkHash { get; set; }
        public string? FailedChunkHash { get; set; }
        public TaskCompletionSource<bool> BlockedChunkRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveRequests => Volatile.Read(ref _activeRequests);
        public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

        public void ReleaseBlockedChunk() => _releaseBlockedChunk.TrySetResult(true);

        public async Task CopyChunkToAsync(
            CultMeshCdnChunkRef chunk,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Requests.AddOrUpdate(chunk.ChunkHash, 1, (_, count) => count + 1);
            var request = Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(ref _maxConcurrentRequests, active);
            try
            {
                if (request >= BlockFromRequest)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                if (string.Equals(chunk.ChunkHash, FailedChunkHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Synthetic chunk failure.");
                if (string.Equals(chunk.ChunkHash, BlockedChunkHash, StringComparison.Ordinal))
                {
                    BlockedChunkRequested.TrySetResult(true);
                    await _releaseBlockedChunk.Task.WaitAsync(cancellationToken);
                }
                if (DelayMilliseconds > 0)
                    await Task.Delay(DelayMilliseconds, cancellationToken);
                if (!_chunks.TryGetValue(chunk.ChunkHash, out var value))
                    throw new FileNotFoundException("Synthetic content chunk is missing.", chunk.RecordKey);
                await destination.WriteAsync(value.Payload, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            var observed = Volatile.Read(ref maximum);
            while (candidate > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }
}
