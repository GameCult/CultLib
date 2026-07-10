using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;
using R3;

namespace GameCult.Mesh
{
    /// <summary>
    /// Describes the route CultMesh used or should prefer for a typed operation, query, pointer, or native view.
    /// </summary>
    public enum CultMeshLocalityKind
    {
        /// <summary>Let CultMesh choose the fastest valid route.</summary>
        Automatic,
        /// <summary>The target is in the same process.</summary>
        InProcess,
        /// <summary>The target is available through a shared CultCache slab or native shared memory view.</summary>
        SharedMemory,
        /// <summary>The target is reachable through local inter-process transport.</summary>
        Ipc,
        /// <summary>The target is reachable through a network Verse route.</summary>
        Network,
        /// <summary>The target is hosted in or exposed to a WebAssembly runtime.</summary>
        Wasm
    }

    /// <summary>
    /// Names a CultMesh locality decision without exposing transport plumbing to application code.
    /// </summary>
    public sealed class CultMeshRouteHint
    {
        /// <summary>Creates a route hint.</summary>
        public CultMeshRouteHint(CultMeshLocalityKind kind = CultMeshLocalityKind.Automatic, string? description = null)
        {
            Kind = kind;
            Description = description;
        }

        /// <summary>Gets the requested or observed locality kind.</summary>
        public CultMeshLocalityKind Kind { get; }

        /// <summary>Gets optional human-facing route diagnostics.</summary>
        public string? Description { get; }

        /// <summary>Gets the default automatic route hint.</summary>
        public static CultMeshRouteHint Automatic { get; } = new();
    }

