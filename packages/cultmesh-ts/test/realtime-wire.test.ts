// The realtime frame codec, pinned rule by rule against the C# reference.
//
// The bytes themselves are pinned by two committed files under
// `contracts/cultmesh/`. `realtime-frame-vectors.json` is written by
// `CultMeshRealtimeWireProtocolTests.RealtimeFrameVectorsAreSharedWithTypeScript`
// under CULTMESH_WRITE_VECTORS=1: this side decodes it and re-encodes to the
// same bytes. `realtime-frame-vectors.ts-written.json` is written by
// `scripts/write-cultmesh-realtime-frame-vectors.mjs` with this codec and
// decoded by the C# reference, so parity is proven in both directions and in
// two processes. A fixture authored on one side alone would only agree with
// itself, which is what the authority vectors already taught.

import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import {
  CULTMESH_REALTIME_ALPN,
  CULTMESH_REALTIME_CONNECTION_CLOSE_CODE,
  CULTMESH_REALTIME_LATEST_ONLY_STREAM,
  CULTMESH_REALTIME_MAX_ENCODED_FRAME_BYTES,
  CULTMESH_REALTIME_MAX_PAYLOAD_BYTES,
  CULTMESH_REALTIME_RELIABLE_STREAM,
  CULTMESH_REALTIME_STREAM_ABORT_CODE,
  decodeRealtimeFrame,
  encodeRealtimeFrame,
  type CultMeshRealtimeDelivery,
  type CultMeshRealtimeFrame,
} from "../src/realtime-wire";

const CONTRACTS = join(__dirname, "..", "..", "..", "..", "contracts", "cultmesh");
const CSHARP_VECTORS = join(CONTRACTS, "realtime-frame-vectors.json");
const TS_VECTORS = join(CONTRACTS, "realtime-frame-vectors.ts-written.json");

interface IdentityRule { repeat: string; byteLength: number }
interface RampRule { ramp: number }
interface VectorFields {
  channelId: string | IdentityRule;
  schemaId: string | IdentityRule;
  bodyId: string | IdentityRule;
  producerEpoch: number;
  sequence: number;
  delivery: CultMeshRealtimeDelivery;
  payload: string | RampRule;
}
interface Vectors {
  frames: {
    label: string;
    fields: VectorFields;
    encoding: string | null;
    encodedLength: number;
    sha256OfEncoding: string;
  }[];
  malformed: { label: string; encoding: string; message: string }[];
}

/** The repeat unit written whole until the UTF-8 byte length is exactly `byteLength`. */
function repeatToByteLength(unit: string, byteLength: number): string {
  const unitBytes = Buffer.byteLength(unit, "utf8");
  assert.equal(byteLength % unitBytes, 0, `'${unit}' is ${unitBytes} UTF-8 bytes and does not divide ${byteLength}`);
  return unit.repeat(byteLength / unitBytes);
}

/** The deterministic payload both runtimes build the same way. */
function ramp(length: number): Uint8Array {
  const bytes = new Uint8Array(length);
  for (let index = 0; index < length; index += 1) bytes[index] = index % 251;
  return bytes;
}

function identity(value: string | IdentityRule): string {
  return typeof value === "string" ? value : repeatToByteLength(value.repeat, value.byteLength);
}

function payload(value: string | RampRule): Uint8Array {
  return typeof value === "string" ? new Uint8Array(Buffer.from(value, "base64")) : ramp(value.ramp);
}

function frameOf(fields: VectorFields): CultMeshRealtimeFrame {
  return {
    channelId: identity(fields.channelId),
    schemaId: identity(fields.schemaId),
    bodyId: identity(fields.bodyId),
    // The C# `long` fields are written as JSON integers and can exceed the safe
    // integer range, so they are read from the raw text, never through
    // `JSON.parse`'s number.
    producerEpoch: 0n,
    sequence: 0n,
    delivery: fields.delivery,
    payload: payload(fields.payload),
  };
}

