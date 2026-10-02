import assert from "node:assert/strict";
import { exec, execFile } from "node:child_process";
import { EventEmitter } from "node:events";
import { existsSync } from "node:fs";
import { access, lstat, mkdir, mkdtemp, readdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
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
import { SchemaConflictError, StoreUnreadableError } from "../src/store-format";
import type { AnyCultCacheDocumentDefinition, CacheBackingStore, CultCacheEnvelope, CultCacheSchema } from "../src/types";

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

test("SingleFileMessagePackBackingStore refuses a record whose schema the catalog does not publish, whatever its payload opens with", async () => {
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

  await assert.rejects(() => cache.pullAllBackingStores(), StoreUnreadableError);
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

// Records and catalog entries a runtime does not own survive its writes. C# writes a store and each other runtime writes its own
// note into it: C# reads its note back, and its record and catalog entry are as C# wrote them. C# then writes again, and the other
// runtime's record and catalog entry are as that runtime wrote them. Identity is compared on the decoded MessagePack values, every
// slot of the entry and of its members included.
test("A runtime's writes lay back another runtime's records and catalog entries unchanged", async () => {
  await buildInteropPeers();
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-lay-back-"));
  const csharpWrite = (file: string, runtimeId: string) =>
    runJsonCommand("csharp-write", dotnetCommand, [csharpInteropDll, "write", "--file", file, "--runtime-id", runtimeId], cultLibRoot);
  const others = [
    {
      name: "rust",
      write: (file: string) => runJsonCommand("rust-write", rustInteropBinary, ["write", "--file", file, "--runtime-id", "rust-writer"], cultcacheRsRoot),
    },
    { name: "ts", write: (file: string) => writeTsInteropStore(file, "ts-writer") },
    {
      name: "python",
      write: (file: string) => runJsonCommand("python-write", pythonCommand, [
        "-m", "cultcache_py.interop", "write", "--file", file, "--runtime-id", "python-writer",
      ], cultcachePyRoot, { PYTHONPATH: cultcachePySrc }),
    },
  ];
  const stored = async (file: string, key: string) => {
    const [, catalog, records] = decode(await readFile(file)) as [string, unknown[][], unknown[][]];
    const record = records.find((candidate) => candidate[0] === key);
    assert.ok(record, `${file} holds no record ${key}`);
    const entry = catalog.find((candidate) => candidate[0] === record[1]);
    assert.ok(entry, `${file} publishes no entry owning ${String(record[1])}`);
    return { record, entry };
  };

  for (const other of others) {
    const file = join(tempDir, `${other.name}.cc`);
    await csharpWrite(file, "csharp-writer");
    const csharp = await stored(file, "note:csharp-writer");

    const written = await other.write(file);
    assert.deepEqual(await stored(file, "note:csharp-writer"), csharp, `${other.name} rewrote the C# record or its catalog entry`);
    const read = await runJsonCommand("csharp-read", dotnetCommand, [csharpInteropDll, "read", "--file", file], cultLibRoot);
    assert.ok(read.documentId === "note:csharp-writer" || read.documentId === written.documentId, `C# failed to read after ${other.name}`);
    const theirs = await stored(file, written.documentId);
    assert.notEqual(theirs.record[1], csharp.record[1], `${other.name} writes under its own id`);

    await csharpWrite(file, "csharp-again");
    assert.deepEqual(await stored(file, written.documentId), theirs, `C# rewrote the ${other.name} record or its catalog entry`);
    assert.deepEqual(await stored(file, "note:csharp-writer"), csharp);
  }
});

// A schema renamed under a stable id: the record's schema id names its type, and the name its catalog entry now carries is metadata.
// A reader that resolved the record by that name would not find its type, so every runtime that can hold the stable id opens it. The
// C# reader is not among them: its schema ids are content hashes, which this file does not carry.
test("CultCache v1 stores stay readable across TS, Rust and Python when a schema is renamed under a stable id", async () => {
  await buildInteropPeers();
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-rename-interop-")), "renamed.msgpack");
  const written = await writeTsInteropStore(file, "ts-before-rename");
  await new SingleFileMessagePackBackingStore(file).push({
    key: "note:ts-after-rename",
    type: "cultcache.interop-note.renamed",
    schemaId: interopNoteDocument.schemaId,
    catalogEntry: {
      schemaId: interopNoteDocument.schemaId!,
      schemaName: "cultcache.interop-note.renamed",
      schemaVersion: interopNoteDocument.schemaVersion!,
      contentHash: "renamed",
      canonicalSchemaJson: interopNoteDocument.canonicalSchemaJson!,
      compatibleSchemaIds: [interopNoteDocument.schemaId!],
      members: interopNoteDocument.members,
    },
    storedAt: new Date().toISOString(),
    payload: encode([
      written.schemaVersion,
      "note:ts-after-rename",
      "ts-after-rename",
      "ts wrote a note under the renamed schema",
      "The v1 store format is the contract.",
      ["interop"],
    ]),
  });
  const catalog = decode(await readFile(file)) as unknown[][];
  assert.deepEqual((catalog[1] as unknown[][]).map((entry) => [entry[0], entry[1]]), [["cultcache.interop-note", "cultcache.interop-note.renamed"]]);

  const readers = [
    { name: "ts", read: async () => readTsInteropStore(file) },
    { name: "rust", read: async () => runJsonCommand("rust-read", rustInteropBinary, ["read", "--file", file], cultcacheRsRoot) },
    {
      name: "python",
      read: async () =>
        runJsonCommand("python-read", pythonCommand, ["-m", "cultcache_py.interop", "read", "--file", file], cultcachePyRoot, {
          PYTHONPATH: cultcachePySrc,
        }),
    },
  ];
  for (const reader of readers) {
    const read = await reader.read();
    assert.ok(
      ["note:ts-before-rename", "note:ts-after-rename"].includes(read.documentId),
      `${reader.name} opened the renamed store and read a note (${read.documentId})`,
    );
    assert.equal(read.body, "The v1 store format is the contract.");
  }
});

// A record TS wrote under an older schema id and another schema name. The C# note type declares that id compatible, so C# opens the
// record, stamps it with its own id when it writes it back, and every runtime opens the result.
test("A C# type that declares a compatible schema id opens a TypeScript record under it and rewrites it under its own id", async () => {
  await buildInteropPeers();
  const legacyId = "cultcache.interop-note.legacy-id";
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-compat-interop-")), "legacy.msgpack");
  await new SingleFileMessagePackBackingStore(file).push({
    key: "note:ts-legacy",
    type: "cultcache.interop-note.legacy",
    schemaId: legacyId,
    catalogEntry: {
      schemaId: legacyId,
      schemaName: "cultcache.interop-note.legacy",
      schemaVersion: interopNoteDocument.schemaVersion!,
      contentHash: "legacy",
      canonicalSchemaJson: interopNoteDocument.canonicalSchemaJson!,
      compatibleSchemaIds: [legacyId],
      members: interopNoteDocument.members,
    },
    storedAt: new Date().toISOString(),
    payload: encode([
      "cultcache.interop_note.v1",
      "note:ts-legacy",
      "ts-legacy",
      "ts wrote a note under an older schema id",
      "The v1 store format is the contract.",
      ["interop"],
    ]),
  });
  const before = decode(await readFile(file)) as unknown[][];
  assert.equal((before[2] as unknown[][])[0]![1], legacyId);

  const rewritten = await runJsonCommand("csharp-rewrite", dotnetCommand, [csharpInteropDll, "rewrite", "--file", file], cultLibRoot);
  assert.equal(rewritten.documentId, "note:ts-legacy");
  const after = decode(await readFile(file)) as unknown[][];
  const stampedId = (after[2] as unknown[][])[0]![1] as string;
  assert.notEqual(stampedId, legacyId, "the rewrite stamped the record with the registered id");
  const entry = (after[1] as unknown[][]).find((candidate) => candidate[0] === stampedId) as unknown[];
  assert.ok(entry, "the catalog publishes the id the record now carries");
  assert.ok((entry[5] as string[]).includes(legacyId), "the registered entry still lists the older id");

  const readers = [
    { name: "csharp", read: async () => runJsonCommand("csharp-read", dotnetCommand, [csharpInteropDll, "read", "--file", file], cultLibRoot) },
    { name: "ts", read: async () => readTsInteropStore(file) },
    { name: "rust", read: async () => runJsonCommand("rust-read", rustInteropBinary, ["read", "--file", file], cultcacheRsRoot) },
    {
      name: "python",
      read: async () =>
        runJsonCommand("python-read", pythonCommand, ["-m", "cultcache_py.interop", "read", "--file", file], cultcachePyRoot, {
          PYTHONPATH: cultcachePySrc,
        }),
    },
  ];
  for (const reader of readers) {
    const read = await reader.read();
    assert.equal(read.documentId, "note:ts-legacy", `${reader.name} opened the rewritten store`);
    assert.equal(read.body, "The v1 store format is the contract.");
  }
});

test("CultCache element ids cross C#, TypeScript, Python and Rust as ordinary members", async () => {
  await buildInteropPeers();
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-deck-"));
  const file = join(tempDir, "deck.cc");
  const written = await runJsonCommand("csharp-write-deck", dotnetCommand, [
    csharpInteropDll, "write-deck", "--file", file,
  ], cultLibRoot);
  assert.equal(written.ids.length, 3);
  assert.equal(new Set(written.ids).size, 3, "two look-alike marks got distinct ids");
  for (const id of written.ids) assert.match(id, /^[0-9a-f]{12}$/u);

  // TypeScript: the id is slot 1 of each mark, read from the raw payload.
  const store = decode(await readFile(file)) as any[];
  const payload = decode(store[2][0][3]) as any[];
  assert.deepEqual(payload[1].map((mark: any[]) => mark[1]), written.ids);

  // Python and Rust read the same store.
  const python = await runJsonCommand("python-deck", pythonCommand, [
    "-c",
    "import msgpack,json,sys;s=msgpack.unpackb(open(sys.argv[1],'rb').read(),raw=False);p=msgpack.unpackb(s[2][0][3],raw=False);print(json.dumps({'ids':[m[1] for m in p[1]]}))",
    file,
  ], cultcachePyRoot);
  assert.deepEqual(python.ids, written.ids);
  const rust = await runJsonCommand("rust-read-deck", rustInteropBinary, ["read-deck", "--file", file], cultcacheRsRoot);
  assert.deepEqual(rust.ids, written.ids);

  // TypeScript writes the ids; C# keeps them.
  const tsIds = ["a00000000001", "a00000000002", "a00000000003"];
  store[2][0][3] = encode(["deck:csharp", [["twin", tsIds[0]], ["twin", tsIds[1]], ["other", tsIds[2]]]]);
  const tsFile = join(tempDir, "ts-deck.cc");
  await writeFile(tsFile, encode(store));
  const back = await runJsonCommand("csharp-read-deck", dotnetCommand, [
    csharpInteropDll, "read-deck", "--file", tsFile,
  ], cultLibRoot);
  assert.deepEqual(back.ids, tsIds);
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

test("CultCache inspector refuses a record whose schema the catalog does not publish, and a store of the wrong slot count", async () => {
  const tempDir = await mkdtemp(join(tmpdir(), "cultcache-inspector-"));
  const record = [
    "record-1",
    "sha256:stale-schema-id-from-cold-record",
    "2026-06-25T12:00:00Z",
    encode(["tests.schema_stamped_entry.v1", "schema-stamped", "still readable"]),
  ];

  assert.throws(
    () => inspectCultCacheBytes("missing-catalog.cc", encode(["cultcache.store.v1", [], [record]])),
    /references missing schema id/u,
  );
  assert.throws(() => inspectCultCacheBytes("header-only.cc", encode(["cultcache.store.v1"])), /top-level slots/u);
  assert.throws(
    () => inspectCultCacheBytes("extra-slot.cc", encode(["cultcache.store.v1", [], [], 0])),
    /top-level slots/u,
  );
  await rm(tempDir, { recursive: true, force: true });
});

// A record written under an id its schema only lists as compatible is stamped with the registered id, and the store the write
// leaves opens and takes the next write. The envelope carries a catalog entry under the foreign id, as a replicated one does.
const foreignIdDocument = defineDocumentType({
  type: "tests.foreign-id",
  schema: z.object({ name: z.string() }),
  schemaId: "tests.foreign-id.current",
  schemaName: "tests.foreign-id",
  schemaVersion: "tests.foreign_id.v1",
  compatibleSchemaIds: ["tests.foreign-id.current", "tests.foreign-id.older"],
});

async function reopenForeignIdStore(file: string): Promise<CultCache> {
  const cache = CultCache.builder()
    .withDocumentType(foreignIdDocument)
    .withGenericStore(new SingleFileMessagePackBackingStore(file))
    .build();
  await cache.pullAllBackingStores();
  return cache;
}

function schemaIdsOf(bytes: Uint8Array): { record: string; catalog: string[] } {
  const decoded = decode(bytes) as [string, unknown[][], unknown[][]];
  return { record: decoded[2][0]![1] as string, catalog: decoded[1].map((entry) => entry[0] as string) };
}

test("putEnvelope under a compatible schema id writes a store that reopens and takes the next write", async () => {
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-foreign-id-")), "store.msgpack");
  const cache = await reopenForeignIdStore(file);
  const seed = await cache.put(foreignIdDocument, "seed", { name: "seed" });
  const payload = cache.getRequiredEnvelope(foreignIdDocument, "seed").payload;

  await cache.putEnvelope(foreignIdDocument, {
    key: "foreign",
    type: "tests.foreign-id",
    schemaId: "tests.foreign-id.older",
    payload,
    storedAt: "2026-09-30T00:00:00.0000000Z",
    catalogEntry: {
      schemaId: "tests.foreign-id.older",
      schemaName: "tests.foreign-id",
      schemaVersion: "tests.foreign_id.v1",
      contentHash: "tests.foreign-id.older",
      canonicalSchemaJson: "",
      compatibleSchemaIds: ["tests.foreign-id.older"],
      members: [],
    },
  });

  const ids = schemaIdsOf(await readFile(file));
  assert.deepEqual(ids, { record: "tests.foreign-id.current", catalog: ["tests.foreign-id.current"] });
  const reopened = await reopenForeignIdStore(file);
  assert.deepEqual(reopened.getRequired(foreignIdDocument, "foreign"), seed);
  await reopened.put(foreignIdDocument, "next", { name: "next" });
  assert.deepEqual((await reopenForeignIdStore(file)).getRequired(foreignIdDocument, "next"), { name: "next" });
});

test("the store publishes a record's schema id even when the envelope's catalog entry does not", async () => {
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-store-publishes-")), "store.msgpack");
  const store = new SingleFileMessagePackBackingStore(file);
  await store.push({
    key: "k",
    type: "tests.store-publishes",
    schemaId: "tests.store-publishes.record",
    payload: encode({ name: "k" }),
    storedAt: "2026-09-30T00:00:00.0000000Z",
    catalogEntry: {
      schemaId: "tests.store-publishes.other",
      schemaName: "tests.store-publishes",
      schemaVersion: "tests.store_publishes.v1",
      contentHash: "tests.store-publishes.other",
      canonicalSchemaJson: "",
      compatibleSchemaIds: ["tests.store-publishes.other"],
      members: [],
    },
  });
  assert.deepEqual(schemaIdsOf(await readFile(file)), { record: "tests.store-publishes.record", catalog: ["tests.store-publishes.record"] });
  assert.equal((await new SingleFileMessagePackBackingStore(file).pullAll()).length, 1);
});

// The catalog a write leaves is derived from its records: one entry per carried id, chosen and never merged, the same whatever order
// the records arrive in. A registered (supplied) entry wins over an arrived (read back) entry with the same own id.
const writerEntry = (schemaId: string, schemaName: string, contentHash: string, compatibleSchemaIds: string[]) => ({
  schemaId,
  schemaName,
  schemaVersion: `${schemaName}.v1`,
  contentHash,
  canonicalSchemaJson: "",
  compatibleSchemaIds,
  members: [],
});
const writerRecord = (key: string, type: string, schemaId: string, catalogEntry?: ReturnType<typeof writerEntry>) => ({
  key,
  type,
  schemaId,
  payload: encode({ key }),
  storedAt: "2026-09-30T00:00:00.0000000Z",
  ...(catalogEntry ? { catalogEntry } : {}),
});
const rawCatalogEntry = (entry: ReturnType<typeof writerEntry>) => [
  entry.schemaId,
  entry.schemaName,
  entry.schemaVersion,
  entry.contentHash,
  entry.canonicalSchemaJson,
  entry.compatibleSchemaIds,
  [],
];
const catalogOf = async (file: string): Promise<unknown[][]> =>
  (decode(await readFile(file)) as [string, unknown[][], unknown[][]])[1];

test("a registered entry is written as it is, over the arrived entries with the same own id, in either order", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-registered-wins-"));
  const stale1 = writerEntry("tests.x", "tests.n", "a-stale-1", ["tests.x", "old"]);
  const stale2 = writerEntry("tests.x", "tests.n", "a-stale-2", ["tests.x"]);
  const registered = writerRecord("a", "tests.n", "tests.x", writerEntry("tests.x", "tests.n", "fresh", ["tests.x"]));

  for (const [name, entries] of [["12", [stale1, stale2]], ["21", [stale2, stale1]]] as const) {
    const file = join(dir, `${name}.msgpack`);
    await writeFile(
      file,
      encode(["cultcache.store.v1", entries.map(rawCatalogEntry), [["b", "tests.x", "2026-09-30T00:00:00.0000000Z", encode({ b: 1 })]]]),
    );
    await new SingleFileMessagePackBackingStore(file).push(registered);
    const catalog = await catalogOf(file);
    assert.equal(catalog.length, 1, name);
    assert.equal(catalog[0]![3], "fresh", `${name}: the registered entry's hash is written`);
    assert.deepEqual(catalog[0]![5], ["tests.x"], `${name}: written as chosen, no union with the arrived entries' ids`);
  }
});

test("a rename with a stable schema id: a whole-view write names the registered schema", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-rename-"));
  const file = join(dir, "rename.msgpack");
  await writeFile(
    file,
    encode(["cultcache.store.v1", [rawCatalogEntry(writerEntry("tests.x", "tests.old", "stale", ["tests.x"]))], [["a", "tests.x", "2026-09-30T00:00:00.0000000Z", encode({ a: 1 })]]]),
  );
  await new SingleFileMessagePackBackingStore(file).pushAll([writerRecord("a", "tests.new", "tests.x", writerEntry("tests.x", "tests.new", "fresh", ["tests.x"]))]);
  assert.deepEqual((await catalogOf(file)).map((entry) => [entry[1], entry[3]]), [["tests.new", "fresh"]]);
});

test("two entries of one tier that share an own id and disagree on the schema name refuse the write, typed", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-disagree-"));
  const first = writerRecord("b", "tests.first", "tests.x", writerEntry("tests.x", "tests.first", "h1", ["tests.x"]));
  const second = writerRecord("a", "tests.first", "tests.x", writerEntry("tests.x", "tests.second", "h2", ["tests.x"]));
  for (const [name, order] of [["fs", [first, second]], ["sf", [second, first]]] as const) {
    const file = join(dir, `${name}.msgpack`);
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pushAll([...order]),
      (error) =>
        error instanceof SchemaConflictError &&
        error.schemaId === "tests.x" &&
        error.recordKey === "a" &&
        [...error.schemaNames].sort().join() === "tests.first,tests.second",
      name,
    );
  }
});

test("records of different types under one schema id refuse the write, typed, and no record changes type", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-retype-"));
  const a = writerRecord("a", "tests.n", "tests.y", writerEntry("tests.x", "tests.n", "h1", ["tests.x", "tests.y"]));
  const b = writerRecord("b", "tests.m", "tests.y", writerEntry("tests.y", "tests.m", "h2", ["tests.y"]));

  for (const [name, order] of [["ab", [a, b]], ["ba", [b, a]]] as const) {
    const file = join(dir, `${name}.msgpack`);
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pushAll([...order]),
      (error) => error instanceof SchemaConflictError && error.schemaId === "tests.y" && error.schemaNames.join() === "tests.m,tests.n",
      name,
    );
  }
});

