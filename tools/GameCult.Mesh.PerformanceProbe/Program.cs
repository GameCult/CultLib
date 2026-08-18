using System.Diagnostics;
using System.Text.Json;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Mesh;
using MessagePack;
using R3;

var quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
var idleDuration = quick ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(10);
var activeDuration = quick ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10);
var documentCounts = quick ? new[] { 1, 100, 1_000 } : new[] { 1, 100, 1_000 };
var results = new List<ProbeResult>();

foreach (var documentCount in documentCounts)
    results.Add(await MeasureAsync(documentCount, idleDuration, activeDuration));
var hotBody = await MeasureHotBodyAsync(activeDuration);

Console.WriteLine(JsonSerializer.Serialize(new
{
    workload = new
    {
        documentCounts,
        payloadBytes = 16 * 1024,
        idleSeconds = idleDuration.TotalSeconds,
        activeSeconds = activeDuration.TotalSeconds,
        updateRateHz = 60,
        changedFraction = 0.01
    },
    results,
    hotBody
}, new JsonSerializerOptions { WriteIndented = true }));

return results.All(result =>
        result.ActualPublishes == result.ExpectedPublishes &&
        result.PayloadBytesPublished > 0 &&
        result.AllocationToPayloadRatio < 10 &&
        result.P99PublishLatencyMilliseconds < 250) &&
    hotBody.ActualFrames == hotBody.ExpectedFrames &&
    hotBody.UnavoidableCopyCount == 0 &&
    hotBody.BlockedWrites == 0 &&
    hotBody.P99RoundTripMilliseconds < 250
    ? 0
    : 1;

