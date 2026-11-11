import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { copyFile, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { decode } from "@msgpack/msgpack";
import { z } from "zod";

import { codecs } from "../src";
import { CultCache } from "../src/cult-cache";
import { defineDocumentType, resolveDocumentMembers } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import type { AnyCultCacheDocumentDefinition } from "../src/types";

// variants:ruling:unique-indexes. `indexes` are unique within one type, `roles` are selection aliases. The vectors in
// tests/vectors/unique-index are written by the C# reference; the rest of the behaviour is pinned here.

function findVectors(from: string): string {
  for (let dir = from; ; dir = resolve(dir, "..")) {
    if (existsSync(join(dir, "tests", "vectors", "unique-index"))) return join(dir, "tests", "vectors", "unique-index");
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/unique-index not found above the test.");
  }
}
const vectors = findVectors(__dirname);

const CANARY = "CANARY-uidx-5e1b";

const skuSchema = z.object({ sku: z.any().optional(), group: z.string().optional() });

function item(type: string, where: "indexes" | "roles" | "none" = "indexes", schemaName = `tests.${type}`) {
  return defineDocumentType({
    type,
    schemaId: `${schemaName}.v1`,
    schemaName,
    schemaVersion: `${schemaName}.v1`,
    schema: skuSchema,
    ...(where === "none" ? {} : { [where]: { sku: "sku" } }),
  });
}

// A definition whose payload is never read back, so a value of any scalar kind (a bigint included) can be held and indexed.
function scalar(type: string) {
  const held: unknown[] = [];
  return defineDocumentType({
    type,
    schemaId: `tests.${type}.v1`,
    schemaName: `tests.${type}`,
    schemaVersion: `tests.${type}.v1`,
    schema: skuSchema,
    formatter: {
      encode: (value: unknown) => new Uint8Array([held.push(value) - 1]),
      decode: (payload: Uint8Array) => held[payload[0]] as never,
    },
    indexes: { sku: "sku" },
  });
}

function memory(...definitions: AnyCultCacheDocumentDefinition[]) {
  let builder = CultCache.builder();
  for (const definition of definitions) builder = builder.withDocumentType(definition);
  return builder.build();
}

function refusal(keyA: string, keyB: string, schemaName: string, alias: string) {
  return `Records ${keyA} and ${keyB} (${schemaName}) both hold unique index '${alias}'; an index value names one record.`;
}

test("a put of a held value at another key is refused naming both keys and the alias, and nothing moves", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uidx-"));
  try {
    const file = join(dir, "items.cc");
    const document = item("item");
    const cache = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
    await cache.pullAllBackingStores();
    await cache.put(document, "zeta", { sku: CANARY });
    const before = await readFile(file);
    const snapshot = cache.snapshot();

    await assert.rejects(cache.put(document, "alpha", { sku: CANARY }), (error: Error) => {
      assert.equal(error.message, refusal("alpha", "zeta", "tests.item", "sku"));
      assert.ok(!error.message.includes(CANARY), "the value is never echoed");
      return true;
    });
    assert.deepEqual(await readFile(file), before, "the store is byte-identical");
    assert.deepEqual(cache.snapshot(), snapshot);
    assert.equal(cache.get(document, "alpha"), undefined);
    assert.equal(cache.getKeyByIndex(document, "sku", CANARY), "zeta");
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("rewriting a record's own key with its own value lands", async () => {
  const document = item("item");
  const cache = memory(document);
  await cache.put(document, "a", { sku: "S", group: "one" });
  await cache.put(document, "a", { sku: "S", group: "two" });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), "a");
  assert.equal(cache.get(document, "a")?.group, "two");
});

test("a removed or changed holder releases its value", async () => {
  const document = item("item");
  const cache = memory(document);
  await cache.put(document, "b", { sku: "S" });
  await assert.rejects(cache.put(document, "a", { sku: "S" }), /Records a and b/u);
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), "b", "the refused write did not take the value");

  await cache.delete(document, "b");
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), undefined);
  await cache.put(document, "a", { sku: "S" });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), "a");

  await cache.put(document, "a", { sku: "T" });
  await cache.put(document, "b", { sku: "S" });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), "b");
  assert.equal(cache.getKeyByIndex(document, "sku", "T"), "a");
});

