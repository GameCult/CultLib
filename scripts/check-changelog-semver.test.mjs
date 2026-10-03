import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, posix as posixPath } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import {
  BREAKING_HEADING,
  checkDeclaration,
  classifyBump,
  evaluateRelease,
  hasBreakingSection,
  isManagedAssembly,
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

// --- CLI-level integration tests: exercise the declaration, the git-backed release
// resolution and the derived first release, which the pure evaluateRelease() tests
// above cannot reach. ---

const PLUGINS = "unity/widget/Runtime/Plugins";
const repoRoot = fileURLToPath(new URL("..", import.meta.url));
const workflowFile = join(repoRoot, ".github", "workflows", "publish-packages.yml");
const releasePackages = (await import("./release-packages.mjs")).default;

// The package every fixture repository declares in its own scripts/release-packages.mjs.
const WIDGET = { tagPrefix: "widget", changelog: "CHANGELOG.md" };

function declare(dir, entries) {
  mkdirSync(join(dir, "scripts"), { recursive: true });
  writeFileSync(join(dir, "scripts", "release-packages.mjs"), `export default ${JSON.stringify(entries)};\n`);
}

function withTempGitRepo(fn) {
  const dir = mkdtempSync(join(tmpdir(), "cultlib-semver-check-"));
  try {
    execFileSync("git", ["init", "-q"], { cwd: dir });
    execFileSync("git", ["config", "user.email", "test@example.com"], { cwd: dir });
    execFileSync("git", ["config", "user.name", "Test"], { cwd: dir });
    declare(dir, { widget: WIDGET });
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

const git = (dir, ...args) => execFileSync("git", args, { cwd: dir });
const commitAndTag = (dir, ...tags) => {
  git(dir, "commit", "--allow-empty", "-q", "-m", `init ${tags.join(" ")}`);
  for (const tag of tags) git(dir, "tag", tag);
};
const writeChangelog = (dir, version, options) => writeFileSync(join(dir, "CHANGELOG.md"), changelogFor(version, options));
const widgetArgs = (version) => ["--package", "widget", "--version", version];

test("CLI: resolves the previous version from git tags and passes a valid bump", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.1.0");
    const output = runChecker(dir, widgetArgs("1.1.0"));
    assert.match(output, /1\.1\.0 is a minor bump over 1\.0\.0/);
  });
});

test("CLI: fails when the changelog claims breaking under too small a bump", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.0.1", { breaking: true });
    const output = runCheckerExpectFailure(dir, widgetArgs("1.0.1"));
    assert.match(output, /needs a major bump/);
  });
});

test("CLI: a tag for the version being released does not skip the check", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0", "widget-v1.0.1");
    writeChangelog(dir, "1.0.1", { breaking: true });
    const output = runCheckerExpectFailure(dir, widgetArgs("1.0.1"));
    assert.match(output, /needs a major bump/);
  });
});

test("CLI: a first release is derived from the tags, and nothing is measured for it", () => {
  withTempGitRepo((dir) => {
    // another package's tag exists; this package has none
    commitAndTag(dir, "other-v1.0.0");
    declare(dir, { widget: { ...WIDGET, assemblies: PLUGINS } });
    const junk = join(dir, "junk.dll");
    writeFileSync(junk, "junk");
    const measured = runChecker(dir, measuredArgs(dir, "1.0.0", [junk]));
    assert.match(measured, /first release \(1\.0\.0\)/);
    assert.doesNotMatch(measured, /public API measured/);
    declare(dir, { widget: WIDGET });
    assert.match(runChecker(dir, widgetArgs("1.0.0")), /first release \(1\.0\.0\)/);
    // the version's own tag, beside another package's, is still a first release
    git(dir, "tag", "widget-v1.0.0");
    assert.match(runChecker(dir, widgetArgs("1.0.0")), /first release \(1\.0\.0\)/);
  });
});

test("CLI: a checkout that holds no other tag refuses, and no option rescues it", () => {
  const holdsNone = /this checkout holds no tags; a first release cannot be told from a missing record/;
  withTempGitRepo((dir) => {
    git(dir, "commit", "--allow-empty", "-q", "-m", "init");
    writeChangelog(dir, "1.0.0");
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.0.0")), holdsNone);
    git(dir, "tag", "widget-v1.0.0");
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.0.0")), holdsNone);
    // an option that used to declare the release is now an unknown option, not a rescue
    runCheckerExpectFailure(dir, [...widgetArgs("1.0.0"), "--first-release"], { status: 2 });
  });
});

test("CLI: tags that cannot be read refuse without git's output", () => {
  const notARepo = mkdtempSync(join(tmpdir(), "cultlib-semver-norepo-"));
  try {
    declare(notARepo, { widget: WIDGET });
    writeChangelog(notARepo, "1.0.0");
    const refused = runCheckerExpectFailure(notARepo, widgetArgs("1.0.0"));
    assert.match(refused, /tags of --cwd could not be read \(not a git repository\?\), so the previous release is unknown/);
    assert.doesNotMatch(refused, /fatal/); // git's own complaint is not forwarded
  } finally {
    rmSync(notARepo, { recursive: true, force: true });
  }
});

