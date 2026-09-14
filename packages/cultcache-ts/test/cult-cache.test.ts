import assert from "node:assert/strict";
import { exec, execFile } from "node:child_process";
import { EventEmitter } from "node:events";
import { existsSync } from "node:fs";
import { access, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { dirname, join, resolve, delimiter } from "node:path";
import { test } from "node:test";
import { promisify } from "node:util";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { CultCache } from "../src/cult-cache";
import { inspectCultCacheBytes } from "../src/cult-cache-inspector";
import { defineDocumentRegistry, defineDocumentType } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import type { CacheBackingStore, CultCacheEnvelope, CultCacheSchema } from "../src/types";

const execFileAsync = promisify(execFile);
const execAsync = promisify(exec);
const cargoCommand = process.env.CARGO ?? (process.platform === "win32" ? join(homedir(), ".cargo", "bin", "cargo.exe") : "cargo");
const dotnetCommand = process.env.DOTNET ?? (process.platform === "win32" ? join("C:", "Program Files", "dotnet", "dotnet.exe") : "dotnet");
const pythonCommand = process.env.PYTHON ?? "python";
const cultCacheTsRoot = resolve(__dirname, "../..");
const cultLibRoot = findAncestor(cultCacheTsRoot, "CultLib.sln") ?? resolve(cultCacheTsRoot, "..", "CultLib");
const cultcachePyRoot = resolve(cultLibRoot, "packages", "cultcache-py");
const cultcachePySrc = ["cultcache-py", "cultnet-py", "cultmesh-py"]
  .map((name) => resolve(cultLibRoot, "packages", name, "src"))
  .join(delimiter);
const cultcacheRsRoot = existsSync(resolve(cultLibRoot, "packages", "cultcache-rs"))
  ? resolve(cultLibRoot, "packages", "cultcache-rs")
  : resolve(cultCacheTsRoot, "..", "cultcache-rs");
const rustInteropBinary = resolve(
  cultcacheRsRoot,
  "target",
  "debug",
  "examples",
  process.platform === "win32" ? "cultcache_interop.exe" : "cultcache_interop",
);
const csharpInteropProject = resolve(
  cultLibRoot,
  "tests",
  "GameCult.Caching.InteropPeer",
  "GameCult.Caching.InteropPeer.csproj",
);
const csharpInteropDll = resolve(
  cultLibRoot,
  "bin",
  "GameCult.Caching.InteropPeer",
  "Debug",
  "net10.0",
  "GameCult.Caching.InteropPeer.dll",
);

function findAncestor(start: string, marker: string): string | undefined {
  let current = start;
  while (true) {
    if (existsSync(resolve(current, marker))) {
      return current;
    }

    const parent = dirname(current);
    if (parent === current) {
      return undefined;
    }

    current = parent;
  }
}

test("CultCache supports registry bootstrap, global documents, and lookup by name and index", async () => {
  const itemDocument = defineDocumentType({
    type: "item",
    schema: z.object({
      name: z.string(),
      category: z.string(),
      value: z.number().int(),
    }),
    name: "name",
    indexes: {
      category: "category",
    },
  });

  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({
      theme: z.string(),
      retries: z.number().int().nonnegative(),
    }),
    global: true,
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "cache.msgpack");
  const registry = defineDocumentRegistry(itemDocument, settingsDocument);

  const cache = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.put(itemDocument, "item:potion", {
    name: "Potion",
    category: "Consumable",
    value: 50,
  });
  await cache.putGlobal(settingsDocument, {
    theme: "ash",
    retries: 3,
  });

  assert.deepEqual(cache.getByName(itemDocument, "Potion"), {
    name: "Potion",
    category: "Consumable",
    value: 50,
  });
  assert.equal(cache.getKeyByIndex(itemDocument, "category", "Consumable"), "item:potion");
  assert.deepEqual(cache.getGlobal(settingsDocument), {
    theme: "ash",
    retries: 3,
  });

  const snapshot = cache.snapshot();
  assert.equal(snapshot.length, 2);
  assert.ok(snapshot.every((entry) => entry.payload instanceof Uint8Array && entry.payload.length > 0));

  const reloaded = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await reloaded.pullAllBackingStores();

  assert.deepEqual(reloaded.getByName(itemDocument, "Potion"), {
    name: "Potion",
    category: "Consumable",
    value: 50,
  });
  assert.deepEqual(reloaded.getRequiredGlobal(settingsDocument), {
    theme: "ash",
    retries: 3,
  });
});

test("CultCache can register name and index lookups after entries already exist", async () => {
  const noteDocument = defineDocumentType({
    type: "note",
    schema: z.object({
      title: z.string(),
      author: z.string(),
      body: z.string(),
    }),
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "cache.msgpack");
  const cache = CultCache.builder()
    .withDocumentType(noteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.put(noteDocument, "note:hello", {
    title: "Hello",
    author: "ari",
    body: "world",
  });

  await cache.registerNameLookup(noteDocument, "title");
  await cache.registerIndex(noteDocument, "author", "author");

  assert.equal(cache.getIdByName(noteDocument, "Hello"), "note:hello");
  assert.equal(cache.getIdByIndex(noteDocument, "author", "ari"), "note:hello");
});

test("CultCache fails closed on unknown or illegal persisted polymorphic state", async () => {
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({
      theme: z.string(),
    }),
    global: true,
  });

  class InMemoryStore implements CacheBackingStore {
    constructor(private readonly entries: CultCacheEnvelope[]) {}

    async pullAll(): Promise<CultCacheEnvelope[]> {
      return this.entries;
    }

    async push(): Promise<void> {
      throw new Error("not used");
    }

    async delete(): Promise<void> {
      throw new Error("not used");
    }
  }

  const cacheWithUnknownType = CultCache.builder()
    .withDocumentType(settingsDocument)
    .withGenericStore(new InMemoryStore([
      {
        key: "mystery",
        type: "forbidden",
        payload: encode({ theme: "ash" }),
        storedAt: new Date().toISOString(),
      },
    ]))
    .build();

  await assert.rejects(
    async () => cacheWithUnknownType.pullAllBackingStores(),
    /No schema is registered for persisted document type "forbidden"\./,
  );

  const cacheWithDuplicateGlobal = CultCache.builder()
    .withDocumentType(settingsDocument)
    .withGenericStore(new InMemoryStore([
      {
        key: "one",
        type: "settings",
        payload: encode({ theme: "ash" }),
        storedAt: new Date().toISOString(),
      },
      {
        key: "two",
        type: "settings",
        payload: encode({ theme: "ember" }),
        storedAt: new Date().toISOString(),
      },
    ]))
    .build();

  await assert.rejects(
    async () => cacheWithDuplicateGlobal.pullAllBackingStores(),
    /has multiple persisted entries/,
  );
});

