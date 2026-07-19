using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

#nullable enable

namespace GameCult.Mesh
{
    // Owner: this publisher owns mapping lifetime, logical generation, and slot commit.
    // Consumers own only read leases; mappings, capabilities, and slot selection remain CultMesh state.
    public sealed class CultMeshFrameBodyPublisher : IDisposable
    {
        internal const string CapabilityPrefix = "frame-v2-";
        internal const int HeaderSize = 512;
        internal const int SequenceOffset = 8;
        internal const int ReaderCountOffset = 32;
        internal const int WriterCountOffset = 48;
        internal const int ContractHashOffset = 64;
        internal const int ByteLengthOffset = 128;
        internal const int LeaseExpiresAtOffset = 144;
        internal const int SemanticHashOffset = 192;
        internal const int SemanticHashStride = 64;
        private readonly object _gate = new();
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _control;
        private readonly Mutex _mutex;
        private readonly TimeSpan _leaseDuration;
        private readonly string _token;
        private int _cursor;
        private long _nextSequence;
        private CultMeshFrameBodyWriteLease? _activeWrite;
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
            for (var slot = 0; slot < CultMeshFrameRegion.SlotCount; slot++)
                _control.Write(SequenceOffset + slot * sizeof(long), -1L);
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
            if (body.Length > SlotByteLength) throw new ArgumentOutOfRangeException(nameof(body));
            if (!TryAcquireWrite(out var write))
            {
                descriptor = null!;
                return false;
            }
            using (write)
            {
                body.CopyTo(write.Span);
                descriptor = write.Commit(body.Length, nowUtc);
                return true;
            }
        }

