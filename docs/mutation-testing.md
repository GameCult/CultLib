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

## Follow-ups

CultMath.Tests is the only project currently scoped. `GameCult.Caching`,
`GameCult.Geometry`, `GameCult.Mesh` (+ `.Quic`, `.Quic.Native`),
`GameCult.Networking` (+ `.WebSockets`), and the non-.NET runtimes
(`cultcache-rs/py/ts`, `cultmesh-*`, `cultnet-*`) each need their own
`stryker-config.json` (or ecosystem-equivalent tool: `cargo-mutants` for
Rust, `StrykerJS` for TypeScript, `mutmut` for Python) and a baseline pass.
None of that is wired into CI yet.
