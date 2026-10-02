#!/usr/bin/env node
// Refuses a release whose CHANGELOG.md entry claims a breaking change under a
// bump too small to say so, or whose version skips or reverses relative to
// the previous published tag. See docs/semver-policy.md for the policy this
// enforces; this script is only the mechanical check.
//
// Pure, testable logic lives in the exported functions below. `main()` is the
// CLI wrapper that reads the changelog file and asks git for the previous
// published tag, then calls `evaluateRelease`.

import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

// The exact heading spelling the policy mandates for a breaking-change
// section. `unity/org.gamecult.cultlib/CHANGELOG.md`'s 1.0.60 entry set this
// precedent; this check is what makes that heading load-bearing instead of
// decorative.
export const BREAKING_HEADING = "### Breaking";

const VERSION_PATTERN = /^(\d+)\.(\d+)\.(\d+)$/;

export function parseVersion(version) {
  const match = VERSION_PATTERN.exec(version.trim());
  if (!match) {
    throw new Error(`not a MAJOR.MINOR.PATCH version: "${version}"`);
  }
  const [, major, minor, patch] = match;
  return { major: Number(major), minor: Number(minor), patch: Number(patch) };
}

function formatVersion(v) {
  return `${v.major}.${v.minor}.${v.patch}`;
}

// The three (and only three) versions that legitimately follow `prev`: one
// component incremented, everything to its right reset to zero. Anything
// else is a skip, a reversal, a no-op, or a multi-step jump — all refused by
// the same rule, since none of them is "the next version".
export function nextVersions(prev) {
  return {
    major: { major: prev.major + 1, minor: 0, patch: 0 },
    minor: { major: prev.major, minor: prev.minor + 1, patch: 0 },
    patch: { major: prev.major, minor: prev.minor, patch: prev.patch + 1 },
  };
}

function sameVersion(a, b) {
  return a.major === b.major && a.minor === b.minor && a.patch === b.patch;
}

function compareVersions(a, b) {
  if (a.major !== b.major) return a.major - b.major;
  if (a.minor !== b.minor) return a.minor - b.minor;
  return a.patch - b.patch;
}

// Classifies `next` relative to `prev`. Returns one of "major" | "minor" |
// "patch" when `next` is exactly the next version in that lane, or an
// { invalid: "skip" | "reverse" | "no-op" } descriptor otherwise.
export function classifyBump(prev, next) {
  if (sameVersion(prev, next)) {
    return { invalid: "no-op" };
  }
  const candidates = nextVersions(prev);
  for (const [lane, candidate] of Object.entries(candidates)) {
    if (sameVersion(candidate, next)) {
      return lane;
    }
  }
  return { invalid: compareVersions(next, prev) < 0 ? "reverse" : "skip" };
}

// Extracts the `## [<version>]` section of a changelog and reports whether it
// carries a BREAKING_HEADING subsection. Throws if the version has no entry
// at all — an undocumented release is refused the same as a mislabelled one.
export function hasBreakingSection(changelogText, version) {
  const sectionHeading = new RegExp(
    `^##\\s*\\[?${version.replace(/\./g, "\\.")}\\]?\\s*$`,
    "m",
  );
  const startMatch = sectionHeading.exec(changelogText);
  if (!startMatch) {
    throw new Error(`no "## [${version}]" entry found in changelog`);
  }
  const start = startMatch.index + startMatch[0].length;
  const rest = changelogText.slice(start);
  const nextSectionMatch = /^##\s/m.exec(rest);
  const section = nextSectionMatch ? rest.slice(0, nextSectionMatch.index) : rest;
  const breakingHeading = new RegExp(`^${BREAKING_HEADING.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*$`, "m");
  return breakingHeading.test(section);
}

// The pre-1.0 lane a package is releasing FROM. `0.y.z` treats a minor bump
// as the breaking lane (semver's own 0.y.z convention: MAJOR is pinned at 0,
// so MINOR carries what MAJOR would mean once the package reaches 1.0.0).
function isPreOneZero(prev) {
  return prev.major === 0;
}

