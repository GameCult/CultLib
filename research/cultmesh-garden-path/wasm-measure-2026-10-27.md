# Measuring probe: orbs simulation under Mono browser-wasm and NetWasm

Date: 2026-10-27. Serves ruling `cultmesh-garden-path:ruling:browser-sim-runtime` (choice `csharp-wasm`: "the first cut is a measuring probe of download size, startup and 60 Hz tick cost on the orbs game under both Mono browser-wasm and NetWasm, and it can overturn the choice"). Campaign `cultmesh-garden-path`, session label `self-2026-10-27-eureka`. Companion: `browser-sim-runtime-2026-10-27.md` (prior art; this probe replaces its "no measurement" gaps 1 and 2 for this workload only).

Measured, not inferred. Nothing admitted, nothing committed.

## What was built

A standalone C# orbs sim with no CultLib dependency: `Fix` (Q16.16 on `int`, products widened to `long`), `Player {Id, Position, Velocity, Mass}`, `Pellet`, `Shot`; `Movement` (integrate, damp, clamp to a 4096-unit arena, scripted LCG steering), `Eating` (orb-pellet overlap, 200 x 1000 tests per tick), `Hits` (ray vs circle over an 8-tick rewind ring, 50 shots x 200 players). 200 players, 1000 pellets, 50 shots. No floating point, no allocation in `Tick`. FNV-1a 64 state hash over all state. Exports: `Init`, `Tick`, `Hash`, `Noop`, `SetInput(int,int,int)`, `GetPositions() -> int[]`, `PushPositions()` (C# calls a JS import with a `Span<int>`).

