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
5. `py-msgpack-default` revision 3 (section P, "Revision 3"): the fix batch (h3) for Soul s2's open findings and
   s1's `store-and-sniff-decide-format` on `hands/py-msgpack-default` at `57f85acd`. Its merge to main waits on the
   Brokkr cut (follow-up `brokkr-msgpack-payloads-r3`).
5a. Not mapped: the Python store takes the cross-process sidecar lock the C# and Rust stores take (follow-up
   `py-ts-store-cross-process-lock`); `migrate_payloads` then holds it too.
6. Not mapped: the C# leaf-vectors revision (`leaf-vectors-next`) gains the P2 map-key rows, so the Python
   key tests read C#-written bytes instead of literals.

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

## P. cultcache-py payload format (py-msgpack-default, revisions 2 and 3)

Imagination pass `imagination-py-msgpack-r2`, session `self-2026-10-12-eureka`, against `hands/py-msgpack-default`
at `7f2c7477`. Probes ran from the session scratchpad and were removed afterwards; their results are recorded here (`jobK` ran
the C# reference on Yggdrasil at `7f2c7477`; `p1`-`p3` ran Python 3.13.15 with msgpack 1.2.2 on Starfire). Revision 1's facts
are in `docs/research/py-msgpack-default/README.md`, and Soul's are in verdict `cut-py-msgpack-default.s1`.

### Body facts

P1. **What decides a payload's format at 7f2c7477.** A search of every `packb`, `unpackb`, `json.loads` and
`json.dumps` call in cultcache-py, cultnet-py and cultmesh-py found five places:
- (a) `PayloadCodec.encode`/`decode`, through `DocumentDefinition.encode_payload`/`decode_payload`.
- (b) `_check_payload_tree` (documents.py:75), which holds the map-key rule. `MESSAGEPACK_PAYLOAD_CODEC.encode`
  does not apply that rule, so a direct call writes `{1: 'a'}` and its own `decode` refuses the bytes.
- (c) `stores.py:217`, `_infer_schema_version_from_payload`. A v1 record whose schema id is missing from the
  catalog is read by unpacking its payload as MessagePack to find a schema stamp.
- (d) `replication.py:186`, the same stamp reader copied into cultnet-py behind a lazy `import msgpack`.
- (e) `stores.py:257`. A legacy envelope whose payload is an inline map or array is repacked as MessagePack.

The other JSON sites are wire messages, schema JSON, CLI output and the cultmesh ready file. None of them is a
document payload.

P2. **The C# reference's object slot, map keys** (`jobK`, `NumericLeafVectorTests.AnySlot` under
`CultDocumentMessagePackSerialization.OptionsFor`, MessagePack `UntrustedData` security):
- Accepted: str, int at every width (negative fixint, u64 max, i64 min), float32, float64, bool, and timestamp
  ext. The reference rewrites each one at its own width, for example `9181ff01` becomes `9181d0ffcc01`.
- Refused: nil (inner `ArgumentNullException`); bin, array and map keys (inner `TypeAccessException`, because
  UntrustedData has no collision-resistant comparer for them).
- Refused as duplicates (inner `ArgumentException`): `{1:1,1:2}`, `{"a":1,"a":2}`, and `{01:1, cc01:2}`, which
  is the same value written at two widths.
- Accepted as two keys: `{1:1, 1.0:2}` and `{1:1, true:2}`. The CLR types differ, so the keys are distinct.
- An `object` value of u64 max or i64 min is accepted and rewritten byte-identical.

P3. **msgpack-python with `strict_map_key=False`** (`p2`, `p3`). Every key kind decodes: nil becomes None, bin
becomes bytes, and a timestamp ext becomes `msgpack.Timestamp`. An array or map key raises TypeError
(unhashable) without a hook. Duplicates are silently last-wins: `{1:1,1:2}`, `{"a":1,"a":2}`, `{01, cc01}`,
`{1, 1.0}` and `{1, true}` each decode to a single key. An `object_pairs_hook` sees every key kind, lists and
dicts included. An exception it raises propagates with `__context__` None. On 100k small maps the hook costs
2.2x the decode time (0.087 s becomes 0.200 s). `packb` raises an untyped `OverflowError` for 2**64 and
-(2**63)-1.

P4. **Hash flooding with non-str keys** (`p1`). This is msgpack-python's reason for `strict_map_key=True`.
Within the 64-bit MessagePack integer range, at most 13 integers share one CPython hash. The full-hash collision
attack on int keys therefore needs unbounded integers, and MessagePack cannot carry them. Keys crafted to share
low hash bits (`k << 43`) took 0.44 s for 200k keys and 1.34 s for 400k. 200k str keys take 0.25 s. The cost is
mildly superlinear, not quadratic. The reference defends itself with UntrustedData's collision-resistant
comparers. Python's dict has no such comparer, and the measured bound makes one unnecessary.

