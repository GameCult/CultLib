# TypeScript CultMesh Content Plane: Map

Campaign `cultmesh-ts-content`. Imagination wrote this on 2026-10-19 at CultLib
`8990bd46` (origin/main `a88c629e` plus the target doc). The target is
`docs/cultmesh-ts-content-target.md`. This page holds the body facts, the model
page, the rationale and the cut sequence. Specs and questions live in the
typed mind, not here.

## 1. Body facts

Each fact was probed by running code or read from source at `8990bd46`.
Scratch probes ran under the session scratchpad (`imagination/`), not in the
repo.

| # | Fact | Evidence |
|---|---|---|
| F1 | A C#-packed manifest is a MessagePack **fixarray of 10** (`0x9a`): `[artifactId, kind, version, contentHash, sizeBytes, mimeType, createdAtUtc, chunks[], tags[], metadata{}]`. Each chunk ref is a 4-array `[chunkHash, offset, sizeBytes, recordKey]`. The fixture manifest is 1,111 bytes: 6 chunks (5 x 262,144 + 4,831), content hash `f4ae2a7c…2af1`. The empty body packs to 315 bytes with one zero-size chunk whose hash is `e3b0c442…b855` (the SHA-256 of nothing). | probe: `@msgpack/msgpack` 3.1.3 decode of `contracts/cultmesh/content-vectors.cs-written.json`, kind `manifest` |
| F2 | `@msgpack/msgpack` 3.1.3 re-encodes all four C# manifest vectors **byte-identically**, metadata included (`zeta, alpha, mid` keep insertion order). Integers come out minimal (`4831` is `cd12df`, `262144` is `ce00040000`), as MessagePack-CSharp writes them. | same probe, `encode(decode(bytes))` equals the input for all four |
| F3 | Three decoder hazards for a TS manifest reader. (a) JS objects put integer-like keys first (`{zeta,"2",alpha,"1"}` iterates `1,2,zeta,alpha`), so metadata must be ordered entries, not an object, or a `{"10":…,"2":…}` manifest re-encodes in another order. (b) Plain `decode` takes the last of duplicate map keys silently (`{"a":"2"}`); C# `Dictionary` and Rust `ordered_map` refuse duplicates. `new Decoder({ mapKeyConverter })` can refuse them: probed. (c) Plain `decode` rounds int64 above 2^53 (`…993` reads as `…992`); `useBigInt64: true` keeps it exact. | probe (node) |
| F4 | `@msgpack/msgpack`'s decoder is iterative: a 1,000,000-deep `0x91` input decodes in 160 ms but costs about 158 MB of heap. So nesting does not crash it but amplifies memory about 150 times. `checkPayloadDepth` (the owner of ruling `payload-depth-limit-256`) is not exported from any `@gamecult/cultcache-ts` entry: `document.ts:108` exports only the error and the constant. | probe (node); source `packages/cultcache-ts/src/document.ts:2,108,123` |
| F5 | The C# HTTPS connector sends `GET <endpoint>?chunkHash=<hex>&recordKey=<escaped>`, **without** a `sha256:` prefix, because `BuildChunkUri` runs `NormalizeHash` first. Probed URIs: `https://ygg.example/content?chunkHash=5993ac…&recordKey=mesh%3Acdn%3Achunk%3A5993ac…`. An existing endpoint query is kept and joined with `&` (`?tenant=sp&chunkHash=…`). A null record key sends `recordKey=` (empty). Other characters use `Uri.EscapeDataString`, so on the wire `k with space/ü&=?#` becomes `k%20with%20space%2F%C3%BC%26%3D%3F%23`. | probe: `BuildChunkUri` lines copied into a net10.0 console (`CultMeshHttpsContentTransport.cs:114-122`) |
| F6 | The connector calls `EnsureSuccessStatusCode` and fails on any status outside 2xx. When `Content-Length` is present and is not the chunk's `SizeBytes`, it throws before reading. With no `Content-Length` it copies the stream without checking the length. Verification belongs to the caller's transfer owner. `CanConnect` admits only `https`. The constructor takes a `Func<HttpMessageHandler>`, so a test can pin a self-signed certificate without modifying the connector. | source `CultMeshHttpsContentTransport.cs:18-29,92-104` |
| F7 | `NormalizeHash` (C#) strips a case-insensitive `sha256:` prefix *before* trimming, then `Trim().ToLowerInvariant()`. It never checks for hex or a length of 64. So the record-key grammar accepts any non-blank string, and a provider must add its own hex check before a hash can become a file name. | source `CultMeshCdn.cs:549-560` |
| F8 | `StableToken` (the token in a manifest record key) is a C# rule with no Rust mirror: `cultnet-rs/src/content.rs` builds no artifact record key. Its Unicode semantics differ from the naive JS spelling in four probed ways. .NET `ToLowerInvariant` leaves `U+0130` unchanged, while JS gives `i` plus `U+0307`. .NET `Trim` keeps `U+FEFF` (and trims `U+0085`, per `char.IsWhiteSpace`), while JS `trim` drops `U+FEFF` and keeps `U+0085`. JS whole-string lowercasing applies final-sigma (`ΑΣ` becomes `ας`), while per-unit lowercasing gives `ασ`. `char.IsLetterOrDigit` is false on a lone surrogate and on `U+00B2`, which JS `\p{L}\p{Nd}` agrees with only when it is tested per UTF-16 unit. | probe (net10.0 console and node); source `CultMeshCdn.cs:562-580` |
| F9 | `CreatedAtUtc` defaults to `DateTimeOffset.UtcNow.ToString("O")`, which looks like `2026-10-19T12:07:25.5235579+00:00`: 7 fractional digits and `+00:00`, never `Z`. | probe (net10.0) |
| F10 | `contracts/cultmesh/content-vectors.cs-written.json` and `.rs-written.json` are written by `tests/GameCult.Mesh.Tests/CultMeshContentVectorTests.cs` and `packages/cultnet-rs/tests/content.rs`, under `CULTNET_WRITE_VECTORS=1`. Each side judges both files. Kinds are `request`, `response` and `manifest`. Rust panics on an unknown kind (`tests/content.rs:302`), and C# requires the Rust labels to equal its own (`CultMeshContentVectorTests.cs:54`). **So a new kind or label in the cs-written file breaks both siblings.** A TS file must be a separate file. | source |
| F11 | **Neither content-vector suite runs in CI.** `cultmesh-portability.yml` filters `dotnet test` to `AuthorityProof`, `RealtimeWireProtocol` and a lease churn test. `cultnet-interop.yml` runs `cargo test … rudp` (a name filter) and the Networking `Rudp` filter. The C#/Rust content parity has only ever been run by hand. | grep of `.github/workflows/*.yml` |
| F12 | TS→C# vector precedent: `scripts/write-cultmesh-realtime-frame-vectors.mjs` writes `realtime-frame-vectors.ts-written.json` from built `dist`, deliberately from **different inputs** than the C# file. `CultMeshRealtimeWireProtocolTests` judges it, and `cultmesh-portability.yml:145-149` runs it. | source |
| F13 | Cross-process precedent for C# against a Node provider exists. `packages/cultnet-ts/test/interop/cultnet-interop.test.ts:536-560` spawns the TS QUIC provider, then runs `dotnet test --filter NativeConnectorReceivesFromExternalProvider` with the endpoint in `CULTMESH_NATIVE_EXTERNAL_ENDPOINT`. The C# test skips without the variable (`tests/GameCult.Mesh.Quic.Native.Tests`, line 21). | source |
| F14 | `cultnet-ts` is CommonJS. Its `./wire`, `./contracts` and `./authority` subpaths import only `@msgpack/msgpack`, `ajv` and `cultcache-ts/document`, so they are browser-safe. Its root `index` pulls `node:` modules (`peer`, `rudp`, `transport`, `security` and others). `cultmesh-browser` (ESM) already imports `cultnet-ts/wire`, `/contracts` and `/authority`. `cultmesh-ts` loads `koffi` lazily, inside a function (`realtime-quic-native.ts:139`), so a new subpath of it pays no native load. The root has `esbuild` ^0.28.1 as a devDependency. | source, grep |
| F15 | TS packages build with `tsc` and test with `node --test dist-test/test/*.test.js` per workspace. `scripts/run-typescript-workspaces.mjs` runs them in order `cultcache-ts, cultnet-ts, cultmesh-ts, cultmesh-browser`, after `check-typescript-tests-wired.mjs`. No StrykerJS config exists in CultLib (`docs/mutation-testing.md:156-159`). | source |
| F16 | **No CultLib TS package except `cultcache-ts` has a publish job.** `publish-packages.yml` triggers on `cultcache-ts-v*`, `cultcache-py-v*`, `cultnet-py-v*` and `cultmesh-py-v*` only. Tags `cultnet-ts-v0.1.1` and `cultmesh-ts-v0.1.1` exist, but no workflow consumes them. `npm view` returns 404 anonymously for `cultnet-ts`, `cultmesh-ts`, `cultmesh-browser` and even `@gamecult/cultcache-ts` (`@gamecult/cultcache-ts` may be private; unverified). The doctrine's rule of at most three tags per push stands (SKILL.md:674). | source; `npm view` |
| F17 | StreamPixels consumes CultLib as the git submodule `vendor/CultLib` (gitlink `30ee8b97`) with `file:` deps and `pnpm.overrides`. Its CI and Idunn recipes build the vendored workspaces (`npm ci --ignore-scripts`, then build `cultcache-ts`, `cultnet-ts` and `cultmesh-ts`). `apps/web` depends only on `cultnet-ts` today, not on `cultmesh-browser`. | StreamPixels `.gitmodules`, `apps/*/package.json`, CI workflow lines 62-95 |
| F18 | The Rust reader sets two guards that the TS reader should match. A caller cap `max_bytes` is checked before any request. And a zero-size chunk is refused unless it is the only one, because a manifest of zero-size chunks would be a request flood. | source `cultnet-rs/src/content.rs:364-385` |

## 2. Model page

There is one row per persistent or wire-visible kind. Identity is what names the
kind, lifecycle is what happens to it over time, and authority is who decides.
The HTTP answers are new design: no runtime has an HTTPS content server (target,
"Why now"). They are grounded in three content-addressed HTTP stores, as I
recall their specs; I did not fetch the specs in this pass. The IPFS trustless
gateway answers raw blocks with `Cache-Control: public, max-age=29030400,
immutable`, uses the CID as `ETag`, and returns 400 for an unparseable CID. The
OCI distribution spec's `GET`/`HEAD /v2/<name>/blobs/<digest>` returns 200 with
the digest header, 404 `BLOB_UNKNOWN` and 400 `DIGEST_INVALID`, and treats Range
(206/416) as optional. Nix binary caches serve `<hash>.narinfo` with 200 or 404,
and `nix` probes them with HEAD. My confidence in those three shapes is
moderate to high.

| Kind | Identity (what names it) | Lifecycle | Authority (who decides; forbidden writers) |
|---|---|---|---|
| **Manifest** `gamecult.mesh.cdn_artifact_manifest.v1` | The record key (row below), derived from `artifactId`, `version` and `contentHash`. On the wire it is the 10-array of F1. Its namespace is the schema id. Two manifests for one body under different ids or versions are distinct and legitimate. | Immutable once packed. A new body or version is a new manifest and a new key. Nothing revises one in place, and deletion is not in scope (no GC). `createdAtUtc` is set at pack, and a caller may pin it so that repacking is reproducible. | The packer (`packContent` in `cultnet-ts/content`) is the only TS writer. The encoder and decoder in the same module are the only TS byte owners. **Forbidden:** a second TS manifest type or encoder in `cultmesh-ts`, `cultmesh-browser` or a consumer, and any URL or endpoint field. |
| **Chunk ref** `CultMeshCdnChunkRef` | Its `chunkHash` (SHA-256 of the chunk bytes) within one manifest, plus `offset`. Two refs may share a hash, which is legal and served once. | Immutable inside its manifest. `recordKey` is derived and may be blank on read (C# and Rust fall back to `mesh:cdn:chunk:<hash>`). | Packer writes; `validateManifest` judges contiguity, non-negative sizes and the size sum (C# `ValidateManifestShape` order). Reader and provider never edit one. |
| **Chunk record key** `mesh:cdn:chunk:<hex>` | Fully derived from `normalizeHash(chunkHash)`. It is injective only over valid hex, because C# accepts any non-blank string (F7). | Derived on demand, never stored independently of a manifest. | `chunkRecordKey` in `cultnet-ts/content`. On the HTTP wire the provider **checks** a non-blank `recordKey` against it and never derives a file from it. |
| **Artifact record key** `mesh:cdn:artifact:<token>:<versionToken\|unversioned>:<hex>` | Derived by `StableToken` (F8). It is not injective: `A/b` and `a b` both give `a-b`, and the content hash at the end disambiguates. | Derived on demand. | `artifactRecordKey` in `cultnet-ts/content` mirrors C# per UTF-16 unit. Truth comes from the C#-written `contracts/cultmesh/cdn-record-key-vectors.json`, and TS never authors expectations for it. |
| **Content hash / chunk hash** | Lowercase 64-hex SHA-256. On input, `normalizeHash` accepts `sha256:`, case and surrounding whitespace, as C# does. The HTTP boundary and the reader also demand `/^[0-9a-f]{64}$/`. | Immutable. It *is* the identity. | `sha256Hex` (WebCrypto `crypto.subtle`) in `cultnet-ts/content` is the only TS hasher. The provider and the reader call it and keep no copy. |
| **Stored chunk** (provider disk) | File `<root>/<hex>`, flat, one file per distinct chunk hash. For a single-chunk artifact (every StreamPixels artifact, since 125 KB is well under the 4 MiB default chunk) this is the body file StreamPixels already plans (`/srv/streampixels/content/<hex>`). | Written by the publisher outside the provider (StreamPixels' publish script) and never written by the provider. Immutable. Deletion has no owner (GC is out of scope). A corrupt or oversized file stays on disk and is never served. | The publisher writes; **the provider is read-only**. The provider verifies each read against its name before serving (`verify-before-serve`). **Forbidden:** any path derived from `recordKey`, the URL path or an unvalidated hash. |
| **Provider store index** | **None.** The file name is the index: the store maps hex to `<root>/<hex>` on each request after the hex check. There is no in-memory table, so nothing can go stale. | Not applicable: it is computed per request. | `createDirectoryChunkStore(root)`, a read-only lookup port. A host may supply another `ContentChunkStore`, and the provider verifies whatever any store returns. |
| **The empty chunk** `e3b0c442…b855` | The SHA-256 of zero bytes. | Intrinsic: it needs no file. | The provider answers it as 200 with `Content-Length: 0` without consulting the store, so empty artifacts work against any store. The reader verifies it locally and sends no request. |
| **Wire request** | `GET` or `HEAD <mount>?chunkHash=<hex>&recordKey=<key>`. Extra query parameters are ignored (an endpoint may carry its own, F5). The mount path belongs to the host. | Stateless. | Built by the C# connector or the TS reader. The TS reader builds the URL **byte-identical** to `BuildChunkUri` (escaping as `Uri.EscapeDataString`), so C# and browser clients share HTTP cache entries. |
| **Answer 200** (GET) | The body is exactly the verified chunk bytes. | Immutable per URL. | Provider. Headers: `Content-Length: <size>`, `Content-Type: application/octet-stream`, `X-Content-Type-Options: nosniff`, `Cache-Control: public, max-age=31536000, immutable, no-transform`, `ETag: "<hex>"` and `Access-Control-Allow-Origin: *`. `no-transform` stops a proxy from compressing the body, which would make `Content-Length` disagree with the decoded size (F6). `nosniff` with octet-stream keeps a public chunk from being rendered as HTML on the host's origin. |
| **Answer 200** (HEAD) | The same headers as GET, with no body. | Same as GET. | Provider. It verifies exactly as GET does, so HEAD can never advertise a corrupt chunk. |
| **Answer 304** | The request's `If-None-Match` contains `"<hex>"` or `*`. | Same as 200. | Provider, answered before the store is touched. It is safe because the ETag is the content identity. |
| **Answer 400** | `chunkHash` missing, repeated, or not 64 hex after `normalizeHash`; or a non-blank `recordKey` that is not `mesh:cdn:chunk:<hex>` (the C# server's `record key disagrees` rule). | Stateless. | Provider. Short `text/plain` reason, no echo of input, `nosniff`. This follows OCI `DIGEST_INVALID` and IPFS's 400 for a bad CID. |
| **Answer 404** | A valid hash whose chunk the store does not hold. | Stateless; a later publish can turn it into 200. | Provider (OCI `BLOB_UNKNOWN`, Nix narinfo 404). The answer carries `Cache-Control: no-store`, so an absence is never cached as immutable. |
| **Answer 405** | Any method other than GET, HEAD or OPTIONS. | Stateless. | Provider, with `Allow: GET, HEAD, OPTIONS`. |
| **Answer 204** (OPTIONS) | A CORS preflight. The reader's GET is a simple request and never preflights. This answer exists for clients that add headers. | Stateless. | Provider: `Access-Control-Allow-Origin: *`, `Access-Control-Allow-Methods: GET, HEAD`, `Access-Control-Allow-Headers: If-None-Match`, `Access-Control-Max-Age: 86400`. |
| **Answer 500** | The store returned bytes whose SHA-256 is not the requested hash, or a file larger than `maxChunkBytes`. | Persists until the operator replaces the file. | Provider. It sends no body bytes and calls the host's `onCorruptChunk(hash)` hook so the host can fail health. It is 500 and not 404 because this is a server fault the operator must see, and a 404 would hide corruption as absence. |
| **Range** | Not supported. A `Range` header is ignored, the answer is 200 with the full chunk (RFC 9110 permits this), and no `Accept-Ranges` header is sent. | Not applicable. | Provider. The chunk is the resumption unit, and resumable TS state is out of scope. OCI treats Range as optional too. |
| **Reader result / error** | `Uint8Array` of the whole body, or one `CultMeshContentError` with `code` ∈ {`manifest-invalid`, `too-large`, `insecure-endpoint`, `webcrypto-unavailable`, `http-status`, `length-mismatch`, `chunk-hash-mismatch`, `content-hash-mismatch`}. An abort rejects with the signal's own reason (`AbortError`). | Transient, held in memory only. No byte of a failed chunk or body is returned, in whole or in part (`verify-before-use`). | `fetchVerifiedContent` in `cultmesh-browser`. Its concurrency window defaults to 4 and is capped at 32 (the C# transfer owner's bounds, `content-sessions.md`). `maxBytes` defaults to 256 MiB and is checked before any request. `https:` is required, except `http:` on loopback hosts for development. |
| **Endpoint** | A URL the caller resolved through CultMesh. | Per call; never persisted in content. | The caller. **Forbidden:** any manifest, chunk-ref or record field carrying it (`no-urls-in-content`). |
| **Vector files** | `content-vectors.cs-written.json` (C#), `.rs-written.json` (Rust), new `content-vectors.ts-written.json` (TS, manifests only, own inputs), new `cdn-record-key-vectors.json` (C#, record-key traps). | Each is rewritten only by its writer under `CULTNET_WRITE_VECTORS=1`. CI never sets the variable: the committed files are the contract. | Each writer owns its file. C# judges the TS file, and TS judges both C# files. The cs-written file gains no kind or label (F10). |

No cell above is a guess, with two marked exceptions: the prior-art shapes
(recalled, not fetched) and `@gamecult/cultcache-ts`'s npm visibility (F16).

## 3. Rationale

**Where the CDN shapes live: `cultnet-ts/src/content.ts`, exported as
`cultnet-ts/content`.** Both consumers already depend on `cultnet-ts`, and its
subpaths are the repo's established browser-safe entry points (F14). A shapes
module inside `cultmesh-browser` cannot be imported by `cultmesh-ts`, which is
CommonJS. A shapes module inside `cultmesh-ts` drags a Node package into the
browser. A new package would add a release surface for roughly 250 lines. The
placement also matches Rust, where `cultnet-rs/src/content.rs` owns the same
rules. An esbuild `platform: "browser"` bundle test pins the browser-safety
claim.

**Hashing is WebCrypto in one place.** `crypto.subtle` exists in browsers (secure
contexts) and in Node 20+, so one hasher serves the packer, the provider and the
reader. The packer is therefore async. WebCrypto has no incremental digest, so
the whole-body hash needs the body in memory. That is acceptable because the
target keeps artifacts in memory in the browser, and a Node publisher of very
large artifacts would want a streaming packer. That packer is not in scope.

**Decoding follows the siblings, not a canonical-bytes tribunal.** The manifest
decoder accepts any integer width (as C# does), refuses duplicate metadata keys
(as C# and Rust do, using `mapKeyConverter`), keeps metadata as ordered entries,
keeps int64 exact, and refuses unsafe integers. Before decoding, it runs the
payload depth walk (ruling `payload-depth-limit-256`). That needs
`checkPayloadDepth` exported from `@gamecult/cultcache-ts/document`, a one-name
export from its existing owner, rather than a second walk in `cultnet-ts`
(F4).

**No TS CultCache document definition for manifests yet.** The shapes module
gives bytes, values and keys. Defining `gamecult.mesh.cdn_artifact_manifest` as
a `cultcache-ts` document needs a formatter, and it would bring the C# `[CultIndex]`
declarations on `Kind`, `Version` and `ContentHash` with it. Ruling
`variants:unique-indexes` says `[CultIndex]` means unique, which would bar two
manifests of one kind in a store. I did not probe whether the reference already
trips on this. No consumer asks for a TS store of manifests: StreamPixels builds
manifests at boot. This is recorded as a follow-up, not done.

**Provider: `cultmesh-ts/content`, a pure core plus a Node adapter.** The core is
`createContentChunkHandler(store, options) => (request) => Promise<answer>`,
where a request is `{ method, url, headers }` and an answer is
`{ status, headers, body? }`. It is framework-neutral, testable without a
socket, and mountable on `node:https`, Express, Fastify or Next. A `toNodeListener`
adapter of about 15 lines covers the bare `node:https` case. The name says
"chunk" because the handler answers chunks only. StreamPixels' sketch,
`createHttpsContentHandler`, implied it does TLS, and it does not: the host or
a reverse proxy terminates TLS. The store is a narrow port, `readChunk(hex) →
bytes | undefined`. The core verifies whatever any store returns, so
`verify-before-serve` does not depend on the store being honest.

**Reader: `fetchVerifiedContent(endpoint, manifest, options)` in
`cultmesh-browser`.** Before any request it validates everything: shape, cap,
the zero-size rule (F18), and hex hashes. It fetches in a bounded window with
`credentials: "omit"` and an internal `AbortController` chained to the caller's
signal. It refuses a mismatched `Content-Length` before reading. With no
`Content-Length`, it reads through a bounded stream reader. It hashes each
chunk, places it at its offset in a private buffer, and returns that buffer
only after the whole-body hash matches. The C# connector's URL shape is copied
byte for byte (F5), so the pin is a fixture taken from the probe output.

**The cross-process test runs from TS, as F13 does.** A `node --test` interop
test starts the TS provider over `node:https` with a throwaway self-signed
certificate. It then runs one env-gated NUnit test that drives the unmodified
`CultMeshHttpsContentTransportConnector`, with a handler pinning that
certificate, and checks every chunk with `CultMeshCdn`'s own validation. The TS
reader reads the same chunks from the same provider in the same test.

**"Released" needs an operator ruling (question admitted).** The target says
"the npm packages StreamPixels vendors", but StreamPixels vendors a gitlink, not
npm (F17). None of the three packages has a publish job, and none is visible on
npm (F16).

## 4. Cut sequence

One line per cut. Only C1 is specified. C2 and C3 run in parallel after C1
merges, each in its own worktree.

1. **C1 `c1-shapes`**: `cultnet-ts/content` owns the manifest and chunk-ref types, the codec, `normalizeHash`/`parseSha256Hex`, both record-key grammars, `sha256Hex` and `packContent`. TS joins the content vectors in both directions, C# writes record-key vectors, and the C# content-vector suite enters CI. **Specified.**
2. **C2 `c2-provider`**: `cultmesh-ts/content` holds `createContentChunkHandler`, `createDirectoryChunkStore` and `toNodeListener`, with the model page's answers as behavioural tests, path-traversal negatives and verify-before-serve mutants.
3. **C3 `c3-reader`**: `cultmesh-browser` gets `fetchVerifiedContent` and `CultMeshContentError`. It is tested against a fetch double and a live `node:http` loopback provider (C2's, once merged), and covers abort, concurrency bound, empty artifact and URL parity with F5.
4. **C4 `c4-cross-process`**: the TS interop test serves over `node:https`; an env-gated NUnit test drives the unmodified C# connector; the TS reader reads the same chunks; CI lane per F13.
5. **C5 `c5-release`**: version bumps for `cultcache-ts`, `cultnet-ts`, `cultmesh-ts` and `cultmesh-browser`, with CHANGELOGs, then a Soul pass, then tags (no more than three per push). The shape follows the release ruling.
