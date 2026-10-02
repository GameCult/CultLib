import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import {
  BREAKING_HEADING,
  classifyBump,
  evaluateRelease,
  hasBreakingSection,
  nextVersions,
  parseVersion,
  readApiCompatRun,
} from "./check-changelog-semver.mjs";

const checkerPath = fileURLToPath(new URL("./check-changelog-semver.mjs", import.meta.url));

function changelogFor(version, { breaking } = {}) {
  return [
    "# Changelog",
    "",
    "All notable changes to this package are documented in this file.",
    "",
    `## [${version}]`,
    "",
    "### Changed",
    "",
    "- something changed",
    "",
    ...(breaking ? [BREAKING_HEADING, "", "- something public was removed", ""] : []),
  ].join("\n");
}

test("parseVersion rejects non-semver strings", () => {
  assert.throws(() => parseVersion("1.0"), /not a MAJOR.MINOR.PATCH version/);
  assert.throws(() => parseVersion("v1.0.0"), /not a MAJOR.MINOR.PATCH version/);
});

test("nextVersions enumerates exactly the three legitimate next versions", () => {
  const prev = parseVersion("1.2.3");
  assert.deepEqual(nextVersions(prev), {
    major: { major: 2, minor: 0, patch: 0 },
    minor: { major: 1, minor: 3, patch: 0 },
    patch: { major: 1, minor: 2, patch: 4 },
  });
});

test("classifyBump identifies each lane and rejects everything else", () => {
  const prev = parseVersion("1.2.3");
  assert.equal(classifyBump(prev, parseVersion("2.0.0")), "major");
  assert.equal(classifyBump(prev, parseVersion("1.3.0")), "minor");
  assert.equal(classifyBump(prev, parseVersion("1.2.4")), "patch");
  assert.deepEqual(classifyBump(prev, parseVersion("1.2.3")), { invalid: "no-op" });
  assert.deepEqual(classifyBump(prev, parseVersion("1.2.2")), { invalid: "reverse" });
  assert.deepEqual(classifyBump(prev, parseVersion("1.2.5")), { invalid: "skip" });
  assert.deepEqual(classifyBump(prev, parseVersion("1.4.0")), { invalid: "skip" });
  assert.deepEqual(classifyBump(prev, parseVersion("3.0.0")), { invalid: "skip" });
});

test("hasBreakingSection scopes to the named version's section only", () => {
  const text = [
    "# Changelog",
    "",
    "## [1.0.60]",
    "",
    "### Breaking",
    "",
    "- removed CultInspectorAssetPath",
    "",
    "## [1.0.59]",
    "",
    "### Added",
    "",
    "- something older, not breaking",
    "",
  ].join("\n");
  assert.equal(hasBreakingSection(text, "1.0.60"), true);
  assert.equal(hasBreakingSection(text, "1.0.59"), false);
  assert.throws(() => hasBreakingSection(text, "0.9.0"), /no "## \[0\.9\.0\]" entry/);
});

// Table-driven per the operator's required cases (spec: breaking+major=pass,
// breaking+patch=fail, breaking+minor=fail, non-breaking+minor=pass,
// 0.y.z breaking+minor=pass, skipped version=fail, downgrade=fail).
const cases = [
  {
    name: "breaking + major = pass",
    previousVersion: "1.0.60",
    version: "2.0.0",
    breaking: true,
    ok: true,
  },
  {
    name: "breaking + patch = fail",
    previousVersion: "1.0.60",
    version: "1.0.61",
    breaking: true,
    ok: false,
  },
  {
    name: "breaking + minor = fail",
    previousVersion: "1.0.60",
    version: "1.1.0",
    breaking: true,
    ok: false,
  },
  {
    name: "non-breaking + minor = pass",
    previousVersion: "1.0.60",
    version: "1.1.0",
    breaking: false,
    ok: true,
  },
  {
    name: "0.y.z breaking + minor = pass",
    previousVersion: "0.2.3",
    version: "0.3.0",
    breaking: true,
    ok: true,
  },
  {
    name: "0.y.z breaking + patch = fail",
    previousVersion: "0.2.3",
    version: "0.2.4",
    breaking: true,
    ok: false,
  },
  {
    name: "skipped version = fail",
    previousVersion: "1.0.60",
    version: "1.0.62",
    breaking: false,
    ok: false,
  },
  {
    name: "downgrade = fail",
    previousVersion: "1.0.60",
    version: "1.0.59",
    breaking: false,
    ok: false,
  },
  {
    name: "first release has nothing to compare against but still needs a changelog entry",
    previousVersion: null,
    version: "0.1.0",
    breaking: false,
    ok: true,
  },
];

