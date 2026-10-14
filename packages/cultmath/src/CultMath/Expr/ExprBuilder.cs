namespace CultMath.Expr;

/// <summary>A node of an <see cref="ExprBuilder"/> under construction. Only the builder that made it accepts it.</summary>
public sealed class ExprValue
{
    internal ExprValue(ExprBuilder owner, int index, int resultCount)
    {
        Owner = owner;
        Index = index;
        ResultCount = resultCount;
    }

    internal ExprBuilder Owner { get; }

    internal int Index { get; }

    /// <summary>More than one for a multi-result op, whose results are read only through <see cref="ExprBuilder.Pick"/>.</summary>
    public int ResultCount { get; }
}

/// <summary>
/// Builds an <see cref="ExprGraph"/>. Identical (op, operands, constant bits) nodes are one node, and
/// <see cref="Build"/> drops nodes no output reaches and renumbers densely, so the same call sequence builds
/// byte-identical graphs. Every call runs the validator's rules and refuses at the call.
/// </summary>
public sealed class ExprBuilder
{
    private readonly List<ExprNode> nodes = new();
    private readonly List<ExprValue> handles = new();
    private readonly Dictionary<string, int> interned = new();
    private readonly List<string> inputNames = new();
    private readonly List<string> paramNames = new();
    private readonly List<int> paramDefaultBits = new();
    private readonly List<string> outputNames = new();
    private readonly List<int> outputNodes = new();

    /// <summary>The graph input of this name, declared on first use.</summary>
    public ExprValue Input(string name)
    {
        var port = inputNames.IndexOf(name);
        if (port < 0)
        {
            ExprGraph.CheckPortName(name);
            port = inputNames.Count;
            inputNames.Add(name);
        }

        return Intern(ExprNode.Input(port));
    }

    /// <summary>The graph param of this name, declared on first use. Declaring it again with other default bits is refused.</summary>
    public ExprValue Param(string name, float defaultValue) => ParamBits(name, BitConverter.SingleToInt32Bits(defaultValue));

    /// <summary>A constant; its bits (a signed zero, a NaN payload, a subnormal) are kept.</summary>
    public ExprValue Const(float value) => ConstBits(BitConverter.SingleToInt32Bits(value));

    public ExprValue Call(string op, params ExprValue[] operands)
    {
        var indices = new int[operands.Length];
        for (var i = 0; i < operands.Length; i++) indices[i] = Own(operands[i]);
        return Intern(ExprNode.Call(op, indices));
    }

    /// <summary>Result <paramref name="k"/> of a multi-result node.</summary>
    public ExprValue Pick(ExprValue node, int k) => Intern(ExprNode.Pick(Own(node), k));

    /// <summary>Names a single-result node as an output.</summary>
    public void Output(string name, ExprValue value)
    {
        ExprGraph.CheckPortName(name);
        if (outputNames.Contains(name)) throw new ExprGraphException("duplicate-port-name");
        var node = Own(value);
        ExprGraph.CheckOutput(node, nodes);
        outputNames.Add(name);
        outputNodes.Add(node);
    }

    /// <summary>The graph of the outputs declared so far: only nodes an output reaches, renumbered densely in build order.</summary>
    public ExprGraph Build()
    {
        var live = new bool[nodes.Count];
        foreach (var node in outputNodes) live[node] = true;
        for (var i = nodes.Count - 1; i >= 0; i--)
            if (live[i])
                foreach (var operand in nodes[i].Operands) live[operand] = true;

        var renumbered = new int[nodes.Count];
        var kept = new List<ExprNode>();
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!live[i]) continue;
            renumbered[i] = kept.Count;
            kept.Add(nodes[i].WithOperands(nodes[i].Operands.Select(operand => renumbered[operand]).ToArray()));
        }

        return ExprGraph.Create(
            ExprGraph.FormatVersion, inputNames, paramNames, paramDefaultBits, outputNames, outputNodes.Select(node => renumbered[node]).ToList(), kept);
    }

    /// <summary>
    /// Rebuilds <paramref name="graph"/> inside this builder with the named inputs replaced by the given values
    /// (Fidget's remap_xyz) and returns each output's value by name. An input not named is this builder's input
    /// of that name; every param is this builder's param of that name. A stencil is N substitutions combined by
    /// ops: an ordinary flat graph, with no loop construct.
    /// </summary>
    public IReadOnlyDictionary<string, ExprValue> Substitute(ExprGraph graph, IReadOnlyDictionary<string, ExprValue> replacements)
    {
        foreach (var pair in replacements)
        {
            if (!graph.InputNames.Contains(pair.Key)) throw new ExprGraphException("unknown-input");
            Own(pair.Value);
            if (pair.Value.ResultCount != 1) throw new ExprGraphException("bare-multi-result");
        }

        var mapped = new ExprValue[graph.Nodes.Count];
        for (var i = 0; i < mapped.Length; i++)
        {
            var node = graph.Nodes[i];
            switch (node.Kind)
            {
                case ExprNodeKind.Input:
                    var input = graph.InputNames[node.Payload];
                    mapped[i] = replacements.TryGetValue(input, out var replacement) ? replacement : Input(input);
                    break;
                case ExprNodeKind.Param:
                    mapped[i] = ParamBits(graph.ParamNames[node.Payload], graph.ParamDefaultBits[node.Payload]);
                    break;
                case ExprNodeKind.Const:
                    mapped[i] = ConstBits(node.Payload);
                    break;
                case ExprNodeKind.Pick:
                    mapped[i] = Pick(mapped[node.Operands[0]], node.Payload);
                    break;
                default:
                    mapped[i] = Call(node.WireName, node.Operands.Select(operand => mapped[operand]).ToArray());
                    break;
            }
        }

        var outputs = new Dictionary<string, ExprValue>();
        for (var o = 0; o < graph.OutputNames.Count; o++) outputs[graph.OutputNames[o]] = mapped[graph.OutputNodes[o]];
        return outputs;
    }

    private ExprValue ParamBits(string name, int bits)
    {
        var port = paramNames.IndexOf(name);
        if (port < 0)
        {
            ExprGraph.CheckPortName(name);
            port = paramNames.Count;
            paramNames.Add(name);
            paramDefaultBits.Add(bits);
        }
        else if (paramDefaultBits[port] != bits)
        {
            throw new ExprGraphException("port-conflict");
        }

        return Intern(ExprNode.Param(port));
    }

    private ExprValue ConstBits(int bits) => Intern(ExprNode.Const(bits));

    private int Own(ExprValue value)
    {
        if (value.Owner != this) throw new ExprGraphException("foreign-operand", nodes.Count);
        return value.Index;
    }

    private ExprValue Intern(ExprNode node)
    {
        ExprGraph.CheckNode(nodes.Count, node, nodes, inputNames.Count, paramNames.Count);
        if (!interned.TryGetValue(node.Key, out var index))
        {
            index = nodes.Count;
            nodes.Add(node);
            handles.Add(new ExprValue(this, index, node.ResultCount));
            interned[node.Key] = index;
        }

        return handles[index];
    }
}
