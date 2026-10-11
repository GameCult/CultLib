import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";

import { decode, encode } from "@msgpack/msgpack";
import { z } from "zod";

import { codecs } from "../src";
import { MAX_SLOT_KEY, type Codec } from "../src/codec";
import { CultCache } from "../src/cult-cache";
import { readWire, WireFloat, writeWire } from "../src/wire";
import {
  CultCachePayloadDepthError,
  MAX_PAYLOAD_DEPTH,
  decodeDocumentPayload,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  encodeDocumentPayload,
} from "../src/document";
import { SingleFileMessagePackBackingStore } from "../src/single-file-messagepack-backing-store";
import type { AnyCultCacheDocumentDefinition, CacheBackingStore, CultCacheEnvelope } from "../src/types";

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
  | { kind: "Image"; id: string; uri: string; alt: string }
  | { kind: "Wrap"; inner: Node | null };
interface Composition { name: string; body: Node | null }
type Outcome = { kind: "Pending"; message: string } | { kind: "Completed"; sourceVersion: number } | { kind: "Denied" } | { kind: "Floor" } | { kind: "Ceiling" };
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
  6: ["Wrap", slots<{ inner: Node | null }>({ 0: ["inner", lazy(() => node)] })],
}, `${CS}Node`);
const outcome = union<Outcome>("Outcome", {
  0: ["Pending", slots<{ message: string }>({ 0: ["message", string] })],
  1: ["Completed", slots<{ sourceVersion: number }>({ 0: ["sourceVersion", int32] })],
  2: ["Denied", unit],
  [-2147483648]: ["Floor", unit],
  2147483647: ["Ceiling", unit],
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
      z.object({ kind: z.literal("Wrap"), inner: nodeSchema }),
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
      z.object({ kind: z.literal("Floor") }),
      z.object({ kind: z.literal("Ceiling") }),
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
  assert.equal(hex(documentPayloadBytes(envelope.payload)), hex(await read("union-surface.msgpack")));

  const document = surfaceDocument(envelope.schemaId);
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(file)).build();
  await cache.pullAllBackingStores();
  assert.deepEqual(cache.get(document, envelope.key), golden);

  const written = join(dir, "written.cc");
  const fresh = CultCache.builder().withDocumentType(document).withGenericStore(new SingleFileMessagePackBackingStore(written)).build();
  await fresh.put(document, envelope.key, golden);
  const [rewritten] = await new SingleFileMessagePackBackingStore(written).pullAll();
  assert.equal(hex(documentPayloadBytes(rewritten.payload)), hex(documentPayloadBytes(envelope.payload)));

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
  assert.throws(() => node.decode([4, []]), /Union Node: unknown arm key 4 \(declared keys: 0, 1, 2, 5, 6\)/u);
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
  assert.deepEqual(record.decode(["x", 5, "y", null]), { a: "x", c: "y", d: 9 });
  assert.throws(() => record.decode(["x", 5, "y", null, null]), { message: "d (slot 4): expected an int32, found nil" });
  assert.throws(() => record.decode(["x"]), /c \(slot 2\): the slot is missing and declares no default/u);
  assert.deepEqual(record.decode(["x", null, "y", null, 1, "extra", "slots"]), { a: "x", c: "y", d: 1 });
  assert.throws(() => record.decode({ a: "x" }), /expected an array of slots/u);
});

test("slots: a default may be a function and fills only a missing slot, never a nil", () => {
  const record = slots<{ tags: string[] }>({ 0: ["tags", list(string), () => ["fresh"]] });
  assert.deepEqual(record.decode([]), { tags: ["fresh"] });
  assert.deepEqual(record.decode([null]), { tags: null });
  const counted = slots<{ n: number }>({ 0: ["n", int32, 4] });
  assert.deepEqual(counted.decode([]), { n: 4 });
  assert.throws(() => counted.decode([null]), { message: "n (slot 0): expected an int32, found nil" });
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
    /^Error: Union Node: the value's kind is not one of its arms \(Box, Text, Use, Image, Wrap\)\.$/u,
  );
});

test("slot names become catalog member names in Pascal case", () => {
  const record = slots<{ _hidden: string; two_words: string; Mixed9: string }>({ 0: ["_hidden", string], 1: ["two_words", string], 2: ["Mixed9", string] });
  assert.deepEqual(record.members.map((member) => member.memberName), ["Hidden", "TwoWords", "Mixed9"]);
});

