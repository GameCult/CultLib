# CultMath

Clean-room C# math shaped for people who think in HLSL.

CultMath provides lowercase vector types and shader-style intrinsics so
math-heavy renderer code can move between C# and HLSL without turning every
line into a translation exercise. It is not a Unity.Mathematics clone. It is a
small, Aquarium-born library built from public shader semantics and plain .NET.

## Current Surface

- `float2`, `float3`, `float4`
- component-wise arithmetic
- scalar-to-vector splats
- swizzles for common lanes
- conversions to and from `System.Numerics`
- `math` intrinsics: `radians`, `degrees`, `abs`, `floor`, `ceil`, `frac`,
  `min`, `max`, `clamp`, `saturate`, `lerp`, `step`, `smoothstep`, `dot`,
  `cross`, `length`, `distance`, `normalize`, and `reflect`

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
```

## Doctrine

The goal is mechanical sympathy between CPU-side authoring and GPU-side shader
math. If an intrinsic exists here, it should behave like the shader concept
unless .NET numeric rules make that impossible. Weird deviations get tests and
documentation, not folklore.
