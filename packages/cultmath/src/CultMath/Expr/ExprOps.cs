using static CultMath.math;

namespace CultMath.Expr;

/// <summary>One entry of the op table: a stable name, its arity, its result count and its C# implementation.</summary>
public sealed class ExprOp
{
    /// <summary>Writes <paramref name="results"/> (<see cref="ResultCount"/> floats) from <paramref name="operands"/>
    /// (<see cref="Arity"/> floats).</summary>
    public delegate void Function(ReadOnlySpan<float> operands, Span<float> results);

    private readonly Function function;

    internal ExprOp(string name, int arity, int resultCount, Function function)
    {
        Name = name;
        Arity = arity;
        ResultCount = resultCount;
        this.function = function;
    }

    /// <summary>The persisted name. Append-only: a released name keeps its meaning.</summary>
    public string Name { get; }

    public int Arity { get; }

    /// <summary>One for a scalar op; more for an op read only through <see cref="ExprBuilder.Pick"/>.</summary>
    public int ResultCount { get; }

    public void Evaluate(ReadOnlySpan<float> operands, Span<float> results) => function(operands, results);
}

/// <summary>
/// The one op table of the expression graph: each op names a CultMath function and calls it, wrapping no type
/// and copying no body. The C# evaluator reads it, and so does any later lowering.
/// </summary>
public static class ExprOps
{
    /// <summary>The largest arity in the table.</summary>
    internal const int MaxArity = 4;

    private static ExprOp Op(string name, int arity, ExprOp.Function function) => new(name, arity, 1, function);

    private static readonly ExprOp[] Table =
    {
        Op("add", 2, (a, r) => r[0] = a[0] + a[1]),
        Op("sub", 2, (a, r) => r[0] = a[0] - a[1]),
        Op("mul", 2, (a, r) => r[0] = a[0] * a[1]),
        Op("div", 2, (a, r) => r[0] = a[0] / a[1]),
        Op("neg", 1, (a, r) => r[0] = -a[0]),
        Op("min", 2, (a, r) => r[0] = min(a[0], a[1])),
        Op("max", 2, (a, r) => r[0] = max(a[0], a[1])),
        Op("abs", 1, (a, r) => r[0] = abs(a[0])),
        Op("floor", 1, (a, r) => r[0] = floor(a[0])),
        Op("frac", 1, (a, r) => r[0] = frac(a[0])),
        Op("sqrt", 1, (a, r) => r[0] = sqrt(a[0])),
        Op("exp", 1, (a, r) => r[0] = exp(a[0])),
        Op("log", 1, (a, r) => r[0] = log(a[0])),
        Op("pow", 2, (a, r) => r[0] = pow(a[0], a[1])),
        Op("sin", 1, (a, r) => r[0] = sin(a[0])),
        Op("cos", 1, (a, r) => r[0] = cos(a[0])),
        Op("clamp", 3, (a, r) => r[0] = clamp(a[0], a[1], a[2])),
        Op("saturate", 1, (a, r) => r[0] = saturate(a[0])),
        Op("lerp", 3, (a, r) => r[0] = lerp(a[0], a[1], a[2])),
        Op("step", 2, (a, r) => r[0] = step(a[0], a[1])),
        Op("smoothstep", 3, (a, r) => r[0] = smoothstep(a[0], a[1], a[2])),
        Op("select", 4, (a, r) => r[0] = select(a[0] > a[1], a[2], a[3])),
        Op("snoise2", 2, (a, r) => r[0] = snoise(new float2(a[0], a[1]))),
        Op("snoise3", 3, (a, r) => r[0] = snoise(new float3(a[0], a[1], a[2]))),
        new ExprOp("cellular3", 3, 9, (a, r) =>
        {
            var c = cellular(new float3(a[0], a[1], a[2]));
            r[0] = c.nearest.x; r[1] = c.nearest.y; r[2] = c.nearest.z; r[3] = c.nearest.w;
            r[4] = c.edge.x; r[5] = c.edge.y; r[6] = c.edge.z; r[7] = c.edge.w;
            r[8] = c.id;
        }),
    };

    private static readonly Dictionary<string, ExprOp> ByName = Table.ToDictionary(op => op.Name);

    /// <summary>Every op, in declaration order.</summary>
    public static IReadOnlyList<ExprOp> All => Table;

    /// <summary>The op with this persisted name, or null.</summary>
    public static ExprOp? Find(string name) => ByName.TryGetValue(name, out var op) ? op : null;
}
