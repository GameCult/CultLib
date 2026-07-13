using System;
using System.Collections.Generic;
using System.Threading;

#nullable enable

namespace GameCult.Mesh
{
    public sealed class CultMeshFrameGeneration
    {
        internal CultMeshFrameGeneration(
            int slotIndex,
            CultMeshBodyDescriptor descriptor,
            long timestampNs,
            long durationNs,
            int unavoidableCopyCount,
            IReadOnlyDictionary<string, string>? metadata)
        {
            SlotIndex = slotIndex;
            Descriptor = descriptor;
            TimestampNs = timestampNs;
            DurationNs = durationNs;
            UnavoidableCopyCount = unavoidableCopyCount;
            Metadata = metadata == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(metadata);
        }

        public int SlotIndex { get; }
        public CultMeshBodyDescriptor Descriptor { get; }
        public long TimestampNs { get; }
        public long DurationNs { get; }
        public int UnavoidableCopyCount { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }

    public readonly struct CultMeshFrameRegionWriteLease
    {
        private readonly CultMeshFrameRegion? _owner;
        private readonly object? _reservation;

        internal CultMeshFrameRegionWriteLease(
            CultMeshFrameRegion owner,
            object reservation,
            int slotIndex,
            long sequence,
            Memory<byte> memory)
        {
            _owner = owner;
            _reservation = reservation;
            SlotIndex = slotIndex;
            Sequence = sequence;
            Memory = memory;
        }

        public int SlotIndex { get; }
        public long Sequence { get; }
        public Memory<byte> Memory { get; }
        public Span<byte> Span => Memory.Span;

        internal CultMeshFrameGeneration Commit(
            int byteLength,
            long timestampNs,
            long durationNs,
            int unavoidableCopyCount,
            IReadOnlyDictionary<string, string>? metadata) =>
            _owner?.Commit(this, _reservation!, byteLength, timestampNs, durationNs, unavoidableCopyCount, metadata)
            ?? throw new InvalidOperationException("The frame-region write lease is not valid.");
    }

    public sealed class CultMeshFrameRegionReadLease : IDisposable
    {
        private CultMeshFrameRegion? _owner;

        internal CultMeshFrameRegionReadLease(
            CultMeshFrameRegion owner,
            CultMeshFrameGeneration generation,
            ReadOnlyMemory<byte> memory)
        {
            _owner = owner;
            Generation = generation;
            Memory = memory;
        }

        public CultMeshFrameGeneration Generation { get; }
        public ReadOnlyMemory<byte> Memory { get; }
        public ReadOnlySpan<byte> Span => Memory.Span;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRead(Generation.SlotIndex);
    }

    public sealed class CultMeshFrameRegionStats
    {
        internal CultMeshFrameRegionStats(
            ulong publishedFrames,
            ulong droppedFrames,
            ulong blockedWrites,
            long latestSequence,
            ulong unavoidableCopyCount)
        {
            PublishedFrames = publishedFrames;
            DroppedFrames = droppedFrames;
            BlockedWrites = blockedWrites;
            LatestSequence = latestSequence;
            UnavoidableCopyCount = unavoidableCopyCount;
        }

        public ulong PublishedFrames { get; }
        public ulong DroppedFrames { get; }
        public ulong BlockedWrites { get; }
        public long LatestSequence { get; }
        public ulong UnavoidableCopyCount { get; }
    }

    public sealed class CultMeshFrameRegion : IDisposable
    {
        public const int SlotCount = 3;

        private readonly object _gate = new();
        private readonly byte[] _storage;
        private readonly int[] _readerCounts = new int[SlotCount];
        private readonly object?[] _writeReservations = new object?[SlotCount];
        private readonly CultMeshFrameGeneration?[] _generations = new CultMeshFrameGeneration?[SlotCount];
        private object? _activeWriteReservation;
        private int _writeCursor;
        private int _latestSlot = -1;
        private bool _disposed;
        private long _nextSequence;
        private ulong _publishedFrames;
        private ulong _droppedFrames;
        private ulong _blockedWrites;
        private ulong _unavoidableCopyCount;

        public CultMeshFrameRegion(
            string bodyId,
            string schemaId,
            int layoutVersion,
            int capacity,
            long producerEpoch,
            int slotByteLength)
        {
            BodyId = Require(bodyId, nameof(bodyId));
            SchemaId = Require(schemaId, nameof(schemaId));
            if (layoutVersion < 0) throw new ArgumentOutOfRangeException(nameof(layoutVersion));
            if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (producerEpoch < 0) throw new ArgumentOutOfRangeException(nameof(producerEpoch));
            if (slotByteLength <= 0) throw new ArgumentOutOfRangeException(nameof(slotByteLength));
            LayoutVersion = layoutVersion;
            Capacity = capacity;
            ProducerEpoch = producerEpoch;
            SlotByteLength = slotByteLength;
            _storage = new byte[checked(SlotCount * slotByteLength)];
        }

