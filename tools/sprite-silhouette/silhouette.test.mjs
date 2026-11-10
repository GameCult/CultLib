import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";
import { deflateSync } from "node:zlib";
import { decodeAlpha } from "./png.mjs";
import { silhouette, silhouettes } from "./silhouette.mjs";
import { largestContour, mask, maxDistanceToRing, simplifyRing } from "./trace.mjs";

// An RGBA8 PNG built from an alpha function, every row encoded with the given PNG filter.
function crc32(buf) {
  let c, crc = ~0;
  for (let i = 0; i < buf.length; i++) {
    c = (crc ^ buf[i]) & 255;
    for (let k = 0; k < 8; k++) c = c & 1 ? (c >>> 1) ^ 0xedb88320 : c >>> 1;
    crc = (crc >>> 8) ^ c;
  }
  return ~crc >>> 0;
}
function chunk(type, body) {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(body.length, 0);
  head.write(type, 4, "latin1");
  const tail = Buffer.alloc(4);
  tail.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), body])), 0);
  return Buffer.concat([head, body, tail]);
}
function png(width, height, alphaAt, { filter = 0 } = {}) {
  const rows = [];
  let prev = Buffer.alloc(width * 4);
  for (let y = 0; y < height; y++) {
    const line = Buffer.alloc(width * 4);
    for (let x = 0; x < width; x++) line.set([200, 10 * (x % 20), 30, alphaAt(x, y)], x * 4);
    const out = Buffer.alloc(width * 4 + 1);
    out[0] = filter;
    for (let i = 0; i < line.length; i++) {
      const a = i >= 4 ? line[i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
      const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
      const pred = [0, a, b, (a + b) >> 1, pa <= pb && pa <= pc ? a : pb <= pc ? b : c][filter];
      out[i + 1] = (line[i] - pred) & 255;
    }
    rows.push(out);
    prev = line;
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr), chunk("IDAT", deflateSync(Buffer.concat(rows))), chunk("IEND", Buffer.alloc(0)),
  ]);
}
const disc = (cx, cy, r) => (x, y) => ((x + 0.5 - cx) ** 2 + (y + 0.5 - cy) ** 2 <= r * r ? 255 : 0);

test("decodes alpha through every PNG row filter", () => {
  const f = (x, y) => (x * 7 + y * 13) % 256;
  for (const filter of [0, 1, 2, 3, 4]) {
    const { width, height, alpha } = decodeAlpha(png(9, 7, f, { filter }));
    assert.deepEqual([width, height], [9, 7]);
    for (let y = 0; y < 7; y++) for (let x = 0; x < 9; x++) assert.equal(alpha[y * 9 + x], f(x, y), `filter ${filter} at ${x},${y}`);
  }
});

test("refuses what it cannot read", () => {
  assert.throws(() => decodeAlpha(Buffer.from("nope")), /not a PNG/);
  const bad = png(2, 2, () => 255);
  bad[24] = 16; // IHDR bit depth
  assert.throws(() => decodeAlpha(bad), /bit depth 16/);
});

test("a fully transparent sprite has no shape", () => {
  const s = silhouette(png(8, 8, () => 0));
  assert.deepEqual(s.points, []);
  assert.equal(s.css, "none");
});

test("a fully opaque sprite is its own bounding box", () => {
  const s = silhouette(png(8, 4, () => 255));
  assert.deepEqual(s.points, [[0, 0], [100, 0], [100, 100], [0, 100]]);
  assert.equal(s.css, "polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)");
});

test("threshold is alpha/255 >= 0.35: 89 is out, 90 is in", () => {
  assert.deepEqual(silhouette(png(4, 4, () => 89)).points, []);
  assert.equal(silhouette(png(4, 4, () => 90)).points.length, 4);
  assert.deepEqual(silhouette(png(4, 4, () => 89), { threshold: 0.3 }).points.length, 4);
});

test("simplified shape stays within tolerance of the traced edge, both ways, and is smaller", () => {
  const { width, height, alpha } = decodeAlpha(png(120, 100, disc(60, 50, 40)));
  const ring = largestContour(mask(alpha, 0.35), width, height);
  for (const tolerance of [0.5, 1.5, 4]) {
    const simple = simplifyRing(ring, tolerance);
    assert.ok(simple.length < ring.length && simple.length >= 4);
    assert.ok(maxDistanceToRing(ring, simple) <= tolerance + 1e-9, `ring to simplified at ${tolerance}`);
    assert.ok(maxDistanceToRing(simple, ring) <= tolerance + 1e-9, `simplified to ring at ${tolerance}`);
  }
  // And the traced edge is the true edge to within a pixel: every ring vertex sits within 1px of the disc's circle.
  for (const [x, y] of ring) assert.ok(Math.abs(Math.hypot(x - 60, y - 50) - 40) <= 1.0, `${x},${y}`);
});

test("only the outside is traced: a hole is ignored", () => {
  const ring = (x, y) => (Math.max(Math.abs(x - 10), Math.abs(y - 10)) <= 8 && Math.max(Math.abs(x - 10), Math.abs(y - 10)) > 3 ? 255 : 0);
  const s = silhouette(png(20, 20, ring), { tolerance: 0 });
  const xs = s.points.map((p) => p[0]), ys = s.points.map((p) => p[1]);
  assert.deepEqual([Math.min(...xs), Math.max(...xs), Math.min(...ys), Math.max(...ys)], [10, 95, 10, 95]);
  assert.equal(s.points.length, 4);
});

test("pixels that only touch diagonally are one shape; a smaller island is dropped", () => {
  const dots = (x, y) => (x === y && x < 4 ? 255 : 0);
  const diag = silhouette(png(8, 8, dots), { tolerance: 0 });
  const xs = diag.points.map((p) => p[0]);
  assert.equal(Math.max(...xs), 50); // x 0..4 of 8
  const island = (x, y) => ((x < 5 && y < 5) || (x === 8 && y === 8) ? 255 : 0);
  const s = silhouette(png(10, 10, island));
  assert.deepEqual(s.points, [[0, 0], [50, 0], [50, 50], [0, 50]]);
});

test("deterministic: same bytes, same polygon; coordinates are percent within the image", () => {
  const bytes = png(90, 70, disc(45, 35, 30));
  const a = silhouette(bytes), b = silhouette(Buffer.from(bytes));
  assert.deepEqual(a, b);
  for (const [x, y] of a.points) assert.ok(x >= 0 && x <= 100 && y >= 0 && y <= 100);
  assert.match(a.css, /^polygon\((\d+(\.\d+)?% \d+(\.\d+)?%)(, \d+(\.\d+)?% \d+(\.\d+)?%){3,}\)$/);
});

const POSES = "/opt/commons/cast-art/poses";
test("every pose in the shared cast-art catalogue gets a polygon", { skip: !existsSync(`${POSES}/catalogue.json`) }, () => {
  const all = silhouettes(POSES);
  const ids = Object.keys(all);
  assert.ok(ids.length > 90 && ids.includes("void/neutral"));
  for (const id of ids) assert.ok(all[id].points.length >= 4 && all[id].points.length < 400, id);
  assert.deepEqual(silhouettes(POSES), all);
});
