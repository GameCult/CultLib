# CultNet error contract: cut map

Status: cut map, not started. Imagination pass 1, 2026-09-30, against CultLib `main` at `8fc74c70`.
This document owns the means. There is no separate target doc: section 1 holds the ends until the
operator rules on section 3. After that, Self moves the rulings and invariants into
`contracts/cultnet/cultnet-distributed-database.md` ("Error Codes") and the schema's own description.

Open: operator questions Q1-Q8 (section 3). No cut is dispatched until Q1, Q2, Q3 and Q8 are ruled. Q4-Q7
change only single emit sites and can be ruled during Cut 1.

Depends on: `hands/cultmesh-put-serve-bound` merging first. It introduces `document_unservable` in Python
(`fd394d54`), a codeless unservable refusal in cultmesh-ts (`0eb7434a`) and `CultMeshRudpRejectionReason` in
cultmesh-rs (`c9cdad12`, `85288a5a`). The cuts below convert those sites. Their anchors are by commit, not by
line, because the branch is not on main.

Map branch: `main`.

---

## 0. What the Body does today (probed)

Every claim in this table was established by running code. The C#, Rust and TypeScript probes ran on
Yggdrasil through `ygg-verify.sh` at `8fc74c70`. The Python bytes came from `msgpack.packb` on the exact
dicts `cultmesh_py/server.py:573-589` and `cultmesh-ts/src/index.ts:5921-5926` build. The six probe inputs:

| Input | Bytes from |
|---|---|
| `cs_cursor_stale` | Rust `tests/error_message.rs` captured C# hex (`cursor_stale`, details `{asOf:1,current:2}`) |
| `cs_routing` | C# `new CultNetErrorMessage { Error, RoutingHint = { Reason="not_primary", Shard=... } }` serialized with `CultNetSchemaMessageSerialization` |
| `cs_codeless` | C# `new CultNetErrorMessage { Error = "boom" }` |
| `cs_unknown_code` | C# with `Code = "document_unservable"`, empty `CultNetErrorDetails` |
| `py_snapshot_byte_limit` | the Python `_error_response` shape: `messageId`, `code: "snapshot_byte_limit_exceeded"`, `details {responseBytes, maxSnapshotBytes}` |
| `ts_two_key` | the cultmesh-ts shape `{schemaVersion, error}` |

Decoded by:

| Input | C# `CultNetSchemaMessageSerialization.Deserialize` | Rust `decode_cultnet_message_from_slice` | TS `parseCultNetMessage` |
|---|---|---|---|
| `cs_cursor_stale` | ok | ok, typed | ok |
| `cs_routing` | ok | ok, **routingHint dropped** (`code: None, details: None`) | ok (untyped passthrough) |
| `cs_codeless` | ok | ok | ok |
| `cs_unknown_code` | ok (free string) | **Err: "not a recognized cultnet.error.v0 code"** | **throws: /code must be one of the allowed values** |
| `py_snapshot_byte_limit` | ok, but **details silently become an all-null object** (`responseBytes`, `maxSnapshotBytes` lost), `messageId` dropped | **Err: not a recognized code** | **throws** (missing routingHint, extra messageId, code enum, details keys) |
| `ts_two_key` | ok | ok | **throws: missing routingHint, code, details** — TypeScript cannot parse the errors TypeScript sends |

Captured C# bytes for the two shapes Rust's vector file does not hold (for Hands' reuse):

- `cs_codeless`: `85AD736368656D6156657273696F6EB063756C746E65742E6572726F722E7630A56572726F72A4626F6F6DAB726F7574696E6748696E74C0A4636F6465C0A764657461696C73C0`
- `cs_routing`: see the probe; the shard descriptor writes ten keys, nil included.

Other findings, from reading source at `8fc74c70`:

1. **The interop CI lane has been red for 40 consecutive `main` runs.** It fails at "Verify CultNet schema
   contracts stayed consolidated" (`.github/workflows/cultnet-interop.yml:67-90`). That step forbids every
   `*.schema.json` outside `contracts/cultnet/`, and R-AB deliberately vendors 22 copies into
   `packages/cultnet-rs/contracts/cultnet/`. Those copies are held byte-equal by
   `packages/cultnet-rs/tests/schema_discovery_contracts.rs:74-99`. Nothing after that step (every
   cross-runtime interop test) has run on main in that time. Run `36714768445` is the latest.
2. **The schema contradicts itself.** `contracts/cultnet/cultnet.error.schema.json:29` says the code is
   "Additive: a v0 peer that does not know this field ignores it" and, in the same sentence, "a closed
   enum: Rust and TypeScript refuse". The closed-enum text came from Soul pass 4d (`3f942a57`). It
   described what the code did; no operator ruled it. `docs/` holds no ruling on unknown codes.
3. **Emitters that break the schema:**
   - cultmesh-ts sends `{schemaVersion, error}` only, at `packages/cultmesh-ts/src/index.ts:5875-5878`,
     `5885-5888`, `5913-5916` and `5922-5925`, and on the branch at `0eb7434a`.
   - cultmesh-py `_error_response` (`packages/cultmesh-py/src/cultmesh_py/server.py:573-589`) adds a
     top-level `messageId`, omits `routingHint`, omits `code` and `details` rather than sending nil, and
     sends eight out-of-enum codes: `unsupported_schema_version` (`:161`), `malformed_document_put`
     (`:358`), `unregistered_document_put` (`:365`), `malformed_document_delete` (`:386`),
     `unregistered_document_delete` (`:393`), `snapshot_document_limit_exceeded` (`:558`),
     `snapshot_byte_limit_exceeded` (`:567`) and `simulation_observations_disabled` (`:596`). The branch
     adds a ninth, `document_unservable` (`fd394d54`).
   - `packages/cultnet-py/src/cultnet_py/interop_peer.py:409-417` also sends `unsupported_schema_version`.
     R-2 (`docs/cultnet-selection-cut.md:2046-2053`) required that code, but it never entered the schema
     enum.
