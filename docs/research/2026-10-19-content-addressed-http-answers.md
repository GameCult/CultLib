# Content-addressed HTTP answers: what the specs and sources say

Fork served: **cultmesh-ts-content C2 provider HTTP answers** (session label
self-2026-10-19-cm-content). Eyes pass, 2026-10-19. Facts with evidence pointers
only; no recommendation. Line numbers refer to the raw files fetched on
2026-10-19 (master/main at that date), so they drift.

Source shorthands:

- **TG** = IPFS Trustless Gateway spec, https://specs.ipfs.tech/http-gateways/trustless-gateway/ (source: https://raw.githubusercontent.com/ipfs/specs/main/src/http-gateways/trustless-gateway.md)
- **PG** = IPFS Path Gateway spec, https://specs.ipfs.tech/http-gateways/path-gateway/ (source: https://raw.githubusercontent.com/ipfs/specs/main/src/http-gateways/path-gateway.md)
- **OCI** = OCI Distribution Spec, https://raw.githubusercontent.com/opencontainers/distribution-spec/main/spec.md
- **Fetch** = WHATWG Fetch Standard, https://fetch.spec.whatwg.org/
- **RFC 9110/9111/9112**, https://www.rfc-editor.org/rfc/rfc9110.txt (and 9111, 9112, 8246)

Two things the draft model page said "from memory" are checked at the end
(section 8).

## 1. IPFS Trustless Gateway (`application/vnd.ipld.raw`)

| Case | What the spec says | Pointer |
|---|---|---|
| Found | Body is "An opaque bytes matching the requested block CID"; "The Body hash MUST match the Multihash from the requested CID." | TG "Block Responses (application/vnd.ipld.raw)" (src l.341-346) |
| Unknown/absent hash | `404 Not Found` MUST be returned when the root block is not in the gateway's storage; applies to HEAD for any CID and GET of raw blocks. Body not specified. For non-recursive gateways it "definitively signals" the content is not in the dataset. | TG "Response Status Codes / 404 Not Found" (src l.274-283); "Recursion" (l.606-612) |
| Malformed request | TG defines no status for a malformed CID. It says servers SHOULD "validate CID format and encoding before processing requests". PG says `400 Bad Request` is a "generic client error", example "malformed CID". TG gives 400 for missing `Accept` in strict trustless mode (l.108) and for an out-of-range `entity-bytes` (l.210). | TG "Security Considerations / HTTP Servers" (src l.616-628); PG "Response Status Codes" |
| Stored object failing its digest | **Not specified for servers.** The only requirement is client-side: clients MUST verify block digest against the CID before processing or storing. TG `500` is "only for genuine server errors, not for content unavailability" (storage failure, resource exhaustion, unexpected internal errors); corruption is not named. | TG "HTTP Clients / Block Validation" (src l.629-631); "500 Internal Server Error" (l.294-296) |
| Range | TG defines no Range support for raw blocks. PG says Range is "for requesting specific byte range of UnixFS files and raw blocks"; gateways SHOULD support single-range, multi-range optional; success is `206` with `Content-Range`; `Accept-Ranges` is optional, `none` is the example for non-deterministic CAR order. | PG "Range (request header)" (src l.184-193), "Content-Range" (l.626), "Accept-Ranges" (l.632-640) |
| HEAD | `HEAD /ipfs/{cid}` is GET without payload; missing root block is 404 for HEAD too. PG: gateways SHOULD do minimal work to produce headers such as `Content-Length`. For the probe path, all HEAD headers are optional. | TG "HEAD /ipfs/{cid}" (src l.68-72), "404" (l.274-283), "Dedicated Probe Paths" (l.525-548); PG HEAD text |
| CORS | **TG defines none.** PG only says gateways serving cross-origin traffic SHOULD list `Ipfs-Uri` (and `X-Ipfs-Path`) in `Access-Control-Expose-Headers`. Kubo's config doc says its default `Gateway.HTTPHeaders` is `{}` "+ implicit CORS headers from `boxo/gateway#AddAccessControlHeaders` and ipfs/specs#423"; the values themselves are not in that file. | PG header sections; https://raw.githubusercontent.com/ipfs/kubo/master/docs/config.md l.1484-1488 |
| Cache-Control | Response value is in PG, not TG: `Cache-Control: public, max-age=29030400, immutable` "MUST be returned for every immutable resource under `/ipfs/` namespace". (Request-side `Cache-Control: only-if-cached` returns `412` when the root block is not immediately available.) | PG "Cache-Control (response header)" (src l.488-500); TG "Cache-Control: only-if-cached" (l.120-126) |
| ETag | TG: "MUST be returned and follow the recommendations in" PG. PG default is the requested CID in quotes: `Etag: "bafy…foo"`; format-specific variants add a suffix (`"bafy…foo.raw"`); non-deterministic streams use `W/`. PG: gateway MUST compare `If-None-Match` against its `Etag` (answer 304). TG: clients SHOULD NOT assume ETags are portable across gateways. | TG "Etag (response header)" (src l.324-338); PG l.130-136, l.453-486 |
| Content-Type | "MUST be returned"; for raw blocks the type is `application/vnd.ipld.raw` (IANA registered). No charset parameter is specified. | TG "Content-Type (response header)" (src l.300-308); IANA entry linked at l.343 |
| Other headers | `Content-Disposition` MUST be `attachment` with `filename="<cid>.bin"` for raw blocks, "to ensure requested bytes are not rendered by a web browser". PG: `X-Content-Type-Options: nosniff` should be returned with `application/vnd.ipld.raw` and `.car` (from the PG fetch summary; line not re-grepped). | TG "Content-Disposition" (src l.310-316); PG "X-Content-Type-Options" |
| Content-Encoding / Length | PG: `Content-Length` "Represents the length of returned HTTP payload", and warns "the value may differ from the real size of requested data if compression or chunked `Transfer-Encoding` are used", citing ipfs/specs#461 (an open TODO in the spec). | PG "Content-Length (response header)" (src l.613-624) |
| Size cap | Clients SHOULD cap block size; the spec names 2 MiB as the p2p block limit (see Block Limits). | TG "Block Limits" (src l.558-573) and "HTTP Clients" (l.629-640) |

