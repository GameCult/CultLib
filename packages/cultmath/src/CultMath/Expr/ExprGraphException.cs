namespace CultMath.Expr;

/// <summary>
/// The one refusal of the expression graph: a build call, a decoded encoding, a binding or an evaluation
/// that breaks a rule. <see cref="Rule"/> names the rule and <see cref="NodeIndex"/> the node it broke at
/// (-1 when no node is involved). The message names the rule and never echoes a value it was given.
/// </summary>
public sealed class ExprGraphException : Exception
{
    public ExprGraphException(string rule, int nodeIndex = -1)
        : base(nodeIndex < 0 ? rule : rule + " at node " + nodeIndex)
    {
        Rule = rule;
        NodeIndex = nodeIndex;
    }

    /// <summary>The rule broken, a short stable name such as <c>unknown-op</c> or <c>operand-order</c>.</summary>
    public string Rule { get; }

    /// <summary>The index of the node the rule broke at, or -1.</summary>
    public int NodeIndex { get; }
}
