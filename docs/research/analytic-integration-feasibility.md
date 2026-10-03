# Analytic noise integration along warped rays: feasibility probe

Campaign `cultmath-tapes`, Imagination probe, 2026-10-03. This is a measurement,
not a spec. The tape target stays parked under ruling
`cultmath-tapes:ruling:tape-target-unparks-when-asura-stable`.

**Verdict.** Exact integration of the noise does not beat sampling for optical
depth on either shipped field.
- The exact line integral of CultMath `snoise` is cheap and correct. Affine warp
  spans are long, at 100 to 300 world units.
- The exact noise integral is not the optical depth, because both densities are
  nonlinear in the noise.
  - The void's wall profile is `pow(·, 0.40)` with two clamps.
  - Aetheria's fog is clamped at zero and goes opaque within a few world units.
- Given the *exact* span mean of the noise for free, the resulting transmittance
  is still 3.6x worse than the shipped march on the void and 22x worse on the
  wells.
- Exact integration wins only on the linear functional `∫ n dt` of octaves the
  march undersamples. That gain does not survive the density's nonlinearity.
- Aetheria's triangle noise is integrable exactly as piecewise linear, but costs
  3x to 22x the march, and its shipped form does not meet the preconditions.

## Scenarios and method

- **Void:** the shipped void (`NoiseBoundTests.VoidField.Shipped()` at
  `hands/cultmath-interval-ops` 2c8585bd).
  - Rh 198, S 50, Rc 248, K 1/30, A 20, F0 0.01 (cells of 100 and 25 world), warp
    D 10.
  - The shipped orbiting camera, N = 8 tiles at f = 935, one jittered pixel ray per
    tile.
