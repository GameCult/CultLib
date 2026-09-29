import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";

// Copied into a checkout of the C0 merge (e382bb4) by old-reader-refusal.sh. Not part of this tree's suite.
test("old TypeScript reader refuses the v3 element-id store by header", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-oldreader-"));
  const file = join(dir, "store.msgpack");
  await writeFile(file, await readFile(resolve(__dirname, "../../../../tests/vectors/document-variants-c2a/v3-base.msgpack")));
  await assert.rejects(() => new SingleFileMessagePackBackingStore(file).pullAll(), (error: Error) => {
    console.log("OLD-READER typescript refused: " + error.message);
    return /cultcache\.store\.v3/u.test(error.message);
  });
});
