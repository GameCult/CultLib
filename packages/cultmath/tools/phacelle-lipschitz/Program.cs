// Measures the Lipschitz constant of math.phacelle's (cosine, sine) field, the number docs/design.md quotes and
// PhacelleTests.GradientNormStaysUnderTheDocumentedLipschitzEnvelope pins.
//   dotnet run -c Release --project packages/cultmath/tools/phacelle-lipschitz -- <samples per cell> <normalizations> <side lengths>
//   e.g. ... -- 4000000 0,0.5,0.75,0.9 0.25,0.5,1,1.5,2,4,8,12.5,25
// Columns: sampledMax (random p), climbedMax (hill-climbed from the 256 best samples; the number to quote), secantMax
// (value differences around the climbed argmax, the check that the analytic gradient is the field's slope),
// scalarMax (the larger of |grad cosine|, |grad sine| over the samples), p99.99 (the typical tail), and climbed*(1-n)/|side|.
using System;
using System.Linq;
using System.Threading.Tasks;
using CultMath;

// Lipschitz bound of math.phacelle's (cosine, sine) field: sup over p of the operator norm of its 2x3 Jacobian,
// found by (1) random sampling of the exact analytic gradient, (2) hill-climbing from the top samples over
// (p, off, side direction), (3) a secant check on the VALUES (|f(p+d) - f(p)| / |d|) so the gradient itself is not trusted.
static double Op(float3 p, float3 side, float off, float n, out double comp)
{
    var r = math.phacelle(p, side, off, n);
    double a = r.cosine.x*r.cosine.x + r.cosine.y*r.cosine.y + r.cosine.z*r.cosine.z;
    double d = r.sine.x*r.sine.x + r.sine.y*r.sine.y + r.sine.z*r.sine.z;
    double b = r.cosine.x*r.sine.x + r.cosine.y*r.sine.y + r.cosine.z*r.sine.z;
    double tr = a + d, det = a*d - b*b;
    comp = Math.Sqrt(Math.Max(a, d));
    var op = Math.Sqrt(tr/2 + Math.Sqrt(Math.Max(0, tr*tr/4 - det)));
    return double.IsFinite(op) ? op : 0;
}
static float3 Dir(double u, double th, double len) { var s = Math.Sqrt(Math.Max(0, 1-u*u)); return new float3((float)(s*Math.Cos(th)*len), (float)(s*Math.Sin(th)*len), (float)(u*len)); }

long N = long.Parse(args[0]);
var norms = args[1].Split(',').Select(float.Parse).ToArray();
var sides = args[2].Split(',').Select(float.Parse).ToArray();
Console.WriteLine("n\t|side|\tN\tsampledMax\tclimbedMax\tsecantMax\tscalarMax(cos|sin)\tp99.99\tclimbed*(1-n)/|side|");
foreach (var n in norms) foreach (var sl in sides)
{
    int T = Environment.ProcessorCount;
    var tops = new (double v, float3 p, double u, double th, float off)[T][];
    var allv = new double[T][]; var scal = new double[T];
    Parallel.For(0, T, t =>
    {
        var rng = new System.Random(unchecked(99991 * t + (int)(n * 1000) * 131 + (int)(sl * 100)));
        var top = new (double v, float3 p, double u, double th, float off)[16];
        var list = new double[N / T];
        double sc = 0;
        for (long i = 0; i < N / T; i++)
        {
            var p = new float3((float)(rng.NextDouble()*64-32), (float)(rng.NextDouble()*64-32), (float)(rng.NextDouble()*64-32));
            double u = rng.NextDouble()*2-1, th = rng.NextDouble()*Math.PI*2; float off = (float)rng.NextDouble();
            var v = Op(p, Dir(u, th, sl), off, n, out var c);
            list[i] = v; if (c > sc) sc = c;
            int m = 0; for (int k = 1; k < 16; k++) if (top[k].v < top[m].v) m = k;
            if (v > top[m].v) top[m] = (v, p, u, th, off);
        }
        tops[t] = top; allv[t] = list; scal[t] = sc;
    });
    var all = allv.SelectMany(x => x).ToArray(); Array.Sort(all);
    double sampled = all[^1], p9999 = all[(int)(all.Length * 0.9999)];
    // hill-climb the best 16*T by random local search
    var seeds = tops.SelectMany(x => x).OrderByDescending(x => x.v).Take(256).ToArray();
    var climbed = new (double v, float3 p, double u, double th, float off)[seeds.Length];
    Parallel.For(0, seeds.Length, s =>
    {
        var rng = new System.Random(s + 17);
        var cur = seeds[s]; double step = 0.05;
        for (int it = 0; it < 10000; it++)
        {
            var q = new float3(cur.p.x + (float)((rng.NextDouble()-.5)*step), cur.p.y + (float)((rng.NextDouble()-.5)*step), cur.p.z + (float)((rng.NextDouble()-.5)*step));
            var u = Math.Clamp(cur.u + (rng.NextDouble()-.5)*step, -1, 1); var th = cur.th + (rng.NextDouble()-.5)*step; var off = cur.off + (float)((rng.NextDouble()-.5)*step);
            var v = Op(q, Dir(u, th, sl), off, n, out _);
            if (v > cur.v) cur = (v, q, u, th, off); else if (it % 1000 == 999) step *= 0.5;
        }
        climbed[s] = cur;
    });
    var best = climbed.OrderByDescending(x => x.v).First();
    // secant check around the climbed argmax: values only, tiny displacements in random directions
    double secant = 0; var sr = new System.Random(5);
    var side0 = Dir(best.u, best.th, sl);
    for (int i = 0; i < 20000; i++)
    {
        double e = Math.Pow(10, -2 - 2*sr.NextDouble()); var d = Dir(sr.NextDouble()*2-1, sr.NextDouble()*6.2832, e);
        var a = math.phacelle(best.p, side0, best.off, n); var b = math.phacelle(best.p + d, side0, best.off, n);
        double dc = b.cosine.w - a.cosine.w, ds = b.sine.w - a.sine.w; var dl = Math.Sqrt((double)d.x*d.x + (double)d.y*d.y + (double)d.z*d.z);
        secant = Math.Max(secant, Math.Sqrt(dc*dc + ds*ds) / dl);
    }
    Console.WriteLine($"{n}\t{sl}\t{N}\t{sampled:F3}\t{best.v:F3}\t{secant:F3}\t{scal.Max():F3}\t{p9999:F3}\t{best.v*(1-n)/sl:F3}");
}
