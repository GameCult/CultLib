# CultMath for Unity

This is CultMath's Unity 2021.3-compatible Git-UPM surface. It contains the
precompiled `CultMath.dll`, portable symbols, the numeric
`Shaders/CultMath.hlsl` mirror, and the `CultMath.UnityBridge` assembly.

`CultMath.UnityBridge` is the only C# source Unity compiles here. It holds the
UnityEngine conversions the engine-free core cannot declare: `ToCultMath()` on
`Vector2/3/4`, `Vector2Int/3Int`, `Quaternion`, and `Color`; `ToUnity()` on
`float2/3/4`, `int2/3`, and `quaternion`; `ToColor()` on `float4`. C# only
admits conversion operators on the source or target type, so these are
extension methods rather than implicit casts. Import `CultMath.UnityBridge` to
use them.

Unity does not compile CultMath's repository source tree. That source uses the
current C# language and remains owned by the normal .NET project. The package
imports only the tracked auto-referenced precompiled assembly, so consumers do
not need `csc.rsp` language overrides or an asmdef propagation shim.

Build and inspect the package from the repository root:

```powershell
.\scripts\build-unity-package.ps1
```

The builder fresh-builds CultMath for `netstandard2.1`, verifies the tracked
DLL/PDB and stable Unity metadata, and stages an inspectable package under
`artifacts/unity/org.gamecult.cultmath`.

Consume the repository package with:

```json
"org.gamecult.cultmath": "https://github.com/GameCult/CultLib.git?path=/packages/cultmath/unity/org.gamecult.cultmath"
```

Production consumers should pin that URL to a commit or release tag.
