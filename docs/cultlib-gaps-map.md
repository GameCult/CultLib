# CultLib Gaps: Map

Campaign `cultlib-gaps` (target: `docs/cultlib-gaps-target.md`). This file holds body facts, probes and
rationale for the cuts the mind's `cut_spec`s name; the specs cite it by section and fact number.

Pinned base: `origin/main` at `6bdc23c9` (2026-10-10). Imagination pass `imagination-cl-gaps-cultmath`, session
`self-2026-10-10-ag`. Probes live under the session scratchpad, `imagination-cl-gaps-cultmath/` (`probe1` traced
copy of the solver and the warm/cold sweep, `suite` a scratch copy of `packages/cultmath` with the candidate fix,
`probe2` the sweep against that copy, `probe3` the overload probe). None of it is committed.

## Cut order

1. `bls-release-stop`: BoundedLeastSquares stops on a release only when the release is not real.
2. `bls-input-field`: a validation helper names the invalid input (follow-up `bls-invalid-input-field`). Blocked on
   question `cultlib-gaps:question:bls-input-error-shape`.
3. `cultmath-release-0-4-1`: release `org.gamecult.cultmath` with cuts 1 and 2. Version follows question
   `cultlib-gaps:question:bls-fix-release-lane`.
4. `cultmath-rotation-algebra`: quaternion algebra, swing-twist and joint clamps (ruling
   `aetheria-release:ruling:solver-in-gamecult-animation`). Ships in the CultMath release that the first
   GameCult.Animation release pins; that release is mapped with GameCult.Animation.

## B. BoundedLeastSquares stopping

Finding `aetheria-release:finding:cut-thrust-allocator-core.s1.solver-converged-early` (Soul, Medium): a cold start
reports `Converged` at a non-optimal point, so Aetheria's thrust allocator (tolerance 1e-12) answers differently
warm and cold, by up to .078 throttle.

### Body facts

