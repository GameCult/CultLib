import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { codecs } from "../src";
import type { Codec } from "../src/codec";
import { CultCache } from "../src/cult-cache";
import { readWire, WireFloat, writeWire } from "../src/wire";
import { defineDocumentType } from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import type { CacheBackingStore, CultCacheEnvelope } from "../src/types";

const { array, boolean, bytes, float32, float64, int32, int64, lazy, list, nullable, slots, string, union, unit } = codecs;

// The names the C# reference gives the fixture's types (CultSchemaTypeNames.FromType), as union-surface.cc carries them.
const CS = "GameCult.Caching.Tests.UnionParityVectorTests+";

// The golden is tests/vectors/union-parity, written by the C# reference. Everything below that is bytes or an expected
// outcome is read from those committed files; only the value the payload must decode to is spelled here.
function findVectors(from: string): string {
  for (let dir = from; ; dir = resolve(dir, "..")) {
    if (existsSync(join(dir, "tests", "vectors", "union-parity"))) return join(dir, "tests", "vectors", "union-parity");
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/union-parity not found above the test.");
  }
}
const vectors = findVectors(__dirname);
const read = async (name: string) => new Uint8Array(await readFile(join(vectors, name)));

// The string the C# writer put inside every refusal payload, to prove a refusal never echoes the payload.
const CANARY = "CANARY-union-golden-4c2a";

type Node =
  | { kind: "Box"; id: string; children: (Node | null)[] }
  | { kind: "Text"; id: string; content: string }
  | { kind: "Use"; id: string; composition: Composition; slots: (Node | null)[] }
  | { kind: "Image"; id: string; uri: string; alt: string };
interface Composition { name: string; body: Node | null }
type Outcome = { kind: "Pending"; message: string } | { kind: "Completed"; sourceVersion: number } | { kind: "Denied" };
type Item = { kind: "Gem"; name: string } | { kind: "Tool"; name: string; uses: number } | { kind: "Part"; name: string; count: number };
type CraftedItem = { kind: "Tool"; name: string; uses: number } | { kind: "Part"; name: string; count: number };
interface Surface {
  id: string;
  version: number;
  root: Node | null;
  outcomes: (Outcome | null)[];
  part: Item | null;
  crafted: CraftedItem | null;
  missing: Node | null;
}

const composition = slots<Composition>({ 0: ["name", string], 1: ["body", lazy(() => node)] }, `${CS}Composition`);
const node: Codec<Node | null> = union<Node>("Node", {
  0: ["Box", slots<{ id: string; children: (Node | null)[] }>({ 0: ["id", string], 1: ["children", list(lazy(() => node))] })],
  1: ["Text", slots<{ id: string; content: string }>({ 0: ["id", string], 1: ["content", string] })],
  2: ["Use", slots<{ id: string; composition: Composition; slots: (Node | null)[] }>({
    0: ["id", string], 1: ["composition", composition], 2: ["slots", list(lazy(() => node))],
  })],
  5: ["Image", slots<{ id: string; uri: string; alt: string }>({ 0: ["id", string], 1: ["uri", string], 3: ["alt", string] })],
}, `${CS}Node`);
const outcome = union<Outcome>("Outcome", {
  0: ["Pending", slots<{ message: string }>({ 0: ["message", string] })],
  1: ["Completed", slots<{ sourceVersion: number }>({ 0: ["sourceVersion", int32] })],
  2: ["Denied", unit],
}, `${CS}Outcome`);
const toolArm = slots<{ name: string; uses: number }>({ 0: ["name", string], 1: ["uses", int32] });
const partArm = slots<{ name: string; count: number }>({ 0: ["name", string], 1: ["count", int32] });
const item = union<Item>("Item", {
  0: ["Gem", slots<{ name: string }>({ 0: ["name", string] })],
  1: ["Tool", toolArm],
  2: ["Part", partArm],
}, `${CS}Item`);
const craftedItem = union<CraftedItem>("CraftedItem", { 0: ["Tool", toolArm], 1: ["Part", partArm] }, `${CS}CraftedItem`);
const surfaceCodec = slots<Surface>({
  0: ["id", string],
  1: ["version", int32],
  2: ["root", node],
  3: ["outcomes", list(outcome)],
  4: ["part", item],
  5: ["crafted", craftedItem],
  6: ["missing", node, null],
}, `${CS}UnionSurface`);