    /// <summary>
    /// Flat, transport-friendly fields for a CultMesh route hint.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshRouteRecord
    {
        /// <summary>Creates flat route fields.</summary>
        [SerializationConstructor]
        public CultMeshRouteRecord(string? kind = null, string? description = null)
        {
            Kind = kind ?? "";
            Description = description ?? "";
        }

        /// <summary>Gets the serialized route locality kind.</summary>
        [Key(0)] public string Kind { get; }

        /// <summary>Gets the serialized route description.</summary>
        [Key(1)] public string Description { get; }

        /// <summary>Creates flat route fields from a route hint.</summary>
        public static CultMeshRouteRecord FromRoute(CultMeshRouteHint? routeHint)
        {
            var route = routeHint ?? CultMeshRouteHint.Automatic;
            return new CultMeshRouteRecord(route.Kind.ToString(), route.Description);
        }

        /// <summary>Rehydrates flat route fields into a route hint.</summary>
        public CultMeshRouteHint ToRoute(CultMeshRouteHint? fallback = null)
        {
            var fallbackRoute = fallback ?? CultMeshRouteHint.Automatic;
            return new CultMeshRouteHint(
                ParseKind(Kind, fallbackRoute.Kind),
                string.IsNullOrWhiteSpace(Description) ? fallbackRoute.Description : Description);
        }

        private static CultMeshLocalityKind ParseKind(string? value, CultMeshLocalityKind fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            if (Enum.TryParse<CultMeshLocalityKind>(value, ignoreCase: true, out var parsed))
                return parsed;

            var normalized = value.Replace("-", "", StringComparison.Ordinal);
            foreach (CultMeshLocalityKind candidate in Enum.GetValues(typeof(CultMeshLocalityKind)))
            {
                if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return fallback;
        }
    }

    /// <summary>
    /// Shared Verse invocation context used by generated domain sugar.
    /// </summary>
    public sealed class CultMeshVerseContext
    {
        /// <summary>Creates a Verse context.</summary>
        public CultMeshVerseContext(
            string verseId,
            string runtimeId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshAuthorityClaim>? claims = null)
        {
            VerseId = RequireNonEmpty(verseId, nameof(verseId));
            RuntimeId = RequireNonEmpty(runtimeId, nameof(runtimeId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Claims = claims?.ToArray() ?? Array.Empty<CultMeshAuthorityClaim>();
        }

        /// <summary>Gets the semantic Verse id.</summary>
        public string VerseId { get; }

        /// <summary>Gets the runtime using the Verse.</summary>
        public string RuntimeId { get; }

        /// <summary>Gets the preferred route for generated operations and queries.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets authority claims carried by generated operations.</summary>
        public IReadOnlyList<CultMeshAuthorityClaim> Claims { get; }

        /// <summary>Returns a copy with one additional authority claim.</summary>
        public CultMeshVerseContext WithClaim(CultMeshAuthorityClaim claim)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            return new CultMeshVerseContext(VerseId, RuntimeId, RouteHint, Claims.Concat(new[] { claim }));
        }

        /// <summary>Returns a copy with the supplied route hint.</summary>
        public CultMeshVerseContext WithRoute(CultMeshRouteHint routeHint)
        {
            if (routeHint == null) throw new ArgumentNullException(nameof(routeHint));
            return new CultMeshVerseContext(VerseId, RuntimeId, routeHint, Claims);
        }

        /// <summary>Creates a typed operation context from the Verse context.</summary>
        public CultMeshOperationContext OperationContext(string? idempotencyKey = null)
        {
            return new CultMeshOperationContext(RuntimeId, Claims, RouteHint, idempotencyKey);
        }

        /// <summary>Creates a typed query context from the Verse context.</summary>
        public CultMeshQueryContext QueryContext()
        {
            return new CultMeshQueryContext(RuntimeId, RouteHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Managed Verse handle that generated domain packages can bind to.
    /// </summary>
    public sealed class CultMeshVerse
    {
        /// <summary>Creates a managed Verse handle.</summary>
        public CultMeshVerse(CultMeshVerseContext context)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Gets the Verse context shared by generated domain sugar.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the semantic Verse id.</summary>
        public string VerseId => Context.VerseId;

        /// <summary>Gets the runtime using the Verse.</summary>
        public string RuntimeId => Context.RuntimeId;

        /// <summary>Binds this Verse to a generated domain facade.</summary>
        public TSchema Use<TSchema>(Func<CultMeshVerseContext, TSchema> schemaFactory)
        {
            if (schemaFactory == null) throw new ArgumentNullException(nameof(schemaFactory));
            return schemaFactory(Context);
        }

        /// <summary>Returns a Verse handle with one additional authority claim.</summary>
        public CultMeshVerse WithClaim(CultMeshAuthorityClaim claim)
        {
            return new CultMeshVerse(Context.WithClaim(claim));
        }

        /// <summary>Returns a Verse handle with the supplied route hint.</summary>
        public CultMeshVerse WithRoute(CultMeshRouteHint routeHint)
        {
            return new CultMeshVerse(Context.WithRoute(routeHint));
        }

        /// <summary>Creates a typed operation context from this Verse.</summary>
        public CultMeshOperationContext OperationContext(string? idempotencyKey = null)
        {
            return Context.OperationContext(idempotencyKey);
        }

        /// <summary>Creates a typed query context from this Verse.</summary>
        public CultMeshQueryContext QueryContext()
        {
            return Context.QueryContext();
        }

        /// <summary>Binds an operation handle to this Verse.</summary>
        public CultMeshBoundOperationHandle<TRequest, TResponse> BindOperation<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            return new CultMeshBoundOperationHandle<TRequest, TResponse>(Context, operation);
        }

        /// <summary>Binds a query surface to this Verse.</summary>
        public CultMeshBoundQuerySurface<TParameters, TResult> BindQuery<TParameters, TResult>(
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            return new CultMeshBoundQuerySurface<TParameters, TResult>(Context, query);
        }

        /// <summary>Binds a live feed to this Verse.</summary>
        public CultMeshBoundLiveFeed<TParameters, TResult> BindLiveFeed<TParameters, TResult>(
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            return new CultMeshBoundLiveFeed<TParameters, TResult>(Context, feed);
        }

        /// <summary>Binds a state pointer to this Verse.</summary>
        public CultMeshBoundStatePointer<TValue> BindStatePointer<TValue>(
            CultMeshStatePointer<TValue> pointer)
        {
            return new CultMeshBoundStatePointer<TValue>(Context, pointer);
        }

        /// <summary>Binds a mutable state pointer to this Verse.</summary>
        public CultMeshBoundMutableStatePointer<TValue> BindMutableStatePointer<TValue>(
            CultMeshMutableStatePointer<TValue> pointer)
        {
            return new CultMeshBoundMutableStatePointer<TValue>(Context, pointer);
        }
    }

    /// <summary>
    /// Describes a runtime authority claim attached to a typed operation.
    /// </summary>
    public sealed class CultMeshAuthorityClaim
    {
        /// <summary>Creates an authority claim.</summary>
        public CultMeshAuthorityClaim(string role, string? shardId = null, string? leaseId = null)
        {
            Role = RequireNonEmpty(role, nameof(role));
            ShardId = shardId;
            LeaseId = leaseId;
        }

        /// <summary>Gets the role or claim kind being asserted.</summary>
        public string Role { get; }

        /// <summary>Gets the optional shard covered by the claim.</summary>
        public string? ShardId { get; }

        /// <summary>Gets the optional lease id backing the claim.</summary>
        public string? LeaseId { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Context supplied when invoking a typed CultMesh operation.
    /// </summary>
    public sealed class CultMeshOperationContext
    {
        /// <summary>Creates an operation context.</summary>
        public CultMeshOperationContext(
            string runtimeId,
            IEnumerable<CultMeshAuthorityClaim>? claims = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            RuntimeId = RequireNonEmpty(runtimeId, nameof(runtimeId));
            Claims = claims?.ToArray() ?? Array.Empty<CultMeshAuthorityClaim>();
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            IdempotencyKey = idempotencyKey;
        }

        /// <summary>Gets the runtime invoking the operation.</summary>
        public string RuntimeId { get; }

        /// <summary>Gets authority claims attached to the operation.</summary>
        public IReadOnlyList<CultMeshAuthorityClaim> Claims { get; }

        /// <summary>Gets the preferred route for the operation.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets an optional operation idempotency key.</summary>
        public string? IdempotencyKey { get; }

        /// <summary>Creates an operation context for a runtime.</summary>
        public static CultMeshOperationContext ForRuntime(string runtimeId)
        {
            return new CultMeshOperationContext(runtimeId);
        }

        /// <summary>Returns a copy with one additional authority claim.</summary>
        public CultMeshOperationContext WithClaim(CultMeshAuthorityClaim claim)
        {
            if (claim == null) throw new ArgumentNullException(nameof(claim));
            return new CultMeshOperationContext(RuntimeId, Claims.Concat(new[] { claim }), RouteHint, IdempotencyKey);
        }

        /// <summary>Returns a copy with the supplied route hint.</summary>
        public CultMeshOperationContext WithRoute(CultMeshRouteHint routeHint)
        {
            if (routeHint == null) throw new ArgumentNullException(nameof(routeHint));
            return new CultMeshOperationContext(RuntimeId, Claims, routeHint, IdempotencyKey);
        }

        /// <summary>Returns a copy with the supplied idempotency key.</summary>
        public CultMeshOperationContext WithIdempotencyKey(string idempotencyKey)
        {
            return new CultMeshOperationContext(
                RuntimeId,
                Claims,
                RouteHint,
                RequireNonEmpty(idempotencyKey, nameof(idempotencyKey)));
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Receipt returned by a typed CultMesh operation.
    /// </summary>
    public sealed class CultMeshOperationReceipt
    {
        /// <summary>Creates an operation receipt.</summary>
        public CultMeshOperationReceipt(
            string operationId,
            bool accepted,
            CultMeshRouteHint? route = null,
            string? diagnostic = null)
        {
            OperationId = RequireNonEmpty(operationId, nameof(operationId));
            Accepted = accepted;
            Route = route ?? CultMeshRouteHint.Automatic;
            Diagnostic = diagnostic;
        }

        /// <summary>Gets the semantic operation id.</summary>
        public string OperationId { get; }

        /// <summary>Gets whether the operation was accepted by the target authority boundary.</summary>
        public bool Accepted { get; }

        /// <summary>Gets the route used for the operation.</summary>
        public CultMeshRouteHint Route { get; }

        /// <summary>Gets optional operation diagnostics.</summary>
        public string? Diagnostic { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Method-shaped handle for a typed CultMesh operation.
    /// </summary>
    public sealed class CultMeshOperationHandle<TRequest, TResponse>
    {
        private readonly Func<TRequest, CultMeshOperationContext, Task<TResponse>> _invoke;

        /// <summary>Creates an operation handle.</summary>
        public CultMeshOperationHandle(
            string operationId,
            Func<TRequest, CultMeshOperationContext, Task<TResponse>> invoke)
        {
            OperationId = RequireNonEmpty(operationId, nameof(operationId));
            _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        }

        /// <summary>Gets the semantic operation id.</summary>
        public string OperationId { get; }

        /// <summary>Invokes the typed operation.</summary>
        public Task<TResponse> InvokeAsync(TRequest request, CultMeshOperationContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _invoke(request, context);
        }

        /// <summary>Invokes the typed operation for one runtime using default context.</summary>
        public Task<TResponse> InvokeAsync(TRequest request, string runtimeId)
        {
            return InvokeAsync(request, CultMeshOperationContext.ForRuntime(runtimeId));
        }

        /// <summary>Binds this operation to a Verse context.</summary>
        public CultMeshBoundOperationHandle<TRequest, TResponse> Bind(CultMeshVerseContext context)
        {
            return new CultMeshBoundOperationHandle<TRequest, TResponse>(context, this);
        }

        /// <summary>Binds this operation to a Verse.</summary>
        public CultMeshBoundOperationHandle<TRequest, TResponse> Bind(CultMeshVerse verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Bind(verse.Context);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Operation handle pre-bound to a Verse so generated domain sugar can invoke without context plumbing.
    /// </summary>
    public sealed class CultMeshBoundOperationHandle<TRequest, TResponse>
    {
        /// <summary>Creates a Verse-bound operation handle.</summary>
        public CultMeshBoundOperationHandle(
            CultMeshVerseContext context,
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the underlying operation handle.</summary>
        public CultMeshOperationHandle<TRequest, TResponse> Operation { get; }

        /// <summary>Gets the semantic operation id.</summary>
        public string OperationId => Operation.OperationId;

        /// <summary>Invokes the operation through the bound Verse.</summary>
        public Task<TResponse> InvokeAsync(TRequest request, string? idempotencyKey = null)
        {
            return Operation.InvokeAsync(request, Context.OperationContext(idempotencyKey));
        }
    }

    /// <summary>
    /// Context supplied when executing a typed CultMesh query surface.
    /// </summary>
    public sealed class CultMeshQueryContext
    {
        /// <summary>Creates a query context.</summary>
        public CultMeshQueryContext(string runtimeId, CultMeshRouteHint? routeHint = null)
        {
            RuntimeId = RequireNonEmpty(runtimeId, nameof(runtimeId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the runtime executing the query.</summary>
        public string RuntimeId { get; }

        /// <summary>Gets the preferred route for the query.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Creates a query context for a runtime.</summary>
        public static CultMeshQueryContext ForRuntime(string runtimeId)
        {
            return new CultMeshQueryContext(runtimeId);
        }

        /// <summary>Returns a copy with the supplied route hint.</summary>
        public CultMeshQueryContext WithRoute(CultMeshRouteHint routeHint)
        {
            if (routeHint == null) throw new ArgumentNullException(nameof(routeHint));
            return new CultMeshQueryContext(RuntimeId, routeHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Fluent builder for typed operation context. This is sugar over <see cref="CultMeshOperationContext"/>,
    /// intended for call sites that should read like native Verse operations instead of transport setup.
    /// </summary>
    public sealed class CultMeshOperationContextBuilder
    {
        private readonly string _runtimeId;
        private readonly List<CultMeshAuthorityClaim> _claims = new();
        private CultMeshRouteHint _routeHint = CultMeshRouteHint.Automatic;
        private string? _idempotencyKey;

        /// <summary>Creates a builder for one runtime.</summary>
        public CultMeshOperationContextBuilder(string runtimeId)
        {
            _runtimeId = RequireNonEmpty(runtimeId, nameof(runtimeId));
        }

        /// <summary>Adds an authority claim.</summary>
        public CultMeshOperationContextBuilder Claim(string role, string? shardId = null, string? leaseId = null)
        {
            _claims.Add(new CultMeshAuthorityClaim(role, shardId, leaseId));
            return this;
        }

        /// <summary>Adds an authority claim.</summary>
        public CultMeshOperationContextBuilder Claim(CultMeshAuthorityClaim claim)
        {
            _claims.Add(claim ?? throw new ArgumentNullException(nameof(claim)));
            return this;
        }

        /// <summary>Adds many authority claims.</summary>
        public CultMeshOperationContextBuilder Claims(IEnumerable<CultMeshAuthorityClaim> claims)
        {
            if (claims == null) throw new ArgumentNullException(nameof(claims));
            _claims.AddRange(claims);
            return this;
        }

        /// <summary>Sets the preferred route hint.</summary>
        public CultMeshOperationContextBuilder Route(CultMeshLocalityKind kind, string? description = null)
        {
            _routeHint = new CultMeshRouteHint(kind, description);
            return this;
        }

        /// <summary>Sets the operation idempotency key.</summary>
        public CultMeshOperationContextBuilder Idempotency(string idempotencyKey)
        {
            _idempotencyKey = RequireNonEmpty(idempotencyKey, nameof(idempotencyKey));
            return this;
        }

        /// <summary>Creates the immutable operation context.</summary>
        public CultMeshOperationContext Build()
        {
            return new CultMeshOperationContext(_runtimeId, _claims, _routeHint, _idempotencyKey);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Fluent builder for typed query context.
    /// </summary>
    public sealed class CultMeshQueryContextBuilder
    {
        private readonly string _runtimeId;
        private CultMeshRouteHint _routeHint = CultMeshRouteHint.Automatic;

        /// <summary>Creates a builder for one runtime.</summary>
        public CultMeshQueryContextBuilder(string runtimeId)
        {
            _runtimeId = RequireNonEmpty(runtimeId, nameof(runtimeId));
        }

        /// <summary>Sets the preferred route hint.</summary>
        public CultMeshQueryContextBuilder Route(CultMeshLocalityKind kind, string? description = null)
        {
            _routeHint = new CultMeshRouteHint(kind, description);
            return this;
        }

        /// <summary>Creates the immutable query context.</summary>
        public CultMeshQueryContext Build()
        {
            return new CultMeshQueryContext(_runtimeId, _routeHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Typed derived-state query surface.
    /// </summary>
    public sealed class CultMeshQuerySurface<TParameters, TResult>
    {
        private readonly Func<TParameters, CultMeshQueryContext, Task<TResult>> _execute;
        private readonly Func<TParameters, CultMeshQueryContext, Observable<TResult>>? _watch;

        /// <summary>Creates a query surface.</summary>
        public CultMeshQuerySurface(
            string queryId,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> execute,
            Func<TParameters, CultMeshQueryContext, Observable<TResult>>? watch = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            QueryId = RequireNonEmpty(queryId, nameof(queryId));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _watch = watch;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the semantic query id.</summary>
        public string QueryId { get; }

        /// <summary>Gets the typed state sources this query depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Gets the preferred or observed route for the query.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Executes the query once.</summary>
        public Task<TResult> ExecuteAsync(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _execute(parameters, context);
        }

        /// <summary>Executes the query once for one runtime using default context.</summary>
        public Task<TResult> ExecuteAsync(TParameters parameters, string runtimeId)
        {
            return ExecuteAsync(parameters, CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Watches query results when the surface supports reactive execution.</summary>
        public Observable<TResult> Watch(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_watch == null)
            {
                throw new NotSupportedException($"Query surface '{QueryId}' does not support reactive watches.");
            }

            return _watch(parameters, context);
        }

        /// <summary>Binds this query surface to a Verse context.</summary>
        public CultMeshBoundQuerySurface<TParameters, TResult> Bind(CultMeshVerseContext context)
        {
            return new CultMeshBoundQuerySurface<TParameters, TResult>(context, this);
        }

        /// <summary>Binds this query surface to a Verse.</summary>
        public CultMeshBoundQuerySurface<TParameters, TResult> Bind(CultMeshVerse verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Bind(verse.Context);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Query surface pre-bound to a Verse so generated domain sugar can execute without context plumbing.
    /// </summary>
    public sealed class CultMeshBoundQuerySurface<TParameters, TResult>
    {
        /// <summary>Creates a Verse-bound query surface.</summary>
        public CultMeshBoundQuerySurface(
            CultMeshVerseContext context,
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Query = query ?? throw new ArgumentNullException(nameof(query));
        }

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the underlying query surface.</summary>
        public CultMeshQuerySurface<TParameters, TResult> Query { get; }

        /// <summary>Gets the semantic query id.</summary>
        public string QueryId => Query.QueryId;

        /// <summary>Gets the typed state sources this query depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources => Query.Sources;

        /// <summary>Gets the preferred or observed route for the query.</summary>
        public CultMeshRouteHint RouteHint => Query.RouteHint;

        /// <summary>Executes the query through the bound Verse.</summary>
        public Task<TResult> ExecuteAsync(TParameters parameters)
        {
            return Query.ExecuteAsync(parameters, Context.QueryContext());
        }

        /// <summary>Watches query results through the bound Verse.</summary>
        public Observable<TResult> Watch(TParameters parameters)
        {
            return Query.Watch(parameters, Context.QueryContext());
        }
    }

    /// <summary>
    /// Typed live view surface for composed client snapshots.
    /// </summary>
    public sealed class CultMeshLiveFeed<TParameters, TResult>
    {
        private readonly Func<TParameters, CultMeshQueryContext, Task<TResult>> _snapshot;
        private readonly Func<TParameters, CultMeshQueryContext, Observable<TResult>>? _watch;

        /// <summary>Creates a live feed surface.</summary>
        public CultMeshLiveFeed(
            string feedId,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> snapshot,
            Func<TParameters, CultMeshQueryContext, Observable<TResult>>? watch = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            FeedId = RequireNonEmpty(feedId, nameof(feedId));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _watch = watch;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the semantic feed id.</summary>
        public string FeedId { get; }

        /// <summary>Gets the typed state sources this live feed depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Gets the preferred or observed route for the feed.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Reads one coherent feed snapshot.</summary>
        public Task<TResult> SnapshotAsync(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _snapshot(parameters, ResolveContext(context));
        }

        /// <summary>Reads one coherent feed snapshot for one runtime using default context.</summary>
        public Task<TResult> SnapshotAsync(TParameters parameters, string runtimeId)
        {
            return SnapshotAsync(parameters, CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Watches coherent feed snapshots when the feed supports reactive execution.</summary>
        public Observable<TResult> Watch(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_watch == null)
            {
                throw new NotSupportedException($"Live feed '{FeedId}' does not support reactive watches.");
            }

            return _watch(parameters, ResolveContext(context));
        }

        /// <summary>Binds this live feed to a Verse context.</summary>
        public CultMeshBoundLiveFeed<TParameters, TResult> Bind(CultMeshVerseContext context)
        {
            return new CultMeshBoundLiveFeed<TParameters, TResult>(context, this);
        }

        /// <summary>Binds this live feed to a Verse.</summary>
        public CultMeshBoundLiveFeed<TParameters, TResult> Bind(CultMeshVerse verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Bind(verse.Context);
        }

        private CultMeshQueryContext ResolveContext(CultMeshQueryContext context)
        {
            return context.RouteHint.Kind == CultMeshLocalityKind.Automatic &&
                   RouteHint.Kind != CultMeshLocalityKind.Automatic
                ? context.WithRoute(RouteHint)
                : context;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Live feed pre-bound to a Verse so generated domain sugar can snapshot or watch without context plumbing.
    /// </summary>
    public sealed class CultMeshBoundLiveFeed<TParameters, TResult>
    {
        /// <summary>Creates a Verse-bound live feed.</summary>
        public CultMeshBoundLiveFeed(
            CultMeshVerseContext context,
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Feed = feed ?? throw new ArgumentNullException(nameof(feed));
        }

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the underlying live feed.</summary>
        public CultMeshLiveFeed<TParameters, TResult> Feed { get; }

        /// <summary>Gets the semantic feed id.</summary>
        public string FeedId => Feed.FeedId;

        /// <summary>Gets the typed state sources this live feed depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources => Feed.Sources;

        /// <summary>Gets the preferred or observed route for the feed.</summary>
        public CultMeshRouteHint RouteHint => Feed.RouteHint;

        /// <summary>Reads one coherent feed snapshot through the bound Verse.</summary>
        public Task<TResult> SnapshotAsync(TParameters parameters)
        {
            return Feed.SnapshotAsync(parameters, Context.QueryContext());
        }

        /// <summary>Watches coherent feed snapshots through the bound Verse.</summary>
        public Observable<TResult> Watch(TParameters parameters)
        {
            return Feed.Watch(parameters, Context.QueryContext());
        }
    }

    /// <summary>
    /// Empty parameter object for document handles that expose one typed document as a live feed.
    /// </summary>
    public readonly struct CultMeshDocumentQueryParameters
    {
        /// <summary>Gets the singleton empty document query.</summary>
        public static CultMeshDocumentQueryParameters Empty { get; } = new();
    }

    /// <summary>
    /// Inspectable typed document handle exposed by CultMesh.
    /// </summary>
    public interface ICultMeshDocumentHandle
    {
        /// <summary>Gets the CLR document type presented by this handle.</summary>
        Type DocumentType { get; }

        /// <summary>Gets the semantic document id.</summary>
        string DocumentId { get; }

        /// <summary>Gets the stable CultCache schema name.</summary>
        string SchemaName { get; }

        /// <summary>Gets the stable CultCache schema version.</summary>
        string SchemaVersion { get; }

        /// <summary>Gets the content-derived schema identifier.</summary>
        string SchemaId { get; }

        /// <summary>Gets the preferred or observed route for document access.</summary>
        CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this document handle depends on, when known.</summary>
        IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Gets whether this handle can replace the underlying document value.</summary>
        bool CanReplace { get; }

        /// <summary>Gets whether this handle can submit a client prediction for the underlying document value.</summary>
        bool CanSubmitPrediction { get; }

        /// <summary>Gets whether this handle can accept a transparent document mutation.</summary>
        bool CanSet { get; }

        /// <summary>Creates a same-schema alias presentation for another CLR document type.</summary>
        CultMeshDocumentHandle<TAlias> AsSchemaAlias<TAlias>() where TAlias : class;
    }

    /// <summary>
    /// Typed reactive document handle with schema-aware alias conversion.
    /// </summary>
    public sealed class CultMeshDocumentHandle<TDocument> : ICultMeshDocumentHandle
        where TDocument : class
    {
        private static readonly CultDocumentDescriptor Descriptor =
            CultDocumentRegistry.Shared.GetRequired<TDocument>();

        private readonly CultMeshBoundLiveFeed<CultMeshDocumentQueryParameters, TDocument> _feed;
        private readonly Func<TDocument, Task>? _replace;
        private readonly Func<TDocument, Task>? _submitPrediction;

        /// <summary>Creates a document handle from a Verse-bound live feed.</summary>
        public CultMeshDocumentHandle(
            CultMeshBoundLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed,
            Func<TDocument, Task>? replace = null,
            Func<TDocument, Task>? submitPrediction = null)
        {
            _feed = feed ?? throw new ArgumentNullException(nameof(feed));
            _replace = replace;
            _submitPrediction = submitPrediction;
        }

        /// <summary>Gets the semantic document id.</summary>
        public string DocumentId => _feed.FeedId;

        /// <summary>Gets the CLR document type presented by this handle.</summary>
        public Type DocumentType => typeof(TDocument);

        /// <summary>Gets the stable CultCache schema name.</summary>
        public string SchemaName => Descriptor.SchemaName;

        /// <summary>Gets the stable CultCache schema version.</summary>
        public string SchemaVersion => Descriptor.SchemaVersion;

        /// <summary>Gets the content-derived schema identifier.</summary>
        public string SchemaId => Descriptor.SchemaId;

        /// <summary>Gets the preferred or observed route for document access.</summary>
        public CultMeshRouteHint RouteHint => _feed.RouteHint;

        /// <summary>Gets typed state sources this document handle depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources => _feed.Sources;

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context => _feed.Context;

        /// <summary>Gets the underlying live feed used by this document handle.</summary>
        public CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> Feed => _feed.Feed;

        /// <summary>Gets whether this handle can replace the underlying document value.</summary>
        public bool CanReplace => _replace != null;

        /// <summary>Gets whether this handle can submit a client prediction for the underlying document value.</summary>
        public bool CanSubmitPrediction => _submitPrediction != null;

        /// <summary>Gets whether this handle can accept a transparent document mutation.</summary>
        public bool CanSet => CanSubmitPrediction || CanReplace;

        /// <summary>Reads one coherent document snapshot.</summary>
        public Task<TDocument> LatestAsync()
        {
            return _feed.SnapshotAsync(CultMeshDocumentQueryParameters.Empty);
        }

        /// <summary>Reads the latest document snapshot synchronously for host APIs that cannot be async.</summary>
        public TDocument Latest()
        {
            return LatestAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>Watches coherent document snapshots.</summary>
        public Observable<TDocument> Watch()
        {
            return _feed.Watch(CultMeshDocumentQueryParameters.Empty);
        }

        /// <summary>Subscribes to coherent document snapshots.</summary>
        public IDisposable Watch(Action<TDocument> onNext)
        {
            if (onNext == null) throw new ArgumentNullException(nameof(onNext));
            return Watch().Subscribe(onNext);
        }

        /// <summary>Replaces the underlying document when this handle is backed by mutable state.</summary>
        public Task ReplaceAsync(TDocument value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (_replace == null)
            {
                throw new NotSupportedException($"Document handle '{DocumentId}' is read-only.");
            }

            return _replace(value);
        }

        /// <summary>Submits a locally predicted document value when this handle is backed by client-authoritative state.</summary>
        public Task SubmitPredictionAsync(TDocument value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (_submitPrediction == null)
            {
                throw new NotSupportedException($"Document handle '{DocumentId}' does not accept client predictions.");
            }

            return _submitPrediction(value);
        }

        /// <summary>
        /// Sets the document value through the configured authority shape.
        /// Prediction-backed documents publish a prediction; mutable documents replace authoritatively.
        /// </summary>
        public Task SetAsync(TDocument value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (_submitPrediction != null)
                return _submitPrediction(value);
            if (_replace != null)
                return _replace(value);

            throw new NotSupportedException($"Document handle '{DocumentId}' does not accept mutations.");
        }

        /// <summary>Reads, updates, and sets the document value through the configured authority shape.</summary>
        public async Task<TDocument> UpdateAsync(Func<TDocument, TDocument> update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            var next = update(await LatestAsync().ConfigureAwait(false));
            await SetAsync(next).ConfigureAwait(false);
            return next;
        }

        /// <summary>Reads, updates, and sets the document value through the configured authority shape.</summary>
        public async Task<TDocument> UpdateAsync(Func<TDocument, Task<TDocument>> update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            var next = await update(await LatestAsync().ConfigureAwait(false)).ConfigureAwait(false);
            await SetAsync(next).ConfigureAwait(false);
            return next;
        }

        /// <summary>
        /// Creates a managed reactive document mirror whose local edits can be coalesced into one prediction or replacement.
        /// </summary>
        public async Task<CultMeshReactiveDocument<TDocument>> ReactiveAsync(
            CultMeshReactiveDocumentOptions? options = null)
        {
            var current = CloneDocument(await LatestAsync().ConfigureAwait(false));
            var reactive = new CultMeshReactiveDocument<TDocument>(this, current, options);
            reactive.Start();
            return reactive;
        }

        /// <summary>
        /// Creates a managed reactive document mirror synchronously for host APIs that cannot be async.
        /// </summary>
        public CultMeshReactiveDocument<TDocument> Reactive(CultMeshReactiveDocumentOptions? options = null)
        {
            return ReactiveAsync(options).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>Creates a same-schema alias presentation for another CLR document type.</summary>
        public CultMeshDocumentHandle<TAlias> AsSchemaAlias<TAlias>() where TAlias : class
        {
            var aliasDescriptor = CultDocumentRegistry.Shared.GetRequired<TAlias>();
            if (!string.Equals(Descriptor.SchemaName, aliasDescriptor.SchemaName, StringComparison.Ordinal) ||
                !string.Equals(Descriptor.SchemaVersion, aliasDescriptor.SchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Document type {typeof(TAlias).FullName} uses schema '{aliasDescriptor.SchemaName}' " +
                    $"version '{aliasDescriptor.SchemaVersion}', but handle '{DocumentId}' exposes " +
                    $"schema '{SchemaName}' version '{SchemaVersion}'.");
            }

            var aliasFeed = new CultMeshLiveFeed<CultMeshDocumentQueryParameters, TAlias>(
                DocumentId,
                async (parameters, context) => ConvertDocument<TDocument, TAlias>(
                    await Feed.SnapshotAsync(parameters, context).ConfigureAwait(false)),
                (parameters, context) => Feed
                    .Watch(parameters, context)
                    .Select(ConvertDocument<TDocument, TAlias>),
                Sources,
                RouteHint);

            Func<TAlias, Task>? replace = _replace == null
                ? null
                : value => _replace(ConvertDocument<TAlias, TDocument>(value));
            Func<TAlias, Task>? submitPrediction = _submitPrediction == null
                ? null
                : value => _submitPrediction(ConvertDocument<TAlias, TDocument>(value));

            return new CultMeshDocumentHandle<TAlias>(aliasFeed.Bind(Context), replace, submitPrediction);
        }

        internal static TDocumentValue CloneDocument<TDocumentValue>(TDocumentValue document)
            where TDocumentValue : class
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TDocumentValue));
            return (TDocumentValue)CultDocumentMessagePackSerialization.DeserializeUntyped(
                typeof(TDocumentValue),
                payload);
        }

        internal static TTarget ConvertDocument<TSource, TTarget>(TSource document)
            where TSource : class
            where TTarget : class
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document is TTarget alreadyTyped)
            {
                return alreadyTyped;
            }

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TSource));
            return (TTarget)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(TTarget), payload);
        }
    }

    /// <summary>
    /// Configures a managed CultMesh reactive document mirror.
    /// </summary>
    public sealed class CultMeshReactiveDocumentOptions
    {
        /// <summary>
        /// Gets or sets the write coalescing window. The default approximates a frame boundary for non-Unity hosts.
        /// </summary>
        public TimeSpan FlushDelay { get; set; } = TimeSpan.FromMilliseconds(16);

        /// <summary>Gets or sets whether direct edits to Current should be detected and flushed automatically.</summary>
        public bool DetectLocalChanges { get; set; } = true;

        /// <summary>Gets or sets whether canonical snapshots replace dirty local predictions immediately.</summary>
        public bool ReplaceDirtyCurrentOnCanonicalSnapshot { get; set; }
    }

    /// <summary>
    /// Captures a canonical snapshot that arrived while the local reactive document had an outstanding prediction.
    /// </summary>
    public sealed class CultMeshReactiveDocumentReconciliation<TDocument>
        where TDocument : class
    {
        internal CultMeshReactiveDocumentReconciliation(
            TDocument canonical,
            TDocument predicted,
            IReadOnlyDictionary<string, object?> delta,
            int version,
            DateTimeOffset receivedAt)
        {
            Canonical = canonical;
            Predicted = predicted;
            Delta = delta ?? throw new ArgumentNullException(nameof(delta));
            Version = version;
            ReceivedAt = receivedAt;
        }

        /// <summary>Gets the monotonically increasing reconciliation version for this reactive document.</summary>
        public int Version { get; }

        /// <summary>Gets the authoritative canonical document snapshot.</summary>
        public TDocument Canonical { get; }

        /// <summary>Gets the locally predicted document snapshot that was active when the canonical value arrived.</summary>
        public TDocument Predicted { get; }

        /// <summary>
        /// Gets the predicted-vs-canonical member delta. Numeric members store predicted minus canonical;
        /// non-numeric members store the predicted value.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Delta { get; }

        /// <summary>Gets when the canonical snapshot was received.</summary>
        public DateTimeOffset ReceivedAt { get; }
    }

    /// <summary>
    /// Managed typed document mirror that keeps an editable current value and coalesces local edits into CultMesh mutations.
    /// </summary>
    public sealed class CultMeshReactiveDocument<TDocument> : IDisposable
        where TDocument : class
    {
        private readonly CultMeshDocumentHandle<TDocument> _document;
        private readonly CultMeshReactiveDocumentOptions _options;
        private readonly object _gate = new();
        private IDisposable? _subscription;
        private Timer? _flushTimer;
        private Timer? _changeDetectionTimer;
        private byte[] _lastCleanSnapshot;
        private bool _dirty;
        private bool _flushQueued;
        private bool _flushing;
        private bool _disposed;
        private int _reconciliationVersion;

        internal CultMeshReactiveDocument(
            CultMeshDocumentHandle<TDocument> document,
            TDocument current,
            CultMeshReactiveDocumentOptions? options)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            Current = current ?? throw new ArgumentNullException(nameof(current));
            _options = options ?? new CultMeshReactiveDocumentOptions();
            _lastCleanSnapshot = SerializeDocument(Current);
        }

        /// <summary>Gets the underlying document handle.</summary>
        public CultMeshDocumentHandle<TDocument> Document => _document;

        /// <summary>Gets the editable local document value.</summary>
        public TDocument Current { get; private set; }

        /// <summary>Gets whether local edits are waiting to be sent.</summary>
        public bool IsDirty
        {
            get
            {
                lock (_gate)
                    return _dirty;
            }
        }

        /// <summary>Gets the most recent reconciliation snapshot, when a canonical value arrived during local prediction.</summary>
        public CultMeshReactiveDocumentReconciliation<TDocument>? Reconciliation { get; private set; }

        internal void Start()
        {
            _subscription = _document.Watch(ApplyCanonicalSnapshot);
            if (_options.DetectLocalChanges && _document.CanSet)
            {
                var interval = _options.FlushDelay <= TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(1)
                    : _options.FlushDelay;
                _changeDetectionTimer = new Timer(_ => _ = Task.Run(DetectLocalChangesAsync));
                _changeDetectionTimer.Change(interval, interval);
            }
        }

        /// <summary>Marks the current value dirty and schedules a coalesced prediction or replacement.</summary>
        public void MarkDirty()
        {
            ThrowIfDisposed();
            lock (_gate)
            {
                _dirty = true;
                ScheduleFlushLocked();
            }
        }

        /// <summary>Mutates the current value and schedules a coalesced prediction or replacement.</summary>
        public TDocument Update(Action<TDocument> update)
        {
            if (update == null) throw new ArgumentNullException(nameof(update));
            ThrowIfDisposed();
            lock (_gate)
            {
                update(Current);
                _dirty = true;
                ScheduleFlushLocked();
                return Current;
            }
        }

        /// <summary>Replaces the current local value and schedules a coalesced prediction or replacement.</summary>
        public TDocument SetCurrent(TDocument value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            ThrowIfDisposed();
            lock (_gate)
            {
                Current = CultMeshDocumentHandle<TDocument>.CloneDocument(value);
                _dirty = true;
                ScheduleFlushLocked();
                return Current;
            }
        }

        /// <summary>Reads a fresh canonical snapshot and adopts it as the current value.</summary>
        public async Task<TDocument> RefreshAsync()
        {
            ThrowIfDisposed();
            var latest = CultMeshDocumentHandle<TDocument>.CloneDocument(
                await _document.LatestAsync().ConfigureAwait(false));
            lock (_gate)
            {
                Current = latest;
                _lastCleanSnapshot = SerializeDocument(Current);
                _dirty = false;
                Reconciliation = null;
                return Current;
            }
        }

        /// <summary>Immediately sends the latest local dirty value, if any, through the document authority shape.</summary>
        public async Task FlushAsync()
        {
            TDocument predicted;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_dirty && _document.CanSet)
                    DetectLocalChangesLocked();
                if (!_dirty)
                    return;
                if (_flushing)
                {
                    _flushQueued = true;
                    return;
                }

                _flushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _flushing = true;
                _dirty = false;
                predicted = CultMeshDocumentHandle<TDocument>.CloneDocument(Current);
                _lastCleanSnapshot = SerializeDocument(predicted);
            }

            try
            {
                await _document.SetAsync(predicted).ConfigureAwait(false);
            }
            finally
            {
                var shouldFlushAgain = false;
                lock (_gate)
                {
                    _flushing = false;
                    if (_document.CanSet)
                        DetectLocalChangesLocked();
                    shouldFlushAgain = _flushQueued || _dirty;
                    _flushQueued = false;
                }

                if (shouldFlushAgain)
                    await FlushAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Clears the most recent reconciliation metadata.</summary>
        public void ClearReconciliation()
        {
            ThrowIfDisposed();
            Reconciliation = null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _subscription?.Dispose();
            _flushTimer?.Dispose();
            _changeDetectionTimer?.Dispose();
        }

        private void ApplyCanonicalSnapshot(TDocument canonical)
        {
            if (canonical == null)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;

                var nextCanonical = CultMeshDocumentHandle<TDocument>.CloneDocument(canonical);
                if (_dirty || _flushing)
                {
                    var predicted = CultMeshDocumentHandle<TDocument>.CloneDocument(Current);
                    var delta = CreateReconciliationDelta(predicted, nextCanonical);
                    if (delta.Count == 0)
                    {
                        Reconciliation = null;
                    }
                    else
                    {
                        Reconciliation = new CultMeshReactiveDocumentReconciliation<TDocument>(
                            nextCanonical,
                            predicted,
                            delta,
                            ++_reconciliationVersion,
                            DateTimeOffset.UtcNow);
                    }

                    if (!_options.ReplaceDirtyCurrentOnCanonicalSnapshot)
                        return;
                }

                Current = nextCanonical;
                _lastCleanSnapshot = SerializeDocument(Current);
                Reconciliation = null;
            }
        }

        private Task DetectLocalChangesAsync()
        {
            lock (_gate)
            {
                if (_disposed || !_document.CanSet || _dirty || _flushing)
                    return Task.CompletedTask;
                DetectLocalChangesLocked();
                if (_dirty)
                    ScheduleFlushLocked();
            }

            return Task.CompletedTask;
        }

        private void DetectLocalChangesLocked()
        {
            var currentSnapshot = SerializeDocument(Current);
            if (_lastCleanSnapshot.SequenceEqual(currentSnapshot))
                return;
            _dirty = true;
        }

        private void ScheduleFlushLocked()
        {
            if (_options.FlushDelay <= TimeSpan.Zero)
            {
                _ = Task.Run(FlushAsync);
                return;
            }

            _flushTimer ??= new Timer(_ => _ = Task.Run(FlushAsync));
            _flushTimer.Change(_options.FlushDelay, Timeout.InfiniteTimeSpan);
        }

        private static byte[] SerializeDocument(TDocument document)
        {
            return CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TDocument));
        }

        private static IReadOnlyDictionary<string, object?> CreateReconciliationDelta(
            TDocument predicted,
            TDocument canonical)
        {
            var delta = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var member in GetComparableMembers(typeof(TDocument)))
            {
                var predictedValue = member.GetValue(predicted);
                var canonicalValue = member.GetValue(canonical);
                if (CultMeshValuesEqual(predictedValue, canonicalValue))
                    continue;

                delta[member.Name] = TryCreateNumericDelta(predictedValue, canonicalValue, out var numericDelta)
                    ? numericDelta
                    : predictedValue;
            }

            return delta;
        }

        private static IEnumerable<CultMeshComparableMember> GetComparableMembers(Type documentType)
        {
            foreach (var property in documentType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetMethod == null || property.GetIndexParameters().Length != 0)
                    continue;
                yield return new CultMeshComparableMember(property.Name, property.GetValue);
            }

            foreach (var field in documentType.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                yield return new CultMeshComparableMember(field.Name, field.GetValue);
            }
        }

        private static bool TryCreateNumericDelta(object? predicted, object? canonical, out object? delta)
        {
            delta = null;
            if (predicted == null || canonical == null)
                return false;
            var predictedType = Nullable.GetUnderlyingType(predicted.GetType()) ?? predicted.GetType();
            var canonicalType = Nullable.GetUnderlyingType(canonical.GetType()) ?? canonical.GetType();
            if (!IsNumericType(predictedType) || !IsNumericType(canonicalType))
                return false;

            delta = Convert.ToDouble(predicted, CultureInfo.InvariantCulture) -
                Convert.ToDouble(canonical, CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsNumericType(Type type)
        {
            if (type.IsEnum)
                return false;
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Byte or
                TypeCode.SByte or
                TypeCode.UInt16 or
                TypeCode.UInt32 or
                TypeCode.UInt64 or
                TypeCode.Int16 or
                TypeCode.Int32 or
                TypeCode.Int64 or
                TypeCode.Decimal or
                TypeCode.Double or
                TypeCode.Single => true,
                _ => false
            };
        }

        private static bool CultMeshValuesEqual(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            if (left.Equals(right))
                return true;
            if (left is string || right is string)
                return false;
            if (left is IEnumerable leftEnumerable && right is IEnumerable rightEnumerable)
                return leftEnumerable.Cast<object?>().SequenceEqual(rightEnumerable.Cast<object?>());
            return false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CultMeshReactiveDocument<TDocument>));
        }

        private readonly struct CultMeshComparableMember
        {
            public CultMeshComparableMember(string name, Func<object, object?> getValue)
            {
                Name = name;
                GetValue = getValue;
            }

            public string Name { get; }

            public Func<object, object?> GetValue { get; }
        }
    }

    /// <summary>
    /// Schema-aware lookup table for typed CultMesh document handles.
    /// </summary>
    public sealed class CultMeshDocumentCatalog
    {
        private readonly IReadOnlyList<ICultMeshDocumentHandle> _documents;
        private readonly IReadOnlyDictionary<Type, ICultMeshDocumentHandle> _documentsByType;
        private readonly IReadOnlyDictionary<string, ICultMeshDocumentHandle> _documentsBySchema;

        /// <summary>Creates a document catalog from known handles.</summary>
        public CultMeshDocumentCatalog(IEnumerable<ICultMeshDocumentHandle> documents)
        {
            if (documents == null) throw new ArgumentNullException(nameof(documents));

            var documentList = documents
                .Where(document => document != null)
                .ToArray();
            var byType = new Dictionary<Type, ICultMeshDocumentHandle>();
            var bySchema = new Dictionary<string, ICultMeshDocumentHandle>(StringComparer.Ordinal);
            foreach (var document in documentList)
            {
                byType[document.DocumentType] = document;
                if (!string.IsNullOrWhiteSpace(document.SchemaId))
                    bySchema[document.SchemaId] = document;
                if (!string.IsNullOrWhiteSpace(document.SchemaVersion))
                    bySchema[document.SchemaVersion] = document;
                if (!string.IsNullOrWhiteSpace(document.SchemaName))
                    bySchema[document.SchemaName] = document;
            }

            _documents = documentList;
            _documentsByType = byType;
            _documentsBySchema = bySchema;
        }

        /// <summary>Gets the handles in this catalog.</summary>
        public IReadOnlyList<ICultMeshDocumentHandle> Documents => _documents;

        /// <summary>Looks up a document handle by schema id, shared schema name, or schema version.</summary>
        public bool TryGetDocumentBySchema(
            string schema,
            out ICultMeshDocumentHandle document)
        {
            if (!string.IsNullOrWhiteSpace(schema) &&
                _documentsBySchema.TryGetValue(schema, out document!))
            {
                return true;
            }

            document = null!;
            return false;
        }

        /// <summary>Looks up a document handle by schema id, shared schema name, or schema version.</summary>
        public ICultMeshDocumentHandle DocumentBySchema(string schema)
        {
            if (TryGetDocumentBySchema(schema, out var document))
                return document;

            throw new NotSupportedException(
                $"CultMesh document catalog does not expose a document for schema '{schema}'.");
        }

        /// <summary>Looks up a typed document handle by CLR type or same-schema alias.</summary>
        public bool TryGetDocument<TDocument>(
            out CultMeshDocumentHandle<TDocument> document)
            where TDocument : class
        {
            if (_documentsByType.TryGetValue(typeof(TDocument), out var untypedDocument) &&
                untypedDocument is CultMeshDocumentHandle<TDocument> typedDocument)
            {
                document = typedDocument;
                return true;
            }

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            if (_documentsBySchema.TryGetValue(descriptor.SchemaVersion, out var schemaDocument) ||
                _documentsBySchema.TryGetValue(descriptor.SchemaName, out schemaDocument!))
            {
                if (schemaDocument is CultMeshDocumentHandle<TDocument> schemaTypedDocument)
                {
                    document = schemaTypedDocument;
                    return true;
                }

                document = schemaDocument.AsSchemaAlias<TDocument>();
                return true;
            }

            document = null!;
            return false;
        }

        /// <summary>Looks up a typed document handle by CLR type or same-schema alias.</summary>
        public CultMeshDocumentHandle<TDocument> Document<TDocument>()
            where TDocument : class
        {
            if (TryGetDocument<TDocument>(out var document))
                return document;

            throw new NotSupportedException(
                $"CultMesh document catalog does not expose a document for {typeof(TDocument).FullName}.");
        }

        /// <summary>Reads one typed document snapshot by CLR type or same-schema alias.</summary>
        public Task<TDocument> LatestAsync<TDocument>()
            where TDocument : class
        {
            return Document<TDocument>().LatestAsync();
        }

        /// <summary>Reads one typed document synchronously by CLR type or same-schema alias.</summary>
        public TDocument Latest<TDocument>()
            where TDocument : class
        {
            return Document<TDocument>().Latest();
        }

        /// <summary>Gets whether one typed document can be replaced by CLR type or same-schema alias.</summary>
        public bool CanReplace<TDocument>()
            where TDocument : class
        {
            return TryGetDocument<TDocument>(out var document) && document.CanReplace;
        }

        /// <summary>Replaces one typed document by CLR type or same-schema alias.</summary>
        public Task ReplaceAsync<TDocument>(TDocument value)
            where TDocument : class
        {
            return Document<TDocument>().ReplaceAsync(value);
        }

        /// <summary>Gets whether one typed document can submit client predictions by CLR type or same-schema alias.</summary>
        public bool CanSubmitPrediction<TDocument>()
            where TDocument : class
        {
            return TryGetDocument<TDocument>(out var document) && document.CanSubmitPrediction;
        }

        /// <summary>Gets whether one typed document can accept transparent mutations by CLR type or same-schema alias.</summary>
        public bool CanSet<TDocument>()
            where TDocument : class
        {
            return TryGetDocument<TDocument>(out var document) && document.CanSet;
        }

        /// <summary>Submits a client prediction for one typed document by CLR type or same-schema alias.</summary>
        public Task SubmitPredictionAsync<TDocument>(TDocument value)
            where TDocument : class
        {
            return Document<TDocument>().SubmitPredictionAsync(value);
        }

        /// <summary>Sets one typed document through its configured authority shape by CLR type or same-schema alias.</summary>
        public Task SetAsync<TDocument>(TDocument value)
            where TDocument : class
        {
            return Document<TDocument>().SetAsync(value);
        }

        /// <summary>Reads, updates, and sets one typed document through its configured authority shape.</summary>
        public Task<TDocument> UpdateAsync<TDocument>(Func<TDocument, TDocument> update)
            where TDocument : class
        {
            return Document<TDocument>().UpdateAsync(update);
        }

        /// <summary>Reads, updates, and sets one typed document through its configured authority shape.</summary>
        public Task<TDocument> UpdateAsync<TDocument>(Func<TDocument, Task<TDocument>> update)
            where TDocument : class
        {
            return Document<TDocument>().UpdateAsync(update);
        }

        /// <summary>Watches one typed document by CLR type or same-schema alias.</summary>
        public Observable<TDocument> Watch<TDocument>()
            where TDocument : class
        {
            return Document<TDocument>().Watch();
        }

        /// <summary>Subscribes to one typed document by CLR type or same-schema alias.</summary>
        public IDisposable Watch<TDocument>(Action<TDocument> onNext)
            where TDocument : class
        {
            return Document<TDocument>().Watch(onNext);
        }

        /// <summary>Creates a managed reactive document mirror by CLR type or same-schema alias.</summary>
        public Task<CultMeshReactiveDocument<TDocument>> ReactiveAsync<TDocument>(
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            return Document<TDocument>().ReactiveAsync(options);
        }

        /// <summary>Creates a managed reactive document mirror synchronously by CLR type or same-schema alias.</summary>
        public CultMeshReactiveDocument<TDocument> Reactive<TDocument>(
            CultMeshReactiveDocumentOptions? options = null)
            where TDocument : class
        {
            return Document<TDocument>().Reactive(options);
        }
    }

    /// <summary>
    /// Inspectable typed collection handle exposed by CultMesh.
    /// </summary>
    public interface ICultMeshCollectionHandle
    {
        /// <summary>Gets the CLR document type presented by this collection.</summary>
        Type DocumentType { get; }

        /// <summary>Gets the semantic collection id.</summary>
        string CollectionId { get; }

        /// <summary>Gets the stable CultCache schema name.</summary>
        string SchemaName { get; }

        /// <summary>Gets the stable CultCache schema version.</summary>
        string SchemaVersion { get; }

        /// <summary>Gets the content-derived schema identifier.</summary>
        string SchemaId { get; }

        /// <summary>Gets the preferred or observed route for collection access.</summary>
        CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this collection handle depends on, when known.</summary>
        IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Creates a same-schema alias presentation for another CLR document type.</summary>
        CultMeshCollectionHandle<TAlias> AsSchemaAlias<TAlias>() where TAlias : class;
    }

    /// <summary>
    /// Schema-aware lookup table for typed CultMesh collection handles.
    /// </summary>
    public sealed class CultMeshCollectionCatalog
    {
        private readonly IReadOnlyList<ICultMeshCollectionHandle> _collections;
        private readonly IReadOnlyDictionary<Type, ICultMeshCollectionHandle> _collectionsByType;
        private readonly IReadOnlyDictionary<string, ICultMeshCollectionHandle> _collectionsBySchema;

        /// <summary>Creates a collection catalog from known handles.</summary>
        public CultMeshCollectionCatalog(IEnumerable<ICultMeshCollectionHandle> collections)
        {
            if (collections == null) throw new ArgumentNullException(nameof(collections));

            var collectionList = collections
                .Where(collection => collection != null)
                .ToArray();
            var byType = new Dictionary<Type, ICultMeshCollectionHandle>();
            var bySchema = new Dictionary<string, ICultMeshCollectionHandle>(StringComparer.Ordinal);
            foreach (var collection in collectionList)
            {
                byType[collection.DocumentType] = collection;
                if (!string.IsNullOrWhiteSpace(collection.SchemaId))
                    bySchema[collection.SchemaId] = collection;
                if (!string.IsNullOrWhiteSpace(collection.SchemaVersion))
                    bySchema[collection.SchemaVersion] = collection;
                if (!string.IsNullOrWhiteSpace(collection.SchemaName))
                    bySchema[collection.SchemaName] = collection;
            }

            _collections = collectionList;
            _collectionsByType = byType;
            _collectionsBySchema = bySchema;
        }

        /// <summary>Gets the handles in this catalog.</summary>
        public IReadOnlyList<ICultMeshCollectionHandle> Collections => _collections;

        /// <summary>Looks up a collection handle by schema id, shared schema name, or schema version.</summary>
        public bool TryGetCollectionBySchema(
            string schema,
            out ICultMeshCollectionHandle collection)
        {
            if (!string.IsNullOrWhiteSpace(schema) &&
                _collectionsBySchema.TryGetValue(schema, out collection!))
            {
                return true;
            }

            collection = null!;
            return false;
        }

        /// <summary>Looks up a collection handle by schema id, shared schema name, or schema version.</summary>
        public ICultMeshCollectionHandle CollectionBySchema(string schema)
        {
            if (TryGetCollectionBySchema(schema, out var collection))
                return collection;

            throw new NotSupportedException(
                $"CultMesh collection catalog does not expose a collection for schema '{schema}'.");
        }

        /// <summary>Looks up a typed collection handle by CLR type or same-schema alias.</summary>
        public bool TryGetCollection<TDocument>(
            out CultMeshCollectionHandle<TDocument> collection)
            where TDocument : class
        {
            if (_collectionsByType.TryGetValue(typeof(TDocument), out var untypedCollection) &&
                untypedCollection is CultMeshCollectionHandle<TDocument> typedCollection)
            {
                collection = typedCollection;
                return true;
            }

            var descriptor = CultDocumentRegistry.Shared.GetRequired<TDocument>();
            if (_collectionsBySchema.TryGetValue(descriptor.SchemaId, out var schemaCollection) ||
                _collectionsBySchema.TryGetValue(descriptor.SchemaVersion, out schemaCollection!) ||
                _collectionsBySchema.TryGetValue(descriptor.SchemaName, out schemaCollection!))
            {
                if (schemaCollection is CultMeshCollectionHandle<TDocument> schemaTypedCollection)
                {
                    collection = schemaTypedCollection;
                    return true;
                }

                collection = schemaCollection.AsSchemaAlias<TDocument>();
                return true;
            }

            collection = null!;
            return false;
        }

        /// <summary>Looks up a typed collection handle by CLR type or same-schema alias.</summary>
        public CultMeshCollectionHandle<TDocument> Collection<TDocument>()
            where TDocument : class
        {
            if (TryGetCollection<TDocument>(out var collection))
                return collection;

            throw new NotSupportedException(
                $"CultMesh collection catalog does not expose a collection for {typeof(TDocument).FullName}.");
        }

        /// <summary>Reads one typed collection snapshot by CLR type or same-schema alias.</summary>
        public Task<IReadOnlyList<TDocument>> LatestAsync<TDocument>()
            where TDocument : class
        {
            return Collection<TDocument>().LatestAsync();
        }

        /// <summary>Watches one typed collection by CLR type or same-schema alias.</summary>
        public Observable<CultMeshCollectionChange<TDocument>> WatchChanges<TDocument>()
            where TDocument : class
        {
            return Collection<TDocument>().WatchChanges();
        }

        /// <summary>Subscribes to one typed collection by CLR type or same-schema alias.</summary>
        public IDisposable WatchChanges<TDocument>(Action<CultMeshCollectionChange<TDocument>> onNext)
            where TDocument : class
        {
            return Collection<TDocument>().WatchChanges(onNext);
        }
    }

    /// <summary>
    /// Describes how a CultMesh collection document changed.
    /// </summary>
    public enum CultMeshCollectionChangeKind
    {
        /// <summary>A document was added.</summary>
        Added,
        /// <summary>A document was updated.</summary>
        Updated,
        /// <summary>A document was removed.</summary>
        Removed,
        /// <summary>A local prediction was published before authority accepted it.</summary>
        Predicted,
        /// <summary>A predicted document was reconciled with authority.</summary>
        Reconciled,
        /// <summary>A document was accepted through schema migration.</summary>
        SchemaMigrated,
        /// <summary>A document change was rejected.</summary>
        Rejected
    }

    /// <summary>
    /// One typed document change observed through a CultMesh collection handle.
    /// </summary>
    public sealed class CultMeshCollectionChange<TDocument>
        where TDocument : class
    {
        /// <summary>Creates a collection change.</summary>
        public CultMeshCollectionChange(
            CultMeshCollectionChangeKind kind,
            CultRecordKey key,
            string schemaId,
            TDocument? document,
            TDocument? previousDocument,
            string? message = null)
        {
            Kind = kind;
            Key = key;
            SchemaId = schemaId ?? "";
            Document = document;
            PreviousDocument = previousDocument;
            Message = message;
        }

        /// <summary>Gets the change kind.</summary>
        public CultMeshCollectionChangeKind Kind { get; }

        /// <summary>Gets the changed record key.</summary>
        public CultRecordKey Key { get; }

        /// <summary>Gets the schema id of the changed document.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the current document, when present.</summary>
        public TDocument? Document { get; }

        /// <summary>Gets the previous document, when present.</summary>
        public TDocument? PreviousDocument { get; }

        /// <summary>Gets an optional rejection or diagnostic message.</summary>
        public string? Message { get; }
    }

    /// <summary>
    /// Typed reactive collection handle with schema-aware alias conversion.
    /// </summary>
    public sealed class CultMeshCollectionHandle<TDocument> : ICultMeshCollectionHandle
        where TDocument : class
    {
        private static readonly CultDocumentDescriptor Descriptor =
            CultDocumentRegistry.Shared.GetRequired<TDocument>();

        private readonly Func<Task<IReadOnlyList<TDocument>>> _latest;
        private readonly Func<Observable<CultMeshCollectionChange<TDocument>>> _watchChanges;

        /// <summary>Creates a collection handle.</summary>
        public CultMeshCollectionHandle(
            string collectionId,
            Func<Task<IReadOnlyList<TDocument>>> latest,
            Func<Observable<CultMeshCollectionChange<TDocument>>> watchChanges,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            CollectionId = string.IsNullOrWhiteSpace(collectionId)
                ? throw new ArgumentException("Value must be non-empty.", nameof(collectionId))
                : collectionId;
            _latest = latest ?? throw new ArgumentNullException(nameof(latest));
            _watchChanges = watchChanges ?? throw new ArgumentNullException(nameof(watchChanges));
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the semantic collection id.</summary>
        public string CollectionId { get; }

        /// <summary>Gets the CLR document type presented by this collection.</summary>
        public Type DocumentType => typeof(TDocument);

        /// <summary>Gets the stable CultCache schema name.</summary>
        public string SchemaName => Descriptor.SchemaName;

        /// <summary>Gets the stable CultCache schema version.</summary>
        public string SchemaVersion => Descriptor.SchemaVersion;

        /// <summary>Gets the content-derived schema identifier.</summary>
        public string SchemaId => Descriptor.SchemaId;

        /// <summary>Gets the preferred or observed route for collection access.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this collection handle depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Reads one coherent collection snapshot.</summary>
        public Task<IReadOnlyList<TDocument>> LatestAsync()
        {
            return _latest();
        }

        /// <summary>Watches typed collection changes.</summary>
        public Observable<CultMeshCollectionChange<TDocument>> WatchChanges()
        {
            return _watchChanges();
        }

        /// <summary>Subscribes to typed collection changes.</summary>
        public IDisposable WatchChanges(Action<CultMeshCollectionChange<TDocument>> onNext)
        {
            if (onNext == null) throw new ArgumentNullException(nameof(onNext));
            return WatchChanges().Subscribe(onNext);
        }

        /// <summary>Creates a same-schema alias presentation for another CLR document type.</summary>
        public CultMeshCollectionHandle<TAlias> AsSchemaAlias<TAlias>() where TAlias : class
        {
            var aliasDescriptor = CultDocumentRegistry.Shared.GetRequired<TAlias>();
            if (!string.Equals(Descriptor.SchemaName, aliasDescriptor.SchemaName, StringComparison.Ordinal) ||
                !string.Equals(Descriptor.SchemaVersion, aliasDescriptor.SchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Document type {typeof(TAlias).FullName} uses schema '{aliasDescriptor.SchemaName}' " +
                    $"version '{aliasDescriptor.SchemaVersion}', but collection '{CollectionId}' exposes " +
                    $"schema '{SchemaName}' version '{SchemaVersion}'.");
            }

            return new CultMeshCollectionHandle<TAlias>(
                CollectionId,
                async () => (await LatestAsync().ConfigureAwait(false))
                    .Select(ConvertDocument<TDocument, TAlias>)
                    .ToArray(),
                () => WatchChanges().Select(change => new CultMeshCollectionChange<TAlias>(
                    change.Kind,
                    change.Key,
                    change.SchemaId,
                    change.Document == null ? null : ConvertDocument<TDocument, TAlias>(change.Document),
                    change.PreviousDocument == null ? null : ConvertDocument<TDocument, TAlias>(change.PreviousDocument),
                    change.Message)),
                Sources,
                RouteHint);
        }

        private static TTarget ConvertDocument<TSource, TTarget>(TSource document)
            where TSource : class
            where TTarget : class
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document is TTarget alreadyTyped)
            {
                return alreadyTyped;
            }

            var payload = CultDocumentMessagePackSerialization.SerializeUntyped(document, typeof(TSource));
            return (TTarget)CultDocumentMessagePackSerialization.DeserializeUntyped(typeof(TTarget), payload);
        }
    }

    /// <summary>
    /// Inspectable metadata for a typed live feed surface.
    /// </summary>
    public sealed class CultMeshLiveFeedDiagnostic
    {
        /// <summary>Creates live feed diagnostics.</summary>
        public CultMeshLiveFeedDiagnostic(
            string feedId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            FeedId = RequireNonEmpty(feedId, nameof(feedId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic feed id.</summary>
        public string FeedId { get; }

        /// <summary>Gets the preferred or observed route for the feed.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets the typed state sources this live feed depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable metadata for a typed query surface.
    /// </summary>
    public sealed class CultMeshQuerySurfaceDiagnostic
    {
        /// <summary>Creates query surface diagnostics.</summary>
        public CultMeshQuerySurfaceDiagnostic(
            string queryId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            QueryId = RequireNonEmpty(queryId, nameof(queryId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic query id.</summary>
        public string QueryId { get; }

        /// <summary>Gets the preferred or observed route for the query.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets the typed state sources this query depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable metadata for a typed operation handle.
    /// </summary>
    public sealed class CultMeshOperationHandleDiagnostic
    {
        /// <summary>Creates operation handle diagnostics.</summary>
        public CultMeshOperationHandleDiagnostic(string operationId)
        {
            OperationId = RequireNonEmpty(operationId, nameof(operationId));
        }

        /// <summary>Gets the semantic operation id.</summary>
        public string OperationId { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable metadata for a typed state pointer.
    /// </summary>
    public sealed class CultMeshStatePointerDiagnostic
    {
        /// <summary>Creates state pointer diagnostics.</summary>
        public CultMeshStatePointerDiagnostic(
            string pointerId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            PointerId = RequireNonEmpty(pointerId, nameof(pointerId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic pointer id.</summary>
        public string PointerId { get; }

        /// <summary>Gets the preferred or observed route for pointer resolution.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this pointer depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable metadata for a typed projection recipe.
    /// </summary>
    public sealed class CultMeshProjectionRecipeDiagnostic
    {
        /// <summary>Creates projection recipe diagnostics.</summary>
        public CultMeshProjectionRecipeDiagnostic(
            string projectionId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            ProjectionId = RequireNonEmpty(projectionId, nameof(projectionId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic projection id.</summary>
        public string ProjectionId { get; }

        /// <summary>Gets the preferred or observed route for the projection.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets the typed state sources this projection depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Names the kind of typed surface described in a CultMesh surface catalog.
    /// </summary>
    public enum CultMeshSurfaceKind
    {
        /// <summary>A typed derived-state query surface.</summary>
        Query,

        /// <summary>A reusable typed projection recipe.</summary>
        ProjectionRecipe,

        /// <summary>A coherent live view feed.</summary>
        LiveFeed,

        /// <summary>A typed operation handle.</summary>
        Operation,

        /// <summary>A typed reactive document handle.</summary>
        Document,

        /// <summary>A typed reactive collection handle.</summary>
        Collection,

        /// <summary>A typed pointer to Verse state.</summary>
        StatePointer,

        /// <summary>A native slice or slab view.</summary>
        NativeSliceView
    }

    /// <summary>
    /// One inspectable typed surface advertised by a runtime.
    /// </summary>
    public sealed class CultMeshSurfaceDiagnostic
    {
        /// <summary>Creates a surface diagnostic.</summary>
        public CultMeshSurfaceDiagnostic(
            CultMeshSurfaceKind kind,
            string surfaceId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            Kind = kind;
            SurfaceId = RequireNonEmpty(surfaceId, nameof(surfaceId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the kind of surface.</summary>
        public CultMeshSurfaceKind Kind { get; }

        /// <summary>Gets the semantic surface id.</summary>
        public string SurfaceId { get; }

        /// <summary>Gets the preferred or observed route for the surface.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this surface depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable catalog of typed surfaces exposed by a runtime or generated binding.
    /// </summary>
    public sealed class CultMeshSurfaceCatalogDiagnostic
    {
        /// <summary>Creates a surface catalog diagnostic.</summary>
        public CultMeshSurfaceCatalogDiagnostic(
            string catalogId,
            IEnumerable<CultMeshSurfaceDiagnostic> surfaces)
        {
            CatalogId = RequireNonEmpty(catalogId, nameof(catalogId));
            Surfaces = (surfaces ?? throw new ArgumentNullException(nameof(surfaces))).ToArray();
        }

        /// <summary>Gets the semantic catalog id.</summary>
        public string CatalogId { get; }

        /// <summary>Gets the advertised typed surfaces.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> Surfaces { get; }

        /// <summary>Finds a surface by semantic id.</summary>
        public CultMeshSurfaceDiagnostic? Find(string surfaceId)
        {
            if (string.IsNullOrWhiteSpace(surfaceId)) return null;
            return Surfaces.FirstOrDefault(surface => string.Equals(surface.SurfaceId, surfaceId, StringComparison.Ordinal));
        }

        /// <summary>Finds surfaces by kind in catalog order.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> FindByKind(CultMeshSurfaceKind kind)
        {
            return Surfaces.Where(surface => surface.Kind == kind).ToArray();
        }

        /// <summary>Groups surfaces by kind for generated bindings and tooling.</summary>
        public CultMeshSurfaceCatalogIndexDiagnostic IndexByKind()
        {
            return new CultMeshSurfaceCatalogIndexDiagnostic(this);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Kind-indexed view of a typed surface catalog for generated bindings, UI runtimes, and tools.
    /// </summary>
    public sealed class CultMeshSurfaceCatalogIndexDiagnostic
    {
        /// <summary>Creates a kind-indexed catalog diagnostic.</summary>
        public CultMeshSurfaceCatalogIndexDiagnostic(CultMeshSurfaceCatalogDiagnostic catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            CatalogId = catalog.CatalogId;
            Queries = catalog.FindByKind(CultMeshSurfaceKind.Query);
            ProjectionRecipes = catalog.FindByKind(CultMeshSurfaceKind.ProjectionRecipe);
            LiveFeeds = catalog.FindByKind(CultMeshSurfaceKind.LiveFeed);
            Operations = catalog.FindByKind(CultMeshSurfaceKind.Operation);
            Documents = catalog.FindByKind(CultMeshSurfaceKind.Document);
            Collections = catalog.FindByKind(CultMeshSurfaceKind.Collection);
            StatePointers = catalog.FindByKind(CultMeshSurfaceKind.StatePointer);
            NativeSliceViews = catalog.FindByKind(CultMeshSurfaceKind.NativeSliceView);
        }

        /// <summary>Gets the semantic catalog id.</summary>
        public string CatalogId { get; }

        /// <summary>Gets typed derived-state query surfaces.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> Queries { get; }

        /// <summary>Gets reusable typed projection recipes.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> ProjectionRecipes { get; }

        /// <summary>Gets coherent live view feeds.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> LiveFeeds { get; }

        /// <summary>Gets typed operation handles.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> Operations { get; }

        /// <summary>Gets typed reactive document handles.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> Documents { get; }

        /// <summary>Gets typed reactive collection handles.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> Collections { get; }

        /// <summary>Gets typed pointers to Verse state.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> StatePointers { get; }

        /// <summary>Gets native slice or slab views.</summary>
        public IReadOnlyList<CultMeshSurfaceDiagnostic> NativeSliceViews { get; }
    }

    /// <summary>
    /// Options for a polling watch fallback over a typed query or live feed snapshot.
    /// </summary>
    public sealed class CultMeshPollingWatchOptions<TResult>
    {
        /// <summary>Creates polling watch options.</summary>
        public CultMeshPollingWatchOptions(
            TimeSpan? interval = null,
            bool emitInitial = true,
            IEqualityComparer<TResult>? comparer = null)
        {
            Interval = interval ?? TimeSpan.FromMilliseconds(50);
            if (Interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "Polling interval must be positive.");
            }

            EmitInitial = emitInitial;
            Comparer = comparer ?? EqualityComparer<TResult>.Default;
        }

        /// <summary>Gets the polling interval.</summary>
        public TimeSpan Interval { get; }

        /// <summary>Gets whether the first sampled value should be emitted.</summary>
        public bool EmitInitial { get; }

        /// <summary>Gets the equality comparer used to suppress unchanged samples.</summary>
        public IEqualityComparer<TResult> Comparer { get; }
    }

    /// <summary>
    /// Timer-backed fallback watcher for runtimes that do not yet have native reactive transport.
    /// </summary>
    internal sealed class CultMeshPollingWatcher<TParameters, TResult> : IDisposable
    {
        private readonly Func<TParameters, CultMeshQueryContext, Task<TResult>> _sample;
        private readonly CultMeshPollingWatchOptions<TResult> _options;
        private readonly Subject<TResult> _subject = new();
        private readonly Timer _timer;
        private readonly object _gate = new();
        private readonly TParameters _parameters;
        private readonly CultMeshQueryContext _context;
        private TResult? _last;
        private bool _hasLast;
        private bool _running;
        private bool _disposed;

        public CultMeshPollingWatcher(
            Func<TParameters, CultMeshQueryContext, Task<TResult>> sample,
            TParameters parameters,
            CultMeshQueryContext context,
            CultMeshPollingWatchOptions<TResult> options)
        {
            _sample = sample ?? throw new ArgumentNullException(nameof(sample));
            _parameters = parameters;
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _timer = new Timer(OnTimer, null, TimeSpan.Zero, _options.Interval);
        }

        public Observable<TResult> Observable => _subject;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _timer.Dispose();
            _subject.Dispose();
        }

        private void OnTimer(object? _)
        {
            lock (_gate)
            {
                if (_disposed || _running)
                {
                    return;
                }

                _running = true;
            }

            _ = SampleAsync();
        }

        private async Task SampleAsync()
        {
            try
            {
                var next = await _sample(_parameters, _context).ConfigureAwait(false);
                var shouldEmit = false;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    shouldEmit = _hasLast
                        ? !_options.Comparer.Equals(_last!, next)
                        : _options.EmitInitial;
                    _last = next;
                    _hasLast = true;
                }

                if (shouldEmit)
                {
                    _subject.OnNext(next);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                }
            }
        }
    }

    internal sealed class CultMeshCompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _disposables;
        private int _disposed;

        public CultMeshCompositeDisposable(params IDisposable[] disposables)
        {
            _disposables = disposables ?? throw new ArgumentNullException(nameof(disposables));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Typed pointer to Verse state that UI surfaces and tools can resolve and watch.
    /// </summary>
    public sealed class CultMeshStatePointer<T>
    {
        private readonly Func<CultMeshQueryContext, Task<T?>> _resolve;
        private readonly Func<CultMeshQueryContext, Observable<T>> _watch;

        /// <summary>Creates a state pointer.</summary>
        public CultMeshStatePointer(
            string pointerId,
            Func<Task<T?>> resolve,
            Func<Observable<T>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
            : this(
                pointerId,
                _ => (resolve ?? throw new ArgumentNullException(nameof(resolve)))(),
                _ => (watch ?? throw new ArgumentNullException(nameof(watch)))(),
                routeHint,
                sources)
        {
        }

        /// <summary>Creates a state pointer that can resolve through a Verse query context.</summary>
        public CultMeshStatePointer(
            string pointerId,
            Func<CultMeshQueryContext, Task<T?>> resolve,
            Func<CultMeshQueryContext, Observable<T>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            PointerId = RequireNonEmpty(pointerId, nameof(pointerId));
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            _watch = watch ?? throw new ArgumentNullException(nameof(watch));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic pointer id.</summary>
        public string PointerId { get; }

        /// <summary>Gets the preferred or observed route for pointer resolution.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this pointer depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Resolves the pointer once.</summary>
        public Task<T?> ResolveAsync()
        {
            return ResolveAsync(CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Resolves the pointer once through a query context.</summary>
        public Task<T?> ResolveAsync(CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _resolve(ResolveContext(context));
        }

        /// <summary>Resolves the pointer once for one runtime using default context.</summary>
        public Task<T?> ResolveAsync(string runtimeId)
        {
            return ResolveAsync(CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Watches resolved values for this pointer.</summary>
        public Observable<T> Watch()
        {
            return Watch(CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Watches resolved values for this pointer through a query context.</summary>
        public Observable<T> Watch(CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _watch(ResolveContext(context));
        }

        /// <summary>Binds this pointer to a Verse context.</summary>
        public CultMeshBoundStatePointer<T> Bind(CultMeshVerseContext context)
        {
            return new CultMeshBoundStatePointer<T>(context, this);
        }

        /// <summary>Binds this pointer to a Verse.</summary>
        public CultMeshBoundStatePointer<T> Bind(CultMeshVerse verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Bind(verse.Context);
        }

        private CultMeshQueryContext ResolveContext(CultMeshQueryContext context)
        {
            return context.RouteHint.Kind == CultMeshLocalityKind.Automatic &&
                   RouteHint.Kind != CultMeshLocalityKind.Automatic
                ? context.WithRoute(RouteHint)
                : context;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// State pointer pre-bound to a Verse so UI/tool runtimes can resolve state without context plumbing.
    /// </summary>
    public sealed class CultMeshBoundStatePointer<T>
    {
        /// <summary>Creates a Verse-bound state pointer.</summary>
        public CultMeshBoundStatePointer(
            CultMeshVerseContext context,
            CultMeshStatePointer<T> pointer)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        }

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the underlying state pointer.</summary>
        public CultMeshStatePointer<T> Pointer { get; }

        /// <summary>Gets the semantic pointer id.</summary>
        public string PointerId => Pointer.PointerId;

        /// <summary>Gets typed state sources this pointer depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources => Pointer.Sources;

        /// <summary>Gets the preferred or observed route for pointer resolution.</summary>
        public CultMeshRouteHint RouteHint => Pointer.RouteHint;

        /// <summary>Resolves the pointer through the bound Verse.</summary>
        public Task<T?> ResolveAsync()
        {
            return Pointer.ResolveAsync(Context.QueryContext());
        }

        /// <summary>Watches the pointer through the bound Verse.</summary>
        public Observable<T> Watch()
        {
            return Pointer.Watch(Context.QueryContext());
        }
    }

    /// <summary>
    /// Typed pointer to Verse state that UI surfaces and tools can resolve, watch, and replace.
    /// </summary>
    public sealed class CultMeshMutableStatePointer<T>
    {
        private readonly Func<CultMeshQueryContext, Task<T?>> _resolve;
        private readonly Func<CultMeshQueryContext, Observable<T>> _watch;
        private readonly Func<CultMeshQueryContext, T, Task> _replace;

        /// <summary>Creates a mutable state pointer.</summary>
        public CultMeshMutableStatePointer(
            string pointerId,
            Func<Task<T?>> resolve,
            Func<Observable<T>> watch,
            Func<T, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
            : this(
                pointerId,
                _ => (resolve ?? throw new ArgumentNullException(nameof(resolve)))(),
                _ => (watch ?? throw new ArgumentNullException(nameof(watch)))(),
                (_, value) => (replace ?? throw new ArgumentNullException(nameof(replace)))(value),
                routeHint,
                sources)
        {
        }

        /// <summary>Creates a mutable state pointer that can operate through a Verse query context.</summary>
        public CultMeshMutableStatePointer(
            string pointerId,
            Func<CultMeshQueryContext, Task<T?>> resolve,
            Func<CultMeshQueryContext, Observable<T>> watch,
            Func<CultMeshQueryContext, T, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            PointerId = RequireNonEmpty(pointerId, nameof(pointerId));
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            _watch = watch ?? throw new ArgumentNullException(nameof(watch));
            _replace = replace ?? throw new ArgumentNullException(nameof(replace));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic pointer id.</summary>
        public string PointerId { get; }

        /// <summary>Gets the preferred or observed route for pointer access.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets typed state sources this pointer depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Resolves the pointer once.</summary>
        public Task<T?> ResolveAsync()
        {
            return ResolveAsync(CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Resolves the pointer once.</summary>
        public Task<T?> ReadAsync()
        {
            return ResolveAsync();
        }

        /// <summary>Resolves the pointer once through a query context.</summary>
        public Task<T?> ResolveAsync(CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _resolve(ResolveContext(context));
        }

        /// <summary>Resolves the pointer once through a query context.</summary>
        public Task<T?> ReadAsync(CultMeshQueryContext context)
        {
            return ResolveAsync(context);
        }

        /// <summary>Resolves the pointer once for one runtime using default context.</summary>
        public Task<T?> ResolveAsync(string runtimeId)
        {
            return ResolveAsync(CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Resolves the pointer once for one runtime using default context.</summary>
        public Task<T?> ReadAsync(string runtimeId)
        {
            return ResolveAsync(runtimeId);
        }

        /// <summary>Replaces the pointed state value.</summary>
        public Task ReplaceAsync(T value)
        {
            return ReplaceAsync(value, CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Replaces the pointed state value through a query context.</summary>
        public Task ReplaceAsync(T value, CultMeshQueryContext context)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _replace(ResolveContext(context), value);
        }

        /// <summary>Replaces the pointed state value for one runtime using default context.</summary>
        public Task ReplaceAsync(T value, string runtimeId)
        {
            return ReplaceAsync(value, CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Watches resolved values for this pointer.</summary>
        public Observable<T> Watch()
        {
            return Watch(CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Watches resolved values for this pointer through a query context.</summary>
        public Observable<T> Watch(CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _watch(ResolveContext(context));
        }

        /// <summary>Views this mutable pointer as a read/watch state pointer.</summary>
        public CultMeshStatePointer<T> AsStatePointer()
        {
            return new CultMeshStatePointer<T>(
                PointerId,
                (Func<CultMeshQueryContext, Task<T?>>)ResolveAsync,
                (Func<CultMeshQueryContext, Observable<T>>)Watch,
                RouteHint,
                Sources);
        }

        /// <summary>Binds this pointer to a Verse context.</summary>
        public CultMeshBoundMutableStatePointer<T> Bind(CultMeshVerseContext context)
        {
            return new CultMeshBoundMutableStatePointer<T>(context, this);
        }

        /// <summary>Binds this pointer to a Verse.</summary>
        public CultMeshBoundMutableStatePointer<T> Bind(CultMeshVerse verse)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Bind(verse.Context);
        }

        private CultMeshQueryContext ResolveContext(CultMeshQueryContext context)
        {
            return context.RouteHint.Kind == CultMeshLocalityKind.Automatic &&
                   RouteHint.Kind != CultMeshLocalityKind.Automatic
                ? context.WithRoute(RouteHint)
                : context;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Mutable state pointer pre-bound to a Verse so UI/tool runtimes can read, watch, and replace state without context plumbing.
    /// </summary>
    public sealed class CultMeshBoundMutableStatePointer<T>
    {
        /// <summary>Creates a Verse-bound mutable state pointer.</summary>
        public CultMeshBoundMutableStatePointer(
            CultMeshVerseContext context,
            CultMeshMutableStatePointer<T> pointer)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        }

        /// <summary>Gets the bound Verse context.</summary>
        public CultMeshVerseContext Context { get; }

        /// <summary>Gets the underlying mutable state pointer.</summary>
        public CultMeshMutableStatePointer<T> Pointer { get; }

        /// <summary>Gets the semantic pointer id.</summary>
        public string PointerId => Pointer.PointerId;

        /// <summary>Gets typed state sources this pointer depends on, when known.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources => Pointer.Sources;

        /// <summary>Gets the preferred or observed route for pointer access.</summary>
        public CultMeshRouteHint RouteHint => Pointer.RouteHint;

        /// <summary>Resolves the pointer through the bound Verse.</summary>
        public Task<T?> ResolveAsync()
        {
            return Pointer.ResolveAsync(Context.QueryContext());
        }

        /// <summary>Resolves the pointer through the bound Verse.</summary>
        public Task<T?> ReadAsync()
        {
            return ResolveAsync();
        }

        /// <summary>Replaces the pointer value through the bound Verse.</summary>
        public Task ReplaceAsync(T value)
        {
            return Pointer.ReplaceAsync(value, Context.QueryContext());
        }

        /// <summary>Watches the pointer through the bound Verse.</summary>
        public Observable<T> Watch()
        {
            return Pointer.Watch(Context.QueryContext());
        }

        /// <summary>Views this mutable pointer as a read/watch state pointer bound to the same Verse.</summary>
        public CultMeshBoundStatePointer<T> AsStatePointer()
        {
            return new CultMeshBoundStatePointer<T>(Context, Pointer.AsStatePointer());
        }
    }

    /// <summary>
    /// Describes one typed source consumed by a projection recipe.
    /// </summary>
    public sealed class CultMeshProjectionSource
    {
        /// <summary>Creates a projection source descriptor.</summary>
        public CultMeshProjectionSource(string sourceId, string? schemaId = null, string? description = null)
        {
            SourceId = RequireNonEmpty(sourceId, nameof(sourceId));
            SchemaId = schemaId;
            Description = description;
        }

        /// <summary>Gets the semantic source id, such as a state pointer, document handle, or native view id.</summary>
        public string SourceId { get; }

        /// <summary>Gets the optional schema id for the source.</summary>
        public string? SchemaId { get; }

        /// <summary>Gets optional human-facing source diagnostics.</summary>
        public string? Description { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Resolves string state references embedded in portable UI/tool surfaces through a named CultMesh resolver.
    /// </summary>
    public sealed class CultMeshStateRefResolver
    {
        private readonly Func<string, CultMeshQueryContext, string?> _resolve;

        /// <summary>Creates a state-reference resolver.</summary>
        public CultMeshStateRefResolver(
            string resolverId,
            Func<string, CultMeshQueryContext, string?> resolve,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            ResolverId = RequireNonEmpty(resolverId, nameof(resolverId));
            _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the semantic resolver id.</summary>
        public string ResolverId { get; }

        /// <summary>Gets the source state read by the resolver.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Gets the preferred route for resolving state refs.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets an empty resolver that always returns an empty string.</summary>
        public static CultMeshStateRefResolver Empty { get; } =
            new("cultmesh.state_refs.empty", (_stateRef, _context) => "");

        /// <summary>Resolves a state ref using a local query context.</summary>
        public string Resolve(string stateRef)
        {
            return Resolve(stateRef, CultMeshQueryContext.ForRuntime("local"));
        }

        /// <summary>Resolves a state ref using a runtime id.</summary>
        public string Resolve(string stateRef, string runtimeId)
        {
            return Resolve(stateRef, CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Resolves a state ref using an explicit query context.</summary>
        public string Resolve(string stateRef, CultMeshQueryContext context)
        {
            if (string.IsNullOrWhiteSpace(stateRef))
                return "";
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _resolve(stateRef, ResolveContext(context)) ?? "";
        }

        /// <summary>Attempts to resolve a non-empty value.</summary>
        public bool TryResolve(string stateRef, out string value)
        {
            value = Resolve(stateRef);
            return !string.IsNullOrEmpty(value);
        }

        /// <summary>Attempts to resolve a non-empty value.</summary>
        public bool TryResolve(string stateRef, CultMeshQueryContext context, out string value)
        {
            value = Resolve(stateRef, context);
            return !string.IsNullOrEmpty(value);
        }

        /// <summary>Creates a resolver that falls back to another resolver when this resolver returns empty.</summary>
        public CultMeshStateRefResolver Or(CultMeshStateRefResolver fallback)
        {
            if (fallback == null) throw new ArgumentNullException(nameof(fallback));
            return new CultMeshStateRefResolver(
                ResolverId + "|" + fallback.ResolverId,
                (stateRef, context) =>
                {
                    var value = Resolve(stateRef, context);
                    return string.IsNullOrEmpty(value)
                        ? fallback.Resolve(stateRef, context)
                        : value;
                },
                Sources.Concat(fallback.Sources),
                RouteHint.Kind == CultMeshLocalityKind.Automatic ? fallback.RouteHint : RouteHint);
        }

        /// <summary>Returns the legacy function shape for existing surface renderers.</summary>
        public Func<string, string> AsFunc()
        {
            return Resolve;
        }

        private CultMeshQueryContext ResolveContext(CultMeshQueryContext context)
        {
            if (context.RouteHint.Kind != CultMeshLocalityKind.Automatic || RouteHint.Kind == CultMeshLocalityKind.Automatic)
                return context;

            return context.WithRoute(RouteHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Diagnostics for a named CultMesh state-reference resolver.
    /// </summary>
    public sealed class CultMeshStateRefResolverDiagnostic
    {
        /// <summary>Creates a state-reference resolver diagnostic.</summary>
        public CultMeshStateRefResolverDiagnostic(
            string resolverId,
            CultMeshRouteHint routeHint,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            ResolverId = RequireNonEmpty(resolverId, nameof(resolverId));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            Sources = sources?.ToArray() ?? Array.Empty<CultMeshProjectionSource>();
        }

        /// <summary>Gets the semantic resolver id.</summary>
        public string ResolverId { get; }

        /// <summary>Gets the preferred route for resolving refs.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets source state used by the resolver.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Describes one UI/tool surface property bound to a typed CultMesh state pointer.
    /// </summary>
    public sealed class CultMeshStateBindingDescriptor
    {
        /// <summary>Creates a state binding descriptor.</summary>
        public CultMeshStateBindingDescriptor(
            string targetProp,
            string pointerId,
            string? sourceId = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            TargetProp = RequireNonEmpty(targetProp, nameof(targetProp));
            PointerId = RequireNonEmpty(pointerId, nameof(pointerId));
            SourceId = sourceId;
            SchemaId = schemaId;
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the component property receiving the resolved state value.</summary>
        public string TargetProp { get; }

        /// <summary>Gets the semantic CultMesh state pointer id.</summary>
        public string PointerId { get; }

        /// <summary>Gets the optional source document, cache, native view, or daemon source id.</summary>
        public string? SourceId { get; }

        /// <summary>Gets the optional schema id for the source state.</summary>
        public string? SchemaId { get; }

        /// <summary>Gets the preferred route for resolving and watching the binding.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Creates a binding from an existing state pointer.</summary>
        public static CultMeshStateBindingDescriptor FromPointer<TValue>(
            string targetProp,
            CultMeshStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            var source = pointer.Sources.FirstOrDefault();
            return new CultMeshStateBindingDescriptor(
                targetProp,
                pointer.PointerId,
                source?.SourceId,
                source?.SchemaId,
                pointer.RouteHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Transport-friendly fields for a typed state binding descriptor.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshStateBindingRecord
    {
        /// <summary>Creates a state binding record.</summary>
        [SerializationConstructor]
        public CultMeshStateBindingRecord(
            string? targetProp = null,
            string? pointerId = null,
            string? sourceId = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            TargetProp = string.IsNullOrWhiteSpace(targetProp) ? "value" : targetProp!;
            PointerId = pointerId ?? "";
            SourceId = sourceId ?? "";
            SchemaId = schemaId ?? "";
            RouteKind = routeKind ?? "";
            RouteDescription = routeDescription ?? "";
        }

        /// <summary>Gets the component property receiving the resolved state value.</summary>
        [Key(0)] public string TargetProp { get; }

        /// <summary>Gets the semantic CultMesh state pointer id.</summary>
        [Key(1)] public string PointerId { get; }

        /// <summary>Gets the source document, cache, native view, or daemon source id.</summary>
        [Key(2)] public string SourceId { get; }

        /// <summary>Gets the source schema id.</summary>
        [Key(3)] public string SchemaId { get; }

        /// <summary>Gets the flattened route kind.</summary>
        [Key(4)] public string RouteKind { get; }

        /// <summary>Gets the flattened route description.</summary>
        [Key(5)] public string RouteDescription { get; }

        /// <summary>Creates transport-friendly fields from a state binding descriptor.</summary>
        public static CultMeshStateBindingRecord FromBinding(CultMeshStateBindingDescriptor? binding)
        {
            var route = CultMeshRouteRecord.FromRoute(binding?.RouteHint);
            return new CultMeshStateBindingRecord(
                binding?.TargetProp,
                binding?.PointerId,
                binding?.SourceId,
                binding?.SchemaId,
                route.Kind,
                route.Description);
        }

        /// <summary>Rehydrates this record as a state binding descriptor.</summary>
        public CultMeshStateBindingDescriptor ToBinding(
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackTargetProp = null)
        {
            var targetProp = string.IsNullOrWhiteSpace(TargetProp)
                ? (string.IsNullOrWhiteSpace(fallbackTargetProp) ? "value" : fallbackTargetProp!)
                : TargetProp;
            var pointerId = string.IsNullOrWhiteSpace(PointerId)
                ? $"{targetProp}.unknown"
                : PointerId;
            var route = new CultMeshRouteRecord(RouteKind, RouteDescription)
                .ToRoute(fallbackRouteHint ?? CultMeshRouteHint.Automatic);

            return new CultMeshStateBindingDescriptor(
                targetProp,
                pointerId,
                SourceId,
                SchemaId,
                route);
        }
    }

    /// <summary>
    /// Describes one UI/tool command bound to a typed CultMesh operation.
    /// </summary>
    public sealed class CultMeshOperationBindingDescriptor
    {
        /// <summary>Creates an operation binding descriptor.</summary>
        public CultMeshOperationBindingDescriptor(
            string operationId,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            OperationId = RequireNonEmpty(operationId, nameof(operationId));
            Label = label ?? "";
            SchemaId = schemaId ?? "";
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
        }

        /// <summary>Gets the semantic CultMesh operation id.</summary>
        public string OperationId { get; }

        /// <summary>Gets the human-facing command label.</summary>
        public string Label { get; }

        /// <summary>Gets the optional request schema id for the operation.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the preferred route for invoking the operation.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Creates a binding from an existing typed operation handle.</summary>
        public static CultMeshOperationBindingDescriptor FromOperation<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshOperationBindingDescriptor(
                operation.OperationId,
                label,
                schemaId,
                routeHint);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Transport-friendly fields for a typed operation binding descriptor.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshOperationBindingRecord
    {
        /// <summary>Creates an operation binding record.</summary>
        [SerializationConstructor]
        public CultMeshOperationBindingRecord(
            string? operationId = null,
            string? label = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            OperationId = operationId ?? "";
            Label = label ?? "";
            SchemaId = schemaId ?? "";
            RouteKind = routeKind ?? "";
            RouteDescription = routeDescription ?? "";
        }

        /// <summary>Gets the semantic CultMesh operation id.</summary>
        [Key(0)] public string OperationId { get; }

        /// <summary>Gets the human-facing command label.</summary>
        [Key(1)] public string Label { get; }

        /// <summary>Gets the optional request schema id for the operation.</summary>
        [Key(2)] public string SchemaId { get; }

        /// <summary>Gets the flattened route kind.</summary>
        [Key(3)] public string RouteKind { get; }

        /// <summary>Gets the flattened route description.</summary>
        [Key(4)] public string RouteDescription { get; }

        /// <summary>Creates transport-friendly fields from an operation binding descriptor.</summary>
        public static CultMeshOperationBindingRecord FromBinding(CultMeshOperationBindingDescriptor? binding)
        {
            var route = CultMeshRouteRecord.FromRoute(binding?.RouteHint);
            return new CultMeshOperationBindingRecord(
                binding?.OperationId,
                binding?.Label,
                binding?.SchemaId,
                route.Kind,
                route.Description);
        }

        /// <summary>Rehydrates this record as an operation binding descriptor.</summary>
        public CultMeshOperationBindingDescriptor ToBinding(
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackOperationId = null)
        {
            var operationId = string.IsNullOrWhiteSpace(OperationId)
                ? (fallbackOperationId ?? "")
                : OperationId;
            var route = new CultMeshRouteRecord(RouteKind, RouteDescription)
                .ToRoute(fallbackRouteHint ?? CultMeshRouteHint.Automatic);

            return new CultMeshOperationBindingDescriptor(
                operationId,
                Label,
                SchemaId,
                route);
        }
    }

    /// <summary>
    /// Describes one concrete invocation of a typed CultMesh operation.
    /// </summary>
    public sealed class CultMeshOperationInvocationDescriptor
    {
        /// <summary>Creates an operation invocation descriptor.</summary>
        public CultMeshOperationInvocationDescriptor(
            string operationId,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            OperationId = RequireNonEmpty(operationId, nameof(operationId));
            SchemaId = schemaId ?? "";
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            IdempotencyKey = idempotencyKey;
        }

        /// <summary>Gets the semantic CultMesh operation id being invoked.</summary>
        public string OperationId { get; }

        /// <summary>Gets the optional request schema id for the operation.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the preferred route for invoking the operation.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Gets an optional caller-provided idempotency key.</summary>
        public string? IdempotencyKey { get; }

        /// <summary>Creates an invocation from an advertised operation binding.</summary>
        public static CultMeshOperationInvocationDescriptor FromBinding(
            CultMeshOperationBindingDescriptor binding,
            string? idempotencyKey = null)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            return new CultMeshOperationInvocationDescriptor(
                binding.OperationId,
                binding.SchemaId,
                binding.RouteHint,
                idempotencyKey);
        }

        /// <summary>Creates an invocation from an existing typed operation handle.</summary>
        public static CultMeshOperationInvocationDescriptor FromOperation<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshOperationInvocationDescriptor(
                operation.OperationId,
                schemaId,
                routeHint,
                idempotencyKey);
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Flat, transport-friendly fields for a concrete CultMesh operation invocation.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshOperationInvocationRecord
    {
        /// <summary>Creates flat operation invocation fields.</summary>
        [SerializationConstructor]
        public CultMeshOperationInvocationRecord(
            string? operationId = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null,
            string? idempotencyKey = null)
        {
            OperationId = operationId ?? "";
            SchemaId = schemaId ?? "";
            RouteKind = routeKind ?? "";
            RouteDescription = routeDescription ?? "";
            IdempotencyKey = idempotencyKey ?? "";
        }

        /// <summary>Gets the semantic CultMesh operation id.</summary>
        [Key(0)]
        public string OperationId { get; }

        /// <summary>Gets the optional request schema id.</summary>
        [Key(1)]
        public string SchemaId { get; }

        /// <summary>Gets the serialized route locality kind.</summary>
        [Key(2)]
        public string RouteKind { get; }

        /// <summary>Gets the serialized route description.</summary>
        [Key(3)]
        public string RouteDescription { get; }

        /// <summary>Gets the serialized idempotency key.</summary>
        [Key(4)]
        public string IdempotencyKey { get; }

        /// <summary>Creates flat fields from a typed invocation descriptor.</summary>
        public static CultMeshOperationInvocationRecord FromInvocation(
            CultMeshOperationInvocationDescriptor? invocation,
            string? fallbackOperationId = null,
            string? fallbackSchemaId = null,
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackIdempotencyKey = null)
        {
            var route = CultMeshRouteRecord.FromRoute(invocation?.RouteHint ?? fallbackRouteHint);
            return new CultMeshOperationInvocationRecord(
                string.IsNullOrWhiteSpace(invocation?.OperationId) ? fallbackOperationId : invocation!.OperationId,
                string.IsNullOrWhiteSpace(invocation?.SchemaId) ? fallbackSchemaId : invocation!.SchemaId,
                route.Kind,
                route.Description,
                string.IsNullOrWhiteSpace(invocation?.IdempotencyKey) ? fallbackIdempotencyKey : invocation!.IdempotencyKey);
        }

        /// <summary>Rehydrates the flat fields into a typed invocation descriptor.</summary>
        public CultMeshOperationInvocationDescriptor ToInvocation(
            string? fallbackOperationId = null,
            string? fallbackSchemaId = null,
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackIdempotencyKey = null)
        {
            var operationId = string.IsNullOrWhiteSpace(OperationId) ? fallbackOperationId : OperationId;
            if (string.IsNullOrWhiteSpace(operationId))
                throw new InvalidOperationException("CultMesh operation invocation fields do not contain an operation id.");

            var route = new CultMeshRouteRecord(RouteKind, RouteDescription)
                .ToRoute(fallbackRouteHint);

            return new CultMeshOperationInvocationDescriptor(
                operationId!,
                string.IsNullOrWhiteSpace(SchemaId) ? fallbackSchemaId : SchemaId,
                route,
                string.IsNullOrWhiteSpace(IdempotencyKey) ? fallbackIdempotencyKey : IdempotencyKey);
        }
    }

    /// <summary>
    /// Shared payload value carried by one concrete CultMesh operation invocation.
    /// </summary>
    public sealed class CultMeshOperationPayload : IReadOnlyDictionary<string, string>
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyFields =
            new Dictionary<string, string>(0, StringComparer.Ordinal);

        private readonly IReadOnlyDictionary<string, string> _fields;

        /// <summary>Creates an empty operation payload.</summary>
        public CultMeshOperationPayload()
            : this(EmptyFields)
        {
        }

        /// <summary>Creates an operation payload from string-compatible fields.</summary>
        public CultMeshOperationPayload(IEnumerable<KeyValuePair<string, string>>? fields)
        {
            if (fields == null)
            {
                _fields = EmptyFields;
                return;
            }

            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Key))
                    continue;
                copy[field.Key] = field.Value ?? "";
            }

            _fields = copy;
        }

        /// <summary>Gets the empty operation payload.</summary>
        public static CultMeshOperationPayload Empty { get; } = new();

        /// <summary>Gets the payload field names.</summary>
        public IEnumerable<string> Keys => _fields.Keys;

        /// <summary>Gets the payload field values.</summary>
        public IEnumerable<string> Values => _fields.Values;

        /// <summary>Gets the number of payload fields.</summary>
        public int Count => _fields.Count;

        /// <summary>Gets a payload field by name.</summary>
        public string this[string key] => _fields[key];

        /// <summary>Gets whether the payload contains a field.</summary>
        public bool ContainsKey(string key)
        {
            return _fields.ContainsKey(key);
        }

        /// <summary>Tries to read a raw string field.</summary>
        public bool TryGetValue(string key, out string value)
        {
            return _fields.TryGetValue(key, out value);
        }

        /// <summary>Reads a raw string field with a default value.</summary>
        public string GetString(string key, string defaultValue = "")
        {
            return _fields.TryGetValue(key, out var value) ? value ?? defaultValue : defaultValue;
        }

        /// <summary>Reads an integer field using invariant parsing.</summary>
        public int GetInt32(string key, int defaultValue = 0)
        {
            return int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>Reads a floating point field using invariant parsing.</summary>
        public double GetDouble(string key, double defaultValue = 0)
        {
            return double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>Reads a boolean field using common CultMesh surface spellings.</summary>
        public bool GetBoolean(string key, bool defaultValue = false)
        {
            var value = GetString(key);
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "0", StringComparison.Ordinal) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
                return false;
            return defaultValue;
        }

        /// <summary>Returns a copy with one field overwritten.</summary>
        public CultMeshOperationPayload With(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Value must be non-empty.", nameof(key));
            var copy = new Dictionary<string, string>(_fields, StringComparer.Ordinal)
            {
                [key] = value ?? ""
            };
            return new CultMeshOperationPayload(copy);
        }

        /// <summary>Copies payload fields into a mutable dictionary.</summary>
        public Dictionary<string, string> ToDictionary(IEqualityComparer<string>? comparer = null)
        {
            var copy = new Dictionary<string, string>(comparer ?? StringComparer.Ordinal);
            foreach (var field in _fields)
                copy[field.Key] = field.Value ?? "";
            return copy;
        }

        /// <summary>Creates a payload from string-compatible fields.</summary>
        public static CultMeshOperationPayload FromStrings(IEnumerable<KeyValuePair<string, string>>? fields)
        {
            return new CultMeshOperationPayload(fields);
        }

        /// <inheritdoc />
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return _fields.GetEnumerator();
        }

        /// <inheritdoc />
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>
    /// Names a reusable projection from typed source state into derived state.
    /// </summary>
    public sealed class CultMeshProjectionRecipe<TParameters, TResult>
    {
        private readonly Func<TParameters, CultMeshQueryContext, Task<TResult>> _project;
        private readonly Func<TParameters, CultMeshQueryContext, Observable<TResult>>? _watch;

        /// <summary>Creates a projection recipe.</summary>
        public CultMeshProjectionRecipe(
            string projectionId,
            IEnumerable<CultMeshProjectionSource> sources,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> project,
            CultMeshRouteHint? routeHint = null,
            Func<TParameters, CultMeshQueryContext, Observable<TResult>>? watch = null)
        {
            ProjectionId = RequireNonEmpty(projectionId, nameof(projectionId));
            Sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
            _project = project ?? throw new ArgumentNullException(nameof(project));
            RouteHint = routeHint ?? CultMeshRouteHint.Automatic;
            _watch = watch;
        }

        /// <summary>Gets the semantic projection id.</summary>
        public string ProjectionId { get; }

        /// <summary>Gets the typed state sources this projection depends on.</summary>
        public IReadOnlyList<CultMeshProjectionSource> Sources { get; }

        /// <summary>Gets the preferred or observed route for the projection.</summary>
        public CultMeshRouteHint RouteHint { get; }

        /// <summary>Projects derived state once.</summary>
        public Task<TResult> ProjectAsync(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return _project(parameters, ResolveContext(context));
        }

        /// <summary>Projects derived state once for one runtime using default context.</summary>
        public Task<TResult> ProjectAsync(TParameters parameters, string runtimeId)
        {
            return ProjectAsync(parameters, CultMeshQueryContext.ForRuntime(runtimeId));
        }

        /// <summary>Watches projection results when the recipe supports reactive execution.</summary>
        public Observable<TResult> Watch(TParameters parameters, CultMeshQueryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (_watch == null)
            {
                throw new NotSupportedException($"Projection recipe '{ProjectionId}' does not support reactive watches.");
            }

            return _watch(parameters, ResolveContext(context));
        }

        /// <summary>Views this projection recipe as a typed query surface.</summary>
        public CultMeshQuerySurface<TParameters, TResult> AsQuerySurface()
        {
            return new CultMeshQuerySurface<TParameters, TResult>(
                ProjectionId,
                ProjectAsync,
                _watch == null ? null : Watch,
                Sources,
                RouteHint);
        }

        private CultMeshQueryContext ResolveContext(CultMeshQueryContext context)
        {
            return context.RouteHint.Kind == CultMeshLocalityKind.Automatic &&
                   RouteHint.Kind != CultMeshLocalityKind.Automatic
                ? context.WithRoute(RouteHint)
                : context;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Describes one column in a native CultMesh slice view.
    /// </summary>
    public sealed class CultMeshNativeSliceColumn
    {
        /// <summary>Creates a native slice column descriptor.</summary>
        public CultMeshNativeSliceColumn(string name, string valueType, int elementSizeBytes)
        {
            Name = RequireNonEmpty(name, nameof(name));
            ValueType = RequireNonEmpty(valueType, nameof(valueType));
            if (elementSizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elementSizeBytes), "Element size must be positive.");
            }

            ElementSizeBytes = elementSizeBytes;
        }

        /// <summary>Gets the column name.</summary>
        public string Name { get; }

        /// <summary>Gets the schema value type for each element.</summary>
        public string ValueType { get; }

        /// <summary>Gets the size of one element in bytes.</summary>
        public int ElementSizeBytes { get; }

        /// <summary>Creates a native slice column descriptor for an unmanaged CLR value type.</summary>
        public static CultMeshNativeSliceColumn For<TValue>(string name) where TValue : unmanaged
        {
            return new CultMeshNativeSliceColumn(name, typeof(TValue).FullName ?? typeof(TValue).Name, SizeOf<TValue>());
        }

        private static int SizeOf<TValue>() where TValue : unmanaged
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<TValue>();
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Describes a typed native slice view exposed by CultMesh.
    /// </summary>
    public sealed class CultMeshNativeSliceViewDescriptor
    {
        /// <summary>Creates a native slice view descriptor.</summary>
        public CultMeshNativeSliceViewDescriptor(
            string viewId,
            string schemaId,
            int rowCount,
            IEnumerable<CultMeshNativeSliceColumn> columns,
            CultMeshRouteHint? route = null,
            string? nativeHandle = null)
        {
            ViewId = RequireNonEmpty(viewId, nameof(viewId));
            SchemaId = RequireNonEmpty(schemaId, nameof(schemaId));
            if (rowCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rowCount), "Row count cannot be negative.");
            }

            RowCount = rowCount;
            Columns = columns?.ToArray() ?? throw new ArgumentNullException(nameof(columns));
            Route = route ?? CultMeshRouteHint.Automatic;
            NativeHandle = nativeHandle;
        }

        /// <summary>Gets the semantic view id.</summary>
        public string ViewId { get; }

        /// <summary>Gets the schema id for the rows in this view.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the number of rows in this view.</summary>
        public int RowCount { get; }

        /// <summary>Gets column descriptors in view order.</summary>
        public IReadOnlyList<CultMeshNativeSliceColumn> Columns { get; }

        /// <summary>Gets the route that exposes this view.</summary>
        public CultMeshRouteHint Route { get; }

        /// <summary>Gets an optional runtime-specific native handle.</summary>
        public string? NativeHandle { get; }

        /// <summary>Gets the estimated byte stride for one row when all columns are densely packed.</summary>
        public int DenseRowStrideBytes => Columns.Sum(column => column.ElementSizeBytes);

        /// <summary>Finds a column descriptor by name.</summary>
        public CultMeshNativeSliceColumn? FindColumn(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value must be non-empty.", nameof(name));
            return Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal));
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    /// <summary>
    /// Inspectable metadata for a native slice view.
    /// </summary>
    public sealed class CultMeshNativeSliceViewDiagnostic
    {
        /// <summary>Creates native slice view diagnostics.</summary>
        public CultMeshNativeSliceViewDiagnostic(CultMeshNativeSliceViewDescriptor view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            ViewId = view.ViewId;
            SchemaId = view.SchemaId;
            RowCount = view.RowCount;
            Columns = view.Columns.ToArray();
            Route = view.Route;
            NativeHandle = view.NativeHandle;
            DenseRowStrideBytes = view.DenseRowStrideBytes;
        }

        /// <summary>Gets the semantic view id.</summary>
        public string ViewId { get; }

        /// <summary>Gets the schema id for rows in this view.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the row count advertised by this view.</summary>
        public int RowCount { get; }

        /// <summary>Gets column descriptors in view order.</summary>
        public IReadOnlyList<CultMeshNativeSliceColumn> Columns { get; }

        /// <summary>Gets the route that exposes this view.</summary>
        public CultMeshRouteHint Route { get; }

        /// <summary>Gets an optional runtime-specific native handle.</summary>
        public string? NativeHandle { get; }

        /// <summary>Gets the estimated byte stride for one densely packed row.</summary>
        public int DenseRowStrideBytes { get; }
    }
}
