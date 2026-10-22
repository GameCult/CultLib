# Changelog

All notable changes to `cultcache-rs` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Earlier releases are recorded
only in git history.

## [0.3.1]

### Fixed
- `push_all` reads the store's header under the lock and refuses a format this runtime does
  not read, with `CultCacheStoreUnreadable` `UnsupportedFormat`, leaving the file as it was.
  A file with no readable stamp is still replaced, so a whole write heals it.
