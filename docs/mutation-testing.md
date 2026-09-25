# Mutation Testing

CultLib measures its C# test suites with Stryker.NET, pinned as a repo-local
tool in `dotnet-tools.json`. Coverage is added project by project; a project
with no `stryker-config.json` next to its test project has not been scoped
yet (see "Follow-ups" below for what's outstanding).

## Running it

Restore the tool once per checkout:

```powershell
dotnet tool restore
```

Run a full mutation pass from a covered test project's directory, for
example CultMath:

```powershell
cd packages/cultmath/tests/CultMath.Tests
dotnet stryker -t mtp
```

`-t mtp` selects Stryker's Microsoft.Testing.Platform runner. It is required
for test projects on `xunit.v3` (CultMath.Tests is one): Stryker's default
`vstest` runner's coverage capture does not work against xunit.v3's native
test host and the run stalls retrying test sessions indefinitely. Projects
still on `xunit` v2 with `xunit.runner.visualstudio` can use the default
`vstest` runner.

For a single cut, scope the run to what changed instead of the whole
project:

```powershell
dotnet stryker -t mtp --since:<base-commit-or-branch>
```

Use a full commit SHA for `--since`, not an abbreviation: `git rev-parse
<short-sha>` first, then pass the full result. Stryker resolves `--since`
through the same git plumbing a short SHA can resolve ambiguously (or fail to
resolve at all) depending on the checkout's object database, and a
misresolved base silently changes what "since" means instead of erroring.

In a container, also set `git config --global --add safe.directory '*'`
before running Stryker (or any other command that shells out to `git`
against this checkout) - git refuses to operate on a repository owned by a
different user by default, which is the common case for a bind-mounted
checkout in a container.

Reports land in `StrykerOutput/` next to the test project (git-ignored).
Open the `reports/mutation-report.html` file for a browsable view, or read
`reports/mutation-report.json` for a scriptable one.

## Triage rules

The mutation score is not a gate (`break` threshold is 0). Every surviving
mutant is a finding about the tests, not a build failure, and gets triaged
into one of:

- **missing behavioural test** — name the test that should exist and what
  branch, edge case, or component it would exercise.
- **degenerate fixture** — the code path is exercised, but the fixture's
  values make two different mutants produce the same observable result (a
  zero component that erases a `+`/`-` distinction, an already-normalized
  input that erases a `*`/`/` distinction, all-equal or all-same-sign inputs
  that erase a boundary or `&&`/`||` distinction). Fix the fixture, don't
  invent a new one that only kills that one mutant.
- **equivalent** — the mutant cannot change behaviour. Float-threshold
  boundary flips (`>` to `>=`) are equivalent by default. A reasoned
  equivalence claim (for example, `>>` and `>>>` on an already-unsigned
  operand) needs its one-line reason recorded, not assumed.

Do not write a hand-rolled mutation suite or anchor test as a substitute.
Where the tool can't reach (native/shader/editor code), the defence is a
behavioural test at the layer the rule is decided, not a committed mutation
harness.

## Known tool caveat

Stryker 5.0.0's `mtp` runner is documented as preview. Its per-test coverage
capture was found to report false "Survived" verdicts specifically for
mutations inside `static readonly` field initializers that a test does
cover (confirmed by hand-mutating `bool3.@false` and `bool4.@true` and
running the full suite directly with `dotnet test`, both of which fail
against the mutation that Stryker reported as surviving). Treat a survivor
inside a `static readonly` field initializer as unproven until confirmed
with a direct `dotnet test` run against the same mutation; do not triage it
as a missing test without that check.

A survivor that looks like it must be covered is unproven until confirmed
with a direct `dotnet test` run against the same hand-applied mutation - but
confirm what the mutant actually is before drawing a tooling conclusion from
it. An earlier pass here reported three survivors in
`CultGeometrySurfaceNets.TryGetOtherEndpoint`'s per-axis `if (x + 1 >=
sizeX) return false;` guards and initially triaged them as a `vstest`
coverage false-survivor (the same shape as the `mtp`/`static readonly`
caveat above). That triage was wrong: the actual mutant Stryker reports
there is `return false` -> `return true` inside the guard, which is
equivalent, not a coverage miss. Returning `true` from
`TryGetOtherEndpoint` still leaves the out `other` coordinate equal to the
current coordinate (see the method: `other` is cloned from `coord` and only
one component is incremented when the method returns `true`), so the caller
compares a sample against itself, the inside/outside flags always match,
and the crossing is skipped exactly as it would have been had the method
correctly returned `false`. No test can distinguish the two branches because
there is nothing to distinguish - it is a legitimate equivalent mutant, not
evidence of a `vstest` false survivor.

`CultGeometrySurfaceNets.Extract`'s `if (inside == (OrientationSign[axis] <
0))` (the sole authority for quad winding after the geometric cross-product
fallback was deleted) has three mutants on that line: `==` -> `!=`, and `< 0`
negated to `> 0` or widened to `<= 0`. The first two, `inside != (OrientationSign[axis]
< 0)` and `OrientationSign[axis] > 0`, are both reported `Killed`. The third,
`OrientationSign[axis] <= 0`, survives, but it is an equivalent mutant, not a
coverage gap: `OrientationSign` is the fixed table `{ 1, -1, 1 }` and never
holds `0`, so `< 0` and `<= 0` agree on every value the table can actually
produce. There is nothing for a test to distinguish, the same shape as the
`TryGetOtherEndpoint` equivalent mutant above.

No `vstest` coverage-based false survivor has actually been confirmed for
GameCult.Geometry. Every survivor triaged here so far has turned out to be a
genuine equivalent mutant once hand-mutated and checked directly against
`dotnet test`; treat any survivor that looks like it must obviously be
covered as unproven until confirmed the same way, but do not cite this
project as precedent for the `vstest` caveat itself - that caveat is
`mtp`-runner-specific and documented above for `static readonly` field
initializers, and GameCult.Geometry has never actually hit it.

## Follow-ups

CultMath.Tests and GameCult.Geometry.Tests are the only projects currently
scoped. GameCult.Geometry's `stryker-config.json` has no `test-runner`
entry: the project is NUnit 4 with `NUnit3TestAdapter` and
`Microsoft.NET.Test.Sdk`, so Stryker's default `vstest` runner applies
directly (no `mtp` workaround needed, unlike CultMath.Tests's xunit.v3
suite).

GameCult.Geometry has only ever been run `--since:`-scoped to the cut that
added surface nets; a full-project baseline (running mutation against all of
`CultGeometryIsoSurface`, `CultGeometryDocuments`, and
`CultGeometryPrimitives`, not just the diff) is a recorded follow-up, not
something either cut did.

That first `--since` pass did not actually stay diff-scoped: adding
`stryker-config.json` itself in the same cut that first used it made Stryker
log "391 mutants will be tested because: Non-CSharp files in test project
were changed" and mutate the whole `mutate` glob (all four existing
GameCult.Geometry source files), not just the new surface-nets files. A cut
that changes a non-C# file inside the test project (a fixture, this config
file, anything under `Fixtures/`) loses `--since` line-level scoping for that
run; a future cut whose diff is C#-only, against a base commit that already
has `stryker-config.json`, should scope correctly.

`GameCult.Caching`, `GameCult.Mesh` (+ `.Quic`, `.Quic.Native`),
`GameCult.Networking` (+ `.WebSockets`), and the non-.NET runtimes
(`cultcache-rs/py/ts`, `cultmesh-*`, `cultnet-*`) each still need their own
`stryker-config.json` (or ecosystem-equivalent tool: `cargo-mutants` for
Rust, `StrykerJS` for TypeScript, `mutmut` for Python) and a baseline pass.
None of that is wired into CI yet.
