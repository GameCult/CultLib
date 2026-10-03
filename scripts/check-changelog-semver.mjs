#!/usr/bin/env node
// Refuses a release whose CHANGELOG.md entry claims a breaking change under a
// bump too small to say so, or whose version skips or reverses relative to
// the previous published tag. See docs/semver-policy.md for the policy this
// enforces; this script is only the mechanical check.
//
// Pure, testable logic lives in the exported functions below. `main()` is the
// CLI wrapper that reads the package's declaration (scripts/release-packages.mjs),
// its changelog and the repository's tags, then calls `evaluateRelease`.

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

// --- CLI wrapper: reads the package's declaration, the release record (git tags) and the
// changelog, then delegates to the pure functions above. ---

// The release record, derived from `git tag -l` in --cwd. The previous release is the
// newest '<prefix>-vMAJOR.MINOR.PATCH' tag strictly older than the version, by version order
// (never git's string order), that is also an ancestor of the commit being checked (HEAD of
// --cwd): a backport tagged later on another branch is not in this commit's history, so it
// never changes a rebuild's predecessor. The version's own tag is ignored, so a rebuild at a
// tagged version gets its release-time verdict. A same-prefix tag that does not parse is never
// skipped. A first release is the absence of any older tag in a checkout that holds other tags (a
// checkout with none cannot tell a first release from a missing record), none of which, under
// any prefix, tracks a managed assembly under the package's declared assemblies directory. A
// package that declares none has no such proof: the prefix of an assembly-less package is
// pinned by the suite test that ties every declared prefix to its tag trigger.
function isAncestorOfHead(tag, cwd) {
  const run = spawnSync("git", ["merge-base", "--is-ancestor", `refs/tags/${tag}`, "HEAD"], { cwd, stdio: "ignore" });
  return run.status === 0 ? true : run.status === 1 ? false : null;
}

function resolveRelease(tagPrefix, versionText, cwd, assemblies) {
  let tags;
  try {
    tags = execFileSync("git", ["tag", "-l"], { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] })
      .split("\n")
      .map((line) => line.trim())
      .filter(Boolean);
  } catch {
    return { failure: "the tags of --cwd could not be read (not a git repository?), so the previous release is unknown" };
  }
  const prefix = `${tagPrefix}-v`;
  const current = parseVersion(versionText);
  const own = `${prefix}${formatVersion(current)}`;
  const older = [];
  let newer = null;
  for (const tag of tags) {
    if (!tag.startsWith(prefix)) continue;
    const text = tag.slice(prefix.length);
    if (!VERSION_PATTERN.test(text)) {
      return { failure: `${tag} starts with ${prefix} but is not ${prefix}MAJOR.MINOR.PATCH, so it cannot be ordered among the releases` };
    }
    const candidate = parseVersion(text);
    const order = compareVersions(candidate, current);
    if (order === 0) continue; // the tag being released itself
    if (order > 0) {
      newer ??= tag;
    } else {
      older.push({ tag, version: candidate });
    }
  }
  older.sort((a, b) => compareVersions(b.version, a.version));
  for (const { tag, version } of older) {
    const ancestor = isAncestorOfHead(tag, cwd);
    if (ancestor === null) return { failure: `whether ${tag} is an ancestor of the commit being checked could not be read` };
    if (ancestor) return { previous: formatVersion(version) };
  }
  if (older.length > 0) {
    return { failure: `${older.length} older ${prefix} tag(s) exist but none is an ancestor of the commit being checked, so the previous release is unknown` };
  }
  if (newer !== null) {
    return { failure: `${newer} exists but no ${prefix} tag is older than ${formatVersion(current)}: the version is older than every release` };
  }
  if (!tags.some((tag) => tag !== own)) {
    return { failure: "this checkout holds no tags; a first release cannot be told from a missing record (fetch the tags)" };
  }
  if (typeof assemblies === "string") {
    // Under every other prefix too: a package whose assemblies some tag has shipped is not new,
    // whatever the declared prefix says. This is read from git, never from the changelog's prose.
    const tracked = firstTagTrackingManaged(tags.filter((tag) => tag !== own), assemblies, cwd);
    if (tracked.failure) return tracked;
    if (tracked.tag) {
      return { failure: `no ${prefix} tag is an earlier release, but ${tracked.tag} tracks the managed assembly ${tracked.path}: the package has shipped before, so the declared tag prefix is wrong` };
    }
  }
  return { previous: null };
}