4. **A second schema authority.** `packages/cultnet-py/src/cultnet_py/schema_catalog.py:17`, `:272` and
   `:357-362` publish a hand-built `cultnet.error.v0` descriptor under the canonical `$id`. It has a
   different shape: `messageId`, `code` as any string, `details` open, and only `schemaVersion` and `error`
   required. The same file does this for every wire message (about 350 lines). This is recorded as
   follow-up F-1, not cut here.
5. **Silent refusals.** cultmesh-rs's RUDP document server returns every rejection to the local caller only
   (`packages/cultmesh-rs/src/rudp_document_server.rs:470-590`, `CultMeshRudpApplicationRejection`). The
   peer gets nothing and times out. On the branch this includes the new `DocumentUnservable`.
6. **Receivers drop the code.**
   - Every C# receiver turns an error into `new IOException(error.Error)` or
     `new InvalidOperationException(error.Error)`, so a caller cannot branch on the code. The sites:
     `src/GameCult.Mesh/CultMeshBodySessions.cs:310`, `CultMeshContentSessions.cs:178/256-260`,
     `CultMeshPeerExchange.cs:430/466`, `CultMeshSnapshots.cs:91/107/222-226/743` and
     `CultMeshVerseDiscovery.cs:273`, plus `src/GameCult.Networking/CultNetShardReplication.cs:440/574`.
   - Rust reads `Error { error, .. }` at `packages/cultmesh-rs/src/lib.rs:823`.
   - TS types `CultNetErrorMessage` as `{schemaVersion, error}` only (`packages/cultnet-ts/src/contracts.ts:167-170`).
   - Kotlin has no error type. A request that gets an error back fails a
     `require(response.schemaVersion == ...)` (`packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/CultMesh.kt:1278`,
     `:1334`), and the peer's text is lost.
7. **No correlation.** Only Python echoes `messageId`.
   - `cultmesh-browser` rejects an error only when exactly one operation is pending
     (`packages/cultmesh-browser/src/index.ts:724-731`); otherwise the error is dropped and the operation
     times out.
   - C# `CultMeshContentSessions.OnError` (`:256-260`) fails every pending request on any error.
8. **The reference is inconsistent with itself.** `src/GameCult.Networking/CultNetDatabaseServer.cs:560`
   refuses a variant change with a codeless error. The subscription server refuses the same condition with
   `ForVariantUnsupported` (`CultNetDatabaseSubscriptionServer.cs:211/333/366`).
9. **routingHint is never consumed.** No receiver in any runtime reads it. C# builds it at
   `CultNetDatabaseServer.cs:638-650`, and Rust's decode discards it.
10. **Consumer audit** (read-only, all of `F:\Projects` except CultLib; no git in Aetheria):
    - Outside CultLib, nothing sets `code`, `details` or `routingHint`, and nothing branches on a code.
    - Emitters:
      - Huginn: `crates/huginn-daemon/src/serve.rs:288/307/312`, Rust struct literals
        `Error { error, code: None, details: None }`, pinned at cultnet-rs `8fc74c70`.
      - AetheriaEve: `Aetheria.State.Daemon/Program.cs:1737/1778`, C# object initializers.
    - Decoders read `.error` only: Gjallar `Program.cs:574`, Mimir
      `MimirOdinMoveProofEvidenceRingProvider.cs:80`, Hermodr `hermodr-daemon.cjs:1380` (hand-rolled),
      AetheriaEve's smoke test (text match against its own emit text), and Huginn's tests (text match).
    - Stale vendored TS:
      - `StreamPixels/vendor/CultLib` carries the current closed schema, so any change here breaks its
        TS error decode until it is re-vendored.
      - `Heimdall/vendor/CultLib` carries a pre-R-X schema with `additionalProperties:false` and no
        `routingHint`, so it already refuses every error C# sends today.
    - Odin, Idunn, Muninn, Ghostlight and voidbot: zero hits.

## 1. Ends

- **One schema.** `contracts/cultnet/cultnet.error.schema.json` is the only authority on the shape and on the
  code registry. C# embeds it (`GameCult.Networking.csproj:19`), TS mirrors it at build time, and Rust
  vendors a copy held byte-equal by test. Nothing else publishes a `cultnet.error.v0` shape (F-1 covers
  Python).
- **Every runtime emits only schema-valid errors.** This is proven per emit path against the schema, and
  byte-equal to the C# reference for every registry code.
- **Every runtime decodes every error it can receive into a typed value.** A known code becomes its typed
  variant with its typed details. An unknown code, if Q1 says so, becomes a typed "other" that keeps the
  code string and the raw details. No well-formed error is ever refused, and no field is silently dropped
  (routingHint, messageId).
- **Code and details have one owner.** The details a message carries are decided by its code. The types in
  every runtime make "cursor_stale with a field" unrepresentable. No free-standing code string plus a
  grab-bag details object.
- **A refusal is an answer, not a silence** (R-2 precedent, and Q4). A server that refuses a peer's request,
  and can still send, sends a coded error that correlates to the request.
- **The public surface is complete in every runtime**, per the consumer-expectation doctrine:
  - a typed error value;
  - a constructor per registry code;
  - a typed exception or error that request APIs raise when the peer refuses, carrying the typed value.

## 2. Identity, lifecycle and authority

