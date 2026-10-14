# Decode stack safety: prior art (facts only)

Serves follow-up `cultlib-gaps:follow_up:decode-stack-safety` (campaign cultlib-gaps), source finding `thing2:finding:cut-contract-rs.s1.decode-stack-overflow-within-bound`.
Gathered 2026-10-12 by eyes-decode-stack at CultLib `67c79d5c`. No recommendation. Web facts come from WebFetch/WebSearch summaries of the pages named; items marked UNVERIFIED were not confirmed against primary source.

## A. The local door (what exists today)

- `packages/cultcache-rs/src/lib.rs:503` `MAX_PAYLOAD_DEPTH = 256`; `check_payload_depth` (~526-540) is an iterative walk with a `[usize; 256]` count stack, scalars skipped by length.
- `lib.rs:598-604` `decode_payload`: `check_payload_depth(payload)?; rmp_serde::from_slice(payload)`. Its doc comment calls rmp-serde's own counter (1024) "only a backstop".
- Derive: `packages/cultcache-rs/cultcache-rs-derive/src/lib.rs:294-330` generates `impl Deserialize` with a `Visitor::visit_seq` over slot-ordered arrays (`deserialize_tuple`); unknown trailing elements are consumed via `IgnoredAny`.
- rmp-serde resolved version: 1.3.1 (rmp 0.8.15; `packages/cultcache-rs/Cargo.lock`). Its depth limit is a `u16` `depth: 1024`, decremented per array/map/ext (`~/.cargo/registry/src/*/rmp-serde-1.3.1/src/decode.rs` lines 181, 213, 294, 566, 583, 616), error `DepthLimitExceeded`, tunable with `Deserializer::set_max_depth` (clamped to u16::MAX, lines 307-310). CHANGELOG line 95: "Default stack protector depth is now 1024 instead of 1000."
- Upstream master rmp-serde (fetched summary, not checked line by line against 1.3.1): the `deserialize_newtype_struct` MSGPACK_EXT_STRUCT_NAME branch builds `ExtDeserializer` without `depth_count!`; the `any_inner` ext path does use it.
- Existing local tests that already use sized threads: `packages/cultcache-rs/tests/union_parity.rs` ~786-830: `stack_budget_debug_2mib` (comment: "A debug build on a 2 MiB thread carries 405 containers of this document; the bound keeps every accepted payload below that") on a slim fixture (`wrap_chain(126, ...)`), and `the_walk_does_not_recurse` (100000 nested arrays refused on a 256 KiB thread). No stacker, `stack_size`, or thread spawn in `src/` or `Cargo.toml`.
- The rmp-serde 1024 counter and the CultLib 256 bound both count containers; per-level stack cost depends on the target type (follow-up text: 15-19 KiB release, 87-95 KiB debug per level for contract-sized types).

## B. Rust serde ecosystem

### serde_json
- Default limit 128: `Deserializer::new` sets `remaining_depth: 128` (field `u8`); `check_recursion!` returns `ErrorCode::RecursionLimitExceeded` at 0; invoked in `deserialize_any/seq/map/struct/enum`. https://github.com/serde-rs/json/blob/master/src/de.rs
- `disable_recursion_limit()` exists only under Cargo feature `unbounded_depth`. Docs: "Parse arbitrarily deep JSON structures without any consideration for overflowing the stack"; tells the caller to "provide some other way to protect against stack overflows" and names `serde_stacker`. Docs also warn that Display, Debug and Drop on the result can overflow. https://docs.rs/serde_json/latest/serde_json/struct.Deserializer.html
- The limit is a nesting-level count; no per-type frame accounting (code read: counter decrement only). The serde_json README mentions no recursion limit.

### serde_stacker (dtolnay)
- Adapter wrapping a serde (de)serializer that grows the stack dynamically; README examples use `serde_json::Deserializer`, call `disable_recursion_limit()`, wrap with `serde_stacker::Deserializer::new`, nest 10,000 levels. README names Display/Debug/Drop as other overflow points and shows a manual iterative drop. README does not say it depends on `stacker` (UNVERIFIED). https://github.com/dtolnay/serde-stacker
- It is a serde-level `Deserializer` wrapper, so it hooks calls routed through the wrapped deserializer.

### rmp-serde / rmpv
- rmp-serde: depth counter 1024 default (section A). Search found no RUSTSEC advisory or issue for rmp / rmp-serde stack overflow (negative search, not proof). https://github.com/3Hren/msgpack-rust (rmp-serde/src/decode.rs).
- rmpv not inspected.

