# Consumer pinning at a breaking land: prior art and internal state

Serves fork `variants:question:unpinned-consumers-at-land` (campaign `variants`, session `self-2026-10-22-eureka-c2a`).
Eyes pass, 2026-10-22. Facts and evidence pointers only; no recommendation.

Question as admitted: Eve.Surface and Brokkr build against an unpinned sibling `..\CultLib` source checkout. C2a (refuse element types with no id) lands on main. Options: pin-then-land, sweep-then-land, land-and-break.

## Sources

| # | Source | URL | Status |
|---|---|---|---|
| S1 | Abseil, About / philosophy | https://abseil.io/about/philosophy | fetched |
| S2 | Abseil, compatibility guidelines | https://abseil.io/about/compatibility | fetched |
| S3 | Software Engineering at Google, ch. 21 Dependency Management | https://abseil.io/resources/swe-book/html/ch21.html | fetched |
| S4 | Software Engineering at Google, ch. 16 Version Control and Branch Management | https://abseil.io/resources/swe-book/html/ch16.html | fetched (does not cover live at head) |
| S5 | Russ Cox, Minimal Version Selection | https://research.swtch.com/vgo-mvs | fetched |
| S6 | Cargo book, Cargo.toml vs Cargo.lock | https://doc.rust-lang.org/cargo/guide/cargo-toml-vs-cargo-lock.html | fetched |
| S7 | npm docs, package-lock.json | https://docs.npmjs.com/cli/v10/configuring-npm/package-lock-json | fetched |
| S8 | Chromium src, dependencies doc (DEPS, gclient, roll-dep) | https://chromium.googlesource.com/chromium/src/+/main/docs/dependencies.md | fetched |
| S9 | Pro Git, Submodules | https://git-scm.com/book/en/v2/Git-Tools-Submodules | fetched |
| S10 | chromium infra dependencies.md | https://chromium.googlesource.com/infra/infra/+/main/docs/dependencies.md | 404, not retrieved |
| L1 | `F:\Projects\Aetheria\Directory.Build.targets`, `Directory.Build.props`, git history | local files | read |
| L2 | `F:\Projects\Aetheria\docs\cultcache-migration-cut.md` (Q6 table, lines ~565-580; pin guard notes ~1900-1925) | local file | read |
| L3 | `F:\Projects\Eve` and `F:\Projects\Brokkr` csprojs and git file lists | local files | read |
| L4 | voidbot `search_history` (two queries), `search_sources` (one query) | MCP | queried |

Anything not in a "fetched" row above is not claimed from the web.

## 1. Established practice: who pays for a breaking upstream change, and when

- **Abseil / live at head (S1, S2).** Recommends users track the latest code. Says it will provide a tool to perform an API refactor for well-behaved code (S1). Requires of downstream: show how you call it, stay within the compatibility guidelines; otherwise fork or use periodic supported branches (about every 6 months, patched 2 years) (S1). Says every change is potentially breaking for someone; breaks are announced in commit messages and LTS notes, and users are asked to report undetected breaks on GitHub (S2). Users should be able to temporarily pin to an earlier commit if a newer change is a problem (S2). S1/S2 do not mention Rosie, the Beyonce rule, or caller-fixing; the fetched pages describe tooling and a feedback channel only.
- **Live at Head as described in SWE at Google ch. 21 (S3).** Providers are expected to test changes against downstream dependents before committing; breaking changes wait until dependents are updated or an automated migration tool exists. Consumers must keep meaningful tests. Stated preconditions: unit tests across dependencies run in CI, a visible reverse-dependency graph, substantial compute, no pinning of dependencies, provider acceptance of the downstream-testing duty. Called "costly but effective" inside Google and "largely unproven at scale." Notes that under SemVer a major bump costs maintainers little and many indirect users a lot. Ch. 16 (S4) does not address live at head; it states large-scale changes across trunk are costly and tooling-supported.
- **Go MVS (S5).** Selects the oldest version satisfying all declared minimums. Publishing a new version changes nobody's build; a consumer gets it only when a requirement names it, directly or through another module. Upgrades are explicit edits to `go.mod` by the top-level module. Compatibility rule: newer versions inside one major version are expected to work where older did; a major version is a different module identity. Cost of a breaking change falls on whoever opts into the new version, at the time they opt in.
- **Cargo (S6).** Git dependency without `rev` takes the default-branch tip at first build and fixes that commit in `Cargo.lock`; changes arrive only on `cargo update`. A `rev` in `Cargo.toml` pins explicitly; the Cargo book calls hand-managed SHAs "tedious and error prone" and prefers the lockfile. Consumer pays at `cargo update` time.
- **npm (S7).** `package-lock.json` records the exact tree; installs stay on it until the lockfile changes and is committed. It is not published with a package. The page gives no timeline for when updates reach a consumer.
- **Chromium DEPS (S8).** `DEPS` stores dependency URLs and commit hashes (often in `vars`). Developers roll with `gclient setdep`, `roll-dep`, or manual `git update-index`; some projects (Skia) are autorolled. The page says manual rolling after an upstream change is good practice to confirm the roll compiles and tests pass. It does not describe revert procedure. Cost is paid by the roller at roll time.
- **Git submodules (S9).** The superproject records one commit SHA (gitlink, mode 160000). Upstream commits never move the pin; it moves when someone updates the checkout and commits the new SHA in the superproject. Collaborators receive the pin via pull and must run `git submodule update`.