| Kind | What names it | Lifecycle | Who decides |
|---|---|---|---|
| Error message | `schemaVersion: cultnet.error.v0`; correlated to its request by `messageId` (Q3) | Sent once, in reply to one request or subscription. Never persisted. | The server that refused. |
| Error code | A snake_case string in the schema's registry (`$defs/knownCode`) | Added by one coordinated change: schema, C# constructor, a C#-written vector, and a typed variant in every runtime. Never renamed and never reused. A retired code stays in the registry marked retired, so an old reader still decodes it. | The operator rules meaning; Self keeps the registry. |
| Details for a code | The code | Frozen with the code. Adding a key to an existing code's details is a new code (or a v1), never an in-place edit (see Q2). | Same as the code. |
| Unknown code, at a reader | The raw string | Preserved as typed `Other`. The reader acts on the `error` text and the absence of a known code. | The reader (Q1). |
| Vector file | `contracts/cultnet/interop/error-vectors.cs-written.json` | Regenerated only by the C# writer test under `CULTNET_WRITE_VECTORS=1` and committed. Every other runtime judges it read-only. | C#, the reference. |
| routingHint | Shard authority refusal | Sent with a null code (unchanged). Typed in every decoder. | CultNetDatabaseServer's authority path. |

## 3. Operator questions

### Q1. What does a reader do with an error code it does not know?

Today Rust and TypeScript refuse the whole message. The probes above show the result: the peer's error text
is lost and the caller sees "not a recognized code". In cultmesh-rs's snapshot client the `?` at `lib.rs:812-815`
even turns a peer refusal into a local decode failure. So under a closed enum every new code breaks every
reader that has not upgraded. By `docs/semver-policy.md:86-91` that makes each new code a breaking wire
change, and nine codes are already in flight.

- **A. Closed enum (today).** Readers refuse unknown codes. Every code addition is a coordinated,
  semver-major wire change across all runtimes and consumers.
- **B. Closed for emitters, open for readers.** The schema keeps the registry, and every runtime's emit
  constructors can produce only registry codes (tests prove each emit path valid against the schema).
  Readers decode an unrecognized code (matching `^[a-z][a-z0-9_]*$`) into a typed `Other { code, details }`.
  A new code is an additive, minor change: old readers still surface the text and know "an error I cannot
  classify". This is how gRPC status codes and protobuf open enums behave.
- **C. Fully open.** No registry; any string, emit and decode.

**Recommended: B.** A refusal must never fail to arrive because it is more specific than the reader
expected. B keeps the registry as the single meaning authority. C loses the meaning authority, and A makes
the error channel the most brittle message in CultNet.

Depends on this: schema `code` shape, every decoder, the doc text at `cultnet-distributed-database.md:107-115`.

### Q2. How are details shaped on the wire?

Today C# writes all four detail keys (`field`, `value`, `asOf`, `current`), nil included, because
`CultNetErrorDetails` (`CultNetSchemaMessages.cs:592-603`) is a MessagePack map-mode object. The codes in
flight need new keys: `responseBytes`, `maxSnapshotBytes`, `documentCount`, `maxSnapshotDocuments` and
`schemaVersion`.

- **A. One shared details record; grow it.** Every detail key of every code sits on one record, written as
  nil when unused. Each new key changes the bytes of every existing error that has details, so all of the
  C# vectors must be recaptured on every addition. It also breaks every strict reader: TS Ajv, and
  StreamPixels' vendored schema, have `additionalProperties:false`. And the type cannot tie a key to its
  code.
- **B. Per-code details.** `details` carries exactly the keys its code defines. A defined-but-optional key
  is present as nil; another code's keys are absent. Keys are named for their meaning, which retires the
  `field: "schemaId"` / `field: "subscriptionId"` indirection that exists only because the record is
  shared. Adding a code never changes an existing code's bytes. In every runtime, code and details are
  fused into one tagged type:
  - Rust: an enum with data.
  - C#: sealed subclasses, with a custom formatter for `CultNetErrorMessage`. There is precedent in
    `src/GameCult.Mesh/CultMeshRouteRecordFormatter.cs`.
  - TS: a discriminated union.
  - Kotlin: a sealed class.
  - Python: constructors per code over a frozen dataclass.

  The churn is one time. The five existing C# cases with details change bytes, and all eight Rust hex
  cases move into the shared vector file anyway (Cut 1).
- **C. Opaque string map.** Untyped; no runtime can offer a typed accessor.

**Recommended: B.** A is the churn the brief warned of, and it recurs on every future code. B pays it once,
at the moment every vector is being recaptured regardless.

Depends on this: the schema's `details`, the C# model and formatter, `encode_error_details`
(`packages/cultnet-rs/src/contracts.rs:1339-1366`), and every decoder.

### Q3. Does an error carry the `messageId` of the request it refuses?

Python already sends `messageId` (`server.py:583`); the schema forbids it. Without it:

- `cultmesh-browser` drops an error whenever two operations are pending (`index.ts:724-731`);
- C# content sessions fail every pending request on any error (`CultMeshContentSessions.cs:256-260`).

Adding a top-level key changes the bytes of every C# error. That happens once, in the same recapture as Q2.

- **A.** Add a nullable top-level `messageId`. A reply to a request echoes the request's id, and an error
  with no request (a subscription push, an unsolicited refusal) sends nil. Receivers correlate by it and
  fall back to their current behavior when it is nil.
- **B.** No correlation. Python deletes the field.

**Recommended: A.** Correlation is a capability any multiplexing client needs, and one runtime already
depends on it.

### Q4. Do servers send `document_unservable` (and every other refusal they can) to the peer?

