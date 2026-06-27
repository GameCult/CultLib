import { createSocket, type Socket } from "node:dgram";
import { decode, encode } from "@msgpack/msgpack";

import {
  CultCache,
  SingleFileMessagePackBackingStore,
  type AnyCultCacheDocumentDefinition,
  type CacheBackingStore,
  type CultCacheDocumentValue,
} from "cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetRudpSession,
  CultNetPeer,
  CultNetRudpSocketTransportConnection,
  CultNetSchemaCatalog,
  CultNetShardCatalog,
  cultNetBuiltinSchemaRegistry,
  decodeRudpPacket,
  defineCultNetDocumentBinding,
  encodeCultNetMessageForWire,
  encodeRudpPacket,
  parseCultNetMessage,
  type CultNetDocumentBinding,
  type CultNetMessage,
  type CultNetRawDocumentRecord,
  type CultNetReconnectPolicy,
  type CultNetSchemaCatalogOptions,
  type CultNetWireContract,
} from "cultnet-ts";

export interface CultMeshVec2 {
  readonly x: number;
  readonly y: number;
}

export interface CultMeshRect {
  readonly min: CultMeshVec2;
  readonly max: CultMeshVec2;
}

export interface CultMeshViewportRequest {
  readonly minX: number;
  readonly minY: number;
  readonly maxX: number;
  readonly maxY: number;
  readonly controlledEntityIndices?: readonly number[];
}

export type CultMeshLocalityKind =
  | "automatic"
  | "in-process"
  | "shared-memory"
  | "ipc"
  | "network"
  | "wasm";

export interface CultMeshRouteHint {
  readonly kind: CultMeshLocalityKind;
  readonly description?: string;
}

export interface CultMeshRouteRecord {
  readonly kind: string;
  readonly description: string;
}

export interface CultMeshAuthorityClaim {
  readonly role: string;
  readonly shardId?: string;
  readonly leaseId?: string;
}

export interface CultMeshVerseContext {
  readonly verseId: string;
  readonly runtimeId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly claims: readonly CultMeshAuthorityClaim[];
}

export interface CultMeshOperationContext {
  readonly runtimeId: string;
  readonly claims: readonly CultMeshAuthorityClaim[];
  readonly routeHint: CultMeshRouteHint;
  readonly idempotencyKey?: string;
}

export interface CultMeshOperationReceipt {
  readonly operationId: string;
  readonly accepted: boolean;
  readonly route: CultMeshRouteHint;
  readonly diagnostic?: string;
}

export interface CultMeshQueryContext {
  readonly runtimeId: string;
  readonly routeHint: CultMeshRouteHint;
}

export type CultMeshUnsubscribe = () => void;

export type CultMeshDocumentWatcher<TDocument> = (
  context: CultMeshQueryContext,
  callback: (value: TDocument) => void,
) => CultMeshUnsubscribe;

export type CultMeshDocumentReplacer<TDocument> = (
  context: CultMeshQueryContext,
  value: TDocument,
) => Promise<void>;

export type CultMeshCollectionSnapshot<TDocument> = readonly TDocument[];

export type CultMeshCollectionChangeKind = "added" | "updated" | "removed" | "reset";

export interface CultMeshCollectionChange<TDocument> {
  readonly kind: CultMeshCollectionChangeKind;
  readonly key?: string;
  readonly value?: TDocument;
}

export type CultMeshCollectionWatcher<TDocument> = (
  context: CultMeshQueryContext,
  callback: (change: CultMeshCollectionChange<TDocument>) => void,
) => CultMeshUnsubscribe;

export interface CultMeshDocumentSchemaDescriptor {
  readonly type?: string;
  readonly schemaId?: string;
  readonly schemaName?: string;
  readonly schemaVersion?: string;
}

export type CultMeshStatePointerResolver<T> = (
  context: CultMeshQueryContext,
) => Promise<T | undefined>;

export type CultMeshStatePointerWatcher<T> = (
  context: CultMeshQueryContext,
  callback: (value: T) => void,
) => CultMeshUnsubscribe;

export type CultMeshMutableStatePointerReplacer<T> = (
  context: CultMeshQueryContext,
  value: T,
) => Promise<void>;

export type CultMeshQueryWatcher<TParameters, TResult> = (
  parameters: TParameters,
  context: CultMeshQueryContext,
  callback: (value: TResult) => void,
) => CultMeshUnsubscribe;

export type CultMeshLiveFeedWatcher<TParameters, TResult> = (
  parameters: TParameters,
  context: CultMeshQueryContext,
  callback: (value: TResult) => void,
) => CultMeshUnsubscribe;

export interface CultMeshPollingWatchOptions<TResult> {
  readonly intervalMs?: number;
  readonly emitInitial?: boolean;
  readonly equals?: (left: TResult, right: TResult) => boolean;
}

export interface CultMeshNativeSliceColumn {
  readonly name: string;
  readonly valueType: string;
  readonly elementSizeBytes: number;
}

export interface CultMeshNativeSliceViewDescriptor {
  readonly viewId: string;
  readonly schemaId: string;
  readonly rowCount: number;
  readonly columns: readonly CultMeshNativeSliceColumn[];
  readonly route: CultMeshRouteHint;
  readonly nativeHandle?: string;
}

export interface CultMeshProjectionSource {
  readonly sourceId: string;
  readonly schemaId?: string;
  readonly description?: string;
}

export interface CultMeshStateBindingDescriptor {
  readonly targetProp: string;
  readonly pointerId: string;
  readonly sourceId?: string;
  readonly schemaId?: string;
  readonly routeHint: CultMeshRouteHint;
}

export interface CultMeshStateBindingRecord {
  readonly targetProp: string;
  readonly pointerId: string;
  readonly sourceId: string;
  readonly schemaId: string;
  readonly routeKind: string;
  readonly routeDescription: string;
}

export interface CultMeshOperationBindingDescriptor {
  readonly operationId: string;
  readonly label: string;
  readonly schemaId: string;
  readonly routeHint: CultMeshRouteHint;
}

export interface CultMeshOperationBindingRecord {
  readonly operationId: string;
  readonly label: string;
  readonly schemaId: string;
  readonly routeKind: string;
  readonly routeDescription: string;
}

export interface CultMeshOperationInvocationDescriptor {
  readonly operationId: string;
  readonly schemaId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly idempotencyKey?: string;
}

export interface CultMeshOperationInvocationRecord {
  readonly operationId: string;
  readonly schemaId: string;
  readonly routeKind: string;
  readonly routeDescription: string;
  readonly idempotencyKey: string;
}

export interface CultMeshOperationPayload {
  readonly fields: Readonly<Record<string, string>>;
  getString(key: string, defaultValue?: string): string;
  getInt(key: string, defaultValue?: number): number;
  getDouble(key: string, defaultValue?: number): number;
  getBoolean(key: string, defaultValue?: boolean): boolean;
  with(key: string, value: string | number | boolean): CultMeshOperationPayload;
  toRecord(): Readonly<Record<string, string>>;
}

export interface CultMeshQuerySurfaceDiagnostic {
  readonly queryId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export interface CultMeshOperationHandleDiagnostic {
  readonly operationId: string;
}

export interface CultMeshStatePointerDiagnostic {
  readonly pointerId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export interface CultMeshStateRefResolverDiagnostic {
  readonly resolverId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export interface CultMeshProjectionRecipeDiagnostic {
  readonly projectionId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export interface CultMeshLiveFeedDiagnostic {
  readonly feedId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export type CultMeshSurfaceKind =
  | "query"
  | "projection-recipe"
  | "live-feed"
  | "operation"
  | "document"
  | "collection"
  | "state-pointer"
  | "native-slice-view";

export interface CultMeshSurfaceDiagnostic {
  readonly kind: CultMeshSurfaceKind;
  readonly surfaceId: string;
  readonly routeHint: CultMeshRouteHint;
  readonly sources: readonly CultMeshProjectionSource[];
}

export interface CultMeshSurfaceCatalogDiagnostic {
  readonly catalogId: string;
  readonly surfaces: readonly CultMeshSurfaceDiagnostic[];
}

export interface CultMeshSurfaceCatalogIndexDiagnostic {
  readonly catalogId: string;
  readonly queries: readonly CultMeshSurfaceDiagnostic[];
  readonly projectionRecipes: readonly CultMeshSurfaceDiagnostic[];
  readonly liveFeeds: readonly CultMeshSurfaceDiagnostic[];
  readonly operations: readonly CultMeshSurfaceDiagnostic[];
  readonly documents: readonly CultMeshSurfaceDiagnostic[];
  readonly collections: readonly CultMeshSurfaceDiagnostic[];
  readonly statePointers: readonly CultMeshSurfaceDiagnostic[];
  readonly nativeSliceViews: readonly CultMeshSurfaceDiagnostic[];
}

export interface CultMeshNativeSliceViewDiagnostic {
  readonly viewId: string;
  readonly schemaId: string;
  readonly rowCount: number;
  readonly columns: readonly CultMeshNativeSliceColumn[];
  readonly route: CultMeshRouteHint;
  readonly nativeHandle?: string;
  readonly denseRowStrideBytes: number;
}

export class CultMeshOperationContextBuilder {
  readonly #runtimeId: string;
  #claims: CultMeshAuthorityClaim[] = [];
  #routeHint: CultMeshRouteHint = cultMeshRouteHint();
  #idempotencyKey: string | undefined;

  public constructor(runtimeId: string) {
    requireNonEmpty(runtimeId, "runtimeId");
    this.#runtimeId = runtimeId;
  }

  public claim(
    role: string,
    options: { shardId?: string; leaseId?: string } = {},
  ): this {
    this.#claims.push(cultMeshAuthorityClaim(role, options));
    return this;
  }

  public claims(claims: readonly CultMeshAuthorityClaim[]): this {
    this.#claims.push(...claims);
    return this;
  }

  public route(kind: CultMeshLocalityKind, description?: string): this {
    this.#routeHint = cultMeshRouteHint(kind, description);
    return this;
  }

  public idempotency(key: string): this {
    requireNonEmpty(key, "idempotencyKey");
    this.#idempotencyKey = key;
    return this;
  }

  public build(): CultMeshOperationContext {
    return cultMeshOperationContext(this.#runtimeId, {
      claims: this.#claims,
      routeHint: this.#routeHint,
      idempotencyKey: this.#idempotencyKey,
    });
  }
}

export class CultMeshQueryContextBuilder {
  readonly #runtimeId: string;
  #routeHint: CultMeshRouteHint = cultMeshRouteHint();

  public constructor(runtimeId: string) {
    requireNonEmpty(runtimeId, "runtimeId");
    this.#runtimeId = runtimeId;
  }

  public route(kind: CultMeshLocalityKind, description?: string): this {
    this.#routeHint = cultMeshRouteHint(kind, description);
    return this;
  }

  public build(): CultMeshQueryContext {
    return cultMeshQueryContext(this.#runtimeId, {
      routeHint: this.#routeHint,
    });
  }
}

export class CultMeshVerse {
  public constructor(public readonly context: CultMeshVerseContext) {
    requireNonEmpty(context.verseId, "verseId");
    requireNonEmpty(context.runtimeId, "runtimeId");
  }

  public get verseId(): string {
    return this.context.verseId;
  }

  public get runtimeId(): string {
    return this.context.runtimeId;
  }

  public use<TSchema>(schemaFactory: (context: CultMeshVerseContext) => TSchema): TSchema {
    return schemaFactory(this.context);
  }

  public withRoute(kind: CultMeshLocalityKind, description?: string): CultMeshVerse {
    return new CultMeshVerse({
      ...this.context,
      routeHint: cultMeshRouteHint(kind, description),
    });
  }

  public withClaim(role: string, options: { shardId?: string; leaseId?: string } = {}): CultMeshVerse {
    return new CultMeshVerse({
      ...this.context,
      claims: [...this.context.claims, cultMeshAuthorityClaim(role, options)],
    });
  }

  public operationContext(options: { idempotencyKey?: string } = {}): CultMeshOperationContext {
    return cultMeshOperationContextFromVerse(this.context, options);
  }

  public queryContext(): CultMeshQueryContext {
    return cultMeshQueryContextFromVerse(this.context);
  }

  public bindOperation<TRequest, TResponse>(
    operation: CultMeshOperationHandle<TRequest, TResponse>,
  ): CultMeshBoundOperationHandle<TRequest, TResponse> {
    return cultMeshBindOperation(this.context, operation);
  }

  public bindQuery<TParameters, TResult>(
    query: CultMeshQuerySurface<TParameters, TResult>,
  ): CultMeshBoundQuerySurface<TParameters, TResult> {
    return cultMeshBindQuery(this.context, query);
  }

  public bindLiveFeed<TParameters, TResult>(
    feed: CultMeshLiveFeed<TParameters, TResult>,
  ): CultMeshBoundLiveFeed<TParameters, TResult> {
    return cultMeshBindLiveFeed(this.context, feed);
  }

  public bindDocument<TDocument>(
    document: CultMeshDocumentHandle<TDocument>,
  ): CultMeshBoundDocumentHandle<TDocument> {
    return cultMeshBindDocument(this.context, document);
  }

  public bindCollection<TDocument>(
    collection: CultMeshCollectionHandle<TDocument>,
  ): CultMeshBoundCollectionHandle<TDocument> {
    return cultMeshBindCollection(this.context, collection);
  }

  public bindStatePointer<T>(
    pointer: CultMeshStatePointer<T>,
  ): CultMeshBoundStatePointer<T> {
    return cultMeshBindStatePointer(this.context, pointer);
  }

  public bindMutableStatePointer<T>(
    pointer: CultMeshMutableStatePointer<T>,
  ): CultMeshBoundMutableStatePointer<T> {
    return cultMeshBindMutableStatePointer(this.context, pointer);
  }
}

export class CultMeshOperationHandle<TRequest, TResponse> {
  public constructor(
    public readonly operationId: string,
    private readonly invokeOperation: (
      request: TRequest,
      context: CultMeshOperationContext,
    ) => Promise<TResponse>,
  ) {
    requireNonEmpty(operationId, "operationId");
  }

  public invoke(
    request: TRequest,
    context: CultMeshOperationContext | string,
  ): Promise<TResponse> {
    return this.invokeOperation(
      request,
      typeof context === "string" ? cultMeshOperationContext(context) : context,
    );
  }

  public bind(
    verse: CultMeshVerseContext | CultMeshVerse,
  ): CultMeshBoundOperationHandle<TRequest, TResponse> {
    return cultMeshBindOperation(verse, this);
  }
}

export class CultMeshBoundOperationHandle<TRequest, TResponse> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly operation: CultMeshOperationHandle<TRequest, TResponse>,
  ) {}

  public get operationId(): string {
    return this.operation.operationId;
  }

  public invoke(
    request: TRequest,
    options: { idempotencyKey?: string } = {},
  ): Promise<TResponse> {
    return this.operation.invoke(
      request,
      cultMeshOperationContextFromVerse(this.verse, options),
    );
  }
}

export class CultMeshQuerySurface<TParameters, TResult> {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly queryId: string,
    private readonly executeQuery: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchQuery?: CultMeshQueryWatcher<TParameters, TResult>;
    } = {},
  ) {
    requireNonEmpty(queryId, "queryId");
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
    this.watchQuery = options.watchQuery;
  }

  private readonly watchQuery: CultMeshQueryWatcher<TParameters, TResult> | undefined;

  public execute(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
  ): Promise<TResult> {
    return this.executeQuery(
      parameters,
      typeof context === "string" ? cultMeshQueryContext(context) : context,
    );
  }

  public query(parameters: TParameters, context: CultMeshQueryContext | string): Promise<TResult> {
    return this.execute(parameters, context);
  }

  public watch(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
    callback: (value: TResult) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchQuery) {
      throw new Error(`Query surface '${this.queryId}' does not support watches.`);
    }

    return this.watchQuery(
      parameters,
      typeof context === "string" ? cultMeshQueryContext(context) : context,
      callback,
    );
  }

  public bind(
    verse: CultMeshVerseContext | CultMeshVerse,
  ): CultMeshBoundQuerySurface<TParameters, TResult> {
    return cultMeshBindQuery(verse, this);
  }
}

export class CultMeshBoundQuerySurface<TParameters, TResult> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly query: CultMeshQuerySurface<TParameters, TResult>,
  ) {}

  public get queryId(): string {
    return this.query.queryId;
  }

  public get sources(): readonly CultMeshProjectionSource[] {
    return this.query.sources;
  }

  public get routeHint(): CultMeshRouteHint {
    return this.query.routeHint;
  }

  public execute(parameters: TParameters): Promise<TResult> {
    return this.query.execute(parameters, cultMeshQueryContextFromVerse(this.verse));
  }

  public queryOnce(parameters: TParameters): Promise<TResult> {
    return this.execute(parameters);
  }

  public watch(
    parameters: TParameters,
    callback: (value: TResult) => void,
  ): CultMeshUnsubscribe {
    return this.query.watch(parameters, cultMeshQueryContextFromVerse(this.verse), callback);
  }
}

