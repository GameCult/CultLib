# Where simulation code runs in the browser: prior art (C# wasm, dual implementation, other arrangements)

Date: 2026-10-27. Read-only Eyes pass. Facts with evidence pointers; no recommendation.

**Serves** open fork 1 of `F:\Projects\CultLib\docs\cultmesh-garden-path-target.md`: "Where simulation code runs in the browser: C# compiled to WebAssembly, C# on the server with TypeScript systems in the browser, or another arrangement." Campaign `cultmesh-garden-path`, session label `self-2026-10-27-eureka`. Companion (not repeated): `research/cultmesh-garden-path/prior-art-2026-10-27.md` (frameworks, scale-out).

## Tags and method

- VERIFIED: read in a fetched source this pass (URL given). Fetching went through a summarising tool; quoted figures are as it returned them.
- SECONDARY: search-result summaries, forum posts, third-party blogs, one project's own claims.
- UNVERIFIED: inferred or not found. Gaps are listed, not filled from memory.
- No measurement was run locally (no wasm-tools workload install, no publish). Every size and speed figure below is someone else's.
- Search returned no official .NET 10 hello-world size, startup time, or per-call interop number. Those gaps are real.

---

## A. C# compiled to WebAssembly

### A1. Runtime options and their status

- Shipping runtime for `browser-wasm` is Mono (Blazor WebAssembly runtime; usable without Blazor UI via `[JSImport]`/`[JSExport]` since .NET 7). VERIFIED (https://meziantou.net/using-dotnet-code-from-javascript-using-webassembly.htm: ".NET 7 ... run any .NET method from JavaScript without the full Blazor framework").
- Microsoft's MAUI post says Blazor WebAssembly keeps Mono in .NET 11. SECONDARY (search summary of https://devblogs.microsoft.com/dotnet/dotnet-maui-moves-to-coreclr-in-dotnet-11/; page not fetched).
- CoreCLR-on-wasm migration: tracking issue dotnet/runtime#121511 targets "an early preview in the .NET 11 timeframe" on the CoreCLR interpreter, with R2R and RyuJIT later and .NET 12 for stability. VERIFIED plan text (https://github.com/dotnet/runtime/issues/121511). The same page's fetch says the maintainer called the work "mostly done" on 2026-08-11 and closed it with debugger and tests outstanding; a search summary said "early, experimental". The two conflict on how far along it is. SECONDARY / unreconciled. A July 2026 PR (dotnet/runtime#131420) fixes publishing for an "experimental CoreCLR wasm SDK". SECONDARY.
- NativeAOT-LLVM (dotnet/runtimelab branch `feature/NativeAOT-LLVM`): described as "an experimental fork of the Native AOT toolchain that uses LLVM", WebAssembly the only target, "for experimentation ... may or may not make it into the main dotnet/runtime repo". VERIFIED (https://github.com/dotnet/runtimelab/tree/feature/NativeAOT-LLVM). In dotnet/runtime#113979 a maintainer says the main focus is Blazor, "which is not NativeAOT-compatible, so NativeAOT-LLVM is not the default yet"; another commenter: still experimental with unresolved issues. VERIFIED (https://github.com/dotnet/runtime/issues/113979). SpacetimeDB's C# module build treats NativeAOT-LLVM as its recommended path after the older wasi-experimental workload was removed from .NET 9+. SECONDARY (mirror of a SpacetimeDB PR, dated May 2026).
- Godot: C# web export is not in official releases. Forum thread, last post 2026-04-17, quotes the GodotCon May 2025 post ("cannot commit to a specific timeline yet"), draft PR #106125 by raulsntos. VERIFIED (https://forum.godotengine.org/t/is-there-an-update-on-exporting-c-projects-to-web/128821). Community builds exist and one user reported a "null function" error. VERIFIED (same thread).

### A2. Download size

- .NET 7 minimal exported method, trimmed: 2.81 MB uncompressed, about 819 kB Brotli; `dotnet.wasm` 342 kB, `System.Private.CoreLib.dll` 407 kB, `dotnet.js` 54 kB, JS interop assembly 14 kB, app DLL 3 kB. Author calls the baseline "quite large"; other assemblies are pay-for-use through the linker. VERIFIED (meziantou URL above).
- A .NET 10 figure of 1.3 MB runtime (versus 1.8 MB on .NET 8) comes from a vendor tutorial with no method. SECONDARY, weak (https://dev.to/ironsoftware/webassembly-and-net-10-run-c-in-the-browser-tutorial-4ffo). Whether 1.3 MB is compressed is not stated there.
- Official docs: trimming reduces size, AOT increases it, `InvariantGlobalization`/`InvariantTimezone` drop ICU and timezone data, gzip and Brotli assets are published by default. VERIFIED (https://github.com/dotnet/runtime/blob/main/src/mono/wasm/features.md).
- AOT cost in a real C# game port (FlatRedBall2 on KNI/BlazorGL): AOT added a ~40 MB `dotnet.native.*.wasm` and about 37 MB to the payload; frame time about 8-12 ms with the IL interpreter kept versus 57-98 ms JIT baseline (their numbers, their game). VERIFIED as reported (https://github.com/vchelaru/FlatRedBall2/issues/1304). Not a minimal app; sets an order of magnitude for AOT of a framework-heavy game.
- Avalonia deployment guide: Brotli typically shrinks .wasm by 60-70%; trimming on by default in release. SECONDARY (https://docs.avaloniaui.net/docs/deployment/webassembly, via search summary).
- UNVERIFIED: a measured .NET 10 minimal `browser-wasm` publish with Brotli sizes and AOT/non-AOT split. Not found.

### A3. Startup time

- Official guidance only: set `EmccInitialHeapSize` to cut memory-growth delay, preload `dotnet.boot.js`, benchmark on real devices; AOT "can slow startup"; the runtime and assemblies are fetched, hashed and cached by the browser (Blazor fingerprints and integrity-checks each file, `blazor.boot.json` removed in .NET 10, manifest inlined in `dotnet.js`). VERIFIED (features.md above; https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/bundle-caching-and-integrity-check-failures).
- No millisecond startup figure found for any .NET version. UNVERIFIED.
- Wavedash's C# template (net9.0, `browser-wasm`, `InvariantGlobalization`) boots by `await dotnet.create()` then runs exports; no timing or size stated. VERIFIED (https://docs.wavedash.com/engines/csharp).

### A4. JS interop cost

- Docs: interop operations are "roughly an order of magnitude slower" than in-.NET calls but "relatively fast"; `JSObject`, `Exception`, `Task`, `ArraySegment` create a `GCHandle` and proxy and "carry significant performance overhead"; `Span<byte|int|float|double>` marshals to JS as `MemoryView` with no copy, valid only for the call's duration; JS cannot return `Span` to .NET as an import parameter. Search summary of the .NET 9 page; the .NET 10 page was fetched and the table lines (MemoryView rows, GCHandle remark at lines 241-310 of the saved output) agree. VERIFIED (https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/?view=aspnetcore-10.0). The "order of magnitude" sentence itself is SECONDARY (search summary).
- Benchmark in dotnet/runtime#113979, 100,000 calls each: Rust wasm-bindgen 5.91 ms (echo number); .NET 9 Mono AOT 65.49 ms (about 0.65 us/call); NativeAOT-LLVM 18.85 ms to 45.08 ms depending on machine. Echo of a struct via JSON round-trip: Rust 238 ms, Mono AOT 1082 ms, NativeAOT-LLVM 191-375 ms. Closed "not planned" in January 2026 with no Mono AOT work committed. VERIFIED (https://github.com/dotnet/runtime/issues/113979).
- A third-party SDK (NetWasm) measures a synchronous `[JSImport]` at about 1.4 us (ulong return) and 0.9 us (void, three scalars) and about 15 ns for a direct wasm-to-JS import. SECONDARY (https://github.com/zion-sati/NetWasm/issues/115, search summary).
- Marshalling pitfalls in a game port: string-only marshalling forced base64 for byte data; `System.Text.Json` reflection disabled on wasm, source-generated contexts required; AOT with IL stripped breaks reflection paths; module-less `[JSImport]` works only with names starting `globalThis`. VERIFIED as reported (FlatRedBall2#1304 above).
- UNVERIFIED: any official per-call microsecond figure for .NET 10 JSImport/JSExport.

### A5. Threading

- Multithreaded wasm is opt-in: `<WasmEnableThreads>true</WasmEnableThreads>`, separate runtime build, server must send COOP and COEP headers, `[JSExport]`/`[JSImport]` limited to the main thread, blocking the main thread discouraged. VERIFIED (features.md above).
- Uno Platform's bootstrap pre-creates 4 web workers for its threading mode and needs the same headers. SECONDARY (https://platform.uno/docs/articles/external/uno.wasm.bootstrap/doc/features-threading.html, search summary).
- Celeste browser port (MercuryWorkshop/celeste-wasm) uses "a multithreaded and JIT compiling version of mono-wasm backported from .NET 10 to .NET 9", renders on a "deputy thread", uses about 600 MB memory, "too high for low-end devices". VERIFIED (https://github.com/MercuryWorkshop/celeste-wasm; Hackaday 2025-10-17, https://hackaday.com/tag/net).
- Unity Web: managed C# threads unsupported, reason given "WebAssembly's lack of multithreaded garbage collection". VERIFIED (https://docs.unity3d.com/6000.0/Documentation/Manual/webgl-technical-overview.html).

### A6. GC under a 60 Hz loop

- .NET wasm docs say nothing on GC behaviour beyond heap growth: growth takes time and can fail on memory-limited devices (hence `EmccInitialHeapSize`/`EmccMaximumHeapSize`); desktop memory 256-512 MB recommended, over 1 GB needs testing, over 2 GB experimental. VERIFIED (features.md).
- A dotnet/runtime commenter said the .NET GC has a conservative mode that needs no multi-threading support. VERIFIED as relayed (dotnet/runtime#121511).
- Unity Web: "garbage collection runs once at the end of each frame, rather than incrementally". VERIFIED (Unity page above). That is Unity's runtime, not .NET's.
- UNVERIFIED: measured GC pause times for Mono-wasm in a 60 Hz loop with a given allocation rate. No source found. Local-allocation guidance (zero allocation in the step) is general practice, not a finding.

### A7. Numeric determinism: integer and fixed-point versus float

- WebAssembly spec design note: nondeterminism is "limited, local"; sources are shared-memory thread races, NaN bit patterns and sign bits from arithmetic, relaxed SIMD, resource exhaustion, and host-call order. Integer operations are not in the list. VERIFIED (https://github.com/WebAssembly/design/blob/main/Nondeterminism.md). IEEE-754 basic float operations (+, -, *, /, sqrt) are specified; only NaN payload is free.
- Rust crate docs (alice-det-math): +, -, *, /, sqrt give the same bits on SSE2, aarch64, wasm32; sin/exp/ln come from the platform libm and differ in the last ulp across macOS, glibc, MSVC and wasm; `mul_add` fuses on FMA hardware. SECONDARY, single crate's claim (https://docs.rs/alice-det-math).
- bevy_ggrs docs: its f32 example is deterministic on the same platform but "may produce different results across different CPU architectures or operating systems". SECONDARY (https://github.com/gschup/bevy_ggrs, search summary).
- .NET-specific: a hydrology vendor (eWater) found `Math.Pow`/`Math.Exp` differing across machines in 0.09% of random calls and replaced them. SECONDARY, 2019, old runtime (https://ewater.atlassian.net/wiki/spaces/SC/pages/51652397).
- UNVERIFIED: whether RyuJIT contracts `a*b+c` into FMA on x64/arm64 and whether Mono-wasm (interpreter, jiterpreter, AOT) does not, so whether plain float arithmetic is bit-identical between .NET native and .NET wasm. Whether `Math.Sin/Cos/Pow` in .NET wasm route to Emscripten's libm versus the host's CRT. No source found for either.
- UNVERIFIED but implied by the above: `int`/`long` arithmetic and shifts are defined by both the CLR and the wasm spec; a fixed-point type built from them has no listed source of divergence between targets.
- Local state: `F:\Projects\CultLib\packages\cultmath` is a C# library with GLSL lowering and mirror tests; a search for `Fix64|Fixed64|struct Fix` found nothing in `src` or `packages` C# (one Rust test file matched). `Fix2` in the target document is illustrative, not existing code.

### A8. Existence proofs and counter-examples

- Unity WebGL/Web (C# via IL2CPP to wasm): ships widely. Empty-project build 14.7 MB falling to 12.5 MB Brotli with "Disk Size with LTO" (CrazyGames). VERIFIED (https://docs.crazygames.com/resources/optimization-tips/). FRVR: runtime about 5 MB "empty", about 7 MB "minimal", targets about 8 MB initial. SECONDARY (https://docs.frvr.com/engines/unity/optimization/, search summary).
- Godot C#: no official web export (A1).
- .NET wasm games found: a Celeste port (mono-wasm backport, runs Everest mods; hacky by its own description), an Uno Platform space shooter (hobby), Wavedash's Pong template, FlatRedBall2/KNI BlazorGL, R136.NET text adventure. SECONDARY (search summaries: https://hackaday.com/tag/net, https://platform.uno/?p=9866, https://github.com/rbergen/R136.NET). No commercial .io-class game on .NET wasm found. UNVERIFIED that none exists.
- MonoGame has no official web target; KNI is the community route. SECONDARY (https://docs.wavedash.com/engines/monogame, search summary).

---

## B. Two implementations (C# server, TypeScript client)

- Colyseus 0.18: prediction needs the local-player `step` to be "the SAME function the server runs"; the docs' shared module is TypeScript imported by both sides. Prediction is documented "across the official SDKs" with examples for TypeScript, C#, Lua, Haxe, GDScript, Dart; `unreliable` input mode only in the JS/TS SDK over WebTransport; some options missing in GDScript and Flutter. Server must advertise a fixed tick via `setFixedTimestep`. Divergence is detected by `warnOnDivergence` and drift telemetry. VERIFIED (https://docs.colyseus.io/netcode/client-prediction). The 0.18 announcement shows prediction APIs only in the JS/TS SDK and says shared code is imported by both sides. VERIFIED (https://colyseus.io/blog/colyseus-018-is-here/). The server is Node/TypeScript, so a non-TS client's `step` is necessarily a second implementation or a port; the pages do not describe how the C# example gets its step. UNVERIFIED which.
- SpacetimeDB TypeScript client: reducers called as promises, subscription cache with `onInsert/onUpdate/onDelete`; the page describes no optimistic update or prediction. VERIFIED (https://spacetimedb.com/docs/clients/typescript). Server modules are Rust or C#, so there is no shared simulation code to predict with.
- Nakama: server runtime Go/Lua/TypeScript; developer owns sync and prediction (see companion §8). VERIFIED there.
- Test-vector mechanism in the field: only drift telemetry (Colyseus) and desync-debug guides (bevy_ggrs) were found; no framework found that ships a cross-language conformance suite for predicted code. UNVERIFIED that none exists.
- Local state (CultLib): runtimes live in `F:\Projects\CultLib\packages\` (`cultcache-ts`, `cultnet-ts`, `cultmesh-ts`, `cultmesh-browser`, `cultcache-rs`, `cultmesh-rs`, `cultmesh-py`, `cultmesh-kotlin`) with wire parity against the C# reference. That parity covers serialisation and protocol, not simulation code; no simulation systems exist in the repo yet.

---

## C. Other arrangements

### C1. Shared Rust (or C++) core compiled to native and wasm

- Several hobby and small-studio projects claim one deterministic core on server (native) and browser (wasm) with client prediction (a Rust engine for web/iOS/Android/server with rollback for 20-player sessions; a Rust+wasm Pong on Cloudflare; a C++20/Raylib game via Emscripten). SECONDARY, project pages from one search, none read in depth. The C++ project listed native-versus-wasm determinism as a challenge.
- Ambient (Rust) runs wasm only on the server, server-authoritative with no prediction. SECONDARY.
- bevy_ggrs with matchbox (WebRTC) is the rollback route in Rust; its browser demo is "currently unmaintained". SECONDARY (https://github.com/gschup/bevy_ggrs, search summary).
- Interop cost reference point: Rust wasm-bindgen was the fastest in dotnet/runtime#113979 (A4).

### C2. C# to JavaScript transpilers

- Bridge.NET: no maintenance data found. UNVERIFIED.
- h5: fork of Bridge; NuGet `h5-compiler` 26.3.65450 targets net10.0. SECONDARY (https://www.nuget.org/packages/h5-compiler).
- Transpose (Curiosity.ai): "next generation of the h5 compiler, rebuilt around a Roslyn translator and rebranded"; compiler runs on .NET 10; projects target .NET Standard 2.0/2.1; NuGet versions 26.7 to 26.8 in 2026; legacy Bridge/NRefactory pipeline removed; license not stated on the docs page ("All rights reserved" footer). VERIFIED docs text (https://docs.curiosity.ai/transpose/), NuGet versions SECONDARY. The docs page says nothing on integer overflow, `long`, `float`, `decimal` or struct semantics in the JS output. UNVERIFIED. A fetch of the h5 core-concepts page returned 404, so those semantics are unread.
- Not examined: CSharp.lua, SharpKit, Duality, Blazor-less `Uno` WebAssembly (Uno uses the .NET wasm runtime, not transpilation).

---

## D. How current .io games ship client code, and payload tolerance

- diep.io: its client loads an Emscripten-built WebAssembly module (community reverse-engineering repo has "emscripten" and "asm.js conversion" sections; source language not stated). VERIFIED the repo lists these sections (https://github.com/ABCxFF/diepindepth); the repo is no longer actively worked on. Whether the wasm holds simulation or prediction: not stated. UNVERIFIED.
- agar.io, slither.io, krunker.io: no payload size or client architecture found. UNVERIFIED. DevTools inspection of the live sites was not done.
- Poki (2018 W3C games workshop, 174 Poki games, desktop only): Conversion To Play (players finishing load / players starting) at 10 MB initial download is about 0.8 in the US, about 0.7 in Brazil, below 0.6 in Egypt; "below roughly 10 MB every MB really counts"; above 40 MB there is "lots to gain" from post-loading. VERIFIED (https://www.w3.org/2018/12/games-workshop/papers/size-matters.html). Eight years old.
- Enclave Games recommends a micro-client under 5 MB with remaining assets streamed. VERIFIED, opinion without data (https://enclavegames.com/p/browser-games-lose.html).
- 2026 State of Web Gaming report (2,000 weekly web gamers): 46% had quit a mobile game for slow load, 32% on computers/consoles. SECONDARY (https://www.sentinelassam.com/cities/guwahati-city/web-gamers-quit-slow-apps-as-browser-play-gains-ground-on-social-media-study, search summary).
- Abratabia rule of thumb: about 5 MB initial transfer, JS parse under 500 ms. SECONDARY, unmeasured guidance.
- CrazyGames gives no numeric size threshold on its optimisation page. VERIFIED (https://docs.crazygames.com/resources/optimization-tips/).

---

## E. Gaps worth noting (not recommendations)

1. No measured .NET 10 minimal `browser-wasm` Brotli size, cold/warm startup, or 60 Hz GC behaviour was found; the closest is .NET 7 at 819 kB Brotli and a vendor claim of 1.3 MB runtime for .NET 10.
2. Native-versus-wasm float bit-identity for .NET (FMA contraction, `Math.*` routing) has no source; integer and fixed-point are unlisted as divergence sources by the wasm spec.
3. Mono-wasm is the shipping .NET browser runtime; CoreCLR-on-wasm and NativeAOT-LLVM are both pre-default, with conflicting reports on how far CoreCLR-wasm has come.
4. Colyseus, the nearest prediction-bearing framework, requires one shared `step` function and documents it in TS with ports in other SDKs.
5. No .io-class game on .NET wasm and no published .io payload sizes were found.

## Addendum (Self, 2026-10-27): NetWasm, and CultCache's reflection

Source: https://github.com/zion-sati/NetWasm README, fetched 2026-10-27 (operator pointer from HN). Self-reported, not measured by us.

- An independent compiler, CoreLib, runtime and SDK: CIL to WebAssembly directly, generics specialised, only reachable runtime support linked, wasm-opt always. Not NativeAOT-LLVM, not Mono.
- Print-42 workload, Compact GC: 38,919 B raw, 13,213 B Brotli-11 (Rust size profile: 16,963 B). Empty Blazor AOT app for context: about 12.3 MB (.NET 10).
- Benchmarks (Wasmtime, M1 Max, 15-run medians): 41-73% less time than Mono AOT on allocation-heavy workloads; parity on n-body and CRC-32. Run in Wasmtime, not a browser.
- Outside the profile, refused at compile time: runtime reflection, `dynamic`, runtime assembly loading, managed threading, named-culture globalisation. Expression trees without general reflection; JSImport scalar calls are direct wasm imports, spans cross as typed-array views.
- Floating-point determinism: not addressed.
- Licence: compiler and tooling under the NetWasm Community License 1.0 (free under 250 employees and USD 10M revenue, paid tiers above); runtime libraries and generated code MIT.
- Maturity: 0.10.3, pre-1.0, contracts may change between minors; 269 stars, 301 commits, single vendor.

CultLib consequence (source read, corrected same day): the C# path reflects at document registration, not necessarily per record. `CultCache.BuildDescriptor`/`DiscoverMembers` (`src/GameCult.Caching/CultCache.cs` :689, :1087; `GetProperties` :1105, :1138; `MakeGenericType` :1330; `ConstructorArity` :1277) walk members and constructors to mirror MessagePack's member rules for the descriptor and schema catalog, and `CultDocumentResolver` (`src/GameCult.Caching.MessagePack/CultDocumentMessagePackSerialization.cs` :14-30) builds `CultRecordRef<>` formatters with `MakeGenericType` + `Activator.CreateInstance`. Document formatters come from MessagePack's `StandardResolver` (:61), which in MessagePack 3.x prefers source-generated formatters and falls back to the IL-emitting `DynamicObjectResolver` only for types with none. An earlier version of this paragraph said the codec emits IL at runtime; that rested on a code comment (:1274) and was not established. Whether every [CultDocument] type gets a generated formatter, and what NativeAOT and NetWasm say about the registration path, is for a probe to establish.
