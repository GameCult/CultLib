using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshDiscoveryServiceTests
{
    [Test]
    public async Task Resolve_QueriesSourcesConcurrentlyAndKeepsSuccessfulResult()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task<IReadOnlyList<CultMeshDiscoveryObservation>> Lookup(bool fail)
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await bothStarted.Task;
            if (fail) throw new IOException("source offline");
            return new[] { Observation("aetheria", "good", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed) };
        }

        using var service = new CultMeshDiscoveryService(
            new[]
            {
                new DelegateSource("dead", _ => Lookup(true)),
                new DelegateSource("good", _ => Lookup(false))
            },
            new CultMeshDiscoveryServiceOptions { Clock = clock });
        var observed = new List<CultMeshDiscoveryState>();
        using var subscription = service.Watch().Subscribe(state => observed.Add(state));

        var state = await service.ResolveAsync(new CultMeshDiscoveryQuery("odin:aetheria"));

        state.Freshness.Should().Be(CultMeshDiscoveryFreshness.Degraded);
        state.Candidates.Should().ContainSingle().Which.Descriptor.VerseId.Should().Be("aetheria");
        state.FailedSourceIds.Should().Equal("dead");
        observed.Should().ContainSingle().Which.Should().BeSameAs(state);
    }

    [Test]
    public async Task Resolve_SharesInflightLookupAcrossConcurrentCallers()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateSource("odin", async _ =>
        {
            await release.Task;
            return new[] { Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed) };
        });
        using var service = new CultMeshDiscoveryService(new[] { source }, new CultMeshDiscoveryServiceOptions { Clock = clock });
        var query = new CultMeshDiscoveryQuery("odin:aetheria");

        var first = service.ResolveAsync(query);
        var second = service.ResolveAsync(query);
        await WaitUntilAsync(() => source.LookupCount == 1);
        release.TrySetResult();
        var states = await Task.WhenAll(first, second);

        source.LookupCount.Should().Be(1);
        states[0].Should().BeSameAs(states[1]);
    }

    [Test]
    public async Task Resolve_CallerCancellationDoesNotCancelSharedLookup()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateSource("odin", async _ =>
        {
            await release.Task;
            return new[] { Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed) };
        });
        using var service = new CultMeshDiscoveryService(new[] { source }, new CultMeshDiscoveryServiceOptions { Clock = clock });
        using var cancelledCaller = new CancellationTokenSource();
        var query = new CultMeshDiscoveryQuery("odin:aetheria");

        var cancelled = service.ResolveAsync(query, cancelledCaller.Token);
        var survivor = service.ResolveAsync(query);
        await WaitUntilAsync(() => source.LookupCount == 1);
        cancelledCaller.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled);
        release.TrySetResult();

        (await survivor).Candidates.Should().ContainSingle();
        source.LookupCount.Should().Be(1);
    }

    [Test]
    public async Task Resolve_HonorsNegativeExpiryWithoutRepeatingLookup()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var source = new DelegateSource("odin", _ =>
            Task.FromException<IReadOnlyList<CultMeshDiscoveryObservation>>(new IOException("offline")));
        using var service = new CultMeshDiscoveryService(
            new[] { source },
            new CultMeshDiscoveryServiceOptions { Clock = clock, NegativeTtl = TimeSpan.FromSeconds(30) });
        var query = new CultMeshDiscoveryQuery("odin:aetheria");

        (await service.ResolveAsync(query)).Freshness.Should().Be(CultMeshDiscoveryFreshness.Unavailable);
        (await service.ResolveAsync(query)).Freshness.Should().Be(CultMeshDiscoveryFreshness.Unavailable);
        source.LookupCount.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(31));
        await service.ResolveAsync(query);
        source.LookupCount.Should().Be(2);
    }

    [Test]
    public async Task Resolve_KeepsDifferentlyConstrainedProjectionsIsolated()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var source = new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
            new[]
            {
                Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed),
                Observation("norn", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed)
            }));
        using var service = new CultMeshDiscoveryService(new[] { source }, new CultMeshDiscoveryServiceOptions { Clock = clock });

        var aetheria = await service.ResolveAsync(new CultMeshDiscoveryQuery("odin:games", new[] { "aetheria" }));
        var norn = await service.ResolveAsync(new CultMeshDiscoveryQuery("odin:games", new[] { "norn" }));

        aetheria.Candidates.Select(candidate => candidate.Descriptor.VerseId).Should().Equal("aetheria");
        norn.Candidates.Select(candidate => candidate.Descriptor.VerseId).Should().Equal("norn");
        aetheria.QueryKey.Should().NotBe(norn.QueryKey);
        source.LookupCount.Should().Be(2);
    }

    [Test]
    public async Task Resolve_PreservesExpiredLastKnownGoodAsStaleAfterFailure()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var failing = false;
        var source = new DelegateSource("odin", _ => failing
            ? Task.FromException<IReadOnlyList<CultMeshDiscoveryObservation>>(new IOException("partition"))
            : Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
                new[] { Observation("aetheria", "odin", clock, TimeSpan.FromSeconds(5), CultMeshDiscoveryTrust.Signed) }));
        using var service = new CultMeshDiscoveryService(new[] { source }, new CultMeshDiscoveryServiceOptions { Clock = clock });
        var query = new CultMeshDiscoveryQuery("odin:aetheria");

        (await service.ResolveAsync(query)).Freshness.Should().Be(CultMeshDiscoveryFreshness.Fresh);
        clock.Advance(TimeSpan.FromSeconds(6));
        failing = true;
        var stale = await service.ResolveAsync(query);

        stale.Freshness.Should().Be(CultMeshDiscoveryFreshness.Stale);
        stale.Candidates.Should().ContainSingle().Which.Descriptor.VerseId.Should().Be("aetheria");
    }

    [Test]
    public async Task Current_DerivesStaleStateImmediatelyAfterCandidateExpiry()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        using var service = new CultMeshDiscoveryService(
            new[]
            {
                new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
                    new[] { Observation("aetheria", "odin", clock, TimeSpan.FromSeconds(5), CultMeshDiscoveryTrust.Signed) }))
            },
            new CultMeshDiscoveryServiceOptions { Clock = clock });
        await service.ResolveAsync(new CultMeshDiscoveryQuery("odin:aetheria"));

        clock.Advance(TimeSpan.FromSeconds(6));

        service.Current("odin:aetheria")!.Freshness.Should().Be(CultMeshDiscoveryFreshness.Stale);
    }

    [Test]
    public async Task Resolve_SignedCandidateCannotBeOverriddenByRejectedObservation()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
        var signed = Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed, "rudp://trusted:3076");
        var poisoned = Observation("aetheria", "gossip", clock, TimeSpan.FromMinutes(2), CultMeshDiscoveryTrust.Rejected, "rudp://poison:3076");
        using var service = new CultMeshDiscoveryService(
            new[]
            {
                new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[] { signed })),
                new DelegateSource("gossip", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(new[] { poisoned }))
            },
            new CultMeshDiscoveryServiceOptions { Clock = clock });

        var state = await service.ResolveAsync(new CultMeshDiscoveryQuery("odin:aetheria"));

        state.Candidates.Should().ContainSingle();
        state.Candidates[0].SourceId.Should().Be("odin");
        state.Candidates[0].Descriptor.DiscoveryEndpoints.Should().Equal("rudp://trusted:3076");
    }

    [Test]
    public async Task CultCacheStore_ReconstructsLastKnownGoodAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "cultmesh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "discovery.cc");
        try
        {
            var clock = new ManualClock(new DateTimeOffset(2026, 7, 12, 20, 0, 0, TimeSpan.Zero));
            using (var cache = new CultCache())
            {
                cache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
                using var writer = new CultMeshDiscoveryService(
                    new[]
                    {
                        new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
                            new[] { Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed) }))
                    },
                    new CultMeshDiscoveryServiceOptions { Clock = clock, Store = new CultMeshCultCacheDiscoveryStore(cache) });
                (await writer.ResolveAsync(new CultMeshDiscoveryQuery("odin:aetheria"))).Freshness
                    .Should().Be(CultMeshDiscoveryFreshness.Fresh);
            }

            clock.Advance(TimeSpan.FromMinutes(2));
            using var restoredCache = new CultCache();
            restoredCache.AddBackingStore(new SingleFileMessagePackBackingStore(path));
            await restoredCache.PullAllBackingStoresAsync();
            using var reader = new CultMeshDiscoveryService(
                new[] { new DelegateSource("odin", _ => Task.FromException<IReadOnlyList<CultMeshDiscoveryObservation>>(new IOException("offline"))) },
                new CultMeshDiscoveryServiceOptions { Clock = clock, Store = new CultMeshCultCacheDiscoveryStore(restoredCache) });

            var restored = await reader.ResolveAsync(new CultMeshDiscoveryQuery("odin:aetheria"));
            restored.Freshness.Should().Be(CultMeshDiscoveryFreshness.Stale);
            restored.Candidates.Should().ContainSingle().Which.SourceId.Should().Be("odin");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Invalidate_BypassesFreshMemoryAndPersistedRoutesForTheNextLookup()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero));
        var route = "rudp://first:3076";
        var source = new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
            new[] { Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(5), CultMeshDiscoveryTrust.Signed, route) }));
        using var cache = new CultCache();
        using var service = new CultMeshDiscoveryService(
            new[] { source },
            new CultMeshDiscoveryServiceOptions
            {
                Clock = clock,
                Store = new CultMeshCultCacheDiscoveryStore(cache)
            });
        var query = new CultMeshDiscoveryQuery("aetheria", new[] { "aetheria" });

        var first = await service.ResolveAsync(query);
        route = "rudp://second:3076";
        service.Invalidate(query);
        var second = await service.ResolveAsync(query);

        first.Candidates.Single().Descriptor.DiscoveryEndpoints.Should().Equal("rudp://first:3076");
        second.Candidates.Single().Descriptor.DiscoveryEndpoints.Should().Equal("rudp://second:3076");
        source.LookupCount.Should().Be(2);
    }

    [Test]
    public async Task Resolve_RejectsVerseRouteThatDoesNotAdvertiseRequestedProvider()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero));
        var source = new DelegateSource("odin", _ => Task.FromResult<IReadOnlyList<CultMeshDiscoveryObservation>>(
            new[] { Observation("aetheria", "odin", clock, TimeSpan.FromMinutes(1), CultMeshDiscoveryTrust.Signed) }));
        using var service = new CultMeshDiscoveryService(
            new[] { source },
            new CultMeshDiscoveryServiceOptions { Clock = clock });

        var state = await service.ResolveAsync(new CultMeshDiscoveryQuery(
            "forged-provider",
            new[] { "aetheria" },
            "forged-provider"));

        state.Freshness.Should().Be(CultMeshDiscoveryFreshness.Unavailable);
        state.Candidates.Should().BeEmpty();
    }

    private static CultMeshDiscoveryObservation Observation(
        string verseId,
        string sourceId,
        ManualClock clock,
        TimeSpan ttl,
        CultMeshDiscoveryTrust trust,
        string endpoint = "rudp://aetheria:3076") => new(
        new CultMeshVerseDescriptor(
            verseId,
            "Aetheria",
            CultMeshVerseAuthorityModel.OperatorCluster,
            new CultMeshVerseCompatibility("cultmesh.v0", "rules"),
            new[] { endpoint },
            new[] { "odin:aetheria", "odin:games", "aetheria" }),
        sourceId,
        clock.UtcNow,
        clock.UtcNow + ttl,
        trust);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(1);
        condition().Should().BeTrue();
    }

    private sealed class DelegateSource : ICultMeshLookupSource
    {
        private readonly Func<CultMeshDiscoveryQuery, Task<IReadOnlyList<CultMeshDiscoveryObservation>>> _lookup;

        public DelegateSource(string sourceId, Func<CultMeshDiscoveryQuery, Task<IReadOnlyList<CultMeshDiscoveryObservation>>> lookup)
        {
            SourceId = sourceId;
            _lookup = lookup;
        }

        public string SourceId { get; }
        public int LookupCount => Volatile.Read(ref _lookupCount);

        public Task<IReadOnlyList<CultMeshDiscoveryObservation>> LookupAsync(
            CultMeshDiscoveryQuery query,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _lookupCount);
            return _lookup(query);
        }

        private int _lookupCount;
    }

    private sealed class ManualClock : ICultMeshClock
    {
        public ManualClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; private set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
