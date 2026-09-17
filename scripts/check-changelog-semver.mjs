#!/usr/bin/env node
// Refuses a release whose CHANGELOG.md entry claims a breaking change under a
// bump too small to say so, or whose version skips or reverses relative to
// the previous published tag. See docs/semver-policy.md for the policy this
// enforces; this script is only the mechanical check.
//
// Pure, testable logic lives in the exported functions below. `main()` is the
// CLI wrapper that reads the changelog file and asks git for the previous
// published tag, then calls `evaluateRelease`.

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

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
// but a changelog entry is still required).
export function evaluateRelease({ packageName, changelogText, version, previousVersion }) {
  const next = parseVersion(version);
  let breaking;
  try {
    breaking = hasBreakingSection(changelogText, version);
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

  if (!breaking) {
    return { ok: true, reason: `${packageName}: ${version} is a ${bump} bump over ${previousVersion}, no breaking section` };
  }

  const pre1 = isPreOneZero(prev);
  const requiredLanes = pre1 ? ["major", "minor"] : ["major"];
  if (!requiredLanes.includes(bump)) {
    const requirement = pre1
      ? `at least a minor bump (the 0.y.z convention's breaking lane)`
      : `a major bump`;
    return {
      ok: false,
      reason: `${packageName}: ${version}'s changelog has a "${BREAKING_HEADING}" section but is only a ${bump} bump ` +
        `over ${previousVersion}; a breaking release needs ${requirement}`,
    };
  }

  return { ok: true, reason: `${packageName}: ${version} is a ${bump} bump over ${previousVersion} and its "${BREAKING_HEADING}" section agrees` };
}

// --- CLI wrapper: resolves the previous published tag via git and reads the
// changelog off disk, then delegates to the pure functions above. ---

function resolvePreviousVersion(tagPrefix, currentVersion, cwd) {
  let tags;
  try {
    tags = execFileSync("git", ["tag", "-l", `${tagPrefix}-v*`], { cwd, encoding: "utf8" })
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean);
  } catch {
    return null;
  }
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

// True when `<tagPrefix>-v<version>` already exists. The check gates a NEW
// release; rebuilding an already-tagged, already-immutable version (routine
// local iteration, CI re-runs) is not a release action and should not keep
// re-litigating history on every build.
function tagAlreadyPublished(tagPrefix, version, cwd) {
  try {
    const output = execFileSync("git", ["tag", "-l", `${tagPrefix}-v${version}`], { cwd, encoding: "utf8" });
    return output.trim().length > 0;
  } catch {
    return false;
  }
}

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith("--")) continue;
    const key = arg.slice(2);
    args[key] = argv[i + 1];
    i += 1;
  }
  return args;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const { package: packageName, changelog, version, "tag-prefix": tagPrefix, cwd = process.cwd() } = args;
  if (!packageName || !changelog || !version || !tagPrefix) {
    console.error(
      "usage: check-changelog-semver.mjs --package <name> --changelog <path> --version <x.y.z> --tag-prefix <prefix> [--cwd <dir>]",
    );
    process.exitCode = 2;
    return;
  }
  if (tagAlreadyPublished(tagPrefix, version, cwd)) {
    console.log(`${packageName}: ${version} is already published as ${tagPrefix}-v${version}; not re-checking a rebuild of immutable history`);
    return;
  }
  if (!existsSync(changelog)) {
    console.error(`${packageName}: changelog not found at ${changelog}`);
    process.exitCode = 1;
    return;
  }
  const changelogText = readFileSync(changelog, "utf8");
  const previousVersion = resolvePreviousVersion(tagPrefix, version, cwd);
  const result = evaluateRelease({ packageName, changelogText, version, previousVersion });
  console.log(result.reason);
  if (!result.ok) {
    process.exitCode = 1;
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  main();
}