test("CultCache accepts generated parse-style schemas without a Zod mirror", async () => {
  type GeneratedSettings = {
    schema_version: "generated.settings.v0";
    theme: string;
    retries: number;
  };

  const generatedSchema: CultCacheSchema<GeneratedSettings> = {
    parse(input: unknown): GeneratedSettings {
      if (!input || typeof input !== "object") {
        throw new Error("generated settings must be an object");
      }

      const value = input as Record<string, unknown>;
      if (value.schema_version !== "generated.settings.v0") {
        throw new Error("generated settings schema_version mismatch");
      }
      if (typeof value.theme !== "string") {
        throw new Error("generated settings theme must be a string");
      }
      if (typeof value.retries !== "number") {
        throw new Error("generated settings retries must be a number");
      }

      return {
        schema_version: "generated.settings.v0",
        theme: value.theme,
        retries: value.retries,
      };
    },
  };

  const generatedDocument = defineDocumentType({
    type: "generated-settings",
    schema: generatedSchema,
    global: true,
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "generated.msgpack");
  const cache = CultCache.builder()
    .withDocumentType(generatedDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.putGlobal(generatedDocument, {
    schema_version: "generated.settings.v0",
    theme: "ash",
    retries: 3,
  });

  const settings = cache.getRequiredGlobal(generatedDocument);
  assert.equal(settings.schema_version, "generated.settings.v0");
  assert.equal(settings.theme, "ash");
  assert.equal(settings.retries, 3);
});

test("CultCache can ingest raw envelopes without re-encoding the payload", async () => {
  const noteDocument = defineDocumentType({
    type: "note",
    schema: z.object({
      title: z.string(),
      body: z.string(),
    }),
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "cache.msgpack");
  const origin = CultCache.builder()
    .withDocumentType(noteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  const target = CultCache.builder()
    .withDocumentType(noteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "target.msgpack")))
    .build();

  await origin.put(noteDocument, "note:hello", {
    title: "Hello",
    body: "world",
  });

  const envelope = origin.getRequiredEnvelope(noteDocument, "note:hello");
  const applied = await target.putEnvelope(noteDocument, envelope);

  assert.deepEqual(applied, {
    title: "Hello",
    body: "world",
  });
  assert.deepEqual(target.getRequired(noteDocument, "note:hello"), applied);
  assert.deepEqual(target.getRequiredEnvelope(noteDocument, "note:hello").payload, envelope.payload);
});

test("SingleFileMessagePackBackingStore writes the CultCache v1 snapshot shape", async () => {
  const namedDocument = defineDocumentType({
    type: "tests.named_entry",
    schema: z.object({
      Name: z.string(),
      Value: z.string(),
    }),
    schemaId: "sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8",
    schemaName: "tests.named_entry",
    schemaVersion: "tests.named_entry.v1",
    contentHash: "sha256:23150930afcc1d84f0cb3012ccc2debcb9b4685f62083033bbaab0083f1e832e",
    canonicalSchemaJson: "{\"schemaName\":\"tests.named_entry\",\"schemaVersion\":\"tests.named_entry.v1\",\"members\":[{\"slot\":0,\"name\":\"Name\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":1,\"name\":\"Value\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}",
    members: [
      {
        slot: 0,
        memberName: "Name",
        typeName: "System.String",
        isName: true,
      },
      {
        slot: 1,
        memberName: "Value",
        typeName: "System.String",
      },
    ],
    name: "Name",
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "snapshot.msgpack");
  const cache = CultCache.builder()
    .withDocumentType(namedDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.put(namedDocument, "record-1", {
    Name: "Teeth",
    Value: "slot-array",
  });

  const snapshot = decode(await readFile(storePath)) as unknown[];
  assert.equal(snapshot[0], "cultcache.store.v1");

  const catalog = snapshot[1] as unknown[][];
  assert.equal(catalog.length, 1);
  assert.equal(catalog[0]?.[0], "sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8");
  assert.equal(catalog[0]?.[1], "tests.named_entry");
  assert.equal(catalog[0]?.[2], "tests.named_entry.v1");

  const records = snapshot[2] as unknown[][];
  assert.equal(records.length, 1);
  assert.equal(records[0]?.[0], "record-1");
  assert.equal(records[0]?.[1], "sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8");
  assert.ok(records[0]?.[3] instanceof Uint8Array);
});

test("SingleFileMessagePackBackingStore reads CultCache v1 snapshots by schema id", async () => {
  const noteDocument = defineDocumentType({
    type: "tests.named_entry",
    schema: z.object({
      Name: z.string(),
      Value: z.string(),
    }),
    schemaId: "schema-1",
    schemaName: "tests.named_entry",
    schemaVersion: "tests.named_entry.v1",
    contentHash: "hash-1",
    canonicalSchemaJson: "{\"fields\":2}",
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "csharp.msgpack");
  await writeFile(
    storePath,
    encode([
      "cultcache.store.v1",
      [
        [
          "schema-1",
          "tests.named_entry",
          "tests.named_entry.v1",
          "hash-1",
          "{\"fields\":2}",
          ["schema-1"],
          [],
        ],
      ],
      [
        [
          "record-1",
          "schema-1",
          "2026-05-08T12:00:00Z",
          encode({
            Name: "Teeth",
            Value: "slot-array",
          }),
        ],
      ],
    ]),
  );

  const cache = CultCache.builder()
    .withDocumentType(noteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  assert.deepEqual(cache.getRequired(noteDocument, "record-1"), {
    Name: "Teeth",
    Value: "slot-array",
  });
  assert.equal(cache.getRequiredEnvelope(noteDocument, "record-1").schemaId, "schema-1");
});

test("SingleFileMessagePackBackingStore recovers schema-stamped records missing catalog entries", async () => {
  const stampedDocument = defineDocumentType({
    type: "runtime-policy",
    schema: z.tuple([
      z.literal("tests.schema_stamped_entry.v1"),
      z.string(),
      z.string(),
    ]),
    schemaId: "schema-current",
    schemaName: "tests.schema_stamped_entry",
    schemaVersion: "tests.schema_stamped_entry.v1",
    compatibleSchemaIds: ["schema-current"],
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "missing-catalog.msgpack");
  await writeFile(
    storePath,
    encode([
      "cultcache.store.v1",
      [],
      [
        [
          "record-1",
          "sha256:stale-schema-id-from-cold-record",
          "2026-06-25T12:00:00Z",
          encode([
            "tests.schema_stamped_entry.v1",
            "schema-stamped",
            "still readable",
          ]),
        ],
      ],
    ]),
  );

  const cache = CultCache.builder()
    .withDocumentType(stampedDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  assert.deepEqual(cache.getRequired(stampedDocument, "record-1"), [
    "tests.schema_stamped_entry.v1",
    "schema-stamped",
    "still readable",
  ]);
  assert.equal(
    cache.getRequiredEnvelope(stampedDocument, "record-1").schemaId,
    "sha256:stale-schema-id-from-cold-record",
  );
});

test("SingleFileMessagePackBackingStore heals legacy envelopes whose payload was persisted as an object", async () => {
  const noteDocument = defineDocumentType({
    type: "note",
    schema: z.object({
      title: z.string(),
      body: z.string(),
    }),
  });

  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-ts-")), "legacy.msgpack");
  const envelopePayload = {
    title: "Hello",
    body: "world",
  };

  await writeFile(
    storePath,
    encode([
      {
        key: "note:hello",
        type: "note",
        payload: envelopePayload,
        storedAt: new Date().toISOString(),
      },
    ]),
  );

  const cache = CultCache.builder()
    .withDocumentType(noteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();

  await cache.pullAllBackingStores();
  assert.deepEqual(cache.getRequired(noteDocument, "note:hello"), envelopePayload);

  const rewritten = decode(await readFile(storePath)) as unknown[];
  assert.equal(rewritten[0], "cultcache.store.v1");
  const records = rewritten[2] as unknown[][];
  assert.equal(records.length, 1);
  assert.ok(records[0]?.[3] instanceof Uint8Array);
});

test("CultCache rejects a second generic backing store", async () => {
  const cache = new CultCache();
  await cache.addGenericBackingStore(new SingleFileMessagePackBackingStore(join(tmpdir(), "unused-a.cc")));
  await assert.rejects(
    cache.addGenericBackingStore(new SingleFileMessagePackBackingStore(join(tmpdir(), "unused-b.cc"))),
    /second generic store/u,
  );
});

test("CultCache rejects a type registered to two stores", async () => {
  const cache = new CultCache();
  await cache.addBackingStore(new SingleFileMessagePackBackingStore(join(tmpdir(), "unused-a.cc")), "item");
  await assert.rejects(
    cache.addBackingStore(new SingleFileMessagePackBackingStore(join(tmpdir(), "unused-b.cc")), "settings", "item"),
    /"item" is already routed/u,
  );
  // A refused registration claims nothing, so "settings" is still free.
  await cache.addBackingStore(new SingleFileMessagePackBackingStore(join(tmpdir(), "unused-c.cc")), "settings");
});

test("CultCache routes each type to its home store", async () => {
  const itemDocument = defineDocumentType({
    type: "item",
    schema: z.object({ name: z.string() }),
  });
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ theme: z.string() }),
  });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-routes-"));
  const genericPath = join(tempDir, "generic.cc");
  const settingsPath = join(tempDir, "settings.cc");
  const build = () => CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument, settingsDocument))
    .withBackingStore(new SingleFileMessagePackBackingStore(settingsPath), settingsDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();
  const keysIn = async (path: string) =>
    (await new SingleFileMessagePackBackingStore(path).pullAll()).map((entry) => `${entry.type}:${entry.key}`);

  const cache = build();
  await cache.put(itemDocument, "potion", { name: "Potion" });
  await cache.put(settingsDocument, "app", { theme: "ash" });
  assert.deepEqual(await keysIn(genericPath), ["item:potion"]);
  assert.deepEqual(await keysIn(settingsPath), ["settings:app"]);

  await cache.delete(settingsDocument, "app");
  assert.deepEqual(await keysIn(settingsPath), []);
  assert.deepEqual(await keysIn(genericPath), ["item:potion"]);

  const reloaded = build();
  await reloaded.pullAllBackingStores();
  assert.deepEqual(reloaded.getRequired(itemDocument, "potion"), { name: "Potion" });
  assert.equal(reloaded.get(settingsDocument, "app"), undefined);
});

test("CultCache refuses an attach that would move a held record's home", async () => {
  const settingsDocument = defineDocumentType({ type: "settings", schema: z.object({ theme: z.string() }) });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-home-move-"));
  const genericPath = join(tempDir, "generic.cc");
  const settingsPath = join(tempDir, "settings.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();
  await cache.put(settingsDocument, "app", { theme: "v1-generic" });

  await assert.rejects(
    async () => cache.addBackingStore(new SingleFileMessagePackBackingStore(settingsPath), settingsDocument),
    /would move "settings" from the generic store to the store routed to settings/u,
  );
  // Nothing attached: the next write still lands in the generic store, and the refused store stays untouched.
  await cache.put(settingsDocument, "app", { theme: "v2-generic" });
  const keysIn = async (path: string) =>
    (await new SingleFileMessagePackBackingStore(path).pullAll()).map((entry) => `${entry.type}:${entry.key}`);
  assert.deepEqual(await keysIn(genericPath), ["settings:app"]);
  assert.equal(existsSync(settingsPath), false);
});

test("CultCache refuses at load a record delivered by a store that is not its home", async () => {
  const settingsDocument = defineDocumentType({ type: "settings", schema: z.object({ theme: z.string() }) });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-load-home-"));
  const genericPath = join(tempDir, "generic.cc");
  const settingsPath = join(tempDir, "settings.cc");
  const writer = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();
  await writer.put(settingsDocument, "app", { theme: "stray" });
  const genericBytes = await readFile(genericPath);

  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withBackingStore(new SingleFileMessagePackBackingStore(settingsPath), settingsDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();
  // The cache already holds a record, in its home store, before the refused load.
  await cache.put(settingsDocument, "held", { theme: "home" });
  const before = cache.snapshot();
  const settingsBytes = await readFile(settingsPath);
  await assert.rejects(
    cache.pullAllBackingStores(),
    /"settings" record "app" was loaded from the generic store, but its home is the store routed to settings/u,
  );
  assert.equal(cache.get(settingsDocument, "app"), undefined);
  assert.deepEqual(cache.snapshot(), before);
  assert.deepEqual(cache.getRequired(settingsDocument, "held"), { theme: "home" });
  assert.deepEqual(await readFile(genericPath), genericBytes);
  assert.deepEqual(await readFile(settingsPath), settingsBytes);
});

async function storeState(path: string): Promise<string[]> {
  if (!existsSync(path)) {
    return [];
  }
  return (await new SingleFileMessagePackBackingStore(path).pullAll())
    .map((entry) => `${entry.type}:${entry.key}:${Buffer.from(entry.payload).toString("hex")}`)
    .sort();
}

test("putWithThrowingAccessorChangesNeitherStoreNorCache", async () => {
  const itemDocument = defineDocumentType({
    type: "item",
    schema: z.object({ n: z.string() }),
    name: (value: { n: string }) => {
      if (value.n === "bad") {
        throw new Error("accessor refuses bad");
      }
      return value.n;
    },
  });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-put-accessor-")), "generic.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.put(itemDocument, "k", { n: "good" });
  const storeBefore = await storeState(storePath);
  const cacheBefore = cache.snapshot();

  await assert.rejects(cache.put(itemDocument, "k", { n: "bad" }), /accessor refuses bad/u);
  assert.deepEqual(await storeState(storePath), storeBefore);
  assert.deepEqual(cache.snapshot(), cacheBefore);
  assert.deepEqual(cache.getRequired(itemDocument, "k"), { n: "good" });
  assert.equal(cache.getKeyByName(itemDocument, "good"), "k");
});

test("overwriteUnderRegisteredAccessorThatNowThrowsChangesNeitherStoreNorCache", async () => {
  const itemDocument = defineDocumentType({ type: "item", schema: z.object({ c: z.string() }) });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-overwrite-accessor-")), "generic.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.put(itemDocument, "k", { c: "old" });
  let refusing = false;
  await cache.registerIndex(itemDocument, "category", (value: { c: string }) => {
    if (refusing) {
      throw new Error("accessor refuses now");
    }
    return value.c;
  });
  refusing = true;
  const storeBefore = await storeState(storePath);
  const cacheBefore = cache.snapshot();

  await assert.rejects(cache.put(itemDocument, "k", { c: "new" }), /accessor refuses now/u);
  assert.deepEqual(await storeState(storePath), storeBefore);
  assert.deepEqual(cache.snapshot(), cacheBefore);
  assert.equal(cache.getKeyByIndex(itemDocument, "category", "old"), "k");
  // Deleting runs no accessor: it uses the lookup values stored when the record was admitted.
  assert.equal(await cache.delete(itemDocument, "k"), true);
  assert.equal(cache.getKeyByIndex(itemDocument, "category", "old"), undefined);
});

test("registeringAccessorThatThrowsOnHeldRecordInstallsNothing", async () => {
  const itemDocument = defineDocumentType({ type: "item", schema: z.object({ n: z.string() }) });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-register-accessor-")), "generic.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.put(itemDocument, "old", { n: "bad" });
  await assert.rejects(
    async () => cache.registerIndex(itemDocument, "i", (value: { n: string }) => {
      if (value.n === "bad") {
        throw new Error("index refuses bad");
      }
      return value.n;
    }),
    /index refuses bad/u,
  );
  await cache.put(itemDocument, "old", { n: "fine" });
  assert.deepEqual(cache.getRequired(itemDocument, "old"), { n: "fine" });
  assert.equal(cache.getKeyByIndex(itemDocument, "i", "fine"), undefined);
});

test("globalReplaceWithFailingStorePushKeepsOldGlobal", async () => {
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ t: z.string() }),
    global: true,
  });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-global-push-")), "generic.cc");
  const inner = new SingleFileMessagePackBackingStore(storePath);
  let failPush = false;
  const store: CacheBackingStore = {
    pullAll: () => inner.pullAll(),
    delete: (entry) => inner.delete(entry),
    pushAll: (entries, options) => inner.pushAll(entries, options),
    push: async (entry) => {
      if (failPush) {
        throw new Error("disk full");
      }
      return inner.push(entry);
    },
  };
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(store)
    .build();
  await cache.putGlobal(settingsDocument, { t: "base" });
  const storeBefore = await storeState(storePath);
  const cacheBefore = cache.snapshot();
  failPush = true;

  await assert.rejects(cache.putGlobal(settingsDocument, { t: "new" }), /disk full/u);
  await assert.rejects(cache.put(settingsDocument, "other", { t: "new" }), /must use key "__global__"/u);
  assert.deepEqual(await storeState(storePath), storeBefore);
  assert.deepEqual(cache.snapshot(), cacheBefore);
  assert.deepEqual(cache.getRequiredGlobal(settingsDocument), { t: "base" });
});

test("concurrentGlobalPutsCannotBothReachDisk", async () => {
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ t: z.string() }),
    global: true,
  });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-global-race-")), "generic.cc");
  const inner = new SingleFileMessagePackBackingStore(storePath);
  // The first push after arming writes, then pauses until released. A second write that reaches the
  // store during that pause is recorded: serialized writes cannot.
  let armed = false;
  let paused = false;
  let reachedStoreWhilePaused = false;
  let release!: () => void;
  let reportPaused!: () => void;
  const pausedSignal = new Promise<void>((resolve) => {
    reportPaused = resolve;
  });
  const store: CacheBackingStore = {
    pullAll: () => inner.pullAll(),
    delete: (entry) => inner.delete(entry),
    push: async (entry) => {
      reachedStoreWhilePaused ||= paused;
      await inner.push(entry);
      if (armed) {
        armed = false;
        paused = true;
        await new Promise<void>((resolve) => {
          release = resolve;
          reportPaused();
        });
        paused = false;
      }
    },
  };
  const build = (backing: CacheBackingStore) => CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(backing)
    .build();
  const cache = build(store);
  await cache.putGlobal(settingsDocument, { t: "base" });

  armed = true;
  const first = cache.putGlobal(settingsDocument, { t: "first" });
  await pausedSignal;
  const second = cache.putGlobal(settingsDocument, { t: "second" });
  await new Promise((resolve) => setTimeout(resolve, 50));
  release();
  await Promise.all([first, second]);

  assert.equal(reachedStoreWhilePaused, false);
  const onDisk = await inner.pullAll();
  assert.deepEqual(onDisk.map((entry) => entry.key), [CultCache.GLOBAL_KEY]);
  assert.deepEqual(
    Buffer.from(onDisk[0].payload),
    Buffer.from(cache.getRequiredEnvelope(settingsDocument, CultCache.GLOBAL_KEY).payload),
  );
  assert.deepEqual(cache.getRequiredGlobal(settingsDocument), { t: "second" });
  const reloaded = build(new SingleFileMessagePackBackingStore(storePath));
  await reloaded.pullAllBackingStores();
  assert.deepEqual(reloaded.getRequiredGlobal(settingsDocument), { t: "second" });
});

