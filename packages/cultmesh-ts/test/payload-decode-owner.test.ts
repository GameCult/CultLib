import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import test from "node:test";

// A document payload is decoded by cultcache-ts alone (decodeDocumentPayload); every other value on a wire goes through
// cultnet-ts's decodeWireValue / encodeWireValue. So no file of this package imports msgpack, and the manifest does not
// list it, which keeps a new direct decode from arriving unnoticed.
// Limits: a text scan. It reads the package's src files for the module specifier and the manifest for the dependency
// name. It does not follow a re-export from another package, a specifier assembled at run time, or a hand-written
// decoder; the dependency absence is what makes the specifier unresolvable here when the workspace does not hoist it.
// The package root: the nearest directory above this file (compiled or run in place) that holds a package.json.
function packageRoot(from: string): string {
  return existsSync(join(from, "package.json")) ? from : packageRoot(dirname(from));
}
const root = packageRoot(__dirname);

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory() ? sourceFiles(join(dir, entry.name)) : entry.name.endsWith(".ts") ? [join(dir, entry.name)] : []);
}

test("no source file imports msgpack and the manifest does not list it", () => {
  const manifest = JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as Record<string, Record<string, string> | undefined>;
  for (const section of ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"]) {
    assert.equal(manifest[section]?.["@msgpack/msgpack"], undefined, section);
  }
  const files = sourceFiles(join(root, "src"));
  assert.ok(files.length >= 1, "fixture: the scan reads the package's sources");
  const offenders = files.filter((file) => /@msgpack\/msgpack/.test(readFileSync(file, "utf8")));
  assert.deepEqual(offenders, []);
});
