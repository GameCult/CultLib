import assert from "node:assert/strict";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { setImmediate as tick } from "node:timers/promises";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { CultCache } from "../src/cult-cache";
import { defineDocumentRegistry, defineDocumentType, documentPayloadBytes, documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore, readSingleFileStore } from "../src/single-file-messagepack-backing-store";
import { SchemaConflictError, schemaIdForEnvelope } from "../src/store-format";
import type { CacheBackingStore } from "../src/types";
import { envelopeOf, memoryStore, read, surfaceDocument } from "./union-surface-fixture";

// CultCache.update: read the record and its content version from the store, apply a function to a copy, commit only if the
// version still holds, run again on a lost race (variants:cut_spec:cut-update-with-retry-ts.r1; the C# tests in
// tests/GameCult.Caching.Tests/UpdateTests.cs are the parity).

const counterDocument = defineDocumentType({
  type: "update.counter",
  schemaId: "update.counter.v1",
  schemaName: "update.counter",
  schemaVersion: "update.counter.v1",
  schema: z.object({ value: z.number().int() }),
});
const tallyDocument = defineDocumentType({
  type: "update.tally",
  schemaId: "update.tally.v1",
  schemaName: "update.tally",
  schemaVersion: "update.tally.v1",
  schema: z.object({ count: z.number().int() }),
});
const registry = defineDocumentRegistry(counterDocument, tallyDocument);
const settingsFields = { schemaName: "update.settings", schemaVersion: "update.settings.v1", schemaId: "update.settings.v1", schema: z.object({ theme: z.string() }) };
const settingsDocument = defineDocumentType({ type: "update.settings", ...settingsFields, global: true });
const plainSettingsDocument = defineDocumentType({ type: "update.settings", ...settingsFields });

const KEY = "counter";

async function withDirectory<T>(body: (path: string) => Promise<T>): Promise<T> {
  const directory = await mkdtemp(join(tmpdir(), "cultcache-update-"));
  try {
    return await body(join(directory, "store.cc"));
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

type Registry = Parameters<ReturnType<typeof CultCache.builder>["withRegistry"]>[0];

async function open(path: string, documents: Registry = registry): Promise<CultCache> {
  const cache = CultCache.builder().withRegistry(documents).withGenericStore(new SingleFileMessagePackBackingStore(path)).build();
  await cache.pullAllBackingStores();
  return cache;
}

// What the file holds at the key, read without a cache.
function stored(path: string, key = KEY): number | undefined {
  const envelope = readSingleFileStore(path).envelopes.find((candidate) => candidate.key === key);
  return envelope === undefined ? undefined : (decode(documentPayloadBytes(envelope.payload)) as { value: number }).value;
}

const plus = (n: number) => (current: { value: number } | undefined) => ({ value: (current?.value ?? 0) + n });

test("two writers updating lose no increment", async () => {
  await withDirectory(async (path) => {
    const [left, right] = [await open(path), await open(path)];
    let calls = 0;
    const loop = async (cache: CultCache) => {
      const outcomes: string[] = [];
      for (let i = 0; i < 50; i++) {
        const result = await cache.update(counterDocument, KEY, async (current) => {
          calls++;
          await tick();
          return { value: (current?.value ?? 0) + 1 };
        }, { maxAttempts: 50 });
        outcomes.push(result.outcome);
      }
      return outcomes;
    };
    const outcomes = (await Promise.all([loop(left), loop(right)])).flat();
    assert.ok(outcomes.every((outcome) => outcome === "updated"), outcomes.join());
    assert.equal(stored(path), 100);
    assert.ok(calls > 100, `the caches raced: apply ran ${calls} times`);
  });
});

test("a lost attempt reruns apply on the fresh value", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    const seen: Array<number | undefined> = [];
    const result = await cache.update(counterDocument, KEY, async (current) => {
      seen.push(current?.value);
      if (seen.length === 1) await rival.put(counterDocument, KEY, { value: 100 });
      return { value: current!.value + 1 };
    });
    assert.deepEqual(seen, [1, 100]);
    assert.equal(result.outcome, "updated");
    assert.equal(result.document?.value, 101);
    assert.equal(stored(path), 101);
  });
});

