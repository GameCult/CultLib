# Design

CultMath exists to make renderer math readable in every place it needs to live:
Rust hot kernels, C# host code, and HLSL shader code.

## Parity Target: HLSL

HLSL is the semantic authority for every type and intrinsic HLSL defines. The
goal is source compatibility: HLSL-shaped math compiles as C# under
`using static CultMath.math;`. C# forces float literal suffixes (`0.5f`); the
rest of the spelling should survive the move.

That makes these HLSL rules, not options:

- Every swizzle exists. `float2/3/4`, `int2/3/4`, and `bool2/3/4` expose every
  read swizzle of length 2 to 4, repeats included (`v.wwxy`), in both `xyzw` and
  `rgba` naming (never mixed), plus single-component `r/g/b/a`. `double2/3` get
  lengths 2 and 3 only, because there is no `double4`. Components and swizzles are
  writable: `v.xz = f2;`, `c.rgb *= 0.5f;`, and `v[1] = s;` work. Setters exist
  only for non-repeating masks, because HLSL rejects `v.xx = ...`.
- Every HLSL mixed constructor exists, as a struct constructor and as a `math`
  function: `float4(float2, float2)`, `float4(float, float2, float)`,
  `float3(float, float2)`, the copy form `float3(float3)`, and the rest, for the
  float, int, and bool families (double gets constructors only; a `math.double3`
  function would hide the `double3` type name).
- Swizzles and mixed constructors are generated. `tools/generate-swizzles.ps1`
  writes the checked-in `src/CultMath/Swizzles.g.cs` (partial structs plus a
  partial `math`). Edit the generator, rerun it, and commit both; never edit the
  output by hand.
- Comparison operators (`<`, `>`, `<=`, `>=`, `==`, `!=`) are component-wise and
  return `bool2`/`bool3`/`bool4`. Reduce them with `any` and `all`.
  `Equals`/`GetHashCode` stay value-based so vectors still work as dictionary
  keys.
- `select(condition, whenTrue, whenFalse)` uses HLSL's argument order.
- Matrices are row-major: constructors take rows, `m[i]` returns a reference to
  row i (so `m[1][2] = s;` writes one element), `_mRC` is row R column C, and
  products go through `mul` (`mul(m, v)` treats `v` as a column, `mul(v, m)` as a
  row). `*` on matrices would be component-wise in HLSL, so CultMath does not
  define it.
- `m[r][c] = v` writes through locals, array elements, fields of classes, and
  ref locals. Through a readonly field, an `in` parameter, a property
  (auto-properties included), or a static readonly value such as
  `float3x3.identity`, C# takes a defensive copy: the write compiles and is
  silently lost. Copy the matrix to a local, write to it, and assign it back.
- Scalar/vector arithmetic and comparisons have explicit overloads on both
  sides rather than leaning on implicit scalar splat.
- Intrinsics return HLSL's types and edge cases: `sign` returns `int`/`intN`
  (NaN gives 0); `step(y, x)` is `x < y ? 0 : 1` (NaN gives 1), which is how dxc
  lowers it, in C# and in the HLSL mirror; float and double `min` and `max`, and
  float `clamp` and `saturate`, follow the DXIL `FMin`/`FMax`/`Saturate` operations dxc emits for
  them: `min(a, b)` is `a < b ? a : b`, `max(a, b)` is `a >= b ? a : b`, a NaN
  operand returns the other (so `saturate(NaN)` is 0), `clamp(x, a, b)` is
  `min(max(x, a), b)`, and `saturate(x)` is `min(1, max(0, x))`, which maps -0
  to +0; `any`/`all` accept numeric vectors (a component is true when it is
  not 0); `min`, `max`, and `abs` on int vectors return int vectors; int
  `clamp` is `IMin(IMax(x, a), b)`, so inverted bounds return `b`.
- Same-size vector conversions (`intN(floatN)`, `floatN(intN)`, `boolN(floatN)`,
  `boolN(intN)`, `floatN(boolN)`, `intN(boolN)`) exist as constructors and
  `math` functions and follow dxc: float to int is `fptosi`, truncating toward
  zero; numeric to bool is `!= 0` (`fcmp une`, so NaN is true); bool to numeric
  is 0 or 1. DXIL leaves float-to-int undefined for NaN and out-of-range values,
  and so does C#; CultMath does not pin them.
- `normalize(x)` is `x * Rsqrt(Dot(x, x))`, which is how dxc lowers it, and
  DXIL.rst defines `Rsqrt` as `1 / sqrt(src)`. A zero vector therefore
  normalizes to NaN (`0 * inf`), and a NaN component makes every component NaN.
  (The special-value table under `Rsqrt` in DXIL.rst is a copy of the `Round`
  table and contradicts that definition, so CultMath follows the definition.)
  `length` and `distance` are `Sqrt` of the summed squares, so a zero vector
  gives 0. Callers that can meet zero-length vectors guard them before calling
  `normalize`.
- dxc demotes double `frac`, `exp`, `lerp`, and `floor` to float. CultMath's
  double overloads keep double precision instead: `frac(double)` is
  `x - floor(x)` in double. That is a deliberate divergence for CPU simulation
  time, which shaders do not carry.

dxc marks float arithmetic and compares `fast` (no NaNs) unless a value is
`precise`, so drivers may optimize NaN handling away and NaN results on real
GPUs are not guaranteed. CultMath defines the CPU result by dxc's lowering and
the DXIL operation spec (DirectXShaderCompiler `docs/DXIL.rst`).

NaN does not reliably propagate through CultMath, just as it does not on the
GPU. Because `min`, `max`, and `saturate` return the non-NaN operand, some
functions turn NaN or infinity into numbers: `smoothstep(a, a, a)` is 0;
infinite inputs to `smoothstep` and the intercept helpers give numbers; the
bezier, `smoothstep01`, and `smootherstep` functions return finite values for
NaN input. Callers that must detect bad data should check `isnan` before calling these
functions.

Deferred until a consumer needs them: `float4x4`, `transpose`, `determinant`,
and HLSL's one-based `_11`..`_33` element names.

### HLSL Target: Source Transformations

