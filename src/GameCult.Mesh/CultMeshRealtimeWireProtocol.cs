using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace GameCult.Mesh
{
    /// <summary>
    /// Stable CultMesh realtime frame encoding shared by managed and native QUIC connectors.
    /// QUIC stream framing remains transport-owned; this codec owns only typed frame bytes.
    /// </summary>
    public static class CultMeshRealtimeWireProtocol
    {
        public const string ApplicationProtocolName = "cultmesh-state-v1";
        public const long ConnectionCloseCode = 0x43554c54;
        public const long StreamAbortCode = 0x53544154;
        public const byte ReliableStream = 1;
        public const byte LatestOnlyStream = 2;
        public const int MaximumFrameBytes = 64 * 1024 * 1024;
        public const int MaximumEncodedFrameBytes = MaximumFrameBytes + 37 + (3 * ushort.MaxValue);

        private const uint Magic = 0x31545343;
        private const int FixedHeaderBytes = 37;

        public static byte[] EncodeFrame(CultMeshRealtimeFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            frame.Validate();
            var channel = Encoding.UTF8.GetBytes(frame.ChannelId);
            var schema = Encoding.UTF8.GetBytes(frame.SchemaId);
            var body = Encoding.UTF8.GetBytes(frame.BodyId);
            if (channel.Length > ushort.MaxValue || schema.Length > ushort.MaxValue || body.Length > ushort.MaxValue)
                throw new InvalidDataException("Realtime frame identity exceeds the QUIC wire limit.");
            if (frame.Payload.Length > MaximumFrameBytes)
                throw new InvalidDataException("Realtime frame payload exceeds the QUIC wire limit.");

            var result = new byte[checked(FixedHeaderBytes + channel.Length + schema.Length + body.Length + frame.Payload.Length)];
            var span = result.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
            span[4] = (byte)frame.Delivery;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(5), frame.ProducerEpoch);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(13), frame.Sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(21), (ushort)channel.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(23), (ushort)schema.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(25), (ushort)body.Length);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(27), frame.Payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(31), FixedHeaderBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(35), 1);
            var offset = FixedHeaderBytes;
            channel.CopyTo(span.Slice(offset)); offset += channel.Length;
            schema.CopyTo(span.Slice(offset)); offset += schema.Length;
            body.CopyTo(span.Slice(offset)); offset += body.Length;
            frame.Payload.Span.CopyTo(span.Slice(offset));
            return result;
        }

        public static CultMeshRealtimeFrame DecodeFrame(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length < FixedHeaderBytes) throw new InvalidDataException("Realtime frame header is truncated.");
            var span = bytes.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
                throw new InvalidDataException("Realtime frame magic is invalid.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(35)) != 1 ||
                BinaryPrimitives.ReadInt32LittleEndian(span.Slice(31)) != FixedHeaderBytes)
                throw new InvalidDataException("Realtime frame wire version is unsupported.");
            var delivery = (CultMeshRealtimeDelivery)span[4];
            if (delivery < CultMeshRealtimeDelivery.ReliableOrdered || delivery > CultMeshRealtimeDelivery.Unreliable)
                throw new InvalidDataException("Realtime frame delivery mode is invalid.");
            var channelLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(21));
            var schemaLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(23));
            var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(25));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(27));
            var expected = checked(FixedHeaderBytes + channelLength + schemaLength + bodyLength + payloadLength);
            if (payloadLength < 0 || expected != bytes.Length)
                throw new InvalidDataException("Realtime frame length is invalid.");
            var offset = FixedHeaderBytes;
            var channel = Encoding.UTF8.GetString(bytes, offset, channelLength); offset += channelLength;
            var schema = Encoding.UTF8.GetString(bytes, offset, schemaLength); offset += schemaLength;
            var body = Encoding.UTF8.GetString(bytes, offset, bodyLength); offset += bodyLength;
            return new CultMeshRealtimeFrame
            {
                ChannelId = channel,
                SchemaId = schema,
                BodyId = body,
                ProducerEpoch = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(5)),
                Sequence = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(13)),
                Delivery = delivery,
                Payload = bytes.AsMemory(offset, payloadLength)
            };
        }
    }
}