test("exhausted attempts return lostRace, and the cache then shows the store", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    let calls = 0;
    const result = await cache.update(
      counterDocument,
      KEY,
      async (current) => {
        calls++;
        await rival.put(counterDocument, KEY, { value: 100 * calls });
        return { value: current!.value + 1 };
      },
      { maxAttempts: 3 },
    );
    assert.equal(calls, 3);
    assert.equal(result.outcome, "lostRace");
    assert.equal(result.document?.value, 300);
    assert.equal(stored(path), 300);
  });
});

test("five attempts by default", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    let calls = 0;
    const result = await cache.update(counterDocument, KEY, async () => {
      calls++;
      await rival.put(counterDocument, KEY, { value: calls });
      return { value: -1 };
    });
    assert.equal(result.outcome, "lostRace");
    assert.equal(calls, 5);
  });
});

test("maxAttempts 1 makes one call", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    let calls = 0;
    const apply = async (current: { value: number } | undefined) => {
      calls++;
      await rival.put(counterDocument, KEY, { value: 50 + calls });
      return { value: current!.value + 1 };
    };
    assert.equal((await cache.update(counterDocument, KEY, apply, { maxAttempts: 1 })).outcome, "lostRace");
    assert.equal(calls, 1);
  });
});

// An apply that declines resolves at once whatever the bound, so a validation that lets a bad value through fails the assertion
// below instead of looping: no apply here ever loses a race.
test("invalid maxAttempts (undefined is the default, null is refused) and a non-function reject before any call", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    let calls = 0;
    const apply = () => (calls++, undefined);
    for (const maxAttempts of [0, -1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, null as never]) {
      await assert.rejects(() => cache.update(counterDocument, KEY, apply, { maxAttempts }), RangeError, `maxAttempts ${String(maxAttempts)}`);
    }
    await assert.rejects(() => cache.update(counterDocument, KEY, "no" as never), TypeError);
    assert.equal(calls, 0);
    assert.equal((await cache.update(counterDocument, KEY, apply, { maxAttempts: undefined })).outcome, "declined");
    assert.equal(calls, 1);
  });
});

test("null from apply declines as undefined does: nothing is written", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    const before = await readFile(path);
    const result = await cache.update(counterDocument, KEY, () => null as never);
    assert.deepEqual(result, { outcome: "declined", document: cache.get(counterDocument, KEY) });
    assert.deepEqual([...(await readFile(path))], [...before]);
  });
});

test("a cache with no store refuses a key held as another type before apply runs", async () => {
  const cache = CultCache.builder().withRegistry(registry).build();
  await cache.put(tallyDocument, KEY, { count: 1 });
  let calls = 0;
  await assert.rejects(() => cache.update(counterDocument, KEY, () => (calls++, { value: 1 })), /is not a "update.counter"/);
  assert.equal(calls, 0);
});

test("a store attached while apply awaits turns the commit into a lost race, never a blind push", async () => {
  await withDirectory(async (path) => {
    const writer = await open(path);
    await writer.put(counterDocument, KEY, { value: 50 });
    const cache = CultCache.builder().withRegistry(registry).build();
    const seen: Array<number | undefined> = [];
    const result = await cache.update(counterDocument, KEY, async (current) => {
      seen.push(current?.value);
      if (seen.length === 1) cache.addBackingStore(new SingleFileMessagePackBackingStore(path));
      return { value: (current?.value ?? 0) + 1 };
    });
    assert.deepEqual(seen, [undefined, 50], "the second attempt reads the store's record");
    assert.equal(result.outcome, "updated");
    assert.equal(stored(path), 51);
  });
});

test("put installs the storedAt the store stamped, so the cache and the file agree", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    const store = new SingleFileMessagePackBackingStore(path);
    const [envelope] = readSingleFileStore(path).envelopes;
    await store.push({ ...envelope!, payload: documentPayloadFromBytes(encode({ value: 99 })), storedAt: "2099-01-01T00:00:00.000Z" });
    await cache.put(counterDocument, KEY, { value: 2 });
    const onDisk = readSingleFileStore(path).envelopes[0]!.storedAt;
    assert.equal(onDisk, "2099-01-01T00:00:00.001Z");
    assert.equal(cache.getEnvelope(counterDocument, KEY)!.storedAt, onDisk);
  });
});

