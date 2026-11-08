# CultNet write receipt (specification)

Status: specification. No runtime implements it yet; the conformance follow-ups below do.
Rules: Heimdall's ruling of 2026-11-08 (a write completes on its result frame; an ack or a return is not admission).
Files here: `cultnet.write-admitted.schema.json`, `cultnet.error.v1.schema.json`, `write-receipt-vectors.json`. They move to `contracts/cultnet/` when the first runtime implements them.

## The problem

A put or delete today has two answers on the wire: nothing (admitted, or the server never read it) and `cultnet.error.v0` (refused, with no id saying which write). A transport ack proves delivery. Clients that completed on the ack, or on a window with no refusal, reported refused writes as done (CultLib #55 to #58 bound this with a result window; this spec replaces the window with an answer).

## Frames

**`cultnet.write_admitted.v0`**: `{ schemaVersion, messageId, operation: "put"|"delete", schemaId, recordKey }`, every key written, none nil. Sent once a put or delete is admitted. The id echoes the write's `messageId` byte for byte; `operation`, `schemaId`, `recordKey` echo the write.

**`cultnet.error.v1`**: `cultnet.error.v0` plus a required `messageId` (string or nil). The code enum is v0's. Sent in place of `error.v0` to a receipt-capable connection. `messageId` is the id of the request the error answers; nil only when the frame was unreadable or carried no id.

Why a new error version and not an extra key on v0: the v0 schema is closed (`additionalProperties: false`) and the TypeScript decoder (Ajv over the closed schemas) rejects unknown keys and unknown `schemaVersion`s, and `parseCultNetMessage` throws on an unknown version; Rust's decoder errors on an unknown version too. Unknown-key tolerance of the Rust, Python and Kotlin error decoders was not checked, so none is assumed, so a surprise key or frame breaks an old client's whole receive path. Old clients are therefore never sent either new frame. This reverses the "old clients ignore it" assumption, which is false for today's decoders.

## Capability: opting in

A connection is receipt-capable when its `cultnet.hello.v0` (sent before its first write; the schema channel is reliable and ordered) lists both `cultnet.write_admitted.v0` and `cultnet.error.v1` in `supportedMessageVersions`. A server MUST NOT send either new frame to any other connection, and answers it exactly as before (no receipt, `error.v0`). A receipt-capable server lists both in its own hello, so a client can tell a legacy server from a receipt server that has not answered yet. A client that sends no hello gets legacy behaviour.

**The capability changes framing only; it never authorizes.** A hello authenticates nothing and grants nothing: who may write, and which keys, is decided by the server from the transport-authenticated actor exactly as without the capability. Declaring the capability never makes a refused write admitted, never widens an actor's authority, and never changes what a write does; it selects only which frame the answer arrives in.

## Server rules

1. **One answer.** For every put or delete on a receipt-capable connection the server sends exactly one of `write_admitted` or `error.v1`, carrying that write's `messageId`. Never both, never two different answers.
2. **After the commit point.** `write_admitted` is sent only after the write is committed by the same rule that makes it visible to readers. Receipt of the bytes, parse, or authorisation alone is not admission.
3. **Id on every refusal.** Every refusal of a write, whatever its code (`write_not_authorized`, `message_invalid`, `unowned_schema`, `unsupported_schema_version`, shard authority errors, ...), is an `error.v1` whose `messageId` is the write's id. When the frame is readable enough to find the id, it is found; `message_invalid` for a missing or ill-typed field still carries the id. Nil only when no id can be read.
4. **Batches.** There is no batch frame; a batch is N writes, each with its own `messageId`, each answered under rules 1 to 3. A failed batch names its member by that id. A server may not collapse a batch into one answer.
5. **Identity and idempotent resend.** A `messageId` is unique per authenticated actor and session: the client mints it, never reuses it for different content in that session, and a server never accepts the id of one actor as the id of another. The server's dedupe key is `(actor, messageId)`, where *actor* is the transport-authenticated identity, never a name the hello or the frame claims. Alongside the key it keeps the **content hash** of the write and the answer it gave. The content hash is SHA-256 of the MessagePack array `[operation, schemaId, recordKey, payload]`, where `payload` is the raw document payload as a MessagePack `bin` for a put and nil for a delete (the vectors give worked hashes). **Scope: at least the session.** The key lives for the life of the session it was made in, and a server may bound how many writes it remembers per session (a session at the bound refuses new writes with an `error.v1` carrying the new write's id, and keeps answering the ids it remembers; it never forgets one it promised to answer). A server MAY keep the key longer for the same actor, up to `DEDUPE_RETENTION_MS` (10 minutes), but a client MUST NOT rely on it: **a reconnect is a new session, and a resend on the new session is a new write.**
   - Same key, same content hash, answer was `write_admitted`: answer `write_admitted` again with the same body. The write is not applied twice.
   - Same key, same content hash, answer was a refusal: the same refusal again.
   - **Same key, different content hash: refuse `message_invalid` (details `field: "messageId"`) and never re-send the old receipt.** Old receipts are for the old content; showing one for new content would report an unapplied write as admitted. The original write's state is unchanged.
   - Past the session (or a server's longer retention, if it keeps one) a resend is a new write.
6. **Forwarded writes.** A server that forwards a non-primary write answers the original peer only with what the primary answered: the primary's receipt becomes `write_admitted` with the original id, its refusal becomes `error.v1` with the original id. If the forward gets no answer the server relays nothing: no receipt, no error, no synthesized refusal. The client's own rule below then reports the write as unconfirmed. A forwarder never turns an absent or ambiguous primary answer into an admission.
7. **Looks alike.** An admitted and a refused write differ in `schemaVersion`; a receipt has no `error`, an `error.v1` has no admission. A transport ack is neither.

## Client rules: three outcomes

A write settles as exactly one of:

- **admitted**: a `write_admitted` whose `messageId` is the write's.
- **refused**: an `error.v1` whose `messageId` is the write's, carrying its `code` and `details` as the typed error.
- **unconfirmed**: nothing settled it. Reasons: `window_elapsed` (the result window ran from the *send* and passed with no answer; see Result window) and `session_ended` (the session ended first). Unconfirmed is not success and not refusal. APIs that return a value return the tri-state; APIs that throw throw a distinct refused error and a distinct unconfirmed error and return normally only for admitted. A caller that needs admission treats unconfirmed as not admitted. Resend with the same `messageId` and unchanged content, **on the same session**, is safe against a receipt-capable server (rule 5). After a reconnect the resend is a new write, and a legacy server may apply a resend twice: the caller resends only writes that are idempotent by record key (a put of the same content to the same key, a delete of a key), and reads the record back before resending any other.

**Result window.** Two named constants (also in the vectors): `RECEIPT_RESULT_WINDOW_MS = 5000`, the default against a receipt-capable server (its hello lists both frames); `LEGACY_RESULT_WINDOW_MS = 500`, used only as the fallback against a server whose hello does not list them (or that sent none). The window runs from the send. A receipt-capable server that has not answered within its window is reported as unconfirmed like any other; clients log it as a server that missed its window.

**Late answers.** An answer for a write that already settled `unconfirmed` does not change what the caller was told, but the client records it on the write: a late `write_admitted` makes the write's recorded state `admitted_late`, a late refusal makes it `refused_late`. The write handle exposes that state, and a caller that got `unconfirmed` MUST consult it (and the record) before resending: it must not blind-resend a write that was admitted late. A resend on the same session with the same id and content is harmless on a receipt-capable server (rule 5); across a reconnect the caller follows the idempotence rule above.

**Legacy answers.** An `error.v0`, or an `error.v1` with nil `messageId`, names no write. It is attributed to a write only when exactly one write on the connection is unanswered; it then refuses that write. With two or more unanswered writes it settles none of them and each ends unconfirmed at its window. A frame naming a `messageId` that is not in flight settles nothing.

**Batches can commit partially.** A batch is N independent writes with no atomicity: some members may be admitted while others are refused or unanswered, and nothing is rolled back. The client reports each member's outcome and a batch result is only their set: admitted if every member is admitted, refused if any member is refused (naming the members), otherwise unconfirmed (naming the unanswered members). The caller owns retry: it resends exactly the members that are not admitted, with their original ids.

## Changes to what #55 to #58 shipped

Those clients complete *successfully* when the window ends with no refusal. Under this spec that case is `unconfirmed`. The conformance work changes it; until then the window-success is the interim, and callers must not read it as admission.

## Vectors

`write-receipt-vectors.json`: `constants` (the named windows and retention), `contentHash` (worked dedupe hashes), `dedupe` (server replay cases for rule 5), `wire` (MessagePack hex plus the decoded message for each new frame; the bytes are spec-authored with msgpack-python and every runtime's first conformance PR re-captures them with its own encoder and must match), `schemaInvalid` (frames that must be refused by a validator, each with why), and `scenarios` (event replays with the outcome each write must settle as; the clock is abstract; each case names its window constant). A conformant client passes every scenario; a conformant server's answers for the same inputs satisfy server rules 1 to 7.

## Conformance follow-ups (not in this PR)

C# server and client, Rust, TypeScript, Python, Kotlin: emit and parse the two frames; hello capability; tri-state client outcome; replace the #55 to #58 window-success; re-capture the wire vectors in each runtime. Server write paths include the shard write forwarder (rule 6) and the Odin catalog.