test("CLI: a same-prefix tag that is not MAJOR.MINOR.PATCH refuses, never skipped", () => {
  for (const malformed of ["widget-v0.9.0-rc1", "widget-v1.0", "widget-vnext"]) {
    // alone
    withTempGitRepo((dir) => {
      commitAndTag(dir, malformed);
      writeChangelog(dir, "0.9.0");
      const output = runCheckerExpectFailure(dir, widgetArgs("0.9.0"));
      assert.match(output, new RegExp(`${malformed.replace(/\./g, "\\.")} starts with widget-v but is not widget-vMAJOR\\.MINOR\\.PATCH`));
    });
    // beside an older valid release the version would otherwise be measured against
    withTempGitRepo((dir) => {
      commitAndTag(dir, "widget-v0.8.0", malformed);
      writeChangelog(dir, "0.9.0");
      const output = runCheckerExpectFailure(dir, widgetArgs("0.9.0"));
      assert.match(output, new RegExp(`${malformed.replace(/\./g, "\\.")} starts with`));
    });
  }
});

test("CLI: a version older than every release refuses", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0", "widget-v2.0.0");
    writeChangelog(dir, "0.9.0");
    const output = runCheckerExpectFailure(dir, widgetArgs("0.9.0"));
    assert.match(output, /widget-v(1|2)\.0\.0 exists but no widget-v tag is older than 0\.9\.0: the version is older than every release/);
  });
});

test("CLI: the previous release is the newest older tag by version order, not git's string order", () => {
  withTempGitRepo((dir) => {
    // git tag -l lists these as 1.0.0, 1.0.5, 1.10.0, 1.2.0, 1.9.0, 2.0.0: 1.10.0 sorts before 1.2.0
    commitAndTag(dir, "widget-v1.0.0", "widget-v1.2.0", "widget-v1.10.0", "widget-v1.9.0", "widget-v2.0.0", "other-v9.9.9");
    git(dir, "tag", "-a", "-m", "annotated", "widget-v1.0.5");
    const previousOf = (version) => {
      writeChangelog(dir, version);
      return runChecker(dir, widgetArgs(version));
    };
    assert.match(previousOf("1.10.1"), /1\.10\.1 is a patch bump over 1\.10\.0/);
    assert.match(previousOf("1.11.0"), /1\.11\.0 is a minor bump over 1\.10\.0/);
    assert.match(previousOf("1.3.0"), /1\.3\.0 is a minor bump over 1\.2\.0/);
    assert.match(previousOf("1.0.6"), /1\.0\.6 is a patch bump over 1\.0\.5/);
    assert.match(previousOf("2.0.1"), /2\.0\.1 is a patch bump over 2\.0\.0/);
    assert.match(previousOf("3.0.0"), /3\.0\.0 is a major bump over 2\.0\.0/);
  });
});

test("CLI: a later backport tag on another branch does not change a rebuild's predecessor", () => {
  const head = (dir) => git(dir, "rev-parse", "HEAD").toString().trim();
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v0.2.0");
    const older = head(dir);
    commitAndTag(dir, "widget-v0.3.0");
    const rebuilt = head(dir);
    writeChangelog(dir, "0.3.0");
    const atRelease = runChecker(dir, widgetArgs("0.3.0"));
    assert.match(atRelease, /0\.3\.0 is a minor bump over 0\.2\.0/);
    // the backport is cut from the older release, later, and is never merged
    git(dir, "checkout", "-q", older);
    commitAndTag(dir, "widget-v0.2.5");
    const backport = head(dir);
    git(dir, "checkout", "-q", rebuilt);
    assert.equal(runChecker(dir, widgetArgs("0.3.0")), atRelease);
    // history that does contain the backport still measures against it
    git(dir, "checkout", "-q", backport);
    writeChangelog(dir, "0.2.6");
    assert.match(runChecker(dir, widgetArgs("0.2.6")), /0\.2\.6 is a patch bump over 0\.2\.5/);
  });
  // older tags that are all outside the history leave the predecessor unknown
  withTempGitRepo((dir) => {
    commitAndTag(dir, "other-v1.0.0");
    const root = head(dir);
    git(dir, "checkout", "-q", "--detach");
    commitAndTag(dir, "widget-v0.1.0");
    git(dir, "checkout", "-q", root);
    writeChangelog(dir, "0.2.0");
    assert.match(runCheckerExpectFailure(dir, widgetArgs("0.2.0")), /none is an ancestor of the commit being checked/);
  });
});

// A repository where each tag tracks the files it is given: tag name -> { path: contents }.
// Every file is committed under the tag that first names it, so a later tag tracks the earlier ones'.
function tagWithFiles(dir, tag, files) {
  for (const [path, contents] of Object.entries(files)) {
    mkdirSync(dirname(join(dir, path)), { recursive: true });
    writeFileSync(join(dir, path), contents);
  }
  git(dir, "add", "--", ...Object.keys(files));
  commitAndTag(dir, tag);
}

