# Third-Party Notices

## 2D and 3D Simplex Noise

`math.snoise` and `cultmath_snoise` port the textureless 2D and 3D
simplex-noise functions by Ian McEwan and Ashima Arts, as distributed by Stefan
Gustavson's `webgl-noise` project and Unity Mathematics.

`math.snoise(float3)` and `cultmath_snoise(float3)` follow `src/noise3D.glsl` and
`math.snoise(float2)` follows `src/noise2D.glsl` of stegu/webgl-noise at commit
`22434e04d7753f7e949e8d724ab3da2864c17a0f` (the simplex-boundary fix is
`21d9fe23d7`, scale `ff3b5d34ea`, permutation `a3e6d57095`). Modified from upstream:
the 3D corner tie order follows Gustavson's `simplexnoise1234`, because the
upstream `step` compares are cyclic where the offset's components are equal.

`math.snoise_grad`/`cultmath_snoise_grad` (and the `fbm_grad`/`ridged_grad`
octave sums built on them) follow the analytic-gradient differentiation from
the same project's `src/noise3Dgrad.glsl`, with the same kernel radius,
permutation and scale as `snoise(float3)`.

- Copyright 2011 Ashima Arts.
- Upstream: <https://github.com/ashima/webgl-noise>
- License: MIT.

## Cycles node semantics

`math.cycles_*` and `cultmath_cycles_*` port what Blender's Cycles renderer
computes for the Math, Map Range, Clamp, Mix and RGB to BW nodes and for a Color
Ramp lookup. Each function's comment names the Cycles file it follows, all from
`intern/cycles` at the Blender `v5.2.2` tag: `kernel/svm/math_util.h`,
`kernel/svm/map_range.h`, `kernel/svm/clamp.h`, `kernel/svm/mix.h`,
`kernel/svm/color_util.h`, `kernel/svm/ramp.h`, `kernel/svm/convert.h`,
`util/math_base.h`, `util/math_float3.h` and `util/color.h`. The code was
changed: Cycles C++ became C# and HLSL, one function per node operation, with
the nodes' socket defaults and clamp options left to the caller, and the modulo
is the quotient form every CultMath target shares rather than `fmodf`. Nothing
was read from Blender's GPL sources (`source/blender/gpu`, `source/blender/blenlib`).

- Copyright 2011-2022 Blender Foundation.
- Upstream: <https://projects.blender.org/blender/blender> (`intern/cycles`).
- License: Apache-2.0 (<https://www.apache.org/licenses/LICENSE-2.0>), which is
  per-file here: it covers `src/CultMath/math.Cycles.cs`,
  `shaders/CultMath.Cycles.hlsl` and the Unity copy of that include, and
  `shaders/CultMath.Cycles.glsl`, which is generated from that include. Each
  carries the Apache-2.0 header and this attribution. The rest of CultMath, the
  generated GLSL library included, is MIT or MPL-2.0 as above, so the
  `GameCult.Math` NuGet package and the `org.gamecult.cultmath` Unity package
  both declare `MIT AND MPL-2.0 AND Apache-2.0` and ship this file.
- The test fixtures under `tests/CultMath.Tests/fixtures/cycles-oracle` are
  results of running Blender 5.2.2 through `tools/cycles-oracle/oracle.py`
  (GPL-3.0-or-later, a Blender Python script, shipped neither in the NuGet
  package nor in the Unity package).

## Phacelle Noise

`math.phacelle` and `cultmath_phacelle` follow Rune Skovbo Johansen's Phacelle
noise (Shadertoy `t3dyWl`; erosion filter `wXcfWn`; blog, March 2026), read
from the C# port `PhacelleNoise` in `lpmitchell/AdvancedTerrainErosion`
(`AdvancedTerrainErosion.cs`, converted to burstable C# by Luke Mitchell,
2026). The cell weight, the phase blend and the normalization follow it. The
3D-cell generalisation, the caller-supplied stripe wave vector, the exact
gradient and the exact pruning are new here.

- Copyright (c) 2025 Rune Skovbo Johansen.
- Upstream: <https://github.com/lpmitchell/AdvancedTerrainErosion>
- License: MPL-2.0 (<https://mozilla.org/MPL/2.0/>). MPL-2.0 is file-level, so it
  covers exactly the files that carry the port and nothing else:
  `src/CultMath/math.Phacelle.cs`, `src/CultMath/CultPhasor.cs`,
  `shaders/CultMath.Phacelle.hlsl` and the Unity copy of that include. Each
  carries the MPL-2.0 header and this attribution. The rest of CultMath is MIT,
  so the `GameCult.Math` NuGet package and the `org.gamecult.cultmath` Unity
  package both declare `MIT AND MPL-2.0` and ship this file.