// Evaluates one release. `previousVersion` may be null for a package's first
// ever release (nothing to compare against, so the bump rule does not apply,
// but a changelog entry is still required). `measuredBreaks` lists the public
// API diagnostics measured against the previous tag's assemblies; a non-empty
// list makes the release breaking whether or not the changelog says so, and
// the changelog must then declare it.
export function evaluateRelease({ packageName, changelogText, version, previousVersion, measuredBreaks = [] }) {
  const next = parseVersion(version);
  let declared;
  try {
    declared = hasBreakingSection(changelogText, version);
  } catch (err) {
    return { ok: false, reason: `${packageName}: ${err.message}` };
  }

  if (previousVersion == null) {
    return { ok: true, reason: `${packageName}: first release (${version}); no prior version to compare against` };
  }

  const prev = parseVersion(previousVersion);
  const bump = classifyBump(prev, next);

  if (typeof bump !== "string") {
    const verb = bump.invalid === "no-op" ? "repeats" : bump.invalid === "reverse" ? "reverses" : "skips ahead of";
    return {
      ok: false,
      reason: `${packageName}: version ${version} ${verb} the previous published version ${previousVersion}; ` +
        `the next version must be exactly one of ${formatVersion(nextVersions(prev).major)}, ` +
        `${formatVersion(nextVersions(prev).minor)}, or ${formatVersion(nextVersions(prev).patch)}`,
    };
  }

  const measured = measuredBreaks.length > 0;
  if (measured && !declared) {
    const shown = measuredBreaks.slice(0, 5).join("; ") + (measuredBreaks.length > 5 ? "; ..." : "");
    return {
      ok: false,
      reason: `${packageName}: ${version} breaks ${measuredBreaks.length} public API member(s) measured against ` +
        `${previousVersion} (${shown}); declare them under "${BREAKING_HEADING}" in the changelog and bump accordingly`,
    };
  }
  if (!declared) {
    return { ok: true, reason: `${packageName}: ${version} is a ${bump} bump over ${previousVersion}, no breaking section` };
  }
  const evidence = measured
    ? `a "${BREAKING_HEADING}" section and ${measuredBreaks.length} measured public API break(s)`
    : `a "${BREAKING_HEADING}" section`;

  const pre1 = isPreOneZero(prev);
  const requiredLanes = pre1 ? ["major", "minor"] : ["major"];
  if (!requiredLanes.includes(bump)) {
    const requirement = pre1
      ? `at least a minor bump (the 0.y.z convention's breaking lane)`
      : `a major bump`;
    return {
      ok: false,
      reason: `${packageName}: ${version} has ${evidence} but is only a ${bump} bump ` +
        `over ${previousVersion}; a breaking release needs ${requirement}`,
    };
  }

  return { ok: true, reason: `${packageName}: ${version} is a ${bump} bump over ${previousVersion} and has ${evidence}, which agree` };
}

// --- CLI wrapper: resolves the previous published tag via git and reads the
// changelog off disk, then delegates to the pure functions above. ---

function resolvePreviousVersion(tagPrefix, currentVersion, cwd) {
  // A directory that cannot list tags (not a git repository, git missing) throws: that
  // is not evidence that there was no previous release.
  const tags = execFileSync("git", ["tag", "-l", `${tagPrefix}-v*`], { cwd, encoding: "utf8" })
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
  const prefix = `${tagPrefix}-v`;
  const current = parseVersion(currentVersion);
  let best = null;
  for (const tag of tags) {
    if (!tag.startsWith(prefix)) continue;
    const versionText = tag.slice(prefix.length);
    let candidate;
    try {
      candidate = parseVersion(versionText);
    } catch {
      continue;
    }
    if (sameVersion(candidate, current)) continue; // the tag being released itself
    if (compareVersions(candidate, current) >= 0) continue; // ignore anything not older
    if (best === null || compareVersions(candidate, best) > 0) {
      best = candidate;
    }
  }
  return best ? formatVersion(best) : null;
}

// --- Measured public API: the built DLLs are compared with the DLLs the
// previous tag tracked, by Microsoft.DotNet.ApiCompat.Tool. The tool, and the
// netstandard reference assemblies it needs to resolve inherited members, are
// pinned in scripts/api-gate/ and restored there; nothing here compares
// members itself. ---

const gateRoot = join(dirname(fileURLToPath(import.meta.url)), "api-gate");
const DIAGNOSTIC_LINE = /^CP\d{4}: /;

// One ApiCompat run's verdict. A diagnostic line is a measured break. A
// non-zero exit with no diagnostic line means the tool did not measure
// anything (missing tool, unreadable input, crash): that is a failure, never a
// pass.
export function readApiCompatRun({ status, output }) {
  const breaks = output.split(/\r?\n/).filter((line) => DIAGNOSTIC_LINE.test(line));
  if (breaks.length === 0 && status !== 0) {
    return { failure: `apicompat exited ${status} without measuring: ${output.trim().split(/\r?\n/).slice(0, 5).join(" | ")}` };
  }
  return { breaks };
}

