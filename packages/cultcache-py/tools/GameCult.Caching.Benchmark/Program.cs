using System.Diagnostics;
using System.Text.Json;
using GameCult.Caching;

var records = 5000;
var emitJson = false;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--records" && index + 1 < args.Length)
    {
        records = int.Parse(args[++index]);
    }
    else if (args[index] == "--json")
    {
        emitJson = true;
    }
}

if (records <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(records), "--records must be greater than zero.");
}

var result = Benchmark.Run(records);
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
if (emitJson)
{
    Console.WriteLine(JsonSerializer.Serialize(result, options));
}
else
{
    Console.WriteLine($"records: {result.Records}");
    foreach (var metric in result.Metrics)
    {
        Console.WriteLine($"{metric.Name}: {metric.OpsPerSecond:F0} ops/s ({metric.ElapsedMs:F2} ms)");
    }
}

internal static class Benchmark
{
    public static BenchmarkResult Run(int records)
    {
        _ = CultDocumentRegistry.Shared.GetRequired<BenchItem>();
        var values = Enumerable.Range(0, records)
            .Select(index => new BenchItem
            {
                Name = $"item-{index}",
                Category = $"cat-{index % 8}",
                Value = index
            })
            .ToArray();

        var cache = new CultCache();
        var keys = Enumerable.Range(0, records)
            .Select(index => new CultRecordKey($"item:{index}"))
            .ToArray();

        var upsertMetric = Measure("cache_upsert", records, () =>
        {
            for (var index = 0; index < records; index++)
            {
                cache.UpsertAsync(values[index], new CultRecordHandle<BenchItem>(keys[index]))
                    .GetAwaiter()
                    .GetResult();
            }
        });
        var getMetric = Measure("cache_get", records, () =>
        {
            for (var index = 0; index < records; index++)
            {
                _ = cache.Get<BenchItem>(keys[index]);
            }
        });

        return new BenchmarkResult("csharp", records, new[] { upsertMetric, getMetric });
    }

    private static BenchmarkMetric Measure(string name, int operations, Action action)
    {
        var started = Stopwatch.StartNew();
        action();
        started.Stop();
        return new BenchmarkMetric(
            name,
            operations,
            started.Elapsed.TotalMilliseconds,
            operations / started.Elapsed.TotalSeconds);
    }
}

internal sealed record BenchmarkResult(string Runtime, int Records, IReadOnlyList<BenchmarkMetric> Metrics);

internal sealed record BenchmarkMetric(string Name, int Operations, double ElapsedMs, double OpsPerSecond);

[CultDocument("bench.item", "bench.item.v1")]
internal sealed class BenchItem
{
    [CultName]
    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int Value { get; set; }
}