## 2. OCI Distribution Spec, blob `GET`/`HEAD /v2/<name>/blobs/<digest>`

| Case | What the spec says | Pointer |
|---|---|---|
| Found (GET) | "MUST provide the expected blob, with a response code that MUST be `200 OK`." Response MUST contain `Docker-Content-Digest`; "If present, the value of this header MUST be a digest matching that of the response body." Clients MAY ignore it, but if used MUST verify it; "Clients SHOULD verify that the response body matches the requested digest." | OCI "Pulling blobs" (src l.180-195) |
| Unknown | `404 Not Found` MUST be returned (GET and HEAD). The endpoint table lists only `200` success and `404` failure. Error code names `BLOB_UNKNOWN` (code-1) exist in the error table; this section does not require a JSON body. | OCI l.194, l.209-216; Endpoints table l.846 (end-2); Error Codes table l.891 |
| Malformed digest | **Not specified for blob pull.** The table lists only 404. (Other endpoints: PUT blob 400 on digest problems; DELETE blob 404/400/405.) `DIGEST_INVALID` is defined as "provided digest did not match uploaded content" (upload semantics). | OCI table l.846-858; code-4 l.894 |
| Stored object failing its digest | **Not specified for servers.** Client-side verification only (above). | OCI l.188-193 |
| Range | "A registry SHOULD support the `Range` request header in accordance with RFC 9110 (section 14)." No `206`/`416`/`Accept-Ranges` text for blob pull (416 appears only in the upload flow, l.392, 421). | OCI l.196 |
| HEAD | HEAD on an existing blob MUST return `200`; MUST contain `Docker-Content-Digest` and `Content-Length` ("the size in bytes"); not found is `404`. Note: older registries may omit the digest header; clients "can reasonably assume the digest algorithm used is sha256". | OCI "Checking if content exists" (src l.198-216) |
| CORS | **Not in spec.** | whole OCI spec (no `Access-Control` occurrence found in the `grep` of the file) |
| Cache-Control/ETag | **Not in spec.** | OCI spec |
| Content-Type | **Not specified for blob GET**; `application/octet-stream` appears only for upload requests. | OCI upload sections |
| Redirects | Registries may answer blob GET with a redirect; clients must not forward `Authorization` across hosts. | OCI pull text (from the WebFetch of the same file; not re-grepped) |

