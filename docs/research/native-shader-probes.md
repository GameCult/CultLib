# Shader runner probes: the HLSL-to-GLSL ES toolchain, lavapipe on Yggdrasil, native ANGLE on Starfire

For the cultmath-tapes questions that replace `device-run-driver`: `glsl-es-translation-owner`,
`conforming-shader-run` and `real-device-shader-run`. These are body facts measured by
imagination-ag4-shader-runner (session self-2026-10-16-ag4) on 2026-10-16, between about 14:10 and
15:20 UTC (16:10 to 17:20 CEST). Every probe read CultLib at f697e03f (origin/main). Companion to
`docs/research/native-shader-testing.md` (f697e03f) and `docs/research/device-capture-runners.md` (c9d3e672).

The probe scripts are in this directory under `p/`: `probe.sh` and `lib.sh` (Yggdrasil jobs), `fam.py`
(the per-family shader generator and bit judge), `vkrun.c` (an 87-line Vulkan compute host) and
`angle_native.py` (a 96-line native ANGLE host). They are throwaway probe code, not a proposed
implementation. The logs are `q12-run3.log` (lavapipe) and `lib-run4.log` (library route). The earlier
runs are superseded: run1 used a raw-ulp metric, run2 died on CRLF, and lib-run1 to lib-run3 failed on
the capability and grep problems recorded below.

## Metric

Each fixture family has 256 cases, and the fixture records the C# result bits. "Exact" means all 256
cases are bit-identical to the C# fixture. Otherwise the worst error is given in units of
2^-23 * max(|expected|, 1) (written "u1" below), the scale the fixture's ulp-bounded checks use. NaN and
Inf cases are compared only as NaN-versus-not-NaN. These figures describe the data. They are not the
fixture's own judge, which is C# and per family: GlslMirrorTests tolerances, and the `check` text on 10
families.

## The fixture surface (glsl-parity.json at f697e03f)

- 109 families, each with 256 cases, encoded `args -> result` as hex 32-bit words. There are at most 32
  words per case.
- 105 families use only float, int, vecN, ivecN and mat3 (mat3 appears as a return only). The other 4
  take or return structs: `CultCellular cultmath_cellular(vec3)`, `CultPhasor cultmath_phacelle(vec3,
  vec3, float, float)`, and `SpanNoise cultmath_span_fbm/ridged(SpanPath, SpanStage, int, float, float)`.
  Any runner needs per-struct flatteners in field order for these 4.
