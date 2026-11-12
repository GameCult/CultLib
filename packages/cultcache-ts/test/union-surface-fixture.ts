import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { join, resolve } from "node:path";

import { z } from "zod";

import { codecs } from "../src";
import type { Codec } from "../src/codec";
import { defineDocumentType, documentPayloadFromBytes } from "../src/document";
import type { CacheBackingStore, CultCacheEnvelope } from "../src/types";

// The union-surface fixture: the codecs, schema and value tests/vectors/union-parity was written from, shared by the
// union-parity walker and the slot-readonly walker so the shape is spelled once.
const { list, int32, lazy, slots, string, union, unit } = codecs;


// The names the C# reference gives the fixture's types (CultSchemaTypeNames.FromType), as union-surface.cc carries them.
export const CS = "GameCult.Caching.Tests.UnionParityVectorTests+";

// The golden is tests/vectors/union-parity, written by the C# reference. Everything below that is bytes or an expected
// outcome is read from those committed files; only the value the payload must decode to is spelled here.
export function findVectors(from: string): string {
  for (let dir = from; ; dir = resolve(dir, "..")) {
    if (existsSync(join(dir, "tests", "vectors", "union-parity"))) return join(dir, "tests", "vectors", "union-parity");
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/union-parity not found above the test.");
  }
}
export const vectors = findVectors(__dirname);
export const read = async (name: string) => new Uint8Array(await readFile(join(vectors, name)));

// The string the C# writer put inside every refusal payload, to prove a refusal never echoes the payload.
export const CANARY = "CANARY-union-golden-4c2a";

export type Node =
  | { kind: "Box"; id: string; children: (Node | null)[] }
  | { kind: "Text"; id: string; content: string }
  | { kind: "Use"; id: string; composition: Composition; slots: (Node | null)[] }
  | { kind: "Image"; id: string; uri: string; alt: string }
  | { kind: "Wrap"; inner: Node | null; id: string };
export interface Composition { name: string; body: Node | null }
export type Outcome = { kind: "Pending"; message: string } | { kind: "Completed"; sourceVersion: number } | { kind: "Denied" } | { kind: "Floor" } | { kind: "Ceiling" };
// The golden's Outcome: the arms of a list carry an element id, so Denied, Floor and Ceiling are one-slot records.
export type GoldenOutcome =
  | { kind: "Pending"; message: string; id: string }
  | { kind: "Completed"; sourceVersion: number; id: string }
  | { kind: "Denied"; id: string }
  | { kind: "Floor"; id: string }
  | { kind: "Ceiling"; id: string };
export type Item = { kind: "Gem"; name: string } | { kind: "Tool"; name: string; uses: number } | { kind: "Part"; name: string; count: number };
export type CraftedItem = { kind: "Tool"; name: string; uses: number } | { kind: "Part"; name: string; count: number };
export interface Surface {
  id: string;
  version: number;
  root: Node | null;
  outcomes: (GoldenOutcome | null)[];
  part: Item | null;
  crafted: CraftedItem | null;
  missing: Node | null;
}

export const composition = slots<Composition>({ 0: ["name", string], 1: ["body", lazy(() => node)] }, `${CS}Composition`);
export const node: Codec<Node | null> = union<Node>("Node", {
  0: ["Box", slots<{ id: string; children: (Node | null)[] }>({ 0: ["id", string], 1: ["children", list(lazy(() => node))] }, `${CS}Box`)],
  1: ["Text", slots<{ id: string; content: string }>({ 0: ["id", string], 1: ["content", string] }, `${CS}Text`)],
  2: ["Use", slots<{ id: string; composition: Composition; slots: (Node | null)[] }>({
    0: ["id", string], 1: ["composition", composition], 2: ["slots", list(lazy(() => node))],
  })],
  5: ["Image", slots<{ id: string; uri: string; alt: string }>({ 0: ["id", string], 1: ["uri", string], 3: ["alt", string] })],
  6: ["Wrap", slots<{ inner: Node | null; id: string }>({ 0: ["inner", lazy(() => node)], 1: ["id", string] })],
}, `${CS}Node`);
export const outcome = union<Outcome>("Outcome", {
  0: ["Pending", slots<{ message: string }>({ 0: ["message", string] })],
  1: ["Completed", slots<{ sourceVersion: number }>({ 0: ["sourceVersion", int32] })],
  2: ["Denied", unit],
  [-2147483648]: ["Floor", unit],
  2147483647: ["Ceiling", unit],
}, `${CS}Outcome`);
export const idOnly = slots<{ id: string }>({ 0: ["id", string] });
export const goldenOutcome = union<GoldenOutcome>("Outcome", {
  0: ["Pending", slots<{ message: string; id: string }>({ 0: ["message", string], 1: ["id", string] })],
  1: ["Completed", slots<{ sourceVersion: number; id: string }>({ 0: ["sourceVersion", int32], 1: ["id", string] })],
  2: ["Denied", idOnly],
  [-2147483648]: ["Floor", idOnly],
  2147483647: ["Ceiling", idOnly],
}, `${CS}Outcome`);
export const toolArm = slots<{ name: string; uses: number }>({ 0: ["name", string], 1: ["uses", int32] });
export const partArm = slots<{ name: string; count: number }>({ 0: ["name", string], 1: ["count", int32] });
export const item = union<Item>("Item", {
  0: ["Gem", slots<{ name: string }>({ 0: ["name", string] })],
  1: ["Tool", toolArm],
  2: ["Part", partArm],
}, `${CS}Item`);
export const craftedItem = union<CraftedItem>("CraftedItem", { 0: ["Tool", toolArm], 1: ["Part", partArm] }, `${CS}CraftedItem`);
export const surfaceCodecOf = (typeName: string) => slots<Surface>({
  0: ["id", string],
  1: ["version", int32],
  2: ["root", node],
  3: ["outcomes", list(goldenOutcome)],
  4: ["part", item],
  5: ["crafted", craftedItem],
  6: ["missing", node, null],
}, typeName);
export const surfaceCodec = surfaceCodecOf(`${CS}UnionSurface`);