Known failures near this area (NVD records, fetched 2026-10-19):

- CVE-2024-3727 (containers/image, Red Hat): "integrity-check flaw" allowing unexpected authenticated registry access, resource exhaustion, local path traversal. It affects the client library, not a registry server. https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=CVE-2024-3727
- CVE-2026-85747 (distribution/distribution, fixed 3.1.2): the client's manifest `Get()` does not hash the body against the requested digest, so a malicious registry can substitute content. https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=CVE-2026-85747 (status "Awaiting Analysis")
- CVE-2026-93317 (Docker build): a registry or OCI-layout blob source could supply content not matching the claimed digest and have it cached under that digest. https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=CVE-2026-93317 (status "Awaiting Analysis")
- Searched for a path traversal CVE in a registry server's digest-to-path mapping: none surfaced.

## 3. Nix binary cache (HTTP)

The manual's Binary Cache page defines layout only (a `nix-cache-info` file, a
`.narinfo` per object with "a (usually relative) URL to the corresponding
compressed NAR", the NAR). It states no status codes, headers, CORS, Range or
Cache-Control. https://nix.dev/manual/nix/2.35/protocols/binary-cache/ (page head).
The behavior below is from the Nix client/writer source.

| Case | What the source shows | Pointer |
|---|---|---|
| Found | Writer stores NARs at `nar/<FileHash nix32>.nar<ext>`, i.e. named by the hash of the compressed file; narinfo records `FileHash`, `FileSize`, plus `NarHash`. | https://raw.githubusercontent.com/NixOS/nix/master/src/libstore/binary-cache-store.cc l.156-181 |
| Absent | Client maps HTTP `404` and `410` to `NotFound` ("The file is definitely not there"); `403` to `Forbidden`; `408`/`429` to `Transient`. `fileExists` issues a **HEAD** and treats `NotFound` **and `Forbidden`** as absent, "S3 buckets return 403 if a file doesn't exist and the bucket is unlistable". | https://raw.githubusercontent.com/NixOS/nix/master/src/libstore/filetransfer.cc l.826-838; .../http-binary-cache-store.cc l.184-202 |
| Malformed request | Not specified. | n/a |
| Failing digest | Client side only; the writer's `FileHash`/`NarHash` are checked by the consumer. No server answer defined. | binary-cache-store.cc l.175-181, l.313 |
| Range | Client records `acceptRanges` for retries (client-side use of Range). No server rule. | filetransfer.cc l.219 |
| HEAD | Used for existence probing (above). | http-binary-cache-store.cc l.190 |
| CORS / Cache-Control / ETag | Not in spec or the files read. | n/a |
| Content-Type | Writer uploads `text/x-nix-narinfo` (narinfo), `text/x-nix-cache-info`, and `application/x-nix-nar` (NAR). | binary-cache-store.cc l.57, l.135, l.270 |
| Content-Encoding | Writer can upload with a `Content-Encoding` header naming the compression (http-binary-cache-store.cc l.232). Client sets `CURLOPT_ACCEPT_ENCODING ""` (accept any supported) and tracks a `hasContentEncoding` flag. The NAR itself is stored already compressed (named by compressed-file hash), so transport encoding and stored compression are separate. | filetransfer.cc l.223-224, l.616 |

## 4. Bazel remote cache, HTTP

