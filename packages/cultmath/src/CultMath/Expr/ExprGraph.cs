namespace CultMath.Expr;

/// <summary>
/// An immutable scalar SSA expression graph: nodes in topological order (an operand names a lower node), named
/// inputs that vary per point, named params that are uniform per batch and carry a default, and named outputs.
/// A graph is a value; an edit builds a new one. <see cref="Create"/> is the one validator: the builder runs
/// it on <see cref="ExprBuilder.Build"/> and a decoder runs it on persisted bytes, so no partial graph is ever returned.
/// </summary>
public sealed class ExprGraph
{
    /// <summary>The persisted format version this build reads and writes.</summary>
    public const int FormatVersion = 1;

    private readonly string[] inputNames;
    private readonly string[] paramNames;
    private readonly int[] paramDefaultBits;
    private readonly string[] outputNames;
    private readonly int[] outputNodes;
    private readonly ExprNode[] nodes;

    private ExprGraph(string[] inputNames, string[] paramNames, int[] paramDefaultBits, string[] outputNames, int[] outputNodes, ExprNode[] nodes)
    {
        this.inputNames = inputNames;
        this.paramNames = paramNames;
        this.paramDefaultBits = paramDefaultBits;
        this.outputNames = outputNames;
        this.outputNodes = outputNodes;
        this.nodes = nodes;
        SlotStart = new int[nodes.Length];
        var slots = 0;
        for (var i = 0; i < nodes.Length; i++)
        {
            SlotStart[i] = slots;
            slots += nodes[i].ResultCount;
        }

        SlotCount = slots;
    }

    public IReadOnlyList<string> InputNames => inputNames;

    public IReadOnlyList<string> ParamNames => paramNames;

    /// <summary>The default of each param as float bits, so a NaN payload or a signed zero survives.</summary>
    public IReadOnlyList<int> ParamDefaultBits => paramDefaultBits;

    public IReadOnlyList<string> OutputNames => outputNames;

    /// <summary>The node each output reads.</summary>
    public IReadOnlyList<int> OutputNodes => outputNodes;

    public IReadOnlyList<ExprNode> Nodes => nodes;

    /// <summary>Where each node's results start in an evaluator's value array.</summary>
    internal int[] SlotStart { get; }

    internal int SlotCount { get; }

    /// <summary>The index of the param of this name, or -1.</summary>
    internal int ParamIndex(string name) => Array.IndexOf(paramNames, name);

    /// <summary>
    /// Validates and returns a graph, or throws <see cref="ExprGraphException"/> naming the rule and the node.
    /// The rules: format version, port counts, port names (syntax, unique per kind), and per node its kind's
    /// operand count, a known op and its arity, operands below the node and never a bare multi-result node,
    /// pick range, port index and payload; and per output a node in range that is not a bare multi-result node.
    /// </summary>
    public static ExprGraph Create(
        int version,
        IReadOnlyList<string> inputNames,
        IReadOnlyList<string> paramNames,
        IReadOnlyList<int> paramDefaultBits,
        IReadOnlyList<string> outputNames,
        IReadOnlyList<int> outputNodes,
        IReadOnlyList<ExprNode> nodes)
    {
        if (version != FormatVersion) throw new ExprGraphException("version");
        if (paramNames.Count != paramDefaultBits.Count || outputNames.Count != outputNodes.Count) throw new ExprGraphException("port-count");
        CheckPortNames(inputNames);
        CheckPortNames(paramNames);
        CheckPortNames(outputNames);

        var before = new List<ExprNode>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            CheckNode(i, nodes[i], before, inputNames.Count, paramNames.Count);
            before.Add(nodes[i]);
        }

        foreach (var node in outputNodes) CheckOutput(node, before);

        return new ExprGraph(
            inputNames.ToArray(), paramNames.ToArray(), paramDefaultBits.ToArray(), outputNames.ToArray(), outputNodes.ToArray(), nodes.ToArray());
    }

    /// <summary>A port name is <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    internal static void CheckPortName(string name)
    {
        if (name.Length == 0 || !IsHead(name[0])) throw new ExprGraphException("port-name");
        for (var i = 1; i < name.Length; i++)
            if (!IsHead(name[i]) && !(name[i] >= '0' && name[i] <= '9')) throw new ExprGraphException("port-name");
    }

    private static bool IsHead(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static void CheckPortNames(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>();
        foreach (var name in names)
        {
            CheckPortName(name);
            if (!seen.Add(name)) throw new ExprGraphException("duplicate-port-name");
        }
    }

    /// <summary>The rules for one node given the nodes before it. The builder runs this per call, the validator per node.</summary>
    internal static void CheckNode(int index, ExprNode node, IReadOnlyList<ExprNode> before, int inputCount, int paramCount)
    {
        var operands = node.Operands;
        switch (node.Kind)
        {
            case ExprNodeKind.Input:
                CheckLeaf(index, node, inputCount);
                break;
            case ExprNodeKind.Param:
                CheckLeaf(index, node, paramCount);
                break;
            case ExprNodeKind.Const:
                if (operands.Count != 0) throw new ExprGraphException("arity", index);
                break;
            case ExprNodeKind.Pick:
                if (operands.Count != 1) throw new ExprGraphException("arity", index);
                CheckOperandOrder(index, operands[0]);
                var results = before[operands[0]].ResultCount;
                if (results <= 1 || node.Payload < 0 || node.Payload >= results) throw new ExprGraphException("pick-range", index);
                break;
            default:
                if (node.Op is null) throw new ExprGraphException("unknown-op", index);
                if (operands.Count != node.Op.Arity) throw new ExprGraphException("arity", index);
                if (node.Payload != 0) throw new ExprGraphException("payload", index);
                foreach (var operand in operands)
                {
                    CheckOperandOrder(index, operand);
                    if (before[operand].ResultCount != 1) throw new ExprGraphException("bare-multi-result", index);
                }

                break;
        }
    }

    private static void CheckLeaf(int index, ExprNode node, int portCount)
    {
        if (node.Operands.Count != 0) throw new ExprGraphException("arity", index);
        if (node.Payload < 0 || node.Payload >= portCount) throw new ExprGraphException("port-index", index);
    }

    private static void CheckOperandOrder(int index, int operand)
    {
        if (operand < 0 || operand >= index) throw new ExprGraphException("operand-order", index);
    }

    /// <summary>An output reads a node in range that is single-result.</summary>
    internal static void CheckOutput(int node, IReadOnlyList<ExprNode> nodes)
    {
        if (node < 0 || node >= nodes.Count) throw new ExprGraphException("output-node");
        if (nodes[node].ResultCount != 1) throw new ExprGraphException("bare-multi-result", node);
    }
}
