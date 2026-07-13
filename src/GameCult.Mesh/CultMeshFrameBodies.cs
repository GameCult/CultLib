using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

#nullable enable

namespace GameCult.Mesh
{
    // Owner: this publisher owns mapping lifetime, logical generation, and slot commit.
    // Consumers own only read leases; mappings, capabilities, and slot selection remain CultMesh state.
    public sealed class CultMeshFrameBodyPublisher : IDisposable
    {
        private const string CapabilityPrefix = "frame-v1-";
        private const int HeaderSize = 128;
        private const int SequenceOffset = 8;
        private const int ReaderCountOffset = 32;
        private const int ContractHashOffset = 64;
        private readonly object _gate = new();
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _control;
        private readonly Mutex _mutex;
        private readonly TimeSpan _leaseDuration;
        private readonly string _token;
        private int _cursor;
        private long _nextSequence;
        private bool _disposed;

        public CultMeshFrameBodyPublisher(
            string bodyId,
            string schemaId,
            int layoutVersion,
            int capacity,
            long producerEpoch,
            int slotByteLength,
            TimeSpan? leaseDuration = null)
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
            _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(5);
            if (_leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            _token = CapabilityPrefix + RandomToken();
            _mapping = MemoryMappedFile.CreateNew(MappingName(_token), checked(HeaderSize + CultMeshFrameRegion.SlotCount * slotByteLength));
            _control = _mapping.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.ReadWrite);
            _control.Write(0, slotByteLength);
            _control.Write(4, CultMeshFrameRegion.SlotCount);
            var contractHash = Encoding.ASCII.GetBytes(ContractHash(BodyId, SchemaId, LayoutVersion, Capacity, ProducerEpoch, SlotByteLength));
            _control.WriteArray(ContractHashOffset, contractHash, 0, contractHash.Length);
            _mutex = new Mutex(false, MutexName(_token));
        }

        public string BodyId { get; }
        public string SchemaId { get; }
        public int LayoutVersion { get; }
        public int Capacity { get; }
        public long ProducerEpoch { get; }
        public int SlotByteLength { get; }