test("an entry that owns an id is chosen over one that lists it, and an entry no record needs is not written", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-own-first-"));
  const owner = writerRecord("a", "tests.n", "tests.y", writerEntry("tests.y", "tests.n", "h1", ["tests.y"]));
  const lister = writerRecord("b", "tests.n", "tests.x", writerEntry("tests.x", "tests.n", "h2", ["tests.x", "tests.y"]));
  const onlyOwner = join(dir, "own.msgpack");
  await new SingleFileMessagePackBackingStore(onlyOwner).pushAll([owner]);
  assert.deepEqual((await catalogOf(onlyOwner)).map((entry) => entry[0]), ["tests.y"]);

  // The lister carried only its own id: its list of y is not needed, and the owner's entry publishes y.
  for (const [name, order] of [["lo", [lister, owner]], ["ol", [owner, lister]]] as const) {
    const file = join(dir, `${name}.msgpack`);
    await new SingleFileMessagePackBackingStore(file).pushAll([...order]);
    const catalog = await catalogOf(file);
    assert.deepEqual(catalog.map((entry) => [entry[0], entry[5]]).sort(), [["tests.x", ["tests.x", "tests.y"]], ["tests.y", ["tests.y"]]], name);
  }
});

test("entries of one tier that tie are taken in one fixed order, and a registered entry that lists an id is chosen over an arrived one", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-tie-"));
  // The content hash orders them against the order their compatible ids would give: h1 sorts first, its list sorts last.
  const one = writerRecord("a", "tests.n", "tests.x", writerEntry("tests.x", "tests.n", "h2", ["tests.x"]));
  const two = writerRecord("b", "tests.n", "tests.x", writerEntry("tests.x", "tests.n", "h1", ["tests.x", "tests.y"]));
  for (const [name, order] of [["12", [one, two]], ["21", [two, one]]] as const) {
    const file = join(dir, `${name}.msgpack`);
    await new SingleFileMessagePackBackingStore(file).pushAll([...order]);
    assert.deepEqual((await catalogOf(file)).map((entry) => entry[3]), ["h1"], name);
  }

  // z sits under old, which the arrived entry (id x) lists; the supplied entry (id r) lists it too: the registered one publishes it.
  const file = join(dir, "lister.msgpack");
  await writeFile(
    file,
    encode([
      "cultcache.store.v1",
      [rawCatalogEntry(writerEntry("tests.x", "tests.n", "stale", ["tests.x", "old"]))],
      [["z", "old", "2026-09-30T00:00:00.0000000Z", encode({ z: 1 })]],
    ]),
  );
  await new SingleFileMessagePackBackingStore(file).push(writerRecord("a", "tests.n", "tests.r", writerEntry("tests.r", "tests.n", "fresh", ["tests.r", "old"])));
  assert.deepEqual((await catalogOf(file)).map((entry) => entry[0]), ["tests.r"]);
});