const nodeSchema: z.ZodType<Node | null> = z.lazy(() =>
  z.union([
    z.null(),
    z.discriminatedUnion("kind", [
      z.object({ kind: z.literal("Box"), id: z.string(), children: z.array(nodeSchema) }),
      z.object({ kind: z.literal("Text"), id: z.string(), content: z.string() }),
      z.object({
        kind: z.literal("Use"),
        id: z.string(),
        composition: z.object({ name: z.string(), body: nodeSchema }),
        slots: z.array(nodeSchema),
      }),
      z.object({ kind: z.literal("Image"), id: z.string(), uri: z.string(), alt: z.string() }),
    ]),
  ]));
const surfaceSchema = z.object({
  id: z.string(),
  version: z.number(),
  root: nodeSchema,
  outcomes: z.array(z.union([
    z.null(),
    z.discriminatedUnion("kind", [
      z.object({ kind: z.literal("Pending"), message: z.string() }),
      z.object({ kind: z.literal("Completed"), sourceVersion: z.number() }),
      z.object({ kind: z.literal("Denied") }),
    ]),
  ])),
  part: z.union([z.null(), z.discriminatedUnion("kind", [
    z.object({ kind: z.literal("Gem"), name: z.string() }),
    z.object({ kind: z.literal("Tool"), name: z.string(), uses: z.number() }),
    z.object({ kind: z.literal("Part"), name: z.string(), count: z.number() }),
  ])]),
  crafted: z.union([z.null(), z.discriminatedUnion("kind", [
    z.object({ kind: z.literal("Tool"), name: z.string(), uses: z.number() }),
    z.object({ kind: z.literal("Part"), name: z.string(), count: z.number() }),
  ])]),
  missing: nodeSchema,
}) as unknown as z.ZodType<Surface>;

function surfaceDocument(schemaId?: string) {
  return defineDocumentType({
    type: "tests.union_surface",
    schemaId: schemaId ?? "tests.union_surface.v1",
    schemaName: "tests.union_surface",
    schemaVersion: "tests.union_surface.v1",
    schema: surfaceSchema,
    codec: surfaceCodec,
    name: "id",
  });
}

const golden: Surface = {
  id: "surface",
  version: 3,
  root: {
    kind: "Box",
    id: "root",
    children: [
      { kind: "Text", id: "t1", content: "hello" },
      {
        kind: "Use",
        id: "u1",
        composition: {
          name: "comp",
          body: {
            kind: "Box",
            id: "inner",
            children: [
              { kind: "Text", id: "deep", content: "x" },
              { kind: "Image", id: "img", uri: "uri://a", alt: "alt" },
            ],
          },
        },
        slots: [{ kind: "Text", id: "s1", content: "slot" }, null, { kind: "Box", id: "b2", children: [] }],
      },
      { kind: "Image", id: "i2", uri: "uri://b", alt: "" },
    ],
  },
  outcomes: [{ kind: "Pending", message: "wait" }, { kind: "Completed", sourceVersion: 7 }, { kind: "Denied" }],
  part: { kind: "Tool", name: "hammer", uses: 12 },
  crafted: { kind: "Part", name: "gear", count: 4 },
  missing: null,
};

const hex = (data: Uint8Array) => Buffer.from(data).toString("hex");

async function outcomeEntries() {
  const entries = decode(await read("outcomes.msgpack")) as [string, string, string, number | null, string][];
  return entries.map(([file, outcome, unionName, key, detail]) => ({ file, outcome, unionName, key, detail }));
}