test("apply gets a fresh copy: changing it changes nothing held, and a lost attempt's change is not seen by the next", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    const argument: Array<{ value: number } | undefined> = [];
    const seen: number[] = [];
    await cache.update(counterDocument, KEY, async (current) => {
      argument.push(current);
      assert.notEqual(current, cache.get(counterDocument, KEY));
      seen.push(current!.value);
      current!.value += 1000;
      assert.equal(cache.get(counterDocument, KEY)!.value, 1);
      if (seen.length === 1) await rival.put(counterDocument, KEY, { value: 7 });
      return { value: current!.value + 1 };
    });
    assert.deepEqual(seen, [1, 7], "the second attempt does not see the first one's change");
    assert.notEqual(argument[0], argument[1]);
  });
});

test("an in-place change to the held value is neither applied nor persisted", async () => {
  await withDirectory(async (path) => {
    const [cache, fresh] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    cache.get(counterDocument, KEY)!.value = 999;
    const seen: number[] = [];
    const result = await cache.update(counterDocument, KEY, (current) => {
      seen.push(current!.value);
      return { value: current!.value + 1 };
    });
    assert.deepEqual(seen, [1]);
    assert.equal(stored(path), 2);
    await fresh.pullAllBackingStores();
    assert.equal(fresh.get(counterDocument, KEY)!.value, 2);
    result.document!.value = 999;
    await cache.update(counterDocument, KEY, plus(1));
    assert.equal(stored(path), 3);
  });
});

test("undefined from apply declines: nothing is written", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    const before = await readFile(path);
    const held = await cache.update(counterDocument, KEY, () => undefined);
    assert.equal(held.outcome, "declined");
    assert.equal(held.document, cache.get(counterDocument, KEY));
    const absent = await cache.update(counterDocument, "absent", () => undefined);
    assert.deepEqual(absent, { outcome: "declined", document: undefined });
    assert.deepEqual([...(await readFile(path))], [...before]);
  });
});

test("an absent key is created once under a race", async () => {
  await withDirectory(async (path) => {
    const [left, right] = [await open(path), await open(path)];
    const outcomes = await Promise.all(
      [left, right].map((cache) =>
        cache.update(counterDocument, KEY, async (current) => {
          await tick();
          return { value: (current?.value ?? 0) + 1 };
        }),
      ),
    );
    assert.deepEqual(outcomes.map((result) => result.outcome), ["updated", "updated"]);
    assert.equal(stored(path), 2);
  });
});

test("a rival writing and restoring the value still reruns apply", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    const seen: number[] = [];
    await cache.update(counterDocument, KEY, async (current) => {
      seen.push(current!.value);
      if (seen.length === 1) {
        await rival.put(counterDocument, KEY, { value: 5 });
        await rival.put(counterDocument, KEY, { value: 1 });
      }
      return { value: current!.value + 1 };
    });
    assert.deepEqual(seen, [1, 1]);
    assert.equal(stored(path), 2);
  });
});

test("a rival removal is seen as undefined", async () => {
  await withDirectory(async (path) => {
    const [cache, rival] = [await open(path), await open(path)];
    await cache.put(counterDocument, KEY, { value: 1 });
    const seen: Array<number | undefined> = [];
    await cache.update(counterDocument, KEY, async (current) => {
      seen.push(current?.value);
      if (seen.length === 1) {
        await rival.pullAllBackingStores();
        await rival.delete(counterDocument, KEY);
      }
      return { value: (current?.value ?? 0) + 1 };
    });
    assert.deepEqual(seen, [1, undefined]);
    assert.equal(stored(path), 1);
  });
});

test("a stale cache's first attempt reads the store", async () => {
  await withDirectory(async (path) => {
    const [stale, rival] = [await open(path), await open(path)];
    await rival.put(counterDocument, KEY, { value: 1 });
    await stale.pullAllBackingStores();
    await rival.put(counterDocument, KEY, { value: 7 });
    const seen: number[] = [];
    await stale.update(counterDocument, KEY, (current) => {
      seen.push(current!.value);
      return { value: current!.value + 1 };
    });
    assert.deepEqual(seen, [7]);
    assert.equal(stored(path), 8);
  });
});