export class CultMeshLiveFeed<TParameters, TResult> {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly feedId: string,
    private readonly snapshotFeed: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchFeed?: CultMeshLiveFeedWatcher<TParameters, TResult>;
    } = {},
  ) {
    requireNonEmpty(feedId, "feedId");
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
    this.watchFeed = options.watchFeed;
  }

  private readonly watchFeed: CultMeshLiveFeedWatcher<TParameters, TResult> | undefined;

  public snapshot(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
  ): Promise<TResult> {
    return this.snapshotFeed(
      parameters,
      this.resolveContext(typeof context === "string" ? cultMeshQueryContext(context) : context),
    );
  }

  public watch(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
    callback: (value: TResult) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchFeed) {
      throw new Error(`Live feed '${this.feedId}' does not support watches.`);
    }

    return this.watchFeed(
      parameters,
      this.resolveContext(typeof context === "string" ? cultMeshQueryContext(context) : context),
      callback,
    );
  }

  public bind(
    verse: CultMeshVerseContext | CultMeshVerse,
  ): CultMeshBoundLiveFeed<TParameters, TResult> {
    return cultMeshBindLiveFeed(verse, this);
  }

  private resolveContext(context: CultMeshQueryContext): CultMeshQueryContext {
    if (context.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return context;
    }

    return cultMeshQueryContext(context.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export class CultMeshBoundLiveFeed<TParameters, TResult> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly feed: CultMeshLiveFeed<TParameters, TResult>,
  ) {}

  public get feedId(): string {
    return this.feed.feedId;
  }

  public get sources(): readonly CultMeshProjectionSource[] {
    return this.feed.sources;
  }

  public get routeHint(): CultMeshRouteHint {
    return this.feed.routeHint;
  }

  public snapshot(parameters: TParameters): Promise<TResult> {
    return this.feed.snapshot(parameters, cultMeshQueryContextFromVerse(this.verse));
  }

  public watch(
    parameters: TParameters,
    callback: (value: TResult) => void,
  ): CultMeshUnsubscribe {
    return this.feed.watch(parameters, cultMeshQueryContextFromVerse(this.verse), callback);
  }
}

export class CultMeshDocumentHandle<TDocument> {
  public readonly schema: CultMeshDocumentSchemaDescriptor;
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly documentId: string,
    schema: CultMeshDocumentSchemaDescriptor,
    private readonly snapshotDocument: (context: CultMeshQueryContext) => Promise<TDocument>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchDocument?: CultMeshDocumentWatcher<TDocument>;
      replaceDocument?: CultMeshDocumentReplacer<TDocument>;
    } = {},
  ) {
    requireNonEmpty(documentId, "documentId");
    this.schema = normalizeCultMeshDocumentSchema(schema);
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
    this.watchDocument = options.watchDocument;
    this.replaceDocument = options.replaceDocument;
  }

  private readonly watchDocument: CultMeshDocumentWatcher<TDocument> | undefined;
  private readonly replaceDocument: CultMeshDocumentReplacer<TDocument> | undefined;

  public get canReplace(): boolean {
    return this.replaceDocument !== undefined;
  }

  public latest(context: CultMeshQueryContext | string = "local"): Promise<TDocument> {
    return this.snapshotDocument(
      this.resolveContext(typeof context === "string" ? cultMeshQueryContext(context) : context),
    );
  }

  public read(context: CultMeshQueryContext | string = "local"): Promise<TDocument> {
    return this.latest(context);
  }

  public watch(callback: (value: TDocument) => void): CultMeshUnsubscribe;
  public watch(context: CultMeshQueryContext | string, callback: (value: TDocument) => void): CultMeshUnsubscribe;
  public watch(
    contextOrCallback: CultMeshQueryContext | string | ((value: TDocument) => void),
    maybeCallback?: (value: TDocument) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchDocument) {
      throw new Error(`Document '${this.documentId}' does not support watches.`);
    }

    const callback =
      typeof contextOrCallback === "function" ? contextOrCallback : maybeCallback;
    if (!callback) {
      throw new Error(`Document '${this.documentId}' requires a watch callback.`);
    }

    const context =
      typeof contextOrCallback === "function"
        ? cultMeshQueryContext("local")
        : typeof contextOrCallback === "string"
          ? cultMeshQueryContext(contextOrCallback)
          : contextOrCallback;

    return this.watchDocument(this.resolveContext(context), callback);
  }

  public replace(value: TDocument): Promise<void>;
  public replace(context: CultMeshQueryContext | string, value: TDocument): Promise<void>;
  public replace(
    contextOrValue: CultMeshQueryContext | string | TDocument,
    maybeValue?: TDocument,
  ): Promise<void> {
    if (!this.replaceDocument) {
      throw new Error(`Document '${this.documentId}' does not support replacement.`);
    }

    const hasContext =
      typeof contextOrValue === "string" || isCultMeshQueryContext(contextOrValue);
    const context = hasContext
      ? typeof contextOrValue === "string"
        ? cultMeshQueryContext(contextOrValue)
        : contextOrValue as CultMeshQueryContext
      : cultMeshQueryContext("local");
    const value = hasContext ? maybeValue : contextOrValue as TDocument;
    if (value === undefined) {
      throw new Error(`Document '${this.documentId}' requires a replacement value.`);
    }

    return this.replaceDocument(this.resolveContext(context), value);
  }

  public asSchemaAlias<TAlias>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TAlias } = {},
  ): CultMeshDocumentHandle<TAlias> {
    const aliasSchema = normalizeCultMeshDocumentSchema(schema);
    if (!cultMeshSchemasAreCompatible(this.schema, aliasSchema)) {
      throw new Error(
        `Document '${this.documentId}' schema ${cultMeshSchemaLabel(this.schema)} is not compatible with alias ${cultMeshSchemaLabel(aliasSchema)}.`,
      );
    }

    const parse = options.parse ?? ((value: unknown) => value as TAlias);
    return new CultMeshDocumentHandle<TAlias>(
      this.documentId,
      aliasSchema,
      async (context) => parse(await this.latest(context)),
      {
        sources: this.sources,
        routeHint: this.routeHint,
        watchDocument: this.watchDocument
          ? (context, callback) => this.watch(context, value => callback(parse(value)))
          : undefined,
        replaceDocument: this.replaceDocument
          ? (context, value) => this.replace(context, value as unknown as TDocument)
          : undefined,
      },
    );
  }

  public bind(verse: CultMeshVerseContext | CultMeshVerse): CultMeshBoundDocumentHandle<TDocument> {
    return cultMeshBindDocument(verse, this);
  }

  private resolveContext(context: CultMeshQueryContext): CultMeshQueryContext {
    if (context.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return context;
    }

    return cultMeshQueryContext(context.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export class CultMeshBoundDocumentHandle<TDocument> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly document: CultMeshDocumentHandle<TDocument>,
  ) {}

  public get documentId(): string {
    return this.document.documentId;
  }

  public get schema(): CultMeshDocumentSchemaDescriptor {
    return this.document.schema;
  }

  public get canReplace(): boolean {
    return this.document.canReplace;
  }

  public latest(): Promise<TDocument> {
    return this.document.latest(cultMeshQueryContextFromVerse(this.verse));
  }

  public read(): Promise<TDocument> {
    return this.latest();
  }

  public watch(callback: (value: TDocument) => void): CultMeshUnsubscribe {
    return this.document.watch(cultMeshQueryContextFromVerse(this.verse), callback);
  }

  public replace(value: TDocument): Promise<void> {
    return this.document.replace(cultMeshQueryContextFromVerse(this.verse), value);
  }

  public asSchemaAlias<TAlias>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TAlias } = {},
  ): CultMeshBoundDocumentHandle<TAlias> {
    return cultMeshBindDocument(this.verse, this.document.asSchemaAlias(schema, options));
  }
}

export class CultMeshDocumentCatalog {
  readonly #byDocumentId = new Map<string, CultMeshDocumentHandle<any>>();
  readonly #byType = new Map<string, CultMeshDocumentHandle<any>>();
  readonly #bySchemaId = new Map<string, CultMeshDocumentHandle<any>>();
  readonly #bySchemaNameVersion = new Map<string, CultMeshDocumentHandle<any>>();

  public constructor(documents: Iterable<CultMeshDocumentHandle<any>>) {
    for (const document of documents) {
      this.add(document);
    }
  }

  public get documents(): readonly CultMeshDocumentHandle<any>[] {
    return [...this.#byDocumentId.values()];
  }

  public add<TDocument>(document: CultMeshDocumentHandle<TDocument>): this {
    this.#byDocumentId.set(document.documentId, document as CultMeshDocumentHandle<any>);
    if (document.schema.type) {
      this.#byType.set(document.schema.type, document as CultMeshDocumentHandle<any>);
    }
    if (document.schema.schemaId) {
      this.#bySchemaId.set(document.schema.schemaId, document as CultMeshDocumentHandle<any>);
    }
    const key = cultMeshSchemaNameVersionKey(document.schema);
    if (key) {
      this.#bySchemaNameVersion.set(key, document as CultMeshDocumentHandle<any>);
    }
    return this;
  }

  public tryDocument<TDocument>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TDocument } = {},
  ): CultMeshDocumentHandle<TDocument> | undefined {
    const descriptor = normalizeCultMeshDocumentSchema(schema);
    const exact =
      (descriptor.type ? this.#byType.get(descriptor.type) : undefined) ??
      (descriptor.schemaId ? this.#bySchemaId.get(descriptor.schemaId) : undefined) ??
      (() => {
        const key = cultMeshSchemaNameVersionKey(descriptor);
        return key ? this.#bySchemaNameVersion.get(key) : undefined;
      })();

    if (!exact) {
      return undefined;
    }

    return cultMeshSchemasAreCompatible(exact.schema, descriptor)
      ? exact.asSchemaAlias(descriptor, options)
      : undefined;
  }

  public document<TDocument>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TDocument } = {},
  ): CultMeshDocumentHandle<TDocument> {
    const document = this.tryDocument(schema, options);
    if (!document) {
      throw new Error(`Document catalog has no document for ${cultMeshSchemaLabel(schema)}.`);
    }
    return document;
  }

  public latest<TDocument>(
    schema: CultMeshDocumentSchemaDescriptor,
    context: CultMeshQueryContext | string = "local",
    options: { parse?: (value: unknown) => TDocument } = {},
  ): Promise<TDocument> {
    return this.document(schema, options).latest(context);
  }

  public watch<TDocument>(
    schema: CultMeshDocumentSchemaDescriptor,
    callback: (value: TDocument) => void,
    options: { parse?: (value: unknown) => TDocument; context?: CultMeshQueryContext | string } = {},
  ): CultMeshUnsubscribe {
    return this.document(schema, options).watch(options.context ?? "local", callback);
  }
}

export class CultMeshCollectionHandle<TDocument> {
  public readonly schema: CultMeshDocumentSchemaDescriptor;
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly collectionId: string,
    schema: CultMeshDocumentSchemaDescriptor,
    private readonly snapshotCollection: (context: CultMeshQueryContext) => Promise<CultMeshCollectionSnapshot<TDocument>>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchCollection?: CultMeshCollectionWatcher<TDocument>;
    } = {},
  ) {
    requireNonEmpty(collectionId, "collectionId");
    this.schema = normalizeCultMeshDocumentSchema(schema);
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
    this.watchCollection = options.watchCollection;
  }

  private readonly watchCollection: CultMeshCollectionWatcher<TDocument> | undefined;

  public latest(context: CultMeshQueryContext | string = "local"): Promise<CultMeshCollectionSnapshot<TDocument>> {
    return this.snapshotCollection(
      this.resolveContext(typeof context === "string" ? cultMeshQueryContext(context) : context),
    );
  }

  public watchChanges(callback: (change: CultMeshCollectionChange<TDocument>) => void): CultMeshUnsubscribe;
  public watchChanges(
    context: CultMeshQueryContext | string,
    callback: (change: CultMeshCollectionChange<TDocument>) => void,
  ): CultMeshUnsubscribe;
  public watchChanges(
    contextOrCallback:
      | CultMeshQueryContext
      | string
      | ((change: CultMeshCollectionChange<TDocument>) => void),
    maybeCallback?: (change: CultMeshCollectionChange<TDocument>) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchCollection) {
      throw new Error(`Collection '${this.collectionId}' does not support watches.`);
    }

    const callback =
      typeof contextOrCallback === "function" ? contextOrCallback : maybeCallback;
    if (!callback) {
      throw new Error(`Collection '${this.collectionId}' requires a watch callback.`);
    }

    const context =
      typeof contextOrCallback === "function"
        ? cultMeshQueryContext("local")
        : typeof contextOrCallback === "string"
          ? cultMeshQueryContext(contextOrCallback)
          : contextOrCallback;

    return this.watchCollection(this.resolveContext(context), callback);
  }

  public asSchemaAlias<TAlias>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TAlias } = {},
  ): CultMeshCollectionHandle<TAlias> {
    const aliasSchema = normalizeCultMeshDocumentSchema(schema);
    if (!cultMeshSchemasAreCompatible(this.schema, aliasSchema)) {
      throw new Error(
        `Collection '${this.collectionId}' schema ${cultMeshSchemaLabel(this.schema)} is not compatible with alias ${cultMeshSchemaLabel(aliasSchema)}.`,
      );
    }

    const parse = options.parse ?? ((value: unknown) => value as TAlias);
    return new CultMeshCollectionHandle<TAlias>(
      this.collectionId,
      aliasSchema,
      async (context) => (await this.latest(context)).map(value => parse(value)),
      {
        sources: this.sources,
        routeHint: this.routeHint,
        watchCollection: this.watchCollection
          ? (context, callback) => this.watchChanges(context, change => callback({
              ...change,
              value: change.value === undefined ? undefined : parse(change.value),
            }))
          : undefined,
      },
    );
  }

  public bind(verse: CultMeshVerseContext | CultMeshVerse): CultMeshBoundCollectionHandle<TDocument> {
    return cultMeshBindCollection(verse, this);
  }

  private resolveContext(context: CultMeshQueryContext): CultMeshQueryContext {
    if (context.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return context;
    }

    return cultMeshQueryContext(context.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export class CultMeshBoundCollectionHandle<TDocument> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly collection: CultMeshCollectionHandle<TDocument>,
  ) {}

  public get collectionId(): string {
    return this.collection.collectionId;
  }

  public get schema(): CultMeshDocumentSchemaDescriptor {
    return this.collection.schema;
  }

  public latest(): Promise<CultMeshCollectionSnapshot<TDocument>> {
    return this.collection.latest(cultMeshQueryContextFromVerse(this.verse));
  }

  public watchChanges(callback: (change: CultMeshCollectionChange<TDocument>) => void): CultMeshUnsubscribe {
    return this.collection.watchChanges(cultMeshQueryContextFromVerse(this.verse), callback);
  }

  public asSchemaAlias<TAlias>(
    schema: CultMeshDocumentSchemaDescriptor,
    options: { parse?: (value: unknown) => TAlias } = {},
  ): CultMeshBoundCollectionHandle<TAlias> {
    return cultMeshBindCollection(this.verse, this.collection.asSchemaAlias(schema, options));
  }
}

export class CultMeshStatePointer<T> {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly pointerId: string,
    private readonly resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
    private readonly watchPointer?:
      | ((callback: (value: T) => void) => CultMeshUnsubscribe)
      | CultMeshStatePointerWatcher<T>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ) {
    requireNonEmpty(pointerId, "pointerId");
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
  }

  public resolve(context?: CultMeshQueryContext | string): Promise<T | undefined> {
    return (this.resolvePointer as CultMeshStatePointerResolver<T>)(
      this.resolveContext(
        typeof context === "string" ? cultMeshQueryContext(context) : context,
      ),
    );
  }

  public watch(callback: (value: T) => void): CultMeshUnsubscribe;
  public watch(context: CultMeshQueryContext | string, callback: (value: T) => void): CultMeshUnsubscribe;
  public watch(
    contextOrCallback: CultMeshQueryContext | string | ((value: T) => void),
    maybeCallback?: (value: T) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchPointer) {
      throw new Error(`State pointer '${this.pointerId}' does not support watches.`);
    }

    const callback =
      typeof contextOrCallback === "function" ? contextOrCallback : maybeCallback;
    if (!callback) {
      throw new Error(`State pointer '${this.pointerId}' requires a watch callback.`);
    }

    if (this.watchPointer.length <= 1) {
      return (this.watchPointer as (callback: (value: T) => void) => CultMeshUnsubscribe)(callback);
    }

    const context =
      typeof contextOrCallback === "function"
        ? undefined
        : typeof contextOrCallback === "string"
          ? cultMeshQueryContext(contextOrCallback)
          : contextOrCallback;
    return (this.watchPointer as CultMeshStatePointerWatcher<T>)(
      this.resolveContext(context),
      callback,
    );
  }

  public bind(
    verse: CultMeshVerseContext | CultMeshVerse,
  ): CultMeshBoundStatePointer<T> {
    return cultMeshBindStatePointer(verse, this);
  }

  private resolveContext(context?: CultMeshQueryContext): CultMeshQueryContext {
    const resolved = context ?? cultMeshQueryContext("local");
    if (resolved.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return resolved;
    }

    return cultMeshQueryContext(resolved.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export class CultMeshBoundStatePointer<T> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly pointer: CultMeshStatePointer<T>,
  ) {}

  public get pointerId(): string {
    return this.pointer.pointerId;
  }

  public get sources(): readonly CultMeshProjectionSource[] {
    return this.pointer.sources;
  }

  public get routeHint(): CultMeshRouteHint {
    return this.pointer.routeHint;
  }

  public resolve(): Promise<T | undefined> {
    return this.pointer.resolve(cultMeshQueryContextFromVerse(this.verse));
  }

  public watch(callback: (value: T) => void): CultMeshUnsubscribe {
    return this.pointer.watch(cultMeshQueryContextFromVerse(this.verse), callback);
  }
}