for (const testCase of cases) {
  test(`evaluateRelease: ${testCase.name}`, () => {
    const result = evaluateRelease({
      packageName: "test-package",
      changelogText: changelogFor(testCase.version, { breaking: testCase.breaking }),
      version: testCase.version,
      previousVersion: testCase.previousVersion,
    });
    assert.equal(result.ok, testCase.ok, result.reason);
  });
}

test("evaluateRelease fails loudly when the version has no changelog entry at all", () => {
  const result = evaluateRelease({
    packageName: "test-package",
    changelogText: changelogFor("1.0.0"),
    version: "1.2.0",
    previousVersion: "1.0.0",
  });
  assert.equal(result.ok, false);
  assert.match(result.reason, /no "## \[1\.2\.0\]" entry/);
});

test("evaluateRelease names package, previous version, new version, and the breaking heading on failure", () => {
  const result = evaluateRelease({
    packageName: "org.gamecult.cultlib",
    changelogText: changelogFor("1.0.61", { breaking: true }),
    version: "1.0.61",
    previousVersion: "1.0.60",
  });
  assert.equal(result.ok, false);
  assert.match(result.reason, /org\.gamecult\.cultlib/);
  assert.match(result.reason, /1\.0\.60/);
  assert.match(result.reason, /1\.0\.61/);
  assert.match(result.reason, new RegExp(BREAKING_HEADING.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
});

// --- CLI-level integration tests: exercise the git-backed previous-version
// resolution and the "don't re-litigate an already-tagged release" skip,
// which the pure evaluateRelease() tests above cannot reach. ---

function withTempGitRepo(fn) {
  const dir = mkdtempSync(join(tmpdir(), "cultlib-semver-check-"));
  try {
    execFileSync("git", ["init", "-q"], { cwd: dir });
    execFileSync("git", ["config", "user.email", "test@example.com"], { cwd: dir });
    execFileSync("git", ["config", "user.name", "Test"], { cwd: dir });
    fn(dir);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

// Runs the checker on the repository at `cwd` (--cwd). The process itself starts in
// `processCwd`, by default that same repository, never the one that holds the checker:
// the release scripts do not run it from there either.
function runChecker(cwd, args, { env, script = checkerPath, processCwd = cwd } = {}) {
  return execFileSync(process.execPath, [script, ...args, "--cwd", cwd], {
    cwd: processCwd,
    env,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
}

function runCheckerExpectFailure(cwd, args, { status = 1, ...options } = {}) {
  try {
    runChecker(cwd, args, options);
    throw new Error("expected the checker to exit non-zero");
  } catch (err) {
    assert.equal(err.status, status);
    return err.stdout + err.stderr;
  }
}

test("CLI: resolves the previous version from git tags and passes a valid bump", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.1.0", { breaking: false }));
    const output = runChecker(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.1.0", "--tag-prefix", "widget"]);
    assert.match(output, /1\.1\.0 is a minor bump over 1\.0\.0/);
  });
});

test("CLI: fails when the changelog claims breaking under too small a bump", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.1", { breaking: true }));
    const output = runCheckerExpectFailure(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.1", "--tag-prefix", "widget"]);
    assert.match(output, /needs a major bump/);
  });
});

test("CLI: a tag for the version being released does not skip the check", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.1"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.1", { breaking: true }));
    const output = runCheckerExpectFailure(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.1", "--tag-prefix", "widget"]);
    assert.match(output, /needs a major bump/);
  });
});

test("CLI: no previous tag, or tags that cannot be read, refuse unless the first release is declared", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "other-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.0"));
    const args = ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.0", "--tag-prefix", "widget"];
    assert.match(runCheckerExpectFailure(dir, args), /nothing to compare against; if this is the package's first release, declare it with --first-release/);
    assert.match(runChecker(dir, [...args, "--first-release"]), /first release \(1\.0\.0\)/);
    // A flag takes no value: the option after it is still read.
    assert.match(runChecker(dir, ["--first-release", ...args]), /first release/);
  });
  const notARepo = mkdtempSync(join(tmpdir(), "cultlib-semver-norepo-"));
  try {
    const changelogPath = join(notARepo, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.0"));
    const args = ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.0", "--tag-prefix", "widget"];
    const refused = runCheckerExpectFailure(notARepo, args);
    assert.match(refused, /tags of --cwd could not be read .*; if this is the package's first release, declare it with --first-release/);
    assert.doesNotMatch(refused, /fatal/); // git's own complaint is not forwarded
    assert.match(runChecker(notARepo, [...args, "--first-release"]), /first release/);
  } finally {
    rmSync(notARepo, { recursive: true, force: true });
  }
});

