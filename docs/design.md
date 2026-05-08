# Design

CultMath exists to make renderer math readable in both places it needs to live:
C# host code and HLSL shader code.

## Rules

- Keep type names HLSL-shaped: `float2`, `float3`, `float4`, and `math`.
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
