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

| Package | Kind | Published by |
| --- | --- | --- |
| `org.gamecult.cultlib` | Unity UPM | `scripts/build-unity-package.ps1`, consumed by git tag |
| `org.gamecult.caching.unity` | Unity UPM | tagged directly (no build script; see "Coverage gaps") |
| `org.gamecult.cultmath` | Unity UPM | `packages/cultmath/scripts/build-unity-package.ps1`, consumed by git tag |
| `@gamecult/cultcache-ts` | npm | `.github/workflows/publish-packages.yml` |
| `cultcache-py` | PyPI | `.github/workflows/publish-packages.yml` |
| `cultnet-py` | PyPI | `.github/workflows/publish-packages.yml` |
| `cultmesh-py` | PyPI | `.github/workflows/publish-packages.yml` |

Each package's tag prefix, changelog path and, for the packages that ship
assemblies, the directory of tracked DLLs are declared in
`scripts/release-packages.mjs`, the one copy. The check reads them by package
name; no release script or workflow passes one.

Rust crates are consumed as pinned git revisions (see `publish-packages.yml`'s
own header comment) and are out of scope for tagged-release semver policing;
a `Cargo.toml` version still exists and should follow this policy by
convention, but nothing in this repo checks it mechanically today.

Releases are tags named `<tagPrefix>-v<version>`, with the prefix declared in
`scripts/release-packages.mjs`. The check refuses a declaration in which two
packages share a prefix or an entry lacks a string prefix or changelog.

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
package name and the version being released, it reads the package's
declaration from `scripts/release-packages.mjs` in `--cwd` (an unknown package
or a missing declaration refuses), and:

1. derives the release record from `git tag -l` in `--cwd`. A same-prefix tag
   is one that starts `<prefix>-v`; one that is not
   `<prefix>-vMAJOR.MINOR.PATCH` refuses, naming the tag, and is never
   skipped. The previous release is the newest same-prefix tag strictly older
   than the version being released, by version order, that is also an ancestor
   of the commit being checked: a backport tagged later on another branch is
   not part of that history, so it never moves a rebuild's predecessor. A tag
   equal to the version does not exempt
   it and does not count as a previous release: there is no "already
   published" skip, so a rebuild or a CI re-run at a tagged version is
   measured against that version's predecessor, exactly as at release, and
   gets the same verdict. With no older tag, a newer one refuses (the version
   is older than every release), and a repository that holds no tag other than
   the version's own refuses, because a first release cannot be told from a
   missing record. Older tags of which none is an ancestor leave the
   predecessor unknown and refuse. Otherwise it is a first release, which no
   caller declares; it is refused when the package's changelog already lists an
   earlier version, because then the declared prefix names none of its
   releases.
   Tags that cannot be read (a `--cwd` that is not a git repository) refuse. A
   deleted predecessor tag leaves nothing in git to find, so in a checkout that
   holds other tags it cannot be told from a first release;
2. requires a `## [<version>]` changelog entry to exist at all;
3. classifies the version bump (major/minor/patch) against the previous
   version, refusing anything that is not exactly the next version in some
   lane — a skip, a reversal, or a repeat are all refused, not just wrong
   bump sizes;
4. when the release script supplies built assemblies (below), treats the
   release as breaking if the public API measurably lost or changed a member
   against the previous tag, whether or not the changelog says so; a
   measured break with no `### Breaking` section is refused, naming the
   count, the first diagnostics, and the heading that must declare them;
5. if the release is breaking (a `### Breaking` section, a measured break, or
   both), requires the bump to be at least major (or, pre-1.0, at least
   minor); a `### Breaking` section with nothing measured is valid, because
   behavioural breaks are invisible to a signature comparison;
6. fails loudly, naming the package, the previous and new version, and (when
   relevant) the changelog heading or the measured diagnostics that
   triggered the requirement.

Additions are never a rule: adding a public member is not breaking (see
above), and the check neither refuses nor demands a bump for one.

### Measured input