// A registered entry that owns an id keeps it against an arrived entry that owns it too and lists the id another record sits under,
// whichever of the two ids sorts first: the arrived entry's list is not merged in, so the record under the listed id is published by
// no chosen entry.
for (const [ownId, listedId] of [["tests.a", "tests.b"], ["tests.b", "tests.a"]] as const) {
  test(`an arrived entry that owns a registered id does not replace it nor publish what it lists (${ownId})`, async () => {
    const dir = await mkdtemp(join(tmpdir(), "cultcache-guard-"));
    const file = join(dir, "guard.msgpack");
    await writeFile(
      file,
      encode([
        "cultcache.store.v1",
        [rawCatalogEntry(writerEntry(ownId, "tests.arrived", "stale", [ownId, listedId]))],
        [["k2", listedId, "2026-09-30T00:00:00.0000000Z", encode({ k: 2 })]],
      ]),
    );
    const before = await readFile(file);
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).push(writerRecord("k1", "tests.registered", ownId, writerEntry(ownId, "tests.registered", "fresh", [ownId]))),
      (error) => error instanceof SchemaConflictError && error.schemaId === listedId && error.recordKey === "k2",
    );
    assert.ok(before.equals(await readFile(file)), "the file is left as it was");
  });
}

// Ids are taken in sorted order, so which entry survives, and so which id the refusal names, does not depend on the order the records
// arrive in.
test("the refusal names the same id whatever order the records arrive in", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-sorted-"));
  const one = writerRecord("r1", "tests.n", "tests.y", writerEntry("tests.x", "tests.n", "h1", ["tests.x", "tests.y"]));
  const two = writerRecord("r2", "tests.n", "tests.z", writerEntry("tests.x", "tests.n", "h2", ["tests.x", "tests.z"]));
  for (const [name, order] of [["yz", [one, two]], ["zy", [two, one]]] as const) {
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(join(dir, `${name}.msgpack`)).pushAll([...order]),
      (error) => error instanceof SchemaConflictError && error.schemaId === "tests.y" && error.recordKey === "r1",
      name,
    );
  }
});

