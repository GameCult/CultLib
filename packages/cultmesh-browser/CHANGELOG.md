# Changelog

All notable changes to `cultmesh-browser` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