### Other Rust deserializers
- ciborium: `recurse: usize` default 256 in `from_reader`; `from_reader_with_recursion_limit`; doc: "Set a high recursion limit at your own risk (of stack exhaustion)!" https://github.com/enarx/ciborium (ciborium/src/de/mod.rs)
- serde_cbor: RUSTSEC-2019-0025, tags nested without limit, <1 kB input overflows the stack, fixed in 0.10.2 by limiting nested tags. https://rustsec.org/advisories/RUSTSEC-2019-0025
- rustc-serialize: RUSTSEC-2022-0004, JSON parser with no depth limit, "stack overflow, which aborts the process", unpatched, advises serde. https://rustsec.org/advisories/RUSTSEC-2022-0004
- yaml-rust: RUSTSEC-2018-0006, deep nesting aborts, fixed by depth check. https://rustsec.org/advisories/RUSTSEC-2018-0006
- serde-json-wasm: GHSA-j87p-gjr6-m4pv, overflow on deep untrusted JSON, fixed 1.0.1 by recursion depth check. https://advisories.gitlab.com/pkg/cargo/serde-json-wasm/GHSA-j87p-gjr6-m4pv/
- quick-xml PR 982: adds a depth cap to its serde deserializer, default 128 "matching serde_json". https://github.com/tafia/quick-xml/pull/982
- prost: `const RECURSION_LIMIT: u32 = 100`, comment "100 is the default recursion limit in the C++ implementation"; disabled by feature `no-recursion-limit`. https://github.com/tokio-rs/prost/blob/master/prost/src/lib.rs
- bincode, postcard, simd-json: searches returned nothing on depth handling; not established either way.
- Rust forum user report: serde overflowing the default stack beyond roughly 100 levels in some types, worked around with a 32 MiB thread. https://users.rust-lang.org/t/deserialize-large-nested-struct-without-stack-overflow/136335

## C. Stack-growth mechanism: stacker / psm / stacksafe

- `stacker::maybe_grow(red_zone, stack_size, f)`: checks whether the current stack is within `red_zone` bytes of its end; if so allocates a new stack of "at least `stack_size` bytes" and runs `f` there; else runs on the current stack. The closure runs on a stack with at least `red_zone` bytes available. https://docs.rs/stacker/latest/stacker/fn.maybe_grow.html
- Source (rust-lang/stacker `src/lib.rs`): remaining stack = stack pointer minus thread-local `STACK_LIMIT` (lazily from `guess_os_stack_limit`); if `remaining_stack()` is `None`, `maybe_grow` "immediately grow[s]"; panics are caught on the new stack and re-raised after the segment is freed (`catch_unwind` / `resume_unwind`); on unsupported platforms `_grow` is a no-op that just calls the callback.
- Platform: README says psm on most platforms, a Fibers-based implementation on Windows; "On all unsupported platforms this library is a noop... code will continue to hit the guard pages". https://github.com/rust-lang/stacker
- psm README table (rust-lang/stacker/psm/README.mkd): Windows x86_64 Available Yes / Tested Yes / Callstack "Yes, but disabled"; Windows i686, aarch64, arm: No/No/No; Linux x86_64, i686, aarch64, arm: Yes/Yes/Yes; wasm: No/No/No ("callstack is not even in an address space"). Text: stacks allocated the usual way are not valid on Windows; Fibers offered as an "unnecessarily slow and inflexible" alternative.
- CONFLICT, not reconciled: stacker's code selects a Windows backend (`backends::windows::_grow`, `cfg(all(windows, not(miri)))`) while the psm table marks Windows callstack "disabled"; stacker has a wasm32 branch (alloc-based restore guard) while psm lists wasm as No. An older docs.rs stacker 0.1.0 page lists a different verified-platform list.
- Not found in sources read: per-call cost numbers; Windows aarch64 behavior of stacker beyond the psm table.
- rustc usage (ensure_sufficient_stack, red zone / segment constants): two fetches returned 404; not recorded.
- `stacksafe` (fast/stacksafe): `#[stacksafe]` attribute checks remaining stack at function entry and allocates a new segment below a threshold; defaults 128 KiB threshold, 2 MiB segment; optional `serde` feature gives stack-safe (de)serialization for the `StackSafe<T>` wrapper; MSRV 1.88.0; credits Orson Peters' `recursive` crate; says it supports platforms stacker supports (dependency on stacker not confirmed). https://github.com/fast/stacksafe

## D. Dedicated thread with sized stack

- `std::thread`: default spawned-thread stack "currently 2 MiB on all Tier-1 platforms"; `Builder::stack_size`; `RUST_MIN_STACK` (overridden by `stack_size`, "may be ignored after program start"); "the stack size of the main thread is not determined by Rust". https://doc.rust-lang.org/std/thread/index.html
- Local use is in tests only (`union_parity.rs`, section A). No library was found in this pass that spawns its own decode thread for untrusted input; the Rust forum report (section B) and the protobuf.js advisory describe it as a caller-side or process-level workaround.
- protobuf.js CVE-2026-44289 workarounds: reject excessively nested messages at an outer protocol boundary, or isolate decoding "in a process that can be safely restarted". https://corgea.com/advisories/vulnerabilities/CVE-2026-44289

## E. Non-recursive decoders

