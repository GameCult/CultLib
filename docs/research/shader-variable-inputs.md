# Shader variable-length inputs: how shader libraries take a span, and what CultMath's build route allows

For `cultmath-tapes:follow_up:shader-weighted-lattice-loop` and `cultmath-tapes:follow_up:lattice-shader-pins` (campaign `cultmath-tapes`).
Imagination pass, agent `imagination-ag4-shader-loop`, session `self-2026-10-16-ag4`, 2026-10-17, against CultLib `origin/main` `e2051c5a`
and the build branch `hands/cultmath-glsl-build-dxc` `23ac4e89`. Probes are in `docs/research/shader-variable-inputs/`.

Tags: [src] primary source text read this pass; [web] search summary only; [probe] run this pass, output quoted below.

## 1. The question

C#'s `weighted_lattice_cells` reads the body span once per lattice cell (the weight is the exact power border of the
cell centre), and the ring search around it is the part a consumer cannot safely write by hand: the exit bound,
the two passes and the box-independent fold order. The shader port has the four steps only. A shader function cannot
take `ReadOnlySpan`, the library targets GLSL ES 3.00 fragment shaders (WebGL2: no storage buffers), and the GLSL is
built from the HLSL by dxc `lib_6_3` SPIR-V plus SPIRV-Cross (ruling `glsl-es-via-dxc-spirv-cross`).
So how do bodies reach a shader `weighted_lattice_cells`?

## 2. Prior art: how shader code that ships as a library takes a variable number of inputs

- [src] Unity URP (`ShaderLibrary/Input.hlsl`, `RealtimeLights.hlsl`, Graphics `master`): a fixed capacity
  `MAX_VISIBLE_LIGHTS` chosen per platform (`MAX_VISIBLE_LIGHT_COUNT_LOW_END_MOBILE` under `SHADER_API_GLES30`,
  `_MOBILE` for GLES3/WebGPU, `_DESKTOP` otherwise), constant-buffer arrays `float4 _AdditionalLightsPosition[MAX_VISIBLE_LIGHTS]`
  and the like, with `StructuredBuffer<LightData>` only behind `USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA` (defined 0). The
  count is a uniform read through `GetAdditionalLightsCount()`, and `GetAdditionalLight(uint i, float3 positionWS)` is
  the accessor; `LIGHT_LOOP_BEGIN(lightCount)` / `LIGHT_LOOP_END` macros hide the cluster versus plain loop. The
  pipeline owns the storage and the accessor; user shaders call them. Capacity plus count, storage owned by the include.
- [src] Filament (`shaders/src/surface_light_punctual.fs`, `main`): the loop is the engine's, over a froxel record
  (`froxelsUniforms.records`, then `froxelRecordUniforms.records`) giving `recordOffset` and `count`; each light is
  `lightsUniforms.lights[lightIndex]`, a uniform-block array. Material code never sees the loop. The engine owns both
  the storage and the loop because it also owns the uniform layout.
- [src] three.js (`ShaderChunk/lights_fragment_begin.glsl.js`, `dev`): `for ( int i = 0; i < NUM_POINT_LIGHTS; i ++ )`
  under `#pragma unroll_loop_start`, reading `pointLights[ i ]`; `NUM_POINT_LIGHTS` is a define the renderer writes,
  so the shader is regenerated per light count. Count as a compile-time constant; the library is source text.
- [web] Bevy: clustered forward needs storage buffers; on WebGL2 it falls back to a uniform buffer with a capped
  object count (`MAX_UNIFORM_BUFFER_CLUSTERABLE_OBJECTS` 204 in an older `bevy_pbr`, asserted to fit 16384 bytes; the
  Many Lights example says 256 lights under `WGPU_SETTINGS_PRIO=webgl2`). Capacity plus count where storage is absent.
- [web] lygia (README conventions, via search): reuse by `#ifndef` / `#define` options the caller sets before the
  include (`SAMPLER_FNC(TEX, UV)`, `MYFUNC_TYPE`, per-function sampler overrides). Caller-supplied accessor macros; the
  library ships as source text, which is what lets a macro reach inside a function body.

