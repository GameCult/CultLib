import { decode, encode } from "@msgpack/msgpack";

// The one msgpack entry point above the wire for packages that do not own it: a frame, an envelope, a command, any
// value that is not a document payload. A document payload is read through cultcache-ts's decodeDocumentPayload.
export function encodeWireValue(value: unknown): Uint8Array {
  return encode(value);
}

export function decodeWireValue(bytes: ArrayLike<number> | ArrayBufferView | ArrayBuffer): unknown {
  return decode(bytes);
}