| Case | What the source/spec says | Pointer |
|---|---|---|
| Protocol | Bazel docs: "Binary data (BLOB) is uploaded via PUT requests and downloaded via GET requests"; action results under `/ac/`, files under `/cas/`; any "HTTP/1.1 server that supports PUT and GET" can serve. The doc page states **no** status codes, no digest verification on PUT, no Content-Type, no compression, no Range. HEAD is not mentioned there. | https://bazel.build/remote/caching "HTTP caching protocol" |
| bazel-remote README | CAS key is "the lowercase SHA256 hash of the entry"; `/cas/<key>` or `/<instance>/cas/<key>` (instance "optional but ignored"); "HEAD requests can be used to confirm whether a key exists"; the empty blob is "always available, even if the cache is empty" (HEAD example: `200` with `Content-Length: 0`); GET with `Accept-Encoding: zstd` "may" return zstd data; PUT of zstd requires `Content-Encoding: zstd` and `X-Digest-SizeBytes`. | https://raw.githubusercontent.com/buchgr/bazel-remote/master/README.md (HTTP section) |
| Found | `200`, `Content-Type: application/octet-stream`, `Content-Length` set to the stored size (uncompressed path). | https://raw.githubusercontent.com/buchgr/bazel-remote/master/server/http.go l.275-282 |
| Absent | `404`, body "Not found" via `http.Error`. | http.go l.268-271 |
| Malformed | Path must match `^/?(.*/)?(ac/\|cas/)([a-f0-9]{64})$` (lowercase 64-hex); otherwise `400` with the escaped URL echoed in the message (`html.EscapeString`). | http.go l.33, l.101-112, l.216-223 |
| Failing digest (read) | No digest check on the GET path in this handler (reader returned by `cache.Get`, `io.Copy` to the client). A failure after headers are sent is logged and cannot change the status ("we've already started writing data"). | http.go l.284-297 |
| Digest check (write) | PUT checks size match (`400` "sizes don't match") and `Content-Encoding` is `identity`/`zstd`/absent (else `400`); the remaining digest verification is in code not read here. | http.go l.306-345, l.372-388 |
| Compression | When zstd is chosen the handler sets `Content-Encoding: zstd` and **omits `Content-Length`** ("TODO: calculate Content-Length for compressed blobs too"). | http.go l.246-250, l.275-281 |
| HEAD | `200` with `Content-Length` of the size, or `404`. | http.go l.468-491 |
| Range / CORS / Cache-Control / ETag | Not present in the handler excerpts read. | n/a |

## 5. Git over HTTP

Smart HTTP does not address single objects. Dumb HTTP does: URL
`$GIT_URL/objects/XX/YYYY…` for a loose object.
https://git-scm.com/docs/http-protocol (introduction example).

| Case | What the spec/source says | Pointer |
|---|---|---|
| Absent | Server "MUST NOT respond with `200 OK`"; "SHOULD respond with `404 Not Found`, `410 Gone`, or any other suitable HTTP status code which does not imply the resource exists". Access denied MUST be `403`. | git http-protocol "General Request Processing" |
| Caching | Clients MAY reuse a cached entity per `Cache-Control`/`Expires` (follow RFC 2616); servers MAY return `304`; clients treat `304` as `200`. Smart services must prevent caching. The page states no Cache-Control value for object files. | same section |
| Content-Type | Specified only for `info/refs` (`text/plain; charset=utf-8` SHOULD, clients MUST NOT validate); nothing for object files. | same page |
| Failing digest | Client side: the dumb walker compares the received object's computed id with the requested id and errors `File %s has bad hash`. | https://raw.githubusercontent.com/git/git/master/http-walker.c l.540-541 |
| Missing target | The client treats a missing-target result as `HTTP_MISSING_TARGET` (http.c l.1944). | https://raw.githubusercontent.com/git/git/master/http.c |
| Content-Encoding | Client sets `CURLOPT_ENCODING ""` at http.c l.2284 (accept any encoding curl supports, curl decodes transparently) and sends `Pragma: no-cache` (l.1452). The http-protocol page says nothing about gzip for objects. | http.c |
| Range, CORS, ETag | Not in the page. | n/a |