- Tolerance classes: 89 are `ulp-bounded`, 2 are `exact`, 7 are `platform` (exp, pow, log, asin, acos,
  atan, and phacelle's sin and cos), and 10 carry a `check` (enclosure).
- What a harness needs: generate one shader per family that reads the argument words, bit-casts them to
  the signature's types, calls the function and writes the result words. The probe generator does this
  for both HLSL and GLSL in 112 lines of Python, judge included.

## Q1. Can dxc emit a SPIR-V library that SPIRV-Cross lowers to GLSL ES for CultMath's real surface?

Yggdrasil, ubuntu:24.04. Tools: dxc v1.9.2607 Linux (`linux_dxc_2026_07_29`, the version get-dxc.ps1
pins on Windows), and SPIRV-Cross built from HEAD 300681bb (2026-10-14).

| Step | Result |
|---|---|
| `dxc -T lib_6_6 -spirv -fspv-target-env=vulkan1.2 CultMath.hlsl`, no change to the source | fails: "No OpEntryPoint ... only allowed if the Linkage ... capability is being used" |
| `-default-linkage external` (lib_6_3, universal1.5), no `export` | fails the same way: no exports in SPIR-V |
| `export` added to every top-level `cultmath_` definition, vulkan1.2 | fails: "Capability Linkage is not allowed by Vulkan 1.2" |
| same, `-fspv-target-env=universal1.5` | compiles, but declares 14 capabilities, ray query, interlock, quad control and partitioned subgroups among them |
| SPIRV-Cross `--es --version 310` on that module | fails: "RayQuery requires Vulkan GLSL 460" |
| ray query stripped | fails: "Cannot find mapping for combined sampler parameter". The Texture2D/SamplerState functions were exported |
| `export` only on the 173 non-texture definitions, all capabilities but Shader and Linkage stripped, every OpExtension stripped, then `spirv-as` | assembles; SPIRV-Cross `--es --version 310` exits 0 |
| SPIRV-Cross `--es --version 300` | fails: "At least ESSL 3.10 required for compute shaders" (a library gets a GLCompute placeholder entry) |
| ES 3.10 output, `#version` guarded by `#ifdef SPIRV_CROSS_LIBRARY_HEADER`, placeholder main removed, compiled by glslang as a `#version 300 es` fragment shader | compiles |

Shape of the output against the committed lowering:

| | committed (CultMath.glsl, .Cycles, .Phacelle) | dxc + SPIRV-Cross |
|---|---|---|
| lines | 1035 + Cycles + Phacelle | 3458, one file (the MPL and Apache files are merged) |
| function definitions | 173 (121 + 51 + 1) | 127 |
| overloads | kept | **46 dropped**: one definition per name survives (for example `cultmath_snoise(vec2)` and `cultmath_iv_snoise_ball(vec2, float)` are gone) |
| SSA temporaries `_NNN` | 0 | 5632 |
| min and max | as written | NaN-guarded `mix(mix(min(a,b),b,isnan(a)),a,isnan(b))` |

In `cultmath_snoise(vec3)` as emitted, the body is one temporary per SSA value (`vec3 _1157 = floor(...)`)
and constants are spelt to 20 digits.

**Answer:** yes, but only with three owned workarounds: an `export` annotation on 173 definitions,
which FXC (Unity's D3D11 compiler) does not accept, so it would have to be a macro; a capability and
extension strip pass; and unique names for 46 overloads, which breaks the public API for both the HLSL
and the GLSL consumers. The output is generated text, not a readable library.

## Q2. Does a conforming Vulkan implementation run the families in a Linux container on Yggdrasil?

Yggdrasil, ubuntu:24.04, apt packages: mesa-vulkan-drivers 25.2.8 (lavapipe), glslang-tools 15.1.0,
spirv-tools 2025.1~rc1, libvulkan-dev, and the same dxc. There is no GPU.

- Device: `llvmpipe (LLVM 20.1.2, 256 bits)`, Vulkan 1.4. Float controls for f32: denormPreserve 0,
  flushToZero 0, RTE 1, RTZ 0, signedZeroInfNanPreserve 1.
- HLSL route: a generated `cs_6_0` compute shader that includes CultMath.hlsl, through `dxc -spirv
  -fspv-target-env=vulkan1.2`. 105 of 105 families compile and run. Fma instructions in the SPIR-V: 0.
- GLSL route: the committed CultMath.glsl, .Cycles.glsl and .Phacelle.glsl concatenated after `#version
  310 es` and highp precision, as a compute shader through `glslangValidator -V`. 105 of 105 compile and
  run.
- Library route: the dxc + SPIRV-Cross library from Q1 through glslang. 103 of 105 run; the 2 missing
  ones are the dropped overloads. Against the fixture: 75 exact, 4 within 4 u1, 7 within 64, 17 above
  64. Its worst rows are the same figures as the HLSL route's.
- Time: all 105 dispatches together take 8 s on a re-run. Each job runs about 4 minutes, almost all of
  it apt, the dxc download and the SPIRV-Cross build.
- Determinism: re-running all 105 SPIR-V modules gave bit-identical outputs.
- The HLSL and GLSL routes are bit-identical to each other in 85 of 105 families.
- Against the C# fixture, HLSL route: 75 exact, 6 within 4 u1, 7 within 64, 17 above 64. GLSL route:
  75, 4, 8 and 18. The largest errors are in the `coordinate bands 1e2 to 4e6` families of snoise_grad
  and snoise_hessian (13e6 and 1.6e8 u1), then fbm_grad and ridged_grad (about 3.5e3 to 5e3 u1), Cycles
  arcsine (2.7e3) and arccosine (1.0e3), and the other hessian families (1e3 to 2.4e3).

**Answer:** yes. The image needs nothing beyond distro packages and the pinned dxc. It is
deterministic and fast, and it runs both the HLSL and the lowered GLSL on the same implementation.

## Q3. A real-device run on Starfire without a browser: native ANGLE

Starfire. The host is `angle_native.py`, Python ctypes over the libEGL.dll and libGLESv2.dll that ship
with Docker Desktop 4.71.0 (an Electron app): ANGLE 2.1.26626, git hash a4490148bd3d. Display:
`EGL_PLATFORM_ANGLE_TYPE_D3D11`. Context: ES 3.0, optionally `EGL_CONTEXT_WEBGL_COMPATIBILITY_ANGLE`.
Each family is a `#version 300 es` fragment shader, with arguments read by texelFetch from an RGBA32UI
texture and results written to up to 3 RGBA32UI targets over a 16x16 grid, then read back with
glReadPixels. The same RGBA32UI readback is used by the scratchpad browser pages (gpu.js).

- `GL_RENDERER`: `ANGLE (NVIDIA, NVIDIA GeForce GTX 1070 (0x00001B81) Direct3D11 vs_5_0 ps_5_0, D3D11)`.
  `GL_VERSION`: `OpenGL ES 3.0 (ANGLE 2.1.26626 git hash: a4490148bd3d)`.
- 105 of 105 families compile and run. All 105 together take 6.3 s wall, process start included. The GL
  error at the end is 0.
- The WebGL-compatibility context and the plain ES 3.0 context are bit-identical in 105 of 105 families.
- Against the C# fixture: 38 exact, 31 within 4 u1, 17 within 64, 19 above 64.
- It agrees with the earlier browser measurements on the same GPU. `cultmath_iv_exp`: 19 of 256 exact,
  worst 12 ulp; the browser run of finding iv-exp-gpu-not-enclosed measured "up to 12 ulp".
  `cultmath_iv_frustum_ball`: 213 of 256 exact, so 43 off, by at most 2 ulp; finding
  frustum-exact-unsound-on-webgl2 measured "43/256 cases by up to 2 ulp".
- The dxc + SPIRV-Cross library from Q1 also runs on this ANGLE, as a WebGL-compatibility ES 3.00
  fragment shader: 103 of 105 families (the 2 dropped overloads fail to compile), with 41 exact. It is
  bit-identical to the committed lowering's ANGLE output in 71 of 103 families.
- Largest differences: the `coordinate bands 1e2 to 4e6` families again. 173 cases of snoise_grad and
  415 component values of snoise_hessian differ by more than 1e-3 relative, up to 2 and 20. These
  families exist for the span allowance's device margin (follow-up span-allowance-gpu-margin, cut
  stage-shaders-device). Their judge is the allowance check, not an ulp bound.

**Answer:** yes. A pinned ANGLE build, loaded natively, runs the site's real translation path
(ES 3.00, then ANGLE, then HLSL, then D3DCompile, then the NVIDIA D3D11 driver) with no browser and no
SwiftShader fallback, because the backend is named explicitly and the renderer string is recorded. The
result agrees bit for bit with the WebGL-compatibility context, and it reproduces the two browser
measurements on record.

## What was not probed

- SwiftShader as a second conforming implementation.
- Amber as the dispatch host instead of a small owned one.
- WARP and native D3D11 HLSL on Starfire, which is the Unity consumer path.
- naga with dxc SPIR-V as input.
- Chrome's own ANGLE feature flags, which the native host does not reproduce.
- Mobile or mediump devices.
- An Electron release's ANGLE pinned by download. The probe used the copy Docker Desktop happens to
  ship.
