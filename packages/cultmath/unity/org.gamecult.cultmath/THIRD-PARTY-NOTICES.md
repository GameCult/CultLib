# Third-Party Notices

## 2D and 3D Simplex Noise

`math.snoise` and `cultmath_snoise` port the textureless 2D and 3D
simplex-noise functions by Ian McEwan and Ashima Arts, as distributed by Stefan
Gustavson's `webgl-noise` project and Unity Mathematics.

`math.snoise(float3)` and `cultmath_snoise(float3)` follow `src/noise3D.glsl` and
`math.snoise(float2)` follows `src/noise2D.glsl` of stegu/webgl-noise at commit
`22434e04d7753f7e949e8d724ab3da2864c17a0f` (the simplex-boundary fix is
`21d9fe23d7`, scale `ff3b5d34ea`, permutation `a3e6d57095`).

`math.snoise_grad`/`cultmath_snoise_grad` (and the `fbm_grad`/`ridged_grad`
octave sums built on them) follow the analytic-gradient differentiation from
the same project's `src/noise3Dgrad.glsl`, with the same kernel radius,
permutation and scale as `snoise(float3)`.

- Copyright 2011 Ashima Arts.
- Upstream: <https://github.com/ashima/webgl-noise>
- License: MIT.

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
  so the `GameCult.Math` NuGet package declares `MIT AND MPL-2.0` and ships this
  file. The Unity package as a whole is MPL-2.0.
