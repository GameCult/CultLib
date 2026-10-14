namespace CultMath.Expr;

/// <summary>
/// Evaluates an <see cref="ExprGraph"/> per point: nodes in order through <see cref="ExprOps"/>, IEEE single per op,
/// so a result equals the direct composition of the same math calls bit for bit. Allocates once per call, never per point.
/// </summary>
public static class ExprEvaluator
{
    /// <summary>
    /// Columns are contiguous: input <c>i</c> of point <c>p</c> is <c>inputs[i * n + p]</c>, and output <c>o</c> is
    /// <c>outputs[o * n + p]</c>, where n = <c>outputs.Length / graph.OutputNames.Count</c>.
    /// </summary>
    public static void Evaluate(ExprGraph graph, ExprBindings bindings, ReadOnlySpan<float> inputs, Span<float> outputs)
    {
        if (bindings.Graph != graph) throw new ExprGraphException("foreign-bindings");
        var outputCount = graph.OutputNames.Count;
        if (outputCount == 0) return;
        if (outputs.Length % outputCount != 0) throw new ExprGraphException("column-shape");
        var points = outputs.Length / outputCount;
        if (inputs.Length != points * graph.InputNames.Count) throw new ExprGraphException("column-shape");

        var nodes = graph.NodeArray;
        var outputNodes = graph.OutputNodeArray;
        var start = graph.SlotStart;
        var slots = new float[graph.SlotCount];
        Span<float> operands = stackalloc float[ExprOps.MaxArity];
        for (var point = 0; point < points; point++)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = nodes[i];
                switch (node.Kind)
                {
                    case ExprNodeKind.Input:
                        slots[start[i]] = inputs[node.Payload * points + point];
                        break;
                    case ExprNodeKind.Param:
                        slots[start[i]] = bindings.Values[node.Payload];
                        break;
                    case ExprNodeKind.Const:
                        slots[start[i]] = BitConverter.Int32BitsToSingle(node.Payload);
                        break;
                    case ExprNodeKind.Pick:
                        slots[start[i]] = slots[start[node.OperandArray[0]] + node.Payload];
                        break;
                    default:
                        var op = node.Op!;
                        for (var k = 0; k < op.Arity; k++) operands[k] = slots[start[node.OperandArray[k]]];
                        op.Evaluate(operands.Slice(0, op.Arity), new Span<float>(slots, start[i], op.ResultCount));
                        break;
                }
            }

            for (var o = 0; o < outputCount; o++) outputs[o * points + point] = slots[start[outputNodes[o]]];
        }
    }
}
