# CultMath

CultMath is the project name. Its clean .NET package identity is
`GameCult.Math`, owned and released from the CultLib monorepo.

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
- `Random`, a compact deterministic XORShift stream with scalar and vector draws
- `BatchMath` SoA kernels over `Span<float>` / `ReadOnlySpan<float>` using
  `System.Numerics.Vector<float>` when available, including radial falloff
  acceleration and semi-implicit Euler integration
- `math` intrinsics: `radians`, `degrees`, `abs`, `floor`, `ceil`, `frac`,
  `min`, `max`, `clamp`, `saturate`, `lerp`, `step`, `smoothstep`, `dot`,
  `cross`, `length`, `distance`, `normalize`, `reflect`, `csum`, exponential
  `decay`/`damp`, intercept/segment helpers, Catmull-Rom, Bezier curves, and
  deterministic value-noise primitives
- `shaders/CultMath.hlsl`, a canonical HLSL mirror include for shader-side
  parity. HLSL already owns `float2`, `float3`, and `float4`; the include
  exposes `cultmath_*` functions for shared semantics, including CultMath's
  safe `normalize` contract.
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
float grain = math.value_noise_bicubic(math.float2(12.0f, 3.5f));
```

```hlsl
#include "CultMath.hlsl"

float3 normal = cultmath_normalize(float3(0.25, 1.0, -0.1));
float rim = cultmath_saturate(1.0 - dot(normal, float3(0.0, 0.0, -1.0)));
float glow = cultmath_smoothstep(0.2, 1.0, rim);
float grain = cultmath_value_noise_bicubic(float2(12.0, 3.5));
```

## Provenance

The small spline, damping, and pursuit helpers were promoted from Aetheria's
local `AetheriaMath` surface. The value-noise family follows public shader
literature from Inigo Quilez's noise and mini-spline articles, expressed in
CultMath names with tests instead of copied project-local helper drift.

## Build

```powershell
dotnet build packages/cultmath/src/CultMath/CultMath.csproj
dotnet test packages/cultmath/tests/CultMath.Tests/CultMath.Tests.csproj
cargo test --manifest-path packages/cultmath/native/cultmath-core/Cargo.toml
cargo build --release --manifest-path packages/cultmath/native/cultmath-core/Cargo.toml
.\packages\cultmath\scripts\build-unity-package.ps1
```

## Unity package

CultMath's Unity 2021.3 surface is the tracked precompiled package at
`packages/cultmath/unity/org.gamecult.cultmath`. Unity consumes `CultMath.dll` and the numeric
`CultMath.hlsl`; it does not compile the repository's current-language C# source
tree and does not require a consumer `csc.rsp` workaround.

```json
"org.gamecult.cultmath": "https://github.com/GameCult/CultLib.git?path=/packages/cultmath/unity/org.gamecult.cultmath"
```

The repository root is not a Unity package. Build the tracked package with
`packages/cultmath/scripts/build-unity-package.ps1`; the script fresh-builds the netstandard2.1
assembly and rejects stale binaries, missing metadata, unexpected assemblies,
or leaked C# source.

## Shader Tooling

CultMath keeps portable DXC outside git under `packages/cultmath/.tools/`:

```powershell
.\packages\cultmath\tools\get-dxc.ps1
.\packages\cultmath\tools\get-spirv-cross.ps1
.\packages\cultmath\tools\compile-hlsl-spirv.ps1 `
  -ShaderPath E:\Projects\Odin\crates\muninn-move-tracker\shaders\MoveSphereCandidate.comp.hlsl `
  -OutputPath E:\Projects\Odin\crates\muninn-move-tracker\artifacts\shader\MoveSphereCandidate.comp.spv `
  -IncludePath E:\Projects\CultLib\packages\cultmath\shaders
.\packages\cultmath\tools\compile-spirv-metal.ps1 `
  -SpirvPath E:\Projects\Odin\crates\muninn-move-tracker\artifacts\shader\MoveSphereCandidate.comp.spv `
  -OutputPath E:\Projects\Odin\crates\muninn-move-tracker\artifacts\shader\MoveSphereCandidate.comp.metal
```

DXC emits Vulkan SPIR-V with `-spirv`. `-TargetEnv vulkan1.2` is exposed by the
script, but the current Windows DXC release compiled this shader successfully
with DXC's default Vulkan target while rejecting the dotted target-env value.
The SPIR-V script applies default Vulkan register shifts for cbuffers, textures,
UAVs, and samplers so cross-compiled Metal buffers do not alias by resource
class. SPIRV-Cross emits Metal Shading Language from SPIR-V on Windows; building
an iPad-ready `.metallib` still belongs to Apple's Metal compiler on an Apple
toolchain.

## Doctrine

The goal is mechanical sympathy between CPU-side authoring, native kernels, and
GPU-side shader math. If an intrinsic exists here, it should behave like the
shader concept unless a runtime's numeric rules make that impossible. Weird
deviations get tests and documentation, not folklore.

CultMath is still not a shader generator. Projects should include
`shaders/CultMath.hlsl` directly or add that directory to their shader include
path. If a shared operation needs CPU/GPU parity, put the primitive here and
test it here before copying local math helpers into a renderer or daemon.