Self ruled that "unservable" must be distinguishable as a typed code in every runtime. On the branch:
Python sends it coded, TypeScript sends it codeless, and Rust tells only its local caller, so the Rust
peer times out. The same Rust silence covers every other document-server rejection: a sink refusal, a
failed snapshot source, too many snapshot documents, and a snapshot response that is too large.

- **A.** Every refusal of a peer's request that the server can still send is answered with a coded error
  carrying the request's `messageId`, and is also reported locally (Rust's `ApplicationRejected` stays as
  the local report). The refusals that cannot be answered are the ones where the answer itself cannot be
  queued: Rust `PayloadBudgetFull` and `ResponseQueueFailed`. Those stay local-only, and the doc says so.
- **B.** Local-only in Rust; peer replies only where a runtime already sends them.

**Recommended: A.** R-2 already ruled that a silence is worse than a missing feature
(`docs/cultnet-selection-cut.md:2049-2053`), and the operator's ruling that CultMesh "refuses" puts only
means something to the party that sent the put.

### Q5. Does Python's `unregistered_document_put` / `unregistered_document_delete` become `unowned_schema`?

Python sends these when the node has no registered document type for the record's schemaId
(`cultmesh_py/node.py:653-666`). C# `unowned_schema` means "no shard owns this schema and key"
(`CultNetSchemaMessages.cs:537-544`). To a peer both mean "this server will not take that schema here; route
elsewhere". The put and delete variants duplicate what the peer already knows (it sent the message).

- **A.** Both collapse into `unowned_schema { schemaId }`. The registry text broadens to "this server
  stores no document for that schema (and key)".
- **B.** Join as a separate `schema_unregistered { schemaId }`.
- **C.** Join both codes as they are.

**Recommended: A.** It is one meaning at the peer, and there is one action to take.

### Q6. Does Python's `malformed_document_put` / `malformed_document_delete` become one `message_invalid { field }`?

These are sent when the request lacks a required field (`server.py:355-359`, `383-387`). C# has the same
condition, codeless, at `CultNetDatabaseServer.cs:373/415` and `CultNetDatabaseSubscriptionServer.cs:148`
("requires a subscriptionId or messageId").

- **A.** One `message_invalid { field }`, naming the missing or ill-typed field. It is used by Python's four
  sites and C#'s three.
- **B.** Join both per-message codes and leave C# codeless.

**Recommended: A.** The per-message suffix repeats the request's own schemaVersion, and C# gains a code for
free.

### Q7. Does `simulation_observations_disabled` become `unsupported_schema_version`?

Python sends it when the observation hub is off (`server.py:593-598`), with
`details { schemaVersion: "cultnet.simulation_observation.v0" }`, which is exactly the
`unsupported_schema_version` shape. cultmesh-ts's "no cache for snapshot requests" and "no operation
handler" refusals (`index.ts:5875`, `5913`) mean the same thing: this server does not serve that message.

- **A.** Collapse. `unsupported_schema_version` means "this server does not serve messages of this
  schemaVersion: unknown, a newer version, or not enabled here". The three TS sites and Huginn's "answers
  only X, Y, Z" adopt it.
- **B.** Keep a separate `feature_disabled`-style code.

**Recommended: A.** A peer cannot act differently on "not enabled" and "not known". Python's
`verify.py:335` check follows the rename.

### Q8. Is this a correction of `cultnet.error.v0` in place, or a new `cultnet.error.v1`?