test("refusals are not retried and apply is not called for a record it cannot copy", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(tallyDocument, "tally", { count: 1 });
    const before = await readFile(path);
    let calls = 0;
    const apply = (current: { value: number } | undefined) => {
      calls++;
      return { value: (current?.value ?? 0) + 1 };
    };
    // Another type at the key.
    await assert.rejects(() => cache.update(counterDocument, "tally", apply), /not a "update.counter"/);
    // A foreign record: a type this cache has no definition for.
    const narrow = await open(path, defineDocumentRegistry(counterDocument));
    await assert.rejects(() => narrow.update(counterDocument, "tally", apply), /not a "update.counter"/);
    assert.equal(calls, 0);
    assert.deepEqual([...(await readFile(path))], [...before]);

    // A store without read and commitIf cannot take a conditional write.
    const bare: CacheBackingStore = { pullAll: async () => [], push: async () => {}, delete: async () => {} };
    const plain = CultCache.builder().withRegistry(registry).withGenericStore(bare).build();
    await plain.pullAllBackingStores();
    await assert.rejects(() => plain.update(counterDocument, KEY, apply), /read and commitIf/);
    assert.equal(calls, 0);

    // A record held read-only is refused by the judge after apply ran once; apply that throws is not retried either.
    const surface = surfaceDocument();
    const entries = [envelopeOf(await read("extra-slot-record.msgpack"))];
    const readOnly = CultCache.builder().withDocumentType(surface).withGenericStore(memoryStore(entries)).build();
    await readOnly.pullAllBackingStores();
    let surfaceCalls = 0;
    await assert.rejects(() => readOnly.update(surface, "surface", (value) => (surfaceCalls++, value)), SchemaConflictError);
    assert.equal(surfaceCalls, 1);
    assert.equal(entries.length, 1, "nothing reached the store");
    await assert.rejects(
      () => cache.update(counterDocument, KEY, () => { calls++; throw new Error("boom"); }),
      /boom/,
    );
    assert.equal(calls, 1);
  });
});

test("own writes cause no retry, and updated returns the held instance", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    let calls = 0;
    const apply = (current: { value: number } | undefined) => {
      calls++;
      return { value: current!.value + 1 };
    };
    await cache.update(counterDocument, KEY, apply);
    const second = await cache.update(counterDocument, KEY, apply);
    assert.equal(calls, 2);
    assert.equal(second.outcome, "updated");
    assert.equal(second.document, cache.get(counterDocument, KEY));
    assert.equal(stored(path), 3);
    // The cache holds the record as the store wrote it, the stamp the store chose included: over a record stamped in the future
    // the store writes one millisecond after it, whatever the clock says.
    const future = readSingleFileStore(path).envelopes[0]!;
    await new SingleFileMessagePackBackingStore(path).push({ ...future, payload: documentPayloadFromBytes(encode({ value: 99 })), storedAt: "2099-01-01T00:00:00.000Z" });
    await cache.update(counterDocument, KEY, apply);
    assert.equal(readSingleFileStore(path).envelopes[0]!.storedAt, "2099-01-01T00:00:00.001Z");
    assert.equal(cache.getEnvelope(counterDocument, KEY)!.storedAt, "2099-01-01T00:00:00.001Z");
  });
});

test("apply runs outside the cache's serial queue", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    let calls = 0;
    const result = await cache.update(counterDocument, KEY, async (current) => {
      calls++;
      await Promise.race([
        cache.put(tallyDocument, "other", { count: 1 }),
        new Promise((_, reject) => setTimeout(() => reject(new Error("a cache call from apply did not finish")), 5000).unref()),
      ]);
      return { value: (current?.value ?? 0) + 1 };
    });
    assert.equal(result.outcome, "updated");
    assert.equal(calls, 1);
  });
});

test("a cache with no store loses no increment under interleaved updates", async () => {
  const cache = CultCache.builder().withRegistry(registry).build();
  await cache.pullAllBackingStores();
  let calls = 0;
  const loop = async () => {
    let updated = 0;
    for (let i = 0; i < 50; i++) {
      const result = await cache.update(counterDocument, KEY, async (current) => {
        calls++;
        await tick();
        return { value: (current?.value ?? 0) + 1 };
      });
      if (result.outcome === "updated") updated++;
    }
    return updated;
  };
  // The two loops run in lockstep, so one may lose every round (a lostRace is the answer, not a lost write): what is held is
  // exactly what the updated outcomes wrote.
  const updated = (await Promise.all([loop(), loop()])).reduce((sum, n) => sum + n, 0);
  assert.equal(cache.get(counterDocument, KEY)!.value, updated);
  assert.ok(calls > updated, `the loops raced: apply ran ${calls} times for ${updated} writes`);
});