test("unawaitedFailingPutIsAnUnhandledRejection", async () => {
  const itemDocument = defineDocumentType({ type: "item", schema: z.object({ n: z.string() }) });
  const store: CacheBackingStore = {
    pullAll: async () => [],
    push: async () => {
      throw new Error("disk full");
    },
    delete: async () => undefined,
  };
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument))
    .withGenericStore(store)
    .build();

  const runnerListeners = process.listeners("unhandledRejection");
  process.removeAllListeners("unhandledRejection");
  const unhandled: unknown[] = [];
  const listener = (reason: unknown) => {
    unhandled.push(reason);
  };
  process.on("unhandledRejection", listener);
  try {
    void cache.put(itemDocument, "k", { n: "x" });
    await new Promise((resolve) => setTimeout(resolve, 50));
  } finally {
    process.off("unhandledRejection", listener);
    for (const runnerListener of runnerListeners) {
      process.on("unhandledRejection", runnerListener);
    }
  }

  assert.equal(unhandled.length, 1);
  assert.match(String((unhandled[0] as Error).message), /disk full/u);
  // The chain survived the rejection: the next operation still runs.
  await assert.rejects(cache.put(itemDocument, "k", { n: "y" }), /disk full/u);
});

test("storeChangeHandlerWritingBackQueuesAfterTheOperation", async () => {
  const itemDocument = defineDocumentType({ type: "item", schema: z.object({ n: z.string() }) });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-writeback-")), "generic.cc");
  const inner = new SingleFileMessagePackBackingStore(storePath);
  const changes = new EventEmitter();
  const pushed: string[] = [];
  const store: CacheBackingStore = {
    pullAll: () => inner.pullAll(),
    delete: (entry) => inner.delete(entry),
    push: async (entry) => {
      await inner.push(entry);
      pushed.push(entry.key);
      changes.emit("pushed", entry.key);
    },
  };
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument))
    .withGenericStore(store)
    .build();
  // The handler writes back and the store does not wait for it: that write queues behind the put.
  const writeBacks: Promise<unknown>[] = [];
  changes.on("pushed", (key: string) => {
    if (key === "k") {
      writeBacks.push(cache.put(itemDocument, "side", { n: "side" }));
    }
  });

  await cache.put(itemDocument, "k", { n: "x" });
  await Promise.all(writeBacks);

  assert.equal(writeBacks.length, 1);
  assert.deepEqual(pushed, ["k", "side"]);
  assert.deepEqual(cache.get(itemDocument, "k"), { n: "x" });
  assert.deepEqual(cache.get(itemDocument, "side"), { n: "side" });
  assert.deepEqual(
    (await inner.pullAll()).map((entry) => entry.key).sort(),
    cache.snapshot().map((entry) => entry.key).sort(),
  );
});