## 2. Internal precedent

- **Aetheria pin (L1).** `Directory.Build.props` holds `CultLibRevision` (currently `45c2f4006f5f7dab357426cabd3197e8d0a17f66`) and `CultMathRevision` (`9c1b06cd703bfa44352050e819b0d0659cf5978a`). `Directory.Build.targets` target `VerifyCultLibRevision` (BeforeTargets `ResolveProjectReferences`) errors if the sibling checkout's `git rev-parse HEAD` differs from the pin, or if `git status --porcelain` is non-empty; escape hatch `SkipCultLibRevisionCheck=true`. File is 39 lines; the pin is one pair of properties per pinned package.
- **Why added (L1 git).** Commit `4f4beb0f` (2026-09-14, "Pin CultLib for headless builds"): "Directory.Build.targets carries Delvehold's VerifyCultLibRevision guard, checking for CultMath.csproj: HEAD must equal the pin and the worktree must be clean." Commit `e91ba369` (2026-09-24, "Pin CultLib per package, as Unity's manifest does"): headless build must compile the revisions Unity consumes; a single pin would have compiled six commits of newer CultCache under headless tests while Unity stays on its tag. The Aetheria cut doc (L2, ~line 1905) calls the guard "the only working pin for a sibling checkout." Aetheria's `Packages/manifest.json` pins Unity packages by git tag.
- **Pin guard already in Delvehold (L4/search_sources hit, `Delvehold:Directory.Build.targets`)** and a variant in AetheriaEve (archived, `Aetheria.State.Dependencies.targets`, also checks a Unity package tree hash). Delvehold's guard checks `GameCult.Mesh.csproj` exists, HEAD equals `CultLibRevision`, tree clean. Date of Delvehold's first guard: not determined.
- **Consumer table (L2, Q6 in Aetheria `docs/cultcache-migration-cut.md`).** Aquarium, Ymir, AquaSynth, Mimir, Gjallar: ".NET sibling, unpinned", compile against main at once. Brokkr, Eve, EveUnity, EvePlugins: "Unity package / sibling", pin "various". Delvehold: sibling with revision guard at `334e60f`, 52 behind, "Not until it re-pins." Heimdall: submodule `b6b1d9c`. Rust consumers (Idunn, Epiphany, Odin, Ghostlight, Muninn, Ratatoskr, CodexConnector): Cargo by commit.
- **Operator discussion (L4).** voidbot `search_history` ("pin CultLib revision vs track main for consumers"; "Brokkr Eve sibling CultLib checkout build pin") returned no relevant operator statement on pinning versus tracking; hits were unrelated Discord chatter. Nearest operator words found in the Discord index: Metacrat, 2026-05-09, "Oh, anyone using CultLib will still be paying the blood price, you can count on that" (message 1502671320406687894; context not fetched, so its subject is not confirmed). No operator quote on pinning versus tracking was found in memory files grepped (`C:\Users\Meta\.claude\projects\F--Projects\memory`, pattern pin/track main/unpinned/sibling) beyond a retired Idunn pin line and Odin/Muninn pin-branch coordination notes.
- Aetheria cut doc operator mentions near pins are about content (Schematic-shape drawer, 2026-09-14; Studio grouping, 2026-09-17), not pinning policy.

## 3. Current state of the two consumers (L3)

- **Eve.** `packages/org.gamecult.eve.surface/GameCult.Eve.Surface.csproj:16` `CultLibRoot` defaults to `$(MSBuildThisFileDirectory)..\..\..\CultLib`; lines 20-21 ProjectReference `$(CultLibRoot)\src\GameCult.Caching\...` and `...\GameCult.Mesh\...`. `tests/GameCult.Eve.Surface.Serialization.Tests/*.csproj:19` references `$(CultLibRoot)\src\GameCult.Mesh\...` (and Eve.Surface by relative path). `git ls-files` shows only `packages/org.gamecult.eve.surface/Directory.Build.props` (sets only BaseOutputPath/BaseIntermediateOutputPath/MSBuildProjectExtensionsPath, no revision). No `Directory.Build.targets`, no `global.json`, no `.gitmodules`, no revision property or check. Eve also holds Cargo.toml/Cargo.lock (Rust side, not examined).
- **Brokkr.** `brokkr-command/Brokkr.Command.csproj:7`, `brokkr-contracts.Tests/Brokkr.Contracts.Tests.csproj:7`, `brokkr-contracts/Brokkr.Contracts.csproj:8`: `CultLibRoot` defaults to `..\..\CultLib`. ProjectReferences: Caching, Caching.MessagePack, Mesh, Networking (by project). No `Directory.Build.*`, no `.gitmodules` in `git ls-files`; no revision property or check.
- Neither repo contains `CultLibRevision`, a `VerifyCultLibRevision` target, or any HEAD check. Both build whatever `F:\Projects\CultLib` currently holds (including dirty or other-branch states).
- Fork text claims (not re-verified here): Eve.Surface has 0 `[CultElementId]`; Brokkr `[CultDocument]` files show no object lists by grep.

## Gaps

- S10 (Chromium infra) 404; Chromium autoroller behavior beyond S8 not fetched.
- Rosie / Beyonce rule not found in fetched pages; not cited from memory.
- No operator statement on pin-versus-track found in voidbot or the memory grep.
