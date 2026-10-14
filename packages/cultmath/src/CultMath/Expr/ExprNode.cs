namespace CultMath.Expr;

/// <summary>What a node is: a graph input, a graph param, a constant, a pick of one result of a multi-result node, or a call of an op.</summary>
public enum ExprNodeKind
{
    Input,
    Param,
    Const,
    Pick,
    Call,
}

/// <summary>
/// One node of an <see cref="ExprGraph"/>: a kind, the operand node indices (always lower than its own),
/// and one integer payload: the port index of an input or param, the float bits of a constant, the result
/// index of a pick, and zero for a call. Immutable.
/// </summary>
public sealed class ExprNode
{
    private const string InputName = "input";
    private const string ParamName = "param";
    private const string ConstName = "const";
    private const string PickName = "pick";

    private static readonly int[] NoOperands = Array.Empty<int>();

    private readonly int[] operandArray;

    private ExprNode(ExprNodeKind kind, string wireName, int[] operands, int payload, ExprOp? op)
    {
        Kind = kind;
        WireName = wireName;
        operandArray = operands;
        Operands = Array.AsReadOnly(operands);
        Payload = payload;
        Op = op;
        ResultCount = op?.ResultCount ?? 1;
    }

    public ExprNodeKind Kind { get; }

    /// <summary>The persisted name: an op name for a call, otherwise <c>input</c>, <c>param</c>, <c>const</c> or <c>pick</c>.</summary>
    public string WireName { get; }

    public IReadOnlyList<int> Operands { get; }

    /// <summary>The evaluator's view of the operands: the private array, never handed out.</summary>
    internal int[] OperandArray => operandArray;

    public int Payload { get; }

    /// <summary>How many floats the node produces: the op's result count for a call, otherwise one.</summary>
    public int ResultCount { get; }

    /// <summary>The op of a known call; null for every other node and for a call naming an unknown op.</summary>
    internal ExprOp? Op { get; }

    public static ExprNode Input(int port) => new(ExprNodeKind.Input, InputName, NoOperands, port, null);

    public static ExprNode Param(int port) => new(ExprNodeKind.Param, ParamName, NoOperands, port, null);

    public static ExprNode Const(int bits) => new(ExprNodeKind.Const, ConstName, NoOperands, bits, null);

    public static ExprNode Pick(int node, int result) => new(ExprNodeKind.Pick, PickName, new[] { node }, result, null);

    public static ExprNode Call(string op, IReadOnlyList<int> operands) =>
        new(ExprNodeKind.Call, op, operands.ToArray(), 0, ExprOps.Find(op));

    /// <summary>The node a persisted <paramref name="wireName"/> names. A name that is none of the four kinds is a call, which the validator checks against the op table.</summary>
    public static ExprNode FromWire(string wireName, IReadOnlyList<int> operands, int payload) => wireName switch
    {
        InputName => new ExprNode(ExprNodeKind.Input, wireName, operands.ToArray(), payload, null),
        ParamName => new ExprNode(ExprNodeKind.Param, wireName, operands.ToArray(), payload, null),
        ConstName => new ExprNode(ExprNodeKind.Const, wireName, operands.ToArray(), payload, null),
        PickName => new ExprNode(ExprNodeKind.Pick, wireName, operands.ToArray(), payload, null),
        _ => new ExprNode(ExprNodeKind.Call, wireName, operands.ToArray(), payload, ExprOps.Find(wireName)),
    };

    internal ExprNode WithOperands(int[] operands) => new(Kind, WireName, operands, Payload, Op);

    /// <summary>Equal keys are the same expression: the builder's hash-consing identity.</summary>
    internal string Key => WireName + "|" + string.Join(",", operandArray) + "|" + Payload;
}