// The C# White_Space set, and nothing else, is blank. Every UTF-16 code unit is judged, so a rule written for a few
// characters (or for JavaScript's \s, which counts U+FEFF and omits U+0085) cannot pass.
const CSHARP_WHITE_SPACE = (code: number) =>
  (code >= 0x09 && code <= 0x0d) || code === 0x20 || code === 0x85 || code === 0xa0 || code === 0x1680 ||
  (code >= 0x2000 && code <= 0x200a) || code === 0x2028 || code === 0x2029 || code === 0x202f || code === 0x205f || code === 0x3000;

test("blank is exactly the C# White_Space set: every code unit, alone, repeated and padded", async () => {
  const document = item("item");
  const cache = memory(document);
  const disagreements: string[] = [];
  for (let code = 0; code <= 0xffff; code++) {
    if (code >= 0xd800 && code <= 0xdfff) continue; // lone surrogates do not survive MessagePack text
    const character = String.fromCharCode(code);
    await cache.put(document, "x", { sku: character });
    let blank = true;
    try {
      await cache.put(document, "y", { sku: character });
    } catch {
      blank = false;
    }
    if (blank !== CSHARP_WHITE_SPACE(code)) disagreements.push(code.toString(16));
    await cache.delete(document, "x");
    await cache.delete(document, "y");
  }
  assert.deepEqual(disagreements, []);

  // Not trimmed: a padded value is the value, distinct from its trimmed form.
  await cache.put(document, "x", { sku: " a " });
  await cache.put(document, "y", { sku: "a" });
  assert.equal(cache.getKeyByIndex(document, "sku", " a "), "x");
  assert.equal(cache.getKeyByIndex(document, "sku", "a"), "y");
  await assert.rejects(cache.put(document, "z", { sku: " a " }), /Records x and z/u);
});

test("blank values are never indexed and never conflict: empty, spaces, absent", async () => {
  const document = item("item");
  const cache = memory(document);
  for (const [key, sku] of [["a", ""], ["b", " "], ["c", "\u0085"], ["d", undefined], ["e", "　"]] as const) {
    await cache.put(document, key, { sku });
  }
  assert.equal(cache.getKeyByIndex(document, "sku", ""), undefined);
  assert.equal(cache.getKeyByIndex(document, "sku", " "), undefined);
  // U+FEFF and U+001F are values.
  await cache.put(document, "f", { sku: "﻿" });
  await assert.rejects(cache.put(document, "g", { sku: "﻿" }), /Records f and g/u);
  await cache.put(document, "h", { sku: "\u001F" });
  await assert.rejects(cache.put(document, "i", { sku: "\u001F" }), /Records h and i/u);
});

test("two types may share an alias and a value", async () => {
  const first = item("first");
  const second = item("second");
  const cache = memory(first, second);
  await cache.put(first, "a", { sku: "S" });
  await cache.put(second, "b", { sku: "S" });
  assert.equal(cache.getKeyByIndex(first, "sku", "S"), "a");
  assert.equal(cache.getKeyByIndex(second, "sku", "S"), "b");
});

test("roles may share values and are not looked up", async () => {
  const document = item("item", "roles");
  const cache = memory(document);
  await cache.put(document, "a", { sku: "S" });
  await cache.put(document, "b", { sku: "S" });
  await cache.put(document, "c", { sku: "S" });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), undefined);
  assert.equal(cache.getByIndex(document, "sku", "S"), undefined);
});

