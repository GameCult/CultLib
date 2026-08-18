using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>Portable failure payload for operation routing and envelope validation.</summary>
    [MessagePackObject]
    public sealed class CultNetOperationFailure
    {
        /// <summary>Gets or sets the stable machine-readable failure code.</summary>
        [Key("code")] public string Code { get; set; } = "operation-error";

        /// <summary>Gets or sets the human-readable failure description.</summary>
        [Key("message")] public string Message { get; set; } = "CultNet operation failed.";
    }

    /// <summary>
    /// Typed application input delivered through one CultNet operation envelope.
    /// The message id is the caller's idempotency key; the application transaction
    /// remains responsible for applying it exactly once.
    /// </summary>
    public sealed class CultNetOperationContext<TRequest>
    {
        internal CultNetOperationContext(CultNetOperationRequestMessage envelope, TRequest value)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Value = value;
        }

        /// <summary>Gets the decoded application request.</summary>
        public TRequest Value { get; }

        /// <summary>Gets the durable caller-supplied idempotency key.</summary>
        public string IdempotencyKey => Envelope.MessageId;

        /// <summary>Gets the calling runtime identity, when supplied.</summary>
        public string? SourceRuntimeId => Envelope.SourceRuntimeId;

        /// <summary>Gets the optional target runtime identity.</summary>
        public string? TargetRuntimeId => Envelope.TargetRuntimeId;

        /// <summary>Gets the original transport-neutral envelope for diagnostics.</summary>
        public CultNetOperationRequestMessage Envelope { get; }
    }

    /// <summary>Provider-authored typed result for one CultNet operation.</summary>
    public sealed class CultNetOperationReply<TResponse>
    {
        /// <summary>Creates one provider-authored typed reply.</summary>
        public CultNetOperationReply(
            string status,
            TResponse value,
            IEnumerable<string>? diagnostics = null)
        {
            Status = RequireNonEmpty(status, nameof(status));
            Value = value;
            Diagnostics = diagnostics == null ? Array.Empty<string>() : new List<string>(diagnostics).ToArray();
        }

        /// <summary>Gets the provider-authored operation status.</summary>
        public string Status { get; }

        /// <summary>Gets the typed response payload.</summary>
        public TResponse Value { get; }

        /// <summary>Gets provider-authored diagnostics.</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>Creates an accepted typed reply.</summary>
        public static CultNetOperationReply<TResponse> Accepted(TResponse value) => new("accepted", value);

        /// <summary>Creates a rejected typed reply whose payload remains schema-valid.</summary>
        public static CultNetOperationReply<TResponse> Rejected(
            TResponse value,
            params string[] diagnostics) => new("rejected", value, diagnostics);

        private static string RequireNonEmpty(string value, string paramName) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value.Trim();
    }

    /// <summary>
    /// Transport-neutral typed dispatcher for CultNet application operations.
    /// It owns envelope validation and serialization only; registered handlers own
    /// domain validation, idempotency, state mutation, and durable receipts.
    /// </summary>
    public sealed class CultNetOperationServer : IDisposable
    {
        /// <summary>Schema used by correlated framework-level operation failures.</summary>
        public const string FailureSchemaId = "gamecult.cultnet.operation_failure.v1";

        private const string PayloadEncoding = "messagepack-base64";
        private readonly ICultNetSchemaServer _server;
        private readonly string? _sourceRuntimeId;
        private readonly Dictionary<Route, IBinding> _bindings = new();
        private readonly Func<CultNetOperationRequestMessage, ICultNetSchemaServerPeer, Task> _handler;
        private bool _disposed;

        /// <summary>Attaches one typed operation dispatcher to a schema server.</summary>
        public CultNetOperationServer(ICultNetSchemaServer server, string? sourceRuntimeId = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _sourceRuntimeId = string.IsNullOrWhiteSpace(sourceRuntimeId) ? null : sourceRuntimeId!.Trim();
            _handler = HandleAsync;
            _server.OnCultNet(_handler);
        }

        /// <summary>
        /// Registers one typed operation. Duplicate service/operation routes are rejected.
        /// </summary>
        public CultNetOperationServer Register<TRequest, TResponse>(
            string serviceId,
            string operation,
            string requestSchema,
            string responseSchema,
            Func<CultNetOperationContext<TRequest>, Task<CultNetOperationReply<TResponse>>> handler)
        {
            ThrowIfDisposed();
            var route = new Route(serviceId, operation);
            if (_bindings.ContainsKey(route))
                throw new InvalidOperationException($"CultNet operation '{route}' is already registered.");
            _bindings.Add(route, new Binding<TRequest, TResponse>(
                route,
                requestSchema,
                responseSchema,
                handler ?? throw new ArgumentNullException(nameof(handler)),
                _sourceRuntimeId));
            return this;
        }

        /// <summary>
        /// Registers one typed operation whose successful handler result is always accepted.
        /// </summary>
        public CultNetOperationServer Register<TRequest, TResponse>(
            string serviceId,
            string operation,
            string requestSchema,
            string responseSchema,
            Func<CultNetOperationContext<TRequest>, Task<TResponse>> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Register<TRequest, TResponse>(
                serviceId,
                operation,
                requestSchema,
                responseSchema,
                async context => CultNetOperationReply<TResponse>.Accepted(await handler(context).ConfigureAwait(false)));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _server.RemoveCultNetMessageListener<CultNetOperationRequestMessage>(_handler);
            _bindings.Clear();
        }

        private async Task HandleAsync(CultNetOperationRequestMessage request, ICultNetSchemaServerPeer peer)
        {
            if (_disposed) return;
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (peer == null) throw new ArgumentNullException(nameof(peer));

            Route route;
            try
            {
                route = new Route(request.ServiceId, request.Operation);
            }
            catch (ArgumentException error)
            {
                peer.SendCultNet(Failure(request, "invalid", "invalid-route", error.Message));
                return;
            }

            if (!_bindings.TryGetValue(route, out var binding))
            {
                peer.SendCultNet(Failure(
                    request,
                    "unsupported",
                    "operation-not-registered",
                    $"CultNet operation '{route}' is not registered."));
                return;
            }

            try
            {
                peer.SendCultNet(await binding.InvokeAsync(request).ConfigureAwait(false));
            }
            catch (RequestException error)
            {
                peer.SendCultNet(Failure(request, "invalid", error.Code, error.Message));
            }
            catch (Exception error)
            {
                peer.SendCultNet(Failure(
                    request,
                    "error",
                    "handler-failed",
                    $"CultNet operation '{route}' failed: {error.Message}"));
            }
        }

        private CultNetOperationResponseMessage Failure(
            CultNetOperationRequestMessage request,
            string status,
            string code,
            string message)
        {
            return new CultNetOperationResponseMessage
            {
                MessageId = request.MessageId ?? "",
                ServiceId = request.ServiceId ?? "",
                Operation = request.Operation ?? "",
                Status = status,
                PayloadSchema = FailureSchemaId,
                PayloadEncoding = PayloadEncoding,
                Payload = Convert.ToBase64String(MessagePackSerializer.Serialize(
                    new CultNetOperationFailure { Code = code, Message = message },
                    CultNetSchemaMessageSerialization.Options)),
                Diagnostics = new[] { message },
                SourceRuntimeId = _sourceRuntimeId
            };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CultNetOperationServer));
        }

        private interface IBinding
        {
            Task<CultNetOperationResponseMessage> InvokeAsync(CultNetOperationRequestMessage request);
        }

        private sealed class Binding<TRequest, TResponse> : IBinding
        {
            private readonly Route _route;
            private readonly string _requestSchema;
            private readonly string _responseSchema;
            private readonly Func<CultNetOperationContext<TRequest>, Task<CultNetOperationReply<TResponse>>> _handler;
            private readonly string? _sourceRuntimeId;

            public Binding(
                Route route,
                string requestSchema,
                string responseSchema,
                Func<CultNetOperationContext<TRequest>, Task<CultNetOperationReply<TResponse>>> handler,
                string? sourceRuntimeId)
            {
                _route = route;
                _requestSchema = RequireNonEmpty(requestSchema, nameof(requestSchema));
                _responseSchema = RequireNonEmpty(responseSchema, nameof(responseSchema));
                _handler = handler;
                _sourceRuntimeId = sourceRuntimeId;
            }

            public async Task<CultNetOperationResponseMessage> InvokeAsync(CultNetOperationRequestMessage request)
            {
                if (string.IsNullOrWhiteSpace(request.MessageId))
                    throw new RequestException("missing-idempotency-key", "CultNet operation omitted its idempotency key.");
                if (!string.Equals(request.PayloadSchema, _requestSchema, StringComparison.Ordinal))
                    throw new RequestException(
                        "request-schema-mismatch",
                        $"CultNet operation '{_route}' expected payload schema '{_requestSchema}', got '{request.PayloadSchema}'.");
                if (!string.Equals(request.PayloadEncoding, PayloadEncoding, StringComparison.Ordinal))
                    throw new RequestException(
                        "request-encoding-mismatch",
                        $"CultNet operation '{_route}' expected payload encoding '{PayloadEncoding}', got '{request.PayloadEncoding}'.");

                TRequest value;
                try
                {
                    value = MessagePackSerializer.Deserialize<TRequest>(
                        Convert.FromBase64String(request.Payload),
                        CultNetSchemaMessageSerialization.Options);
                }
                catch (FormatException error)
                {
                    throw new RequestException("request-payload-invalid", "CultNet operation payload was not valid base64.", error);
                }
                catch (MessagePackSerializationException error)
                {
                    throw new RequestException("request-payload-invalid", "CultNet operation payload was not valid MessagePack.", error);
                }
                if (value == null) throw new RequestException("request-payload-null", "CultNet operation payload decoded to null.");

                var reply = await _handler(new CultNetOperationContext<TRequest>(request, value)).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("CultNet operation handler returned no reply.");
                if (reply.Value == null) throw new InvalidOperationException("CultNet operation handler returned a null payload.");
                return new CultNetOperationResponseMessage
                {
                    MessageId = request.MessageId,
                    ServiceId = _route.ServiceId,
                    Operation = _route.Operation,
                    Status = reply.Status,
                    PayloadSchema = _responseSchema,
                    PayloadEncoding = PayloadEncoding,
                    Payload = Convert.ToBase64String(MessagePackSerializer.Serialize(
                        reply.Value,
                        CultNetSchemaMessageSerialization.Options)),
                    Diagnostics = new List<string>(reply.Diagnostics).ToArray(),
                    SourceRuntimeId = _sourceRuntimeId
                };
            }
        }

        private sealed class RequestException : Exception
        {
            public RequestException(string code, string message, Exception? inner = null)
                : base(message, inner)
            {
                Code = code;
            }

            public string Code { get; }
        }

        private readonly struct Route : IEquatable<Route>
        {
            public Route(string serviceId, string operation)
            {
                ServiceId = RequireNonEmpty(serviceId, nameof(serviceId));
                Operation = RequireNonEmpty(operation, nameof(operation));
            }

            public string ServiceId { get; }
            public string Operation { get; }

            public bool Equals(Route other) =>
                string.Equals(ServiceId, other.ServiceId, StringComparison.Ordinal) &&
                string.Equals(Operation, other.Operation, StringComparison.Ordinal);

            public override bool Equals(object? value) => value is Route route && Equals(route);
            public override int GetHashCode() =>
                (StringComparer.Ordinal.GetHashCode(ServiceId) * 397) ^ StringComparer.Ordinal.GetHashCode(Operation);
            public override string ToString() => ServiceId + "/" + Operation;
        }

        private static string RequireNonEmpty(string value, string paramName) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value.Trim();
    }
}
