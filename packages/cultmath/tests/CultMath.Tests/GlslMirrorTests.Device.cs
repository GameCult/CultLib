using System.Globalization;
using System.Text.Json.Nodes;
using CultMath;
using Xunit;
using Xunit.Sdk;

namespace CultMath.Tests;

/// <summary>
/// The conforming run's C# half. <see cref="WriteWork"/> says what a device must compute: every fixture
/// family on the fixture's own arguments, plus one reference family for each sampled-form family, whose
/// points are the ones the form check evaluates. <see cref="JudgeDevice"/> says whether a device's results
/// hold: exact and integer families bit for bit, check families by their Verify, sampled-form families by
/// the form check on the device's own reference values, every other family within its recorded bound.
/// The generator, the Vulkan host and the job script only spell, dispatch and run; they compare nothing.
/// </summary>
public sealed partial class GlslMirrorTests
{
    private static readonly string[] DeviceRoutes = { "lavapipe-hlsl", "lavapipe-glsl" };

    private static string BoundsPath => Path.Combine(Root, "tests", "CultMath.Tests", "fixtures", "device-bounds-lavapipe.txt");

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static JsonArray FixtureFamilies() => JsonNode.Parse(File.ReadAllText(FixturePath))!["functions"]!.AsArray();

    private static string[] Sides(JsonNode? entry) => ((string)entry!).Split(" -> ");

    private static uint[] DevWords(string hex) => hex.Split(' ').Select(h => Convert.ToUInt32(h, 16)).ToArray();

    private static string DevHex(IEnumerable<uint> words) => string.Join(' ', words.Select(w => w.ToString("x8")));

    private static uint[] ReadWords(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var words = new uint[bytes.Length / 4];
        for (var i = 0; i < words.Length; i++)
            words[i] = BitConverter.ToUInt32(bytes, i * 4);
        return words;
    }

    private static void WriteWords(string path, IEnumerable<uint> words)
    {
        using var writer = new BinaryWriter(File.Create(path));
        foreach (var word in words)
            writer.Write(word);
    }

    private static bool IsSampledForm(Family family) =>
        family.Check is not null && (family.Glsl.Contains("cultmath_af_snoise(") || family.Glsl.Contains("cultmath_af_fbm("));

    private static string Tag(int family) => $"f{family:D3}";

    private static string RefTag(int family) => $"r{family:D3}";

    private sealed record WorkItem(string Tag, string Index, string Glsl, int N, int WordsIn, int WordsOut);

    // ---- What a device must compute ----

    private static List<WorkItem> WriteWork(string dir)
    {
        Directory.CreateDirectory(dir);
        var fixture = FixtureFamilies();
        Assert.Equal(Families.Length, fixture.Count);
        var items = new List<WorkItem>();
        for (var f = 0; f < Families.Length; f++)
        {
            Assert.Equal(Families[f].Glsl, (string?)fixture[f]!["glsl"]);
            var cases = fixture[f]!["cases"]!.AsArray();
            WriteWords(Path.Combine(dir, Tag(f) + ".in"), cases.SelectMany(c => DevWords(Sides(c)[0])));
            items.Add(new WorkItem(Tag(f), f.ToString(CultureInfo.InvariantCulture), Families[f].Glsl, cases.Count, DevWords(Sides(cases[0])[0]).Length, DevWords(Sides(cases[0])[1]).Length));
            if (IsSampledForm(Families[f]))
                items.Add(WriteReference(dir, f, cases));
        }

        File.WriteAllLines(Path.Combine(dir, "manifest.txt"), items.Select(i => string.Join('\t', i.Tag, i.Index, i.Glsl, i.N, i.WordsIn, i.WordsOut)));
        return items;
    }

