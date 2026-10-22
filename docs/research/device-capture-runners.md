# Device capture runners: how established projects capture real-GPU results, and what a browser driver can silently get wrong

For `cultmath-tapes:question:device-run-driver` (campaign `cultmath-tapes`) and follow-up `cultmath-tapes:follow_up:glsl-browser-parity-run`.
Gathered by eyes-ag3-device-driver, session self-2026-10-15-ag3, 2026-10-16. Facts only; no recommendation.

Strength marks: [src] read from a primary source text this pass; [doc] vendor or standards documentation read this pass; [web] search-result or fetch-summary of a page (a small model summarised it; check before quoting); [abs] searched, not found.
Chromium doc pages were read at `main` or at an old pinned revision as noted; Chromium GPU bot details drift, so the dates matter.

## 0. What the question asks (the record)

- [src] Question `device-run-driver` (Imagination, 2026-10-16): a static page (`packages/cultmath/tools/device-runner`) compiles each family's call against `CultMath.glsl`, reads back through an RGBA32UI target, emits raw bits; C# judges. Options: `committed-captures` (page + stdlib Python server, any WebGL2 browser is a driver, captures committed with renderer, revision, shader sha, args hash, result bits, judged by `DeviceCaptureTests` on Linux), `playwright-live` (Microsoft.Playwright test dependency, Windows-only, ANGLE D3D11, must refuse SwiftShader), `both`.
- [src] Prior precedent named in the question: three Soul/Hands passes through the desktop app's built-in browser on Starfire, renderer `ANGLE (NVIDIA GeForce GTX 1070 Direct3D11)`.
- [src] Follow-up `glsl-browser-parity-run` records both GLSL files probed on a GTX 1070 (phacelle within 3.1e-6 of max(|v|,1), frustum check 0/256).

## 1. How established projects capture real-GPU results for CI

