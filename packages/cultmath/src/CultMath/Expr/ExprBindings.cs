namespace CultMath.Expr;

/// <summary>
/// The param values of one graph for an evaluation batch: every param at its default unless overridden by name.
/// </summary>
public sealed class ExprBindings
{
    private ExprBindings(ExprGraph graph, float[] values)
    {
        Graph = graph;
        Values = values;
    }

    public ExprGraph Graph { get; }

    /// <summary>One value per param, in the graph's param order.</summary>
    internal float[] Values { get; }

    /// <summary>
    /// Binds <paramref name="graph"/>'s params: a name in <paramref name="overrides"/> replaces the param's default
    /// and every other param keeps it. A name the graph has no param for is refused.
    /// </summary>
    public static ExprBindings Bind(ExprGraph graph, IReadOnlyDictionary<string, float>? overrides = null)
    {
        var values = new float[graph.ParamNames.Count];
        for (var i = 0; i < values.Length; i++) values[i] = BitConverter.Int32BitsToSingle(graph.ParamDefaultBits[i]);
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                var index = graph.ParamIndex(pair.Key);
                if (index < 0) throw new ExprGraphException("unknown-param");
                values[index] = pair.Value;
            }
        }

        return new ExprBindings(graph, values);
    }
}