The pattern that splits them: a caller-supplied accessor (lygia, URP's accessor functions, three.js's define) works
where the library is source text the caller's preprocessor sees. Where the library is compiled or its storage layout is
fixed (URP's GLES path, Bevy's WebGL2 fallback, Filament's uniform blocks), the input is a fixed-capacity array plus a
count, and the capacity is a named library constant.

## 3. What the build route allows (probes)

| Candidate | HLSL (dxc) | Library SPIR-V and SPIRV-Cross GLSL | C# mirror (`TransformHlslToCSharp`) | Verdict |
|---|---|---|---|---|
| Fixed-size array parameter plus count | compiles, `lib_6_3` and `cs_6_0` [probe P1, P3] | see P2 | needs one transform (`T x[N]` parameter to `T[] x`) | see section 4 |
| Caller accessor macro (`CULTMATH_BODY(i)`) | the library build defines no accessor, so the function is absent [probe P4] | absent from the built GLSL: SPIRV-Cross output carries no consumer macro | the mirror would need the macro too | refused by the route |
| Bodies packed into library-owned constants | a library cbuffer or uniform | a uniform block in a function-only library; a binding the consumer does not own | a static field | refused (P5, by construction, not run): a library may not own a consumer's binding |

Probes (files in `shader-variable-inputs/`):

- [probe] P1, local, dxc 2026_07_29 (`.tools/dxc`): `lattice-array.hlsl` (CultMath.hlsl, `prototype.hlsl`, one export)
  compiles with `-T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -Od`. The SPIR-V types the array as
  `OpTypeArray %v4float %uint_16` behind a Function pointer, and `-Od` copies it at each call (`param_var_bodies`).
- [probe] P3, local: `entry-cbuffer.hlsl` passes a cbuffer array directly and a `StructuredBuffer` copied into a local
  array; both compile to SPIR-V (`cs_6_0`) and to DXIL.
- [probe] P4, local: `macro-accessor.hlsl` with no `CULTMATH_BODY` defined fails, `use of undeclared identifier`: a
  macro-parameterised function does not exist in a library compiled without the caller's macros, so the built GLSL
  cannot carry it. Shipping it would need a hand-kept GLSL text beside the build, a second owner the ruling removed.
- [probe] P2, Yggdrasil job (`probe.sh` at `23ac4e89`, sdk:10.0, 2026-10-17): with the prototype appended to
  `CultMath.hlsl` and two build-tool edits (`wrappers.py` strips `[N]` from an argument name; `partition.py` accepts
  `static const int`), `build-glsl.sh` is green ("build shape ok", "suffixed ids: 0; inout: 0") and emits
  `const int CULTMATH_MAX_SITES = 16;`, `vec4 cultmath_power_border(vec3 p, vec4 sites[16], int count)` and
  `vec4 cultmath_weighted_lattice_cells(vec2 p, float time, vec4 bodies[16], int count, ...)`. A `#version 300 es`
  fragment consumer passing `uniform vec4 zone[CULTMATH_MAX_SITES]` compiles with glslang.
- [probe] Same job, lavapipe (`llvmpipe (LLVM 20.1.2, 256 bits)`), 257 cases from `ref/Program.cs` (the ring-17 point
  of `TheCapIsReachedAndMatchesTheFullBox` and 7 neighbours, four looks, five normals including (3e-30, -4e-30) and
  (1e30, 2e30), a zero normal at stretch 1, counts 0, 1, 3, 6, 16), judged against C#'s bits at 2.2e-6 max(|v|, 1):

| Variant (HLSL route unless named) | bit-exact | over bound | worst |
|---|---|---|---|
| built GLSL | 237 | 0 | 1.19e-6 |
| HLSL (equal to built GLSL bit for bit) | 237 | 0 | 1.19e-6 |
| M1 exit at `0.5 k` | 236 | 1 | 4.28e-3 |
| M2 exit ignores the smooth band (`>= f2`) | 226 | 13 | 8.33e-2 |
| M3 normaliser drops the divide by the largest component | 148 | 96 | 1e30 |
| M4 normaliser drops the divide by length | 1 | 208 | 0.259 |
| M5 smooth step loses its `min` cap | 153 | 85 | 2.26e-3 |
| M6 box pass skips its first row | 228 | 9 | 0.807 |
| M7 exit one ring late (`0.5 (k - 2)`) | 237 | 0 | 1.19e-6 |

M7 is equivalent: the result does not depend on the box (the proof above `weighted_lattice_cells`), so a late exit
costs rings and changes nothing. M1 is killed by one case of 257, the ring-17 point: an early exit only matters where
a far ring wins, so the device family must carry several such points.

## 4. Decision

The fixed-size array plus count is the only candidate the route admits, and it is the shape the compiled-library
prior art uses (URP's GLES path, Bevy's WebGL2 fallback, Filament's uniform blocks). The capacity is a named library
constant, `CULTMATH_MAX_SITES`, shared by every span-taking shader function. Its value is
`cultmath-tapes:question:shader-site-capacity` (Aetheria zones: p50 17, p99 57, max 80 bodies, gravity map B28).

The shader recomputes each distance in the box pass instead of caching it: C#'s 75 x 75 stack cache is not a shader
option, and recomputing gives the same value, as the probe's bit-exact HLSL and built-GLSL agreement shows.
Reflection cannot pass a `ReadOnlySpan`, so the mirror and the fixture reach the C# span functions through one
explicit adapter table rather than `MethodInfo.Invoke`.

## 5. lattice-shader-pins

- The smooth-step family draws m in [f1, f1 + smoothing), so the cap never binds. The cap binds when m = f1 + smoothing,
  d = f1 + x smoothing with x around 1.3 to 1.9, and smoothing above about 0.25: next - m = (d - m) h - h (1 - h)
  smoothing / (1 + 3 smoothing) is positive there (for smoothing 1 and d - m = 0.5, h = 0.156 and next - m = +0.045).
  M5 above is killed by the whole function already; the step family should kill it on its own.
- Platform values: `8b31e47e` rewrote 7 platform-family entries of `glsl-parity.json` from "Ubuntu 24.04.5 LTS" to
  "Microsoft Windows 10.0.26200", while `7ddb1040` recorded the lavapipe bounds on Yggdrasil against that fixture.
  The bounds, the lavapipe job and the build route all run in Yggdrasil's sdk:10.0 Ubuntu image, so that image
  owns the fixture's platform values too. A write on another OS is refused rather than reviewed.
