// The one declaration of what each released package is, read by
// scripts/check-changelog-semver.mjs (docs/semver-policy.md) and by nothing a caller types.
// Keyed by the --package name. Paths are repo-relative.
//   tagPrefix   releases are tags named '<tagPrefix>-vMAJOR.MINOR.PATCH'
//   changelog   the package's CHANGELOG.md
//   assemblies  the directory whose tracked DLLs the package ships; absent when it ships none
export default {
  "org.gamecult.cultlib": {
    tagPrefix: "cultlib-unity",
    changelog: "unity/org.gamecult.cultlib/CHANGELOG.md",
    assemblies: "unity/org.gamecult.cultlib/Runtime/Plugins",
  },
  "org.gamecult.cultmath": {
    tagPrefix: "cultmath-unity",
    changelog: "packages/cultmath/unity/org.gamecult.cultmath/CHANGELOG.md",
    assemblies: "packages/cultmath/unity/org.gamecult.cultmath/Runtime/Plugins",
  },
  "org.gamecult.caching.unity": {
    tagPrefix: "caching-unity",
    changelog: "src/GameCult.Unity/Assets/Caching/CHANGELOG.md",
  },
  "@gamecult/cultcache-ts": {
    tagPrefix: "cultcache-ts",
    changelog: "packages/cultcache-ts/CHANGELOG.md",
  },
  "cultcache-py": {
    tagPrefix: "cultcache-py",
    changelog: "packages/cultcache-py/CHANGELOG.md",
  },
  "cultnet-py": {
    tagPrefix: "cultnet-py",
    changelog: "packages/cultnet-py/CHANGELOG.md",
  },
  "cultmesh-py": {
    tagPrefix: "cultmesh-py",
    changelog: "packages/cultmesh-py/CHANGELOG.md",
  },
};