export class CultMeshMutableStatePointer<T> {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly pointerId: string,
    private readonly resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
    private readonly watchPointer:
      | ((callback: (value: T) => void) => CultMeshUnsubscribe)
      | CultMeshStatePointerWatcher<T>,
    private readonly replacePointer:
      | ((value: T) => Promise<void>)
      | CultMeshMutableStatePointerReplacer<T>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ) {
    requireNonEmpty(pointerId, "pointerId");
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
  }

  public resolve(context?: CultMeshQueryContext | string): Promise<T | undefined> {
    return (this.resolvePointer as CultMeshStatePointerResolver<T>)(
      this.resolveContext(
        typeof context === "string" ? cultMeshQueryContext(context) : context,
      ),
    );
  }

  public read(context?: CultMeshQueryContext | string): Promise<T | undefined> {
    return this.resolve(context);
  }

  public replace(value: T): Promise<void>;
  public replace(context: CultMeshQueryContext | string, value: T): Promise<void>;
  public replace(
    contextOrValue: CultMeshQueryContext | string | T,
    maybeValue?: T,
  ): Promise<void> {
    if (this.replacePointer.length <= 1) {
      return (this.replacePointer as (value: T) => Promise<void>)(
        contextOrValue as T,
      );
    }

    const context =
      typeof contextOrValue === "string"
        ? cultMeshQueryContext(contextOrValue)
        : isCultMeshQueryContext(contextOrValue)
          ? contextOrValue
          : undefined;
    const value = context ? maybeValue : (contextOrValue as T);
    if (value === undefined) {
      throw new Error(`Mutable state pointer '${this.pointerId}' requires a replacement value.`);
    }

    return (this.replacePointer as CultMeshMutableStatePointerReplacer<T>)(
      this.resolveContext(context),
      value,
    );
  }

  public watch(callback: (value: T) => void): CultMeshUnsubscribe;
  public watch(context: CultMeshQueryContext | string, callback: (value: T) => void): CultMeshUnsubscribe;
  public watch(
    contextOrCallback: CultMeshQueryContext | string | ((value: T) => void),
    maybeCallback?: (value: T) => void,
  ): CultMeshUnsubscribe {
    const callback =
      typeof contextOrCallback === "function" ? contextOrCallback : maybeCallback;
    if (!callback) {
      throw new Error(`Mutable state pointer '${this.pointerId}' requires a watch callback.`);
    }

    if (this.watchPointer.length <= 1) {
      return (this.watchPointer as (callback: (value: T) => void) => CultMeshUnsubscribe)(callback);
    }

    const context =
      typeof contextOrCallback === "function"
        ? undefined
        : typeof contextOrCallback === "string"
          ? cultMeshQueryContext(contextOrCallback)
          : contextOrCallback;
    return (this.watchPointer as CultMeshStatePointerWatcher<T>)(
      this.resolveContext(context),
      callback,
    );
  }

  public asStatePointer(): CultMeshStatePointer<T> {
    return cultMeshStatePointer(
      this.pointerId,
      (context) => this.resolve(context),
      (context, callback) => this.watch(context, callback),
      {
        routeHint: this.routeHint,
        sources: this.sources,
      },
    );
  }

  public bind(
    verse: CultMeshVerseContext | CultMeshVerse,
  ): CultMeshBoundMutableStatePointer<T> {
    return cultMeshBindMutableStatePointer(verse, this);
  }

  private resolveContext(context?: CultMeshQueryContext): CultMeshQueryContext {
    const resolved = context ?? cultMeshQueryContext("local");
    if (resolved.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return resolved;
    }

    return cultMeshQueryContext(resolved.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export class CultMeshBoundMutableStatePointer<T> {
  public constructor(
    public readonly verse: CultMeshVerseContext,
    public readonly pointer: CultMeshMutableStatePointer<T>,
  ) {}

  public get pointerId(): string {
    return this.pointer.pointerId;
  }

  public get sources(): readonly CultMeshProjectionSource[] {
    return this.pointer.sources;
  }

  public get routeHint(): CultMeshRouteHint {
    return this.pointer.routeHint;
  }

  public resolve(): Promise<T | undefined> {
    return this.pointer.resolve(cultMeshQueryContextFromVerse(this.verse));
  }

  public read(): Promise<T | undefined> {
    return this.resolve();
  }

  public replace(value: T): Promise<void> {
    return this.pointer.replace(cultMeshQueryContextFromVerse(this.verse), value);
  }

  public watch(callback: (value: T) => void): CultMeshUnsubscribe {
    return this.pointer.watch(cultMeshQueryContextFromVerse(this.verse), callback);
  }

  public asStatePointer(): CultMeshBoundStatePointer<T> {
    return cultMeshBindStatePointer(this.verse, this.pointer.asStatePointer());
  }
}

export class CultMeshProjectionRecipe<TParameters, TResult> {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly projectionId: string,
    sources: readonly CultMeshProjectionSource[],
    private readonly projectProjection: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      routeHint?: CultMeshRouteHint;
      watchProjection?: (
        parameters: TParameters,
        context: CultMeshQueryContext,
        callback: (value: TResult) => void,
      ) => CultMeshUnsubscribe;
    } = {},
  ) {
    requireNonEmpty(projectionId, "projectionId");
    this.sources = [...sources];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
    this.watchProjection = options.watchProjection;
  }

  private readonly watchProjection:
    | ((
        parameters: TParameters,
        context: CultMeshQueryContext,
        callback: (value: TResult) => void,
      ) => CultMeshUnsubscribe)
    | undefined;

  public project(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
  ): Promise<TResult> {
    return this.projectProjection(
      parameters,
      this.resolveContext(
        typeof context === "string" ? cultMeshQueryContext(context) : context,
      ),
    );
  }

  public watch(
    parameters: TParameters,
    context: CultMeshQueryContext | string,
    callback: (value: TResult) => void,
  ): CultMeshUnsubscribe {
    if (!this.watchProjection) {
      throw new Error(`Projection recipe '${this.projectionId}' does not support watches.`);
    }

    return this.watchProjection(
      parameters,
      this.resolveContext(
        typeof context === "string" ? cultMeshQueryContext(context) : context,
      ),
      callback,
    );
  }

  public asQuerySurface(): CultMeshQuerySurface<TParameters, TResult> {
    return cultMeshQuery(this.projectionId, (parameters, context) =>
      this.project(parameters, context),
      {
        sources: this.sources,
        routeHint: this.routeHint,
        watchQuery: this.watchProjection
          ? (parameters, context, callback) => this.watch(parameters, context, callback)
          : undefined,
      },
    );
  }

  private resolveContext(context: CultMeshQueryContext): CultMeshQueryContext {
    if (context.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return context;
    }

    return cultMeshQueryContext(context.runtimeId, {
      routeHint: this.routeHint,
    });
  }
}

export function cultMeshOperationContextFor(runtimeId: string): CultMeshOperationContextBuilder {
  return new CultMeshOperationContextBuilder(runtimeId);
}

export function cultMeshQueryContextFor(runtimeId: string): CultMeshQueryContextBuilder {
  return new CultMeshQueryContextBuilder(runtimeId);
}

export function cultMeshVec2(x: number, y: number): CultMeshVec2 {
  return { x, y };
}

export function cultMeshRect(min: CultMeshVec2, max: CultMeshVec2): CultMeshRect {
  return {
    min: cultMeshVec2(Math.min(min.x, max.x), Math.min(min.y, max.y)),
    max: cultMeshVec2(Math.max(min.x, max.x), Math.max(min.y, max.y)),
  };
}

export function cultMeshRectFromBounds(
  minX: number,
  minY: number,
  maxX: number,
  maxY: number,
): CultMeshRect {
  return cultMeshRect(cultMeshVec2(minX, minY), cultMeshVec2(maxX, maxY));
}

export function cultMeshViewportRequest(
  viewport: CultMeshRect,
  controlledEntityIndices?: readonly number[],
): CultMeshViewportRequest {
  return {
    minX: viewport.min.x,
    minY: viewport.min.y,
    maxX: viewport.max.x,
    maxY: viewport.max.y,
    controlledEntityIndices,
  };
}

export function cultMeshRouteHint(
  kind: CultMeshLocalityKind = "automatic",
  description?: string,
): CultMeshRouteHint {
  return description ? { kind, description } : { kind };
}

export function cultMeshRouteRecord(
  routeHint?: CultMeshRouteHint,
): CultMeshRouteRecord;
export function cultMeshRouteRecord(
  kind?: string,
  description?: string,
): CultMeshRouteRecord;
export function cultMeshRouteRecord(
  routeOrKind?: CultMeshRouteHint | string,
  description = "",
): CultMeshRouteRecord {
  if (typeof routeOrKind === "string" || routeOrKind === undefined) {
    return {
      kind: routeOrKind ?? "",
      description,
    };
  }

  return {
    kind: routeOrKind.kind,
    description: routeOrKind.description ?? "",
  };
}

export function cultMeshRouteFromRecord(
  record: Partial<CultMeshRouteRecord>,
  fallback: CultMeshRouteHint = cultMeshRouteHint(),
): CultMeshRouteHint {
  return cultMeshRouteHint(
    parseCultMeshLocalityKind(record.kind, fallback.kind),
    nonBlankOr(record.description, fallback.description),
  );
}

export function cultMeshAuthorityClaim(
  role: string,
  options: { shardId?: string; leaseId?: string } = {},
): CultMeshAuthorityClaim {
  requireNonEmpty(role, "role");
  return {
    role,
    shardId: options.shardId,
    leaseId: options.leaseId,
  };
}

export function cultMeshVerseContext(
  verseId: string,
  runtimeId: string,
  options: {
    routeHint?: CultMeshRouteHint;
    claims?: readonly CultMeshAuthorityClaim[];
  } = {},
): CultMeshVerseContext {
  requireNonEmpty(verseId, "verseId");
  requireNonEmpty(runtimeId, "runtimeId");
  return {
    verseId,
    runtimeId,
    routeHint: options.routeHint ?? cultMeshRouteHint(),
    claims: options.claims ? [...options.claims] : [],
  };
}

export function cultMeshVerse(
  verseId: string,
  runtimeId: string,
  options: {
    routeHint?: CultMeshRouteHint;
    claims?: readonly CultMeshAuthorityClaim[];
  } = {},
): CultMeshVerse {
  return new CultMeshVerse(cultMeshVerseContext(verseId, runtimeId, options));
}

export function cultMeshOperationContextFromVerse(
  context: CultMeshVerseContext,
  options: { idempotencyKey?: string } = {},
): CultMeshOperationContext {
  return cultMeshOperationContext(context.runtimeId, {
    claims: context.claims,
    routeHint: context.routeHint,
    idempotencyKey: options.idempotencyKey,
  });
}

export function cultMeshQueryContextFromVerse(
  context: CultMeshVerseContext,
): CultMeshQueryContext {
  return cultMeshQueryContext(context.runtimeId, {
    routeHint: context.routeHint,
  });
}

export function cultMeshBindOperation<TRequest, TResponse>(
  verse: CultMeshVerseContext | CultMeshVerse,
  operation: CultMeshOperationHandle<TRequest, TResponse>,
): CultMeshBoundOperationHandle<TRequest, TResponse> {
  return new CultMeshBoundOperationHandle(resolveCultMeshVerseContext(verse), operation);
}

export function cultMeshBindQuery<TParameters, TResult>(
  verse: CultMeshVerseContext | CultMeshVerse,
  query: CultMeshQuerySurface<TParameters, TResult>,
): CultMeshBoundQuerySurface<TParameters, TResult> {
  return new CultMeshBoundQuerySurface(resolveCultMeshVerseContext(verse), query);
}

export function cultMeshBindLiveFeed<TParameters, TResult>(
  verse: CultMeshVerseContext | CultMeshVerse,
  feed: CultMeshLiveFeed<TParameters, TResult>,
): CultMeshBoundLiveFeed<TParameters, TResult> {
  return new CultMeshBoundLiveFeed(resolveCultMeshVerseContext(verse), feed);
}

export function cultMeshBindStatePointer<T>(
  verse: CultMeshVerseContext | CultMeshVerse,
  pointer: CultMeshStatePointer<T>,
): CultMeshBoundStatePointer<T> {
  return new CultMeshBoundStatePointer(resolveCultMeshVerseContext(verse), pointer);
}

export function cultMeshBindMutableStatePointer<T>(
  verse: CultMeshVerseContext | CultMeshVerse,
  pointer: CultMeshMutableStatePointer<T>,
): CultMeshBoundMutableStatePointer<T> {
  return new CultMeshBoundMutableStatePointer(resolveCultMeshVerseContext(verse), pointer);
}

export function cultMeshBindDocument<TDocument>(
  verse: CultMeshVerseContext | CultMeshVerse,
  document: CultMeshDocumentHandle<TDocument>,
): CultMeshBoundDocumentHandle<TDocument> {
  return new CultMeshBoundDocumentHandle(resolveCultMeshVerseContext(verse), document);
}

export function cultMeshBindCollection<TDocument>(
  verse: CultMeshVerseContext | CultMeshVerse,
  collection: CultMeshCollectionHandle<TDocument>,
): CultMeshBoundCollectionHandle<TDocument> {
  return new CultMeshBoundCollectionHandle(resolveCultMeshVerseContext(verse), collection);
}

function resolveCultMeshVerseContext(verse: CultMeshVerseContext | CultMeshVerse): CultMeshVerseContext {
  return verse instanceof CultMeshVerse ? verse.context : verse;
}

function isCultMeshQueryContext(value: unknown): value is CultMeshQueryContext {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const maybeContext = value as Partial<CultMeshQueryContext>;
  return typeof maybeContext.runtimeId === "string" &&
    typeof maybeContext.routeHint === "object" &&
    maybeContext.routeHint !== null;
}

export function cultMeshOperationContext(
  runtimeId: string,
  options: {
    claims?: readonly CultMeshAuthorityClaim[];
    routeHint?: CultMeshRouteHint;
    idempotencyKey?: string;
  } = {},
): CultMeshOperationContext {
  requireNonEmpty(runtimeId, "runtimeId");
  return {
    runtimeId,
    claims: options.claims ? [...options.claims] : [],
    routeHint: options.routeHint ?? cultMeshRouteHint(),
    idempotencyKey: options.idempotencyKey,
  };
}

export function cultMeshQueryContext(
  runtimeId: string,
  options: { routeHint?: CultMeshRouteHint } = {},
): CultMeshQueryContext {
  requireNonEmpty(runtimeId, "runtimeId");
  return {
    runtimeId,
    routeHint: options.routeHint ?? cultMeshRouteHint(),
  };
}

export function cultMeshOperationReceipt(
  operationId: string,
  accepted: boolean,
  options: { route?: CultMeshRouteHint; diagnostic?: string } = {},
): CultMeshOperationReceipt {
  requireNonEmpty(operationId, "operationId");
  return {
    operationId,
    accepted,
    route: options.route ?? cultMeshRouteHint(),
    diagnostic: options.diagnostic,
  };
}

export function cultMeshOperation<TRequest, TResponse>(
  operationId: string,
  invokeOperation: (
    request: TRequest,
    context: CultMeshOperationContext,
  ) => Promise<TResponse>,
): CultMeshOperationHandle<TRequest, TResponse> {
  return new CultMeshOperationHandle(operationId, invokeOperation);
}

export function cultMeshQuery<TParameters, TResult>(
  queryId: string,
  executeQuery: (
    parameters: TParameters,
    context: CultMeshQueryContext,
  ) => Promise<TResult>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
    watchQuery?: CultMeshQueryWatcher<TParameters, TResult>;
  } = {},
): CultMeshQuerySurface<TParameters, TResult> {
  return new CultMeshQuerySurface(queryId, executeQuery, options);
}

export function cultMeshDescribeQuerySurface(
  query: {
    readonly queryId: string;
    readonly routeHint: CultMeshRouteHint;
    readonly sources: readonly CultMeshProjectionSource[];
  },
): CultMeshQuerySurfaceDiagnostic {
  return {
    queryId: query.queryId,
    routeHint: query.routeHint,
    sources: [...query.sources],
  };
}

export function cultMeshDescribeOperationHandle(
  operation: {
    readonly operationId: string;
  },
): CultMeshOperationHandleDiagnostic {
  return {
    operationId: operation.operationId,
  };
}

export function cultMeshDescribeStatePointer(
  pointer: {
    readonly pointerId: string;
    readonly routeHint?: CultMeshRouteHint;
    readonly sources?: readonly CultMeshProjectionSource[];
  },
): CultMeshStatePointerDiagnostic {
  return {
    pointerId: pointer.pointerId,
    routeHint: pointer.routeHint ?? cultMeshRouteHint(),
    sources: [...(pointer.sources ?? [])],
  };
}

