# Changelog

All notable changes to `@gamecult/cultcache-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.14.1]

### Added
- `checkPayloadDepth` is re-exported from `@gamecult/cultcache-ts/document`, so dependents
  (`cultnet-ts/content`) bound payload nesting with the same check the cache
  applies on decode.