test("an alias declared in indexes and roles is refused at definition; each map is checked like indexes", () => {
  const base = { type: "bad", schema: skuSchema, schemaId: "tests.bad.v1", schemaName: "tests.bad", schemaVersion: "tests.bad.v1" };
  assert.throws(
    () => defineDocumentType({ ...base, indexes: { sku: "sku" }, roles: { sku: "sku" } }),
    { message: 'CultCache document type "bad" declares "sku" as both an index and a role; an index already names its role.' },
  );
  assert.throws(() => defineDocumentType({ ...base, roles: { " ": "sku" } }), /declares a role with an empty name/u);
  assert.throws(
    () => defineDocumentType({ ...base, roles: [{ name: "r", accessor: "sku" }, { name: "r", accessor: "group" }] }),
    /declares duplicate role "r"/u,
  );
  assert.doesNotThrow(() => defineDocumentType({ ...base, indexes: { sku: "sku" }, roles: { group: "group" } }));
});

test("a codec definition's catalog entry is identical with an alias under indexes or roles", async () => {
  const codec = codecs.slots<{ sku: string; group: string }>({ 0: ["sku", codecs.string], 1: ["group", codecs.string] }, "T.Item");
  const make = (where: "indexes" | "roles") =>
    defineDocumentType({
      type: "codec_item",
      schemaId: "tests.codec_item.v1",
      schemaName: "tests.codec_item",
      schemaVersion: "tests.codec_item.v1",
      schema: z.object({ sku: z.string(), group: z.string() }),
      codec,
      [where]: { sku: "sku" },
    });
  const catalog = async (definition: AnyCultCacheDocumentDefinition) => {
    const cache = memory(definition);
    await cache.put(definition, "a", { sku: "S", group: "g" } as never);
    return cache.snapshot()[0].catalogEntry!;
  };
  const members = (definition: AnyCultCacheDocumentDefinition) => resolveDocumentMembers(definition);
  assert.deepEqual(members(make("indexes")), members(make("roles")));
  assert.equal(members(make("roles")).find((member) => member.memberName === "Sku")?.indexAlias, "sku");
  const [unique, role] = [await catalog(make("indexes")), await catalog(make("roles"))];
  assert.equal(unique.canonicalSchemaJson, role.canonicalSchemaJson);
  assert.equal(unique.contentHash, role.contentHash);
  assert.equal(unique.schemaId, role.schemaId);
});

test("vectors values.msgpack: every row's canonical text, blank rows no value", async () => {
  const rows = decode(await readFile(join(vectors, "values.msgpack")), { useBigInt64: true }) as [string, string, unknown, string | null][];
  assert.ok(rows.length >= 20);
  for (const [label, kind, value, text] of rows) {
    const document = scalar(`value_${label.replaceAll(/[^a-z0-9]/gu, "_")}`);
    const cache = memory(document);
    void kind;
    const normalized = value;
    await cache.put(document, "one", { sku: normalized });
    if (text === null) {
      await cache.put(document, "two", { sku: normalized });
      assert.equal(cache.get(document, "two") !== undefined, true, `${label}: a blank value commits twice`);
      for (const probe of ["", " ", "NaN", "0", "Infinity", "-Infinity", "null", "undefined"]) {
        assert.equal(cache.getKeyByIndex(document, "sku", probe), undefined, `${label}: found by '${probe}'`);
      }
    } else {
      assert.equal(cache.getKeyByIndex(document, "sku", text), "one", `${label}: found by its text`);
      await assert.rejects(cache.put(document, "two", { sku: normalized }), /Records one and two/u, label);
    }
  }
});

// An independent oracle for the double rule: the text must denote exactly the double, so N / 10^d == mantissa * 2^exponent
// in exact integer arithmetic, with no trailing zero and no bare point. A rule written to a few spellings cannot pass.
function exactlyDenotes(text: string, value: number): boolean {
  const match = /^(-?)(\d+)(?:\.(\d*[1-9]))?$/u.exec(text);
  if (!match) return false;
  const decimals = match[3]?.length ?? 0;
  const numerator = BigInt(match[2] + (match[3] ?? ""));
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, Math.abs(value));
  const bits = view.getBigUint64(0);
  const biased = Number((bits >> 52n) & 0x7ffn);
  const mantissa = biased === 0 ? bits & 0xfffffffffffffn : (bits & 0xfffffffffffffn) | (1n << 52n);
  const exponent = (biased === 0 ? 1 : biased) - 1075;
  const lhs = numerator * (exponent >= 0 ? 1n : 2n ** BigInt(-exponent));
  const rhs = mantissa * (exponent >= 0 ? 2n ** BigInt(exponent) : 1n) * 10n ** BigInt(decimals);
  return (match[1] === "-") === (value < 0) && lhs === rhs;
}