A package whose declaration names an `assemblies` directory is measured, and
the release must hand over one `--api-built <dll>` per assembly it built (plus
`--api-refs <dir>` for directories that hold the built assemblies'
dependencies). A package with no such directory takes no `--api-built`, and one
that has it cannot omit it. The measured set is every managed assembly tracked
under that directory, at any depth, at the previous tag, read as git blobs (no
worktree, no network, unaffected by what the working tree tracks now) and keyed
by file name. A DLL is managed when its PE header carries a CLI header; a
native DLL has none and is not measured, and a tracked `.dll` that is not a PE
image refuses. Two managed assemblies with one name refuse. The checker
compares each built assembly with its namesake using
`Microsoft.DotNet.ApiCompat.Tool`; each `CPnnnn` diagnostic is one measured
break. Every managed assembly the previous tag tracked that the build no
longer hands over is itself a measured break, whatever its name or place, so a
release script passes every assembly it ships. A built assembly with no
namesake at the tag is an addition and is not examined.

Measuring nothing is a refusal, never a pass. The check refuses, naming what
is missing, when the previous tag tracks no managed assembly under the
declared directory (a wrong or moved directory, or a tag that predates it);
when git cannot read the tracked assemblies; and when the tool fails to
measure (not installed, an unreadable assembly, a crash: a non-zero exit with
no diagnostic line). A first release measures nothing.

The tool and the `NETStandard.Library.Ref` 2.1.0 reference assemblies are
pinned in `scripts/api-gate/` (`.config/dotnet-tools.json` and
`ApiRefs.csproj`) and restored there by the check itself, so a release does
not depend on any other tool in the repository's manifest. NuGet is needed
until both are cached under `scripts/api-gate`. Both sides of every
comparison resolve netstandard types through those reference assemblies, and the
new side also through the built DLL's own directory and every `--api-refs`
directory. No suppression file is kept: a suppression would be a second, silent
declaration of a break, so a break is declared only under `### Breaking`.

A passing measured run prints that unmeasured changes still need declaring.

#### Unmeasured changes

ApiCompat does not report these changes to a public member, and the check
therefore passes them. Each is a breaking change under the rules above and is
declared under `### Breaking`, as behavioural breaks are:

- a member gaining or losing `static`;
- a parameter gaining or losing `ref`, `out` or `in`;
- a `const` field's value changing;
- an enum member's underlying value changing;
- a field gaining or losing `readonly`;
- a default parameter value changing.

The release scripts run the check once the assemblies under judgement exist,
after the build and before anything is copied into the package.

Run it directly: `node scripts/check-changelog-semver.mjs --package <name>
--version <x.y.z> [--cwd <dir>] [--api-built <dll>... [--api-refs <dir>...]]`.
Any other option is a usage error (exit 2). Its own tests live in
`scripts/check-changelog-semver.test.mjs` (`node --test
scripts/check-changelog-semver.test.mjs`).

It is wired into:

- `.github/workflows/publish-packages.yml`, right after each job's existing
  "tag matches manifest version" check, for `cultcache-ts`, `cultcache-py`,
  `cultnet-py`, and `cultmesh-py`, with `fetch-depth: 0` so the checkout holds
  the tags;
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

What stays unmeasured: behaviour behind an unchanged signature, wire formats,
native exports of the QUIC bridge, and the Caching Unity package's source
(it has no assembly). Those rest on the `### Breaking` declaration.

Rust crates (consumed as pinned git revisions, not tagged releases in the
usual sense) and the CultNet protocol surface itself (no automated wire
compatibility test currently gates a release) are policy by convention only —
described above, not mechanically checked. Extending the checker to a Rust
crate's `Cargo.toml`/`CHANGELOG.md` or to an automated CultNet interop
compatibility gate is future work, not part of this change.

`@gamecult/cultcache-ts`, `cultcache-py`, `cultnet-py`, and `cultmesh-py`
currently have **no `CHANGELOG.md` file at all**; the check refuses their next
tagged release ("changelog not found at <declared path>") until each
package's file exists. A release with no changelog is not a smaller problem
than a mislabelled one. The next release of each creates its file:
`packages/cultcache-ts/CHANGELOG.md` arrives with the document-variants work
(`hands/variants-c2a`), and whoever cuts the next release of `cultcache-py`,
`cultnet-py` or `cultmesh-py` creates that package's file. Backfilling
changelog content for past releases would mean inventing release-note prose
for history nobody here knows first-hand, so the gap stays named.
