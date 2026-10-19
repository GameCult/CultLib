# TypeScript CultMesh Content Plane: Map

Campaign `cultmesh-ts-content`. Imagination wrote this on 2026-10-19 at CultLib
`8990bd46` (origin/main `a88c629e` plus the target doc), and refreshed it the
same day at `2de07065` for C2 and C3, reading C1 from `b8b825e1`. The target
is `docs/cultmesh-ts-content-target.md`. The HTTP prior art is
`docs/research/2026-10-19-content-addressed-http-answers.md` (cited below as
**R**, with its section number). This page holds the body facts, the model
page, the rationale and the cut sequence. Specs and questions live in the
typed mind, not here.

## 1. Body facts

F1-F18 were probed by running code or read from source at `8990bd46`. F19-F27
were probed or read at `b8b825e1` (C1's head) and `2de07065`. Scratch probes
ran under the session scratchpad (`imagination/`), not in the repo.

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
| F19 | `BuildChunkUri` on more endpoint shapes, as `AbsoluteUri` (what `HttpClient` sends). `https://h` and `https://h/` both give `https://h/?chunkHash=…&recordKey=k`. `https://h:443/x` drops the default port. `https://H.Example/X/` lowercases the host and keeps the path's case. `https://h/x#frag` keeps the fragment after the query. `https://h/x?a=%20b+c` keeps the existing query verbatim and joins with `&`. **`https://h/x?` (an empty query) gives `https://h/x?&chunkHash=…`**, because `UriBuilder.Query` is `?`, which is not blank. `https://[::1]:8443/c` keeps the bracketed host. `Uri.EscapeDataString("!'()*~-._ +é𐐀")` is `%21%27%28%29%2A~-._%20%2B%C3%A9%F0%90%90%80`: unlike JS `encodeURIComponent`, it escapes `!'()*`. | probe: `dotnet fsi` on .NET 10, the three lines of `BuildChunkUri` (`CultMeshHttpsContentTransport.cs:114-122`) copied verbatim |
| F20 | Node 24.15 global `fetch` (undici) against a raw-socket server. A gzip body (`Content-Encoding: gzip`, `Content-Length: 29`) arrives as 1,000 decoded bytes, while `headers.get("content-length")` still says `29`. A body shorter than its `Content-Length` (400 of 1,000) rejects while reading: `TypeError: terminated`, cause code `UND_ERR_RES_CONTENT_LENGTH_MISMATCH`. A body longer than its `Content-Length` (1,000 against 400) is cut silently to 400 by HTTP framing. With no `Content-Length`, all 1,000 bytes arrive. So a reader that trusts the header refuses valid gzipped content, and it learns nothing the received bytes do not tell it (R §6: Fetch decodes content codings, and its own note calls `Content-Length` unreliable). | probe: `imagination/fetchprobe.mjs` |
| F21 | The C# TCP content server is the reference for what a provider does per request (`SendChunkAsync`, `CultMeshTcpContentTransport.cs:384-430`). It runs `NormalizeHash` on the requested hash, refuses a non-blank `RecordKey` that is not `CreateRecordKey(hash)` (ordinal compare, message "record key disagrees with its content hash"), resolves the chunk **by hash** (`_resolveChunk(hash)`), and runs `CultMeshCdn.ValidateChunkPayload` before it sends a byte. So **verify-before-serve on every request is the reference behaviour**. C# chunk storage is one record per chunk, keyed by the chunk record key (`CultMeshDatabaseContentProvider`, `CultMeshContentTransfer.cs:41-71`). Per-chunk storage is the reference layout. | source |
| F22 | C1's exports at `b8b825e1` (`packages/cultnet-ts/src/content.ts`): `normalizeHash`, `parseSha256Hex`, `sha256Hex`, `chunkRecordKey`, `artifactRecordKey` (async), `validateManifest`, `encodeManifest`, `decodeManifest`, `packContent`, `CULTMESH_CDN_MANIFEST_SCHEMA`, `DEFAULT_CHUNK_SIZE_BYTES` (4,194,304), `EMPTY_SHA256`, and the types `CultMeshCdnChunkRef`, `CultMeshCdnArtifactManifest`, `CultMeshCdnPackOptions` and `CultMeshCdnPackedChunk`. `sha256Hex` throws a plain `Error` ("WebCrypto SHA-256 is unavailable.") when `crypto.subtle` is missing. `validateManifest` takes the whole manifest (it reads `metadata`). `cultnet-ts/authority` already exports `isLoopbackEndpoint` (`cultmesh-authority.ts:377`: `localhost`, `loopback`, `::1`, `::ffff:7f00:1`, `127.x.x.x`), and `cultmesh-browser` already imports it (`index.ts:23`). | `git show b8b825e1:…`; cut report `cut-c1-shapes.h1` landed names |
| F23 | The consumer (StreamPixels `dfbe2a6`, `docs/web-character-customization-map.md`) stores one whole-artifact file per content hash at `/srv/streampixels/content/<hex>`, builds manifests at service boot, and hashes every referenced blob at boot (lines 149-159, 175, 317-325). Its artifacts are 10-125 KB (W10), well under the 4 MiB default chunk. It also builds manifests for `catalog.cc` and `character-artifacts.cc`, whose sizes the map does not give. It mounts the handler at `GET /api/content` on its existing server (line 320). Its sketch, `createHttpsContentHandler(store)` (line 293), takes `(chunkHash, recordKey)` and returns bytes or not-found. | StreamPixels `git show dfbe2a6:docs/web-character-customization-map.md` |
| F24 | Build shapes for the two packages. `cultmesh-ts` is CommonJS (`module` and `moduleResolution` `Node16`), and its `package.json` exports only `.` (lines 7-13). `cultmesh-browser` is ESM (`"type": "module"`, `moduleResolution` `Bundler`) and is one file, `src/index.ts`, with no relative imports. A new relative import must therefore be spelled `./content.js`, or Node's ESM loader cannot find it at test time. Both packages' `test` scripts run the glob `dist-test/test/*.test.js`, which the wiring check accepts (`scripts/check-typescript-tests-wired.mjs`). CI runs both in `cultmesh-portability.yml`: the browser at lines 119-120, and `cultmesh-ts` at lines 121-129 under the step name "Verify the realtime frame codec with the TypeScript codec". `esbuild` resolves from the root `devDependencies`, as C1's browser-bundle test shows. | source at `b8b825e1` |
| F25 | The existing TLS fixture `packages/cultmesh-ts/test/fixtures/quic-test.p12` is a self-signed P-256 certificate with subject `CN=cultmesh-quic-native-tests` and no subjectAltName (fixtures README). Node's TLS hostname check therefore refuses it for `127.0.0.1` even when it is trusted through `NODE_EXTRA_CA_CERTS`. A C# handler that pins by certificate hash does not care about the name. | source: fixtures README |
| F26 | `CultMeshCdn.ValidateChunkPayload`, `HashBytes`, `ValidateManifest` and `NormalizeHash` are `internal`, and `GameCult.Mesh` declares no `InternalsVisibleTo`. A test in `tests/GameCult.Mesh.Tests` can therefore verify chunk bytes only with `SHA256.HashData`, or through a public consumer of the connector. The public surface is `CreateRecordKey`, `PackArtifact`, `PublishAsync` and `ReadArtifact(Async)`. | grep of `CultMeshCdn.cs`, `src/GameCult.Mesh` |
| F27 | No CultLib workflow runs `cultmesh-ts` interop tests. `cultnet-interop.yml` (windows-latest, main only) runs `cultnet-ts`'s `test/interop`, and its QUIC native case spawns `dotnet test --filter NativeConnectorReceivesFromExternalProvider` through `runDotnetTestFilter` (`cultnet-interop.test.ts:550,2826`). `cultmesh-ts` has a `test/interop/` folder (a QUIC peer script) but no `test:interop` script. | grep of `.github/workflows`, source |

## 2. Model page

There is one row per persistent or wire-visible kind. Identity is what names the
kind, lifecycle is what happens to it over time, and authority is who decides.
The HTTP answers are new design: no runtime has an HTTPS content server (target,
"Why now"). They rest on R, which fetched the specs and sources on 2026-10-19
and corrected this page's first draft (R §8). In short:

- **404 for absent** is universal (R §9). Nix also reads 410 and S3's 403 as
  absent; we send 404 only.
- **400 for a malformed id** comes from the IPFS path gateway (generic client
  error, with a malformed CID as its example) and bazel-remote (a hash that is
  not 64 lowercase hex). The OCI blob-pull text defines no 400; its
  `DIGEST_INVALID` is an upload code, so the first draft was wrong to cite it.
- **No system specifies a server answer for a stored object that fails its
  digest** (R §9). Every one puts verification on the client. bazel-remote
  streams without checking, so a failure after the headers have gone "cannot
  change the status" (R §4). That is the scar `verify-before-serve` avoids by
  buffering and hashing before the status line.
- **Range** may be ignored (RFC 9110 §14.2). OCI says registries SHOULD
  support it, which the first draft called "optional". We ignore it.
- **`Cache-Control: public, max-age=29030400, immutable`** is the IPFS path
  gateway's MUST for immutable content (R §1). The trustless gateway itself
  does not state it.
- **The Fetch facts** decide the reader (R §6). Fetch decodes content codings
  before the page sees the body, so `Content-Length` describes the encoded
  body, which F20 reproduces in Node. `Content-Length` is CORS-safelisted, and
  `ETag` is not. `Access-Control-Allow-Origin: *` passes the CORS check for any
  request whose credentials mode is not `include`.

| Kind | Identity (what names it) | Lifecycle | Authority (who decides; forbidden writers) |
|---|---|---|---|
| **Manifest** `gamecult.mesh.cdn_artifact_manifest.v1` | The record key (row below), derived from `artifactId`, `version` and `contentHash`. On the wire it is the 10-array of F1. Its namespace is the schema id. Two manifests for one body under different ids or versions are distinct and legitimate. | Immutable once packed. A new body or version is a new manifest and a new key. Nothing revises one in place, and deletion is not in scope (no GC). `createdAtUtc` is set at pack, and a caller may pin it so that repacking is reproducible. | `packContent` in `cultnet-ts/content` (C1, `b8b825e1`) is the only TS writer. The encoder and decoder in the same module are the only TS byte owners. The reader takes a manifest value and never builds or edits one. **Forbidden:** a second TS manifest type or encoder in `cultmesh-ts`, `cultmesh-browser` or a consumer, and any URL or endpoint field. |
| **Chunk ref** `CultMeshCdnChunkRef` | Its `chunkHash` (SHA-256 of the chunk bytes) within one manifest, plus `offset`. Two refs may share a hash: this is legal, and the reader fetches each ref. | Immutable inside its manifest. `recordKey` is derived and may be blank on read. A blank key goes on the wire as `recordKey=` (F5), and the provider accepts it. | The packer writes. `validateManifest` (C1) judges contiguity, non-negative sizes and the size sum. The reader and provider never edit one. |
| **Chunk record key** `mesh:cdn:chunk:<hex>` | Fully derived from `normalizeHash(chunkHash)`. It is injective only over valid hex, because C# accepts any non-blank string (F7). | Derived on demand, never stored independently of a manifest. | `chunkRecordKey` in `cultnet-ts/content`. On the HTTP wire the provider **checks** a non-blank `recordKey` against it, as the C# TCP server does (F21), and never derives a file from it. |
| **Artifact record key** `mesh:cdn:artifact:<token>:<versionToken\|unversioned>:<hex>` | Derived by `StableToken` (F8). It is not injective: `A/b` and `a b` both give `a-b`, and the content hash at the end disambiguates. | Derived on demand. | `artifactRecordKey` in `cultnet-ts/content` (async; C1 deviation). Truth comes from the C#-written `contracts/cultmesh/cdn-record-key-vectors.json`. Neither C2 nor C3 uses it. |
| **Content hash / chunk hash** | Lowercase 64-hex SHA-256. On input, `normalizeHash` accepts `sha256:`, any case and surrounding whitespace, as C# does. The HTTP boundary and the reader also demand 64 hex through `parseSha256Hex`. | Immutable. It *is* the identity. | `sha256Hex` and `parseSha256Hex` (C1) are the only TS hasher and the only hex rule. The provider and reader call them and keep no copy. The negative greps pin this. |
| **Stored chunk** (provider disk) | File `<root>/<hex>`, flat, one file per distinct chunk hash. This is the reference layout (F21: C# stores one record per chunk). For a single-chunk artifact the chunk hash equals the content hash, so StreamPixels' whole-artifact file `/srv/streampixels/content/<hex>` (F23) is already a stored chunk. A multi-chunk artifact's publisher writes `packContent(...).chunks`, one file each. | Written by the publisher outside the provider, and never written by the provider. Immutable. Deletion has no owner (GC is out of scope). A corrupt or oversized file stays on disk and is never served. | The publisher writes; **the provider is read-only**. The provider verifies every read against its name before serving (`verify-before-serve`, the C# per-request behaviour of F21). A symlink or a replaced file cannot leak anything, because only bytes that hash to the requested name are ever served. **Forbidden:** any path derived from `recordKey`, the URL path or an unvalidated hash. |
| **Provider store index** | **None.** The file name is the index. The directory store maps hex to `<root>/<hex>` on each request, after its own `parseSha256Hex` equality check. No in-memory table exists, so nothing can go stale. | Not applicable: it is computed per request. | `ContentChunkStore` is the port: `readChunk(chunkHash, maxBytes) → Promise<Uint8Array \| undefined>`. `createDirectoryChunkStore(root)` stats before reading and rejects a file over `maxBytes` with `ContentChunkTooLargeError`, so an oversized file is never read into memory. A host may supply another store, for example one indexed from manifests, and the handler verifies whatever any store returns. |
| **The empty chunk** `e3b0c442…b855` | The SHA-256 of zero bytes. | Intrinsic: it needs no file. | The provider answers it as 200 with `Content-Length: 0` without consulting the store, so empty artifacts work against any store (bazel-remote does the same, R §4). The reader verifies it locally and sends no request. |
| **Wire request** | `GET` or `HEAD <mount>?chunkHash=<hash>&recordKey=<key>`. Extra query parameters are ignored, because an endpoint may carry its own (F5). The mount path belongs to the host, and the handler never reads it. | Stateless. | Built by the C# connector or the TS reader. The TS reader builds the URL **byte-identical** to `BuildChunkUri`, `Uri.EscapeDataString` escaping (F19) and the empty-query `?&` join included, so C# and browser clients share HTTP cache entries. |
| **Content coding** | Identity, always. | Not applicable. | Provider: no `Content-Encoding`, and `Cache-Control` carries `no-transform`, which forbids an intermediary to transform the content (RFC 9111 §5.2.2.6). That keeps the C# connector's `Content-Length` check meaningful (F6). The reader does not rely on it: it judges the bytes it received (F20). |
| **Answer 200** (GET) | The body is exactly the verified chunk bytes. | Immutable per URL. | Provider. Headers: `Content-Length: <size>`, `Content-Type: application/octet-stream`, `X-Content-Type-Options: nosniff`, `Cache-Control: public, max-age=31536000, immutable, no-transform` and `Access-Control-Allow-Origin: *`. Octet-stream with `nosniff` keeps a public chunk from rendering as HTML on the host's origin. The IPFS `Content-Disposition: attachment` adds nothing over that for a fetch client, so it is not sent. |
| **Answer 200** (HEAD) | The same headers as GET, with no body. | Same as GET. | Provider. HEAD verifies exactly as GET does, so it can never advertise a corrupt chunk. RFC 9110 §8.6 requires HEAD's `Content-Length` to equal GET's. |
| **No ETag, no 304** | Not applicable. | Not applicable. | Provider. `immutable` with a one-year max-age means browsers do not revalidate. `ETag` is not CORS-exposed (R §6), and the URL is already the content identity. An `ETag` without a 304 path is noise, and a 304 path is a second answer to test. The first draft had both, and both are cut. |
| **Answer 400** | `chunkHash` missing, repeated, or not 64 hex after `normalizeHash`. Or `recordKey` repeated, or non-blank and not exactly `mesh:cdn:chunk:<hex>` (the C# server's "record key disagrees" rule, F21). | Stateless. | Provider. Body is a fixed `text/plain; charset=utf-8` reason with no echo of input (bazel-remote echoes the URL; we do not). Headers: `nosniff`, `Cache-Control: no-store`, `Access-Control-Allow-Origin: *`. |
| **Answer 404** | A valid hash whose chunk the store does not hold. | Stateless; a later publish can turn it into 200. | Provider. Same error headers as 400. `no-store` keeps an absence from being cached as immutable. |
| **Answer 405** | Any method but GET and HEAD, OPTIONS included. | Stateless. | Provider, with `Allow: GET, HEAD`. The reader's GET is a CORS simple request (no custom headers, credentials omitted) and never preflights. A client that adds headers would preflight, and its preflight is refused, which is correct because the provider honours no request header. The first draft's 204 preflight answer is cut. |
| **Answer 500** | The store returned bytes whose SHA-256 is not the requested hash; the store holds a file over `maxChunkBytes`; or the store failed. | Persists until the operator replaces the file. | Provider. It sends a fixed reason and none of the chunk's bytes, then calls the host's `onServerFault({ chunkHash, kind, error })` with `kind` ∈ {`hash-mismatch`, `too-large`, `store-error`}, so the host can fail health. A throwing hook does not change the answer. It is 500 and not 404 because the IPFS trustless gateway reserves 500 for "genuine server errors" (R §1), and corruption is one the operator must see, while a 404 would hide it as absence. |
| **Range** | Not supported. A `Range` header is ignored, and the answer is 200 with the full chunk (RFC 9110 §14.2). No `Accept-Ranges` header is sent. | Not applicable. | Provider. The chunk is the resumption unit, and resumable TS state is out of scope. |
| **Provider memory** | At most `maxChunkBytes` buffered per in-flight request. | Per request. | Provider. Buffering is forced, because the status line cannot be sent before the hash is known (the bazel-remote scar above). `maxChunkBytes` defaults to `DEFAULT_CHUNK_SIZE_BYTES` (4 MiB) and must lie in 1..2147483647. The host bounds concurrency (server `maxConnections` or a reverse proxy), and the README says so. |
| **Reader result / error** | A `Uint8Array` of the whole body, or one `CultMeshContentError` with `code` ∈ {`manifest-invalid`, `too-large`, `insecure-endpoint`, `webcrypto-unavailable`, `network`, `http-status`, `length-mismatch`, `chunk-hash-mismatch`, `content-hash-mismatch`}, plus `chunkHash` and `status` where they apply. A caller abort rejects with the signal's own `reason`, unwrapped. Bad options reject with `RangeError`. | Transient, held in memory only. No byte of a failed chunk or body is returned, in whole or in part (`verify-before-use`). | `fetchVerifiedContent` (and `fetchVerifiedChunk`) in `cultmesh-browser`. Concurrency defaults to 4 and is bounded 1..32 (the C# `CultMeshContentTransferOptions` bounds). `maxBytes` defaults to 256 MiB and is checked before any request. The scheme must be `https:`, or `http:` where `isLoopbackEndpoint` (F22) holds. Only status 200 is accepted. **`Content-Length` is not consulted** (F20). The body is read through a stream into the chunk's slot, a body that runs past the chunk's size is cancelled at the first excess byte, and one that ends short is refused. |
| **Endpoint** | A URL the caller resolved through CultMesh. | Per call; never persisted in content. | The caller. **Forbidden:** any manifest, chunk-ref or record field carrying it (`no-urls-in-content`). Error messages never echo it, because an endpoint query may carry a tenant or a token. |
| **Vector files** | `content-vectors.cs-written.json` (C#), `.rs-written.json` (Rust), `content-vectors.ts-written.json` (TS, manifests only, own inputs) and `cdn-record-key-vectors.json` (C#, record-key traps), all landed by C1. | Each is rewritten only by its writer under `CULTNET_WRITE_VECTORS=1`. CI never sets the variable: the committed files are the contract. | Each writer owns its file. C2 and C3 add no vector file. HTTP behaviour is pinned by tests and, in C4, by the C# connector itself. |

Two cells rest on reasoning rather than a probe: that the host, not the
provider, bounds concurrency, and the cost of hashing on HEAD. Both are
defaults a later cut can revise without changing any shape.

## 3. Rationale

**Where the CDN shapes live: `cultnet-ts/src/content.ts`, exported as
`cultnet-ts/content`.** C1 landed this at `b8b825e1`. Both consumers already
depend on `cultnet-ts`, and its subpaths are the repo's established
browser-safe entry points (F14). The placement matches Rust, where
`cultnet-rs/src/content.rs` owns the same rules.

**Hashing is WebCrypto in one place.** `crypto.subtle` exists in browsers
(secure contexts) and in Node 20+, so one hasher serves the packer, the
provider and the reader. WebCrypto has no incremental digest, so every hash
needs its bytes in memory. That fits all three users: the reader holds the
body (the target keeps artifacts in memory), the provider holds one chunk at a
time, and the packer holds the body.

**Decoding follows the siblings.** C1's report records two places where its
decoder is stricter than MessagePack-CSharp: an exact field count, and refusal
of nil strings. Ruling `leaf-acceptance-follows-reference` may make that a
finding. That belongs to C1's Soul pass, not to C2 or C3, which take manifest
values and never decode bytes.

**No TS CultCache document definition for manifests yet.** This is still
follow-up `ts-manifest-document-definition`.

**Provider: `cultmesh-ts/content`, a request-to-answer core, one store and one
Node adapter, in one file.** `createContentChunkHandler(store, options)`
returns `(request: { method, url }) => Promise<{ status, headers, body? }>`.
The request carries no headers, because the provider honours none: Range is
ignored, and conditional requests have no answer now that ETag is cut. The
core reads only the query, never the path, which is what makes
`chunk-only-route` structural rather than checked. `toNodeListener(handler)`
is about 15 lines for `node:http(s)`, and Express, Fastify or Next mount the
core the same way. The name says "chunk" because the handler answers chunks
only. StreamPixels' sketch name, `createHttpsContentHandler` (F23), implied it
does TLS, and it does not: the host terminates TLS. One file, not three,
because the store and adapter are about 50 lines between them and have no
consumer apart from the core. `cultmesh-ts` is already a Node package, so
`node:fs` in the file costs nothing.

**Store layout: per-chunk flat files.** This is the reference layout (F21),
and it needs no index that can go stale. It coincides with StreamPixels'
whole-artifact files, because every StreamPixels artifact is a single chunk
(F23). The alternative is a store indexed from manifests, mapping a chunk hash
to (artifact file, offset, size), so that multi-chunk artifacts could be
stored whole. It is not shipped, because it adds a derived index that must be
rebuilt when manifests change, and no consumer needs it. A host that wants it
writes about 30 lines against the port, and the handler still verifies every
byte. **Consumer note for StreamPixels B3:** its `catalog.cc` and
`character-artifacts.cc` manifests must stay single-chunk, so their files are
the stored chunk. Either they are packed with `chunkSizeBytes` at least the
file size and the provider's `maxChunkBytes` is raised to match, or B3
publishes the chunk files.

**Verify on every request, not once at boot.** The invariant says "serves
only bytes whose SHA-256 equals the requested chunk hash", and a boot check
cannot see a file replaced after boot. C# verifies per request (F21). A
"verified at boot" cache would be a cache pretending to be truth. StreamPixels'
boot check is its own deploy gate (missing blob means failed health), and it
stays useful beside per-request verification. The cost is one SHA-256 of at
most 4 MiB per request, and the immutable one-year caching keeps repeats away
from the origin.

**Reader: `fetchVerifiedContent(endpoint, manifest, options)` and
`fetchVerifiedChunk(endpoint, chunkHash, sizeBytes, options)` in
`cultmesh-browser`, over one internal per-chunk primitive.** The second is the
"fetch by hash" a general consumer expects for a single-chunk artifact. It
builds no manifest: it validates the hash and size, sends
`recordKey=chunkRecordKey(hash)` so its URL equals a C#-packed manifest's, and
runs the same primitive. The whole-body check is the chunk check, because for
a single chunk the two hashes are the same. Before any request,
`fetchVerifiedContent` runs `validateManifest`, `parseSha256Hex` on every
hash, the zero-size rule (F18) and the `maxBytes` cap. It then fills a private
buffer of `sizeBytes` through a sliding window of at most
`maxConcurrentChunkRequests` requests. It uses `credentials: "omit"` and an
internal `AbortController` chained to the caller's signal, so the first
failure cancels every request still in flight. It hashes each chunk in place
and returns the buffer only after the whole-body hash matches.

**Why the reader ignores `Content-Length`.** The target says the reader
"refuses a response whose length differs from the advertised size before
reading it all". The header cannot carry that duty. Fetch decodes content
codings, and a proxy that gzips in spite of `no-transform` turns a correct
1,000-byte chunk into a 29-byte `Content-Length` (F20, R §6). In a browser the
reader cannot even see `Content-Encoding` cross-origin, because it is not
safelisted, so it cannot tell that case apart. What the invariant protects is
bounded reading and no acceptance of a wrong length. The streaming cap
delivers both from the bytes actually received: the reader stops at the first
byte past the chunk's size, and refuses a short body at its end. The target
sentence should be reworded (reported to Self as a contradiction).

**C3 does not wait for C2.** C3's live test serves from a stub `node:http`
loopback server written in the test (about 30 lines). The stub sends what the
model page says, plus the misbehaviours: gzip, a short body, an endless body,
and wrong bytes. The real provider meets the real reader in C4. This keeps
the two cuts parallel, and it lets C3 test misbehaviours the real provider can
never produce.

**"Released"** is ruled: `release-shape-gitlink` (Defaulted by Self).

## 4. Cut sequence

C1 (`cut-c1-shapes.r1`, report `h1` under a Soul pass), C2
(`cut-c2-provider.r1`) and C3 (`cut-c3-reader.r1`) are specified in the mind.
C2 and C3 run in parallel once C1 merges, each in its own worktree from C1's
merge. Both are based on `b8b825e1`, and they touch disjoint files except
`cultmesh-portability.yml`, where their hunks lie 15 lines apart. What
remains:

4. **C4 `c4-cross-process`**, waiting on C2 and C3. Line-level plan for the
   pass that specifies it:
   - New fixture `packages/cultmesh-ts/test/fixtures/content-https-test.p12`
     plus `content-https-test-cert.pem`, a self-signed P-256 certificate with
     subjectAltName `IP:127.0.0.1, DNS:localhost` (F25 rules out reusing the
     QUIC pair). The recipe goes in the fixtures README beside the QUIC one.
   - New `packages/cultmesh-ts/test/interop/content-https.test.ts`, and a
     `test:interop` script in `cultmesh-ts/package.json` that runs
     `dist-test/test/interop/*.test.js`, as `cultnet-ts`'s does. The test is
     skipped when `dotnet` is absent. It packs a 3.5-chunk artifact (chunk
     size 64 KiB), an empty artifact and a single-chunk artifact with C1's
     `packContent`, and writes the chunks into a temporary directory store.
     It serves them through `createContentChunkHandler` and `toNodeListener`
     on `node:https` with the new fixture on `127.0.0.1:0`. It writes the
     manifests' bytes (`encodeManifest`) to a temporary file.
   - C# leg: it spawns `dotnet test tests/GameCult.Mesh.Tests --filter
     HttpsConnectorFetchesFromExternalTypeScriptProvider` with
     `CULTMESH_HTTPS_CONTENT_ENDPOINT`, `CULTMESH_HTTPS_CONTENT_MANIFESTS`
     (the file path) and `CULTMESH_HTTPS_CONTENT_CERT_SHA256`. Reuse or lift
     the `runDotnetTestFilter` shape from `cultnet-interop.test.ts:2826`
     (F27), and assert on "Test Run Successful." and "Passed: 1", not on a
     skip.
   - New C# test in `tests/GameCult.Mesh.Tests/CultMeshHttpsContentInteropTests.cs`:
     `Assert.Ignore` without the variable, as F13 does. It builds the
     **unmodified** `CultMeshHttpsContentTransportConnector` with a
     `HttpClientHandler` whose `ServerCertificateCustomValidationCallback`
     accepts exactly the pinned SHA-256, then `ConnectAsync` with a
     `CultMeshTransportCandidate { Endpoint }` and a session target, then
     `CopyChunkToAsync` for every chunk ref of every manifest (deserialized
     by the public MessagePack path the vector tests use). It checks each
     chunk with `SHA256.HashData` and the whole body against `ContentHash`
     (F26: the validators are internal). It also asserts that an unknown
     hash raises `HttpRequestException` with 404. The first step of the pass
     probes whether `CultMeshContentTransferService` can drive the connector
     without Odin; if it can, it verifies instead of the test's own hashing.
   - TS reader leg: a child `node` process with `NODE_EXTRA_CA_CERTS` set to
     the fixture PEM runs `fetchVerifiedContent` from built
     `cultmesh-browser/dist` against the same `https://127.0.0.1:<port>`
     endpoint, through the real global `fetch` (no injection), and prints
     the SHA-256 of each body. The parent compares them with the manifests'.
     A child process, because the trust anchor must be set before the
     process starts. `cultmesh-ts` gains `cultmesh-browser` as a workspace
     devDependency, which is test-only.
   - Corrupt leg: one stored file is flipped after serving starts. The C#
     connector then fails on the 500 (`EnsureSuccessStatusCode`), the reader
     rejects with `http-status` 500, and `onServerFault` fired with
     `hash-mismatch`.
   - CI: a step in `cultmesh-portability.yml` after the content-vector step
     (`npm run test:interop --workspace packages/cultmesh-ts`). That job
     already has .NET 10 and Node on both OSes.
   - Negative: the git diff of `src/GameCult.Mesh/` over the cut prints
     nothing (the connector is unmodified).
   - Size: about 350 lines and one fixture pair; one Hands pass.
5. **C5 `c5-release`**, waiting on C4 and the Soul passes of C2 to C4. Ruling
   `release-shape-gitlink`: merged to CultLib `main` with version bumps,
   CHANGELOGs and git tags, and no npm publish. Line-level plan:
   - Bumps (minor; every change is additive): `cultnet-ts` 0.2.0 to 0.3.0
     (`./content`), `cultmesh-ts` 0.1.1 to 0.2.0 (`./content`), and `cultmesh-browser` 0.3.0 to 0.4.0 (the reader).
     `@gamecult/cultcache-ts` gains only the `checkPayloadDepth` re-export:
     a patch bump, 0.14.0 to 0.14.1 (its version at `b8b825e1`).
   - Dependency ranges: `cultmesh-ts` and `cultmesh-browser` move
     `cultnet-ts` to `^0.3.0`. `cultnet-ts`'s `^0.14.0` on
     `@gamecult/cultcache-ts` already admits 0.14.1 and stays. `npm install
     --no-package-lock` and the four workspace tests run after the bumps.
   - A `CHANGELOG.md` per bumped package. None exists in any package at
     `b8b825e1`, so C5 creates four, each naming the live surface and not the
     history (Keep a Changelog headings).
   - A Soul pass over the release diff, then four tags: `cultcache-ts-v…`,
     `cultnet-ts-v0.3.0`, `cultmesh-ts-v0.2.0` and `cultmesh-browser-v0.4.0`.
     These go in **two pushes** (at most three tags per push, SKILL.md):
     `cultcache-ts` first, because its tag triggers `publish-packages.yml`
     (F16), and the three plane tags second. Whether the `cultcache-ts` tag's
     npm publish job still runs, and to where, is checked before that push;
     follow-up `npm-publish-ts-packages` owns the rest.
   - The consumer's pickup (StreamPixels bumping `vendor/CultLib` to the
     tagged commit) is StreamPixels' B-cuts, not this campaign.
   - Size: about 60 lines of version and CHANGELOG text; a short Hands pass.