test("the union surface vector decodes to the constructed value and re-encodes byte-identical", async () => {
  const committed = await read("union-surface.msgpack");
  const decoded = surfaceCodec.decode(readWire(committed));
  assert.deepEqual(decoded, golden);
  assert.equal(hex(writeWire(surfaceCodec.encode(decoded))), hex(committed));
  assert.equal(hex(writeWire(surfaceCodec.encode(golden))), hex(committed));
});

test("a unit arm is written [key, []] and its payload must be an array", () => {
  assert.deepEqual(outcome.encode({ kind: "Denied" }), [2, []]);
  assert.throws(() => outcome.decode([2, null]), /Union Outcome/u);
});

test("union-surface.cc reads through SingleFileMessagePackBackingStore and writes the same payload", async () => {
  const dir = await mkdtemp(join(tmpdir(), "union-parity-"));
  const file = join(dir, "union-surface.cc");
  await writeFile(file, await read("union-surface.cc"));
  const [envelope] = await new SingleFileMessagePackBackingStore(file).pullAll();
  assert.equal(hex(envelope.payload), hex(await read("union-surface.msgpack")));

  const document = surfaceDocument(envelope.schemaId);
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
  await cache.pullAllBackingStores();
  assert.deepEqual(cache.get(document, envelope.key), golden);

  const written = join(dir, "written.cc");
  const fresh = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(written)).build();
  await fresh.put(document, envelope.key, golden);
  const [rewritten] = await new SingleFileMessagePackBackingStore(written).pullAll();
  assert.equal(hex(rewritten.payload), hex(envelope.payload));

  // The reference refuses a store whose schema catalog names another type for a slot, so the catalog TS writes must be
  // the one C# wrote: same canonical schema text, same member names, slots and types.
  assert.equal(rewritten.catalogEntry!.canonicalSchemaJson, envelope.catalogEntry!.canonicalSchemaJson);
  const shape = (members: readonly { slot: number; memberName: string; typeName: string; isName?: boolean }[]) =>
    members.map((member) => [member.slot, member.memberName, member.typeName, member.isName === true]);
  assert.deepEqual(shape(rewritten.catalogEntry!.members!), shape(envelope.catalogEntry!.members!));
  assert.equal(rewritten.catalogEntry!.members!.length, 7);
});

test("every outcome vector is listed and holds: refusals name the union and key, never the payload", async () => {
  const entries = await outcomeEntries();
  assert.equal(entries.length, 9);
  const document = surfaceDocument();
  let refused = 0;
  const carrying: string[] = [];
  let readOnly = 0;

  for (const { file, outcome: expected, unionName, key, detail } of entries) {
    const payload = await read(file);
    if (expected === "refused") {
      refused += 1;
      if (Buffer.from(payload).includes(CANARY)) carrying.push(file);
      assert.throws(
        () => surfaceCodec.decode(readWire(payload)),
        (error: Error) => {
          assert.ok(!error.message.includes(CANARY), `${file}: the refusal echoed the payload`);
          if (detail === "arity") {
            return error.message.includes(`Union ${unionName}`) && error.message.includes("expected [key, slots]");
          }
          return error.message.includes(`Union ${unionName}:`) && error.message.includes(`key ${key} `);
        },
        file,
      );
      const refusingCache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore([envelopeOf(payload)])).build();
      await assert.rejects(() => refusingCache.pullAllBackingStores(), new RegExp(`Union ${unionName}`, "u"), file);
    } else {
      readOnly += 1;
      assert.equal(expected, "read-only");
      assert.deepEqual(surfaceCodec.decode(readWire(payload)), golden, `${file}: the extra slot is read past`);
    }
  }
  assert.deepEqual([refused, readOnly], [5, 4]);
  assert.deepEqual(carrying, ["unknown-arm-field.msgpack", "unknown-arm-list.msgpack", "unknown-arm-nested.msgpack"], "the canary is in the payloads whose arm is unknown");
});

test("a refusal lists the declared keys, so a key only the outer union declares is unknown to the inner", async () => {
  const entry = (await outcomeEntries()).find((candidate) => candidate.file === "inner-union-outer-key.msgpack")!;
  const payload = readWire(await read(entry.file)) as unknown[];
  assert.throws(
    () => surfaceCodec.decode(payload),
    /Union CraftedItem: unknown arm key 2 \(declared keys: 0, 1\)\./u,
  );
});