test("a number's index text is its exact value: integers as digits, doubles as the exact expansion, NaN and infinities none", async () => {
  const document = scalar("number");
  const cache = memory(document);
  const probes = [0.1, -0.1, 2.5e-7, 1.5, -123.456, 5e-324, 2.2250738585072014e-308, 1e-10, 123456789.123456789, Number.MIN_VALUE * 3,
    ...Array.from({ length: 200 }, (_, i) => Math.sin(i + 1) * 10 ** ((i % 40) - 20)), 1e21 + 2 ** 20 + 0.5 * 0];
  // Round trip through lookups: the exact expansion of each probe is found, and it denotes the probe exactly.
  for (const value of probes) {
    if (Number.isInteger(value)) continue;
    await cache.put(document, "k", { sku: value });
    const candidates = candidateTexts(value);
    const found = candidates.filter((candidate) => cache.getKeyByIndex(document, "sku", candidate) === "k");
    assert.equal(found.length, 1, `${value}: exactly one spelling is its index text`);
    assert.ok(exactlyDenotes(found[0], value), `${value}: ${found[0]} denotes it exactly`);
  }

  await cache.put(document, "k", { sku: 1e21 });
  assert.equal(cache.getKeyByIndex(document, "sku", "1000000000000000000000"), "k");
  await cache.put(document, "k", { sku: -0 });
  assert.equal(cache.getKeyByIndex(document, "sku", "0"), "k");
  await cache.put(document, "k", { sku: 12n });
  assert.equal(cache.getKeyByIndex(document, "sku", "12"), "k");
  await cache.put(document, "k", { sku: true });
  assert.equal(cache.getKeyByIndex(document, "sku", "True"), "k");
  await cache.put(document, "k", { sku: false });
  assert.equal(cache.getKeyByIndex(document, "sku", "False"), "k");
  for (const value of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
    await cache.put(document, "p", { sku: value });
    await cache.put(document, "q", { sku: value });
  }
});

// Spellings a lookup could be probed with: the exact decimal expansion computed by an arbitrary-precision route.
function candidateTexts(value: number): string[] {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, Math.abs(value));
  const bits = view.getBigUint64(0);
  const biased = Number((bits >> 52n) & 0x7ffn);
  const mantissa = biased === 0 ? bits & 0xfffffffffffffn : (bits & 0xfffffffffffffn) | (1n << 52n);
  const k = 1075 - (biased === 0 ? 1 : biased);
  const scaled = (mantissa * 5n ** BigInt(k)).toString().padStart(k + 1, "0");
  const point = scaled.length - k;
  const text = `${scaled.slice(0, point)}.${scaled.slice(point)}`.replace(/0+$/u, "").replace(/\.$/u, "");
  return [(value < 0 ? "-" : "") + text, String(value), value.toString()].filter((spelling, i, all) => all.indexOf(spelling) === i);
}

function twoDefinitions(schemaId: string, where: "indexes" | "roles") {
  return defineDocumentType({
    type: "unique_item",
    schemaId,
    schemaName: "tests.unique_item",
    schemaVersion: "tests.unique_item.v1",
    schema: z.object({ sku: z.string(), category: z.string() }),
    codec: codecs.slots<{ sku: string; category: string }>({ 0: ["sku", codecs.string], 1: ["category", codecs.string] }, "UqVecItem"),
    [where]: { Sku: "sku" },
  });
}

