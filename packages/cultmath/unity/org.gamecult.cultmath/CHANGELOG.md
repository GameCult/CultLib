# Changelog

All notable changes to this package are documented in this file.

Earlier releases (0.2.0-0.2.3) predate this package's changelog and are not
backfilled here; see `docs/semver-policy.md` for why.

## [0.4.0]

### Breaking

- `math.snoise(float3)` and `math.snoise_grad`, and their HLSL mirrors in
  `Shaders/CultMath.hlsl`, now resolve ties among the components of the cell
  offset in a fixed order: x over y, y over z, x over z. The cyclic rule they
  used before read a different function wherever the three components of the
  cell offset are equal. That is every input whose coordinate differences
  `y - x` and `z - x` are whole numbers: every integer point (the origin
  included), and the whole `(1, 1, 1)` line through each of them (`(t, t, t)`
  among them). On that set values change by up to 0.776; for example
  `snoise(0, 0, 0)` was -0.4358730 and is 0, `snoise(5, 0, 0)` was 0.7076616 and
  is 0.671713, and `snoise(1, 2, 3)` was 0.22423841 and is 0. Float32 rounding
  decides whether a given input ties, and an input that ties may still keep its
  value; measured over random integer points, about 97% change. The cell offset
  is computed in float32, so an input whose differences `y - x` and `z - x`
  round, in float32, onto whole numbers can tie and change too. How far from the set that
  reaches scales with the coordinates' magnitude: a few float32 ulps of the largest
  coordinate (a probe over random inputs found changed inputs up to about two ulps
  away; measured, not proved). Computed coordinates such as `i * 0.1f`
  can land there, so near large coordinates the band is wide. Elsewhere the results are
  bit-identical to 0.3.0 (`SnoiseTieScopeTests`, goldens computed by the 0.3.0
  DLL). `snoise` is now
  continuous across the diagonals to within 1e-4 over two ulps either side
  (`NoiseGradTests`, `SnoiseIsContinuousOnTheSimplexDiagonal`). Anything keyed
  on integer lattice coordinates (voxel or tile seeds, cell centres, generated
  content sampled on a whole-number grid) changes; regenerate it when you take
  this version.

### Added

- Interval arithmetic on `float2(lo, hi)`: `math.iv_point`, `iv_add`, `iv_sub`,
  `iv_mul`, `iv_neg`, `iv_scale`, `iv_abs`, `iv_min`, `iv_max`, `iv_clamp`,
  `iv_saturate`, `iv_lerp`, `iv_sqr`, `iv_sqrt`, `iv_exp`, `iv_smoothstep`,
  `iv_snoise_ball` (`float3` and `float2` centres), `iv_fbm_ball` and
  `iv_frustum_ball`, with the HLSL mirrors in the new
  `Shaders/CultMath.Interval.hlsl`. Each function is built to return an interval that
  contains the pointwise float32 result for the operand intervals, but the noise
  functions rest on empirical constants with a margin, not on a proof:
  `iv_snoise_ball(float3)` on `SNOISE_LIPSCHITZ`, `iv_fbm_ball` on the same constant through
  `iv_snoise_ball(float3)`, `iv_snoise_ball(float2)` on `SNOISE2_LIPSCHITZ`, and
  `af_snoise` on `SNOISE_HESSIAN` and `SNOISE_LIPSCHITZ`, which `af_fbm` reaches through
  `af_snoise`. `iv_frustum_ball` and `af_frustum_ball` read no noise constant: they are
  geometry plus a 2^-20 rounding widening, and they return a ball (`float4`) to hand to a noise
  call, which carries the constant. The enclosure tests sample
  points and check containment without a tolerance; they do not prove it for
  every input.
- Reduced affine forms on `float3(x0, a, e)` that share one symbol over a
  region: `math.af_point`, `af_symbol`, `af_range`, `af_from_iv`, `af_add`,
  `af_add_iv`, `af_sub`, `af_neg`, `af_scale`, `af_mul`, `af_snoise`, `af_fbm`,
  `af_frustum_axis` and `af_frustum_ball`, with the HLSL mirrors in the new
  `Shaders/CultMath.Affine.hlsl`.
- Noise bound constants `math.SNOISE_LIPSCHITZ`, `SNOISE2_LIPSCHITZ` and
  `SNOISE_HESSIAN`: empirical bounds with a margin, re-measured and pinned by
  `NoiseBoundTests`.
- `math.asfloat(uint)`, the inverse of `math.asuint`.
- `BoundedLeastSquares.Solve` overload taking `kktRelativeTolerance`, and the
  constant `BoundedLeastSquares.DefaultKktRelativeTolerance` (1e-6). The
  tolerance must be finite and greater than zero, else the result is
  `InvalidInput`. The overload without it passes the default and returns the same
  result as before.

### Fixed

- The NuGet package `GameCult.Math` now carries `CultMath.Affine.hlsl` under
  `contentFiles/any/any/shaders`, as the README says it does.

## [0.3.0]

### Breaking

- `math.snoise(float3)` and `math.snoise(float2)`, and their HLSL mirrors
  `cultmath_snoise`, return different values. The kernel now follows
  stegu/webgl-noise. For 3D only, the falloff `r^2` constant is 0.5 (was 0.6)
  and the output scale is 105 (was 42). For 2D, the falloff constant (0.5) and
  the output scale (130) are unchanged. In both, the shared permutation offset
  is now +10 (was +1), which changes the values of both. This removes the value
  discontinuities the 3D kernel had at simplex cell boundaries. Any content
  seeded from `snoise` changes; regenerate it when you take this version.

### Added

- `math.smin_grad`: smooth minimum with gradient, for `float4` operands
  carrying the gradient in `xyz` and the value in `w`.
- `math.cellular` and the `CultCellular` result struct: cellular (Worley)
  noise returning the gradient and distance to the nearest feature points.
- `math.snoise_grad`, `math.fbm_grad` and `math.ridged_grad`: simplex noise,
  fractal Brownian motion and ridged multifractal with analytic gradients,
  returned as `float4` with the gradient in `xyz` and the value in `w`.
- `math.phacelle` and the `CultPhasor` result struct: Phacelle noise with
  gradient. `math.Phacelle.cs`, `CultPhasor.cs` and
  `Shaders/CultMath.Phacelle.hlsl` are MPL-2.0 (see
  `THIRD-PARTY-NOTICES.md`).
- `BoundedLeastSquares`: an allocation-free, deterministic bounded
  least-squares solver (`Solve`) with a status result and the input contract
  documented on the type.
- The matching `cultmath_*` HLSL mirrors of the functions above in
  `Shaders/CultMath.hlsl` and `Shaders/CultMath.Phacelle.hlsl`.

## [0.2.4]

### Added

- `math.erf` and `math.erfinv`: single-precision error function and its
  inverse. `erf` is the Abramowitz & Stegun 7.1.26 rational/exponential fit
  (max absolute error 1.5e-7 by that source, 6.621e-7 measured here against
  an independent reference). `erfinv` is Giles' minimax polynomial
  ("Approximating the erfinv function", GPU Computing Gems, 2010), guarded
  explicitly at the domain edges (`erfinv(1) = +Infinity`,
  `erfinv(-1) = -Infinity`, outside `[-1, 1]` is `NaN`); measured worst
  absolute error 5.066e-7, worst `erf(erfinv(y))` round trip 6.109e-7.