// `JSON.parse` would round `long.MaxValue` to 9223372036854775808, which is not
// a valid i64 and not what either side encoded. The two i64 fields are lifted
// out of the raw text as bigints before the document is parsed.
function readVectors(path: string): { doc: Vectors; int64s: Map<string, { producerEpoch: bigint; sequence: bigint }> } {
  const text = readFileSync(path, "utf8");
  const int64s = new Map<string, { producerEpoch: bigint; sequence: bigint }>();
  const pattern = /"label": "(?<label>(?:[^"\\]|\\.)*)"[\s\S]*?"producerEpoch": (?<epoch>-?\d+),\s*"sequence": (?<sequence>-?\d+),/g;
  for (const match of text.matchAll(pattern)) {
    const groups = match.groups!;
    int64s.set(JSON.parse(`"${groups["label"]!}"`) as string, {
      producerEpoch: BigInt(groups["epoch"]!),
      sequence: BigInt(groups["sequence"]!),
    });
  }
  return { doc: JSON.parse(text) as Vectors, int64s };
}

const sha256 = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");

for (const [label, path] of [["C# reference", CSHARP_VECTORS], ["TypeScript-written", TS_VECTORS]] as const) {
  test(`every ${label} frame vector decodes to its recorded fields and re-encodes to its recorded bytes`, () => {
    const { doc, int64s } = readVectors(path);
    assert.ok(doc.frames.length >= 8, `${label} carries ${doc.frames.length} frames`);
    const seen = new Set<CultMeshRealtimeDelivery>();
    for (const vector of doc.frames) {
      const int64 = int64s.get(vector.label);
      assert.ok(int64, `${vector.label}: no i64 pair was lifted from the text`);
      const frame = { ...frameOf(vector.fields), ...int64 };
      seen.add(frame.delivery);

      // This side encodes the recorded fields to the recorded bytes.
      const encoded = encodeRealtimeFrame(frame);
      assert.equal(encoded.byteLength, vector.encodedLength, vector.label);
      assert.equal(sha256(encoded), vector.sha256OfEncoding, vector.label);
      // The two oversized cases carry no literal encoding; the digest above is
      // the whole of their parity.
      const recorded = vector.encoding === null ? encoded : new Uint8Array(Buffer.from(vector.encoding, "base64"));
      assert.deepEqual(recorded, encoded, vector.label);

      // And decodes those bytes back to the recorded fields.
      const decoded = decodeRealtimeFrame(recorded);
      assert.equal(decoded.channelId, frame.channelId, vector.label);
      assert.equal(decoded.schemaId, frame.schemaId, vector.label);
      assert.equal(decoded.bodyId, frame.bodyId, vector.label);
      assert.equal(decoded.producerEpoch, frame.producerEpoch, vector.label);
      assert.equal(decoded.sequence, frame.sequence, vector.label);
      assert.equal(decoded.delivery, frame.delivery, vector.label);
      assert.deepEqual(decoded.payload, frame.payload, vector.label);
    }
    // One vector per delivery mode, or a mode is unpinned.
    assert.deepEqual([...seen].sort(), ["latest-only", "reliable-ordered", "unreliable"]);
  });

  test(`every ${label} malformed vector is refused with the C# reference's exact message`, () => {
    const { doc } = readVectors(path);
    assert.ok(doc.malformed.length >= 8, `${label} carries ${doc.malformed.length} malformed frames`);
    for (const vector of doc.malformed) {
      const bytes = new Uint8Array(Buffer.from(vector.encoding, "base64"));
      assert.throws(
        () => decodeRealtimeFrame(bytes),
        (error: Error) => error.message === vector.message,
        `${vector.label}: expected ${JSON.stringify(vector.message)}`,
      );
    }
    // Every refusal the C# decoder can give is exercised by the file.
    assert.deepEqual([...new Set(doc.malformed.map(vector => vector.message))].sort(), [
      "Realtime frame delivery mode is invalid.",
      "Realtime frame header is truncated.",
      "Realtime frame length is invalid.",
      "Realtime frame magic is invalid.",
      "Realtime frame wire version is unsupported.",
    ]);
  });
}

