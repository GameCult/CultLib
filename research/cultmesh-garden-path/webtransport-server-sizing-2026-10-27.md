# WebTransport server: protocol stack, .NET gap, reference sizes (2026-10-27)

Serves ruling `cultmesh-garden-path:ruling:browser-transport` (InForce, Operator, 2026-10-27: WebSocket in stage 1 behind a transport port; a WebTransport server is built in parallel, "sized by how hard it proves"; the size estimate is advice owed to her). Facts with evidence pointers; no recommendation. Browser support table and Kestrel docs status are in `stage1-forks-prior-art-2026-10-27.md` and are not repeated.

Tags: VERIFIED = read on the primary source in this pass (spec text, source file, issue page, local file). SECONDARY = third-party page or search summary. UNVERIFIED = looked for, not found or not readable.

Line counts below are `wc -l` over shallow clones taken 2026-10-27 (clones in the session scratchpad, not kept), non-test files unless stated.

---

## 1. Protocol stack

### 1.1 Layers and what each demands of a server

| Layer | Spec | What the server must do for WebTransport | Tag |
|---|---|---|---|
| QUIC transport | RFC 9000 | Handshake with ALPN `h3`, streams, flow control, TLS 1.3. | UNVERIFIED (not re-read; standard) |
| QUIC datagrams | RFC 9221 | Advertise a non-zero `max_datagram_frame_size` transport parameter. | VERIFIED (draft-16 s3.1 text, via fetch) |
| QUIC reliable reset | draft-ietf-quic-reliable-stream-reset | Draft-16: servers MUST send an empty `reset_stream_at` transport parameter; stream resets MUST use RESET_STREAM_AT with a reliable size at least the WebTransport stream header. | VERIFIED (draft-16 s3.1, s4.4, via fetch) |
| HTTP/3 | RFC 9114 | Control stream (type 0x00) carrying SETTINGS first; QPACK encoder/decoder uni streams (0x02, 0x03); request streams; GOAWAY. | VERIFIED (type codes from quiche `http_constants.h`) |
| QPACK | RFC 9204 | See 1.2. | VERIFIED (RFC text) |
| Extended CONNECT | RFC 9220 | Send `SETTINGS_ENABLE_CONNECT_PROTOCOL` (0x08) = 1; accept `:method CONNECT` with `:protocol`. | VERIFIED (quiche constants; draft-16) |
| HTTP Datagrams | RFC 9297 | `SETTINGS_H3_DATAGRAM` (0x33) = 1; datagram payload = quarter-stream-ID varint + payload; capsule protocol on the CONNECT stream. | VERIFIED (quiche constants; draft-16 s4.5) |
| WebTransport over H3 | draft-ietf-webtrans-http3 | See 1.3 and 1.4. | VERIFIED |

### 1.2 QPACK for a server

- A server's maximum dynamic table capacity is 0 until its encoder processes a peer SETTINGS frame with non-zero `SETTINGS_QPACK_MAX_TABLE_CAPACITY` (default 0). So a server that never advertises capacity leaves the browser unable to use the dynamic table toward it. VERIFIED (RFC 9204 s3.2.3 text lines 560-575; s5 default zero). Browser encoders sending to the server obey the capacity the server advertises (decoder side), so advertising 0 (the default) means requests use only the static table plus literals. VERIFIED by implementation: wtransport's decoder returns `DynamicNotSupported` on any dynamic reference (`wtransport-proto/src/qpack.rs` lines 58-110), and the repo ships against browsers.
- Huffman is not optional for a decoder. The Huffman flag is per string literal and chosen by the peer encoder; the table is RFC 7541 Appendix B unmodified. VERIFIED (RFC 9204 lines 680-690). wtransport uses the `httlib-huffman` crate for it (`qpack.rs:198-204`, `wtransport-proto/Cargo.toml`). The server's own response encoder may avoid Huffman and dynamic table entirely (literals/static references only); wtransport's `qpack.rs` is 583 lines containing both directions.
- Both QPACK uni streams still have to be opened/read by the server (stream-type bytes, instructions ignored when capacity is 0). UNVERIFIED in detail; wtransport has `driver/streams/qpack.rs` (106 lines) for it.

### 1.3 Current draft and codepoints

