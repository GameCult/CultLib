# Third-Party Notices

## 2D and 3D Simplex Noise

`math.snoise` and `cultmath_snoise` port the textureless 2D and 3D
simplex-noise functions by Ian McEwan and Ashima Arts, as distributed by Stefan
Gustavson's `webgl-noise` project and Unity Mathematics.

`math.snoise_grad`/`cultmath_snoise_grad` (and the `fbm_grad`/`ridged_grad`
octave sums built on them) follow the analytic-gradient differentiation from
the same project's `src/noise3Dgrad.glsl`, retargeted onto this file's own
`snoise(float3)` falloff radius, permutation, and scale constants (see the
`math.snoise_grad` doc comment).

- Copyright 2011 Ashima Arts.
- Upstream: <https://github.com/ashima/webgl-noise>
- License: MIT.