test("CLI: declaring a first release while an earlier tag exists is refused", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.1.0"));
    const output = runCheckerExpectFailure(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.1.0", "--tag-prefix", "widget", "--first-release"]);
    assert.match(output, /--first-release was declared but widget-v1\.0\.0 already exists/);
  });
});

// --- Measured public API. The fixtures are real assemblies, built here with
// the SDK and compared by the pinned ApiCompat tool through the CLI, so every
// rule is observed where the release scripts observe it. ---

// Built fixtures are cached by content under the temp directory, so reruns (and a
// mutation tool's many reruns) do not rebuild the same tiny assemblies.
const fixtureRoot = join(tmpdir(), "cultlib-semver-fixtures");

// Builds `source` into <assemblyName>.dll (netstandard2.1, like the shipped
// assemblies) once per distinct (name, source) and returns the dll's path.
function buildAssembly(assemblyName, source, references = []) {
  const dir = join(fixtureRoot, createHash("sha256").update(`${assemblyName}
${source}
${references.join("|")}`).digest("hex").slice(0, 16));
  const dll = join(dir, "out", `${assemblyName}.dll`);
  if (!existsSync(dll)) {
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, "Fixture.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework>` +
        `<AssemblyName>${assemblyName}</AssemblyName><Deterministic>true</Deterministic></PropertyGroup>` +
        `<ItemGroup>${references.map((path) => `<Reference Include="${basename(path, ".dll")}"><HintPath>${path}</HintPath></Reference>`).join("")}</ItemGroup></Project>`,
    );
    writeFileSync(join(dir, "Source.cs"), source);
    execFileSync("dotnet", ["build", "-c", "Release", "-o", "out", "--nologo", "-v", "q"], { cwd: dir, stdio: "pipe" });
  }
  return dll;
}

const PLUGINS = "unity/widget/Runtime/Plugins";

// A repo whose tag widget-v<previous> tracks `baseline` ({file name: dll path})
// under PLUGINS, and whose working tree then holds `afterTag` instead.
function withReleasedBaseline({ baseline, afterTag = baseline, previous = "1.0.0" }, fn) {
  withTempGitRepo((dir) => {
    mkdirSync(join(dir, PLUGINS), { recursive: true });
    for (const [name, path] of Object.entries(baseline)) copyFileSync(path, join(dir, PLUGINS, name));
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", `widget-v${previous}`], { cwd: dir });
    if (afterTag !== baseline) {
      for (const name of Object.keys(baseline)) rmSync(join(dir, PLUGINS, name));
      for (const [name, path] of Object.entries(afterTag)) copyFileSync(path, join(dir, PLUGINS, name));
      execFileSync("git", ["add", "-A", "."], { cwd: dir });
      execFileSync("git", ["commit", "-q", "-m", "after the release"], { cwd: dir });
    }
    fn(dir);
  });
}

function measuredArgs(dir, version, builtPaths, { breaking = false, news = [], refs = [] } = {}) {
  const changelogPath = join(dir, "CHANGELOG.md");
  writeFileSync(changelogPath, changelogFor(version, { breaking }));
  return [
    "--package", "widget", "--changelog", changelogPath, "--version", version, "--tag-prefix", "widget",
    "--api-baseline-path", PLUGINS, ...builtPaths.flatMap((path) => ["--api-built", path]),
    ...news.flatMap((name) => ["--api-new", name]), ...refs.flatMap((refDir) => ["--api-refs", refDir]),
  ];
}

const WITH_GONE = "namespace N { public class C { public void Keep() {} public void Gone() {} } }";
const WITHOUT_GONE = "namespace N { public class C { public void Keep() {} } }";
const WITH_ADDED = "namespace N { public class C { public void Keep() {} public void Gone() {} public void Added() {} } }";

const oldWidget = () => buildAssembly("GameCult.Widget", WITH_GONE);
const trimmedWidget = () => buildAssembly("GameCult.Widget", WITHOUT_GONE);

test("measured: a removed public member without ### Breaking is refused and named", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]));
    assert.match(output, /Gone/);
    assert.match(output, /declare them under "### Breaking"/);
    assert.match(output, /breaks 1 public API member/);
  });
});