test("legacyKeyGlobalLoadsWithoutWritingAndFirstWriteLeavesOneGlobal", async () => {
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ t: z.string() }),
    global: true,
  });
  const storePath = join(await mkdtemp(join(tmpdir(), "cultcache-legacy-global-")), "generic.cc");
  const build = () => CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  const seed = CultCache.builder().withRegistry(defineDocumentRegistry(settingsDocument)).build();
  await seed.putGlobal(settingsDocument, { t: "old" });
  const legacy = { ...seed.getRequiredEnvelope(settingsDocument, CultCache.GLOBAL_KEY), key: "legacy" };
  await new SingleFileMessagePackBackingStore(storePath).push(legacy);
  const bytes = await readFile(storePath);

  const cache = build();
  await cache.pullAllBackingStores();
  assert.deepEqual(cache.getRequiredGlobal(settingsDocument), { t: "old" });
  assert.deepEqual(cache.get(settingsDocument, CultCache.GLOBAL_KEY), { t: "old" });
  assert.deepEqual(await readFile(storePath), bytes);

  await cache.putGlobal(settingsDocument, { t: "new" });
  assert.deepEqual(
    (await new SingleFileMessagePackBackingStore(storePath).pullAll()).map((entry) => entry.key),
    [CultCache.GLOBAL_KEY],
  );
  const reloaded = build();
  await reloaded.pullAllBackingStores();
  assert.deepEqual(reloaded.getRequiredGlobal(settingsDocument), { t: "new" });

  // Deleting an adopted global removes the legacy record.
  await new SingleFileMessagePackBackingStore(storePath).pushAll([legacy]);
  const deleting = build();
  await deleting.pullAllBackingStores();
  assert.equal(await deleting.deleteGlobal(settingsDocument), true);
  assert.deepEqual(await new SingleFileMessagePackBackingStore(storePath).pullAll(), []);

  // Two globals of one type on disk, under any keys, are refused.
  await new SingleFileMessagePackBackingStore(storePath).pushAll([legacy, { ...legacy, key: CultCache.GLOBAL_KEY }]);
  await assert.rejects(build().pullAllBackingStores(), /has multiple persisted entries/u);
});