function runApiCompat(args) {
  const run = spawnSync("dotnet", ["tool", "run", "apicompat", ...args], { cwd: gateRoot, encoding: "utf8" });
  return readApiCompatRun({ status: run.status, output: `${run.stdout}${run.stderr}` });
}

function lastLines(text) {
  return text.trim().split(/\r?\n/).slice(-3).join(" | ");
}

// Restores the pinned tool and the pinned netstandard reference package, and
// returns the reference assemblies' directory. Both come from scripts/api-gate/
// and need NuGet only until they are cached there.
function prepareGate() {
  if (!existsSync(join(gateRoot, ".config", "dotnet-tools.json"))) {
    return { failure: "the api-gate tool manifest (scripts/api-gate/.config/dotnet-tools.json) is missing" };
  }
  for (const [what, args] of [["dotnet tool restore", ["tool", "restore"]], ["dotnet restore", ["restore", "ApiRefs.csproj", "--nologo", "-v", "q"]]]) {
    const run = spawnSync("dotnet", args, { cwd: gateRoot, encoding: "utf8" });
    if (run.status !== 0) {
      return { failure: `${what} failed: ${run.error ? run.error.message : lastLines(`${run.stdout}${run.stderr}`)}` };
    }
  }
  const packageDir = join(gateRoot, "packages", "netstandard.library.ref");
  const versions = existsSync(packageDir) ? readdirSync(packageDir).sort() : [];
  const refs = versions.length > 0 ? join(packageDir, versions[0], "ref", "netstandard2.1") : null;
  if (refs == null || !existsSync(join(refs, "netstandard.dll"))) {
    return { failure: "the pinned netstandard reference assemblies were not restored under scripts/api-gate/packages" };
  }
  return { refs };
}

// The DLLs the previous tag tracked under `baselinePath`, as git blobs: no
// worktree, no network, and unaffected by what the working tree holds now.
function extractBaseline({ tag, baselinePath, into, cwd }) {
  const dir = baselinePath.replace(/\\/g, "/").replace(/\/+$/, "");
  const listing = execFileSync("git", ["ls-tree", "--name-only", tag, `${dir}/`], { cwd, encoding: "utf8" });
  const names = listing.split("\n").map((line) => basename(line.trim())).filter((name) => name.toLowerCase().endsWith(".dll"));
  for (const name of names) {
    const blob = execFileSync("git", ["show", `${tag}:${dir}/${name}`], { cwd, maxBuffer: 1 << 28 });
    writeFileSync(join(into, name), blob);
  }
  return { dir, names };
}

// Compares each built assembly with its namesake at the previous tag. Measuring
// nothing is a failure: the baseline path must hold DLLs at the tag, and every
// built assembly must have a namesake there unless the caller declares it new
// (`declaredNew`). A GameCult.* assembly the tag tracked and the build no longer
// produces is itself a break.
export function measureApiBreaks({ tagPrefix, previousVersion, baselinePath, built, refs, declaredNew = [], cwd }) {
  const tag = `${tagPrefix}-v${previousVersion}`;
  const work = mkdtempSync(join(tmpdir(), "cultlib-apicompat-"));
  try {
    const left = join(work, "left");
    mkdirSync(left);
    const { dir, names: baselineNames } = extractBaseline({ tag, baselinePath, into: left, cwd });
    if (baselineNames.length === 0) {
      return { failure: `${tag} tracks no assembly under ${dir}: the baseline path is missing or moved, or that tag does not track the DLLs` };
    }
    const builtNames = new Set(built.map((path) => basename(path)));
    for (const name of declaredNew) {
      if (baselineNames.includes(name)) return { failure: `${name} is declared new but ${tag} already tracks it under ${dir}` };
    }
    for (const name of builtNames) {
      if (!baselineNames.includes(name) && !declaredNew.includes(name)) {
        return { failure: `${name} is built but ${tag} does not track it under ${dir}; if it is a new assembly, declare it with --api-new ${name}` };
      }
    }
    const gate = prepareGate();
    if (gate.failure) return gate;
    const breaks = [];
    for (const name of baselineNames) {
      if (name.startsWith("GameCult.") && !builtNames.has(name)) {
        breaks.push(`assembly ${name} exists at ${tag} but is no longer built`);
      }
    }
    for (const path of built) {
      const name = basename(path);
      if (!baselineNames.includes(name)) continue;
      const leftRefs = [left, gate.refs].join(",");
      const rightRefs = [...new Set([dirname(path), ...refs, gate.refs])].join(",");
      const run = runApiCompat(["--left-assembly", join(left, name), "--right-assembly", path, "--left-assembly-references", leftRefs, "--right-assembly-references", rightRefs]);
      if (run.failure) return { failure: `${name}: ${run.failure}` };
      breaks.push(...run.breaks);
    }
    return { breaks };
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

// Repeatable options collect into arrays; flags take no value.
const REPEATABLE = new Set(["api-built", "api-refs", "api-new"]);
const FLAGS = new Set(["first-release"]);

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith("--")) continue;
    const key = arg.slice(2);
    if (FLAGS.has(key)) {
      args[key] = true;
      continue;
    }
    if (REPEATABLE.has(key)) {
      (args[key] ??= []).push(argv[i + 1]);
    } else {
      args[key] = argv[i + 1];
    }
    i += 1;
  }
  return args;
}

