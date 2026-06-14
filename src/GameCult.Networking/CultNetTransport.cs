using System;
using System.Buffers.Binary;
using System.IO;
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