        public string BodyId { get; }
        public string SchemaId { get; }
        public int LayoutVersion { get; }
        public int Capacity { get; }
        public long ProducerEpoch { get; }
        public int SlotByteLength { get; }

        public bool TryAcquireWrite(out CultMeshFrameRegionWriteLease lease)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_activeWriteReservation != null)
                {
                    _blockedWrites++;
                    lease = default;
                    return false;
                }

                for (var attempt = 0; attempt < SlotCount; attempt++)
                {
                    var slotIndex = (_writeCursor + attempt) % SlotCount;
                    if (_readerCounts[slotIndex] != 0 || _writeReservations[slotIndex] != null)
                        continue;

                    if (_generations[slotIndex] != null)
                        _droppedFrames++;

                    var reservation = new object();
                    _writeReservations[slotIndex] = reservation;
                    _activeWriteReservation = reservation;
                    _writeCursor = (slotIndex + 1) % SlotCount;
                    lease = new CultMeshFrameRegionWriteLease(
                        this,
                        reservation,
                        slotIndex,
                        _nextSequence,
                        new Memory<byte>(_storage, slotIndex * SlotByteLength, SlotByteLength));
                    return true;
                }

                _blockedWrites++;
                lease = default;
                return false;
            }
        }

        public CultMeshFrameGeneration Commit(
            CultMeshFrameRegionWriteLease lease,
            int byteLength,
            long timestampNs,
            long durationNs = 0,
            int unavoidableCopyCount = 0,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            lease.Commit(byteLength, timestampNs, durationNs, unavoidableCopyCount, metadata);

        internal CultMeshFrameGeneration Commit(
            CultMeshFrameRegionWriteLease lease,
            object reservation,
            int byteLength,
            long timestampNs,
            long durationNs,
            int unavoidableCopyCount,
            IReadOnlyDictionary<string, string>? metadata)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                RequireSlot(lease.SlotIndex);
                if (!ReferenceEquals(_activeWriteReservation, reservation) ||
                    !ReferenceEquals(_writeReservations[lease.SlotIndex], reservation) ||
                    lease.Sequence != _nextSequence)
                    throw new InvalidOperationException("The frame-region write lease is stale or was already committed.");
                if (byteLength < 0 || byteLength > SlotByteLength)
                    throw new ArgumentOutOfRangeException(nameof(byteLength));

                var descriptor = new CultMeshBodyDescriptor
                {
                    BodyId = BodyId,
                    SchemaId = SchemaId,
                    LayoutVersion = LayoutVersion,
                    ByteSize = byteLength,
                    Capacity = Capacity,
                    ProducerEpoch = ProducerEpoch,
                    Sequence = _nextSequence,
                    AccessMode = CultMeshBodyAccessMode.ReadOnly,
                    Synchronization = CultMeshBodySynchronization.TripleBuffer,
                    LeaseExpiresAtUnixMs = long.MaxValue,
                    TransportKind = CultMeshBodyTransportKind.SharedMemory,
                    SemanticHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(
                        lease.Span[..byteLength])
                };
                var generation = new CultMeshFrameGeneration(
                    lease.SlotIndex, descriptor, timestampNs, durationNs, unavoidableCopyCount, metadata);

                _generations[lease.SlotIndex] = generation;
                _writeReservations[lease.SlotIndex] = null;
                _activeWriteReservation = null;
                _latestSlot = lease.SlotIndex;
                _nextSequence++;
                _publishedFrames++;
                _unavoidableCopyCount += (ulong)Math.Max(0, unavoidableCopyCount);
                return generation;
            }
        }

        public bool TryAcquireLatestRead(
            CultMeshBodyValidationRequest request,
            out CultMeshFrameRegionReadLease lease)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_latestSlot < 0 || _generations[_latestSlot] == null)
                {
                    lease = null!;
                    return false;
                }

                var generation = _generations[_latestSlot]!;
                CultMeshBodyDescriptorValidator.Validate(generation.Descriptor, request);
                _readerCounts[_latestSlot]++;
                lease = new CultMeshFrameRegionReadLease(
                    this,
                    generation,
                    new ReadOnlyMemory<byte>(
                        _storage,
                        _latestSlot * SlotByteLength,
                        checked((int)generation.Descriptor.ByteSize)));
                return true;
            }
        }

        public CultMeshFrameRegionStats Stats()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return new CultMeshFrameRegionStats(
                    _publishedFrames,
                    _droppedFrames,
                    _blockedWrites,
                    _latestSlot < 0 ? 0 : _generations[_latestSlot]!.Descriptor.Sequence,
                    _unavoidableCopyCount);
            }
        }

        internal void ReleaseRead(int slotIndex)
        {
            lock (_gate)
            {
                if (_disposed) return;
                RequireSlot(slotIndex);
                if (_readerCounts[slotIndex] <= 0)
                    throw new InvalidOperationException("The frame-region reader lease was released more than once.");
                _readerCounts[slotIndex]--;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshFrameRegion));
        }

        private static string Require(string value, string parameterName) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", parameterName) : value;

        private static void RequireSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
    }
}