test("concurrentPutAndAttachCannotLandRecordInNonHomeStore", async () => {
  const settingsDocument = defineDocumentType({ type: "settings", schema: z.object({ t: z.string() }) });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-put-attach-race-"));
  const genericPath = join(tempDir, "generic.cc");
  const settingsPath = join(tempDir, "settings.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();

  const pending = cache.put(settingsDocument, "app", { t: "x" });
  const attach = Promise.resolve().then(() =>
    cache.addBackingStore(new SingleFileMessagePackBackingStore(settingsPath), settingsDocument),
  );
  await pending;
  await assert.rejects(attach, /would move "settings" from the generic store/u);

  assert.deepEqual(
    (await new SingleFileMessagePackBackingStore(genericPath).pullAll()).map((entry) => entry.key),
    ["app"],
  );
  assert.equal(existsSync(settingsPath), false);
  const reloaded = CultCache.builder()
    .withRegistry(defineDocumentRegistry(settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(genericPath))
    .build();
  await reloaded.pullAllBackingStores();
  assert.deepEqual(reloaded.getRequired(settingsDocument, "app"), { t: "x" });
});

test("pullWithDuplicateGlobalLeavesCacheUnchanged", async () => {
  const itemDocument = defineDocumentType({ type: "item", schema: z.object({ name: z.string() }) });
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ theme: z.string() }),
    global: true,
  });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-duplicate-global-"));
  const storePath = join(tempDir, "generic.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument, settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.put(itemDocument, "potion", { name: "Potion" });
  await cache.putGlobal(settingsDocument, { theme: "ash" });
  const before = cache.snapshot();

  // A second record of the global type lands in the store file behind the cache's back.
  const stray = cache.getRequiredEnvelope(settingsDocument, CultCache.GLOBAL_KEY);
  await new SingleFileMessagePackBackingStore(storePath).push({ ...stray, key: "stray" });

  await assert.rejects(cache.pullAllBackingStores(), /has multiple persisted entries/u);
  assert.deepEqual(cache.snapshot(), before);
  assert.deepEqual(cache.getRequired(itemDocument, "potion"), { name: "Potion" });
  assert.deepEqual(cache.getRequiredGlobal(settingsDocument), { theme: "ash" });
});