Scratch (not in the repo): `C:\Users\Meta\AppData\Local\Temp\claude\F--Projects-CultLib\dfe49a4b-e0ab-440c-81c7-7325d2eb7405\scratchpad\wasm-probe\src\` (a scratch git repo; `shared\` is the sim, `mono\` and `netwasm\` the two hosts, `bench\bench.mjs` the harness, `run.sh` the job).

## Environment

- Yggdrasil via `ygg-verify.sh`, image `mcr.microsoft.com/playwright:v1.55.0-noble` (Chromium 140.0.7339.16 headless), container capped at 4 CPUs, niced, sharing the host with other verify jobs. .NET SDK 10.0.401 installed with `dotnet-install` inside the job; Mono runtime pack 10.0.14; `wasm-tools` workload installed for AOT. NetWasm.Sdk/Templates 0.10.3 (restore needed the NuGet index to catch up once; it worked on retry).
- Pages served from a local Node server on 127.0.0.1, files pre-compressed Brotli-11 in memory and sent with `Content-Encoding: br`; no network throttling; so startup excludes download time and shows decode, compile and run. Cross-origin isolated (COOP/COEP) so `performance.now()` resolves to about 5 us.
- Two full runs of the job (run 3, run 5); the tables use run 5 unless stated. Run 3 agrees within noise on every figure.
- Licence: NetWasm's compiler is under its Community License (free under 250 employees / USD 10M); this is internal evaluation use.

## Native reference

Native .NET 10 JIT, same `Sim.cs`, `Init(12345)` then 3000 ticks: hash `5de317214d945ca9` (hits 1620, eats 1105), 0.30 ms per tick (Stopwatch, console app, 3000 ticks, JIT warm).

## Mono browser-wasm (.NET 10, `Microsoft.NET.Sdk.WebAssembly`, trimmed, `InvariantGlobalization`)

| Measure | Interpreter | AOT (`RunAOTCompilation`) |
|---|---|---|
| Files downloaded | 10 | 10 |
| Raw bytes | 4,476,896 | 7,062,992 |
| Brotli-11 bytes | 1,412,481 | 1,625,283 |
| Largest file (raw / br) | `dotnet.native.wasm` 3,001,422 / 976,306 | `dotnet.native.wasm` 5,519,143 / 1,267,110 |
| `System.Private.CoreLib.wasm` (raw / br) | 1,021,205 / 318,581 | 1,018,133 / 233,418 |
| Cold start to first Tick, median of 10 | 227 ms | 207 ms |
| Warm start (reload, same context), median of 10 | 104 ms | 131 ms |
| Tick median (3,000 ticks, after 600 warm-up) | 1.36 - 1.40 ms | 0.455 ms |
| Tick p90 | 1.48 - 1.49 ms | 0.50 - 0.55 ms |
| Tick p99 | 11.4 ms | 0.74 - 0.89 ms |
| Batch mean (3,000 ticks, one timer) | 1.52 - 1.55 ms | 0.50 - 0.56 ms |
| Ticks over 4 ms (of 3,000) | 47 - 48 | 16 - 17 |
| Max tick | 12.3 - 13.2 ms | 10.7 - 11.0 ms |
| Hash after 3,000 ticks vs native | match | match |

## NetWasm 0.10.3 (`NetWasm.Sdk`, `JsModule`, page host, Compact GC, `-p:NetWasmPublishTarget=browser`)

| Measure | Value |
|---|---|
| Files downloaded | 18 (3 `.mjs` runtime modules, `NetProbe.wasm`, adapters and small json) |
| Raw bytes | 302,974 |
| Brotli-11 bytes | 68,959 |
| Largest files (raw / br) | `netwasm.runtime.*.mjs` 177,895 / 35,052; `NetProbe.wasm` 82,481 / 21,953 |
| Cold start to first Tick, median of 10 | 94.5 ms |
| Warm start, median of 10 | 31.6 ms |
| Tick median | 0.535 - 0.555 ms |
| Tick p90 / p99 | 0.56 - 0.96 ms / 0.91 - 1.05 ms |
| Batch mean | 0.62 - 0.64 ms |
| Ticks over 4 ms (of 3,000) | 18 - 21 |
| Max tick | 11.6 - 11.8 ms |
| Hash after 3,000 ticks vs native | match |

Determinism: all three wasm builds produced `5de317214d945ca9` after 3,000 ticks, both in the per-tick-timed pass and the batch pass, in every repetition, equal to the native JIT hash. No mismatch. Integer and Q16.16 only; this says nothing about floating point.

## Per-call JS interop (microseconds per call, 20,000 calls after one warm call; range over three repetitions in run 5)

| Call | Mono interp | Mono AOT | NetWasm |
|---|---|---|---|
| `Noop()` int return | 0.31 - 0.47 | 0.26 - 0.37 | 0.16 - 0.24 |
| `SetInput(int,int,int)` (input struct as three scalars) | 0.36 - 1.15 | 0.28 - 0.99 | 0.31 - 0.46 |
| `GetPositions()` returns `int[400]` (copy), JS reads two elements | 3.8 - 4.9 | 2.3 - 3.3 | 2.6 - 3.2 |
| `PushPositions()`: C# calls a JS import with a `Span<int>` of 400 (no copy), JS sums it | 5.7 - 6.1 | 3.9 - 5.6 | 1.3 - 1.9 |

A span or array read-out of all 200 positions costs a few microseconds per frame on every toolchain, under 0.1 percent of a 16.7 ms frame. Mono needed `[JSMarshalAs<JSType.MemoryView>]` on the span parameter and `AllowUnsafeBlocks`; NetWasm accepted the same source unchanged apart from `AllowUnsafeBlocks`.

## How each number was taken

- Shipped bytes: the set of URLs the server actually served during the first cold page load (every file the browser requested; the 404 favicon excluded). Raw = file size on disk. Brotli-11 = `zlib.brotliCompress` quality 11, window 24, per file, summed. The SDK-produced `.br`/`.gz` siblings were not requested and are not counted. These are floors: the sim uses almost none of the BCL and no CultCache, MessagePack or CultNet.
- Cold start: a fresh browser context (empty HTTP and compile caches) per run, `page.goto`, wait for `window.ready`; the page records `performance.now()` (from navigation start) right after `Init(12345)` and the first `Tick()` return. Median of 10. Warm: `page.reload()` in one context, 10 times, same metric.
- Tick cost: on the last cold page, `Init(1)` plus 600 ticks of warm-up, `Init(12345)`, then 3,000 `Tick()` calls each wrapped in `performance.now()` (about 5 us resolution), then a second pass of 3,000 ticks under one timer for the batch mean. Repeated three times in the same page. Median, p90 and p99 from the sorted per-tick array.
- Determinism: `Hash()` after each 3,000-tick pass compared with the native JIT hash.
- Interop: 20,000 consecutive calls from a JS loop in the page, mean per call.

## Caveats

- Every variant, including the AOT build and NetWasm, shows the same pattern: 16 - 48 ticks of 3,000 take 10.7 - 13.2 ms against a 0.5 - 1.4 ms median, all near 11 ms. The common value across three different runtimes points to the environment (a niced, CPU-capped container on a shared host, or a Chromium main-thread task), not to the runtimes' GC, but this probe did not isolate the cause. For Mono interpreter p99 it lands at 11.4 ms because 1.6 percent of ticks are hit. A run on a quiet host or on a real client machine is needed before any p99 figure is trusted. At a 60 Hz frame budget of 16.7 ms the spikes still fit, with little left for rendering.
- The sim is a probe workload, not CultCache documents; the ruling's prerequisite (AOT-safe CultCache registration) is not tested here. NetWasm refuses reflection and CultCache's registration reflects; whether CultCache can run under NetWasm is a separate cut.
- Sizes will grow with real BCL use (collections, LINQ, MessagePack, CultNet) and fastest-growing on Mono; NetWasm links only reachable code, Mono ships a fixed runtime of about 1.4 MB Brotli before any code.
- Mono AOT here is the default mixed mode; Mono interpreter has the jiterpreter on by default. Network time was not simulated: a 1.4 MB Brotli payload on a 10 Mbit link adds about 1.1 s that this table does not show; NetWasm's 69 KB adds about 0.05 s.
- NetWasm is 0.10.3, pre-1.0, single vendor, compiler under the Community License; the restore failed once because the NuGet index had not yet listed 0.10.3 for the container, then passed.
- Single machine, single browser (headless Chromium 140). No Firefox or Safari, no phone.