- **B1. One runtime ships the solver.** `BoundedLeastSquares` exists only in
  `packages/cultmath/src/CultMath/BoundedLeastSquares.cs` (C#, netstandard2.1 and net8.0). The type doc says there is
  no HLSL mirror; grep over `packages/*` finds no Rust, TypeScript, Python or Kotlin sibling, and no other CultLib
  project calls it. Its consumer outside CultLib is Aetheria's `ThrustAllocator`.
- **B2. The defect reproduces against CultMath at base, outside Aetheria.** `probe1` rebuilds Soul's allocator
  matrix (Soul's `ProbeAllocator2`, constants H=100, Wy=1000, W=.3, kappa=10, rho=.1, EMax=2) for hull
  `Synthetic(8, 7005)` and the 8th intent of `System.Random(5)`, (-0.8505, 0.6739, 0.5587): 15 rows, 12 columns.
  Real `BoundedLeastSquares.Solve` at tolerance 1e-12 from x = 0: `Converged`, 13 iterations, cost 36.0313737,
  x7 = 1. Re-solving from that answer: 1 iteration, cost 36.0312608, x7 = 0.9222145, which is also the warm answer.
  The same literals (dumped with `"R"` floats, `probe1 dump`) reproduce it at tolerance 1e-9 and 1e-12, and not at
  the default 1e-6, where the cold solve stops at iteration 10 inside the tolerance and the re-solve takes 0
  iterations.
- **B2a. The W5 problem as literals** (`probe1 dump`, float `"R"` round trip; m = 15, n = 12, row-major; the
  regression fixture for `bls-release-stop`). Nonzero entries of A:
  - row 0: a[0,2] = 100f, a[0,8] = -100f, a[0,9] = 100f
  - row 1: 13.882518f, 20.86695f, 0, 14.8965845f, 12.38046f, 22.115599f, -13.706654f, 15.85789f, 0, 0, -100f, 100f
  - row 2: -187.72859f, 239.31168f, 158.87828f, -270.31165f, 235.38467f, -14.704258f, 121.61232f, 244.81305f, 0, 0,
    0, 0
  - rows 3-6: a[3+k, 8+k] = 0.3f (k = 0..3); rows 7-14: a[7+i, i] = 0.1f (i = 0..7)
  - b = {0, 67.385445f, 558.70917f, -3f, -3f, -3f, -3f, 0 x 8}; lo = 0; hi = 1f for columns 0-7, 2f for 8-11.
  At base, tolerance 1e-12 from x = 0: `Converged`, 13 iterations, x[7] = 1, cost 36.031373704487095; a re-solve
  from that answer: 1 iteration, x[7] = 0.92221445f, cost 36.031260771352414.
- **B3. The stopping rule that fires is the progress rule, after a release** (`BoundedLeastSquares.cs:273-278`). A
  traced copy of the solver, bit-equal to the real one on this problem, shows the last two passes:
  - iteration 12: full step lands; column 2 sits at its lower bound with gradient -0.00205 (violation 2.05e-3,
    tolerance 1.5e-7); the solver releases it and records fMark = f.
  - iteration 13: the re-solve moves x2 from 0 to 2.05e-7. The cost falls by about 2.1e-10, and the stall band
    `1e-15 * fScale` is 2.11e-10, so `f >= fMark - band` holds and the solver reports `Converged`. Column 7 is then at
    its upper bound with gradient +0.00145, ten thousand times the tolerance; releasing it is worth 1.1e-4 of cost.
- **B4. Why the progress rule cannot judge a release.** Near a minimiser, a cost difference resolves only about the
  square root of what a gradient comparison resolves: a real step of size dx against gradient g lowers the cost by
  about g*dx, while the cost itself is a sum of terms of size fScale. Here g*dx (2e-10) sat exactly at the cost's
  rounding band. The gradient comparison is far from its own rounding: for column 7, the bound on the double
  gradient's rounding error, `(n+1) * eps * (|A^T b|_7 + sum_k |(A^T A)_7k x_k|)` (Higham's inner-product bound,
  gamma_(n+1)), is 9.3e-10, and the violation is 1.56e6 times that.
- **B5. What the progress rule is for** (commit 43035bc8, and the tests at `BoundedLeastSquaresTests.cs:774-942`).
  It stops two kinds of pass that can never reach the tolerance: a free-set re-solve whose residual is rounding on a
  near-singular free set, and a release whose violation is itself rounding (kktTol at or below the noise, including
  kktTol = 0 when the box point nearest the origin is stationary). Deleting it outright breaks both: with a noise
  gate on every test and no progress rule, 8 of 66 BLS tests fail (`wide`, `integer-tie`, `collinear`, `cond` spin to
  the cap; `NearSingularFreeSetsConfirmOnceAndStop` sees 0 confirmations). Exempting releases from the progress rule
  with no noise gate fails one: `StalledPassesConvergeInsteadOfSpinningToTheCap("plain")` seed 141, b = 0,
  kktTol = 0, cycles release 3 (violation 1.7e-18) / stationarity miss / block on 3 until the cap.
- **B6. The candidate fix, measured.** In a scratch copy of the package (`suite`, diff `suite/clean.diff`, 44 lines):
  (a) a bound column is a release candidate only if its violation exceeds both kktTol and the rounding bound of B4;
  (b) the progress rule judges only passes that release nothing; a pass that releases sets fMark to +Inf, so a
  release is never measured against a cost recorded before it. Results:
  - all 66 tests in `BoundedLeastSquaresTests` pass, including `DefaultSolvesAreBitIdenticalToTheirGoldenBits` and
    every stall and noise-floor test; the same holds with the bound's factor at 1, 16 and 256 in place of n+1;
  - the W5 problem: cold `Converged` in 14 iterations at the warm answer (x7 = 0.92221, cost 36.03126077);
  - 400 synthetic hulls x 60 random intents (24,000 calls, warm and cold) at tolerance 1e-12: every call `Converged`,
    max iterations 45, warm and cold agree within 6e-8 on every call (the original: one call off by .078 in the first
    2,400). At the default tolerance warm and cold still differ, by more than .1 in 10,273 of 24,000 calls: that is
    the documented resolution of 1e-6 times the gradient scale, pinned by
    `AnAllocatorShapedProblemIsStartIndependentAtATightTolerance` (spread >= 0.5), and not this cut's subject.
- **B7. Released columns the factor drops.** With the fix, 266 releases in the suite land on a column that
  `SolveFreeSet` marks dependent (p = 0). The next pass is then a stationarity miss with no release, and the
  progress rule stops it, as before. No release in the suite or the 24,000 allocator calls produced a step that moves
  the released column outward. A Lawson-Hanson style rejection guard (re-bind the column, try the next candidate)
  was prototyped and is not needed by any measured case; it is not in the cut.

### Prior art, and which one the solver follows

- The solver is a primal active-set method on the normal equations: solve the free set, step to the first bound
  crossed, release the most violated bound only after a full step lands. That is the outer/inner loop of
  Lawson-Hanson NNLS (1974) generalised to two-sided bounds, which is the shape of Stark-Parker BVLS (1995) and of
  scipy's `lsq_linear(method='bvls')`. It differs in factorisation (Cholesky of `A^T A`, not QR of `A`) and in
  stopping.
- Lawson-Hanson stop on the dual test only (netlib `NNLS`, label 350: `IF (WMAX .le. ZERO)`), and guard roundoff
  per candidate: a column whose new norm is negligible against rounding (`DIFF(UNORM+...)`) or whose new
  coefficient is not positive (`ZTEST .gt. ZERO`) is rejected with `W(J)=ZERO` and the next candidate tried. There is
  no cost-based stop.
- scipy's BVLS (`scipy/optimize/_lsq/bvls.py`, main) measures KKT exactly as CultMath's status doc states
  (`g * on_bound` for bound variables, `|g|` for free ones, max over components) and has two stops: optimality below
  `tol` (status 1) and a relative cost change below `tol` (status 2), reported as different statuses.
- CultMath reported its cost stop as `Converged`, which its status doc defines as "the KKT conditions hold". The fix
  follows Lawson-Hanson: a release is judged on the gradient against its rounding (the role of their `DIFF` test),
  and the cost stop is kept only where no gradient test can decide (a free-set residual at rounding level), which is
  where scipy's status 2 also lives.

### Rejected alternatives

- Remove the progress rule and stop on the gradient alone: B5, 8 failures.
- Report the cost stop as a new status (scipy's status 2): the W5 answer would still be wrong, just labelled, and a
  new enum member is a breaking change for callers that switch on the status.
- Measure progress from the step (the exact model decrease `t(1-t/2)|L^-1 g|^2`) instead of differencing the cost:
  exact, but a noise-level gradient still yields a tiny positive decrease, so it needs the same noise threshold, on a
  quantity further from the KKT test than the gradient itself.

## I. Naming the invalid input

Follow-up `cultlib-gaps:follow_up:bls-invalid-input-field` (from Soul finding
`cut-bls-kkt-tolerance.s1.invalid-input-names-no-field`): every validation failure returns the same `InvalidInput`.

- **I1.** Validation today is three sites in `Solve`: tolerance (`:147-148`), bounds (`:149-151`), and finiteness of
  A and b folded into the `A^T b` build (`:153-161`), which cannot tell A from b.
- **I2.** Precedent: LAPACK reports a bad argument as `INFO = -i`, naming the argument and never its value. .NET
  throws `ArgumentException` with a parameter name, which the solver's no-throw, zero-allocation contract rules out.
- Recommended shape (question `bls-input-error-shape`, option `validate-helper`): a public
  `BoundedLeastSquares.Validate(m, n, a, b, lo, hi, kktRelativeTolerance)` returning a new enum
  `BoundedLeastSquaresInput { Valid, InvalidTolerance, InvalidBounds, NonFiniteMatrix, NonFiniteTarget }`, the first
  failure in that order. `Solve` calls it and returns `InvalidInput` for anything but `Valid`, so validation has
  one owner and the folded finiteness check in the `A^T b` loop is deleted. Additive: `InvalidInput` and every
  existing result stay.

## R. Release 0.4.1

- **R1.** `org.gamecult.cultmath` is at 0.4.0 (`package.json:4`, `CultMath.csproj:13`, tag
  `cultmath-unity-v0.4.0`). `scripts/check-changelog-semver.mjs:97-101`: in 0.y.z a minor bump is the breaking lane;
  additions and fixes take the patch lane (0.2.4 added `erf` as a patch).
- **R2.** Cut 1 changes results only where the old solver stopped after a release whose violation was above both the
  caller's tolerance and the gradient's rounding, that is, where it reported `Converged` in breach of its status doc.
  The default path's golden bits are unchanged (B6). The 0.4.0 precedent (ruling `cultmath-0-4-0-breaking-r2`)
  declared the snoise tie fix Breaking because two valid functions gave different values; here the old value broke
  the documented contract. Question `bls-fix-release-lane` asks which lane; the recommendation is `fixed-patch`
  (0.4.1, `### Fixed`).
- The release procedure is the 0.4.0 cut's (`cultlib-gaps:cut_spec:cut-cultmath-release.r1`): Windows-only DLL rebuild
  on Starfire, tests on Yggdrasil, public-surface diff, Unity import smoke, Soul before the tag.

## Q. Rotation algebra

Ruling `aetheria-release:ruling:solver-in-gamecult-animation`: CultMath gains quaternion algebra, swing-twist and
joint clamps; the aim-chain solve lives in a new package GameCult.Animation. The interface the consumer needs is the
Aetheria map's section "The rig's interface to GameCult.Animation and CultMath" (Aetheria `origin/master`
`30d02719`, `docs/aetheria-release-map.md:7559-7601`).

### Body facts

- **Q1. What exists.** `quaternion` (`src/CultMath/quaternion.cs`, 95 lines): public float fields x, y, z, w;
  `identity`; `LookRotation(float3 forward, float3 up)`; implicit conversions to and from `float4`; scalar `==`.
  `math.normalize(quaternion)` (`math.cs:225-235`) returns identity below length 1e-20. No mul, rotate, conjugate,
  inverse, axis-angle, slerp, swing-twist or clamp exists. `math` is a partial class (`math.cs`,
  `math.Interval.cs`, `math.Affine.cs`, `math.Phacelle.cs`); `math.rotate(float2, float)` exists (`math.cs:240`).
- **Q2. Serialization.** `GameCult.Caching.MessagePack` serialises `quaternion` as the float array [x, y, z, w]
  (`CultMathResolver.cs:170-171`) and as an xyzw object in JSON (`CultMathJson.cs:468`). No other runtime
  (`cultcache-rs`, `-ts`, `-py`) has a quaternion type. The cut adds static functions only: no field, layout or
  formatter changes, so no wire change and no cross-runtime fixture.
- **Q3. Runtimes.** The algebra is C#-only, like `LookRotation`: shaders carry rotations as `float4` (the type's doc),
  the mirror tests map shader functions to C# and never the reverse (`HlslSourceCompatibilityTests.cs:114-117`), and
  the native core (`native/cultmath-core`) is a Voronoi tone helper. The Unity bridge already converts `quaternion`
  (`UnityConversions.cs:16,23`); it needs no change.
- **Q4. An overload trap, probed.** `quaternion` converts implicitly from `float4`, and CultMath has no
  `mul(float4, float4)`. `probe3` declares `mul(quaternion, quaternion)` beside scalar and matrix `mul` overloads and
  calls it with two `float4`: it compiles and returns the quaternion product. HLSL's `mul(vector, vector)` is the
  inner product, and Unity.Mathematics 1.3.3 (the copy in Aetheria's PackageCache) defines `mul(float4 a, float4 b)`
  as such (`matrix.gen.cs:129`), so the same call there is a dot product. The cut therefore adds HLSL's vector
  `mul(float2|3|4, float2|3|4)` (dot) beside the quaternion `mul`, so a `float4` pair keeps its shader meaning.
- **Q5. Unity.Mathematics as reference, not template.** Its signatures for `mul`, `rotate`, `conjugate`, `inverse`,
  `AxisAngle` and `slerp` (`quaternion.cs:99,493,502,630,651,678`) are the consumer's stated convention ("`mul(a, b)`
  applies `b` first"). CultMath's design rule is to state semantics in code and not copy Unity.Mathematics
  (`docs/design.md:465-469`), so the formulas are written from the definitions below.

### Semantics the cut lands

Quaternions are Hamilton, stored xyzw, unit for rotation; angles are radians, right-handed about the axis.

- `math.mul(quaternion a, quaternion b)`: the Hamilton product; `rotate(mul(a, b), v) == rotate(a, rotate(b, v))`.
- `math.rotate(quaternion q, float3 v)`: `t = 2 cross(q.xyz, v); v + q.w t + cross(q.xyz, t)`; q assumed unit.
- `math.conjugate(q)`: (-x, -y, -z, w). `math.inverse(q)`: `conjugate(q) / dot(q, q)`, no guard (zero in, non-finite
  out, as HLSL division).
- `quaternion.AxisAngle(float3 unitAxis, float radians)`: (axis sin(r/2), cos(r/2)); the axis is not normalised.
- `math.slerp(a, b, t)`: shortest arc (negate b when `dot(a, b) < 0`); normalised lerp when the quaternions are
  nearly parallel; t is not clamped.
- `math.swing_twist(q, unitTwistAxis, out swing, out twist)`: twist is q's projection onto the axis, normalised
  (identity when that projection vanishes, a half-turn swing); `swing = mul(q, conjugate(twist))`, so
  `q == mul(swing, twist)` (twist applied first).
- `math.clamp_twist(twist, unitAxis, min, max)`: angle `2 atan2(dot(twist.xyz, axis), twist.w)` wrapped to (-pi, pi];
  inside [min, max] the input is returned unchanged, otherwise `AxisAngle(axis, clamp(angle, min, max))`.
- `math.clamp_hinge(q, unitAxis, min, max)`: `clamp_twist` of q's twist about the axis; the swing is discarded.
- `math.clamp_cone(swing, unitAxis, maxAngle)`: the angle between `rotate(swing, axis)` and `axis`; inside the cone
  the input is returned unchanged, otherwise the rotation by `maxAngle` about `normalize(cross(axis,
  rotate(swing, axis)))` (at a half turn, where that cross product vanishes, about the swing's own axis).
- Preconditions (`min <= max`, unit axes) are documented, not checked, as everywhere in `math`.

### What GameCult.Animation will need from this cut (not mapped here)

- From cut 4: `mul`, `rotate`, `AxisAngle` for `AimChain.Pose` and `EffectorDirection`; `conjugate` or `inverse` for
  frame changes; `swing_twist` and the clamps if its joints ever carry cone or twist limits (the presenter's per-axis
  boxes do not).
- From cut 1: start-independent `BoundedLeastSquares` at a tight tolerance. `AimChainSolver.Solve` is warm-started
  from `current`; without cut 1 its answer would depend on the previous frame exactly as the allocator's did.
- A CultMath release that carries cut 4, which `org.gamecult.animation`'s `package.json` pins. That release is
  mapped with GameCult.Animation, so one Soul-verified release pair ships both.

## Observed, not owned by these cuts

- At the default tolerance the solver's answer on allocator-shaped problems depends on the start by up to the whole
  box (B6). That is documented and pinned (`AnAllocatorShapedProblemIsStartIndependentAtATightTolerance`); a
  consumer that needs one answer passes a tight tolerance, as Aetheria does.