function refuse(message) {
  console.error(message);
  process.exitCode = 1;
}

const UNMEASURED_NOTICE =
  'public API measured; changes ApiCompat cannot see (see "Unmeasured changes" in docs/semver-policy.md) must still be declared under "### Breaking"';

function main() {
  const args = parseArgs(process.argv.slice(2));
  const { package: packageName, changelog, version, "tag-prefix": tagPrefix, cwd = process.cwd() } = args;
  // The tool runs from scripts/api-gate, so a path given relative to this process is made absolute first.
  const built = (args["api-built"] ?? []).map((path) => resolve(path));
  if (!packageName || !changelog || !version || !tagPrefix || (built.length > 0) !== Boolean(args["api-baseline-path"])) {
    console.error(
      "usage: check-changelog-semver.mjs --package <name> --changelog <path> --version <x.y.z> --tag-prefix <prefix> [--cwd <dir>] [--first-release]\n" +
        "         [--api-baseline-path <repo-relative dir of tracked DLLs> --api-built <dll>... [--api-refs <dir>...] [--api-new <dll name>...]]",
    );
    process.exitCode = 2;
    return;
  }
  // Checked before anything reads tags: a malformed version is its own refusal, and the
  // value is not repeated back.
  if (!VERSION_PATTERN.test(version.trim())) {
    refuse(`${packageName}: --version is not a MAJOR.MINOR.PATCH version`);
    return;
  }
  if (!existsSync(changelog)) {
    refuse(`${packageName}: changelog not found at ${changelog}`);
    return;
  }
  const changelogText = readFileSync(changelog, "utf8");
  const firstRelease = args["first-release"] === true;
  let previousVersion;
  try {
    previousVersion = resolvePreviousVersion(tagPrefix, version, cwd);
  } catch {
    if (!firstRelease) {
      refuse(`${packageName}: the tags of --cwd could not be read (not a git repository?), so the previous release is unknown; ` +
        "if this is the package's first release, declare it with --first-release");
      return;
    }
    previousVersion = null;
  }
  if (previousVersion == null && !firstRelease) {
    refuse(`${packageName}: no ${tagPrefix}-v tag older than ${version} exists, so there is nothing to compare against; ` +
      "if this is the package's first release, declare it with --first-release");
    return;
  }
  if (previousVersion != null && firstRelease) {
    refuse(`${packageName}: --first-release was declared but ${tagPrefix}-v${previousVersion} already exists`);
    return;
  }
  let measuredBreaks = [];
  if (built.length > 0 && previousVersion != null) {
    const measurement = measureApiBreaks({
      tagPrefix,
      previousVersion,
      baselinePath: args["api-baseline-path"],
      built,
      refs: (args["api-refs"] ?? []).map((path) => resolve(path)),
      declaredNew: args["api-new"] ?? [],
      cwd,
    });
    if (measurement.failure) {
      refuse(`${packageName}: public API could not be measured against ${tagPrefix}-v${previousVersion}: ${measurement.failure}`);
      return;
    }
    measuredBreaks = measurement.breaks;
  }
  const result = evaluateRelease({ packageName, changelogText, version, previousVersion, measuredBreaks });
  console.log(result.reason);
  if (!result.ok) {
    process.exitCode = 1;
    return;
  }
  if (previousVersion != null && built.length > 0) {
    console.log(UNMEASURED_NOTICE);
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  main();
}