Under Q2-B and Q3-A, strict v0 readers (TS Ajv and vendored copies) refuse the new bytes. Tolerant readers
(C#, Rust, Python) keep working, because the probes show they ignore unknown keys and treat a missing key
as null.

- **A. In place.** The v0 contract has never been decodable across all five runtimes (section 0). No
  consumer outside CultLib reads code or details (finding 10). Every publishing package takes the semver
  bump its policy requires:
  - `org.gamecult.cultlib` major, because the public `CultNetErrorDetails` properties change;
  - `cultnet-py` and `cultmesh-py` major, because emitted codes change;
  - Rust pins move deliberately.

  StreamPixels and Heimdall re-vendor.
- **B. v1.** Add `cultnet.error.v1`, keep v0 emit for peers that did not advertise v1 in hello, and run
  two error encoders in every runtime until v0 retires.

**Recommended: A.** B builds a version-negotiation path for a message nobody outside CultLib parses beyond
`.error`. The one thing every old reader does need, the `error` text, survives A in every tolerant runtime.

## 4. The registry (as recommended; rewrite on each ruling)

| Code | Meaning | Details (per Q2-B; `?` = present-as-nil when absent) | Emitted by after this map |
|---|---|---|---|
| `selection_invalid` | A selection field failed the door. | `{ field, value? }` | C#, Rust (selection) |
| `cursor_stale` | The cursor predates the server's retained history. | `{ asOf, current }` | C#, Rust |
| `cursor_invalid` | The cursor could not be parsed. | none (details nil) | C#, Rust |
| `reference_outside_target` | A hop reference left the declared target. | none | C#, Rust |
| `variant_unsupported` | The selection selects a document variant CultNet does not carry. | `{ subscriptionId? }` | C# (incl. `DatabaseServer.cs:560`) |
| `unowned_schema` | This server stores no document for that schema (and key). | `{ schemaId }` | C#, Python (Q5) |
| `unsupported_schema_version` | This server does not serve messages of this schemaVersion. | `{ schemaVersion }` | Python, TS, Huginn (Q7) |
| `message_invalid` | The request lacks a required field or has an ill-typed one. | `{ field }` | C#, Python (Q6) |
| `snapshot_document_limit_exceeded` | The snapshot answer would exceed the document bound. | `{ documentCount, maxSnapshotDocuments }` | Python, Rust (Q4) |
| `snapshot_byte_limit_exceeded` | The snapshot answer would exceed the byte bound. | `{ responseBytes, maxSnapshotBytes }` | Python, Rust (Q4) |
| `document_unservable` | A put was refused because no snapshot could ever return the stored document under the byte bound. | `{ responseBytes, maxSnapshotBytes }` | Python, TS, Rust (Q4) |

A null code is legal and means "unclassified". Examples: an exception message from a failed snapshot
source, an authority refusal carrying `routingHint`, and "not authorized"
(`CultNetDatabaseSubscriptionServer.cs:165`). This map does not invent codes for those.

`snapshot_byte_limit_exceeded` and `document_unservable` have identical details. That is deliberate, and it
is the fixture trap named in the verification rules.

---

## 5. Cuts

All cuts are made on one campaign branch, `hands/error-contract`, from `main` after
`hands/cultmesh-put-serve-bound` merges. The branch merges to `main` once, after Cut 5's Soul pass. The
reason: Cut 1 changes C# bytes, and Rust's static hex cases would stay green against the stale bytes
(finding: `tests/error_message.rs` pins constants, not the reference). A partial merge would ship a
wire split while every suite passed. Each cut gets its own worktree, created by Self. Heavy verification
runs through `ygg-verify.sh`, never on Starfire.

### Cut 0. Unblock the interop lane

- **Repo/branch:** CultLib, `hands/interop-guard` from `main`, merged alone ahead of the campaign. It
  does not depend on the rulings.
- **Deletes first:** nothing.
- **Per-file changes:** `.github/workflows/cultnet-interop.yml:72`. The `$stray` filter also accepts
  `^packages/cultnet-rs/contracts/cultnet/`, with a comment naming R-AB and
  `packages/cultnet-rs/tests/schema_discovery_contracts.rs:74-99` as the equality guard. The namespace
  check is unchanged.
- **Authority map:** no ownership change. The canonical owner stays `contracts/cultnet/`, and the Rust
  mirror is derived from it, with drift judged by the Rust test.
- **Verification:**
  - Push the branch and run the workflow. Report every step that goes red after the guard. None of those
    steps has run on main for 40 runs, so expect more red, and treat each failure as its own finding;
    do not fix it in this cut.
  - Negative: edit one byte of a vendored copy locally and check that the Rust equality test fails.
    Reach it on Yggdrasil with `cd packages/cultnet-rs && cargo test --test schema_discovery_contracts`.
- **Ledger:** +3 lines.

### Cut 1. The contract and the C# reference

- **Repo/branch:** CultLib `hands/error-contract`, worktree named by Self.
- **First:** capture the current C# bytes for all eight factory cases, plus `cs_codeless` and `cs_routing`,
  into the Hands report. This is the before-image a Soul pass compares against.
- **Deletes first:**
  - `CultNetErrorDetails` (`src/GameCult.Networking/CultNetSchemaMessages.cs:588-603`, 16 lines) as a
    shared record.
  - The `field`/`value`/`asOf`/`current` property block and the `[MessagePackObject]`/`[Key]` attributes
    on `CultNetErrorMessage` (`:497-521`). Its serialization moves to one formatter.
  - `SoulP4dProbeTests.Wire_UnownedSchemaBytes` (`tests/GameCult.Networking.Tests/SoulP4dProbeTests.cs:191-199`).
    It is an `[Explicit]` hex printer, superseded by the vector writer.
  - The closed-enum paragraph at `contracts/cultnet/cultnet-distributed-database.md:107-115` and
    the `code`/`details` descriptions in the schema (`:28-42`).
- **Adds:**
  - **Schema** (`contracts/cultnet/cultnet.error.schema.json`, rewritten; the Rust mirror is re-copied
    byte-equal):
    - Top level: `schemaVersion`, `error` and `routingHint` as today, plus `messageId: string|null` (Q3)
      and `code`. All five are required, present-as-nil (R-X is unchanged at the top level).
    - `$defs/knownCode` is the registry enum from section 4.
    - `code` is `null`, or a string matching `^[a-z][a-z0-9_]*$` (Q1-B).
    - `details` is decided by a top-level `oneOf`, one branch per known code, each fixing `code` by
      `const` and `details` by exact `properties`/`required`/`additionalProperties:false`. Two more
      branches: a null code with null details, and an unknown code (`not: {$ref knownCode}`) with details
      `object|null`, open.
    - Use `oneOf`/`not`/`const` only. `tests/GameCult.Networking.Tests/MiniJsonSchemaValidator.cs:30-35`
      implements those and **not** `if`/`then`/`allOf`, and it refuses unimplemented keywords. TS
      compiles with Ajv2020 (`packages/cultnet-ts/src/contracts.ts:1,674`), which handles both.
  - **C# model** (`GameCult.Networking`):
    - `CultNetErrorMessage { SchemaVersion, Error, MessageId?, RoutingHint?, Refusal? }`, where
      `Code => Refusal?.Code` is derived and not settable.
    - `abstract CultNetRefusal { string Code }`, with one sealed subclass per registry code carrying its
      typed details (`CultNetCursorStaleRefusal(ulong AsOf, ulong Current)` and so on), plus
      `CultNetUnknownRefusal(string Code, IReadOnlyDictionary<string, object?>? Details)` for Q1-B.
    - `CultNetErrorCodes`: string constants, one per registry code.
    - `CultNetErrorMessageFormatter : IMessagePackFormatter<CultNetErrorMessage>`. It writes the five
      top-level keys in order (schemaVersion, error, messageId, routingHint, code, details), and details
      as exactly the refusal's keys. It reads by key name, tolerates unknown keys at both levels, and
      maps an unknown code to `CultNetUnknownRefusal`. Register it through `[MessagePackFormatter]` on
      the type, the way `CultMeshRouteRecordFormatter` is wired.
    - Factories: keep `ForSelectionInvalid`, `ForCursor`, `ForReferenceOutsideTarget`,
      `ForVariantUnsupported` and `ForUnownedSchema` as thin constructors over the refusal types, and add
      one per new code. Each takes an optional `messageId`.
    - `CultNetPeerErrorException : IOException`, carrying the `CultNetErrorMessage`.
  - **Vector writer:** `ErrorVectorTests.WriteErrorVectors`, gated on `CULTNET_WRITE_VECTORS=1` like
    `SelectionParityVectorTests.cs:460-552`. It writes `contracts/cultnet/interop/error-vectors.cs-written.json`:
    one entry per registry code (both optional-key states where a key is optional), `cs_codeless`,
    `cs_routing`, one unknown code with open details, and one error with `messageId` set. Each entry
    holds `{ name, messagePackBase64, expected: { error, messageId, code, details, routingHint } }`,
    where `expected` is a JSON projection every runtime compares its typed decode against.
- **Per-file changes** (`8fc74c70`):
  - `CultNetDatabaseServer.cs:560`: `ForVariantUnsupported(refusal.Message, subscriptionId)`.
  - `CultNetDatabaseServer.cs:373`, `:415` and `CultNetDatabaseSubscriptionServer.cs:148` become
    `message_invalid { field: "subscriptionId" }` (Q6-A).
  - Every `SendCultNet(CultNetErrorMessage...)` that answers a request passes the request's `MessageId`.
  - Each receiver listed in finding 6 throws `new CultNetPeerErrorException(error)` instead of
    wrapping `error.Error`. `CultMeshContentSessions.OnError` (`:256-260`) fails only the pending entry
    whose id matches `error.MessageId`, and falls back to all entries only when the id is nil.
  - `CultNetSchemaContractTests.cs:165-214`: the per-factory tests are replaced by one test that validates
    every vector's decoded JSON against the schema with `MiniJsonSchemaValidator`, and one test that
    serializes each registry factory and asserts byte equality with its committed vector.
  - Rewrite `contracts/cultnet/cultnet-distributed-database.md` "Error Codes" to the registry, and state
    the Q1 and Q2 rules.
- **Authority map:**
  - Owner: the schema's registry owns code meaning and detail shape. `CultNetErrorMessageFormatter` owns
    C# bytes. The refusal subclass owns the code-to-details binding.
  - Inputs: the refusal instance, messageId, routingHint.
  - Outputs: bytes, and `cs-written.json` for the other runtimes.
  - Derived state: `CultNetErrorMessage.Code` becomes derived; the refusal decides it.
  - Forbidden writers: any `Code = "..."` assignment (it cannot compile); `[Key]` attribute serialization
    of the message; hand-typed hex anywhere.
  - Shared paths: every server send uses the factories; every receiver uses `CultNetPeerErrorException`.
  - Deletion line: `CultNetErrorDetails` and the settable `Code`.
- **Verification** (`dotnet` image):
  - Builds: `tests/GameCult.Networking.Tests`, `tests/GameCult.Mesh.Tests`.
  - `ErrorVectorTests` with `CULTNET_WRITE_VECTORS=1`, then without, and check that the committed file
    reproduces byte-for-byte.
  - Tests and the rules they pin:
    - vector round trip: bytes, decode, bytes are equal for every entry;
    - a registry factory is byte-equal to its vector;
    - every vector validates against the schema;
    - an unknown code decodes to `CultNetUnknownRefusal` with its raw details intact;
    - a Python-shaped error (`py_snapshot_byte_limit` above, with the Q2/Q3 shape) decodes typed with
      `responseBytes` and `maxSnapshotBytes` intact. Today's C# silently empties those.
    - content-session correlation: two pending requests, an error for the second, and the first still
      completes.
  - Mutation: Stryker.NET on the formatter and factories. The survivors that must die:
    - swapping two known codes whose details are identical (`snapshot_byte_limit_exceeded` and
      `document_unservable`);
    - padding a nil key into details;
    - dropping `messageId` on encode;
    - mapping an unknown code to null.
  - Negative greps (checked at HEAD, where they hit the sites being cut):
    - `git grep -nE 'new (IOException|InvalidOperationException)\(error\.Error\)' -- src` must return 0
      (9 hits at HEAD).
    - `git grep -nE 'Field = "(schemaId|subscriptionId)"' -- src` must return 0 (2 at HEAD).
  - Operator: none.
- **Ledger:** about -60 C# (the details record, the settable code, the probe printer, and the per-factory
  schema tests), +250 (the formatter, the refusal types, the vector writer). The schema grows by about 90
  JSON lines. The net is positive, bought by Q1/Q2 typing in the reference.

