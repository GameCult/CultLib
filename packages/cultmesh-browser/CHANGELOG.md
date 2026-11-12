# Changelog

All notable changes to `@gamecult/cultmesh-browser` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Breaking

- The package is renamed `cultmesh-browser` to `@gamecult/cultmesh-browser` (the repo already scopes `@gamecult/cultcache-ts`), so the unscoped name cannot be claimed by someone else. Import from `@gamecult/cultmesh-browser` and its subpaths; the API is unchanged. Depends on `@gamecult/cultnet-ts`; the interop test uses `@gamecult/cultmesh-ts`.

### Dependencies

- Needs `@gamecult/cultnet-ts` `^0.4.0` (was `^0.3.0`), moving with the `cultnet-ts` 0.4.0 release.

## [0.4.0]

### Added
- A verifying content reader: `fetchVerifiedContent` and `fetchVerifiedChunk`
  read CultMesh CDN content over HTTP and release no byte that failed its chunk
  or whole-body SHA-256. Failures are `CultMeshContentError` with a stable
  `code`. The reader refuses every redirect, including https to https, while
  the single-host content trust stands.

### Changed
- Depends on `cultnet-ts` `^0.3.0`; the interop test uses `cultmesh-ts`
  `^0.2.0` as a devDependency.