- Current draft: `draft-ietf-webtrans-http3-16`, 2026-07-06, expires 2027-01-07, datatracker state "In WG Last Call", Standards Track. VERIFIED (datatracker). Previous: -15 (2026-03), -14 (2025-10), -13 (2025-07).
- Draft-16 codepoints VERIFIED (datatracker fetch):
  - Uni stream type `0x54`; bidi signal value `0x41` (also reserved frame type WT_STREAM).
  - `SETTINGS_WT_ENABLED` `0x2c7cf000` (value 1 = "webtransport-h3"; >1 is a connection error); `SETTINGS_WT_INITIAL_MAX_STREAMS_UNI` `0x2b64`; `..._BIDI` `0x2b65`; `SETTINGS_WT_INITIAL_MAX_DATA` `0x2b61`.
  - The datatracker page says the draft defines no `SETTINGS_WT_MAX_SESSIONS`; session limits use 429 / `H3_REQUEST_REJECTED` and RateLimit headers. But webtransport-go (draft-16) defines `settingsWebTransportMaxSessions = 0x14e9cd29` and sends it with value 2^62-1, with the comment "Safari requires SETTINGS_WT_MAX_SESSIONS >= 1 (draft-ietf-webtrans-http3-14)" (`webtransport-go/protocol.go`, `server.go:46`). Draft-14 search snippets (SECONDARY) also name `SETTINGS_WT_MAX_SESSIONS` with "must advertise > 0". The -16 page and the -14 text disagree; the codepoint webtransport-go uses was not found in the -16 page fetched. UNVERIFIED which text is canonical for the 0x14e9cd29 setting.
  - Capsules: WT_CLOSE_SESSION `0x2843`; WT_DRAIN_SESSION (provisional) `0x78ae`; WT_MAX_STREAMS bidi/uni `0x190B4D3F`/`0x190B4D40`; WT_STREAMS_BLOCKED `0x190B4D43`/`0x190B4D44`; WT_MAX_DATA `0x190B4D3D`; WT_DATA_BLOCKED `0x190B4D41`. WT_MAX_STREAM_DATA and WT_STREAM_DATA_BLOCKED are prohibited.
  - Errors: WT_BUFFERED_STREAM_REJECTED `0x3994bd84`, WT_SESSION_GONE `0x170d7b68`, WT_FLOW_CONTROL_ERROR `0x045d4487`, WT_ALPN_ERROR `0x0817b3dd`, WT_REQUIREMENTS_NOT_MET `0x212c0d48`; application error range `0x52e4a40fa8db`..`0x52e5ac983162`, skipping `0x1f*N+0x21`.
  - Session: extended CONNECT, `:protocol webtransport-h3`, `Origin` required for browser clients (403 on failure, 405 if resource not WT, 2xx accepts); optional `WT-Available-Protocols` / `WT-Protocol` negotiation. Close capsule = 32-bit error + UTF-8 message (prose says 1024 bytes, diagram says 8192: internal inconsistency in the draft text). Datagram = quarter-stream-ID + payload. Peer streams/datagrams may arrive before the CONNECT is accepted and SHOULD be buffered within limits. Flow control is enabled only when both sides send non-zero for one of the three `WT_INITIAL_MAX_*` settings; otherwise a client must not open more than one session.
