# Design

CultMath exists to make renderer math readable in every place it needs to live:
Rust hot kernels, C# host code, and HLSL shader code.

## Parity Target: HLSL

HLSL is the semantic authority for every type and intrinsic HLSL defines. The
goal is source compatibility: HLSL-shaped math compiles as C# under
`using static CultMath.math;`. C# forces float literal suffixes (`0.5f`); the
rest of the spelling should survive the move.

That makes these HLSL rules, not options:

- Components and swizzles are writable. `v.xz = f2;` and `v[1] = s;` work.
  Swizzle setters exist only for non-repeating masks, because HLSL rejects
  `v.xx = ...`.
- Comparison operators (`<`, `>`, `<=`, `>=`, `==`, `!=`) are component-wise and
  return `bool2`/`bool3`/`bool4`. Reduce them with `any` and `all`.
  `Equals`/`GetHashCode` stay value-based so vectors still work as dictionary
  keys.
- `select(condition, whenTrue, whenFalse)` uses HLSL's argument order.
- Matrices are row-major: constructors take rows, `m[i]` is row i, `_mRC` is
  row R column C, and products go through `mul` (`mul(m, v)` treats `v` as a
  column, `mul(v, m)` as a row). `*` on matrices would be component-wise in HLSL,
  so CultMath does not define it.
- Scalar/vector arithmetic and comparisons have explicit overloads on both
  sides rather than leaning on implicit scalar splat.

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