export function cultMeshLiveFeed<TParameters, TResult>(
  feedId: string,
  snapshotFeed: (
    parameters: TParameters,
    context: CultMeshQueryContext,
  ) => Promise<TResult>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
    watchFeed?: CultMeshLiveFeedWatcher<TParameters, TResult>;
  } = {},
): CultMeshLiveFeed<TParameters, TResult> {
  return new CultMeshLiveFeed(feedId, snapshotFeed, options);
}

export function cultMeshDocument<TDocument>(
  documentId: string,
  schema: CultMeshDocumentSchemaDescriptor,
  snapshotDocument: (context: CultMeshQueryContext) => Promise<TDocument>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
    watchDocument?: CultMeshDocumentWatcher<TDocument>;
    replaceDocument?: CultMeshDocumentReplacer<TDocument>;
  } = {},
): CultMeshDocumentHandle<TDocument> {
  return new CultMeshDocumentHandle(documentId, schema, snapshotDocument, options);
}

export function cultMeshDocumentFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
  cache: CultCache,
  definition: TDefinition,
  key: string,
  options: {
    documentId?: string;
    routeHint?: CultMeshRouteHint;
    pollMs?: number;
  } = {},
): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
  requireNonEmpty(key, "key");
  const documentId = options.documentId ?? `${definition.type}:${key}`;
  const schema = cultMeshSchemaFromDefinition(definition);
  return cultMeshDocument(
    documentId,
    schema,
    async () => cache.getRequired(definition, key),
    {
      routeHint: options.routeHint ?? cultMeshRouteHint("in-process", "CultCache"),
      sources: [cultMeshProjectionSource(documentId, { schemaId: schema.schemaId })],
      watchDocument: cultMeshPollingDocumentWatcher(
        async () => cache.getRequired(definition, key),
        { intervalMs: options.pollMs ?? 50 },
      ),
      replaceDocument: async (_context, value) => {
        await cache.put(definition, key, value);
      },
    },
  );
}

export function cultMeshGlobalDocumentFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
  cache: CultCache,
  definition: TDefinition,
  options: {
    documentId?: string;
    routeHint?: CultMeshRouteHint;
    pollMs?: number;
  } = {},
): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
  const documentId = options.documentId ?? `${definition.type}:global`;
  const schema = cultMeshSchemaFromDefinition(definition);
  return cultMeshDocument(
    documentId,
    schema,
    async () => cache.getRequiredGlobal(definition),
    {
      routeHint: options.routeHint ?? cultMeshRouteHint("in-process", "CultCache"),
      sources: [cultMeshProjectionSource(documentId, { schemaId: schema.schemaId })],
      watchDocument: cultMeshPollingDocumentWatcher(
        async () => cache.getRequiredGlobal(definition),
        { intervalMs: options.pollMs ?? 50 },
      ),
      replaceDocument: async (_context, value) => {
        await cache.putGlobal(definition, value);
      },
    },
  );
}

export function cultMeshDocuments(
  ...documents: readonly CultMeshDocumentHandle<any>[]
): CultMeshDocumentCatalog {
  return new CultMeshDocumentCatalog(documents);
}

export function cultMeshCollection<TDocument>(
  collectionId: string,
  schema: CultMeshDocumentSchemaDescriptor,
  snapshotCollection: (context: CultMeshQueryContext) => Promise<CultMeshCollectionSnapshot<TDocument>>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
    watchCollection?: CultMeshCollectionWatcher<TDocument>;
  } = {},
): CultMeshCollectionHandle<TDocument> {
  return new CultMeshCollectionHandle(collectionId, schema, snapshotCollection, options);
}

export function cultMeshCollectionFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
  cache: CultCache,
  definition: TDefinition,
  options: {
    collectionId?: string;
    routeHint?: CultMeshRouteHint;
    pollMs?: number;
  } = {},
): CultMeshCollectionHandle<CultCacheDocumentValue<TDefinition>> {
  const collectionId = options.collectionId ?? definition.type;
  const schema = cultMeshSchemaFromDefinition(definition);
  return cultMeshCollection(
    collectionId,
    schema,
    async () => cache.getAll(definition),
    {
      routeHint: options.routeHint ?? cultMeshRouteHint("in-process", "CultCache"),
      sources: [cultMeshProjectionSource(collectionId, { schemaId: schema.schemaId })],
      watchCollection: cultMeshPollingCollectionWatcher(
        async () => cache.getAll(definition),
        { intervalMs: options.pollMs ?? 50 },
      ),
    },
  );
}

export function cultMeshDescribeLiveFeed(
  feed: {
    readonly feedId: string;
    readonly routeHint: CultMeshRouteHint;
    readonly sources: readonly CultMeshProjectionSource[];
  },
): CultMeshLiveFeedDiagnostic {
  return {
    feedId: feed.feedId,
    routeHint: feed.routeHint,
    sources: [...feed.sources],
  };
}

export function cultMeshPollingQueryWatcher<TParameters, TResult>(
  executeQuery: (
    parameters: TParameters,
    context: CultMeshQueryContext,
  ) => Promise<TResult>,
  options: CultMeshPollingWatchOptions<TResult> = {},
): CultMeshQueryWatcher<TParameters, TResult> {
  const intervalMs = Math.max(1, options.intervalMs ?? 50);
  const emitInitial = options.emitInitial ?? true;
  return (parameters, context, callback) => {
    let disposed = false;
    let running = false;
    let last: TResult | undefined;
    let hasLast = false;
    let timer: ReturnType<typeof setInterval> | undefined;

    const poll = async () => {
      if (disposed || running) {
        return;
      }

      running = true;
      try {
        const next = await executeQuery(parameters, context);
        const changed = !hasLast || !(options.equals?.(last as TResult, next) ?? Object.is(last, next));
        const shouldEmit = hasLast ? changed : emitInitial;
        last = next;
        hasLast = true;
        if (shouldEmit) {
          callback(next);
        }
      } finally {
        running = false;
      }
    };

    void poll();
    timer = setInterval(() => {
      void poll();
    }, intervalMs);

    return () => {
      disposed = true;
      if (timer) {
        clearInterval(timer);
      }
    };
  };
}

export function cultMeshPollingDocumentWatcher<TDocument>(
  readDocument: (context: CultMeshQueryContext) => Promise<TDocument>,
  options: CultMeshPollingWatchOptions<TDocument> = {},
): CultMeshDocumentWatcher<TDocument> {
  const watch = cultMeshPollingQueryWatcher<void, TDocument>(
    async (_parameters, context) => readDocument(context),
    options,
  );
  return (context, callback) => watch(undefined, context, callback);
}

export function cultMeshPollingCollectionWatcher<TDocument>(
  readCollection: (context: CultMeshQueryContext) => Promise<CultMeshCollectionSnapshot<TDocument>>,
  options: CultMeshPollingWatchOptions<CultMeshCollectionSnapshot<TDocument>> = {},
): CultMeshCollectionWatcher<TDocument> {
  const watch = cultMeshPollingQueryWatcher<void, CultMeshCollectionSnapshot<TDocument>>(
    async (_parameters, context) => readCollection(context),
    {
      ...options,
      equals: options.equals ?? ((left, right) => JSON.stringify(left) === JSON.stringify(right)),
    },
  );
  return (context, callback) => watch(undefined, context, () => callback({
    kind: "reset",
  }));
}

export function cultMeshStatePointer<T>(
  pointerId: string,
  resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
  watchPointer?:
    | ((callback: (value: T) => void) => CultMeshUnsubscribe)
    | CultMeshStatePointerWatcher<T>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
  } = {},
): CultMeshStatePointer<T> {
  return new CultMeshStatePointer(pointerId, resolvePointer, watchPointer, options);
}

export function cultMeshMutableStatePointer<T>(
  pointerId: string,
  resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
  watchPointer:
    | ((callback: (value: T) => void) => CultMeshUnsubscribe)
    | CultMeshStatePointerWatcher<T>,
  replacePointer:
    | ((value: T) => Promise<void>)
    | CultMeshMutableStatePointerReplacer<T>,
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
  } = {},
): CultMeshMutableStatePointer<T> {
  return new CultMeshMutableStatePointer(
    pointerId,
    resolvePointer,
    watchPointer,
    replacePointer,
    options,
  );
}

export function cultMeshProjectionSource(
  sourceId: string,
  options: { schemaId?: string; description?: string } = {},
): CultMeshProjectionSource {
  requireNonEmpty(sourceId, "sourceId");
  return {
    sourceId,
    schemaId: options.schemaId,
    description: options.description,
  };
}

export class CultMeshStateRefResolver {
  public readonly sources: readonly CultMeshProjectionSource[];
  public readonly routeHint: CultMeshRouteHint;

  public constructor(
    public readonly resolverId: string,
    private readonly resolveRef: (stateRef: string, context: CultMeshQueryContext) => string | undefined,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ) {
    requireNonEmpty(resolverId, "resolverId");
    this.sources = [...(options.sources ?? [])];
    this.routeHint = options.routeHint ?? cultMeshRouteHint();
  }

  public resolve(stateRef: string, context: CultMeshQueryContext | string = "local"): string {
    if (!stateRef.trim()) {
      return "";
    }
    return (
      this.resolveRef(
        stateRef,
        this.resolveContext(typeof context === "string" ? cultMeshQueryContext(context) : context),
      ) ?? ""
    );
  }

  public tryResolve(
    stateRef: string,
    context: CultMeshQueryContext | string = "local",
  ): { resolved: boolean; value: string } {
    const value = this.resolve(stateRef, context);
    return { resolved: value.length > 0, value };
  }

  public or(fallback: CultMeshStateRefResolver): CultMeshStateRefResolver {
    return new CultMeshStateRefResolver(
      `${this.resolverId}|${fallback.resolverId}`,
      (stateRef, context) => {
        const value = this.resolve(stateRef, context);
        return value.length > 0 ? value : fallback.resolve(stateRef, context);
      },
      {
        sources: [...this.sources, ...fallback.sources],
        routeHint: this.routeHint.kind === "automatic" ? fallback.routeHint : this.routeHint,
      },
    );
  }

  public asFunction(): (stateRef: string) => string {
    return (stateRef) => this.resolve(stateRef);
  }

  private resolveContext(context: CultMeshQueryContext): CultMeshQueryContext {
    if (context.routeHint.kind !== "automatic" || this.routeHint.kind === "automatic") {
      return context;
    }
    return cultMeshQueryContext(context.runtimeId, { routeHint: this.routeHint });
  }
}

export function cultMeshStateRefResolver(
  resolverId: string,
  resolveRef:
    | ((stateRef: string) => string | undefined)
    | ((stateRef: string, context: CultMeshQueryContext) => string | undefined),
  options: {
    sources?: readonly CultMeshProjectionSource[];
    routeHint?: CultMeshRouteHint;
  } = {},
): CultMeshStateRefResolver {
  return new CultMeshStateRefResolver(
    resolverId,
    (stateRef, context) =>
      resolveRef.length <= 1
        ? (resolveRef as (stateRef: string) => string | undefined)(stateRef)
        : (resolveRef as (stateRef: string, context: CultMeshQueryContext) => string | undefined)(
            stateRef,
            context,
          ),
    options,
  );
}

export function cultMeshDescribeStateRefResolver(
  resolver: CultMeshStateRefResolver,
): CultMeshStateRefResolverDiagnostic {
  return {
    resolverId: resolver.resolverId,
    routeHint: resolver.routeHint,
    sources: [...resolver.sources],
  };
}

export function cultMeshStateBinding(
  targetProp: string,
  pointer:
    | {
        readonly pointerId: string;
        readonly routeHint?: CultMeshRouteHint;
        readonly sources?: readonly CultMeshProjectionSource[];
      }
    | string,
  options: {
    sourceId?: string;
    schemaId?: string;
    routeHint?: CultMeshRouteHint;
  } = {},
): CultMeshStateBindingDescriptor {
  requireNonEmpty(targetProp, "targetProp");
  if (typeof pointer === "string") {
    requireNonEmpty(pointer, "pointerId");
    return {
      targetProp,
      pointerId: pointer,
      sourceId: options.sourceId,
      schemaId: options.schemaId,
      routeHint: options.routeHint ?? cultMeshRouteHint(),
    };
  }

  requireNonEmpty(pointer.pointerId, "pointerId");
  const source = pointer.sources?.[0];
  return {
    targetProp,
    pointerId: pointer.pointerId,
    sourceId: options.sourceId ?? source?.sourceId,
    schemaId: options.schemaId ?? source?.schemaId,
    routeHint: options.routeHint ?? pointer.routeHint ?? cultMeshRouteHint(),
  };
}

export function cultMeshStateBindingRecord(
  binding?: Partial<CultMeshStateBindingDescriptor>,
): CultMeshStateBindingRecord;
export function cultMeshStateBindingRecord(
  targetProp?: string,
  pointerId?: string,
  sourceId?: string,
  schemaId?: string,
  routeKind?: string,
  routeDescription?: string,
): CultMeshStateBindingRecord;
export function cultMeshStateBindingRecord(
  bindingOrTargetProp?: Partial<CultMeshStateBindingDescriptor> | string,
  pointerId?: string,
  sourceId?: string,
  schemaId?: string,
  routeKind?: string,
  routeDescription?: string,
): CultMeshStateBindingRecord {
  if (typeof bindingOrTargetProp === "object" || bindingOrTargetProp === undefined) {
    const binding = bindingOrTargetProp;
    const route = cultMeshRouteRecord(binding?.routeHint);
    return {
      targetProp: binding?.targetProp?.trim() || "value",
      pointerId: binding?.pointerId ?? "",
      sourceId: binding?.sourceId ?? "",
      schemaId: binding?.schemaId ?? "",
      routeKind: route.kind,
      routeDescription: route.description,
    };
  }

  return {
    targetProp: bindingOrTargetProp?.trim() || "value",
    pointerId: pointerId ?? "",
    sourceId: sourceId ?? "",
    schemaId: schemaId ?? "",
    routeKind: routeKind ?? "",
    routeDescription: routeDescription ?? "",
  };
}

export function cultMeshStateBindingFromRecord(
  record: Partial<CultMeshStateBindingRecord>,
  options: {
    fallbackRouteHint?: CultMeshRouteHint;
    fallbackTargetProp?: string;
  } = {},
): CultMeshStateBindingDescriptor {
  const targetProp = record.targetProp?.trim() || options.fallbackTargetProp?.trim() || "value";
  return {
    targetProp,
    pointerId: record.pointerId?.trim() || `${targetProp}.unknown`,
    sourceId: record.sourceId ?? "",
    schemaId: record.schemaId ?? "",
    routeHint: cultMeshRouteFromRecord(
      {
        kind: record.routeKind,
        description: record.routeDescription,
      },
      options.fallbackRouteHint,
    ),
  };
}

export function cultMeshOperationBinding(
  operation:
    | {
        readonly operationId: string;
      }
    | string,
  options: {
    label?: string;
    schemaId?: string;
    routeHint?: CultMeshRouteHint;
  } = {},
): CultMeshOperationBindingDescriptor {
  const operationId = typeof operation === "string" ? operation : operation.operationId;
  requireNonEmpty(operationId, "operationId");
  return {
    operationId,
    label: options.label ?? "",
    schemaId: options.schemaId ?? "",
    routeHint: options.routeHint ?? cultMeshRouteHint(),
  };
}

export function cultMeshOperationBindingRecord(
  binding?: Partial<CultMeshOperationBindingDescriptor>,
): CultMeshOperationBindingRecord;
export function cultMeshOperationBindingRecord(
  operationId?: string,
  label?: string,
  schemaId?: string,
  routeKind?: string,
  routeDescription?: string,
): CultMeshOperationBindingRecord;
export function cultMeshOperationBindingRecord(
  bindingOrOperationId?: Partial<CultMeshOperationBindingDescriptor> | string,
  label?: string,
  schemaId?: string,
  routeKind?: string,
  routeDescription?: string,
): CultMeshOperationBindingRecord {
  if (typeof bindingOrOperationId === "object" || bindingOrOperationId === undefined) {
    const binding = bindingOrOperationId;
    const route = cultMeshRouteRecord(binding?.routeHint);
    return {
      operationId: binding?.operationId ?? "",
      label: binding?.label ?? "",
      schemaId: binding?.schemaId ?? "",
      routeKind: route.kind,
      routeDescription: route.description,
    };
  }

  return {
    operationId: bindingOrOperationId ?? "",
    label: label ?? "",
    schemaId: schemaId ?? "",
    routeKind: routeKind ?? "",
    routeDescription: routeDescription ?? "",
  };
}

export function cultMeshOperationBindingFromRecord(
  record: Partial<CultMeshOperationBindingRecord>,
  options: {
    fallbackRouteHint?: CultMeshRouteHint;
    fallbackOperationId?: string;
  } = {},
): CultMeshOperationBindingDescriptor {
  return {
    operationId: record.operationId?.trim() || options.fallbackOperationId || "",
    label: record.label ?? "",
    schemaId: record.schemaId ?? "",
    routeHint: cultMeshRouteFromRecord(
      {
        kind: record.routeKind,
        description: record.routeDescription,
      },
      options.fallbackRouteHint,
    ),
  };
}