test("CLI: a mistyped declared prefix is refused, because a tag under another prefix tracks the package's files", () => {
  // The real entries of CultMath, cultlib and a Python package, each released once under its
  // real prefix, then the declaration misspelled. Nothing is measured: the refusal comes from git.
  const cases = [
    { name: "org.gamecult.cultmath", released: "0.3.0", next: "0.3.1", file: "CultMath.dll" },
    { name: "org.gamecult.cultlib", released: "1.0.60", next: "1.0.61", file: "GameCult.Core.dll" },
    { name: "cultcache-py", released: "0.1.0", next: "0.1.1", file: "cultcache/__init__.py" },
  ];
  for (const { name, released, next, file } of cases) {
    const real = releasePackages[name];
    const folder = real.assemblies ?? posixPath.dirname(real.changelog);
    withTempGitRepo((dir) => {
      declare(dir, { [name]: real });
      tagWithFiles(dir, `${real.tagPrefix}-v${released}`, { [`${folder}/${file}`]: "released" });
      mkdirSync(dirname(join(dir, real.changelog)), { recursive: true });
      writeFileSync(join(dir, real.changelog), changelogFor(next));
      const junk = join(dir, "junk.dll");
      writeFileSync(junk, "junk");
      const args = ["--package", name, "--version", next, ...(real.assemblies ? ["--api-built", junk] : [])];
      declare(dir, { [name]: { ...real, tagPrefix: real.tagPrefix.slice(0, -1) } });
      const refused = runCheckerExpectFailure(dir, args);
      assert.match(refused, new RegExp(`${real.tagPrefix}-v${released} tracks files under `));
      assert.doesNotMatch(refused, /first release \(/);
      // the same release under the real prefix is a bump over its predecessor, not a first release
      if (!real.assemblies) {
        declare(dir, { [name]: real });
        assert.match(runChecker(dir, args), /patch bump over/);
      }
    });
  }
});

test("CLI: no tag tracks the package's folder, so a release is a first release however many other tags exist", () => {
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, changelog: "widget/CHANGELOG.md", assemblies: PLUGINS } });
    tagWithFiles(dir, "other-v1.0.0", { "other/file.txt": "other" });
    tagWithFiles(dir, "other-v1.1.0", { "other/more.txt": "more" });
    // the package's folder arrives after every tag, and its files are not tracked by any of them
    mkdirSync(join(dir, "widget"), { recursive: true });
    writeFileSync(join(dir, "widget", "CHANGELOG.md"), changelogFor("1.0.0"));
    const junk = join(dir, "junk.dll");
    writeFileSync(junk, "junk");
    const args = [...widgetArgs("1.0.0"), "--api-built", junk];
    assert.match(runChecker(dir, args), /first release \(1\.0\.0\)/);
    // its own tag does not stop a rebuild of that first release
    tagWithFiles(dir, "widget-v1.0.0", { [`${PLUGINS}/GameCult.Widget.dll`]: "shipped" });
    assert.match(runChecker(dir, args), /first release \(1\.0\.0\)/);
    // but the same files under any other tag make a later release with no same-prefix baseline a refusal
    declare(dir, { widget: { ...WIDGET, changelog: "widget/CHANGELOG.md", assemblies: PLUGINS, tagPrefix: "widgt" } });
    assert.match(runCheckerExpectFailure(dir, args), /widget-v1\.0\.0 tracks files under unity\/widget\/Runtime\/Plugins/);
  });
});

test("CLI: a package without assemblies is told from a new one by its own directory, and a changelog at the root by the whole tree", () => {
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, changelog: "pkg/CHANGELOG.md" } });
    tagWithFiles(dir, "other-v1.0.0", { "elsewhere.txt": "x" });
    mkdirSync(join(dir, "pkg"));
    writeFileSync(join(dir, "pkg", "CHANGELOG.md"), changelogFor("1.0.0"));
    assert.match(runChecker(dir, widgetArgs("1.0.0")), /first release \(1\.0\.0\)/);
    tagWithFiles(dir, "other-v1.1.0", { "pkg/code.py": "x" });
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.0.0")), /other-v1\.1\.0 tracks files under pkg/);
  });
  withTempGitRepo((dir) => {
    tagWithFiles(dir, "other-v1.0.0", { "elsewhere.txt": "x" });
    writeChangelog(dir, "1.0.0");
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.0.0")), /other-v1.0.0 tracks files under ./);
  });
});

test("CLI: a folder that moved is the declared folder alone: the old path does not count", () => {
  const baseline = buildAssembly("GameCult.Widget", WITH_GONE);
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": baseline } }, (dir) => {
    const moved = "unity/widget2/Runtime/Plugins";
    const built = buildAssembly("GameCult.Widget", WITH_GONE);
    // under the real prefix the moved folder holds nothing at the tag: refused as a moved directory
    declare(dir, { widget: { ...WIDGET, assemblies: moved } });
    writeChangelog(dir, "1.0.1");
    assert.match(runCheckerExpectFailure(dir, [...widgetArgs("1.0.1"), "--api-built", built]), /tracks no managed assembly under unity\/widget2\/Runtime\/Plugins/);
    // under a mistyped prefix, no tag tracks the new folder, and the old one is not looked at
    declare(dir, { widget: { ...WIDGET, assemblies: moved, tagPrefix: "widgt" } });
    assert.match(runChecker(dir, [...widgetArgs("1.0.1"), "--api-built", built]), /first release \(1\.0\.1\)/);
  });
});