test("definitions are refused at definition time", () => {
  assert.throws(() => union<{ kind: "A" }>("U", { 0: ["A", slots<{ kind: string }>({ 0: ["kind", string] })] }), /Union U arm "A" declares a slot named kind/u);
  assert.throws(() => union<{ kind: "A" | "B" }>("U", { 0: ["A", unit], 1: ["A" as "B", unit] }), /declares the arm "A" twice/u);
  assert.throws(() => union<{ kind: "A" }>("U", { x: ["A", unit] } as never), /^Error: Union U declares the key "x", which is not an integer\.$/u);
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
  const store = memoryStore([{ key: "k", type: "t", payload: documentPayloadFromBytes(encode(["xy"])), storedAt: new Date().toISOString() }]);
  await assert.rejects(() => CultCache.builder().withDocumentType(strict).withGenericStore(store).build().pullAllBackingStores());

  const plain = defineDocumentType({ type: "p", schema: z.object({ id: z.string() }) });
  const holder = memoryStore([]);
  await CultCache.builder().withDocumentType(plain).withGenericStore(holder).build().put(plain, "k", { id: "d" });
  assert.deepEqual(decode(documentPayloadBytes(holder.entries[0].payload)), { id: "d" });
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
    assert.throws(() => outcome.decode(readWire(fromHex(`92${key}9107`))), { message: "Union Outcome: the arm key is not an integer (declared keys: -2147483648, 0, 1, 2, 2147483647)." }, key);
  }
  assert.throws(() => outcome.decode(readWire(fromHex("92cb800000000000000091a477616974"))), /^Error: Union Outcome: the arm key is not an integer/u, "-0.0 is a float, not key 0");
  for (const key of ["ce80000000", "cf8000000000000000", "d30000000080000000"]) {
    assert.throws(() => outcome.decode(readWire(fromHex(`92${key}9107`))), { message: "Union Outcome: the arm key is outside Int32 (declared keys: -2147483648, 0, 1, 2, 2147483647)." }, key);
  }
  assert.throws(() => outcome.decode(readWire(fromHex("92d2800000019107"))), { message: "Union Outcome: unknown arm key -2147483647 (declared keys: -2147483648, 0, 1, 2, 2147483647)." });
  assert.throws(() => outcome.decode(readWire(fromHex("92ff9107"))), { message: "Union Outcome: unknown arm key -1 (declared keys: -2147483648, 0, 1, 2, 2147483647)." });
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
  const raw = { type: "tests.union_surface", schemaId: "tests.union_surface.v1", schema: surfaceSchema, codec: surfaceCodec, name: "id" } as unknown as AnyCultCacheDocumentDefinition;
  const holder = memoryStore([]);
  const cache = CultCache.builder().withDocumentType(raw).withGenericStore(holder).build();
  await cache.put(raw as never, "surface", golden as never);
  assert.equal(hex(documentPayloadBytes(holder.entries[0].payload)), hex(await read("union-surface.msgpack")));
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

// tests/vectors/numeric-leaves is written by the C# reference (NumericLeafVectorTests): what it reads and refuses for each
// numeric leaf, and the bytes it writes.
const numericVectors = join(vectors, "..", "numeric-leaves");
const numericLeaves: Record<string, Codec<unknown>> = { int32, int64, float32, float64 };
const doubleBitsHex = (value: number) => {
  const view = new DataView(new ArrayBuffer(8));
  view.setFloat64(0, value);
  return Buffer.from(view.buffer).toString("hex");
};
const int64BitsHex = (value: number | bigint) => BigInt.asUintN(64, BigInt(value)).toString(16).padStart(16, "0");
const isNaNBits = (bitsHex: string) => {
  const bits = BigInt(`0x${bitsHex}`);
  return ((bits >> 52n) & 0x7ffn) === 0x7ffn && (bits & ((1n << 52n) - 1n)) !== 0n;
};

const isSignallingFloat32 = (payload: Uint8Array) => {
  // payload is a one-slot record [float32]: 91 ca b0 b1 b2 b3
  if (payload.length !== 6 || payload[1] !== 0xca) return false;
  const bits = new DataView(payload.buffer, payload.byteOffset).getUint32(2);
  return (bits & 0x7f800000) === 0x7f800000 && (bits & 0x007fffff) !== 0 && (bits & 0x00400000) === 0;
};

test("numeric leaves read and refuse exactly what the C# reference does", async () => {
  const rows = readWire(new Uint8Array(await readFile(join(numericVectors, "reads.msgpack")))) as [string, Uint8Array, boolean, Uint8Array][];
  assert.equal(rows.length, 160);
  let accepted = 0;
  let refused = 0;
  for (const [leaf, payload, wasAccepted, bits] of rows) {
    const label = `${leaf} ${hex(payload)}`;
    const record = slots<{ v: unknown }>({ 0: ["v", numericLeaves[leaf]] });
    if (wasAccepted && leaf === "float32" && isSignallingFloat32(payload)) {
      // The one reading that differs from the reference: it reads a float32 signalling NaN, this runtime refuses it
      // (float32-nan.msgpack shows the reference keeps its bits, which a JS number cannot).
      assert.throws(() => record.decode(readWire(payload)), (error: Error) => !error.message.includes(hex(payload).slice(2)), label);
      refused += 1;
    } else if (wasAccepted) {
      const read = record.decode(readWire(payload)).v as number | bigint;
      const wanted = hex(bits);
      if (leaf.startsWith("float")) {
        if (isNaNBits(wanted)) assert.ok(Number.isNaN(read), label);
        else assert.equal(doubleBitsHex(read as number), wanted, label);
      } else {
        assert.equal(int64BitsHex(read), wanted, label);
      }
      accepted += 1;
    } else {
      assert.throws(() => record.decode(readWire(payload)), (error: Error) => !error.message.includes(hex(payload).slice(2)), label);
      refused += 1;
    }
  }
  assert.ok(accepted > 40 && refused > 40, `accepted ${accepted}, refused ${refused}`);
  assert.equal(accepted + refused, rows.length);
});

// float32-nan.msgpack: [payload, bytes the reference writes after reading it] for a float32 slot holding an infinity or a NaN.
// The reference keeps every payload bit; a JS number cannot keep a signalling NaN's, so that read is refused.
test("a float32 NaN round-trips with its bits, and a signalling one is refused on read", async () => {
  const rows = readWire(new Uint8Array(await readFile(join(numericVectors, "float32-nan.msgpack")))) as [Uint8Array, Uint8Array][];
  assert.equal(rows.length, 6);
  const record = slots<{ v: number }>({ 0: ["v", float32] });
  let refused = 0;
  for (const [payload, rewritten] of rows) {
    if (isSignallingFloat32(payload)) {
      assert.equal(hex(rewritten), hex(payload), "the reference keeps the signalling bits");
      assert.throws(() => record.decode(readWire(payload)), { message: "v (slot 0): expected a float32, found a signalling NaN" }, hex(payload));
      refused += 1;
    } else {
      assert.equal(hex(writeWire(record.encode(record.decode(readWire(payload))))), hex(rewritten), hex(payload));
    }
  }
  assert.equal(refused, 3);
});

test("only a NaN with its quiet bit clear is signalling: a finite float32 with a clear bit 22 reads, and a float64 slot reads any float32 NaN", () => {
  const f32 = slots<{ v: number }>({ 0: ["v", float32] });
  const f64 = slots<{ v: number }>({ 0: ["v", float64] });
  assert.equal(f32.decode(readWire(fromHex("91ca3f800001"))).v, Math.fround(1.0000001192092896));
  assert.equal(f32.decode(readWire(fromHex("91ca00000001"))).v, Math.fround(1e-45));
  assert.equal(f32.decode(readWire(fromHex("91ca7f800000"))).v, Infinity);
  assert.ok(Number.isNaN(f32.decode(readWire(fromHex("91ca7fc00001"))).v));
  assert.ok(Number.isNaN(f64.decode(readWire(fromHex("91ca7f800001"))).v));
});

test("numeric leaves write the bytes the C# reference writes, and 64-bit values lose nothing", async () => {
  const rows = readWire(new Uint8Array(await readFile(join(numericVectors, "writes.msgpack")))) as [string, string, Uint8Array][];
  assert.equal(rows.length, 46);
  for (const [leaf, text, expected] of rows) {
    const value = leaf.startsWith("float") ? new DataView(Uint8Array.from(Buffer.from(text, "hex")).buffer).getFloat64(0) : leaf === "int32" ? Number(text) : BigInt(text);
    const record = slots<{ v: unknown }>({ 0: ["v", numericLeaves[leaf]] });
    assert.equal(hex(writeWire(record.encode({ v: value }))), hex(expected), `${leaf} ${text}`);
  }
  const wide = slots<{ v: number | bigint }>({ 0: ["v", int64] });
  for (const value of [2n ** 53n + 1n, 2n ** 63n - 1n, -(2n ** 63n), -(2n ** 53n) - 1n]) {
    assert.equal(wide.decode(readWire(writeWire(wide.encode({ v: value })))).v, value);
  }
  assert.equal(wide.decode(readWire(writeWire(wide.encode({ v: 2n })))).v, 2, "a bigint that fits comes back a number");
});

test("the wire writer's width boundaries: where a length or an integer changes its header", () => {
  assert.equal(hex(writeWire(0)), "00");
  assert.equal(hex(writeWire("a".repeat(31))).slice(0, 2), "bf");
  assert.equal(hex(writeWire("a".repeat(32))).slice(0, 4), "d920");
  assert.equal(hex(writeWire("a".repeat(255))).slice(0, 4), "d9ff");
  assert.equal(hex(writeWire("a".repeat(256))).slice(0, 6), "da0100");
  assert.equal(hex(writeWire("a".repeat(65535))).slice(0, 6), "daffff");
  assert.equal(hex(writeWire("a".repeat(65536))).slice(0, 10), "db00010000");
  assert.equal(hex(writeWire(new Uint8Array(1))), "c40100");
  assert.equal(hex(writeWire(new Uint8Array(256))).slice(0, 6), "c50100");
  assert.equal(hex(writeWire(new Uint8Array(65536))).slice(0, 10), "c600010000");
  assert.equal(hex(writeWire(new Array(15).fill(null))), `9f${"c0".repeat(15)}`);
  assert.equal(hex(writeWire(new Array(65535).fill(null))).slice(0, 6), "dcffff");
  assert.equal(hex(writeWire(new Array(65536).fill(null))).slice(0, 10), "dd00010000");
  assert.throws(() => writeWire(-(2n ** 63n) - 1n), { message: "cannot write an integer wider than 64 bits." });
  assert.throws(() => writeWire(undefined), /this kind of value/u);
  assert.equal(hex(writeWire(-(2n ** 63n))), "d38000000000000000");
});

test("the wire reader's boundaries: the largest fixarray, the 32-bit binary and string, the safe integer edge", () => {
  assert.deepEqual(readWire(fromHex(`9f${"c0".repeat(15)}`)), new Array(15).fill(null));
  assert.deepEqual(readWire(fromHex("c600000001aa")), new Uint8Array([0xaa]));
  assert.equal(readWire(fromHex("db0000000161")), "a");
  assert.equal(readWire(fromHex("d3ffe0000000000001")), -(2 ** 53 - 1));
  assert.equal(readWire(fromHex("d3ffe0000000000000")), -(2n ** 53n));
  assert.equal(readWire(fromHex("cf0020000000000000")), 2n ** 53n);
});

test("a refusal says what was found: nil, a float, an integer that is not the slot's, a plain kind", () => {
  assert.throws(() => int32.encode(undefined as unknown as number), { message: "expected an int32, found nil" });
  assert.throws(() => string.decode(new WireFloat(1, true)), { message: "expected a string, found float" });
  assert.throws(() => string.decode(5n), { message: "expected a string, found integer" });
  assert.throws(() => int32.decode(new WireFloat(7, true)), { message: "expected an int32, found float" });
  assert.throws(() => float64.decode("1"), { message: "expected a float64, found string" });
  assert.throws(() => int64.encode(1.5), { message: "expected an int64, found a non-integer number" });
});

test("nullable names a value type Nullable<T> and anything else as itself", () => {
  assert.equal(nullable(boolean).typeName, "System.Nullable<System.Boolean>");
  assert.equal(nullable(int64).typeName, "System.Nullable<System.Int64>");
  assert.equal(nullable(float32).typeName, "System.Nullable<System.Single>");
  assert.equal(nullable(float64).typeName, "System.Nullable<System.Double>");
  assert.equal(nullable(bytes).typeName, "System.Byte[]");
  const unnamedValueType: Codec<number> = { valueType: true, encode: (value) => value, decode: (raw) => raw as number };
  assert.equal(nullable(unnamedValueType).typeName, undefined);
  assert.equal(lazy(() => int32).valueType, true);
});

test("the Int32 edges of an arm key: the largest is an arm, one past either end is outside", () => {
  const edge = union<{ kind: "A" }>("U", { 2147483647: ["A", unit] });
  assert.deepEqual(edge.decode(readWire(fromHex("92ce7fffffff90"))), { kind: "A" });
  assert.deepEqual(edge.encode({ kind: "A" }), [2147483647, []]);
  assert.throws(() => edge.decode(readWire(fromHex("92ce8000000090"))), { message: "Union U: the arm key is outside Int32 (declared keys: 2147483647)." });
  assert.throws(() => edge.decode(readWire(fromHex("92d3ffffffff7fffffff90"))), { message: "Union U: the arm key is outside Int32 (declared keys: 2147483647)." });
  assert.throws(() => edge.decode(readWire(fromHex("92d3ffffffff8000000090"))), { message: "Union U: unknown arm key -2147483648 (declared keys: 2147483647)." });
});

function envelopeOf(payload: Uint8Array): CultCacheEnvelope {
  return { key: "surface", type: "tests.union_surface", payload: documentPayloadFromBytes(payload), storedAt: new Date().toISOString() };
}

function memoryStore(entries: CultCacheEnvelope[]): CacheBackingStore & { entries: CultCacheEnvelope[] } {
  return {
    entries,
    async pullAll() { return entries.map((entry) => ({ ...entry })); },
    async push(entry) { entries.push(entry); },
    async delete() {},
  };
}

test("arm keys span Int32 and are listed ascending; slot keys are non-negative Int32", () => {
  const keys = union<{ kind: "High" | "Zero" | "Neg" | "Low" }>("U", {
    2147483647: ["High", unit],
    0: ["Zero", unit],
    [-1]: ["Neg", unit],
    [-2147483648]: ["Low", unit],
  });
  for (const [kind, key] of [["High", 2147483647], ["Zero", 0], ["Neg", -1], ["Low", -2147483648]] as const) {
    const written = keys.encode({ kind });
    assert.deepEqual(written, [key, []]);
    assert.deepEqual(keys.decode(readWire(writeWire(written))), { kind });
  }
  assert.throws(() => keys.decode(readWire(fromHex("920590"))), { message: "Union U: unknown arm key 5 (declared keys: -2147483648, -1, 0, 2147483647)." });
  assert.throws(() => union<{ kind: "A" }>("U", { [-2147483649]: ["A", unit] }), { message: "Union U declares the key -2147483649, which is outside Int32." });
  assert.throws(() => union<{ kind: "A" }>("U", { 99999999999999999999: ["A", unit] } as never), { message: "Union U declares the key 100000000000000000000, which is outside Int32." });
  assert.throws(() => slots<{ a: string }>({ 2147483647: ["a", string] }), { message: "A slot record declares the key 2147483647, which is above the slot key limit 65535." });
  assert.throws(() => slots<{ a: string }>({ 2147483648: ["a", string] }), { message: "A slot record declares the key 2147483648, which is above the slot key limit 65535." });
  assert.throws(() => slots<{ a: string }>({ [-1]: ["a", string] }), { message: 'A slot record declares the key "-1", which is not a non-negative integer.' });
});

// A slot record is a dense array of key+1 slots, as the reference writes it, so the key is capped: the largest accepted
// key writes a 65536-slot payload, one more is refused at definition.
test("a slot key is capped at 65535: the cap is the largest slot record, one past it is refused at definition", () => {
  assert.equal(MAX_SLOT_KEY, 65535);
  const edge = slots<{ a: string }>({ 65535: ["a", string] });
  const written = edge.encode({ a: "x" }) as unknown[];
  assert.equal(written.length, 65536);
  assert.equal(written[65535], "x");
  assert.equal(written.filter((slot) => slot === null).length, 65535);
  assert.deepEqual(edge.decode(readWire(writeWire(written))), { a: "x" });
  assert.throws(() => slots<{ a: string }>({ 65536: ["a", string] }), { message: "A slot record declares the key 65536, which is above the slot key limit 65535." });
});

test("the mixed-record vector is the bytes the reference writes for a record of every leaf kind", async () => {
  const mixed = slots<{ id: string; i: number; l: number | bigint; f: number; d: number }>({
    0: ["id", string], 1: ["i", int32], 2: ["l", int64], 3: ["f", float32], 4: ["d", float64],
  });
  const committed = new Uint8Array(await readFile(join(vectors, "..", "numeric-leaves", "mixed-record.msgpack")));
  assert.equal(hex(writeWire(mixed.encode({ id: "n1", i: 7, l: 8, f: 1.5, d: 2.5 }))), hex(committed));
  assert.deepEqual(mixed.decode(readWire(committed)), { id: "n1", i: 7, l: 8, f: 1.5, d: 2.5 });
});

// tests/vectors/slot-nil is written by the C# reference (SlotNilVectorTests): what it reads and rewrites for a nil, a str in a
// byte[] slot, and a record short of its last slots. A runtime accepts what it accepts and rewrites the same bytes.
test("slots read and rewrite exactly what the C# reference does for nil, a str in bytes, and short records", async () => {
  const inner = slots<{ x: string }>({ 0: ["x", string, ""] });
  const probe = slots<{ s: string; i: number; b: Uint8Array; r: { x: string }; l: string[] }>({
    0: ["s", string, ""],
    1: ["i", int32, 0],
    2: ["b", bytes, () => new Uint8Array()],
    3: ["r", inner, null as unknown as { x: string }],
    4: ["l", list(string), () => []],
  });
  const rows = readWire(new Uint8Array(await readFile(join(vectors, "..", "slot-nil", "reads.msgpack")))) as [string, Uint8Array, boolean, Uint8Array][];
  assert.equal(rows.length, 15);
  let accepted = 0;
  for (const [name, payload, wasAccepted, rewritten] of rows) {
    if (wasAccepted) {
      assert.equal(hex(writeWire(probe.encode(probe.decode(readWire(payload))))), hex(rewritten), name);
      accepted += 1;
    } else {
      assert.throws(() => probe.decode(readWire(payload)), (error: Error) => !error.message.includes(hex(payload).slice(2)), name);
    }
  }
  assert.equal(accepted, 13);
});

test("a nil reads as null and writes as nil in every reference slot, and a value slot refuses it", () => {
  const record = slots<{ s: string; b: Uint8Array; i: number }>({ 0: ["s", string], 1: ["b", bytes], 2: ["i", int32, 5] });
  const read = record.decode([null, null, 3]);
  assert.deepEqual(read, { s: null, b: null, i: 3 });
  assert.deepEqual(record.encode(read), [null, null, 3]);
  assert.throws(() => record.encode({ s: "a", b: new Uint8Array(), i: null as unknown as number }), { message: "i (slot 2): expected an int32, found nil" });
  assert.throws(() => record.decode(["a", new Uint8Array(), null]), { message: "i (slot 2): expected an int32, found nil" });
});

test("a bytes slot reads a MessagePack str as its bytes and writes bin, and refuses a str that was not valid UTF-8", () => {
  const record = slots<{ b: Uint8Array }>({ 0: ["b", bytes] });
  const read = record.decode(readWire(fromHex("91a3616263")));
  assert.equal(hex(read.b), "616263");
  assert.equal(hex(writeWire(record.encode(read))), "91c403616263");
  assert.throws(() => record.decode(readWire(fromHex("91a2c328"))), { message: "b (slot 0): expected bytes, found a string that is not valid UTF-8" });
  assert.throws(() => record.decode([5]), { message: "b (slot 0): expected bytes, found number" });
});

// ---- the bounds vectors: arm keys at the ends of the Int32 domain, nil arm slots, payload nesting ----

const wrapChain = (wraps: number, leaf: Node): Node => {
  let node: Node = leaf;
  for (let index = 0; index < wraps; index += 1) node = { kind: "Wrap", inner: node };
  return node;
};
// A Wrap adds 2 containers, a Box with no children 3, a Text leaf 2, and the outermost array is 1 (the C# test's arithmetic).
const AT_LIMIT_WRAPS = (MAX_PAYLOAD_DEPTH - 1 - 3) / 2;
const PAST_LIMIT_WRAPS = (MAX_PAYLOAD_DEPTH + 1 - 1 - 2) / 2;
const atLimitValue: Surface = { ...golden, root: wrapChain(AT_LIMIT_WRAPS, { kind: "Box", id: "leaf", children: [] }) };
const pastLimitValue: Surface = { ...golden, root: wrapChain(PAST_LIMIT_WRAPS, { kind: "Text", id: "leaf", content: CANARY }) };

async function boundsEntries() {
  const raw = decode(await read("bounds-outcomes.msgpack")) as unknown[][];
  const [limit, ...rest] = raw;
  return { limit, entries: rest.map(([file, outcome, unionName, key, detail]) => ({ file: file as string, outcome: outcome as string, unionName: unionName as string, key: key as number | null, detail: detail as string })) };
}

test("every bounds vector is listed and holds: accepted files decode and re-encode, refused files are refused naming the union or the depth", async () => {
  const { limit, entries } = await boundsEntries();
  assert.deepEqual(limit, ["limit", MAX_PAYLOAD_DEPTH]);
  assert.equal(entries.length, 12);
  const document = surfaceDocument();
  const counts = { accepted: 0, refused: 0, readOnly: 0 };

  for (const { file, outcome: expected, unionName, key, detail } of entries) {
    const payload = documentPayloadFromBytes(await read(file));
    if (expected === "accepted") {
      counts.accepted += 1;
      const value = decodeDocumentPayload(document, payload);
      assert.equal(hex(documentPayloadBytes(encodeDocumentPayload(document, value))), hex(await read(detail)), `${file}: re-encodes to ${detail}`);
    } else if (expected === "read-only") {
      counts.readOnly += 1;
      assert.deepEqual(decodeDocumentPayload(document, payload), golden, `${file}: the extra slots are read past`);
    } else {
      counts.refused += 1;
      assert.throws(
        () => decodeDocumentPayload(document, payload),
        (error: Error) => {
          assert.ok(!error.message.includes(CANARY), `${file}: the refusal echoed the payload`);
          if (detail === "depth") {
            return error instanceof CultCachePayloadDepthError && error.depth === MAX_PAYLOAD_DEPTH + 1 && error.limit === MAX_PAYLOAD_DEPTH
              && error.message.includes(`"${document.type}"`) && error.message.includes("257") && error.message.includes("256");
          }
          assert.ok(!(error instanceof CultCachePayloadDepthError), file);
          const said = detail === "key-out-of-range" ? "outside Int32" : detail === "key-not-integer" ? "not an integer" : "expected [key, slots]";
          return error.message.includes(`Union ${unionName}:`) && error.message.includes(said);
        },
        file,
      );
    }
  }
  assert.deepEqual(counts, { accepted: 3 + 1, refused: 4 + 1 + 2, readOnly: 1 });
});

test("the constructed values write the committed at-limit payload byte for byte, and the past-limit value is refused and stored nowhere", async () => {
  const document = surfaceDocument();
  assert.equal(hex(documentPayloadBytes(encodeDocumentPayload(document, atLimitValue))), hex(await read("depth-at-limit.msgpack")));
  assert.deepEqual(decodeDocumentPayload(document, documentPayloadFromBytes(await read("depth-at-limit.msgpack"))), atLimitValue);

  assert.throws(
    () => encodeDocumentPayload(document, pastLimitValue),
    (error: Error) => error instanceof CultCachePayloadDepthError && error.depth === 257 && !error.message.includes(CANARY),
  );

  const entries: CultCacheEnvelope[] = [];
  const cache = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore(entries)).build();
  await assert.rejects(() => cache.put(document, "past", pastLimitValue), CultCachePayloadDepthError);
  assert.equal(entries.length, 0, "nothing reached the store");
  assert.equal(cache.get(document, "past"), undefined, "nothing is held in memory");
  await cache.put(document, "at", atLimitValue);
  assert.equal(entries.length, 1);
  assert.equal(hex(documentPayloadBytes(entries[0]!.payload)), hex(await read("depth-at-limit.msgpack")));
});

test("a cache load refuses a store whose record is past the limit, naming the type and not the payload", async () => {
  const document = surfaceDocument();
  const refusing = CultCache.builder().withDocumentType(document).withGenericStore(memoryStore([envelopeOf(await read("depth-past-limit.msgpack"))])).build();
  await assert.rejects(() => refusing.pullAllBackingStores(), (error: Error) => error instanceof CultCachePayloadDepthError && !error.message.includes(CANARY));
});