test("an arm that is not [key, slots] is refused naming the union, and a non-integer key is not echoed", () => {
  for (const bad of [[1], [1, null], [1, [], []], "x", 7, {}]) {
    assert.throws(() => node.decode(bad), /^Error: Union Node: expected \[key, slots\]/u);
  }
  assert.throws(
    () => node.decode([CANARY, []]),
    (error: Error) => error.message.includes("Union Node") && error.message.includes("not an integer") && !error.message.includes(CANARY),
  );
});

test("an arm key that is not a declared key is refused, with no fallback", () => {
  assert.throws(() => node.decode([4, []]), /Union Node: unknown arm key 4 \(declared keys: 0, 1, 2, 5\)/u);
  assert.throws(() => outcome.decode([3, []]), /Union Outcome: unknown arm key 3/u);
});

test("a union encodes by kind and refuses a kind it does not declare without echoing it", () => {
  assert.deepEqual(node.encode({ kind: "Text", id: "a", content: "b" }), [1, ["a", "b"]]);
  assert.equal(node.encode(null), null);
  assert.throws(
    () => node.encode({ kind: CANARY, id: "a" } as unknown as Node),
    (error: Error) => error.message.includes("Union Node") && !error.message.includes(CANARY),
  );
});

test("slots: a gap writes nil, a missing slot reads its default, one without a default is refused naming the slot", () => {
  const record = slots<{ a: string; c: string; d: number }>({ 0: ["a", string], 2: ["c", string], 4: ["d", int32, 9] });
  assert.deepEqual(record.encode({ a: "x", c: "y", d: 1 }), ["x", null, "y", null, 1]);
  assert.deepEqual(record.decode(["x", null, "y"]), { a: "x", c: "y", d: 9 });
  assert.deepEqual(record.decode(["x", 5, "y", null, null]), { a: "x", c: "y", d: 9 });
  assert.throws(() => record.decode(["x"]), /c \(slot 2\): the slot is missing and declares no default/u);
  assert.deepEqual(record.decode(["x", null, "y", null, 1, "extra", "slots"]), { a: "x", c: "y", d: 1 });
  assert.throws(() => record.decode({ a: "x" }), /expected an array of slots/u);
});

test("slots: a default may be a function, and nil in a defaulted slot takes it", () => {
  const record = slots<{ tags: string[] }>({ 0: ["tags", list(string), () => ["fresh"]] });
  assert.deepEqual(record.decode([]), { tags: ["fresh"] });
  assert.deepEqual(record.decode([null]), { tags: ["fresh"] });
});

test("a slot refusal names the field and slot and never the value", () => {
  const record = slots<{ name: string }>({ 3: ["name", string] });
  assert.throws(
    () => record.decode([null, null, null, { secret: CANARY }]),
    (error: Error) => error.message.startsWith("name (slot 3):") && !error.message.includes(CANARY),
  );
});

