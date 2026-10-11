# Payload nesting limits in comparable libraries

Fork: `cultlib-gaps:question:payload-depth-limit` (options reference-500, shared-256, shared-128).
Raised in `cultlib-gaps:cut_spec:cut-union-bounds-cs.r1`. Gathered 2026-10-11 by Eyes
(`eyes-depth-limit-prior-art`). Facts only; no recommendation. Source files were fetched
from each project's default branch on 2026-10-11 (raw.githubusercontent.com); line numbers
refer to those fetches and move with the branch. "(inferred)" marks a reading of code or
text that the source does not state.

Terms: "read" = decode/parse; "write" = encode/serialize; "skipped" = content a decoder
walks past without materialising (ignored fields, unknown fields).

## 1. Summary table

| Library | Default | Counts | Read / write | Source |
|---|---|---|---|---|
| MessagePack-CSharp (UntrustedData and TrustedData) | 500 (`MaximumObjectGraphDepth`) | each `DepthStep` call: formatter-driven, one step per container a formatter enters; check is `Depth >= max` before increment | read only; skipped content not counted unless a formatter calls DepthStep (see 2.1) | MessagePackSecurity.cs:31,41,92,266-273 |
| rmp-serde | 1024 (`depth: u16`, `set_max_depth`) | containers (array, map, ext newtype) via `depth_count!`; counter decremented, error at 0 | read: yes; write: error variant exists but is never constructed | decode.rs:51-60,213,294,309-310,566,583,616; encode.rs:37,49,64 |
| serde_json | 128 (`remaining_depth`, `disable_recursion_limit`) | each `[` and `{` entered by `deserialize_any`-style paths; error when the counter reaches 0 (deepest enterable container is 127, inferred from the decrement-then-test-zero code) | read only; serializer has no depth code; skipped values (`ignore_value`) are iterative and contain no `check_recursion!` call | de.rs:63,1357-1387,1434,1445,1102-1215; ser.rs (no match) |
| msgpack-python (C unpacker) | 1024 (`MSGPACK_EMBED_STACK_SIZE`) | each container pushed on a fixed stack, before the zero-length check; raises `StackError` | read: yes (including `skip()`); write: `Packer` `DEFAULT_RECURSE_LIMIT = 1024` (`ValueError`) | unpack.h:19; unpack_template.h:117-127; _unpacker.pyx:208-211,524; _packer.pyx:42,259-262 |
| msgpack-python (pure-Python fallback) | Python's recursion limit; `RecursionError` is mapped to `StackError` | recursion frames | read; write via `DEFAULT_RECURSE_LIMIT = 1024` | fallback.py:66,586-606 |
| @msgpack/msgpack | decode: no depth limit found (heap-allocated `StackPool`, iterative); encode `maxDepth` 100 | encode: depth argument to `doEncode` | read: unbounded; write: yes (`Too deep objects in depth N`) | Decoder.ts:111-190,640-643; Encoder.ts:9,97,173; README.md:119 |
| msgpackr | no depth option found | recursive `readObject` (inferred from the single recursion comment at unpack.js:506) | neither | unpack.js, pack.js (grep for depth/maxDepth: no hit) |
| System.Text.Json | 64 (reader and serializer); writer default 1000 | `JsonReaderOptions.MaxDepth`; `JsonSerializerOptions.MaxDepth` default is the minimum of reader and writer defaults | read: yes; write: yes (writer 1000, serializer 64) | JsonReaderOptions.cs:13,43; JsonWriterOptions.cs:18; JsonSerializerOptions.cs:27-28,580-593 |
| Newtonsoft.Json | 64 (`JsonReader` ctor; `MaxDepth` nullable) | reader `Depth + 1 > _maxDepth` | read; `JsonWriter.cs` has no MaxDepth match | JsonReader.cs:332,356-359 |
| protobuf C++ | 100 (`default_recursion_limit_`) | recursion budget decremented per nested message/group | read | coded_stream.h:630,1588-1602,1637 |
| protobuf C# | 100 (`DefaultRecursionLimit`) | CodedInputStream recursion | read | CodedInputStream.cs:57,120-147 |
| protobuf Java | 100 (`defaultRecursionLimit`) | CodedInputStream recursion | read | CodedInputStream.java:45,52,521 |
| protobuf upb | 100 (`kUpb_WireFormat_DefaultDepthLimit`) | `d->depth` per decode; max configurable up to a uint16 | read | upb/wire/internal/constants.h:11; decode.c:105,140,192,619,1306-1312 |
| ciborium | 256 (`recurse` counter) | `recurse()` wrapper around array/map/tag decode | read only (cib_ser.rs has no depth/recurs match) | ciborium/src/de/mod.rs:54-74,812,854 |
| cbor2 (Python) | 400 `max_depth` since 5.9.0 (CVE-2026-26209 fix); 6.0.0 (2026-04-28) rewrote the decoder in Rust as iterative, "limited only by the available memory" | containers | read | docs/versionhistory.rst:120-142,172-173 |
| BSON (MongoDB) | 100 levels | "Each object or array adds a level" | the doc page does not say read vs write | mongodb.com/docs/manual/reference/limits/ |
| Go encoding/json | 10000 (`maxNestingDepth`) | scanner `parseState` stack length; the scanner validates the whole input before decoding (`checkValid`) | read; encoder has no depth limit, only pointer-cycle detection after 1000 pointer levels (`startDetectingCyclesAfter`) | scanner.go:146-186; encode.go:298-305,764 |
| simdjson | 1024 (`DEFAULT_MAX_DEPTH`) | parser allocation parameter | read | common_defs.h:49; dom/parser.h:596,599 |