| Project | What runs where | What is stored, and where | What identifies the device | Source |
|---|---|---|---|---|
| Khronos WebGL conformance suite | Static HTML pages. Open `webgl-conformance-tests.html` in the browser under test (`?run=1` auto-starts), then "display text summary". Run by hand in any browser. | Formal results go as a pull request to the `KhronosGroup/WebGLConformanceSubmissions` repo. Older README copies said email `webgl_conformance_submissions@khronos.org`. Only WebGL implementors submit. | README instruction: "Verify that the User Agent and WebGL renderer strings identify your browser and target correctly." The summary text carries them. | [src] raw.githubusercontent.com/KhronosGroup/WebGL/main/sdk/tests/README.md (read 2026-10-16); [web] search summary of CONFORMANCE_RULES.txt |
| Khronos WebGL conformance rules | Results carry forward: "results submitted for an earlier version of the browser carry forward to later versions ... that do not cause any previously passing test to fail"; for OS versions likewise (a new OS version needs a new submission). | The carry-forward rule is the only staleness rule found. | | [web] search summary of CONFORMANCE_RULES.txt, not read in full |
| Vulkan CTS conformance submission | Run on the vendor's driver builds, every fraction. | Submission package must contain full `TestResults.qpa` logs per driver build and fraction, `git status` and `git log --first-parent <release tag>^..HEAD` output of a clean CTS checkout, `git format-patch` output of any CTS changes over the release tag, and a statement file with a `CONFORM_VERSION` line carrying the CTS git tag, plus PRODUCT, CPU, OS. `verify_submission.py` checks the package against the current mustpass list. | PRODUCT/CPU/OS lines in the statement. | [web] search summaries of `external/vulkancts/README.md` in Fuchsia/Android/Chromium mirrors (mirror copies differ by version) |
| Chromium GPU bots | Physical machines in Swarming; most tests via Telemetry launching the full browser; `webgl_conformance_tests` and pixel tests. `chromium.gpu.fyi` tests top-of-tree ANGLE. | Expected outcomes in per-test expectation files (`pixel_expectations.txt` under `content/test/gpu/gpu_tests/test_expectations/`, `Failure` entries). Pixel images are not in the repo (Skia Gold, below). Some pixel tests check only a few pixels with handwritten code so one check serves all vendors. | Swarming task dimensions: `gpu` = PCI vendor:device-driver version (example `10de:2184-440.100`), plus `os`, `cpu`, `pool`. The page does not describe a runtime renderer-string check. | [src] chromium.googlesource.com/chromium/src/+/main/docs/gpu/gpu_testing.md (read 2026-10-16) |
| Skia Gold (Chromium pixel tests) | goldctl client sends the produced image, the test name and hardware/software config metadata to a service. | Approved images live in a service (storage bucket + triage UI), not git. A test passes if the hash of the produced image is in that test's approved-hash list; default exact matching, multiple baselines per test. An unknown hash fails until a human triages it. Doc says local runs "differ slightly from bot images" and uploads need allow-listing; `--local-pixel-tests` disables upload. | Config metadata uploaded with each image. | [web] search summary of `docs/gpu/gpu_pixel_testing_with_gold.md` (pinned old revisions); [src] gpu_testing.md for the local-run flags |
| WebGPU CTS in Chromium | gpuweb/cts compiled to JS, run through WPT and Telemetry; Telemetry covers non-virtual devices with real GPUs. A pinned revision is auto-rolled. | Expectations per platform: Fail/Skip may be any CTS query; Slow/Crash/Timeout must list parameters in loader order. | | [web] search summaries of Chromium `web_tests/webgpu/README.md` and a 2022 W3C public-gpu post by Kai Ninomiya (not read in full) |
| Dawn node runner | `dawn.node` binding runs the CTS without a browser; `run-cts` can instead drive a Chrome instance. | `--output <path>` writes a results file, `--expect <path>` diffs a later run against it. Dawn keeps `webgpu-cts/expectations.txt`, pruned when tests start passing, with wide `Failure` wildcards for flaky ones. | | [web] search summaries of Dawn `src/dawn/node/README.md` and commit `f78edf56` ("Expand CTS expectations for Win11 QC CI bot") |
| three.js | `test/e2e/puppeteer.js` screenshots almost all examples and compares to stored references; helpers `deterministic-injection.js`, `clean-page.js`, `check-coverage.js` exist in an older release's `test/e2e`. | Reference screenshots in the repo. | A 2020 issue: screenshots were black on macOS with the GPU-path flags; a maintainer floated using SwiftShader everywhere "to avoid GPU-specific bugs". Current flags and CI setup not confirmed. | [web] search summaries of discourse.threejs.org and a lightrun mirror of the GitHub issue; [abs] current `puppeteer.js` flags not read |
| Babylon.js / Babylon Native | Visualization tests list scenes in `tests/test/visualization/config.json` (Babylon Native: `Apps\ValidationTests\Scripts\config.json`); each entry names a reference image, optional per-scene pixel-difference threshold. | Reference images in `ReferenceImages/` in the repo; `-u` regenerates. Babylon Native CI fails on too-different images and stores rendered/error images as an artifact. | | [web] search summaries of forum.babylonjs.com and a Babylon Native `ContinuousIntegration.md` fork; [abs] an `errorRatio` key was not found |
| glslang / SPIRV-Cross | No GPU. SPIRV-Cross round-trips `shaders/` through glslangValidator/spirv-as and SPIRV-Cross, diffs output text against committed `reference/` files; `test_shaders.py` exits non-zero on difference, `--update` regenerates references that then ride in the PR; says to use the right glslang and SPIRV-Tools versions. Only hardware option: `--malisc` static cycle counts. | Reference text in the repo. | | [src] raw.githubusercontent.com/KhronosGroup/SPIRV-Cross/main/README.md (read 2026-10-16); [abs] glslang's own test practice not read |
| dEQP GLES3 builtin precision tests | Random inputs through built-ins in a shader; outputs via transform feedback or framebuffer pixel; compared to a reference interval, not a single value. | Spec text of tolerances (section 4). | | [src] doc/testspecs/GLES3/functional.shaders.builtin_functions.precision.txt (Fuchsia mirror, read 2026-10-16) |