test("leaf codecs refuse the wrong shape on both sides, saying what was expected and what kind was found", () => {
  const table: [string, Codec<unknown>, string, unknown, string][] = [
    ["string", string, "a string", 5, "number"],
    ["int32", int32, "an int32", "5", "string"],
    ["int32", int32, "an int32", Number.POSITIVE_INFINITY, "a non-integer number"],
    ["int32", int32, "an int32", Number.NaN, "a non-integer number"],
    ["int32", int32, "an int32", 1.5, "a non-integer number"],
    ["int32", int32, "an int32", 2 ** 31, "an integer out of range"],
    ["int64", int64, "an int64", 2 ** 53, "a number beyond 2^53, which needs a bigint"],
    ["int64", int64, "an int64", 2n ** 63n, "an integer out of range"],
    ["float64", float64, "a float64", "1.5", "string"],
    ["float32", float32, "a float32", null, "nil"],
    ["boolean", boolean, "a boolean", "true", "string"],
    ["bytes", bytes, "bytes", [1, 2], "array"],
    ["string", string, "a string", null, "nil"],
    ["list", list(string), "an array", "abc", "string"],
  ];
  for (const [label, codec, what, bad, found] of table) {
    for (const side of ["encode", "decode"] as const) {
      // Decode sees the wire's shapes: a JS float or a number beyond 2^53 never arrives from a payload.
      if (side === "decode" && (typeof bad === "number" && (!Number.isInteger(bad) || Math.abs(bad) >= 2 ** 53))) continue;
      assert.throws(() => codec[side](bad), { message: `expected ${what}, found ${found}` }, `${label} ${side}`);
    }
  }
  for (const [codec, good] of [[string, "s"], [int32, 1], [int64, 5], [float64, 1.5], [float32, 1.5], [boolean, false], [bytes, new Uint8Array([7])], [list(int32), [1, 2]]] as [Codec<unknown>, unknown][]) {
    assert.deepEqual(codec.decode(codec.encode(good)), good);
  }
});

test("a list element refusal names the element, and nullable writes nil for null and undefined", () => {
  const numbers = list(int32);
  assert.throws(() => numbers.encode([1, "x" as unknown as number]), /^Error: element 1: expected an int32, found string$/u);
  assert.throws(() => numbers.decode([1, 2, null]), /^Error: element 2: expected an int32, found nil$/u);
  const maybe = nullable(int32);
  assert.equal(maybe.encode(null), null);
  assert.equal(maybe.encode(undefined as unknown as null), null);
  assert.equal(maybe.encode(0), 0);
  assert.equal(maybe.decode(null), null);
  assert.equal(maybe.decode(0), 0);
  assert.throws(() => maybe.decode("x"), /expected an int32/u);
});

test("a record refuses what is not a record and names the field and slot of a bad value", () => {
  const record = slots<{ name: string }>({ 3: ["name", string] });
  assert.throws(() => record.encode(null as unknown as { name: string }), /^Error: expected a record, found nil$/u);
  assert.throws(() => record.encode("x" as unknown as { name: string }), /^Error: expected a record, found string$/u);
  assert.throws(() => record.encode({ name: 5 as unknown as string }), /^Error: name \(slot 3\): expected a string, found number$/u);
});

test("a union names itself, its arm and the field when an arm's slots are refused", () => {
  assert.throws(() => node.encode({ kind: "Text", id: "a", content: 5 as unknown as string }), /^Error: arm Text: content \(slot 1\): expected a string, found number$/u);
  assert.throws(() => node.decode([1, ["a", 5]]), /^Error: arm Text: content \(slot 1\): expected a string, found number$/u);
  assert.equal(node.encode(undefined as unknown as null), null);
  assert.throws(
    () => node.encode({ kind: "Nope" } as unknown as Node),
    /^Error: Union Node: the value's kind is not one of its arms \(Box, Text, Use, Image\)\.$/u,
  );
});

test("slot names become catalog member names in Pascal case", () => {
  const record = slots<{ _hidden: string; two_words: string; Mixed9: string }>({ 0: ["_hidden", string], 1: ["two_words", string], 2: ["Mixed9", string] });
  assert.deepEqual(record.members.map((member) => member.memberName), ["Hidden", "TwoWords", "Mixed9"]);
});