test("vectors duplicate-store.cc and blank-store.cc hold their verdicts", async () => {
  const outcomes = decode(await readFile(join(vectors, "outcomes.msgpack"))) as [string, string | null, string, string | null, string | null][];
  assert.equal(outcomes.length, 3);
  const dir = await mkdtemp(join(tmpdir(), "uidx-vec-"));
  try {
    for (const [name, alias, outcome, key1, key2] of outcomes) {
      const file = join(dir, `${outcomes.indexOf(outcomes.find((row) => row[0] === name && row[1] === alias)!)}-${name}`);
      await copyFile(join(vectors, name), file);
      const before = await readFile(file);
      const [envelope] = await new SingleFileMessagePackBackingStore(file).pullAll();
      const document = twoDefinitions(envelope.schemaId!, alias === null ? "roles" : "indexes");
      const cache = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
      const stateBefore = cache.snapshot();
      if (outcome === "open") {
        await cache.pullAllBackingStores();
        assert.equal(cache.snapshot().length, name === "blank-store.cc" ? 2 : 4);
        if (alias === null) assert.equal(cache.getKeyByIndex(document, "Sku", "S-1"), undefined);
      } else {
        await assert.rejects(cache.pullAllBackingStores(), (error: Error) => {
          assert.equal(error.message, refusal(key1!, key2!, "tests.unique_item", alias!));
          assert.ok(!error.message.includes("S-1"));
          return true;
        });
        assert.deepEqual(cache.snapshot(), stateBefore, "a refused load admits nothing");
      }
      assert.deepEqual(await readFile(file), before, `${name} is untouched`);
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

// refusals.msgpack: rows [label, ops, refusal] written by the C# reference. TypeScript has no batch commit
// (variants:idea:unique-index-runtimes.ts-batch-commit), so it performs every row whose commits each hold one record:
// 5, 8-13, 19-22, 24, 25, 27, 29, 31, 32, 33, 35 and 37 of the README (20).
const REFUSAL_SCHEMAS: [string, string, string, Record<string, "sku" | "second">][] = [
  ["unique_item", "tests.unique_item", "sha256:93bb890de83069061af696b23f94fd32fb3c2dad3c91ee645e8add56085f1882", { Sku: "sku" }],
  ["uq_pair", "tests.uq_pair", "sha256:2538cceafb6dcbce22ef11b932ecb804af5f96bc3aaedff692a52d3640433e60", { Sku: "sku", Alt: "second" }],
  ["uq_alias", "tests.uq_alias", "sha256:77a77a394ebd8bf71e14b9b6f2cea4978c25d4e705fd12364f660edbf6c63b1f", { Sku: "sku", alt: "second" }],
  ["uq_wide", "tests.uq_wide", "sha256:b79eecd11eed5e456902f095d2748da7f59125c4c8972b0da7dddc81d07fc03f", { "\uFF21": "sku", "\u{10400}": "second" }],
  ["uq_alpha", "tests.uq_alpha", "sha256:53aeaf2ea9b25e85ade6b15fc04c7653e7b206c87e258f1aa74dd32a840a1389", { Sku: "sku" }],
  ["uq_beta", "tests.uq_beta", "sha256:45ed62a64c91f6d815d3ec975c4a4a1b2ce07b6e7262a32d828673cf10899586", { Sku: "sku" }],
];

type RefusalOp = ["open", string | null] | ["pull", string] | ["commit", [string, string, string, string][]];

test("vectors refusals.msgpack: every row TS can perform", async () => {
  const rows = decode(await readFile(join(vectors, "refusals.msgpack"))) as [string, RefusalOp[], [string, string, string, string] | null][];
  const definitions = new Map(
    REFUSAL_SCHEMAS.map(([type, schemaName, schemaId, indexes]) => [
      schemaName,
      defineDocumentType({
        type,
        schemaId,
        schemaName,
        schemaVersion: `${schemaName}.v1`,
        schema: z.object({ sku: z.string(), second: z.string() }),
        codec: codecs.slots<{ sku: string; second: string }>({ 0: ["sku", codecs.string], 1: ["second", codecs.string] }, `Uq${type}`),
        indexes,
      }),
    ]),
  );
  const dir = await mkdtemp(join(tmpdir(), "uidx-refusals-"));
  let walked = 0;
  try {
    for (const [label, ops, refused] of rows) {
      if (ops.some((op) => op[0] === "commit" && op[1].length > 1)) continue;
      const file = join(dir, `${walked}.cc`);
      let cache: CultCache | undefined;
      const perform = async (op: RefusalOp) => {
        if (op[0] === "open") {
          if (op[1] !== null) await copyFile(join(vectors, op[1]), file);
          let builder = CultCache.builder().withGenericStore(new SingleFileMessagePackBackingStore(file));
          for (const definition of definitions.values()) builder = builder.withDocumentType(definition);
          cache = builder.build();
          await cache.pullAllBackingStores();
        } else if (op[0] === "pull") {
          await copyFile(join(vectors, op[1]), file);
          await cache!.pullAllBackingStores();
        } else {
          for (const [key, schema, sku, second] of op[1]) await cache!.put(definitions.get(schema)!, key, { sku, second });
        }
      };
      for (const op of ops.slice(0, -1)) await perform(op);
      const last = ops[ops.length - 1];
      if (refused === null) {
        await perform(last);
      } else {
        const [k1, k2, schema, alias] = refused;
        const before = last[0] === "commit" || (last[0] === "open" && last[1] === null)
          ? (existsSync(file) ? await readFile(file) : Buffer.alloc(0))
          : await readFile(join(vectors, last[1]!));
        await assert.rejects(perform(last), { message: refusal(k1, k2, schema, alias) }, label);
        assert.deepEqual(existsSync(file) ? await readFile(file) : Buffer.alloc(0), before, `${label}: the file is untouched`);
      }
      walked++;
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
  assert.equal(walked, 20);
});

test("a refused load leaves the cache's prior state as it was, and a later good load still works", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uidx-load-"));
  try {
    const bad = join(dir, "bad.cc");
    await copyFile(join(vectors, "duplicate-store.cc"), bad);
    const [envelope] = await new SingleFileMessagePackBackingStore(bad).pullAll();
    const document = twoDefinitions(envelope.schemaId!, "indexes");
    const cache = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(bad)).build();
    await assert.rejects(cache.pullAllBackingStores(), /Records item:a and item:b/u);
    assert.equal(cache.get(document, "item:a"), undefined);
    assert.equal(cache.getKeyByIndex(document, "Sku", "S-1"), undefined);
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("registerIndex refuses a name the definition declares as a role", async () => {
  const document = item("item", "roles");
  const cache = memory(document);
  await cache.put(document, "a", { sku: "S", group: "g" });
  await cache.put(document, "b", { sku: "S", group: "h" });
  await assert.rejects(cache.registerIndex(document, "sku", "sku"), { message: /declared as a role; a role is never a unique index/u });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), undefined);
  await cache.put(document, "c", { sku: "S", group: "i" });
});

test("registerIndex over held duplicates installs nothing", async () => {
  const document = item("item", "none");
  const cache = memory(document);
  await cache.put(document, "a", { sku: "S", group: "g" });
  await cache.put(document, "b", { sku: "S", group: "h" });
  await assert.rejects(cache.registerIndex(document, "sku", "sku"), { message: refusal("a", "b", "tests.item", "sku") });
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), undefined, "no map was installed");
  await cache.put(document, "c", { sku: "S", group: "i" });
  await cache.registerIndex(document, "group", "group");
  assert.equal(cache.getKeyByIndex(document, "group", "h"), "b");
  await assert.rejects(cache.put(document, "d", { sku: "x", group: "h" }), /Records b and d/u);
});

test("registerIndex over distinct held values installs the lookup", async () => {
  const document = item("item", "none");
  const cache = memory(document);
  await cache.put(document, "a", { sku: "S" });
  await cache.registerIndex(document, "sku", "sku");
  assert.equal(cache.getKeyByIndex(document, "sku", "S"), "a");
});

// ---- a type with several unique indexes: the second alias is judged, in alias order, in both judges ----

const pairSchema = z.object({ a: z.string().optional(), b: z.string().optional() });

function pair(where: { indexes?: Record<string, "a" | "b">; roles?: Record<string, "a" | "b"> }) {
  return defineDocumentType({
    type: "pair",
    schemaId: "tests.pair.v1",
    schemaName: "tests.pair",
    schemaVersion: "tests.pair.v1",
    schema: pairSchema,
    ...where,
  });
}

// Declared b first, so insertion order is the opposite of alias order.
const BOTH_UNIQUE = { indexes: { b: "b", a: "a" } as Record<string, "a" | "b"> };

test("a collision on the second unique alias is refused, with a value on the first alias that does not collide", async () => {
  const document = pair(BOTH_UNIQUE);
  const cache = memory(document);
  await cache.put(document, "k1", { a: "x1", b: "shared" });
  await assert.rejects(cache.put(document, "k2", { a: "x2", b: "shared" }), { message: refusal("k1", "k2", "tests.pair", "b") });
  await assert.rejects(cache.put(document, "k2", { a: "x1", b: "y" }), { message: refusal("k1", "k2", "tests.pair", "a") });
  assert.equal(cache.get(document, "k2"), undefined);
});

test("when two aliases collide the first alias in ordinal order is named, and its keys are in ordinal order", async () => {
  const document = pair(BOTH_UNIQUE);
  const cache = memory(document);
  await cache.put(document, "m", { a: "same-a", b: "same-b" });
  await assert.rejects(cache.put(document, "c", { a: "same-a", b: "same-b" }), { message: refusal("c", "m", "tests.pair", "a") });
  // The colliding holders differ per alias: a is held by m, b by n; alias a is still the one named.
  await cache.put(document, "n", { a: "other-a", b: "other-b" });
  await assert.rejects(cache.put(document, "z", { a: "same-a", b: "other-b" }), { message: refusal("m", "z", "tests.pair", "a") });
});

test("a load judges every unique alias: a store collision on the second alias refuses the open", async () => {
  const dir = await mkdtemp(join(tmpdir(), "uidx-pair-"));
  try {
    for (const [name, records, expected] of [
      ["second-only", [["k1", { a: "1", b: "s" }], ["k2", { a: "2", b: "s" }]], refusal("k1", "k2", "tests.pair", "b")],
      ["both-aliases", [["m", { a: "s", b: "s" }], ["c", { a: "s", b: "s" }]], refusal("c", "m", "tests.pair", "a")],
    ] as const) {
      const file = join(dir, `${name}.cc`);
      const roles = pair({ roles: { b: "b", a: "a" } });
      const writer = CultCache.builder().withDocumentType(roles).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
      for (const [key, value] of records) await writer.put(roles, key, value);
      const before = await readFile(file);

      const unique = pair(BOTH_UNIQUE);
      const reader = CultCache.builder().withDocumentType(unique).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
      await assert.rejects(reader.pullAllBackingStores(), { message: expected }, name);
      assert.deepEqual(reader.snapshot(), [], "a refused load admits nothing");
      assert.deepEqual(await readFile(file), before);

      const rolesReader = CultCache.builder().withDocumentType(roles).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
      await rolesReader.pullAllBackingStores();
      assert.equal(rolesReader.snapshot().length, 2, `${name}: the same store opens under roles`);
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test("registerIndex of a second unique alias judges it, naming the two smallest keys whatever order they were written in", async () => {
  const document = pair({ indexes: { a: "a" } });
  const cache = memory(document);
  await cache.put(document, "z", { a: "1", b: "s" });
  await cache.put(document, "m", { a: "2", b: "s" });
  await cache.put(document, "q", { a: "3", b: "s" });
  await assert.rejects(cache.registerIndex(document, "b", "b"), { message: refusal("m", "q", "tests.pair", "b") });
  assert.equal(cache.getKeyByIndex(document, "a", "1"), "z", "the alias already installed is intact");
  assert.equal(cache.getKeyByIndex(document, "b", "s"), undefined);
});
