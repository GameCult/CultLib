# Native shader testing: how established projects check shader numerics without a browser, and what each path proves

For `cultmath-tapes:question:device-run-driver` (campaign `cultmath-tapes`). Companion to `docs/research/device-capture-runners.md` (CultLib c9d3e672), which covered browser drivers only; not repeated here.
Also serves the operator's question of 2026-10-16 on why CultMath lowers HLSL to GLSL with its own text lowering (section on HLSL to GLSL ES / SPIR-V toolchains).
Gathered by an Eyes pass for Eureka Self, session self-2026-10-16-ag4, 2026-10-16. Facts only; no recommendation.

Strength marks: [src] read from a primary source text this pass (raw file fetched and read or grepped locally, or a spec PDF converted to text); [doc] vendor or project documentation page read this pass (via a fetch summariser, so check before quoting); [web] search-result summary only, not read in full; [abs] searched, not found.
Nothing was built or run. Dates matter: several repos below are moving; read at HEAD on 2026-10-16.

## 0. The record

- [src] Operator, 2026-10-16, verbatim: "There has to be a better way to test shader code than getting a browser involved". An observation that sets research direction, not a design ruling.
- [src] Question `device-run-driver` (viewed in the mind): options committed-captures / playwright-live / both; the runner is a static page that compiles each family's call against `CultMath.glsl`, reads back via RGBA32UI, emits raw bits; C# judges.
- [src] CultMath shader files: `packages/cultmath/shaders/` has `CultMath.hlsl` (673 lines), `CultMath.Affine.hlsl`, `.Cycles.hlsl`, `.Interval.hlsl`, `.Phacelle.hlsl`, and `CultMath.glsl` (900), `.Cycles.glsl`, `.Phacelle.glsl`. HLSL intrinsics seen by grep across the `.hlsl` files: `asuint` (13), `asfloat` (6), `saturate` (2); no `mad`, `fma`, `frexp`, `ldexp`, `precise`, `sincos`, `rcp`, `groupshared`, `#include` hits in the grep (the `GlslLowering.cs` doc comment says the HLSL has "includes" that are inlined).
- [src] The `.glsl` files contain no `precision` statement and no `#version` line (grep found none); a consumer supplies them.
- [src] `GlslLowering.cs` header: the GLSL ES 3.00 files are a regex text transformation of the HLSL; a dialect gap is fixed in the HLSL "in the subset both languages share".

## 1. Google Amber

- [doc] README (github.com/google/amber): runs on Vulkan (fully supported); Dawn listed "WIP". A command-line `amber` tool runs script files, reports pass/fail by exit code, can write output buffers and images to files. Says "not an officially supported Google product". AmberScript is the format getting new features; VkScript (the VkRunner syntax) is kept for history, no new features. Build bots check that Amber compiles on several targets; "integration tests don't run on the bots because of bot limitations, so only unit tests run in CI".
- [src] Maintenance: GitHub API `google/amber`: not archived, created 2018-11-13, pushed 2026-09-24, 76 open issues. Last five commits 2026-05-28 to 2026-09-24 (ray query support, acceleration structures, alpha-to-coverage, barrier fixes).
- [doc] AmberScript (docs/amber_script.md): starts `#!amber`. `SHADER compute <name> <format>` where format is GLSL, HLSL, SPIRV-ASM, SPIRV-HEX, SPIRV-BIN or OPENCL-C, inline, `FILE`, or `VIRTUAL_FILE`; optional `TARGET_ENV`. `PIPELINE compute <name> ... END`. `BUFFER <name> DATA_TYPE <type> [STD140|STD430] DATA ... END | FILL v | SERIES_FROM s INC_BY i`, types include int8..int64, uint8..uint64, float16, float, double, vectors, matrices. `COPY a TO b`. `RUN <pipeline> x y z`.
- [doc] Expectations: `EXPECT <buf> IDX <n> EQ|NE|LT|LE|GT|GE <values>`; with tolerance `EXPECT <buf> IDX <n> TOLERANCE <t1..t4> EQ <values>` (1 to 4 floats, a trailing `%` makes one relative; components without one use a default); whole-buffer `EQ_BUFFER`, `RMSE_BUFFER ... TOLERANCE <x>`, `EQ_HISTOGRAM_EMD_BUFFER ... TOLERANCE <x>`. Pixel forms `EQ_RGB`, `EQ_RGBA` use 0-255. Comparison is on typed values, not raw bit patterns; a uint32 buffer compared with `EQ` is an exact integer compare. [abs] A ULP-based tolerance form was not found in the AmberScript doc.
- [doc] VkScript (docs/vk_script.md): `probe ssbo` has a `~=` fuzzy-equal comparator and a `tolerance t0 [t1 t2 t3]` command.
- [web] Amber with SwiftShader: a build option `AMBER_ENABLE_SWIFTSHADER`/`AMBER_USE_SWIFTSHADER` builds SwiftShader as a Vulkan ICD; run with `VK_ICD_FILENAMES` pointing at its manifest; `amber -d` style device listing confirms it. [abs] Amber's own CI workflow files were not found at the paths tried (404); whether Amber CI uses lavapipe or SwiftShader was not established.
- [web] Amber lineage: shader test files mirror VkRunner's format; Amber is shipped inside Android's `external/deqp-deps` and Vulkan CTS.