P5. **Schema-stamp recovery.** The C# store deleted its stamp recovery in `a3cc3942` (2026-09-13), so a persisted
schema id that is absent from the embedded catalog is refused ("Persisted schema ... is not present in the
embedded catalog", `CultCache.cs:504-507`). The C# CultNet registry keeps payload-stamp routing for raw wire
records (`CultNetDocumentRegistry.ResolveDescriptorForRawDocument`, `TryResolveDescriptorByPayloadSchema`).
cultcache-py's store copy (P1c) is therefore a divergence from the reference. cultnet-py's copy (P1d) mirrors
the reference. The TS store (`single-file-messagepack-backing-store.ts:336`) also keeps the store copy.

P6. **Legacy envelope arrays.** The C# reference does not read them. TS (`normalizePayload`, line 490) and Python
both repack an inline payload with MessagePack `encode`. A legacy inline payload is a subtree of a MessagePack
document, so it is MessagePack by construction. No JSON-payload writer ever produced one: the 0.3 JSON default
wrote bytes, which land as bin.

P7. **Brokkr** (read-only, `f97d4dc`). The bridge imports cultcache-py from `F:/Projects/CultLib/packages`
(`blender_target.py:19`) and defines 8 types with `define_document_type` (630-637). Merging to that checkout's
main is therefore the switch itself: Soul s1 measured that the next `_open_node` pull refuses the whole Blender
store. The daemon writes JSON into the Blender store and the Unity store with `put_json_document`
(`main.rs:1424`, called at 790-1392 and in tests from 2710). It also has `put_messagepack_document` (1435) and
reads JSON first, then MessagePack (`decode_payload`, 1447). The daemon is Rust and cannot call cultcache-py.

### Authority, and why

- **Payload format.** The definition's `PayloadCodec` owns its bytes: the encoding, and which maps and integers
  the format can carry. On read, `MESSAGEPACK_PAYLOAD_CODEC` refuses key kinds and duplicates through its
  `object_pairs_hook`; that is the only place they are visible, because a dict cannot show a collapsed duplicate
  afterwards. On write, its encode walks the tree first, so a direct `codec.encode` call cannot write bytes
  its own `decode` refuses (Soul's single path).
- **What the definition owns.** `DocumentDefinition.encode_payload`/`decode_payload` own the cross-codec rules
  (depth 256) and the translation of codec faults into the typed refusals, naming the type. `PayloadCodec.map_keys`
  is deleted, and `_check_payload_tree` goes back to checking depth only.
- **The key rule is P2's.** It is not `strict_map_key`'s. Duplicates are refused as the reference refuses them.
- **Residual.** Keys Python holds as one but the reference holds as two (`1` and `1.0`, `1` and `true`) are
  refused, because a dict cannot hold both. The refusal is typed (`duplicate_map_key`) and pinned so a change is
  seen. Matching the reference here would need a decoded map type that is not a dict. No consumer is known to
  write such maps.
- **Prior art for refusing duplicates** (from knowledge, not fetched this pass): CBOR's RFC 8949 section 5.6
  expects decoders to reject duplicate keys; I-JSON (RFC 7493) requires the same of JSON; MessagePack-CSharp's
  `UntrustedData` refuses duplicates and keys it cannot hash safely (P2). msgpack-python's `strict_map_key` exists
  for hash flooding, which P4 bounds.
- **Integer range.** Beyond 64 bits is a format limit, so the MessagePack codec's write walk refuses it with the
  code `integer_out_of_range`. The value is never named, and neither is a map key on its path, because in a
  free-form tree a key is payload data. Entry slots keep their leaf refusals, which name the slot and the field.
  A bigint was never wire-parity data: MessagePack has no integer wider than 64 bits (P3), and the 0.3 JSON
  payloads that could hold one were unreadable by the reference (`py-default-payload-json`).
- **The stamp readers.** P1c is deleted and its record is refused as the reference refuses it (P5). P1d stays as
  cultnet-py's raw-record router, mirroring C#. It reads a stamp and decides no format: the resolved definition's
  codec decodes the payload or refuses it. It loses its lazy import.
- **Legacy envelopes.** P1e is demoted and stays (P6). It extracts the MessagePack bytes the inline subtree
  already is. A definition whose codec is not MessagePack refuses the record like any other foreign record.
- **The default codec.** The `DocumentDefinition` field is its only author. `define_document_type`'s parameter
  defaults to None and forwards only a codec it was given.
- **Refusal text.** The JSON remedy sentence is dropped from the read refusal: it was false for every entry
  definition. Each refusal carries a `code` (`not_one_value`, `map_key_kind`, `duplicate_map_key`,
  `integer_out_of_range`) and names the kind or the fault class. It is raised after the `except` block, so
  `__context__` holds no decoder exception and no payload.

### Migration: what CultLib owns

Revision 1's README procedure (read under JSON, then put each value) was per record and not atomic. Soul s1
stranded a store with it. Revision 2 replaces the procedure with
`migrate_payloads(store, definitions, *, source_codec=JSON_PAYLOAD_CODEC) -> int` in cultcache-py:

1. It pulls every envelope from the store.
2. For each record whose type is among `definitions`, it decodes under that definition with `source_codec` and
   encodes under the definition itself. That is the whole definition path, validation and every refusal included.
3. Any refusal stops the migration before the store is touched. The error names the type, the record key and
   the refusal's code, never the value.
4. Otherwise one `push_all` writes the store whole. `SingleFileMessagePackBackingStore` and
   `JsonLinesBackingStore` each do that as a temp file and a rename.
5. Records of other types keep their bytes and `stored_at`. The return value is the count rewritten.

It never sniffs, and it cannot leave a store half-migrated. Revision 2 also claimed that a second run is refused;
Soul s2 falsified that (P10), and a concurrent writer strands the store (P11). Revision 3 replaces this shape
(see "Revision 3").

Rejected alternatives:
- A sniffing reader that accepts both formats: P1's one-digit payloads are valid in both formats, and r1's map
  forbade readers that try one format and then another.
- A consumer-run loop over `put`: that is the procedure that stranded the store.

### Brokkr landing contract (follow-up `brokkr-msgpack-payloads-r2`)

The merge of `hands/py-msgpack-default` to main waits on three things:
1. Soul holds on the fix batch.
2. A Brokkr branch, Soul-held, is ready in its own campaign. On it, the daemon writes MessagePack
   (`put_messagepack_document`) into both stores. The JSON-first read becomes a read of exactly one MessagePack
   value with a trailing-bytes check, and the bridge's 8 definitions are unchanged. The Blender store is migrated
   by `cultcache_py.migrate_payloads` over those 8 definitions, because those payloads came from cultcache-py's
   old default. From revision 3 the call names a backup path that must not exist (P12), and the original JSON
   store is kept there. The daemon-written JSON receipts in the Unity store are the daemon's own, and it rewrites
   them.
3. An operator window: close Blender and stop sync, Self merges CultLib and Brokkr together, build the daemon,
   run both migrations, then restart. The backup stays until the operator has opened the migrated store; deleting
   it is the operator's step, after which a rerun is no longer guarded (P12).

### Revision 3

Imagination pass `imagination-py-msgpack-r3`, session `self-2026-10-12-eureka`, against `hands/py-msgpack-default`
at `57f85acd`. Python probes ran on Starfire (Python 3.13, msgpack 1.2.2) over a `git archive` of the cultcache-py
and cultnet-py sources at that head, and were removed afterwards. The C# facts are Soul s2's (`jobB`, Yggdrasil),
not rerun here.

P8. **What still decides map keys at 57f85acd.** One search found three MessagePack readers that see document
payload maps. (a) The codec's `_unpack_tree` applies the reference rule through `_checked_map`. (b) The store's
whole-file `msgpack.unpackb(data, raw=False)` (`stores.py:98`) applies msgpack's `strict_map_key=True`. A legacy
envelope file whose inline payload is `{1: 'a'}` is refused with "int is not allowed for map key". Read through
`_unpack_tree` instead, the same file yields the payload `8101a161`, and a v1 snapshot's framing decodes
identically either way. (c) cultnet-py's stamp router (`replication.py:192`) applies `strict_map_key=True`. Read
through the codec, `['x.v1', {1: 'a'}]` and `{'schemaVersion': 'x.v1', 'v': {1: 'a'}}` both route to `x.v1` (C#
routes both, Soul s2 STAMPPROBE). A nil-keyed payload gives `None`, as it does today and in C#. Every other
`unpackb` in cultnet-py and cultmesh-py reads wire messages, cursor files or fixed-slot protocol records, whose
maps are frames rather than payload data. Their bound is follow-up `py-wire-decoders-unbounded`. Unmeasured: C#
reads a map stamp as `IReadOnlyDictionary<string, object>`, so a top-level non-str key may stop C# routing where
Python routes. The resolved definition decides the record either way.

P9. **The two depth counters.** `_check_writable` (documents.py:184) counts every node, scalars included, to
`_PACK_DEPTH_LIMIT = 511`. `_check_payload_tree` counts containers to 256. Commit `eef1cb15` moved the codec
from 256 to 511 so that `test_union_parity.py:650` could build its 257-deep read fixture with
`document.payload_codec.encode(past)`. The fixture needs bytes that are 257 deep, not a codec that writes them:
`msgpack.packb(past, use_bin_type=True)` and `json.dumps(past).encode()` build them, and both writers nest to 511
or further. One container walk, bounded at `MAX_PAYLOAD_DEPTH`, serves both the definition and the codec. It also
ends a cycle, because a cycle reaches 257.

P10. **Rerun.** No byte in a CultCache store records a payload format, and no wire change is wanted for one. A
migration that rewrites the live store in place therefore cannot tell its own output from its input when a
payload is valid in both formats. `b'5'` is the JSON 5 or the MessagePack 53, and Soul s2 saw 53 become 5. No
check on the payload content can separate the two, so the rerun guard has to come from outside the payload.

P11. **Concurrency in the store.** cultcache-py's file stores hold a per-object `threading.RLock` only, so every
`push` is a read-modify-write that loses another process's write. The C# store opens a `.lock` sidecar with
`FileShare.None` (`CultCache.cs:3489-3509`), and cultcache-rs takes an `fs2` lock on its sidecar (`lib.rs:853-859`).
cultcache-ts has none (a search of `packages/cultcache-ts/src` finds no lock). The migration race is one case of a
store that lacks the reference's lock.

P12. **Prior art and the shape chosen** (primary docs fetched 2026-10-12). SQLite `VACUUM INTO`: "the original
database file is unchanged", and the target "must not previously exist, or else it must be an empty file". LMDB
`mdb_env_copy`: the target directory "must already exist and be writable but must otherwise be empty". Realm is
the counter-example to Self's hypothesis. It migrates in place on open, and a rerun is safe because the file
records its schema version, which CultCache's payload format does not (P10). The chosen shape follows `VACUUM
INTO`, with the destination role taken by a mandatory backup:

`migrate_payloads(store, definitions, *, backup, source_codec=JSON_PAYLOAD_CODEC) -> int`

1. Copy the live store's bytes to `backup` with exclusive create (`open(backup, "xb")`). If the backup exists,
   the call is refused before anything is read. Every rerun is refused this way, whatever the payloads hold.
2. Read the records from the backup, the frozen copy, and rewrite the listed ones. Any refusal of any exception
   class raises `CultCacheError` naming the type, the record key and the code or class.
3. Compare the live bytes with the copied bytes. A difference means a writer ran during the migration, and the
   call refuses and says to stop every writer.
4. Replace the live file in one `_replace_all` (temp file and rename).
5. On any failure after step 1, the backup is removed and the live store is unchanged. A count of zero also
   removes it.

Probe (`probe2`, this shape over the 57f85acd sources). The first run over `b'53'`, `b'5'` and an unlisted type
gives 53 and 5, keeps the unlisted bytes, and keeps the JSON in the backup. A second identical call raises
`FileExistsError` and leaves the live bytes unchanged. A push between the read and the swap (the s2 interleave)
is refused, with the backup removed and the live store holding the writer's JSON record. A validate that raises
`AssertionError` is named by class and the backup is removed.

What remains and is stated: the window between the compare and the rename, closed by the lock in `5a`. The
precondition is to stop every writer, as it is for SQLite's offline tools. If someone deletes the backup and then
reruns the migration on its own output, P10 recurs. The README says to delete the backup only after the
migrated store has been opened, and never to rerun after that.

P13. **Float32 keys.** `Float32(0.1).value` is `0.1`, so a `Float32` key is not rounded until it is packed.
Decoded, it is the float32-rounded value. Writing `{1, F32(1.0)}`, `{True, F32(1.0)}`, `{1.5, F32(1.5)}` or
`{0, F32(-0.0)}` produces bytes that the codec's own decode refuses as `duplicate_map_key`. `{0.1, F32(0.1)}` and
two NaN keys read back as two keys. The rule that matches decode exactly compares each key's decoded image (a
`Float32` key's image is `struct.unpack('>f', struct.pack('>f', v))[0]`) and refuses a collision on write as
`duplicate_map_key`. The reference reads all four collision maps as two keys (Soul s2 KEYPROBE). Python cannot,
for the same reason it cannot read `{1, 1.0}`, which is the residual already recorded. Refusing on write keeps
Python inside what it can read back, and it is the write half of that residual.

## Observed, not owned by these cuts

- At the default tolerance the solver's answer on allocator-shaped problems depends on the start by up to the whole
  box (B6). That is documented and pinned (`AnAllocatorShapedProblemIsStartIndependentAtATightTolerance`); a
  consumer that needs one answer passes a tight tolerance, as Aetheria does.