test("CLI: a tag that cannot be read at its ancestry or its files refuses", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v0.8.0");
    // a tag whose object is missing from the repository (a damaged checkout)
    writeFileSync(join(dir, ".git", "refs", "tags", "widget-v0.9.0"), "1".repeat(40) + "\n");
    writeChangelog(dir, "1.0.0");
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.0.0")), /whether widget-v0\.9\.0 is an ancestor of the commit being checked could not be read/);
  });
  withTempGitRepo((dir) => {
    commitAndTag(dir, "other-v1.0.0");
    writeFileSync(join(dir, ".git", "refs", "tags", "other-v2.0.0"), "1".repeat(40) + "\n");
    writeChangelog(dir, "1.0.0");
    const refused = runCheckerExpectFailure(dir, widgetArgs("1.0.0"));
    assert.match(refused, /could not be read, so a first release cannot be told from a mistyped prefix/);
    assert.doesNotMatch(refused, /fatal|bad object|1111111/); // git's own complaint is not forwarded
  });
});

test("CLI: two packages that share a tag prefix are refused, and so is an entry that is not whole", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.0.1");
    declare(dir, { widget: WIDGET, gadget: { ...WIDGET } });
    for (const name of ["widget", "gadget"]) {
      const output = runCheckerExpectFailure(dir, ["--package", name, "--version", "1.0.1"]);
      assert.match(output, /scripts\/release-packages\.mjs is malformed: widget and gadget share one tag prefix/);
    }
    // distinct prefixes pass
    declare(dir, { widget: WIDGET, gadget: { ...WIDGET, tagPrefix: "gadget" } });
    assert.match(runChecker(dir, widgetArgs("1.0.1")), /patch bump over 1\.0\.0/);
    for (const broken of [
      { changelog: "CHANGELOG.md" },
      { tagPrefix: 7, changelog: "CHANGELOG.md" },
      { tagPrefix: "widget" },
      { tagPrefix: "widget", changelog: ["CHANGELOG.md"] },
      null,
    ]) {
      declare(dir, { widget: WIDGET, gadget: broken });
      const output = runCheckerExpectFailure(dir, widgetArgs("1.0.1"));
      assert.match(output, /scripts\/release-packages\.mjs is malformed: gadget must declare a string tagPrefix and a string changelog/);
    }
  });
});

test("the real declaration is whole and shares no tag prefix", () => {
  assert.equal(checkDeclaration(releasePackages), null);
  assert.equal(new Set(Object.values(releasePackages).map((entry) => entry.tagPrefix)).size, Object.keys(releasePackages).length);
});

test("CLI: the tag prefix is the declaration's, not a spelling the caller or the directory supplies", () => {
  withTempGitRepo((dir) => {
    declare(dir, { widget: { tagPrefix: "widget-unity", changelog: "widget/CHANGELOG.md" } });
    mkdirSync(join(dir, "widget"));
    const changelog = (version) => writeFileSync(join(dir, "widget", "CHANGELOG.md"), changelogFor(version));
    // widget-v1.2.0 is another package's tag: were it this package's, 1.1.0 would be a reversal
    commitAndTag(dir, "widget-v1.2.0", "widget-unity-v1.0.0");
    changelog("1.1.0");
    assert.match(runChecker(dir, widgetArgs("1.1.0")), /1\.1\.0 is a minor bump over 1\.0\.0/);
    // with the declared prefix's only release deleted, 1.0.1 is a first release, not a bump over widget-v1.2.0
    git(dir, "tag", "-d", "widget-unity-v1.0.0");
    changelog("1.0.1");
    assert.match(runChecker(dir, widgetArgs("1.0.1")), /first release \(1\.0\.1\)/);
  });
});

test("CLI: an undeclared package refuses, and so does a name that only the prototype holds", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.1.0");
    for (const name of ["gadget", "constructor", "__proto__"]) {
      const output = runCheckerExpectFailure(dir, ["--package", name, "--version", "1.1.0"]);
      assert.match(output, /not a package declared in scripts\/release-packages\.mjs/);
    }
  });
});

test("CLI: a repository with no declaration refuses", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.1.0");
    rmSync(join(dir, "scripts"), { recursive: true });
    assert.match(runCheckerExpectFailure(dir, widgetArgs("1.1.0")), /scripts\/release-packages\.mjs could not be loaded from --cwd/);
  });
});