test("pullWithThrowingIndexAccessorLeavesCacheUnchanged", async () => {
  const itemDocument = defineDocumentType({
    type: "item",
    schema: z.object({ name: z.string(), category: z.string() }),
    name: "name",
    indexes: {
      category: (item: { name: string; category: string }) => {
        if (item.name === "Bomb") {
          throw new Error("category accessor refuses Bomb");
        }
        return item.category;
      },
    },
  });
  const settingsDocument = defineDocumentType({
    type: "settings",
    schema: z.object({ theme: z.string() }),
    global: true,
  });
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-throwing-accessor-"));
  const storePath = join(tempDir, "generic.cc");
  const cache = CultCache.builder()
    .withRegistry(defineDocumentRegistry(itemDocument, settingsDocument))
    .withGenericStore(new SingleFileMessagePackBackingStore(storePath))
    .build();
  await cache.put(itemDocument, "potion", { name: "Potion", category: "consumable" });
  await cache.putGlobal(settingsDocument, { theme: "ash" });
  const before = cache.snapshot();

  // A record whose index accessor throws lands in the store file behind the cache's back.
  const potion = cache.getRequiredEnvelope(itemDocument, "potion");
  await new SingleFileMessagePackBackingStore(storePath).push({
    ...potion,
    key: "bomb",
    payload: encode({ name: "Bomb", category: "explosive" }),
  });

  await assert.rejects(cache.pullAllBackingStores(), /category accessor refuses Bomb/u);
  assert.deepEqual(cache.snapshot(), before);
  assert.deepEqual(cache.getRequired(itemDocument, "potion"), { name: "Potion", category: "consumable" });
  assert.equal(cache.get(itemDocument, "bomb"), undefined);
  assert.equal(cache.getKeyByName(itemDocument, "Potion"), "potion");
  assert.equal(cache.getKeyByName(itemDocument, "Bomb"), undefined);
  assert.equal(cache.getKeyByIndex(itemDocument, "category", "consumable"), "potion");
  assert.deepEqual(cache.getRequiredGlobal(settingsDocument), { theme: "ash" });
});

test("CultCache with zero stores writes and deletes in memory", async () => {
  const settingsDocument = defineDocumentType({ type: "settings", schema: z.object({ theme: z.string() }) });
  const cache = CultCache.builder().withRegistry(defineDocumentRegistry(settingsDocument)).build();
  await cache.put(settingsDocument, "x", { theme: "mem" });
  assert.deepEqual(cache.getRequired(settingsDocument, "x"), { theme: "mem" });
  assert.equal(await cache.delete(settingsDocument, "x"), true);
  assert.equal(cache.get(settingsDocument, "x"), undefined);
});

