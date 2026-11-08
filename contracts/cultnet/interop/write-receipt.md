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

A connection is receipt-capable when its `cultnet.hello.v0` (sent before its first write; the schema channel is reliable and ordered) lists both `cultnet.write_admitted.v0` and `cultnet.error.v1` in `supportedMessageVersions`. A server MUST NOT send either new frame to any other connection, and answers it exactly as before (no receipt, `error.v0`). A receipt-capable server lists both in its own hello, so a client can tell silence from a legacy server from a receipt server's fault. A client that sends no hello gets legacy behaviour.

## Server rules

1. **One answer.** For every put or delete on a receipt-capable connection the server sends exactly one of `write_admitted` or `error.v1`, carrying that write's `messageId`. Never both, never two different answers.
2. **After the commit point.** `write_admitted` is sent only after the write is committed by the same rule that makes it visible to readers. Receipt of the bytes, parse, or authorisation alone is not admission.
3. **Id on every refusal.** Every refusal of a write, whatever its code (`write_not_authorized`, `message_invalid`, `unowned_schema`, `unsupported_schema_version`, shard authority errors, ...), is an `error.v1` whose `messageId` is the write's id. When the frame is readable enough to find the id, it is found; `message_invalid` for a missing or ill-typed field still carries the id. Nil only when no id can be read.
4. **Batches.** There is no batch frame; a batch is N writes, each with its own `messageId`, each answered under rules 1 to 3. A failed batch names its member by that id. A server may not collapse a batch into one answer.
5. **Idempotent resend.** A resend of the `messageId` of an already admitted write is answered with `write_admitted` again, never with an error. A resend of a refused write is answered with the same refusal.
6. **Forwarded writes.** A server that forwards a non-primary write answers the original peer only with what the primary answered: the primary's receipt becomes `write_admitted` with the original id, its refusal becomes `error.v1` with the original id. If the forward gets no answer the server sends nothing; the client's own rule below settles it as unconfirmed. A forwarder that cannot tell admitted from refused is a fault, never an admission.
7. **Looks alike.** An admitted and a refused write differ in `schemaVersion`; a receipt has no `error`, an `error.v1` has no admission. A transport ack is neither.

## Client rules: three outcomes

A write settles as exactly one of:

- **admitted**: a `write_admitted` whose `messageId` is the write's.
- **refused**: an `error.v1` whose `messageId` is the write's, carrying its `code` and `details` as the typed error.
- **unconfirmed**: nothing settled it. Reasons: `window_elapsed` (the result window ran from the *send*, default 500 ms, passed with no answer) and `session_ended` (the session ended first). Unconfirmed is not success and not refusal. APIs that return a value return the tri-state; APIs that throw throw a distinct refused error and a distinct unconfirmed error and return normally only for admitted. A caller that needs admission treats unconfirmed as not admitted and decides whether to read the record back or resend (resend is safe under server rule 5).

The result window is the *fallback* for a server that predates receipts. Against a receipt-capable server (its hello lists the frames) clients SHOULD wait longer than 500 ms (5 s suggested) and SHOULD report a silent receipt-capable server as a server fault; the outcome is still unconfirmed.

**Legacy answers.** An `error.v0`, or an `error.v1` with nil `messageId`, names no write. It is attributed to a write only when exactly one write on the connection is unanswered; it then refuses that write. With two or more unanswered writes it settles none of them and each ends unconfirmed at its window. Late answers (after a write settled) never rewrite the outcome; log them. A frame naming a `messageId` that is not in flight settles nothing.

A batch is the set of its members' outcomes: admitted only if every member is admitted, refused if any member is refused (naming the members), otherwise unconfirmed (naming the unanswered members).

## Changes to what #55 to #58 shipped

Those clients complete *successfully* when the window ends with no refusal. Under this spec that case is `unconfirmed`. The conformance work changes it; until then the window-success is the interim, and callers must not read it as admission.

## Vectors

`write-receipt-vectors.json`: `wire` (MessagePack hex plus the decoded message for each new frame; the bytes are spec-authored with msgpack-python and every runtime's first conformance PR re-captures them with its own encoder and must match), `schemaInvalid` (frames that must be refused by a validator, each with why), and `scenarios` (event replays with the outcome each write must settle as; the clock is abstract, `windowMs` is 500). A conformant client passes every scenario; a conformant server's answers for the same inputs satisfy server rules 1 to 7.

## Conformance follow-ups (not in this PR)

C# server and client, Rust, TypeScript, Python, Kotlin: emit and parse the two frames; hello capability; tri-state client outcome; replace the #55 to #58 window-success; re-capture the wire vectors in each runtime. Server write paths include the shard write forwarder (rule 6) and the Odin catalog.