test("CLI: the flags the declaration replaced are usage errors, as is any unknown option", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.1.0");
    // the same call without the extra option passes, so a silently ignored flag would exit 0
    assert.match(runChecker(dir, widgetArgs("1.1.0")), /minor bump/);
    for (const extra of [
      ["--first-release"],
      ["--first-release", "true"],
      ["--first-release=true"],
      ["--api-new", "GameCult.Widget.dll"],
      ["--tag-prefix", "widget"],
      ["--changelog", join(dir, "CHANGELOG.md")],
      ["--api-baseline-path", PLUGINS],
      ["--bogus", "1"],
    ]) {
      assert.match(runCheckerExpectFailure(dir, [...widgetArgs("1.1.0"), ...extra], { status: 2 }), /usage: check-changelog-semver\.mjs/);
      assert.match(runCheckerExpectFailure(dir, [...extra, ...widgetArgs("1.1.0")], { status: 2 }), /usage:/);
    }
  });
});

test("workflow: every tag trigger is a declared prefix, and every declared assemblies directory is tracked", () => {
  const workflow = readFileSync(workflowFile, "utf8");
  const triggers = [...workflow.match(/^\s+tags:\r?\n((?:\s+- "[^"]+"\r?\n)+)/m)[1].matchAll(/- "([^"]+)-v\*"/g)].map((match) => match[1]);
  assert.ok(triggers.length >= 4, "the workflow triggers on the npm and PyPI packages' tags");
  const prefixes = Object.values(releasePackages).map((entry) => entry.tagPrefix);
  for (const trigger of triggers) assert.ok(prefixes.includes(trigger), `${trigger}-v* triggers the workflow but no package declares that prefix`);
  for (const [name, entry] of Object.entries(releasePackages)) {
    if (entry.assemblies === undefined) continue;
    assert.notEqual(execFileSync("git", ["ls-files", "--", entry.assemblies], { cwd: repoRoot, encoding: "utf8" }).trim(), "", `${name} declares ${entry.assemblies}, which tracks nothing`);
  }
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

// A repo whose tag widget-v<previous> tracks `baseline` ({path under PLUGINS: dll path}) under
// PLUGINS, and whose working tree then holds `afterTag` instead. The package declares PLUGINS
// as its assemblies directory.
function withReleasedBaseline({ baseline, afterTag = baseline, previous = "1.0.0" }, fn) {
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, assemblies: PLUGINS } });
    const place = (files) => {
      for (const [name, path] of Object.entries(files)) {
        mkdirSync(dirname(join(dir, PLUGINS, name)), { recursive: true });
        copyFileSync(path, join(dir, PLUGINS, name));
      }
    };
    place(baseline);
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", `widget-v${previous}`], { cwd: dir });
    if (afterTag !== baseline) {
      for (const name of Object.keys(baseline)) rmSync(join(dir, PLUGINS, name));
      place(afterTag);
      execFileSync("git", ["add", "-A", "."], { cwd: dir });
      execFileSync("git", ["commit", "-q", "-m", "after the release"], { cwd: dir });
    }
    fn(dir);
  });
}

function measuredArgs(dir, version, builtPaths, { breaking = false, refs = [] } = {}) {
  writeChangelog(dir, version, { breaking });
  return [
    ...widgetArgs(version),
    ...builtPaths.flatMap((path) => ["--api-built", path]),
    ...refs.flatMap((refDir) => ["--api-refs", refDir]),
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

test("measured: a tracked assembly that is no longer built is a break, whatever its name; a built assembly with no namesake is an addition", () => {
  const other = buildAssembly("GameCult.Other", "namespace O { public class D {} }");
  const thirdParty = buildAssembly("Vendor.Lib", "namespace V { public class E {} }");
  const baseline = { "GameCult.Widget.dll": oldWidget(), "GameCult.Other.dll": other, "Vendor.Lib.dll": thirdParty };
  withReleasedBaseline({ baseline }, (dir) => {
    const refused = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [oldWidget()]));
    assert.match(refused, /GameCult\.Other\.dll exists at widget-v1\.0\.0 but is no longer built/);
    assert.match(refused, /Vendor\.Lib\.dll exists at widget-v1\.0\.0 but is no longer built/);
    assert.match(refused, /breaks 2 public API member/);
    const brandNew = buildAssembly("GameCult.Brand", "namespace B { public class F {} }");
    const ok = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget(), other, thirdParty, brandNew]));
    assert.match(ok, /1\.0\.1 is a patch bump/);
  });
});

test("measured: renaming the only assembly is a break with nothing declared, and a declared break can ship", () => {
  // CultMath's shape: one assembly, and its name is not GameCult.*.
  const cultMath = buildAssembly("CultMath", WITH_GONE);
  const renamedTrimmed = buildAssembly("CultMath.Next", WITHOUT_GONE);
  withReleasedBaseline({ baseline: { "CultMath.dll": cultMath } }, (dir) => {
    const own = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [buildAssembly("CultMath", WITHOUT_GONE)]));
    assert.match(own, /Gone/);
    const renamed = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [renamedTrimmed]));
    assert.match(renamed, /assembly CultMath\.dll exists at widget-v1\.0\.0 but is no longer built/);
    const shipped = runChecker(dir, measuredArgs(dir, "2.0.0", [renamedTrimmed], { breaking: true }));
    assert.match(shipped, /2\.0\.0 is a major bump over 1\.0\.0 and has a "### Breaking" section and 1 measured public API break/);
  });
  // with another assembly built, the unbuilt one is still the break
  const other = buildAssembly("Other", "namespace O { public class D {} }");
  withReleasedBaseline({ baseline: { "CultMath.dll": cultMath, "Other.dll": other } }, (both) => {
    const partial = runCheckerExpectFailure(both, measuredArgs(both, "1.0.1", [cultMath, renamedTrimmed]));
    assert.match(partial, /Other\.dll exists at widget-v1\.0\.0 but is no longer built/);
  });
});