test("measured: a removed member with the heading needs a major bump after 1.0 and a minor bump before it", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const tooSmall = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()], { breaking: true }));
    assert.match(tooSmall, /needs a major bump/);
    const ok = runChecker(dir, measuredArgs(dir, "2.0.0", [trimmedWidget()], { breaking: true }));
    assert.match(ok, /2\.0\.0 is a major bump/);
  });
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() }, previous: "0.4.0" }, (dir) => {
    const patch = runCheckerExpectFailure(dir, measuredArgs(dir, "0.4.1", [trimmedWidget()], { breaking: true }));
    assert.match(patch, /at least a minor bump/);
    const ok = runChecker(dir, measuredArgs(dir, "0.5.0", [trimmedWidget()], { breaking: true }));
    assert.match(ok, /0\.5\.0 is a minor bump/);
  });
});

test("measured: additions alone pass under a patch bump with no heading", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runChecker(dir, measuredArgs(dir, "1.0.1", [buildAssembly("GameCult.Widget", WITH_ADDED)]));
    assert.match(output, /1\.0\.1 is a patch bump over 1\.0\.0, no breaking section/);
  });
});

test("measured: a declared break with nothing measured still passes", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runChecker(dir, measuredArgs(dir, "2.0.0", [oldWidget()], { breaking: true }));
    assert.match(output, /2\.0\.0 is a major bump over 1\.0\.0 and has a "### Breaking" section, which agree/);
  });
});

test("measured: a removed GameCult.* assembly is a break, a declared new assembly and a removed third-party one are not", () => {
  const other = buildAssembly("GameCult.Other", "namespace O { public class D {} }");
  const thirdParty = buildAssembly("Vendor.Lib", "namespace V { public class E {} }");
  const baseline = { "GameCult.Widget.dll": oldWidget(), "GameCult.Other.dll": other, "Vendor.Lib.dll": thirdParty };
  withReleasedBaseline({ baseline }, (dir) => {
    const refused = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [oldWidget()]));
    assert.match(refused, /GameCult\.Other\.dll exists at widget-v1\.0\.0 but is no longer built/);
    assert.doesNotMatch(refused, /Vendor\.Lib/);
    assert.match(refused, /breaks 1 public API member/);
    const brandNew = buildAssembly("GameCult.Brand", "namespace B { public class F {} }");
    const ok = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget(), other, brandNew], { news: ["GameCult.Brand.dll"] }));
    assert.match(ok, /1\.0\.1 is a patch bump/);
  });
});

test("measured: a tool failure with no diagnostic fails closed", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const notAnAssembly = join(dir, "GameCult.Widget.dll");
    writeFileSync(notAnAssembly, "not an assembly");
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [notAnAssembly]));
    assert.match(output, /could not be measured/);
    assert.match(output, /without measuring/);
  });
});

test("readApiCompatRun: diagnostics are breaks, an empty failure is a failure, an empty success is clean", () => {
  const breaks = readApiCompatRun({ status: 1, output: "API compatibility errors between a (left) and b (right):\nCP0002: Member 'void C.M()' exists on a but not on b\n" });
  assert.deepEqual(breaks, { breaks: ["CP0002: Member 'void C.M()' exists on a but not on b"] });
  assert.match(readApiCompatRun({ status: 1, output: "Unhandled exception: boom" }).failure, /exited 1 without measuring: Unhandled exception: boom/);
  assert.deepEqual(readApiCompatRun({ status: 0, output: "" }), { breaks: [] });
  assert.deepEqual(readApiCompatRun({ status: 0, output: "Could not resolve reference 'netstandard.dll'\n" }), { breaks: [] });
});

test("measured: removed inherited members are caught", () => {
  // The shipped assemblies are netstandard2.1 and ApiCompat cannot resolve netstandard.dll
  // for them; both ways an inherited member can disappear are still reported.
  const viaNetstandardBase = buildAssembly("GameCult.Widget", "namespace N { public class C : System.Collections.Generic.List<int> { } }");
  const baseDropped = buildAssembly("GameCult.Widget", "namespace N { public class C { } }");
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": viaNetstandardBase } }, (dir) => {
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [baseDropped]));
    assert.match(output, /does not inherit from base type 'System.Collections.Generic.List<int>'/);
  });
  const inheritsInh = buildAssembly("GameCult.Widget", "namespace N { public class B { public void Inh() {} } public class C : B { } }");
  const baseLostInh = buildAssembly("GameCult.Widget", "namespace N { public class B { } public class C : B { } }");
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": inheritsInh } }, (dir) => {
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [baseLostInh]));
    assert.match(output, /N.B.Inh()/);
  });
});

