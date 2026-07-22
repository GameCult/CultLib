import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { decodePlanetarySurfaceTile } from "../src/planetary-tile.ts";
if (process.argv.length !== 3) throw new Error("Expected a generated CMPT fixture path");
const bytes = await readFile(process.argv[2]);
assert.equal(bytes.length, 1221);
assert.equal(createHash("sha256").update(bytes).digest("hex"), "8b2bc46e4123b8ac8936b43f8d08a34e3f2e74f38d0bb3f101cf3728cd201962");
const tile = decodePlanetarySurfaceTile(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
assert.equal(tile.header.projectionVersion, 3); assert.equal(tile.header.layerId, 29);
assert.equal(tile.header.projectionKind, 2); assert.equal(tile.header.interiorSize, 5);
assert.equal(tile.header.borderSize, 1); assert.equal(tile.storageSize, 7); assert.equal(tile.samples.length, 49);
assert.ok(tile.samples.some(sample => sample !== null));
for (const sample of tile.samples) if (sample !== null) {
  assert.ok(Number.isFinite(sample.radialDisplacement)); assert.ok(Number.isFinite(sample.unresolvedHeightBound));
}