        public bool TryPublish(ReadOnlySpan<byte> body, DateTimeOffset nowUtc, out CultMeshBodyDescriptor descriptor)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (body.Length > SlotByteLength) throw new ArgumentOutOfRangeException(nameof(body));
                var publishedBytes = body.ToArray();
                CultMeshBodyDescriptor? published = null;
                WithMutex(() =>
                {
                    for (var attempt = 0; attempt < CultMeshFrameRegion.SlotCount; attempt++)
                    {
                        var slot = (_cursor + attempt) % CultMeshFrameRegion.SlotCount;
                        if (_control.ReadInt32(ReaderCountOffset + slot * sizeof(int)) != 0) continue;
                        using (var view = _mapping.CreateViewAccessor(SlotOffset(slot, SlotByteLength), SlotByteLength, MemoryMappedFileAccess.Write))
                            view.WriteArray(0, publishedBytes, 0, publishedBytes.Length);
                        _control.Write(SequenceOffset + slot * sizeof(long), _nextSequence);
                        _cursor = (slot + 1) % CultMeshFrameRegion.SlotCount;
                        published = Descriptor(slot, publishedBytes, nowUtc);
                        _nextSequence++;
                        return;
                    }
                });
                descriptor = published!;
                return published != null;
            }
        }

        private CultMeshBodyDescriptor Descriptor(int slot, ReadOnlySpan<byte> body, DateTimeOffset nowUtc) => new()
        {
            BodyId = BodyId, SchemaId = SchemaId, LayoutVersion = LayoutVersion, ByteSize = body.Length,
            Capacity = Capacity, ProducerEpoch = ProducerEpoch, Sequence = _nextSequence,
            AccessMode = CultMeshBodyAccessMode.ReadOnly, Synchronization = CultMeshBodySynchronization.TripleBuffer,
            LeaseExpiresAtUnixMs = nowUtc.Add(_leaseDuration).ToUnixTimeMilliseconds(),
            TransportKind = CultMeshBodyTransportKind.SharedMemory,
            CapabilityToken = _token + "." + slot,
            SemanticHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(body)
        };

        internal static bool IsFrameCapability(string capability) => capability.StartsWith(CapabilityPrefix, StringComparison.Ordinal);

        internal static ICultMeshBodyReadLease OpenReadOnly(CultMeshBodyDescriptor descriptor)
        {
            ParseCapability(descriptor.CapabilityToken, out var token, out var slot);
            MemoryMappedFile mapping;
            Mutex mutex;
            try
            {
                mapping = MemoryMappedFile.OpenExisting(MappingName(token), MemoryMappedFileRights.ReadWrite);
                mutex = Mutex.OpenExisting(MutexName(token));
            }
            catch (Exception error) when (error is FileNotFoundException || error is WaitHandleCannotBeOpenedException)
            {
                throw new InvalidDataException("CultMesh frame capability is unavailable or revoked.", error);
            }
            try
            {
                int slotByteLength = 0;
                WithMutex(mutex, () =>
                {
                    using var control = mapping.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.ReadWrite);
                    slotByteLength = control.ReadInt32(0);
                    if (slot < 0 || slot >= control.ReadInt32(4) || descriptor.ByteSize > slotByteLength)
                        throw new InvalidDataException("CultMesh frame capability does not match its mapping layout.");
                    var contractHash = new byte[64];
                    control.ReadArray(ContractHashOffset, contractHash, 0, contractHash.Length);
                    var expectedContract = ContractHash(
                        descriptor.BodyId, descriptor.SchemaId, descriptor.LayoutVersion,
                        descriptor.Capacity, descriptor.ProducerEpoch, slotByteLength);
                    if (!string.Equals(Encoding.ASCII.GetString(contractHash), expectedContract, StringComparison.Ordinal) ||
                        control.ReadInt64(SequenceOffset + slot * sizeof(long)) != descriptor.Sequence)
                        throw new InvalidOperationException("CultMesh frame generation is stale or belongs to a different body contract.");
                    var offset = ReaderCountOffset + slot * sizeof(int);
                    control.Write(offset, checked(control.ReadInt32(offset) + 1));
                });
                return new CultMeshMappedBodyLease(
                    descriptor, mapping, SlotOffset(slot, slotByteLength),
                    () => ReleaseReader(token, slot));
            }
            catch
            {
                mapping.Dispose();
                mutex.Dispose();
                throw;
            }
            finally { mutex.Dispose(); }
        }

        private static void ReleaseReader(string token, int slot)
        {
            try
            {
                using var mapping = MemoryMappedFile.OpenExisting(MappingName(token), MemoryMappedFileRights.ReadWrite);
                using var mutex = Mutex.OpenExisting(MutexName(token));
                WithMutex(mutex, () =>
                {
                    using var control = mapping.CreateViewAccessor(0, HeaderSize, MemoryMappedFileAccess.ReadWrite);
                    var offset = ReaderCountOffset + slot * sizeof(int);
                    var readers = control.ReadInt32(offset);
                    if (readers > 0) control.Write(offset, readers - 1);
                });
            }
            catch (Exception error) when (error is FileNotFoundException || error is WaitHandleCannotBeOpenedException) { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _control.Dispose();
                _mapping.Dispose();
                _mutex.Dispose();
            }
        }

        private void WithMutex(Action action) => WithMutex(_mutex, action);
        private static void WithMutex(Mutex mutex, Action action)
        {
            mutex.WaitOne();
            try { action(); }
            finally { mutex.ReleaseMutex(); }
        }
        private static long SlotOffset(int slot, int slotByteLength) => HeaderSize + (long)slot * slotByteLength;
        private static string MappingName(string token) => "cultmesh-map-" + token;
        private static string MutexName(string token) => "cultmesh-lock-" + token;
        private static void ParseCapability(string capability, out string token, out int slot)
        {
            var separator = capability.LastIndexOf('.');
            if (separator <= CapabilityPrefix.Length || !int.TryParse(capability.Substring(separator + 1), out slot))
                throw new UnauthorizedAccessException("Invalid CultMesh frame capability.");
            token = capability.Substring(0, separator);
        }
        private static string RandomToken()
        {
            var bytes = new byte[24];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
        private static string ContractHash(string bodyId, string schemaId, int layoutVersion, int capacity, long epoch, int slotByteLength) =>
            CultMeshBodyDescriptorValidator.ComputeSemanticHash(
                Encoding.UTF8.GetBytes($"{bodyId}\n{schemaId}\n{layoutVersion}\n{capacity}\n{epoch}\n{slotByteLength}"));
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CultMeshFrameBodyPublisher)); }
        private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
    }

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
