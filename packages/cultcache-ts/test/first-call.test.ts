import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { z } from "zod";

import {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentRegistry,
  defineDocumentType,
} from "../src/index";

// The TypeScript first-call snippet that docs/start/README.md links: define a typed document,
// store it in a .cc file, read it back through a second cache. Keep it small; it is the front door.
const itemDocument = defineDocumentType({
  type: "item",
  schemaId: "gamecult.first_call_item.v1",
  schemaName: "gamecult.first_call_item",
  schemaVersion: "gamecult.first_call_item.v1",
  schema: z.object({ name: z.string(), category: z.string(), value: z.number().int() }),
  name: "name",
  indexes: { category: "category" },
});
const registry = defineDocumentRegistry(itemDocument);

test("define, store and read back a typed document", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-first-call-"));
  const path = join(dir, "first-call.cc");
  try {
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

    assert.equal(reopened.get(itemDocument, "item:potion")?.value, 50);
    assert.equal(reopened.getByName(itemDocument, "Potion")?.category, "Consumable");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