`HlslSourceCompatibilityTests` compiles the real `shaders/CultMath.hlsl` with
Roslyn against CultMath. These transformations are the complete list; if a body
needs anything else, the test fails:

1. Include guards (`#ifndef`/`#define`/`#endif`) are dropped.
2. Functions taking `Texture2D`/`SamplerState` are dropped; GPU resources have
   no CultMath analog.
3. File-scope `static const` becomes `const`.
4. Floating literals gain `f` (`0.5` becomes `0.5f`).
5. Struct fields gain `public`. HLSL struct members have no access-modifier
   concept; a C# struct's fields default to `private`, which would hide them
   from the mirror test's reflection-based field comparison.
6. The file body is wrapped in a class, since C# has no free functions; HLSL
   functions become private instance methods.

HLSL constructs that stay outside the target and must not appear in the mirror:
`const` locals of vector type (C# `const` is primitives only; write a plain
local) and writes through a swizzle component (`v.xy.x = s`, which C# rejects as
modifying a temporary).

The HLSL side is checked too: `tools/get-dxc.ps1` fetches Microsoft's
DirectXShaderCompiler, and
`dxc -T lib_6_3 -HV 2021 shaders/CultMath.hlsl` must compile cleanly.

The mirror test compares bit patterns (any NaN equals any NaN; -0 differs from
0). It proves the text of `CultMath.hlsl` computes the same float32 results as
C# `math` on the CPU, not that a GPU agrees: driver `sin` precision alone makes
`cultmath_hash` and value noise differ bit for bit on hardware. The mirror is
compiled under `using static CultMath.math;`, so its intrinsics are C# `math`
itself: the test proves the composition in the text, not the intrinsic rules,
which only `HlslSemanticsTests` pins.

The comparison also allows exactly one struct return shape: a mirror function
whose HLSL return type is a plain struct of `float`/`floatN`/`int`/`intN`
fields is compared to its C# counterpart field by field, recursing into each
field's own components, down to bit-for-bit scalars, matching each leaf's
declaration path (field names, not just position) and declared type as well
as its value. `CultCellular` (`math.cs`, `cellular`) and `CultPhasor`
(`math.Phacelle.cs`, `phacelle`) are its consumers. A struct return with a mismatched, reordered-by-name, or
retyped field does not get a second comparison path or a shape of its own;
it fails the same walk that already handles a bare vector return.

### GLSL Target: Source Transformations

`shaders/CultMath.glsl` is a GLSL ES 3.00 (WebGL2) library generated from
`shaders/CultMath.hlsl` by `GlslLowering` (in the test project), and
`shaders/CultMath.Phacelle.glsl` is generated beside it from
`shaders/CultMath.Phacelle.hlsl`. Both are derived files: never edit them by
hand. Regenerate them, and the golden fixture
`tests/CultMath.Tests/fixtures/glsl-parity.json`, with:

```powershell
$env:CULTMATH_WRITE_GLSL = "1"; dotnet test packages/cultmath/tests/CultMath.Tests --filter GlslMirrorTests
```

`GlslMirrorTests.CommittedGlslEqualsLowering` fails on any difference between
either committed file and the lowering of the committed HLSL. These
transformations, in order, are the complete list:

1. Each `#include "name"` is replaced in place by the included file, by the
   same resolver the C# mirror uses; GLSL has no `#include`. The exception is
   an include under a file-level licence of its own, listed in one table,
   `GlslLowering.SeparateFiles`: its line is dropped and the file is lowered on
   its own, by the same steps, into its own output. The table has one entry,
   `CultMath.Phacelle.hlsl` to `CultMath.Phacelle.glsl`, because Phacelle is
   MPL-2.0 and MPL-2.0 is file-level: kept separate, it leaves
   `CultMath.glsl` MIT. Nothing else in the lowering is keyed by name.
2. Include guards are dropped, and so are the functions taking
   `Texture2D`/`SamplerState`; the host shader samples its own textures.
3. Type names: `floatN`, `intN`, `uintN` and `boolN` become `vecN`, `ivecN`,
   `uvecN` and `bvecN`.
4. File-scope `static const` becomes `const`.
5. C-style casts `(float)x`, `(int)x` and `(uint)x` become `float(x)`, `int(x)`
   and `uint(x)`. The operand is found by a balanced-bracket scanner:
   `(int)((state >> 28) + 4u)` nests parentheses.
6. Intrinsics are renamed: `lerp` to `mix`, `frac` to `fract`, `rsqrt` to
   `inversesqrt`, `asuint` to `floatBitsToUint`, `asfloat` to
   `uintBitsToFloat`, and `saturate(x)` becomes `clamp(x, 0.0, 1.0)`.
7. Float literals pass through unchanged.
8. Each output is wrapped in a guard named for its file: `CULTMATH_GLSL`,
   `CULTMATH_PHACELLE_GLSL`.
9. The first line names the file as generated, its sources and its licence,
   and says how to regenerate it; `CultMath.glsl`'s also says which
   functions live in the separate file.

GLSL ES 3.00 is stricter than HLSL: no implicit conversions between int, uint
and float, no C-style casts, and no `step(genType, float)` overload. Those gaps
are closed in the HLSL, never in the lowering: the HLSL is written in the subset
both languages share, with edits that are legal HLSL and compute the same bits
(`step(h, float4(0.0, 0.0, 0.0, 0.0))` rather than `step(h, 0.0)`, `+ 4u` where
a uint is added, `(float)((uint)hash.x >> 8)` before a float multiply). The C#
mirror and the FXC and dxc compiles keep those edits honest. A body that needs a
transformation outside the list fails `GlslMirrorTests.NoHlslOnlyTokensSurvive`
rather than gaining a special case.

The library declares no precision. The host shader does, and it must be
`precision highp float; precision highp int;`: the PCG hashes need 32-bit
integers. `tools/compile-glsl.ps1` writes such a wrapper (its own `#version
300 es` line, the precision, the library by string concatenation, and one call
of every public function) and compiles it with glslang, which
`tools/get-glslang.ps1` fetches pinned. It runs twice: over `CultMath.glsl`
alone, and with `-Phacelle` over `CultMath.glsl` followed by
`CultMath.Phacelle.glsl`, the order a consumer that calls `cultmath_phacelle`
concatenates them in. Two negative controls must fail: `-Body` with an HLSL
token (`float x = lerp(0.0, 1.0, 0.5);`), and `-Body` calling
`cultmath_phacelle` without `-Phacelle`, which proves the MIT file does not
carry it. The Unity package ships neither GLSL file.