test("CultCache v1 MessagePack stores are readable across TS, Rust, C#, and Python", async () => {
  await buildInteropPeers();
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-interop-"));
  const writers = [
    {
      name: "ts",
      write: async (file: string) => writeTsInteropStore(file, "ts-writer"),
    },
    {
      name: "rust",
      write: async (file: string) => runJsonCommand("rust-write", rustInteropBinary, [
        "write",
        "--file", file,
        "--runtime-id", "rust-writer",
      ], cultcacheRsRoot),
    },
    {
      name: "csharp",
      write: async (file: string) => runJsonCommand("csharp-write", dotnetCommand, [
        csharpInteropDll,
        "write",
        "--file", file,
        "--runtime-id", "csharp-writer",
      ], cultLibRoot),
    },
    {
      name: "python",
      write: async (file: string) => runJsonCommand("python-write", pythonCommand, [
        "-m", "cultcache_py.interop",
        "write",
        "--file", file,
        "--runtime-id", "python-writer",
      ], cultcachePyRoot, { PYTHONPATH: cultcachePySrc }),
    },
  ];
  const readers = [
    {
      name: "ts",
      read: async (file: string) => readTsInteropStore(file),
    },
    {
      name: "rust",
      read: async (file: string) => runJsonCommand("rust-read", rustInteropBinary, [
        "read",
        "--file", file,
      ], cultcacheRsRoot),
    },
    {
      name: "csharp",
      read: async (file: string) => runJsonCommand("csharp-read", dotnetCommand, [
        csharpInteropDll,
        "read",
        "--file", file,
      ], cultLibRoot),
    },
    {
      name: "python",
      read: async (file: string) => runJsonCommand("python-read", pythonCommand, [
        "-m", "cultcache_py.interop",
        "read",
        "--file", file,
      ], cultcachePyRoot, { PYTHONPATH: cultcachePySrc }),
    },
  ];

  for (const writer of writers) {
    const file = join(tempDir, `${writer.name}.msgpack`);
    const written = await writer.write(file);
    const decoded = decode(await readFile(file)) as unknown[];
    assert.equal(decoded[0], "cultcache.store.v1");
    assert.ok(Array.isArray(decoded[1]), `${writer.name} did not write a schema catalog`);
    assert.ok(Array.isArray(decoded[2]), `${writer.name} did not write records`);

    for (const reader of readers) {
      const read = await reader.read(file);
      assert.equal(read.documentId, written.documentId, `${reader.name} failed to read ${writer.name}`);
      assert.equal(read.authorRuntimeId, written.authorRuntimeId);
      assert.equal(read.body, "The v1 store format is the contract.");
      assert.ok(read.tags.includes("interop"));
    }
  }

  // C# write-routed attaches one home store per type: each file is a complete v1 snapshot holding only its own records.
  const catalogFile = join(tempDir, "catalog.cc");
  const runFile = join(tempDir, "run.cc");
  const routed = await runJsonCommand("csharp-write-routed", dotnetCommand, [
    csharpInteropDll,
    "write-routed",
    catalogFile,
    runFile,
  ], cultLibRoot);
  const recordsIn = async (file: string) => {
    const decoded = decode(await readFile(file)) as unknown[];
    assert.equal(decoded[0], "cultcache.store.v1");
    assert.ok(Array.isArray(decoded[1]), `${file} has no schema catalog`);
    return (decoded[2] as unknown[][]).map((record) => ({ schemaId: record[1], key: record[0] }));
  };
  const catalogRecords = await recordsIn(catalogFile);
  const runRecords = await recordsIn(runFile);
  assert.deepEqual(catalogRecords.map((record) => record.key), ["note:csharp-routed"]);
  assert.deepEqual(runRecords.map((record) => record.key), ["run-note:csharp-routed"]);
  assert.notEqual(catalogRecords[0]?.schemaId, runRecords[0]?.schemaId);
  for (const reader of readers) {
    const read = await reader.read(catalogFile);
    assert.equal(read.documentId, routed.documentId, `${reader.name} failed to read the routed catalog store`);
    assert.equal(read.body, "One home store per document type.");
  }
});

test("CultCache interop reader accepts missing compatible trailing slots and rejects mismatched slots", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-interop-"));
  const compatible = join(tempDir, "compatible.msgpack");
  const mismatch = join(tempDir, "mismatch.msgpack");
  await writeTsInteropStore(compatible, "legacy-writer", { legacyPayload: true });
  assert.deepEqual(await readTsInteropStore(compatible), {
    schemaVersion: "cultcache.interop_note.v1",
    documentId: "note:legacy-writer",
    authorRuntimeId: "legacy-writer",
    title: "legacy-writer wrote a CultCache note",
    body: "The v1 store format is the contract.",
    tags: [],
  });

  await writeTsInteropStore(mismatch, "bad-writer", { mismatchedPayload: true });
  await assert.rejects(
    () => readTsInteropStore(mismatch),
    /Expected string/u,
  );
});

test("CultCache inspector decodes v1 store catalog and record payloads from bytes", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-inspector-"));
  const file = join(tempDir, "cache.cc");
  const written = await writeTsInteropStore(file, "inspector");

  const inspection = inspectCultCacheBytes(file, await readFile(file));
  assert.equal(inspection.format, "cultcache.store.v1");
  assert.equal(inspection.catalog.length, 1);
  assert.equal(inspection.catalog[0]?.schemaName, "cultcache.interop-note");
  assert.equal(inspection.records.length, 1);
  assert.equal(inspection.records[0]?.key, written.documentId);
  assert.deepEqual(inspection.records[0]?.payloadPreview, [
    "cultcache.interop_note.v1",
    written.documentId,
    written.authorRuntimeId,
    written.title,
    written.body,
    written.tags,
  ]);
});

test("CultCache inspector recovers schema-stamped records missing catalog entries", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-inspector-"));
  const file = join(tempDir, "missing-catalog.cc");

  await writeFile(
    file,
    encode([
      "cultcache.store.v1",
      [],
      [
        [
          "record-1",
          "sha256:stale-schema-id-from-cold-record",
          "2026-06-25T12:00:00Z",
          encode([
            "tests.schema_stamped_entry.v1",
            "schema-stamped",
            "still readable",
          ]),
        ],
      ],
    ]),
  );

  const inspection = inspectCultCacheBytes(file, await readFile(file));
  assert.equal(inspection.catalog.length, 1);
  assert.equal(inspection.catalog[0]?.schemaId, "sha256:stale-schema-id-from-cold-record");
  assert.equal(inspection.catalog[0]?.schemaName, "tests.schema_stamped_entry");
  assert.equal(inspection.records[0]?.schemaName, "tests.schema_stamped_entry");
  assert.deepEqual(inspection.records[0]?.payloadPreview, [
    "tests.schema_stamped_entry.v1",
    "schema-stamped",
    "still readable",
  ]);
});