test("a store-less cache copies the bytes it admitted, not the held value", async () => {
  const cache = CultCache.builder().withRegistry(registry).build();
  await cache.put(counterDocument, KEY, { value: 1 });
  cache.get(counterDocument, KEY)!.value = 999;
  const seen: number[] = [];
  await cache.update(counterDocument, KEY, (current) => (seen.push(current!.value), { value: current!.value + 1 }));
  assert.deepEqual(seen, [1]);
});

test("updateGlobal updates the global record and refuses one loaded from a legacy key until putGlobal retires it", async () => {
  await withDirectory(async (path) => {
    const settings = defineDocumentRegistry(settingsDocument);
    const cache = await open(path, settings);
    const first = await cache.updateGlobal(settingsDocument, (current) => ({ theme: `${current?.theme ?? ""}a` }));
    assert.equal(first.outcome, "updated");
    assert.deepEqual(cache.getGlobal(settingsDocument), { theme: "a" });
    assert.equal(readSingleFileStore(path).envelopes[0]!.key, CultCache.GLOBAL_KEY);
    await rm(path);

    const legacy = await open(path, defineDocumentRegistry(plainSettingsDocument));
    await legacy.put(plainSettingsDocument, "old-settings", { theme: "x" });
    const loaded = await open(path, settings);
    let calls = 0;
    await assert.rejects(() => loaded.updateGlobal(settingsDocument, (current) => (calls++, current)), /putGlobal to retire its legacy key/);
    assert.equal(calls, 0);
    // Every path to the write is refused, the global's own key included, and the legacy record survives.
    const before = await readFile(path);
    await assert.rejects(() => loaded.update(settingsDocument, CultCache.GLOBAL_KEY, () => ({ theme: "lost" })), /putGlobal to retire its legacy key/);
    assert.deepEqual([...(await readFile(path))], [...before]);
    assert.deepEqual(readSingleFileStore(path).envelopes.map((e) => e.key), ["old-settings"]);
    await loaded.putGlobal(settingsDocument, { theme: "y" });
    assert.equal((await loaded.updateGlobal(settingsDocument, (current) => ({ theme: `${current!.theme}z` }))).outcome, "updated");
    assert.deepEqual(loaded.getGlobal(settingsDocument), { theme: "yz" });
    await assert.rejects(() => cache.update(settingsDocument, "not-global", () => ({ theme: "q" })), /must use key/);
  });
});

test("a legacy global that a pull loads while apply awaits is refused at the write, and its legacy record survives", async () => {
  await withDirectory(async (path) => {
    const settings = defineDocumentRegistry(settingsDocument);
    const cache = await open(path, settings);
    const legacy = await open(path, defineDocumentRegistry(plainSettingsDocument));
    await assert.rejects(
      () =>
        cache.update(settingsDocument, CultCache.GLOBAL_KEY, async () => {
          await legacy.put(plainSettingsDocument, "old-settings", { theme: "x" });
          await cache.pullAllBackingStores();
          return { theme: "lost" };
        }),
      /putGlobal to retire its legacy key/,
    );
    assert.deepEqual(readSingleFileStore(path).envelopes.map((e) => e.key), ["old-settings"]);
  });
});

test("the ledger holds under contention: the stored value is the number of updated outcomes", async () => {
  await withDirectory(async (path) => {
    const caches = await Promise.all([open(path), open(path), open(path)]);
    const results = await Promise.all(
      caches.map(async (cache) => {
        let updated = 0;
        for (let i = 0; i < 25; i++) {
          const result = await cache.update(counterDocument, KEY, async (current) => {
            await tick();
            return { value: (current?.value ?? 0) + 1 };
          });
          if (result.outcome === "updated") updated++;
        }
        return updated;
      }),
    );
    assert.equal(stored(path), results.reduce((sum, n) => sum + n, 0));
  });
});