Parity runs in two legs, each with one owner: HLSL against C# by the mirror
test, bit for bit; GLSL against C# by the golden fixture, which records 256
seeded cases per function family as float32 bit patterns, evaluated by the
consumer on WebGL2 and read back. The families `pcg3d` and `pcg4d` are marked
exact; the float families are ulp-bounded, the bound measured on the device.

`iv_frustum_ball` is ulp-bounded, not exact: it calls `sqrt`, which GLSL ES
3.00 does not require to be correctly rounded, and WebGL2 compilers may
reassociate. On a GTX 1070 under ANGLE's D3D11 backend its radius differs from
C# by up to 2 ulp in 43 of 256 cases, sometimes smaller. What the march needs
is enclosure, and what culling needs is a ball no wider than its rounding
widening, so the family's fixture entry carries a `check` the consumer applies
to every case: from the case's arguments `(mx, my, z0, z1, fp, warp)`, compute
in double `zm = (z0 + z1) / 2`, the centre `c = (mx zm, my zm, zm)`, its
`|c|_1 = |mx zm| + |my zm| + |zm|`, the radius
`r = (z1 - z0) / 2 sqrt(mx^2 + my^2 + 1) + z1 fp + warp` and
`d = |GPU centre - c|`, and require
`r + d <= GPU radius <= r + d + 2^-19 (r + |c|_1)`. The lower side is
enclosure; the upper side is the bound `NoiseBoundTests` pins in C#, so a
lowering that inflates the ball fails too. Only those two bounds fail the
check; the largest ulp distance is reported as for any ulp-bounded family.
`GoldenFixtureMatchesCSharp` applies the same check to C#'s own results.

