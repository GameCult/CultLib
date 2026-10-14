# CultLib Gaps: Target

Date: 2026-10-09. Campaign `cultlib-gaps` in the Eureka mind.

## Why

Consumers of CultLib keep finding capabilities a reasonable consumer of a general
library would expect and that CultLib lacks. Doctrine says a gap is filled in its
owner, never worked around in the consumer. This campaign is the standing home for
those gaps, so each one lands in CultLib as a small cut instead of a local helper in
whichever project hit it.

Known gaps at opening (each a follow-up in the mind, owned by the consumer campaign
that found it):

- CultMath `BoundedLeastSquares.Solve` has a fixed KKT tolerance (1e-6); Aetheria's
  thrust allocator needs a tighter one (`aetheria-release:follow_up:cultmath-bls-kkt-tolerance`).
- `cultcache-rs` has no decimal type, its snapshot reads follow symlinks, and its
  single-file store stages files with the process umask and no mode option
  (`eureka-body:follow_up:cultcache-rs-decimal-and-nofollow`).

## Invariants

- `owner-fills-gap`: a capability a consumer needs from CultLib is added to CultLib, with the consumer adopting it; no consumer-local copy survives the cut that adds it.
- `defaults-unchanged`: a new option defaults to today's behaviour; existing callers and stored bytes do not change.
- `wire-parity`: a CultCache wire change keeps byte parity across runtimes against the C# reference, with a cross-runtime fixture.
- `semver-measured`: a public API change bumps the package version by measured semver and ships as a tagged release consumers can pin.

## Not in scope

- Growing CultCache into a general database (memory `cultcache-scope-restraint`).
- Any consumer's adoption code beyond the pin bump; that stays in the consumer's campaign.