test("definitions are refused at definition time", () => {
  assert.throws(() => union<{ kind: "A" }>("U", { 0: ["A", slots<{ kind: string }>({ 0: ["kind", string] })] }), /Union U arm "A" declares a slot named kind/u);
  assert.throws(() => union<{ kind: "A" | "B" }>("U", { 0: ["A", unit], 1: ["A" as "B", unit] }), /declares the arm "A" twice/u);
  assert.throws(() => union<{ kind: "A" }>("U", { x: ["A", unit] } as never), /^Error: Union U declares the key "x", which is not a non-negative integer\.$/u);
  assert.throws(() => union<{ kind: "A" }>("U", { "01": ["A", unit] } as never), /key "01"/u);
  assert.throws(() => union<{ kind: "A" }>("U", { "1.5": ["A", unit] } as never), /key "1\.5"/u);
  assert.throws(() => union<{ kind: "A" }>("U", {} as never), /^Error: Union U declares no arms\.$/u);
  assert.throws(() => slots<{ a: string; b: string }>({ 0: ["a", string], 1: ["a" as "b", string] }), /declares the field "a" twice/u);
  assert.throws(() => slots<{ a: string }>({}), /declares no slots/u);
  assert.throws(() => slots<{ a: string }>({ [-1]: ["a", string] }), /^Error: A slot record declares the key "-1"/u);
  assert.throws(
    () => defineDocumentType({
      type: "t",
      schema: surfaceSchema,
      codec: surfaceCodec,
      formatter: { encode: () => new Uint8Array(), decode: () => golden },
    }),
    /declares both a codec and a formatter/u,
  );
});

test("a codec supplies the document's catalog members unless it is given its own", () => {
  const document = defineDocumentType({
    type: "t",
    schema: z.object({ a: z.string(), the_b: z.string() }),
    codec: slots<{ a: string; the_b: string }>({ 0: ["a", string], 3: ["the_b", string] }),
    name: "a",
    indexes: { byB: "the_b" },
  });
  assert.deepEqual(document.members, [
    { slot: 0, memberName: "A", typeName: "System.String", isName: true, indexAlias: null },
    { slot: 3, memberName: "TheB", typeName: "System.String", isName: false, indexAlias: "byB" },
  ]);
  const given = [{ slot: 9, memberName: "Mine", typeName: "System.String" }];
  const own = defineDocumentType({ type: "t", schema: z.object({ a: z.string() }), codec: slots<{ a: string }>({ 0: ["a", string] }), members: given });
  assert.deepEqual(own.members, given);
});

test("schema.parse still runs on a decoded value, and a document without a codec still writes a map", async () => {
  const strict = defineDocumentType({
    type: "t",
    schema: z.object({ a: z.string().min(3) }),
    codec: slots<{ a: string }>({ 0: ["a", string] }),
  });
  const store = memoryStore([{ key: "k", type: "t", payload: encode(["xy"]), storedAt: new Date().toISOString() }]);
  await assert.rejects(() => CultCache.builder().withDocumentType(strict).withGenericStore(store).build().pullAllBackingStores());

  const plain = defineDocumentType({ type: "p", schema: z.object({ id: z.string() }) });
  const holder = memoryStore([]);
  await CultCache.builder().withDocumentType(plain).withGenericStore(holder).build().put(plain, "k", { id: "d" });
  assert.deepEqual(decode(holder.entries[0].payload), { id: "d" });
});

test("bytes and nullable round-trip through a slot record", () => {
  const record = slots<{ blob: Uint8Array; maybe: string | null }>({ 0: ["blob", bytes], 1: ["maybe", nullable(string)] });
  const wire = encode(record.encode({ blob: new Uint8Array([1, 2]), maybe: null }));
  assert.deepEqual(record.decode(decode(wire)), { blob: new Uint8Array([1, 2]), maybe: null });
  assert.throws(() => record.decode([new Uint8Array(), 4]), /maybe \(slot 1\): expected a string/u);
});

const fromHex = (text: string) => new Uint8Array(Buffer.from(text, "hex"));

