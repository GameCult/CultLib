using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Networking;

#nullable enable

namespace GameCult.Mesh
{
    /// <summary>
    /// Owns a bounded set of immutable network body generations. This is ephemeral transport state,
    /// not a CultCache document store and not world truth.
    /// </summary>
    public sealed class CultMeshNetworkBodyStore : IDisposable
    {
        public const long DefaultMaximumBodyBytes = 16L * 1024L * 1024L;
        public const int DefaultRetainedGenerations = 8;

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly byte[] _capabilitySecret = new byte[32];
        private readonly long _maximumBodyBytes;
        private readonly int _retainedGenerations;
        private bool _disposed;

        public CultMeshNetworkBodyStore(
            long maximumBodyBytes = DefaultMaximumBodyBytes,
            int retainedGenerations = DefaultRetainedGenerations)
        {
            if (maximumBodyBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
            if (retainedGenerations <= 0) throw new ArgumentOutOfRangeException(nameof(retainedGenerations));
            _maximumBodyBytes = maximumBodyBytes;
            _retainedGenerations = retainedGenerations;
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(_capabilitySecret);
        }

        public CultMeshBodyDescriptor Publish(CultMeshBodyGeneration generation, ReadOnlySpan<byte> body)
        {
            return PublishCore(generation, body.ToArray());
        }

        /// <summary>
        /// Publishes a body buffer by transferring its ownership to the ephemeral store. The caller
        /// must not mutate the array after this call; this avoids an extra full-frame copy.
        /// </summary>
        public CultMeshBodyDescriptor PublishOwned(CultMeshBodyGeneration generation, byte[] body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            return PublishCore(generation, body);
        }

        private CultMeshBodyDescriptor PublishCore(CultMeshBodyGeneration generation, byte[] bytes)
        {
            ValidateGeneration(generation);
            if (bytes.LongLength > _maximumBodyBytes)
                throw new InvalidOperationException(
                    $"CultMesh network body exceeds the configured {_maximumBodyBytes}-byte live-body bound.");

            var hash = CultMeshBodyDescriptorValidator.ComputeSemanticHash(bytes);
            var token = CapabilityToken(generation);
            var descriptor = new CultMeshBodyDescriptor
            {
                BodyId = generation.BodyId,
                SchemaId = generation.SchemaId,
                LayoutVersion = generation.LayoutVersion,
                ByteSize = bytes.LongLength,
                Capacity = generation.Capacity,
                ProducerEpoch = generation.ProducerEpoch,
                Sequence = generation.Sequence,
                AccessMode = CultMeshBodyAccessMode.ReadOnly,
                Synchronization = generation.Synchronization,
                LeaseExpiresAtUnixMs = generation.LeaseExpiresAtUnixMs,
                TransportKind = CultMeshBodyTransportKind.Network,
                CapabilityToken = token,
                SemanticHash = hash
            };

            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (_entries.TryGetValue(token, out var existing))
                {
                    if (!SameDescriptor(existing.Descriptor, descriptor) ||
                        !string.Equals(existing.Descriptor.SemanticHash, hash, StringComparison.Ordinal))
                        throw new InvalidOperationException("CultMesh network body generation is immutable.");
                    Array.Clear(bytes, 0, bytes.Length);
                    return Clone(existing.Descriptor);
                }

                _entries.Add(token, new Entry(Clone(descriptor), bytes));
                TrimOldest();
                return descriptor;
            }
        }

