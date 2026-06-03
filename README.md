# CultMath

Clean-room math shaped for people who think in HLSL.

CultMath provides lowercase vector types, shader-style intrinsics, and native
hot-path kernels so math-heavy renderer code can move between C#, Rust, and HLSL
without turning every line into a translation exercise. It is not a
Unity.Mathematics clone. It is a small, Aquarium-born library built from public
shader semantics.

## Current Surface

- `float2`, `float3`, `float4`
- component-wise arithmetic
- scalar-to-vector splats
- swizzles for common lanes
- conversions to and from `System.Numerics`
- `math` intrinsics: `radians`, `degrees`, `abs`, `floor`, `ceil`, `frac`,
  `min`, `max`, `clamp`, `saturate`, `lerp`, `step`, `smoothstep`, `dot`,
  `cross`, `length`, `distance`, `normalize`, and `reflect`
- `Voronoi.SampleTones`, a C# batch surface that calls the Rust
  `cultmath-core` native kernel when `cultmath_core` is available and falls back
  to the managed parity path otherwise.

## Native Core

The canonical hot-path body lives in `native/cultmath-core`. It exports a C ABI
for batch kernels such as `cultmath_apollonian_voronoi_tones`.

Runtime wrappers own syntax and integration. Rust owns optimized primitive
kernel behavior and parity fixtures.

## Example

```csharp
using CultMath;

float3 normal = math.normalize(new float3(0.25f, 1.0f, -0.1f));
float rim = math.saturate(1.0f - math.dot(normal, new float3(0.0f, 0.0f, -1.0f)));
float glow = math.smoothstep(0.2f, 1.0f, rim);
```

## Build

```powershell
dotnet build CultMath.slnx
dotnet test CultMath.slnx
cargo test --manifest-path native/cultmath-core/Cargo.toml
cargo build --release --manifest-path native/cultmath-core/Cargo.toml
```

## Doctrine

The goal is mechanical sympathy between CPU-side authoring, native kernels, and
GPU-side shader math. If an intrinsic exists here, it should behave like the
shader concept unless a runtime's numeric rules make that impossible. Weird
deviations get tests and documentation, not folklore.