// A write may not retype records already in the file: across tiers the id keeps the type its records resolve to, unless the entries of
// both tiers own it, which is a rename under a stable id and the registered descriptor wins.
test("a write that would change the type an existing record resolves to refuses, typed, and leaves the file", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-cross-tier-"));
  const at = "2026-09-30T00:00:00.0000000Z";
  const cases = [
    {
      name: "registered entry lists the id, arrived entry owns it",
      catalog: writerEntry("tests.x", "tests.arrived", "h1", ["tests.x"]),
      registered: writerRecord("a", "tests.registered", "tests.x", writerEntry("tests.r", "tests.registered", "h2", ["tests.r", "tests.x"])),
    },
    {
      name: "registered entry owns the id, arrived entry lists it",
      catalog: writerEntry("tests.r", "tests.arrived", "h1", ["tests.r", "tests.x"]),
      registered: writerRecord("a", "tests.registered", "tests.x", writerEntry("tests.x", "tests.registered", "h2", ["tests.x"])),
    },
  ];
  for (const { name, catalog, registered } of cases) {
    const file = join(dir, `${name}.msgpack`);
    await writeFile(file, encode(["cultcache.store.v1", [rawCatalogEntry(catalog)], [["z", "tests.x", at, encode({ z: 1 })]]]));
    const before = await readFile(file);
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).push(registered),
      (error) =>
        error instanceof SchemaConflictError &&
        error.schemaId === "tests.x" &&
        [...error.schemaNames].sort().join() === "tests.arrived,tests.registered",
      name,
    );
    assert.ok(before.equals(await readFile(file)), `${name}: the file is left as it was`);
  }

  // The same registered record renames the schema when both tiers own the id: it writes, and the reader finds every record.
  const file = join(dir, "rename.msgpack");
  await writeFile(file, encode(["cultcache.store.v1", [rawCatalogEntry(writerEntry("tests.x", "tests.arrived", "h1", ["tests.x"]))], [["z", "tests.x", at, encode({ z: 1 })]]]));
  await new SingleFileMessagePackBackingStore(file).push(writerRecord("a", "tests.registered", "tests.x", writerEntry("tests.x", "tests.registered", "h2", ["tests.x"])));
  assert.deepEqual((await catalogOf(file)).map((entry) => [entry[0], entry[1]]), [["tests.x", "tests.registered"]]);
  assert.equal((await new SingleFileMessagePackBackingStore(file).pullAll()).length, 2);
});