### Cut 2. Rust: cultnet-rs and cultmesh-rs

- **Deletes first:**
  - `CultNetErrorCode` and `CultNetErrorDetails` as separate types (`packages/cultnet-rs/src/contracts.rs:252-310`,
    59 lines);
  - `require_error_details` (`:1121-1146`);
  - `encode_error_details` (`:1339-1366`);
  - the eight hex `Case`s and the header comment in `packages/cultnet-rs/tests/error_message.rs:1-160`.
    That file is rewritten to read the vector file.
  - Keep `require_legacy_optional_u64` (`:1149`). Check with `git grep` for other callers first; at HEAD,
    only the error details use it.
- **Adds:**
  - `CultNetMessage::Error { error: String, message_id: Option<String>, routing_hint: Option<CultNetShardRoutingHint>, refusal: Option<CultNetRefusal> }`.
  - `enum CultNetRefusal`, with one variant per registry code and its typed fields, plus
    `Other { code: String, details: Option<rmpv::Value> }`.
  - `CultNetRefusal::code(&self) -> &str`.
  - A constructor `CultNetMessage::error(text)` and builder-style `with_refusal` / `with_message_id`, so
    consumers stop writing struct literals. Huginn has three.
  - Decode reads every top-level key by name and routes unknown codes to `Other` (Q1-B).
  - Encode writes the Cut 1 key order.
  - `CultNetShardRoutingHint` is typed, reusing the crate's existing shard descriptor type.