static async Task<HotBodyProbeResult> MeasureHotBodyAsync(TimeSpan activeDuration)
{
    const int payloadBytes = 16 * 1024;
    using var publisher = new CultMeshFrameBodyPublisher(
        "performance:hot-body",
        "gamecult.mesh.performance_hot_body.v1",
        layoutVersion: 1,
        capacity: payloadBytes,
        producerEpoch: 1,
        slotByteLength: payloadBytes,
        leaseDuration: TimeSpan.FromMinutes(1));
    if (!publisher.TryAcquireWrite(out var bootstrapWrite))
        throw new InvalidOperationException("Hot-body probe could not reserve its bootstrap slot.");
    CultMeshBodyDescriptor bootstrap;
    using (bootstrapWrite)
    {
        bootstrapWrite.Span[..payloadBytes].Fill(1);
        bootstrap = bootstrapWrite.Commit(payloadBytes, DateTimeOffset.UtcNow);
    }
    using var cursor = new CultMeshMappedFrameBodyCursor(bootstrap);
    if (!cursor.TryAcquireLatest(out var bootstrapRead))
        throw new InvalidOperationException("Hot-body probe could not acquire its bootstrap generation.");
    bootstrapRead.Dispose();

    ForceCollection();
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
    var privateBytesStart = process.PrivateMemorySize64;
    var cpuStart = process.TotalProcessorTime;
    var clock = Stopwatch.StartNew();
    var frameInterval = TimeSpan.FromSeconds(1.0 / 60.0);
    var latencies = new List<double>();
    var frames = 0;
    while (clock.Elapsed < activeDuration)
    {
        var started = Stopwatch.GetTimestamp();
        if (!publisher.TryAcquireWrite(out var write))
            throw new InvalidOperationException("Hot-body probe encountered unexpected write backpressure.");
        var marker = (byte)(frames % 251 + 1);
        using (write)
        {
            write.Span[..payloadBytes].Fill(marker);
            write.Commit(payloadBytes, DateTimeOffset.UtcNow);
        }
        if (!cursor.TryAcquireLatest(out var read))
            throw new InvalidOperationException("Hot-body cursor missed a committed generation.");
        using (read)
        {
            if (read.Descriptor.ByteSize != payloadBytes || read.ReadByte(0) != marker || read.ReadByte(payloadBytes - 1) != marker)
                throw new InvalidOperationException("Hot-body cursor observed torn or mismatched bytes.");
        }
        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        frames++;
        var remaining = frameInterval * frames - clock.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    process.Refresh();
    var stats = publisher.Stats();
    var ordered = latencies.OrderBy(value => value).ToArray();
    return new HotBodyProbeResult(
        ExpectedFrames: frames + 1,
        ActualFrames: checked((int)stats.PublishedFrames),
        PayloadBytes: payloadBytes,
        P50RoundTripMilliseconds: Percentile(ordered, 0.50),
        P95RoundTripMilliseconds: Percentile(ordered, 0.95),
        P99RoundTripMilliseconds: Percentile(ordered, 0.99),
        AllocatedBytes: GC.GetTotalAllocatedBytes(precise: true) - allocatedStart,
        PrivateBytesBefore: privateBytesStart,
        PrivateBytesAfter: process.PrivateMemorySize64,
        CpuMilliseconds: (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
        BlockedWrites: stats.BlockedWrites,
        UnavoidableCopyCount: stats.UnavoidableCopyCount);
}

static async Task<ProbeResult> MeasureAsync(
    int documentCount,
    TimeSpan idleDuration,
    TimeSpan activeDuration)
{
    var payload = new string('x', 16 * 1024);
    var changedDocumentCount = Math.Max(1, documentCount / 100);
    var subjects = new Subject<PerformanceDocument>[documentCount];
    var reactive = new CultMeshReactiveDocument<PerformanceDocument>[documentCount];
    var publishSignals = new TaskCompletionSource<bool>?[documentCount];
    var updateStartedAt = new long[documentCount];
    var latencies = new List<double>();
    var measurementClock = Stopwatch.StartNew();
    long payloadBytesPublished = 0;
    var publishes = 0;

    for (var index = 0; index < documentCount; index++)
    {
        var documentIndex = index;
        var current = new PerformanceDocument
        {
            Id = "performance:" + index,
            Payload = payload,
            Revision = 1
        };
        var subject = new Subject<PerformanceDocument>();
        subjects[index] = subject;
        var handle = CultMesh.Document(
            "performance:" + index,
            CultMesh.Verse("performance", "probe"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                Interlocked.Add(
                    ref payloadBytesPublished,
                    CultDocumentMessagePackSerialization.SerializeUntyped(value, typeof(PerformanceDocument)).Length);
                lock (latencies)
                {
                    latencies.Add(Stopwatch.GetElapsedTime(updateStartedAt[documentIndex]).TotalMilliseconds);
                }
                Interlocked.Increment(ref publishes);
                current = value;
                subject.OnNext(value);
                publishSignals[documentIndex]?.TrySetResult(true);
                return Task.CompletedTask;
            });
        reactive[index] = await handle.AuthoritativeWriter().ReactiveAsync(
            new CultMeshReactiveDocumentOptions { FlushDelay = TimeSpan.Zero });
    }

    try
    {
        ForceCollection();
        var process = Process.GetCurrentProcess();
        var idleAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
        var idleCpuStart = process.TotalProcessorTime;
        var idleThreadsStart = process.Threads.Count;
        await Task.Delay(idleDuration);
        process.Refresh();
        var idleAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - idleAllocatedStart;
        var idleCpu = process.TotalProcessorTime - idleCpuStart;
        var idleThreadsEnd = process.Threads.Count;

        ForceCollection();
        var activeAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
        var activeCpuStart = process.TotalProcessorTime;
        var activeThreadsStart = process.Threads.Count;
        var activeClock = Stopwatch.StartNew();
        var frameInterval = TimeSpan.FromSeconds(1.0 / 60.0);
        var frames = 0;
        while (activeClock.Elapsed < activeDuration)
        {
            var completions = new Task[changedDocumentCount];
            for (var index = 0; index < changedDocumentCount; index++)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                publishSignals[index] = completion;
                updateStartedAt[index] = Stopwatch.GetTimestamp();
                reactive[index].Update(document => document.Revision++);
                completions[index] = completion.Task;
            }
            await Task.WhenAll(completions);
            frames++;
            var nextFrameAt = frameInterval * frames;
            var remaining = nextFrameAt - activeClock.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }

        process.Refresh();
        var activeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - activeAllocatedStart;
        var activeCpu = process.TotalProcessorTime - activeCpuStart;
        var measuredActiveSeconds = activeClock.Elapsed.TotalSeconds;
        var activeThreadsEnd = process.Threads.Count;
        double[] orderedLatencies;
        lock (latencies) orderedLatencies = latencies.OrderBy(value => value).ToArray();

        return new ProbeResult(
            documentCount,
            changedDocumentCount,
            frames,
            frames * changedDocumentCount,
            publishes,
            payloadBytesPublished,
            idleAllocatedBytes,
            idleCpu.TotalMilliseconds,
            idleThreadsStart,
            idleThreadsEnd,
            activeAllocatedBytes,
            activeCpu.TotalMilliseconds,
            activeThreadsStart,
            activeThreadsEnd,
            activeAllocatedBytes / measuredActiveSeconds,
            payloadBytesPublished / measuredActiveSeconds,
            (double)activeAllocatedBytes / payloadBytesPublished,
            Percentile(orderedLatencies, 0.50),
            Percentile(orderedLatencies, 0.95),
            Percentile(orderedLatencies, 0.99),
            measurementClock.Elapsed.TotalSeconds);
    }
    finally
    {
        foreach (var document in reactive) document.Dispose();
        foreach (var subject in subjects) subject.Dispose();
    }
}

static double Percentile(double[] ordered, double percentile)
{
    if (ordered.Length == 0) return 0;
    var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
    return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
}

static void ForceCollection()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

internal sealed record ProbeResult(
    int DocumentCount,
    int ChangedDocumentCount,
    int Frames,
    int ExpectedPublishes,
    int ActualPublishes,
    long PayloadBytesPublished,
    long IdleAllocatedBytes,
    double IdleCpuMilliseconds,
    int IdleThreadsStart,
    int IdleThreadsEnd,
    long ActiveAllocatedBytes,
    double ActiveCpuMilliseconds,
    int ActiveThreadsStart,
    int ActiveThreadsEnd,
    double ActiveAllocatedBytesPerSecond,
    double PublishedPayloadBytesPerSecond,
    double AllocationToPayloadRatio,
    double P50PublishLatencyMilliseconds,
    double P95PublishLatencyMilliseconds,
    double P99PublishLatencyMilliseconds,
    double ScenarioWallSeconds);

internal sealed record HotBodyProbeResult(
    int ExpectedFrames,
    int ActualFrames,
    int PayloadBytes,
    double P50RoundTripMilliseconds,
    double P95RoundTripMilliseconds,
    double P99RoundTripMilliseconds,
    long AllocatedBytes,
    long PrivateBytesBefore,
    long PrivateBytesAfter,
    double CpuMilliseconds,
    ulong BlockedWrites,
    ulong UnavoidableCopyCount);

[CultDocument("gamecult.mesh.performance_probe", "gamecult.mesh.performance_probe.v1")]
[MessagePackObject]
public sealed class PerformanceDocument
{
    [Key(0), CultName] public string Id { get; set; } = string.Empty;
    [Key(1)] public string Payload { get; set; } = string.Empty;
    [Key(2)] public int Revision { get; set; }
}