    // The points the form check evaluates the reference at, in the order it evaluates them: the reference
    // func records p, and for fBm the case's octaves, lacunarity and gain, then answers as C# does.
    private static WorkItem WriteReference(string dir, int family, JsonArray cases)
    {
        var fbm = Families[family].Glsl.Contains("cultmath_af_fbm(");
        var recorded = new List<uint>();
        Func<string[], float3, float> recorder = (t, p) =>
        {
            recorded.AddRange(new[] { BitConverter.SingleToUInt32Bits(p.x), BitConverter.SingleToUInt32Bits(p.y), BitConverter.SingleToUInt32Bits(p.z) });
            if (fbm)
                recorded.AddRange(t[7..10].Select(h => Convert.ToUInt32(h, 16)));
            return fbm ? FbmReference(t, p) : SnoiseReference(t, p);
        };
        if (fbm)
            AssertFbmForm(cases, false, recorder);
        else
            AssertSnoiseForm(cases, false, recorder);

        var wordsIn = fbm ? 6 : 3;
        WriteWords(Path.Combine(dir, RefTag(family) + ".in"), recorded);
        return new WorkItem(RefTag(family), "ref:" + family.ToString(CultureInfo.InvariantCulture),
            fbm ? "vec4 cultmath_fbm_grad(vec3, int, float, float)" : "float cultmath_snoise(vec3)", recorded.Count / wordsIn, wordsIn, fbm ? 4 : 1);
    }

    [Fact]
    public void WriteDeviceWork()
    {
        var dir = Env("CULTMATH_DEVICE_WORK");
        Assert.SkipUnless(dir is not null, "CULTMATH_DEVICE_WORK=<dir> writes the work list a device runs");
        var items = WriteWork(dir!);
        output.WriteLine($"{items.Count} work items ({items.Count(i => i.Index.StartsWith("ref:"))} reference families) in {dir}");
    }

    // ---- Whether a device's results hold ----

    private sealed record BoundRow(string Route, int Index, string Glsl, string Tolerance, int Exact, double Worst, int NanClass);

    private sealed record Measured(BoundRow Row, string Class);

    private sealed record Judgement(List<string> Failures, List<Measured> Rows);

    // Which words of a family's result are floats; the rest are integers and must match bit for bit.
    private static IEnumerable<bool> FloatWords(Type type) =>
        type == typeof(float) ? new[] { true }
        : type == typeof(int) || type == typeof(uint) ? new[] { false }
        : type == typeof(float3x3) ? Enumerable.Repeat(true, 9)
        : type.GetFields().Where(f => !f.IsStatic).SelectMany(f => FloatWords(f.FieldType));

    // u1 = |device - fixture| / max(|fixture|, 1) / 2^-23 per float component, worst over the family; NaN and
    // Inf are compared as classes (NaN against not-NaN, an Inf against anything else), never by value.
    private static (int Exact, double Worst, int NanClass) Measure(JsonArray cases, bool[] floatWord, uint[] got)
    {
        int exact = 0, nanClass = 0;
        var worst = 0.0;
        var wordsOut = floatWord.Length;
        for (var c = 0; c < cases.Count; c++)
        {
            var want = DevWords(Sides(cases[c])[1]);
            var same = true;
            for (var w = 0; w < wordsOut; w++)
            {
                var have = got[c * wordsOut + w];
                if (have == want[w])
                    continue;
                same = false;
                if (!floatWord[w])
                {
                    nanClass++;
                    continue;
                }

                var e = BitConverter.UInt32BitsToSingle(want[w]);
                var h = BitConverter.UInt32BitsToSingle(have);
                if (float.IsNaN(e) || float.IsNaN(h))
                    nanClass += float.IsNaN(e) == float.IsNaN(h) ? 0 : 1;
                else if (float.IsInfinity(e) || float.IsInfinity(h))
                    nanClass += e == h ? 0 : 1;
                else
                    worst = Math.Max(worst, Math.Abs((double)e - h) / Math.Max(Math.Abs((double)e), 1.0) / Math.ScaleB(1.0, -23));
            }

            if (same)
                exact++;
        }

        return (exact, worst, nanClass);
    }

