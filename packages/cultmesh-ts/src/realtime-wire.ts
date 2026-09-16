// The one TypeScript encoding of a CultMesh realtime frame.
//
// Rule for rule, this is `CultMeshRealtimeWireProtocol` in the C# reference
// (`src/GameCult.Mesh/CultMeshRealtimeWireProtocol.cs`), over the frame shape
// `CultMeshRealtimeTransports.cs` defines. QUIC stream framing stays
// transport-owned; this file owns only the typed frame bytes and never touches
// a socket.
//
// Byte parity is proven both ways, cross-process, by two committed vector files
// under `contracts/cultmesh/`: `realtime-frame-vectors.json`, which the C# test
// `CultMeshRealtimeWireProtocolTests` writes under CULTMESH_WRITE_VECTORS=1 and
// this package's test decodes and re-encodes, and
// `realtime-frame-vectors.ts-written.json`, which
// `scripts/write-cultmesh-realtime-frame-vectors.mjs` writes with this codec and
// the same C# test decodes. A fixture authored on one side alone would only
// agree with itself.

import { isNullOrWhiteSpaceCSharp } from "cultnet-ts";

/** `CultMeshRealtimeWireProtocol.ApplicationProtocolName`: the QUIC ALPN. */
export const CULTMESH_REALTIME_ALPN = "cultmesh-state-v1";

/** `ConnectionCloseCode`: ASCII "CULT". A QUIC error code is 62-bit, so bigint. */
export const CULTMESH_REALTIME_CONNECTION_CLOSE_CODE = 0x43554c54n;

/** `StreamAbortCode`: ASCII "STAT". */
export const CULTMESH_REALTIME_STREAM_ABORT_CODE = 0x53544154n;

/** `ReliableStream`: the stream kind carrying `reliable-ordered`. */
export const CULTMESH_REALTIME_RELIABLE_STREAM = 1;

/** `LatestOnlyStream`: the stream kind carrying `latest-only`. */
export const CULTMESH_REALTIME_LATEST_ONLY_STREAM = 2;

/** `MaximumFrameBytes`: the payload ceiling, 64 MiB. */
export const CULTMESH_REALTIME_MAX_PAYLOAD_BYTES = 64 * 1024 * 1024;

/** `MaximumEncodedFrameBytes`: the payload ceiling plus the header and three maximal identities. */
export const CULTMESH_REALTIME_MAX_ENCODED_FRAME_BYTES =
  CULTMESH_REALTIME_MAX_PAYLOAD_BYTES + 37 + 3 * 0xffff;

/**
 * `CultMeshRealtimeDelivery`. The wire byte is the C# enum's ordinal, so the
 * order of these three is the wire contract, not a naming choice.
 */
export type CultMeshRealtimeDelivery = "reliable-ordered" | "latest-only" | "unreliable";

const DELIVERY_BYTES: readonly CultMeshRealtimeDelivery[] = ["reliable-ordered", "latest-only", "unreliable"];

/** `CultMeshRealtimeFrame`. `producerEpoch` and `sequence` are `long` in C#, which exceeds `Number.MAX_SAFE_INTEGER`. */
export interface CultMeshRealtimeFrame {
  channelId: string;
  schemaId: string;
  bodyId: string;
  producerEpoch: bigint;
  sequence: bigint;
  delivery: CultMeshRealtimeDelivery;
  payload: Uint8Array;
}

const MAGIC = 0x31545343;
const FIXED_HEADER_BYTES = 37;
const WIRE_VERSION = 1;
const MAX_IDENTITY_BYTES = 0xffff;
const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;

/**
 * `CultMeshRealtimeFrame.Validate`. The C# reading of "non-empty" is
 * `string.IsNullOrWhiteSpace`, whose whitespace set is not
 * `String.prototype.trim`'s, so this borrows the one TypeScript implementation
 * of that set from `cultnet-ts` rather than keeping a second reading here.
 *
 * The range check has no C# counterpart because `long` gives it for free; a
 * `bigint` does not, and a `DataView` would otherwise throw a bare `RangeError`
 * from inside the encoder.
 */
function validateFrame(frame: CultMeshRealtimeFrame): void {
  if (isNullOrWhiteSpaceCSharp(frame.channelId)) throw new Error("Realtime channel identity is required.");
  if (isNullOrWhiteSpaceCSharp(frame.schemaId)) throw new Error("Realtime schema identity is required.");
  if (isNullOrWhiteSpaceCSharp(frame.bodyId)) throw new Error("Realtime body identity is required.");
  if (frame.producerEpoch < 0n || frame.sequence < 0n) {
    throw new Error("Realtime epoch and sequence must be non-negative.");
  }
  if (frame.producerEpoch > INT64_MAX || frame.sequence > INT64_MAX || frame.producerEpoch < INT64_MIN || frame.sequence < INT64_MIN) {
    throw new Error("Realtime epoch and sequence must fit a 64-bit signed integer.");
  }
}