- **Per-file changes:**
  - `packages/cultnet-rs/src/contracts.rs:361-368` (variant), `:1097-1115` (decode), `:1300-1336` (encode).
  - `packages/cultnet-rs/src/selection.rs:1210-1255`: the four refusals build `CultNetRefusal` variants.
  - `packages/cultnet-rs/src/content.rs:316`: uses `CultNetMessage::error`.
  - `packages/cultmesh-rs/src/lib.rs:812-827`: a peer error becomes a typed error carrying the refusal,
    instead of `anyhow!` text. Name it `CultMeshPeerError`, next to the crate's existing error types.
  - `packages/cultmesh-rs/src/rudp_document_server.rs` (Q4-A): each `CultMeshRudpRejectionReason` that
    can be answered sends the matching coded error, correlated by the request's message id, before
    returning `ApplicationRejected`. `PayloadBudgetFull` and `ResponseQueueFailed` stay local.
- **Authority map:**
  - Owner: `CultNetRefusal` owns code and details. The schema owns the registry.
  - Forbidden writers: string codes (`from_wire_str` survives only inside decode); struct literals of
    details.
  - Shared paths: selection refusals, document-server refusals and content errors all go through the one
    encoder.
- **Verification** (`rust` image, `cd packages/cultnet-rs` then `cd ../cultmesh-rs`):
  - `cargo test --workspace`.
  - `tests/error_message.rs` pins: every vector decodes to `expected`; encoding the decoded value
    reproduces the vector's bytes; the unknown-code vector decodes to `Other`; the routing vector keeps
    `routing_hint`.
  - cultmesh-rs: a put over the bound makes the peer receive `document_unservable` with the request's
    message id, and details equal to the computed size and the bound. Run it at two bounds so that a
    constant or an offset in `maxSnapshotBytes` dies.
  - A peer that sends a Python-shaped `snapshot_byte_limit_exceeded` produces a `CultMeshPeerError`, not
    a decode failure.
  - Mutation: `cargo mutants --in-diff` on the cut's diff. Triage every survivor by name.
  - Negative: `git grep -n 'CultNetErrorDetails' -- packages` must return 0.
- **Consumer note:** Huginn's `serve.rs:288/307/312` and its tests stop compiling at the next pin bump.
  The migration is mechanical (`CultNetMessage::error(...)`), and `:312` adopts `unsupported_schema_version`
  (Q7). Self routes it as a Huginn change; it is not part of this branch.

### Cut 3. TypeScript: cultnet-ts, cultmesh-ts and cultmesh-browser

- **Deletes first:**
  - the two-field `CultNetErrorMessage` interface (`packages/cultnet-ts/src/contracts.ts:167-170`);
  - the four literal error constructions in `packages/cultmesh-ts/src/index.ts:5875-5878`, `5885-5888`,
    `5913-5916` and `5922-5925`, and the branch's codeless unservable site (`0eb7434a`);
  - `rejectCorrelated`'s single-pending heuristic (`packages/cultmesh-browser/src/index.ts:724-731`).
- **Adds:**
  - `CultNetErrorMessage` with every field.
  - `type CultNetRefusal` as a discriminated union on `code`, with an `{ code: string; details: Record<string, unknown> | null; known: false }` arm.
  - `createCultNetError(text, { messageId, refusal })` as the only constructor.
  - `class CultNetPeerError extends Error { message: CultNetErrorMessage }`.
  - `parseCultNetMessage` keeps Ajv validation against the canonical schema; it now accepts every schema
    branch, including unknown codes.
- **Per-file changes:**
  - cultmesh-ts: the three "does not serve this" sites (`5875`, `5913`, `5922`) send
    `unsupported_schema_version` (Q7-A); the snapshot catch (`5885`) sends a null code; the unservable site
    sends `document_unservable` with details.
  - cultmesh-ts `6527-6529` throws `CultNetPeerError`.
  - cultmesh-browser `163`, `584` and `690`: correlate by `messageId`, falling back to today's behavior only
    for a nil id.
  - `packages/cultnet-ts/src/idunn-odin-presence-publisher.ts:118-120` throws `CultNetPeerError`.
  - The legacy `gamecult.networking.v0` path (`contracts.ts:840-844`, `:899-900`) is unchanged. That is a
    separate legacy contract.
- **Verification** (`kotlin` image, which carries Node 24; `npm install`, then build cultcache-ts before
  cultnet-ts):
  - A new `test/error-vectors.test.ts` pins that every vector parses and deep-equals `expected`, and that
    `encode(createCultNetError(...))` is byte-equal to each registry vector.
  - The cultmesh-ts document-server tests assert that each refusal parses with TypeScript's own
    `parseCultNetMessage`. That is the probe's `ts_two_key` failure, pinned.
  - Browser: two pending operations, an error for the second, and the first still resolves.
  - Mutation: StrykerJS on the diff.
  - Negative: `git grep -nE 'schemaVersion: "cultnet\.error\.v0",' -- packages/cultmesh-ts/src packages/cultmesh-browser/src`
    must return 0. Exclude `cultnet-ts/src/schema-discovery.ts:302` (a descriptor) and
    `contracts.ts:842` (legacy decode); both are legitimate hits for the unscoped pattern.

