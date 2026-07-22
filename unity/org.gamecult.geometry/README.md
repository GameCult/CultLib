# GameCult.Geometry for Unity

This package is the Unity projection of the GameCult.Geometry authority. It
depends on `org.gamecult.cultmath` for numeric primitives and
`org.gamecult.cultlib` for CultCache/CultNet/CultMesh integration.

Build it with `scripts/build-geometry-unity-package.ps1`. Do not edit generated
plugin assemblies in the package output.

CultLib-authored integration files are MIT licensed. The planetary C# compiled
into `GameCult.Geometry.dll` and the geometry HLSL under `Shaders` are
MPL-2.0 and retain their source notices and provenance. See `LICENSES.md` and
`THIRD-PARTY-NOTICES.md` in the staged package. No MPL-covered source is
relicensed. The package manifest therefore uses `SEE LICENSES IN README.md`
rather than claiming one license for every file.