const sample = (overrides: Partial<CultMeshRealtimeFrame> = {}): CultMeshRealtimeFrame => ({
  channelId: "aetheria.zone-1",
  schemaId: "cultmesh.realtime.v1",
  bodyId: "body-7",
  producerEpoch: 1n,
  sequence: 2n,
  delivery: "reliable-ordered",
  payload: Uint8Array.from([1, 2, 3]),
  ...overrides,
});

test("the wire constants are the C# reference's, including the ALPN and the two QUIC error codes", () => {
  assert.equal(CULTMESH_REALTIME_ALPN, "cultmesh-state-v1");
  assert.equal(CULTMESH_REALTIME_CONNECTION_CLOSE_CODE, 0x43554c54n);
  assert.equal(CULTMESH_REALTIME_STREAM_ABORT_CODE, 0x53544154n);
  assert.equal(CULTMESH_REALTIME_RELIABLE_STREAM, 1);
  assert.equal(CULTMESH_REALTIME_LATEST_ONLY_STREAM, 2);
  assert.equal(CULTMESH_REALTIME_MAX_PAYLOAD_BYTES, 64 * 1024 * 1024);
  assert.equal(CULTMESH_REALTIME_MAX_ENCODED_FRAME_BYTES, 64 * 1024 * 1024 + 37 + 3 * 65535);
  // The ALPN and the close codes are ASCII the C# side spells as numbers.
  assert.equal(Buffer.from("CULT", "ascii").readUInt32BE(0), Number(CULTMESH_REALTIME_CONNECTION_CLOSE_CODE));
  assert.equal(Buffer.from("STAT", "ascii").readUInt32BE(0), Number(CULTMESH_REALTIME_STREAM_ABORT_CODE));
});

// `CultMeshRealtimeWireProtocol.cs:37-54`, offset by offset. A vector proves the
// whole layout at once; this says which byte carries which field, so a swap of
// two same-width fields names itself.
test("the fixed header is 37 little-endian bytes in the reference's order", () => {
  const encoded = encodeRealtimeFrame(sample({
    channelId: "ch", schemaId: "sch", bodyId: "bdy",
    producerEpoch: 0x0102030405060708n, sequence: 0x1112131415161718n,
    delivery: "unreliable", payload: Uint8Array.from([0xaa]),
  }));
  const view = new DataView(encoded.buffer, encoded.byteOffset, encoded.byteLength);
  assert.equal(view.getUint32(0, true), 0x31545343, "magic, little-endian");
  assert.deepEqual([...encoded.subarray(0, 4)], [0x43, 0x53, 0x54, 0x31], "magic bytes as written");
  assert.equal(encoded[4], 2, "delivery");
  assert.equal(view.getBigInt64(5, true), 0x0102030405060708n, "producer epoch");
  assert.equal(view.getBigInt64(13, true), 0x1112131415161718n, "sequence");
  assert.equal(view.getUint16(21, true), 2, "channel length");
  assert.equal(view.getUint16(23, true), 3, "schema length");
  assert.equal(view.getUint16(25, true), 3, "body length");
  assert.equal(view.getInt32(27, true), 1, "payload length");
  assert.equal(view.getInt32(31, true), 37, "header size");
  assert.equal(view.getUint16(35, true), 1, "wire version");
  assert.equal(Buffer.from(encoded.subarray(37, 37 + 2 + 3 + 3)).toString("utf8"), "chschbdy", "identities in order");
  assert.deepEqual([...encoded.subarray(45)], [0xaa], "payload last");
  assert.equal(encoded.byteLength, 37 + 2 + 3 + 3 + 1);
});

