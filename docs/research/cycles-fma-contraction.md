# Cycles CPU FMA contraction: facts for the kernel-pin fork

Serves finding `cultmath-tapes:finding:cut-cycles-converters.s1.kernel-pin-unraised-fork` (kind Finding, InForce, Medium, admitted 2026-10-13, soul-cycles-converters-s1).
Gathered by eyes-cycles-fma, session self-2026-10-14-eureka, 2026-10-14. Facts only; no recommendation.

Strength marks: [src] read from source text; [doc] vendor or standards documentation; [web] issue, forum or search-result summary; [abs] absence of evidence (searched, not found).
"Raw" Blender paths below are fetched as `https://projects.blender.org/api/v1/repos/blender/blender/raw/<path>?ref=<tag or branch>`. Local copies of the main-branch files are in `...\scratchpad\eyes-cycles-fma\dl\`.

## 0. The finding's own record (what is being tested)

- [src] The finding quotes the oracle probes: with the AVX2 kernel, 17 function rows differ from committed fixtures (floored_modulo 151/1024 cases ... color 55/1024), worst `mix_overlay` 493 ulp, `cycles_floored_modulo` 958152704 ulp (sign flip). Yggdrasil's /proc/cpuinfo shows avx2 and fma.
- [src] Oracle code: `kernel_check()` in `tools/cycles-oracle/oracle.py` bakes FLOORED_MODULO of 10000 by 0.1 and compares to numpy unfused `a - floor(a/b)*b`; exits 4 if the kernel fuses. Read with `git show e48f9488:packages/cultmath/tools/cycles-oracle/oracle.py` (lines ~528-551), README lines 20-30, `CyclesOracleTests.cs` lines 5-18 (those paths are not in the working tree at main; they exist at commit e48f9488). The oracle Blender is 5.2.2.

## 1. Cycles CPU kernels and flags

1. [src] The CMake file defines two x86 CPU kernel variants only: an SSE4.2 baseline and an AVX2 kernel (`WITH_KERNEL_AVX2`). No AVX-512 flag or kernel appears; `kernel.cpp` lists `cpu` and `cpu_avx2`. `intern/cycles/CMakeLists.txt` lines 76-130, 137-143 (main and v5.2.2); `intern/cycles/device/cpu/kernel.cpp` line 11 (v5.2.2): `KERNEL_NAME_EVAL(cpu, name), KERNEL_NAME_EVAL(cpu_avx2, name)`.
2. [src] GCC/Clang flags (CMakeLists.txt lines 97-100): SSE4.2 = `-msse -msse2 -msse3 -mssse3 -msse4.1 -msse4.2`; AVX2 = SSE4.2 flags + `-mavx -mavx2 -mfma -mlzcnt -mbmi -mbmi2 -mf16c`. The `-mfma` flag is what makes FMA instructions available only in the AVX2 kernel. MSVC: AVX2 kernel `/arch:AVX2`, SSE4.2 kernel empty on x64 (lines 82-89). Intel compilers use `/QxCORE-AVX2` or `-xcore-avx2`.
3. [src] Contraction flags, GCC/Clang block (CMakeLists.txt lines 160-180, main = v5.2.2): `-fno-trapping-math`, `-fno-math-errno`, `-fno-signed-zeros`, `-ffp-contract=on`, `-freciprocal-math`; `-fassociative-math` is commented out ("problems with bsdf_D for GGX on Linux (#130389)"); `-ffinite-math-only` deliberately not set. These are appended to the global `CMAKE_CXX_FLAGS`, so they apply to both kernels; only `-mfma` differs. GCC additionally gets `-fno-signaling-nans`, `-fno-rounding-math`, `-mfpmath=sse`.
4. [src] MSVC (non-clang) block (line 149): `/fp:fast`, with the comment "Unlike GCC/clang we still use fast math, because there is no fine grained control and the speedup we get here is too big to ignore." For clang-cl the GCC/Clang flag list is passed with a `/clang:` prefix (line 198).
5. [src] History of the contraction flag (Forgejo API commit messages):
   - `f96f3c33af`, 2024-09-30, "Cycles: Add some more math optimizations": added `-ffp-contract=fast`, `-freciprocal-math`, `-fassociative-math`. Message: "These are used on Windows and HIP anyways, so our code can't expect exact IEEE semantics in any case." Measured "~1.5% speedup in my very quick test".
   - `0fc58f0aba`, 2024-11-25, "Disable -fassociative-math on Linux to avoid GGX issues".
   - `7b19e74cad`, 2025-12-14 (PR 150318): changed `fast` to `on`: "Disable FMA auto-vectorization across different statements in Cycles, because GCC 15.2.1 manages to ruin the viewport's scaling and offset" (GCC bug 106902). It also removed a `remove_cc_flag("-ffp-contract=off")` block ("Not needed to make cycles automated tests pass with -march=native. However Blender itself needs this flag").
   - Before 2024-09-30 the CMake file carried no Cycles-specific contraction flag; the global Blender flag was `-ffp-contract=off` (stripped for Cycles by that `remove_cc_flag` block). [src, inferred from the removed lines; the earlier state was not read at an older tag.]
6. [doc] GCC definition: `-ffp-contract=on` "enables contraction within one expression, but not across different statements"; `fast` forms FMA "if the target has native support"; default `off` for ISO C, `fast` otherwise. Source: https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html. (Which GCC release implemented `on` for C++ was not checked.)
7. [src] Cycles' floored modulo is one C++ expression: `return (b != 0.0f) ? a - floorf(a / b) * b : 0.0f;` in `intern/cycles/util/math_base.h` (v5.2.2 line 613-616; main line 646-649). `NODE_MATH_FLOORED_MODULO` calls it from `kernel/svm/math_util.h` line 164. `safe_modulo` (truncated) uses `fmodf`, not an expression. `-freciprocal-math` also permits `a/b` to become `a * (1/b)`.
8. [src] Env switch: `intern/cycles/util/debug.cpp` (v5.2.2) line 29 `CHECK_CPU_FLAGS(avx2, "CYCLES_CPU_NO_AVX2")`, line 23 `flag = (getenv(env) == nullptr)`; presence of the variable disables the AVX2 kernel. [web] added 2015-02 by Sergey Sharybin, commit 4b04a4e, described as developer-only: https://lists.blender.org/pipermail/bf-blender-cvs/2015-February/073396.html (via search summary).
9. [src] The AVX2 kernel is selected at runtime by CPU capability: `system.cpp` `system_cpu_support_avx2()` requires avx2 and f16c ("F16C is considered part of AVX2 for our purpose"), so a default Blender on any AVX2 CPU uses it.
10. [src] Cycles' own render tests are image tests with a default `fail_threshold = 0.016`, `fail_percent = 1` (`tests/python/modules/render_report.py` lines 287-288), overridden per test (e.g. 0.032, 0.06, 0.048; `tests/python/cycles_render_tests.py` ~339-357). They compare images, not per-node bit patterns. A grep for AVX/NO_AVX in these two files found nothing, so the tests do not appear to pin a kernel. [src for what was found; absence is [abs]]

## 1b. GPU backends

11. [src] CUDA: `intern/cycles/device/cuda/device_impl.cpp` line ~225 passes `--use_fast_math` to nvcc (nvcc fuses FMA by default; `--fmad` default is documented NVIDIA behaviour, not fetched here). `kernel/device/cuda/compat.h` line 89 has the comment "Use fast math functions".
12. [src] HIP: `kernel/device/hip/compat.h` line 91 "Use fast math functions" (the HIP compile flags were not located; the commit message in item 5 states HIP already runs without exact IEEE semantics).
13. [src] Metal: `device/metal/device_impl.mm` line 570 `options.fastMathEnabled = YES;`.
14. [src] oneAPI: `kernel/device/oneapi/CMakeLists.txt` line 71 `-ffast-math`, line 83 `-cl-fast-relaxed-math` for spir64.
15. [src] OptiX: not separately examined (OptiX kernels are CUDA-compiled).
16. [src] The fp-contract/fast-math choices are thus explicit in all of: CPU (`on`, both kernels), MSVC CPU (`/fp:fast`), CUDA, HIP, Metal, oneAPI.

## 1c. Do Blender developers treat CPU/GPU numeric differences as bugs

17. [web] Issue #101114 (Power math node precision): a developer said they do not handle such precision issues as bugs since implementation is by the GPU driver and manufacturer. https://developer.blender.org/T101114 (search-result summary; page not read).
18. [web] Issue #68687 (Eevee modulo inconsistent): a developer said the imprecision "is likely to remain as it is an implementation behavior we cannot ensure"; reporters noted Cycles gave the expected result. https://developer.blender.org/T68687 (summary).
19. [web] Issue #53348 (Cycles CPU vs GPU in World nodes): a developer attributed the difference to "--fast-math we use for CUDA" and noted disabling it costs about 15% speed. https://projects.blender.org/blender/blender/issues/53348 (summary; page returned 403 to WebFetch).
20. [web] Issue #67448 "Shader node float precision issue cause different results on CPU and GPU" exists (archived); its content was not read (redirect not followed). https://developer.blender.org/T67448
21. [abs] No Blender issue specific to Floored Modulo and FMA/AVX2 was found; no Blender document stating a CPU/GPU numeric parity guarantee was found.

## 2. Shader languages

22. [doc] HLSL `precise`: separate operations stay separate ("where a mul and add operation might have been fused into a mad operation, precise forces the operations to remain separate"; fusion then only via the explicit `mad` intrinsic); order of operations preserved; IEEE NaN/INF honoured. It propagates only to operations that contribute to the `precise`-qualified variable; marking shader outputs is recommended. https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-variable-syntax
23. [doc] HLSL `mad`: may be fused or mul+add in hardware; for `precise` values the hardware must be consistent across all uses of that mad on that hardware. https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/mad (search summary). [web] The HLSL team blog says any operation may be refactored unless `precise`, and `/Gis` makes all computations precise: https://learn.microsoft.com/ko-kr/archive/blogs/marcelolr/precise-and-ieee-strictness-in-hlsl (summary).
24. [abs] DXC's default contraction of plain `a*b+c` was not confirmed from DXC docs or source; clang HLSL PR #119820 "Make fast math the default for HLSL" exists (https://lists.llvm.org/pipermail/cfe-commits/Week-of-Mon-20241209/654101.html), content not read.
25. [src] Vulkan SPIR-V environment (https://registry.khronos.org/vulkan/specs/1.3/html/chap37.html, fetched and grepped): "Implementations may rearrange floating-point operations using any of the mathematical properties governing the expressions in precise arithmetic ... may involve a different number of rounding steps"; "This rearrangement can be prevented for particular operations by using the NoContraction decoration." Without NoContraction the flags AllowContract/AllowReassoc/AllowRecip/AllowTransform are assumed. Same page: OpFMod "Inherited from x - y * floor(x/y)"; OpFRem inherited from `x - y * trunc(x/y)`; OpFDiv 2.5 ULP for |y| in [2^-126, 2^126]; and the page lists `OpFmaKHR`/`shaderFmaFloat32` (a core-spec FMA capability) as new.
26. [doc] GLSL: `precise` qualifier ("the qualifier precise will ensure that operations contributing to a variable's value are performed in the order and with the precision specified") "effectively prohibits the arbitrary use of fused multiply-add"; under `precise`, `fma()` is one operation and `a*b+c` is two. Sources: GL_ARB_gpu_shader5 / OES_gpu_shader5 text and https://registry.khronos.org/OpenGL-Refpages/gl4/html/fma.xhtml (search summary). `invariant` was not looked up.
27. [doc] SPIR-V `NoContraction` goes on the intermediate-result instructions, not the variable; GLSL `precise` lowers to it in glslang/ANGLE. [web] LLVM-based backends have contracted NoContraction multiplies into fma before (bug report in search results, wording in item summary only).
28. [web] Metal: `MTLCompileOptions.fastMathEnabled`/`mathMode` (fast vs safe); in safe mode MSL requires correctly rounded FP32 add, sub, mul, div, fma; `metal::precise` namespace gives precise library functions; explicit `fma()` is the advice for controlling fusion. https://developer.apple.com/documentation/metal/mtlcompileoptions/mathmode.md and https://developer.apple.com/forums/thread/90959 (summaries). Current Xcode default for fast math was not confirmed ([web] sources conflict). No Metal `fp_contract` pragma found [abs].
29. [abs] Unity's shader compiler (FXC/DXC via HLSLcc/SPIRV-Cross) contraction behaviour: no documentation found.

## 3. C# / .NET

30. [doc] `Math.FusedMultiplyAdd` (and `MathF`): "Returns (x * y) + z, rounded as one ternary operation"; infinite-precision multiply and add then one rounding. Available .NET Core 3.0+ and .NET 5-11. https://learn.microsoft.com/en-us/dotnet/api/system.math.fusedmultiplyadd
31. [web] dotnet/runtime issue #10137 "[Jit] Recognize FMA pattern and Emit Fused Multiply Add" closed 2024-02-05 as COMPLETED with the maintainer comment: "Closing this. We have explicit `FusedMultiplyAdd` APIs and will likely cover any future 'pick the better of MultiplyAdd or FusedMultiplyAdd' via an explicit `MultiplyAddEstimate` API." (read with `gh issue view 10137 -R dotnet/runtime`). Earlier comments there state the intent that `a*b+c` stay two IEEE roundings. PR #61037 adds an arm64 MADD fold for integers only (summary).
32. [web] Issue #98704 "Math.FusedMultiplyAdd returns wrong results on systems without FMA3 support", closed COMPLETED 2025-05-06: the software fallback had been an estimate (suspected ucrtbase `fma`), so correct rounding was guaranteed only with hardware FMA3; `COMPlus_EnableFMA=0` did not reproduce it on FMA3 hardware. Fix details were not read. Also [web] TA-Lib issue #340 reports the same misrounding concern.
33. [abs] A statement in .NET docs that RyuJIT never contracts floating-point `a*b+c` was not found; #10137's closure is the nearest evidence. The CultMath repo's own use of FusedMultiplyAdd/MathF.FusedMultiplyAdd was not searched.

## 4. Other reference implementations and tolerance policies

34. [src] OpenShadingLanguage testsuite (`testsuite/runtest.py` lines 82-87): image diff thresholds `failthresh = 0.004`, `hardfail = 0.01`, `failpercent = 0.02`, warn at 2x failthresh; "A test fails if more than failpercent of pixel values differ by more than failthresh ... or if even one pixel differs by more than hardfail." Raw URL: https://raw.githubusercontent.com/AcademySoftwareFoundation/OpenShadingLanguage/main/testsuite/runtest.py
35. [src] Blender's Cycles render tests: item 10 (image thresholds, default 0.016/1%).
36. [doc] Vulkan precision table lists per-operation ULP classes (item 25); functions defined as expressions inherit the budgets of their constituents (GLES3 CTS spec, https://fuchsia.googlesource.com/third_party/vulkan-cts/+/30e25b62d60b36b923d19a0c0759ef6cb7201f9e/doc/testspecs/GLES3/functional.shaders.builtin_functions.precision.txt, summary). Historical draft GL_ARB_shader_precision: `a*b+c` "correctly rounded single operation or sequence of two correctly rounded operations", fma same as a*b+c (https://developer.download.nvidia.com/opengl/specs/GL_ARB_shader_precision.txt, summary).
37. [abs] Not found/not examined: MaterialX reference-vs-generated shader tolerances (only structure of MaterialXTest located); glTF sample renderer conformance; any published "bit-exact vs ulp class" policy statement from Khronos, MaterialX, OSL or Blender; the Vulkan CTS source ULP constants for mix/smoothstep/fma.

## 5. Floored modulo specifics

38. [src] Definition in Cycles: `a - floorf(a / b) * b` (item 7). For a=10000, b=0.1f the quotient a/b rounds to 100000 (float) and floorf gives 100000, so the unfused product 100000*b rounds to 10000 and the result is 0, as the oracle records; the fused form computes the residual of the exact product (b is not exactly 0.1), yielding -1.49e-4 as the finding records. [abs] This arithmetic was not re-run here; it restates the finding's `kernel_check` docstring (oracle.py).
39. [abs] No Blender issue or commit mentioning Floored Modulo together with FMA, contraction or AVX2 was found. [web] Related reports concern modulo/wrap disagreements between Cycles and Eevee/GPU drivers (#101562, #68687, #88151).
40. [doc] Vulkan states OpFMod is "inherited from x - y * floor(x/y)", i.e. the same formula, so a GPU implementation of it follows its own contraction/precision rules (item 25).

## Coverage and gaps

- Strong [src]: Cycles flag set per ISA and compiler, the history of `-ffp-contract`, `safe_floored_modulo`, `CYCLES_CPU_NO_AVX2` mechanism, GPU backends' fast-math flags, Cycles/OSL image-test thresholds, Vulkan NoContraction and OpFMod text, HLSL `precise`.
- Gaps: which compiler/version builds official Blender 5.2.2 release binaries (matters for how `-ffp-contract=on` is honoured); the 4.x flags before/after 2024-09-30 at release tags; DXC/Unity default contraction; Metal contraction pragma; MaterialX, glTF and Vulkan CTS numeric tolerances; a direct statement that RyuJIT never contracts; Blender developer position on CPU/GPU parity beyond forum/issue summaries (several pages 403 to WebFetch).
- Files of record: `C:\Users\Meta\AppData\Local\Temp\claude\F--Projects-CultLib\0d5eef6e-e366-457f-bbc6-0151080ae1a8\scratchpad\eyes-cycles-fma\dl\` (CMakeLists.txt, math_base.h, math_util.h, vk.html).
