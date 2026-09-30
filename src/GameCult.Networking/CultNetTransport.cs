using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;

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
        /// <summary>
        /// Gets the number of datagrams read and discarded because they belong to no session on
        /// this transport: malformed frames, another session's connection id, a sender that is not
        /// the peer, or a packet the session refuses.
        /// </summary>
        public long PacketsDropped { get; internal set; }

        internal CultNetTransportStats Snapshot()
        {
            return new CultNetTransportStats
            {
                FramesSent = FramesSent,
                FramesReceived = FramesReceived,
                BytesSent = BytesSent,
                BytesReceived = BytesReceived,
                PacketsDropped = PacketsDropped
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
    /// Portable reconnect backoff policy document.
    /// </summary>
    [MessagePack.MessagePackObject]
    public sealed class CultNetReconnectPolicy
    {
        /// <summary>
        /// Gets or sets the shared reconnect policy schema version.
        /// </summary>
        [MessagePack.Key("schemaVersion")]
        public string SchemaVersion { get; set; } = "cultnet.reconnect_policy.v0";
        /// <summary>
        /// Gets or sets the policy identifier.
        /// </summary>
        [MessagePack.Key("policyId")]
        public string PolicyId { get; set; } = "default";
        /// <summary>
        /// Gets or sets the first reconnect delay.
        /// </summary>
        [MessagePack.Key("baseDelayMs")]
        public int BaseDelayMs { get; set; } = 1_000;
        /// <summary>
        /// Gets or sets the maximum exponential backoff delay before jitter.
        /// </summary>
        [MessagePack.Key("maxDelayMs")]
        public int MaxDelayMs { get; set; } = 30_000;
        /// <summary>
        /// Gets or sets the maximum positive jitter a caller may add.
        /// </summary>
        [MessagePack.Key("maxJitterMs")]
        public int MaxJitterMs { get; set; } = 250;
        /// <summary>
        /// Gets or sets the optional maximum reconnect attempts.
        /// </summary>
        [MessagePack.Key("maxAttempts")]
        public int? MaxAttempts { get; set; }
    }

    /// <summary>
    /// Helpers for portable reconnect policy documents.
    /// </summary>
    public static class CultNetReconnectPolicies
    {
        /// <summary>
        /// Creates a reconnect policy using the shared default values.
        /// </summary>
        public static CultNetReconnectPolicy CreateDefault(
            string policyId = "default",
            int baseDelayMs = 1_000,
            int maxDelayMs = 30_000,
            int maxJitterMs = 250,
            int? maxAttempts = null)
        {
            return new CultNetReconnectPolicy
            {
                PolicyId = string.IsNullOrWhiteSpace(policyId) ? "default" : policyId,
                BaseDelayMs = baseDelayMs,
                MaxDelayMs = maxDelayMs,
                MaxJitterMs = maxJitterMs,
                MaxAttempts = maxAttempts
            };
        }

        /// <summary>
        /// Computes the deterministic exponential reconnect delay for an attempt.
        /// </summary>
        public static int ComputeDelayMs(CultNetReconnectPolicy policy, int attempt, int jitterMs = 0)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            var normalizedAttempt = Math.Max(1, attempt);
            var cappedBaseDelay = Math.Min(
                policy.MaxDelayMs,
                (int)Math.Min(int.MaxValue, policy.BaseDelayMs * Math.Pow(2, normalizedAttempt - 1)));
            var boundedJitter = Math.Max(0, Math.Min(policy.MaxJitterMs, jitterMs));
            return cappedBaseDelay + boundedJitter;
        }
    }

    /// <summary>
    /// Decision emitted by the portable reconnect controller.
    /// </summary>
    public sealed class CultNetReconnectDecision
    {
        /// <summary>
        /// Gets or sets the scheduled attempt number.
        /// </summary>
        public int Attempt { get; set; }
        /// <summary>
        /// Gets or sets whether another attempt should be made.
        /// </summary>
        public bool ShouldRetry { get; set; }
        /// <summary>
        /// Gets or sets the computed delay before the next attempt.
        /// </summary>
        public int DelayMs { get; set; }
        /// <summary>
        /// Gets or sets the absolute scheduler time for the next attempt.
        /// </summary>
        public long? NextAttemptAtMs { get; set; }
        /// <summary>
        /// Gets or sets whether the policy has exhausted its attempts.
        /// </summary>
        public bool Exhausted { get; set; }
    }

    /// <summary>
    /// Portable reconnect attempt scheduler for CultNet transports.
    /// </summary>
    public sealed class CultNetReconnectController
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CultNetReconnectController"/> class.
        /// </summary>
        public CultNetReconnectController(CultNetReconnectPolicy? policy = null)
        {
            Policy = policy ?? CultNetReconnectPolicies.CreateDefault();
        }

        /// <summary>
        /// Gets the reconnect policy.
        /// </summary>
        public CultNetReconnectPolicy Policy { get; }
        /// <summary>
        /// Gets the last scheduled attempt number.
        /// </summary>
        public int Attempt { get; private set; }
        /// <summary>
        /// Gets the absolute scheduler time for the next attempt.
        /// </summary>
        public long? NextAttemptAtMs { get; private set; }
        /// <summary>
        /// Gets a value indicating whether the policy exhausted reconnect attempts.
        /// </summary>
        public bool Exhausted { get; private set; }

        /// <summary>
        /// Clears attempt state after a successful connection.
        /// </summary>
        public void Reset()
        {
            Attempt = 0;
            NextAttemptAtMs = null;
            Exhausted = false;
        }

        /// <summary>
        /// Returns whether a caller may attempt to connect at the supplied scheduler time.
        /// </summary>
        public bool CanAttempt(long nowMs)
        {
            return !Exhausted && (!NextAttemptAtMs.HasValue || nowMs >= NextAttemptAtMs.Value);
        }

        /// <summary>
        /// Records a failed connection attempt and schedules the next retry.
        /// </summary>
        public CultNetReconnectDecision RecordFailure(long nowMs, int jitterMs = 0)
        {
            var nextAttempt = Attempt + 1;
            if (Policy.MaxAttempts.HasValue && nextAttempt > Policy.MaxAttempts.Value)
            {
                Exhausted = true;
                NextAttemptAtMs = null;
                return new CultNetReconnectDecision
                {
                    Attempt = Attempt,
                    ShouldRetry = false,
                    DelayMs = 0,
                    NextAttemptAtMs = null,
                    Exhausted = true
                };
            }

            Attempt = nextAttempt;
            var delayMs = CultNetReconnectPolicies.ComputeDelayMs(Policy, Attempt, jitterMs);
            NextAttemptAtMs = nowMs + delayMs;
            return new CultNetReconnectDecision
            {
                Attempt = Attempt,
                ShouldRetry = true,
                DelayMs = delayMs,
                NextAttemptAtMs = NextAttemptAtMs,
                Exhausted = false
            };
        }
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
        /// <summary>
        /// Gets or sets the advertised reconnect policy for this TCP transport.
        /// </summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
    }

    /// <summary>
    /// Options for creating a LiteNetLib transport profile.
    /// </summary>
    public sealed class LiteNetLibTransportProfileOptions
    {
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "litenetlib";
        /// <summary>
        /// Gets or sets the advertised host.
        /// </summary>
        public string? Host { get; set; }
        /// <summary>
        /// Gets or sets the advertised port.
        /// </summary>
        public int? Port { get; set; }
        /// <summary>
        /// Gets or sets the maximum payload size for LiteNetLib channels.
        /// </summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>
        /// Gets or sets the advertised reconnect policy for this LiteNetLib transport.
        /// </summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
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
        /// <summary>
        /// Gets or sets the maximum pending reliable packet count for RUDP channels.
        /// </summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>
        /// Gets or sets the advertised reconnect policy for this RUDP transport.
        /// </summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
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
                        ReconnectPolicy = options.ReconnectPolicy ?? CultNetReconnectPolicies.CreateDefault(),
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
        /// Creates a profile for the current C# LiteNetLib production lane.
        /// </summary>
        public static CultNetTransportProfile CreateLiteNetLib(
            string runtimeId,
            LiteNetLibTransportProfileOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(runtimeId)) throw new ArgumentException("Runtime id is required.", nameof(runtimeId));
            options ??= new LiteNetLibTransportProfileOptions();
            return new CultNetTransportProfile
            {
                RuntimeId = runtimeId,
                Transports =
                [
                    new CultNetTransportDescriptor
                    {
                        TransportId = string.IsNullOrWhiteSpace(options.TransportId)
                            ? "litenetlib"
                            : options.TransportId,
                        Protocol = "litenetlib",
                        Host = options.Host,
                        Port = options.Port,
                        WireContracts =
                        [
                            CultNetWireContracts.SchemaV0,
                            CultNetWireContracts.GameCultNetworkingV0
                        ],
                        ReconnectPolicy = options.ReconnectPolicy ?? CultNetReconnectPolicies.CreateDefault(),
                        Channels =
                        [
                            new CultNetTransportChannel
                            {
                                ChannelId = "schema",
                                Delivery = "reliable",
                                Ordering = "ordered",
                                MaxPayloadBytes = options.MaxPayloadBytes
                            },
                            new CultNetTransportChannel
                            {
                                ChannelId = "legacy",
                                Delivery = "reliable",
                                Ordering = "ordered",
                                MaxPayloadBytes = options.MaxPayloadBytes
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
                        ReconnectPolicy = options.ReconnectPolicy ?? CultNetReconnectPolicies.CreateDefault(),
                        Channels =
                        [
                            new CultNetTransportChannel
                            {
                                ChannelId = "schema",
                                Delivery = "reliable",
                                Ordering = "ordered",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes,
                                MaxPendingReliablePackets = options.MaxPendingReliablePackets
                            },
                            new CultNetTransportChannel
                            {
                                ChannelId = "latest",
                                Delivery = "unreliable",
                                Ordering = "sequenced",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes,
                                MaxPendingReliablePackets = options.MaxPendingReliablePackets
                            },
                            new CultNetTransportChannel
                            {
                                ChannelId = "realtime",
                                Delivery = "unreliable",
                                Ordering = "unordered",
                                MaxPayloadBytes = options.MaxPayloadBytes,
                                MaxFragmentBytes = options.MaxFragmentBytes,
                                MaxPendingReliablePackets = options.MaxPendingReliablePackets
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

            // Compared as a long: a declared length past int.MaxValue is a malformed packet, not an overflow.
            var declaredPayloadLength = BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(30, 4));
            if (declaredPayloadLength != (long)wire.Length - headerBytes)
            {
                throw new InvalidOperationException("CultNet RUDP packet payload length does not match the packet size.");
            }

            var payloadLength = (int)declaredPayloadLength;

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
        /// Gets or sets queued reliable packets admitted by newly received acknowledgements.
        /// </summary>
        public IReadOnlyList<CultNetRudpPacket> ReadyToSend { get; set; } = Array.Empty<CultNetRudpPacket>();
        /// <summary>
        /// Gets or sets an optional immediate reply packet.
        /// </summary>
        public CultNetRudpPacket? Reply { get; set; }
        /// <summary>
        /// Gets or sets whether the packet was a pong response.
        /// </summary>
        public bool Pong { get; set; }
        /// <summary>
        /// Gets or sets the pong payload bytes.
        /// </summary>
        public byte[] PongPayload { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets whether the remote peer sent a disconnect packet.
        /// </summary>
        public bool Disconnected { get; set; }
        /// <summary>
        /// Gets or sets the remote disconnect reason bytes.
        /// </summary>
        public byte[] DisconnectReason { get; set; } = Array.Empty<byte>();
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
        /// Gets or sets the first local packet sequence. Unset, each session draws its own at random:
        /// every session is a new identity to its peer, and an options object reused for several
        /// must not give them one sequence.
        /// </summary>
        public uint? InitialSequence { get; set; }
        /// <summary>
        /// Gets or sets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; set; } = 250;
        /// <summary>
        /// Gets or sets the maximum pending reliable packet count.
        /// </summary>
        public int? MaxPendingReliablePackets { get; set; }
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
    /// Role for a single-peer CultNet RUDP socket transport.
    /// </summary>
    public enum CultNetRudpSocketMode
    {
        /// <summary>
        /// Initiates the connect packet.
        /// </summary>
        Client,
        /// <summary>
        /// Accepts the first connect packet from a remote endpoint.
        /// </summary>
        Server
    }

    /// <summary>
    /// Options for binding a CultNet RUDP session to a UDP socket.
    /// </summary>
    public sealed class CultNetRudpSocketTransportOptions
    {
        /// <summary>
        /// Gets or sets the runtime id advertised by this transport.
        /// </summary>
        public string RuntimeId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the bound UDP socket.
        /// </summary>
        public Socket Socket { get; set; } = null!;
        /// <summary>
        /// Gets or sets whether this side initiates or accepts the handshake.
        /// </summary>
        public CultNetRudpSocketMode Mode { get; set; }
        /// <summary>
        /// Gets or sets the expected remote endpoint. Servers may leave this unset until the first connect packet.
        /// </summary>
        public EndPoint? RemoteEndPoint { get; set; }
        /// <summary>
        /// Gets or sets the RUDP connection/session binding id.
        /// </summary>
        public uint ConnectionId { get; set; }
        /// <summary>
        /// Gets or sets the first local packet sequence. Unset, each session draws its own at random:
        /// every session is a new identity to its peer, and an options object reused for several
        /// must not give them one sequence.
        /// </summary>
        public uint? InitialSequence { get; set; }
        /// <summary>
        /// Gets or sets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; set; } = 250;
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "rudp";
        /// <summary>
        /// Gets or sets the maximum payload size for RUDP channels.
        /// </summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum fragment size for RUDP channels.
        /// </summary>
        public int? MaxFragmentBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum pending reliable packet count for RUDP channels.
        /// </summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>
        /// Gets or sets the advertised reconnect policy for this RUDP transport.
        /// </summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
    }

    /// <summary>
    /// Socket-free reliability state machine for CultNet RUDP.
    /// </summary>
    public sealed class CultNetRudpSession
    {
        /// <summary>Maximum reliable packets admitted to the wire before acknowledgements advance the window.</summary>
        public const int ReliableSendWindowPackets = 32;
        // Flow window: a reliable packet is admitted only while its sequence is at most FlowWindowSequences above the
        // lowest unacked one and the payload above that sequence stays within FlowWindowBytes.
        private const uint FlowWindowSequences = 1023;
        private const int FlowWindowBytes = 4 * 1024 * 1024;
        private const int ReceivedSequenceWindow = 4096;
        // How long a client keeps retransmitting a Connect nobody answered before it abandons that
        // attempt for a fresh one. A Connect the server answers with an Ack, never an Accept, is one
        // the server judged stale (see AcceptConnect); retransmitting the same sequence would never
        // change its mind.
        private const long ConnectAttemptMs = 3_000;
        private sealed class PendingReliablePacket
        {
            public CultNetRudpPacket Packet { get; set; } = new CultNetRudpPacket();
            public long LastSentAtMs { get; set; }
        }

        private sealed class FragmentBuffer
        {
            public string ChannelId { get; set; } = string.Empty;
            public bool Ordered { get; set; }
            public ushort FragmentCount { get; set; }
            public Dictionary<ushort, byte[]> Payloads { get; } = new Dictionary<ushort, byte[]>();
            public Dictionary<ushort, uint> Sequences { get; } = new Dictionary<ushort, uint>();
        }

        private uint _nextSequence;
        private readonly Dictionary<string, uint> _nextSequencedByChannel = new Dictionary<string, uint>(StringComparer.Ordinal);
        private ushort _nextFragmentId = 1;
        private readonly int? _maxPendingReliablePackets;
        private volatile bool _connected;
        // Advances every time a generation ends. Everything issued in a generation (writes, flushes)
        // belongs to it and dies with it.
        private long _generation;
        // True from the end of a generation until the next Connect or Accept begins one. A flush started
        // in that interval has no live generation to wait on.
        private bool _ended;
        // The sequence of the Connect that started the current generation: sent by this side, or
        // accepted from the peer. A Connect repeats exactly when the session is connected and the
        // Connect carries this sequence.
        private uint? _connectSequence;
        // A Connect this side sent is unanswered. Only then is an Accept honoured.
        private bool _awaitingAccept;
        // When the unanswered Connect first went out; DueResends abandons it for a fresh attempt once
        // ConnectAttemptMs has passed.
        private long _connectStartedAtMs;
        // The payload of the Connect this side sent, kept so a fresh attempt can always be built
        // whatever became of the pending packet.
        private byte[] _connectPayload = Array.Empty<byte>();
        private long? _lastReceivedAtMs;
        private uint? _highestReceivedSequence;
        private readonly HashSet<uint> _receivedSequences = new HashSet<uint>();
        private readonly Dictionary<string, uint> _latestSequencedByChannel = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly object _pendingReliableGate = new object();
        private readonly Dictionary<uint, PendingReliablePacket> _pendingReliable = new Dictionary<uint, PendingReliablePacket>();
        // Payload sizes of acknowledged reliable sequences above the lowest unacknowledged one. The receiver still
        // holds those bytes behind the gap, so the flow window keeps counting them until the lowest sequence passes them.
        private readonly Dictionary<uint, int> _ackedAboveLowest = new Dictionary<uint, int>();
        private readonly Queue<CultNetRudpPacket> _queuedReliable = new Queue<CultNetRudpPacket>();
        // Every reliable sequence up to and including this one has been received since the peer state
        // was last reset. Only the handshake seeds it: the peer's Connect on the accepting side, the
        // peer's Accept on the connecting side. Reliable data that arrives before that is refused.
        private uint? _receivedThrough;
        // Ordered frames received but not yet deliverable, keyed by first sequence. A frame is held for
        // exactly one reason: a reliable sequence below it has not arrived.
        private readonly SortedDictionary<uint, CultNetRudpDeliveredFrame> _orderedHeld =
            new SortedDictionary<uint, CultNetRudpDeliveredFrame>();
        private readonly Dictionary<string, FragmentBuffer> _fragmentBuffers = new Dictionary<string, FragmentBuffer>(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a RUDP reliability session.
        /// </summary>
        public CultNetRudpSession(CultNetRudpSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            ValidateLimits(options.InitialSequence, options.MaxPendingReliablePackets);

            ConnectionId = options.ConnectionId;
            _nextSequence = options.InitialSequence ?? DrawSequence();
            ResendDelayMs = options.ResendDelayMs;
            _maxPendingReliablePackets = options.MaxPendingReliablePackets;
        }

        /// <summary>
        /// A sequence drawn from a secure source in [1, 2^31). The Connect's sequence is what tells a
        /// peer whether a Connect repeats one it already accepted or starts a new session, so two
        /// sessions must not share one by default.
        /// </summary>
        private static uint DrawSequence()
        {
            const uint ceiling = 1u << 31;
            var bytes = new byte[4];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return 1u + BitConverter.ToUInt32(bytes, 0) % (ceiling - 1u);
        }

        // Whether `sequence` is at or before `mark` in serial order, within the receive window. The
        // compare is modular, so it holds across the wrap of the 32-bit space; a sequence further back
        // than the window is the duplicate test's below-window clause, and one ahead of the mark is
        // never before it.
        private static bool AtOrBefore(uint sequence, uint mark) =>
            unchecked(mark - sequence) < ReceivedSequenceWindow;

        internal static readonly byte[] RefusedPacketReason = Encoding.UTF8.GetBytes("session refused a packet");

        /// <summary>
        /// A session cannot admit a Connect when its sequence space starts exhausted or its reliable
        /// queue holds nothing, so neither is a usable configuration.
        /// </summary>
        internal static void ValidateLimits(uint? initialSequence, int? maxPendingReliablePackets)
        {
            if (initialSequence == uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(initialSequence), "RUDP InitialSequence must leave room for a reliable packet.");
            }

            if (maxPendingReliablePackets.HasValue && maxPendingReliablePackets.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingReliablePackets), "RUDP MaxPendingReliablePackets must be greater than zero.");
            }
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
        internal long Generation => Interlocked.Read(ref _generation);
        internal bool Ended => _ended;
        /// <summary>
        /// Gets the logical time of the last received packet.
        /// </summary>
        public long? LastReceivedAtMs => _lastReceivedAtMs;
        /// <summary>
        /// Gets reliable packet sequences awaiting acknowledgement.
        /// </summary>
        public IReadOnlyList<uint> PendingReliableSequences
        {
            get
            {
                lock (_pendingReliableGate)
                {
                    return _pendingReliable.Keys.OrderBy(value => value).ToArray();
                }
            }
        }

        /// <summary>Gets the number of reliable packets waiting for admission to the wire.</summary>
        public int QueuedReliablePacketCount
        {
            get { lock (_pendingReliableGate) return _queuedReliable.Count; }
        }

        /// <summary>Gets all reliable packets that have not yet been acknowledged.</summary>
        public int OutstandingReliablePacketCount
        {
            get { lock (_pendingReliableGate) return _pendingReliable.Count + _queuedReliable.Count; }
        }

        /// <summary>
        /// Creates a reliable ordered connect packet.
        /// </summary>
        public CultNetRudpPacket CreateConnect(long nowMs = 0, byte[]? payload = null)
        {
            // A session that has had a peer starts a new generation: nothing it learned from that
            // peer describes the one this Connect reaches, and what it still owed that peer no
            // longer takes room in the queue.
            if (_connectSequence.HasValue)
                ResetPeerState();
            EnsureReliableCapacity(1);
            _ended = false;
            var packet = CreatePacket(CultNetRudpPacketType.Connect, "control", payload ?? Array.Empty<byte>(), reliable: true, ordered: true, sequenced: false);
            _connectSequence = packet.Sequence;
            _awaitingAccept = true;
            _connectStartedAtMs = nowMs;
            _connectPayload = packet.Payload ?? Array.Empty<byte>();
            TrackReliable(packet, nowMs);
            return packet;
        }

        /// <summary>
        /// Whether <paramref name="packet"/> is a Connect this session's current generation already owns:
        /// a retransmit of the Connect that started it (the session is connected and the sequence is
        /// that Connect's), or a stale copy of an earlier attempt by the same client (a sequence before
        /// it, within the receive window). Servers that keep one session per peer ask this to tell a
        /// Connect that starts a new session from one that does not; anything else that reaches
        /// <see cref="AcceptConnect"/> starts a new generation.
        /// </summary>
        public bool ConnectRepeats(CultNetRudpPacket packet)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            return packet.PacketType == CultNetRudpPacketType.Connect
                && _connected
                && _connectSequence.HasValue
                && (_connectSequence.Value == packet.Sequence
                    || ConnectIsStale(packet.Sequence, _connectSequence.Value));
        }

        // A Connect that precedes the current generation's within the receive window is the client's
        // earlier attempt, delayed in the network: a client that retried never sends a lower sequence
        // again. Restarting on it would strand the client, which honours only the Accept for its newest
        // Connect. The rule is TCP's answer to a delayed SYN (RFC 5961's challenge ACK): keep the
        // connection and answer with an Ack. A restarted client whose random initial sequence lands in
        // this window is answered the same way and abandons the attempt for a fresh draw
        // (ConnectAttemptMs).
        private static bool ConnectIsStale(uint sequence, uint current) =>
            sequence != current && AtOrBefore(sequence, current);

        /// <summary>
        /// Answers a connect packet. A repeat of the accepted connect queues nothing, so a connect storm
        /// cannot grow the reliable queue: the reply is the accept still awaiting acknowledgement, or an
        /// ack once it was acknowledged. A stale copy of an earlier attempt gets the same reply and
        /// changes nothing else: it is not evidence the peer is alive. Any other connect ends the
        /// current generation, forgets the peer and accepts a new one with a reliable ordered accept
        /// packet.
        /// </summary>
        public CultNetRudpPacket AcceptConnect(CultNetRudpPacket packet, long nowMs = 0, byte[]? payload = null)
        {
            RequireConnection(packet);
            if (packet.PacketType != CultNetRudpPacketType.Connect)
            {
                throw new InvalidOperationException($"Expected RUDP connect packet, got {packet.PacketType}.");
            }

            if (ConnectRepeats(packet))
            {
                if (_connectSequence == packet.Sequence)
                {
                    ApplyAcknowledgements(packet);
                    RememberReceived(packet.Sequence);
                    _lastReceivedAtMs = nowMs;
                }
                return PendingAcceptForResend(nowMs) ?? CreateAck();
            }
            ResetPeerState();
            EnsureReliableCapacity(1);
            SeedReceived(packet.Sequence);
            _lastReceivedAtMs = nowMs;
            _connectSequence = packet.Sequence;
            _connected = true;
            _ended = false;
            var response = CreatePacket(CultNetRudpPacketType.Accept, "control", payload ?? Array.Empty<byte>(), reliable: true, ordered: true, sequenced: false);
            TrackReliable(response, nowMs);
            return response;
        }

        // The Accept still awaiting acknowledgement, resent.
        private CultNetRudpPacket? PendingAcceptForResend(long nowMs)
        {
            lock (_pendingReliableGate)
            {
                foreach (var pending in _pendingReliable.Values)
                {
                    if (pending.Packet.PacketType != CultNetRudpPacketType.Accept)
                        continue;
                    pending.LastSentAtMs = nowMs;
                    return ClonePacket(pending.Packet);
                }
            }

            return null;
        }

        /// <summary>
        /// Creates a data packet.
        /// </summary>
        public CultNetRudpPacket Send(string channelId, byte[] payload, CultNetRudpSendOptions? options = null)
        {
            options ??= new CultNetRudpSendOptions();
            lock (_pendingReliableGate)
            {
                if (options.Reliable && (_queuedReliable.Count > 0 || !WindowAdmits(_nextSequence, payload.Length)))
                    throw new InvalidOperationException("RUDP reliable send window is full; receive acknowledgements before sending.");
            }
            return SendMany(channelId, payload, options).First();
        }

        /// <summary>
        /// Creates one or more data packets, fragmenting when requested.
        /// </summary>
        public IReadOnlyList<CultNetRudpPacket> SendMany(string channelId, byte[] payload, CultNetRudpSendOptions? options = null, int? maxFragmentBytes = null)
        {
            if (!_connected)
            {
                throw new InvalidOperationException("Cannot send RUDP data before the session is connected.");
            }

            options ??= new CultNetRudpSendOptions();
            if (options.Ordered && !options.Reliable)
            {
                throw new InvalidOperationException("RUDP ordered delivery requires reliability.");
            }
            payload ??= Array.Empty<byte>();
            if (maxFragmentBytes.HasValue && maxFragmentBytes.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFragmentBytes), "RUDP maxFragmentBytes must be greater than zero.");
            }

            if (maxFragmentBytes.HasValue && payload.Length > maxFragmentBytes.Value)
            {
                var fragmentCount = (payload.Length + maxFragmentBytes.Value - 1) / maxFragmentBytes.Value;
                if (fragmentCount > ushort.MaxValue)
                {
                    throw new InvalidOperationException("RUDP payload requires more than 65535 fragments.");
                }

                EnsureReliableCapacity(options.Reliable ? fragmentCount : 0);
                var fragmentId = AllocateFragmentId();
                var packets = new List<CultNetRudpPacket>();
                for (var index = 0; index < fragmentCount; index++)
                {
                    var start = index * maxFragmentBytes.Value;
                    var length = Math.Min(maxFragmentBytes.Value, payload.Length - start);
                    var chunk = new byte[length];
                    Array.Copy(payload, start, chunk, 0, length);
                    var fragmentPacket = CreatePacket(
                        CultNetRudpPacketType.Data,
                        channelId,
                        chunk,
                        options.Reliable,
                        options.Ordered,
                        options.Sequenced,
                        fragmentId,
                        (ushort)index,
                        (ushort)fragmentCount);
                    packets.Add(fragmentPacket);
                }
                return options.Reliable
                    ? AdmitReliablePackets(packets, options.NowMs)
                    : packets;
            }

            EnsureReliableCapacity(options.Reliable ? 1 : 0);
            var packet = CreatePacket(
                CultNetRudpPacketType.Data,
                channelId,
                payload,
                options.Reliable,
                options.Ordered,
                options.Sequenced);
            if (packet.Reliable)
            {
                return AdmitReliablePackets(new[] { packet }, options.NowMs);
            }
            return new[] { packet };
        }

        /// <summary>
        /// Applies a remote packet to the session.
        /// </summary>
        public CultNetRudpReceiveResult Receive(CultNetRudpPacket packet, long nowMs = 0)
        {
            RequireConnection(packet);
            // An Accept counts only while this side's Connect is unanswered and the Accept names it.
            // A late or duplicate one, or one from an earlier generation, must not seed the watermark
            // or revive an ended session.
            var honoursAccept = packet.PacketType == CultNetRudpPacketType.Accept
                && _awaitingAccept
                && _connectSequence.HasValue
                && PacketAcknowledges(packet, _connectSequence.Value);
            if (packet.PacketType == CultNetRudpPacketType.Accept && !honoursAccept)
            {
                return new CultNetRudpReceiveResult();
            }
            // While this side's Connect awaits its Accept, only the Accept it honours retires it: an Ack
            // that names the Connect (a server's reply to a repeat or a stale copy) says the server did
            // not start a session.
            if (honoursAccept || !_awaitingAccept)
                ApplyAcknowledgements(packet);
            var readyToSend = PromoteQueuedReliable(nowMs);
            _lastReceivedAtMs = nowMs;

            if (honoursAccept)
            {
                _awaitingAccept = false;
                SeedReceived(packet.Sequence);
                _connected = true;
                return new CultNetRudpReceiveResult { ReadyToSend = readyToSend };
            }

            if (packet.PacketType == CultNetRudpPacketType.Ping)
            {
                return new CultNetRudpReceiveResult
                {
                    ReadyToSend = readyToSend,
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
                return new CultNetRudpReceiveResult
                {
                    Delivered = Array.Empty<CultNetRudpDeliveredFrame>(),
                    ReadyToSend = readyToSend,
                    Pong = packet.PacketType == CultNetRudpPacketType.Pong,
                    PongPayload = packet.PacketType == CultNetRudpPacketType.Pong
                        ? packet.Payload ?? Array.Empty<byte>()
                        : Array.Empty<byte>()
                };
            }

            if (packet.PacketType == CultNetRudpPacketType.Disconnect)
            {
                EndSession();
                return new CultNetRudpReceiveResult
                {
                    ReadyToSend = readyToSend,
                    Disconnected = true,
                    DisconnectReason = packet.Payload ?? Array.Empty<byte>()
                };
            }

            if (packet.PacketType != CultNetRudpPacketType.Data)
            {
                return new CultNetRudpReceiveResult { ReadyToSend = readyToSend };
            }

            // Reliable data before the handshake has seeded the watermark has no place in the order:
            // refuse it unremembered, so it is not acknowledged and the sender retransmits it once
            // the handshake is done.
            if (packet.Reliable && !_receivedThrough.HasValue)
            {
                return new CultNetRudpReceiveResult { ReadyToSend = readyToSend };
            }

            var duplicate = packet.Reliable && WasReceived(packet.Sequence);
            if (packet.Reliable)
            {
                RememberReceived(packet.Sequence);
            }
            if (duplicate)
            {
                return new CultNetRudpReceiveResult { ReadyToSend = readyToSend };
            }

            var delivered = new List<CultNetRudpDeliveredFrame>();
            var reassembled = Reassemble(packet);
            if (reassembled != null)
            {
                if (reassembled.Ordered)
                {
                    _orderedHeld[reassembled.Frame.Sequence] = reassembled.Frame;
                }
                else if (!packet.Sequenced)
                {
                    delivered.Add(reassembled.Frame);
                }
                else
                {
                    var newestSequence = reassembled.NextSequence - 1;
                    if (!_latestSequencedByChannel.TryGetValue(reassembled.Frame.ChannelId, out var latestSequence)
                        || newestSequence > latestSequence)
                    {
                        _latestSequencedByChannel[reassembled.Frame.ChannelId] = newestSequence;
                        delivered.Add(reassembled.Frame);
                    }
                }
            }

            // Any reliable packet may have advanced the watermark, a fragment or an unordered frame as
            // much as an ordered one, so the drain runs after all of them.
            delivered.AddRange(DrainOrdered());
            return new CultNetRudpReceiveResult { ReadyToSend = readyToSend, Delivered = delivered };
        }

        /// <summary>
        /// Creates a packet carrying the current acknowledgement state.
        /// </summary>
        public CultNetRudpPacket CreateAck(uint? acknowledgedSequence = null)
        {
            var (ack, ackMask) = AckState();
            if (acknowledgedSequence.HasValue)
            {
                ack = acknowledgedSequence.Value;
                ackMask = 0;
            }
            return new CultNetRudpPacket
            {
                PacketType = CultNetRudpPacketType.Ack,
                ConnectionId = ConnectionId,
                Sequence = 0,
                Ack = ack,
                AckMask = ackMask,
                ChannelId = "control",
                Payload = Array.Empty<byte>()
            };
        }

        /// <summary>
        /// Creates a cumulative acknowledgement when the received packet is inside the
        /// acknowledgement horizon, or an exact acknowledgement for an older retransmit.
        /// </summary>
        public CultNetRudpPacket CreateAckForReceived(uint receivedSequence)
        {
            // What Receive refused is not acknowledged by name (the ack carries only what was
            // received), so its sender retransmits it.
            if (!WasReceived(receivedSequence))
                return CreateAck();
            var (ack, _) = AckState();
            return ack >= receivedSequence && ack - receivedSequence <= 32
                ? CreateAck()
                : CreateAck(receivedSequence);
        }

        /// <summary>
        /// Creates a packet carrying a keepalive ping payload.
        /// </summary>
        public CultNetRudpPacket CreatePing(byte[]? payload = null)
        {
            return CreatePacket(CultNetRudpPacketType.Ping, "control", payload ?? Array.Empty<byte>(), reliable: false, ordered: false, sequenced: false);
        }

        /// <summary>
        /// The one way a session generation ends. The session stops being connected and what it still
        /// owed the peer dies with it: a write not yet acknowledged is dropped, so no later session
        /// retransmits it or credits an ack to it. What was learned from the peer is not touched: the
        /// peer may not know the session ended, and forgetting what it sent would let its retransmits
        /// be delivered twice.
        /// </summary>
        private void EndSession()
        {
            _connected = false;
            _ended = true;
            _awaitingAccept = false;
            Interlocked.Increment(ref _generation);
            lock (_pendingReliableGate)
            {
                _pendingReliable.Clear();
                _ackedAboveLowest.Clear();
                _queuedReliable.Clear();
            }
        }

        /// <summary>
        /// Ends the current generation and forgets everything learned from the peer; sequence numbers
        /// already issued stay issued.
        /// </summary>
        public void ResetPeerState()
        {
            EndSession();
            _lastReceivedAtMs = null;
            _highestReceivedSequence = null;
            _receivedSequences.Clear();
            _nextSequencedByChannel.Clear();
            _latestSequencedByChannel.Clear();
            _receivedThrough = null;
            _orderedHeld.Clear();
            _fragmentBuffers.Clear();
        }

        /// <summary>
        /// Ends a session that refused a packet. Receive has already recorded the packet's reliable
        /// sequence, so the peer's sequences are forgotten before the goodbye is built: its ack field
        /// would otherwise acknowledge the very frame the session refused.
        /// </summary>
        internal CultNetRudpPacket EndRefused()
        {
            ResetPeerState();
            return CreateDisconnect(RefusedPacketReason);
        }

        /// <summary>
        /// Creates a packet carrying a transport-level disconnect reason.
        /// </summary>
        public CultNetRudpPacket CreateDisconnect(byte[]? reason = null)
        {
            EndSession();
            return CreatePacket(CultNetRudpPacketType.Disconnect, "control", reason ?? Array.Empty<byte>(), reliable: false, ordered: false, sequenced: false);
        }

        /// <summary>
        /// Marks the session disconnected when no packet has arrived within the timeout window.
        /// </summary>
        public bool CheckTimeout(long nowMs, long timeoutMs)
        {
            if (!_connected || !_lastReceivedAtMs.HasValue)
            {
                return false;
            }
            if (nowMs - _lastReceivedAtMs.Value <= timeoutMs)
            {
                return false;
            }
            EndSession();
            return true;
        }

        /// <summary>
        /// Returns reliable packets due for resend at the supplied logical time.
        /// </summary>
        public IReadOnlyList<CultNetRudpPacket> DueResends(long nowMs)
        {
            var fresh = AbandonUnansweredConnect(nowMs);
            if (fresh != null)
                return new[] { fresh };
            var due = new List<CultNetRudpPacket>();
            lock (_pendingReliableGate)
            {
                foreach (var pending in _pendingReliable.Values)
                {
                    if (nowMs - pending.LastSentAtMs >= ResendDelayMs)
                    {
                        pending.LastSentAtMs = nowMs;
                        due.Add(ClonePacket(pending.Packet));
                    }
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
            return CreatePacket(packetType, channelId, payload, reliable, ordered, sequenced, 0, 0, 0);
        }

        private CultNetRudpPacket CreatePacket(
            CultNetRudpPacketType packetType,
            string channelId,
            byte[] payload,
            bool reliable,
            bool ordered,
            bool sequenced,
            ushort fragmentId,
            ushort fragmentIndex,
            ushort fragmentCount)
        {
            uint sequence;
            if (reliable)
            {
                sequence = _nextSequence;
                _nextSequence = checked(_nextSequence + 1);
            }
            else if (sequenced)
            {
                if (!_nextSequencedByChannel.TryGetValue(channelId, out sequence))
                {
                    sequence = 1;
                }
                _nextSequencedByChannel[channelId] = checked(sequence + 1);
            }
            else
            {
                sequence = 0;
            }
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
                FragmentId = fragmentId,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        private void TrackReliable(CultNetRudpPacket packet, long nowMs)
        {
            lock (_pendingReliableGate)
            {
                _pendingReliable[packet.Sequence] = new PendingReliablePacket
                {
                    Packet = ClonePacket(packet),
                    LastSentAtMs = nowMs
                };
            }
        }

        /// <summary>
        /// Whether a reliable packet may go on the wire now: the window has a slot, and its sequence and the
        /// payload above the lowest unacked sequence stay inside the flow window. With nothing pending, any
        /// packet is admissible. Callers hold the pending gate.
        /// </summary>
        private bool WindowAdmits(uint sequence, int payloadLength)
        {
            if (_pendingReliable.Count >= ReliableSendWindowPackets)
                return false;
            if (_pendingReliable.Count == 0)
                return true;
            var lowest = _pendingReliable.Keys.Min();
            long bytesAbove = 0;
            foreach (var pending in _pendingReliable)
            {
                if (pending.Key > lowest)
                    bytesAbove += pending.Value.Packet.Payload.Length;
            }
            foreach (var length in _ackedAboveLowest.Values)
                bytesAbove += length;
            return sequence - lowest <= FlowWindowSequences && bytesAbove + payloadLength <= FlowWindowBytes;
        }

        private IReadOnlyList<CultNetRudpPacket> AdmitReliablePackets(
            IReadOnlyList<CultNetRudpPacket> packets,
            long nowMs)
        {
            lock (_pendingReliableGate)
            {
                var ready = new List<CultNetRudpPacket>();
                foreach (var packet in packets)
                {
                    if (_queuedReliable.Count == 0 && WindowAdmits(packet.Sequence, packet.Payload.Length))
                    {
                        _pendingReliable[packet.Sequence] = new PendingReliablePacket
                        {
                            Packet = ClonePacket(packet),
                            LastSentAtMs = nowMs
                        };
                        ready.Add(packet);
                    }
                    else
                    {
                        _queuedReliable.Enqueue(ClonePacket(packet));
                    }
                }
                return ready;
            }
        }

        private IReadOnlyList<CultNetRudpPacket> PromoteQueuedReliable(long nowMs)
        {
            lock (_pendingReliableGate)
            {
                var ready = new List<CultNetRudpPacket>();
                while (_queuedReliable.Count > 0
                    && WindowAdmits(_queuedReliable.Peek().Sequence, _queuedReliable.Peek().Payload.Length))
                {
                    var packet = _queuedReliable.Dequeue();
                    _pendingReliable[packet.Sequence] = new PendingReliablePacket
                    {
                        Packet = ClonePacket(packet),
                        LastSentAtMs = nowMs
                    };
                    ready.Add(packet);
                }
                return ready;
            }
        }

        private void EnsureReliableCapacity(int packetCount)
        {
            if (packetCount == 0 || !_maxPendingReliablePackets.HasValue)
            {
                return;
            }

            lock (_pendingReliableGate)
            {
                if (_pendingReliable.Count + _queuedReliable.Count + packetCount > _maxPendingReliablePackets.Value)
                {
                    throw new InvalidOperationException("RUDP reliable send queue is full.");
                }
            }
        }

        private void ApplyAcknowledgements(CultNetRudpPacket packet)
        {
            lock (_pendingReliableGate)
            {
                Acknowledge(packet.Ack);
                for (var bit = 0; bit < 32; bit++)
                {
                    if ((packet.AckMask & (1u << bit)) != 0 && packet.Ack > bit)
                    {
                        Acknowledge(packet.Ack - (uint)bit - 1);
                    }
                }
                // Acknowledged sizes stop counting once the lowest unacknowledged sequence passes them, and all
                // of them stop when nothing is unacknowledged.
                if (_pendingReliable.Count == 0)
                {
                    _ackedAboveLowest.Clear();
                }
                else
                {
                    var lowest = _pendingReliable.Keys.Min();
                    foreach (var sequence in _ackedAboveLowest.Keys.Where(sequence => sequence <= lowest).ToArray())
                        _ackedAboveLowest.Remove(sequence);
                }
            }
        }

        // Callers hold the pending gate.
        private void Acknowledge(uint sequence)
        {
            if (_pendingReliable.Remove(sequence, out var pending))
                _ackedAboveLowest[sequence] = pending.Packet.Payload.Length;
        }

        private static bool PacketAcknowledges(CultNetRudpPacket packet, uint sequence)
        {
            if (packet.Ack == sequence)
                return true;
            for (var bit = 0; bit < 32; bit++)
            {
                if ((packet.AckMask & (1u << bit)) != 0 && packet.Ack > bit && packet.Ack - (uint)bit - 1 == sequence)
                    return true;
            }
            return false;
        }

        // A Connect unanswered for ConnectAttemptMs is replaced, not retransmitted further, by a Connect
        // with the same payload and the abandoned sequence plus the receive window less one. Nothing
        // was ever sent in an unanswered generation, so no sequence issued so far is owed. The jump
        // keeps a late copy of the abandoned Connect inside the new one's stale window, so a server
        // that took the new Connect answers the copy with an Ack instead of restarting; and it leaves
        // the stale window of whatever generation the server holds, unless that generation sits
        // exactly at the jump, and then the next attempt leaves it. The first Connect of a session is
        // the only one drawn at random.
        private CultNetRudpPacket? AbandonUnansweredConnect(long nowMs)
        {
            if (!_awaitingAccept || nowMs - _connectStartedAtMs < ConnectAttemptMs || !_connectSequence.HasValue)
                return null;
            var jumped = (ulong)_connectSequence.Value + ReceivedSequenceWindow - 1;
            _nextSequence = (uint)Math.Min(jumped, uint.MaxValue - 1);
            return CreateConnect(nowMs, _connectPayload);
        }

        // The handshake's one act on the watermark: the peer's Connect or Accept is the first sequence
        // of the session, whatever else it has sent.
        private void SeedReceived(uint sequence)
        {
            _receivedThrough = sequence;
            RememberReceived(sequence);
        }

        // The one duplicate test: true for a sequence in the window that was received, for one below
        // the window, and for one at or before the watermark. The watermark starts at the handshake's
        // seed, so a frame the peer sent in an earlier generation (every sequence it issued is below
        // the Connect that began this one) is a duplicate of something already delivered and is
        // acknowledged, never delivered again.
        private bool WasReceived(uint sequence)
        {
            return _receivedSequences.Contains(sequence)
                || (_highestReceivedSequence.HasValue
                    && sequence < _highestReceivedSequence.Value
                    && _highestReceivedSequence.Value - sequence >= ReceivedSequenceWindow)
                || (_receivedThrough.HasValue && AtOrBefore(sequence, _receivedThrough.Value));
        }

        private void RememberReceived(uint sequence)
        {
            _receivedSequences.Add(sequence);
            if (_receivedThrough.HasValue)
            {
                var through = _receivedThrough.Value;
                while (through != uint.MaxValue && _receivedSequences.Contains(through + 1))
                {
                    through++;
                }
                _receivedThrough = through;
            }
            if (!_highestReceivedSequence.HasValue || sequence > _highestReceivedSequence.Value)
            {
                _highestReceivedSequence = sequence;
            }
            if (_receivedSequences.Count > ReceivedSequenceWindow)
            {
                var keepFrom = (_highestReceivedSequence ?? sequence) >= ReceivedSequenceWindow - 1
                    ? (_highestReceivedSequence ?? sequence) - (uint)(ReceivedSequenceWindow - 1)
                    : 0;
                _receivedSequences.RemoveWhere(received => received < keepFrom);
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

        private sealed class ReassembledFrame
        {
            public CultNetRudpDeliveredFrame Frame { get; set; } = new CultNetRudpDeliveredFrame();
            public bool Ordered { get; set; }
            public uint NextSequence { get; set; }
        }

        private ReassembledFrame? Reassemble(CultNetRudpPacket packet)
        {
            if (packet.FragmentCount == 0)
            {
                return new ReassembledFrame
                {
                    Frame = new CultNetRudpDeliveredFrame
                    {
                        ChannelId = packet.ChannelId,
                        Payload = packet.Payload ?? Array.Empty<byte>(),
                        Sequence = packet.Sequence
                    },
                    Ordered = packet.Ordered,
                    NextSequence = packet.Sequence + 1
                };
            }
            if (packet.FragmentId == 0)
            {
                throw new InvalidOperationException("RUDP fragmented packet must have a non-zero fragment id.");
            }
            if (packet.FragmentIndex >= packet.FragmentCount)
            {
                throw new InvalidOperationException("RUDP fragment index must be lower than fragment count.");
            }

            var key = $"{packet.ChannelId}\0{packet.FragmentId}";
            if (!_fragmentBuffers.TryGetValue(key, out var buffer))
            {
                buffer = new FragmentBuffer
                {
                    ChannelId = packet.ChannelId,
                    Ordered = packet.Ordered,
                    FragmentCount = packet.FragmentCount
                };
                _fragmentBuffers[key] = buffer;
            }
            if (buffer.FragmentCount != packet.FragmentCount || buffer.Ordered != packet.Ordered)
            {
                throw new InvalidOperationException("RUDP fragment metadata changed within a fragment set.");
            }

            buffer.Payloads[packet.FragmentIndex] = packet.Payload?.ToArray() ?? Array.Empty<byte>();
            buffer.Sequences[packet.FragmentIndex] = packet.Sequence;
            if (buffer.Payloads.Count < packet.FragmentCount)
            {
                return null;
            }

            var payloadLength = buffer.Payloads.Values.Sum(chunk => chunk.Length);
            var payload = new byte[payloadLength];
            var offset = 0;
            for (ushort index = 0; index < packet.FragmentCount; index++)
            {
                var chunk = buffer.Payloads[index];
                Array.Copy(chunk, 0, payload, offset, chunk.Length);
                offset += chunk.Length;
            }
            var sequences = buffer.Sequences.Values.ToArray();
            _fragmentBuffers.Remove(key);
            return new ReassembledFrame
            {
                Frame = new CultNetRudpDeliveredFrame
                {
                    ChannelId = buffer.ChannelId,
                    Payload = payload,
                    Sequence = sequences.Min()
                },
                Ordered = buffer.Ordered,
                NextSequence = sequences.Max() + 1
            };
        }

        /// <summary>
        /// Delivers, in sequence order, every held ordered frame whose first sequence is at most one past
        /// the watermark. Ordered delivery has one owner: the watermark. A frame this call does not
        /// deliver waits for the sequence below it, and nothing else releases it.
        /// </summary>
        private IReadOnlyList<CultNetRudpDeliveredFrame> DrainOrdered()
        {
            var delivered = new List<CultNetRudpDeliveredFrame>();
            if (!_receivedThrough.HasValue)
            {
                return delivered;
            }

            var through = _receivedThrough.Value;
            foreach (var pair in _orderedHeld)
            {
                if (through != uint.MaxValue && pair.Key > through + 1)
                {
                    break;
                }
                delivered.Add(pair.Value);
            }
            foreach (var frame in delivered)
            {
                _orderedHeld.Remove(frame.Sequence);
            }
            return delivered;
        }

        private ushort AllocateFragmentId()
        {
            var fragmentId = _nextFragmentId;
            _nextFragmentId++;
            if (_nextFragmentId == 0)
            {
                _nextFragmentId = 1;
            }
            return fragmentId;
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
    /// Single-peer UDP socket binding for the CultNet RUDP reliability session.
    /// </summary>
    public sealed class CultNetRudpSocketTransportConnection : IDisposable
    {
        private const int MinimumReceiveBufferBytes = 4 * 1024 * 1024;
        // One acknowledgement carries the preceding 32-packet receive mask.
        // Pace at that transport window instead of sleeping after every fragment;
        // a one-millisecond sleep can consume a full scheduler quantum on Windows.
        private const int WireBurstPackets = 32;
        private readonly Socket _socket;
        private readonly CultNetRudpSession _session;
        private readonly object _sessionGate = new object();
        private readonly CultNetRudpSocketMode _mode;
        private readonly int? _maxFragmentBytes;
        private readonly CultNetTransportStats _stats = new CultNetTransportStats();
        private readonly Queue<CultNetTransportFrame> _deliveredFrames = new Queue<CultNetTransportFrame>();
        private readonly Queue<byte[]> _pongPayloads = new Queue<byte[]>();
        private EndPoint? _remoteEndPoint;
        private bool _disposed;

        /// <summary>
        /// Initializes a UDP socket binding around a CultNet RUDP session.
        /// </summary>
        public CultNetRudpSocketTransportConnection(CultNetRudpSocketTransportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.RuntimeId)) throw new ArgumentException("Runtime id is required.", nameof(options));
            _socket = options.Socket ?? throw new ArgumentNullException(nameof(options.Socket));
            if (_socket.ReceiveBufferSize < MinimumReceiveBufferBytes)
                _socket.ReceiveBufferSize = MinimumReceiveBufferBytes;
            _mode = options.Mode;
            _remoteEndPoint = options.RemoteEndPoint;
            _maxFragmentBytes = options.MaxFragmentBytes;
            _session = new CultNetRudpSession(new CultNetRudpSessionOptions
            {
                ConnectionId = options.ConnectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets
            });

            var local = _socket.LocalEndPoint as IPEndPoint;
            Profile = CultNetTransportProfiles.CreateRudp(
                options.RuntimeId,
                new RudpTransportProfileOptions
                {
                    TransportId = options.TransportId,
                    Host = local?.Address.ToString(),
                    Port = local?.Port,
                    MaxPayloadBytes = options.MaxPayloadBytes,
                    MaxFragmentBytes = options.MaxFragmentBytes,
                    MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                    ReconnectPolicy = options.ReconnectPolicy
                });
        }

        /// <summary>
        /// Gets the profile this connection implements.
        /// </summary>
        public CultNetTransportProfile Profile { get; }

        /// <summary>
        /// Gets whether the RUDP handshake has completed.
        /// </summary>
        public bool Connected { get { lock (_sessionGate) return _session.Connected; } }

        /// <summary>
        /// Gets a snapshot of the current transfer counters.
        /// </summary>
        public CultNetTransportStats Stats => _stats.Snapshot();

        /// <summary>
        /// Gets the last transport-level remote disconnect reason, if one was received.
        /// </summary>
        public byte[]? DisconnectReason { get; private set; }

        /// <summary>
        /// Attempts to dequeue the next received pong payload.
        /// </summary>
        public bool TryDequeuePongPayload(out byte[] payload)
        {
            if (_pongPayloads.Count > 0)
            {
                payload = _pongPayloads.Dequeue();
                return true;
            }
            payload = Array.Empty<byte>();
            return false;
        }

        /// <summary>
        /// Sends the client connect packet.
        /// </summary>
        public void Connect(byte[]? payload = null)
        {
            if (_mode != CultNetRudpSocketMode.Client)
            {
                throw new InvalidOperationException("Only a client RUDP socket transport can initiate connect.");
            }

            DisconnectReason = null;
            lock (_sessionGate)
                SendPacket(_session.CreateConnect(NowMs(), payload ?? Array.Empty<byte>()));
        }

        /// <summary>
        /// Sends the client connect packet with a UTF-8 payload.
        /// </summary>
        public void Connect(string payload)
        {
            Connect(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        }

        /// <summary>
        /// Sends the client connect packet and polls until the handshake completes or times out.
        /// </summary>
        public bool ConnectAndWait(
            byte[]? payload = null,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null)
        {
            Connect(payload);
            return AwaitConnected(timeout, pollInterval);
        }

        /// <summary>
        /// Sends the client connect packet with a UTF-8 payload and polls until the handshake completes or times out.
        /// </summary>
        public bool ConnectAndWait(
            string payload,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null)
        {
            return ConnectAndWait(Encoding.UTF8.GetBytes(payload ?? string.Empty), timeout, pollInterval);
        }

        /// <summary>
        /// Polls the transport until the RUDP handshake completes or times out.
        /// </summary>
        public bool AwaitConnected(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(1));
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                _ = ReceiveOnce();
                PollResends();
                if (Connected)
                {
                    return true;
                }

                Thread.Sleep(interval);
            }

            return Connected;
        }

        /// <summary>
        /// Sends a logical transport frame through the RUDP session.
        /// </summary>
        public void Send(string channelId, byte[] payload)
        {
            lock (_sessionGate)
                SendPackets(_session.SendMany(channelId, payload, ChannelSendOptions(channelId), _maxFragmentBytes));
            _stats.FramesSent++;
        }

        /// <summary>
        /// Pumps acknowledgements and resends until every queued reliable packet is acknowledged.
        /// </summary>
        public void FlushReliable(TimeSpan? timeout = null)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
            var preserved = new List<CultNetTransportFrame>();
            while (_deliveredFrames.Count > 0)
                preserved.Add(_deliveredFrames.Dequeue());
            // A flush belongs to the generation it started in: whatever ends that generation, and
            // whatever begins after it, the writes being waited on are gone.
            long generation;
            lock (_sessionGate)
                generation = _session.Generation;
            try
            {
                while (true)
                {
                    lock (_sessionGate)
                    {
                        // An ended session forgot its unacknowledged writes; reporting them flushed
                        // would be a lie.
                        if (_session.Ended || _session.Generation != generation)
                            throw new InvalidOperationException(
                                "RUDP session ended before its reliable writes were acknowledged: " +
                                (DisconnectReason == null ? "no reason given" : Encoding.UTF8.GetString(DisconnectReason)));
                        if (_session.OutstandingReliablePacketCount == 0)
                            return;
                    }
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        int outstanding;
                        lock (_sessionGate)
                            outstanding = _session.OutstandingReliablePacketCount;
                        throw new TimeoutException($"RUDP reliable flush timed out with {outstanding} packets outstanding.");
                    }
                    if (TryReceiveOnce(out var delivered) && delivered != null)
                        preserved.Add(delivered);
                    PollResends();
                    Thread.Sleep(5);
                }
            }
            finally
            {
                while (_deliveredFrames.Count > 0)
                    preserved.Add(_deliveredFrames.Dequeue());
                foreach (var frame in preserved)
                    _deliveredFrames.Enqueue(frame);
            }
        }

        /// <summary>
        /// Sends a reliable ordered schema-channel payload.
        /// </summary>
        public void SendSchema(byte[] payload)
        {
            Send("schema", payload);
        }

        /// <summary>
        /// Sends a reliable ordered schema-channel UTF-8 payload.
        /// </summary>
        public void SendSchema(string payload)
        {
            SendSchema(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        }

        /// <summary>
        /// Sends a CultNet schema-v0 message on the reliable ordered schema channel.
        /// </summary>
        public void SendSchemaMessage<TMessage>(TMessage message)
            where TMessage : ICultNetSchemaMessage
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            Send("schema", CultNetSchemaMessageSerialization.Serialize(message));
        }

        /// <summary>
        /// Sends an unreliable sequenced latest-state payload.
        /// </summary>
        public void SendLatest(byte[] payload)
        {
            Send("latest", payload);
        }

        /// <summary>
        /// Sends an unreliable sequenced latest-state UTF-8 payload.
        /// </summary>
        public void SendLatest(string payload)
        {
            SendLatest(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        }

        /// <summary>
        /// Sends an unreliable unordered realtime payload.
        /// </summary>
        public void SendRealtime(byte[] payload)
        {
            Send("realtime", payload);
        }

        /// <summary>
        /// Sends an unreliable unordered realtime UTF-8 payload.
        /// </summary>
        public void SendRealtime(string payload)
        {
            SendRealtime(Encoding.UTF8.GetBytes(payload ?? string.Empty));
        }

        /// <summary>
        /// Attempts to receive the next CultNet schema-v0 message.
        /// </summary>
        public ICultNetSchemaMessage? ReceiveSchemaMessageOnce()
        {
            while (true)
            {
                var frame = ReceiveOnce();
                if (frame == null)
                {
                    return null;
                }

                if (!string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal))
                {
                    continue;
                }

                return CultNetSchemaMessageSerialization.Deserialize(frame.Payload);
            }
        }

        /// <summary>
        /// Attempts to receive the next CultNet schema-v0 message of the requested type.
        /// </summary>
        public TMessage? ReceiveSchemaMessageOnce<TMessage>()
            where TMessage : class, ICultNetSchemaMessage
        {
            return ReceiveSchemaMessageOnce() as TMessage;
        }

        /// <summary>
        /// Polls until a delivered transport frame matches the predicate or the timeout expires.
        /// </summary>
        public CultNetTransportFrame? ReceiveUntil(
            TimeSpan timeout,
            Func<CultNetTransportFrame, bool>? predicate = null,
            TimeSpan? pollInterval = null)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var interval = pollInterval ?? TimeSpan.FromMilliseconds(5);
            predicate ??= _ => true;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var frame = ReceiveOnce();
                if (frame != null && predicate(frame))
                {
                    return frame;
                }

                PollResends();
                Thread.Sleep(interval);
            }

            return null;
        }

        /// <summary>
        /// Polls until a schema-channel payload arrives or the timeout expires.
        /// </summary>
        public byte[]? ReceiveSchema(TimeSpan timeout, TimeSpan? pollInterval = null)
        {
            return ReceiveUntil(
                timeout,
                frame => string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal),
                pollInterval)?.Payload;
        }

        /// <summary>
        /// Polls until a CultNet schema-v0 message arrives or the timeout expires.
        /// </summary>
        public ICultNetSchemaMessage? ReceiveSchemaMessage(TimeSpan timeout, TimeSpan? pollInterval = null)
        {
            var payload = ReceiveSchema(timeout, pollInterval);
            return payload == null ? null : CultNetSchemaMessageSerialization.Deserialize(payload);
        }

        /// <summary>
        /// Polls until a CultNet schema-v0 message of the requested type arrives or the timeout expires.
        /// </summary>
        public TMessage? ReceiveSchemaMessage<TMessage>(TimeSpan timeout, TimeSpan? pollInterval = null)
            where TMessage : class, ICultNetSchemaMessage
        {
            return ReceiveSchemaMessage(timeout, pollInterval) as TMessage;
        }

        /// <summary>
        /// Sends a transport-level disconnect packet.
        /// </summary>
        public void Disconnect(byte[]? reason = null)
        {
            lock (_sessionGate)
                SendPacket(_session.CreateDisconnect(reason ?? Array.Empty<byte>()));
        }

        /// <summary>
        /// Sends a transport-level ping packet.
        /// </summary>
        public void Ping(byte[]? payload = null)
        {
            lock (_sessionGate)
                SendPacket(_session.CreatePing(payload ?? Array.Empty<byte>()));
        }

        /// <summary>
        /// Checks whether the session has exceeded the receive timeout.
        /// </summary>
        public bool CheckTimeout(long timeoutMs)
        {
            lock (_sessionGate)
                return _session.CheckTimeout(NowMs(), timeoutMs);
        }

        /// <summary>
        /// Polls the UDP socket once and returns the next delivered transport frame if one is available.
        /// </summary>
        public CultNetTransportFrame? ReceiveOnce()
        {
            TryReceiveOnce(out var delivered);
            return delivered;
        }

        /// <summary>
        /// Consumes one available transport work item and reports progress independently
        /// of whether that packet completed an application frame.
        /// </summary>
        public bool TryReceiveOnce(out CultNetTransportFrame? delivered)
        {
            if (_deliveredFrames.Count > 0)
            {
                delivered = _deliveredFrames.Dequeue();
                return true;
            }

            delivered = null;

            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                if (!_socket.Poll(0, SelectMode.SelectRead))
                {
                    return false;
                }

                received = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.WouldBlock ||
                error.SocketErrorCode == SocketError.TimedOut)
            {
                return false;
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.ConnectionReset ||
                error.SocketErrorCode == SocketError.ConnectionAborted ||
                error.SocketErrorCode == SocketError.Shutdown)
            {
                return false;
            }

            _stats.BytesReceived += received;

            // What a datagram carries is the sender's business, not a fault of this process: a
            // malformed frame, another session's connection id, a sender that is not the peer and
            // a packet the session refuses are dropped and counted. Only the socket ends a loop.
            // The id is checked before the peer endpoint is claimed, so a stray cannot become it.
            var wire = new byte[received];
            Array.Copy(buffer, wire, received);
            CultNetRudpPacket packet;
            try
            {
                packet = CultNetRudpPacketCodec.Decode(wire);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                _stats.PacketsDropped++;
                return true;
            }
            if (packet.ConnectionId != _session.ConnectionId)
            {
                _stats.PacketsDropped++;
                return true;
            }
            // A Connect from another endpoint is a new client, whatever sequence it carries: the endpoint
            // moves and the Connect starts a new generation. Only a Connect from the peer's own
            // endpoint can be its repeat.
            var connectFromNewEndpoint = false;
            if (_remoteEndPoint == null)
            {
                // Only a Connect claims the endpoint of a server-mode transport.
                if (_mode == CultNetRudpSocketMode.Server && packet.PacketType != CultNetRudpPacketType.Connect)
                {
                    _stats.PacketsDropped++;
                    return true;
                }

                _remoteEndPoint = remote;
            }
            else if (!_remoteEndPoint.Equals(remote))
            {
                if (_mode == CultNetRudpSocketMode.Server && packet.PacketType == CultNetRudpPacketType.Connect)
                {
                    _remoteEndPoint = remote;
                    connectFromNewEndpoint = true;
                }
                else
                {
                    _stats.PacketsDropped++;
                    return true;
                }
            }

            TracePacket("rx", packet, received, remote);
            if (_mode == CultNetRudpSocketMode.Server && packet.PacketType == CultNetRudpPacketType.Connect)
            {
                lock (_sessionGate)
                {
                    CultNetRudpPacket accept;
                    try
                    {
                        // The session answers a repeat of the connect it accepted with the accept
                        // already owed, and anything else with a new generation.
                        if (connectFromNewEndpoint)
                            _session.ResetPeerState();
                        accept = _session.AcceptConnect(packet, NowMs());
                        DisconnectReason = null;
                    }
                    catch (InvalidOperationException)
                    {
                        _stats.PacketsDropped++;
                        return true;
                    }
                    SendPacket(accept);
                }
                return true;
            }

            CultNetRudpReceiveResult result;
            CultNetRudpPacket? acknowledgement = null;
            lock (_sessionGate)
            {
                try
                {
                    result = _session.Receive(packet, NowMs());
                }
                catch (InvalidOperationException)
                {
                    // Receive has already recorded the packet's reliable sequence, so the session
                    // cannot be kept: a retransmit would be acknowledged and the frame silently lost.
                    // End it and tell the peer; a server-mode transport also forgets its endpoint, so
                    // only a Connect can claim it again.
                    _stats.PacketsDropped++;
                    var goodbye = _session.EndRefused();
                    DisconnectReason = CultNetRudpSession.RefusedPacketReason;
                    try
                    {
                        SendPacket(goodbye);
                    }
                    catch (SocketException)
                    {
                        // Best effort: the session ends whether or not the peer hears it.
                    }

                    if (_mode == CultNetRudpSocketMode.Server)
                        _remoteEndPoint = null;
                    return true;
                }
                if (result.Reply != null)
                    SendPacket(result.Reply);
                SendPackets(result.ReadyToSend);
                if (packet.PacketType == CultNetRudpPacketType.Accept ||
                    packet.PacketType == CultNetRudpPacketType.Data)
                    acknowledgement = _session.CreateAckForReceived(packet.Sequence);
            }
            if (result.Pong)
            {
                _pongPayloads.Enqueue(result.PongPayload);
            }
            if (result.Disconnected)
            {
                DisconnectReason = result.DisconnectReason;
                return true;
            }

            foreach (var frame in result.Delivered)
            {
                _deliveredFrames.Enqueue(new CultNetTransportFrame
                {
                    ChannelId = frame.ChannelId,
                    Payload = frame.Payload
                });
                _stats.FramesReceived++;
            }

            delivered = _deliveredFrames.Count > 0 ? _deliveredFrames.Dequeue() : null;
            if (acknowledgement != null)
                SendPacket(acknowledgement);

            return true;
        }

        /// <summary>
        /// Sends any reliable packets whose resend timers are due.
        /// </summary>
        public void PollResends()
        {
            if (_socket.Poll(0, SelectMode.SelectRead))
                return;
            lock (_sessionGate)
                SendPackets(_session.DueResends(NowMs()));
        }

        private void SendPackets(IReadOnlyList<CultNetRudpPacket> packets)
        {
            for (var index = 0; index < packets.Count; index++)
            {
                SendPacket(packets[index]);
                if ((index + 1) % WireBurstPackets == 0 && index + 1 < packets.Count)
                    Thread.Yield();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _socket.Dispose();
        }

        private void SendPacket(CultNetRudpPacket packet)
        {
            if (_remoteEndPoint == null)
            {
                throw new InvalidOperationException("RUDP socket transport does not have a remote endpoint.");
            }

            var wire = CultNetRudpPacketCodec.Encode(packet);
            var sent = _socket.SendTo(wire, _remoteEndPoint);
            _stats.BytesSent += sent;
            TracePacket("tx", packet, sent, _remoteEndPoint);
        }

        private static void TracePacket(string direction, CultNetRudpPacket packet, int bytes, EndPoint? endpoint)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CULTNET_TRACE_RUDP"), "1", StringComparison.Ordinal))
            {
                return;
            }

            Console.Error.WriteLine(
                $"CultNet RUDP {direction} type={packet.PacketType} seq={packet.Sequence} ack={packet.Ack} bytes={bytes} endpoint={endpoint}");
        }

        private static CultNetRudpSendOptions ChannelSendOptions(string channelId)
        {
            return string.Equals(channelId, "schema", StringComparison.Ordinal)
                ? new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = NowMs() }
                : string.Equals(channelId, "latest", StringComparison.Ordinal)
                    ? new CultNetRudpSendOptions { Sequenced = true, NowMs = NowMs() }
                    : new CultNetRudpSendOptions { NowMs = NowMs() };
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Options for a multi-peer CultNet RUDP UDP listener.
    /// </summary>
    public sealed class CultNetRudpSocketTransportServerOptions
    {
        /// <summary>
        /// Gets or sets the runtime id advertised by this listener.
        /// </summary>
        public string RuntimeId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the bound UDP socket.
        /// </summary>
        public Socket Socket { get; set; } = null!;
        /// <summary>
        /// Gets or sets the RUDP connection/session binding id accepted by this listener.
        /// </summary>
        public uint ConnectionId { get; set; }
        /// <summary>
        /// Gets or sets the first local packet sequence for each accepted peer session. Unset, each
        /// session draws its own at random.
        /// </summary>
        public uint? InitialSequence { get; set; }
        /// <summary>
        /// Gets or sets the resend delay in milliseconds.
        /// </summary>
        public long ResendDelayMs { get; set; } = 250;
        /// <summary>
        /// Gets or sets the advertised transport id.
        /// </summary>
        public string TransportId { get; set; } = "rudp";
        /// <summary>
        /// Gets or sets the maximum payload size for RUDP channels.
        /// </summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum fragment size for RUDP channels.
        /// </summary>
        public int? MaxFragmentBytes { get; set; }
        /// <summary>
        /// Gets or sets the maximum pending reliable packet count for RUDP channels.
        /// </summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>
        /// Gets or sets the advertised reconnect policy for this RUDP listener.
        /// </summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
        /// <summary>
        /// Gets or sets the payload sent with accept packets.
        /// </summary>
        public byte[]? AcceptPayload { get; set; }
    }

    /// <summary>
    /// Peer context owned by a multi-peer CultNet RUDP listener.
    /// </summary>
    public sealed class CultNetRudpSocketServerPeer
    {
        internal CultNetRudpSocketServerPeer(EndPoint remoteEndPoint, byte[] connectPayload, CultNetRudpSession session)
        {
            RemoteEndPoint = remoteEndPoint;
            ConnectPayload = connectPayload;
            Session = session;
        }

        internal byte[] ConnectPayload { get; }
        internal CultNetRudpSession Session { get; }
        internal object SessionGate { get; } = new object();

        /// <summary>
        /// Gets the UDP endpoint for this peer.
        /// </summary>
        public EndPoint RemoteEndPoint { get; }
        /// <summary>
        /// Gets whether the peer RUDP handshake has completed.
        /// </summary>
        public bool Connected { get { lock (SessionGate) return Session.Connected; } }
        /// <summary>
        /// Gets the number of reliable packets still awaiting acknowledgement.
        /// </summary>
        public int PendingReliablePacketCount { get { lock (SessionGate) return Session.OutstandingReliablePacketCount; } }
        /// <summary>
        /// Gets the last transport-level remote disconnect reason, if one was received.
        /// </summary>
        public byte[]? DisconnectReason { get; internal set; }
    }

    /// <summary>
    /// A frame delivered by a multi-peer CultNet RUDP listener.
    /// </summary>
    public sealed class CultNetRudpSocketServerFrame
    {
        /// <summary>
        /// Gets the peer that delivered the frame.
        /// </summary>
        public CultNetRudpSocketServerPeer Peer { get; set; } = null!;
        /// <summary>
        /// Gets the delivered transport frame.
        /// </summary>
        public CultNetTransportFrame Frame { get; set; } = new CultNetTransportFrame();
    }

    /// <summary>
    /// Multi-peer UDP socket listener for CultNet RUDP sessions.
    /// </summary>
    public sealed class CultNetRudpSocketTransportServer : IDisposable
    {
        private const int MinimumReceiveBufferBytes = 4 * 1024 * 1024;
        private static readonly byte[] ReplacedPeerReason = Encoding.UTF8.GetBytes("replaced by a new Connect generation");
        // Keep server response pacing aligned with the 32-packet ACK mask. Large
        // content responses otherwise pay one Windows scheduler quantum per 1 KiB.
        private const int WireBurstPackets = 32;
        private readonly Socket _socket;
        private readonly uint _connectionId;
        private readonly uint? _initialSequence;
        private readonly long _resendDelayMs;
        private readonly int? _maxFragmentBytes;
        private readonly int? _maxPendingReliablePackets;
        private readonly byte[] _acceptPayload;
        private readonly CultNetTransportStats _stats = new CultNetTransportStats();
        private readonly Dictionary<string, CultNetRudpSocketServerPeer> _peers = new Dictionary<string, CultNetRudpSocketServerPeer>(StringComparer.Ordinal);
        private readonly Queue<CultNetRudpSocketServerFrame> _deliveredFrames = new Queue<CultNetRudpSocketServerFrame>();
        private bool _disposed;

        /// <summary>
        /// Initializes a multi-peer UDP listener for CultNet RUDP sessions.
        /// </summary>
        public CultNetRudpSocketTransportServer(CultNetRudpSocketTransportServerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.RuntimeId)) throw new ArgumentException("Runtime id is required.", nameof(options));
            _socket = options.Socket ?? throw new ArgumentNullException(nameof(options.Socket));
            if (_socket.ReceiveBufferSize < MinimumReceiveBufferBytes)
                _socket.ReceiveBufferSize = MinimumReceiveBufferBytes;
            CultNetRudpSession.ValidateLimits(options.InitialSequence, options.MaxPendingReliablePackets);
            _connectionId = options.ConnectionId;
            _initialSequence = options.InitialSequence;
            _resendDelayMs = options.ResendDelayMs;
            _maxFragmentBytes = options.MaxFragmentBytes;
            _maxPendingReliablePackets = options.MaxPendingReliablePackets;
            _acceptPayload = options.AcceptPayload ?? Array.Empty<byte>();

            var local = _socket.LocalEndPoint as IPEndPoint;
            Profile = CultNetTransportProfiles.CreateRudp(
                options.RuntimeId,
                new RudpTransportProfileOptions
                {
                    TransportId = options.TransportId,
                    Host = local?.Address.ToString(),
                    Port = local?.Port,
                    MaxPayloadBytes = options.MaxPayloadBytes,
                    MaxFragmentBytes = options.MaxFragmentBytes,
                    MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                    ReconnectPolicy = options.ReconnectPolicy
                });
        }

        /// <summary>
        /// Gets the profile this listener implements.
        /// </summary>
        public CultNetTransportProfile Profile { get; }

        /// <summary>
        /// Gets currently tracked peers indexed by remote endpoint.
        /// </summary>
        public IReadOnlyCollection<CultNetRudpSocketServerPeer> Peers => _peers.Values.ToArray();

        /// <summary>
        /// Raised when a tracked peer closes its RUDP session.
        /// </summary>
        public event Action<CultNetRudpSocketServerPeer>? PeerDisconnected;

        /// <summary>
        /// Gets a snapshot of the current transfer counters.
        /// </summary>
        public CultNetTransportStats Stats => _stats.Snapshot();

        /// <summary>
        /// Sends a logical transport frame to a connected peer.
        /// </summary>
        public void Send(CultNetRudpSocketServerPeer peer, string channelId, byte[] payload)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            lock (peer.SessionGate)
                SendPackets(peer, peer.Session.SendMany(channelId, payload, ChannelSendOptions(channelId), _maxFragmentBytes));
            _stats.FramesSent++;
        }

        /// <summary>
        /// Sends a reliable ordered schema-channel payload to a peer.
        /// </summary>
        public void SendSchema(CultNetRudpSocketServerPeer peer, byte[] payload)
        {
            Send(peer, "schema", payload);
        }

        /// <summary>
        /// Sends a reliable ordered schema-channel UTF-8 payload to a peer.
        /// </summary>
        public void SendSchema(CultNetRudpSocketServerPeer peer, string payload)
        {
            SendSchema(peer, Encoding.UTF8.GetBytes(payload ?? string.Empty));
        }

        /// <summary>
        /// Sends a CultNet schema-v0 message to a peer on the reliable ordered schema channel.
        /// </summary>
        public void SendSchemaMessage<TMessage>(CultNetRudpSocketServerPeer peer, TMessage message)
            where TMessage : ICultNetSchemaMessage
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            SendSchema(peer, CultNetSchemaMessageSerialization.Serialize(message));
        }

        /// <summary>
        /// Polls the UDP socket once and returns the next delivered peer frame if one is available.
        /// </summary>
        public CultNetRudpSocketServerFrame? ReceiveOnce()
        {
            TryReceiveOnce(out var delivered);
            return delivered;
        }

        /// <summary>
        /// Consumes one available transport work item and reports whether transport progress was made,
        /// independently of whether that work item completed an application frame.
        /// </summary>
        public bool TryReceiveOnce(out CultNetRudpSocketServerFrame? delivered)
        {
            if (_deliveredFrames.Count > 0)
            {
                delivered = _deliveredFrames.Dequeue();
                return true;
            }

            delivered = null;

            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                if (!_socket.Poll(0, SelectMode.SelectRead))
                {
                    return false;
                }

                received = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.WouldBlock ||
                error.SocketErrorCode == SocketError.TimedOut)
            {
                return false;
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.ConnectionReset ||
                error.SocketErrorCode == SocketError.ConnectionAborted ||
                error.SocketErrorCode == SocketError.Shutdown)
            {
                return false;
            }

            _stats.BytesReceived += received;
            var wire = new byte[received];
            Array.Copy(buffer, wire, received);

            CultNetRudpPacket packet;
            try
            {
                packet = CultNetRudpPacketCodec.Decode(wire);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                _stats.PacketsDropped++;
                return true;
            }

            if (packet.ConnectionId != _connectionId)
            {
                _stats.PacketsDropped++;
                return true;
            }

            var peerKey = RemoteKey(remote);
            if (packet.PacketType == CultNetRudpPacketType.Connect)
            {
                var connectPayload = packet.Payload ?? Array.Empty<byte>();
                _peers.TryGetValue(peerKey, out var admitted);
                // The admitted peer's session decides whether this Connect repeats the one it accepted:
                // answer a repeat with the Accept already owed and queue nothing. Any other Connect is
                // a new generation.
                if (admitted != null)
                {
                    lock (admitted.SessionGate)
                    {
                        if (admitted.Session.ConnectRepeats(packet))
                        {
                            CultNetRudpPacket reply;
                            try
                            {
                                reply = admitted.Session.AcceptConnect(packet, NowMs(), _acceptPayload);
                            }
                            catch (InvalidOperationException)
                            {
                                _stats.PacketsDropped++;
                                return true;
                            }
                            SendPacket(admitted.RemoteEndPoint, reply);
                            return true;
                        }
                    }
                }

                var peer = new CultNetRudpSocketServerPeer(
                    CloneEndPoint(remote),
                    connectPayload,
                    new CultNetRudpSession(new CultNetRudpSessionOptions
                    {
                        ConnectionId = _connectionId,
                        InitialSequence = _initialSequence,
                        ResendDelayMs = _resendDelayMs,
                        MaxPendingReliablePackets = _maxPendingReliablePackets
                    }));
                _peers[peerKey] = peer;
                lock (peer.SessionGate)
                    SendPacket(peer.RemoteEndPoint, peer.Session.AcceptConnect(packet, NowMs(), _acceptPayload));
                if (admitted != null)
                {
                    admitted.DisconnectReason = ReplacedPeerReason;
                    PeerDisconnected?.Invoke(admitted);
                }
                return true;
            }

            if (!_peers.TryGetValue(peerKey, out var existingPeer))
            {
                _stats.PacketsDropped++;
                return true;
            }

            CultNetRudpReceiveResult? outcome;
            CultNetRudpPacket? acknowledgement = null;
            lock (existingPeer.SessionGate)
            {
                try
                {
                    outcome = existingPeer.Session.Receive(packet, NowMs());
                }
                catch (InvalidOperationException)
                {
                    // Receive has already recorded the packet's reliable sequence, so the session
                    // cannot be kept: a retransmit would be acknowledged and the frame silently lost.
                    _stats.PacketsDropped++;
                    outcome = null;
                }

                if (outcome == null)
                {
                    try
                    {
                        SendPacket(existingPeer.RemoteEndPoint, existingPeer.Session.EndRefused());
                    }
                    catch (SocketException)
                    {
                        // Best effort: the session ends whether or not the peer hears it.
                    }
                }
                else
                {
                    if (outcome.Reply != null)
                        SendPacket(existingPeer.RemoteEndPoint, outcome.Reply);
                    SendPackets(existingPeer, outcome.ReadyToSend);
                    if (packet.PacketType == CultNetRudpPacketType.Data)
                        acknowledgement = existingPeer.Session.CreateAckForReceived(packet.Sequence);
                }
            }
            if (outcome == null)
            {
                existingPeer.DisconnectReason = CultNetRudpSession.RefusedPacketReason;
                _peers.Remove(peerKey);
                PeerDisconnected?.Invoke(existingPeer);
                return true;
            }

            var result = outcome;
            if (result.Disconnected)
            {
                existingPeer.DisconnectReason = result.DisconnectReason;
                _peers.Remove(peerKey);
                PeerDisconnected?.Invoke(existingPeer);
                return true;
            }

            foreach (var frame in result.Delivered)
            {
                _deliveredFrames.Enqueue(new CultNetRudpSocketServerFrame
                {
                    Peer = existingPeer,
                    Frame = new CultNetTransportFrame
                    {
                        ChannelId = frame.ChannelId,
                        Payload = frame.Payload
                    }
                });
                _stats.FramesReceived++;
            }

            delivered = _deliveredFrames.Count > 0 ? _deliveredFrames.Dequeue() : null;
            if (acknowledgement != null)
                SendPacket(existingPeer.RemoteEndPoint, acknowledgement);

            return true;
        }

        /// <summary>
        /// Sends any reliable packets whose resend timers are due for all peers.
        /// </summary>
        public void PollResends()
        {
            if (_socket.Poll(0, SelectMode.SelectRead))
                return;
            foreach (var peer in _peers.Values.ToArray())
            {
                lock (peer.SessionGate)
                    SendPackets(peer, peer.Session.DueResends(NowMs()));
            }
        }

        private void SendPackets(CultNetRudpSocketServerPeer peer, IReadOnlyList<CultNetRudpPacket> packets)
        {
            for (var index = 0; index < packets.Count; index++)
            {
                SendPacket(peer.RemoteEndPoint, packets[index]);
                if ((index + 1) % WireBurstPackets == 0 && index + 1 < packets.Count)
                    Thread.Yield();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _socket.Dispose();
        }

        private void SendPacket(EndPoint remoteEndPoint, CultNetRudpPacket packet)
        {
            var wire = CultNetRudpPacketCodec.Encode(packet);
            var sent = _socket.SendTo(wire, remoteEndPoint);
            _stats.BytesSent += sent;
        }

        private static string RemoteKey(EndPoint endpoint)
        {
            return endpoint.ToString() ?? string.Empty;
        }

        private static EndPoint CloneEndPoint(EndPoint endpoint)
        {
            if (endpoint is IPEndPoint ip)
            {
                return new IPEndPoint(ip.Address, ip.Port);
            }

            return endpoint;
        }

        private static CultNetRudpSendOptions ChannelSendOptions(string channelId)
        {
            return string.Equals(channelId, "schema", StringComparison.Ordinal)
                ? new CultNetRudpSendOptions { Reliable = true, Ordered = true, NowMs = NowMs() }
                : string.Equals(channelId, "latest", StringComparison.Ordinal)
                    ? new CultNetRudpSendOptions { Sequenced = true, NowMs = NowMs() }
                    : new CultNetRudpSendOptions { NowMs = NowMs() };
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>
    /// Caller-owned reconnect loop for the socket-backed CultNet RUDP transport.
    /// </summary>
    public sealed class CultNetRudpReconnectLoop : IDisposable
    {
        private readonly Func<CultNetRudpSocketTransportConnection> _createTransport;
        private readonly byte[] _connectPayload;
        private CultNetRudpSocketTransportConnection? _transport;
        private bool _stopped = true;
        private bool _disposed;

        /// <summary>
        /// Initializes a new reconnect loop around caller-owned RUDP transport construction.
        /// </summary>
        public CultNetRudpReconnectLoop(
            Func<CultNetRudpSocketTransportConnection> createTransport,
            CultNetReconnectPolicy? reconnectPolicy = null,
            byte[]? connectPayload = null)
        {
            _createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
            _connectPayload = connectPayload?.ToArray() ?? Array.Empty<byte>();
            ReconnectController = new CultNetReconnectController(reconnectPolicy);
        }

        /// <summary>
        /// Gets the shared reconnect controller that owns attempt, delay, and exhaustion state.
        /// </summary>
        public CultNetReconnectController ReconnectController { get; }

        /// <summary>
        /// Gets the current transport, if the loop has one open.
        /// </summary>
        public CultNetRudpSocketTransportConnection? Transport => _transport;

        /// <summary>
        /// Opens the first transport and sends its connect packet.
        /// </summary>
        public CultNetRudpSocketTransportConnection Start()
        {
            ThrowIfDisposed();
            _stopped = false;
            ReconnectController.Reset();
            return OpenTransport();
        }

        /// <summary>
        /// Stops reconnecting, disposes the current transport, and clears retry state.
        /// </summary>
        public void Stop()
        {
            _stopped = true;
            DisposeTransport();
            ReconnectController.Reset();
        }

        /// <summary>
        /// Clears retry state after the caller observes an established connection.
        /// </summary>
        public void MarkConnected()
        {
            ReconnectController.Reset();
        }

        /// <summary>
        /// Reports that the current transport closed and records the next retry decision.
        /// </summary>
        public CultNetReconnectDecision? HandleClosed(long nowMs, int jitterMs = 0)
        {
            ThrowIfDisposed();
            DisposeTransport();
            return _stopped ? null : ReconnectController.RecordFailure(nowMs, jitterMs);
        }

        /// <summary>
        /// Opens a new transport when the shared controller says the next attempt is due.
        /// </summary>
        public bool ReconnectIfDue(long nowMs)
        {
            ThrowIfDisposed();
            if (_stopped || !ReconnectController.CanAttempt(nowMs))
            {
                return false;
            }

            OpenTransport();
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();
        }

        private CultNetRudpSocketTransportConnection OpenTransport()
        {
            var next = _createTransport();
            try
            {
                next.Connect(_connectPayload);
            }
            catch
            {
                next.Dispose();
                throw;
            }

            DisposeTransport();
            _transport = next;
            return next;
        }

        private void DisposeTransport()
        {
            _transport?.Dispose();
            _transport = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CultNetRudpReconnectLoop));
            }
        }
    }

    /// <summary>
    /// Channel-aware send adapter for the C# LiteNetLib production lane.
    /// </summary>
    public sealed class LiteNetLibTransportConnection
    {
        private readonly NetPeer? _peer;
        private readonly CultNetTransportStats _stats = new CultNetTransportStats();

        /// <summary>
        /// Initializes a new channel-aware adapter around a LiteNetLib peer.
        /// </summary>
        public LiteNetLibTransportConnection(NetPeer peer, CultNetTransportProfile? profile = null)
        {
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            Profile = profile ?? CultNetTransportProfiles.CreateLiteNetLib("csharp-litenetlib-peer");
        }

        /// <summary>
        /// Initializes a receive-only channel-aware adapter for LiteNetLib payload classification.
        /// </summary>
        public LiteNetLibTransportConnection(CultNetTransportProfile? profile = null)
        {
            Profile = profile ?? CultNetTransportProfiles.CreateLiteNetLib("csharp-litenetlib-peer");
        }

        /// <summary>
        /// Gets the transport profile this adapter implements.
        /// </summary>
        public CultNetTransportProfile Profile { get; }

        /// <summary>
        /// Gets a snapshot of outbound transfer counters.
        /// </summary>
        public CultNetTransportStats Stats => _stats.Snapshot();

        /// <summary>
        /// Classifies an inbound LiteNetLib payload into the adapter's schema or legacy channel shape.
        /// </summary>
        public CultNetTransportFrame Receive(byte[] payload)
        {
            var frame = Decode(payload);
            _stats.BytesReceived += frame.Payload.Length;
            _stats.FramesReceived++;
            return frame;
        }

        /// <summary>
        /// Sends a payload over a supported LiteNetLib transport channel.
        /// </summary>
        public void Send(string channelId, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (_peer == null) throw new InvalidOperationException("A LiteNetLib peer is required to send through this transport.");
            if (!string.Equals(channelId, "schema", StringComparison.Ordinal)
                && !string.Equals(channelId, "legacy", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"litenetlib transport only supports schema and legacy channels, got \"{channelId}\".");
            }

            _peer.Send(payload, DeliveryMethod.ReliableOrdered);
            _stats.BytesSent += payload.Length;
            _stats.FramesSent++;
        }

        /// <summary>
        /// Serializes and sends a legacy GameCult.Networking union message.
        /// </summary>
        public void SendLegacy<T>(T message)
            where T : Message
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            Send("legacy", MessageSerialization.Serialize(message));
        }

        /// <summary>
        /// Serializes and sends a CultNet schema-v0 message.
        /// </summary>
        public void SendSchema<T>(T message)
            where T : ICultNetSchemaMessage
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            Send("schema", CultNetSchemaMessageSerialization.Serialize(message));
        }

        /// <summary>
        /// Decodes an inbound LiteNetLib payload into the adapter's schema or legacy channel shape.
        /// </summary>
        public static CultNetTransportFrame Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (TryDeserializeCultNet(payload, out _))
            {
                return new CultNetTransportFrame
                {
                    ChannelId = "schema",
                    Payload = payload.ToArray()
                };
            }

            return new CultNetTransportFrame
            {
                ChannelId = "legacy",
                Payload = payload.ToArray()
            };
        }

        /// <summary>
        /// Decodes a schema-channel frame as a CultNet schema-v0 message.
        /// </summary>
        public static ICultNetSchemaMessage DecodeSchema(CultNetTransportFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (!string.Equals(frame.ChannelId, "schema", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected schema channel, got \"{frame.ChannelId}\".");
            }

            return CultNetSchemaMessageSerialization.Deserialize(frame.Payload);
        }

        /// <summary>
        /// Decodes a legacy-channel frame as a GameCult.Networking union message.
        /// </summary>
        public static Message DecodeLegacy(CultNetTransportFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (!string.Equals(frame.ChannelId, "legacy", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected legacy channel, got \"{frame.ChannelId}\".");
            }

            return MessageSerialization.Deserialize<Message>(frame.Payload);
        }

        private static bool TryDeserializeCultNet(byte[] payload, out ICultNetSchemaMessage message)
        {
            try
            {
                message = CultNetSchemaMessageSerialization.Deserialize(payload);
                return true;
            }
            catch (MessagePack.MessagePackSerializationException)
            {
                message = null!;
                return false;
            }
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