- `deser` crate (mitsuhiko, experimental): "does not recurse on the call stack, so deep nesting needs no recursion limit"; nested sinks/emitters kept in an arena; not serde-compatible; calls itself "an experiment". https://github.com/mitsuhiko/deser/wiki
- `core-json`: no-std JSON parser with a fixed-size or caller-supplied state stack; typed layer is a separate crate, `core-json-traits`. https://docs.rs/core-json
- No serde-compatible explicit-stack derive deserializer found (search negative).
- msgpack-javascript: decoder iterative, `StackPool` array of states; DecoderOptions has no max depth, only length caps (`maxStrLength`, `maxBinLength`, `maxArrayLength`, `maxMapLength`, `maxExtLength`); pool memory grows with deepest nesting; array states allocate `new Array(size)` up front. https://github.com/msgpack/msgpack-javascript/blob/main/src/Decoder.ts
- msgpack-python C unpacker (`unpack_template.h`): explicit `unpack_stack stack[MSGPACK_EMBED_STACK_SIZE]`, `start_container` returns -3 at `top >= SIZE`. `unpack.h` defines the size as 1024 (`unpack_define.h` default is 32). Local probe (msgpack 1.2.2, CPython 3.13.15, `python -I`): 100, 256, 1000 nested arrays decode ok; 100000 raises `StackError`.
- CultLib's own `check_payload_depth` walkers are iterative (Rust lib.rs ~526; TS `packages/cultcache-ts/src/payload-depth.ts`; C# `CultPayloadDepth.cs`).

## F. Advisories and other-language limits

- MessagePack-CSharp CVE-2026-48506 / GHSA-vh6j-jc39-fggf (published 2026-06-25): `MessagePackReader.TrySkip()` recursed without counting depth, bypassing `MaximumObjectGraphDepth`; reachable through generated formatters' `reader.Skip()` on unknown keys; uncatchable StackOverflowException; affected <2.5.301 and 3.0.0 to <3.1.7; fix: iterative loop with a heap pending-count. https://securelayer7.net/lab/cve-2026-48506-messagepack-csharp-tryskip-uncontrolled-recursion-dos . Related: CVE-2026-48512 (`ConvertFromJson` recursion), CVE-2026-48513 (Union deserialization not consistently under depth enforcement).
- Default `MaximumObjectGraphDepth`: the sources read do not state it (a figure of 500 was not confirmed).
- protobuf: documented unmarshal depth limits Java 100, C++ 100, Go 10000 (plan to lower to 100); the page gives no rationale. https://protobuf.dev/programming-guides/proto-limits/ . CVE-2024-7254: protobuf-java nested SGROUP unknown fields/maps unbounded recursion, StackOverflow, CVSS 4.0 8.7, fixed 3.25.5 / 4.27.5 / 4.28.2. https://cveawg.mitre.org/api/cve/CVE-2024-7254 (CONFLICT: RHEL reports not affected; an OpenCVE page says no fix, against the CVE record's fixed versions).
- Go encoding/gob CVE-2022-30635: uncontrolled recursion on ignored fields; fix adds a depth limit of 10,000; fixed in 1.17.12 / 1.18.4. https://go.googlesource.com/go/+/6fa37e98ea4382bf881428ee0c150ce591500eb7 . Go encoding/json nesting constant: not confirmed.
- .NET System.Text.Json: `JsonSerializerOptions.MaxDepth` default 0 meaning 64; exceeding throws `JsonException`. https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth
- Python `json`: docs say the module "does not impose any such limits beyond those of the relevant Python datatypes themselves or the Python interpreter itself"; RecursionError for deep input not mentioned. https://docs.python.org/3/library/json.html . The pure-Python msgpack fallback Unpacker recurses (`# TODO should we eliminate the recursion?`), has no depth option, converts `RecursionError` to `StackError`. https://github.com/msgpack/msgpack-python/blob/main/msgpack/fallback.py
- msgpack5 (JS) CVE-2026-107298: no nesting limit, deep input exhausts the decoder stack (title seen only). https://vulert.com/vuln-db/CVE-2026-107298

## G. CultLib's other runtimes

- C# reference: `src/GameCult.Caching.MessagePack/CultMessagePackSecurity.cs`: `UntrustedData.WithMaximumObjectGraphDepth(256)` as backstop; the rule is `CultPayloadDepth.Check`. MessagePack package pinned 3.1.7 (`GameCult.Caching.MessagePack.csproj`), the TrySkip fix version. Other C# surfaces use bare `MessagePackSecurity.UntrustedData` (`src/GameCult.Mesh/CultMeshProviderSessionWire.cs:231`, `src/GameCult.Networking/CultNetSchemaMessageSerialization.cs:15`, `src/GameCult.Networking/MessageSerialization.cs:8`).
- TypeScript: `@msgpack/msgpack ^3.1.3` (`packages/cultcache-ts/package.json`); decoder iterative with no depth option (section E); encode passes `maxDepth: MAX_PAYLOAD_DEPTH * 2` (`src/document-formatter.ts:21`); `checkPayloadDepth` iterative. node_modules not present, so the installed version was not read.
- Python: `msgpack>=1.0` (`packages/cultcache-py/pyproject.toml`); `documents.py:135` and `stores.py:97,384` call `msgpack.unpackb(..., raw=False)`; the C extension has an explicit 1024 stack, so 256-bounded payloads fit; the pure-Python fallback is recursive.
- Whether a typed (class-mapped) TS/Py/C# decode layer above these raw decoders recurses per type was not inspected in this pass.