export function cultMeshOperationInvocation(
  operation:
    | {
        readonly operationId: string;
        readonly schemaId?: string;
        readonly routeHint?: CultMeshRouteHint;
      }
    | string,
  options: {
    schemaId?: string;
    routeHint?: CultMeshRouteHint;
    idempotencyKey?: string;
  } = {},
): CultMeshOperationInvocationDescriptor {
  const operationId = typeof operation === "string" ? operation : operation.operationId;
  requireNonEmpty(operationId, "operationId");
  return {
    operationId,
    schemaId: options.schemaId ?? (typeof operation === "string" ? "" : operation.schemaId) ?? "",
    routeHint:
      options.routeHint ??
      (typeof operation === "string" ? undefined : operation.routeHint) ??
      cultMeshRouteHint(),
    idempotencyKey: options.idempotencyKey,
  };
}

export function cultMeshOperationInvocationRecord(
  invocation?: CultMeshOperationInvocationDescriptor,
  options: {
    fallbackOperationId?: string;
    fallbackSchemaId?: string;
    fallbackRouteHint?: CultMeshRouteHint;
    fallbackIdempotencyKey?: string;
  } = {},
): CultMeshOperationInvocationRecord {
  const route = cultMeshRouteRecord(invocation?.routeHint ?? options.fallbackRouteHint);
  return {
    operationId: nonBlankOr(invocation?.operationId, options.fallbackOperationId),
    schemaId: nonBlankOr(invocation?.schemaId, options.fallbackSchemaId),
    routeKind: route.kind,
    routeDescription: route.description,
    idempotencyKey: nonBlankOr(invocation?.idempotencyKey, options.fallbackIdempotencyKey),
  };
}

export function cultMeshOperationInvocationFromRecord(
  record: Partial<CultMeshOperationInvocationRecord>,
  options: {
    fallbackOperationId?: string;
    fallbackSchemaId?: string;
    fallbackRouteHint?: CultMeshRouteHint;
    fallbackIdempotencyKey?: string;
  } = {},
): CultMeshOperationInvocationDescriptor {
  const operationId = nonBlankOr(record.operationId, options.fallbackOperationId);
  requireNonEmpty(operationId, "operationId");
  const fallbackRoute = options.fallbackRouteHint ?? cultMeshRouteHint();
  const route = cultMeshRouteFromRecord(
    {
      kind: record.routeKind,
      description: record.routeDescription,
    },
    fallbackRoute,
  );
  return {
    operationId,
    schemaId: nonBlankOr(record.schemaId, options.fallbackSchemaId),
    routeHint: route,
    idempotencyKey: nonBlankOr(record.idempotencyKey, options.fallbackIdempotencyKey) || undefined,
  };
}

export function cultMeshOperationPayload(
  fields: Readonly<Record<string, string | number | boolean | undefined>> = {},
): CultMeshOperationPayload {
  const normalized: Record<string, string> = {};
  for (const [key, value] of Object.entries(fields)) {
    if (!key.trim() || value === undefined) {
      continue;
    }
    normalized[key] = String(value);
  }

  return {
    fields: Object.freeze({ ...normalized }),
    getString(key, defaultValue = "") {
      return normalized[key] ?? defaultValue;
    },
    getInt(key, defaultValue = 0) {
      const raw = normalized[key];
      if (raw === undefined || raw.trim() === "") {
        return defaultValue;
      }
      const parsed = Number.parseInt(raw, 10);
      return Number.isNaN(parsed) ? defaultValue : parsed;
    },
    getDouble(key, defaultValue = 0) {
      const raw = normalized[key];
      if (raw === undefined || raw.trim() === "") {
        return defaultValue;
      }
      const parsed = Number.parseFloat(raw);
      return Number.isNaN(parsed) ? defaultValue : parsed;
    },
    getBoolean(key, defaultValue = false) {
      const raw = normalized[key]?.toLowerCase();
      if (raw === "true" || raw === "1" || raw === "yes" || raw === "on") {
        return true;
      }
      if (raw === "false" || raw === "0" || raw === "no" || raw === "off") {
        return false;
      }
      return defaultValue;
    },
    with(key, value) {
      requireNonEmpty(key, "key");
      return cultMeshOperationPayload({
        ...normalized,
        [key]: value,
      });
    },
    toRecord() {
      return Object.freeze({ ...normalized });
    },
  };
}

export function cultMeshProjectionRecipe<TParameters, TResult>(
  projectionId: string,
  sources: readonly CultMeshProjectionSource[],
  projectProjection: (
    parameters: TParameters,
    context: CultMeshQueryContext,
  ) => Promise<TResult>,
  options: {
    routeHint?: CultMeshRouteHint;
    watchProjection?: (
      parameters: TParameters,
      context: CultMeshQueryContext,
      callback: (value: TResult) => void,
    ) => CultMeshUnsubscribe;
  } = {},
): CultMeshProjectionRecipe<TParameters, TResult> {
  return new CultMeshProjectionRecipe(
    projectionId,
    sources,
    projectProjection,
    options,
  );
}

export function cultMeshDescribeProjectionRecipe(
  recipe: {
    readonly projectionId: string;
    readonly routeHint: CultMeshRouteHint;
    readonly sources: readonly CultMeshProjectionSource[];
  },
): CultMeshProjectionRecipeDiagnostic {
  return {
    projectionId: recipe.projectionId,
    routeHint: recipe.routeHint,
    sources: [...recipe.sources],
  };
}

export function cultMeshDescribeSurface(
  surface:
    | {
        readonly queryId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly operationId: string;
      }
    | {
        readonly projectionId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly feedId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly documentId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly collectionId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly pointerId: string;
        readonly routeHint?: CultMeshRouteHint;
        readonly sources?: readonly CultMeshProjectionSource[];
      }
    | CultMeshNativeSliceViewDescriptor,
): CultMeshSurfaceDiagnostic {
  if ("queryId" in surface) {
    return cultMeshSurfaceDiagnostic("query", surface.queryId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    });
  }

  if ("operationId" in surface) {
    return cultMeshSurfaceDiagnostic("operation", surface.operationId);
  }

  if ("pointerId" in surface) {
    return cultMeshSurfaceDiagnostic("state-pointer", surface.pointerId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    });
  }

  if ("documentId" in surface) {
    return cultMeshSurfaceDiagnostic("document", surface.documentId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    });
  }

  if ("collectionId" in surface) {
    return cultMeshSurfaceDiagnostic("collection", surface.collectionId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    });
  }

  if ("viewId" in surface) {
    return cultMeshSurfaceDiagnostic("native-slice-view", surface.viewId, {
      routeHint: surface.route,
    });
  }

  if ("projectionId" in surface) {
    return cultMeshSurfaceDiagnostic("projection-recipe", surface.projectionId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    });
  }

  return cultMeshSurfaceDiagnostic("live-feed", surface.feedId, {
    routeHint: surface.routeHint,
    sources: surface.sources,
  });
}

export function cultMeshSurfaceDiagnostic(
  kind: CultMeshSurfaceKind,
  surfaceId: string,
  options: {
    routeHint?: CultMeshRouteHint;
    sources?: readonly CultMeshProjectionSource[];
  } = {},
): CultMeshSurfaceDiagnostic {
  requireNonEmpty(surfaceId, "surfaceId");
  return {
    kind,
    surfaceId,
    routeHint: options.routeHint ?? cultMeshRouteHint(),
    sources: [...(options.sources ?? [])],
  };
}

export function cultMeshDescribeSurfaceCatalog(
  catalogId: string,
  surfaces: readonly CultMeshSurfaceDiagnostic[],
): CultMeshSurfaceCatalogDiagnostic {
  requireNonEmpty(catalogId, "catalogId");
  return {
    catalogId,
    surfaces: surfaces.map(surface => cultMeshSurfaceDiagnostic(surface.kind, surface.surfaceId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    })),
  };
}

export function cultMeshFindSurface(
  catalog: CultMeshSurfaceCatalogDiagnostic,
  surfaceId: string,
): CultMeshSurfaceDiagnostic | undefined {
  requireNonEmpty(surfaceId, "surfaceId");
  return catalog.surfaces.find(surface => surface.surfaceId === surfaceId);
}

export function cultMeshSurfacesByKind(
  catalog: CultMeshSurfaceCatalogDiagnostic,
  kind: CultMeshSurfaceKind,
): CultMeshSurfaceDiagnostic[] {
  return catalog.surfaces
    .filter(surface => surface.kind === kind)
    .map(surface => cultMeshSurfaceDiagnostic(surface.kind, surface.surfaceId, {
      routeHint: surface.routeHint,
      sources: surface.sources,
    }));
}

export function cultMeshSurfaceCatalogIndex(
  catalog: CultMeshSurfaceCatalogDiagnostic,
): CultMeshSurfaceCatalogIndexDiagnostic {
  return {
    catalogId: catalog.catalogId,
    queries: cultMeshSurfacesByKind(catalog, "query"),
    projectionRecipes: cultMeshSurfacesByKind(catalog, "projection-recipe"),
    liveFeeds: cultMeshSurfacesByKind(catalog, "live-feed"),
    operations: cultMeshSurfacesByKind(catalog, "operation"),
    documents: cultMeshSurfacesByKind(catalog, "document"),
    collections: cultMeshSurfacesByKind(catalog, "collection"),
    statePointers: cultMeshSurfacesByKind(catalog, "state-pointer"),
    nativeSliceViews: cultMeshSurfacesByKind(catalog, "native-slice-view"),
  };
}

export function cultMeshNativeSliceColumn(
  name: string,
  valueType: string,
  elementSizeBytes: number,
): CultMeshNativeSliceColumn {
  requireNonEmpty(name, "name");
  requireNonEmpty(valueType, "valueType");
  if (!Number.isInteger(elementSizeBytes) || elementSizeBytes <= 0) {
    throw new Error("elementSizeBytes must be a positive integer.");
  }

  return { name, valueType, elementSizeBytes };
}

export function cultMeshNativeSliceView(
  viewId: string,
  schemaId: string,
  rowCount: number,
  columns: readonly CultMeshNativeSliceColumn[],
  options: { route?: CultMeshRouteHint; nativeHandle?: string } = {},
): CultMeshNativeSliceViewDescriptor {
  requireNonEmpty(viewId, "viewId");
  requireNonEmpty(schemaId, "schemaId");
  if (!Number.isInteger(rowCount) || rowCount < 0) {
    throw new Error("rowCount must be a non-negative integer.");
  }

  return {
    viewId,
    schemaId,
    rowCount,
    columns: [...columns],
    route: options.route ?? cultMeshRouteHint(),
    nativeHandle: options.nativeHandle,
  };
}

export function cultMeshDenseRowStrideBytes(
  view: CultMeshNativeSliceViewDescriptor,
): number {
  return view.columns.reduce((sum, column) => sum + column.elementSizeBytes, 0);
}

export function cultMeshFindNativeSliceColumn(
  view: CultMeshNativeSliceViewDescriptor,
  name: string,
): CultMeshNativeSliceColumn | undefined {
  requireNonEmpty(name, "name");
  return view.columns.find((column) => column.name === name);
}

export function cultMeshDescribeNativeSliceView(
  view: CultMeshNativeSliceViewDescriptor,
): CultMeshNativeSliceViewDiagnostic {
  return {
    viewId: view.viewId,
    schemaId: view.schemaId,
    rowCount: view.rowCount,
    columns: [...view.columns],
    route: view.route,
    nativeHandle: view.nativeHandle,
    denseRowStrideBytes: cultMeshDenseRowStrideBytes(view),
  };
}

export interface CultMeshNodeOptions {
  documents?: Iterable<AnyCultCacheDocumentDefinition>;
  bindings?: Iterable<CultNetDocumentBinding>;
  store?: CacheBackingStore;
  pullOnStart?: boolean;
}

export class CultMeshNode {
  public constructor(
    public readonly cache: CultCache,
    public readonly store: CacheBackingStore,
    public readonly documents: CultNetDocumentRegistry,
  ) {}

  public get<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> | undefined {
    return this.cache.get(definition, key);
  }

  public getRequired<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): CultCacheDocumentValue<TDefinition> {
    return this.cache.getRequired(definition, key);
  }

  public put<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    value: CultCacheDocumentValue<TDefinition>,
  ): Promise<CultCacheDocumentValue<TDefinition>> {
    return this.cache.put(definition, key, value);
  }

  public delete<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
  ): Promise<boolean> {
    return this.cache.delete(definition, key);
  }

  public document<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    key: string,
    options: {
      documentId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshDocumentFromCache(this.cache, definition, key, options);
  }

  public globalDocument<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    options: {
      documentId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshGlobalDocumentFromCache(this.cache, definition, options);
  }

  public collection<TDefinition extends AnyCultCacheDocumentDefinition>(
    definition: TDefinition,
    options: {
      collectionId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshCollectionHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshCollectionFromCache(this.cache, definition, options);
  }

  public async flush(soft = false): Promise<void> {
    await this.store.pushAll?.(this.cache.snapshot(), { soft });
  }
}

export class CultMeshVerseCatalog<TVerse = unknown> {
  readonly #verses = new Map<string, TVerse>();
  readonly #subscribers = new Set<(verse: TVerse) => void>();

  public get verses(): readonly TVerse[] {
    return [...this.#verses.entries()]
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([, verse]) => verse);
  }

  public watch(callback: (verse: TVerse) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(verseId: string, verse: TVerse): void {
    requireNonEmpty(verseId, "verseId");
    this.#verses.set(verseId, verse);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(verse);
    }
  }

  public get(verseId: string): TVerse | undefined {
    requireNonEmpty(verseId, "verseId");
    return this.#verses.get(verseId);
  }
}

export interface CultMeshPeerCard {
  peerId: string;
  verseId: string;
  endpoints: readonly string[];
  roles?: readonly string[];
  shardIds?: readonly string[];
  authorityLeaseId?: string;
}

export interface CultMeshRudpDocumentPut {
  schemaId: string;
  recordKey: string;
  storedAt: string;
  payload: unknown;
  sourceRuntimeId: string | null;
  sourceAgentId: string | null;
  sourceRole: string | null;
  tags: string[];
  remote: {
    address: string;
    family: string;
    port: number;
  };
}

export interface CultMeshRudpDocumentServerOptions extends CultMeshRudpSocketOptions {
  documents: CultNetDocumentRegistry;
  getCache?: () => Promise<CultCache> | CultCache;
  onError?: (error: Error) => void;
  onDocumentPutRaw?: (document: CultMeshRudpDocumentPut) => void | Promise<void>;
  wireContract?: CultNetWireContract;
  sessionTimeoutMs?: number;
}

export interface CultMeshRudpDocumentServer {
  readonly bind: { host: string; port: number };
  start(): Promise<void>;
  close(): void;
}

export interface CultMeshRudpDocumentPublishOptions extends CultMeshRudpPeerOptions {
  messageId?: string;
  sourceRuntimeId?: string;
  sourceAgentId?: string;
  sourceRole?: string;
  tags?: string[];
  flushTimeoutMs?: number;
}

export interface CultMeshRudpEndpoint {
  host: string;
  port: number;
  uri: string;
}

export interface CultMeshRudpSocketOptions {
  bindHost?: string;
  bindPort?: number;
  socket?: Socket;
  initialSequence?: number;
  resendDelayMs?: number;
  resendPollMs?: number;
  transportId?: string;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
  maxPendingReliablePackets?: number;
  reconnectPolicy?: CultNetReconnectPolicy;
}

export interface CultMeshAuthorizedRudpSocketOptions
  extends CultMeshRudpSocketOptions {
  shardId?: string;
  at?: Date;
}

export interface CultMeshRudpPeerOptions extends CultMeshRudpSocketOptions {
  connectPayload?: Uint8Array;
  connectTimeoutMs?: number;
  wireContract?: CultNetWireContract;
}

export interface CultMeshAuthorizedRudpPeerOptions
  extends CultMeshRudpPeerOptions {
  shardId?: string;
  at?: Date;
}

export class CultMeshPeerCatalog {
  readonly #peers = new Map<string, CultMeshPeerCard>();
  readonly #subscribers = new Set<(peer: CultMeshPeerCard) => void>();

  public get peers(): readonly CultMeshPeerCard[] {
    return [...this.#peers.values()].sort((left, right) =>
      left.peerId.localeCompare(right.peerId),
    );
  }

  public watch(callback: (peer: CultMeshPeerCard) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(peer: CultMeshPeerCard): void {
    requireNonEmpty(peer.peerId, "peer.peerId");
    requireNonEmpty(peer.verseId, "peer.verseId");
    this.#peers.set(peer.peerId, peer);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(peer);
    }
  }

  public get(peerId: string): CultMeshPeerCard | undefined {
    requireNonEmpty(peerId, "peerId");
    return this.#peers.get(peerId);
  }

  public find(verseId: string, role?: string): readonly CultMeshPeerCard[] {
    requireNonEmpty(verseId, "verseId");
    return this.peers.filter(
      (peer) =>
        peer.verseId === verseId &&
        (!role || peer.roles?.includes(role) === true),
    );
  }