test("the delivery byte is the C# enum ordinal: 0, 1, 2 in that order", () => {
  const bytes: Record<CultMeshRealtimeDelivery, number> = { "reliable-ordered": 0, "latest-only": 1, "unreliable": 2 };
  for (const [delivery, byte] of Object.entries(bytes) as [CultMeshRealtimeDelivery, number][]) {
    const encoded = encodeRealtimeFrame(sample({ delivery }));
    assert.equal(encoded[4], byte, delivery);
    assert.equal(decodeRealtimeFrame(encoded).delivery, delivery, delivery);
  }
  assert.throws(
    () => encodeRealtimeFrame(sample({ delivery: "at-most-once" as CultMeshRealtimeDelivery })),
    /delivery mode is invalid/,
  );
});

test("identities are UTF-8 and their length prefixes count bytes, not characters", () => {
  const frame = sample({ channelId: "z\u00f6ne-\u221e", schemaId: "s", bodyId: "b" });
  const encoded = encodeRealtimeFrame(frame);
  const view = new DataView(encoded.buffer, encoded.byteOffset, encoded.byteLength);
  assert.equal(frame.channelId.length, 6);
  assert.equal(view.getUint16(21, true), Buffer.byteLength(frame.channelId, "utf8"));
  assert.equal(view.getUint16(21, true), 9);
  assert.equal(decodeRealtimeFrame(encoded).channelId, frame.channelId);
});

test("an identity of exactly 65535 UTF-8 bytes encodes and one byte more is refused", () => {
  const wide = repeatToByteLength("\u00e4\u20ac", 65535);
  assert.equal(Buffer.byteLength(wide, "utf8"), 65535);
  assert.notEqual(wide.length, 65535);
  const encoded = encodeRealtimeFrame(sample({ channelId: wide }));
  assert.equal(new DataView(encoded.buffer, encoded.byteOffset).getUint16(21, true), 65535);
  assert.equal(decodeRealtimeFrame(encoded).channelId, wide);
  for (const field of ["channelId", "schemaId", "bodyId"] as const) {
    assert.throws(
      () => encodeRealtimeFrame(sample({ [field]: `${wide}a` })),
      /identity exceeds the QUIC wire limit/,
      field,
    );
  }
});

test("a payload at the 64 MiB ceiling encodes and one byte more is refused", () => {
  const encoded = encodeRealtimeFrame(sample({ payload: ramp(CULTMESH_REALTIME_MAX_PAYLOAD_BYTES) }));
  assert.equal(encoded.byteLength, 37 + 15 + 20 + 6 + CULTMESH_REALTIME_MAX_PAYLOAD_BYTES);
  assert.equal(decodeRealtimeFrame(encoded).payload.byteLength, CULTMESH_REALTIME_MAX_PAYLOAD_BYTES);
  assert.throws(
    () => encodeRealtimeFrame(sample({ payload: new Uint8Array(CULTMESH_REALTIME_MAX_PAYLOAD_BYTES + 1) })),
    /payload exceeds the QUIC wire limit/,
  );
});

// `CultMeshRealtimeTransports.cs:29-39`, message for message. The blank strings
// are C#'s `IsNullOrWhiteSpace` set, not `String.prototype.trim`'s: U+0085 is
// whitespace to .NET and not to JavaScript, and U+FEFF the other way round.
test("encode refuses what the transport contract forbids, in the reference's words", () => {
  for (const blank of ["", " ", "\t", "\u0085", "\u3000"]) {
    assert.throws(() => encodeRealtimeFrame(sample({ channelId: blank })), /^Error: Realtime channel identity is required\.$/);
    assert.throws(() => encodeRealtimeFrame(sample({ schemaId: blank })), /^Error: Realtime schema identity is required\.$/);
    assert.throws(() => encodeRealtimeFrame(sample({ bodyId: blank })), /^Error: Realtime body identity is required\.$/);
  }
  // U+FEFF is not whitespace to .NET, so it is a legal one-character identity.
  assert.equal(decodeRealtimeFrame(encodeRealtimeFrame(sample({ channelId: "\ufeff" }))).channelId, "\ufeff");
  assert.throws(() => encodeRealtimeFrame(sample({ producerEpoch: -1n })), /epoch and sequence must be non-negative/);
  assert.throws(() => encodeRealtimeFrame(sample({ sequence: -1n })), /epoch and sequence must be non-negative/);
  // C# gets this bound from `long`; a bigint does not.
  assert.throws(() => encodeRealtimeFrame(sample({ sequence: 2n ** 63n })), /must fit a 64-bit signed integer/);
  assert.equal(encodeRealtimeFrame(sample({ sequence: 2n ** 63n - 1n })).byteLength, 37 + 15 + 20 + 6 + 3);
});

