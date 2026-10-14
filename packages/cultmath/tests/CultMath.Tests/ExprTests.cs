using CultMath.Expr;
using Xunit;
using static CultMath.math;

namespace CultMath.Tests;

/// <summary>
/// CultMath.Expr: the evaluator equals the direct composition of the same math calls bit for bit (every op and a
/// corpus imitating Aetheria's trophic compositions), the builder is canonical and refuses at the call, substitution
/// builds stencils, constants and param defaults keep their bits, bindings resolve by name, the validator's rules
/// refuse each one by name, and no refusal echoes a value it was given.
///
/// Hand mutations these must kill: an op's implementation or operand order swapped in the table (the per-op
/// comparison); hash-consing disabled or the dead-node pass removed (BuildIsCanonical); Substitute leaving an input
/// unmapped (SubstituteEqualsOffsetEvaluation); a validator rule deleted (ValidatorRefusesEachRuleByName and the
/// builder refusals); a constant routed through float arithmetic (ConstantsKeepTheirBits).
/// </summary>
public sealed class ExprTests
{
    private const int Points = 4096;
    private const string Canary = "CANARY";

    private static readonly float[] Edge =
    {
        0f, -0f, 1f, -1f, 0.5f, 3f, 1e-30f, float.Epsilon, -float.Epsilon, 1.17549435e-38f / 2f,
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e30f, -1e30f,
    };

    private static bool Same(float a, float b) =>
        (float.IsNaN(a) && float.IsNaN(b)) || BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static float[] Column(System.Random rng, int points, float lo, float hi, double edgeShare)
    {
        var column = new float[points];
        for (var i = 0; i < points; i++)
            column[i] = rng.NextDouble() < edgeShare ? Edge[rng.Next(Edge.Length)] : lo + (hi - lo) * rng.NextSingle();
        return column;
    }

    private static float[] Pack(params float[][] columns) => columns.SelectMany(column => column).ToArray();

    private static float[] Run(ExprGraph graph, ExprBindings bindings, params float[][] columns)
    {
        var points = columns[0].Length;
        var outputs = new float[points * graph.OutputNames.Count];
        ExprEvaluator.Evaluate(graph, bindings, Pack(columns), outputs);
        return outputs;
    }

