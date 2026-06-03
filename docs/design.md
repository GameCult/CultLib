# Design

CultMath exists to make renderer math readable in every place it needs to live:
Rust hot kernels, C# host code, and HLSL shader code.

## Rules

- Keep type names HLSL-shaped: `float2`, `float3`, `float4`, and `math`.
- Keep Rust as the owner of native hot-path kernels and parity fixtures.
- Keep C#, shader, and scripting surfaces as runtime wrappers, not competing
  math authorities.
- Implement public shader semantics directly; do not copy Unity.Mathematics.
- Prefer small immutable value types.
- Prefer component-wise overloads over clever generic machinery until the API
  earns that complexity.
- Keep the first surface boring and exact. Noise, matrices, quaternions, and
  packing helpers come later.

## Non-Goals

- Compiling C# to shaders.
- Replacing `System.Numerics` for general .NET work.
- Recreating every Unity.Mathematics type because a spreadsheet somewhere says
  “coverage.” That way lies decorative obesity.
