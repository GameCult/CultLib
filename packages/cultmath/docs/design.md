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
  lowers it, in C# and in the HLSL mirror; float `min`, `max`, `clamp`, and
  `saturate` follow the DXIL `FMin`/`FMax`/`Saturate` operations dxc emits for
  them: `min(a, b)` is `a < b ? a : b`, `max(a, b)` is `a >= b ? a : b`, a NaN
  operand returns the other (so `saturate(NaN)` is 0), `clamp(x, a, b)` is
  `min(max(x, a), b)`, and `saturate(x)` is `min(1, max(0, x))`, which maps -0
  to +0; `any`/`all` accept numeric vectors (a component is true when it is
  not 0); `min`, `max`, and `abs` on int vectors return int vectors.

dxc marks float arithmetic and compares `fast` (no NaNs) unless a value is
`precise`, so drivers may optimize NaN handling away and NaN results on real
GPUs are not guaranteed. CultMath defines the CPU result by dxc's lowering and
the DXIL operation spec (DirectXShaderCompiler `docs/DXIL.rst`).

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
5. The file body is wrapped in a class, since C# has no free functions; HLSL
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
`cultmath_hash` and value noise differ bit for bit on hardware.

Where HLSL is silent, CultMath keeps its own decisions and does not defer to
Unity.Mathematics: `normalize` is safe (divides by `max(length, 1e-20)`),
`hash` returns float, and `Random` is CultMath's own xorshift32. Engine-shaped
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
