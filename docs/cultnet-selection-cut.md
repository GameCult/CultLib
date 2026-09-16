# CultNet typed selection: cut map

Date: 2026-09-17. Imagination output. Intended path: `F:\Projects\CultLib\docs\cultnet-selection-cut.md`.
No code in this document has been written; nothing here is committed by
Imagination. This is its own campaign with its own map; it does not touch
`docs/typescript-quic-realtime-cut.md` or any code in that tree.

Status: cut map for one cut, **Cut 1: the selection vocabulary in the C#
reference runtime and the Rust runtime**, with the follow-on runtime cuts
and the Huginn consumer cut named and not mapped.

**Rulings this map is written under (operator, 2026-09-17):**

- The vocabulary lands in CultLib at the CultNet layer, not in Huginn. It
  fills a hole: the query side of the substrate was never dropped by
  decision, only never asked for. `contracts/cultnet/cultnet-distributed-database.md:10-11`
  names RethinkDB changefeeds with "point, table/schema, and **filtered
  watches**" as prior art to keep; schema and key allowlists were built,
  filtered selection never was.
- The first cut covers the C# reference (`src\GameCult.Networking`) and
  Rust (`packages\cultnet-rs`, what Huginn consumes). TypeScript, Python and
  Kotlin follow when they have a caller.
- Deletes first: the two untyped stand-ins are replaced, not sat beside.
- The CultMesh subtraction audit is a required part of the map, with real
  line counts, and an honest miss beats a padded ledger.
- Nothing on the wire is serde-untagged (rule of this cut; evidence below).

