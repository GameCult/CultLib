// The slice of MessagePack a codec value uses: nil, booleans, integers, floats, strings, binaries and arrays. A codec
// must tell an integer from a float and keep 64-bit integers whole, which a general decoder that hands back JS numbers
// cannot; so the codec path owns its reader and writer. An integer decodes to a number, or to a bigint when it is
// outside the safe range. A float decodes to a WireFloat, whatever its value.

export class WireFloat {
  // signalling: a float32 NaN whose quiet bit is clear. The JS number it becomes has lost that payload.
  constructor(readonly value: number, readonly wide: boolean, readonly signalling = false) {}
}

const SAFE_MIN = BigInt(Number.MIN_SAFE_INTEGER);
const SAFE_MAX = BigInt(Number.MAX_SAFE_INTEGER);
const utf8 = new TextEncoder();
const utf8Decoder = new TextDecoder();

export function writeWire(tree: unknown): Uint8Array {
  const parts: number[] = [];
  put(tree, parts);
  return Uint8Array.from(parts);
}

function be(parts: number[], bytes: number, value: bigint): void {
  for (let shift = BigInt((bytes - 1) * 8); shift >= 0n; shift -= 8n) {
    parts.push(Number((value >> shift) & 0xffn));
  }
}

function putInt(value: bigint, parts: number[]): void {
  if (value >= 0n) {
    if (value < 128n) parts.push(Number(value));
    else if (value < 0x100n) { parts.push(0xcc); be(parts, 1, value); }
    else if (value < 0x10000n) { parts.push(0xcd); be(parts, 2, value); }
    else if (value < 0x100000000n) { parts.push(0xce); be(parts, 4, value); }
    else if (value < 0x10000000000000000n) { parts.push(0xcf); be(parts, 8, value); }
    else throw new Error("cannot write an integer wider than 64 bits.");
  } else if (value >= -32n) parts.push(0x100 + Number(value));
  else if (value >= -0x80n) { parts.push(0xd0); be(parts, 1, value & 0xffn); }
  else if (value >= -0x8000n) { parts.push(0xd1); be(parts, 2, value & 0xffffn); }
  else if (value >= -0x80000000n) { parts.push(0xd2); be(parts, 4, value & 0xffffffffn); }
  else if (value >= -0x8000000000000000n) { parts.push(0xd3); be(parts, 8, value & 0xffffffffffffffffn); }
  else throw new Error("cannot write an integer wider than 64 bits.");
}

function putLength(length: number, parts: number[], fix: number, fixMax: number, codes: readonly [number, number, number]): void {
  if (length <= fixMax) parts.push(fix | length);
  else if (codes[0] !== 0 && length < 0x100) { parts.push(codes[0]); be(parts, 1, BigInt(length)); }
  else if (length < 0x10000) { parts.push(codes[1]); be(parts, 2, BigInt(length)); }
  else { parts.push(codes[2]); be(parts, 4, BigInt(length)); }
}

function put(node: unknown, parts: number[]): void {
  if (node === null) parts.push(0xc0);
  else if (typeof node === "boolean") parts.push(node ? 0xc3 : 0xc2);
  else if (typeof node === "bigint") putInt(node, parts);
  else if (typeof node === "number") {
    if (!Number.isSafeInteger(node)) throw new Error("cannot write a number that is not a safe integer; floats are WireFloat.");
    putInt(BigInt(node), parts);
  } else if (node instanceof WireFloat) {
    const view = new DataView(new ArrayBuffer(8));
    if (node.wide) { view.setFloat64(0, node.value); parts.push(0xcb); } else { view.setFloat32(0, node.value); parts.push(0xca); }
    for (let index = 0; index < (node.wide ? 8 : 4); index += 1) parts.push(view.getUint8(index));
  } else if (typeof node === "string") {
    const text = utf8.encode(node);
    putLength(text.length, parts, 0xa0, 31, [0xd9, 0xda, 0xdb]);
    for (const byte of text) parts.push(byte);
  } else if (node instanceof Uint8Array) {
    putLength(node.length, parts, 0, -1, [0xc4, 0xc5, 0xc6]);
    for (const byte of node) parts.push(byte);
  } else if (Array.isArray(node)) {
    putLength(node.length, parts, 0x90, 15, [0, 0xdc, 0xdd]);
    for (const element of node) put(element, parts);
  } else {
    throw new Error("cannot write this kind of value.");
  }
}

export function readWire(payload: Uint8Array): unknown {
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  let offset = 0;

  const take = (count: number): number => {
    if (offset + count > payload.length) throw new Error("the MessagePack payload is truncated.");
    offset += count;
    return offset - count;
  };
  const integer = (value: bigint): number | bigint => (value >= SAFE_MIN && value <= SAFE_MAX ? Number(value) : value);
  const string = (length: number): string => utf8Decoder.decode(payload.subarray(take(length), offset));
  const binary = (length: number): Uint8Array => payload.slice(take(length), offset);
  const array = (length: number): unknown[] => {
    const out: unknown[] = [];
    for (let index = 0; index < length; index += 1) out.push(next());
    return out;
  };

  function next(): unknown {
    const head = payload[take(1)];
    if (head < 0x80) return head;
    if (head >= 0xe0) return head - 0x100;
    if (head >= 0xa0 && head <= 0xbf) return string(head & 0x1f);
    if (head >= 0x90 && head <= 0x9f) return array(head & 0x0f);
    switch (head) {
      case 0xc0: return null;
      case 0xc2: return false;
      case 0xc3: return true;
      case 0xc4: return binary(view.getUint8(take(1)));
      case 0xc5: return binary(view.getUint16(take(2)));
      case 0xc6: return binary(view.getUint32(take(4)));
      case 0xca: {
        const at = take(4);
        const bits = view.getUint32(at);
        return new WireFloat(view.getFloat32(at), false, (bits & 0x7f800000) === 0x7f800000 && (bits & 0x007fffff) !== 0 && (bits & 0x00400000) === 0);
      }
      case 0xcb: return new WireFloat(view.getFloat64(take(8)), true);
      case 0xcc: return view.getUint8(take(1));
      case 0xcd: return view.getUint16(take(2));
      case 0xce: return view.getUint32(take(4));
      case 0xcf: return integer(view.getBigUint64(take(8)));
      case 0xd0: return view.getInt8(take(1));
      case 0xd1: return view.getInt16(take(2));
      case 0xd2: return view.getInt32(take(4));
      case 0xd3: return integer(view.getBigInt64(take(8)));
      case 0xd9: return string(view.getUint8(take(1)));
      case 0xda: return string(view.getUint16(take(2)));
      case 0xdb: return string(view.getUint32(take(4)));
      case 0xdc: return array(view.getUint16(take(2)));
      case 0xdd: return array(view.getUint32(take(4)));
      default: throw new Error("the MessagePack payload holds a type a codec does not read (map, extension or reserved).");
    }
  }

  const tree = next();
  if (offset !== payload.length) throw new Error("the MessagePack payload has bytes after its value.");
  return tree;
}