test("measured: an assembly tracked in a subdirectory is measured, and is a break when it is no longer built", () => {
  const widget = oldWidget();
  const sub = buildAssembly("GameCult.B", "namespace B { public class K { public void Keep() {} public void Gone() {} } }");
  const subTrimmed = buildAssembly("GameCult.B", "namespace B { public class K { public void Keep() {} } }");
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": widget, "sub/GameCult.B.dll": sub } }, (dir) => {
    const unbuilt = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [widget]));
    assert.match(unbuilt, /assembly GameCult\.B\.dll exists at widget-v1\.0\.0 but is no longer built/);
    const compared = runCheckerExpectFailure(dir, measuredArgs(dir, "1.1.0", [widget, subTrimmed]));
    assert.match(compared, /B\.K\.Gone/);
    assert.doesNotMatch(compared, /no longer built/);
    assert.match(runChecker(dir, measuredArgs(dir, "1.0.1", [widget, sub])), /1\.0\.1 is a patch bump/);
  });
});

// A PE image written byte by byte. A managed assembly's CLI header is data directory 14; a
// native DLL has none.
function peImage({ plus = true, directories = 16, cli = null } = {}) {
  const pe = 0x40;
  const optionalSize = (plus ? 112 : 96) + directories * 8;
  const bytes = Buffer.alloc(pe + 24 + optionalSize);
  bytes.writeUInt16LE(0x5a4d, 0);
  bytes.writeUInt32LE(pe, 0x3c);
  bytes.writeUInt32LE(0x00004550, pe);
  bytes.writeUInt16LE(plus ? 0x8664 : 0x14c, pe + 4);
  bytes.writeUInt16LE(optionalSize, pe + 20);
  const optional = pe + 24;
  bytes.writeUInt16LE(plus ? 0x20b : 0x10b, optional);
  const dirs = optional + (plus ? 112 : 96);
  bytes.writeUInt32LE(directories, dirs - 4);
  if (cli) {
    bytes.writeUInt32LE(cli.rva, dirs + 14 * 8);
    bytes.writeUInt32LE(cli.size, dirs + 14 * 8 + 4);
  }
  return bytes;
}

const CLI = { rva: 0x2000, size: 0x48 };

test("isManagedAssembly: a non-empty CLI header makes an assembly managed, nothing else does", () => {
  assert.equal(isManagedAssembly(peImage({ cli: CLI })), true);
  assert.equal(isManagedAssembly(peImage({ plus: false, cli: CLI })), true);
  assert.equal(isManagedAssembly(peImage()), false);
  assert.equal(isManagedAssembly(peImage({ plus: false })), false);
  assert.equal(isManagedAssembly(peImage({ cli: { rva: CLI.rva, size: 0 } })), false);
  assert.equal(isManagedAssembly(peImage({ cli: { rva: 0, size: CLI.size } })), false);
  // too few data directories to have a CLI header, whatever the bytes after them say
  assert.equal(isManagedAssembly(peImage({ directories: 14 })), false);
  assert.equal(isManagedAssembly(readFileSync(oldWidget())), true);
});

test("isManagedAssembly: bytes that are not a PE image throw", () => {
  const withByte = (offset, value) => {
    const bytes = peImage({ cli: CLI });
    bytes[offset] = value;
    return bytes;
  };
  for (const bytes of [
    Buffer.alloc(0),
    Buffer.from("not a PE image, just text that is long enough to be read as a header ........."),
    peImage({ cli: CLI }).subarray(0, 0x50),
    peImage({ cli: CLI }).subarray(0, 0x40 + 24 + 100),
    withByte(0, 0x4e),
    withByte(0x40, 0x51),
    withByte(0x40 + 24 + 1, 0x00),
  ]) {
    assert.throws(() => isManagedAssembly(bytes), /not a PE image/);
  }
});

test("isManagedAssembly: the shipped QUIC bridge is native and the shipped managed assemblies are managed", () => {
  const plugins = join(repoRoot, "unity", "org.gamecult.cultlib", "Runtime", "Plugins");
  assert.equal(isManagedAssembly(readFileSync(join(plugins, "x86_64", "gamecult_mesh_quic_native.dll"))), false);
  assert.equal(isManagedAssembly(readFileSync(join(plugins, "GameCult.Caching.dll"))), true);
});