    // The device's own reference values, in the order WriteReference recorded their points. The check asks
    // for the reference at a point; the point it asks for must be the one the device was given at that place.
    private static Func<string[], float3, float> DeviceReference(string work, string routeResults, WorkItem reference)
    {
        var points = ReadWords(Path.Combine(work, reference.Tag + ".in"));
        var values = ReadWords(Path.Combine(routeResults, reference.Tag + ".out"));
        Assert.Equal(reference.N * reference.WordsOut, values.Length);
        var next = 0;
        return (_, p) =>
        {
            Assert.True(next < reference.N, "reference point asked for past the recorded work");
            var at = next * reference.WordsIn;
            Assert.True(points[at] == BitConverter.SingleToUInt32Bits(p.x) && points[at + 1] == BitConverter.SingleToUInt32Bits(p.y) && points[at + 2] == BitConverter.SingleToUInt32Bits(p.z),
                $"reference point {next} is not the point the device was given");
            return BitConverter.UInt32BitsToSingle(values[next++ * reference.WordsOut + reference.WordsOut - 1]);
        };
    }

    private static string FirstLine(Exception e)
    {
        var line = e.Message.Split('\n')[0];
        return line.Length > 300 ? line[..300] : line;
    }

    private static Judgement JudgeDevice(string work, string results, string renderer, string identity, string boundsPath, bool write)
    {
        var failures = new List<string>();
        var measured = new List<Measured>();
        Dictionary<(string, int), BoundRow>? recorded = null;
        if (!write)
        {
            if (!File.Exists(boundsPath))
                return new Judgement(new List<string> { "no recorded device bounds; record them with CULTMATH_WRITE_DEVICE_BOUNDS=1" }, measured);
            var (header, rows) = ReadBounds(boundsPath);
            if (header.GetValueOrDefault("renderer") != renderer || header.GetValueOrDefault("identity") != identity)
                return new Judgement(new List<string> { "the device or its toolchain is not the one the bounds were recorded on; re-record them with CULTMATH_WRITE_DEVICE_BOUNDS=1" }, measured);
            recorded = rows;
        }

        var fixture = FixtureFamilies();
        var items = File.ReadAllLines(Path.Combine(work, "manifest.txt")).Select(l => l.Split('\t'))
            .ToDictionary(c => c[1], c => new WorkItem(c[0], c[1], c[2], int.Parse(c[3]), int.Parse(c[4]), int.Parse(c[5])));
        foreach (var route in DeviceRoutes)
        for (var f = 0; f < Families.Length; f++)
        {
            var family = Families[f];
            var cases = fixture[f]!["cases"]!.AsArray();
            var name = $"{route} #{f} {family.Glsl}";
            var path = Path.Combine(results, route, Tag(f) + ".out");
            if (!File.Exists(path))
            {
                failures.Add($"{name}: missing {Tag(f)}.out");
                continue;
            }

            var floatWord = FloatWords(Resolve(family.Glsl).ReturnType).ToArray();
            var got = ReadWords(path);
            if (got.Length != cases.Count * floatWord.Length)
            {
                failures.Add($"{name}: {Tag(f)}.out holds {got.Length} words, not {cases.Count * floatWord.Length}");
                continue;
            }

            var (exact, worst, nanClass) = Measure(cases, floatWord, got);
            var exactClass = family.Tolerance == "exact" || !floatWord.Any(w => w);
            var kind = exactClass ? "exact" : IsSampledForm(family) ? "sampled" : family.Verify is not null ? "check" : "bound";
            measured.Add(new Measured(new BoundRow(route, f, family.Glsl, family.Tolerance, exact, worst, nanClass), kind));
            try
            {
                if (exactClass)
                    Assert.True(exact == cases.Count, $"{exact} of {cases.Count} cases are bit-identical to the fixture");
                else if (kind == "sampled")
                {
                    var reference = DeviceReference(work, Path.Combine(results, route), items["ref:" + f]);
                    if (family.Glsl.Contains("cultmath_af_fbm("))
                        AssertFbmForm(cases, false, reference);
                    else
                        AssertSnoiseForm(cases, false, reference);
                }
                else if (kind == "check")
                    family.Verify!(new JsonArray(cases.Select((c, i) => (JsonNode)JsonValue.Create(Sides(c)[0] + " -> " + DevHex(got.Skip(i * floatWord.Length).Take(floatWord.Length)))!).ToArray()));
            }
            catch (XunitException e)
            {
                failures.Add($"{name}: {FirstLine(e)}");
            }
        }

        if (write)
        {
            WriteBounds(boundsPath, renderer, identity, measured.Select(m => m.Row));
            recorded = measured.ToDictionary(m => (m.Row.Route, m.Row.Index), m => m.Row);
        }

        foreach (var m in measured)
        {
            var name = $"{m.Row.Route} #{m.Row.Index} {m.Row.Glsl}";
            if (!recorded!.TryGetValue((m.Row.Route, m.Row.Index), out var bound))
                failures.Add($"{name}: no recorded row; re-record with CULTMATH_WRITE_DEVICE_BOUNDS=1");
            else if (bound.Glsl != m.Row.Glsl || bound.Tolerance != m.Row.Tolerance)
                failures.Add($"{name}: the recorded row is for another family; re-record with CULTMATH_WRITE_DEVICE_BOUNDS=1");
            else if (m.Class == "bound" && (m.Row.Worst > bound.Worst || m.Row.NanClass > bound.NanClass))
                failures.Add($"{name}: worst u1 {m.Row.Worst:R} and {m.Row.NanClass} NaN-class mismatches, past the recorded {bound.Worst:R} and {bound.NanClass}");
        }

        return new Judgement(failures, measured);
    }