interface InteropNote {
  schemaVersion: "cultcache.interop_note.v1";
  documentId: string;
  authorRuntimeId: string;
  title: string;
  body: string;
  tags: string[];
}

const interopNoteDocument = defineDocumentType({
  type: "cultcache.interop-note",
  schemaId: "cultcache.interop-note",
  schemaName: "cultcache.interop-note",
  schemaVersion: "cultcache.interop_note.v1",
  contentHash: "cultcache.interop-note",
  canonicalSchemaJson: "{\"schemaName\":\"cultcache.interop-note\",\"schemaVersion\":\"cultcache.interop_note.v1\",\"members\":[{\"slot\":0,\"name\":\"SchemaVersion\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":1,\"name\":\"DocumentId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":true},{\"slot\":2,\"name\":\"AuthorRuntimeId\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":3,\"name\":\"Title\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":4,\"name\":\"Body\",\"type\":\"System.String\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false},{\"slot\":5,\"name\":\"Tags\",\"type\":\"System.String[]\",\"isReference\":false,\"many\":false,\"targetSchemaName\":null,\"indexAlias\":null,\"isName\":false}]}",
  compatibleSchemaIds: ["cultcache.interop-note"],
  members: [
    { slot: 0, memberName: "SchemaVersion", typeName: "System.String" },
    { slot: 1, memberName: "DocumentId", typeName: "System.String", isName: true },
    { slot: 2, memberName: "AuthorRuntimeId", typeName: "System.String" },
    { slot: 3, memberName: "Title", typeName: "System.String" },
    { slot: 4, memberName: "Body", typeName: "System.String" },
    { slot: 5, memberName: "Tags", typeName: "System.String[]" },
  ],
  schema: z.object({
    schemaVersion: z.literal("cultcache.interop_note.v1"),
    documentId: z.string().min(1),
    authorRuntimeId: z.string().min(1),
    title: z.string().min(1),
    body: z.string().min(1),
    tags: z.array(z.string().min(1)),
  }),
  formatter: {
    encode(value: InteropNote): Uint8Array {
      return encode([
        value.schemaVersion,
        value.documentId,
        value.authorRuntimeId,
        value.title,
        value.body,
        value.tags,
      ]);
    },
    decode(payload: Uint8Array): InteropNote {
      const decoded = decode(payload);
      if (!Array.isArray(decoded) || decoded.length < 5) {
        throw new Error("CultCache interop note payload must be a MessagePack slot array.");
      }
      const [schemaVersion, documentId, authorRuntimeId, title, body, tags] = decoded;
      return interopNoteDocument.schema.parse({
        schemaVersion,
        documentId,
        authorRuntimeId,
        title,
        body,
        tags: Array.isArray(tags) ? tags : [],
      });
    },
  },
});

async function writeTsInteropStore(
  file: string,
  runtimeId: string,
  options: { legacyPayload?: boolean; mismatchedPayload?: boolean } = {},
): Promise<InteropNote> {
  const cache = CultCache.builder()
    .withDocumentType(interopNoteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(file))
    .build();
  const note: InteropNote = {
    schemaVersion: "cultcache.interop_note.v1",
    documentId: `note:${runtimeId}`,
    authorRuntimeId: runtimeId,
    title: `${runtimeId} wrote a CultCache note`,
    body: "The v1 store format is the contract.",
    tags: [runtimeId, "ts", "interop"],
  };

  if (options.legacyPayload || options.mismatchedPayload) {
    const payload = options.mismatchedPayload
      ? encode([note.schemaVersion, note.documentId, 42, note.title, note.body, note.tags])
      : encode([note.schemaVersion, note.documentId, note.authorRuntimeId, note.title, note.body]);
    await new SingleFileMessagePackBackingStore(file).push({
      key: note.documentId,
      type: interopNoteDocument.type,
      schemaId: interopNoteDocument.schemaId,
      catalogEntry: {
        schemaId: interopNoteDocument.schemaId!,
        schemaName: interopNoteDocument.schemaName!,
        schemaVersion: interopNoteDocument.schemaVersion!,
        contentHash: interopNoteDocument.contentHash!,
        canonicalSchemaJson: interopNoteDocument.canonicalSchemaJson!,
        compatibleSchemaIds: [interopNoteDocument.schemaId!],
        members: interopNoteDocument.members,
      },
      storedAt: new Date().toISOString(),
      payload,
    });
    return note;
  }

  await cache.put(interopNoteDocument, note.documentId, note);
  return note;
}

async function readTsInteropStore(file: string): Promise<InteropNote> {
  const cache = CultCache.builder()
    .withDocumentType(interopNoteDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(file))
    .build();
  await cache.pullAllBackingStores();
  const notes = cache.getAll(interopNoteDocument);
  const note = notes[0];
  if (!note) {
    throw new Error("No cultcache.interop-note records found.");
  }
  return note;
}

async function buildInteropPeers(): Promise<void> {
  if (!(await exists(rustInteropBinary))) {
    await execAsync(`"${cargoCommand}" build --quiet --example cultcache_interop`, {
      cwd: cultcacheRsRoot,
    });
  }
  if (!(await exists(csharpInteropDll))) {
    await execAsync(`"${dotnetCommand}" build "${csharpInteropProject}" -nologo`, {
      cwd: cultLibRoot,
    });
  }
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function runJsonCommand(
  name: string,
  command: string,
  args: string[],
  cwd: string,
  env: NodeJS.ProcessEnv = {},
): Promise<any> {
  const { stdout, stderr } = await execFileAsync(command, args, {
    cwd,
    env: { ...process.env, ...env },
    timeout: 30_000,
  });
  const trimmed = stdout.trim();
  if (!trimmed) {
    throw new Error(`${name} produced no stdout.\n${stderr}`);
  }

  return JSON.parse(trimmed.split(/\r?\n/).at(-1) as string);
}