  public findAuthorized(
    verseId: string,
    role: string,
    leases: CultMeshAuthorityLeaseCatalog,
    shardId?: string,
    at = new Date(),
  ): readonly CultMeshPeerCard[] {
    requireNonEmpty(verseId, "verseId");
    requireNonEmpty(role, "role");
    return this.find(verseId, role).filter((peer) =>
      leases.isAuthorized(peer, role, shardId, at),
    );
  }

  public firstAuthorized(
    verseId: string,
    role: string,
    leases: CultMeshAuthorityLeaseCatalog,
    shardId?: string,
    at = new Date(),
  ): CultMeshPeerCard | undefined {
    return this.findAuthorized(verseId, role, leases, shardId, at)[0];
  }
}

export interface CultMeshAuthorityLease {
  leaseId: string;
  verseId: string;
  peerId: string;
  roles: readonly string[];
  shardIds?: readonly string[];
  validFrom: Date;
  expiresAt: Date;
}

export class CultMeshAuthorityLeaseCatalog {
  readonly #leases = new Map<string, CultMeshAuthorityLease>();
  readonly #subscribers = new Set<(lease: CultMeshAuthorityLease) => void>();

  public get leases(): readonly CultMeshAuthorityLease[] {
    return [...this.#leases.values()].sort((left, right) =>
      left.leaseId.localeCompare(right.leaseId),
    );
  }

  public watch(callback: (lease: CultMeshAuthorityLease) => void): () => void {
    this.#subscribers.add(callback);
    return () => {
      this.#subscribers.delete(callback);
    };
  }

  public upsert(lease: CultMeshAuthorityLease): void {
    requireNonEmpty(lease.leaseId, "lease.leaseId");
    requireNonEmpty(lease.verseId, "lease.verseId");
    requireNonEmpty(lease.peerId, "lease.peerId");
    if (lease.expiresAt <= lease.validFrom) {
      throw new Error("CultMesh authority lease expiry must be after validFrom.");
    }

    this.#leases.set(lease.leaseId, lease);
    for (const subscriber of [...this.#subscribers]) {
      subscriber(lease);
    }
  }

  public get(leaseId: string): CultMeshAuthorityLease | undefined {
    requireNonEmpty(leaseId, "leaseId");
    return this.#leases.get(leaseId);
  }

  public isAuthorized(
    peer: CultMeshPeerCard,
    role: string,
    shardId?: string,
    at = new Date(),
  ): boolean {
    requireNonEmpty(role, "role");
    if (!peer.authorityLeaseId) {
      return false;
    }

    const lease = this.#leases.get(peer.authorityLeaseId);
    if (!lease) {
      return false;
    }

    return (
      at >= lease.validFrom &&
      at < lease.expiresAt &&
      lease.verseId === peer.verseId &&
      lease.peerId === peer.peerId &&
      lease.roles.includes(role) &&
      peer.roles?.includes(role) === true &&
      (!shardId ||
        !lease.shardIds ||
        lease.shardIds.length === 0 ||
        lease.shardIds.includes(shardId))
    );
  }
}

export type CultMeshStreamKind = "audio" | "video" | "tensor" | "bytes";

export type CultMeshStreamBodyTransport =
  | "shared-memory"
  | "shared-d3d12-texture"
  | "shared-d3d11-texture"
  | "dma-buf"
  | "iosurface"
  | "ahardwarebuffer"
  | "cultcache-page"
  | "inline-bytes";

export type CultMeshStreamAccess = "read" | "write" | "read-write";

export interface CultMeshStreamClock {
  clockDomainId: string;
  sourceId?: string;
  sampleRate?: number;
  offsetToVerseTimeNs?: number;
  confidence?: number;
  evidenceKind?: string;
}

export interface CultMeshAudioStreamFormat {
  sampleRate: number;
  channels: number;
  sampleFormat: "float32" | "int16" | "int24" | "int32";
  framesPerPacket?: number;
}

export interface CultMeshVideoStreamFormat {
  width: number;
  height: number;
  pixelFormat: string;
  framesPerSecond?: number;
  planeCount?: number;
}

export interface CultMeshStreamDescriptor {
  streamId: string;
  verseId: string;
  ownerPeerId: string;
  kind: CultMeshStreamKind;
  label?: string;
  clock: CultMeshStreamClock;
  audio?: CultMeshAudioStreamFormat;
  video?: CultMeshVideoStreamFormat;
  preferredTransports: readonly CultMeshStreamBodyTransport[];
  requiredAccess?: CultMeshStreamAccess;
  maxInFlightFrames?: number;
  metadataSchemaId?: string;
}

export interface CultMeshStreamConsumerProfile {
  peerId: string;
  verseId: string;
  supportedTransports: readonly CultMeshStreamBodyTransport[];
  acceptedKinds?: readonly CultMeshStreamKind[];
  canImportGpuHandles?: boolean;
  canMapSharedMemory?: boolean;
  maxInFlightFrames?: number;
}

export interface CultMeshStreamNegotiation {
  streamId: string;
  producerPeerId: string;
  consumerPeerId: string;
  transport: CultMeshStreamBodyTransport;
  access: CultMeshStreamAccess;
  maxInFlightFrames: number;
  copyBudget: "zero-copy-target" | "one-copy-fallback" | "opaque-runtime";
}

export interface CultMeshStreamFrameHandle {
  streamId: string;
  sequence: bigint;
  timestampNs: bigint;
  durationNs?: bigint;
  transport: CultMeshStreamBodyTransport;
  byteLength?: number;
  nativeHandle?: string;
  resourceKey?: string;
  pageRef?: string;
  fenceHandle?: string;
  fenceValue?: bigint;
  unavoidableCopyCount?: number;
  metadata?: Record<string, unknown>;
}

export class CultMeshStreamCatalog {
  readonly #streams = new Map<string, CultMeshStreamDescriptor>();
  readonly #latestFrames = new Map<string, CultMeshStreamFrameHandle>();
  readonly #streamSubscribers = new Set<(stream: CultMeshStreamDescriptor) => void>();
  readonly #frameSubscribers = new Set<(frame: CultMeshStreamFrameHandle) => void>();

  public get streams(): readonly CultMeshStreamDescriptor[] {
    return [...this.#streams.values()].sort((left, right) =>
      left.streamId.localeCompare(right.streamId),
    );
  }

  public watch(callback: (stream: CultMeshStreamDescriptor) => void): () => void {
    this.#streamSubscribers.add(callback);
    return () => {
      this.#streamSubscribers.delete(callback);
    };
  }

  public watchFrames(callback: (frame: CultMeshStreamFrameHandle) => void): () => void {
    this.#frameSubscribers.add(callback);
    return () => {
      this.#frameSubscribers.delete(callback);
    };
  }

  public declare(stream: CultMeshStreamDescriptor): CultMeshStreamDescriptor {
    requireNonEmpty(stream.streamId, "stream.streamId");
    requireNonEmpty(stream.verseId, "stream.verseId");
    requireNonEmpty(stream.ownerPeerId, "stream.ownerPeerId");
    requireNonEmpty(stream.clock.clockDomainId, "stream.clock.clockDomainId");
    if (stream.preferredTransports.length === 0) {
      throw new Error("stream.preferredTransports must not be empty.");
    }

    this.#streams.set(stream.streamId, stream);
    for (const subscriber of [...this.#streamSubscribers]) {
      subscriber(stream);
    }
    return stream;
  }

  public get(streamId: string): CultMeshStreamDescriptor | undefined {
    requireNonEmpty(streamId, "streamId");
    return this.#streams.get(streamId);
  }

  public find(
    verseId: string,
    kind?: CultMeshStreamKind,
  ): readonly CultMeshStreamDescriptor[] {
    requireNonEmpty(verseId, "verseId");
    return this.streams.filter(
      (stream) => stream.verseId === verseId && (!kind || stream.kind === kind),
    );
  }

  public negotiate(
    streamId: string,
    consumer: CultMeshStreamConsumerProfile,
  ): CultMeshStreamNegotiation {
    const stream = this.get(streamId);
    if (!stream) {
      throw new Error(`Unknown CultMesh stream '${streamId}'.`);
    }

    if (consumer.verseId !== stream.verseId) {
      throw new Error("stream and consumer must belong to the same Verse.");
    }

    if (
      consumer.acceptedKinds &&
      !consumer.acceptedKinds.includes(stream.kind)
    ) {
      throw new Error(`consumer does not accept ${stream.kind} streams.`);
    }

    const transport = stream.preferredTransports.find((candidate) =>
      consumer.supportedTransports.includes(candidate),
    );
    if (!transport) {
      throw new Error("stream and consumer have no compatible body transport.");
    }

    return {
      streamId: stream.streamId,
      producerPeerId: stream.ownerPeerId,
      consumerPeerId: consumer.peerId,
      transport,
      access: stream.requiredAccess ?? "read",
      maxInFlightFrames: Math.min(
        stream.maxInFlightFrames ?? Number.MAX_SAFE_INTEGER,
        consumer.maxInFlightFrames ?? Number.MAX_SAFE_INTEGER,
      ),
      copyBudget: copyBudgetFor(transport),
    };
  }

  public publishFrame(frame: CultMeshStreamFrameHandle): CultMeshStreamFrameHandle {
    requireNonEmpty(frame.streamId, "frame.streamId");
    if (!this.#streams.has(frame.streamId)) {
      throw new Error(`Unknown CultMesh stream '${frame.streamId}'.`);
    }

    this.#latestFrames.set(frame.streamId, frame);
    for (const subscriber of [...this.#frameSubscribers]) {
      subscriber(frame);
    }
    return frame;
  }

  public latestFrame(streamId: string): CultMeshStreamFrameHandle | undefined {
    requireNonEmpty(streamId, "streamId");
    return this.#latestFrames.get(streamId);
  }
}

export class CultMesh {
  public static vec2(x: number, y: number): CultMeshVec2 {
    return cultMeshVec2(x, y);
  }

  public static rect(min: CultMeshVec2, max: CultMeshVec2): CultMeshRect {
    return cultMeshRect(min, max);
  }

  public static rectFromBounds(
    minX: number,
    minY: number,
    maxX: number,
    maxY: number,
  ): CultMeshRect {
    return cultMeshRectFromBounds(minX, minY, maxX, maxY);
  }

  public static viewportRequest(
    viewport: CultMeshRect,
    controlledEntityIndices?: readonly number[],
  ): CultMeshViewportRequest {
    return cultMeshViewportRequest(viewport, controlledEntityIndices);
  }

  public static routeHint(
    kind: CultMeshLocalityKind = "automatic",
    description?: string,
  ): CultMeshRouteHint {
    return cultMeshRouteHint(kind, description);
  }

  public static routeRecord(routeHint?: CultMeshRouteHint): CultMeshRouteRecord;
  public static routeRecord(kind?: string, description?: string): CultMeshRouteRecord;
  public static routeRecord(
    routeOrKind?: CultMeshRouteHint | string,
    description = "",
  ): CultMeshRouteRecord {
    return typeof routeOrKind === "string" || routeOrKind === undefined
      ? cultMeshRouteRecord(routeOrKind, description)
      : cultMeshRouteRecord(routeOrKind);
  }

  public static routeFromRecord(
    record: Partial<CultMeshRouteRecord>,
    fallback: CultMeshRouteHint = cultMeshRouteHint(),
  ): CultMeshRouteHint {
    return cultMeshRouteFromRecord(record, fallback);
  }

  public static authorityClaim(
    role: string,
    options: { shardId?: string; leaseId?: string } = {},
  ): CultMeshAuthorityClaim {
    return cultMeshAuthorityClaim(role, options);
  }

  public static verseContext(
    verseId: string,
    runtimeId: string,
    options: {
      routeHint?: CultMeshRouteHint;
      claims?: readonly CultMeshAuthorityClaim[];
    } = {},
  ): CultMeshVerseContext {
    return cultMeshVerseContext(verseId, runtimeId, options);
  }

  public static verse(
    verseId: string,
    runtimeId: string,
    options: {
      routeHint?: CultMeshRouteHint;
      claims?: readonly CultMeshAuthorityClaim[];
    } = {},
  ): CultMeshVerse {
    return cultMeshVerse(verseId, runtimeId, options);
  }

  public static async connectVerse(
    verseId: string,
    runtimeId: string,
    options: {
      routeHint?: CultMeshRouteHint;
      claims?: readonly CultMeshAuthorityClaim[];
    } = {},
  ): Promise<CultMeshVerse> {
    return cultMeshVerse(verseId, runtimeId, options);
  }

  public static operationContextFromVerse(
    context: CultMeshVerseContext,
    options: { idempotencyKey?: string } = {},
  ): CultMeshOperationContext {
    return cultMeshOperationContextFromVerse(context, options);
  }

  public static queryContextFromVerse(
    context: CultMeshVerseContext,
  ): CultMeshQueryContext {
    return cultMeshQueryContextFromVerse(context);
  }

  public static bindOperation<TRequest, TResponse>(
    verse: CultMeshVerseContext | CultMeshVerse,
    operation: CultMeshOperationHandle<TRequest, TResponse>,
  ): CultMeshBoundOperationHandle<TRequest, TResponse> {
    return cultMeshBindOperation(verse, operation);
  }

  public static bindQuery<TParameters, TResult>(
    verse: CultMeshVerseContext | CultMeshVerse,
    query: CultMeshQuerySurface<TParameters, TResult>,
  ): CultMeshBoundQuerySurface<TParameters, TResult> {
    return cultMeshBindQuery(verse, query);
  }

  public static bindLiveFeed<TParameters, TResult>(
    verse: CultMeshVerseContext | CultMeshVerse,
    feed: CultMeshLiveFeed<TParameters, TResult>,
  ): CultMeshBoundLiveFeed<TParameters, TResult> {
    return cultMeshBindLiveFeed(verse, feed);
  }

  public static operationContext(
    runtimeId: string,
    options: {
      claims?: readonly CultMeshAuthorityClaim[];
      routeHint?: CultMeshRouteHint;
      idempotencyKey?: string;
    } = {},
  ): CultMeshOperationContext {
    return cultMeshOperationContext(runtimeId, options);
  }

  public static operationContextFor(runtimeId: string): CultMeshOperationContextBuilder {
    return cultMeshOperationContextFor(runtimeId);
  }

  public static queryContext(
    runtimeId: string,
    options: { routeHint?: CultMeshRouteHint } = {},
  ): CultMeshQueryContext {
    return cultMeshQueryContext(runtimeId, options);
  }

  public static queryContextFor(runtimeId: string): CultMeshQueryContextBuilder {
    return cultMeshQueryContextFor(runtimeId);
  }

  public static operationReceipt(
    operationId: string,
    accepted: boolean,
    options: { route?: CultMeshRouteHint; diagnostic?: string } = {},
  ): CultMeshOperationReceipt {
    return cultMeshOperationReceipt(operationId, accepted, options);
  }

  public static operation<TRequest, TResponse>(
    operationId: string,
    invokeOperation: (
      request: TRequest,
      context: CultMeshOperationContext,
    ) => Promise<TResponse>,
  ): CultMeshOperationHandle<TRequest, TResponse> {
    return cultMeshOperation(operationId, invokeOperation);
  }

  public static query<TParameters, TResult>(
    queryId: string,
    executeQuery: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchQuery?: CultMeshQueryWatcher<TParameters, TResult>;
    } = {},
  ): CultMeshQuerySurface<TParameters, TResult> {
    return cultMeshQuery(queryId, executeQuery, options);
  }

  public static describeQuerySurface(
    query: {
      readonly queryId: string;
      readonly routeHint: CultMeshRouteHint;
      readonly sources: readonly CultMeshProjectionSource[];
    },
  ): CultMeshQuerySurfaceDiagnostic {
    return cultMeshDescribeQuerySurface(query);
  }

  public static describeOperationHandle(
    operation: {
      readonly operationId: string;
    },
  ): CultMeshOperationHandleDiagnostic {
    return cultMeshDescribeOperationHandle(operation);
  }

  public static describeStatePointer(
    pointer: {
      readonly pointerId: string;
      readonly routeHint?: CultMeshRouteHint;
      readonly sources?: readonly CultMeshProjectionSource[];
    },
  ): CultMeshStatePointerDiagnostic {
    return cultMeshDescribeStatePointer(pointer);
  }

  public static liveFeed<TParameters, TResult>(
    feedId: string,
    snapshotFeed: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchFeed?: CultMeshLiveFeedWatcher<TParameters, TResult>;
    } = {},
  ): CultMeshLiveFeed<TParameters, TResult> {
    return cultMeshLiveFeed(feedId, snapshotFeed, options);
  }

  public static document<TDocument>(
    documentId: string,
    schema: CultMeshDocumentSchemaDescriptor,
    snapshotDocument: (context: CultMeshQueryContext) => Promise<TDocument>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchDocument?: CultMeshDocumentWatcher<TDocument>;
      replaceDocument?: CultMeshDocumentReplacer<TDocument>;
    } = {},
  ): CultMeshDocumentHandle<TDocument> {
    return cultMeshDocument(documentId, schema, snapshotDocument, options);
  }

  public static documentFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
    cache: CultCache,
    definition: TDefinition,
    key: string,
    options: {
      documentId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshDocumentFromCache(cache, definition, key, options);
  }