test("measured: netstandard reference assemblies keep non-breaking changes to netstandard-derived types clean", () => {
  const pass = (baseline, built) =>
    withReleasedBaseline({ baseline: { "GameCult.Widget.dll": baseline } }, (dir) => {
      assert.match(runChecker(dir, measuredArgs(dir, "1.0.1", [built])), /1\.0\.1 is a patch bump/);
    });
  // Inserting a class between the type and its netstandard base keeps every old base in the chain.
  pass(
    buildAssembly("GameCult.Widget", "namespace N { public class C : System.Exception { } }"),
    buildAssembly("GameCult.Widget", "namespace N { public class C : System.ArgumentException { } }"),
  );
  // Dropping a member that hid a netstandard member leaves the inherited one visible.
  pass(
    buildAssembly("GameCult.Widget", 'namespace N { public class C : System.Exception { public new string Message => "m"; } }'),
    buildAssembly("GameCult.Widget", "namespace N { public class C : System.Exception { } }"),
  );
});

test("measured: a base type moved to a subclass in another assembly resolves through --api-refs and the baseline directory", () => {
  const oldA = buildAssembly("Vendor.A", "namespace A { public class Base { public void Inh() {} } }");
  const newA = buildAssembly("Vendor.A", "namespace A { public class Base { public void Inh() {} } public class Mid : Base { } }");
  const oldB = buildAssembly("GameCult.Widget", "namespace B { public class D : A.Base { } }", [oldA]);
  const newB = buildAssembly("GameCult.Widget", "namespace B { public class D : A.Mid { } }", [newA]);
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldB, "Vendor.A.dll": oldA } }, (dir) => {
    const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-refs-"));
    try {
      mkdirSync(join(scratch, "built"));
      mkdirSync(join(scratch, "refs"));
      copyFileSync(newB, join(scratch, "built", "GameCult.Widget.dll"));
      copyFileSync(newA, join(scratch, "refs", "Vendor.A.dll"));
      const built = join(scratch, "built", "GameCult.Widget.dll");
      // The new base assembly is reachable only through --api-refs: the checker must pass it on.
      assert.match(runChecker(dir, measuredArgs(dir, "1.0.1", [built], { refs: [join(scratch, "refs")] })), /1\.0\.1 is a patch bump/);
      assert.match(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [built])), /CP0007/);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });
});

test("measured: a local tag of the released version does not skip the measurement", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    execFileSync("git", ["tag", "widget-v1.1.0"], { cwd: dir });
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]));
    assert.match(output, /Gone/);
  });
});

test("measured: the baseline is the tag's blob, not the working tree's DLL", () => {
  withReleasedBaseline(
    { baseline: { "GameCult.Widget.dll": oldWidget() }, afterTag: { "GameCult.Widget.dll": trimmedWidget() } },
    (dir) => {
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]));
      assert.match(output, /Gone/);
    },
  );
});

test("measured: a declared first release measures nothing", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    const junk = join(dir, "junk.dll");
    writeFileSync(junk, "junk");
    const output = runChecker(dir, [...measuredArgs(dir, "1.0.0", [junk]), "--first-release"]);
    assert.match(output, /first release/);
    assert.doesNotMatch(output, /public API measured/);
  });
});

test("measured: measuring nothing refuses, naming what is missing", () => {
  const fail = (dir, args) => runCheckerExpectFailure(dir, args);
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    // a baseline path that is wrong or moved
    const moved = measuredArgs(dir, "1.1.0", [trimmedWidget()]);
    moved[moved.indexOf("--api-baseline-path") + 1] = "unity/moved/Runtime/Plugins";
    assert.match(fail(dir, moved), /widget-v1\.0\.0 tracks no assembly under unity\/moved\/Runtime\/Plugins: the baseline path is missing or moved/);
    // a shipped DLL the tag does not track
    const fresh = buildAssembly("GameCult.Fresh", "namespace F { public class G {} }");
    const output = fail(dir, measuredArgs(dir, "1.1.0", [oldWidget(), fresh]));
    assert.match(output, /GameCult\.Fresh\.dll is built but widget-v1\.0\.0 does not track it under .*; if it is a new assembly, declare it with --api-new GameCult\.Fresh\.dll/);
    // declaring an assembly new that the tag already tracks is a wrong declaration
    const wrong = fail(dir, measuredArgs(dir, "1.1.0", [oldWidget()], { news: ["GameCult.Widget.dll"] }));
    assert.match(wrong, /GameCult\.Widget\.dll is declared new but widget-v1\.0\.0 already tracks it/);
  });
  // a previous tag that does not track the DLLs at all
  withTempGitRepo((dir) => {
    mkdirSync(join(dir, PLUGINS), { recursive: true });
    writeFileSync(join(dir, PLUGINS, "README.txt"), "no dlls yet");
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    assert.match(fail(dir, measuredArgs(dir, "1.1.0", [oldWidget()])), /does not track the DLLs/);
  });
});

