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
