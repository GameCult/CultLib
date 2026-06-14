using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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
    /// Payload delivered by the CultNet RUDP reliability state machine.
    /// </summary>
    public sealed class CultNetRudpDeliveredFrame
    {
        /// <summary>
        /// Gets or sets the logical channel id.
        /// </summary>
        public string ChannelId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the delivered payload bytes.
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the packet sequence that delivered the frame.
        /// </summary>
        public uint Sequence { get; set; }
    }

    /// <summary>
    /// Result of feeding one packet into a CultNet RUDP session.
    /// </summary>
    public sealed class CultNetRudpReceiveResult
    {
        /// <summary>
        /// Gets or sets delivered frames.
        /// </summary>
        public IReadOnlyList<CultNetRudpDeliveredFrame> Delivered { get; set; } = Array.Empty<CultNetRudpDeliveredFrame>();
        /// <summary>
        /// Gets or sets an optional immediate reply packet.
        /// </summary>
        public CultNetRudpPacket? Reply { get; set; }
    }

    /// <summary>
    /// Options for the in-memory CultNet RUDP reliability state machine.
    /// </summary>
    public sealed class CultNetRudpSessionOptions
    {
        /// <summary>
        /// Gets or sets the connection/session binding id.
        /// </summary>
        public uint ConnectionId { get; set; }
        /// <summary>
        /// Gets or sets the first local packet sequence.
        /// </summary>
        public uint InitialSequence { get; set; } = 1;
        /// <summary>
        /// Gets or sets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; set; } = 250;
    }

    /// <summary>
    /// Send options for RUDP data packets.
    /// </summary>
    public sealed class CultNetRudpSendOptions
    {
        /// <summary>
        /// Gets or sets whether the packet participates in reliable delivery.
        /// </summary>
        public bool Reliable { get; set; }
        /// <summary>
        /// Gets or sets whether the packet participates in ordered delivery.
        /// </summary>
        public bool Ordered { get; set; }
        /// <summary>
        /// Gets or sets whether the packet is latest-state sequenced.
        /// </summary>
        public bool Sequenced { get; set; }
        /// <summary>
        /// Gets or sets the current logical time in milliseconds.
        /// </summary>
        public long NowMs { get; set; }
    }

    /// <summary>
    /// Socket-free reliability state machine for CultNet RUDP.
    /// </summary>
    public sealed class CultNetRudpSession
    {
        private sealed class PendingReliablePacket
        {
            public CultNetRudpPacket Packet { get; set; } = new CultNetRudpPacket();
            public long LastSentAtMs { get; set; }
        }

        private uint _nextSequence;
        private bool _connected;
        private uint? _highestReceivedSequence;
        private readonly HashSet<uint> _receivedSequences = new HashSet<uint>();
        private readonly Dictionary<uint, PendingReliablePacket> _pendingReliable = new Dictionary<uint, PendingReliablePacket>();
        private readonly Dictionary<string, uint> _orderedNextSequenceByChannel = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedDictionary<uint, CultNetRudpDeliveredFrame>> _orderedBuffers =
            new Dictionary<string, SortedDictionary<uint, CultNetRudpDeliveredFrame>>(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a RUDP reliability session.
        /// </summary>
        public CultNetRudpSession(CultNetRudpSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            ConnectionId = options.ConnectionId;
            _nextSequence = options.InitialSequence;
            ResendDelayMs = options.ResendDelayMs;
        }

        /// <summary>
        /// Gets the connection/session binding id.
        /// </summary>
        public uint ConnectionId { get; }
        /// <summary>
        /// Gets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; }
        /// <summary>
        /// Gets whether the session has completed the connect/accept handshake.
        /// </summary>
        public bool Connected => _connected;
        /// <summary>
        /// Gets reliable packet sequences awaiting acknowledgement.
        /// </summary>
        public IReadOnlyList<uint> PendingReliableSequences => _pendingReliable.Keys.OrderBy(value => value).ToArray();

        /// <summary>
        /// Creates a reliable ordered connect packet.
        /// </summary>
        public CultNetRudpPacket CreateConnect(long nowMs = 0, byte[]? payload = null)
        {
            var packet = CreatePacket(CultNetRudpPacketType.Connect, "control", payload ?? Array.Empty<byte>(), reliable: true, ordered: true, sequenced: false);
            TrackReliable(packet, nowMs);
            return packet;
        }

        /// <summary>
        /// Accepts a connect packet and returns a reliable ordered accept packet.
        /// </summary>
        public CultNetRudpPacket AcceptConnect(CultNetRudpPacket packet, long nowMs = 0, byte[]? payload = null)
        {
            RequireConnection(packet);
            if (packet.PacketType != CultNetRudpPacketType.Connect)
            {
                throw new InvalidOperationException($"Expected RUDP connect packet, got {packet.PacketType}.");
            }

            RememberReceived(packet.Sequence);
            _connected = true;
            var response = CreatePacket(CultNetRudpPacketType.Accept, "control", payload ?? Array.Empty<byte>(), reliable: true, ordered: true, sequenced: false);
            TrackReliable(response, nowMs);
            return response;
        }

        /// <summary>
        /// Creates a data packet.
        /// </summary>
        public CultNetRudpPacket Send(string channelId, byte[] payload, CultNetRudpSendOptions? options = null)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("Cannot send RUDP data before the session is connected.");
            }

            options ??= new CultNetRudpSendOptions();
            var packet = CreatePacket(
                CultNetRudpPacketType.Data,
                channelId,
                payload,
                options.Reliable,
                options.Ordered,
                options.Sequenced);
            if (packet.Reliable)
            {
                TrackReliable(packet, options.NowMs);
            }
            return packet;
        }

        /// <summary>
        /// Applies a remote packet to the session.
        /// </summary>
        public CultNetRudpReceiveResult Receive(CultNetRudpPacket packet, long nowMs = 0)
        {
            RequireConnection(packet);
            ApplyAcknowledgements(packet);

            if (packet.PacketType == CultNetRudpPacketType.Accept)
            {
                RememberReceived(packet.Sequence);
                _connected = true;
                return new CultNetRudpReceiveResult();
            }

            if (packet.PacketType == CultNetRudpPacketType.Ping)
            {
                RememberReceived(packet.Sequence);
                return new CultNetRudpReceiveResult
                {
                    Reply = CreatePacket(
                        CultNetRudpPacketType.Pong,
                        "control",
                        packet.Payload ?? Array.Empty<byte>(),
                        reliable: false,
                        ordered: false,
                        sequenced: false)
                };
            }

            if (packet.PacketType == CultNetRudpPacketType.Ack || packet.PacketType == CultNetRudpPacketType.Pong)
            {
                RememberReceived(packet.Sequence);
                return new CultNetRudpReceiveResult();
            }

            if (packet.PacketType != CultNetRudpPacketType.Data)
            {
                return new CultNetRudpReceiveResult();
            }

            var duplicate = _receivedSequences.Contains(packet.Sequence);
            RememberReceived(packet.Sequence);
            if (duplicate)
            {
                return new CultNetRudpReceiveResult();
            }

            var frame = new CultNetRudpDeliveredFrame
            {
                ChannelId = packet.ChannelId,
                Payload = packet.Payload ?? Array.Empty<byte>(),
                Sequence = packet.Sequence
            };

            return new CultNetRudpReceiveResult
            {
                Delivered = packet.Ordered ? DeliverOrdered(frame) : new[] { frame }
            };
        }

        /// <summary>
        /// Creates a packet carrying the current acknowledgement state.
        /// </summary>
        public CultNetRudpPacket CreateAck()
        {
            return CreatePacket(CultNetRudpPacketType.Ack, "control", Array.Empty<byte>(), reliable: false, ordered: false, sequenced: false);
        }

        /// <summary>
        /// Returns reliable packets due for resend at the supplied logical time.
        /// </summary>
        public IReadOnlyList<CultNetRudpPacket> DueResends(long nowMs)
        {
            var due = new List<CultNetRudpPacket>();
            foreach (var pending in _pendingReliable.Values)
            {
                if (nowMs - pending.LastSentAtMs >= ResendDelayMs)
                {
                    pending.LastSentAtMs = nowMs;
                    due.Add(ClonePacket(pending.Packet));
                }
            }

            return due.OrderBy(packet => packet.Sequence).ToArray();
        }

        private CultNetRudpPacket CreatePacket(
            CultNetRudpPacketType packetType,
            string channelId,
            byte[] payload,
            bool reliable,
            bool ordered,
            bool sequenced)
        {
            var sequence = _nextSequence;
            _nextSequence = checked(_nextSequence + 1);
            var (ack, ackMask) = AckState();
            return new CultNetRudpPacket
            {
                PacketType = packetType,
                ConnectionId = ConnectionId,
                Sequence = sequence,
                Ack = ack,
                AckMask = ackMask,
                ChannelId = channelId,
                Reliable = reliable,
                Ordered = ordered,
                Sequenced = sequenced,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        private void TrackReliable(CultNetRudpPacket packet, long nowMs)
        {
            _pendingReliable[packet.Sequence] = new PendingReliablePacket
            {
                Packet = ClonePacket(packet),
                LastSentAtMs = nowMs
            };
        }

        private void ApplyAcknowledgements(CultNetRudpPacket packet)
        {
            _pendingReliable.Remove(packet.Ack);
            for (var bit = 0; bit < 32; bit++)
            {
                if ((packet.AckMask & (1u << bit)) != 0 && packet.Ack > bit)
                {
                    _pendingReliable.Remove(packet.Ack - (uint)bit - 1);
                }
            }
        }

        private void RememberReceived(uint sequence)
        {
            _receivedSequences.Add(sequence);
            if (!_highestReceivedSequence.HasValue || sequence > _highestReceivedSequence.Value)
            {
                _highestReceivedSequence = sequence;
            }
        }

        private (uint Ack, uint AckMask) AckState()
        {
            var ack = _highestReceivedSequence ?? 0;
            uint ackMask = 0;
            for (var bit = 0; bit < 32; bit++)
            {
                if (ack > bit && _receivedSequences.Contains(ack - (uint)bit - 1))
                {
                    ackMask |= 1u << bit;
                }
            }

            return (ack, ackMask);
        }

        private IReadOnlyList<CultNetRudpDeliveredFrame> DeliverOrdered(CultNetRudpDeliveredFrame frame)
        {
            if (!_orderedNextSequenceByChannel.TryGetValue(frame.ChannelId, out var next))
            {
                _orderedNextSequenceByChannel[frame.ChannelId] = frame.Sequence + 1;
                return new[] { frame }.Concat(DrainOrdered(frame.ChannelId)).ToArray();
            }

            if (frame.Sequence < next)
            {
                return Array.Empty<CultNetRudpDeliveredFrame>();
            }

            if (frame.Sequence > next)
            {
                if (!_orderedBuffers.TryGetValue(frame.ChannelId, out var buffer))
                {
                    buffer = new SortedDictionary<uint, CultNetRudpDeliveredFrame>();
                    _orderedBuffers[frame.ChannelId] = buffer;
                }

                buffer[frame.Sequence] = frame;
                return Array.Empty<CultNetRudpDeliveredFrame>();
            }

            _orderedNextSequenceByChannel[frame.ChannelId] = next + 1;
            return new[] { frame }.Concat(DrainOrdered(frame.ChannelId)).ToArray();
        }

        private IReadOnlyList<CultNetRudpDeliveredFrame> DrainOrdered(string channelId)
        {
            var delivered = new List<CultNetRudpDeliveredFrame>();
            if (!_orderedBuffers.TryGetValue(channelId, out var buffer))
            {
                return delivered;
            }

            while (_orderedNextSequenceByChannel.TryGetValue(channelId, out var next) && buffer.TryGetValue(next, out var frame))
            {
                buffer.Remove(next);
                delivered.Add(frame);
                _orderedNextSequenceByChannel[channelId] = next + 1;
            }

            return delivered;
        }

        private void RequireConnection(CultNetRudpPacket packet)
        {
            if (packet.ConnectionId != ConnectionId)
            {
                throw new InvalidOperationException($"RUDP packet connection id {packet.ConnectionId} does not match {ConnectionId}.");
            }
        }

        private static CultNetRudpPacket ClonePacket(CultNetRudpPacket packet)
        {
            return new CultNetRudpPacket
            {
                PacketType = packet.PacketType,
                ConnectionId = packet.ConnectionId,
                Sequence = packet.Sequence,
                Ack = packet.Ack,
                AckMask = packet.AckMask,
                ChannelId = packet.ChannelId,
                Reliable = packet.Reliable,
                Ordered = packet.Ordered,
                Sequenced = packet.Sequenced,
                FragmentId = packet.FragmentId,
                FragmentIndex = packet.FragmentIndex,
                FragmentCount = packet.FragmentCount,
                Payload = packet.Payload?.ToArray() ?? Array.Empty<byte>()
            };
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
