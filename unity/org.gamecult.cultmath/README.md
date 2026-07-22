# CultMath for Unity

This is CultMath's Unity 2021.3-compatible Git-UPM surface. It contains the
precompiled `CultMath.dll`, portable symbols, and the numeric
`Shaders/CultMath.hlsl` mirror.

Unity does not compile CultMath's repository source tree. That source uses the
current C# language and remains owned by the normal .NET project. The package
facade references only the tracked precompiled assembly, so consumers do not
need `csc.rsp` language overrides.

Build and inspect the package from the repository root:

```powershell
.\scripts\build-unity-package.ps1
```

The builder fresh-builds CultMath for `netstandard2.1`, verifies the tracked
DLL/PDB and stable Unity metadata, and stages an inspectable package under
`artifacts/unity/org.gamecult.cultmath`.

Consume the repository package with:

```json
"org.gamecult.cultmath": "https://github.com/GameCult/CultMath.git?path=/unity/org.gamecult.cultmath"
```

Production consumers should pin that URL to a commit or release tag.
