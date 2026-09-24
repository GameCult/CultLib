# Changelog

All notable changes to this package are documented in this file.

Earlier releases (0.2.0-0.2.3) predate this package's changelog and are not
backfilled here; see `docs/semver-policy.md` for why.

## [0.2.4]

### Added

- `math.erf` and `math.erfinv`: single-precision error function and its
  inverse. `erf` is the Abramowitz & Stegun 7.1.26 rational/exponential fit
  (max absolute error 1.5e-7 by that source, 6.621e-7 measured here against
  an independent reference). `erfinv` is Giles' minimax polynomial
  ("Approximating the erfinv function", GPU Computing Gems, 2010), guarded
  explicitly at the domain edges (`erfinv(1) = +Infinity`,
  `erfinv(-1) = -Infinity`, outside `[-1, 1]` is `NaN`); measured worst
  absolute error 5.066e-7, worst `erf(erfinv(y))` round trip 6.109e-7.