test("a record no chosen entry publishes refuses the write, typed, and leaves the file", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-orphan-"));
  const file = join(dir, "orphan.msgpack");
  // z sits under old, which only the arrived entry (id x) lists; the supplied entry owns x and does not.
  await writeFile(
    file,
    encode([
      "cultcache.store.v1",
      [rawCatalogEntry(writerEntry("tests.x", "tests.legacy", "stale", ["tests.x", "old"]))],
      [["z", "old", "2026-09-30T00:00:00.0000000Z", encode({ z: 1 })]],
    ]),
  );
  const before = await readFile(file);
  await assert.rejects(
    () => new SingleFileMessagePackBackingStore(file).push(writerRecord("a", "tests.legacy", "tests.x", writerEntry("tests.x", "tests.legacy", "fresh", ["tests.x"]))),
    (error) => error instanceof SchemaConflictError && error.schemaId === "old" && error.recordKey === "z",
  );
  assert.ok(before.equals(await readFile(file)), "the file is left as it was");
});

test("an id an entry owns names that entry, not one that lists it as compatible", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-own-id-"));
  for (const vector of ["own-id-over-compatible-v3.bin", "compatible-before-owner-v3.bin"]) {
    const file = await copyVector(dir, join(c2aVectors, "readability", vector), "own.msgpack");
    const envelopes = await new SingleFileMessagePackBackingStore(file).pullAll();
    assert.deepEqual(envelopes.map((envelope) => envelope.type), ["vectors.item", "vectors.item"], vector);
    const inspection = inspectCultCacheBytes("own.msgpack", await readFile(file));
    assert.deepEqual(inspection.records.map((record) => record.schemaName), ["vectors.item", "vectors.item"], vector);
  }
});

// The inspector reads what the reader reads: a record under an id its catalog lists only as compatible is inspected.
test("CultCache inspector reads a record whose schema the catalog publishes only as a compatible id", async () => {
  const bytes = await readFile(join(c2aVectors, "readability", "compatible-id-only-v3.bin"));
  const inspection = inspectCultCacheBytes("compatible.msgpack", bytes);
  assert.deepEqual(inspection.records.map((record) => record.schemaName), ["vectors.item", "vectors.item"]);
});

test("a record loaded under a compatible schema id is written back under the id its catalog entry carries", async () => {
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-foreign-id-")), "store.msgpack");
  const payload = encode({ name: "old" });
  await writeFile(
    file,
    encode([
      "cultcache.store.v1",
      [[
        "tests.foreign-id.current",
        "tests.foreign-id",
        "tests.foreign_id.v1",
        "tests.foreign-id.current",
        "",
        ["tests.foreign-id.current", "tests.foreign-id.older"],
        [],
      ]],
      [["old", "tests.foreign-id.older", "2026-09-30T00:00:00.0000000Z", payload]],
    ]),
  );

  const cache = await reopenForeignIdStore(file);
  assert.equal(cache.getRequiredEnvelope(foreignIdDocument, "old").schemaId, "tests.foreign-id.current");
  await cache.put(foreignIdDocument, "next", { name: "next" });
  const reopened = await reopenForeignIdStore(file);
  assert.deepEqual(reopened.getRequired(foreignIdDocument, "old"), { name: "old" });
  assert.deepEqual(reopened.getRequired(foreignIdDocument, "next"), { name: "next" });
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

// Shared refusal vectors: tests/vectors/document-variants-c0, read by every runtime's tests.
const variantVectors = resolve(cultLibRoot, "tests", "vectors", "document-variants-c0");
const itemSchemaId = "sha256:88d3fdf0a927acf3b163940d8f8c7fe62b3316542ce771a67ec8bc038f594788";

async function pullVector(name: string): Promise<CultCacheEnvelope[]> {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-vector-"));
  const file = join(dir, "store.msgpack");
  await writeFile(file, await readFile(join(variantVectors, name)));
  return new SingleFileMessagePackBackingStore(file).pullAll();
}

test("SingleFileMessagePackBackingStore refuses an unknown store header by name", async () => {
  await assert.rejects(() => pullVector("unknown-header.msgpack"), /cultcache\.store\.v9/u);
});

test("SingleFileMessagePackBackingStore refuses an extra record slot naming the record", async () => {
  await assert.rejects(
    () => pullVector("extra-slot-full-payload.msgpack"),
    (error: Error) => error.message.includes("item:anvil") && error.message.includes(itemSchemaId),
  );
});

test("SingleFileMessagePackBackingStore refuses a variant store by version or record", async () => {
  await assert.rejects(
    () => pullVector("variant-v2.msgpack"),
    (error: Error) => error.message.includes("cultcache.store.v2") || error.message.includes("item:anvil-big"),
  );
});

test("SingleFileMessagePackBackingStore refuses the C#-written variant store by version or record", async () => {
  await assert.rejects(
    () => pullVector("../document-variants-c1/variant-store.msgpack"),
    (error: Error) => error.message.includes("cultcache.store.v2") || error.message.includes("laser-big"),
  );
});

test("SingleFileMessagePackBackingStore refusals say what was found without blaming variants", async () => {
  await assert.rejects(
    () => pullVector("unknown-header.msgpack"),
    (error: Error) => error.message.includes("not one this runtime reads") && !error.message.includes("variant"),
  );
  await assert.rejects(
    () => pullVector("extra-slot-full-payload.msgpack"),
    (error: Error) => error.message.includes("not a valid store") && !error.message.includes("variant"),
  );
});

test("SingleFileMessagePackBackingStore refusal says what was found and names every format it reads", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-refusal-"));
  const file = join(dir, "store.cc");
  const reads = 'it reads "cultcache.store.v1" and "cultcache.store.v3" only.';
  for (const [header, found] of [
    ["cultcache.store.v12", '"cultcache.store.v12"'],
    ["cultcache.store.SECRET", "an unrecognised cultcache.store.* header of 22 bytes"],
    ["cultcache.store.v", "an unrecognised cultcache.store.* header of 17 bytes"],
    // Digits are ASCII only: two Arabic-Indic digits are four bytes and are not the known shape.
    ["cultcache.store.v١٢", "an unrecognised cultcache.store.* header of 21 bytes"],
  ] as const) {
    await writeFile(file, encode([header, [], []]));
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pullAll(),
      (error: Error) => error.message.includes(`CultCache store format ${found} is not one this runtime reads; ${reads}`),
      header,
    );
  }
});

test("SingleFileMessagePackBackingStore reads only a missing store as empty", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-absent-"));
  assert.deepEqual(await new SingleFileMessagePackBackingStore(join(dir, "store.cc")).pullAll(), []);

  // An existing empty file is not a store: no CultCache writer leaves one.
  const empty = join(dir, "empty.cc");
  await writeFile(empty, new Uint8Array());
  await assert.rejects(() => new SingleFileMessagePackBackingStore(empty).pullAll());
  assert.equal((await readFile(empty)).length, 0);

  // An empty array is the legacy envelope array with no envelopes: no header, no format claimed.
  const legacyEmpty = join(dir, "legacy-empty.cc");
  await writeFile(legacyEmpty, Uint8Array.of(0x90));
  assert.deepEqual(await new SingleFileMessagePackBackingStore(legacyEmpty).pullAll(), []);
});

