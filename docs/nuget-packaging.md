# NuGet Packaging

See `docs/semver-policy.md` for what a version bump on these packages is
required to mean and how a release is checked against that before it ships.

CultLib publishes the managed dependency graph as separate packages. The leaf
package is `GameCult.Mesh`; its NuGet dependencies preserve the CultMesh,
CultNet, and CultCache ownership boundaries.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-nuget.ps1
```

The command packs `GameCult.Mesh`, `GameCult.Networking.WebSockets`, and every
project they reach through `ProjectReference` (including `GameCult.Math`, which
carries its own version) into a local feed under `artifacts/nuget`. It verifies
that each of those packages exists at the identity MSBuild reports for it, then
restores and runs a clean .NET consumer using only
`PackageReference Include="GameCult.Mesh"`.

The Unity distribution remains `org.gamecult.cultlib`, built by
`scripts/build-unity-package.ps1`, because Unity consumes the managed assembly
closure through UPM rather than NuGet restore.

Install the Unity package from the immutable release tag:

```text
https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#cultlib-unity-v1.0.57
```