        /// <summary>
        /// Reserves one producer-owned shared-memory slot for direct writes. The caller must commit or dispose the lease.
        /// </summary>
        public bool TryAcquireWrite(out CultMeshFrameBodyWriteLease lease)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_activeWrite != null)
                {
                    lease = null!;
                    return false;
                }

                var slot = -1;
                WithMutex(() =>
                {
                    for (var attempt = 0; attempt < CultMeshFrameRegion.SlotCount; attempt++)
                    {
                        var candidate = (_cursor + attempt) % CultMeshFrameRegion.SlotCount;
                        if (_control.ReadInt32(ReaderCountOffset + candidate * sizeof(int)) != 0 ||
                            _control.ReadInt32(WriterCountOffset + candidate * sizeof(int)) != 0)
                            continue;
                        _control.Write(WriterCountOffset + candidate * sizeof(int), 1);
                        slot = candidate;
                        _cursor = (candidate + 1) % CultMeshFrameRegion.SlotCount;
                        return;
                    }
                });
                if (slot < 0)
                {
                    lease = null!;
                    return false;
                }

                try
                {
                    var view = _mapping.CreateViewAccessor(
                        SlotOffset(slot, SlotByteLength),
                        SlotByteLength,
                        MemoryMappedFileAccess.ReadWrite);
                    lease = new CultMeshFrameBodyWriteLease(this, view, slot, _nextSequence, SlotByteLength);
                    _activeWrite = lease;
                    return true;
                }
                catch
                {
                    WithMutex(() => _control.Write(WriterCountOffset + slot * sizeof(int), 0));
                    throw;
                }
            }
        }

        internal CultMeshBodyDescriptor Commit(
            CultMeshFrameBodyWriteLease lease,
            int byteLength,
            DateTimeOffset nowUtc)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!ReferenceEquals(_activeWrite, lease) || lease.Sequence != _nextSequence)
                    throw new InvalidOperationException("The CultMesh frame write lease is stale or is not owned by this publisher.");
                if (byteLength < 0 || byteLength > SlotByteLength)
                    throw new ArgumentOutOfRangeException(nameof(byteLength));
                var semanticHash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(lease.Span[..byteLength]);
                WithMutex(() =>
                {
                    var writerOffset = WriterCountOffset + lease.SlotIndex * sizeof(int);
                    if (_control.ReadInt32(writerOffset) != 1 ||
                        _control.ReadInt32(ReaderCountOffset + lease.SlotIndex * sizeof(int)) != 0)
                        throw new InvalidOperationException("The CultMesh frame write reservation was lost before commit.");
                    _control.Write(SequenceOffset + lease.SlotIndex * sizeof(long), lease.Sequence);
                    _control.Write(ByteLengthOffset + lease.SlotIndex * sizeof(int), byteLength);
                    _control.Write(LeaseExpiresAtOffset + lease.SlotIndex * sizeof(long), nowUtc.Add(_leaseDuration).ToUnixTimeMilliseconds());
                    var hashBytes = Encoding.ASCII.GetBytes(semanticHash);
                    _control.WriteArray(SemanticHashOffset + lease.SlotIndex * SemanticHashStride, hashBytes, 0, hashBytes.Length);
                    _control.Write(writerOffset, 0);
                });
                var descriptor = Descriptor(lease.SlotIndex, byteLength, lease.Sequence, semanticHash, nowUtc);
                _nextSequence++;
                _activeWrite = null;
                lease.Complete();
                return descriptor;
            }
        }

        internal void Abandon(CultMeshFrameBodyWriteLease lease)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_activeWrite, lease)) return;
                if (!_disposed)
                {
                    WithMutex(() =>
                        _control.Write(WriterCountOffset + lease.SlotIndex * sizeof(int), 0));
                }
                _activeWrite = null;
            }
        }

        private CultMeshBodyDescriptor Descriptor(
            int slot,
            int byteLength,
            long sequence,
            string semanticHash,
            DateTimeOffset nowUtc) => new()
        {
            BodyId = BodyId, SchemaId = SchemaId, LayoutVersion = LayoutVersion, ByteSize = byteLength,
            Capacity = Capacity, ProducerEpoch = ProducerEpoch, Sequence = sequence,
            AccessMode = CultMeshBodyAccessMode.ReadOnly, Synchronization = CultMeshBodySynchronization.TripleBuffer,
            LeaseExpiresAtUnixMs = nowUtc.Add(_leaseDuration).ToUnixTimeMilliseconds(),
            TransportKind = CultMeshBodyTransportKind.SharedMemory,
            CapabilityToken = _token + "." + slot,
            SemanticHash = semanticHash
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

        internal static void ReleaseReader(string token, int slot)
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
                _activeWrite?.Invalidate();
                _activeWrite = null;
                _control.Dispose();
                _mapping.Dispose();
                _mutex.Dispose();
            }
        }

        private void WithMutex(Action action) => WithMutex(_mutex, action);
        internal static void WithMutex(Mutex mutex, Action action)
        {
            mutex.WaitOne();
            try { action(); }
            finally { mutex.ReleaseMutex(); }
        }
        internal static long SlotOffset(int slot, int slotByteLength) => HeaderSize + (long)slot * slotByteLength;
        internal static string MappingName(string token) => "cultmesh-map-" + token;
        internal static string MutexName(string token) => "cultmesh-lock-" + token;
        internal static void ParseCapability(string capability, out string token, out int slot)
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
        internal static string ContractHash(string bodyId, string schemaId, int layoutVersion, int capacity, long epoch, int slotByteLength) =>
            CultMeshBodyDescriptorValidator.ComputeSemanticHash(
                Encoding.UTF8.GetBytes($"{bodyId}\n{schemaId}\n{layoutVersion}\n{capacity}\n{epoch}\n{slotByteLength}"));
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CultMeshFrameBodyPublisher)); }
        private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value;
    }

    /// <summary>
    /// Retains one verified shared-memory frame mapping and leases only newer committed generations.
    /// The control plane is needed for bootstrap and layout changes, not for every frame.
    /// </summary>
    public sealed class CultMeshMappedFrameBodyCursor : IDisposable
    {
        private readonly CultMeshBodyDescriptor _contract;
        private readonly string _token;
        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _control;
        private readonly Mutex _mutex;
        private readonly int _slotByteLength;
        private long _lastSequence = -1;
        private bool _disposed;

        public static bool CanOpen(CultMeshBodyDescriptor descriptor) =>
            descriptor != null &&
            descriptor.TransportKind == CultMeshBodyTransportKind.SharedMemory &&
            descriptor.Synchronization == CultMeshBodySynchronization.TripleBuffer &&
            CultMeshFrameBodyPublisher.IsFrameCapability(descriptor.CapabilityToken);

        public CultMeshMappedFrameBodyCursor(CultMeshBodyDescriptor descriptor)
        {
            _contract = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (!CanOpen(descriptor))
                throw new ArgumentException("Descriptor is not a mapped CultMesh frame capability.", nameof(descriptor));
            CultMeshFrameBodyPublisher.ParseCapability(descriptor.CapabilityToken, out _token, out _);
            try
            {
                _mapping = MemoryMappedFile.OpenExisting(
                    CultMeshFrameBodyPublisher.MappingName(_token),
                    MemoryMappedFileRights.ReadWrite);
                _mutex = Mutex.OpenExisting(CultMeshFrameBodyPublisher.MutexName(_token));
            }
            catch (Exception error) when (error is FileNotFoundException || error is WaitHandleCannotBeOpenedException)
            {
                throw new InvalidDataException("CultMesh frame capability is unavailable or revoked.", error);
            }
            try
            {
                _control = _mapping.CreateViewAccessor(
                    0,
                    CultMeshFrameBodyPublisher.HeaderSize,
                    MemoryMappedFileAccess.ReadWrite);
                _slotByteLength = _control.ReadInt32(0);
                var contractHash = new byte[64];
                _control.ReadArray(CultMeshFrameBodyPublisher.ContractHashOffset, contractHash, 0, contractHash.Length);
                var expected = CultMeshFrameBodyPublisher.ContractHash(
                    descriptor.BodyId,
                    descriptor.SchemaId,
                    descriptor.LayoutVersion,
                    descriptor.Capacity,
                    descriptor.ProducerEpoch,
                    _slotByteLength);
                if (!string.Equals(Encoding.ASCII.GetString(contractHash), expected, StringComparison.Ordinal))
                    throw new InvalidDataException("CultMesh frame cursor contract does not match its mapping.");
            }
            catch
            {
                _mapping.Dispose();
                _mutex.Dispose();
                throw;
            }
        }

        public long LastSequence => _lastSequence;

        public bool TryAcquireLatest(out ICultMeshBodyReadLease lease) =>
            TryAcquireLatest(DateTimeOffset.UtcNow, out lease);

        public bool TryAcquireLatest(DateTimeOffset nowUtc, out ICultMeshBodyReadLease lease)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshMappedFrameBodyCursor));
            var slot = -1;
            var sequence = -1L;
            var byteLength = 0;
            var leaseExpiresAt = 0L;
            var semanticHash = "";
            CultMeshFrameBodyPublisher.WithMutex(_mutex, () =>
            {
                var slotCount = _control.ReadInt32(4);
                for (var candidate = 0; candidate < slotCount; candidate++)
                {
                    if (_control.ReadInt32(CultMeshFrameBodyPublisher.WriterCountOffset + candidate * sizeof(int)) != 0)
                        continue;
                    var candidateSequence = _control.ReadInt64(
                        CultMeshFrameBodyPublisher.SequenceOffset + candidate * sizeof(long));
                    if (candidateSequence <= _lastSequence || candidateSequence <= sequence)
                        continue;
                    var candidateExpiry = _control.ReadInt64(
                        CultMeshFrameBodyPublisher.LeaseExpiresAtOffset + candidate * sizeof(long));
                    var candidateLength = _control.ReadInt32(
                        CultMeshFrameBodyPublisher.ByteLengthOffset + candidate * sizeof(int));
                    // A byte-length change means the control-plane layout contract changed.
                    // Do not expose that generation through a cursor bootstrapped against
                    // the previous layout; its new descriptor must arrive first.
                    if (candidateExpiry <= nowUtc.ToUnixTimeMilliseconds() ||
                        candidateLength != _contract.ByteSize || candidateLength > _slotByteLength)
                        continue;
                    slot = candidate;
                    sequence = candidateSequence;
                    leaseExpiresAt = candidateExpiry;
                    byteLength = candidateLength;
                }
                if (slot < 0) return;
                var hashBytes = new byte[CultMeshFrameBodyPublisher.SemanticHashStride];
                _control.ReadArray(
                    CultMeshFrameBodyPublisher.SemanticHashOffset + slot * CultMeshFrameBodyPublisher.SemanticHashStride,
                    hashBytes,
                    0,
                    hashBytes.Length);
                semanticHash = Encoding.ASCII.GetString(hashBytes);
                var readerOffset = CultMeshFrameBodyPublisher.ReaderCountOffset + slot * sizeof(int);
                _control.Write(readerOffset, checked(_control.ReadInt32(readerOffset) + 1));
            });
            if (slot < 0)
            {
                lease = null!;
                return false;
            }

            var descriptor = new CultMeshBodyDescriptor
            {
                BodyId = _contract.BodyId,
                SchemaId = _contract.SchemaId,
                LayoutVersion = _contract.LayoutVersion,
                ByteSize = byteLength,
                Capacity = _contract.Capacity,
                ProducerEpoch = _contract.ProducerEpoch,
                Sequence = sequence,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                Synchronization = CultMeshBodySynchronization.TripleBuffer,
                LeaseExpiresAtUnixMs = leaseExpiresAt,
                TransportKind = CultMeshBodyTransportKind.SharedMemory,
                CapabilityToken = _token + "." + slot,
                SemanticHash = semanticHash
            };
            try
            {
                var mapping = MemoryMappedFile.OpenExisting(
                    CultMeshFrameBodyPublisher.MappingName(_token),
                    MemoryMappedFileRights.ReadWrite);
                lease = new CultMeshMappedBodyLease(
                    descriptor,
                    mapping,
                    CultMeshFrameBodyPublisher.SlotOffset(slot, _slotByteLength),
                    () => CultMeshFrameBodyPublisher.ReleaseReader(_token, slot));
                _lastSequence = sequence;
                return true;
            }
            catch
            {
                CultMeshFrameBodyPublisher.ReleaseReader(_token, slot);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _control.Dispose();
            _mapping.Dispose();
            _mutex.Dispose();
        }
    }

    /// <summary>A bounded producer lease over one directly writable shared-memory frame slot.</summary>
    public sealed unsafe class CultMeshFrameBodyWriteLease : IDisposable
    {
        private CultMeshFrameBodyPublisher? _owner;
        private readonly MemoryMappedViewAccessor _view;
        private readonly CultMeshMappedMemoryManager _memory;
        private bool _completed;

        internal CultMeshFrameBodyWriteLease(
            CultMeshFrameBodyPublisher owner,
            MemoryMappedViewAccessor view,
            int slotIndex,
            long sequence,
            int byteLength)
        {
            _owner = owner;
            _view = view;
            _memory = new CultMeshMappedMemoryManager(view, byteLength);
            SlotIndex = slotIndex;
            Sequence = sequence;
        }

        public int SlotIndex { get; }
        public long Sequence { get; }
        public Memory<byte> Memory => _memory.Memory;
        public Span<byte> Span => Memory.Span;

        public CultMeshBodyDescriptor Commit(int byteLength, DateTimeOffset nowUtc) =>
            (_owner ?? throw new ObjectDisposedException(nameof(CultMeshFrameBodyWriteLease)))
            .Commit(this, byteLength, nowUtc);

        internal void Complete()
        {
            _completed = true;
            ReleaseResources();
        }

        internal void Invalidate()
        {
            _owner = null;
            ReleaseResources();
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (!_completed) owner?.Abandon(this);
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            ((IDisposable)_memory).Dispose();
            _view.Dispose();
            _owner = null;
        }
    }

    internal sealed unsafe class CultMeshMappedMemoryManager : MemoryManager<byte>
    {
        private SafeMemoryMappedViewHandle? _handle;
        private byte* _pointer;
        private readonly int _length;

        public CultMeshMappedMemoryManager(MemoryMappedViewAccessor view, int length)
        {
            _handle = view.SafeMemoryMappedViewHandle;
            _length = length;
            byte* pointer = null;
            _handle.AcquirePointer(ref pointer);
            _pointer = pointer + view.PointerOffset;
        }

        public override Span<byte> GetSpan() =>
            _handle == null ? throw new ObjectDisposedException(nameof(CultMeshMappedMemoryManager)) : new Span<byte>(_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if ((uint)elementIndex > (uint)_length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            if (_handle == null) throw new ObjectDisposedException(nameof(CultMeshMappedMemoryManager));
            return new MemoryHandle(_pointer + elementIndex);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle == null) return;
            handle.ReleasePointer();
            _pointer = null;
        }
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
