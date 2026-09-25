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
field's own components, down to bit-for-bit scalars. `CultCellular` (`math.cs`,
`cellular`) is the first and, for now, only consumer. A struct return with a
mismatched field does not get a second comparison path or a shape of its own;
it fails the same walk that already handles a bare vector return.

## Invariant 8: Value-and-Gradient Primitives

A primitive tagged invariant 8 returns its value and analytic gradient
together, laid out as `float4(gradient.xyz, value.w)`, with no value-only twin
(a value-only form would be a second path with no consumer). `smin_grad` and
`cellular` are the first primitives in this family:

- `smin_grad(float4 a, float4 b, float k)` is Inigo Quilez's cubic-polynomial
  smooth minimum ("smooth minimum",
  <https://iquilezles.org/articles/smin/>), carried to value-and-gradient
  form. Its blend factor `h` is affine in `b.w - a.w`, so differentiating the
  value with respect to position, the terms carrying `dh/dp` cancel exactly;
  the surviving gradient is `lerp(∇b, ∇a, h)`, the same `h` the value uses.
  This is the analytic gradient, not an approximation, and it is continuous
  across the `|a.w - b.w| = k` seam where `h` saturates to 0 or 1.
- `cellular(float3 p)` is Worley's cellular texture basis function ("A
  Cellular Texture Basis Function", SIGGRAPH 1996), searched over the
  jittered 3×3×3 neighbourhood of feature points selected by `pcg3d` (never
  the sin-based `hash`). It returns `CultCellular { nearest, edge, id }`:
  `nearest` is `(∇F1, F1)`, `edge` is `(∇F2 - ∇F1, F2 - F1)`, and `id` is the
  nearest cell's `pcg3d` hash mapped to `[0, 1)`. `∇F1` is undefined exactly
  at its own feature point (`F1 = 0`); `cellular` follows the repo's existing
  degenerate-normal convention
  (`GameCult.Geometry.CultGeometryIsoSurface.EmitOrientedTriangle`, which
  guards a zero-length normal and returns the zero vector instead of the NaN
  a bare `normalize` gives there) and returns the zero vector rather than NaN.
  `∇F2` carries no such guard: `F2 = 0` would need two distinct cells'
  pcg3d-jittered feature points to land on the exact same float32 value in
  every component, which the jitter's float precision makes unreachable for
  any `p` this function is actually called with, so there is no reachable
  case to defend. The identity of the nearest and second-nearest feature
  point changes discontinuously across the `F1 = F2` set (a genuine kink, not
  a numerical artifact), so `∇F1` and `∇F2` are only continuous away from
  that set.

Integer hashing uses the PCG hashes from Jarzynski and Olano, "Hash Functions
for GPU Rendering" (JCGT 9(3), 2020): `pcg(uint)` is O'Neill's RXS-M-XS 32/32
permutation over one LCG step, and `pcg3d`/`pcg4d` are the paper's (3 → 3) and
(4 → 4) hashes. They use only uint multiply, add, xor, and shifts. dxc wraps
uint multiply and add modulo 2^32 exactly as C# unchecked arithmetic does, so
they are integer-exact in HLSL and on the CPU, GPU included, unlike the
`sin`-based `hash`. CultMath has no uint vectors, so `pcg3d(int3)` and
`pcg4d(int4)` carry uint bit patterns in int components. The `float2`/`float3`/`float4`
overloads hash the IEEE-754 bits (`asuint`), so -0 and 0 hash differently.
`pcg3d(float2)` holds z at 0, which is the paper's route for (2 → N) inputs.
Seeds and other scalar uses take one output component.

Where HLSL is silent, CultMath keeps its own decisions and does not defer to
Unity.Mathematics: `hash` returns float, and `Random` is CultMath's own xorshift32. Engine-shaped
helpers that HLSL lacks (`float2x2.Rotate`, `float3x3.Euler`, `quaternion`,
`snoise`) enter only when a consumer needs them, with their semantics stated in
code.

Known C# friction: under `using static CultMath.math;` the constructor
functions (`float3(...)`, `float3x3(...)`) hide the type names in member access,
so static members need a qualified type (`CultMath.float3x3.Euler(...)`).

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