## 2. Detail by library

### 2.1 MessagePack-CSharp (the C# reference's decoder)

- `UntrustedData` and `TrustedData` both set `MaximumObjectGraphDepth = 500`
  (MessagePackSecurity.cs:31, 41; property initialiser 92). `TrustedData` was lowered to 500
  by commit 6b025eab "Limit stack depth to 500 even for trusted data" (2024-10-29) and
  set from `UntrustedData` by 7c087905 (2024-11-28), per the commit history of
  MessagePackSecurity.cs (api.github.com/repos/MessagePack-CSharp/MessagePack-CSharp/commits?path=src/MessagePack/MessagePackSecurity.cs).
  The commit messages give no rationale for the value 500.
- `DepthStep(ref MessagePackReader)` throws `InsufficientExecutionStackException` when
  `reader.Depth >= MaximumObjectGraphDepth`, else increments (MessagePackSecurity.cs:266-273).
  The doc comment says callers must decrement after leaving the edge (:254-260).
- Counting is by contract with formatters, not by the reader: README.md says `DepthStep` and
  `Depth--` "should surround the bulk of any `IMessagePackFormatter<T>.Deserialize` method"
  (README lines ~1161-1175). So content no formatter reads is not counted (inferred from the
  README contract; the CultLib measurement in the question confirms it: an extra trailing slot
  of 100000 nested arrays is accepted).
- Write: no depth check in the serializer or writer in the code fetched (MessagePackReader.cs
  holds `Depth` at :53-75,133; the writer was not fetched). The CultLib question reports a
  3001-deep payload written and then refused on read.
- Advisory GHSA-7q36-4xx7-xcxf / CVE-2020-5234 (published 2020-01-31): "Untrusted data can lead
  to DoS attack due to hash collisions and stack overflow in MessagePack". Affected < 1.9.11 and
  >= 2.0.0 < 2.1.90. Mitigation is opting into `UntrustedData`; the advisory text does not state
  the depth value.
- Advisory GHSA-cj9g-3mj2-g8vv / CVE-2026-48512 (published 2026-06-25), fixed in 2.5.301 and
  3.1.7: the JSON conversion helpers (`ConvertFromJson`, `TinyJsonReader.ReadNextToken`, the
  ext-100 branch of `ConvertToJson`) recursed without consulting `MaximumObjectGraphDepth`; the
  advisory says `UntrustedData` "does not fully mitigate" them. This is the class of defect
  "a depth limit exists but one recursive path skips it".