- Draft-02 / draft-07 codepoints (what Chromium's stack uses), from quiche `http_constants.h` and `quic_spdy_session.cc` (VERIFIED): `SETTINGS_WEBTRANS_DRAFT00` = `0x2b603742` (draft-02, "ENABLE_WEBTRANSPORT"); `SETTINGS_WEBTRANS_MAX_SESSIONS_DRAFT07` = `0xc671706a`; `SETTINGS_H3_DATAGRAM_DRAFT04` = `0xffd277`; `SETTINGS_H3_DATAGRAM` = `0x33`.
- Draft-02 vs -07 incompatibilities (draft-07 Appendix A.1, VERIFIED): -07 requires `SETTINGS_WEBTRANSPORT_MAX_SESSIONS` and uses it for version negotiation; explicitly requires `SETTINGS_ENABLE_CONNECT_PROTOCOL`; explicitly requires `SETTINGS_H3_DATAGRAM`; WEBTRANSPORT_STREAM only at stream start. Safe back-ports into -02: 32-bit reset codes, SESSION_GONE, GOAWAY handling, DRAIN capsule, no auto-redirects.

### 1.4 Which draft each browser speaks, and negotiation

- Negotiation mechanism: each draft version owns a `SETTINGS_WT_ENABLED`-style codepoint; endpoints send one per supported version and the highest common one is used. VERIFIED (draft-16 text). In quiche this is implemented as a bit-set of versions: server sends `0x2b603742` for draft-02 and `0xc671706a` (value 16) for draft-07; it records which peer settings it received (`quic_spdy_session.cc:642-654, 1350-1380`). VERIFIED.
- Chromium's shared stack (quiche `main`) supports exactly draft-02 and draft-07 (`kDraft02`, `kDraft07` in `quic_spdy_session.h:137-154`); the header calls draft-02 "the first version to be ever publicly shipped in Chrome". VERIFIED for quiche; that Chrome stable ships quiche's `main` set is SECONDARY (inference; no Chromium release note read).
- Firefox: IETF 124 webtrans minutes state "implemented to draft 4" for WebTransport over H3 with draft 14 in progress, flow control and RESET_STREAM_AT in progress. VERIFIED (minutes text as returned by fetch; "draft 4" is as written).
- Safari: minutes say its prototype supports "the latest H2 and H3 drafts"; the number is not given. VERIFIED (minutes). Safari 26.4 shipped it (see prior note). Safari "requires SETTINGS_WT_MAX_SESSIONS >= 1" per webtransport-go comment citing draft-14. VERIFIED (source comment). A third-party page says Safari refuses a session when `WT_MAX_SESSIONS` > 1 enables flow control while the three `WT_INITIAL_MAX_*` default to 0, while other engines tolerate it; and that different quic-go generations with identical-looking settings got different Safari results (SECONDARY, echo.semantic-ui.com/compat, low-provenance domain).
- `:protocol` token: draft-16 is `webtransport-h3`; webtransport-go accepts both `webtransport-h3` and legacy `webtransport` (`protocol.go:26-38`: "pre-draft-15 value ... for peers that have not yet migrated"). VERIFIED. SECONDARY that Chrome and Safari still send legacy `webtransport` (same low-provenance page).
- Server-side draft header: for the draft-02 world, aioquic's example server adds response header `sec-webtransport-http3-draft: draft02` (`aioquic/examples/http3_server.py:291`); a fails-components issue records "Opening handshake failed" in Chrome resolved elsewhere (wrong URL path), citing that header from an aioquic issue. VERIFIED for the aioquic line; the Chrome necessity of the header is SECONDARY.
- webtransport-go (draft-16) sends three settings for compatibility: draft-06-era `0x2b603742`, `SETTINGS_WT_ENABLED` `0x2c7cf000`, and `0x14e9cd29` max sessions. It does not send `0xc671706a`. It ships a headless-Chrome interop harness (`interop_chrome/`). VERIFIED (source). Net: a draft-16 server in this repo reaches Chrome through the draft-02 setting.

### 1.5 Browser requirements the RFCs leave optional or the drafts leave soft

- `SETTINGS_ENABLE_CONNECT_PROTOCOL` and `SETTINGS_H3_DATAGRAM` are explicit requirements from draft-07 up, and in quiche a draft-07 negotiation with only the draft-04 datagram setting is rejected (`quic_spdy_session.cc:1145-1172`). VERIFIED.
- A third-party page reports: if the max-sessions setting is absent `ready` never resolves or rejects; Chrome `maxDatagramSize` fixed at 1024; Safari can silently fall back to HTTP/2 over TCP (datagrams vanish); Safari flow-control credit not released on FIN/RESET (WebKit bug 319818). SECONDARY (single page).
- Origin header: required from browsers by draft-16 s3.2. VERIFIED.

---

## 2. The .NET gap

### System.Net.Quic
- No datagram API in the shipped surface. The CultLib managed transport itself says so: "System.Net.Quic exposes streams but not QUIC datagrams" (`src/GameCult.Mesh.Quic/CultMeshQuicRealtimeTransport.cs:399-401`). VERIFIED (local file). That the file targets net10.0 is VERIFIED (`GameCult.Mesh.Quic.csproj`).
- dotnet/runtime #53533 "QUIC Datagram API": OPEN, labels `api-suggestion`, `area-System.Net.Quic`, milestone **12.0.0** (as returned 2026-10-27), last activity 2026-07-25. Maintainer ManickaP, 2026-02-25: "we're re-evaluating this for the next .NET. It's mandatory for WebTransport over H/3 AFAIK." nibanks (MsQuic) 2026-01-21 recommends streams over datagrams where one controls both ends. VERIFIED (gh). A duplicate community proposal #123418 (a contributor reports a working fork `wes-sleeman/runtime branch quic-datagrams`) was closed as dupe 2026-01-21 with "we will look into this internally and align the API shape ... No need to put more effort into this atm". VERIFIED (gh).
- Proposed API shape in #53533: `QuicConnectionOptions.DatagramReceiveEnabled`, `QuicConnection.DatagramReceived` event, `SendDatagram`, `DatagramMaxSendLength`, send-state callbacks. VERIFIED (issue body). Not shipped in any .NET 8/9/10 API read.
- System.Net.Quic is stable from .NET 9. SECONDARY (prior note).

### Kestrel experimental WebTransport
- Implemented: draft-02 only. `docs/WebTransport.md`: "most of the WebTransport draft-02 specification, except for datagrams"; needs `EnablePreviewFeatures` + runtime option `...Experimental.WebTransportAndH3Datagrams`; sample apps and a 14-day ECDSA P-256 test cert generator in the doc. VERIFIED (dotnet/aspnetcore `docs/WebTransport.md`).
- Settings it speaks: `Http3SettingType.EnableWebTransport = 0x2b603742` and `H3Datagram = 0xffd277` (the draft-04 value, not RFC 9297's `0x33`); no max-sessions setting. VERIFIED (`Http3SettingType.cs`). So it is draft-02 with draft-04 H3 datagrams: by quiche's rules (1.3) that cannot negotiate draft-07.
- Code size: `Internal/WebTransport/` = `WebTransportSession.cs` 6,618 bytes, `WebTransportStream.cs` 4,104, `WebTransportStreamType.cs` 674, plus hooks in `Http3Connection.cs` (39,423 bytes; WebTransport at lines ~43-85, 406-452, 650-667), `Http3Stream.cs` (59,394), `Http3StreamContext.cs`. Public surface: `IHttpWebTransportFeature`, `IWebTransportSession` in `Microsoft.AspNetCore.Http.Features`. VERIFIED (gh api listing/search; sizes in bytes as returned, lines not counted).
- Kestrel already owns the whole H3 server stack under it: `Http3/` (control stream, frame reader/writer, SETTINGS, GOAWAY), `Http3/QPack/` (decoder/encoder stream readers, dynamic table), Huffman. VERIFIED (gh listing). dotnet/runtime holds a second copy of QPack for the H3 client at `src/libraries/Common/src/System/Net/Http/aspnetcore/Http3/QPack/` (shared source). VERIFIED (gh search path).
- Open issues: aspnetcore #42788 "WebTransport PR follow-ups" (open, updated 2026-09-14); #42784 "Adding datagrams support to WebTransport" (open, updated 2026-02-03); #42785 WT over HTTP/2 (open); #43171 streams may arrive out of order (open; this is the buffered-before-CONNECT case); #65406 "Quic + Datagrams" (open, Backlog; focused by danroth27 2026-05-04 to just QUIC+datagrams). VERIFIED (gh).
- Team position, BrennanConroy 2026-02-12 on #65406: QUIC datagrams "would need to be added in dotnet/runtime"; WebTransport "still in draft 4 years later. We aren't likely to put much investment into it until it matures"; he "could definitely see us pointing AI at the current codebase if it's not working with browsers c[urrently]" (truncated in fetch). VERIFIED (gh, quoted portions). Whether an external PR would be accepted: UNVERIFIED (no PR or maintainer statement either way found; `contributing` rules not read).
- dotnet/runtime #43641 "Add support for WebTransport": open, `api-suggestion`, `area-System.Net.Http`, no activity since 2023-01. VERIFIED (gh).

### MsQuic
- Latest release v2.6.3 (2026-10-13). VERIFIED (GitHub API). CultLib pins 2.5.9 (`scripts/build-quic-native.ps1:16`; Linux CMake message says "msquic v2.5.9 headers"). VERIFIED (local).
- Datagram API in `msquic.h` v2.5.9: `QUIC_SETTINGS.DatagramReceiveEnabled` (line 757/811), params `QUIC_PARAM_CONN_DATAGRAM_RECEIVE_ENABLED` 0x0500000D and `..._SEND_ENABLED` 0x0500000E, `DatagramSend(Connection, Buffers, BufferCount, Flags, ClientSendContext)` (must fit one QUIC packet), events `DATAGRAM_STATE_CHANGED {SendEnabled, MaxSendLength}`, `DATAGRAM_RECEIVED`, `DATAGRAM_SEND_STATE_CHANGED` with states UNKNOWN/SENT/LOST_SUSPECT/LOST_DISCARDED/ACKNOWLEDGED/ACKNOWLEDGED_SPURIOUS/CANCELED, flag `QUIC_SEND_FLAG_DGRAM_PRIORITY`. VERIFIED (header read).
- Reliable reset: `QUIC_SETTINGS.ReliableResetEnabled`, `QUIC_PARAM_STREAM_RELIABLE_OFFSET`, event `RELIABLE_RESET_NEGOTIATED`. VERIFIED (header). That its transport-parameter codepoint matches the one browsers send for draft-16's `reset_stream_at`: UNVERIFIED.
- Windows ships `msquic.dll` with the runtime; Linux needs `libmsquic`. SECONDARY (prior note).

---

## 3. Reference implementations (sizes)

All sizes are non-test lines. "Under it" names the layer the project does NOT contain.

| Project | Draft | WT-specific size | Under it | Notes | Tag |
|---|---|---|---|---|---|
| quic-go/webtransport-go (Go, last commit 2026-10-26) | -16 (README), accepts legacy `webtransport` token, sends draft-06/-16/-14 settings | 3,397 lines in repo root (4,517 incl. interop/example/testdata); tests 4,408. Largest: `server.go` 533, `session.go` 458, `streams_map_outgoing.go` 357, `send_stream.go` 322, `client_conn.go` 289, `capsule.go` 261, `session_manager.go` 190, `receive_stream.go` 185, `streams_map_incoming.go` 179, `flow_control.go` 107, `transport.go` 125 | quic-go v0.62.0 `http3` pkg 5,946 lines + quic-go core ~15.2k + internal ~10.9k; `qpack` v0.6.0 separate module | Client+server. Uses quic-go hooks: additional SETTINGS map, conn context, stream hijack, datagram enable. Own per-session stream maps and flow control. Used by Caddy, Centrifugo, libp2p, MediaMTX. | VERIFIED |
| BiagioFesta/wtransport (Rust, last commit 2026-09-22) | README: "still a draft"; settings `0x2b603742` + `0xc671706a` in `wtransport-proto/src/settings.rs:247-248` (draft-02/-07 pair) | `wtransport-proto` 6,072 lines (incl. inline unit tests; `bytes.rs` 1,008, `stream.rs` 1,233, `frame.rs` 718, `session.rs` 485, `stream_header.rs` 509, `qpack.rs` 583, `ids.rs` 455); `wtransport` crate 6,787 lines (`tls.rs` 1,373, `config.rs` 1,287, `endpoint.rs` 855, `driver/mod.rs` 682, `driver/streams/mod.rs` 588) | quinn 0.11 (QUIC + datagrams), rustls, `httlib-huffman`; pure-Rust, QPACK static-only | The closest "H3 from scratch over a QUIC library that exposes only streams+datagrams" model. Sans-io protocol crate separate from async driver. Config/TLS are about 40% of the driver crate. | VERIFIED (counts); draft-pair inferred from codepoints |
| aioquic (Python, last commit 2026-10-16) | draft-02 only: `Setting.ENABLE_WEBTRANSPORT = 0x2B603742`, WT stream types `0x41`/`0x54`, requires H3_DATAGRAM | `h3/connection.py` 1,591 lines for all of HTTP/3 + QPACK glue (via `pylsqpack` C extension) + WT hooks; WT hooks are about 15 mentions | aioquic QUIC: `quic/connection.py` 4,266, `packet.py` 665, `packet_builder.py` 413 | Smallest complete reference; no capsule handling, no flow control, no session limits, no draft-07. Example server adds `sec-webtransport-http3-draft: draft02`. | VERIFIED |
| Kestrel WebTransport (C#, experimental) | draft-02 + draft-04 H3 datagram setting | ~11 KB in `Internal/WebTransport/`, plus WT branches in `Http3Connection`/`Http3Stream` | Kestrel H3 + QPack + MsQuic via System.Net.Quic | No datagrams, no server-initiated sessions, no capsules. | VERIFIED (bytes, not lines) |
| Chromium / quiche (C++) | -02 and -07 | `web_transport_http3.cc` 509 lines (18 KB; header 8.7 KB), `web_transport_only_server_session.cc` 7.8 KB, `web_transport_stream_adapter.cc` 8.3 KB, `web_transport_priority_scheduler.cc` 6 KB, `quic/web_transport` headers (`web_transport.h` 17.6 KB); `quic_spdy_session.cc` 2,075 lines holds the settings/negotiation | Full quiche QUIC (not sized) and `qpack` dir (~201 KB incl. tests) | Test server `web_transport_only_*` exists; Chrome's reference server. Draft support is a runtime bit-set. | VERIFIED (sizes in bytes/lines as shown) |
| libwebtransport / other C and C++ libraries on MsQuic, ngtcp2, lsquic | | | | A search for a C/C++ WebTransport server over msquic, ngtcp2 or lsquic returned nothing relevant. | UNVERIFIED that none exists |
| Demiurgos.WebTransport.Client (C#) | | client only, Windows x64, preview, native MsQuic backend with datagram send/receive (prior note) | | The only C# WebTransport package found; no server. | VERIFIED (prior note) |

---

## 4. CultLib's own starting point

### `native/GameCult.Mesh.Quic.Native/`
- Size: `cultmesh_quic_native.cpp` 1,513 lines, `include/cultmesh_quic_native.h` 371, `tests/cultmesh_quic_native_tests.cpp` 1,305, `CMakeLists.txt` 118. VERIFIED (`wc -l`).
- Datagrams: no mention of "datagram" in the .cpp, header, README or CMake (`grep -i datagram` empty). It sets `IdleTimeoutMs`, `KeepAliveIntervalMs`, `PeerUnidiStreamCount`, `HandshakeIdleTimeoutMs` only (`cultmesh_quic_native.cpp:769-777`); no `DatagramReceiveEnabled`, no `DatagramSend`, no `ReliableResetEnabled`. VERIFIED.
- Design: runtime-neutral queue ABI v2, no host callbacks, 64-byte event struct, 13 exports; owns registration, configuration, listener, connection and stream handles, TLS credential loading, ALPN `cultmesh-state-v1`, and a 1-byte-kind + 4-byte-LE-length stream framing; "never decodes a CultMesh frame". Two platforms (Windows x64 OpenSSL-flavoured MsQuic NuGet 2.5.9, Linux libmsquic). VERIFIED (README, header). Windows build uses the OpenSSL MsQuic because the Schannel build "refuses" something (build script line 17; not read further).
- Consumers: TS via koffi (`packages/cultmesh-ts/src/realtime-quic-native.ts`, native binaries committed under `packages/cultmesh-ts/native/{linux-x64,win32-x64}/`), and C# via P/Invoke (`src/GameCult.Mesh.Quic.Native/CultMeshNativeQuicRealtimeTransport.cs`, 278 lines). VERIFIED (git ls-files).

### Managed C# QUIC today
- `src/GameCult.Mesh.Quic/CultMeshQuicRealtimeTransport.cs` (700 lines) uses `QuicListener.ListenAsync`, `QuicConnection.ConnectAsync`, `OpenOutboundStreamAsync`, one ALPN list; sending `CultMeshRealtimeDelivery.Unreliable` throws `NotSupportedException("... unreliable delivery requires a native MsQuic connector")` (lines 397-401). VERIFIED (local).
- Rust CultMesh (`packages/cultmesh-rs`: `lib.rs` 1,902 lines, `rudp_document_server.rs` 1,082) and cultnet-rs have no QUIC dependency (`Cargo.toml` grep empty). VERIFIED. Existing server transports: RUDP, WebSocket (`GameCult.Networking.WebSockets`), QUIC (above).
- `docs/cultmesh-garden-path-target.md:146` lists the transport as an open item; `docs/typescript-quic-realtime-target.md:112` says "Browser QUIC (WebTransport). Browsers keep the verified WebSocket path." VERIFIED (local).

### Which layer can host H3 + WebTransport
Facts bearing on it, not a choice:

| Layer | Datagrams | H3/QPACK present | Needs written |
|---|---|---|---|
| Managed C# over System.Net.Quic (net10) | No public API (runtime #53533, milestone 12.0.0) | No public H3 server API; QPack source exists inside runtime/Kestrel as internal | Datagrams impossible without a runtime change or reflection/private MsQuic access; H3 control + QPACK + capsules + WT layer |
| Native bridge (MsQuic 2.5.9 C++) | MsQuic has full API; bridge does not enable it | No | Datagram + reliable-reset plumbing in the bridge ABI (new events/exports), H3 + QPACK + WT either in C++ or in C#/TS above a bridge that exposes raw streams, per-stream FIN/reset and datagrams |
| Upstream Kestrel | No (needs runtime #53533 first) | Yes: complete H3, QPack, GOAWAY | Datagrams, draft-07+ settings, capsules, server-initiated sessions, flow control; team says low priority |
| Rust (wtransport over quinn) | Yes via quinn | Yes, own static-only QPACK | No dependency on the .NET/MsQuic stack; CultLib Rust crates currently use RUDP only |

---

## 5. Interop pain reported

- Draft churn: Chromium stack on -02/-07 while the IETF text is at -16 with different codepoints; Firefox on a "draft 4"/14-in-progress; Safari "latest" (1.4). VERIFIED/SECONDARY as tagged there. webtransport-go keeps three settings plus a legacy `:protocol` token to span them (1.4).
- Draft-16 itself marks "draft-only" version rules (client sends draft codepoint, server defers processing until client SETTINGS) as to be removed before publication. VERIFIED.
- Certificates: `serverCertificateHashes` rules, W3C WebTransport CR Snapshot, VERIFIED: validity period MUST NOT exceed two weeks; key must be ECDSA secp256r1 (P-256); the 14-day figure matches Kestrel's doc ("14 days is the max duration"). Let's Encrypt community thread confirms public-CA certs (90 days) cannot be pinned this way (SECONDARY). Default Kestrel dev cert does not work with WebTransport (docs), aspnetcore #41762 "Default dev certificate not working with Http3" open, updated 2026-01-26 (VERIFIED, gh). Other pinning gotchas (hash of leaf DER not SPKI, lowercase `"sha-256"`, `allowPooling` incompatible, Chrome requires a public root otherwise `ERR_QUIC_CERT_ROOT_NOT_KNOWN`, Safari ignores user-installed roots): SECONDARY (single low-provenance page). Feisty Duck newsletter and an IPFS issue exist on the same topic, not read. The W3C group discussed removing `serverCertificateHashes` (issue #623) and marked it at-risk: SECONDARY.
- Headless Chrome testing: webtransport-go's `interop_chrome/interop.py` drives Chrome through selenium/chromedriver with `--no-sandbox --enable-quic --headless --origin-to-force-quic-on=localhost:12345 --host-resolver-rules='MAP localhost:12345 127.0.0.1:12345'`. VERIFIED (file read). `--webtransport-developer-mode` as the private-CA escape: SECONDARY.
- Handshake failures that look like draft mismatch can be a wrong URL path (fails-components issue #358, 2025-04-04): VERIFIED (fetch).
- Kestrel sample app issue "ERR_CONNECTION_RESET running WebTransportInteractiveSampleApp" (aspnetcore #52663, open) shows the shipped experimental path failing for at least one user: VERIFIED title/state only; cause not read.
- Not found: an engineering blog post from someone who wrote a WebTransport H3 server from scratch describing effort. UNVERIFIED that none exists.

---

## Evidence index

- Spec: datatracker `draft-ietf-webtrans-http3` (-16), `ietf.org/archive/id/draft-ietf-webtrans-http3-16.html`, `...-07.txt`, RFC 9204 text, W3C `webtransport` TR snapshot, IETF 124 webtrans minutes.
- Code: `google/quiche` (`http_constants.h`, `quic_spdy_session.{h,cc}`, `web_transport_http3.{h,cc}`), `quic-go/webtransport-go`, `quic-go/quic-go`, `BiagioFesta/wtransport`, `aiortc/aioquic`, `dotnet/aspnetcore` (`docs/WebTransport.md`, Kestrel `Internal/Http3`, `Internal/WebTransport`), `microsoft/msquic` v2.5.9 `src/inc/msquic.h`.
- Issues: dotnet/runtime #53533, #123418, #43641; dotnet/aspnetcore #42788, #42784, #42785, #43171, #65406, #41762, #52663.
- Local: `F:\Projects\CultLib\native\GameCult.Mesh.Quic.Native\{cultmesh_quic_native.cpp,README.md,include\cultmesh_quic_native.h,CMakeLists.txt}`, `F:\Projects\CultLib\src\GameCult.Mesh.Quic\CultMeshQuicRealtimeTransport.cs`, `F:\Projects\CultLib\scripts\build-quic-native.ps1`.