// `CultMeshRealtimeWireProtocol.cs:60-76`, refusal for refusal and in the same
// order: a frame that breaks two rules names the earlier one.
test("decode refuses every malformed frame the reference refuses, in the reference's order", () => {
  const good = encodeRealtimeFrame(sample());
  const spoil = (mutate: (bytes: Uint8Array) => void) => { const copy = Uint8Array.from(good); mutate(copy); return copy; };
  const cases: readonly (readonly [string, Uint8Array, string])[] = [
    ["a header one byte short", good.subarray(0, 36), "Realtime frame header is truncated."],
    ["an empty buffer", new Uint8Array(0), "Realtime frame header is truncated."],
    ["a wrong magic", spoil(bytes => { bytes[3] = 0x32; }), "Realtime frame magic is invalid."],
    ["a big-endian magic", spoil(bytes => bytes.set([0x31, 0x54, 0x53, 0x43], 0)), "Realtime frame magic is invalid."],
    ["wire version 0", spoil(bytes => { bytes[35] = 0; }), "Realtime frame wire version is unsupported."],
    ["wire version 2", spoil(bytes => { bytes[35] = 2; }), "Realtime frame wire version is unsupported."],
    ["header size 36", spoil(bytes => { bytes[31] = 36; }), "Realtime frame wire version is unsupported."],
    ["header size 38", spoil(bytes => { bytes[31] = 38; }), "Realtime frame wire version is unsupported."],
    ["delivery 3", spoil(bytes => { bytes[4] = 3; }), "Realtime frame delivery mode is invalid."],
    ["delivery 255", spoil(bytes => { bytes[4] = 255; }), "Realtime frame delivery mode is invalid."],
    ["a negative payload length", spoil(bytes => { bytes[30] = 0x80; }), "Realtime frame length is invalid."],
    ["one byte too many", Uint8Array.from([...good, 0]), "Realtime frame length is invalid."],
    ["one byte too few", good.subarray(0, good.byteLength - 1), "Realtime frame length is invalid."],
    ["a channel length one too long", spoil(bytes => { bytes[21] += 1; }), "Realtime frame length is invalid."],
  ];
  for (const [label, bytes, message] of cases) {
    assert.throws(() => decodeRealtimeFrame(bytes), (error: Error) => error.message === message, label);
  }
  // Order: a frame whose magic and delivery are both wrong is refused for the
  // magic, and one whose version and delivery are both wrong for the version.
  assert.throws(
    () => decodeRealtimeFrame(spoil(bytes => { bytes[3] = 0x32; bytes[4] = 9; })),
    (error: Error) => error.message === "Realtime frame magic is invalid.",
  );
  assert.throws(
    () => decodeRealtimeFrame(spoil(bytes => { bytes[35] = 3; bytes[4] = 9; })),
    (error: Error) => error.message === "Realtime frame wire version is unsupported.",
  );
  // Like the reference, decode does not re-run the transport contract: a frame
  // with a blank identity decodes rather than being refused.
  const blank = spoil(bytes => bytes.set(new Uint8Array(15).fill(0x20), 37));
  assert.equal(decodeRealtimeFrame(blank).channelId, " ".repeat(15));
});