test("measured: a passing release says that unmeasured changes still need declaring", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget()]));
    assert.match(output, /public API measured; changes ApiCompat cannot see \(see "Unmeasured changes" in docs\/semver-policy\.md\) must still be declared under "### Breaking"/);
  });
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.1"));
    const unmeasured = runChecker(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.1", "--tag-prefix", "widget"]);
    assert.doesNotMatch(unmeasured, /public API measured/);
  });
});

test("evaluateRelease: measured breaks count as breaking for the lane, declared or not", () => {
  const evaluate = (version, previousVersion, breaking, measuredBreaks) =>
    evaluateRelease({ packageName: "p", changelogText: changelogFor(version, { breaking }), version, previousVersion, measuredBreaks });
  assert.equal(evaluate("1.1.0", "1.0.0", false, ["CP0002: x"]).ok, false);
  assert.equal(evaluate("1.1.0", "1.0.0", true, ["CP0002: x"]).ok, false);
  assert.equal(evaluate("2.0.0", "1.0.0", true, ["CP0002: x"]).ok, true);
  assert.equal(evaluate("0.2.0", "0.1.0", true, ["CP0002: x"]).ok, true);
  assert.equal(evaluate("0.1.1", "0.1.0", true, ["CP0002: x"]).ok, false);
  assert.equal(evaluate("1.0.1", "1.0.0", false, []).ok, true);
  const many = Array.from({ length: 7 }, (_, i) => `CP0002: m${i}`);
  const reason = evaluate("1.1.0", "1.0.0", false, many).reason;
  assert.match(reason, /breaks 7 public API/);
  assert.match(reason, /m4/);
  assert.doesNotMatch(reason, /m5/);
  assert.match(reason, /; \.\.\./);
});

test("measured: every missing required option is a usage error", () => {
  withTempGitRepo((dir) => {
    const complete = ["--package", "widget", "--changelog", join(dir, "CHANGELOG.md"), "--version", "1.1.0", "--tag-prefix", "widget"];
    for (let i = 0; i < complete.length; i += 2) {
      const withoutOne = complete.filter((_, index) => index !== i && index !== i + 1);
      const output = runCheckerExpectFailure(dir, withoutOne, { status: 2 });
      assert.match(output, /usage: check-changelog-semver\.mjs/);
      assert.match(output, /--api-baseline-path/);
    }
    const builtWithoutBaseline = runCheckerExpectFailure(dir, [...complete, "--api-built", "x.dll"], { status: 2 });
    assert.match(builtWithoutBaseline, /usage:/);
    const baselineWithoutBuilt = runCheckerExpectFailure(dir, [...complete, "--api-baseline-path", PLUGINS], { status: 2 });
    assert.match(baselineWithoutBuilt, /usage:/);
  });
});

test("CLI: a missing changelog fails and names the path; stray positional words are ignored", () => {
  withTempGitRepo((dir) => {
    const missing = join(dir, "NOPE.md");
    const output = runCheckerExpectFailure(dir, ["stray", "--package", "widget", "--changelog", missing, "--version", "1.1.0", "--tag-prefix", "widget"]);
    assert.match(output, /changelog not found at .*NOPE.md/);
  });
});

test("measured: the baseline path may use backslashes or a trailing slash", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    for (const path of [`${PLUGINS}/`, `${PLUGINS}//`, PLUGINS.replace(/\//g, "\\")]) {
      const args = measuredArgs(dir, "1.1.0", [trimmedWidget()]);
      args[args.indexOf("--api-baseline-path") + 1] = path;
      assert.match(runCheckerExpectFailure(dir, args), /Gone/);
    }
  });
});

test("measured: reads the repository named by --cwd, not the directory the process started in", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const elsewhere = mkdtempSync(join(tmpdir(), "cultlib-semver-elsewhere-"));
    try {
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]), { processCwd: elsewhere });
      assert.match(output, /Gone/);
    } finally {
      rmSync(elsewhere, { recursive: true, force: true });
    }
  });
});

test("measured: leaves no temporary files behind, passing or failing", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-scratch-"));
    try {
      const env = { ...process.env, TMPDIR: scratch, TEMP: scratch, TMP: scratch };
      const leftovers = () => readdirSync(scratch).filter((name) => name.startsWith("cultlib-apicompat-"));
      runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { env });
      assert.deepEqual(leftovers(), []);
      runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]), { env });
      assert.deepEqual(leftovers(), []);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });
});