// The first of the tags that tracks a managed assembly anywhere under `dir`, or none. Whether a
// blob is managed is decided by its bytes once per blob id, however many tags track it. A tag
// that cannot be read, or a .dll that is not a PE image, is a refusal, never a tag that tracks
// nothing.
function firstTagTrackingManaged(tags, dir, cwd) {
  const managedBlobs = new Map();
  for (const tag of tags) {
    try {
      const entries = execFileSync("git", ["ls-tree", "-r", "-z", `refs/tags/${tag}`, "--", `${dir}/`], {
        cwd,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "ignore"],
      })
        .split(" ")
        .filter((entry) => entry.toLowerCase().endsWith(".dll"));
      for (const entry of entries) {
        const tab = entry.indexOf("	");
        const id = entry.slice(0, tab).split(" ")[2];
        const path = entry.slice(tab + 1);
        if (!managedBlobs.has(id)) {
          const bytes = execFileSync("git", ["cat-file", "blob", id], { cwd, stdio: ["ignore", "pipe", "ignore"], maxBuffer: 1 << 28 });
          try {
            managedBlobs.set(id, isManagedAssembly(bytes));
          } catch {
            return { failure: `${path} at ${tag} is a .dll but not a PE image, so a first release cannot be told from a mistyped prefix` };
          }
        }
        if (managedBlobs.get(id)) return { tag, path };
      }
    } catch {
      return { failure: `the assemblies ${tag} tracks under ${dir} could not be read, so a first release cannot be told from a mistyped prefix` };
    }
  }
  return {};
}

