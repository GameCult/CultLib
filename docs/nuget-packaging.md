# NuGet Packaging

CultLib publishes the managed dependency graph as separate packages. The leaf
package is `GameCult.Mesh`; its NuGet dependencies preserve the CultMesh,
CultNet, and CultCache ownership boundaries.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-nuget.ps1
```

The command creates a local feed under `artifacts/nuget`, verifies that every
internal dependency package exists at the same version, then restores and runs
a clean .NET consumer using only `PackageReference Include="GameCult.Mesh"`.

The Unity distribution remains `org.gamecult.cultlib`, built by
`scripts/build-unity-package.ps1`, because Unity consumes the managed assembly
closure through UPM rather than NuGet restore.

Install the Unity package from the immutable release tag:

```text
https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#cultlib-unity-v1.0.46
```