    private static void AssertSame(float[] expected, float[] actual, int offset, string what)
    {
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Same(expected[i], actual[offset + i]), $"{what} point {i}: expected bits {BitConverter.SingleToInt32Bits(expected[i]):X8}, got {BitConverter.SingleToInt32Bits(actual[offset + i]):X8}");
    }

    // The reference side is written by hand, beside the table and not from it.
    private static readonly Dictionary<string, Func<float[], float[]>> Reference = new()
    {
        ["add"] = a => new[] { a[0] + a[1] },
        ["sub"] = a => new[] { a[0] - a[1] },
        ["mul"] = a => new[] { a[0] * a[1] },
        ["div"] = a => new[] { a[0] / a[1] },
        ["neg"] = a => new[] { -a[0] },
        ["min"] = a => new[] { min(a[0], a[1]) },
        ["max"] = a => new[] { max(a[0], a[1]) },
        ["abs"] = a => new[] { abs(a[0]) },
        ["floor"] = a => new[] { floor(a[0]) },
        ["frac"] = a => new[] { frac(a[0]) },
        ["sqrt"] = a => new[] { sqrt(a[0]) },
        ["exp"] = a => new[] { exp(a[0]) },
        ["log"] = a => new[] { log(a[0]) },
        ["pow"] = a => new[] { pow(a[0], a[1]) },
        ["sin"] = a => new[] { sin(a[0]) },
        ["cos"] = a => new[] { cos(a[0]) },
        ["clamp"] = a => new[] { clamp(a[0], a[1], a[2]) },
        ["saturate"] = a => new[] { saturate(a[0]) },
        ["lerp"] = a => new[] { lerp(a[0], a[1], a[2]) },
        ["step"] = a => new[] { step(a[0], a[1]) },
        ["smoothstep"] = a => new[] { smoothstep(a[0], a[1], a[2]) },
        ["select"] = a => new[] { a[0] > a[1] ? a[2] : a[3] },
        ["snoise2"] = a => new[] { snoise(new float2(a[0], a[1])) },
        ["snoise3"] = a => new[] { snoise(new float3(a[0], a[1], a[2])) },
        ["cellular3"] = a =>
        {
            var c = cellular(new float3(a[0], a[1], a[2]));
            return new[] { c.nearest.x, c.nearest.y, c.nearest.z, c.nearest.w, c.edge.x, c.edge.y, c.edge.z, c.edge.w, c.id };
        },
    };

    [Fact]
    public void OpTableIsTheStableVocabulary()
    {
        var expected = new (string Name, int Arity, int Results)[]
        {
            ("add", 2, 1), ("sub", 2, 1), ("mul", 2, 1), ("div", 2, 1), ("neg", 1, 1), ("min", 2, 1), ("max", 2, 1),
            ("abs", 1, 1), ("floor", 1, 1), ("frac", 1, 1), ("sqrt", 1, 1), ("exp", 1, 1), ("log", 1, 1), ("pow", 2, 1),
            ("sin", 1, 1), ("cos", 1, 1), ("clamp", 3, 1), ("saturate", 1, 1), ("lerp", 3, 1), ("step", 2, 1),
            ("smoothstep", 3, 1), ("select", 4, 1), ("snoise2", 2, 1), ("snoise3", 3, 1), ("cellular3", 3, 9),
        };
        Assert.Equal(expected, ExprOps.All.Select(op => (op.Name, op.Arity, op.ResultCount)).ToArray());
        Assert.Equal(ExprOps.All.Count, Reference.Count);
        Assert.Equal(expected.Select(op => op.Name).OrderBy(name => name), Reference.Keys.OrderBy(name => name));
        Assert.Null(ExprOps.Find("input"));
        Assert.Null(ExprOps.Find("nope"));
        Assert.Same(ExprOps.All[3], ExprOps.Find("div"));
    }

    [Fact]
    public void EvaluatorEqualsDirectComposition()
    {
        var rng = new System.Random(20261014);
        foreach (var op in ExprOps.All)
        {
            var builder = new ExprBuilder();
            var inputs = Enumerable.Range(0, op.Arity).Select(i => builder.Input("i" + i)).ToArray();
            var call = builder.Call(op.Name, inputs);
            if (op.ResultCount == 1) builder.Output("r", call);
            else for (var k = 0; k < op.ResultCount; k++) builder.Output("r" + k, builder.Pick(call, k));
            var graph = builder.Build();

            var columns = Enumerable.Range(0, op.Arity).Select(_ => Column(rng, Points, -8f, 8f, 0.25)).ToArray();
            var outputs = Run(graph, ExprBindings.Bind(graph), columns);
            for (var k = 0; k < op.ResultCount; k++)
            {
                var expected = new float[Points];
                for (var p = 0; p < Points; p++)
                    expected[p] = Reference[op.Name](columns.Select(column => column[p]).ToArray())[k];
                AssertSame(expected, outputs, k * Points, op.Name + " result " + k);
            }
        }
    }

    [Fact]
    public void EvaluatorEqualsDirectCompositionOnTheCorpus()
    {
        var rng = new System.Random(77);
        var x = Column(rng, Points, -2f, 3f, 0.2);
        var z = Column(rng, Points, -500f, 500f, 0.1);

        // smooth: smoothstep(0.2, 0.8, v)
        var b = new ExprBuilder();
        b.Output("o", b.Call("smoothstep", b.Const(0.2f), b.Const(0.8f), b.Input("v")));
        var smooth = b.Build();
        AssertSame(x.Select(v => smoothstep(0.2f, 0.8f, v)).ToArray(), Run(smooth, ExprBindings.Bind(smooth), x), 0, "smooth");

        // holling: p * (1 + k) / (p + k), k a param
        b = new ExprBuilder();
        var p = b.Input("p");
        var k = b.Param("k", 0.25f);
        b.Output("o", b.Call("div", b.Call("mul", p, b.Call("add", b.Const(1f), k)), b.Call("add", p, k)));
        var holling = b.Build();
        AssertSame(x.Select(v => v * (1f + 0.25f) / (v + 0.25f)).ToArray(), Run(holling, ExprBindings.Bind(holling), x), 0, "holling");
        var overrides = new Dictionary<string, float> { ["k"] = 0.4f };
        AssertSame(x.Select(v => v * (1f + 0.4f) / (v + 0.4f)).ToArray(), Run(holling, ExprBindings.Bind(holling, overrides), x), 0, "holling k=0.4");

        // envelope: b^0.75
        b = new ExprBuilder();
        b.Output("o", b.Call("pow", b.Input("b"), b.Const(0.75f)));
        var envelope = b.Build();
        AssertSame(x.Select(v => pow(v, 0.75f)).ToArray(), Run(envelope, ExprBindings.Bind(envelope), x), 0, "envelope");

        // an 8-tap ring stencil of snoise2, with the centre, summed in order
        var (ring, offsets) = RingStencil(48f);
        var expected = new float[Points];
        for (var i = 0; i < Points; i++)
        {
            var sum = snoise(new float2(x[i] * 0.013f, z[i] * 0.017f));
            for (var t = 0; t < 8; t++) sum += snoise(new float2((x[i] + offsets[t].x) * 0.013f, (z[i] + offsets[t].y) * 0.017f));
            expected[i] = sum / 9f;
        }

        AssertSame(expected, Run(ring, ExprBindings.Bind(ring), x, z), 0, "ring stencil");

        // a cellular3 Pick chain: (edge.w - nearest.w) * id
        b = new ExprBuilder();
        var cell = b.Call("cellular3", b.Input("x"), b.Input("y"), b.Input("z"));
        b.Output("o", b.Call("mul", b.Call("sub", b.Pick(cell, 7), b.Pick(cell, 3)), b.Pick(cell, 8)));
        var chain = b.Build();
        var y = Column(rng, Points, -6f, 6f, 0.05);
        var cx = Column(rng, Points, -6f, 6f, 0.05);
        var cz = Column(rng, Points, -6f, 6f, 0.05);
        AssertSame(
            Enumerable.Range(0, Points).Select(i =>
            {
                var c = cellular(new float3(cx[i], y[i], cz[i]));
                return (c.edge.w - c.nearest.w) * c.id;
            }).ToArray(),
            Run(chain, ExprBindings.Bind(chain), cx, y, cz), 0, "cellular chain");
    }

    private static ExprGraph Field()
    {
        var b = new ExprBuilder();
        b.Output("f", b.Call("snoise2", b.Call("mul", b.Input("x"), b.Const(0.013f)), b.Call("mul", b.Input("z"), b.Const(0.017f))));
        return b.Build();
    }

    private static (ExprGraph Graph, (float x, float y)[] Offsets) RingStencil(float radius)
    {
        var field = Field();
        var offsets = Enumerable.Range(0, 8)
            .Select(t => (MathF.Cos(2f * MathF.PI * t / 8f) * radius, MathF.Sin(2f * MathF.PI * t / 8f) * radius))
            .ToArray();
        var b = new ExprBuilder();
        var x = b.Input("x");
        var z = b.Input("z");
        var sum = b.Substitute(field, new Dictionary<string, ExprValue> { ["x"] = x, ["z"] = z })["f"];
        foreach (var (ox, oz) in offsets)
        {
            var tap = b.Substitute(field, new Dictionary<string, ExprValue>
            {
                ["x"] = b.Call("add", x, b.Const(ox)),
                ["z"] = b.Call("add", z, b.Const(oz)),
            })["f"];
            sum = b.Call("add", sum, tap);
        }

        b.Output("o", b.Call("div", sum, b.Const(9f)));
        return (b.Build(), offsets);
    }

    [Fact]
    public void SubstituteEqualsOffsetEvaluation()
    {
        var rng = new System.Random(5);
        var x = Column(rng, Points, -500f, 500f, 0.05);
        var z = Column(rng, Points, -500f, 500f, 0.05);
        var field = Field();
        var (stencil, offsets) = RingStencil(48f);

        var centre = Run(field, ExprBindings.Bind(field), x, z);
        var sum = (float[])centre.Clone();
        for (var t = 0; t < 8; t++)
        {
            var tap = Run(field, ExprBindings.Bind(field), x.Select(v => v + offsets[t].x).ToArray(), z.Select(v => v + offsets[t].y).ToArray());
            for (var i = 0; i < Points; i++) sum[i] += tap[i];
        }

        var expected = sum.Select(s => s / 9f).ToArray();
        AssertSame(expected, Run(stencil, ExprBindings.Bind(stencil), x, z), 0, "substituted stencil");

        // The wrong input substituted (z only) is a different graph and differs.
        var b = new ExprBuilder();
        b.Input("x");
        var wz = b.Input("z");
        var wrong = b.Substitute(field, new Dictionary<string, ExprValue> { ["z"] = b.Call("add", wz, b.Const(offsets[1].y)) })["f"];
        b.Output("o", wrong);
        var wrongGraph = b.Build();
        var wrongOut = Run(wrongGraph, ExprBindings.Bind(wrongGraph), x, z);
        var offsetOut = Run(field, ExprBindings.Bind(field), x, z.Select(v => v + offsets[1].y).ToArray());
        AssertSame(offsetOut, wrongOut, 0, "z-only substitution equals z-only offset");
        var xOffset = Run(field, ExprBindings.Bind(field), x.Select(v => v + offsets[1].x).ToArray(), z.Select(v => v + offsets[1].y).ToArray());
        Assert.Contains(Enumerable.Range(0, Points), i => !Same(xOffset[i], wrongOut[i]));

        // Substitute declares the graph's ports it does not replace, and refuses a name the graph lacks.
        Assert.Equal(new[] { "x", "z" }, wrongGraph.InputNames);
        var half = new ExprBuilder();
        half.Output("f", half.Substitute(field, new Dictionary<string, ExprValue> { ["z"] = half.Const(1f) })["f"]);
        Assert.Equal(new[] { "x" }, half.Build().InputNames);
        var c = new ExprBuilder();
        var ex = Assert.Throws<ExprGraphException>(() => c.Substitute(field, new Dictionary<string, ExprValue> { [Canary] = c.Const(1f) }));
        Assert.Equal("unknown-input", ex.Rule);
        Assert.DoesNotContain(Canary, ex.Message);
        var d = new ExprBuilder();
        var foreign = Assert.Throws<ExprGraphException>(() => d.Substitute(field, new Dictionary<string, ExprValue> { ["x"] = c.Const(1f) }));
        Assert.Equal("foreign-operand", foreign.Rule);
        var cell = d.Call("cellular3", d.Const(0f), d.Const(0f), d.Const(0f));
        Assert.Equal("bare-multi-result", Assert.Throws<ExprGraphException>(() => d.Substitute(field, new Dictionary<string, ExprValue> { ["x"] = cell })).Rule);
    }

    [Fact]
    public void SubstituteCarriesParamsConstantsAndPicks()
    {
        // o = p * k + cellular(p, p, k).edge.w - 0.5, with k a param of default 0.25.
        var g = new ExprBuilder();
        var gp = g.Input("p");
        var gk = g.Param("k", 0.25f);
        var cell = g.Call("cellular3", gp, gp, gk);
        g.Output("o", g.Call("sub", g.Call("add", g.Call("mul", gp, gk), g.Pick(cell, 7)), g.Const(0.5f)));
        var graph = g.Build();

        var b = new ExprBuilder();
        var shifted = b.Call("add", b.Input("x"), b.Const(1.5f));
        b.Output("o", b.Substitute(graph, new Dictionary<string, ExprValue> { ["p"] = shifted })["o"]);
        var rebuilt = b.Build();

        Assert.Equal(new[] { "x" }, rebuilt.InputNames);
        Assert.Equal(graph.ParamNames, rebuilt.ParamNames);
        Assert.Equal(graph.ParamDefaultBits, rebuilt.ParamDefaultBits);
        var x = Column(new System.Random(3), 512, -5f, 5f, 0.1);
        foreach (var k in new[] { 0.25f, 0.9f })
        {
            var expected = x.Select(v =>
            {
                var p = v + 1.5f;
                return p * k + cellular(new float3(p, p, k)).edge.w - 0.5f;
            }).ToArray();
            var bindings = ExprBindings.Bind(rebuilt, new Dictionary<string, float> { ["k"] = k });
            AssertSame(expected, Run(rebuilt, bindings, x), 0, "substituted graph, k=" + k);
        }

        // A param met again with other default bits is a conflict, not a silent pick.
        var c = new ExprBuilder();
        c.Param("k", 0.5f);
        Assert.Equal("port-conflict", Refusal(() => c.Substitute(graph, new Dictionary<string, ExprValue>())).Rule);
    }

    private static string Signature(ExprGraph g) =>
        string.Join(";", g.InputNames) + "|" + string.Join(";", g.ParamNames.Zip(g.ParamDefaultBits, (n, v) => n + "=" + v)) + "|" +
        string.Join(";", g.OutputNames.Zip(g.OutputNodes, (n, v) => n + "=" + v)) + "|" +
        string.Join(";", g.Nodes.Select(n => n.WireName + "(" + string.Join(",", n.Operands) + ")" + n.Payload));

    private static ExprGraph Sample(bool withDeadNode)
    {
        var b = new ExprBuilder();
        var x = b.Input("x");
        var y = b.Input("y");
        b.Param("unused", 2f);
        var once = b.Call("mul", x, y);
        var twice = b.Call("mul", x, y);
        if (withDeadNode) b.Call("sin", b.Call("neg", y));
        b.Output("o", b.Call("add", once, twice));
        return b.Build();
    }

    [Fact]
    public void BuildIsCanonical()
    {
        var graph = Sample(false);
        Assert.Equal(Signature(graph), Signature(Sample(false)));

        Assert.Single(graph.Nodes, n => n.WireName == "mul");
        Assert.Equal(new[] { "input", "input", "mul", "add" }, graph.Nodes.Select(n => n.WireName));
        Assert.Equal(new[] { 2, 2 }, graph.Nodes[3].Operands);

        var withDead = Sample(true);
        Assert.Equal(Signature(graph), Signature(withDead));
        Assert.DoesNotContain(withDead.Nodes, n => n.WireName == "sin" || n.WireName == "neg");

        // The ports are the graph's contract: an unused param stays, and constants are told apart by bits.
        Assert.Equal(new[] { "unused" }, graph.ParamNames);
        var b = new ExprBuilder();
        var sameZero = b.Call("add", b.Const(0f), b.Const(0f));
        var minusZero = b.Const(-0f);
        b.Output("a", sameZero);
        b.Output("b", minusZero);
        var zeros = b.Build();
        Assert.Equal(new[] { 0, int.MinValue }, zeros.Nodes.Where(n => n.Kind == ExprNodeKind.Const).Select(n => n.Payload));
        Assert.Equal(3, zeros.Nodes.Count);
    }

    [Fact]
    public void ConstantsKeepTheirBits()
    {
        var nanBits = 0x7FC12345;
        var constants = new[] { -0f, BitConverter.Int32BitsToSingle(nanBits), BitConverter.Int32BitsToSingle(1) };
        var b = new ExprBuilder();
        for (var i = 0; i < constants.Length; i++) b.Output("c" + i, b.Const(constants[i]));
        b.Output("p", b.Param("p", BitConverter.Int32BitsToSingle(nanBits)));
        var graph = b.Build();

        Assert.Equal(new[] { unchecked((int)0x80000000), nanBits, 1 }, graph.Nodes.Where(n => n.Kind == ExprNodeKind.Const).Select(n => n.Payload));
        Assert.Equal(new[] { nanBits }, graph.ParamDefaultBits);
        var outputs = new float[graph.OutputNames.Count];
        ExprEvaluator.Evaluate(graph, ExprBindings.Bind(graph), ReadOnlySpan<float>.Empty, outputs);
        Assert.Equal(new[] { unchecked((int)0x80000000), nanBits, 1, nanBits }, outputs.Select(BitConverter.SingleToInt32Bits));
    }

    [Fact]
    public void BindingsResolveByName()
    {
        var b = new ExprBuilder();
        b.Output("o", b.Call("sub", b.Param("a", 1.5f), b.Param("b", 4f)));
        var graph = b.Build();
        float Eval(ExprBindings bindings)
        {
            var o = new float[1];
            ExprEvaluator.Evaluate(graph, bindings, ReadOnlySpan<float>.Empty, o);
            return o[0];
        }

        Assert.Equal(1.5f - 4f, Eval(ExprBindings.Bind(graph)));
        Assert.Equal(1.5f - 4f, Eval(ExprBindings.Bind(graph, new Dictionary<string, float>())));
        Assert.Equal(10f - 4f, Eval(ExprBindings.Bind(graph, new Dictionary<string, float> { ["a"] = 10f })));
        Assert.Equal(1.5f - 7f, Eval(ExprBindings.Bind(graph, new Dictionary<string, float> { ["b"] = 7f })));

        var ex = Assert.Throws<ExprGraphException>(() => ExprBindings.Bind(graph, new Dictionary<string, float> { [Canary] = 1f }));
        Assert.Equal("unknown-param", ex.Rule);
        Assert.DoesNotContain(Canary, ex.Message);

        var other = new ExprBuilder();
        other.Output("o", other.Const(1f));
        var foreign = other.Build();
        Assert.Equal("foreign-bindings", Assert.Throws<ExprGraphException>(() =>
            ExprEvaluator.Evaluate(graph, ExprBindings.Bind(foreign), ReadOnlySpan<float>.Empty, new float[1])).Rule);
    }

    [Fact]
    public void EvaluateRefusesWrongColumnShapes()
    {
        var b = new ExprBuilder();
        b.Output("o", b.Input("x"));
        b.Output("p", b.Call("neg", b.Input("x")));
        var graph = b.Build();
        var bindings = ExprBindings.Bind(graph);
        Assert.Equal("column-shape", Assert.Throws<ExprGraphException>(() => ExprEvaluator.Evaluate(graph, bindings, new float[4], new float[5])).Rule);
        Assert.Equal("column-shape", Assert.Throws<ExprGraphException>(() => ExprEvaluator.Evaluate(graph, bindings, new float[3], new float[4])).Rule);
        var outputs = new float[6];
        ExprEvaluator.Evaluate(graph, bindings, new[] { 1f, 2f, 3f }, outputs);
        Assert.Equal(new[] { 1f, 2f, 3f, -1f, -2f, -3f }, outputs);
    }

    private static ExprGraphException Refusal(Action action) => Assert.Throws<ExprGraphException>(action);

    [Fact]
    public void TheBuilderRefusesAtTheCall()
    {
        var b = new ExprBuilder();
        var x = b.Input("x");
        var cell = b.Call("cellular3", x, x, x);

        var unknown = Refusal(() => b.Call(Canary + "op", x));
        Assert.Equal("unknown-op", unknown.Rule);
        Assert.Equal(2, unknown.NodeIndex);
        Assert.DoesNotContain(Canary, unknown.Message);
        Assert.Equal("arity", Refusal(() => b.Call("add", x)).Rule);
        Assert.Equal("arity", Refusal(() => b.Call("neg", x, x)).Rule);
        Assert.Equal("pick-range", Refusal(() => b.Pick(x, 0)).Rule);
        Assert.Equal("pick-range", Refusal(() => b.Pick(cell, 9)).Rule);
        Assert.Equal("pick-range", Refusal(() => b.Pick(cell, -1)).Rule);
        Assert.Equal("bare-multi-result", Refusal(() => b.Call("neg", cell)).Rule);
        Assert.Equal("bare-multi-result", Refusal(() => b.Output("o", cell)).Rule);
        Assert.Equal("foreign-operand", Refusal(() => b.Call("neg", new ExprBuilder().Const(1f))).Rule);
        Assert.Equal("port-name", Refusal(() => b.Input(Canary + "-bad")).Rule);
        Assert.Equal("port-name", Refusal(() => b.Input("1x")).Rule);
        Assert.Equal("port-name", Refusal(() => b.Input("")).Rule);
        Assert.Equal("port-name", Refusal(() => b.Param("a b", 1f)).Rule);
        Assert.Equal("port-name", Refusal(() => b.Output("a.b", x)).Rule);
        Assert.Equal(b.Pick(cell, 8).ResultCount, 1);
        Assert.Equal(9, cell.ResultCount);

        b.Output("o", x);
        Assert.Equal("duplicate-port-name", Refusal(() => b.Output("o", x)).Rule);
        Assert.Same(x, b.Input("x"));
        b.Param("p", 1f);
        Assert.Equal("port-conflict", Refusal(() => b.Param("p", 2f)).Rule);
        Assert.Same(b.Param("p", 1f), b.Param("p", 1f));
        Assert.NotNull(b.Input("p"));
        Assert.Equal(new[] { "x", "p" }, b.Build().InputNames);
    }

    private static ExprNode[] N(params ExprNode[] nodes) => nodes;

    private static ExprGraph Make(
        ExprNode[] nodes,
        int version = 1,
        string[]? inputs = null,
        string[]? paramNames = null,
        int[]? paramBits = null,
        string[]? outputNames = null,
        int[]? outputNodes = null) =>
        ExprGraph.Create(
            version,
            inputs ?? new[] { "x" },
            paramNames ?? Array.Empty<string>(),
            paramBits ?? Array.Empty<int>(),
            outputNames ?? new[] { "o" },
            outputNodes ?? new[] { 0 },
            nodes);

    [Fact]
    public void ValidatorRefusesEachRuleByName()
    {
        void Rule(string rule, Action act, int node = -1)
        {
            var ex = Assert.Throws<ExprGraphException>(act);
            Assert.Equal(rule, ex.Rule);
            Assert.Equal(node, ex.NodeIndex);
        }

        var x = ExprNode.Input(0);
        Assert.Equal(1, Make(nodes: N(x)).Nodes.Count);
        Rule("version", () => Make(version: 2, nodes: N(x)));
        Rule("version", () => Make(version: 0, nodes: N(x)));
        Rule("port-count", () => Make(paramNames: new[] { "a" }, nodes: N(x)));
        Rule("port-count", () => Make(outputNames: new[] { "a", "b" }, nodes: N(x)));
        Rule("port-name", () => Make(inputs: new[] { "9" }, nodes: N(x)));
        Rule("duplicate-port-name", () => Make(inputs: new[] { "x", "x" }, nodes: N(x)));
        Rule("duplicate-port-name", () => Make(paramNames: new[] { "a", "a" }, paramBits: new[] { 0, 0 }, nodes: N(x)));
        Rule("duplicate-port-name", () => Make(outputNames: new[] { "a", "a" }, outputNodes: new[] { 0, 0 }, nodes: N(x)));
        Assert.Equal(1, Make(inputs: new[] { "x" }, paramNames: new[] { "x" }, paramBits: new[] { 0 }, nodes: N(x)).Nodes.Count);
        Rule("port-index", () => Make(nodes: N(ExprNode.Input(1))), 0);
        Rule("port-index", () => Make(nodes: N(ExprNode.Input(-1))), 0);
        Rule("port-index", () => Make(nodes: N(x, ExprNode.Param(0))), 1);
        Rule("arity", () => Make(nodes: N(ExprNode.FromWire("input", new[] { 0 }, 0))), 0);
        Rule("arity", () => Make(nodes: N(ExprNode.FromWire("const", new[] { 0 }, 0))), 0);
        Rule("arity", () => Make(nodes: N(x, ExprNode.FromWire("pick", new int[0], 0))), 1);
        Rule("payload", () => Make(nodes: N(x, ExprNode.FromWire("neg", new[] { 0 }, 7))), 1);
        Rule("output-node", () => Make(outputNodes: new[] { 1 }, nodes: N(x)));
        Rule("output-node", () => Make(outputNodes: new[] { -1 }, nodes: N(x)));
        Rule("bare-multi-result", () => Make(outputNodes: new[] { 1 }, nodes: N(x, ExprNode.Call("cellular3", new[] { 0, 0, 0 }))), 1);
        Rule("pick-range", () => Make(outputNodes: new[] { 1 }, nodes: N(x, ExprNode.Pick(0, 0))), 1);
        Rule("operand-order", () => Make(nodes: N(x, ExprNode.Pick(5, 0))), 1);
        Rule("operand-order", () => Make(nodes: N(x, ExprNode.Call("neg", new[] { -1 }))), 1);
    }

    [Fact]
    public void FromWireNamesTheFourLeafKindsAndCalls()
    {
        foreach (var kind in new[] { "input", "param", "const", "pick" })
            Assert.Equal(kind, ExprNode.FromWire(kind, Array.Empty<int>(), 0).WireName);
        Assert.Equal(ExprNodeKind.Call, ExprNode.FromWire("add", new[] { 0, 0 }, 0).Kind);
        Assert.Equal(ExprNodeKind.Call, ExprNode.FromWire("nope", Array.Empty<int>(), 0).Kind);
        Assert.Equal(new[] { ExprNodeKind.Input, ExprNodeKind.Param, ExprNodeKind.Const, ExprNodeKind.Pick },
            new[] { "input", "param", "const", "pick" }.Select(name => ExprNode.FromWire(name, Array.Empty<int>(), 0).Kind));
    }
}