// The declaration as a whole: every entry names a string tagPrefix and changelog, and no two
// packages share a prefix (each would be measured against the other's releases). Returns a
// refusal text, or null.
export function checkDeclaration(declared) {
  const seen = new Map();
  for (const [name, entry] of Object.entries(declared ?? {})) {
    if (entry == null || typeof entry.tagPrefix !== "string" || typeof entry.changelog !== "string") {
      return `${name} must declare a string tagPrefix and a string changelog`;
    }
    if (seen.has(entry.tagPrefix)) return `${seen.get(entry.tagPrefix)} and ${name} share one tag prefix`;
    seen.set(entry.tagPrefix, name);
  }
  return null;
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

// Whether a PE image is a managed assembly: its optional header's data directory 14 (the
// CLI header) is non-empty. A PE without one is native and ApiCompat cannot read it. A buffer
// that is not a PE image, or is cut short, throws: decided from the bytes, never from the
// file's name or place.
export function isManagedAssembly(bytes) {
  const need = (end) => {
    if (end > bytes.length) throw new RangeError("not a PE image");
  };
  need(0x40);
  if (bytes.readUInt16LE(0) !== 0x5a4d) throw new RangeError("not a PE image");
  const pe = bytes.readUInt32LE(0x3c);
  need(pe + 24);
  if (bytes.readUInt32LE(pe) !== 0x00004550) throw new RangeError("not a PE image");
  const optionalSize = bytes.readUInt16LE(pe + 20);
  const optional = pe + 24;
  const magic = bytes.readUInt16LE(optional);
  if (magic !== 0x10b && magic !== 0x20b) throw new RangeError("not a PE image");
  const directories = optional + (magic === 0x10b ? 96 : 112);
  need(directories);
  if (bytes.readUInt32LE(directories - 4) <= 14 || optionalSize < directories + 15 * 8 - optional) return false;
  need(directories + 15 * 8);
  return bytes.readUInt32LE(directories + 14 * 8) !== 0 && bytes.readUInt32LE(directories + 14 * 8 + 4) !== 0;
}

// The managed assemblies the previous tag tracked anywhere under `dir`, as git blobs: no
// worktree, no network, and unaffected by what the working tree holds now. Each is written to
// `into` by file name. A git failure is a refusal that names the step, never git's own output.
function extractBaseline({ tag, dir, into, cwd }) {
  const git = (args, options) => execFileSync("git", args, { cwd, stdio: ["ignore", "pipe", "ignore"], ...options });
  const blobs = [];
  try {
    const paths = git(["ls-tree", "-r", "-z", "--name-only", tag, "--", `${dir}/`], { encoding: "utf8" })
      .split("\0")
      .filter((path) => path.toLowerCase().endsWith(".dll"));
    for (const path of paths) blobs.push({ path, bytes: git(["show", `${tag}:${path}`], { maxBuffer: 1 << 28 }) });
  } catch {
    return { failure: `git could not read the assemblies tracked under ${dir} at ${tag}` };
  }
  const names = [];
  for (const { path, bytes } of blobs) {
    let managed;
    try {
      managed = isManagedAssembly(bytes);
    } catch {
      return { failure: `${path} at ${tag} is a .dll but not a PE image` };
    }
    if (!managed) continue;
    const name = basename(path);
    if (names.includes(name)) return { failure: `${tag} tracks two managed assemblies named ${name} under ${dir}` };
    writeFileSync(join(into, name), bytes);
    names.push(name);
  }
  return { names };
}

// Compares each built assembly with its namesake at the previous tag. Measuring nothing is a
// failure: the declared directory must hold a managed assembly at the tag. A managed assembly
// the tag tracked and the build no longer hands over is itself a break, whatever its name or
// place; a built assembly with no namesake is an addition and is not examined.
export function measureApiBreaks({ tagPrefix, previousVersion, assemblies, built, refs, cwd }) {
  const tag = `${tagPrefix}-v${previousVersion}`;
  const work = mkdtempSync(join(tmpdir(), "cultlib-apicompat-"));
  try {
    const left = join(work, "left");
    mkdirSync(left);
    const baseline = extractBaseline({ tag, dir: assemblies, into: left, cwd });
    if (baseline.failure) return baseline;
    const baselineNames = baseline.names;
    if (baselineNames.length === 0) {
      return { failure: `${tag} tracks no managed assembly under ${assemblies}: the declared directory is missing or moved, or that tag does not track the DLLs` };
    }
    const gate = prepareGate();
    if (gate.failure) return gate;
    const builtNames = new Set(built.map((path) => basename(path)));
    const breaks = baselineNames
      .filter((name) => !builtNames.has(name))
      .map((name) => `assembly ${name} exists at ${tag} but is no longer built`);
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

// Repeatable options collect into arrays. Any other option is a usage error.
const KNOWN = new Set(["package", "version", "cwd", "api-built", "api-refs"]);
const REPEATABLE = new Set(["api-built", "api-refs"]);

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith("--")) continue;
    const key = arg.slice(2);
    if (!KNOWN.has(key)) return { unknown: arg };
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

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const { package: packageName, version, cwd = process.cwd() } = args;
  // The tool runs from scripts/api-gate, so a path given relative to this process is made absolute first.
  const built = (args["api-built"] ?? []).map((path) => resolve(path));
  if (args.unknown || !packageName || !version) {
    console.error(
      "usage: check-changelog-semver.mjs --package <name> --version <x.y.z> [--cwd <dir>] [--api-built <dll>... [--api-refs <dir>...]]",
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
  // The package's release identity is declared once, in the repository being released.
  let declared;
  try {
    declared = (await import(pathToFileURL(resolve(cwd, "scripts", "release-packages.mjs")).href)).default;
  } catch {
    refuse(`${packageName}: scripts/release-packages.mjs could not be loaded from --cwd`);
    return;
  }
  const malformed = checkDeclaration(declared);
  if (malformed) {
    refuse(`${packageName}: scripts/release-packages.mjs is malformed: ${malformed}`);
    return;
  }
  const entry = Object.hasOwn(declared ?? {}, packageName) ? declared[packageName] : null;
  if (entry == null) {
    refuse(`${packageName}: not a package declared in scripts/release-packages.mjs`);
    return;
  }
  const { tagPrefix, assemblies } = entry;
  const changelog = resolve(cwd, entry.changelog);
  let changelogText;
  try {
    changelogText = readFileSync(changelog, "utf8");
  } catch (err) {
    refuse(`${packageName}: ${err.code === "ENOENT" ? "changelog not found" : "changelog cannot be read"} at ${changelog}`);
    return;
  }
  if (typeof assemblies === "string" && built.length === 0) {
    refuse(`${packageName}: ships the assemblies under ${assemblies}, so the release must hand them over with --api-built`);
    return;
  }
  if (typeof assemblies !== "string" && built.length > 0) {
    refuse(`${packageName}: declares no assemblies directory, so --api-built has nothing to be measured against`);
    return;
  }
  const release = resolveRelease(tagPrefix, version, cwd, assemblies);
  if (release.failure) {
    refuse(`${packageName}: ${release.failure}`);
    return;
  }
  const previousVersion = release.previous;
  let measuredBreaks = [];
  if (previousVersion != null && built.length > 0) {
    const measurement = measureApiBreaks({
      tagPrefix,
      previousVersion,
      assemblies,
      built,
      refs: (args["api-refs"] ?? []).map((path) => resolve(path)),
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
  await main();
}