test("reading a store that is not there writes nothing, and a read of a store that is readable does not rewrite it", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-read-writes-nothing-"));
  const missing = join(dir, "store.cc");
  assert.deepEqual(await new SingleFileMessagePackBackingStore(missing).pullAll(), []);
  assert.equal(existsSync(missing), false);
  assert.deepEqual(await readdir(dir), []);

  const written = join(dir, "written.cc");
  const envelope = { key: "k", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-09-30T00:00:00Z" };
  await new SingleFileMessagePackBackingStore(written).push(envelope);
  const before = await readFile(written);
  await new SingleFileMessagePackBackingStore(written).pullAll();
  assert.deepEqual(await readFile(written), before);
});

test("a soft pushAll writes where nothing is, leaves a readable store alone, and refuses what the reader refuses", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-soft-"));
  const envelope = { key: "k", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-09-30T00:00:00Z" };
  const other = { key: "other", type: "t", payload: Uint8Array.of(0x91, 0x01), storedAt: "2026-09-30T00:00:00Z" };

  // Absent: the soft write seeds the store.
  const absent = join(dir, "absent.cc");
  await new SingleFileMessagePackBackingStore(absent).pushAll([envelope], { soft: true });
  assert.deepEqual((await new SingleFileMessagePackBackingStore(absent).pullAll()).map((entry) => entry.key), ["k"]);

  // Readable: the soft write is skipped, byte for byte.
  const seeded = await readFile(absent);
  await new SingleFileMessagePackBackingStore(absent).pushAll([other], { soft: true });
  assert.deepEqual(await readFile(absent), seeded);

  // Unreadable: refused, as pullAll refuses it, and the file is left exactly as it was.
  const refused: Array<[string, Uint8Array]> = [
    ["zero-byte", new Uint8Array()],
    ["unknown-format", encode(["cultcache.store.v9", [], []])],
    ["bin-header", Uint8Array.of(0x93, 0xc4, 0x01, 0x76, 0x90, 0x90)],
    ["truncated", seeded.subarray(0, seeded.length - 1)],
    ["garbage", Uint8Array.of(0xc1)],
  ];
  for (const [name, bytes] of refused) {
    const file = join(dir, `${name}.cc`);
    await writeFile(file, bytes);
    await assert.rejects(() => new SingleFileMessagePackBackingStore(file).pullAll(), StoreUnreadableError, name);
    await assert.rejects(() => new SingleFileMessagePackBackingStore(file).pushAll([other], { soft: true }), StoreUnreadableError, name);
    assert.deepEqual(await readFile(file), Buffer.from(bytes), name);
  }
});

test("a header that does not start at the store prefix is not echoed whole, though it ends like a version", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-forged-header-"));
  const file = join(dir, "store.cc");
  for (const header of ["cultcache.store.SECRETcultcache.store.v9", "cultcache.store.\ncultcache.store.v9", "SECRETcultcache.store.v9"]) {
    await writeFile(file, encode([header, [], []]));
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pullAll(),
      (error: Error) => error instanceof StoreUnreadableError && !error.message.includes("SECRET") && !error.message.includes(header),
      header,
    );
  }
});

test("SingleFileMessagePackBackingStore reports a store it cannot reach as an I/O error", { skip: process.platform === "win32" }, async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-unreachable-"));
  const loop = join(dir, "loop.cc");
  await symlink(loop, loop);
  const parent = join(dir, "body");
  await writeFile(parent, "not a directory");
  for (const [path, code] of [[loop, "ELOOP"], [join(parent, "store.cc"), "ENOTDIR"]] as const) {
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(path).pullAll(),
      (error: NodeJS.ErrnoException) => error.code === code,
    );
  }
});

test("SingleFileMessagePackBackingStore refusals never echo a value from the store", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-canary-"));
  const file = join(dir, "store.cc");
  const cases: Array<[unknown[], boolean | undefined]> = [
    [["cultcache.store.v12", [], []], true],
    [["cultcache.store.SECRET-HEADER", [], []], false],
    [["cultcache.store.v12SECRET", [], []], false],
    [["cultcache.store.v", [], []], false],
    [["cultcache.store.v1", ["SECRET-CATALOG-TEXT"], []], undefined],
  ];
  for (const [value, echoed] of cases) {
    await writeFile(file, encode(value));
    const header = value[0] as string;
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pullAll(),
      (error: Error) => {
        if (echoed) {
          return error.message.includes(`"${header}"`);
        }
        return !error.message.includes("SECRET")
          && (echoed === undefined || (!error.message.includes(`"${header}"`) && error.message.includes(`of ${header.length} bytes`)));
      },
    );
  }
});

test("SingleFileMessagePackBackingStore refuses a dangling symlink at its path and writes nothing", { skip: process.platform === "win32" }, async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-dangling-"));
  const volume = join(dir, "volume");
  await mkdir(volume);
  const file = join(dir, "store.cc");
  await symlink(join(volume, "store.cc"), file);
  const store = new SingleFileMessagePackBackingStore(file);
  const envelope = { key: "k", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-09-30T00:00:00Z" };
  await assert.rejects(
    () => store.pullAll(),
    (error: NodeJS.ErrnoException) => error.code === "ENOENT" && error.message.includes("symbolic link") && error.message.includes(file),
  );
  await assert.rejects(() => store.push(envelope), (error: NodeJS.ErrnoException) => error.code === "ENOENT");
  await assert.rejects(() => store.pushAll([envelope], { soft: true }), (error: NodeJS.ErrnoException) => error.code === "ENOENT");
  assert.ok((await lstat(file)).isSymbolicLink());
  assert.deepEqual(await readdir(volume), []);
});

test("SingleFileMessagePackBackingStore record refusal names only a string key", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-record-key-"));
  const file = join(dir, "store.cc");
  const secret = new TextEncoder().encode("SECRET-BYTES");
  for (const [key, schema] of [[["SECRET-IN-ARRAY"], "s"], [secret, "s"], ["k", { SECRET: 1 }]] as const) {
    await writeFile(file, encode(["cultcache.store.v1", [], [[key, schema, "t", Uint8Array.of(0x90), "extra"]]]));
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pullAll(),
      (error: Error) => !error.message.includes("SECRET") && !error.message.includes("83,69,67") && error.message.includes("not a valid store"),
    );
  }
});

// What a write does through a symbolic link today. R3 decides whether a write resolves the link
// or refuses it; these pin the current behaviour so that change is deliberate.
test("R3 decides resolve-or-refuse: a push through a live link replaces the link", { skip: process.platform === "win32" }, async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-live-link-"));
  const volume = join(dir, "volume");
  await mkdir(volume);
  const target = join(volume, "store.cc");
  const envelope = { key: "a", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-09-30T00:00:00Z" };
  await new SingleFileMessagePackBackingStore(target).push(envelope);
  const before = await readFile(target);
  const link = join(dir, "store.cc");
  await symlink(target, link);
  await new SingleFileMessagePackBackingStore(link).push({ ...envelope, key: "b" });
  assert.ok((await lstat(link)).isFile());
  assert.deepEqual(await readFile(target), before);
});