test("measured: natives are not measured, decided by their bytes; a .dll that is not a PE image refuses", () => {
  const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-pe-"));
  try {
    const file = (name, bytes) => {
      const path = join(scratch, name);
      writeFileSync(path, bytes);
      return path;
    };
    // a native in x86_64/ and one beside the managed assemblies: neither is built, neither is a break
    const baseline = { "GameCult.Widget.dll": oldWidget(), "x86_64/native.dll": file("a.dll", peImage()), "Native32.dll": file("b.dll", peImage({ plus: false })) };
    withReleasedBaseline({ baseline }, (dir) => {
      const ok = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget()]));
      assert.match(ok, /1\.0\.1 is a patch bump/);
      assert.doesNotMatch(ok, /native/i);
    });
    for (const [name, bytes] of [["text.dll", Buffer.from("not a PE image")], ["cut.dll", peImage({ cli: CLI }).subarray(0, 0x50)]]) {
      withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget(), "x86_64/bogus.dll": file(name, bytes) } }, (dir) => {
        const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]));
        assert.match(output, /unity\/widget\/Runtime\/Plugins\/x86_64\/bogus\.dll at widget-v1\.0\.0 is a \.dll but not a PE image/);
      });
    }
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});

test("measured: two managed assemblies with one name refuse", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget(), "a/X.dll": oldWidget(), "b/X.dll": oldWidget() } }, (dir) => {
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]));
    assert.match(output, /widget-v1\.0\.0 tracks two managed assemblies named X\.dll/);
  });
});

test("measured: a declared assemblies directory makes measurement mandatory, and a package without one takes no --api-built", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", []));
    assert.match(output, /ships the assemblies under unity\/widget\/Runtime\/Plugins, so the release must hand them over with --api-built/);
  });
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    assert.match(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()])), /declares no assemblies directory/);
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
      mkdirSync(join(scratch, "builtA"));
      mkdirSync(join(scratch, "refs"));
      copyFileSync(newB, join(scratch, "built", "GameCult.Widget.dll"));
      copyFileSync(newA, join(scratch, "builtA", "Vendor.A.dll"));
      copyFileSync(newA, join(scratch, "refs", "Vendor.A.dll"));
      const built = [join(scratch, "built", "GameCult.Widget.dll"), join(scratch, "builtA", "Vendor.A.dll")];
      // The new base assembly is reachable from the widget's directory only through --api-refs: the checker must pass it on.
      assert.match(runChecker(dir, measuredArgs(dir, "1.0.1", built, { refs: [join(scratch, "refs")] })), /1\.0\.1 is a patch bump/);
      assert.match(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", built)), /CP0007/);
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  });
});

test("measured: a base chain changed in a dependency is seen on the GameCult type through the left references", () => {
  // Old Vendor.A: Mid derives from Exception. New Vendor.A: Mid has no base. GameCult's D derives from Mid in both.
  // ApiCompat reports the lost ISerializable on D only when the left side can resolve netstandard and Vendor.A.
  const oldA = buildAssembly("Vendor.A", "namespace A { public class Mid : System.Exception { } }");
  const newA = buildAssembly("Vendor.A", "namespace A { public class Mid { } }");
  const oldB = buildAssembly("GameCult.Widget", "namespace B { public class D : A.Mid { } }", [oldA]);
  const newB = buildAssembly("GameCult.Widget", "namespace B { public class D : A.Mid { } }", [newA]);
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldB, "Vendor.A.dll": oldA } }, (dir) => {
    const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-left-"));
    try {
      copyFileSync(newB, join(scratch, "GameCult.Widget.dll"));
      copyFileSync(newA, join(scratch, "Vendor.A.dll"));
      const output = runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [join(scratch, "GameCult.Widget.dll"), join(scratch, "Vendor.A.dll")]));
      assert.match(output, /Type 'B\.D' does not implement interface 'System\.Runtime\.Serialization\.ISerializable'/);
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

test("measured: measuring nothing refuses, naming what is missing", () => {
  const fail = (dir, args) => runCheckerExpectFailure(dir, args);
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    // a declared directory that is wrong or moved
    declare(dir, { widget: { ...WIDGET, assemblies: "unity/moved/Runtime/Plugins" } });
    assert.match(fail(dir, measuredArgs(dir, "1.1.0", [trimmedWidget()])), /widget-v1\.0\.0 tracks no managed assembly under unity\/moved\/Runtime\/Plugins: the declared directory is missing or moved/);
  });
  // a previous tag that does not track the DLLs at all
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, assemblies: PLUGINS } });
    mkdirSync(join(dir, PLUGINS), { recursive: true });
    writeFileSync(join(dir, PLUGINS, "README.txt"), "no dlls yet");
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    assert.match(fail(dir, measuredArgs(dir, "1.1.0", [oldWidget()])), /does not track the DLLs/);
  });
  // a previous tag whose only DLLs are native
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, assemblies: PLUGINS } });
    mkdirSync(join(dir, PLUGINS), { recursive: true });
    writeFileSync(join(dir, PLUGINS, "native.dll"), peImage());
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    assert.match(fail(dir, measuredArgs(dir, "1.1.0", [oldWidget()])), /tracks no managed assembly/);
  });
});

