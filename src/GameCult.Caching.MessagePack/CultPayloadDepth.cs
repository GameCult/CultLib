using System;
using System.Buffers.Binary;
using MessagePack;

namespace GameCult.Caching.MessagePack;

internal enum PayloadDirection { Read, Write }

/// <summary>
/// The one payload nesting count. Walks the MessagePack headers of a document payload iteratively, counting every array and
/// map (read by a formatter or skipped past it; the outermost array is depth 1) and refusing the first container past
/// <see cref="CultMessagePackSecurity.MaximumPayloadDepth"/>. Scalars are skipped by length, nothing is allocated per
/// element, and the count stack is bounded by the limit. A refusal names the type, the depth reached and the limit,
/// never payload bytes. A payload that ends early is the deserializer's to refuse.
/// </summary>
internal static class CultPayloadDepth
{
    public static void Check(ReadOnlySpan<byte> bytes, Type type, PayloadDirection direction)
    {
        const int limit = CultMessagePackSecurity.MaximumPayloadDepth;
        Span<long> remaining = stackalloc long[limit];
        var depth = 0;
        long position = 0;
        while (position < bytes.Length)
        {
            var code = bytes[(int)position];
            long children = 0;
            long header = 0;
            long skip = 0;
            var container = false;
            switch (code)
            {
                case <= 0x7f or >= 0xe0 or 0xc0 or 0xc1 or 0xc2 or 0xc3: skip = 1; break;
                case <= 0x8f: container = true; header = 1; children = 2L * (code & 0x0f); break;
                case <= 0x9f: container = true; header = 1; children = code & 0x0f; break;
                case <= 0xbf: skip = 1 + (code & 0x1f); break;
                case 0xc4 or 0xd9: if (!Length(bytes, position, 1, out var l8)) return; skip = 2 + l8; break;
                case 0xc5 or 0xda: if (!Length(bytes, position, 2, out var l16)) return; skip = 3 + l16; break;
                case 0xc6 or 0xdb: if (!Length(bytes, position, 4, out var l32)) return; skip = 5 + l32; break;
                case 0xc7: if (!Length(bytes, position, 1, out var e8)) return; skip = 3 + e8; break;
                case 0xc8: if (!Length(bytes, position, 2, out var e16)) return; skip = 4 + e16; break;
                case 0xc9: if (!Length(bytes, position, 4, out var e32)) return; skip = 6 + e32; break;
                case 0xca or 0xce or 0xd2: skip = 5; break;
                case 0xcb or 0xcf or 0xd3: skip = 9; break;
                case 0xcc or 0xd0: skip = 2; break;
                case 0xcd or 0xd1 or 0xd4: skip = 3; break;
                case 0xd5: skip = 4; break;
                case 0xd6: skip = 6; break;
                case 0xd7: skip = 10; break;
                case 0xd8: skip = 18; break;
                case 0xdc: if (!Length(bytes, position, 2, out var a16)) return; container = true; header = 3; children = a16; break;
                case 0xdd: if (!Length(bytes, position, 4, out var a32)) return; container = true; header = 5; children = a32; break;
                case 0xde: if (!Length(bytes, position, 2, out var m16)) return; container = true; header = 3; children = 2 * m16; break;
                default: if (!Length(bytes, position, 4, out var m32)) return; container = true; header = 5; children = 2 * m32; break;
            }

            if (container)
            {
                if (depth == limit)
                    throw new MessagePackSerializationException(
                        $"{type.FullName} payload nests {limit + 1} MessagePack containers deep; the limit is {limit} (refused on {(direction == PayloadDirection.Read ? "read" : "write")}).");
                position += header;
                if (children > 0)
                {
                    remaining[depth++] = children;
                    continue;
                }
            }
            else
            {
                position += skip;
            }

            // One element is complete; close every container it was the last element of.
            while (depth > 0 && --remaining[depth - 1] == 0) depth--;
            if (depth == 0) return;
        }
    }

    // The big-endian length that follows the code byte, when the payload is long enough to hold it.
    private static bool Length(ReadOnlySpan<byte> bytes, long position, int width, out long length)
    {
        length = 0;
        if (position + 1 + width > bytes.Length) return false;
        var field = bytes.Slice((int)position + 1, width);
        length = width switch
        {
            1 => field[0],
            2 => BinaryPrimitives.ReadUInt16BigEndian(field),
            _ => BinaryPrimitives.ReadUInt32BigEndian(field)
        };
        return true;
    }
}