### 2.2 rmp-serde (the Rust runtime's decoder)

- `Deserializer` has `depth: u16`, initialised to 1024 (decode.rs:181, 213, 294);
  `set_max_depth` clamps to `u16::MAX` (:309-310).
- `depth_count!` decrements, returns `DepthLimitExceeded` when the counter reaches 0, runs the
  body, increments (decode.rs:51-62). Used for arrays (:566), maps (:583) and ext newtype (:616).
  So the usable nesting is 1023 levels (inferred from decrement-then-test-zero).
- `ignored_any` is forwarded to `deserialize_any` (decode.rs:~749-753 `forward_to_deserialize_any!`),
  which goes through the same counted paths: skipped containers are counted.
- Write: `Error::DepthLimitExceeded` exists in encode.rs (:37, 49, 64) but a grep finds no site
  that constructs it (only definition, `source`, `Display`). The writer has no depth check
  (inferred from the three matches).
- Crate version at fetch: 1.3.1 (Cargo.toml). No RUSTSEC advisory about depth was found for
  rmp-serde, rmpv or ciborium in a web search (search-engine result, not a database query).

### 2.3 serde_json

- `remaining_depth: u8 = 128` (de.rs:34, 63); `check_recursion!` decrements and errors with
  `RecursionLimitExceeded` when it reaches 0 (de.rs:1372-1387), used at the `[`/`{` branches of
  `deserialize_any` and the typed deserialize methods (de.rs:1434, 1445, 1750, 1801, 1838, 1849,
  1882). `disable_recursion_limit()` exists behind the `unbounded_depth` feature (de.rs:197-216,
  1357-1370). A third-party analysis (search result, not fetched) states the deepest enterable
  container is level 127.
- `deserialize_ignored_any` calls `ignore_value()` (de.rs:1910-1916). `ignore_value` (:1102-~1215)
  is an explicit loop with a heap `scratch` stack of frames and contains no `check_recursion!`
  (the `check_recursion!` lines in the file are all at 1372+). Reading: ignored values in
  serde_json are walked iteratively and are not depth-limited (inferred from the code; not
  tested). This differs from the question's statement that "serde_json ... count ignored values
  the same way".
- Serializer: grep of ser.rs for depth/recurs returns no match.
- Related: serde-json-wasm GHSA-rr69-rxr6-8qwf / RUSTSEC-2024-0012 (2024-02-09), fixed in 1.0.1
  and 0.5.2: "This was fixed by adding a check for recursion depth"; the advisory text gives no value.
  rustc_serialize RUSTSEC-2022-0004: parsing had no stack-depth limit and "can cause a stack
  overflow, which aborts the process".
- serde_stacker (crates.io description) grows the stack dynamically and is documented as paired
  with `disable_recursion_limit()`; its README warns that Drop, Debug and Display on deep values
  can still overflow. A forum thread reports dropping a deeply nested `Value` (~10 million
  levels) overflowing after a successful parse.

### 2.4 msgpack-python

- C unpacker: `MSGPACK_EMBED_STACK_SIZE` 1024 (unpack.h:19); `start_container` returns -3 when
  `top >= 1024` before it checks for an empty container (unpack_template.h:117-127), and -3 is
  raised as `StackError` (_unpacker.pyx:208-211, 521-524). The docstring says "Raises `StackError`
  when `packed` contains too nested" (_unpacker.pyx:158, 313). Empty containers at depth 1025 are
  therefore also refused (inferred from check order). `skip()` runs the same state machine
  (fallback.py:599-606 shows `skip` raising `StackError`; the C path is the same unpack function,
  inferred).
- Pure-Python fallback: depth is Python's recursion limit; `RecursionError` is converted to
  `StackError` (fallback.py:586-606).
- Packer: `DEFAULT_RECURSE_LIMIT = 1024`; `_pack` raises `ValueError("recursion limit exceeded.")`
  when `nest_limit < 0` and decrements per container (_packer.pyx:42, 259-262; fallback.py:66, 699-711, 801-804).