    // ---- The recorded bounds: plain text, tab-separated, one row per route and family ----

    private static void WriteBounds(string path, string renderer, string identity, IEnumerable<BoundRow> rows)
    {
        var lines = new List<string>
        {
            "# renderer\t" + renderer,
            "# identity\t" + identity,
            "# route\tindex\tglsl\ttolerance\texact cases\tworst u1\tNaN-class mismatches; written only by CULTMATH_WRITE_DEVICE_BOUNDS=1",
        };
        lines.AddRange(rows.OrderBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Index)
            .Select(r => string.Join('\t', r.Route, r.Index, r.Glsl, r.Tolerance, r.Exact, r.Worst.ToString("R", CultureInfo.InvariantCulture), r.NanClass)));
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private static (Dictionary<string, string> Header, Dictionary<(string, int), BoundRow> Rows) ReadBounds(string path)
    {
        var header = new Dictionary<string, string>();
        var rows = new Dictionary<(string, int), BoundRow>();
        foreach (var line in File.ReadAllLines(path))
        {
            var c = line.Split('\t');
            if (line.StartsWith("# renderer\t") || line.StartsWith("# identity\t"))
                header[c[0][2..]] = c[1];
            else if (!line.StartsWith('#') && line.Length > 0)
                rows[(c[0], int.Parse(c[1]))] = new BoundRow(c[0], int.Parse(c[1]), c[2], c[3], int.Parse(c[4]), double.Parse(c[5], CultureInfo.InvariantCulture), int.Parse(c[6]));
        }

        return (header, rows);
    }