test("a flush of an unchanged cache rewrites the file byte for byte, and an update does not lose a race to it", async () => {
  await withDirectory(async (path) => {
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    await cache.put(counterDocument, "other", { value: 7 });
    const store = new SingleFileMessagePackBackingStore(path);
    const before = await readFile(path);
    // What CultMesh.flush does: push the whole stored image, twice.
    await store.pushAll(await store.pullAll());
    await store.pushAll(await store.pullAll());
    assert.deepEqual(await readFile(path), before);
    const result = await cache.update(counterDocument, KEY, async (current) => {
      await store.pushAll(await store.pullAll());
      return { value: current!.value + 1 };
    }, { maxAttempts: 1 });
    assert.equal(result.outcome, "updated");
    assert.equal(stored(path), 2);
  });
});

test("a changed record is re-stamped later than the one it replaces, and a revert to earlier bytes too", async () => {
  await withDirectory(async (path) => {
    const store = new SingleFileMessagePackBackingStore(path);
    const cache = await open(path);
    await cache.put(counterDocument, KEY, { value: 1 });
    const first = readSingleFileStore(path).envelopes[0]!;
    const same = await store.push({ ...first, storedAt: "2099-01-01T00:00:00.000Z" });
    assert.equal(same.storedAt, first.storedAt, "identical content keeps its stamp");
    await cache.put(counterDocument, KEY, { value: 2 });
    const second = readSingleFileStore(path).envelopes[0]!;
    const reverted = await store.push({ ...first, storedAt: second.storedAt });
    assert.ok(Date.parse(reverted.storedAt) > Date.parse(second.storedAt));
  });
});

test("a push result that is not a parseable stamp is ignored", async () => {
  for (const result of [true, {}, "", "garbage", { storedAt: "" }, { storedAt: "garbage" }]) {
    const entries: Parameters<typeof memoryStore>[0] = [];
    const store = { ...memoryStore(entries), push: async (entry: (typeof entries)[number]) => { entries.push(entry); return result as never; } };
    const cache = CultCache.builder().withRegistry(registry).withGenericStore(store).build();
    await cache.put(counterDocument, KEY, { value: 1 });
    const held = cache.getEnvelope(counterDocument, KEY)!.storedAt;
    assert.equal(Number.isNaN(Date.parse(held)), false, `held stamp ${held} after a push result of ${JSON.stringify(result)}`);
  }
});

test("a rewrite of identical content keeps the stamp but writes the writer's type and catalog entry", async () => {
  await withDirectory(async (path) => {
    const old = defineDocumentType({ type: "update.rn.old", schemaId: "update.rn.v1", schemaName: "update.rn.old", schemaVersion: "update.rn.v1", schema: z.object({ value: z.number().int() }) });
    const renamed = defineDocumentType({ type: "update.rn.new", schemaId: "update.rn.v1", schemaName: "update.rn.new", schemaVersion: "update.rn.v1", schema: z.object({ value: z.number().int() }) });
    await (await open(path, defineDocumentRegistry(old))).put(old, KEY, { value: 1 });
    const before = readSingleFileStore(path).envelopes[0]!;
    await (await open(path, defineDocumentRegistry(renamed))).put(renamed, KEY, { value: 1 });
    const after = readSingleFileStore(path).envelopes[0]!;
    assert.equal(after.storedAt, before.storedAt, "identical content keeps its stamp");
    assert.equal(after.type, "update.rn.new", "the writer's type is what the file says");
  });
});

test("a rewrite of the same bytes under another schema id is a changed record: stamped later, written under the new id", async () => {
  await withDirectory(async (path) => {
    const v1 = defineDocumentType({ type: "update.sid", schemaId: "update.sid.v1", schemaName: "update.sid", schemaVersion: "update.sid.v1", schema: z.object({ value: z.number().int() }) });
    const v2 = defineDocumentType({ type: "update.sid", schemaId: "update.sid.v2", schemaName: "update.sid", schemaVersion: "update.sid.v2", compatibleSchemaIds: ["update.sid.v1"], schema: z.object({ value: z.number().int() }) });
    await (await open(path, defineDocumentRegistry(v1))).put(v1, KEY, { value: 1 });
    const before = readSingleFileStore(path).envelopes[0]!;
    await (await open(path, defineDocumentRegistry(v2))).put(v2, KEY, { value: 1 });
    const after = readSingleFileStore(path).envelopes[0]!;
    assert.ok(Date.parse(after.storedAt) > Date.parse(before.storedAt));
    assert.equal(schemaIdForEnvelope(after), "update.sid.v2");
  });
});