## 6. Browser fetch facts (verifying reader)

| Fact | Evidence |
|---|---|
| CORS-safelisted response-header names are exactly `Cache-Control`, `Content-Language`, `Content-Length`, `Content-Type`, `Expires`, `Last-Modified`, `Pragma`. So `Content-Length` is readable cross-origin from a CORS response without `Access-Control-Expose-Headers`. `ETag` is **not** on the list; it needs `Access-Control-Expose-Headers` to be visible. | Fetch 2.2.2 "Headers", CORS-safelisted response-header name (https://fetch.spec.whatwg.org/#cors-safelisted-response-header-name) |
| The CORS-exposed header-name list comes from `Access-Control-Expose-Headers`; a CORS filtered response exposes safelisted names plus that list. | Fetch 2.2.6 "Responses" (https://fetch.spec.whatwg.org/#concept-response-cors-exposed-header-name-list); 3.3.3 |
| CORS check: failure if `Access-Control-Allow-Origin` is absent; "If request's credentials mode is not "include" and origin is `*`, then return success." With credentials mode `include`, `*` is not enough (needs exact origin plus `Access-Control-Allow-Credentials: true`). | Fetch 4.10 "CORS check"; 3.3.5 |
| A CORS-preflight happens for non-safelisted methods/headers (`GET`/`HEAD` with no custom headers are simple requests). | Fetch 3.3 (general); not re-quoted here |
| Fetch decodes content codings before exposing the body. In HTTP-network fetch: it records encoded size from transmitted bytes, sets `bytes` to "the result of handling content codings", then increases decoded size by the decoded length. Unsupported codings: bytes are returned as-is; decoding error: failure. | Fetch 4.7 "HTTP-network fetch"; 2.2.4 "handle content codings" (https://fetch.spec.whatwg.org/#handle-content-codings) |
| The spec itself notes: "This makes the `Content-Length` header unreliable to the extent that it was reliable to begin with." (the header describes the encoded body; the body the page reads is decoded.) | Fetch 4.7 (fetch_t.txt l.9670) |
| When a request has a `Range` header, the browser appends `Accept-Encoding: identity`, "This avoids a failure when handling content codings with a part of an encoded response." | Fetch 4.6 "HTTP-network-or-cache fetch" (l.8760-8767) |
| HTTP framing: with a valid `Content-Length`, a response is incomplete if fewer octets arrive than `Content-Length`; a client MUST record it as incomplete. `Content-Length` "refers specifically to the amount of data enclosed" (encoded form on the wire). | RFC 9112 section 8 "Handling Incomplete Messages"; RFC 9110 section 8.6 |
| `Cache-Control: no-transform`: an intermediary "MUST NOT transform the content". | RFC 9111 section 5.2.2.6 |
| `immutable` extension: identifies responses that will not be updated during freshness lifetime so clients need not revalidate. | RFC 8246 section 2 |

## 7. HTTP core facts that apply to all five systems

| Fact | Evidence |
|---|---|
| Range: "A server MAY ignore the Range header field"; a server MUST ignore Range for methods without defined range handling (GET is the only one). | RFC 9110 section 14.2 "Range" (l.6476-6484) |
| HEAD: "server SHOULD send the same header fields" as for GET; a `Content-Length` on HEAD MUST equal the GET length if sent. | RFC 9110 section 9.3.2 (l.3993), section 8.6 (l.3225) |
| `304`: a `Content-Length` in 304 MUST equal what a 200 would carry, if present. | RFC 9110 section 8.6 |
| `Content-Length` MUST NOT be sent on `204`. | RFC 9110 section 8.6 |

## 8. Cross-check against the draft model page's from-memory claims

| Draft claim (map l.44-49) | Finding |
|---|---|
| IPFS trustless gateway answers raw blocks with `Cache-Control: public, max-age=29030400, immutable` | In **PG**, not TG: the `/ipfs/` immutable value is a MUST there; TG itself does not state the response value. |
| IPFS uses the CID as `ETag` | PG: default `Etag: "<cid>"` quoted; variants add suffixes (raw: `"<cid>.raw"`). |
| IPFS returns 400 for an unparseable CID | PG lists `400` as "generic client error" with malformed-CID as the example. TG itself lists no malformed-CID rule. |
| OCI `GET`/`HEAD` returns 200 with the digest header | Confirmed (`Docker-Content-Digest` MUST on GET and HEAD; `Content-Length` MUST on HEAD). |
| OCI 404 `BLOB_UNKNOWN` | 404 confirmed; `BLOB_UNKNOWN` exists as a code name; the blob-pull text does not mandate a JSON body. |
| OCI 400 `DIGEST_INVALID` on a bad digest | **Not in the blob-pull text.** Endpoint table lists only 404 for blob GET/HEAD; `DIGEST_INVALID` means "digest did not match uploaded content". |
| OCI Range (206/416) optional | Spec says "SHOULD support" Range per RFC 9110; no 206/416/Accept-Ranges text for pull. |
| Nix `<hash>.narinfo` 200 or 404, probed with HEAD | Client source: HEAD for existence; 404 and 410 both map to NotFound; 403 also treated as absent in `fileExists`. |
| Map's own claims (open `*` CORS, 500 on corrupt, Range ignored, Cache-Control immutable) | `*` with non-`include` credentials passes the CORS check (Fetch 4.10). No system surveyed specifies a status for a corrupt stored object. Ignoring Range is permitted (RFC 9110 14.2). |

## 9. Where the systems agree and differ (matrix of what was found)

| Item | IPFS TG/PG | OCI | Nix | Bazel (bazel-remote) | Git dumb |
|---|---|---|---|---|---|
| Absent | 404 (MUST, TG) | 404 (MUST) | 404/410 -> NotFound; 403 also "absent" in HEAD probe | 404 "Not found" | 404/410/other non-200 |
| Malformed id | PG 400 generic; TG silent | silent for blob pull | silent | 400 with message | silent |
| Corrupt stored object (server) | silent; client MUST verify | silent; client SHOULD verify | silent; client verifies hashes | no read-path check in handler | silent; client errors "bad hash" |
| Range | PG: 206 supported (raw blocks); TG silent | SHOULD support | client-side retry only | not in handler | not in spec |
| HEAD | same as GET minus body; 404 | 200 + digest + Content-Length MUST | used to probe | 200 + Content-Length or 404 | not in spec |
| CORS | none in TG/PG (Kubo adds implicit headers) | none | none | none | none |
| Cache-Control on content | PG: `public, max-age=29030400, immutable` MUST | none | none | none | defers to RFC 2616 |
| ETag | PG: quoted CID | none | none | none | 304 allowed |
| Content-Type | `application/vnd.ipld.raw` | unspecified (pull) | `application/x-nix-nar` etc. | `application/octet-stream` | unspecified (objects) |
| Content-Length vs encoding | PG warns they may differ | n/a | transport `Content-Encoding` separate from stored compression | zstd path omits Content-Length | client auto-decodes |
| nosniff / attachment | TG: Content-Disposition attachment MUST; PG: nosniff | none | none | none | none |

## 10. Limits of this pass

- Fetch spec 4.7 was read from the HTML text; the exact algorithm line that fails the fetch on a body shorter than Content-Length was not found in the Fetch text (framing is RFC 9112 section 8, which says the client records the message incomplete).
- bazel-remote digest verification on PUT was not read to the end; the read-path (GET) has no digest check in the handler.
- Kubo's actual default CORS header values were not retrieved (only the pointer to boxo and ipfs/specs#423).
- git CVEs were not searched; Nix and Bazel CVEs were not searched.
- OCI "CORS: not in spec" is from a search of the spec file, not from a registry implementation (distribution/distribution's own headers were not read).
