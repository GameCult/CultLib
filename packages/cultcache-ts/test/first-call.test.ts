import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

// The TypeScript first-call snippet that docs/start/README.md shows: define a typed document, store it
// in a .cc file, read it back through a second cache. Keep it small; it is the front door. The docs show
// the region between the `snippet` markers verbatim, except that the package import reads
// "@gamecult/cultcache-ts" where this file reads "../src/index"; the docs test below fails when they differ.
// snippet:begin
import { z } from "zod";
import {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentRegistry,
  defineDocumentType,
} from "../src/index";

const itemDocument = defineDocumentType({
  type: "item",
  schemaId: "gamecult.first_call_item.v1",
  schemaName: "gamecult.first_call_item",
  schemaVersion: "gamecult.first_call_item.v1",
  schema: z.object({ name: z.string(), category: z.string(), value: z.number().int() }),
  name: "name",
});
const registry = defineDocumentRegistry(itemDocument);

async function firstCall(path: string) {
  const cache = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await cache.pullAllBackingStores();
  await cache.put(itemDocument, "item:potion", { name: "Potion", category: "Consumable", value: 50 });

  const reopened = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await reopened.pullAllBackingStores();
  const potion = reopened.get(itemDocument, "item:potion");
  const byName = reopened.getByName(itemDocument, "Potion");
  return { potion, byName };
}
// snippet:end

// The update snippet, which docs/start/README.md shows after the first call and which uses its definitions.
// snippet:update:begin
async function restock(path: string) {
  const cache = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await cache.pullAllBackingStores();
  return cache.update(itemDocument, "item:potion", (item) => {
    const current = item ?? { name: "Potion", category: "Consumable", value: 0 };
    return { ...current, value: current.value + 10 };
  });
}
// snippet:update:end

test("define, store and read back a typed document", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-first-call-"));
  try {
    const { potion, byName } = await firstCall(join(dir, "first-call.cc"));
    assert.equal(potion?.value, 50);
    assert.equal(byName?.category, "Consumable");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("update with retry: restock twice reads value 20 through a second cache", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-first-call-"));
  try {
    const path = join(dir, "restock.cc");
    assert.equal((await restock(path)).outcome, "updated");
    assert.equal((await restock(path)).outcome, "updated");
    const reopened = CultCache.builder()
      .withRegistry(registry)
      .withGenericStore(new SingleFileMessagePackBackingStore(path))
      .build();
    await reopened.pullAllBackingStores();
    assert.equal(reopened.get(itemDocument, "item:potion")?.value, 20);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

// The docs show the region between `// snippet:<region>begin` and `end` markers verbatim, the package import aside.
async function assertDocsShow(region: "" | "update:") {
  // Compiled to dist-test/test/first-call.test.js, so the package root is two levels up.
  const source = await readFile(join(__dirname, "..", "..", "test", "first-call.test.ts"), "utf8");
  const snippet = source
    .split(`// snippet:${region}begin\n`)[1]
    .split(`// snippet:${region}end\n`)[0]
    .replace('"../src/index"', '"@gamecult/cultcache-ts"');
  const docs = (await readFile(join(__dirname, "..", "..", "..", "..", "docs", "start", "README.md"), "utf8")).replace(/\r\n/g, "\n");
  assert.ok(docs.includes("```ts\n" + snippet + "```"), `docs/start/README.md no longer shows the ${region || "first-call "}region of test/first-call.test.ts`);
}

test("docs show the tested snippet", () => assertDocsShow(""));

test("docs show the tested update snippet", () => assertDocsShow("update:"));