        public bool TryRead(CultMeshBodyReadRequestMessage request, DateTimeOffset nowUtc, out CultMeshBodyDescriptor descriptor, out byte[] body)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpired(nowUtc.ToUnixTimeMilliseconds());
                if (!_entries.TryGetValue(request.CapabilityToken ?? string.Empty, out var entry) ||
                    !MatchesRequest(entry.Descriptor, request))
                {
                    descriptor = new CultMeshBodyDescriptor();
                    body = Array.Empty<byte>();
                    return false;
                }
                descriptor = Clone(entry.Descriptor);
                body = (byte[])entry.Body.Clone();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var entry in _entries.Values) Array.Clear(entry.Body, 0, entry.Body.Length);
                _entries.Clear();
                Array.Clear(_capabilitySecret, 0, _capabilitySecret.Length);
            }
        }

        private string CapabilityToken(CultMeshBodyGeneration generation)
        {
            var input = Encoding.UTF8.GetBytes(string.Join("\u001f", generation.ProducerId, generation.BodyId,
                generation.ProducerEpoch, generation.Sequence));
            using var hmac = new HMACSHA256(_capabilitySecret);
            return string.Concat(hmac.ComputeHash(input).Select(value => value.ToString("x2")));
        }

        private void PruneExpired(long nowUnixMs)
        {
            foreach (var key in _entries.Where(pair => pair.Value.Descriptor.LeaseExpiresAtUnixMs <= nowUnixMs)
                         .Select(pair => pair.Key).ToArray())
                Remove(key);
        }

        private void TrimOldest()
        {
            while (_entries.Count > _retainedGenerations)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.Descriptor.ProducerEpoch)
                    .ThenBy(pair => pair.Value.Descriptor.Sequence)
                    .First().Key;
                Remove(oldest);
            }
        }

        private void Remove(string token)
        {
            if (!_entries.Remove(token, out var removed)) return;
            Array.Clear(removed.Body, 0, removed.Body.Length);
        }

        private static bool MatchesRequest(CultMeshBodyDescriptor descriptor, CultMeshBodyReadRequestMessage request) =>
            string.Equals(descriptor.CapabilityToken, request.CapabilityToken, StringComparison.Ordinal) &&
            string.Equals(descriptor.BodyId, request.BodyId, StringComparison.Ordinal) &&
            string.Equals(descriptor.SchemaId, request.BodySchemaId, StringComparison.Ordinal) &&
            descriptor.LayoutVersion == request.LayoutVersion &&
            descriptor.ProducerEpoch == request.ProducerEpoch && descriptor.Sequence == request.Sequence &&
            descriptor.ByteSize == request.ExpectedSizeBytes &&
            string.Equals(descriptor.SemanticHash, request.SemanticHash, StringComparison.Ordinal);

        private static bool SameDescriptor(CultMeshBodyDescriptor left, CultMeshBodyDescriptor right) =>
            left.BodyId == right.BodyId && left.SchemaId == right.SchemaId &&
            left.LayoutVersion == right.LayoutVersion && left.ByteSize == right.ByteSize &&
            left.Capacity == right.Capacity && left.ProducerEpoch == right.ProducerEpoch &&
            left.Sequence == right.Sequence && left.Synchronization == right.Synchronization &&
            left.LeaseExpiresAtUnixMs == right.LeaseExpiresAtUnixMs;

        private static CultMeshBodyDescriptor Clone(CultMeshBodyDescriptor value) => new()
        {
            BodyId = value.BodyId, SchemaId = value.SchemaId, LayoutVersion = value.LayoutVersion,
            ByteSize = value.ByteSize, Capacity = value.Capacity, ProducerEpoch = value.ProducerEpoch,
            Sequence = value.Sequence, AccessMode = value.AccessMode, Synchronization = value.Synchronization,
            LeaseExpiresAtUnixMs = value.LeaseExpiresAtUnixMs, TransportKind = value.TransportKind,
            CapabilityToken = value.CapabilityToken, SemanticHash = value.SemanticHash
        };

        private static void ValidateGeneration(CultMeshBodyGeneration generation)
        {
            if (generation == null) throw new ArgumentNullException(nameof(generation));
            if (string.IsNullOrWhiteSpace(generation.BodyId) || string.IsNullOrWhiteSpace(generation.ProducerId) ||
                string.IsNullOrWhiteSpace(generation.SchemaId))
                throw new ArgumentException("Body, producer, and schema identities are required.", nameof(generation));
            if (generation.LayoutVersion < 0 || generation.Capacity < 0 || generation.ProducerEpoch < 0 ||
                generation.Sequence < 0 || generation.LeaseExpiresAtUnixMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultMeshNetworkBodyStore));
        }

        private sealed class Entry
        {
            public Entry(CultMeshBodyDescriptor descriptor, byte[] body) { Descriptor = descriptor; Body = body; }
            public CultMeshBodyDescriptor Descriptor { get; }
            public byte[] Body { get; }
        }
    }

    /// <summary>Serves capability-bound live bodies directly over an existing CultNet host.</summary>
    public sealed class CultMeshBodyServer : IDisposable
    {
        private readonly ICultNetSchemaServer _server;
        private readonly CultMeshNetworkBodyStore _bodies;
        private readonly Func<CultMeshBodyReadRequestMessage, ICultNetSchemaServerPeer, Task> _handler;
        private bool _disposed;

        public CultMeshBodyServer(ICultNetSchemaServer server, CultMeshNetworkBodyStore bodies)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
            _handler = HandleAsync;
            _server.OnCultNet(_handler);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultMeshBodyReadRequestMessage>(_handler);
        }

        private Task HandleAsync(CultMeshBodyReadRequestMessage request, ICultNetSchemaServerPeer peer)
        {
            var response = new CultMeshBodyReadResponseMessage
            {
                MessageId = request?.MessageId ?? string.Empty,
                CapabilityToken = request?.CapabilityToken ?? string.Empty
            };
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.MessageId))
                    throw new InvalidDataException("Body read request requires a message identity.");
                if (!_bodies.TryRead(request, DateTimeOffset.UtcNow, out var descriptor, out var bytes))
                    throw new FileNotFoundException("Body capability is unavailable or does not match its generation.");
                response.Found = true;
                response.BodyId = descriptor.BodyId;
                response.ProducerEpoch = descriptor.ProducerEpoch;
                response.Sequence = descriptor.Sequence;
                response.SizeBytes = descriptor.ByteSize;
                response.SemanticHash = descriptor.SemanticHash;
                response.Payload = bytes;
            }
            catch (Exception error)
            {
                response.Found = false;
                response.Error = error.GetType().Name + ": " + error.Message;
                response.Payload = Array.Empty<byte>();
            }
            peer.SendCultNet(response);
            return Task.CompletedTask;
        }
    }

    public sealed class CultMeshSessionBodyProviderOptions
    {
        public ICultMeshClock Clock { get; set; } = CultMeshSystemClock.Instance;
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }

    /// <summary>Reads live body generations through one reusable identity-first body session.</summary>
    public sealed class CultMeshSessionBodyProvider
    {
        private readonly CultMeshSessionManager _sessions;
        private readonly CultMeshSessionTarget _target;
        private readonly CultMeshSessionBodyProviderOptions _options;

        public CultMeshSessionBodyProvider(string providerId, CultMeshSessionManager sessions,
            CultMeshSessionTarget target, CultMeshSessionBodyProviderOptions? options = null)
        {
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? throw new ArgumentException("Provider identity is required.", nameof(providerId)) : providerId;
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _options = options ?? new CultMeshSessionBodyProviderOptions();
            if (_options.ResponseTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        }

        public string ProviderId { get; }
        public Func<CultMeshBodyDescriptor, byte[]> CreateFetchDelegate() =>
            descriptor => FetchAsync(descriptor).GetAwaiter().GetResult();

        public async Task<byte[]> FetchAsync(CultMeshBodyDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (descriptor.TransportKind != CultMeshBodyTransportKind.Network)
                throw new NotSupportedException("Direct body provider requires a network descriptor.");
            var session = await _sessions.ConnectAsync(_target, CultMeshProtocols.Bodies, cancellationToken).ConfigureAwait(false);
            var messageId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<CultMeshBodyReadResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var responseSubscription = session.OnCultNet<CultMeshBodyReadResponseMessage>(response =>
            {
                if (string.Equals(response.MessageId, messageId, StringComparison.Ordinal)) completion.TrySetResult(response);
            });
            using var errorSubscription = session.OnCultNet<CultNetErrorMessage>(error => completion.TrySetException(new IOException(error.Error)));
            session.SendCultNet(new CultMeshBodyReadRequestMessage
            {
                MessageId = messageId, CapabilityToken = descriptor.CapabilityToken, BodyId = descriptor.BodyId,
                BodySchemaId = descriptor.SchemaId, LayoutVersion = descriptor.LayoutVersion,
                ProducerEpoch = descriptor.ProducerEpoch, Sequence = descriptor.Sequence,
                ExpectedSizeBytes = descriptor.ByteSize, SemanticHash = descriptor.SemanticHash
            });
            var response = await WaitAsync(completion.Task, cancellationToken).ConfigureAwait(false);
            if (!response.Found) throw new FileNotFoundException(
                $"Body provider '{ProviderId}' rejected '{descriptor.BodyId}' generation {descriptor.Sequence}: {response.Error}");
            var body = response.Payload ?? Array.Empty<byte>();
            if (!string.Equals(response.CapabilityToken, descriptor.CapabilityToken, StringComparison.Ordinal) ||
                !string.Equals(response.BodyId, descriptor.BodyId, StringComparison.Ordinal) ||
                response.ProducerEpoch != descriptor.ProducerEpoch || response.Sequence != descriptor.Sequence ||
                response.SizeBytes != descriptor.ByteSize || body.LongLength != descriptor.ByteSize ||
                !string.Equals(response.SemanticHash, descriptor.SemanticHash, StringComparison.Ordinal) ||
                !string.Equals(CultMeshBodyDescriptorValidator.ComputeSemanticHash(body), descriptor.SemanticHash, StringComparison.Ordinal))
                throw new InvalidDataException("Direct body response disagrees with its advertised generation.");
            return body;
        }

        private async Task<CultMeshBodyReadResponseMessage> WaitAsync(Task<CultMeshBodyReadResponseMessage> response, CancellationToken cancellationToken)
        {
            using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var deadline = _options.Clock.DelayAsync(_options.ResponseTimeout, deadlineCancellation.Token);
            var completed = await Task.WhenAny(response, deadline).ConfigureAwait(false);
            if (completed == response) { deadlineCancellation.Cancel(); return await response.ConfigureAwait(false); }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Timed out fetching live body from provider '{ProviderId}'.");
        }
    }
}
