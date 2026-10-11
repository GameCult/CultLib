# TS document-payload decode owner: making the rule structural

Research for `cultlib-gaps:cut_spec:cut-union-codec-ts.r2` and
`cultlib-gaps:question:ts-payload-opaque-type`. Body: branch `hands/union-codec-ts` at
`294469e3`. Gathered 2026-10-11 by Imagination (`imagination-cl-decode-owner-ts`).

## The rule and why a guard does not hold it

The rule: one owner decodes a CultCache document payload, and it always has the
definition. Five Soul passes on `cut-union-codec-ts` each found another payload reader
outside the owner (s3 mesh readers, s4 RUDP put and browser, s5 provider command and the
browser default path). The s5 guard (`payload-decode-owner.test.ts`, one per mesh package)
bans the `@msgpack/msgpack` specifier. Soul's mutant S1 put `decodeWireValue(document.payload)`
at the RUDP put site and the guard passed: a plain decode under another name is not a
specifier. As long as a payload is typed `Uint8Array`, every byte decoder in the program
accepts it, so the rule can only be policed by text.

## What the type system can enforce (probes)

Run with TypeScript 6.0.3 (`F:\Projects\Heimdall\node_modules\typescript`) against
`@msgpack/msgpack` 3.1.3 source, `strict`. CultLib pins TypeScript `^5.9`; the rules used
below (structural assignability, intersection widening) are unchanged between them, and
Hands re-confirms on 5.9 with the compile-fail fixture the spec names. Scratch files:
`scratchpad/brand/probe.ts`, `probe2.ts`, `probe3.ts`, `runtime.cjs`, `runtime2.cjs` in the
session scratchpad (not committed).

| Shape of `payload` | `decode(p)` | `decodeWireValue(p)` | `const u: Uint8Array = p` | Notes |
|---|---|---|---|---|
| `Uint8Array & { __brand: true }` | compiles | compiles | compiles | Intersection brand leaks: still a Uint8Array. |
| `Uint8Array & { [uniqueSymbol]: true }` | compiles | compiles | compiles | Same leak. |
| unique-symbol brand plus a wire decoder typed `bytes & { [brand]?: never }` | compiles (raw msgpack) | error | compiles | The wire decoder refuses the brand, but `const u: Uint8Array = p` (no cast) or any helper taking `Uint8Array` strips it, and then `decodeWireValue(u)` compiles. Laundered with no greppable escape. |
| class with ES `#bytes` field | error | error | error | Nominal. Forgery by object literal is an error. Escapes: `as any`, `as unknown as`, or a named method. Runtime: `decode(wrapper)` throws `RangeError: Offset is outside the bounds of the DataView`; `encode({ payload: wrapper })` silently writes an empty map (`81 a7 payload 80`), so a JS consumer that re-encodes a record corrupts it. Two installed copies of the package are incompatible at type level and at `instanceof`. |
| opaque object type over the same runtime Uint8Array, keyed by a string property: `interface DocumentPayload { readonly "cultcache.documentPayload": true; readonly byteLength: number }` | error | error | error | `ArrayLike<number>`, `new Uint8Array(p)`, a `Uint8Array` helper param, a generic `T extends ArrayLike<number>` and a destructured field all refuse it. Forgery from bytes is an error. Escapes that compile: a named unwrap function, `as any`, `as unknown as Uint8Array`, a helper with an `unknown` parameter that casts inside. Runtime value is unchanged: frames still encode the bytes, JS consumers still work. Two package copies are structurally compatible (a unique-symbol key would make them incompatible). |

Conclusion: an intersection or unique-symbol brand on `Uint8Array` cannot hold the rule,
because widening to `Uint8Array` is implicit. A class holds it but changes the runtime value
and adds a silent-corruption hazard for JS consumers. The string-keyed opaque object type
holds it at compile time with no runtime change. Every remaining way past it is a cast or a
named call that a grep finds.

## Read sites at 294469e3