### 2.5 JavaScript

- @msgpack/msgpack Decoder: iterative, with a heap `StackPool` (Decoder.ts:111-190, loop :640-643);
  no depth option appears in the decoder (grep of "depth" returns only stack-pool lines). The
  README option table lists `maxDepth` default 100 (README.md:119) and Encoder.ts uses it
  (`DEFAULT_MAX_DEPTH = 100`, :9; `doEncode` throws `Too deep objects in depth N` when
  `depth > maxDepth`, :173). So the library is asymmetric: encode bounded, decode unbounded in
  depth (limited by memory). The CultLib question states TS "parse the whole payload"; consistent.
- msgpackr: grep of unpack.js and pack.js finds no maxDepth option or depth counter.
- msgpack5 (another npm package) GHSA-24ch-f2g6-9hhh (2026-10-08): "The decoder has no
  nesting-depth limit for arrays and maps ... exhaust the JavaScript call stack"; fix in 6.1.0
  limits depth to 100 by default, option `maxDepth`, error "Maximum decode depth exceeded".
- msgpack-java: CVE record at vulncheck.com ("through 0.9.12", GitHub issue #1015):
  `MessageUnpacker.unpackValue()` "recursively deserializes arrays and maps without nesting
  depth limits", causing StackOverflowError. Fix value not retrieved.

### 2.6 .NET JSON

- System.Text.Json: `JsonReaderOptions.DefaultMaxDepth = 64` (JsonReaderOptions.cs:13);
  `JsonWriterOptions.DefaultMaxDepth = 1000` (JsonWriterOptions.cs:18);
  `JsonSerializerOptions.DefaultMaxDepth` = the reader's 64, with a comment that it is the
  minimum of the reader and writer defaults (JsonSerializerOptions.cs:27-28). So the serializer
  limit applies to both directions at 64; the bare writer at 1000.
- Newtonsoft.Json: `JsonReader` constructor sets `_maxDepth = 64` (JsonReader.cs:332); exceeding
  throws `JsonReaderException` "The reader's MaxDepth of {0} has been exceeded" (:356-359).
  `JsonWriter.cs` has no MaxDepth match. Advisory GHSA-5crp-9r3c-p9vr / CVE-2024-21907
  (published 2022-06-22, affects < 13.0.1): "Insecure Defaults ... improper handling of
  expressions with high nesting level that lead to StackOverFlow exception or high CPU and RAM
  usage". Advisory figures: deserialising needs >10 million nesting (9.5 MB of `{a:{a:`) for >10 s
  of CPU; serialising (`SerializeObject`, `JObject.ToString`) overflowed the stack at about
  20,000 levels. The advisory's own mitigation example sets `MaxDepth = 128`; the 13.0.1 default
  is 64 (the changelog line was not retrieved; the 64 is read from the code above).

### 2.7 Protobuf

- Default recursion limit 100 in C++ (coded_stream.h:630 comment "100 by default"), C#
  (CodedInputStream.cs:57), Java (CodedInputStream.java:45) and upb (constants.h:11). C# rejects a
  non-positive limit (cs:142-144); upb stores it in a 16-bit field (`upb_DecodeOptions_MaxDepth`).
- Skipped content: GHSA-735f-pc8j-v9w8 / CVE-2024-7254 (published 2024-09-19): in protobuf Java
  (full and lite), Kotlin and JRuby, "Parsing nested groups as unknown fields with
  DiscardUnknownFieldsParser or Java Protobuf Lite parser, or against Protobuf map fields,
  creates unbounded recursions". Fixed in 3.25.5, 4.27.5, 4.28.2. The advisory implies unknown
  (skipped) content had escaped the recursion limit until the fix.
- No write-side limit was looked up.

### 2.8 CBOR, BSON, Go, simdjson

- ciborium: `recurse: 256` in `from_reader` and `deserializer_from_reader`
  (de/mod.rs:812, 854); `from_reader_with_recursion_limit` documents "Set a high recursion limit at
  your own risk (of stack exhaustion)!" (:819-822, 859-862). `recurse()` errors
  `RecursionLimitExceeded` at 0 (:64-74). No depth/recurs match in ser/mod.rs.
- cbor2: advisory GHSA-3c37-wwvx-h642 / CVE-2026-26209 (published 2026-03-23, fixed 5.9.0):
  "uncontrolled recursion" in both the pure-Python and C decoders; PoC is 1000 nested `0x81`
  bytes raising `RecursionError`. The C decoder used `Py_EnterRecursiveCall` (Python's
  interpreter limit). Fix: `max_depth` parameter, default 400, in 5.9.0 (versionhistory.rst:172-173).
  6.0.0 (2026-04-28): decoder rewritten in Rust, iterative, depth "limited only by the available
  memory" (versionhistory.rst:138-140).
- BSON: MongoDB documents "no more than 100 levels of nesting for BSON documents. Each object or
  array adds a level." The page does not state read vs write.
- Go encoding/json: `maxNestingDepth = 10000` "limits the max nesting depth to prevent stack
  overflow" (scanner.go:146-148); `pushParseState` errors "exceeded max depth" when the parse stack
  exceeds it (:181-185). `checkValid` scans the whole input before decode (scanner.go:30), so every
  container, including ones the target type ignores, is counted (inferred from `checkValid` being
  the first step of `Unmarshal`; not read in decode.go). Encoder: `ptrLevel` over 1000 starts
  cycle detection, which is not a depth limit (encode.go:298-305).
- simdjson: `constexpr size_t DEFAULT_MAX_DEPTH = 1024` (common_defs.h:49); `allocate(capacity,
  max_depth = DEFAULT_MAX_DEPTH)`. (The PHP binding defaults to 512 per the PHP manual.)

## 3. Read-side vs write-side, and skipped content

Enforces on write: rmp-serde (variant defined, no construction found: no); msgpack-python Packer
(1024); @msgpack/msgpack Encoder (100); System.Text.Json (serializer 64, writer 1000); Go
encoding/json (no depth limit; cycle detection only); MessagePack-CSharp (no); serde_json (no);
ciborium (no); Newtonsoft (no match in JsonWriter.cs).

Counts skipped/ignored content: rmp-serde (yes, via `ignored_any` to `deserialize_any`);
msgpack-python C unpacker (yes, `skip()` shares the stack); Go (scanner pre-pass, inferred);
serde_json (apparently no: `ignore_value` is iterative and uncounted, inferred);
MessagePack-CSharp (no: counting is by formatter contract); protobuf Java (unknown groups were
unbounded before CVE-2024-7254).

## 4. Stack use per nesting level

No library documents bytes of stack per nesting level. What exists:
- ciborium: "Set a high recursion limit at your own risk (of stack exhaustion)!"
- Newtonsoft advisory: serialisation overflowed the default stack at about 20,000 levels
  (a count, not bytes; thread stack size not stated in the advisory).
- cbor2 advisory PoC: 1000 nested arrays reached the Python recursion limit.
- Rhai docs (search snippet, a different system): default call depth 64 in release, 8 in debug,
  an illustration that debug frames are larger.
- CultLib's own measurement (from the question, not a prior-art source): Rust debug on 2 MiB
  aborts at about 408 containers, which is about 5 KiB per level (2 MiB / 408, inferred);
  release carries 1200+; Python near 1000; TS near 4000.
- serde_json and rmp-serde pick 128 and 1024 respectively; neither source documents how the value
  was derived. Arrow Parquet Variant (docs.rs search result) says it picked MAX_NESTING_DEPTH 128
  "to match serde_json" because validation recurses.

## 5. Not retrieved

The rationale for 500 in MessagePack-CSharp; the Newtonsoft 13.0.1 release note; the fix depth
chosen by msgpack-java and serde-json-wasm; protobuf write-side behaviour; decode.go's use of
`checkValid`; MessagePack-CSharp writer code; simdjson's depth-error semantics.