test("measured: a git failure while reading the baseline, and a changelog that is not a file, are refusals without a stack or git's output", () => {
  const clean = (output) => {
    assert.doesNotMatch(output, /fatal|Command failed|\n\s+at |usage: git|unknown option/);
    return output;
  };
  // the tag tracks a gitlink named like a DLL: ls-tree lists it, git show cannot read it
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, assemblies: PLUGINS } });
    mkdirSync(join(dir, PLUGINS), { recursive: true });
    copyFileSync(oldWidget(), join(dir, PLUGINS, "GameCult.Widget.dll"));
    execFileSync("git", ["add", "."], { cwd: dir });
    execFileSync("git", ["update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,unity/widget/Runtime/Plugins/Gitlink.dll"], { cwd: dir });
    execFileSync("git", ["commit", "-q", "-m", "release"], { cwd: dir });
    execFileSync("git", ["tag", "widget-v1.0.0"], { cwd: dir });
    const output = clean(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()])));
    assert.match(output, /git could not read the assemblies tracked under unity\/widget\/Runtime\/Plugins at widget-v1\.0\.0/);
  });
  // a declared directory that git would take for an option
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    declare(dir, { widget: { ...WIDGET, assemblies: "--bogus" } });
    assert.match(clean(runCheckerExpectFailure(dir, measuredArgs(dir, "1.0.1", [oldWidget()]))), /tracks no managed assembly under --bogus|git could not read/);
  });
  // a changelog path that is a directory
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    mkdirSync(join(dir, "CHANGELOG.md"));
    const output = clean(runCheckerExpectFailure(dir, widgetArgs("1.0.1")));
    assert.match(output, /changelog cannot be read at /);
  });
});

test("measured: a passing release says that unmeasured changes still need declaring", () => {
  withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
    const output = runChecker(dir, measuredArgs(dir, "1.0.1", [oldWidget()]));
    assert.match(output, /public API measured; changes ApiCompat cannot see \(see "Unmeasured changes" in docs\/semver-policy\.md\) must still be declared under "### Breaking"/);
  });
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.0.1");
    const unmeasured = runChecker(dir, widgetArgs("1.0.1"));
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

test("every missing required option is a usage error", () => {
  withTempGitRepo((dir) => {
    const complete = widgetArgs("1.1.0");
    for (let i = 0; i < complete.length; i += 2) {
      const withoutOne = complete.filter((_, index) => index !== i && index !== i + 1);
      const output = runCheckerExpectFailure(dir, withoutOne, { status: 2 });
      assert.match(output, /usage: check-changelog-semver\.mjs/);
      assert.match(output, /--api-built/);
    }
  });
});

test("CLI: a missing changelog fails and names the declared path; stray positional words are ignored", () => {
  withTempGitRepo((dir) => {
    declare(dir, { widget: { ...WIDGET, changelog: "docs/NOPE.md" } });
    const output = runCheckerExpectFailure(dir, ["stray", ...widgetArgs("1.1.0")]);
    assert.match(output, /changelog not found at .*docs.NOPE\.md/);
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
      const emptyPackage = 'if [ "$1" = restore ]; then /bin/mkdir -p packages/netstandard.library.ref/2.1.0/ref/netstandard2.1; fi\nexit 0';
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

test("CLI: a malformed version is refused by name, without repeating it", () => {
  withTempGitRepo((dir) => {
    commitAndTag(dir, "widget-v1.0.0");
    writeChangelog(dir, "1.1.0");
    const output = runCheckerExpectFailure(dir, widgetArgs("1.1.x-canary"));
    assert.match(output, /--version is not a MAJOR\.MINOR\.PATCH version/);
    assert.doesNotMatch(output, /canary/);
    assert.doesNotMatch(output, /could not be read/);
  });
});

test("refusals never repeat what the inputs contain", () => {
  const canary = "CANARY-5f3a9c-never-echoed";
  const scratch = mkdtempSync(join(tmpdir(), "cultlib-semver-canary-"));
  try {
    const junk = join(scratch, "GameCult.Widget.dll");
    writeFileSync(junk, `MZ not an assembly ${canary}`);
    const canaryChangelog = (dir, version, { breaking = false } = {}) => {
      writeFileSync(join(dir, "CHANGELOG.md"), changelogFor(version, { breaking }).replace("- something changed", `- something changed ${canary}`) + `\n## [0.0.1]\n\n- ${canary}\n`);
    };
    const refuse = (dir, args) => {
      const output = runCheckerExpectFailure(dir, args);
      assert.doesNotMatch(output, new RegExp(canary), `the refusal repeated an input: ${output}`);
      return output;
    };
    withReleasedBaseline({ baseline: { "GameCult.Widget.dll": oldWidget() } }, (dir) => {
      const measured = (version, built, options) => {
        const args = measuredArgs(dir, version, built, options);
        canaryChangelog(dir, version, options);
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
    // the tag's own blob is not a PE image
    withReleasedBaseline({ baseline: { "GameCult.Widget.dll": junk } }, (dir) => {
      const args = measuredArgs(dir, "1.0.1", [oldWidget()]);
      canaryChangelog(dir, "1.0.1");
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
