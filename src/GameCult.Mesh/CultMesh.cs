using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Options for creating a local CultMesh node.
    /// </summary>
    public sealed class CultMeshNodeOptions
    {
        /// <summary>
        /// Gets or sets the cache-open options used for the underlying CultCache.
        /// </summary>
        public CultCacheOpenOptions? CacheOptions { get; set; }

        /// <summary>
        /// Gets or sets the server security configuration. When omitted, development defaults are used.
        /// </summary>
        public ServerSecurityOptions? Security { get; set; }

        /// <summary>
        /// Gets or sets whether the server should start immediately after creation.
        /// </summary>
        public bool StartServer { get; set; } = true;

        /// <summary>
        /// Gets or sets the mesh database sharding options used for the hosted CultCache.
        /// </summary>
        public CultNetDatabaseOptions? DatabaseOptions { get; set; }

        /// <summary>
        /// Gets or sets whether CultMesh should attach a file-backed authoritative shard-log store when none is supplied.
        /// </summary>
        public bool EnableDurableShardLogs { get; set; }

        /// <summary>
        /// Gets or sets the directory used for file-backed authoritative shard logs.
        /// </summary>
        public string? ShardLogPath { get; set; }

        /// <summary>
        /// Gets or sets the database server bridge options used for shard routing and forwarding.
        /// </summary>
        public CultNetDatabaseServerOptions? DatabaseServerOptions { get; set; }

        /// <summary>
        /// Gets or sets an optional callback used to customize the server before start.
        /// </summary>
        public Action<Server>? ConfigureServer { get; set; }

        internal CultNetHostOptions ToCultNetOptions(string cachePath)
        {
            return new CultNetHostOptions
            {
                CacheOptions = CacheOptions,
                Security = Security,
                StartServer = StartServer,
                DatabaseOptions = CreateDatabaseOptions(cachePath),
                DatabaseServerOptions = DatabaseServerOptions,
                ConfigureServer = ConfigureServer
            };
        }

        private CultNetDatabaseOptions? CreateDatabaseOptions(string cachePath)
        {
            if (!EnableDurableShardLogs)
            {
                return DatabaseOptions;
            }

            var source = DatabaseOptions ?? new CultNetDatabaseOptions();
            if (source.MutationLogStore != null)
            {
                return source;
            }

            return new CultNetDatabaseOptions
            {
                RuntimeId = source.RuntimeId,
                Shards = source.Shards,
                ClientAuthorityScopes = source.ClientAuthorityScopes,
                DocumentRegistry = source.DocumentRegistry,
                MutationLogStore = new CultNetFileShardMutationLogStore(ResolveShardLogPath(cachePath))
            };
        }

        private string ResolveShardLogPath(string cachePath)
        {
            if (!string.IsNullOrWhiteSpace(ShardLogPath))
            {
                return ShardLogPath!;
            }

            var fullPath = Path.GetFullPath(cachePath);
            var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var name = Path.GetFileNameWithoutExtension(fullPath);
            return Path.Combine(directory, name + ".cultmesh", "shard-logs");
        }
    }

    /// <summary>
    /// A locally hosted CultMesh node over CultCache, CultNet transport, and the mesh database facade.
    /// </summary>
    public sealed class CultMeshNode : IDisposable
    {
        private readonly CultNetHost _host;

        internal CultMeshNode(CultNetHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Gets the durable cache used by the node.
        /// </summary>
        public CultCache Cache => _host.Cache;

        /// <summary>
        /// Gets the canonical single-file MessagePack store, when one is attached.
        /// </summary>
        public SingleFileMessagePackBackingStore? Store => _host.Store;

        /// <summary>
        /// Gets the underlying CultNet server.
        /// </summary>
        public Server Server => _host.Server;

        /// <summary>
        /// Gets the distributed realtime database facade.
        /// </summary>
        public CultNetDatabase Database => _host.Database;

        /// <summary>
        /// Gets the schema-v0 server bridge for distributed database messages.
        /// </summary>
        public CultNetDatabaseServer DatabaseServer => _host.DatabaseServer;

        /// <summary>
        /// Starts the underlying server.
        /// </summary>
        public void Start()
        {
            _host.Start();
        }

        /// <summary>
        /// Flushes the durable cache.
        /// </summary>
        public Task FlushAsync(bool soft = false)
        {
            return _host.FlushAsync(soft);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _host.Dispose();
        }
    }

    /// <summary>
    /// Parsed CultMesh RUDP endpoint.
    /// </summary>
    public sealed class CultMeshRudpEndpoint
    {
        /// <summary>
        /// Creates a parsed RUDP endpoint.
        /// </summary>
        public CultMeshRudpEndpoint(string host, int port, string uri)
        {
            Host = string.IsNullOrWhiteSpace(host)
                ? throw new ArgumentException("Host must be non-empty.", nameof(host))
                : host;
            if (port <= 0 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
            }

            Port = port;
            Uri = string.IsNullOrWhiteSpace(uri)
                ? throw new ArgumentException("Uri must be non-empty.", nameof(uri))
                : uri;
        }

        /// <summary>Gets the endpoint host.</summary>
        public string Host { get; }
        /// <summary>Gets the endpoint port.</summary>
        public int Port { get; }
        /// <summary>Gets the normalized rudp:// URI.</summary>
        public string Uri { get; }
    }

    /// <summary>
    /// Options for CultMesh-branded RUDP socket helpers.
    /// </summary>
    public sealed class CultMeshRudpSocketOptions
    {
        /// <summary>Gets or sets the local bind host.</summary>
        public string BindHost { get; set; } = "127.0.0.1";
        /// <summary>Gets or sets the local bind port.</summary>
        public int BindPort { get; set; }
        /// <summary>Gets or sets a caller-owned bound socket.</summary>
        public Socket? Socket { get; set; }
        /// <summary>Gets or sets the first local packet sequence.</summary>
        public uint InitialSequence { get; set; } = 1;
        /// <summary>Gets or sets the reliable resend delay in milliseconds.</summary>
        public long ResendDelayMs { get; set; } = 250;
        /// <summary>Gets or sets the advertised transport id.</summary>
        public string TransportId { get; set; } = "rudp";
        /// <summary>Gets or sets the advertised maximum payload size.</summary>
        public int? MaxPayloadBytes { get; set; }
        /// <summary>Gets or sets the maximum RUDP fragment size.</summary>
        public int? MaxFragmentBytes { get; set; }
        /// <summary>Gets or sets the maximum pending reliable packet count.</summary>
        public int? MaxPendingReliablePackets { get; set; }
        /// <summary>Gets or sets the advertised reconnect policy.</summary>
        public CultNetReconnectPolicy? ReconnectPolicy { get; set; }
    }

    /// <summary>
    /// Options for CultMesh-branded connected RUDP client helpers.
    /// </summary>
    public sealed class CultMeshRudpClientOptions
    {
        /// <summary>Gets or sets the socket construction and transport profile options.</summary>
        public CultMeshRudpSocketOptions SocketOptions { get; set; } = new();
        /// <summary>Gets or sets the handshake payload.</summary>
        public byte[] ConnectPayload { get; set; } = Array.Empty<byte>();
        /// <summary>Gets or sets the handshake timeout.</summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Gets or sets the handshake polling interval.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(5);
    }

    /// <summary>
    /// Public CultMesh entrypoints.
    /// </summary>
    public static class CultMesh
    {
        /// <summary>
        /// Creates a local CultMesh node with a durable CultCache and the canonical MessagePack backing store.
        /// </summary>
        public static async Task<CultMeshNode> CreateNodeAsync(string cachePath, CultMeshNodeOptions? options = null)
        {
            var host = await CultNetLocal.CreateHostAsync(
                cachePath,
                (options ?? new CultMeshNodeOptions()).ToCultNetOptions(cachePath)).ConfigureAwait(false);
            return new CultMeshNode(host);
        }

        /// <summary>
        /// Creates and starts a local CultMesh node with development-friendly defaults.
        /// </summary>
        public static Task<CultMeshNode> StartNodeAsync(string cachePath, CultMeshNodeOptions? options = null)
        {
            options ??= new CultMeshNodeOptions();
            options.StartServer = true;
            return CreateNodeAsync(cachePath, options);
        }

        /// <summary>
        /// Creates a local reactive catalog for discovered Verses.
        /// </summary>
        public static CultMeshVerseCatalog CreateVerseCatalog()
        {
            return new CultMeshVerseCatalog();
        }

        /// <summary>
        /// Creates a local reactive catalog for discovered peers.
        /// </summary>
        public static CultMeshPeerCatalog CreatePeerCatalog()
        {
            return new CultMeshPeerCatalog();
        }

        /// <summary>
        /// Creates a local authority lease catalog.
        /// </summary>
        public static CultMeshAuthorityLeaseCatalog CreateAuthorityLeaseCatalog()
        {
            return new CultMeshAuthorityLeaseCatalog();
        }

        /// <summary>
        /// Creates a local stream catalog for zero-copy-oriented audio, video, tensor, and byte frame streams.
        /// </summary>
        public static CultMeshStreamCatalog CreateStreamCatalog()
        {
            return new CultMeshStreamCatalog();
        }

        /// <summary>
        /// Creates a managed Verse context for generated domain sugar.
        /// </summary>
        public static CultMeshVerse Verse(
            string verseId,
            string runtimeId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshAuthorityClaim>? claims = null)
        {
            return new CultMeshVerse(new CultMeshVerseContext(verseId, runtimeId, routeHint, claims));
        }

        /// <summary>
        /// Creates a managed Verse context for generated domain sugar.
        /// </summary>
        public static Task<CultMeshVerse> ConnectVerseAsync(
            string verseId,
            string runtimeId,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshAuthorityClaim>? claims = null)
        {
            return Task.FromResult(Verse(verseId, runtimeId, routeHint, claims));
        }

        /// <summary>
        /// Starts a fluent typed operation context for one runtime.
        /// </summary>
        public static CultMeshOperationContextBuilder OperationContextFor(string runtimeId)
        {
            return new CultMeshOperationContextBuilder(runtimeId);
        }

        /// <summary>
        /// Starts a fluent typed query context for one runtime.
        /// </summary>
        public static CultMeshQueryContextBuilder QueryContextFor(string runtimeId)
        {
            return new CultMeshQueryContextBuilder(runtimeId);
        }

        /// <summary>
        /// Binds a typed operation handle to a Verse context.
        /// </summary>
        public static CultMeshBoundOperationHandle<TRequest, TResponse> BindOperation<TRequest, TResponse>(
            CultMeshVerseContext context,
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            return new CultMeshBoundOperationHandle<TRequest, TResponse>(context, operation);
        }

        /// <summary>
        /// Binds a typed operation handle to a Verse.
        /// </summary>
        public static CultMeshBoundOperationHandle<TRequest, TResponse> BindOperation<TRequest, TResponse>(
            CultMeshVerse verse,
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindOperation(verse.Context, operation);
        }

        /// <summary>
        /// Binds a typed query surface to a Verse context.
        /// </summary>
        public static CultMeshBoundQuerySurface<TParameters, TResult> BindQuery<TParameters, TResult>(
            CultMeshVerseContext context,
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            return new CultMeshBoundQuerySurface<TParameters, TResult>(context, query);
        }

        /// <summary>
        /// Binds a typed query surface to a Verse.
        /// </summary>
        public static CultMeshBoundQuerySurface<TParameters, TResult> BindQuery<TParameters, TResult>(
            CultMeshVerse verse,
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindQuery(verse.Context, query);
        }

        /// <summary>
        /// Binds a typed live feed to a Verse context.
        /// </summary>
        public static CultMeshBoundLiveFeed<TParameters, TResult> BindLiveFeed<TParameters, TResult>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            return new CultMeshBoundLiveFeed<TParameters, TResult>(context, feed);
        }

        /// <summary>
        /// Binds a typed live feed to a Verse.
        /// </summary>
        public static CultMeshBoundLiveFeed<TParameters, TResult> BindLiveFeed<TParameters, TResult>(
            CultMeshVerse verse,
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindLiveFeed(verse.Context, feed);
        }

        /// <summary>
        /// Binds a typed document live feed to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerseContext context,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed)
            where TDocument : class
        {
            return new CultMeshDocumentHandle<TDocument>(BindLiveFeed(context, feed));
        }

        /// <summary>
        /// Binds a typed document live feed to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> BindDocument<TDocument>(
            CultMeshVerse verse,
            CultMeshLiveFeed<CultMeshDocumentQueryParameters, TDocument> feed)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindDocument(verse.Context, feed);
        }

        /// <summary>
        /// Binds a typed state pointer to a Verse context.
        /// </summary>
        public static CultMeshBoundStatePointer<TValue> BindStatePointer<TValue>(
            CultMeshVerseContext context,
            CultMeshStatePointer<TValue> pointer)
        {
            return new CultMeshBoundStatePointer<TValue>(context, pointer);
        }

        /// <summary>
        /// Binds a typed state pointer to a Verse.
        /// </summary>
        public static CultMeshBoundStatePointer<TValue> BindStatePointer<TValue>(
            CultMeshVerse verse,
            CultMeshStatePointer<TValue> pointer)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindStatePointer(verse.Context, pointer);
        }

        /// <summary>
        /// Binds a mutable typed state pointer to a Verse context.
        /// </summary>
        public static CultMeshBoundMutableStatePointer<TValue> BindMutableStatePointer<TValue>(
            CultMeshVerseContext context,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            return new CultMeshBoundMutableStatePointer<TValue>(context, pointer);
        }

        /// <summary>
        /// Binds a mutable typed state pointer to a Verse.
        /// </summary>
        public static CultMeshBoundMutableStatePointer<TValue> BindMutableStatePointer<TValue>(
            CultMeshVerse verse,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return BindMutableStatePointer(verse.Context, pointer);
        }

        /// <summary>
        /// Creates a typed state pointer to Verse state.
        /// </summary>
        public static CultMeshStatePointer<TValue> StatePointer<TValue>(
            string pointerId,
            Func<Task<TValue?>> resolve,
            Func<R3.Observable<TValue>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a typed state pointer to Verse state that can resolve through a query context.
        /// </summary>
        public static CultMeshStatePointer<TValue> StatePointer<TValue>(
            string pointerId,
            Func<CultMeshQueryContext, Task<TValue?>> resolve,
            Func<CultMeshQueryContext, R3.Observable<TValue>> watch,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a mutable typed state pointer to Verse state.
        /// </summary>
        public static CultMeshMutableStatePointer<TValue> MutableStatePointer<TValue>(
            string pointerId,
            Func<Task<TValue?>> resolve,
            Func<R3.Observable<TValue>> watch,
            Func<TValue, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshMutableStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                replace,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a mutable typed state pointer to Verse state that can operate through a query context.
        /// </summary>
        public static CultMeshMutableStatePointer<TValue> MutableStatePointer<TValue>(
            string pointerId,
            Func<CultMeshQueryContext, Task<TValue?>> resolve,
            Func<CultMeshQueryContext, R3.Observable<TValue>> watch,
            Func<CultMeshQueryContext, TValue, Task> replace,
            CultMeshRouteHint? routeHint = null,
            IEnumerable<CultMeshProjectionSource>? sources = null)
        {
            return new CultMeshMutableStatePointer<TValue>(
                pointerId,
                resolve,
                watch,
                replace,
                routeHint,
                sources);
        }

        /// <summary>
        /// Creates a typed projection source descriptor.
        /// </summary>
        public static CultMeshProjectionSource ProjectionSource(
            string sourceId,
            string? schemaId = null,
            string? description = null)
        {
            return new CultMeshProjectionSource(sourceId, schemaId, description);
        }

        /// <summary>
        /// Creates a named state-reference resolver for portable UI/tool surfaces.
        /// </summary>
        public static CultMeshStateRefResolver StateRefResolver(
            string resolverId,
            Func<string, string?> resolve,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));
            return new CultMeshStateRefResolver(
                resolverId,
                (stateRef, _context) => resolve(stateRef),
                sources,
                routeHint);
        }

        /// <summary>
        /// Creates a named state-reference resolver for portable UI/tool surfaces.
        /// </summary>
        public static CultMeshStateRefResolver StateRefResolver(
            string resolverId,
            Func<string, CultMeshQueryContext, string?> resolve,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshStateRefResolver(resolverId, resolve, sources, routeHint);
        }

        /// <summary>
        /// Describes a state-reference resolver for catalogs and tools.
        /// </summary>
        public static CultMeshStateRefResolverDiagnostic DescribeStateRefResolver(
            CultMeshStateRefResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            return new CultMeshStateRefResolverDiagnostic(
                resolver.ResolverId,
                resolver.RouteHint,
                resolver.Sources);
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a typed state pointer.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding<TValue>(
            string targetProp,
            CultMeshStatePointer<TValue> pointer)
        {
            return CultMeshStateBindingDescriptor.FromPointer(targetProp, pointer);
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a mutable typed state pointer.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding<TValue>(
            string targetProp,
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return CultMeshStateBindingDescriptor.FromPointer(targetProp, pointer.AsStatePointer());
        }

        /// <summary>
        /// Creates a UI/tool binding descriptor from a component property to a state pointer id.
        /// </summary>
        public static CultMeshStateBindingDescriptor StateBinding(
            string targetProp,
            string pointerId,
            string? sourceId = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshStateBindingDescriptor(targetProp, pointerId, sourceId, schemaId, routeHint);
        }

        /// <summary>
        /// Creates transport-friendly fields from a state binding descriptor.
        /// </summary>
        public static CultMeshStateBindingRecord StateBindingRecord(CultMeshStateBindingDescriptor? binding)
        {
            return CultMeshStateBindingRecord.FromBinding(binding);
        }

        /// <summary>
        /// Creates transport-friendly state binding fields directly.
        /// </summary>
        public static CultMeshStateBindingRecord StateBindingRecord(
            string? targetProp,
            string? pointerId,
            string? sourceId = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            return new CultMeshStateBindingRecord(
                targetProp,
                pointerId,
                sourceId,
                schemaId,
                routeKind,
                routeDescription);
        }

        /// <summary>
        /// Creates a UI/tool command binding descriptor from a typed operation handle.
        /// </summary>
        public static CultMeshOperationBindingDescriptor OperationBinding<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return CultMeshOperationBindingDescriptor.FromOperation(operation, label, schemaId, routeHint);
        }

        /// <summary>
        /// Creates a UI/tool command binding descriptor from a typed operation id.
        /// </summary>
        public static CultMeshOperationBindingDescriptor OperationBinding(
            string operationId,
            string? label = null,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshOperationBindingDescriptor(operationId, label, schemaId, routeHint);
        }

        /// <summary>
        /// Creates transport-friendly fields from an operation binding descriptor.
        /// </summary>
        public static CultMeshOperationBindingRecord OperationBindingRecord(
            CultMeshOperationBindingDescriptor? binding)
        {
            return CultMeshOperationBindingRecord.FromBinding(binding);
        }

        /// <summary>
        /// Creates transport-friendly operation binding fields directly.
        /// </summary>
        public static CultMeshOperationBindingRecord OperationBindingRecord(
            string? operationId,
            string? label = null,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null)
        {
            return new CultMeshOperationBindingRecord(
                operationId,
                label,
                schemaId,
                routeKind,
                routeDescription);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from an advertised operation binding.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation(
            CultMeshOperationBindingDescriptor binding,
            string? idempotencyKey = null)
        {
            return CultMeshOperationInvocationDescriptor.FromBinding(binding, idempotencyKey);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from a typed operation handle.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            return CultMeshOperationInvocationDescriptor.FromOperation(
                operation,
                schemaId,
                routeHint,
                idempotencyKey);
        }

        /// <summary>
        /// Creates a concrete invocation descriptor from a typed operation id.
        /// </summary>
        public static CultMeshOperationInvocationDescriptor OperationInvocation(
            string operationId,
            string? schemaId = null,
            CultMeshRouteHint? routeHint = null,
            string? idempotencyKey = null)
        {
            return new CultMeshOperationInvocationDescriptor(operationId, schemaId, routeHint, idempotencyKey);
        }

        /// <summary>
        /// Creates transport-friendly fields from a route hint.
        /// </summary>
        public static CultMeshRouteRecord RouteRecord(CultMeshRouteHint? routeHint)
        {
            return CultMeshRouteRecord.FromRoute(routeHint);
        }

        /// <summary>
        /// Creates transport-friendly route fields directly.
        /// </summary>
        public static CultMeshRouteRecord RouteRecord(string? kind, string? description = null)
        {
            return new CultMeshRouteRecord(kind, description);
        }

        /// <summary>
        /// Creates transport-friendly fields from a typed operation invocation.
        /// </summary>
        public static CultMeshOperationInvocationRecord OperationInvocationRecord(
            CultMeshOperationInvocationDescriptor? invocation,
            string? fallbackOperationId = null,
            string? fallbackSchemaId = null,
            CultMeshRouteHint? fallbackRouteHint = null,
            string? fallbackIdempotencyKey = null)
        {
            return CultMeshOperationInvocationRecord.FromInvocation(
                invocation,
                fallbackOperationId,
                fallbackSchemaId,
                fallbackRouteHint,
                fallbackIdempotencyKey);
        }

        /// <summary>
        /// Creates transport-friendly operation invocation fields directly.
        /// </summary>
        public static CultMeshOperationInvocationRecord OperationInvocationRecord(
            string? operationId,
            string? schemaId = null,
            string? routeKind = null,
            string? routeDescription = null,
            string? idempotencyKey = null)
        {
            return new CultMeshOperationInvocationRecord(
                operationId,
                schemaId,
                routeKind,
                routeDescription,
                idempotencyKey);
        }

        /// <summary>
        /// Creates an empty shared operation payload.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload()
        {
            return CultMeshOperationPayload.Empty;
        }

        /// <summary>
        /// Creates a shared operation payload from string-compatible fields.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload(
            IEnumerable<KeyValuePair<string, string>>? fields)
        {
            return CultMeshOperationPayload.FromStrings(fields);
        }

        /// <summary>
        /// Creates a shared operation payload from key/value pairs.
        /// </summary>
        public static CultMeshOperationPayload OperationPayload(params (string Key, string Value)[] fields)
        {
            var pairs = fields?.Select(field => new KeyValuePair<string, string>(field.Key, field.Value));
            return CultMeshOperationPayload.FromStrings(pairs);
        }

        /// <summary>
        /// Creates a reusable projection recipe over typed source state.
        /// </summary>
        public static CultMeshProjectionRecipe<TParameters, TResult> ProjectionRecipe<TParameters, TResult>(
            string projectionId,
            IEnumerable<CultMeshProjectionSource> sources,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> project,
            CultMeshRouteHint? routeHint = null,
            Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>>? watch = null)
        {
            return new CultMeshProjectionRecipe<TParameters, TResult>(
                projectionId,
                sources,
                project,
                routeHint,
                watch);
        }

        /// <summary>
        /// Creates a typed live feed surface for coherent client snapshots.
        /// </summary>
        public static CultMeshLiveFeed<TParameters, TResult> LiveFeed<TParameters, TResult>(
            string feedId,
            Func<TParameters, CultMeshQueryContext, Task<TResult>> snapshot,
            Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>>? watch = null,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
        {
            return new CultMeshLiveFeed<TParameters, TResult>(
                feedId,
                snapshot,
                watch,
                sources,
                routeHint);
        }

        /// <summary>
        /// Creates a typed document handle from snapshot/watch delegates and binds it to a Verse context.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerseContext context,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (latest == null) throw new ArgumentNullException(nameof(latest));
            if (watch == null) throw new ArgumentNullException(nameof(watch));

            var feed = LiveFeed<CultMeshDocumentQueryParameters, TDocument>(
                documentId,
                (_parameters, queryContext) => latest(queryContext),
                (_parameters, queryContext) => watch(queryContext),
                sources,
                routeHint);
            return BindDocument(context, feed);
        }

        /// <summary>
        /// Creates a typed document handle from snapshot/watch delegates and binds it to a Verse.
        /// </summary>
        public static CultMeshDocumentHandle<TDocument> Document<TDocument>(
            string documentId,
            CultMeshVerse verse,
            Func<CultMeshQueryContext, Task<TDocument>> latest,
            Func<CultMeshQueryContext, R3.Observable<TDocument>> watch,
            IEnumerable<CultMeshProjectionSource>? sources = null,
            CultMeshRouteHint? routeHint = null)
            where TDocument : class
        {
            if (verse == null) throw new ArgumentNullException(nameof(verse));
            return Document(documentId, verse.Context, latest, watch, sources, routeHint);
        }

        /// <summary>
        /// Describes a typed live feed surface for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshLiveFeedDiagnostic DescribeLiveFeed<TParameters, TResult>(
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            return new CultMeshLiveFeedDiagnostic(feed.FeedId, feed.RouteHint, feed.Sources);
        }

        /// <summary>
        /// Describes a typed query surface for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshQuerySurfaceDiagnostic DescribeQuerySurface<TParameters, TResult>(
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            return new CultMeshQuerySurfaceDiagnostic(query.QueryId, query.RouteHint, query.Sources);
        }

        /// <summary>
        /// Describes a typed operation handle for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshOperationHandleDiagnostic DescribeOperationHandle<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshOperationHandleDiagnostic(operation.OperationId);
        }

        /// <summary>
        /// Describes a typed state pointer for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshStatePointerDiagnostic DescribeStatePointer<TValue>(
            CultMeshStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshStatePointerDiagnostic(pointer.PointerId, pointer.RouteHint, pointer.Sources);
        }

        /// <summary>
        /// Describes a mutable typed state pointer for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshStatePointerDiagnostic DescribeStatePointer<TValue>(
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshStatePointerDiagnostic(pointer.PointerId, pointer.RouteHint, pointer.Sources);
        }

        /// <summary>
        /// Describes a native slice view for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshNativeSliceViewDiagnostic DescribeNativeSliceView(
            CultMeshNativeSliceViewDescriptor view)
        {
            return new CultMeshNativeSliceViewDiagnostic(view);
        }

        /// <summary>
        /// Describes a typed projection recipe for tooling, UI, and diagnostics.
        /// </summary>
        public static CultMeshProjectionRecipeDiagnostic DescribeProjectionRecipe<TParameters, TResult>(
            CultMeshProjectionRecipe<TParameters, TResult> recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            return new CultMeshProjectionRecipeDiagnostic(recipe.ProjectionId, recipe.RouteHint, recipe.Sources);
        }

        /// <summary>
        /// Describes a typed query surface as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshQuerySurface<TParameters, TResult> query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.Query, query.QueryId, query.RouteHint, query.Sources);
        }

        /// <summary>
        /// Describes a typed operation handle as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TRequest, TResponse>(
            CultMeshOperationHandle<TRequest, TResponse> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.Operation, operation.OperationId);
        }

        /// <summary>
        /// Describes a typed projection recipe as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshProjectionRecipe<TParameters, TResult> recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.ProjectionRecipe, recipe.ProjectionId, recipe.RouteHint, recipe.Sources);
        }

        /// <summary>
        /// Describes a typed live feed as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TParameters, TResult>(
            CultMeshLiveFeed<TParameters, TResult> feed)
        {
            if (feed == null) throw new ArgumentNullException(nameof(feed));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.LiveFeed, feed.FeedId, feed.RouteHint, feed.Sources);
        }

        /// <summary>
        /// Describes a typed state pointer as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TValue>(
            CultMeshStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.StatePointer,
                pointer.PointerId,
                pointer.RouteHint,
                pointer.Sources);
        }

        /// <summary>
        /// Describes a mutable typed state pointer as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface<TValue>(
            CultMeshMutableStatePointer<TValue> pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            return new CultMeshSurfaceDiagnostic(
                CultMeshSurfaceKind.StatePointer,
                pointer.PointerId,
                pointer.RouteHint,
                pointer.Sources);
        }

        /// <summary>
        /// Describes a native slice view as one entry in a surface catalog.
        /// </summary>
        public static CultMeshSurfaceDiagnostic DescribeSurface(
            CultMeshNativeSliceViewDescriptor view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            return new CultMeshSurfaceDiagnostic(CultMeshSurfaceKind.NativeSliceView, view.ViewId, view.Route);
        }

        /// <summary>
        /// Creates a typed surface catalog diagnostic for tooling, UI, and generated bindings.
        /// </summary>
        public static CultMeshSurfaceCatalogDiagnostic DescribeSurfaceCatalog(
            string catalogId,
            IEnumerable<CultMeshSurfaceDiagnostic> surfaces)
        {
            return new CultMeshSurfaceCatalogDiagnostic(catalogId, surfaces);
        }

        /// <summary>
        /// Creates a kind-indexed surface catalog diagnostic for generated bindings, UI, and tools.
        /// </summary>
        public static CultMeshSurfaceCatalogIndexDiagnostic DescribeSurfaceCatalogIndex(
            CultMeshSurfaceCatalogDiagnostic catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            return catalog.IndexByKind();
        }

        /// <summary>
        /// Creates a timer-backed query watcher for runtimes that do not yet have native reactive transport.
        /// </summary>
        public static Func<TParameters, CultMeshQueryContext, R3.Observable<TResult>> PollingQueryWatcher<TParameters, TResult>(
            Func<TParameters, CultMeshQueryContext, Task<TResult>> sample,
            CultMeshPollingWatchOptions<TResult>? options = null)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            var resolvedOptions = options ?? new CultMeshPollingWatchOptions<TResult>();
            return (parameters, context) => R3.Observable.Create<TResult>(observer =>
            {
                var watcher = new CultMeshPollingWatcher<TParameters, TResult>(
                    sample,
                    parameters,
                    context,
                    resolvedOptions);
                var subscription = watcher.Observable.Subscribe(observer);
                return new CultMeshCompositeDisposable(subscription, watcher);
            });
        }

        /// <summary>
        /// Parses a rudp://host:port endpoint into its host/port parts.
        /// </summary>
        public static CultMeshRudpEndpoint ParseRudpEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Value must be non-empty.", nameof(endpoint));
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "rudp", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("RUDP endpoint must use the rudp:// scheme.", nameof(endpoint));
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > 65535)
            {
                throw new ArgumentException("RUDP endpoint must include a host and port.", nameof(endpoint));
            }

            var host = uri.Host;
            var uriHost = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)
                ? $"[{host}]"
                : host;
            return new CultMeshRudpEndpoint(host, uri.Port, $"rudp://{uriHost}:{uri.Port}");
        }

        /// <summary>
        /// Creates a CultNet RUDP server transport with CultMesh-branded defaults.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpServer(
            string runtimeId,
            uint connectionId,
            CultMeshRudpSocketOptions? options = null)
        {
            options ??= new CultMeshRudpSocketOptions();
            return new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = runtimeId,
                Socket = options.Socket ?? BindRudpSocket(options.BindHost, options.BindPort),
                Mode = CultNetRudpSocketMode.Server,
                ConnectionId = connectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                TransportId = options.TransportId,
                MaxPayloadBytes = options.MaxPayloadBytes,
                MaxFragmentBytes = options.MaxFragmentBytes,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                ReconnectPolicy = options.ReconnectPolicy
            });
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport for a parsed endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClient(
            string runtimeId,
            uint connectionId,
            CultMeshRudpEndpoint endpoint,
            CultMeshRudpSocketOptions? options = null)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            options ??= new CultMeshRudpSocketOptions();
            return new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
            {
                RuntimeId = runtimeId,
                Socket = options.Socket ?? BindRudpSocket(options.BindHost, options.BindPort),
                Mode = CultNetRudpSocketMode.Client,
                RemoteEndPoint = new IPEndPoint(ResolveRudpAddress(endpoint.Host), endpoint.Port),
                ConnectionId = connectionId,
                InitialSequence = options.InitialSequence,
                ResendDelayMs = options.ResendDelayMs,
                TransportId = options.TransportId,
                MaxPayloadBytes = options.MaxPayloadBytes,
                MaxFragmentBytes = options.MaxFragmentBytes,
                MaxPendingReliablePackets = options.MaxPendingReliablePackets,
                ReconnectPolicy = options.ReconnectPolicy
            });
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport for a rudp://host:port endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClient(
            string runtimeId,
            uint connectionId,
            string endpoint,
            CultMeshRudpSocketOptions? options = null)
        {
            return CreateRudpClient(runtimeId, connectionId, ParseRudpEndpoint(endpoint), options);
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport from a peer-card contact hint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClientForPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCard peer,
            CultMeshRudpSocketOptions? options = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var endpoint = peer.Endpoints.FirstOrDefault(value => value.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                throw new InvalidOperationException($"Peer {peer.PeerId} does not advertise a RUDP endpoint.");
            }

            return CreateRudpClient(runtimeId, connectionId, endpoint, options);
        }

        /// <summary>
        /// Creates a CultNet RUDP client transport from the first peer authorized for a Verse role.
        /// </summary>
        public static CultNetRudpSocketTransportConnection CreateRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityLeaseCatalog leases,
            string verseId,
            string role,
            string? shardId = null,
            DateTimeOffset? at = null,
            CultMeshRudpSocketOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (leases == null) throw new ArgumentNullException(nameof(leases));
            var peer = peers.FirstAuthorized(verseId, role, leases, shardId, at);
            if (peer == null)
            {
                throw new InvalidOperationException($"No authorized RUDP peer for role {role} in Verse {verseId}.");
            }

            return CreateRudpClientForPeer(runtimeId, connectionId, peer, options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport for a parsed endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClient(
            string runtimeId,
            uint connectionId,
            CultMeshRudpEndpoint endpoint,
            CultMeshRudpClientOptions? options = null)
        {
            options ??= new CultMeshRudpClientOptions();
            var client = CreateRudpClient(runtimeId, connectionId, endpoint, options.SocketOptions);
            if (!client.ConnectAndWait(options.ConnectPayload, options.ConnectTimeout, options.PollInterval))
            {
                client.Dispose();
                throw new TimeoutException($"Timed out waiting for RUDP client {runtimeId} to connect.");
            }

            return client;
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport for a rudp://host:port endpoint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClient(
            string runtimeId,
            uint connectionId,
            string endpoint,
            CultMeshRudpClientOptions? options = null)
        {
            return ConnectRudpClient(runtimeId, connectionId, ParseRudpEndpoint(endpoint), options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport from a peer-card contact hint.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClientForPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCard peer,
            CultMeshRudpClientOptions? options = null)
        {
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            var endpoint = peer.Endpoints.FirstOrDefault(value => value.StartsWith("rudp://", StringComparison.OrdinalIgnoreCase));
            if (endpoint == null)
            {
                throw new InvalidOperationException($"Peer {peer.PeerId} does not advertise a RUDP endpoint.");
            }

            return ConnectRudpClient(runtimeId, connectionId, endpoint, options);
        }

        /// <summary>
        /// Creates and handshakes a CultNet RUDP client transport from the first peer authorized for a Verse role.
        /// </summary>
        public static CultNetRudpSocketTransportConnection ConnectRudpClientForAuthorizedPeer(
            string runtimeId,
            uint connectionId,
            CultMeshPeerCatalog peers,
            CultMeshAuthorityLeaseCatalog leases,
            string verseId,
            string role,
            string? shardId = null,
            DateTimeOffset? at = null,
            CultMeshRudpClientOptions? options = null)
        {
            if (peers == null) throw new ArgumentNullException(nameof(peers));
            if (leases == null) throw new ArgumentNullException(nameof(leases));
            var peer = peers.FirstAuthorized(verseId, role, leases, shardId, at);
            if (peer == null)
            {
                throw new InvalidOperationException($"No authorized RUDP peer for role {role} in Verse {verseId}.");
            }

            return ConnectRudpClientForPeer(runtimeId, connectionId, peer, options);
        }

        /// <summary>
        /// Creates a committer for writing quorum simulation facts to a mesh database.
        /// </summary>
        public static CultMeshSimulationFactCommitter CreateSimulationFactCommitter(CultNetDatabase database)
        {
            return new CultMeshSimulationFactCommitter(database);
        }

        /// <summary>
        /// Creates a gameplay-facing session facade over a CultMesh node.
        /// </summary>
        public static CultMeshGameSession CreateGameSession(
            CultMeshNode node,
            CultMeshGameSessionOptions? options = null)
        {
            return new CultMeshGameSession(node, options);
        }

        /// <summary>
        /// Attaches Verse discovery responses to a CultMesh node.
        /// </summary>
        public static CultMeshVerseDiscoveryServer ServeVerseCatalog(
            CultMeshNode node,
            CultMeshVerseCatalog catalog)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return new CultMeshVerseDiscoveryServer(node.Server, catalog);
        }

        /// <summary>
        /// Creates a client for fetching Verse catalogs from discovery endpoints.
        /// </summary>
        public static CultMeshVerseDiscoveryClient CreateVerseDiscoveryClient(
            CultMeshVerseDiscoveryClientOptions? options = null)
        {
            return new CultMeshVerseDiscoveryClient(options);
        }

        /// <summary>
        /// Attaches peer exchange responses to a CultMesh node.
        /// </summary>
        public static CultMeshPeerExchangeServer ServePeerExchange(
            CultMeshNode node,
            CultMeshPeerCatalog catalog)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            return new CultMeshPeerExchangeServer(node.Server, catalog);
        }

        /// <summary>
        /// Creates a client for fetching peer cards from peer exchange endpoints.
        /// </summary>
        public static CultMeshPeerExchangeClient CreatePeerExchangeClient(
            CultMeshPeerExchangeClientOptions? options = null)
        {
            return new CultMeshPeerExchangeClient(options);
        }

        /// <summary>
        /// Creates a CultNet client configured for CultMesh use.
        /// </summary>
        public static Client CreateClient(ClientSecurityOptions? security = null, Action<Client>? configureClient = null)
        {
            return CultNetLocal.CreateClient(security, configureClient);
        }

        /// <summary>
        /// Creates and connects a CultNet client to a CultMesh node.
        /// </summary>
        public static Client ConnectClient(
            string host = "localhost",
            int port = 3075,
            ClientSecurityOptions? security = null,
            Action<Client>? configureClient = null)
        {
            return CultNetLocal.ConnectClient(host, port, security, configureClient);
        }

        private static Socket BindRudpSocket(string host, int port)
        {
            var address = ResolveRudpAddress(host);
            var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(address, port));
            socket.ReceiveTimeout = 20;
            return socket;
        }

        private static IPAddress ResolveRudpAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Value must be non-empty.", nameof(host));
            if (IPAddress.TryParse(host, out var parsed))
            {
                return parsed;
            }

            return Dns.GetHostAddresses(host).FirstOrDefault()
                   ?? throw new InvalidOperationException($"Could not resolve RUDP host {host}.");
        }
    }
}