/** `CultMeshRealtimeWireProtocol.EncodeFrame`. */
export function encodeRealtimeFrame(frame: CultMeshRealtimeFrame): Uint8Array {
  validateFrame(frame);
  const deliveryByte = DELIVERY_BYTES.indexOf(frame.delivery);
  // C# reads an enum, which holds any byte; a TypeScript union does not, so the
  // unspellable value is refused here with the message decode gives it.
  if (deliveryByte < 0) throw new Error("Realtime frame delivery mode is invalid.");
  const encoder = new TextEncoder();
  const channel = encoder.encode(frame.channelId);
  const schema = encoder.encode(frame.schemaId);
  const body = encoder.encode(frame.bodyId);
  if (channel.byteLength > MAX_IDENTITY_BYTES || schema.byteLength > MAX_IDENTITY_BYTES || body.byteLength > MAX_IDENTITY_BYTES) {
    throw new Error("Realtime frame identity exceeds the QUIC wire limit.");
  }
  if (frame.payload.byteLength > CULTMESH_REALTIME_MAX_PAYLOAD_BYTES) {
    throw new Error("Realtime frame payload exceeds the QUIC wire limit.");
  }

  const result = new Uint8Array(
    FIXED_HEADER_BYTES + channel.byteLength + schema.byteLength + body.byteLength + frame.payload.byteLength,
  );
  const view = new DataView(result.buffer);
  view.setUint32(0, MAGIC, true);
  result[4] = deliveryByte;
  view.setBigInt64(5, frame.producerEpoch, true);
  view.setBigInt64(13, frame.sequence, true);
  view.setUint16(21, channel.byteLength, true);
  view.setUint16(23, schema.byteLength, true);
  view.setUint16(25, body.byteLength, true);
  view.setInt32(27, frame.payload.byteLength, true);
  view.setInt32(31, FIXED_HEADER_BYTES, true);
  view.setUint16(35, WIRE_VERSION, true);
  let offset = FIXED_HEADER_BYTES;
  result.set(channel, offset); offset += channel.byteLength;
  result.set(schema, offset); offset += schema.byteLength;
  result.set(body, offset); offset += body.byteLength;
  result.set(frame.payload, offset);
  return result;
}

/**
 * `CultMeshRealtimeWireProtocol.DecodeFrame`, refusal for refusal and in the
 * same order. Like the reference, it does not call `validateFrame`: a frame
 * whose identities are blank decodes here exactly as it does in C#, and only
 * encoding refuses it.
 *
 * One corner has no C# counterpart. The reference sums the lengths under
 * `checked`, so a payload length near `int.MaxValue` throws `OverflowException`
 * there rather than its length refusal; JavaScript numbers do not overflow, so
 * the sum simply fails to match and the length refusal covers that case too.
 * No encoder on either side can produce such a frame.
 */
export function decodeRealtimeFrame(bytes: Uint8Array): CultMeshRealtimeFrame {
  if (bytes.byteLength < FIXED_HEADER_BYTES) throw new Error("Realtime frame header is truncated.");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.getUint32(0, true) !== MAGIC) throw new Error("Realtime frame magic is invalid.");
  if (view.getUint16(35, true) !== WIRE_VERSION || view.getInt32(31, true) !== FIXED_HEADER_BYTES) {
    throw new Error("Realtime frame wire version is unsupported.");
  }
  const delivery = DELIVERY_BYTES[bytes[4]!];
  if (delivery === undefined) throw new Error("Realtime frame delivery mode is invalid.");
  const channelLength = view.getUint16(21, true);
  const schemaLength = view.getUint16(23, true);
  const bodyLength = view.getUint16(25, true);
  const payloadLength = view.getInt32(27, true);
  const expected = FIXED_HEADER_BYTES + channelLength + schemaLength + bodyLength + payloadLength;
  if (payloadLength < 0 || expected !== bytes.byteLength) throw new Error("Realtime frame length is invalid.");
  // `Encoding.UTF8.GetString` substitutes U+FFFD for invalid bytes rather than
  // throwing, which is what a non-fatal `TextDecoder` does. `ignoreBOM` is not
  // cosmetic: by default `TextDecoder` *deletes* a leading U+FEFF, and
  // `GetString` keeps it, so an identity starting with a byte-order mark would
  // decode to a different string on the two sides. U+FEFF is not whitespace to
  // .NET either, so such an identity passes the transport contract and reaches
  // the wire.
  const decoder = new TextDecoder("utf-8", { ignoreBOM: true });
  let offset = FIXED_HEADER_BYTES;
  const channelId = decoder.decode(bytes.subarray(offset, offset + channelLength)); offset += channelLength;
  const schemaId = decoder.decode(bytes.subarray(offset, offset + schemaLength)); offset += schemaLength;
  const bodyId = decoder.decode(bytes.subarray(offset, offset + bodyLength)); offset += bodyLength;
  return {
    channelId,
    schemaId,
    bodyId,
    producerEpoch: view.getBigInt64(5, true),
    sequence: view.getBigInt64(13, true),
    delivery,
    // Not `bytes.slice(...)`. Node's `Buffer` overrides `slice` with an alias of
    // `subarray`, so on a `Buffer` — which is what every socket read hands a Node
    // caller — that spelling returns a *view* of the frame, and a decoded payload
    // that changes when the frame buffer is reused.
    //
    // Nor `Uint8Array.prototype.slice.call(bytes, ...)`, which does bypass the
    // override and copy, but takes its result constructor from `@@species`: on a
    // `Buffer` that is `FastBuffer`, so the payload comes back a `Buffer` from a
    // `Buffer` frame and a `Uint8Array` from a `Uint8Array` one. The declared
    // type holds either way, but `deepStrictEqual` does not, so the same frame
    // would compare equal or not depending on how it reached the decoder.
    //
    // The constructor copies and is the one type on both inputs.
    payload: new Uint8Array(bytes.subarray(offset, offset + payloadLength)),
  };
}
