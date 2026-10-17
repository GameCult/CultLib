// Probe reference: writes the lattice cases (76 words each) and C#'s weighted_lattice_cells bits (4 words each).
// usage: Ref <in.bin> <ref.bin>; prints the case count.
using CultMath;
using static CultMath.math;

const int Max = 16;
var cases = new List<(float2 p, float t, float4[] b, float cell, float drift, float boost, float falloff, float s, float2 n, float st)>();
float4[] Bodies(float t, int count)
{
    var b = new float4[count];
    for (var i = 0; i < count; i++)
    {
        var a = 0.001f * (i + 1) * t + i; var r = 300.0f + 220.0f * i;
        b[i] = new float4(r * MathF.Cos(a), r * MathF.Sin(a), 0.0f, 40.0f + 40.0f * i);
    }
    return b;
}
// The ring-17 point of LatticeCellsTests.TheCapIsReachedAndMatchesTheFullBox, and its neighbours.
for (var j = 0; j < 8; j++)
    cases.Add((new float2(1446.7345f + 3.0f * j, -680.7637f), 97.69269f, Bodies(97.69269f, 6), 260.0f, 0.7f, 4.0f, 1.0e6f, 1.0f, new float2(0.6f, 0.8f), 2.0f));
var looks = new[] { (1.6f, 1.0f, 0.35f), (1.6f, 2.0f, 0.35f), (4.0f, 2.0f, 1.0f), (0.0f, 1.0f, 0.05f) };
var normals = new[] { new float2(0.6f, 0.8f), new float2(3.0f, 4.0f), new float2(3.0e-30f, -4.0e-30f), new float2(1.0e30f, 2.0e30f), new float2(-1.0f, 1.0e-3f) };
var rng = new System.Random(20261017);
float U(float a, float b) => a + (b - a) * (float)rng.NextDouble();
for (var i = 0; i < 248; i++)
{
    var (boost, stretch, s) = looks[i % 4];
    var n = normals[(i / 4) % normals.Length];
    var count = new[] { 6, 0, 1, 16, 6, 3 }[(i / 20) % 6];
    var t = U(0.0f, 200.0f);
    cases.Add((new float2(U(-3000, 3000), U(-3000, 3000)), t, Bodies(t, count), 260.0f, 0.7f, boost, 120.0f, s, n, stretch));
}
// Zero normal at stretch 1 is (1, 0).
cases.Add((new float2(10.0f, 20.0f), 1.0f, Bodies(1.0f, 6), 260.0f, 0.7f, 1.6f, 120.0f, 0.35f, new float2(0.0f, 0.0f), 1.0f));
using var input = new BinaryWriter(File.Create(args[0]));
using var reference = new BinaryWriter(File.Create(args[1]));
foreach (var c in cases)
{
    input.Write(c.p.x); input.Write(c.p.y);
    for (var k = 0; k < Max; k++)
    {
        var v = k < c.b.Length ? c.b[k] : new float4(0.0f, 0.0f, 0.0f, 0.0f);
        input.Write(v.x); input.Write(v.y); input.Write(v.z); input.Write(v.w);
    }
    input.Write(c.b.Length);
    foreach (var f in new[] { c.t, c.cell, c.drift, c.boost, c.falloff, c.s, c.n.x, c.n.y, c.st }) input.Write(f);
    var r = weighted_lattice_cells(c.p, c.t, c.b, c.cell, c.drift, c.boost, c.falloff, c.s, c.n, c.st);
    reference.Write(r.x); reference.Write(r.y); reference.Write(r.z); reference.Write(r.w);
}
Console.WriteLine(cases.Count);