// Arm keys are integers within Int32 in the reference: a float key is refused even when it holds a whole number, and a
// key outside Int32 is refused whether it is declared or read. Every payload below is [key, [7]] or [key, ["wait"]].
test("an arm key is an integer in any MessagePack integer encoding, and a float-typed key is refused", () => {
  const completed = { kind: "Completed", sourceVersion: 7 };
  for (const key of ["01", "cc01", "cd0001", "ce00000001", "cf0000000000000001", "d001", "d10001", "d200000001", "d30000000000000001"]) {
    assert.deepEqual(outcome.decode(readWire(fromHex(`92${key}9107`))), completed, key);
  }
  for (const key of ["ca3f800000", "cb3ff0000000000000"]) {
    assert.throws(() => outcome.decode(readWire(fromHex(`92${key}9107`))), { message: "Union Outcome: the arm key is not an integer (declared keys: 0, 1, 2)." }, key);
  }
  assert.throws(() => outcome.decode(readWire(fromHex("92cb800000000000000091a477616974"))), /^Error: Union Outcome: the arm key is not an integer/u, "-0.0 is a float, not key 0");
  for (const key of ["ce80000000", "cf8000000000000000", "d30000000080000000"]) {
    assert.throws(() => outcome.decode(readWire(fromHex(`92${key}9107`))), { message: "Union Outcome: the arm key is outside Int32 (declared keys: 0, 1, 2)." }, key);
  }
  assert.throws(() => outcome.decode(readWire(fromHex("92d2800000009107"))), { message: "Union Outcome: unknown arm key -2147483648 (declared keys: 0, 1, 2)." });
  assert.throws(() => outcome.decode(readWire(fromHex("92ff9107"))), { message: "Union Outcome: unknown arm key -1 (declared keys: 0, 1, 2)." });
});

test("a union refuses a declared arm key outside Int32", () => {
  assert.doesNotThrow(() => union<{ kind: "A" }>("U", { 2147483647: ["A", unit] }));
  assert.throws(() => union<{ kind: "A" }>("U", { 2147483648: ["A", unit] }), { message: "Union U declares the key 2147483648, which is outside Int32." });
  assert.throws(() => union<{ kind: "A" }>("U", { 3000000000: ["A", unit] }), /outside Int32/u);
});

test("the wire reader keeps 64-bit integers whole, tells a float from an integer, and refuses what is not a value", () => {
  assert.equal(readWire(fromHex("cf0020000000000001")), 2n ** 53n + 1n);
  assert.equal(readWire(fromHex("cf001fffffffffffff")), 2 ** 53 - 1);
  assert.equal(readWire(fromHex("d38000000000000000")), -(2n ** 63n));
  assert.equal(readWire(fromHex("cfffffffffffffffff")), 2n ** 64n - 1n);
  assert.ok(readWire(fromHex("cb3ff0000000000000")) instanceof WireFloat);
  assert.ok(readWire(fromHex("ca3f800000")) instanceof WireFloat);
  assert.equal(readWire(fromHex("01")), 1);
  assert.throws(() => readWire(fromHex("92a161")), /truncated/u, "an array that promises more than the payload holds");
  assert.throws(() => readWire(fromHex("dd")), /truncated/u);
  assert.throws(() => readWire(fromHex("ddffffffff")), /truncated/u, "a huge array header reserves nothing");
  assert.throws(() => readWire(fromHex("a5616263")), /truncated/u);
  assert.throws(() => readWire(fromHex("0102")), /bytes after its value/u);
  assert.throws(() => readWire(fromHex("81a161a162")), /map, extension or reserved/u);
  assert.throws(() => readWire(fromHex("c1")), /map, extension or reserved/u);
  assert.throws(() => readWire(fromHex("d4010a")), /map, extension or reserved/u);
});

test("the wire writer writes the smallest integer, keeps a float's width, and round-trips what it reads", () => {
  const tree = [0, 127, 128, 255, 256, 65535, 65536, 4294967295, 4294967296, -1, -32, -33, -128, -129, -32768, -32769, -2147483648, -2147483649,
    2n ** 53n, 2n ** 64n - 1n, -(2n ** 63n), new WireFloat(1.5, false), new WireFloat(1.5, true), "", "a".repeat(31), "a".repeat(32), "a".repeat(256),
    new Uint8Array(0), new Uint8Array(300), [], null, true, false, "héllo"];
  assert.deepEqual(readWire(writeWire(tree)), tree);
  assert.equal(hex(writeWire(5)), "05");
  assert.equal(hex(writeWire(128)), "cc80");
  assert.equal(hex(writeWire(-33)), "d0df");
  assert.equal(hex(writeWire(2n ** 53n + 2n)), "cf0020000000000002");
  assert.equal(hex(writeWire(new WireFloat(3, true))), "cb4008000000000000");
  assert.equal(hex(writeWire(new WireFloat(1.5, false))), "ca3fc00000");
  assert.equal(hex(writeWire(new Array(16).fill(null))), `dc0010${"c0".repeat(16)}`);
  assert.throws(() => writeWire(1.5), /not a safe integer/u);
  assert.throws(() => writeWire(2 ** 53), /not a safe integer/u);
  assert.throws(() => writeWire(2n ** 64n), /wider than 64 bits/u);
  assert.throws(() => writeWire({}), /this kind of value/u);
});

