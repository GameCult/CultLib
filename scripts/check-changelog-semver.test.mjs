import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
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

// Runs the checker with `cwd` as its working directory, which is never the
// repository that holds the checker: the release scripts do not run it from there either.
function runChecker(cwd, args, { env, script = checkerPath } = {}) {
  return execFileSync(process.execPath, [script, ...args, "--cwd", cwd], {
    cwd,
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

test("CLI: does not re-litigate a version already published as a tag", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    // 1.0.0 itself is a scar: its own changelog claims breaking under a
    // patch-looking first release. Rebuilding it locally (no new version)
    // must not fail just because history already shipped that way.
    execFileSync("git", ["tag", "widget-v0.9.0"], { cwd: dir });
    const changelogPath = join(dir, "CHANGELOG.md");
    writeFileSync(changelogPath, changelogFor("1.0.0", { breaking: true }));
    const output = runChecker(dir, ["--package", "widget", "--changelog", changelogPath, "--version", "1.0.0", "--tag-prefix", "widget"]);
    assert.match(output, /already published/);
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
function buildAssembly(assemblyName, source) {
  const dir = join(fixtureRoot, createHash("sha256").update(`${assemblyName}
${source}`).digest("hex").slice(0, 16));
  const dll = join(dir, "out", `${assemblyName}.dll`);
  if (!existsSync(dll)) {
    mkdirSync(dir, { recursive: true });
    writeFileSync(
      join(dir, "Fixture.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework>` +
        `<AssemblyName>${assemblyName}</AssemblyName><Deterministic>true</Deterministic></PropertyGroup></Project>`,
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

function measuredArgs(dir, version, builtPaths, { breaking = false } = {}) {
  const changelogPath = join(dir, "CHANGELOG.md");
  writeFileSync(changelogPath, changelogFor(version, { breaking }));
  return [
    "--package", "widget", "--changelog", changelogPath, "--version", version, "--tag-prefix", "widget",
    "--api-baseline-path", PLUGINS, ...builtPaths.flatMap((path) => ["--api-built", path]),
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

test("measured: a removed GameCult.* assembly is a break, a new assembly and a removed third-party one are not", () => {
  const other = buildAssembly("GameCult.Other", "namespace O { public class D {} }");
  const thirdParty = buildAssembly("Vendor.Lib", "namespace V { public class E {} }");
  const baseline = { "GameCult.Widget.dll": oldWidget(), "GameCult.Other.dll": other, "Vendor.Lib.dll": thirdParty };
  withReleasedBaseline({ baseline }, (dir) => {
    const refused = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [oldWidget()]));
    assert.match(refused, /GameCult\.Other\.dll exists at widget-v1\.0\.0 but is no longer built/);
    assert.doesNotMatch(refused, /Vendor\.Lib/);
    assert.match(refused, /breaks 1 public API member/);
    const brandNew = buildAssembly("GameCult.Brand", "namespace B { public class F {} }");
    const ok = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget(), other, brandNew]));
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

test("measured: removed inherited members are caught although netstandard cannot be resolved", () => {
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

test("measured: an already-published version is not measured", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const junk = join(dir, "junk.dll");
    writeFileSync(junk, "junk");
    const output = runChecker(dir, measuredArgs(dir, "1.0.0", [junk], { breaking: true }));
    assert.match(output, /already published/);
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

test("measured: a first release measures nothing", () => {
  withTempGitRepo((dir) => {
    execFileSync("git", ["commit", "--allow-empty", "-q", "-m", "init"], { cwd: dir });
    const junk = join(dir, "junk.dll");
    writeFileSync(junk, "junk");
    const output = runChecker(dir, measuredArgs(dir, "1.0.0", [junk]));
    assert.match(output, /first release/);
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
    for (const path of [`${PLUGINS}/`, PLUGINS.replace(/\//g, "\\")]) {
      const args = measuredArgs(dir, "1.1.0", [trimmedWidget()]);
      args[args.indexOf("--api-baseline-path") + 1] = path;
      assert.match(runCheckerExpectFailure(dir, args), /Gone/);
    }
  });
});

test("measured: leaves no temporary files behind, passing or failing", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-scratch-"));
    try {
      const env = { ...process.env, TMPDIR: scratch, TEMP: scratch, TMP: scratch };
      runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { env });
      assert.deepEqual(readdirSync(scratch), []);
      runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()]), { env });
      assert.deepEqual(readdirSync(scratch), []);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });
});

test("measured: a tool that cannot be restored fails closed and says why", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    // The checker finds its tool manifest next to its own scripts directory; a copy without one cannot restore.
    const bare = mkdtempSync(join(tmpdir(), "cultlib-semver-bare-"));
    try {
      mkdirSync(join(bare, "scripts"));
      const script = join(bare, "scripts", "check-changelog-semver.mjs");
      copyFileSync(checkerPath, script);
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { script });
      assert.match(output, /could not be measured/);
      assert.match(output, /dotnet tool restore failed: .*manifest/i);
    } finally {
      rmSync(bare, { recursive: true, force: true });
    }
  });
});

test("measured: dotnet missing from the PATH fails closed", { skip: process.platform === "win32" }, () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const gitOnly = mkdtempSync(join(tmpdir(), "cultlib-semver-path-"));
    try {
      const git = execFileSync("sh", ["-c", "command -v git"], { encoding: "utf8" }).trim();
      symlinkSync(git, join(gitOnly, "git"));
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]), { env: { ...process.env, PATH: gitOnly } });
      assert.match(output, /dotnet tool restore failed: .*ENOENT/);
    } finally {
      rmSync(gitOnly, { recursive: true, force: true });
    }
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