test("R3 decides resolve-or-refuse: a pushAll through a dangling link replaces the link", { skip: process.platform === "win32" }, async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-dangling-push-all-"));
  const volume = join(dir, "volume");
  await mkdir(volume);
  const link = join(dir, "store.cc");
  await symlink(join(volume, "store.cc"), link);
  await new SingleFileMessagePackBackingStore(link).pushAll([{ key: "a", type: "t", payload: Uint8Array.of(0x90), storedAt: "2026-09-30T00:00:00Z" }]);
  assert.ok((await lstat(link)).isFile());
  assert.deepEqual(await readdir(volume), []);
});

test("CultCache inspector refuses the same vectors by name", async () => {
  const inspect = async (name: string) => inspectCultCacheBytes(name, await readFile(join(variantVectors, name)));
  await assert.rejects(() => inspect("unknown-header.msgpack"), /cultcache\.store\.v9/u);
  await assert.rejects(
    () => inspect("extra-slot-full-payload.msgpack"),
    (error: Error) => error.message.includes("item:anvil") && error.message.includes(itemSchemaId),
  );
});

test("a v1 store written at the base commit still reads byte for byte", async () => {
  const envelopes = await pullVector("v1-base.msgpack");
  assert.deepEqual(envelopes.map((entry) => [entry.key, entry.type]), [["alpha", "vectors.item"], ["beta", "vectors.item"]]);
  assert.deepEqual([...envelopes[0]!.payload], [0x92, 0xa5, 0x61, 0x6c, 0x70, 0x68, 0x61, 0x01]);
  assert.deepEqual([...envelopes[1]!.payload], [0x92, 0xa4, 0x62, 0x65, 0x74, 0x61, 0x02]);
});

// The element-id marker: tests/vectors/document-variants-c2a/v3-base.msgpack is v1-base with its header replaced.
const c2aVectors = resolve(cultLibRoot, "tests", "vectors", "document-variants-c2a");

async function copyVector(directory: string, source: string, name: string): Promise<string> {
  const file = join(directory, name);
  await writeFile(file, await readFile(source));
  return file;
}

test("SingleFileMessagePackBackingStore pushAll applies the header the file on disk carries", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-v3-pushall-"));
  const header = async (file: string) => (decode(await readFile(file)) as unknown[])[0];

  // A store that never read the file still keeps a marked file marked.
  const v3 = await copyVector(dir, join(c2aVectors, "v3-base.msgpack"), "v3.msgpack");
  const envelopes = await new SingleFileMessagePackBackingStore(v3).pullAll();
  await new SingleFileMessagePackBackingStore(v3).pushAll(envelopes);
  assert.equal(await header(v3), "cultcache.store.v3");

  // A store that read a marked file, whose file is then gone, writes unmarked: the disk decides, not the last read.
  const reader = new SingleFileMessagePackBackingStore(v3);
  await reader.pullAll();
  await rm(v3);
  await reader.pushAll(envelopes);
  assert.equal(await header(v3), "cultcache.store.v1");

  const v1 = await copyVector(dir, join(variantVectors, "v1-base.msgpack"), "v1.msgpack");
  await new SingleFileMessagePackBackingStore(v1).pushAll(envelopes);
  assert.equal(await header(v1), "cultcache.store.v1");
});

test("SingleFileMessagePackBackingStore push after a read of a marked file that is gone writes v1", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-v3-gone-"));
  const v3 = await copyVector(dir, join(c2aVectors, "v3-base.msgpack"), "v3.msgpack");
  const store = new SingleFileMessagePackBackingStore(v3);
  const envelopes = await store.pullAll();
  await rm(v3);
  await store.push(envelopes[0]!);
  assert.equal((decode(await readFile(v3)) as unknown[])[0], "cultcache.store.v1");
});

test("SingleFileMessagePackBackingStore reads a v3 element-id store and a rewrite keeps the marker", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-v3-"));
  const v3 = await copyVector(dir, join(c2aVectors, "v3-base.msgpack"), "v3.msgpack");
  const store = new SingleFileMessagePackBackingStore(v3);
  const envelopes = await store.pullAll();
  assert.deepEqual(envelopes.map((entry) => entry.key), ["alpha", "beta"]);
  assert.deepEqual([...envelopes[0]!.payload], [0x92, 0xa5, 0x61, 0x6c, 0x70, 0x68, 0x61, 0x01]);
  await store.push({ ...envelopes[0]!, storedAt: "2026-09-30T00:00:00.0000000Z" });
  assert.equal((decode(await readFile(v3)) as unknown[])[0], "cultcache.store.v3");

  // A v1 store stays v1: the marker is carried, never invented.
  const v1 = await copyVector(dir, join(variantVectors, "v1-base.msgpack"), "v1.msgpack");
  const plain = new SingleFileMessagePackBackingStore(v1);
  const plainEnvelopes = await plain.pullAll();
  await plain.push({ ...plainEnvelopes[0]!, storedAt: "2026-09-30T00:00:00.0000000Z" });
  assert.equal((decode(await readFile(v1)) as unknown[])[0], "cultcache.store.v1");

  const inspection = inspectCultCacheBytes("v3.msgpack", await readFile(join(c2aVectors, "v3-base.msgpack")));
  assert.equal(inspection.format, "cultcache.store.v3");
});

test("SingleFileMessagePackBackingStore pushAll refuses a store whose header it cannot read and leaves the file untouched", async () => {
  const dir = await mkdtemp(join(tmpdir(), "cultcache-pushall-refused-"));
  const envelopes = await new SingleFileMessagePackBackingStore(
    await copyVector(dir, join(c2aVectors, "v3-base.msgpack"), "seed.msgpack"),
  ).pullAll();
  for (const vector of [
    join(variantVectors, "unknown-header.msgpack"),
    join(variantVectors, "variant-v2.msgpack"),
    resolve(cultLibRoot, "tests", "vectors", "document-variants-c1", "variant-store.msgpack"),
  ]) {
    const file = await copyVector(dir, vector, "store.msgpack");
    const before = await readFile(file);
    await assert.rejects(
      () => new SingleFileMessagePackBackingStore(file).pushAll(envelopes),
      (error) => error instanceof StoreUnreadableError && /is not readable/u.test(error.message),
      vector,
    );
    assert.deepEqual(await readFile(file), before, `${vector} was rewritten`);
  }
});

