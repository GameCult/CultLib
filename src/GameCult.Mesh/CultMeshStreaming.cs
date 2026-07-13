using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static GameCult.Mesh.CultMeshGuards;

#pragma warning disable CS1591

namespace GameCult.Mesh
{
    public enum CultMeshStreamKind
    {
        Audio,
        Video,
        Tensor,
        Bytes
    }

    public enum CultMeshStreamBodyTransport
    {
        SharedMemory,
        SharedD3D12Texture,
        SharedD3D11Texture,
        DmaBuf,
        IOSurface,
        AHardwareBuffer,
        CultCachePage,
        InlineBytes
    }

    public enum CultMeshStreamAccess
    {
        Read,
        Write,
        ReadWrite
    }

    public enum CultMeshStreamCopyBudget
    {
        ZeroCopyTarget,
        OneCopyFallback,
        OpaqueRuntime
    }

    public sealed class CultMeshStreamClock
    {
        public CultMeshStreamClock(
            string clockDomainId,
            string? sourceId = null,
            int sampleRate = 0,
            long offsetToVerseTimeNs = 0,
            double confidence = 0,
            string? evidenceKind = null)
        {
            ClockDomainId = RequireNonEmpty(clockDomainId, nameof(clockDomainId));
            SourceId = sourceId;
            SampleRate = sampleRate;
            OffsetToVerseTimeNs = offsetToVerseTimeNs;
            Confidence = confidence;
            EvidenceKind = evidenceKind;
        }

        public string ClockDomainId { get; }
        public string? SourceId { get; }
        public int SampleRate { get; }
        public long OffsetToVerseTimeNs { get; }
        public double Confidence { get; }
        public string? EvidenceKind { get; }
    }

    public sealed class CultMeshAudioStreamFormat
    {
        public CultMeshAudioStreamFormat(int sampleRate, int channels, string sampleFormat, int framesPerPacket = 0)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            SampleRate = sampleRate;
            Channels = channels;
            SampleFormat = RequireNonEmpty(sampleFormat, nameof(sampleFormat));
            FramesPerPacket = framesPerPacket;
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public string SampleFormat { get; }
        public int FramesPerPacket { get; }
    }

    public sealed class CultMeshVideoStreamFormat
    {
        public CultMeshVideoStreamFormat(int width, int height, string pixelFormat, double framesPerSecond = 0, int planeCount = 1)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (planeCount <= 0) throw new ArgumentOutOfRangeException(nameof(planeCount));
            Width = width;
            Height = height;
            PixelFormat = RequireNonEmpty(pixelFormat, nameof(pixelFormat));
            FramesPerSecond = framesPerSecond;
            PlaneCount = planeCount;
        }