**What survives unchanged from the Huginn-targeted specification**
(`scratchpad/cut10b-read-side-spec.md`, D1-D4): the five capabilities, the
chosen representation and the rejected ones with their reasons, the order and
snapshot cursor, the role-typed hop, the one negation, and the bound. What
changes: owner, repo, the value model of a field predicate (strings, because
the cache's indexes are strings), the parity obligation, and the subtraction.

## Pins

| Repo | Branch | HEAD | State |
|---|---|---|---|
| CultLib | `main` | `51fb449` | dirty in `native/GameCult.Mesh.Quic.Native/*` and `scripts/mutate-cultmesh.mjs` from the live QUIC campaign; **not touched**. Every `file:line` below is against `51fb449`. |
| Huginn | `eureka/memory-organ` | `25a841d` | the consumer; its cut is named in section 12 and mapped in the Epiphany campaign map. |
| Epiphany leaf | `5cda0886` | — | untouched. |

Baselines: `src/GameCult.Networking` 14,848 lines; `src/GameCult.Mesh`
21,720 lines; `tests/GameCult.Networking.Tests/NetworkingTests.cs` 141
`[Test]`s; `packages/cultnet-rs` 111 `#[test]`s; `cultnet-rs` depends on
`serde`, `rmp-serde 1.3`, `rmpv`, `serde_json`, `cultcache-rs` and no
`schemars` (`packages/cultnet-rs/Cargo.toml:14-31`).

## 1. Body facts, each by source read

**The reference's read side today.**

- `CultNetDatabase` (`src/GameCult.Networking/CultNetDatabase.cs`): change
  kinds `:15-45`; `Watch<T>` `:1052`, `WatchAllChanges` `:1063`,
  `WatchRecord<T>` `:1072`; `GetAll<T>` `:776`; per-shard mutation log with
  sequence (`:465-466`, `:528-590`, `:631` a snapshot carries
  `ShardLogSequence`). `CultNetSchemaAliasMatching` `:47-97` is the one
  schema-alias matcher, `internal static`.
- **Three separate selector engines evaluate the same two allowlists:**
  `CultNetDocumentRegistry.CreateRawSnapshotResponse` `:263-349` (snapshot,
  over `cache.AllEntries` by reflection at `:325-342`) with its own
  `MatchesRequestedSchema` `:505-524`; `CultNetDatabaseServer.CreateChangeMessage`
  `:347-384` with `Matches`/`MatchesSchema`/`InferSchemaName` `:401-468`
  (the LiteNetLib path, the one production construction site,
  `CultNetLocal.cs:154`); `CultNetDatabaseSubscriptionServer.CreateChangeCore`
  `:442-485` with `MatchesRequestedSchema` `:493-500` (the RUDP path). Two
  of them carry an identical reflection copy of `CreateRawRecord`
  (`CultNetDatabaseServer.cs:386-399`, `CultNetDatabaseSubscriptionServer.cs:502-515`).
- **The two stand-ins.** `CultNetDatabaseSubscribeMessage`
  (`CultNetSchemaMessages.cs:788-826`): `schemaIds` `:805`, `recordKeys`
  `:809`, `includeSnapshot` `:813`, then `consumerRuntimeId`, `bodyIds`,
  `supportedBodyTransports` (body-plane demand, not selection). The same
  pair on `CultNetSnapshotRequestMessage` `:737-741`,
  `CultNetShardCatalogRequestMessage` `:949-953` (shard routing, stays), and
  `CultNetSchemaCatalogRequestMessage` `:1591` (catalog, stays).
  `_projectRecord` on the subscription server (`:27`, `:44`, `:50`,
  `:239-240`, `:314`): **no production caller constructs the subscription
  server at all** — `grep "new CultNetDatabaseSubscriptionServer"` over
  `src/` is empty; it is built only in tests (`NetworkingTests.cs:1596-1605`
  passes `projectRecord`). Projection is authority that exists in the
  reference and reaches no wire and no caller.
- **The cache already owns addressing.** `CultIndexAttribute`
  (`src/GameCult.Caching/CultDocumentContracts.cs:34`, `Alias` `:41`) names
  a member as an index; the cache's index values are **strings by
  construction** (`CultCache.cs:1084` `Getter = document => getValue(document)?.ToString()`;
  `GetByIndex<T>(string alias, string value)` `:1503-1509`).
  `CultReferenceAttribute` (`:45`, `TargetType` `:53`, `Many` `:55`) names a
  member as a reference; the descriptor records `IsReference`, `IsMany`,
  `TargetSchemaName` (`CultCache.cs:1069-1080`). So a field predicate's key
  is an index alias and a hop's role is a reference member, both declared
  by the document's owner and both readable from `CultDocumentDescriptor`.
  No cache change is needed for addressing.
- **Schemas are hand-written JSON** in `contracts/cultnet/*.schema.json`,
  listed by file name in `CultNetSchemaRegistry.cs:360-385`; TypeScript
  compiles them with Ajv and refuses unknown fields
  (`cultnet.database-subscribe.schema.json` has `additionalProperties: false`);
  Python lists required fields per version by hand
  (`packages/cultnet-py/src/cultnet_py/schema_catalog.py:283`, `:407`); Rust
  types are a serde enum tagged on `schemaVersion`
  (`packages/cultnet-rs/src/contracts.rs:252`) with hand validation
  (`require_optional_string_vec`, `:548-594`). C# encodes with
  MessagePack-CSharp string keys (`[Key("name")]`), i.e. named maps; Rust
  encodes with `rmp_serde::to_vec_named`.
- **Runtime coverage of the subscribe message today.** C# server ×2, C#
  client (`CultNetDatabaseSubscriptionClient.cs:138-151`); TypeScript
  contract only (`packages/cultnet-ts/src/contracts.ts:237-247`, no server
  fanout, interop tests send it at `test/interop/cultnet-interop.test.ts:719,798`);
  Python server ×2 (`cultnet_py/interop_peer.py:390-401` with
  `DatabaseSubscription.matches` `:110-125`, and
  `cultmesh_py/server.py:315-331` with `_subscription_matches_document`
  `:449-470`, `_schema_matches_request` `:699-716`, and a third copy in
  `cultmesh_py/node.py:1322-1345`); **Rust: none** — `cultnet-rs` has
  `SnapshotRequest` with the two allowlists (`contracts.rs:314-321`) and no
  `DatabaseSubscribe` variant; Kotlin: none (a snapshot-request builder,
  `CultMesh.kt:2111`, and a `require(schemaVersion == "…snapshot_request.v0")`
  at `:2195`).
- **How each runtime treats a message it does not know**, which is the
  wire-parity fact section 9 rests on: C# throws
  (`CultNetSchemaMessageSerialization.cs:85` `_ => throw`); TypeScript
  `parseCultNetMessage` (`contracts.ts:728-744`) looks the version up in its
  Ajv validators and throws when absent; Kotlin `require`s the exact version
  (`CultMesh.kt:2195-2196`); Rust's tagged enum fails to deserialize on an
  unknown tag; **Python returns `[]`** (`interop_peer.py:360-387` falls off
  the chain; `cultmesh_py/server.py:341` delegates to `handle_message`) — a
  silent drop, no reply, and for a *known* version with unknown fields
  Python reads only the fields it expects (`message.get(...)`), so it would
  answer a selector it did not understand with the wrong rows.
- **Every committed write has a shard-log sequence** — assumed from
  `CultNetDatabase.cs:465` (`_nextLogSequences`) and the invariant "raw
  snapshot/put/delete messages must pass through `CultNetDatabase`"
  (`cultnet-distributed-database.md:115-116`); **Hands confirms** that a
  local `PutAsync` outside any shard also takes a sequence, because the
  order in section 3 rests on it. If it does not, the row's ordinal is its
  `storedAt` and the cursor refuses on any advance (section 3 says what
  that costs).

**The CultMesh audit** (Explore pass over `src/GameCult.Mesh` and
`CultNetDocumentRegistry.cs`, 92 sites classified) is in section 11 with its
numbers.

## 2. The vocabulary, exactly

One shape, `cultnet.selection.v1`, carried by two request messages at a new
version and answered by one page message. Field names are the wire
spelling; C# and Rust type names follow each runtime's convention.

```
Selection {
  schemas:     string[]?              // kinds: schema ids or aliases, matched by CultNetSchemaAliasMatching; absent = every schema
  keys:        string[]?              // record-key allowlist; absent = every key
  fields:      FieldPredicate[]?      // conjunction; each is any-of over one declared index alias
  cites:       Citation?              // rows whose declared reference `role` (or any reference) names `target`
  cited:       Incoming?              // rows that some row does / does not name in `role`
  projection:  "header" | "document"  // default "header"
  descending:  bool                   // default false; the one order, reversed
  limit:       uint32?                // clamped 1..=200
  cursor:      string?                // opaque; minted by the answering server
}
FieldPredicate { index: string, anyOf: string[] }          // anyOf non-empty; index must be declared on some schema the selection can reach, else refused
Citation       { target: RecordRef, role: string? }        // target validated as a reference the row owner recognises
Incoming       { role: string, exists: bool }
RecordRef      { schemaId: string, recordKey: string }

SelectionPage {                                            // cultnet.snapshot_response_raw.v1 carries this
  matched:  uint32
  asOf:     uint64                                          // the snapshot the page is exact for
  next:     string?                                         // absent on the last page
  headers:  RawDocumentHeader[]?  |  documents: RawDocumentRecord[]?   // exactly one present, by projection
  shardId?, shardEpoch?, shardLogSequence?                   // as v0 carries them
}
RawDocumentHeader = RawDocumentRecord minus payload, payloadEncoding
```

Messages: `cultnet.snapshot_request.v1 { messageId, selection, shardId?, shardEpoch? }`;
`cultnet.database_subscribe.v1 { messageId, subscriptionId, selection, includeSnapshot, consumerRuntimeId?, bodyIds?, supportedBodyTransports? }`;
`cultnet.snapshot_response_raw.v1` as above. `database_change_raw.v0` and
`database_unsubscribe.v0` are unchanged: a change is a change.

**Rules, each one owner:**

- **Order** is `(ordinal, schemaId, recordKey)` ascending, or the reverse
  under `descending`. `ordinal` is the sequence of the commit that last
  wrote the row (the shard-log sequence in the reference; the receipt
  sequence in Huginn). The row owner supplies it; the vocabulary never reads
  a clock.
- **Snapshot.** A page is exact as of `asOf`. A cursor carries `asOf`, the
  last `(ordinal, schemaId, recordKey)`, and a digest of the selection with
  `cursor` and `limit` cleared. A server that can present rows as of the
  cursor's `asOf` answers the next page over that snapshot; **a server that
  cannot refuses** `cultnet.error.v0 { code: "cursor_stale", details: { asOf, current } }`.
  The reference refuses when the shard log has advanced (rows mutate, and a
  replay is not this cut's); Huginn never refuses (append-only, one
  writer). One contract, two honest servers. A cursor that does not decode,
  or whose digest is not this selection's, is `cursor_invalid`.
- **Fields** are any-of over string values of one declared index, conjoined
  across predicates. A row of a schema that does not declare the index
  never matches it. An `index` no reachable schema declares, an empty
  `anyOf`, an empty `keys` or `schemas` list, and a `role` no schema
  declares are refused typed at the door (`selection_invalid { field, value }`),
  never answered as an empty page. Values are strings because the cache's
  indexes are strings (`CultCache.cs:1084`); an enum-typed field on the
  consumer's side is a string on the wire and the row owner refuses a value
  its enum does not carry — the closed key set survives as "declared
  aliases only", and the fixed operator survives as any-of.
- **The hop** reads edges the row owner declares: in the reference, members
  with `CultReferenceAttribute` (role = the member's index alias or name;
  target = the referenced key, or each key when `Many`); in Rust, the
  `Row::references` the consumer implements. The citer's status, fields and
  authorization are not consulted by the hop; one hop, no second.
- **Projection.** `header` is the record without its payload; `document` is
  the record. The consumer decides what a header *means* for typed rows
  (Huginn's typed summary is its header; the substrate carries whichever
  type the page was instantiated with).
- **Not on the wire:** the admission window (`admittedAfter/Before`) of
  Huginn's Cut 9 — `descending` plus the cursor answer "the latest"; a date
  window is a range, and ranges are out (Q-G).

**The bound, carried forward in force.** Closed key set (declared aliases),
fixed operator (any-of), no path language, no expression terms, no OR, one
negation, one hop, no depth field, no sort field, no range, no text.
Section 13 lists what is deliberately impossible.

**Nothing on the wire is serde-untagged — rule, with evidence.** Probe
`scratchpad/untagged-probe/` (result in `untagged-probe-result.txt`), run
2026-09-17 against `rmp-serde 1.3.1` / `rmp 0.8.15` / `serde 1.0.229` (the
versions Huginn's lockfile resolves; `cultnet-rs` pins `rmp-serde 1.3`):
every `#[serde(untagged)]` enum with struct variants **fails to round-trip
under positional MessagePack** (`data did not match any variant of untagged
enum`) and passes only under named maps; externally tagged shapes pass under
both. The reference encodes named maps and so does Rust today, so an
untagged shape would work by accident of an undocumented encoding choice.
`grep untagged packages/cultnet-rs/src packages/cultcache-rs/src` is empty
at `51fb449`; this cut keeps it empty and pins it (section 10).

## 3. Decisions, with the reasons

**D1. Strings for field values, not typed enums.** In the Huginn-targeted
design a predicate was `Option<Vec<FindingSeverity>>`, closed at compile
time. The substrate cannot know a consumer's enums, and the cache's own index
model is string-valued; so the wire carries strings and the row owner
refuses a value outside its enum at the door. The loss is compile-time
closure; what is kept is runtime closure with a typed refusal, which is the
same guarantee to a caller.

**D2. Declared aliases, not a field-path language.** `index` and `role` are
names the document's owner declared with `CultIndexAttribute` and
`CultReferenceAttribute`; an undeclared name is refused. A dot-path over the
payload would be the string language the bound excludes, and the cache
already has the declaration mechanism.

**D3. A new message version, not new fields on v0.** Section 1's fact:
Python reads only the fields it knows and would answer a v0 selector it
did not understand with the wrong rows. A v1 version string makes every
runtime not in this cut refuse or drop by its own dispatch rather than
mis-answer. The cost is that v0 stays answerable by the reference until the
follow-ups land (Q-F).

**D4. One evaluator in the reference, three engines deleted.** `Select`
over `(descriptor, key, document)` rows from the cache replaces
`CreateRawSnapshotResponse`'s loop, `CultNetDatabaseServer.CreateChangeMessage`'s
`Matches`, and `CultNetDatabaseSubscriptionServer.CreateChangeCore`.
`CultNetSchemaAliasMatching` (`CultNetDatabase.cs:47-97`) becomes the one
alias matcher and goes `public`; the four Mesh copies and two Networking
copies go.

**D5. Projection becomes a wire value; `_projectRecord` dies.** The
delegate ran after authorization and reached no caller. `projection` on the
selection is the same authority as a value; `_authorizeRequest` and
`_authorizeRecord` stay, because authorization is not selection.

**D6. Subscriptions evaluate the selection on the snapshot and on every
change, and hop-bearing selections reconcile.** `cites`/`cited` are
set-dependent: a change to row B can change whether row A matches. The
subscription server already owns a diff-against-delivered reconcile
(`Reconcile` `:251-292`, `DeliveredBySourceRecordKey`); a hop-bearing
subscription runs it on every change instead of the per-change fast path.
That is the mechanism by which "watch a question" falls out, and this cut
does **not** add a local `CultNetDatabase.Watch(Selection)`; section 11
records what that later cut would collapse (Q-H).

**D7. The Rust runtime carries the evaluator, not only the types.** Huginn
evaluates selections over its own rows; if `select` lived only in C#, Rust
consumers would each write one. `packages/cultnet-rs/src/selection.rs`
holds the types, the cursor, and `select` over a `Row` trait, with no
subscription client in this cut (Huginn does not subscribe).

**Agreement with our own abandoned prior art** (`ThreadEpiphanyGraphQuery`,
Epiphany `5c62a650`, deleted with its host `5f6f2441`): direction and edge
kind as data, schema derived — agreed; differs as before (predicate not
subgraph read; roles are declared names not free strings; one negation
added; no depth field).

**Rejected, unchanged in reason:** Qdrant's `Filter` (open dot-path keys,
polymorphic values, OR/threshold/recursion advertised then refused, the
untagged hazard; Cut 11 lowers `FieldPredicate` to `must`/`match.any` in ~30
lines on the daemon side); ReQL term trees; Firestore `CompositeFilter`;
Mongo/ES magic-key documents; every string-parsed evaluator surveyed.

## 4. Deletes first

| Path:lines (at `51fb449`) | Lines | What |
|---|---:|---|
| `src/GameCult.Networking/CultNetDatabaseServer.cs:347-468` | 122 | `CreateChangeMessage`, `CreateRawDocumentRecord`, `Matches`, `MatchesSchema`, `InferSchemaName` — the LiteNetLib selector engine; the handler calls the shared evaluator and the shared raw-record projection |
| `src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs:424-515` | 92 | `CreateAuthorizedChange`, `CreateChange`, `CreateChangeCore`, `ResolveWireSchemaId`, `MatchesRequestedSchema`, `CreateRawRecord` — the RUDP selector engine |
| `CultNetDatabaseSubscriptionServer.cs:27, :44, :50, :239-240, :314` | 6 | `_projectRecord`, its parameter and its two call sites |
| `CultNetDatabaseSubscriptionServer.cs:294-322` | 29 | `CreateProjectedSnapshot` body: becomes evaluate + authorize + project (~12 lines stay) |
| `src/GameCult.Networking/CultNetDocumentRegistry.cs:263-349` | 87 | `CreateRawSnapshotResponse`: the loop and its reflection over `cache.AllEntries` (`:325-342`); the raw-record construction (`:297-311`) survives as `ToRawRecord` |
| `CultNetDocumentRegistry.cs:505-524` | 20 | `MatchesRequestedSchema`, a copy of `CultNetSchemaAliasMatching.Matches(candidate, descriptor)` |
| `CultNetDocumentRegistry.cs:247-258` | 12 | `CreateSnapshotRequest` (v0 builder); v1 takes a `Selection` |
| **Networking total** | **368** | |
| `src/GameCult.Mesh/CultMeshSnapshots.cs:638-661, 750-773, 889-911` | 71 | three verbatim clones of the selector-carrying options; one `with` copy over a record type remains |
| `src/GameCult.Mesh/CultMesh.cs:2721-2748, 1941-1969` | 57 | the fourth and fifth copies (`ToSnapshotRequestOptions`, `WithPublicationBindingOptions`) |
| `CultMeshSnapshots.cs:973-983` | 11 | `CleanSnapshotFilter`: normalisation is the selection's own validation |
| `CultMeshSnapshots.cs:1000-1009, 1033-1047, 1078-1088` | 36 | the client-side re-filter of a snapshot by schema and `InferSchemaName`; the server's v1 answer is exact, decode stays |
| `CultMesh.cs:2750-2800` | 51 | `ReadDocumentFromSnapshotResponse`'s four-tier fallback (which returns a record with the wrong key when only the schema matches) and `TryDecodeSnapshotDocument`; replaced by one exact read (~10 stay) |
| `CultMesh.cs:3474-3497` | 24 | `IsSameCultDocumentSchema`: `CultNetSchemaAliasMatching`, now public |
| `CultMeshSnapshots.cs:625-636` and the rule at `:198`, `CultMesh.cs:2716` | ~15 | the "no schema filter when keys are given" rule, three times; one place |
| `CultMeshClient.cs:574-604, 830-860, 932-962` selector construction | ~25 net | `{recordKeys, schemaIds}` literals become `Selection` literals; the retry loops stay |
| `CultMeshBodyDemand.cs:177-221` | ~15 net | `CultMeshHotBodySubscription` builds two allowlists; builds one `Selection` |
| `CultMesh.cs:2701-2719, 2838-2858, 2875-2915` | ~20 net | selector construction for peer snapshots and body subscriptions |
| `CultMeshSnapshots.cs:139-201, 434-516, 611-623` overlay logic | ~40 net | overlaying caller `schemaIds`/`recordKeys` onto defaults, four times |
| **Mesh total** | **≈ 365 gross, ≈ 331 net** | see section 11 for what does not go |
| `tests/GameCult.Networking.Tests/NetworkingTests.cs`: `CultNetDatabaseServer_Creates_Filtered_SnapshotResponse` `:3124`, `…_ForCompatibleSchemaAlias` `:3158`, `CultNetDatabaseServer_Creates_Filtered_SubscriptionChange` `:5547`, `…_ForSchemaAlias` `:5601`, `DatabaseSubscription_FiltersLiveChangesByWireSchemaBinding` `:2256`, `…ByRequestedSchemaAlias` `:2310`, and the `projectRecord` lambda in `…AuthorizesRequestAndFiltersSnapshotAndLiveRecords` `:1601-1605` | ~250 | rewritten over v1 selections against the one evaluator (section 10); the rules they pin survive |

**Not deleted in this cut, by ruling (Q-F):** the v0 message classes and
their three schema files, the v0 arms in `CultNetSchemaMessageSerialization`,
and the v0 handling in the two servers — kept as a **lowering**: a v0
request lowers to `Selection { schemas: schemaIds, keys: recordKeys, projection: document }`
and is answered through the same evaluator, so v0 has no engine of its own.
They die with the last runtime follow-up.

## 5. Keeps and moves

- `CultNetSchemaAliasMatching` (`CultNetDatabase.cs:47-97`), `internal` →
  `public`, the one alias matcher for every runtime-side match.
- `_authorizeRequest`, `_authorizeRecord`, `Reconcile`, `Watch`,
  `ApplyProjectedChange`, `PublishDemand`, `DemandChanged` and the body-plane
  fields of the subscribe message: authorization, reconciliation and demand
  are not selection.
- `CultNetDocumentRegistry`'s raw-record construction (`:297-311`) as
  `ToRawRecord(descriptor, binding, key, document, options)`; the payload
  schema resolution (`:452-503`) untouched.
- The v0 messages, schemas and arms as a lowering (above).
- `CultMeshSnapshotRequestOptions` as a record `{ Selection, ShardId, ShardEpoch, …transport knobs }`.
- Every (B)-class site of the audit (section 11): transport racing,
  authority, freshness, body routing, provider-session validation.
- The interop lanes and CI witness (`.github/workflows/cultnet-interop.yml`)
  green throughout, because v0 still answers.

## 6. Adds

| Add | Owner | Live consumer | Protected invariant | What it replaces |
|---|---|---|---|---|
| `contracts/cultnet/cultnet.selection.schema.json` (`$id`, referenced by `$ref`), `cultnet.snapshot-request.v1.schema.json`, `cultnet.database-subscribe.v1.schema.json`, `cultnet.snapshot-response-raw.v1.schema.json`; registry entries in `CultNetSchemaRegistry.cs` | CultLib contracts | C#, Rust; TS/Py/Kotlin follow-ups | one published shape of a selection, hand-written like its siblings, pinned by vectors in both runtimes | `schemaIds`/`recordKeys`/`includeSnapshot` as the wire's only selection |
| `src/GameCult.Networking/CultNetSelection.cs`: `CultNetSelection`, `CultNetFieldPredicate`, `CultNetCitation`, `CultNetIncoming`, `CultNetSelectionProjection`, `CultNetRecordRef`, `CultNetRawDocumentHeader`, `CultNetSelectionPage`; `Validate(IReadOnlyList<CultDocumentDescriptor>)` (declared aliases and roles, non-empty lists) | `GameCult.Networking` | the three message classes, the evaluator, Mesh | a selection is typed data with one validation | `_projectRecord`, the allowlist pair |
| `src/GameCult.Networking/CultNetSelectionEvaluator.cs`: `Select(cache, descriptors, selection, ordinals, asOf)` → ordered, hopped, cursored, projected page; `Matches(selection, descriptor, key, document)` for a single change; the cursor mint/parse with `cursor_stale` / `cursor_invalid` | `GameCult.Networking` | `CultNetDocumentRegistry` (snapshot), both servers (changes), `Reconcile` | one evaluator; order, snapshot and hop have one owner | the three engines (section 4) |
| `CultNetDatabase.LastWriteSequence(schemaId, key)` (a per-key ordinal kept on apply, ~20 lines) | `CultNetDatabase` | the evaluator's order and cursor | a row's ordinal is its last commit's sequence | `storedAt` as the only time |
| v1 message classes beside the v0 ones; the v0→`Selection` lowering (one function) | `GameCult.Networking` | both servers | v0 has no engine of its own | — |
| `packages/cultnet-rs/src/selection.rs`: the types (`Serialize`, `Deserialize`, no `untagged`), `Row` trait (`id`, `ordinal`, `values(index)`, `references()`), `RowSet` trait (`declared_indexes`, `declared_roles`), `Malformed`, `select`, cursor mint/parse, `LIMIT_MAX` | `cultnet-rs` | Huginn (section 12), later Rust services | the same evaluator semantics as the reference, pinned by vectors both ways | Huginn's own `select.rs` from the earlier design |
| `contracts.rs`: `SnapshotRequestV1`, `DatabaseSubscribeV1`, `SnapshotResponseRawV1` variants | `cultnet-rs` | Huginn's daemon | the wire spelling equals the reference's | — |
| `contracts/cultnet/selection-vectors.cs-written.json` and `selection-vectors.rs-written.json`: a fixture row set (`cultnet.interop-selection-row` document type with declared indexes `kind`, `severity`, `tags[]` and references `parent`, `related[]`), twenty selections, and each selection's expected page as ids + `matched` + `next` presence | tests, both runtimes | `NetworkingTests`, `cultnet-rs/tests/selection.rs` | **vectors written by the reference decode and evaluate identically in Rust, and vectors written by Rust decode and evaluate identically in the reference** | a within-runtime round trip, which pins nothing about parity |
| `contracts/cultnet/interop/cultnet.interop-selection-row.schema.json` | contracts | the vectors | — | — |
| `docs/cultnet-selection-cut.md` (this file), a paragraph in `contracts/cultnet/cultnet-distributed-database.md` under "Implemented", and the runtime table row in `docs/runtime-parity-scope.md` | docs | — | describe the live system | — |

No dependency in either runtime (`schemars` is **not** added to
`cultnet-rs`: the schema is hand-written JSON like every CultNet schema, and
Huginn's derived schema for its own request embeds `Selection` through a
hand-maintained `JsonSchema` impl in Huginn, not through the substrate —
see section 12). No new package, binary, transport, or store format.

## 7. Per-file changes (code that exists, anchored; new code by name and rule)

**`src/GameCult.Networking/CultNetSchemaMessages.cs`**: after `:826`, the
three v1 classes; `CultNetSchemaVersions` (`:77`, `:93` and the
`SnapshotResponseRaw` constant) gains `SnapshotRequestV1`,
`DatabaseSubscribeV1`, `SnapshotResponseRawV1`. v0 classes untouched.

**`CultNetSchemaMessageSerialization.cs:9-85`**: three arms before the
`_ => throw`.

**`CultNetSchemaRegistry.cs:360-385`**: four `SchemaResourceSpec` entries.

**`CultNetDatabase.cs`**: `:47` `internal static class` → `public static
class`; a `Dictionary<(string, string), long>` maintained where a mutation
is appended to the shard log (Hands anchors on the append site; section 1's
assumption is confirmed there) and `LastWriteSequence`; `:1052-1075` untouched.

**`CultNetDocumentRegistry.cs`**: `:247-258` → `CreateSnapshotRequest(messageId, CultNetSelection)`;
`:263-349` → `CreateRawSnapshotResponse(cache, messageId, selection, ordinals, asOf, options)`
calling the evaluator and `ToRawRecord`; `:505-524` deleted.

**`CultNetDatabaseServer.cs`**: `:276-316` handles v0 (lowered) and v1;
`:332-345` `CreateSubscription` calls `evaluator.Matches(selection, change)`
then `ToRawRecord`; `:347-468` deleted.

**`CultNetDatabaseSubscriptionServer.cs`**: `:27, :44, :50` `_projectRecord`
deleted; `:112-167` handles v0 (lowered) and v1, and answers a
`snapshot_response_raw.v1` page with `matched`/`asOf`/`next`; `:218-249`
`Watch`: a selection with `cites`/`cited` runs `Reconcile` on every change,
otherwise `Matches` on the changed row; `:294-322` evaluate + authorize +
project; `:424-515` deleted. Module doc `:14-18` rewritten: projection is a
value on the request.

**`CultNetSelection.cs`**, **`CultNetSelectionEvaluator.cs`** (new): as
section 6. Rules that must die under their own mutation are in section 10.

**`src/GameCult.Mesh/CultMeshSnapshots.cs`**: `:16-62` the options record
carries `Selection`; `:139-201`, `:434-516`, `:611-636` overlay through one
`WithSelection`; `:638-661`, `:750-773`, `:889-911` deleted; `:973-983`,
`:1000-1009`, `:1033-1047`, `:1078-1088` deleted.
**`CultMesh.cs`**: `:1941-1969`, `:2721-2748`, `:2750-2800` (one exact read
remains), `:3474-3497` deleted; `:2701-2719`, `:2838-2858`, `:2875-2915`
build a `Selection`.
**`CultMeshClient.cs:574-604, 830-860, 932-962`**, **`CultMeshBodyDemand.cs:177-221`**:
`Selection` literals.

**`packages/cultnet-rs/src/selection.rs`** (new), **`contracts.rs:314-331`**
region gains the three v1 variants, **`lib.rs`** re-exports. **`tests/selection.rs`**
(new): the vectors both directions, the evaluator, the cursor, the
refusals, the untagged grep.

**`tests/GameCult.Networking.Tests/NetworkingTests.cs`**: the seven tests in
section 4 rewritten; new tests in section 10; the vector writer under
`CULTNET_WRITE_VECTORS=1` following `scripts/write-cultmesh-realtime-frame-vectors.mjs`
and the C#-written pattern the QUIC campaign established (`docs/typescript-quic-realtime-cut.md:15-21`).

## 8. Authority map

- **Owner.** CultNet's database organ, `src/GameCult.Networking`, as the
  reference: `CultNetSelection` owns the shape and its validation;
  `CultNetSelectionEvaluator` owns matching, the hop, the order, the
  snapshot and the cursor; `CultNetSchemaAliasMatching` owns schema
  identity; `CultNetDatabase` owns a row's ordinal. **The cache owns
  addressing**: which names are indexes and which members are references,
  and what a value of each is (a string), through `CultIndexAttribute` and
  `CultReferenceAttribute` read from `CultDocumentDescriptor`. The
  vocabulary reads those declarations and adds none. `packages/cultnet-rs`
  is the Rust spelling of the same owner, at wire parity, with the row
  owner supplying what the cache supplies in C# (`Row`, `RowSet`).
- **Inputs.** The cache's rows and descriptors, the shard log's sequences,
  the selection, the peer's authorization answers. Not inputs: a clock,
  a payload path, a transport.
- **Outputs.** Pages (`headers` or `documents`, `matched`, `asOf`, `next`),
  per-change match decisions, typed refusals (`selection_invalid`,
  `cursor_stale`, `cursor_invalid`) as `cultnet.error.v0` with a code, the
  form the reference already uses for routing errors.
- **Derived state.** The page, the order, the incoming-edge index per
  evaluation, the cursor. **Demotions:** `schemaIds`/`recordKeys` are no
  longer a selection language; they are a lowering of v0 into one.
  `_projectRecord` is no longer an authority; projection is a request
  value. `CreateRawSnapshotResponse`, `CreateChangeMessage` and
  `CreateChangeCore` are no longer engines; they call one.
  `IsSameCultDocumentSchema`, the Mesh `InferSchemaName` and the registry's
  `MatchesRequestedSchema` are no longer owners of schema identity.
- **Forbidden writers.** Neither server may match a record by a rule of its
  own; Mesh may not re-filter a page the server answered; nothing may mint a
  cursor a caller could construct; nothing may answer a page over a
  different `asOf` than its cursor's; the evaluator may not read a payload
  by path, only declared indexes and references; no type on the wire is
  `untagged`; the Rust `select` may not diverge from the reference on any
  vector.
- **Shared paths.** The evaluator under snapshot, change, and reconcile;
  `CultNetSchemaAliasMatching` under every schema match in both assemblies;
  `ToRawRecord` under snapshot and change; the v0 lowering under both
  servers.
- **Deletion line.** Commit 1: the shape, the evaluator and the v1
  messages beside v0, with the three engines rewired and deleted and the
  seven tests rewritten — green. Commit 2: `_projectRecord` and the
  Mesh collapse. Commit 3: Rust. Commit 4: vectors both ways and entries.
  Nothing lands with two engines alive.
- **Where the line to CultMesh falls.** Mesh is a client that builds
  selections and reads pages; it evaluates nothing. Where Mesh watches a
  cache locally by name or index (audit sites #2, #4-9, #16-21), it keeps
  its own loops until `CultNetDatabase.Watch(Selection)` exists (Q-H).

## 9. Parity: what this cut covers, and what the other runtimes do

**In this cut:** the C# reference (both servers, the client, the registry)
and Rust (`cultnet-rs`: types, evaluator, cursor; no subscription client,
because no Rust caller subscribes). Parity is pinned by vectors in both
directions (section 10), not by a round trip inside one runtime.

**Runtimes not in this cut, stated answer per runtime, by source read:**

| Runtime | On v0 (unchanged) | On a v1 request it receives | On a v1 page it receives |
|---|---|---|---|
| TypeScript | unaffected; the reference still answers v0 | **refuses**: `parseCultNetMessage` finds no validator for the version and throws (`contracts.ts:728-744`) | refuses the same way |
| Kotlin | unaffected (client only) | **refuses**: `require(schemaVersion == …v0)` throws (`CultMesh.kt:2195`) | n/a as a server; a Kotlin client sends v0 and gets v0 |
| Python | unaffected | **silently drops, no reply**: `handle_server_message` returns `[]` for an unknown version (`interop_peer.py:387`), so a caller times out rather than reading a refusal; **it cannot answer with wrong rows**, because v1 is a version it does not dispatch, which is why D3 chose a version over new fields | as above |
| Rust | in this cut | in this cut | in this cut |

The one runtime that mis-answers today is none: nothing sends v1 to a
runtime that does not speak it until its follow-up lands, and a stray v1 is
refused (TS, Kotlin, C#) or dropped (Python). Python's drop is a silence,
not a refusal, and is recorded as the first line of its follow-up.

**Follow-ups, recorded with triggers:**

- **FU-TS.** `cultnet-ts`: the three v1 contracts, Ajv registration, a
  `Selection` type; `cultmesh-ts` builders. Trigger: the first TypeScript
  caller that needs a filtered snapshot or subscription (the browser Eve
  runtime, when it reads pipeline state). Until then TS refuses v1 loudly.
- **FU-Py.** `cultnet-py` and `cultmesh-py`: reply `cultnet.error.v0
  { code: "unsupported_schema_version" }` for an unknown version (five
  lines, the honest minimum, may land before the rest); then the v1
  contracts, and the **three** copies of the selector match
  (`interop_peer.py:110-125`, `cultmesh_py/server.py:449-470`,
  `cultmesh_py/node.py:1322-1345`) collapsed into one evaluator. Trigger:
  the first Python peer asked a v1 question, or the interop lane gaining a
  v1 case.
- **FU-Kt.** `cultmesh-kotlin`: v1 snapshot request builder and page
  reader. Trigger: an Android/JVM client reading filtered state.
- **FU-v0.** Retire v0's classes, schemas and lowering once FU-TS, FU-Py,
  FU-Kt have landed and the interop lanes send v1. Trigger: the last of the
  three.
- **FU-Watch.** `CultNetDatabase.Watch(Selection)` in the reference and the
  Mesh collapse it enables (section 11, ≈ −240). Trigger: the first
  consumer that watches a question locally, or the operator's call (Q-H).

`docs/runtime-parity-scope.md`'s table gains, per runtime, "typed selection
v1: C# and Rust claimed; TS, Python, Kotlin not claimed (refuse / refuse /
drop)". The claim is written where the parity claims live, not here alone.

## 10. Verification

**Builds.** `dotnet build` of `GameCult.Networking`, `GameCult.Mesh` and
their test projects; `cargo test -p cultnet-rs`; the TypeScript workspace
untouched but `npm run test --workspace packages/cultnet-ts` run once to
prove the interop lane still passes on v0. Host = target = workstation for
C# and Rust; no native code. Nothing cleaned.

**Tests, each named for the rule it pins, with the mutation that kills it —
a loosening where one exists.**

| # | Test (C# unless marked) | Pins | Revert kills | Loosening kills |
|---|---|---|---|---|
| S1 | `Selection_RefusesUndeclaredIndexRoleAndEmptyLists` | D2, door refusals typed | validation dropped (empty page) | `index` checked against *any* schema's aliases rather than the reachable ones (a predicate on an alias only an excluded schema declares matches nothing silently) |
| S2 | `Evaluator_ConjoinsAnyOfPredicatesOverDeclaredIndexes` | fields | an arm dropped | any-of treated as all-of |
| S3 | `Evaluator_OrdersByLastWriteSequenceThenIdentityAndReverses` | order, `descending` | sort by `storedAt` | `descending` ignored |
| S4 | `Evaluator_PagesExactlyOnceAndTheLastPageSaysSo` | cursor walk | `next` always present | position `>=` (last item repeats) |
| S5 | `Evaluator_RefusesAStaleCursorAfterTheShardLogAdvances` | `cursor_stale` typed | advance ignored (a page over a moved set) | digest check dropped (a cursor accepted under another selection) |
| S6 | `Evaluator_HopsOneEdgeByDeclaredReferenceAndRole` | hop, roles from `CultReferenceAttribute`, `Many` | role ignored | a second hop followed |
| S7 | `Evaluator_NegatedIncomingEdgeIsTheOnlyNegation` | `cited { exists: false }` | `exists` ignored | citer's status consulted (an unauthorized citer's edge dropped) |
| S8 | `Evaluator_HeaderProjectionCarriesNoPayload` | projection | payload carried | `payloadEncoding` carried without payload |
| S9 | `Subscription_EvaluatesSelectionOnSnapshotAndEveryChange_AndReconcilesHopBearingOnes` (rewrite of `:1587`) | D6 | a change matched by v0 allowlists | a hop-bearing subscription taking the per-change fast path (a change to the citer not reconciled) |
| S10 | `V0Request_LowersToASelectionAndHasNoEngineOfItsOwn` (rewrites of `:3124`, `:3158`, `:5547`, `:5601`, `:2256`, `:2310`) | D3/D4 | v0 matched by its own loop | alias matching inlined again in one server |
| S11 | `SchemaAliasMatching_IsTheOneMatcherInBothAssemblies` (grep-shaped, negative) | D4 | — | — |
| S12 | `SelectionVectors_WrittenByTheReference_DecodeAndEvaluateIdenticallyInRust` (Rust reads `selection-vectors.cs-written.json`) and `SelectionVectors_WrittenByRust_DecodeAndEvaluateIdenticallyInTheReference` (C# reads `selection-vectors.rs-written.json`) | **parity** | any semantic drift on either side | a vector whose expected ids are compared as a set (order lost) — the vectors carry ordered ids and `next` presence |
| S13 (Rust) | `selection_is_never_untagged` (grep over `packages/cultnet-rs/src` for `untagged`, empty) and `selection_round_trips_named_and_positional` (both encodings) | the encoding rule | an `untagged` shape added | — |
| S14 (Rust) | `select_over_a_toy_row_set_matches_orders_hops_pages_and_refuses` | D7, no CultCache knowledge in the evaluator | as S1-S8 | as S1-S8 |
| S15 | `Mesh_ReadsASnapshotPageWithoutReFilteringIt` (Mesh tests) | the client trusts the page | the four-tier fallback restored | schema re-filter restored |

**Negative greps:** `rg -n "untagged" packages/cultnet-rs/src packages/cultcache-rs/src`
empty; `rg -n "InferSchemaName" src/GameCult.Mesh` empty and
`src/GameCult.Networking` only in `CultNetSchemaAliasMatching` and the
registry's payload-schema resolution; `rg -n "MatchesRequestedSchema|MatchesSchema\(" src`
empty; `rg -n "_projectRecord|projectRecord" src` empty;
`rg -n "SchemaIds = .*RecordKeys = " src/GameCult.Mesh` empty (no
allowlist pair assembled by hand); `rg -n "CloneSnapshot(Facade)?RequestOptions" src` at most one.

**Mutations.** `tools`-style entries in `scripts/mutate-cultmesh.mjs`'s
format with two new targets, `networking` (killer `dotnet test
tests/GameCult.Networking.Tests`) and `rust` (killer `cargo test -p cultnet-rs`),
one entry per rule above with revert and loosening; the runner's
byte-rewrite/verified-restore discipline applies unchanged. The runner is
the QUIC campaign's file and is dirty in the tree; **this campaign adds its
entries in its own file** (`scripts/mutate-cultnet-selection.mjs` or an
entries module the runner imports) and does not edit the QUIC campaign's
targets — Self decides the exact split at landing.

## 11. The CultMesh subtraction audit, with real numbers

Method: an Explore pass classified 92 sites in `src/GameCult.Mesh` and
`CultNetDocumentRegistry.cs` as (A) mechanical selection/projection over
schema ids, keys or fields, or (B) a domain rule (authority, freshness,
transport, session). (A) totalled **1,158 lines**, (B) **429**. **(A) is not
a reduction figure**; it is what *could* be expressed as a selection. What
collapses is what has a replacement in this cut.

| Category | Sites (audit #) | Lines now | After | Net |
|---|---|---:|---:|---:|
| Selector-carrying options cloned five times | #11, #3, #74, #75, #77 | 128 | ~30 (`with` on a record) | **−98** |
| "No schema filter when keys are given" rule, three times | #65, #73, #10 | ~16 | ~4 | −12 |
| `CleanSnapshotFilter` | #79 | 11 | 0 | −11 |
| Client-side re-filter of a snapshot by schema, plus `InferSchemaName` | #80 (part), #81, #82 | ~51 | ~10 (decode only) | −41 |
| Four-tier read fallback + decode gate | #12, #13 | 50 | ~10 | −40 |
| `IsSameCultDocumentSchema` | #22 | 24 | 0 (public matcher) | −24 |
| Selector literals in client bindings | #32, #34, #35 | 93 | ~68 | −25 |
| Hot-body subscription allowlists | #25 | 45 | ~30 | −15 |
| Peer snapshot / body subscribe selector construction | #10, #14, #15 | 81 | ~61 | −20 |
| Snapshot facade overlay logic | #64, #66-70, #72 | ~110 | ~70 | −40 |
| **Collapses in this cut** | | | | **≈ −326** |
| Local cache/database watches by name and index and their predicates | #2, #4-9, #16-21 | ≈ 240 | ≈ 240 | 0 now; **≈ −200 under FU-Watch** |
| Handle catalogs by type/schema, surface catalog, operation payload | #41-49 | 233 | 233 | 0 — these index *handles*, not records |
| Verse discovery selection and reshaping | #36, #38 (part), #39, #40, #83, #85 | ~110 | ~110 | 0 — over Verse observations, not documents |
| Single-file catalog membership | #58-62 | 81 | 81 | 0 — file-format membership |
| Provider session wire decode gate | #50 | 12 | 12 | 0 |
| (B) sites | 24 sites | 429 | 429 | 0 by definition |

**Plain statement.** `src/GameCult.Mesh` shrinks by about **326 lines of
21,720 (1.5%)** in this cut; a further ≈ 200 collapse only when
`CultNetDatabase.Watch(Selection)` exists (FU-Watch); about 590 of the
audit's (A) lines stay because they select handles, Verse observations or
file-catalog entries, not database records. The operator's expectation of a
net reduction in CultMesh holds, modestly; it does not hold for CultLib as a
whole in this cut, because the Rust runtime is new capability (section 14).
If the expectation was a large reduction, this audit says it is not there
without the watch cut, and it is not there at all for the catalogs and
discovery.

## 12. Huginn: the consumer cut, and what moved

**Moves to CultLib (this map):** `Selection`, `Citation`, `Incoming`,
`Projection`, `Page`, the cursor, `select`, the `Row`/`RowSet` traits, the
untagged rule and its probe, the cap, the order rule and the snapshot
contract. Huginn's earlier `select.rs` is never written.

**Stays with the organ (a smaller, later cut in the Epiphany campaign map,
against a `cultnet-rs` dependency Huginn already has):**

- `impl Row for PipelineDocumentView`: `ordinal = admission.sequence`
  (Q-B A, still recommended; under Q-B B Huginn refuses `cursor_stale` on
  advance exactly as the reference does, which is now a legitimate second
  option), `values(index)` for the declared aliases `campaign`, `repo`,
  `cut`, `kinds`→(schema), `in_force`, `faculty`, `severity`, `confidence`,
  `origin`, `authority`, `claim_outcome`, `outcome`; `references()` from
  `docs::citations` with the fifteen roles; `RowSet::declared_*` as the
  organ's closed lists, so an unknown alias or a value outside an enum
  refuses typed.
- `docs::citations`, `CitationRole`, `ResolutionOutcomeKind`, the summary
  as the organ's header, the `as_of` restriction of the image to receipts,
  the semantic refusal, the ref validation of `cites.target` through the
  leaf, and the deletion of `open_items`/`history` with the relation table
  unchanged (`history(Subject)` = `kinds: [resolution], cites { target, role: subject }`;
  the open lists = five selections).
- **Dropped from the organ's wire:** `PipelineQuery`, `admitted_after/before`
  (Q-G), `QUERY_LIMIT_MAX`. Huginn's `Query { instance, selection }` carries
  the substrate's type; its published request schema embeds `Selection`
  through a hand-maintained `JsonSchema` impl for the `cultnet-rs` types
  inside Huginn (the substrate publishes JSON, not `schemars`), pinned by the
  existing byte-for-byte test.
- Cut 11's lowering to Qdrant and the after-index application of the hop,
  as before.

The Huginn cut is ordered **after** this cut's commit 3 (Rust) and before
Cut 11 and Cut 13, as 10b was.

## 13. Deliberately not possible, and why

OR across predicates (ask twice); negation beyond `cited { exists: false }`;
a predicate on the far end of the hop; a second hop; ranges, dates, text,
paths, sort fields, counts beyond `matched`; a page not tied to `asOf`;
selection across shards or minds; a cursor the caller constructs. Each for
the reason the earlier specification gave, unchanged.

## 14. Subtraction ledger (estimate)

| | Removed | Added |
|---|---:|---:|
| `src/GameCult.Networking` | 368 | ~350 (`CultNetSelection` 140, evaluator + cursor 180, ordinals 20, v1 classes and lowering ~40 minus shared) |
| `src/GameCult.Mesh` | ≈ 365 gross | ≈ 40 (one `WithSelection`, one exact read) |
| `packages/cultnet-rs/src` | 0 | ~390 (`selection.rs` 350, `contracts.rs` 40) |
| `contracts/cultnet` | 0 | 4 schema files (~200 lines JSON), 1 interop row schema, 2 vector files (generated) |
| tests, C# | ~250 rewritten | ~450 (S1-S12, S15, the vector writer) |
| tests, Rust | 0 | ~300 (S12-S14) |
| entries | 0 | ~30 entries |
| docs | — | this map; two paragraphs |
| **net source outside tests** | | **≈ −18 in C#, +390 in Rust; ≈ +370 across the repo** |

The repo grows by about 370 source lines for: a typed selection with a
derived-by-vector schema where two allowlists were the only language;
projection as a value where it was a delegate no caller reached; an exact
snapshot cursor where paging did not exist; one evaluator where three
engines and six schema matchers stood; a Rust runtime that did not have the
capability at all. CultMesh proper shrinks ≈ 326. The liability retired is
not in the line count: five copies of one options record, four spellings
of schema identity, a client that re-filtered what the server answered and
a read that could return the wrong key.

## 15. Build budget

C#: two assemblies and two test projects, debug, workstation. Rust:
`cultnet-rs` lib + tests, warm target dir, +0 to +60 paths. Nothing cleaned.
No native, no TypeScript build beyond one interop-lane run.

## 16. Operator questions, one batch

- **Q-A. Leaf `Short` titles for question and ruling** (Huginn/Epiphany).
  Unchanged: **A, not now** (recommended); B, a leaf follow-up before Cut 13;
  C, a text projection (Cut 11's question). Depends: the organ's header row
  for those two kinds.
- **Q-B. Huginn's receipt ordinal.** **A, record it** (recommended; exact
  order, no `cursor_stale` on an append-only mind); **B, do not**: Huginn
  refuses `cursor_stale` on advance like the reference does — now a
  legitimate option under one contract, costing a re-walk on a busy mind
  and id-order ties within a second. Depends: Huginn's consumer cut only.
- **Q-C. Delete `open_items` and `history`.** **A, delete** (recommended);
  B, thin presets in Huginn; C, wire operations (refused). Depends: Cut 13's
  tool list.
- **Q-D. The representation.** **A, declared-alias any-of over strings in a
  new v1 selection** (recommended; D1, D2); B, Qdrant's `Filter` (rejected,
  section 3); C, typed enums on the wire (impossible in the substrate: it
  cannot know a consumer's enums; the organ keeps the typed refusal at its
  door). Depends: everything in sections 2-10.
- **Q-E.** Withdrawn: the seam question is answered by the placement ruling.
- **Q-F. v0's life.** **A, keep v0 answerable in the reference as a
  lowering until FU-TS, FU-Py, FU-Kt land** (recommended; the interop CI
  witness stays green, and v0 has no engine of its own); B, delete v0 now
  and break the three runtimes' lanes; C, add the new fields to v0 —
  refused by D3 because Python would mis-answer. Also under Q-F: whether
  FU-Py's five-line "reply with `unsupported_schema_version`" lands ahead of
  its trigger (recommended yes; it is a refusal where there is a silence).
- **Q-G. `descending` in place of the admission window.** **A** (recommended):
  one fixed order, reversible; "the latest N" is `descending, limit: N`;
  Huginn's `admitted_after/before` leave its wire. **B**: keep a window as
  the one range, on the ordinal only (`sinceOrdinal`), which is a second way
  to say what a cursor says. **C**: keep Huginn's date window as an
  organ-side alias any-of over `admitted_on` dates — expressible today with
  no substrate change, weaker than a range, honest. Depends: Huginn's
  filter list; nothing in the substrate.
- **Q-H. `CultNetDatabase.Watch(Selection)` as a named follow-up cut.**
  **A, yes, after the runtime follow-ups** (recommended): it is the
  "watch a question" affordance the seam was drawn for and the ≈ 200 further
  Mesh lines; B, fold into this cut — refused by the coordinator's own
  constraint not to design the watch now; C, never — leaves the local
  watch helpers as they are.
- **Q-I. Vectors: the fixture row type.** **A, a dedicated
  `cultnet.interop-selection-row`** (recommended; declares indexes and
  references purposely, both runtimes register it); B, reuse
  `cultnet.interop-note` and add index/reference declarations to it —
  touches the live interop lane's document.

What depends on each: S1-S8 on Q-D A; S5 on the shard-log assumption
(section 1) and on Q-B for Huginn; S9 on D6; S10 on Q-F A; S12 on Q-I;
the Huginn cut on Q-B, Q-C, Q-G; FU-Watch on Q-H.

## 17. What was probed, read, and not settled

**Probed:** the untagged round trip (section 2). **Read:** every line
anchored above; the CultMesh audit's 92 sites by an Explore pass whose
classification I checked at the sites that carry the ledger (the five
clones, the four matchers, the fallback, the two engines). **Not run:**
`dotnet` or `cargo` on CultLib — the tree is dirty from the QUIC campaign
and no claim here depends on a build; Hands takes the baselines named in
Pins. **Not settled:** whether every committed write has a shard-log
sequence (section 1, Hands confirms at the append site); the exact split of
mutation entries against the QUIC campaign's runner (section 10, Self at
landing); whether MessagePack-CSharp needs a union attribute for the
page's `headers | documents` (it does not if both are optional arrays with
exactly one present, which is the spelling chosen so no union is needed —
Hands confirms the schema says "exactly one" with `oneOf` over `required`).
