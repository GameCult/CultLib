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
        /// Gets or sets the first local packet sequence.
        /// </summary>
        public uint InitialSequence { get; set; } = 1;
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
        /// Gets or sets the first local packet sequence.
        /// </summary>
        public uint InitialSequence { get; set; } = 1;
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
        private sealed class PendingReliablePacket
        {
            public CultNetRudpPacket Packet { get; set; } = new CultNetRudpPacket();
            public long LastSentAtMs { get; set; }
        }

        private sealed class PendingOrderedFrame
        {
            public CultNetRudpDeliveredFrame Frame { get; set; } = new CultNetRudpDeliveredFrame();
            public uint NextSequence { get; set; }
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
        private ushort _nextFragmentId = 1;
        private readonly int? _maxPendingReliablePackets;
        private bool _connected;
        private long? _lastReceivedAtMs;
        private uint? _highestReceivedSequence;
        private readonly HashSet<uint> _receivedSequences = new HashSet<uint>();
        private readonly Dictionary<uint, PendingReliablePacket> _pendingReliable = new Dictionary<uint, PendingReliablePacket>();
        private readonly Dictionary<string, uint> _orderedNextSequenceByChannel = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedDictionary<uint, PendingOrderedFrame>> _orderedBuffers =
            new Dictionary<string, SortedDictionary<uint, PendingOrderedFrame>>(StringComparer.Ordinal);
        private readonly Dictionary<string, FragmentBuffer> _fragmentBuffers = new Dictionary<string, FragmentBuffer>(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a RUDP reliability session.
        /// </summary>
        public CultNetRudpSession(CultNetRudpSessionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.MaxPendingReliablePackets.HasValue && options.MaxPendingReliablePackets.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "RUDP MaxPendingReliablePackets must be greater than zero.");
            }

            ConnectionId = options.ConnectionId;
            _nextSequence = options.InitialSequence;
            ResendDelayMs = options.ResendDelayMs;
            _maxPendingReliablePackets = options.MaxPendingReliablePackets;
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
        /// Gets the logical time of the last received packet.
        /// </summary>
        public long? LastReceivedAtMs => _lastReceivedAtMs;
        /// <summary>
        /// Gets reliable packet sequences awaiting acknowledgement.
        /// </summary>
        public IReadOnlyList<uint> PendingReliableSequences => _pendingReliable.Keys.OrderBy(value => value).ToArray();

        /// <summary>
        /// Creates a reliable ordered connect packet.
        /// </summary>
        public CultNetRudpPacket CreateConnect(long nowMs = 0, byte[]? payload = null)
        {
            EnsureReliableCapacity(1);
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

            EnsureReliableCapacity(1);
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
                    if (fragmentPacket.Reliable)
                    {
                        TrackReliable(fragmentPacket, options.NowMs);
                    }
                    packets.Add(fragmentPacket);
                }
                return packets;
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
                TrackReliable(packet, options.NowMs);
            }
            return new[] { packet };
        }

        /// <summary>
        /// Applies a remote packet to the session.
        /// </summary>
        public CultNetRudpReceiveResult Receive(CultNetRudpPacket packet, long nowMs = 0)
        {
            RequireConnection(packet);
            ApplyAcknowledgements(packet);
            _lastReceivedAtMs = nowMs;
            var expectedSequenceIfUninitialized = _highestReceivedSequence.HasValue
                ? _highestReceivedSequence.Value + 1
                : packet.Sequence;

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
                return new CultNetRudpReceiveResult
                {
                    Pong = packet.PacketType == CultNetRudpPacketType.Pong,
                    PongPayload = packet.PacketType == CultNetRudpPacketType.Pong
                        ? packet.Payload ?? Array.Empty<byte>()
                        : Array.Empty<byte>()
                };
            }

            if (packet.PacketType == CultNetRudpPacketType.Disconnect)
            {
                RememberReceived(packet.Sequence);
                _connected = false;
                return new CultNetRudpReceiveResult
                {
                    Disconnected = true,
                    DisconnectReason = packet.Payload ?? Array.Empty<byte>()
                };
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

            var reassembled = Reassemble(packet);
            if (reassembled == null)
            {
                return new CultNetRudpReceiveResult();
            }

            return new CultNetRudpReceiveResult
            {
                Delivered = reassembled.Ordered
                    ? DeliverOrdered(reassembled.Frame, reassembled.NextSequence, expectedSequenceIfUninitialized)
                    : new[] { reassembled.Frame }
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
        /// Creates a packet carrying a keepalive ping payload.
        /// </summary>
        public CultNetRudpPacket CreatePing(byte[]? payload = null)
        {
            return CreatePacket(CultNetRudpPacketType.Ping, "control", payload ?? Array.Empty<byte>(), reliable: false, ordered: false, sequenced: false);
        }

        /// <summary>
        /// Creates a packet carrying a transport-level disconnect reason.
        /// </summary>
        public CultNetRudpPacket CreateDisconnect(byte[]? reason = null)
        {
            _connected = false;
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
            _connected = false;
            return true;
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
                FragmentId = fragmentId,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
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

        private void EnsureReliableCapacity(int packetCount)
        {
            if (packetCount == 0 || !_maxPendingReliablePackets.HasValue)
            {
                return;
            }

            if (_pendingReliable.Count + packetCount > _maxPendingReliablePackets.Value)
            {
                throw new InvalidOperationException("RUDP reliable send queue is full.");
            }
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

        private IReadOnlyList<CultNetRudpDeliveredFrame> DeliverOrdered(
            CultNetRudpDeliveredFrame frame,
            uint nextSequenceAfterFrame,
            uint expectedSequenceIfUninitialized)
        {
            if (!_orderedNextSequenceByChannel.TryGetValue(frame.ChannelId, out var next))
            {
                next = Math.Min(expectedSequenceIfUninitialized, frame.Sequence);
                _orderedNextSequenceByChannel[frame.ChannelId] = next;
            }

            if (frame.Sequence < next)
            {
                return Array.Empty<CultNetRudpDeliveredFrame>();
            }

            if (frame.Sequence > next)
            {
                if (!_orderedBuffers.TryGetValue(frame.ChannelId, out var buffer))
                {
                    buffer = new SortedDictionary<uint, PendingOrderedFrame>();
                    _orderedBuffers[frame.ChannelId] = buffer;
                }

                buffer[frame.Sequence] = new PendingOrderedFrame { Frame = frame, NextSequence = nextSequenceAfterFrame };
                return Array.Empty<CultNetRudpDeliveredFrame>();
            }

            _orderedNextSequenceByChannel[frame.ChannelId] = nextSequenceAfterFrame;
            return new[] { frame }.Concat(DrainOrdered(frame.ChannelId)).ToArray();
        }

        private IReadOnlyList<CultNetRudpDeliveredFrame> DrainOrdered(string channelId)
        {
            var delivered = new List<CultNetRudpDeliveredFrame>();
            if (!_orderedBuffers.TryGetValue(channelId, out var buffer))
            {
                return delivered;
            }

            while (_orderedNextSequenceByChannel.TryGetValue(channelId, out var next) && buffer.TryGetValue(next, out var pending))
            {
                buffer.Remove(next);
                delivered.Add(pending.Frame);
                _orderedNextSequenceByChannel[channelId] = pending.NextSequence;
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
        private readonly Socket _socket;
        private readonly CultNetRudpSession _session;
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
        public bool Connected => _session.Connected;

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
            foreach (var packet in _session.SendMany(channelId, payload, ChannelSendOptions(channelId), _maxFragmentBytes))
            {
                SendPacket(packet);
            }
            _stats.FramesSent++;
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
            SendPacket(_session.CreateDisconnect(reason ?? Array.Empty<byte>()));
        }

        /// <summary>
        /// Sends a transport-level ping packet.
        /// </summary>
        public void Ping(byte[]? payload = null)
        {
            SendPacket(_session.CreatePing(payload ?? Array.Empty<byte>()));
        }

        /// <summary>
        /// Checks whether the session has exceeded the receive timeout.
        /// </summary>
        public bool CheckTimeout(long timeoutMs)
        {
            return _session.CheckTimeout(NowMs(), timeoutMs);
        }

        /// <summary>
        /// Polls the UDP socket once and returns the next delivered transport frame if one is available.
        /// </summary>
        public CultNetTransportFrame? ReceiveOnce()
        {
            if (_deliveredFrames.Count > 0)
            {
                return _deliveredFrames.Dequeue();
            }

            var buffer = new byte[65535];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException error) when (
                error.SocketErrorCode == SocketError.WouldBlock ||
                error.SocketErrorCode == SocketError.TimedOut)
            {
                return null;
            }

            _stats.BytesReceived += received;
            if (_remoteEndPoint == null)
            {
                _remoteEndPoint = remote;
            }
            else if (!_remoteEndPoint.Equals(remote))
            {
                return null;
            }

            var wire = new byte[received];
            Array.Copy(buffer, wire, received);
            var packet = CultNetRudpPacketCodec.Decode(wire);
            if (_mode == CultNetRudpSocketMode.Server && packet.PacketType == CultNetRudpPacketType.Connect)
            {
                SendPacket(_session.AcceptConnect(packet, NowMs()));
                return null;
            }

            var result = _session.Receive(packet, NowMs());
            if (result.Reply != null)
            {
                SendPacket(result.Reply);
            }
            if (result.Pong)
            {
                _pongPayloads.Enqueue(result.PongPayload);
            }
            if (result.Disconnected)
            {
                DisconnectReason = result.DisconnectReason;
                return null;
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

            var delivered = _deliveredFrames.Count > 0 ? _deliveredFrames.Dequeue() : null;
            if (packet.PacketType == CultNetRudpPacketType.Accept || delivered != null)
            {
                SendPacket(_session.CreateAck());
            }

            return delivered;
        }

        /// <summary>
        /// Sends any reliable packets whose resend timers are due.
        /// </summary>
        public void PollResends()
        {
            foreach (var packet in _session.DueResends(NowMs()))
            {
                SendPacket(packet);
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