// A copy of the checker with its own scripts/api-gate directory, so a test can break the
// gate without touching the repository's.
function withCheckerCopy(gateFiles, fn) {
  const bare = mkdtempSync(join(tmpdir(), "cultlib-semver-bare-"));
  try {
    mkdirSync(join(bare, "scripts"));
    const script = join(bare, "scripts", "check-changelog-semver.mjs");
    copyFileSync(checkerPath, script);
    for (const [path, text] of Object.entries(gateFiles)) {
      mkdirSync(dirname(join(bare, "scripts", "api-gate", path)), { recursive: true });
      writeFileSync(join(bare, "scripts", "api-gate", path), text);
    }
    fn(script);
  } finally {
    rmSync(bare, { recursive: true, force: true });
  }
}

test("measured: a missing or broken tool manifest fails closed and says why", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    // A gate directory without the manifest, and one with a .config directory but no manifest in it.
    for (const gateFiles of [{}, { ".config/other.json": "{}" }]) {
      withCheckerCopy(gateFiles, (script) => {
        const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { script });
        assert.match(output, /could not be measured/);
        assert.match(output, /the api-gate tool manifest \(scripts\/api-gate\/\.config\/dotnet-tools\.json\) is missing/);
      });
    }
    withCheckerCopy({ ".config/dotnet-tools.json": "{ this is not a manifest" }, (script) => {
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { script });
      assert.match(output, /could not be measured/);
      assert.match(output, /dotnet tool restore failed: .*Json parsing error/);
    });
  });
});

// A directory holding git and a stand-in `dotnet` shell script, as the whole PATH.
function withFakeDotnet(dotnetScript, fn) {
  const bin = mkdtempSync(join(tmpdir(), "cultlib-semver-path-"));
  try {
    const git = execFileSync("sh", ["-c", "command -v git"], { encoding: "utf8" }).trim();
    symlinkSync(git, join(bin, "git"));
    if (dotnetScript != null) writeFileSync(join(bin, "dotnet"), `#!/bin/sh\n${dotnetScript}\n`, { mode: 0o755 });
    fn({ ...process.env, PATH: bin });
  } finally {
    rmSync(bin, { recursive: true, force: true });
  }
}

const posix = { skip: process.platform === "win32" };

test("measured: dotnet missing from the PATH fails closed", posix, () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    withFakeDotnet(null, (env) => {
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { env });
      assert.match(output, /dotnet tool restore failed: .*ENOENT/);
    });
  });
});

test("measured: a restore failure shows the last three lines of the tool's own output, in order", posix, () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const args = measuredArgs(dir, "1.0.1", [oldWidget()]);
    // stdout then stderr, CRLF and LF line ends, trailing blank lines: only the last three real lines remain.
    const toolRestore = "if [ \"$1\" = tool ] && [ \"$2\" = restore ]; then printf 'out1\\nout2\\n'; printf 'err1\\r\\nerr2\\n\\n' >&2; exit 1; fi\nexit 0";
    withFakeDotnet(toolRestore, (env) => {
      const output = runCheckerExpectFailure(dir, args, { env });
      assert.match(output, /could not be measured against widget-v1\.0\.0: dotnet tool restore failed: out2 \| err1 \| err2\s*$/);
    });
    const packageRestore = "if [ \"$1\" = restore ]; then printf 'a\\nb\\nc\\nd\\n' >&2; exit 1; fi\nexit 0";
    withFakeDotnet(packageRestore, (env) => {
      const output = runCheckerExpectFailure(dir, args, { env });
      assert.match(output, /: dotnet restore failed: b \| c \| d\s*$/);
    });
  });
});

test("measured: restores that succeed without delivering the reference assemblies fail closed", posix, () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    withCheckerCopy({ ".config/dotnet-tools.json": "{}" }, (script) => {
      const notRestored = /the pinned netstandard reference assemblies were not restored under scripts\/api-gate\/packages/;
      withFakeDotnet("exit 0", (env) => {
        assert.match(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { script, env }), notRestored);
      });
      // The package directory arrives, but without netstandard.dll in it.
      const emptyPackage = 'if [ "$1" = restore ]; then mkdir -p packages/netstandard.library.ref/2.1.0/ref/netstandard2.1; fi\nexit 0';
      withFakeDotnet(emptyPackage, (env) => {
        assert.match(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { script, env }), notRestored);
      });
    });
  });
});

test("readApiCompatRun: only line-initial diagnostics count, and a failure shows the first five trimmed lines", () => {
  assert.deepEqual(readApiCompatRun({ status: 1, output: "warning CP1002: not at the start\nCP0002: Member 'x'\n" }), { breaks: ["CP0002: Member 'x'"] });
  const failure = readApiCompatRun({ status: 3, output: "\n  one\ntwo\nthree\nfour\nfive\nsix\n" }).failure;
  assert.equal(failure, "apicompat exited 3 without measuring: one | two | three | four | five");
});