export const nodeSchema: z.ZodType<Node | null> = z.lazy(() =>
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
      z.object({ kind: z.literal("Wrap"), inner: nodeSchema, id: z.string() }),
    ]),
  ]));
export const surfaceSchema = z.object({
  id: z.string(),
  version: z.number(),
  root: nodeSchema,
  outcomes: z.array(z.union([
    z.null(),
    z.discriminatedUnion("kind", [
      z.object({ kind: z.literal("Pending"), message: z.string(), id: z.string() }),
      z.object({ kind: z.literal("Completed"), sourceVersion: z.number(), id: z.string() }),
      z.object({ kind: z.literal("Denied"), id: z.string() }),
      z.object({ kind: z.literal("Floor"), id: z.string() }),
      z.object({ kind: z.literal("Ceiling"), id: z.string() }),
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

// The reference's SurfaceDeclaring: the same layout, schema tests.union_surface.v2, listing the older wide id compatible.
export function surfaceDeclaringDocument() {
  return defineDocumentType({
    type: "tests.union_surface",
    schemaId: "tests.union_surface.v2",
    schemaName: "tests.union_surface",
    schemaVersion: "tests.union_surface.v2",
    compatibleSchemaIds: ["tests.union_surface.wide"],
    schema: surfaceSchema,
    codec: surfaceCodecOf(`${CS}SurfaceDeclaring`),
    name: "id",
  });
}

export function surfaceDocument(schemaId?: string, contentHash?: string) {
  return defineDocumentType({
    ...(contentHash === undefined ? {} : { contentHash }),
    type: "tests.union_surface",
    schemaId: schemaId ?? "tests.union_surface.v1",
    schemaName: "tests.union_surface",
    schemaVersion: "tests.union_surface.v1",
    schema: surfaceSchema,
    codec: surfaceCodec,
    name: "id",
  });
}

export const golden: Surface = {
  id: "surface",
  version: 3,
  root: {
    kind: "Box",
    id: "0000000000c0",
    children: [
      { kind: "Text", id: "0000000000c1", content: "hello" },
      {
        kind: "Use",
        id: "0000000000c2",
        composition: {
          name: "comp",
          body: {
            kind: "Box",
            id: "0000000000d0",
            children: [
              { kind: "Text", id: "0000000000d1", content: "x" },
              { kind: "Image", id: "0000000000d2", uri: "uri://a", alt: "alt" },
            ],
          },
        },
        slots: [{ kind: "Text", id: "0000000000e1", content: "slot" }, null, { kind: "Box", id: "0000000000e2", children: [] }],
      },
      { kind: "Image", id: "0000000000c3", uri: "uri://b", alt: "" },
    ],
  },
  outcomes: [
    { kind: "Pending", message: "wait", id: "0000000000a1" },
    { kind: "Completed", sourceVersion: 7, id: "0000000000a2" },
    { kind: "Denied", id: "0000000000a3" },
  ],
  part: { kind: "Tool", name: "hammer", uses: 12 },
  crafted: { kind: "Part", name: "gear", count: 4 },
  missing: null,
};
export const hex = (data: Uint8Array) => Buffer.from(data).toString("hex");

// tests.slot_unit: a document [Name, Outcome: UnitOutcome], whose one arm Denied2 is a unit arm (extra-slot-store.cc, record
// unit-arm). The reader's names are the reference's: a mark names Denied2.
export const unitOutcome = union<{ kind: "Denied2" }>("UnitOutcome", { 2: ["Denied2", unit] }, `${CS}UnitOutcome`);
export function slotUnitDocument(schemaId: string, contentHash?: string) {
  return defineDocumentType({
    ...(contentHash === undefined ? {} : { contentHash }),
    type: "tests.slot_unit",
    schemaId,
    schemaName: "tests.slot_unit",
    schemaVersion: "tests.slot_unit.v1",
    schema: z.object({ name: z.string(), outcome: z.union([z.null(), z.object({ kind: z.literal("Denied2") })]) }),
    codec: slots<{ name: string; outcome: { kind: "Denied2" } | null }>({ 0: ["name", string], 1: ["outcome", unitOutcome] }, `${CS}SlotUnit`),
    name: "name",
  });
}

export function envelopeOf(payload: Uint8Array): CultCacheEnvelope {
  return { key: "surface", type: "tests.union_surface", payload: documentPayloadFromBytes(payload), storedAt: new Date().toISOString() };
}

export function memoryStore(entries: CultCacheEnvelope[]): CacheBackingStore & { entries: CultCacheEnvelope[] } {
  return {
    entries,
    async pullAll() { return entries.map((entry) => ({ ...entry })); },
    async push(entry) { entries.push(entry); },
    async delete() {},
  };
}

