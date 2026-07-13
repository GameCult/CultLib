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

        public FakeProvider(string providerId, IEnumerable<CultMeshCdnArtifactChunk> chunks)
        {
            ProviderId = providerId;
            _chunks = chunks.ToDictionary(chunk => chunk.ChunkHash, Clone, StringComparer.Ordinal);
        }

        public string ProviderId { get; }
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);
        public int BlockFromRequest { get; set; } = int.MaxValue;
        public int DelayMilliseconds { get; set; }

        public async Task<CultMeshCdnArtifactChunk?> GetChunkAsync(CultMeshCdnChunkRef chunk, CancellationToken cancellationToken = default)
        {
            Requests.AddOrUpdate(chunk.ChunkHash, 1, (_, count) => count + 1);
            var request = Interlocked.Increment(ref _requestCount);
            if (request >= BlockFromRequest)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (DelayMilliseconds > 0)
                await Task.Delay(DelayMilliseconds, cancellationToken);
            return _chunks.TryGetValue(chunk.ChunkHash, out var value) ? Clone(value) : null;
        }
    }
}
