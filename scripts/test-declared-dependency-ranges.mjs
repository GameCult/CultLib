// Observes the dependency ranges the TypeScript workspace packages declare, not the workspace symlinks that satisfy them.
//
// Every workspace test resolves a sibling package through node_modules/<name> -> packages/<dir>, so a package that starts
// importing a new export from a sibling passes whatever range it declares. A consumer outside the workspace installs the
// newest published version the range admits. This check builds every tagged release (`<dir>-v<version>`) that a declared
// range admits and requires each runtime value the dependent imports from that package to be an export of it.
//
//   node scripts/test-declared-dependency-ranges.mjs
//
// It needs the workspace installed (typescript at the repo root) and git with the release tags. A package whose range
// admits no tagged release other than the workspace's own passes, because nothing a consumer could install lacks the export.
// Namespace imports, default imports, type-only imports and subpath imports are not checked.

import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const rootRequire = createRequire(join(repoRoot, "package.json"));
const ts = rootRequire("typescript");
const semver = createRequire(join(npmRoot(), "package.json"))("semver");

function npmRoot() {
  const cli = process.env.npm_execpath ?? join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
  return dirname(dirname(cli));
}

function readJson(path) {
  return JSON.parse(readFileSync(path, "utf8"));
}

function git(...args) {
  return execFileSync("git", args, { cwd: repoRoot, encoding: "utf8" });
}

// The runtime names `sourceText` imports from, or re-exports from, exactly `packageName`.
function importedNames(sourceText, packageName) {
  const file = ts.createSourceFile("source.ts", sourceText, ts.ScriptTarget.Latest, true);
  const names = new Set();
  for (const statement of file.statements) {
    const from = statement.moduleSpecifier;
    if (!from || !ts.isStringLiteral(from) || from.text !== packageName) {
      continue;
    }

    let clause;
    if (ts.isImportDeclaration(statement) && statement.importClause && !statement.importClause.isTypeOnly) {
      clause = statement.importClause.namedBindings;
    } else if (ts.isExportDeclaration(statement) && !statement.isTypeOnly) {
      clause = statement.exportClause;
    }

    if (clause && (ts.isNamedImports(clause) || ts.isNamedExports(clause))) {
      for (const element of clause.elements) {
        if (!element.isTypeOnly) {
          names.add((element.propertyName ?? element.name).text);
        }
      }
    }
  }

  return [...names].sort();
}

function sourceFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return sourceFiles(path);
    }

    return entry.name.endsWith(".ts") && !entry.name.endsWith(".d.ts") ? [path] : [];
  });
}

// Builds the package as the tag released it, in a scratch directory that borrows the workspace's node_modules.
function buildRelease(directory, tag, scratch) {
  const archive = join(scratch, `${tag}.tar`);
  git("archive", "--format=tar", "-o", archive, tag, `packages/${directory}`);
  const extracted = join(scratch, tag);
  mkdirSync(extracted);
  execFileSync("tar", ["-xf", archive, "-C", extracted]);
  symlinkSync(join(repoRoot, "node_modules"), join(extracted, "node_modules"), "junction");
  const packageRoot = join(extracted, "packages", directory);
  execFileSync(process.execPath, [rootRequire.resolve("typescript/bin/tsc"), "-p", "tsconfig.json"], { cwd: packageRoot, stdio: "inherit" });
  return rootRequire(join(packageRoot, "dist", "index.js"));
}

const workspaces = readJson(join(repoRoot, "package.json")).workspaces.map((directory) => {
  const manifest = readJson(join(repoRoot, directory, "package.json"));
  return { directory: directory.replace(/^packages\//u, ""), root: join(repoRoot, directory), manifest };
});
const byName = new Map(workspaces.map((workspace) => [workspace.manifest.name, workspace]));
const tags = git("tag", "-l").split(/\r?\n/u).filter(Boolean);
assert.ok(tags.length > 0, "this clone has no release tags; fetch them (git fetch --tags) so the admitted releases can be built");
const scratch = mkdtempSync(join(tmpdir(), "cultlib-declared-ranges-"));
const built = new Map();
let checked = 0;

try {
  for (const dependent of workspaces) {
    for (const [name, range] of Object.entries(dependent.manifest.dependencies ?? {})) {
      const dependency = byName.get(name);
      if (!dependency) {
        continue;
      }

      assert.ok(
        semver.satisfies(dependency.manifest.version, range),
        `${dependent.manifest.name} declares ${name} ${range}, which excludes the workspace's own ${dependency.manifest.version}`,
      );
      const src = join(dependent.root, "src");
      const imported = existsSync(src)
        ? [...new Set(sourceFiles(src).flatMap((file) => importedNames(readFileSync(file, "utf8"), name)))].sort()
        : [];
      const prefix = `${dependency.directory}-v`;
      const admitted = tags.filter((tag) => {
        const version = tag.startsWith(prefix) ? tag.slice(prefix.length) : undefined;
        return version !== undefined && semver.valid(version) !== null && semver.satisfies(version, range) && version !== dependency.manifest.version;
      });
      for (const tag of admitted) {
        if (!built.has(tag)) {
          built.set(tag, buildRelease(dependency.directory, tag, scratch));
        }

        const exports = built.get(tag);
        const missing = imported.filter((symbol) => !(symbol in exports));
        assert.deepEqual(
          missing,
          [],
          `${dependent.manifest.name} declares ${name} ${range}, which admits ${tag}, and ${tag} does not export ${missing.join(", ")}`,
        );
      }

      checked += 1;
      console.log(`${dependent.manifest.name} -> ${name} ${range}: ${imported.length} imported names, ${admitted.length} admitted tagged releases checked`);
    }
  }

  assert.ok(checked > 0, "no workspace package declares a dependency on another workspace package");
  console.log(`Declared dependency ranges: ${checked} checked`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