- **Wells:** the test's Aetheria wells at warp D = 60 (`FogField.Wells(60)`).
  - F0 1/414.2167, A 20, B 10, extinction 0.5, flow frequency 1/512, period 8.
  - Like the test, this uses the snoise field shape (r1's), not Aetheria's own
    triangle noise. Triangle noise is measured separately below, on the same rays.
- **Rays:** 200 per scenario, and 120 for the triangle basis.
  - The wells give 147 rays with an active interval; the rest look up and miss
    the fog.
  - The active interval is where the envelope leaves the density to the noise.
    It is cut where the true optical depth reaches 6.
  - Active length per ray: void median 140 world (p10 114, p90 193); wells median
    268 (p10 137, p90 623).
- **March steps:** the void's march step (LodStep, 12.5 in z) is a median 14.9
  world along the ray. The wells' quadratic grid gives a median of 9.1 in the
  active region.
- **Warp:** `w(p) = flow(p) · shift`, so the affine error scales exactly with
  `|shift|`.
  - Spans are grown at the worst phase shift, Period / 2. A span is therefore
    valid at every frame's phase, for any camera that crosses the same world
    segment.
  - Each span is anchored at p0 with J·d from a central difference. It grows in
    1-unit (void) or 2-unit (wells) steps until `|w − (w0 + J·d·Δ)| > τ`.
- **Exact simplex integral:** CultMath `snoise(float3)` has kernel radius² 0.5,
  so its support never leaves the incident simplices.
  - Along a line it is therefore a sum over the lattice points within √0.5 of the
    line. Each point contributes `∫ (0.5 − |x|²)⁴ (g·x) dt` on its chord, a
    closed-form degree-9 polynomial.
  - Validation:
    - The lattice sum matches `snoise` within 2e-4 (float32 rounding at
      coordinates near 300).
    - The segment integral matches a 5e-4-step quadrature within 2.5e-5 per
      unit length.
- **Run:** probe code was in a scratch clone (probe-clone@91c8cbf1, never merged)
  and ran on Yggdrasil through the verify stopgap, in a dotnet 10 container with
  4 CPUs. Timings are single-threaded C# on that host and are relative.

## 1. Affine-valid span length (ray length, world units)

| τ (× coverage cell) | Void p10 / median / p90 | Wells p10 / median / p90 |
|---|---|---|
| 0.05 | 94 / 129 / 211 | 71 / 103 / 169 |
| 0.10 | 141 / 197 / 324 | 108 / 151 / 253 |
| 0.25 | 263 / 420 / 1244 | 184 / 275 / 715 |
| 0.05 × detail cell | 44 / 63 / 97 | 34 / 47 / 75 |

- One affine span at τ = 0.1 covers about 13 void march steps and about 16 wells
  march steps.
- The void shell is shorter than one span. The tiled spans are clipped by the
  active interval, at a median of 27 world and 2 spans per ray.
- On the wells, the tiled spans run a median of 128 world, with 2 per ray (p90 4).
- The tangent anchor is the worst affine choice. A secant or Chebyshev fit over
  the span cuts the error bound by 2x to 8x, so the spans could be about 1.4x to
  2.8x longer at the same τ. That was not measured.

## 2. Simplex cells per span, per octave

Octave `xm` is the coverage octave's frequency times m, warped like the coverage
octave. The shipped detail octave is x4 and is *unwarped* in both fields, so it
needs no affine span at all.

Counts are for τ = 0.1 tiled spans with both warp phases, given as the median and
then the p90. The rows are simplex cells crossed, then lattice points touched,
which is the exact work.

| Octave | Void cells | Void lattice points | Wells cells | Wells lattice points |
|---|---|---|---|---|
| x1 | 6 / 16 | 10 / 16 | 4 / 8 | 8 / 10 |
| x2 | 8 / 27 | 10 / 26 | 8 / 12 | 10 / 14 |
| x4 | 13 / 52 | 14 / 44 | 12 / 20 | 14 / 20 |
| x8 | 20 / 100 | 20 / 80 | 22 / 38 | 22 / 34 |
| x16 | 40 / 201 | 35 / 154 | 44 / 72 | 38 / 60 |
| x32 | 75 / 396 | 62 / 302 | 86 / 144 | 70 / 112 |
| x64 | 151 / 793 | 118 / 592 | 172 / 287 | 132 / 218 |

- On the same spans, the march takes a median of 2 samples per span on the void
  (p90 10) and 12 on the wells (p90 25).
- A straight line touches 3.44 lattice points per noise unit.

## 3. Cost: exact against the march

- **Measured cost.**
  - One snoise is 530 to 610 ns in C# on this host.
  - One lattice-point integral (hash, gradient, closed-form polynomial) is
    85 ns, or 0.16 snoise.
  - With this probe's naive box-scan enumeration it is 1.24 snoise. That is an
    upper bound; a simplex walk visits only crossed cells.
- **How the table counts.** Exact is lattice points × 0.16 + 1 snoise per span
  for the Jacobian. March is 1 snoise per sample. Both sides carry the two warp
  phases.
- **Tolerance.** τ is 0.1 × *that octave's own* cell, so the affine error is
  comparable across octaves.
- **Columns.** The two error columns are RMS errors of the active-length mean
  of one warped octave. The last error column is what replacing the octave by its
  expectation of 0 costs.

| Octave | Void exact/march cost | Void err exact / march / expectation | Wells exact/march cost | Wells err exact / march / expectation |
|---|---|---|---|---|
| x1 | 0.36 | 1.1e-2 / 1.6e-3 / 0.21 | 0.11 | 1.9e-2 / 9e-5 / 0.29 |
| x2 | 0.46 | 1.0e-2 / 4.7e-3 / 0.13 | 0.15 | 1.5e-2 / 2e-4 / 0.21 |
| x4 | 0.68 | 9.0e-3 / 4.0e-2 / 0.10 | 0.23 | 1.1e-2 / 5e-4 / 0.18 |
| x8 | **1.03** | 5.8e-3 / 8.0e-2 / 0.067 | 0.36 | 7.2e-3 / 1.1e-3 / 0.10 |
| x16 | 1.64 | 4.0e-3 / 9.0e-2 / 0.050 | 0.56 | 6.4e-3 / 9.4e-3 / 0.088 |
| x32 | 2.89 | 2.8e-3 / 0.105 / 0.036 | 0.87 | 4.7e-3 / 3.3e-2 / 0.057 |
| x64 | 5.26 | 2.0e-3 / 0.095 / 0.025 | **1.44** | 3.2e-3 / 5.3e-2 / 0.040 |

- **Where exact stops paying on cost:**
  - At the measured kernel cost: x8 on the void and between x32 and x64 on the
    wells.
  - At the brief's model of one snoise per cell crossed: about x1 to x2 on the
    void and x8 on the wells, from the per-span table in the probe log.
- **Exact against the march on accuracy:**
  - At the shipped step the march is *more* accurate for octaves it oversamples:
    void x1 to x2, wells x1 to x8. The exact path's error floor is the affine warp
    error, about 1e-2 at τ = 0.1 per cell, and it scales with τ.
  - Exact is more accurate at about 0.7x the cost where the march undersamples:
    void x4 to x8, wells x16 to x32.
  - Above about x16 on the void, the undersampled march is worse than simply
    using the expectation (0.095 against 0.025 at x64). That is a band-limiting
    result, which the footprint octave weights already exploit. It is not an
    exact-integration result.

## 4. High-octave variance over a span

- **Span-mean statistics.** Over straight lines of noise length ℓ, the std of
  the snoise span mean, against a point std of 0.358, is:
  - 0.234 at ℓ = 1;
  - 0.170 at ℓ = 2;
  - 0.121 at ℓ = 4;
  - 0.087 at ℓ = 8;
  - 0.061 at ℓ = 16;
  - 0.031 at ℓ = 64.
  
  This is about ℓ^-0.5: the high octaves average out like a random field, not
  like a periodic one.
- **Detail octave replaced by its expectation of 0,** on τ = 0.1 spans, measured
  as |ΔOD| per span:
  - Void: median 0.000 and p90 0.091. Relative to the span's OD (OD > 0.01) the
    median is 2.5% and the p90 is 7.6%. The mean detail displacement is a median
    1.0 world (p90 3.6) against the ramp width S = 50.
  - Wells: median 0.11 and p90 5.7 optical depths. The relative median is 72%.
- **Why the wells are so bad.** Normalised to the clamp, the density moves
  A·fade/B = 2 per unit of noise, and with extinction 0.5 that is 1 optical depth
  per world unit per unit of noise. The mean detail displacement of 1.5 world
  (p90 3.8) is 15% to 38% of the floor blend B = 10. The wells' optical depth is
  set by where the clamp boundary lies, and that is exactly what the expectation
  erases.
- So the expectation is adequate for the void's detail octave and is not
  adequate for the wells.

## 5. The clamp and other nonlinearity

- **Void.** 50.0% of τ = 0.1 spans cross a regime boundary: 0 (d' ≤ Rh), the
  wall ramp, or K (d' ≥ Rc). 45.6% of the active length lies in the ramp, where
  the density is `K max(0, 1 − 1.5 (1 − (d'/Rc)²)^0.40)`. That is not a
  polynomial in n, so exact ∫n does not give ∫ρ there. Outside the ramp the
  density is constant and needs no noise.
- **Wells.** 42.3% of spans cross the clamp. Only 9.8% of the active length is
  unclamped, so most of the length where the noise "matters" is length where the
  noise only decides whether the density is zero.
- **The decisive check: transmittance error.** This is the sup over the ray of
  |T_est − T_true|, with the truth taken on a 0.5- or 1-unit grid.

  | Estimator | Void median / p90 | Wells median / p90 |
  |---|---|---|
  | Shipped march | 0.020 / 0.038 | 0.033 / 0.155 |
  | March at 4x density | 0.006 / 0.009 | 0.030 / 0.049 |
  | Density of the *exact* span-mean n (τ = 0.1 spans) | 0.072 / 0.125 | 0.719 / 0.997 |

  The last row is an upper bound on what any exact-mean method can deliver
  without also modelling the distribution inside the span. It is the
  Heitz-style correction the survey describes. It loses to the march on both
  fields.

## Triangle basis (operator's addition)

The triangle noise is `triNoise3d` in Aetheria `Assets/Shaders/Volumetric.cginc`
(origin/master f1dee184).
- `tri(x) = |frac(x) − 0.5|`.
- It runs two Nimitz-style iterations. Each iterates `p += tri3(bp·2)`, scales
  `bp` by 1.8 and `p` by 1.2, and adds `tri(p.z + tri(p.x + tri(p.y)))/z`.
- It makes 18 tri calls per evaluation.

As shipped, `density()` uses it as follows:
- `pow(triNoise3d(·), exponent)`, with Noise.Exponent −0.25 multiplied by a
  slope `flatness` read from a texture (NOISE_SLOPE is on).
- Two phases, warped by `globalFlow`, which is
  `Tri3D = cross(normalize(tri3(P)), normalize(tri3(1.618P))) × 15`.
- The fine octave at ×8 is evaluated at a `pos` whose y the coarse octave has
  already displaced.
- Amplitude −36.17.

Self's reading, tested:

- **"The warped ray is an exact polyline."** This holds for the warp *inside*
  `triNoise3d`. It does not hold for Aetheria's flow: `normalize` and `cross`
  make the flow piecewise rational. With the normalize dropped, the cross product
  of two piecewise-linear vectors is piecewise quadratic. tri composed with a
  piecewise polynomial stays piecewise polynomial of the same degree, with
  breakpoints at quadratic roots, so that variant is still closed-form.
  Counted along the real flow at the worst shift, the kinks run 15% to 30% above
  the straight-ray counts, but the pieces between them are curved.
- **"The clamp roots are linear."** This holds only if the exponent is 1. With
  −0.25·flatness, each piece is `(a + bt)^e`. One such piece has a closed-form
  integral, but the clamp's root on a sum of two powered phases plus the
  smoothstep fade has none.
- **"Whole periods give the exact mean."** This holds for one triangle wave at
  whole periods, where the mean is exactly 0.25. It fails for the composed form,
  which has no period along a general line; the scales 2, 1.8 and 1.2 and the
  nested warp are incommensurate.
  - Measured on lines, the span-mean std of `triNoise3d` is 0.029 at ℓ = 1,
    0.010 at ℓ = 8 and 0.0042 at ℓ = 64, against a point std of 0.083. That is
    about ℓ^-0.47, the same random-field decay as simplex.
  - Its bias against the global mean of 0.1995 is within ±0.0015, so the
    expectation is unbiased. It is not exact.
- **The cost decides it.** The exact piecewise-linear walk is verified, matching
  a 1e-4 quadrature within 3.4e-8. But the nest has 77.7 linear pieces per noise
  unit of straight line, against 3.44 lattice points for simplex.
  - One piece costs 1.38 point evaluations with a derivative carried.
  - At Aetheria's scale, on the wells rays, the pieces per world unit and the
    exact cost relative to the march are:

    | Octave | Pieces per world unit | Exact cost / march |
    |---|---|---|
    | x1 | 0.20 | 2.8x |
    | x2 | 0.38 | 5.5x |
    | x4 | 0.76 | 11x |
    | x8 (shipped fine) | 1.5 | 22x |
    | x64 | 12 | 176x |

  - On the void rays it is 4.1x at x1 and 29x at x8.
  - At the same time the march's RMS error on the span mean is already small:
    2.4e-3 at x1 and 1.7e-2 at x8.

## Per-basis verdict

| Basis | Exact line integral | Under the shipped warp | Optical depth | Verdict |
|---|---|---|---|---|
| Simplex (CultMath `snoise`) | Closed form, 0.16 snoise per lattice point, 3.44 points per noise unit; validated | Affine spans of 100 to 300 world at τ = 0.1 cell; error floor about 1e-2 | Not delivered: the void's pow profile and the wells' clamp make ρ nonlinear in n; the exact-mean transmittance loses 3.6x and 22x | **Viable kernel, wrong target.** Pays only if the density were affine in n over the span, or as a control variate for a march (survey §4, Kettunen 2021) |
| Triangle (`triNoise3d`) | Closed form per linear piece; validated | Not piecewise linear under Aetheria's normalized-cross flow or the −0.25 power; made linear by exponent 1 and a non-normalized flow | Same nonlinearity, plus 78 pieces per noise unit | **Loses.** 3x to 22x the march even where its preconditions hold |
| Gabor kernels (survey §1.2, not probed) | Closed form for any affine-transformed kernel (complex erf, at most 16 terms) | The affine warp spans above transfer directly, with each span a whitening transform | Same density-nonlinearity limit | **Candidate smooth basis for large structure**, to be measured only if the density is first made linear in the noise over a span |

## What would change the verdict

- **A density that is affine in the noise inside a span.** For example, the
  void's ramp replaced by a linear ramp in d', with clamp crossings bracketed by
  the interval bound. Exact simplex integration would then deliver optical depth
  directly in the 54% of void length that is outside the ramp's clamps. As
  measured, that 54% is constant density and needs no noise at all.
- **Exact integration as a control variate,** not a replacement. The exact mean
  is cheap and unbiased for ∫n, and the march corrects the nonlinear residual.
  That is the Kettunen construction. It was not measured here.
- **Wider skipping.** The operator's first idea, skipping space outside the warp
  range, is already what the interval envelope does, and the probe's active
  intervals are its output.
  - On the void the noise matters over only about 140 world per ray.
  - On the wells, opacity ends the ray within a few world units of entering the
    unclamped region.