`GoldenFixtureMatchesCSharp` evaluates C# on the committed fixture's own
arguments. Arguments are drawn only when the fixture is regenerated
(`CULTMATH_WRITE_GLSL=1`), because the draw itself calls `MathF.Pow`, whose
bits differ by OS. `iv_exp` and `phacelle` take their C# results from the
platform's `exp`, `sin` and `cos`, which also differ by OS, so their entries
name the platform that generated them and the test compares them case by case
against the running platform, printing the largest distance. `iv_exp`
("ulp-bounded, platform exp") must agree within one ulp. `phacelle`
("ulp-bounded, platform exp, sin, cos; across platforms |diff| <= 2^-20
max(|v|, 1)") must agree within `2^-20 max(|v|, 1)` per component, `v` being
the fixture's value: its sums of `exp`, `sin` and `cos` cancel near zero, where
ulps mean nothing, and Windows differs from the Linux fixture by up to
`3.6e-7 max(|v|, 1)`. Every other family is compared as text. `phacelle` is in
`CultMath.Phacelle.glsl`, so the evaluator concatenates
`CultMath.Phacelle.glsl` after `CultMath.glsl` for that family, with
normalization 0.5. The site does not vendor the MPL file and skips the family.
Its evaluator is the cultmath-tapes follow-up `glsl-browser-parity-run`, owned
by the next CultMath parity cut: a repeatable CultLib WebGL2 run of every
fixture family, `phacelle` included. Until it lands, the one GLSL evaluation of
`phacelle` is a single probe (GTX 1070, ANGLE D3D11: within `3.1e-6 max(|v|, 1)`
of C#). Asura's `gpu-snoise-parity` is a different check, an FXC and Unity
readback of `snoise`, and evaluates no GLSL.

## Invariant 8: Value-and-Gradient Primitives

A primitive tagged invariant 8 returns its value and analytic gradient
together, laid out as `float4(gradient.xyz, value.w)`, with no value-only twin
(a value-only form would be a second path with no consumer). `smin_grad` and
`cellular` are the first primitives in this family:

- `smin_grad(float4 a, float4 b, float k)` is Inigo Quilez's quadratic-polynomial
  smooth minimum ("smooth minimum",
  <https://iquilezles.org/articles/smin/>), carried to value-and-gradient
  form. Its blend factor `h` is affine in `b.w - a.w`, so differentiating the
  value with respect to position, the terms carrying `dh/dp` cancel exactly;
  the surviving gradient is `lerp(∇b, ∇a, h)`, the same `h` the value uses.
  This is the analytic gradient, not an approximation, and it is continuous
  across the `|a.w - b.w| = k` seam where `h` saturates to 0 or 1.
- `cellular(float3 p)` is Worley's cellular texture basis function ("A
  Cellular Texture Basis Function", SIGGRAPH 1996), searched over the
  jittered 5×5×5 neighbourhood of feature points selected by `pcg3d` (never
  the sin-based `hash`), with cells whose position-only lower bound already
  exceeds the running F2 skipped before their hash is even computed. It
  returns `CultCellular { nearest, edge, id }`: `nearest` is `(∇F1, F1)`,
  `edge` is `(∇F2 - ∇F1, F2 - F1)`, and `id` is the nearest cell's `pcg4d`
  hash (over the cell coordinate alone, never the `pcg3d` hash that produced
  its jitter) mapped to `[0, 1)`. `∇F1` and `∇F2` are each undefined exactly
  at their own feature point (`F1 = 0` or `F2 = 0`); `cellular` follows the
  repo's existing degenerate-normal convention
  (`GameCult.Geometry.CultGeometryIsoSurface.EmitOrientedTriangle`, which
  guards a zero-length normal and returns the zero vector instead of the NaN
  a bare `normalize` gives there) and returns the zero vector rather than NaN
  for either. `F1 = 0` is ordinary (`p` sits on a feature point); `F2 = 0`
  needs two distinct cells' `pcg3d`-jittered feature points to land on the
  same float32 value in every component, which is unreachable at ordinary
  magnitudes but confirmed reachable (see "Precision domain" below). The
  identity of the nearest and second-nearest feature point changes
  discontinuously across the `F1 = F2` set (a genuine kink, not a numerical
  artifact), so `∇F1` and `∇F2` are only continuous away from that set.

  The 5×5×5 radius is exact, not merely safer than the classic 3×3×3 radius:
  for a query at position `u` inside its own cell (`u` in `[0, 1)^3` per
  component) and a candidate feature at integer cell offset `d` with jitter
  `j` in `[0, 1)^3`, the per-axis displacement `(u_i - j_i) - d_i` is always
  strictly greater in magnitude than `max(0, |d_i| - 1)`, because
  `u_i - j_i` is always strictly inside `(-1, 1)`. So any cell with `|d_i| >=
  3` on some axis is strictly farther than 2 from the query. Two real,
  always-present candidates bound `F1` and `F2`: the query's own cell, whose
  distance is always strictly less than `sqrt(3)` (every axis strictly under
  1); and the near-side neighbour on whichever single axis has `u_i` closest
  to a cell boundary. Let `m = min(u_i, 1 - u_i)` on that chosen axis, so
  `m` is in `[0, 1/2]` by construction. Stepping one cell towards that
  nearer boundary puts the query within `1 + m` of the far face on the
  offset axis and within `1 - m` of the near face on each of the other two
  axes, so that neighbour's squared distance is at most
  `(1 + m)^2 + 2*(1 - m)^2 = 3 - 2m + 3m^2 <= 3` for every `m` in `[0, 1/2]`
  (3 at `m = 0`, dipping to a minimum of `8/3 ~= 2.667` at `m = 1/3`, then
  rising back to 2.75 at `m = 1/2`), and strictly below 3 because
  the jitter never reaches exactly 0 or 1. `F2` is at most the larger of
  these two real candidates (any two real candidates upper-bound the
  2nd-smallest value over the full infinite candidate set), so `F1 <
  sqrt(3)` and `F2 < sqrt(3)` always, comfortably under the radius-2
  exclusion distance of 2, with margin `2 - sqrt(3) ~= 0.268` (Soul's
  adversarial check, cut 2a-i: the worst realizable in-box `F2` is exactly
  `sqrt(3)`, reached only in the unattainable limit at a cell corner). 3×3×3's
  matching argument only reaches radius 1, whose excluded cells start at
  distance 1, short of the `sqrt(3)` ceiling — the gap its measured failures
  (9 of 400,000 points in `[-50, 50]^3`, worst error 0.088) live in.

  An earlier version of this proof picked the near-side neighbour on a fixed
  axis instead of whichever axis sits closest to its own boundary, and that
  version is wrong (Soul, cut 2a-i): `u = (0.5, 0, 0)` with the x-neighbour's
  jittered feature at `(-1, 1 - 2^-24, 1 - 2^-24)` gives a distance of about
  2.06, outside the radius-2 exclusion the proof needs — `u.x = 0.5` is the
  point on the cell farthest from any boundary, so `x` was the wrong axis to
  fix there. Choosing the axis by proximity to its own boundary is what keeps
  `m <= 1/2` and the bound at `sqrt(3)`.

  `CellularAndSminGradTests` pins both the prune's bit-identical equivalence
  to an unpruned 5×5×5 reference and the search's exact agreement with an
  independent 7×7×7 brute-force oracle.

  Precision domain: `cellular`'s exactness above is a statement about the
  search radius, not about float32. Past `|p|` of roughly `2^23`, adjacent
  integer cells stop being distinguishable from their neighbours at float32
  precision in some directions, and distinct cells' jittered features can
  round onto the same value, making `F1 = F2` ties (and, per-component,
  `F2 = 0`) increasingly common from about `1e6` and confirmed at `2^24`
  (measured: 4096 of 4096 sampled integer points at `2^24` hit this before
  the guard above). `cellular` stays finite there; it does not stay accurate
  in the sense of matching the idealized real-valued Worley field.

  The lower-bound prune's bit-identity to the unpruned 5×5×5 search also has
  a domain: it holds for `|p| < 2^25`. Above that, `cell + d` (an integer
  cell coordinate plus a small integer offset) starts to round in float32,
  so a pruned cell's own recomputed offset can land on a different neighbour
  than the unpruned loop visits at that same nominal `(dx, dy, dz)`, and the
  two search paths diverge (Soul, cut 2a-i: measured 180 of 1,100,000 points
  differ at `|p| ~= 3.4e7`). This is a float32-precision limit on the prune's
  equivalence proof, not a bug in the prune itself: below `2^25` the two
  paths are proven bit-identical (`PrunedSearchIsBitIdenticalToTheUnprunedFiveCubedSearch`,
  which samples `[-50, 50]^3`, is comfortably inside that domain).

- `snoise(float3)` follows webgl-noise `src/noise3D.glsl` as of
  stegu/webgl-noise `22434e04d7`: kernel radius squared `0.5`, output scale
  `105`, permutation `mod289((34x+10)x)`. Upstream's `21d9fe23d7` (2020-10-14,
  "an age-old bug that caused slight discontinuities along simplex
  boundaries") moved the radius from `0.6` to `0.5`; at `0.6` a lattice vertex
  outside the four summed corners can lie inside the kernel, leaving a value
  jump at the cell boundary (3.1e-3 over the committed probe's 400 lines). `ff3b5d34ea` set the scale
  to `105` and `a3e6d57095` (2021-06-30) the permutation; the 2D `snoise` shares
  that permutation helper, so its values changed too, while its own kernel
  (`0.5`) and scale (`130`) already matched upstream.
  `NoiseGradTests.SnoiseAndItsGradientAreContinuousAcrossSimplexCellBoundaries`
  pins the continuity. Ties in the offset `x0` resolve in Gustavson's
  `simplexnoise1234` order (x over y, y over z, x over z), a total order, so the
  four corners are always a simplex. Upstream's `step` compares are cyclic: where
  the three components are equal, which includes the origin and every
  `(t, t, t)`, the corners repeat the origin vertex and the value jumps by about
  0.77 near the origin and up to 1.56 at magnitudes of 64 and above. `NoiseGradTests.SnoiseIsContinuousOnTheSimplexDiagonal` pins the order.
- `snoise_grad(float3 p)` carries `snoise(float3)` to value-and-gradient form,
  following the differentiation in webgl-noise `src/noise3Dgrad.glsl` (Ashima
  Arts / Ian McEwan, MIT), which uses the same radius, permutation and scale as
  `snoise(float3)`: per corner, `m0 = max(0.5 - dot(x,x), 0)`, so
  `d(m0^4 * dot(p,x))/dx = -8*m0^3*dot(p,x)*x + m0^4*p`, summed over the four
  corners and scaled by the same constant as the value, so `.w` is exactly
  `snoise(float3)`. `fbm_grad` and
  `ridged_grad` are octave sums of `snoise_grad`: each octave samples at
  `p * frequency`, so by the chain rule its gradient scales by that same
  frequency, and both amplitude (`gain`) and frequency (`lacunarity`) compound
  per octave rather than applying once to the whole sum. `ridged_grad` folds
  each octave about zero (`Σ aᵢ(1 − |nᵢ|)`, gradient `−Σ aᵢfᵢ sign(nᵢ)∇nᵢ`,
  Musgrave's ridged multifractal, *Texturing and Modeling* ch. 16); the crease
  at `nᵢ = 0` is deliberate (the operator's dune term, 2026-09-25), not a bug.
  Both clamp `octaves` to `[0, 16]`: `HlslSourceCompatibilityTests`'s bit-parity
  oracle drives every mirrored `int` parameter across the full int32 range,
  which is the right domain for `pcg3d`/`pcg4d`'s O(1) hash inputs but would
  turn an unclamped octave count into a multi-billion-iteration loop for these
  two mirrors.
- `phacelle(float3 p, float3 side, float offset, float normalization)` is Rune
  Skovbo Johansen's Phacelle noise ("phase + cell": a stripe pattern built by
  blending one cosine/sine wave per jittered cell), generalised to 3D cells. It
  returns `CultPhasor { cos, sin }`, each `(∇, value)`. `side` is the stripe
  wave vector, whose length is 2π times the stripe frequency; in 3D the
  perpendicular to the flow is not unique, so the caller chooses it (on a unit
  sphere, `cross(p̂, flow)`). `offset` is the phase in cycles. It visits 4×4×4
  cells with `pcg3d`-hashed jitter in `[-0.5, 0.5)`, weights each wave by
  `max(0, exp(-2d²) - 0.01111)`, and normalizes the blended phasor as upstream
  does (length 1 where the raw length is at least `1 - normalization`, scaled
  by `1 / (1 - normalization)` below). The gradient is exact, weight
  derivatives and normalization included, rather than upstream's
  `∇cos ≈ -sin·side`, which ignores the weight gradient. It is discontinuous
  where the raw length crosses the normalization threshold and where a cell's
  weight leaves its support (d² = 2.24995, distance 1.49998); the value is
  continuous at both. At the support edge `∇w` jumps from `-4·0.01111·v` to 0,
  a per-cell jump of length `0.0444·|v|` (0.0667 at `|v| = 1.5`); measured over
  random points, the output gradient jumps by a median 1.5% of its length and
  at most about 27%. `normalization` is meant to lie in `[0, 1]`: 1 or more
  saturates (every output is stretched to length 1), a negative value scales
  every output down by `1 / (1 - normalization)`, and the normalized gradient
  scales as `1 / |I|` for the blended phasor `I`, so it grows where the blend
  nearly cancels. The total weight `W` is positive for every `pcg3d` input (the
  smallest found by hill-climbing is 0.056); `W = 0`, and a NaN output, needs
  adversarial jitter no hash yields.
  Cells whose per-axis lower bound `Σ max(0, |local - g| - 0.5)²` is at least
  2.25 lie outside the support and have weight exactly 0, so they are skipped,
  which changes no output bit. Cells whose bound sits just under 2.25 can still
  carry a nonzero weight, so the cut cannot be lowered; `PhacelleTests` pins
  the cut with points next to such cells, against an internal overload with the
  prune disabled, and the HLSL mirror test pins it on the shader side. The prune
  keeps about 45 of the 64 cells on average (a saving of about 30%); about 14
  have a nonzero weight. The port sits in its own MPL-2.0 files
  (`math.Phacelle.cs`, `CultPhasor.cs`, `CultMath.Phacelle.hlsl`); see
  `THIRD-PARTY-NOTICES.md`. No 2D variant exists; nothing consumes one.

Integer hashing uses the PCG hashes from Jarzynski and Olano, "Hash Functions
for GPU Rendering" (JCGT 9(3), 2020): `pcg(uint)` is O'Neill's RXS-M-XS 32/32
permutation over one LCG step, and `pcg3d`/`pcg4d` are the paper's (3 → 3) and
(4 → 4) hashes. They use only uint multiply, add, xor, and shifts. dxc wraps
uint multiply and add modulo 2^32 exactly as C# unchecked arithmetic does, so
they are integer-exact in HLSL and on the CPU, GPU included, unlike the
`sin`-based `hash`. CultMath has no uint vectors, so `pcg3d(int3)` and
`pcg4d(int4)` carry uint bit patterns in int components. The `float2`/`float3`/`float4`
overloads hash the IEEE-754 bits (`asuint`), so -0 and 0 hash differently; they
exist for callers that already hold a hash key as float bits, and `pcg3d(float2)`
holds z at 0, which is the paper's route for (2 → N) inputs. Seeds and other
scalar uses take one output component.

`cellular`'s jitter and id always hash the integer cell coordinate directly
(`pcg3d(int3(neighbor))`, `pcg4d(int4(int3(cellId), 0))`), never a cell
coordinate's float32 bit pattern. Hashing the bit pattern of an
integer-valued float is not the same permutation as hashing the integer: two
adjacent cells' bit patterns share more structure (both have the same
exponent and a mantissa that differs by one increment) than two adjacent
integers do going into `pcg3d`'s own mixing, and that residual structure
survived into the jitter. Hashing the integer cell coordinate directly is
exact and runtime-independent, and it is the HLSL mirror's own hash, so C#
and shader agree bit-for-bit. What pins this is the oracle and mirror tests,
not a uniformity measurement: reverting to hashing the float bits (or the
sin-based hash) is caught by `ProductionSearchMatchesTheBruteForceOracleExactly`,
`EveryMirrorFunctionMatchesCSharpMath`, `F1NeverExceedsF2`, and the other
tests that key off the integer-hash fixtures (cut 2a-i). `CellularAndSminGradTests`
(`JitterAndIdPassAChiSquareUniformityTest`) checks the uniformity of the hash
as `cellular` uses it: the jitter marginals and the id pass a chi-square test
at a stated significance against `pcg3d(int3(...))`.

Where HLSL is silent, CultMath keeps its own decisions and does not defer to
Unity.Mathematics: `hash` returns float, and `Random` is CultMath's own xorshift32. Engine-shaped
helpers that HLSL lacks (`float2x2.Rotate`, `float3x3.Euler`, `quaternion`,
`snoise`) enter only when a consumer needs them, with their semantics stated in
code.

Known C# friction: under `using static CultMath.math;` the constructor
functions (`float3(...)`, `float3x3(...)`) hide the type names in member access,
so static members need a qualified type (`CultMath.float3x3.Euler(...)`).

## Intervals

`math.Interval.cs` and `shaders/CultMath.Interval.hlsl` (included from
`CultMath.hlsl` after the Phacelle include) carry interval arithmetic for
culling empty space with a proof. Their invariant, **intervals-enclose**: every
interval function returns a `[lo, hi]` that contains `f(x)` for every `x` in
its input region, where `f(x)` is the same function evaluated pointwise in
float32. A consumer that skips a region because `hi < cutoff` has skipped
nothing that was there.

Representation: an interval is `float2(lo, hi)` with `lo <= hi`, both finite.
There is no empty interval; a caller that needs "provably empty" tests
`hi < cutoff`. `iv_point(x)` is `float2(x, x)`.

Ops: `iv_point`, `iv_add`, `iv_sub`, `iv_neg`, `iv_mul` (the least and greatest
of the four corner products), `iv_scale(iv, float)`, `iv_abs` and `iv_sqr`
(an interval straddling zero starts at 0), `iv_min`, `iv_max`, `iv_sqrt`
(`lo` clamped at 0), `iv_exp`, `iv_clamp(iv, float, float)`, `iv_saturate`,
`iv_smoothstep(float, float, iv)` (its ordered endpoints) and
`iv_lerp(iv, iv, float)`. No others until a consumer names one. Each has an
HLSL mirror named `cultmath_iv_*`, compared bit for bit by
`HlslSourceCompatibilityTests`.

The ulp-widening rule. Each bound is computed with the pointwise function's
own float32 operations. IEEE round-to-nearest is monotone, so an op that is
monotone on its region encloses its float32 evaluation exactly, with no
widening; that covers every op above except two, which widen by exactly one
ulp, in the implementation and never in the test:

- `iv_exp`: `exp` is not required to be correctly rounded, so a libm need not
  be monotone. .NET's `MathF.Exp` on Linux steps down 0 times over every float
  in [-87, 88.7] (`IntervalTests.MeasureMonotonicity`), so on that CPU the
  widening is a margin; it is there for other libms and GPUs.
- `iv_smoothstep`: `t * t * (3 - 2t)` in float32 steps down 337,095 times over
  the floats in [0, 1], each time by at most 1 ulp (`MeasureMonotonicity`).
  Since `t = saturate((x - minimum) / (maximum - minimum))` is itself monotone
  in `x`, that covers every `smoothstep`.

`iv_lerp` is the natural interval extension of `x + (y - x) * amount`, which is
how `lerp` evaluates; every step is monotone, so it encloses with no widening
for any `amount`, and it is tight only when `a` is a point, because `x` appears
twice.

`IntervalTests` runs every op over 10,000 seeded intervals (centres in
[-20, 20], widths log-uniform in [1e-4, 10], every eighth a point) and 16
points inside each, endpoints and zero included, with no tolerance; and for
every op it raises `lo` and lowers `hi` by one ulp and requires the harness to
catch each. A new op is one entry in that table.

Noise bounds. `iv_snoise_ball(c, r)` is `[n - L r, n + L r]` intersected with
`[-1, 1]`, where `n = snoise(c)` and `L = SNOISE_LIPSCHITZ`: one `snoise`, value
only. No interval function reads `snoise_grad`, and none has a value-only or
gradient twin. `iv_fbm_ball(c, r, octaves, lacunarity, gain)` is the octave sum
of `iv_snoise_ball(c * f_i, r * |f_i|)` scaled by `a_i`, compounding frequency
and amplitude exactly as `fbm_grad` does, octaves clamped to [0, 16]. A domain
warp that moves a point by at most `D` is enclosed by the radius `r + D`
(`NoiseBoundTests.WarpedPointsStayEnclosed`); this is what the gamecult.org
ground's march rests on.

`iv_frustum_ball(m_c, z0, z1, footprintPerDepth, warp)` is the ball one probe
serves a whole screen tile with. In a pinhole camera frame looking down `+z`,
where the ray of slope `m` is the points `(m z, z)`, it encloses every point of
every ray whose slope lies within `footprintPerDepth` of `m_c` (`N / (sqrt(2) f)`
for an `N x N` tile at focal length `f` pixels: the full pixel footprint, so
sub-pixel jitter is covered), over depths `[z0, z1]`, each point moved by a warp
of length at most `warp`. The centre is `(m_c z_m, z_m)`, `z_m` the mid-depth;
the radius is `((z1 - z0) / 2) |(m_c, 1)| + z1 footprintPerDepth + warp`. A point
of slope `m_c + e` at depth `z` is the centre plus `(z - z_m)(m_c, 1)` plus
`z (e, 0)`. The returned ball is float32, and on a degenerate (`z0 == z1`),
thin or far segment the rounding of the centre alone exceeds the gap the
triangle inequality leaves, so the radius is widened by
`(|c|_1 + radius) 2^-20`. With `u = 2^-24`, each centre component carries at most
two roundings (`z_m`, then the product), so the float centre is within
`2u |c|_1` of the exact one; the radius carries at most seven (`z1 - z0`, the
axis length at `3u`, the product, `z1 footprintPerDepth`, the sum, `+ warp`), so
the exact radius is at most `(1 + 7u)` times the float one; the widening rounds
once more. `2^-20 = 16u` covers `8u (|c|_1 + radius)` twice over, and fma
contraction only removes roundings. The axis count assumes a correctly rounded
`sqrt`, which IEEE gives C#. GLSL ES 3.00 does not: it inherits `sqrt`'s
precision from `inversesqrt`, and D3D-backed compilers may reassociate. So the
bound is proven for C# and HLSL on IEEE hardware, and on GLSL it is measured:
on a GTX 1070 under ANGLE's D3D11 backend the fixture's 256 cases keep a
minimum relative slack of `1.98e-6` (about `33u`), the same as C#'s. The
fixture's enclosure check is what measures it on each device. The caller rotates and translates the centre into world space and scales
centre and radius by its noise frequency; the rounding of that transform is the
caller's. `NoiseBoundTests.TileBallEnclosesEveryRaySegment` checks 2,000 seeded
tiles x 64 points of the r2 domain and 2,000 tiles x 8 corner points of each of
seven extreme families (`z0 == z1`, `z1 - z0 = 1e-6 z1`, `|m_c|` to 1000, `z` to
`1e7`, `z0 = 0` with `z1` to `1e-3`, `f` down to 1, warp to `1e6`), corner flows
full-length and outward, distances in double, with no tolerance. It pins the ball
to the derivation both ways (`r <= ball.w <= r + 2^-19 (r + |c|_1)`), so a ball
that forgets the widening fails and one grown past it fails too. A moving camera needs nothing
more: each frame's probes use that frame's camera, and no ball, mask or probe
result is carried from one frame to the next.

The noise ball alone proves little: at `L` near 10 it bounds `snoise` to a width
of `2 L r`, which covers all of `[-1, 1]` once `L r` reaches 1. Empty space
becomes provable through an analytic envelope. The consumer's density is a
pre-distortion SDF (a height fog, a well map, a carved sphere) that noise
displaces only near its surface, and the consumer composes the bound from `iv_*`
ops. The envelope is taken over the slice's exact box (`iv_mul` of the depth
range by each slope range, then the SDF's own monotone pieces), together with its
fade. The noise is `iv_snoise_ball` over `iv_frustum_ball`, scaled by the noise
frequency. The warp moves only the noise argument, so it enlarges the ball and
the envelope's box takes none of it. Where the fade's upper end is 0 the noise
term is identically 0 and no `snoise` is evaluated. Where the envelope already
proves the slice empty with the noise at its full range, the ball is not
evaluated either. A probe is worth attempting only when the best result any
centre value could give would prove the slice, and a ball with `L r >= 2` is
`[-1, 1]` whatever `snoise(c)` is, so it costs nothing. CultMath owns none of the
envelope: the interval files have no envelope function, and each consumer composes
its own. `NoiseBoundTests.EnvelopeBoundEnclosesDensity` checks that composition,
the one the site and Aetheria use, over 2,000 seeded slices x 64 warped points
with no tolerance.

`L` has provenance, not a proof. `NoiseBoundTests.MeasureLipschitz` (slow,
explicit) takes the largest `|snoise_grad|` over 1e6 seeded points in
[-256, 256]^3 (7.1933103), refines the 1e4 largest by gradient ascent
(7.2400064), and multiplies by 1.10: `L = 7.9640074`. It also reaches
`|snoise| = 0.9718335` by the same ascent on the value, which is what lets the
bound intersect with `[-1, 1]`. `LipschitzConstantPinsSampledGradients`
checks 1e5 more seeded points against `L` and climbs again from the start
`MeasureLipschitz` prints as its witness, failing if `L` is not 1.10 times that
maximum to within half a percent, so a 1% move of `L` fails. The enclosure tests
over 2,000 balls x 64 points are the defence. A change to the `snoise` kernel
must re-run `MeasureLipschitz` and re-pin `L` and the witness.

What the ball bounds guarantee: enclosure of `snoise` and `fbm_grad(...).w` over
the ball, in float32. What they do not: anything about tapes, pruning, choice
tracking or affine forms; those are later steps of
`docs/cultmath-tape-target.md` (CultLib root). How loose they are is a number:
`NoiseBoundTests.TightnessReport` prints the mean interval width over the
sampled range: 5.08 for `iv_snoise_ball` at `r = 0.05`, 1.96 at 0.25 and 1.28
at 1.0; 6.64 and 2.82 for 4-octave `iv_fbm_ball` at 0.05 and 0.25.

The saving is measured where there is empty space to find.
`NoiseBoundTests.IntervalSkipHalvesEvaluations` draws screen tiles, each with
its own camera, and runs one pre-pass per tile over the dense grid's cell ranges:
it starts with the whole grid, skips and doubles a range it proves empty, halves
one it cannot, and runs to the grid's end. It then draws the tile's `N^2` ray
slopes, one per pixel, jittered inside it. Each ray is marched three times with
the same integrator and the same early-out at transmittance 0.02: over every
cell (dense), over the tile's unproven cells (the tile march), and over the
oracle mask, the cells whose mid-depth density is nonzero for at least one of
the tile's rays. The tile march and the oracle march integrate the dense march's
nonzero cells, so their transmittance agrees with it exactly in every
configuration. The oracle is a ceiling the pre-pass cannot move: dense cost over
oracle cost. Against it the test prints the efficiency (oracle cost over the
tile march's cost with its probes) and the cull fraction (the oracle-empty cells
the pre-pass proves empty). The probe overhead is the tile march's cost over that
cost plus the probes'. Cost is counted in `snoise` evaluations, with an envelope
evaluation or probe weighted 0.2. That weight is an estimate, and both counts are
printed. The flow is not counted. At `N = 8`, in Aetheria's units on its
256-cell quadratic grid out to the Main Camera's far plane, 2048:

- (a) Height fog, camera above the safety band, rays level and up: the whole
  grid is one envelope probe per tile, against 256 envelope evaluations per ray,
  16384x. Every cell is oracle-empty and the pre-pass proves them all.
- (b) Inside the fog: 1.15x, and 0.99x with the warp. Most cells are fog, and
  the pre-pass probes each one.
- (c) The zone bowl and four wells, the camera gazing across. With no warp:
  2.74x, oracle ceiling 7.26x, efficiency 0.378, cull fraction 0.952, probe
  overhead 0.961. With the warp `D = 60`: 1.39x, oracle ceiling 7.17x,
  efficiency 0.194, cull fraction 0.842, probe overhead 0.993. With the warp the
  pre-pass cuts envelope evaluations from 177.46 to 33.49 per ray, but the
  oracle march costs 0.194 of the tile march, so about four fifths of the tile
  march's cost goes to cells no ray samples nonzero: the warp bound swamps the
  noise ball, so the ball cannot prove those cells empty. That gap belongs to
  the tape target's affine forms, not to a better pre-pass.
- (d) r1's uniform slab, which has no envelope: 1.23x, and 1.48x with no warp.
- (e) The void at the shipped point (ruling `operator-shipped-void`): `Rh = 198`,
  `S = 50`, `Rc = Rh + S = 248`, `e = -ln 1.5 / ln(1 - (Rh / Rc)^2)`, the
  shipped camera, 200 tiles. The fixed-step reference takes 36.34 steps per
  pixel. The same cells masked by the pre-pass take 20.13, with identical
  transmittance. The footprint-aware march takes 13.54: 2.68x fewer. `snoise`
  per pixel goes from 32.98 to 26.69, and combined cost from 40.25 to 29.52 with
  the probes, 1.36x. Every one of the 200 tiles proves a body. Before the body
  both marches are measured against a converged one (midpoint quadrature at
  `h = 0.5` over every cell, trusting no pre-pass): the relative optical-depth
  error at the body start is at most 0.0608 for the fixed-step march and 0.08639
  for the footprint march, 1.42x. Over the grid of hollow radius, camera offset
  and ramp width the steps ratio runs from 1.57x (`Rh = 100`, low, `S = 150`) to
  27.45x (`Rh = 1000`, centred, `S = 10`), the cost ratio from 1.26x to 3.89x,
  and the depth-error ratio from 0.85 to 4.72 (`Rh = 200`, centred, `S = 150`,
  where the errors are 0.01504 and 0.003188). LOD-far takes 14.19x fewer steps;
  its depth errors, 0.2849 and 0.205, are not comparable, because its
  fixed-step reference keeps the octaves the footprint march truncates.

The analytic body (ruling `operator-analytic-wall-body`). When a single dense
cell's own bound is a positive point, the pre-pass spends one more probe over
the rest of the grid. If that bound is a positive point too, every point of
every ray of the tile from there on has exactly that density, so the footprint
march takes one closed-form step, `exp(-BodyDepth)`, from the body start to the
grid's end and stops. `BodyDepth` is density x extinction x length x `|dir|`.
`NoiseBoundTests.AnalyticBodyMatchesFineMarch` pins it: the bound, the mask and
the pointwise densities agree exactly at the body, and `BodyDepth` equals the
summed fixed-step depth of the body cells within 1e-4 relative. The comparison
is in the depth domain, so LOD-far's deep bodies do not underflow.

The footprint-truncated void weights its octaves by `w_i(z)`, decreasing in `z`.
Its bound takes each weight over `[w_i(z1), 1]` rather than
`[w_i(z1), w_i(z0)]`, because one bound serves both the full field (`w = 1`) and
the truncated one. `[w_i(z1), 1]` contains both, and `iv_mul` is monotone in its
interval argument, so the composed interval encloses both fields.
`NoiseBoundTests.WeightBoundEnclosesOneSignedNoise` checks it on LOD-far over
2,000 segments: each weight lies in its bound, and `w(z) n` lies in
`iv_mul(bound, n)` for a one-signed `n`. A lower end of `w(z0)` fails it.

The contracts, at `N = 8`, the lower of warp 0 and `D`:

- (a) at least 2x combined cost;
- (c) (ruling `wells-overhead-plus-floor`): probe overhead, the tile march over
  the tile march plus its probes, at least 0.90; and, hard, the pre-pass proves
  at least half of the oracle-empty cells empty, so a pre-pass that skips
  nothing fails. The oracle ceiling, the efficiency against it and the cull
  fraction are printed, not asserted;
- (e) at the shipped void: the masked fixed-step march agrees with the reference
  exactly; the footprint march's optical depth at the body start is off the
  converged march's by at most twice the fixed-step march's error; and it takes
  at most half the reference's steps per pixel.

This run meets every contract: (a) 16384x, (c) probe overhead 0.961 and cull
fraction 0.842, (e) 2.68x fewer steps and a depth error 1.42x the fixed-step
march's. The fields, the grids and the 0.2 weight are not tuned toward the
contracts.

Consumers: the gamecult.org ground shader (through the GLSL lowering) and
Aetheria's nebula raymarch at its CultMath pin bump.

## Rules

- Keep type names HLSL-shaped: `float2`, `float3`, `float4`, `float3x3`, and `math`.
- Keep Rust as the owner of native hot-path kernels and parity fixtures.
- Keep C#, shader, and scripting surfaces as runtime wrappers, not competing
  math authorities.
- Keep `shaders/CultMath.hlsl` as the canonical shader mirror for shared
  CultMath functions HLSL does not already provide (HLSL intrinsics such as
  `mul`, `any`, and `log` need no mirror). Project-local shader helpers should
  move here once more than one organ needs them or once CPU/GPU parity matters.
- Implement public shader semantics directly; do not copy Unity.Mathematics.
- Prefer plain mutable value types with public component fields, as HLSL does.
- Prefer component-wise overloads over clever generic machinery until the API
  earns that complexity.
- Keep the surface boring and exact. Fill what a consumer uses, guided by HLSL
  semantics; noise primitives, matrices, quaternions, and packing helpers need an
  owner before they enter.
- Keep the core assembly engine-free. Unity conversions live in the Unity
  package's `CultMath.UnityBridge` assembly.

## Non-Goals

- Compiling C# to shaders.
- Generating HLSL from Rust or Rust from HLSL.
- Replacing `System.Numerics` for general .NET work.
- Recreating every Unity.Mathematics type because a spreadsheet somewhere says
  “coverage.” That way lies decorative obesity.
