using System;
using System.Buffers;
using System.Collections.Generic;
using CultMath.Expr;
using MessagePack;
using MessagePack.Formatters;

namespace GameCult.Caching.MessagePack;

/// <summary>
/// The canonical MessagePack encoding of <see cref="ExprGraph"/>, beside <see cref="CultMathResolver"/> in the
/// default chain. A graph is <c>[version, inputs, params, outputs, nodes]</c>: <c>inputs</c> are names; a param is
/// <c>[name, default float bits]</c>; an output is <c>[name, node]</c>; a node is <c>[wire name, operands, payload]</c>
/// (the payload is the port index of an input or param, the float bits of a constant, the result index of a pick,
/// zero for a call). Decoding runs <see cref="ExprGraph.Create"/>, the one validator, so a graph that breaks a rule
/// (unknown op, newer version, forward operand) is refused whole with an <see cref="ExprGraphException"/> and never
/// partly loaded. Decoding also refuses any encoding other than the one <c>Serialize</c> writes (a wider integer,
/// a longer length prefix), so a graph has one byte string. A nil graph is nil, as in every reference slot.
/// </summary>
public sealed class CultMathExprResolver : IFormatterResolver
{
    public static readonly CultMathExprResolver Instance = new();
    private CultMathExprResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>() => Cache<T>.Formatter;

    private static class Cache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter =
            typeof(T) == typeof(ExprGraph) ? (IMessagePackFormatter<T>)(object)new GraphFormatter() : null;
    }

    private sealed class GraphFormatter : IMessagePackFormatter<ExprGraph?>
    {
        public void Serialize(ref MessagePackWriter writer, ExprGraph? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            Write(ref writer, value);
        }

        private static void Write(ref MessagePackWriter writer, ExprGraph value)
        {
            writer.WriteArrayHeader(5);
            writer.Write(ExprGraph.FormatVersion);

            writer.WriteArrayHeader(value.InputNames.Count);
            foreach (var name in value.InputNames) writer.Write(name);

            writer.WriteArrayHeader(value.ParamNames.Count);
            for (var i = 0; i < value.ParamNames.Count; i++)
            {
                writer.WriteArrayHeader(2);
                writer.Write(value.ParamNames[i]);
                writer.Write(value.ParamDefaultBits[i]);
            }

            writer.WriteArrayHeader(value.OutputNames.Count);
            for (var i = 0; i < value.OutputNames.Count; i++)
            {
                writer.WriteArrayHeader(2);
                writer.Write(value.OutputNames[i]);
                writer.Write(value.OutputNodes[i]);
            }

            writer.WriteArrayHeader(value.Nodes.Count);
            foreach (var node in value.Nodes)
            {
                writer.WriteArrayHeader(3);
                writer.Write(node.WireName);
                writer.WriteArrayHeader(node.Operands.Count);
                foreach (var operand in node.Operands) writer.Write(operand);
                writer.Write(node.Payload);
            }
        }

        public ExprGraph? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return null;
            options.Security.DepthStep(ref reader);
            try
            {
                var start = reader.Position;
                Expect(ref reader, 5);
                var version = reader.ReadInt32();

                var inputs = new List<string>();
                for (int i = 0, n = reader.ReadArrayHeader(); i < n; i++) inputs.Add(ReadName(ref reader));

                var paramNames = new List<string>();
                var paramBits = new List<int>();
                for (int i = 0, n = reader.ReadArrayHeader(); i < n; i++)
                {
                    Expect(ref reader, 2);
                    paramNames.Add(ReadName(ref reader));
                    paramBits.Add(reader.ReadInt32());
                }

                var outputNames = new List<string>();
                var outputNodes = new List<int>();
                for (int i = 0, n = reader.ReadArrayHeader(); i < n; i++)
                {
                    Expect(ref reader, 2);
                    outputNames.Add(ReadName(ref reader));
                    outputNodes.Add(reader.ReadInt32());
                }

                var nodes = new List<ExprNode>();
                for (int i = 0, n = reader.ReadArrayHeader(); i < n; i++)
                {
                    Expect(ref reader, 3);
                    var wireName = ReadName(ref reader);
                    var operands = new List<int>();
                    for (int k = 0, m = reader.ReadArrayHeader(); k < m; k++) operands.Add(reader.ReadInt32());
                    nodes.Add(ExprNode.FromWire(wireName, operands, reader.ReadInt32()));
                }

                var graph = ExprGraph.Create(version, inputs, paramNames, paramBits, outputNames, outputNodes, nodes);
                RequireCanonical(ref reader, start, graph);
                return graph;
            }
            finally
            {
                reader.Depth--;
            }
        }

        /// <summary>The bytes just read must be the bytes <see cref="Write"/> produces for the graph they decoded to.</summary>
        private static void RequireCanonical(ref MessagePackReader reader, SequencePosition start, ExprGraph graph)
        {
            var canonical = new ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(canonical);
            Write(ref writer, graph);
            writer.Flush();
            var read = reader.Sequence.Slice(start, reader.Position);
            if (read.Length != canonical.WrittenCount || !read.ToArray().AsSpan().SequenceEqual(canonical.WrittenSpan))
                throw new ExprGraphException("non-canonical");
        }

        private static void Expect(ref MessagePackReader reader, int count)
        {
            if (reader.ReadArrayHeader() != count) throw new ExprGraphException("shape");
        }

        private static string ReadName(ref MessagePackReader reader) =>
            reader.ReadString() ?? throw new ExprGraphException("shape");
    }
}