        public int Width { get; }
        public int Height { get; }
        public string PixelFormat { get; }
        public double FramesPerSecond { get; }
        public int PlaneCount { get; }
    }

    public sealed class CultMeshStreamDescriptor
    {
        public CultMeshStreamDescriptor(
            string streamId,
            string verseId,
            string ownerPeerId,
            CultMeshStreamKind kind,
            CultMeshStreamClock clock,
            IReadOnlyList<CultMeshStreamBodyTransport> preferredTransports,
            string? label = null,
            CultMeshAudioStreamFormat? audio = null,
            CultMeshVideoStreamFormat? video = null,
            CultMeshStreamAccess requiredAccess = CultMeshStreamAccess.Read,
            int maxInFlightFrames = 0,
            string? metadataSchemaId = null)
        {
            StreamId = RequireNonEmpty(streamId, nameof(streamId));
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            OwnerPeerId = RequireNonEmpty(ownerPeerId, nameof(ownerPeerId));
            Kind = kind;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            PreferredTransports = preferredTransports?.ToArray() ?? throw new ArgumentNullException(nameof(preferredTransports));
            if (PreferredTransports.Count == 0)
            {
                throw new ArgumentException("At least one body transport must be preferred.", nameof(preferredTransports));
            }

            Label = label;
            Audio = audio;
            Video = video;
            RequiredAccess = requiredAccess;
            MaxInFlightFrames = maxInFlightFrames;
            MetadataSchemaId = metadataSchemaId;
        }

        public string StreamId { get; }
        public string VerseId { get; }
        public string OwnerPeerId { get; }
        public CultMeshStreamKind Kind { get; }
        public string? Label { get; }
        public CultMeshStreamClock Clock { get; }
        public CultMeshAudioStreamFormat? Audio { get; }
        public CultMeshVideoStreamFormat? Video { get; }
        public IReadOnlyList<CultMeshStreamBodyTransport> PreferredTransports { get; }
        public CultMeshStreamAccess RequiredAccess { get; }
        public int MaxInFlightFrames { get; }
        public string? MetadataSchemaId { get; }
    }

    public sealed class CultMeshStreamConsumerProfile
    {
        public CultMeshStreamConsumerProfile(
            string peerId,
            string verseId,
            IReadOnlyList<CultMeshStreamBodyTransport> supportedTransports,
            IReadOnlyList<CultMeshStreamKind>? acceptedKinds = null,
            bool canImportGpuHandles = false,
            bool canMapSharedMemory = false,
            int maxInFlightFrames = 0)
        {
            PeerId = RequireNonEmpty(peerId, nameof(peerId));
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            SupportedTransports = supportedTransports?.ToArray() ?? throw new ArgumentNullException(nameof(supportedTransports));
            AcceptedKinds = acceptedKinds?.ToArray() ?? Array.Empty<CultMeshStreamKind>();
            CanImportGpuHandles = canImportGpuHandles;
            CanMapSharedMemory = canMapSharedMemory;
            MaxInFlightFrames = maxInFlightFrames;
        }

        public string PeerId { get; }
        public string VerseId { get; }
        public IReadOnlyList<CultMeshStreamBodyTransport> SupportedTransports { get; }
        public IReadOnlyList<CultMeshStreamKind> AcceptedKinds { get; }
        public bool CanImportGpuHandles { get; }
        public bool CanMapSharedMemory { get; }
        public int MaxInFlightFrames { get; }
    }

    public sealed class CultMeshStreamNegotiation
    {
        public CultMeshStreamNegotiation(
            string streamId,
            string producerPeerId,
            string consumerPeerId,
            CultMeshStreamBodyTransport transport,
            CultMeshStreamAccess access,
            int maxInFlightFrames,
            CultMeshStreamCopyBudget copyBudget)
        {
            StreamId = streamId;
            ProducerPeerId = producerPeerId;
            ConsumerPeerId = consumerPeerId;
            Transport = transport;
            Access = access;
            MaxInFlightFrames = maxInFlightFrames;
            CopyBudget = copyBudget;
        }

        public string StreamId { get; }
        public string ProducerPeerId { get; }
        public string ConsumerPeerId { get; }
        public CultMeshStreamBodyTransport Transport { get; }
        public CultMeshStreamAccess Access { get; }
        public int MaxInFlightFrames { get; }
        public CultMeshStreamCopyBudget CopyBudget { get; }
    }

    public sealed class CultMeshStreamFrameHandle
    {
        public CultMeshStreamFrameHandle(
            string streamId,
            ulong sequence,
            long timestampNs,
            CultMeshStreamBodyTransport transport,
            long durationNs = 0,
            int byteLength = 0,
            string? nativeHandle = null,
            string? resourceKey = null,
            string? pageRef = null,
            string? fenceHandle = null,
            ulong fenceValue = 0,
            int unavoidableCopyCount = 0,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            StreamId = RequireNonEmpty(streamId, nameof(streamId));
            Sequence = sequence;
            TimestampNs = timestampNs;
            DurationNs = durationNs;
            Transport = transport;
            ByteLength = byteLength;
            NativeHandle = nativeHandle;
            ResourceKey = resourceKey;
            PageRef = pageRef;
            FenceHandle = fenceHandle;
            FenceValue = fenceValue;
            UnavoidableCopyCount = unavoidableCopyCount;
            Metadata = metadata ?? new Dictionary<string, string>();
        }

        public string StreamId { get; }
        public ulong Sequence { get; }
        public long TimestampNs { get; }
        public long DurationNs { get; }
        public CultMeshStreamBodyTransport Transport { get; }
        public int ByteLength { get; }
        public string? NativeHandle { get; }
        public string? ResourceKey { get; }
        public string? PageRef { get; }
        public string? FenceHandle { get; }
        public ulong FenceValue { get; }
        public int UnavoidableCopyCount { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }

    public readonly struct CultMeshFrameWriteLease
    {
        private readonly CultMeshFrameRegionWriteLease _lease;

        internal CultMeshFrameWriteLease(CultMeshFrameRegionWriteLease lease)
        {
            _lease = lease;
        }

        public int SlotIndex => _lease.SlotIndex;
        public ulong Sequence => checked((ulong)_lease.Sequence);
        public Memory<byte> Memory => _lease.Memory;
        public Span<byte> Span => Memory.Span;

        internal CultMeshFrameRegionWriteLease RegionLease => _lease;
    }

    public readonly struct CultMeshFrameReadLease : IDisposable
    {
        private readonly CultMeshFrameRegionReadLease? _lease;

        internal CultMeshFrameReadLease(
            CultMeshFrameRegionReadLease lease,
            CultMeshStreamFrameHandle handle,
            int slotIndex)
        {
            _lease = lease;
            SlotIndex = slotIndex;
            Handle = handle;
        }

        public int SlotIndex { get; }
        public CultMeshStreamFrameHandle Handle { get; }
        public ReadOnlyMemory<byte> Memory => _lease?.Memory ?? ReadOnlyMemory<byte>.Empty;
        public ReadOnlySpan<byte> Span => Memory.Span;

        public void Dispose()
        {
            _lease?.Dispose();
        }
    }

    public sealed class CultMeshSharedMemoryFrameRingStats
    {
        public CultMeshSharedMemoryFrameRingStats(
            string streamId,
            int slotCount,
            int slotByteLength,
            ulong publishedFrames,
            ulong droppedFrames,
            ulong blockedWrites,
            ulong latestSequence,
            ulong unavoidableCopyCount)
        {
            StreamId = streamId;
            SlotCount = slotCount;
            SlotByteLength = slotByteLength;
            PublishedFrames = publishedFrames;
            DroppedFrames = droppedFrames;
            BlockedWrites = blockedWrites;
            LatestSequence = latestSequence;
            UnavoidableCopyCount = unavoidableCopyCount;
        }

        public string StreamId { get; }
        public int SlotCount { get; }
        public int SlotByteLength { get; }
        public ulong PublishedFrames { get; }
        public ulong DroppedFrames { get; }
        public ulong BlockedWrites { get; }
        public ulong LatestSequence { get; }
        public ulong UnavoidableCopyCount { get; }
    }

    public sealed class CultMeshSharedMemoryFrameRing : IDisposable
    {
        private readonly CultMeshFrameRegion _region;

        public CultMeshSharedMemoryFrameRing(string streamId, int slotCount, int slotByteLength)
        {
            StreamId = RequireNonEmpty(streamId, nameof(streamId));
            if (slotCount != CultMeshFrameRegion.SlotCount)
                throw new ArgumentOutOfRangeException(nameof(slotCount), "CultMesh frame regions use exactly three slots.");
            _region = new CultMeshFrameRegion(
                streamId,
                $"{streamId}.frame",
                layoutVersion: 1,
                capacity: slotByteLength,
                producerEpoch: 0,
                slotByteLength);
        }

        public string StreamId { get; }
        public int SlotCount => CultMeshFrameRegion.SlotCount;
        public int SlotByteLength => _region.SlotByteLength;

        public bool TryAcquireWriteSlot(out CultMeshFrameWriteLease lease)
        {
            if (_region.TryAcquireWrite(out var regionLease))
            {
                lease = new CultMeshFrameWriteLease(regionLease);
                return true;
            }
            lease = default;
            return false;
        }

        public CultMeshStreamFrameHandle CommitWriteSlot(
            CultMeshFrameWriteLease lease,
            long timestampNs,
            int byteLength,
            long durationNs = 0,
            int unavoidableCopyCount = 0,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var generation = _region.Commit(
                lease.RegionLease, byteLength, timestampNs, durationNs, unavoidableCopyCount, metadata);
            return ToHandle(generation);
        }

        public bool TryPublishCopy(
            ReadOnlySpan<byte> bytes,
            long timestampNs,
            long durationNs,
            out CultMeshStreamFrameHandle handle)
        {
            if (bytes.Length > SlotByteLength)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            if (!TryAcquireWriteSlot(out var lease))
            {
                handle = default!;
                return false;
            }

            bytes.CopyTo(lease.Span);
            handle = CommitWriteSlot(lease, timestampNs, bytes.Length, durationNs, unavoidableCopyCount: 1);
            return true;
        }

        public bool TryAcquireLatestRead(out CultMeshFrameReadLease lease)
        {
            var request = new CultMeshBodyValidationRequest
            {
                BodyId = _region.BodyId,
                SchemaId = _region.SchemaId,
                LayoutVersion = _region.LayoutVersion,
                ProducerEpoch = _region.ProducerEpoch,
                Capacity = _region.Capacity,
                AccessMode = CultMeshBodyAccessMode.ReadOnly
            };
            if (!_region.TryAcquireLatestRead(request, out var regionLease))
            {
                lease = default;
                return false;
            }
            var generation = regionLease.Generation;
            lease = new CultMeshFrameReadLease(regionLease, ToHandle(generation), generation.SlotIndex);
            return true;
        }

        public CultMeshSharedMemoryFrameRingStats Stats()
        {
            var stats = _region.Stats();
            return new CultMeshSharedMemoryFrameRingStats(
                StreamId,
                SlotCount,
                SlotByteLength,
                stats.PublishedFrames,
                stats.DroppedFrames,
                stats.BlockedWrites,
                checked((ulong)stats.LatestSequence),
                stats.UnavoidableCopyCount);
        }

        public void Dispose() => _region.Dispose();

        private CultMeshStreamFrameHandle ToHandle(CultMeshFrameGeneration generation) => new(
            StreamId,
            checked((ulong)generation.Descriptor.Sequence),
            generation.TimestampNs,
            CultMeshStreamBodyTransport.SharedMemory,
            generation.DurationNs,
            checked((int)generation.Descriptor.ByteSize),
            resourceKey: $"{StreamId}:slot:{generation.SlotIndex}",
            unavoidableCopyCount: generation.UnavoidableCopyCount,
            metadata: generation.Metadata);
    }

    public sealed class CultMeshStreamCatalog
    {
        private readonly Dictionary<string, CultMeshStreamDescriptor> _streams = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CultMeshStreamFrameHandle> _latestFrames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CultMeshSharedMemoryFrameRing> _rings = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public IReadOnlyList<CultMeshStreamDescriptor> Streams
        {
            get
            {
                lock (_gate)
                {
                    return _streams.Values.OrderBy(stream => stream.StreamId, StringComparer.Ordinal).ToArray();
                }
            }
        }

        public void Declare(CultMeshStreamDescriptor stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            lock (_gate)
            {
                _streams[stream.StreamId] = stream;
            }
        }

        public CultMeshStreamDescriptor? Get(string streamId)
        {
            lock (_gate)
            {
                return _streams.TryGetValue(RequireNonEmpty(streamId, nameof(streamId)), out var stream)
                    ? stream
                    : null;
            }
        }

        public IReadOnlyList<CultMeshStreamDescriptor> Find(string verseId, CultMeshStreamKind? kind = null)
        {
            verseId = RequireNonEmpty(verseId, nameof(verseId));
            lock (_gate)
            {
                return _streams.Values
                    .Where(stream => string.Equals(stream.VerseId, verseId, StringComparison.Ordinal) &&
                                     (!kind.HasValue || stream.Kind == kind.Value))
                    .OrderBy(stream => stream.StreamId, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        public CultMeshStreamNegotiation Negotiate(string streamId, CultMeshStreamConsumerProfile consumer)
        {
            if (consumer == null) throw new ArgumentNullException(nameof(consumer));
            CultMeshStreamDescriptor stream;
            lock (_gate)
            {
                stream = _streams.TryGetValue(RequireNonEmpty(streamId, nameof(streamId)), out var existing)
                    ? existing
                    : throw new InvalidOperationException($"Unknown CultMesh stream '{streamId}'.");
            }
            if (!string.Equals(stream.VerseId, consumer.VerseId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Stream and consumer must belong to the same Verse.");
            }

            if (consumer.AcceptedKinds.Count > 0 && !consumer.AcceptedKinds.Contains(stream.Kind))
            {
                throw new InvalidOperationException($"Consumer does not accept {stream.Kind} streams.");
            }

            var transport = FirstCompatibleTransport(stream, consumer);
            if (!transport.HasValue)
            {
                throw new InvalidOperationException("Stream and consumer have no compatible body transport.");
            }

            var producerMax = stream.MaxInFlightFrames <= 0 ? int.MaxValue : stream.MaxInFlightFrames;
            var consumerMax = consumer.MaxInFlightFrames <= 0 ? int.MaxValue : consumer.MaxInFlightFrames;
            return new CultMeshStreamNegotiation(
                stream.StreamId,
                stream.OwnerPeerId,
                consumer.PeerId,
                transport.Value,
                stream.RequiredAccess,
                Math.Min(producerMax, consumerMax),
                CopyBudgetFor(transport.Value));
        }

        public void PublishFrame(CultMeshStreamFrameHandle handle)
        {
            if (handle == null) throw new ArgumentNullException(nameof(handle));
            lock (_gate)
            {
                if (!_streams.ContainsKey(handle.StreamId))
                {
                    throw new InvalidOperationException($"Unknown CultMesh stream '{handle.StreamId}'.");
                }

                _latestFrames[handle.StreamId] = handle;
            }
        }

        public CultMeshStreamFrameHandle? LatestFrame(string streamId)
        {
            lock (_gate)
            {
                return _latestFrames.TryGetValue(RequireNonEmpty(streamId, nameof(streamId)), out var handle)
                    ? handle
                    : null;
            }
        }

        public CultMeshSharedMemoryFrameRing CreateSharedMemoryRing(string streamId, int slotCount, int slotByteLength)
        {
            lock (_gate)
            {
                if (!_streams.ContainsKey(RequireNonEmpty(streamId, nameof(streamId))))
                {
                    throw new InvalidOperationException($"Unknown CultMesh stream '{streamId}'.");
                }

                var ring = new CultMeshSharedMemoryFrameRing(streamId, slotCount, slotByteLength);
                _rings[streamId] = ring;
                return ring;
            }
        }

        public CultMeshSharedMemoryFrameRing? Ring(string streamId)
        {
            lock (_gate)
            {
                return _rings.TryGetValue(RequireNonEmpty(streamId, nameof(streamId)), out var ring)
                    ? ring
                    : null;
            }
        }

        private static CultMeshStreamCopyBudget CopyBudgetFor(CultMeshStreamBodyTransport transport)
        {
            return transport switch
            {
                CultMeshStreamBodyTransport.SharedMemory => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.SharedD3D12Texture => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.SharedD3D11Texture => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.DmaBuf => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.IOSurface => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.AHardwareBuffer => CultMeshStreamCopyBudget.ZeroCopyTarget,
                CultMeshStreamBodyTransport.CultCachePage => CultMeshStreamCopyBudget.OneCopyFallback,
                _ => CultMeshStreamCopyBudget.OpaqueRuntime
            };
        }

        private static CultMeshStreamBodyTransport? FirstCompatibleTransport(
            CultMeshStreamDescriptor stream,
            CultMeshStreamConsumerProfile consumer)
        {
            foreach (var transport in stream.PreferredTransports)
            {
                if (consumer.SupportedTransports.Contains(transport))
                {
                    return transport;
                }
            }

            return null;
        }
    }

    internal static partial class CultMeshGuards
    {
        internal static string RequireNonEmpty(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be non-empty.", name);
            }

            return value;
        }
    }
}