test("a codec and a formatter are refused together wherever the definition enters", () => {
  const both = { type: "t", schema: surfaceSchema, codec: surfaceCodec, formatter: { encode: () => new Uint8Array(), decode: () => golden } };
  assert.throws(() => defineDocumentType(both), /declares both a codec and a formatter/u);
  assert.throws(() => CultCache.builder().withDocumentType(both), /declares both a codec and a formatter/u);
  assert.throws(() => CultCache.builder().withRegistry([both]), /declares both a codec and a formatter/u);
});

test("a definition that skips defineDocumentType still writes its codec's bytes and its derived catalog", async () => {
  const raw = { type: "tests.union_surface", schemaId: "tests.union_surface.v1", schema: surfaceSchema, codec: surfaceCodec, name: "id" as const };
  const holder = memoryStore([]);
  const cache = CultCache.builder().withDocumentType(raw).withGenericStore(holder).build();
  await cache.put(raw, "surface", golden);
  assert.equal(hex(holder.entries[0].payload), hex(await read("union-surface.msgpack")));
  const [snapshot] = cache.snapshot();
  assert.deepEqual(snapshot.catalogEntry!.members!.map((member) => member.typeName).slice(0, 2), ["System.String", "System.Int32"]);
});

test("every leaf and wrapper carries the reference's type name, and a catalog is not derived from a codec that cannot name a slot", () => {
  assert.deepEqual(
    [string, boolean, bytes, int32, int64, float32, float64].map((codec) => codec.typeName),
    ["System.String", "System.Boolean", "System.Byte[]", "System.Int32", "System.Int64", "System.Single", "System.Double"],
  );
  assert.equal(list(int64).typeName, "System.Collections.Generic.List<System.Int64>");
  assert.equal(array(string).typeName, "System.String[]");
  assert.equal(nullable(int32).typeName, "System.Nullable<System.Int32>");
  assert.equal(nullable(string).typeName, "System.String");
  assert.equal(nullable(list(string)).typeName, "System.Collections.Generic.List<System.String>");
  assert.equal(list(union<{ kind: "A" }>("U", { 0: ["A", unit] })).typeName, undefined);
  const unnamed = slots<{ id: string; what: { kind: "A" } | null }>({ 0: ["id", string], 1: ["what", union<{ kind: "A" }>("U", { 0: ["A", unit] })] });
  assert.throws(() => unnamed.members, /^Error: what \(slot 1\) has no C# type name for the schema catalog/u);
  assert.throws(() => defineDocumentType({ type: "t", schema: z.any(), codec: unnamed as never }), /what \(slot 1\) has no C# type name/u);
  const given = defineDocumentType({ type: "t", schema: z.any(), codec: unnamed as never, members: [{ slot: 0, memberName: "Id", typeName: "System.String" }] });
  assert.equal(given.members!.length, 1);
});

function envelopeOf(payload: Uint8Array): CultCacheEnvelope {
  return { key: "surface", type: "tests.union_surface", payload, storedAt: new Date().toISOString() };
}

function memoryStore(entries: CultCacheEnvelope[]): CacheBackingStore & { entries: CultCacheEnvelope[] } {
  return {
    entries,
    async pullAll() { return entries.map((entry) => ({ ...entry })); },
    async push(entry) { entries.push(entry); },
    async delete() {},
  };
}
