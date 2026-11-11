import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { copyFile, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { CultCache } from "../src/cult-cache";
import { defineDocumentType, documentPayloadBytes, documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore, readSingleFileStore } from "../src/single-file-messagepack-backing-store";
import { SchemaConflictError } from "../src/store-format";
import type { AnyCultCacheDocumentDefinition, CultCacheEnvelope } from "../src/types";

// variants:ruling:cache-refuses-retype, write-ownership-by-written-type, unheld-removal-refused-at-commit. The vectors in
// tests/vectors/key-identity are written by the C# reference (see its README for the rule and the row shapes); this walks
// every row against the TS cache. A leaf keeps no verdict of its own.

function findVectors(from: string): string {
  for (let dir = from; ; dir = resolve(dir, "..")) {
    if (existsSync(join(dir, "tests", "vectors", "key-identity"))) return join(dir, "tests", "vectors", "key-identity");
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/key-identity not found above the test.");
  }
}
const vectors = findVectors(__dirname);

// One string slot [0: Text]; the schema ids are the ones C# wrote (README). The type is the schema name, the name a record
// read back from a store carries as its type (the store's delete matches on it).
function kiType(type: string, id: string) {
  const schemaName = `tests.ki_${type}`;
  return defineDocumentType({
    type: schemaName,
    schemaId: `sha256:${id}`,
    schemaName,
    schemaVersion: `${schemaName}.v1`,
    schema: z.object({ text: z.string() }),
    formatter: {
      encode: (value: { text: string }) => encode([value.text]),
      decode: (payload: Uint8Array) => ({ text: (decode(payload) as [string])[0] }),
    },
  });
}
const types: Record<string, AnyCultCacheDocumentDefinition> = {
  alpha: kiType("alpha", "b20703a98f49c253898404ab50ba2c630720f0b8a5e1a41614d5650be22799fe"),
  beta: kiType("beta", "dee4b727cf2ee04e61ab635ff0c5f3dca7345a411dc5f1018e583ab16009cf94"),
  gamma: kiType("gamma", "fbc26e351e00c9e7185e8de171c7486aba707c229c949f0aae6b889288af5f6d"),
};

type Op = [string, ...unknown[]];
type Row = [string, string[], Op[], [[string] | null, [string, string, string][], [string, string][] | null, string[], string[]]];

const compare = (left: string, right: string) => (left < right ? -1 : left > right ? 1 : 0);
const textOf = (envelope: CultCacheEnvelope) => (decode(documentPayloadBytes(envelope.payload)) as [string])[0];
const stored = (path: string) => new Map(readSingleFileStore(path).envelopes.map((e) => [e.key, e]));
const sameRecord = (left: CultCacheEnvelope | undefined, right: CultCacheEnvelope | undefined) =>
  left !== undefined && right !== undefined && left.schemaId === right.schemaId && left.storedAt === right.storedAt &&
  Buffer.compare(Buffer.from(documentPayloadBytes(left.payload)), Buffer.from(documentPayloadBytes(right.payload))) === 0;

test("vectors rows.msgpack: every row's refusal, held records, file, foreign list and kept bytes", async () => {
  const rows = decode(await readFile(join(vectors, "rows.msgpack"))) as Row[];
  assert.equal(rows.length, 11);
  let walked = 0;
  const failures: string[] = [];
  for (const [label, registered, ops, expect] of rows) {
    const dir = await mkdtemp(join(tmpdir(), "ki-"));
    try {
      const [refusal, held, file, foreign, kept] = expect;
      let counter = 0;
      let path: string | undefined;
      let cache!: CultCache;
      let before: Map<string, CultCacheEnvelope> | undefined;
      const attached: string[] = [];
      const build = (store: string | undefined) => {
        let builder = CultCache.builder();
        for (const name of registered) builder = builder.withDocumentType(types[name]!);
        if (store !== undefined) builder = builder.withGenericStore(new SingleFileMessagePackBackingStore(store));
        return builder.build();
      };
      const fresh = async (name: string) => {
        const copy = join(dir, `${counter++}-${name}`);
        await copyFile(join(vectors, name), copy);
        return copy;
      };

      let refused: unknown;
      let failed = false;
      let beforeBytes: Buffer | undefined;
      let beforeCache: CultCacheEnvelope[] = [];
      let beforeAttached: Buffer[] = [];
      for (const [index, op] of ops.entries()) {
        const last = index === ops.length - 1;
        if (last) {
          beforeCache = cache?.snapshot() ?? [];
          beforeAttached = await Promise.all(attached.map((copy) => readFile(copy)));
          beforeBytes = path !== undefined ? await readFile(path) : undefined;
        }
        try {
          switch (op[0]) {
            case "open": {
              const arg = op[1] as string | null;
              path = arg === "memory" ? undefined : arg === null ? join(dir, "fresh.cc") : await fresh(arg);
              if (path !== undefined && arg !== null) before = stored(path);
              cache = build(path);
              await cache.pullAllBackingStores();
              break;
            }
            case "attach": {
              const copy = await fresh(op[1] as string);
              attached.push(copy);
              await cache.addBackingStore(new SingleFileMessagePackBackingStore(copy), ...(op[2] as string[]).map((name) => types[name]!));
              await cache.pullAllBackingStores();
              break;
            }
            case "reopen":
              cache = build(path);
              await cache.pullAllBackingStores();
              break;
            case "put":
              await cache.put(types[op[1] as string]!, op[2] as string, { text: op[3] as string } as never);
              break;
            case "delete":
              await cache.delete(types[op[1] as string]!, op[2] as string);
              break;
            default:
              assert.fail(`${label}: unknown op ${op[0]}`);
          }
        } catch (error) {
          if (!last) throw error;
          refused = error;
          failed = true;
        }
      }

      if (refusal === null) {
        assert.equal(failed, false, `${label}: the last op lands (${String(refused)})`);
      } else {
        assert.equal(failed, true, `${label}: the last op is refused`);
        const [key] = refusal;
        if (label === "key-in-two-stores-refused") {
          assert.ok((refused as Error).message.includes(key), `${label}: the refusal names the key`);
          assert.deepEqual(cache.snapshot(), beforeCache, `${label}: the earlier pulled state survives`);
          assert.deepEqual(await Promise.all(attached.slice(0, beforeAttached.length).map((copy) => readFile(copy))), beforeAttached, `${label}: the attached files are byte-identical`);
        } else {
          assert.ok(refused instanceof SchemaConflictError, `${label}: a typed schema conflict, got ${String(refused)}`);
          assert.equal((refused as SchemaConflictError).recordKey, key, `${label}: names the key`);
          assert.deepEqual(cache.snapshot(), beforeCache, `${label}: the cache is unchanged`);
          if (path !== undefined) assert.deepEqual(await readFile(path), beforeBytes, `${label}: the store is byte-identical`);
        }
      }

      assert.deepEqual(
        cache.snapshot()
          .map((e) => [e.type.replace("tests.ki_", ""), e.key, textOf(e)] as [string, string, string])
          .sort((l, r) => compare(l[0], r[0]) || compare(l[1], r[1])),
        held,
        `${label}: held`,
      );
      if (path === undefined) {
        assert.equal(file, null, `${label}: no store`);
      } else {
        assert.deepEqual(
          [...stored(path).values()]
            .map((e) => [e.key, e.catalogEntry?.schemaName ?? e.type] as [string, string])
            .sort((l, r) => compare(l[0], r[0])),
          file,
          `${label}: file`,
        );
        if (before !== undefined) {
          const after = stored(path);
          assert.deepEqual(foreign.filter((key) => sameRecord(before!.get(key), after.get(key))), kept, `${label}: kept`);
        }
      }
      assert.deepEqual(cache.foreignRecords().map((record: { key: string }) => record.key), foreign, `${label}: foreign`);
      walked++;
    } catch (error) {
      failures.push(`${label}: ${error instanceof Error ? error.message.split("\n")[0] : String(error)}`);
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  }
  assert.deepEqual(failures, [], "every row holds its verdict");
  assert.equal(walked, 11);
});

// Beyond the vector: what the walker's string-typed rows cannot say.
const CANARY = "CANARY-ki-77c1";
const plain = (type: string, schemaId: string, compatibleSchemaIds?: string[]) =>
  defineDocumentType({ type, schemaId, schemaName: type, schemaVersion: `${type}.v1`, schema: z.object({ v: z.string() }), ...(compatibleSchemaIds ? { compatibleSchemaIds } : {}) });

test("a refusal names the key and schemas and never echoes the written value", async () => {
  const alpha = plain("cn.alpha", "cn.alpha.v1");
  const beta = plain("cn.beta", "cn.beta.v1");
  const cache = CultCache.builder().withDocumentType(alpha).withDocumentType(beta).build();
  await cache.put(alpha, "k", { v: "first" });
  await assert.rejects(cache.put(beta, "k", { v: CANARY }), (error: Error) => {
    assert.ok(error instanceof SchemaConflictError && error.recordKey === "k");
    assert.ok(!error.message.includes(CANARY));
    return true;
  });
  assert.deepEqual(cache.get(alpha, "k"), { v: "first" });
  assert.equal(cache.get(beta, "k"), undefined);
  await assert.rejects(cache.putEnvelope(beta, { key: "k", type: "cn.beta", payload: documentPayloadFromBytes(encode({ v: CANARY })), storedAt: "2026-01-01T00:00:00Z" }), SchemaConflictError);
  assert.equal(cache.snapshot().length, 1);
});

test("a type that owns the held record's id replaces it: one record at the key, no second type held", async () => {
  const old = plain("cn.old", "cn.id.v1");
  const renamed = plain("cn.new", "cn.id.v2", ["cn.id.v2", "cn.id.v1"]);
  const cache = CultCache.builder().withDocumentType(old).withDocumentType(renamed).build();
  await cache.put(old, "k", { v: "a" });
  await cache.put(renamed, "k", { v: "b" });
  assert.deepEqual(cache.snapshot().map((e) => [e.type, e.key]), [["cn.new", "k"]]);
  assert.equal(cache.get(old, "k"), undefined);
});

test("a store carrying an unregistered record loads, lists it sorted, keeps it through writes of other keys, and a later registration claims it", async () => {
  const dir = await mkdtemp(join(tmpdir(), "ki-"));
  try {
    const path = join(dir, "s.cc");
    await copyFile(join(vectors, "foreign-store.cc"), path);
    const open = (...names: string[]) => {
      let builder = CultCache.builder();
      for (const name of names) builder = builder.withDocumentType(types[name]!);
      return builder.withGenericStore(new SingleFileMessagePackBackingStore(path)).build();
    };
    const cache = open("alpha");
    await cache.pullAllBackingStores();
    const record = stored(path).get("k")!;
    assert.deepEqual(cache.foreignRecords(), [
      { key: "k", schemaId: "sha256:fbc26e351e00c9e7185e8de171c7486aba707c229c949f0aae6b889288af5f6d", schemaName: "tests.ki_gamma", storedAt: record.storedAt },
    ]);
    await cache.put(types.alpha!, "z", { text: "z" } as never);
    await cache.delete(types.alpha!, "j");
    assert.ok(sameRecord(stored(path).get("k"), record), "the foreign record's stored bytes survive");
    assert.equal(await cache.delete(types.alpha!, "nobody"), false);

    const claimed = open("alpha", "gamma");
    await claimed.pullAllBackingStores();
    assert.deepEqual(claimed.foreignRecords(), []);
    assert.equal(claimed.snapshot().length, 2);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("a foreign key one store carries and another holds under a registered type refuses the load and admits nothing", async () => {
  const dir = await mkdtemp(join(tmpdir(), "ki-"));
  try {
    const foreign = join(dir, "foreign.cc");
    const catalog = join(dir, "catalog.cc");
    await copyFile(join(vectors, "catalog-store.cc"), catalog);
    const writer = CultCache.builder().withDocumentType(types.gamma!).withGenericStore(new SingleFileMessagePackBackingStore(foreign)).build();
    await writer.put(types.gamma!, "k", { text: "g" } as never);
    const cache = CultCache.builder().withDocumentType(types.alpha!).build();
    await cache.addBackingStore(new SingleFileMessagePackBackingStore(catalog), types.alpha!);
    await cache.pullAllBackingStores();
    await cache.addBackingStore(new SingleFileMessagePackBackingStore(foreign));
    await assert.rejects(cache.pullAllBackingStores(), /held by two stores/u);
    assert.deepEqual(cache.snapshot().map((e) => e.key), ["k"], "the earlier pulled state survives");
    assert.deepEqual(cache.foreignRecords(), []);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});