// The `payloadLength < 0` clause in the length refusal is not redundant with the
// sum check beside it, which is what a surviving mutant that dropped it claimed.
// A negative payload length cancels against the identity lengths, so a frame can
// be crafted whose sum lands exactly on its own byte length. Without the clause
// each of these decodes to blank identities and an empty payload where the C#
// reference (`CultMeshRealtimeWireProtocol.cs:74-76`, same clause) refuses.
test("decode refuses a negative payload length that the length sum cancels out", () => {
  const good = encodeRealtimeFrame(sample());
  // A valid magic, wire version, header size and delivery byte, then the three
  // identity lengths and the payload length written by hand.
  const crafted = (channelLength: number, payloadLength: number, totalBytes: number) => {
    const bytes = new Uint8Array(totalBytes);
    bytes.set(good.subarray(0, 37), 0);
    const view = new DataView(bytes.buffer);
    view.setUint16(21, channelLength, true);
    view.setUint16(23, 0, true);
    view.setUint16(25, 0, true);
    view.setInt32(27, payloadLength, true);
    // The sum the decoder computes must land exactly on the frame's own length,
    // or the check beside the clause would refuse it and prove nothing.
    assert.equal(37 + channelLength + 0 + 0 + payloadLength, totalBytes, "the crafted sum must match");
    return bytes;
  };
  const cases: readonly (readonly [string, Uint8Array])[] = [
    ["a maximal channel length cancelled by -65535, summing to 37", crafted(0xffff, -0xffff, 37)],
    ["a channel length of 1 cancelled by -1, summing to 37", crafted(1, -1, 37)],
    ["a channel length of 2 cancelled by -1, summing to 38", crafted(2, -1, 38)],
  ];
  for (const [label, bytes] of cases) {
    assert.throws(
      () => decodeRealtimeFrame(bytes),
      (error: Error) => error.message === "Realtime frame length is invalid.",
      label,
    );
  }
});

test("decode reads exactly the viewed bytes of an offset view, not the buffer behind it", () => {
  const good = encodeRealtimeFrame(sample({ payload: Uint8Array.from([9, 8, 7]) }));
  const pool = new Uint8Array(good.byteLength + 64).fill(0xa5);
  pool.set(good, 17);
  const view = pool.subarray(17, 17 + good.byteLength);
  assert.deepEqual(decodeRealtimeFrame(view).payload, Uint8Array.from([9, 8, 7]));
  assert.equal(decodeRealtimeFrame(view).channelId, "aetheria.zone-1");
  // And the decoded payload does not alias the frame it came from.
  const decoded = decodeRealtimeFrame(good);
  good[good.byteLength - 1] = 0;
  assert.deepEqual(decoded.payload, Uint8Array.from([9, 8, 7]));
});

// The `Uint8Array` case above passes with a plain `bytes.slice(...)`; this one
// does not. Node's `Buffer` overrides `slice` with an alias of `subarray`, and a
// `Buffer` is what a socket read hands a Node caller, so the aliasing bug this
// pins is the one that would actually reach a consumer.
test("the decoded payload copies out of a Node Buffer too, which overrides slice", () => {
  const frame = Buffer.from(encodeRealtimeFrame(sample({ payload: Uint8Array.from([9, 8, 7]) })));
  const decoded = decodeRealtimeFrame(frame);
  frame[frame.byteLength - 1] = 0;
  assert.deepEqual(decoded.payload, Uint8Array.from([9, 8, 7]), "payload unchanged by a later write to the frame");
  assert.equal(Buffer.isBuffer(decoded.payload), false, "the copy is a plain Uint8Array, not a Buffer");
  assert.notEqual(decoded.payload.buffer, frame.buffer, "the copy has its own backing store");
  // The three identities need no such check: `TextDecoder` returns strings, which
  // hold no reference to the bytes they were decoded from.
  assert.equal(decoded.channelId, "aetheria.zone-1");
});

test("the codec touches no socket: no node: import in the source", () => {
  const source = readFileSync(join(__dirname, "..", "..", "src", "realtime-wire.ts"), "utf8");
  assert.doesNotMatch(source, /from "node:|require\("node:/);
});