    [Fact]
    public void DeviceResultsHoldFixture()
    {
        var results = Env("CULTMATH_DEVICE_RESULTS");
        Assert.SkipUnless(results is not null, "CULTMATH_DEVICE_RESULTS=<dir> (holding <route>/<tag>.out) judges a device's results");
        var work = Env("CULTMATH_DEVICE_WORK");
        var renderer = Env("CULTMATH_DEVICE_NAME");
        var identity = Env("CULTMATH_DEVICE_IDENTITY");
        Assert.True(work is not null && renderer is not null && identity is not null, "CULTMATH_DEVICE_WORK, CULTMATH_DEVICE_NAME and CULTMATH_DEVICE_IDENTITY are required beside CULTMATH_DEVICE_RESULTS");
        var judgement = JudgeDevice(work!, results!, renderer!, identity!, BoundsPath, Env("CULTMATH_WRITE_DEVICE_BOUNDS") == "1");
        foreach (var route in DeviceRoutes)
        {
            var rows = judgement.Rows.Where(m => m.Row.Route == route).ToList();
            output.WriteLine($"{route}: {rows.Count} families judged: " + string.Join(", ", rows.GroupBy(m => m.Class).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")));
            foreach (var m in rows.OrderByDescending(m => m.Row.Worst).Take(5))
                output.WriteLine($"  worst u1 {m.Row.Worst:R}, {m.Row.NanClass} NaN-class: #{m.Row.Index} {m.Row.Glsl} ({m.Class})");
        }

        foreach (var failure in judgement.Failures.Take(60))
            output.WriteLine("FAIL " + failure);
        Assert.True(judgement.Failures.Count == 0, $"{judgement.Failures.Count} device results do not hold the fixture; first: {judgement.Failures.FirstOrDefault()}");
    }

    // ---- The judge against simulated devices ----
    // A faithful device answers the fixture's own result bits and, for a reference family, C#'s snoise or
    // fBm at the recorded points. A test alters what one family or one route answers.

    private sealed record Simulated(string Root, string Work, string Results, string Bounds) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static Simulated Simulate(Func<string, WorkItem, uint[], uint[]>? alter = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cultmath-device-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(root, "work");
        var items = WriteWork(work);
        var fixture = FixtureFamilies();
        foreach (var route in DeviceRoutes)
        {
            Directory.CreateDirectory(Path.Combine(root, "results", route));
            foreach (var item in items)
            {
                uint[] words;
                if (item.Index.StartsWith("ref:"))
                {
                    var points = ReadWords(Path.Combine(work, item.Tag + ".in"));
                    words = new uint[item.N * item.WordsOut];
                    for (var k = 0; k < item.N; k++)
                    {
                        var at = k * item.WordsIn;
                        var p = new float3(BitConverter.UInt32BitsToSingle(points[at]), BitConverter.UInt32BitsToSingle(points[at + 1]), BitConverter.UInt32BitsToSingle(points[at + 2]));
                        var tokens = new string[10];
                        if (item.WordsIn == 6)
                            for (var t = 0; t < 3; t++)
                                tokens[7 + t] = points[at + 3 + t].ToString("x8");
                        words[k * item.WordsOut + item.WordsOut - 1] = BitConverter.SingleToUInt32Bits(item.WordsIn == 6 ? FbmReference(tokens, p) : SnoiseReference(tokens, p));
                    }
                }
                else
                    words = fixture[int.Parse(item.Index)]!["cases"]!.AsArray().SelectMany(c => DevWords(Sides(c)[1])).ToArray();

                WriteWords(Path.Combine(root, "results", route, item.Tag + ".out"), alter?.Invoke(route, item, words) ?? words);
            }
        }

        return new Simulated(root, work, Path.Combine(root, "results"), Path.Combine(root, "bounds.txt"));
    }

    private static Judgement Record(Simulated device) => JudgeDevice(device.Work, device.Results, "test renderer", "test identity", device.Bounds, true);

    private static Judgement Judge(Simulated device, Simulated recordedOn, string renderer = "test renderer", string identity = "test identity") =>
        JudgeDevice(device.Work, device.Results, renderer, identity, recordedOn.Bounds, false);

    // Every case's first result word, altered.
    private static uint[] Alter(WorkItem item, uint[] words, Func<uint, uint> change)
    {
        var altered = (uint[])words.Clone();
        for (var c = 0; c < item.N; c++)
            altered[c * item.WordsOut] = change(altered[c * item.WordsOut]);
        return altered;
    }

    // A family judged by its recorded bound alone: not exact, not checked, not a sampled form, a float first word.
    private static int BoundFamily() =>
        Array.FindIndex(Families, f => f.Tolerance == "ulp-bounded" && f.Verify is null && FloatWords(Resolve(f.Glsl).ReturnType).First());

    [Fact]
    public void AFaithfulDeviceHoldsEveryFamilyOnBothRoutes()
    {
        using var device = Simulate();
        var recorded = Record(device);
        Assert.Empty(recorded.Failures);
        Assert.Equal(DeviceRoutes.Length * Families.Length, recorded.Rows.Count);
        Assert.All(recorded.Rows, m => Assert.Equal(0.0, m.Row.Worst));
        Assert.Equal(DeviceRoutes.Length * Families.Count(IsSampledForm), recorded.Rows.Count(m => m.Class == "sampled"));
        Assert.Equal(DeviceRoutes.Length * Families.Count(f => f.Tolerance == "exact"), recorded.Rows.Count(m => m.Class == "exact"));
        Assert.Empty(Judge(device, device).Failures);
    }

    [Fact]
    public void AnExactFamilyAllowsNoDifferenceAtAll()
    {
        var exact = Array.FindIndex(Families, f => f.Tolerance == "exact");
        using var faithful = Simulate();
        Record(faithful);
        using var off = Simulate((route, item, words) => route == "lavapipe-glsl" && item.Index == exact.ToString() ? Alter(item, words, w => w ^ 1u) : words);
        var failures = Judge(off, faithful).Failures;
        Assert.Single(failures);
        Assert.Contains($"lavapipe-glsl #{exact} ", failures[0]);
    }

    [Fact]
    public void AFamilyIsHeldToItsRecordedWorstAndNotBeyond()
    {
        var f = BoundFamily();
        using var near = Simulate((_, item, words) => item.Index == f.ToString() ? Alter(item, words, w => w ^ 1u) : words);
        Record(near);
        Assert.Empty(Judge(near, near).Failures);
        using var farther = Simulate((_, item, words) => item.Index == f.ToString() ? Alter(item, words, w => w ^ 8u) : words);
        var failures = Judge(farther, near).Failures;
        Assert.Equal(DeviceRoutes.Length, failures.Count);
        Assert.All(failures, line => Assert.Contains($"#{f} ", line));

        var lines = File.ReadAllLines(near.Bounds).ToList();
        var at = lines.FindIndex(l => l.StartsWith($"lavapipe-hlsl\t{f}\t"));
        var row = lines[at].Split('\t');
        Assert.NotEqual(0.0, double.Parse(row[5], CultureInfo.InvariantCulture));
        row[5] = "0";
        lines[at] = string.Join('\t', row);
        File.WriteAllLines(near.Bounds, lines);
        Assert.Single(Judge(near, near).Failures);

        lines.RemoveAt(at);
        File.WriteAllLines(near.Bounds, lines);
        var missing = Judge(near, near).Failures;
        Assert.Single(missing);
        Assert.Contains("no recorded row", missing[0]);
    }

    [Fact]
    public void ABoundMeasuresTheErrorAgainstTheFixturesMagnitude()
    {
        // The metric divides by max(|fixture|, 1): a result of twice a fixture value of at least 2 is 2^23 away,
        // and of twice a fixture value under 1 is |fixture| 2^23 away.
        var f = BoundFamily();
        var cases = FixtureFamilies()[f]!["cases"]!.AsArray();
        float Fixture(int c) => BitConverter.UInt32BitsToSingle(DevWords(Sides(cases[c])[1])[0]);
        var large = Enumerable.Range(0, cases.Count).First(c => Math.Abs(Fixture(c)) is >= 2.0f and < 1e30f);
        var small = Enumerable.Range(0, cases.Count).First(c => Math.Abs(Fixture(c)) is > 1e-3f and < 1.0f);
        foreach (var (c, expected) in new[] { (large, Math.ScaleB(1.0, 23)), (small, Math.Abs((double)Fixture(small)) * Math.ScaleB(1.0, 23)) })
        {
            using var device = Simulate((_, item, words) =>
            {
                if (item.Index != f.ToString())
                    return words;
                var doubled = (uint[])words.Clone();
                doubled[c * item.WordsOut] = BitConverter.SingleToUInt32Bits(2.0f * BitConverter.UInt32BitsToSingle(words[c * item.WordsOut]));
                return doubled;
            });
            Assert.Equal(expected, Record(device).Rows.First(m => m.Row.Route == "lavapipe-hlsl" && m.Row.Index == f).Row.Worst);
        }
    }

    [Fact]
    public void ANaNWhereTheFixtureHasANumberFailsAFamilyRecordedWithout()
    {
        var f = BoundFamily();
        using var faithful = Simulate();
        Record(faithful);
        using var nan = Simulate((_, item, words) => item.Index == f.ToString() ? Alter(item, words, _ => 0x7fc00000u) : words);
        var failures = Judge(nan, faithful).Failures;
        Assert.Equal(DeviceRoutes.Length, failures.Count);
        Assert.All(failures, line => Assert.Contains("NaN-class", line));
    }

    [Fact]
    public void AMissingOutputFailsOnlyItsFamily()
    {
        using var device = Simulate();
        Record(device);
        File.Delete(Path.Combine(device.Results, "lavapipe-hlsl", Tag(3) + ".out"));
        var failures = Judge(device, device).Failures;
        Assert.Single(failures);
        Assert.Contains("missing", failures[0]);
        Assert.Contains("lavapipe-hlsl #3 ", failures[0]);
    }

    [Fact]
    public void ADeviceOrToolchainOtherThanTheRecordedOneIsRefusedBeforeJudging()
    {
        using var device = Simulate();
        Record(device);
        foreach (var (renderer, identity) in new[] { ("another renderer", "test identity"), ("test renderer", "another toolchain") })
        {
            var judgement = Judge(device, device, renderer, identity);
            Assert.Single(judgement.Failures);
            Assert.Empty(judgement.Rows);
        }
    }

    [Fact]
    public void ASampledFormIsJudgedOnTheDevicesOwnReferenceAtTheRecordedPoints()
    {
        var snoise = Array.FindIndex(Families, f => IsSampledForm(f) && f.Glsl.Contains("cultmath_af_snoise("));
        using var faithful = Simulate();
        Record(faithful);

        // A reference that answers wrongly puts the form's enclosure out of reach.
        using var wrong = Simulate((_, item, words) => item.Index == "ref:" + snoise
            ? words.Select(w => BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(w) + 100.0f)).ToArray() : words);
        var failures = Judge(wrong, faithful).Failures;
        Assert.Equal(DeviceRoutes.Length, failures.Count);
        Assert.All(failures, line => Assert.Contains($"#{snoise} ", line));

        // Two points in the other order: the device's i-th answer is not for the point the check asks about.
        using var swapped = Simulate();
        var path = Path.Combine(swapped.Work, RefTag(snoise) + ".in");
        var points = ReadWords(path);
        (points[0], points[3]) = (points[3], points[0]);
        WriteWords(path, points);
        var order = Judge(swapped, faithful).Failures;
        Assert.Equal(DeviceRoutes.Length, order.Count);
        Assert.All(order, line => Assert.Contains("not the point the device was given", line));
    }

    [Fact]
    public void ACheckFamilyIsJudgedByItsVerify()
    {
        var f = Array.FindIndex(Families, fam => fam.Verify is not null && !IsSampledForm(fam));
        using var faithful = Simulate();
        Record(faithful);
        using var broken = Simulate((_, item, words) => item.Index == f.ToString() ? words.Select(_ => 0x7fc00000u).ToArray() : words);
        var failures = Judge(broken, faithful).Failures;
        Assert.Equal(DeviceRoutes.Length, failures.Count);
        Assert.All(failures, line => Assert.Contains($"#{f} ", line));
    }
}
