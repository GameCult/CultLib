// test-declared-dependency-ranges.mjs against a fixture repo: a dependency that gained an export a dependent imports,
// while its manifest still carries the version of its latest release tag, must fail the check, because a consumer's
// range admits that tagged release and it lacks the export. Bumping the version and the range makes it pass.
// The fixture package a builds like cultnet-ts: its build script first generates a module from the repo-level contracts/
// directory, so a tag built by a bare tsc, or archived without contracts/, fails here as it does on the real tree.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const script = join(repoRoot, "scripts", "test-declared-dependency-ranges.mjs");

const write = (root, path, text) => {
  mkdirSync(dirname(join(root, path)), { recursive: true });
  writeFileSync(join(root, path), text);
};
const git = (root, ...args) => execFileSync("git", ["-c", "user.name=t", "-c", "user.email=t@t", ...args], { cwd: root, stdio: "pipe" });
const tsc = "node ../../node_modules/typescript/bin/tsc -p tsconfig.json";
const manifest = (name, version, extra = {}, build = tsc) =>
  JSON.stringify({ name: `@t/${name}`, version, scripts: { build }, ...extra });
const generated = "node tools/generate.mjs && " + tsc;
const tsconfig = JSON.stringify({ compilerOptions: { outDir: "dist", module: "commonjs", target: "es2020", declaration: false }, include: ["src"] });

function fixture() {
  const root = mkdtempSync(join(tmpdir(), "declared-ranges-fixture-"));
  write(root, "package.json", JSON.stringify({ name: "fixture", private: true, workspaces: ["packages/a", "packages/b"] }));
  write(root, "contracts/value.json", "2");
  write(root, "packages/a/tools/generate.mjs",
    'import { readFileSync, writeFileSync } from "node:fs";\n' +
    'writeFileSync("src/data.ts", `export const contractValue = ${readFileSync("../../contracts/value.json", "utf8")};\\n`);\n');
  write(root, "packages/a/package.json", manifest("a", "1.0.0", {}, generated));
  write(root, "packages/a/tsconfig.json", tsconfig);
  write(root, "packages/a/src/index.ts", 'export { contractValue } from "./data";\nexport const foo = 1;\n');
  write(root, "packages/b/package.json", manifest("b", "1.0.0", { dependencies: { "@t/a": "^1.0.0" } }));
  write(root, "packages/b/tsconfig.json", tsconfig);
  write(root, "packages/b/src/index.ts", 'import { foo } from "@t/a";\nexport const b = foo;\n');
  mkdirSync(join(root, "scripts"));
  copyFileSync(script, join(root, "scripts", "test-declared-dependency-ranges.mjs"));
  symlinkSync(join(repoRoot, "node_modules"), join(root, "node_modules"), "junction");
  git(root, "init", "-q", "-b", "main");
  git(root, "add", "package.json", "contracts", "packages", "scripts");
  git(root, "commit", "-q", "-m", "release a 1.0.0");
  git(root, "tag", "a-v1.0.0");
  return root;
}

const check = (root) => spawnSync(process.execPath, [join(root, "scripts", "test-declared-dependency-ranges.mjs")], { cwd: root, encoding: "utf8" });

test("a dependency that gained an export a dependent imports, still at its latest tag's version, fails the check", () => {
  const root = fixture();
  try {
    assert.equal(check(root).status, 0, "the fixture passes before the export is added");

    write(root, "packages/a/src/index.ts", 'export { contractValue } from "./data";\nexport const foo = 1;\nexport const bar = 2;\n');
    write(root, "packages/b/src/index.ts", 'import { foo, bar } from "@t/a";\nexport const b = foo + bar;\n');
    const stale = check(root);
    assert.notEqual(stale.status, 0, stale.stdout);
    assert.match(stale.stderr, /a-v1\.0\.0 does not export bar/u);

    write(root, "packages/a/package.json", manifest("a", "1.1.0", {}, generated));
    write(root, "packages/b/package.json", manifest("b", "1.0.0", { dependencies: { "@t/a": "^1.1.0" } }));
    const bumped = check(root);
    assert.equal(bumped.status, 0, bumped.stderr);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
