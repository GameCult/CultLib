import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { decodePlanetarySurfaceTile } from "./planetary-tile.ts";

if (process.argv.length !== 3) throw new Error("Expected a generated CMPT fixture path");
const bytes = await readFile(process.argv[2]);
const arrayBuffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
const tile = decodePlanetarySurfaceTile(arrayBuffer);
assert.equal(tile.header.projectionVersion, 3);
assert.equal(tile.header.layerId, 29);
assert.equal(tile.header.projectionKind, 2);
assert.equal(tile.header.interiorSize, 5);
assert.equal(tile.header.borderSize, 1);
assert.equal(tile.storageSize, 7);
assert.equal(tile.samples.length, 49);
assert.ok(tile.samples.some(sample => sample !== null));
for (const sample of tile.samples) {
  if (sample === null) continue;
  assert.ok(Number.isFinite(sample.radialDisplacement));
  assert.ok(Number.isFinite(sample.unresolvedHeightBound));
}
