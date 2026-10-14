// Fails when a TypeScript test file exists that no npm script of its package runs.
//
// A package's `test` script names the compiled files it runs, either by glob or one by one, and a file that is compiled
// and listed nowhere passes every suite while proving nothing: cultcache-ts's decode-document-payload test sat in that
// state behind an explicit two-file list. Every test/**/*.test.ts maps to its dist-test/**/*.test.js, and that path has
// to match an argument of `node --test` in the package's `test` script, or in `test:interop` for test/interop.
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const rootManifest = JSON.parse(readFileSync(join(repoRoot, "package.json"), "utf8"));

function testFiles(dir, prefix = "") {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry =>
    entry.isDirectory()
      ? testFiles(join(dir, entry.name), `${prefix}${entry.name}/`)
      : entry.name.endsWith(".test.ts") ? [`${prefix}${entry.name}`] : []);
}

// The paths `node --test` is given in a script: the words of its command after `--test` that are not flags.
function nodeTestArguments(script) {
  const words = script?.split("&&").map(part => part.trim().split(/\s+/)).find(part => part[0] === "node" && part.includes("--test"));
  return words ? words.slice(words.indexOf("--test") + 1).filter(argument => !argument.startsWith("--")) : [];
}

function matches(pattern, path) {
  const [directory, file] = [dirname(pattern), pattern.slice(dirname(pattern).length + 1)];
  if (directory !== dirname(path)) return false;
  return new RegExp(`^${file.split("*").map(part => part.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join("[^/]*")}$`)
    .test(path.slice(directory.length + 1));
}

const unwired = [];
let checked = 0;
for (const workspace of rootManifest.workspaces) {
  const packageRoot = join(repoRoot, workspace);
  const scripts = JSON.parse(readFileSync(join(packageRoot, "package.json"), "utf8")).scripts ?? {};
  for (const file of testFiles(join(packageRoot, "test"))) {
    checked += 1;
    const compiled = `dist-test/test/${file.replace(/\.ts$/, ".js")}`;
    const scriptName = file.startsWith("interop/") ? "test:interop" : "test";
    if (!nodeTestArguments(scripts[scriptName]).some(pattern => matches(pattern, compiled))) {
      unwired.push(`${workspace}: test/${file} is not run by its package's ${scriptName} script`);
    }
  }
}

if (checked === 0) throw new Error("The wiring check found no test files; it is looking in the wrong place.");
if (unwired.length > 0) {
  console.error(unwired.join("\n"));
  process.exit(1);
}
console.log(`${checked} TypeScript test files, all run by their package's script.`);