Observations across the rows (no ranking):
- [src/web] Where real-GPU outputs are exact or large, the approved results are stored outside a source repo (Gold, Swarming expectations) or as submission packages (WebGL, Vulkan CTS). Where outputs are text from a deterministic tool (SPIRV-Cross) the reference is committed beside the source and `--update` regenerates it.
- [web] Only the Vulkan CTS submission package was found to record the exact source revision (tag plus `git log`) inside the result bundle; WebGL's rule is the carry-forward rule above. No project was found that refuses a committed capture automatically because a shader source hash changed. [abs]
- [src] Chromium's GPU doc keys a bot's hardware by PCI id and driver version rather than by a renderer string read inside the page.

## 2. Headless browser drivers on real GPUs

### 2.1 Fallback to a software renderer

- [src] Chromium SwiftShader doc (read at `refs/heads/main`): flags `--use-gl=angle --use-angle=swiftshader` (SwANGLE as the ES driver); `--use-gl=angle --use-angle=swiftshader-webgl --enable-unsafe-swiftshader` (WebGL fallback). "Automatic fallback to SwiftShader-backed WebGL is deprecated ... WebGL context creation will soon fail instead of falling back." `--enable-unsafe-swiftshader` opts in and suppresses the console warning. The page does not describe how to verify the renderer.
- [web] Blink-dev "Intent to Remove: SwiftShader Fallback": deprecation period began Chrome 130; estimated desktop ship Chrome 137; a later reply from Rick Byers says API owners consider it high-risk and may phase it, so the milestone was unconfirmed in the thread; Finch feature `AllowSwiftShaderFallback`; headless SwiftShader use "will still be supported by opting in"; desktop only. Sources disagree on the shipped milestone (a Winaero Chrome 137 write-up says WARP replaces it on Windows, which the blink-dev page does not mention; Samsung's browser policy page says M139). Not resolved. [web]
- [src] Chrome developer blog "Supercharge Web AI model testing": "By default, Headless Chrome disables GPU." `chrome://gpu` showed "Software only, hardware acceleration unavailable" until drivers were fixed; "Some combinations of flags allow detection of the GPU, but don't allow you to actually use the GPU." Failing renderer string: `ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) (0x0000C0DE)), SwiftShader driver-5.0.0)`; working: `ANGLE (NVIDIA Corporation, Tesla T4/PCIe/SSE2, OpenGL ES 3.2 NVIDIA 525.105.17)`. Linux flags there: `--headless=new --use-angle=vulkan --enable-features=Vulkan --disable-vulkan-surface --enable-unsafe-webgpu`. Setup was Colab, not CI.
- [web] A Playwright-on-GPU-runner write-up (createit.com) reported renderer ANGLE-on-Vulkan with SwiftShader; headed Chromium under a virtual display used the GPU; a Vulkan flag set later gave `llvmpipe`, still software.
- [src] Chromium GPU testing doc: example bot flags `--use-gl=angle --use-angle=gl --use-cmd-decoder=passthrough --force_high_performance_gpu`, passed via `--extra-browser-args`.
- [src] Playwright `chromiumSwitches.ts` at `main` (read 2026-10-16) contains no GPU, SwiftShader, ANGLE or headless switch. [abs] What the downloaded headless shell binary itself defaults to was not found.
- [abs] No source was found on `--use-angle=d3d11` combined with headless Playwright on Windows, and none on the renderer string a Windows GPU reports under headless.
- [web] `--disable-gpu` is reported by one source to force software rendering.

### 2.2 Detecting a fallback from inside the page

- [doc] `WEBGL_debug_renderer_info` exposes `UNMASKED_RENDERER_WEBGL` through `gl.getExtension(...)` then `gl.getParameter(...)`; MDN says it may be limited to privileged contexts depending on privacy settings, and Firefox disables it when `privacy.resistFingerprinting` is true, so `getExtension` can return null (MDN, via search result).
- [doc] `failIfMajorPerformanceCaveat: true` makes `getContext` return null if the browser would use a dramatically slow implementation (MDN, via search result). [web, unconfirmed here] whether Chrome treats SwiftShader as such a caveat was not found in a source this pass.
- [src] Strings observed in sources: SwiftShader `ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device (Subzero) ...))` and `Google SwiftShader`; hardware `ANGLE (NVIDIA ..., Direct3D11)` form as in the question's precedent.
- [src] Chromium's own bots do not read a string; they identify hardware by Swarming dimensions (section 1).
- [web] `WebGLShaderPrecisionFormat` from `getShaderPrecisionFormat(FRAGMENT_SHADER, HIGH_FLOAT)`: `precision` 23 means real highp float; it reports mantissa bits, nothing about subnormal handling; Safari mis-reported it as of April 2020 (WebKit bug via webglfundamentals).
- [web] A sample WebGL report line: `ANGLE (Intel(R) HD Graphics 4600 Direct3D11 vs_5_0 ps_5_0)`, highp/highp, precision 23.

### 2.3 Windows service and session 0

- [web] Microsoft XPDM-vs-WDDM table: on WDDM, Session 0 isolation means a Direct3D 9 HAL device is never available from a service (NULLREF, HAL, REF "No"; WARP10 "Yes").
- [web] Microsoft support article 978635: starting with Windows 8.1 / Server 2012 R2, Direct3D 11 supports use from a Windows service. WARP is recommended by Microsoft for service/server environments.
- [web] VTK issue 18657 (GitLab): swap-chain creation failed on Windows CI runners because the process ran in Session 0, which has no graphics hardware access; runners were moved from system services to scheduled tasks.
- [abs] No source found on headless Chrome or ANGLE behaviour specifically inside a service. The pattern the sources support is an interactive user session or a scheduled task that runs only when the user is logged on.

## 3. Committed-capture designs: versioning against source hashes and refusing stale captures

- [src] SPIRV-Cross: references are text regenerated by the tool; staleness is detected by re-running the tool and diffing, not by a stored hash. The tool versions (glslang, SPIRV-Tools) are named in the README as the ones to use.
- [web] Skia Gold: the approved set is keyed by test name plus image hash; an unknown hash fails until triaged. Config metadata is uploaded with the image. No source-revision binding was found.
- [web] Vulkan CTS: the result package carries the CTS tag, `git log` and patches; a checker script validates the package against the mustpass list for the version.
- [web] WebGL: carry-forward rule (earlier passing results hold for later browser versions that fail no previously passing test).
- [web] Babylon / three.js: reference image names live in a config list; regeneration is a flag (`-u`); a missing reference is an error (a 404 in a Karma run, Babylon forum).
- [src] Chromium expectation files: per-test `Failure` entries are removed once a test passes (gpu_testing.md); Dawn prunes `expectations.txt` the same way.
- [abs] No project was found whose committed device capture embeds a hash of the shader source it ran and is rejected by CI when that hash no longer matches the checked-in source. (Search was for the pattern, not an exhaustive survey of repositories.)

## 4. Float behaviour that bears on judging captures across ANGLE/D3D11, native GL and mobile GPUs

### 4.1 What the standards guarantee

- [doc] Qualifiers are minimums. highp float "is at least a 32 bit value", range about +-2^62 in the WebGL2Fundamentals table (exponents stripped in extraction, inferred); mediump at least 16 bit, range +-2^14, relative precision 2^-10; lowp at least 9 bit, range +-2 (webgl2fundamentals precision lesson; asawicki.info April 2015 for the 2^-10 and 2^-8 figures). On a desktop GPU mediump "will still most likely use 32bits internally"; the lesson's author's Pixel 2 XL used 16 bit for mediump and lowp.
- [doc] Highp in fragment shaders is optional in OpenGL ES 2.0 (a shader declaring it fails to compile where unsupported). [web] GL_ARB_shader_precision text: internal processing need not match IEEE 754; rounding mode cannot be set and is undefined; "any denormalized value input into a shader or potentially generated by an operation in a shader can be flushed to 0."
- [src] dEQP GLES3 precision spec tolerances (ULP unless noted): `+ - *` under 1 ULP; `/` 2.5 ULP for 2^-126 <= |y| <= 2^127-1; sin/cos 2^-11 abs on [-pi, pi]; asin/acos 4; atan2 6; atan 5; exp 3+2|x| highp; inversesqrt 2; log 3 ULP highp outside the central range; exact (0 ULP) abs, sign, floor, trunc, roundEven, ceil, min, max, clamp, step; derived functions (tan, pow, sqrt, mix, smoothstep, fract, mod ...) judged by their expansions. The page does not mention flush to zero. Lowp and mediump results on overflow "must produce an infinity or the max/min value".
- [web] Olli Etuaho on the WebGL list, 14 Nov 2014: proposes testing only at the spec minimums, with subnormals flushed to zero, since code that works there will likely work with more bits; concedes not simulating built-in internals is a gap.

### 4.2 Direct3D 11 (the ANGLE/D3D11 path)

- [doc] Microsoft Learn "Floating-point rules (Direct3D 11)" (updated 2025-04-15), 32-bit rules: "Denorms are flushed to sign-preserved zero on input and output of any floating-point mathematical operation" (exceptions: I/O or data movement that does not manipulate the data). Add/sub/mul within 0.5 ULP, and the page says hardware may truncate; sqrt and rcp 1 ULP; rcp and rsq have a separate relaxed requirement; "Fused operations (such as mad, dp3) produce results that are no less accurate than the worst possible serial ordering of evaluation of the unfused expansion", each unfused step allowed 1 ULP (D3D10 page: 1 ULP for mul/div). NaN bit patterns are not preserved. min/max flush denorms for comparison but the result "may or may not be denorm flushed". Doubles preserve denorms; 16-bit floats preserve denorms.
- [abs] No source found that states how ANGLE's D3D11 translation, fxc/dxc flags, or NVIDIA's D3D11 driver treat denormals or contraction beyond the D3D rules above.
- [web] ANGLE's `FeaturesD3D.h` workaround list contains HLSL-compiler-specific rewrites (for example expanding `pow` into multiplies on an affected compiler, per search summary); a Wine D3D11 trig test failed on one NVIDIA configuration with roughly 0.1% relative error, not on AMD (Wine bug thread, cause not identified).
- [web] ANGLE D3D11 vs D3D9 rendering differences are reported in Mozilla bug 1122912 (unresolved in the excerpt).

### 4.3 Mobile and other native GPUs

- [web] ARM blog "Benchmarking floating point precision in mobile GPUs, Part III": Vivante GC4000 and Imagination SGX 554 "do not support subnormal values" (below 2^-126 treated as zero); Mali-T604 supports subnormals, and the authors say Mali Midgard is the only mobile family they know to; the measuring shader divides by 2 per exponent step and multiplies back, drawing underflow pixels red. No ULP measurements in that part; Part II (not read) is said to find GPUs taking "the easy (round-to-zero) way out".
- [web] asawicki.info: `length`, `normalize`, `distance` and dot products in a `mediump` shader can overflow at (74,74,74) because 3*74^2 = 16428 exceeds 2^14; a GPU may pass because it uses more precision than required.
- [abs] No source read this pass on Adreno, Apple, current Mali or PowerVR denormal and contraction behaviour; the ARM post covers 2013-era parts.
- [abs] FMA contraction in GLSL: no WebGL or GLSL ES source was read here on whether `a*b+c` may be fused (see `docs/research/cycles-fma-contraction.md` for the Cycles CPU case and the D3D "mad" fused-op tolerance above).

## 5. Gaps left by this pass

- Current three.js `test/e2e/puppeteer.js` flags and Babylon `config.json` tolerance keys were not read from the repositories (search summaries only).
- Gold, WebGPU CTS and Dawn node pages are search summaries, not full reads.
- Whether Chrome's shipped (not 130-era) behaviour fails WebGL context creation when no GPU is usable, versus using WARP on Windows, is unresolved between the sources in section 2.1.
- Nothing here was measured on Starfire; no run of any driver was made.
