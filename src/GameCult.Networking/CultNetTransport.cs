using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Transfer counters for a CultNet transport connection.
    /// </summary>
    public sealed class CultNetTransportStats
    {
        /// <summary>
        /// Gets the number of payload frames sent through the connection.
        /// </summary>
        public long FramesSent { get; internal set; }
        /// <summary>
        /// Gets the number of payload frames received through the connection.
        /// </summary>
        public long FramesReceived { get; internal set; }
        /// <summary>
        /// Gets the number of bytes sent, including transport framing.
        /// </summary>
        public long BytesSent { get; internal set; }
        /// <summary>
        /// Gets the number of bytes received, including transport framing.
        /// </summary>
        public long BytesReceived { get; internal set; }

        internal CultNetTransportStats Snapshot()
        {
            return new CultNetTransportStats
            {
                FramesSent = FramesSent,
                FramesReceived = FramesReceived,
                BytesSent = BytesSent,
                BytesReceived = BytesReceived
            };
        }
    }

    /// <summary>
    /// Payload delivered by a CultNet transport connection.
    /// </summary>
    public sealed class CultNetTransportFrame
    {
        /// <summary>
        /// Gets or sets the logical transport channel.
        /// </summary>
        public string ChannelId { get; set; } = "schema";
        /// <summary>
        /// Gets or sets the raw payload bytes carried by the frame.
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Options for creating a TCP framed transport profile.
    /// </summary>
    public sealed class TcpFramedTransportProfileOptions
    {
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "tcp-framed";
        /// <summary>
        /// Gets or sets the advertised host.
        /// </summary>
        public string? Host { get; set; }
        /// <summary>
        /// Gets or sets the advertised port.
        /// </summary>
        public int? Port { get; set; }
        /// <summary>
        /// Gets or sets the maximum payload size for the schema channel.
        /// </summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum fragment size for the schema channel.
        /// </summary>
        public int? MaxFragmentBytes { get; set; }
    }

    /// <summary>
    /// Options for creating a CultNet RUDP transport profile.
    /// </summary>
    public sealed class RudpTransportProfileOptions
    {
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "rudp";
        /// <summary>
        /// Gets or sets the advertised host.
        /// </summary>
        public string? Host { get; set; }
        /// <summary>
        /// Gets or sets the advertised port.
        /// </summary>
        public int? Port { get; set; }
        /// <summary>
        /// Gets or sets the maximum payload size for RUDP channels.
        /// </summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum fragment size for RUDP channels.
        /// </summary>
        public int? MaxFragmentBytes { get; set; }
    }

    /// <summary>
    /// Helpers for creating CultNet transport profile documents.
    /// </summary>
    public static class CultNetTransportProfiles
    {
        /// <summary>
        /// Creates a profile for the current length-prefixed TCP schema lane.
        /// </summary>
        public static CultNetTransportProfile CreateTcpFramed(
            string runtimeId,
            TcpFramedTransportProfileOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(runtimeId)) throw new ArgumentException("Runtime id is required.", nameof(runtimeId));
            options ??= new TcpFramedTransportProfileOptions();
            return new CultNetTransportProfile
            {
                RuntimeId = runtimeId,
                Transports =
                [
                    new CultNetTransportDescriptor
                    {
                        TransportId = string.IsNullOrWhiteSpace(options.TransportId)
                            ? "tcp-framed"
                            : options.TransportId,
                        Protocol = "tcp_framed",
                        Host = options.Host,
                        Port = options.Port,
                        WireContracts = [CultNetWireContracts.SchemaV0],
                        Channels =
                        [
                            new CultNetTransportChannel
                            {
                                ChannelId = "schema",
                                Delivery = "reliable",
                                Ordering = "ordered",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes
                            }
                        ]
                    }
                ]
            };
        }

        /// <summary>
        /// Creates a profile for the CultNet reliable UDP transport.
        /// </summary>
        public static CultNetTransportProfile CreateRudp(
            string runtimeId,
            RudpTransportProfileOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(runtimeId)) throw new ArgumentException("Runtime id is required.", nameof(runtimeId));
            options ??= new RudpTransportProfileOptions();
            return new CultNetTransportProfile
            {
                RuntimeId = runtimeId,
                Transports =
                [
                    new CultNetTransportDescriptor
                    {
                        TransportId = string.IsNullOrWhiteSpace(options.TransportId)
                            ? "rudp"
                            : options.TransportId,
                        Protocol = "rudp",
                        Host = options.Host,
                        Port = options.Port,
                        WireContracts = [CultNetWireContracts.SchemaV0],
                        Channels =
                        [
                            new CultNetTransportChannel
                            {
                                ChannelId = "schema",
                                Delivery = "reliable",
                                Ordering = "ordered",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes
                            },
                            new CultNetTransportChannel
                            {
                                ChannelId = "latest",
                                Delivery = "unreliable",
                                Ordering = "sequenced",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes
                            },
                            new CultNetTransportChannel
                            {
                                ChannelId = "realtime",
                                Delivery = "unreliable",
                                Ordering = "unordered",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes
                            }
                        ]
                    }
                ]
            };
        }
    }

    /// <summary>
    /// Packet type codes for CultNet reliable UDP transport packets.
    /// </summary>
    public enum CultNetRudpPacketType : byte
    {
        /// <summary>Connection request packet.</summary>
        Connect = 1,
        /// <summary>Connection accepted packet.</summary>
        Accept = 2,
        /// <summary>Payload data packet.</summary>
        Data = 3,
        /// <summary>Selective acknowledgement packet.</summary>
        Ack = 4,
        /// <summary>Ping packet.</summary>
        Ping = 5,
        /// <summary>Pong packet.</summary>
        Pong = 6,
        /// <summary>Disconnect packet.</summary>
        Disconnect = 7
    }

    /// <summary>
    /// Binary packet for the CultNet reliable UDP transport.
    /// </summary>
    public sealed class CultNetRudpPacket
    {
        /// <summary>
        /// Gets or sets the packet type.
        /// </summary>
        public CultNetRudpPacketType PacketType { get; set; }
        /// <summary>
        /// Gets or sets the connection/session binding id.
        /// </summary>
        public uint ConnectionId { get; set; }
        /// <summary>
        /// Gets or sets the packet sequence number.
        /// </summary>
        public uint Sequence { get; set; }
        /// <summary>
        /// Gets or sets the latest sequence acknowledged by this packet.
        /// </summary>
        public uint Ack { get; set; }
        /// <summary>
        /// Gets or sets the selective acknowledgement mask.
        /// </summary>
        public uint AckMask { get; set; }
        /// <summary>
        /// Gets or sets the logical channel id.
        /// </summary>
        public string ChannelId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets whether the packet participates in reliable delivery.
        /// </summary>
        public bool Reliable { get; set; }
        /// <summary>
        /// Gets or sets whether the packet participates in ordered delivery.
        /// </summary>
        public bool Ordered { get; set; }
        /// <summary>
        /// Gets or sets whether the packet is a latest-state sequenced packet.
        /// </summary>
        public bool Sequenced { get; set; }
        /// <summary>
        /// Gets or sets the fragment id, or zero when unfragmented.
        /// </summary>
        public ushort FragmentId { get; set; }
        /// <summary>
        /// Gets or sets the zero-based fragment index.
        /// </summary>
        public ushort FragmentIndex { get; set; }
        /// <summary>
        /// Gets or sets the fragment count, or zero when unfragmented.
        /// </summary>
        public ushort FragmentCount { get; set; }
        /// <summary>
        /// Gets or sets the transport-neutral payload bytes.
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Encodes and decodes CultNet reliable UDP packet bytes.
    /// </summary>
    public static class CultNetRudpPacketCodec
    {
        private const int FixedHeaderBytes = 36;
        private const byte Version = 0;
        private static readonly byte[] Magic = [0x43, 0x4e, 0x52, 0x30];

        /// <summary>
        /// Encodes a RUDP packet into the canonical binary envelope.
        /// </summary>
        public static byte[] Encode(CultNetRudpPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            var channelId = Encoding.UTF8.GetBytes(packet.ChannelId ?? string.Empty);
            if (channelId.Length > 255)
            {
                throw new InvalidOperationException("CultNet RUDP channel id cannot exceed 255 UTF-8 bytes.");
            }

            var payload = packet.Payload ?? Array.Empty<byte>();
            var headerBytes = checked(FixedHeaderBytes + channelId.Length);
            var wire = new byte[checked(headerBytes + payload.Length)];
            Magic.CopyTo(wire, 0);
            wire[4] = Version;
            wire[5] = (byte)packet.PacketType;
            wire[6] = EncodeFlags(packet);
            wire[7] = checked((byte)headerBytes);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(8, 4), packet.ConnectionId);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(12, 4), packet.Sequence);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(16, 4), packet.Ack);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(20, 4), packet.AckMask);
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(24, 2), packet.FragmentId);
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(26, 2), packet.FragmentIndex);
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(28, 2), packet.FragmentCount);
            BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(30, 4), checked((uint)payload.Length));
            wire[34] = checked((byte)channelId.Length);
            wire[35] = 0;
            channelId.CopyTo(wire.AsSpan(FixedHeaderBytes));
            payload.CopyTo(wire.AsSpan(headerBytes));
            return wire;
        }

        /// <summary>
        /// Decodes a RUDP packet from the canonical binary envelope.
        /// </summary>
        public static CultNetRudpPacket Decode(byte[] wire)
        {
            if (wire == null) throw new ArgumentNullException(nameof(wire));
            if (wire.Length < FixedHeaderBytes)
            {
                throw new InvalidOperationException("CultNet RUDP packet is shorter than the fixed header.");
            }

            for (var index = 0; index < Magic.Length; index++)
            {
                if (wire[index] != Magic[index])
                {
                    throw new InvalidOperationException("CultNet RUDP packet has the wrong magic.");
                }
            }

            if (wire[4] != Version)
            {
                throw new InvalidOperationException($"Unsupported CultNet RUDP packet version {wire[4]}.");
            }

            var type = (CultNetRudpPacketType)wire[5];
            if (!Enum.IsDefined(typeof(CultNetRudpPacketType), type))
            {
                throw new InvalidOperationException($"Unsupported CultNet RUDP packet type {wire[5]}.");
            }

            var headerBytes = wire[7];
            var channelIdLength = wire[34];
            if (headerBytes != FixedHeaderBytes + channelIdLength)
            {
                throw new InvalidOperationException("CultNet RUDP packet header length does not match the channel id length.");
            }

            var payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(30, 4)));
            if (wire.Length != headerBytes + payloadLength)
            {
                throw new InvalidOperationException("CultNet RUDP packet payload length does not match the packet size.");
            }

            var flags = wire[6];
            var payload = new byte[payloadLength];
            Array.Copy(wire, headerBytes, payload, 0, payload.Length);
            return new CultNetRudpPacket
            {
                PacketType = type,
                Reliable = (flags & 0b0000_0001) != 0,
                Ordered = (flags & 0b0000_0010) != 0,
                Sequenced = (flags & 0b0000_0100) != 0,
                ConnectionId = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(8, 4)),
                Sequence = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(12, 4)),
                Ack = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(16, 4)),
                AckMask = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(20, 4)),
                FragmentId = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(24, 2)),
                FragmentIndex = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(26, 2)),
                FragmentCount = BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(28, 2)),
                ChannelId = Encoding.UTF8.GetString(wire, FixedHeaderBytes, channelIdLength),
                Payload = payload
            };
        }

        private static byte EncodeFlags(CultNetRudpPacket packet)
        {
            return (byte)(
                (packet.Reliable ? 0b0000_0001 : 0) |
                (packet.Ordered ? 0b0000_0010 : 0) |
                (packet.Sequenced ? 0b0000_0100 : 0) |
                (packet.FragmentCount > 0 ? 0b0000_1000 : 0));
        }
    }

    /// <summary>
    /// Transport connection for the current TCP length-prefixed CultNet schema lane.
    /// </summary>
    public sealed class TcpFramedTransportConnection
    {
        private const int HeaderBytes = 4;
        private readonly Stream _stream;
        private readonly CultNetTransportStats _stats = new CultNetTransportStats();

        /// <summary>
        /// Initializes a TCP framed transport over an existing stream.
        /// </summary>
        public TcpFramedTransportConnection(Stream stream, CultNetTransportProfile profile)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        /// <summary>
        /// Gets the profile this connection implements.
        /// </summary>
        public CultNetTransportProfile Profile { get; }

        /// <summary>
        /// Gets a snapshot of the current transfer counters.
        /// </summary>
        public CultNetTransportStats Stats => _stats.Snapshot();

        /// <summary>
        /// Sends a schema-channel payload.
        /// </summary>
        public async Task SendAsync(string channelId, byte[] payload, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(channelId, "schema", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"tcp_framed transport only supports the schema channel, got \"{channelId}\".");
            }

            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var header = new byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _stats.BytesSent += HeaderBytes + payload.Length;
            _stats.FramesSent++;
        }

        /// <summary>
        /// Receives the next schema-channel payload.
        /// </summary>
        public async Task<CultNetTransportFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            var header = new byte[HeaderBytes];
            await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            var payload = new byte[payloadLength];
            await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            _stats.BytesReceived += HeaderBytes + payloadLength;
            _stats.FramesReceived++;
            return new CultNetTransportFrame
            {
                ChannelId = "schema",
                Payload = payload
            };
        }

        private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
        }
    }
}
