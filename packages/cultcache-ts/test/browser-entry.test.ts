import assert from "node:assert/strict";
import { join } from "node:path";
import test from "node:test";
import { runInNewContext } from "node:vm";
import { build } from "esbuild";

// dist-test/test/ -> dist-test/src/
const srcDir = join(__dirname, "..", "src");

async function bundleForBrowser(entry: string): Promise<string> {
  const result = await build({
    entryPoints: [join(srcDir, entry)],
    bundle: true,
    platform: "browser",
    format: "iife",
    globalName: "cultcache",
    write: false,
    logLevel: "silent",
  });
  return result.outputFiles[0].text;
}

test("the browser entry bundles for platform=browser and runs without node", async () => {
  const code = await bundleForBrowser("browser.js");
  assert.ok(!/node:fs|koffi/.test(code), "no node-only store in the browser bundle");
  // A context with no require/process/Buffer: only what a browser page has.
  const sandbox: Record<string, unknown> = { TextEncoder, TextDecoder };
  runInNewContext(code + "\nglobalThis.__api = cultcache;", sandbox);
  const api = sandbox.__api as Record<string, unknown>;
  assert.equal(typeof api.CultCache, "function");
  assert.equal(typeof api.defineDocumentType, "function");
  assert.equal(api.SingleFileMessagePackBackingStore, undefined);
});

test("the node entry still exports the single-file store and everything the browser entry has", async () => {
  const node = await import(join(srcDir, "index.js"));
  const browser = await import(join(srcDir, "browser.js"));
  assert.equal(typeof node.SingleFileMessagePackBackingStore, "function");
  assert.equal(typeof node.readSingleFileStore, "function");
  for (const name of Object.keys(browser)) assert.ok(name in node, `node entry lost ${name}`);
});

test("the package root keeps the node-only store out of the browser build", async () => {
  await assert.rejects(bundleForBrowser("index.js"), /node:fs|node:path|koffi/);
});
