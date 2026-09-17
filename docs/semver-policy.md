# Semantic Versioning Policy

Operator ruling, 2026-09-17: **semantic versioning is policy** for every
package CultLib publishes, strictly. A release that removes or changes public
surface incompatibly is a major bump, full stop — not a judgment call left to
the person cutting the release.

This document says what "breaking" means for each kind of surface this repo
actually ships, and how a release is checked against it. See
`docs/nuget-packaging.md` for how the managed/Unity packaging mechanics work;
this document is the versioning contract those mechanics must satisfy.

## The scar

`org.gamecult.cultlib` 1.0.60 removed `CultInspectorAssetPath` — public API —
and shipped as a patch bump. Its own changelog entry is titled "Breaking"; the
version number said otherwise. A reviewer relying on the number alone would
have missed it. This is recorded as history, not fixed by renumbering: `1.0.60`
stays `1.0.60`. This policy, and the check below, exist so the next one is
caught before it ships instead of written up afterward.

## Packages in scope

| Package | Kind | Tag prefix | Published by |
| --- | --- | --- | --- |
| `org.gamecult.cultlib` | Unity UPM | `cultlib-unity` | `scripts/build-unity-package.ps1`, consumed by git tag |
| `org.gamecult.caching.unity` | Unity UPM | `caching-unity` | tagged directly (no build script; see "Coverage gaps") |
| `org.gamecult.cultmath` | Unity UPM | `cultmath-unity` | `packages/cultmath/scripts/build-unity-package.ps1`, consumed by git tag |
| `@gamecult/cultcache-ts` | npm | `cultcache-ts` | `.github/workflows/publish-packages.yml` |
| `cultcache-py` | PyPI | `cultcache-py` | `.github/workflows/publish-packages.yml` |
| `cultnet-py` | PyPI | `cultnet-py` | `.github/workflows/publish-packages.yml` |
| `cultmesh-py` | PyPI | `cultmesh-py` | `.github/workflows/publish-packages.yml` |

Rust crates are consumed as pinned git revisions (see `publish-packages.yml`'s
own header comment) and are out of scope for tagged-release semver policing;
a `Cargo.toml` version still exists and should follow this policy by
convention, but nothing in this repo checks it mechanically today.

Tag naming stays `<package>-v<version>` for every package above, matching the
prefixes already in use.

## What counts as breaking

Semver's ordinary rule — additive is minor or patch, anything a consumer's
existing code or data can no longer rely on is major — applies per surface as
follows.

### Managed public API (`org.gamecult.cultlib`, NuGet packages, `cultmath`)

Breaking: removing or renaming a public type/member, changing a public
member's signature or observable behavior in an incompatible way, tightening
a public contract (new required parameter, narrowed accepted input),
or moving a type across assemblies/namespaces without a compatible forwarder.

Not breaking: adding a new public member, type, or overload; widening an
accepted type; deprecating (marking obsolete) without removing.

### Unity package inspector attributes (`org.gamecult.cultlib`,
`org.gamecult.caching.unity`)

These ship as ordinary public API by the rule above, but get their own
callout because they are easy to mistake for "just editor tooling": an
attribute class, its constructor shape, and the serialized value shape it
reads/writes are all public surface a consumer's `[CultInspectorX(...)]`
usage and serialized scene/prefab data depend on. Removing or renaming an
attribute, or changing what it expects a annotated member's persisted value to
look like (as `CultInspectorAssetPath` to `CultInspectorAssetGuid` did in
1.0.60) is breaking.

### Persisted wire format / store compatibility (CultCache)

Governed by `src/GameCult.Caching/Contracts/cultcache-schema-compatibility.md`
and `cultcache-persistence-format.md` — read those for the authoritative
rules. In semver terms: anything those documents classify as **hard
rejection** (type change, reference/value change, one-vs-many change, target
schema change, name/index lookup semantics change, a persisted schema with no
compatible local candidate) is breaking for any package that reads or writes
the format. Anything classified as **soft-migratable drift** (compatible
schema id drift, an ignorable missing slot, a defaultable missing slot) is not
breaking on its own, but changing what counts as soft-migratable — loosening
or tightening the drift rules themselves — is breaking for whichever package
owns that classification logic.

### CultNet protocol surface

Breaking: a wire-incompatible change to a CultNet message, frame, or
handshake shape; removing or repurposing a message type or field that another
runtime's implementation depends on; changing routing, authority, or trust
semantics such that a peer built against the previous protocol version
misbehaves rather than cleanly failing. Additive, backward-compatible framing
changes (a new optional field a receiver may ignore) are not breaking.

### Native bridge exported functions (`GameCult.Mesh.Quic.Native`)

Breaking: removing or changing the signature/ABI of a `CULTMESH_API`
(`extern "C"`) exported function, or changing an exported function's
behavior such that the existing managed P/Invoke caller (`GameCult.Mesh.Quic.Native`'s
C# side) or Rust caller (`cultmath-core`'s equivalent `#[no_mangle] extern "C"`
surface) no longer gets what it asked for. Swapping the underlying TLS/QUIC
implementation (as 1.0.60 did, Schannel to OpenSSL MsQuic) is not breaking
*by itself* as long as the exported v1 functions keep their signatures and
behavior — 1.0.60's actual breaking change was the inspector attribute
removal, not the bridge swap, and its changelog correctly filed the bridge
swap under "Changed" rather than "Breaking".

## Pre-1.0 packages