  public static globalDocumentFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
    cache: CultCache,
    definition: TDefinition,
    options: {
      documentId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshDocumentHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshGlobalDocumentFromCache(cache, definition, options);
  }

  public static documents(
    ...documents: readonly CultMeshDocumentHandle<any>[]
  ): CultMeshDocumentCatalog {
    return cultMeshDocuments(...documents);
  }

  public static bindDocument<TDocument>(
    verse: CultMeshVerseContext | CultMeshVerse,
    document: CultMeshDocumentHandle<TDocument>,
  ): CultMeshBoundDocumentHandle<TDocument> {
    return cultMeshBindDocument(verse, document);
  }

  public static collection<TDocument>(
    collectionId: string,
    schema: CultMeshDocumentSchemaDescriptor,
    snapshotCollection: (context: CultMeshQueryContext) => Promise<CultMeshCollectionSnapshot<TDocument>>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
      watchCollection?: CultMeshCollectionWatcher<TDocument>;
    } = {},
  ): CultMeshCollectionHandle<TDocument> {
    return cultMeshCollection(collectionId, schema, snapshotCollection, options);
  }

  public static collectionFromCache<TDefinition extends AnyCultCacheDocumentDefinition>(
    cache: CultCache,
    definition: TDefinition,
    options: {
      collectionId?: string;
      routeHint?: CultMeshRouteHint;
      pollMs?: number;
    } = {},
  ): CultMeshCollectionHandle<CultCacheDocumentValue<TDefinition>> {
    return cultMeshCollectionFromCache(cache, definition, options);
  }

  public static bindCollection<TDocument>(
    verse: CultMeshVerseContext | CultMeshVerse,
    collection: CultMeshCollectionHandle<TDocument>,
  ): CultMeshBoundCollectionHandle<TDocument> {
    return cultMeshBindCollection(verse, collection);
  }

  public static describeLiveFeed(
    feed: {
      readonly feedId: string;
      readonly routeHint: CultMeshRouteHint;
      readonly sources: readonly CultMeshProjectionSource[];
    },
  ): CultMeshLiveFeedDiagnostic {
    return cultMeshDescribeLiveFeed(feed);
  }

  public static pollingQueryWatcher<TParameters, TResult>(
    executeQuery: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: CultMeshPollingWatchOptions<TResult> = {},
  ): CultMeshQueryWatcher<TParameters, TResult> {
    return cultMeshPollingQueryWatcher(executeQuery, options);
  }

  public static statePointer<T>(
    pointerId: string,
    resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
    watchPointer?:
      | ((callback: (value: T) => void) => CultMeshUnsubscribe)
      | CultMeshStatePointerWatcher<T>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ): CultMeshStatePointer<T> {
    return cultMeshStatePointer(pointerId, resolvePointer, watchPointer, options);
  }

  public static mutableStatePointer<T>(
    pointerId: string,
    resolvePointer: (() => Promise<T | undefined>) | CultMeshStatePointerResolver<T>,
    watchPointer:
      | ((callback: (value: T) => void) => CultMeshUnsubscribe)
      | CultMeshStatePointerWatcher<T>,
    replacePointer:
      | ((value: T) => Promise<void>)
      | CultMeshMutableStatePointerReplacer<T>,
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ): CultMeshMutableStatePointer<T> {
    return cultMeshMutableStatePointer(
      pointerId,
      resolvePointer,
      watchPointer,
      replacePointer,
      options,
    );
  }

  public static bindStatePointer<T>(
    verse: CultMeshVerseContext | CultMeshVerse,
    pointer: CultMeshStatePointer<T>,
  ): CultMeshBoundStatePointer<T> {
    return cultMeshBindStatePointer(verse, pointer);
  }

  public static bindMutableStatePointer<T>(
    verse: CultMeshVerseContext | CultMeshVerse,
    pointer: CultMeshMutableStatePointer<T>,
  ): CultMeshBoundMutableStatePointer<T> {
    return cultMeshBindMutableStatePointer(verse, pointer);
  }

  public static projectionSource(
    sourceId: string,
    options: { schemaId?: string; description?: string } = {},
  ): CultMeshProjectionSource {
    return cultMeshProjectionSource(sourceId, options);
  }

  public static stateRefResolver(
    resolverId: string,
    resolveRef:
      | ((stateRef: string) => string | undefined)
      | ((stateRef: string, context: CultMeshQueryContext) => string | undefined),
    options: {
      sources?: readonly CultMeshProjectionSource[];
      routeHint?: CultMeshRouteHint;
    } = {},
  ): CultMeshStateRefResolver {
    return cultMeshStateRefResolver(resolverId, resolveRef, options);
  }

  public static describeStateRefResolver(
    resolver: CultMeshStateRefResolver,
  ): CultMeshStateRefResolverDiagnostic {
    return cultMeshDescribeStateRefResolver(resolver);
  }

  public static stateBinding(
    targetProp: string,
    pointer:
      | {
          readonly pointerId: string;
          readonly routeHint?: CultMeshRouteHint;
          readonly sources?: readonly CultMeshProjectionSource[];
        }
      | string,
    options: {
      sourceId?: string;
      schemaId?: string;
      routeHint?: CultMeshRouteHint;
    } = {},
  ): CultMeshStateBindingDescriptor {
    return cultMeshStateBinding(targetProp, pointer, options);
  }

  public static stateBindingRecord(
    binding?: Partial<CultMeshStateBindingDescriptor>,
  ): CultMeshStateBindingRecord;
  public static stateBindingRecord(
    targetProp?: string,
    pointerId?: string,
    sourceId?: string,
    schemaId?: string,
    routeKind?: string,
    routeDescription?: string,
  ): CultMeshStateBindingRecord;
  public static stateBindingRecord(
    bindingOrTargetProp?: Partial<CultMeshStateBindingDescriptor> | string,
    pointerId?: string,
    sourceId?: string,
    schemaId?: string,
    routeKind?: string,
    routeDescription?: string,
  ): CultMeshStateBindingRecord {
    return typeof bindingOrTargetProp === "object" || bindingOrTargetProp === undefined
      ? cultMeshStateBindingRecord(bindingOrTargetProp)
      : cultMeshStateBindingRecord(
          bindingOrTargetProp,
          pointerId,
          sourceId,
          schemaId,
          routeKind,
          routeDescription,
        );
  }

  public static stateBindingFromRecord(
    record: Partial<CultMeshStateBindingRecord>,
    options: {
      fallbackRouteHint?: CultMeshRouteHint;
      fallbackTargetProp?: string;
    } = {},
  ): CultMeshStateBindingDescriptor {
    return cultMeshStateBindingFromRecord(record, options);
  }

  public static operationBinding(
    operation:
      | {
          readonly operationId: string;
        }
      | string,
    options: {
      label?: string;
      schemaId?: string;
      routeHint?: CultMeshRouteHint;
    } = {},
  ): CultMeshOperationBindingDescriptor {
    return cultMeshOperationBinding(operation, options);
  }

  public static operationBindingRecord(
    binding?: Partial<CultMeshOperationBindingDescriptor>,
  ): CultMeshOperationBindingRecord;
  public static operationBindingRecord(
    operationId?: string,
    label?: string,
    schemaId?: string,
    routeKind?: string,
    routeDescription?: string,
  ): CultMeshOperationBindingRecord;
  public static operationBindingRecord(
    bindingOrOperationId?: Partial<CultMeshOperationBindingDescriptor> | string,
    label?: string,
    schemaId?: string,
    routeKind?: string,
    routeDescription?: string,
  ): CultMeshOperationBindingRecord {
    return typeof bindingOrOperationId === "object" || bindingOrOperationId === undefined
      ? cultMeshOperationBindingRecord(bindingOrOperationId)
      : cultMeshOperationBindingRecord(
          bindingOrOperationId,
          label,
          schemaId,
          routeKind,
          routeDescription,
        );
  }

  public static operationBindingFromRecord(
    record: Partial<CultMeshOperationBindingRecord>,
    options: {
      fallbackRouteHint?: CultMeshRouteHint;
      fallbackOperationId?: string;
    } = {},
  ): CultMeshOperationBindingDescriptor {
    return cultMeshOperationBindingFromRecord(record, options);
  }

  public static operationInvocation(
    operation:
      | {
          readonly operationId: string;
          readonly schemaId?: string;
          readonly routeHint?: CultMeshRouteHint;
        }
      | string,
    options: {
      schemaId?: string;
      routeHint?: CultMeshRouteHint;
      idempotencyKey?: string;
    } = {},
  ): CultMeshOperationInvocationDescriptor {
    return cultMeshOperationInvocation(operation, options);
  }

  public static operationInvocationRecord(
    invocation?: CultMeshOperationInvocationDescriptor,
    options: {
      fallbackOperationId?: string;
      fallbackSchemaId?: string;
      fallbackRouteHint?: CultMeshRouteHint;
      fallbackIdempotencyKey?: string;
    } = {},
  ): CultMeshOperationInvocationRecord {
    return cultMeshOperationInvocationRecord(invocation, options);
  }

  public static operationInvocationFromRecord(
    record: Partial<CultMeshOperationInvocationRecord>,
    options: {
      fallbackOperationId?: string;
      fallbackSchemaId?: string;
      fallbackRouteHint?: CultMeshRouteHint;
      fallbackIdempotencyKey?: string;
    } = {},
  ): CultMeshOperationInvocationDescriptor {
    return cultMeshOperationInvocationFromRecord(record, options);
  }

  public static operationPayload(
    fields: Readonly<Record<string, string | number | boolean | undefined>> = {},
  ): CultMeshOperationPayload {
    return cultMeshOperationPayload(fields);
  }

  public static projectionRecipe<TParameters, TResult>(
    projectionId: string,
    sources: readonly CultMeshProjectionSource[],
    projectProjection: (
      parameters: TParameters,
      context: CultMeshQueryContext,
    ) => Promise<TResult>,
    options: {
      routeHint?: CultMeshRouteHint;
      watchProjection?: (
        parameters: TParameters,
        context: CultMeshQueryContext,
        callback: (value: TResult) => void,
      ) => CultMeshUnsubscribe;
    } = {},
  ): CultMeshProjectionRecipe<TParameters, TResult> {
    return cultMeshProjectionRecipe(
      projectionId,
      sources,
      projectProjection,
      options,
    );
  }

  public static describeProjectionRecipe(
    recipe: {
      readonly projectionId: string;
      readonly routeHint: CultMeshRouteHint;
      readonly sources: readonly CultMeshProjectionSource[];
    },
  ): CultMeshProjectionRecipeDiagnostic {
    return cultMeshDescribeProjectionRecipe(recipe);
  }

  public static describeSurface(
    surface:
      | {
          readonly queryId: string;
          readonly routeHint: CultMeshRouteHint;
          readonly sources: readonly CultMeshProjectionSource[];
        }
      | {
          readonly operationId: string;
        }
      | {
          readonly projectionId: string;
          readonly routeHint: CultMeshRouteHint;
          readonly sources: readonly CultMeshProjectionSource[];
        }
    | {
        readonly feedId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly pointerId: string;
      }
    | {
        readonly documentId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | {
        readonly collectionId: string;
        readonly routeHint: CultMeshRouteHint;
        readonly sources: readonly CultMeshProjectionSource[];
      }
    | CultMeshNativeSliceViewDescriptor,
  ): CultMeshSurfaceDiagnostic {
    return cultMeshDescribeSurface(surface);
  }

  public static surfaceDiagnostic(
    kind: CultMeshSurfaceKind,
    surfaceId: string,
    options: {
      routeHint?: CultMeshRouteHint;
      sources?: readonly CultMeshProjectionSource[];
    } = {},
  ): CultMeshSurfaceDiagnostic {
    return cultMeshSurfaceDiagnostic(kind, surfaceId, options);
  }

  public static describeSurfaceCatalog(
    catalogId: string,
    surfaces: readonly CultMeshSurfaceDiagnostic[],
  ): CultMeshSurfaceCatalogDiagnostic {
    return cultMeshDescribeSurfaceCatalog(catalogId, surfaces);
  }

  public static findSurface(
    catalog: CultMeshSurfaceCatalogDiagnostic,
    surfaceId: string,
  ): CultMeshSurfaceDiagnostic | undefined {
    return cultMeshFindSurface(catalog, surfaceId);
  }

  public static surfacesByKind(
    catalog: CultMeshSurfaceCatalogDiagnostic,
    kind: CultMeshSurfaceKind,
  ): CultMeshSurfaceDiagnostic[] {
    return cultMeshSurfacesByKind(catalog, kind);
  }

  public static surfaceCatalogIndex(
    catalog: CultMeshSurfaceCatalogDiagnostic,
  ): CultMeshSurfaceCatalogIndexDiagnostic {
    return cultMeshSurfaceCatalogIndex(catalog);
  }

  public static nativeSliceColumn(
    name: string,
    valueType: string,
    elementSizeBytes: number,
  ): CultMeshNativeSliceColumn {
    return cultMeshNativeSliceColumn(name, valueType, elementSizeBytes);
  }

  public static nativeSliceView(
    viewId: string,
    schemaId: string,
    rowCount: number,
    columns: readonly CultMeshNativeSliceColumn[],
    options: { route?: CultMeshRouteHint; nativeHandle?: string } = {},
  ): CultMeshNativeSliceViewDescriptor {
    return cultMeshNativeSliceView(viewId, schemaId, rowCount, columns, options);
  }

  public static denseRowStrideBytes(view: CultMeshNativeSliceViewDescriptor): number {
    return cultMeshDenseRowStrideBytes(view);
  }

  public static findNativeSliceColumn(
    view: CultMeshNativeSliceViewDescriptor,
    name: string,
  ): CultMeshNativeSliceColumn | undefined {
    return cultMeshFindNativeSliceColumn(view, name);
  }

  public static describeNativeSliceView(
    view: CultMeshNativeSliceViewDescriptor,
  ): CultMeshNativeSliceViewDiagnostic {
    return cultMeshDescribeNativeSliceView(view);
  }

  public static async createNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    requireNonEmpty(cachePath, "cachePath");
    const store =
      options.store ?? new SingleFileMessagePackBackingStore(cachePath);
    const cache = CultCache.builder().withGenericStore(store).build();
    const documents = new CultNetDocumentRegistry(options.bindings);

    for (const definition of options.documents ?? []) {
      cache.registerDocumentType(definition);
      if (!documents.get(definition.type)) {
        documents.register(defineCultNetDocumentBinding({ definition }));
      }
    }

    if (options.pullOnStart !== false) {
      await cache.pullAllBackingStores();
    }

    return new CultMeshNode(cache, store, documents);
  }

  public static startNode(
    cachePath: string,
    options: CultMeshNodeOptions = {},
  ): Promise<CultMeshNode> {
    return CultMesh.createNode(cachePath, options);
  }

  public static createVerseCatalog<TVerse = unknown>(): CultMeshVerseCatalog<TVerse> {
    return new CultMeshVerseCatalog<TVerse>();
  }

  public static createPeerCatalog(): CultMeshPeerCatalog {
    return new CultMeshPeerCatalog();
  }

  public static createAuthorityLeaseCatalog(): CultMeshAuthorityLeaseCatalog {
    return new CultMeshAuthorityLeaseCatalog();
  }

  public static createStreamCatalog(): CultMeshStreamCatalog {
    return new CultMeshStreamCatalog();
  }

  public static createSchemaCatalog(): CultNetSchemaCatalog {
    return new CultNetSchemaCatalog();
  }

  public static createBuiltInSchemaCatalog(
    options: CultNetSchemaCatalogOptions = {},
  ): CultNetSchemaCatalog {
    const catalog = new CultNetSchemaCatalog();
    catalog.applyResponse(cultNetBuiltinSchemaRegistry.createCatalogResponse({
      schemaVersion: "cultnet.schema_catalog_request.v0",
      messageId: "cultmesh-ts-builtins",
      includeSchemaJson: options.includeSchemaJson,
      schemaIds: options.schemaIds ? [...options.schemaIds] : undefined,
      kinds: options.kinds ? [...options.kinds] : undefined,
    }));
    return catalog;
  }

  public static createShardCatalog(): CultNetShardCatalog {
    return new CultNetShardCatalog();
  }

  public static createRudpDocumentServer(
    runtimeId: string,
    connectionId: number,
    options: CultMeshRudpDocumentServerOptions,
  ): CultMeshRudpDocumentServer {
    requireNonEmpty(runtimeId, "runtimeId");
    if (!options.documents) {
      throw new Error("CultMesh RUDP document server requires a document registry.");
    }

    const host = options.bindHost ?? "127.0.0.1";
    const port = options.bindPort ?? 0;
    const bind = { host, port };
    const socket = options.socket ?? createSocket(host.includes(":") ? "udp6" : "udp4");
    const sessions = new Map<string, {
      remote: { address: string; family: string; port: number };
      session: CultNetRudpSession;
    }>();
    const resendPollMs = Math.max(10, options.resendPollMs ?? 25);
    const sessionTimeoutMs = Math.max(1_000, options.sessionTimeoutMs ?? 30_000);
    const wireContract = options.wireContract ?? "cultnet.schema.v0";
    let resendTimer: NodeJS.Timeout | undefined;

    function reportError(error: unknown): void {
      const normalized = error instanceof Error ? error : new Error(String(error));
      if (options.onError) {
        options.onError(normalized);
        return;
      }
      console.error(`CultMesh RUDP document server error: ${normalized.message}`);
    }

    socket.on("message", (wire, remote) => {
      try {
        const packet = decodeRudpPacket(wire);
        const key = `${remote.address}:${remote.port}`;
        const nowMs = Date.now();
        let record = sessions.get(key);

        if (packet.packetType === "connect") {
          record = {
            remote: { address: remote.address, family: remote.family, port: remote.port },
            session: new CultNetRudpSession({
              connectionId,
              initialSequence: options.initialSequence,
              resendDelayMs: options.resendDelayMs,
              maxPendingReliablePackets: options.maxPendingReliablePackets,
            }),
          };
          sessions.set(key, record);
          socket.send(encodeRudpPacket(record.session.acceptConnect(packet, nowMs)), remote.port, remote.address);
          return;
        }

        if (!record) {
          return;
        }

        const result = record.session.receive(packet, nowMs);
        if (result.reply) {
          socket.send(encodeRudpPacket(result.reply), record.remote.port, record.remote.address);
        }
        for (const frame of result.delivered) {
          if (frame.channelId !== "schema") {
            continue;
          }
          handleDocumentServerFrame(record, frame.payload).catch((error) => {
            reportError(error);
          });
        }
        if (result.disconnected) {
          sessions.delete(key);
          return;
        }
        if (packet.packetType === "accept" || result.delivered.length > 0) {
          socket.send(encodeRudpPacket(record.session.createAck()), record.remote.port, record.remote.address);
        }
      } catch (error) {
        reportError(error);
      }
    });
    socket.on("error", reportError);

    async function handleDocumentServerFrame(
      record: { remote: { address: string; family: string; port: number }; session: CultNetRudpSession },
      payload: Uint8Array,
    ): Promise<void> {
      const message = parseCultNetMessage(decode(payload), wireContract);
      switch (message.schemaVersion) {
        case "cultnet.snapshot_request.v0": {
          if (!options.getCache) {
            sendSchemaMessage(record, {
              schemaVersion: "cultnet.error.v0",
              error: "CultMesh RUDP document server has no cache for snapshot requests.",
            });
            return;
          }
          try {
            const cache = await options.getCache();
            sendSchemaMessage(record, options.documents.createRawSnapshotResponse(cache, message.messageId, message));
          } catch (error) {
            sendSchemaMessage(record, {
              schemaVersion: "cultnet.error.v0",
              error: error instanceof Error ? error.message : String(error),
            });
          }
          return;
        }
        case "cultnet.schema_catalog_request.v0":
          sendSchemaMessage(record, cultNetBuiltinSchemaRegistry.createCatalogResponse(message));
          return;
        case "cultnet.document_put_raw.v0":
          if (options.onDocumentPutRaw) {
            await options.onDocumentPutRaw(normalizeRudpDocumentPut(message, record.remote));
          }
          return;
        default:
          sendSchemaMessage(record, {
            schemaVersion: "cultnet.error.v0",
            error: `Unsupported CultMesh RUDP document request ${message.schemaVersion}.`,
          });
      }
    }

    function sendSchemaMessage(
      record: { remote: { address: string; port: number }; session: CultNetRudpSession },
      message: CultNetMessage,
    ): void {
      const payload = encode(encodeCultNetMessageForWire(message, wireContract));
      for (const packet of record.session.sendMany("schema", payload, {
        reliable: true,
        ordered: true,
        nowMs: Date.now(),
      })) {
        socket.send(encodeRudpPacket(packet), record.remote.port, record.remote.address);
      }
    }

    return {
      bind,
      async start(): Promise<void> {
        await new Promise<void>((resolve, reject) => {
          socket.once("error", reject);
          socket.bind(port, host, () => {
            socket.off("error", reject);
            const address = socket.address();
            if (typeof address === "object") {
              bind.port = address.port;
            }
            resolve();
          });
        });
        resendTimer = setInterval(() => {
          const nowMs = Date.now();
          for (const [key, record] of sessions) {
            if (record.session.checkTimeout(nowMs, sessionTimeoutMs)) {
              sessions.delete(key);
              continue;
            }
            for (const packet of record.session.dueResends(nowMs)) {
              socket.send(encodeRudpPacket(packet), record.remote.port, record.remote.address);
            }
          }
        }, resendPollMs);
        resendTimer.unref?.();
      },
      close(): void {
        if (resendTimer) {
          clearInterval(resendTimer);
          resendTimer = undefined;
        }
        sessions.clear();
        socket.close();
      },
    };
  }

  public static parseRudpEndpoint(endpoint: string): CultMeshRudpEndpoint {
    requireNonEmpty(endpoint, "endpoint");
    const parsed = new URL(endpoint);
    if (parsed.protocol.toLowerCase() !== "rudp:") {
      throw new Error("RUDP endpoint must use the rudp:// scheme.");
    }
    if (!parsed.hostname || !parsed.port) {
      throw new Error("RUDP endpoint must include a host and port.");
    }
    const port = Number.parseInt(parsed.port, 10);
    if (!Number.isInteger(port) || port <= 0 || port > 65535) {
      throw new Error("RUDP endpoint port must be between 1 and 65535.");
    }
    const host = parsed.hostname;
    const uriHost =
      host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
    return { host, port, uri: `rudp://${uriHost}:${port}` };
  }

  public static async publishRudpDocumentOnce<TDefinition extends AnyCultCacheDocumentDefinition>(
    runtimeId: string,
    connectionId: number,
    endpoint: string | CultMeshRudpEndpoint,
    binding: CultNetDocumentBinding<TDefinition>,
    recordKey: string,
    value: CultCacheDocumentValue<TDefinition>,
    options: CultMeshRudpDocumentPublishOptions = {},
  ): Promise<void> {
    requireNonEmpty(runtimeId, "runtimeId");
    requireNonEmpty(recordKey, "recordKey");
    const registry = new CultNetDocumentRegistry([binding]);
    const peer = await CultMesh.createRudpPeer(runtimeId, connectionId, endpoint, options);
    try {
      peer.send(registry.createRawDocumentPutMessage(
        binding,
        options.messageId ?? `${runtimeId}:${binding.definition.type}:${recordKey}`,
        recordKey,
        value,
        {
          sourceRuntimeId: options.sourceRuntimeId ?? runtimeId,
          sourceAgentId: options.sourceAgentId,
          sourceRole: options.sourceRole,
          tags: options.tags,
        },
      ));
      await new Promise((resolve) => setTimeout(resolve, Math.max(0, options.flushTimeoutMs ?? 150)));
    } finally {
      peer.close();
    }
  }

  public static async createRudpServer(
    runtimeId: string,
    connectionId: number,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    requireNonEmpty(runtimeId, "runtimeId");
    const socket = options.socket ?? (await bindRudpSocket(options));
    return new CultNetRudpSocketTransportConnection({
      runtimeId,
      socket,
      mode: "server",
      connectionId,
      initialSequence: options.initialSequence,
      resendDelayMs: options.resendDelayMs,
      resendPollMs: options.resendPollMs,
      transportId: options.transportId,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });
  }

  public static async createRudpClient(
    runtimeId: string,
    connectionId: number,
    endpoint: string | CultMeshRudpEndpoint,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    requireNonEmpty(runtimeId, "runtimeId");
    const parsedEndpoint =
      typeof endpoint === "string" ? CultMesh.parseRudpEndpoint(endpoint) : endpoint;
    const socket = options.socket ?? (await bindRudpSocket(options));
    return new CultNetRudpSocketTransportConnection({
      runtimeId,
      socket,
      mode: "client",
      remoteHost: parsedEndpoint.host,
      remotePort: parsedEndpoint.port,
      connectionId,
      initialSequence: options.initialSequence,
      resendDelayMs: options.resendDelayMs,
      resendPollMs: options.resendPollMs,
      transportId: options.transportId,
      maxPayloadBytes: options.maxPayloadBytes,
      maxFragmentBytes: options.maxFragmentBytes,
      maxPendingReliablePackets: options.maxPendingReliablePackets,
      reconnectPolicy: options.reconnectPolicy,
    });
  }

  public static async createRudpClientForPeer(
    runtimeId: string,
    connectionId: number,
    peer: CultMeshPeerCard,
    options: CultMeshRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    const endpoint = peer.endpoints.find((value) =>
      value.toLowerCase().startsWith("rudp://"),
    );
    if (!endpoint) {
      throw new Error(`Peer ${peer.peerId} does not advertise a RUDP endpoint.`);
    }
    return CultMesh.createRudpClient(runtimeId, connectionId, endpoint, options);
  }

  public static async createRudpClientForAuthorizedPeer(
    runtimeId: string,
    connectionId: number,
    peers: CultMeshPeerCatalog,
    leases: CultMeshAuthorityLeaseCatalog,
    verseId: string,
    role: string,
    options: CultMeshAuthorizedRudpSocketOptions = {},
  ): Promise<CultNetRudpSocketTransportConnection> {
    const peer = peers.firstAuthorized(
      verseId,
      role,
      leases,
      options.shardId,
      options.at,
    );
    if (!peer) {
      throw new Error(`No authorized RUDP peer for role ${role} in Verse ${verseId}.`);
    }
    return CultMesh.createRudpClientForPeer(runtimeId, connectionId, peer, options);
  }

  public static async createRudpPeer(
    runtimeId: string,
    connectionId: number,
    endpoint: string | CultMeshRudpEndpoint,
    options: CultMeshRudpPeerOptions = {},
  ): Promise<CultNetPeer> {
    const client = await CultMesh.createRudpClient(
      runtimeId,
      connectionId,
      endpoint,
      options,
    );
    client.connect(
      options.connectPayload === undefined
        ? undefined
        : Uint8Array.from(options.connectPayload),
    );
    await waitForRudpConnected(
      client,
      options.connectTimeoutMs ?? 1_000,
      `RUDP peer ${runtimeId}`,
    );
    return new CultNetPeer(client, {
      wireContract: options.wireContract ?? "cultnet.schema.v0",
    });
  }

  public static async createRudpPeerForPeer(
    runtimeId: string,
    connectionId: number,
    peer: CultMeshPeerCard,
    options: CultMeshRudpPeerOptions = {},
  ): Promise<CultNetPeer> {
    const endpoint = peer.endpoints.find((value) =>
      value.toLowerCase().startsWith("rudp://"),
    );
    if (!endpoint) {
      throw new Error(`Peer ${peer.peerId} does not advertise a RUDP endpoint.`);
    }
    return CultMesh.createRudpPeer(runtimeId, connectionId, endpoint, options);
  }

  public static async createRudpPeerForAuthorizedPeer(
    runtimeId: string,
    connectionId: number,
    peers: CultMeshPeerCatalog,
    leases: CultMeshAuthorityLeaseCatalog,
    verseId: string,
    role: string,
    options: CultMeshAuthorizedRudpPeerOptions = {},
  ): Promise<CultNetPeer> {
    const peer = peers.firstAuthorized(
      verseId,
      role,
      leases,
      options.shardId,
      options.at,
    );
    if (!peer) {
      throw new Error(`No authorized RUDP peer for role ${role} in Verse ${verseId}.`);
    }
    return CultMesh.createRudpPeerForPeer(runtimeId, connectionId, peer, options);
  }
}