## 2. Software Vulkan: Mesa lavapipe and SwiftShader

### 2.1 Conformance and float behaviour

- [web] Lavapipe announced formally Vulkan 1.2 conformant (Phoronix, May 2022) and Vulkan 1.3 conformant (July 2022; Khronos submission 696, AMD Ryzen 7 1800X, Mesa 22.1). A Mesa issue (#5472, older) complained lavapipe reported a conformance version while not yet conformant; resolution not checked.
- [web] Mesa MR 11739 "WIP: lavapipe: float_controls" (author's words as summarised): passes about 97% of CTS, fails `float_controls...denorm_*_preserve` cases, "llvmpipe/gallivm have no concept of setting a specific denorm mode"; the author said they probably would not continue. Current merged state of float controls in lavapipe: [abs] (the Mesa GitLab pages and `lvp_device.c` were behind bot protection or not at the tried path).
- [web] A separate llvmpipe MR disables denorms for compute shaders to align with fragment shaders, because OpenCL CTS tests "don't expect working denorms".
- [web] SwiftShader commit "Implement VK_KHR_shader_float_controls": at that time SwiftShader exposed no preserve, denorm or rounding controls and set all denorm-preserve and flush-to-zero flags false. Current values: [abs].
- [doc] Vulkan float controls (core in 1.2): a flush-to-zero flag set means denormals may be flushed, not that they are; preservation needs `shaderDenormPreserveFloat32` and a `DenormPreserve` execution mode on the entry point. Rounding-mode controls do not apply to float to int conversions (always toward zero).
- [web] Mesa lavapipe docs page (docs.mesa3d.org/drivers/lavapipe.html) 404s at the tried URL; the `llvmpipe.html` page was read and says nothing of lavapipe floats.
- [src] D3D11 floating-point rules (learn.microsoft.com, doc dated 2025-02/04): 32-bit ops are within 1 ULP (add, subtract, multiply: 0.5 ULP "applies only to addition, subtraction, and multiplication", hardware may truncate); "Denorms are flushed to sign-preserved zero on input and output of any floating-point mathematical operation" (data movement exempt); min/max flush for comparison but the result may or may not be flushed; fused operations (mad, dp3) are no less accurate than the worst serial ordering with each step allowed 1 ULP; sqrt and rcp 1 ULP, rcp/rsq have their own relaxed requirement; x*1.0, x/1.0, x+-0.0 return x "except denorm flushed"; NaN payload bits not preserved; no exceptions or traps; double precision preserves denorms and follows IEEE 754R; float16 preserves denorms.
- [src] GLSL ES 3.00 spec section 4.5.1 (Khronos PDF, converted locally): highp float is "defined by the IEEE 754 standard for 32-bit" in storage; "Any subnormal (denormalized) value input into a shader or potentially generated by any operation in a shader can be flushed to 0. The rounding mode cannot be set and is undefined." Required precisions: `a+b, a-b, a*b` correctly rounded; `a/b` and `1.0/b` 2.5 ULP for b in [2^-126, 2^126]; `a*b+c` "correctly rounded single operation or sequence of two correctly rounded operations"; `exp`, `exp2` (3 + 2|x|) ULP; `log`, `log2` 3 ULP outside [0.5, 2.0] and absolute error < 2^-21 inside; `inversesqrt` 2 ULP; `sqrt` inherits from `1.0/inversesqrt`; trig and `determinant` have undefined precision; explicit conversions correctly rounded; "rounding mode ... must not affect the result by more than 1 ULP". NaNs and Infs "are not required to be generated".
- [src] GLSL ES 3.10 spec (NVIDIA-hosted copy of the Khronos PDF) repeats the same flush-to-zero sentence (line 3061 of the converted text).
- [src] Table of required ranges in ES 3.00: highp relative precision 2^-24 (as IEEE); mediump minimum range (-2^14, 2^14), relative 2^-10; lowp minimum range (-2, 2), absolute 2^-8. Integers: highp 32-bit; mediump 16 to 32 bits; lowp 9 to 32 bits. "Mediump and lowp ... have maximum range and precision as defined by IEEE 754", so an implementation may run them at 32 bits.

### 2.2 Who uses software Vulkan in CI

- [src] wgpu `.github/workflows/ci.yml` (trunk, read via fetch): job `gpu-test` on `windows-2022`, `macos-14`, `ubuntu-24.04`. Linux installs the Vulkan SDK and Mesa (driver named only via the composite action, not visible), sets `LVP_POISON_MEMORY: true`; Windows installs DXC and WARP (`install-warp` action), Agility SDK, Mesa, Vulkan SDK, sets `WGPU_DX12_COMPILER: dxc`. Tests run with `cargo xtask test` (nextest).
- [doc] wgpu `docs/testing.md`: names `LVP_POISON_MEMORY` ("fills all newly initialized memory with a non-zero value"); describes a parameter system that skips or expects failure per adapter; says nothing on float or denorm differences.
- [web] onnxruntime commit replaced Mesa lavapipe with SwiftShader in headless CI because an old lavapipe returned `VK_ERROR_INCOMPATIBLE_DRIVER` when Dawn asked for a Vulkan 1.3 instance; the SwiftShader ICD is built from source there and used by Dawn.
- [web] Dawn/Chromium use the SwiftShader Vulkan ICD for headless runs; ANGLE end2end has a `VULKAN_SWIFTSHADER` config and a `--deqp-egl-display-type=angle-swiftshader` mode (section 4).
- [web] Wine/vkd3d: tests marked broken on WARP; a November 2025 commit disabled 64-bit wave bitwise ops on WARP "known to be buggy"; a merge request says WARP "doesn't emulate faithfully a hardware device".
- [abs] No project was found that commits raw float bit patterns from lavapipe or SwiftShader as a reference. Projects found compare with tolerance, by pixel, or by exact integer.

## 3. GLSL/HLSL to SPIR-V

### 3.1 glslang

- [src] glslang README (read at main): ESSL is a supported front end; compute shaders use `.comp`; stage is inferred from the file extension; C++ and C interfaces exist; JS/WASM build via `-DENABLE_GLSLANG_JS=ON`. Tests: gtest under `gtests/` plus a `runtests` script, expected outputs in `Test/baseResults/`.
- [src] The HLSL front end of glslang is deprecated: README states deprecation, slated for removal in the next major version with at least 18 months' notice, no bug reports accepted. Commit "Deprecate the HLSL front-end." dated 2026-04-16 (GitHub API). This does not touch `dxc -spirv`, a separate compiler.
- [web] Compute shaders need desktop GLSL 4.30 or GLSL ES 3.10 (Mesa error text "Compute shaders require GLSL 4.30 or GLSL ES 3.10"). In ES 3.10 only the last member of a shader storage block may be a runtime-sized array; desktop is looser.
- [src] SPIRV-Cross (section 3.3) throws "At least ESSL 3.10 required for compute shaders." for ES versions under 310. So a `#version 300 es` compute shader does not exist: a GLSL ES 3.00 fragment function ported to compute becomes a 3.10 shader.
- [src] ES 3.10 section 4.7.4: vertex and compute languages have predeclared `precision highp float; precision highp int;`; the fragment language has "no default precision qualifier for floating point types" and `mediump int`. So an unqualified float in a compute shader is highp, while a fragment shader must declare a float precision.
- [src] ES 3.10 and 3.00 share the flush-to-zero sentence and the ULP table for highp (the 3.10 table was not diffed line by line; the denorm sentence was).
- [abs] A published comparison of ES compute against desktop 4.30 numerics (same function, fragment vs compute) was not found. The two specs' highp rules, as read, are the same sentence; what a given driver does per stage is not stated by either spec.

### 3.2 dxc -spirv

- [src] DXC `docs/SPIR-V.rst` (235 KB, read by grep): default target environment `vulkan1.0` via `-fspv-target-env`; `-fspv-entrypoint-name=<name>` exists; `min16float`, `min10float`, `min16int`, `min12int`, `min16uint` map to 32-bit types with `RelaxedPrecision` unless `-enable-16bit-types`, which gives real 16-bit types and the `Float16` capability. `lib_6_x` appears only in the raytracing section ("must be compiled as library using lib_6_3/lib_6_4"). [abs] No statement in the doc that a non-raytracing `lib_6_x` library compiles to SPIR-V with exported functions; no statement on denormal execution modes.
- [src] `SPV_KHR_float_controls` is in the supported-extension list; no description of when dxc emits denorm modes (not found by grep of the doc).
- [src] HLSL exposes `asuint`/`asfloat` as bitcasts; in SPIR-V these are `OpBitcast`. (CultMath's use is section 0.)

### 3.3 SPIRV-Cross (SPIR-V to GLSL ES/HLSL/MSL)

- [src] README (main): ES example `spirv-cross --version 310 --es test.spv`; sections on HLSL-derived SPIR-V; names glslangValidator's `-e` entry-point flag in an error hint. Active: commits 2026-09-18, 2026-09-22, 2026-10-14.
- [src] `spirv_glsl.cpp`: `#version` is written as `#version <n> es` when `options.es && version > 100`; ES is allowed from 100 up (the `300 es` form is the emitted string); buffer blocks need ES >= 300, `binding` needs >= 310.
- [src] Precision: `backend.allow_precision_qualifiers = options.vulkan_semantics || options.es;` `flags_to_qualifiers_glsl` emits `mediump ` for ids with `RelaxedPrecision`, else `highp `, and omits either when it equals the fragment default; structs and doubles get none. `Options::Fragment` defaults are `default_float_precision = Mediump`, `default_int_precision = Highp` (`spirv_glsl.hpp` lines 196-197). `emit_header()` writes `precision <p> float;` and `precision <p> int;` only for fragment shaders when `options.es`. So a function-only library run through `--es` with default options would, in a fragment context, assume mediump float defaults and tag non-relaxed values `highp`.
- [src] Library modules: `spirv_parser.cpp` (main) handles a module with no `OpEntryPoint` but with `OpDecorate ... LinkageAttributes ... Export`: marks `is_library_module`, uses the first exported function as a placeholder entry point with execution model GLCompute, and `spirv_glsl.cpp` emits "each exported function as a normal free function" plus the callees. A module with neither throws "There is no entry point in the SPIR-V module." The code paths exist at HEAD; which release first had them, and whether the output is valid for ES, were not checked.
- [web] A Unity issue reports min16float hints (`min16f as def32` modifier in FXC bytecode) not honoured in SPIR-V/ESSL output (mediump produced where highp was expected). One blog summary says SPIRV-Cross may pick a low default precision unless told otherwise (not authoritative).
- [src] Readability: [abs] no source characterising readability of the emitted GLSL was found; SPIRV-Cross output is generated text with temporaries named `_<id>` unless debug names survive (names come from OpName; not verified for dxc output).

## 4. Native ANGLE without a browser

- [doc] ANGLE README: implements EGL 1.5 and GLES 2.0/3.0/3.1 over Vulkan, desktop GL, GLES, D3D9, D3D11, Metal; "Chrome uses ANGLE for all graphics rendering on Windows" and it is the default WebGL backend for Chrome and Firefox on Windows; its shader compiler can emit Desktop GLSL, Vulkan GLSL, HLSL, ESSL.
- [doc] ANGLE `doc/DevSetup.md`: build standalone with `fetch angle`, `gn gen out/Debug`, `autoninja -C out/Debug`; tests, libraries and samples land in `out/Debug`; on Windows default backend is D3D11 (D3D9 otherwise); backend chosen at runtime through `EGL_ANGLE_platform_angle` via `eglGetPlatformDisplayEXT` (`util/EGLWindow.cpp` as example); backends removable via GN args `angle_enable_d3d11` etc. [abs] prebuilt binaries not covered in the doc.
- [src] ANGLE `doc/dEQP.md`: targets `angle_deqp_gles2_tests`, `angle_deqp_gles3_tests`, `angle_deqp_gles31_tests`, `angle_deqp_egl_tests`; renderer chosen with `--deqp-egl-display-type=angle-d3d11 | angle-d3d9 | angle-d3d11-fl93 | angle-gl | angle-gles | angle-metal | angle-swiftshader | angle-vulkan` (same as `--use-angle=X`); default is Vulkan except Apple (OpenGL); test lists come from `third_party/VK-GL-CTS/.../gles3-main.txt`; GoogleTest builds of the same targets run on the bots with expectation files in `src/tests/deqp_support`.
- [src] ANGLE `doc/TestingAndProcesses.md`: angle_end2end_tests expectations live in `src/tests/angle_end2end_tests_expectations.txt` (`SKIP`, `TIMEOUT` only); the WebGL conformance expectations live in the Chromium tree, not the ANGLE tree.
- [web] `angle_end2end_tests` configs include `ES2_D3D11_WARP`, `ES2_D3D11_REFERENCE`, feature-level variants and `VULKAN_SWIFTSHADER`; end2end tests subsume the older angle_tests and standalone tests.
- [doc] ANGLE `EGL_ANGLE_create_context_webgl_compatibility` / `ANGLE_webgl_compatibility` (spec text, Draft): `eglCreateContext` attribute `EGL_CONTEXT_WEBGL_COMPATIBILITY_ANGLE` adds WebGL validation; shaders with `#version 100` get WebGL 1 rules and `#version 300 es` get WebGL 2 rules.
- [src] Path in D3D11 (`src/libANGLE/renderer/d3d/d3d11/Renderer11.cpp`, main): ANGLE translator emits HLSL, then `D3DCompile` with profile `vs_5_0`/`ps_5_0` style (`getMajorShaderModel`), flags `D3DCOMPILE_OPTIMIZATION_LEVEL2`, plus `D3DCOMPILE_IEEE_STRICTNESS` only `if (workarounds.enableIEEEStrictness)`; on failure retries with `SKIP_VALIDATION`, then `SKIP_OPTIMIZATION`. The HLSL is generated at link time (`DynamicShaderCompilation.md`) because signatures between stages must be known.
- [src] ANGLE `doc/CompilingTranslatorWithEmscripten.md`: the translator alone can be compiled for use from a page (preliminary notes).
- [abs] The `enableIEEEStrictness` workaround's default value in ANGLE and in Chrome was not found. [abs] Whether Chrome's WebGL2 context and a standalone ANGLE EGL context share all translator options and workaround flags was not established (same repository; Chrome applies its own feature flags and the WebGL-compatibility context).
- [abs] No source found for a project that uses a standalone ANGLE `libEGL`/`libGLESv2` as a compute-readback harness; ANGLE's own tests use it as the system under test.
- [src] Context: GLES 3.0 cannot run compute (3.1 adds it); ANGLE advertises 3.1 (README). A WebGL2 fragment readback and an ES 3.1 compute readback are different ANGLE shader paths (pixel-shader HLSL vs compute-shader HLSL).

## 5. D3D11/D3D12 compute harnesses for HLSL

- [src] DXC repo `tools/clang/unittests/HLSLExec/` files: `ExecutionTest.cpp`, `HlslExecTestUtils.cpp`, `LongVectors.cpp`, `ShaderOpArithTable.xml`, `ShaderOpTest.cpp` and others; the test project uses TAEF (WEX) test methods.
- [src] `HlslExecTestUtils.cpp`: `useWarpByDefault()` returns true unless `_HLK_CONF` is defined; when the `UseWARP` parameter is set it creates a device from `EnumWarpAdapter` at feature level 11_0, with an optional `WARP_DLL` path to load a specific WARP build.
- [src] `ExecutionTest.cpp`: `DenormBinaryFloatOpTest`/`DenormTertiaryFloatOpTest` read tables from `ShaderOpArithTable.xml#DenormBinaryFloatOpTable`/`DenormTertiaryFloatOpTable` (rows like `FDivDenormFTZ`; columns `ShaderOp.Target`, `ShaderOp.Arguments`, `Validation.Type`, `Validation.Tolerance`, inputs and expected values); a helper `ifdenorm_flushf_eq` accepts either flushed or unflushed results; derivative tests use `CompareFloatULP(..., 1)` with alternative acceptable values.
- [src] `LongVectors.cpp`: validation config `Epsilon(tol)` or `Ulp(tol)`; default is ULP 1.0 for floating-point types, exact for others; failures print the validation type and tolerance.
- [src] DXC's `HLSLExec` tests thus run on WARP by default and on hardware via a flag; tolerances are per-row in data tables.
- [web] Shader Test Framework (KStocky, D3D12, Catch2, HLSL 202x): README says "very experimental"; tests are HLSL functions run through a `ShaderTestFixture`; a device can be hardware or WARP (README read by fetch did not state WARP; a search summary and the Skyrim Community Shaders DeepWiki page did); README does not state its float comparison. Used by Skyrim Community Shaders per a DeepWiki page (AI-generated, unverified).
- [web] An LLVM 2025 dev meeting talk lists "accuracy tolerances" and "NaN/INF/denorm handling" among hard areas for HLSL test writing.
- [web] WARP is Microsoft's recommended D3D11 device for service/server environments; Session 0 services cannot use hardware D3D9 HAL on WDDM (also in the earlier file).
- [abs] WARP's own float conformance statement (denorm flushing, mad fusion) was not found in a primary document; the D3D11 rules in 2.1 are the contract WARP implements.
- [web] vkd3d-proton and Wine's `d3d12` tests mark WARP-specific broken cases (see 2.2).

## 6. Other paths

### 6.1 Slang

- [doc] Targets page (shader-slang.org/slang/user-guide/targets.html): D3D11 (DXBC), D3D12 (DXIL), Vulkan (SPIR-V), OpenGL (GLSL source, SPIR-V, vendor binaries; "only limited support for compiling code for OpenGL"), Metal, CUDA and OptiX, CPU compute ("compute only", "functional, but not feature- or performance-complete"), WebGPU (WGSL, "work in progress"). GLSL ES: not mentioned.
- [doc] `a2-04-glsl-target-specific.md`: version selected by `-profile glsl_<version>` (example `glsl_450`); the GLSL target "is currently less mature than the SPIR-V target and has several known limitations"; known issue with 3-element vector constant-buffer packing. GLSL ES / `300 es`: not mentioned. [abs] A search for Slang emitting `#version 300 es` found nothing.
- [doc] `docs/cpu-target.md`: outputs C++ (`-target cpp`) or executables, shared libraries, host-callable code, object code; in-process execution through `getTargetHostCallable`; default host-callable backend is `slang-llvm` (no file system, no C compiler needed, shader style only), alternatives are system Clang/GCC/Visual Studio via temp files; limitations: no barriers, no user atomics (except compiler-generated 32/64-bit int add), entry points cannot be named `main`, `float16_t` unsupported, limited bounds-check handling; "no content on floating-point semantics".
- [web] Slang can compile to SPIR-V directly or via GLSL (`-emit-spirv-via-glsl`, glslang downstream).

### 6.2 wgpu / naga

- [src] naga README table: inputs SPIR-V (primary), WGSL (primary, "fully validated"), GLSL (secondary, "GLSL 440+ and Vulkan semantics only"); outputs SPIR-V, Metal, HLSL ("Shader Model 5.0+"), WGSL (secondary), GLSL (secondary, "GLSL 330+ and GLSL ES 300+"). HLSL is not an input language. naga is a different translator from ANGLE's; wgpu's DX12 path uses naga HLSL output compiled with DXC or FXC (`WGPU_DX12_COMPILER`, ci.yml).
- [src] wgpu CI deletes `naga/tests/out` snapshots before test runs (ci.yml), i.e. naga keeps snapshot-compared output files.

### 6.3 Tint (Dawn's WGSL compiler)

- [web] Search snapshots (2020-2021, a CMake-only Dawn fork): Tint reads WGSL (default on) and SPIR-V (`TINT_BUILD_SPV_READER`, off by default); writers for SPIR-V, WGSL, HLSL, MSL, GLSL (GLSL writer added October 2021, validated with glslang). [abs] The current upstream README was not retrievable (404 at tried paths), so current readers and writers are unconfirmed.

### 6.4 Shader-function unit-test frameworks and GPU-free paths

- [web] `shadertest` (Kupoman, Python): load a GLSL file, call its functions from pytest; supports bool, int, uint, float and vectors; implementation (GPU context) not read.
- [web] `shdr` (cdave1): experimental CPU emulator for GLSL fragment shaders, translates GLSL to JavaScript, "does not quite match the glsl". `rpu` (Rust crate): a GLSL-like language compiled to WebAssembly for CPU rendering, not full GLSL. HlslUnit (.NET): an interpreter that runs HLSL on the CPU (search summary).
- [web] Blender added infrastructure to run GLSL shader unit tests and shares C++/GLSL source in the GPU module, compiled through the real GPU backend.
- [web] Khronos forum "GLSL Unit" (Google MapsGL team) unit-testing framework for shaders, old, likely dormant.
- [doc] Vulkan Validation Layers `debug_printf`: shaders print through `debugPrintfEXT`; output goes to the validation layer message stream; [web] each a runtime Vulkan layer, not a numeric assertion framework.
- [abs] No GPU-free GLSL ES interpreter with IEEE-faithful float semantics, maintained, and used by a notable project was found.

## 7. Cross-cutting: tools that produce SPIR-V for sections 1-3 (item 8)

| Tool | Input | Output | Library with no entry point | Maintenance |
|---|---|---|---|---|
| glslang | GLSL, ESSL, HLSL (deprecated 2026-04-16) | SPIR-V | not covered in README | active (commits 2026-04) |
| dxc `-spirv` | HLSL | SPIR-V | `lib_6_x` only documented for raytracing; `-E` entry otherwise [abs] export behaviour | active repo, wiki and docs |
| SPIRV-Cross | SPIR-V | GLSL (incl. `300 es`, `310 es`), HLSL, MSL | HEAD has library-module path keyed on `LinkageAttributes Export` | active (2026-10-14) |
| Slang | Slang (accepts HLSL-like syntax) | SPIR-V, HLSL, GLSL desktop (`-profile glsl_N`), WGSL (WIP), CPU, others | not examined | active |
| Unity HLSLcc | DX bytecode (DXBC) | GLSL 3.2+, GLSL ES 2.0+ ("OpenGL ES 3.0+" used for Unity), Metal, Vulkan GLSL | n/a (bytecode) | `pushed_at` 2024-07-16, not archived, 30 open issues, README "used to generate all Unity shaders for OpenGL, OpenGL ES 3.0+, Metal, Vulkan"; supports `min16float`, infers sampler precision |
| naga | WGSL, SPIR-V, GLSL 440+ | SPIR-V, WGSL, GLSL ES 300+, HLSL SM5+, MSL | n/a | active (wgpu) |
| Tint | WGSL (SPIR-V reader optional) | SPIR-V, HLSL, MSL, GLSL (per 2021 snapshot) | n/a | unconfirmed |

- [src] Which can also feed Amber/Vulkan runs: glslang and dxc produce SPIR-V from GLSL/HLSL; Amber itself accepts HLSL and GLSL source in `SHADER` blocks directly (section 1), choosing compilers internally [web: Amber builds against glslang/shaderc; which is used for HLSL not read].
- [src] Which can emit GLSL ES 3.00 for the web from one source: SPIRV-Cross (`--es --version 300`, with the fragment precision options above), naga (GLSL ES 300+), HLSLcc (GLSL ES from DXBC; `ES 3.0+` per README), Slang (not documented).
- [web] WGSL for the web: naga and Tint write WGSL (naga: "secondary"); Slang WGSL target "work in progress".

## 8. What each path exercises (stated as facts about the scope of each check)

| Path | Executes | Compiler/translator in the chain | Float rules in force | What a pass establishes | What it does not |
|---|---|---|---|---|---|
| Amber on lavapipe | SPIR-V on Mesa llvmpipe JIT (CPU) | glslang or dxc/other to SPIR-V, then Mesa NIR/LLVM | Vulkan spec; GLSL-derived SPIR-V denorm handling per Mesa; FMA contraction per Mesa/LLVM | The shader source, as compiled to SPIR-V, computes within the stated tolerance on a Vulkan 1.3 conformant implementation | Behaviour of ANGLE's HLSL, fxc/D3DCompile, NVIDIA D3D11 |
| Amber on SwiftShader | SPIR-V on SwiftShader's JIT (Reactor/Subzero) | same front end | Vulkan spec; float controls reported off (older commit) | same, on a second conforming implementation | same |
| Amber on a real Vulkan GPU | SPIR-V on the driver | same | vendor | The SPIR-V on that driver | WebGL path |
| glslang/SPIRV-Cross text snapshot tests | nothing executed | translators only | none | translation is stable between versions | numerics |
| ANGLE `angle_end2end`/dEQP on D3D11 | GLSL ES via ANGLE translator, HLSL, `D3DCompile` (fxc), NVIDIA D3D11 driver, as in Chrome on Windows | ANGLE as built in the harness (the same codebase Chrome vendors) | D3D11 rules (flush to zero, 1 ULP) over ESSL rules | The ES shader behaviour of that ANGLE build on that driver | Chrome's own flags/feature overrides unless reproduced |
| ANGLE on SwiftShader (`angle-swiftshader`) | GLSL ES via ANGLE to SPIR-V, SwiftShader | ANGLE Vulkan backend | Vulkan | ANGLE's Vulkan translation path, software | ANGLE's D3D11 path |
| D3D11/12 compute on WARP | HLSL via dxc/fxc, WARP CPU rasterizer | Microsoft compiler | D3D rules as implemented by WARP | The HLSL source on a D3D conformant software device | NVIDIA hardware or the WebGL route |
| D3D11/12 compute on GTX 1070 | HLSL via dxc/fxc, NVIDIA driver | | D3D rules, vendor | The HLSL source on that GPU | other vendors |
| wgpu/naga on lavapipe or WARP | WGSL (or SPIR-V in) via naga | naga | WGSL/Vulkan or D3D12 | naga's translation of that source on that device | ANGLE or glslang/dxc paths |
| Slang CPU | Slang to C++ or LLVM, run on the host CPU | Slang | host CPU IEEE with the host compiler's flags (page silent) | Slang source semantics on a CPU | HLSL or GLSL as consumers compile them |
| GLSL-to-CPU emulators/interpreters | the interpreter | the interpreter | the interpreter's host floats | the interpreted source | any real driver |
| Browser WebGL2 (earlier file) | GLSL ES via browser's ANGLE | | | the exact consumer path on that browser/GPU | other browsers/drivers |

- [src] Spec positions that bound what a conforming-implementation pass can claim: GLSL ES 3.00/3.10 allow flush of subnormals to zero at any time, undefined rounding, `a*b+c` either fused or two roundings, and undefined trig precision; D3D11 requires flushing subnormals on math input/output and allows fused ops up to the unfused tolerance; Vulkan lets an implementation flush unless a preserve mode is requested and supported.
- [src] Where the same GLSL ES source goes through ANGLE→HLSL→`D3DCompile`→driver, the compiler flags in `Renderer11.cpp` (OPTIMIZATION_LEVEL2, retry configurations, conditional IEEE_STRICTNESS) are part of the path; a SPIR-V route (glslang or dxc, then a Vulkan implementation) has none of those stages.
- [abs] No source found that quantifies disagreement between ANGLE→D3D11 and a Vulkan software implementation for the same GLSL ES function.

## 9. Gaps (searched, not resolved)

- Lavapipe and SwiftShader current `VkPhysicalDeviceFloatControlsProperties` values; FMA contraction policy in either; the lavapipe docs page.
- Amber's own CI jobs and which compilers it uses for HLSL and GLSL.
- Whether `dxc -spirv` can emit an exported-function library that SPIRV-Cross's library path accepts; whether `dxc` flush/preserve execution modes are emitted by default.
- Tint's current readers and writers.
- Slang GLSL ES output; Slang CPU float flags.
- ANGLE: default of `enableIEEEStrictness`; prebuilt standalone `libEGL`/`libGLESv2` availability for Windows outside Chrome/Electron; Chrome-specific translator options.
- WARP numerics statement; DXC test tolerance values per intrinsic row (the XML table structure was read, not every row).