A package at `0.y.z` (currently `org.gamecult.cultmath`, at `0.2.x`) uses the
standard pre-1.0 convention: `MAJOR` stays `0` during initial development, so
`MINOR` carries what `MAJOR` means once the package reaches `1.0.0`. Concretely:

- a breaking change bumps `MINOR` (and resets `PATCH` to `0`);
- a non-breaking change bumps `PATCH`.

Leaving `0.y.z` for `1.0.0` is a deliberate, separate decision (the API is now
considered stable), not something the checker infers on its own.

## What a release changelog entry must contain

Every published package keeps a `CHANGELOG.md` beside its manifest
(`unity/org.gamecult.cultlib/CHANGELOG.md` and
`src/GameCult.Unity/Assets/Caching/CHANGELOG.md` are the existing precedent).
Each release adds a `## [<version>]` section containing:

- a category subsection per change (`### Added`, `### Changed`, `### Fixed`,
  etc., following Keep a Changelog style);
- **`### Breaking`** — this exact heading, this exact spelling — for any
  change that meets the "what counts as breaking" rules above. This heading
  is not decorative: the CI check below greps for it by name.
- enough detail that a consumer can tell what to change, in the style of the
  1.0.60 entry (name the removed/changed symbol and its replacement).

A version with no changelog entry at all fails the check below, the same as
a mislabelled one — an undocumented release is not a smaller sin than a
mislabelled one.

## Enforcement

`scripts/check-changelog-semver.mjs` is the mechanical check. Given a
package name, its `CHANGELOG.md` path, the version being released, and a git
tag prefix, it:

1. resolves the previous published version from the highest existing
   `<prefix>-v*` tag older than the version being released (or treats the
   release as a package's first if none exists);
2. requires a `## [<version>]` changelog entry to exist at all;
3. classifies the version bump (major/minor/patch) against the previous
   version, refusing anything that is not exactly the next version in some
   lane — a skip, a reversal, or a repeat are all refused, not just wrong
   bump sizes;
4. if the changelog entry has a `### Breaking` section, requires the bump to
   be at least major (or, pre-1.0, at least minor);
5. fails loudly, naming the package, the previous and new version, and (when
   relevant) the changelog heading that triggered the requirement.

Run it directly: `node scripts/check-changelog-semver.mjs --package <name>
--changelog <path> --version <x.y.z> --tag-prefix <prefix>`. Its own tests
live in `scripts/check-changelog-semver.test.mjs` (`node --test
scripts/check-changelog-semver.test.mjs`).

It is wired into:

- `.github/workflows/publish-packages.yml`, right after each job's existing
  "tag matches manifest version" check, for `cultcache-ts`, `cultcache-py`,
  `cultnet-py`, and `cultmesh-py`;
- `scripts/build-unity-package.ps1`, for `org.gamecult.cultlib`;
- `packages/cultmath/scripts/build-unity-package.ps1`, for
  `org.gamecult.cultmath`;
- `scripts/verify-caching-unity-release.ps1`, for `org.gamecult.caching.unity`.

### Coverage gaps

`publish-packages.yml` only triggers on `cultcache-ts-v*`, `cultcache-py-v*`,
`cultnet-py-v*`, and `cultmesh-py-v*` tag pushes — it does not build or gate
the Unity UPM packages at all; those are consumed directly by git tag
(`unity/org.gamecult.cultlib`, `packages/cultmath/unity/org.gamecult.cultmath`,
`src/GameCult.Unity/Assets/Caching`), so a workflow trigger on their tags
cannot be added to that file without also giving it a reason to check out and
build Unity content it currently has no job for. The check is instead wired
into the scripts that already run at Unity-package release time:
`build-unity-package.ps1` and `packages/cultmath/scripts/build-unity-package.ps1`
already verify version consistency between the built assembly and
`package.json`, so the semver check runs alongside that as one more release
gate. `org.gamecult.caching.unity` has no build script at all today — it is
pure C# source tagged directly with no compile step — so
`scripts/verify-caching-unity-release.ps1` exists solely to run this check
before that package is tagged; it is not a build script and does not become
one.

None of this is enforced by a git hook or branch protection rule: it runs
when someone remembers to run the Unity release scripts, the same way the
existing assembly-version check does. A tag pushed by hand without running
the release script bypasses both checks equally; closing that gap is a CI
trigger on tag push for the Unity packages, not something this change adds.

Rust crates (consumed as pinned git revisions, not tagged releases in the
usual sense) and the CultNet protocol surface itself (no automated wire
compatibility test currently gates a release) are policy by convention only —
described above, not mechanically checked. Extending the checker to a Rust
crate's `Cargo.toml`/`CHANGELOG.md` or to an automated CultNet interop
compatibility gate is future work, not part of this change.

`@gamecult/cultcache-ts`, `cultcache-py`, `cultnet-py`, `cultmesh-py`, and
`org.gamecult.cultmath` currently have **no `CHANGELOG.md` file at all**. This
is a real, pre-existing gap, not something introduced by this policy: their
next tagged release will fail the new check for exactly that reason ("no
changelog entry for version X") until each package's `CHANGELOG.md` exists.
That failure is intended — a release with no changelog is not a smaller
problem than a mislabelled one — but it does mean the very next release of
any of these five packages needs a `CHANGELOG.md` created first. Backfilling
changelog content for their past releases is not part of this change: it
would mean inventing release-note prose for history this pass has no
first-hand knowledge of, which is worse than leaving the gap named.