function cultMeshSchemaFromDefinition(
  definition: AnyCultCacheDocumentDefinition,
): CultMeshDocumentSchemaDescriptor {
  return {
    type: definition.type,
    schemaId: definition.schemaId ?? definition.type,
    schemaName: definition.schemaName,
    schemaVersion: definition.schemaVersion,
  };
}

function normalizeCultMeshDocumentSchema(
  schema: CultMeshDocumentSchemaDescriptor,
): CultMeshDocumentSchemaDescriptor {
  const normalized = {
    type: schema.type?.trim() || undefined,
    schemaId: schema.schemaId?.trim() || undefined,
    schemaName: schema.schemaName?.trim() || undefined,
    schemaVersion: schema.schemaVersion?.trim() || undefined,
  };

  if (!normalized.type && !normalized.schemaId && !normalized.schemaName) {
    throw new Error("Document schema must include type, schemaId, or schemaName.");
  }

  return normalized;
}

function cultMeshSchemaNameVersionKey(
  schema: CultMeshDocumentSchemaDescriptor,
): string | undefined {
  return schema.schemaName
    ? `${schema.schemaName}@${schema.schemaVersion ?? ""}`
    : undefined;
}

function cultMeshSchemasAreCompatible(
  left: CultMeshDocumentSchemaDescriptor,
  right: CultMeshDocumentSchemaDescriptor,
): boolean {
  if (left.schemaId && right.schemaId && left.schemaId === right.schemaId) {
    return true;
  }

  if (
    left.schemaName &&
    right.schemaName &&
    left.schemaName === right.schemaName &&
    (left.schemaVersion ?? "") === (right.schemaVersion ?? "")
  ) {
    return true;
  }

  return Boolean(left.type && right.type && left.type === right.type);
}

function cultMeshSchemaLabel(schema: CultMeshDocumentSchemaDescriptor): string {
  return schema.schemaId ??
    cultMeshSchemaNameVersionKey(schema) ??
    schema.type ??
    "<anonymous-schema>";
}

function requireNonEmpty(value: string, name: string): void {
  if (!value || value.trim().length === 0) {
    throw new Error(`${name} must be non-empty.`);
  }
}

function normalizeRudpDocumentPut(
  message: CultNetMessage,
  remote: { address: string; family: string; port: number },
): CultMeshRudpDocumentPut {
  if (message.schemaVersion !== "cultnet.document_put_raw.v0") {
    throw new Error(`Expected cultnet.document_put_raw.v0, received ${message.schemaVersion}.`);
  }
  const document: CultNetRawDocumentRecord = message.document;
  if (!document.schemaId || !document.recordKey) {
    throw new Error("Raw document put is missing schemaId or recordKey.");
  }
  if (document.payloadEncoding !== "messagepack") {
    throw new Error(`Unsupported raw payload encoding ${document.payloadEncoding}.`);
  }
  return {
    schemaId: document.schemaId,
    recordKey: document.recordKey,
    storedAt: document.storedAt ?? new Date().toISOString(),
    payload: decode(document.payload),
    sourceRuntimeId: document.sourceRuntimeId ?? null,
    sourceAgentId: document.sourceAgentId ?? null,
    sourceRole: document.sourceRole ?? null,
    tags: Array.isArray(document.tags) ? document.tags : [],
    remote,
  };
}

async function bindRudpSocket(options: CultMeshRudpSocketOptions): Promise<Socket> {
  const socket = createSocket("udp4");
  const host = options.bindHost ?? "127.0.0.1";
  const port = options.bindPort ?? 0;
  await new Promise<void>((resolve, reject) => {
    socket.once("error", reject);
    socket.bind(port, host, () => {
      socket.off("error", reject);
      resolve();
    });
  });
  return socket;
}

async function waitForRudpConnected(
  transport: CultNetRudpSocketTransportConnection,
  timeoutMs: number,
  description: string,
): Promise<void> {
  if (transport.connected) {
    return;
  }
  await new Promise<void>((resolve, reject) => {
    let settled = false;
    const cleanup = (): void => {
      clearInterval(poll);
      clearTimeout(timer);
      transport.off("error", onError);
      transport.off("close", onClose);
    };
    const finish = (error?: Error): void => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      if (error) {
        reject(error);
      } else {
        resolve();
      }
    };
    const check = (): void => {
      if (transport.connected) {
        finish();
      }
    };
    const onError = (error: Error): void => finish(error);
    const onClose = (): void =>
      finish(new Error(`${description} closed before handshake completed.`));
    const poll = setInterval(check, 5);
    const timer = setTimeout(
      () => finish(new Error(`Timed out waiting for ${description} handshake.`)),
      timeoutMs,
    );
    poll.unref?.();
    timer.unref?.();
    transport.once("error", onError);
    transport.once("close", onClose);
    check();
  });
}

function copyBudgetFor(
  transport: CultMeshStreamBodyTransport,
): CultMeshStreamNegotiation["copyBudget"] {
  switch (transport) {
    case "shared-memory":
    case "shared-d3d12-texture":
    case "shared-d3d11-texture":
    case "dma-buf":
    case "iosurface":
    case "ahardwarebuffer":
      return "zero-copy-target";
    case "cultcache-page":
      return "one-copy-fallback";
    case "inline-bytes":
      return "opaque-runtime";
  }
}

function nonBlankOr(value?: string, fallback = ""): string {
  return value && value.trim() ? value : fallback;
}

function parseCultMeshLocalityKind(
  value: string | undefined,
  fallback: CultMeshLocalityKind,
): CultMeshLocalityKind {
  switch ((value ?? "").trim().toLowerCase()) {
    case "automatic":
      return "automatic";
    case "inprocess":
    case "in-process":
      return "in-process";
    case "sharedmemory":
    case "shared-memory":
      return "shared-memory";
    case "ipc":
      return "ipc";
    case "network":
      return "network";
    case "wasm":
      return "wasm";
    default:
      return fallback;
  }
}
