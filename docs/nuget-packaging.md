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
https://github.com/GameCult/CultLib.git?path=/unity/org.gamecult.cultlib#cultlib-unity-v3.0.0
```

## Rolling back across the store format

A consumer that upgrades and writes a store holding an element id leaves a
`cultcache.store.v3` single-file store (`v5` directory store). Refusal by name depends on the store kind and the pin. A **directory** store (`v5`) is refused by every Unity pin from `cultlib-unity-v1.0.57` and `caching-unity-v1.2.0` on (the earliest tags of each line whose `DirectoryMessagePackBackingStore` reads only the `v4` stamp: "only ... is readable"). A **single-file** store (`v3`) is refused only from `cultlib-unity-v2.0.0` and `caching-unity-v2.0.0`: `cultlib-unity-v1.0.61` and `caching-unity-v1.4.0` read the header string and never compare it. The NuGet line (`cultlib-v1.0.32`, the last `cultlib-v` tag) refuses neither: its directory store writes `cultcache.store.v1.directory` and never checks the stamp on read. Rolling back a single-file store to any of those pins reads it silently, drops the ids, and can rewrite the elements without them. That is the dangerous case. There is
no general carry-back; restore from a copy taken before upgrading, and upgrade
readers before writers. Detail: the Store format and rollback section of
`src/GameCult.Caching/README.md`.
