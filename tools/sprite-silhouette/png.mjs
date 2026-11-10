// Minimal PNG reader: the alpha plane only. 8-bit, non-interlaced (every pose in cast-art is RGBA8).
// Anything else is refused by name, never guessed at.
import { inflateSync } from "node:zlib";

const SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const CHANNELS = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 };

/** @returns {{ width: number, height: number, alpha: Uint8Array }} alpha is row-major, 0..255. */
export function decodeAlpha(buffer) {
  if (buffer.length < 8 || !buffer.subarray(0, 8).equals(SIGNATURE)) throw new Error("not a PNG");
  let header = null;
  let palette = null;
  let trns = null;
  const idat = [];
  for (let at = 8; at + 8 <= buffer.length; ) {
    const length = buffer.readUInt32BE(at);
    const type = buffer.toString("latin1", at + 4, at + 8);
    const body = buffer.subarray(at + 8, at + 8 + length);
    if (body.length !== length) throw new Error(`truncated ${type} chunk`);
    at += 12 + length;
    if (type === "IHDR") {
      header = { width: body.readUInt32BE(0), height: body.readUInt32BE(4), depth: body[8], color: body[9], interlace: body[12] };
    } else if (type === "PLTE") palette = body;
    else if (type === "tRNS") trns = body;
    else if (type === "IDAT") idat.push(body);
    else if (type === "IEND") break;
  }
  if (!header) throw new Error("PNG has no IHDR");
  const { width, height, depth, color, interlace } = header;
  if (depth !== 8) throw new Error(`unsupported PNG bit depth ${depth} (8 only)`);
  if (interlace !== 0) throw new Error("unsupported PNG: interlaced");
  const channels = CHANNELS[color];
  if (!channels) throw new Error(`unsupported PNG color type ${color}`);
  if (color === 3 && !palette) throw new Error("indexed PNG has no PLTE");

  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  if (raw.length !== (stride + 1) * height) throw new Error("PNG pixel data has the wrong length");

  const alpha = new Uint8Array(width * height);
  let prev = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Uint8Array.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    unfilter(filter, line, prev, channels);
    for (let x = 0; x < width; x++) {
      const p = x * channels;
      let a = 255;
      if (color === 6) a = line[p + 3];
      else if (color === 4) a = line[p + 1];
      else if (color === 3) a = trns && line[p] < trns.length ? trns[line[p]] : 255;
      else if (trns && color === 0 && line[p] === trns.readUInt16BE(0)) a = 0;
      else if (trns && color === 2 && line[p] === trns.readUInt16BE(0) && line[p + 1] === trns.readUInt16BE(2) && line[p + 2] === trns.readUInt16BE(4)) a = 0;
      alpha[y * width + x] = a;
    }
    prev = line;
  }
  return { width, height, alpha };
}

function unfilter(filter, line, prev, bpp) {
  for (let i = 0; i < line.length; i++) {
    const a = i >= bpp ? line[i - bpp] : 0;
    const b = prev[i];
    const c = i >= bpp ? prev[i - bpp] : 0;
    let add;
    switch (filter) {
      case 0: add = 0; break;
      case 1: add = a; break;
      case 2: add = b; break;
      case 3: add = (a + b) >> 1; break;
      case 4: {
        const p = a + b - c;
        const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
        add = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        break;
      }
      default: throw new Error(`bad PNG filter ${filter}`);
    }
    line[i] = (line[i] + add) & 255;
  }
}