test("evaluateRelease: lists at most five measured breaks, and says nothing more for five", () => {
  const evaluate = (count) =>
    evaluateRelease({
      packageName: "p",
      changelogText: changelogFor("1.1.0"),
      version: "1.1.0",
      previousVersion: "1.0.0",
      measuredBreaks: Array.from({ length: count }, (_, i) => `CP0002: m${i}`),
    }).reason;
  assert.match(evaluate(5), /\(CP0002: m0; CP0002: m1; CP0002: m2; CP0002: m3; CP0002: m4\);/);
  assert.doesNotMatch(evaluate(5), /\.\.\./);
  assert.match(evaluate(6), /m4; \.\.\.\);/);
  const agreed = evaluateRelease({
    packageName: "p",
    changelogText: changelogFor("2.0.0", { breaking: true }),
    version: "2.0.0",
    previousVersion: "1.0.0",
    measuredBreaks: ["CP0002: m0"],
  }).reason;
  assert.match(agreed, /a "### Breaking" section and 1 measured public API break\(s\)/);
});

test("CLI: a malformed version is refused by name, without repeating it, with or without --first-release", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.1.0"));
    for (const extra of [[], ["--first-release"]]) {
      const output = runCheckerExpectFailure(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.1.x-canary", "--tag-prefix", "widget", ...extra]);
      assert.match(output, /--version is not a MAJOR\.MINOR\.PATCH version/);
      assert.doesNotMatch(output, /canary/);
      assert.doesNotMatch(output, /could not be read/);
    }
  });
});

test("refusals never repeat what the inputs contain", () => {
  const canary = "CANARY-5f3a9c-never-echoed";
  const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-canary-"));
  try {
    const junk = join(scratch, "GameCult.Widget.dll");
    writeFileSync(junk, `MZ not an assembly ${canary}`);
    const canaryChangelog = (dir, version, { breaking = false } = {}) => {
      const path = join(dir, "CHANGELOG.md");
      writeFileSync(path, changelogFor(version, { breaking }).replace("- something changed", `- something changed ${canary}`) + `\n## [0.0.1]\n\n- ${canary}\n`);
      return path;
    };
    const refuse = (dir, args) => {
      const output = runCheckerExpectFailure(dir, args);
      assert.doesNotMatch(output, new RegExp(canary), `the refusal repeated an input: ${output}`);
      return output;
    };
    withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
      const measured = (version, built, options) => {
        const args = measuredArgs(dir, version, built, options);
        args[args.indexOf("--changelog") + 1] = canaryChangelog(dir, version, options);
        return args;
      };
      // a measured break, with the canary in the changelog it must be declared in
      assert.match(refuse(dir, measured("1.1.0", [trimmedWidget()])), /Gone/);
      // the tool cannot read the built assembly
      assert.match(refuse(dir, measured("1.0.1", [junk])), /could not be measured/);
      // no entry for the version, a skipped version, a breaking entry under too small a bump
      const noEntry = measured("1.0.1", [oldWidget()]);
      noEntry[noEntry.indexOf("--version") + 1] = "1.0.2";
      assert.match(refuse(dir, noEntry), /no "## \[1\.0\.2\]" entry/);
      const skip = measured("1.0.9", [oldWidget()]);
      assert.match(refuse(dir, skip), /skips ahead/);
      assert.match(refuse(dir, measured("1.0.1", [oldWidget()], { breaking: true })), /needs a major bump/);
    });
    // the tag's own blob is unreadable
    withReleasedBaseline({ baseline: { "GameCult.Widget.dll": junk } }, (dir) => {
      const args = measuredArgs(dir, "1.0.1", [oldWidget()]);
      args[args.indexOf("--changelog") + 1] = canaryChangelog(dir, "1.0.1");
      assert.match(refuse(dir, args), /could not be measured/);
    });
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test("measured: built and reference paths given relative to the process are read from there", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-relative-"));
    try {
      mkdirSync(join(scratch, "refs"));
      copyFileSync(trimmedWidget(), join(scratch, "GameCult.Widget.dll"));
      const args = measuredArgs(dir, "1.1.0", ["GameCult.Widget.dll"], { refs: ["refs"] });
      // The tool is started elsewhere, so an unresolved relative path would not be found.
      assert.match(runCheckerExpectFailure(dir, args, { processCwd: scratch }), /Gone/);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });
});
