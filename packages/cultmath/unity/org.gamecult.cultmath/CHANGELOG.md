# Changelog

All notable changes to this package are documented in this file.

Earlier releases (0.2.0-0.2.3) predate this package's changelog and are not
backfilled here; see `docs/semver-policy.md` for why.

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
