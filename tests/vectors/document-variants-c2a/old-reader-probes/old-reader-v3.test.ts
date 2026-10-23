import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";

// Copied into a checkout of cultcache-rs-v0.3.1 (02cf04b5) by old-reader-refusal.sh. Not part of this tree's suite.
// The vector is the store C2aGateWriter wrote through CultCacheMessagePack.Create.
const vector = resolve(__dirname, "../../../../tests/vectors/document-variants-c2a/c2a-written-ids.msgpack");
const envelope = { key: "probe", type: "probe", payload: documentPayloadFromBytes(new Uint8Array([0x80])), storedAt: "2026-10-22T00:00:00Z" };

async function copyOfTheVector(): Promise<{ file: string; bytes: Buffer }> {
  const bytes = await readFile(vector);
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-oldreader-")), "store.msgpack");
  await writeFile(file, bytes);
  return { file, bytes };
}

function refusesV3(label: string) {
  return (error: Error) => {
    console.log(`OLD-READER typescript refused ${label}: ` + error.message);
    return /cultcache\.store\.v3/u.test(error.message);
  };
}

test("old TypeScript reader refuses the C2a element-id store at read", async () => {
  const { file, bytes } = await copyOfTheVector();
  await assert.rejects(() => new SingleFileMessagePackBackingStore(file).pullAll(), refusesV3("pullAll"));
  assert.equal(Buffer.compare(await readFile(file), bytes), 0);
});

test("old TypeScript reader refuses a per-record push to the C2a element-id store", async () => {
  const { file, bytes } = await copyOfTheVector();
  await assert.rejects(() => new SingleFileMessagePackBackingStore(file).push(envelope), refusesV3("push"));
  assert.equal(Buffer.compare(await readFile(file), bytes), 0);
});

test("old TypeScript reader refuses a whole-store pushAll over the C2a element-id store", async () => {
  const { file, bytes } = await copyOfTheVector();
  await assert.rejects(() => new SingleFileMessagePackBackingStore(file).pushAll([envelope]), refusesV3("pushAll"));
  assert.equal(Buffer.compare(await readFile(file), bytes), 0);
});