Document-payload reads (bytes typed `Uint8Array` that are a document's payload):

| Site | Today |
|---|---|
| `packages/cultcache-ts/src/cult-cache.ts:276-277, 457-458, 604-605` via `#createFormatter` 679-694 | the cache's own reads, through the definition |
| `packages/cultcache-ts/src/document.ts:86-97` | `decodeDocumentPayload(definition | undefined, …)`: the `undefined` door is plain msgpack |
| `packages/cultcache-ts/src/single-file-messagepack-backing-store.ts:331, 352-365` | `inferSchemaVersionFromPayload`: plain decode to recover a missing catalog version |
| `packages/cultcache-ts/src/cult-cache-inspector.ts:289-291` | `previewPayload`: plain decode for inspection |
| `packages/cultnet-ts/src/replication.ts:343-345, 433-440` | `decodeDocumentValue` through `resolveDocumentFormatter` (a second owner-shaped function) |
| `packages/cultmesh-ts/src/index.ts:2641, 6334, 6643, 6682, 6782-6794` | through `decodeDocumentPayload`; the `string` schema overloads pass `undefined` (plain) |
| `packages/cultmesh-ts/src/provider-session-wire.ts:158-171` | `decodeWireValue(document.payload)`: plain, provider command document |
| `packages/cultmesh-browser/src/index.ts:763-776` | `decodeCultNetPayload(record, formatter?)`: plain without a formatter |
| `packages/cultnet-ts/src/idunn-runtime-authority.ts:320-330, 603-605` | hand reader of a CultCache v1 file and fixed slot arrays (never sees a record type) |

`decodeWireValue` (cultnet-ts/wire) source consumers: 7. Legitimate (not a document payload): 5,
namely cultmesh-ts `index.ts:5960` (frame), `provider-session-wire.ts:130` (provider-session
operation payload), `:143` (connect evidence), cultmesh-browser `index.ts:788` (operation
payload), `:826` (frame). Document payloads: 2 (`provider-session-wire.ts:165`, browser
`index.ts:775`). Tests: 4 document-payload reads (`cultmesh.test.ts:3348, 3451`,
`provider-rudp-transport.test.ts:49`, `cultmesh-browser.test.ts:981`), the rest frames and
operation payloads.

Document payloads also arrive as `CultMeshRealtimeFrame.payload`
(`packages/cultmesh-ts/src/realtime-wire.ts:51-59`): cultmesh-ts never decodes it, consumers
do (StreamPixels `overlay-quic-publisher.test.ts:54-60` through the definition's formatter).

## Consumers outside CultLib (rg under F:\Projects, excluding CultLib clones, vendor, node_modules)

Hand payload decodes of CultNet records or store envelopes: weksa
`scripts/weksa-rudp-command.cjs:133, 285` (`decode(message.document.payload)`), Heimdall
`src/runtime-presence.ts:83`, `src/provider-health-identity.ts:53, 172` (builds and decodes a
`CultCacheEnvelope` by hand), StreamPixels `apps/web/lib/server/idunn-runtime-launch.test.ts:37`,
AetheriaEve `scripts/aetheria-browser-provider-witness.ts:55, 62, 161`
(`decodeCultNetPayload` with no formatter). JS daemons that build `document_put_raw` records
with pre-encoded or signed `Uint8Array` payloads and call `encodeCultNetMessageForWire`:
Stonks `src/idunn-rudp.cjs`, Vili `scripts/idunn-rudp.cjs`, VoidBot
`scripts/publish-idunn-rudp-health.cjs`, weksa `scripts/idunn-rudp-health.cjs`, Bifrost
`tools/persona-feedback-idunn-health.mjs`. All consume vendored pins, so a change reaches them
only on a pin bump.

## Test wiring at 294469e3

- cultcache-ts `package.json:26` runs an explicit list (`cult-cache`, `union-parity`);
  `test/decode-document-payload.test.ts` is compiled and never run.
- cultnet-ts, cultmesh-ts and cultmesh-browser run `dist-test/test/*.test.js`;
  cultnet-ts `test/interop/*.test.ts` runs under `test:interop` and the interop workflow.
- No workflow runs cultcache-ts's tests: `.github/workflows/cultmesh-portability.yml:107-119`
  runs cultnet-ts, cultmesh-browser and cultmesh-ts; `cultnet-interop.yml:112` only builds
  cultcache-ts. `scripts/run-typescript-workspaces.mjs test` covers all four, but no workflow
  calls it.

## Prior art: how decoding is kept schema-bound

- **protobuf-ts `Any`**: a message packed into a bytes field plus a `typeUrl`.
  `Any.unpack(any, type: IMessageType<T>): T` needs the message type; `Any.contains(any,
  type)` asks first; JSON reading needs a `typeRegistry`. The bytes stay publicly readable;
  the API is shaped so the typed path is the convenient one, not the only one.
- **avsc (Avro JS)**: decoding is a method of a schema-built type
  (`Type.forSchema(schema).fromBuffer(buf)`); Avro binary carries no schema, so there is no
  schema-less decoder to misuse. The format itself enforces the owner.
- **msgpack-javascript `ExtensionCodec`**: typed decoding of extension types is opt-in per
  call (`decode(bytes, { extensionCodec })`); an unregistered extension decodes to
  `ExtData` (`src/ExtensionCodec.ts:74, 86, 104`) rather than refusing, and plain `decode`
  stays available. It shows the failure mode: the schema-bound path is optional and nothing
  refuses the plain one. Its `DecoderOptions` have no `maxDepth` (encode defaults to 100).
- **serde_json `RawValue`**: an unparsed span kept opaque (`&RawValue`), read as text only by
  the explicit `.get()`, or parsed into a typed value with `from_str`. The closest analogue to
  an opaque payload type with a named escape.

The pattern that holds the rule without trusting callers: the bytes travel as a type no plain
decoder accepts, the typed decoder takes the schema as a required argument, and the escape to
raw bytes is a named call.
