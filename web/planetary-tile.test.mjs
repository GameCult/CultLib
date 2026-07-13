import assert from "node:assert/strict";
import { decodePlanetarySurfaceTile } from "./planetary-tile.ts";

const bytes = new ArrayBuffer(152);
const view = new DataView(bytes);
let offset = 0;
const u8 = value => view.setUint8(offset++, value);
const u32 = value => { view.setUint32(offset, value, true); offset += 4; };
const i32 = value => { view.setInt32(offset, value, true); offset += 4; };
const u64 = value => { view.setBigUint64(offset, value, true); offset += 8; };
const f32 = value => { view.setFloat32(offset, value, true); offset += 4; };
const f64 = value => { view.setFloat64(offset, value, true); offset += 8; };

for (const character of "CMPT") u8(character.charCodeAt(0));
u32(1); u64(0x1234n); u32(2); u32(17); i32(0);
f64(0); f64(0); f64(1);
i32(0); i32(0); i32(0); i32(2); i32(0);
f32(10); f32(0.5); i32(4);
u8(1);
for (const value of [1, 0, 0]) f32(value);
f32(8.4); f32(0.25);
for (const value of [0, 0.1, 0]) f32(value);
for (const value of [0.99, -0.1, 0]) f32(value);
for (const value of [0.1, 0.8, 0.2, 2.5, 0.05]) f32(value);
u8(0); u8(0); u8(0);
assert.equal(offset, bytes.byteLength);

const tile = decodePlanetarySurfaceTile(bytes);
assert.equal(tile.header.fieldVersion, 0x1234n);
assert.equal(tile.header.layerId, 17);
assert.equal(tile.storageSize, 2);
assert.equal(tile.samples.length, 4);
assert.equal(tile.samples[0]?.radialDisplacement, 0.25);
assert.equal(tile.samples[1], null);

assert.throws(() => decodePlanetarySurfaceTile(bytes.slice(0, bytes.byteLength - 1)), /Truncated/);
const trailing = new Uint8Array(bytes.byteLength + 1);
trailing.set(new Uint8Array(bytes));
assert.throws(() => decodePlanetarySurfaceTile(trailing.buffer), /trailing/);
