# Design

CultMath exists to make renderer math readable in every place it needs to live:
Rust hot kernels, C# host code, and HLSL shader code.

## Rules

- Keep type names HLSL-shaped: `float2`, `float3`, `float4`, and `math`.
- Keep Rust as the owner of native hot-path kernels and parity fixtures.
- Keep C#, shader, and scripting surfaces as runtime wrappers, not competing
  math authorities.
- Keep `shaders/CultMath.hlsl` as the canonical shader mirror for shared
  intrinsics. Project-local shader helpers should move here once more than one
  organ needs them or once CPU/GPU parity matters.
- Implement public shader semantics directly; do not copy Unity.Mathematics.
- Prefer small immutable value types.
- Prefer component-wise overloads over clever generic machinery until the API
  earns that complexity.
- Keep the surface boring and exact. Noise primitives are allowed when they are
  source-grounded, shader-shaped, and shared by more than one project. Matrices,
  quaternions, and packing helpers still need an owner before they enter.
- Planetary geometry is a scale-aware field query, not a renderer asset. CPU
  simulation is the reference consumer; pages, patches, and maps are derived
  lowerings carrying field identity and unresolved error.
- Application base fields and gameplay meaning enter through narrow inputs.
  CultMath does not own biomes, resources, traversability, render materials, or
  committed game consequences.

## Non-Goals

- Compiling C# to shaders.
- Generating HLSL from Rust or Rust from HLSL.
- Replacing `System.Numerics` for general .NET work.
- Recreating every Unity.Mathematics type because a spreadsheet somewhere says
  “coverage.” That way lies decorative obesity.