### Cut 4. Python: cultnet-py and cultmesh-py

- **Deletes first:**
  - `_error_response` (`packages/cultmesh-py/src/cultmesh_py/server.py:573-589`);
  - `unsupported_schema_version_error` (`packages/cultnet-py/src/cultnet_py/interop_peer.py:409-417`);
  - every string literal code in `server.py` (lines in finding 3).
- **Adds:**
  - `cultnet_py/errors.py`, holding:
    - `CultNetErrorCode(StrEnum)`, the registry;
    - a frozen `CultNetError` dataclass (`error`, `message_id`, `routing_hint`, `code: CultNetErrorCode | str | None`,
      `details: Mapping | None`);
    - one constructor function per code, taking exactly that code's keys;
    - `to_wire()`, which writes the five top-level keys in the Cut 1 order, nil included;
    - `parse_cultnet_error(dict)`.
  - `CultNetPeerError` (`client.py:46-50`) keeps `.response` and gains `.error: CultNetError`.
- **Per-file changes:**
  - Sites `server.py:158-163`, `355-394`, `555-569` and `593-598` and the branch's `document_unservable`
    site use the constructors, with the Q5-A/Q6-A/Q7-A codes.
  - `interop_peer.py` uses the constructor.
  - `verify.py:335` reads `error.error.code`.
  - Tests `packages/cultmesh-py/tests/test_cultmesh.py:2088`, `3791`, `4323` and `4363-4371`, and
    `packages/cultnet-py/tests/test_cultnet.py:2342`, follow the renames.
- **Verification** (the image the campaign already uses for Python; state it in the report):
  - A vector test pins that every vector parses to `expected` and that each constructor's `msgpack.packb`
    is byte-equal to its vector.
  - Mutation: mutmut on `errors.py` and the changed server sites.
  - Negative: `git grep -nE '"schemaVersion": "cultnet\.error\.v0"' -- packages/cultnet-py/src packages/cultmesh-py/src`
    returns only `errors.py`.
- **Not here:** F-1 (`schema_catalog.py` publishes its own shapes).

### Cut 5. Kotlin: cultmesh-kotlin

- **Adds:**
  - A `CultNetErrorMessage` data class and a `sealed class CultNetRefusal` (known codes plus `Other`),
    with a `CultNetMessage.asError(): CultNetErrorMessage?` view.
  - `class CultNetPeerException(val error: CultNetErrorMessage) : IOException`.
  - At `CultMesh.kt:1278` and `:1334`, and at every `receiveSchemaMessage` consumer that expects a
    response, an error response throws `CultNetPeerException` before the `require`.
- **Verification** (`kotlin` image, `pwsh -File build.ps1`):
  - A vector test pins that every vector decodes to `expected`.
  - Kotlin emits no errors today, so encode parity applies only if a Kotlin server path is added. Say
    so in the report; do not invent one.
  - There is no mutation tool. Behavioural tests cover it: an error reply to a schema-catalog request
    raises `CultNetPeerException` carrying the code.

### Cut 6. Merge, release, consumers

- Soul runs over the whole branch before the merge:
  - rerun every vector suite;
  - decode each runtime's live emissions with every other runtime. The interop lane from Cut 0 is the
    harness; add an error round to `tests/GameCult.Networking.InteropPeer` and
    `packages/cultnet-ts/test/interop` if Soul finds a runtime pair unreached.
- Release per `docs/semver-policy.md`, with a Soul pass before tags:
  - `org.gamecult.cultlib` major, and the tracked Unity DLLs rebuilt, since they embed the schema;
  - `cultnet-py` and `cultmesh-py` major.
- Consumers, routed by Self as separate repo changes:
  - Huginn: pin bump plus Cut 2's note.
  - StreamPixels and Heimdall: re-vendor. Heimdall is already broken by R-X.
  - AetheriaEve, Gjallar and Mimir compile against the live CultLib path and read only `.Error`, so they
    need no change. That is inferred from the audit; the next build of each confirms it.
  - Hermodr: none.

## 6. Subtraction ledger

| Cut | Removed (estimate) | Added (estimate) | Formats / targets |
|---|---|---|---|
| 0 | 0 | 3 | none |
| 1 | ~60 C#, schema text | ~250 C#, ~90 schema | + `error-vectors.cs-written.json`; - `[Explicit]` hex printer |
| 2 | ~150 (types, two helpers, 160-line hex test) | ~200 | hex constants gone; vectors read from file |
| 3 | ~40 | ~120 | none |
| 4 | ~40 | ~120 (`errors.py`) | 8 ad-hoc codes → registry |
| 5 | 0 | ~80 | none |

The net is positive, by roughly 500 lines, across five runtimes. It buys typed decode where there was
none (Kotlin, TS and Python), a correlation capability, and one vector file in place of hand-copied hex.
The pressure point: if Cut 1's formatter passes 150 lines, report it rather than trimming typing.

## 7. Follow-ups outside this map

- **F-1.** `packages/cultnet-py/src/cultnet_py/schema_catalog.py` publishes about 350 lines of hand-built
  schemas under canonical `$id`s. Python should package and serve `contracts/cultnet/*.schema.json`, the
  way Rust vendors them. It is a separate cut, because it covers every message, not errors. It can wait
  because the catalog is advisory: no runtime validates a peer's message against a published descriptor.
- **F-2.** Codes for the remaining codeless refusals (authorization, failed snapshot sources, routing).
  Wait for a consumer that branches on them.
- **F-3.** Whatever the Cut 0 lane reveals once its later steps run again.