// A file is replaced by a rewrite exactly when this runtime's own reader opens it: one verdict per file, asked by pullAll,
// pushAll, push and delete alike. The bytes and every runtime's verdict: tests/vectors/document-variants-c2a/readability.
test("SingleFileMessagePackBackingStore replaces a file exactly when it reads", async () => {
  const root = join(c2aVectors, "readability");
  const rows = (await readFile(join(root, "manifest.txt"), "utf8"))
    .split(/\r?\n/u)
    .filter((line) => line.length > 0 && !line.startsWith("#"))
    .map((line) => line.split(/\s+/u));
  // Every vector in the folder has a manifest row.
  const listed = rows.map(([vector]) => vector!).filter((vector) => !vector.startsWith("..")).sort();
  assert.deepEqual((await readdir(root)).filter((name) => name.endsWith(".bin")).sort(), listed);
  const dir = await mkdtemp(join(tmpdir(), "cultcache-readability-"));
  const seed = await copyVector(dir, join(c2aVectors, "v3-base.msgpack"), "seed.msgpack");
  const envelopes = await new SingleFileMessagePackBackingStore(seed).pullAll();
  for (const [vector, , , typescript] of rows) {
    const reads = typescript === "reads";
    const bytes = await readFile(join(root, vector));
    for (const operation of ["pullAll", "pushAll", "push", "delete"] as const) {
      const file = join(dir, `${vector.replace(/[^a-z0-9.-]/giu, "_")}.${operation}.msgpack`);
      await writeFile(file, bytes);
      const store = new SingleFileMessagePackBackingStore(file);
      const run = () =>
        operation === "pullAll"
          ? store.pullAll()
          : operation === "pushAll"
            ? store.pushAll(envelopes)
            : operation === "push"
              ? store.push(envelopes[0]!)
              : store.delete(envelopes[0]!);
      if (reads) {
        await run();
        if (operation === "pullAll") {
          continue;
        }

        // The replaced file is a store: it carries the header its old content decides (a v3 store keeps its marker, all else
        // is v1), and it holds what the operation wrote.
        assert.equal((decode(await readFile(file)) as unknown[])[0], vector.includes("v3") ? "cultcache.store.v3" : "cultcache.store.v1", `${vector} ${operation}`);
        const keys = (await new SingleFileMessagePackBackingStore(file).pullAll()).map((entry) => entry.key);
        if (operation === "pushAll") {
          assert.deepEqual(keys, envelopes.map((entry) => entry.key).sort(), `${vector} ${operation}`);
        } else if (operation === "push") {
          assert.ok(keys.includes(envelopes[0]!.key), `${vector} ${operation}`);
        } else {
          assert.ok(!keys.includes(envelopes[0]!.key), `${vector} ${operation}`);
        }
      } else {
        await assert.rejects(
          run,
          (error) => error instanceof StoreUnreadableError && error.cause !== undefined,
          `${vector} ${operation}`,
        );
        assert.ok(bytes.equals(await readFile(file)), `${vector} ${operation} rewrote a file it cannot read`);
      }
    }
  }
});

// Owner beats lister: a definition that owns an id and one that lists it as compatible register together in either order. A record
// under the id is the owner's, and a lister's only when nothing owns it; two listers and no owner name no single definition.
const ownsId = defineDocumentType({ type: "tests.owns", schema: z.object({ name: z.string() }), schemaId: "id.owned", schemaName: "tests.owns" });
const listsId = defineDocumentType({
  type: "tests.lists",
  schema: z.object({ name: z.string() }),
  schemaId: "id.lister",
  schemaName: "tests.lists",
  compatibleSchemaIds: ["id.owned"],
});
const alsoListsId = defineDocumentType({
  type: "tests.also-lists",
  schema: z.object({ name: z.string() }),
  schemaId: "id.also-lister",
  schemaName: "tests.also-lists",
  compatibleSchemaIds: ["id.owned"],
});

async function storeUnder(records: [string, string][]): Promise<string> {
  const seed = CultCache.builder().withDocumentType(ownsId).build();
  await seed.put(ownsId, "seed", { name: "n" });
  const payload = seed.getRequiredEnvelope(ownsId, "seed").payload;
  const file = join(await mkdtemp(join(tmpdir(), "cultcache-owner-lister-")), "store.msgpack");
  const ids = [...new Set(records.map(([, schemaId]) => schemaId))];
  await writeFile(file, encode(["cultcache.store.v1",
    ids.map((schemaId) => [schemaId, schemaId, `${schemaId}.v1`, schemaId, "", [schemaId], []]),
    records.map(([key, schemaId]) => [key, schemaId, "2026-09-30T00:00:00.0000000Z", payload])]));
  return file;
}

async function openWith(file: string, ...definitions: AnyCultCacheDocumentDefinition[]): Promise<CultCache> {
  let builder = CultCache.builder();
  for (const definition of definitions) builder = builder.withDocumentType(definition);
  const cache = builder.withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
  await cache.pullAllBackingStores();
  return cache;
}

test("a definition listing an id registers beside the one owning it in either order, and the owner wins", async () => {
  const file = await storeUnder([["o", "id.owned"], ["l", "id.lister"]]);
  for (const order of [[ownsId, listsId], [listsId, ownsId]]) {
    const cache = await openWith(file, ...order);
    assert.deepEqual(cache.getRequired(ownsId, "o"), { name: "n" });
    assert.equal(cache.get(listsId, "o"), undefined);
    assert.deepEqual(cache.getRequired(listsId, "l"), { name: "n" });
  }
  const listerOnly = await openWith(await storeUnder([["o", "id.owned"]]), listsId);
  assert.deepEqual(listerOnly.getRequired(listsId, "o"), { name: "n" });
});

test("two definitions listing one id register, and a record under it needs its owner", async () => {
  const file = await storeUnder([["k", "id.owned"]]);
  for (const listers of [[listsId, alsoListsId], [alsoListsId, listsId]]) {
    await assert.rejects(openWith(file, ...listers), (error: unknown) => {
      assert.ok(error instanceof SchemaConflictError);
      assert.equal(error.schemaId, "id.owned");
      assert.equal(error.recordKey, "k");
      assert.deepEqual(error.schemaNames, ["tests.also-lists", "tests.lists"]);
      return true;
    });
    for (const order of [[...listers, ownsId], [ownsId, ...listers]]) {
      assert.deepEqual((await openWith(file, ...order)).getRequired(ownsId, "k"), { name: "n" });
    }
  }
});

test("every registration refusal is a SchemaConflictError naming the id and both schema names", () => {
  const sameId = defineDocumentType({ type: "tests.owns-too", schema: z.object({ name: z.string() }), schemaId: "id.owned", schemaName: "tests.owns-too" });
  const sameName = defineDocumentType({ type: "tests.named-too", schema: z.object({ name: z.string() }), schemaId: "id.other", schemaName: "tests.owns" });
  const sameType = defineDocumentType({ type: "tests.owns", schema: z.object({ name: z.string() }), schemaId: "id.third", schemaName: "tests.third" });
  for (const [claimant, schemaId, names] of [
    [sameId, "id.owned", ["tests.owns", "tests.owns-too"]],
    [sameName, "id.other", ["tests.owns", "tests.owns"]],
    [sameType, "id.third", ["tests.owns", "tests.third"]],
  ] as const) {
    for (const order of [[ownsId, claimant], [claimant, ownsId]]) {
      assert.throws(() => CultCache.builder().withDocumentType(order[0]).withDocumentType(order[1]).build(), (error: unknown) => {
        assert.ok(error instanceof SchemaConflictError, String(error));
        const first = order[0] === ownsId;
        assert.equal(error.schemaId, first ? schemaId : ownsId.schemaId);
        assert.deepEqual(error.schemaNames, first ? names : [...names].reverse());
        return true;
      });
    }
  }
});
