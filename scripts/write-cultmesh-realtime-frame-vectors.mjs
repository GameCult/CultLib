// The TypeScript -> C# direction of the CultMesh realtime frame parity check.
//
// `contracts/cultmesh/realtime-frame-vectors.json` is written by the C#
// reference and decoded by TypeScript. This script writes the mirror file,
// `contracts/cultmesh/realtime-frame-vectors.ts-written.json`: the TypeScript
// codec (`packages/cultmesh-ts/src/realtime-wire.ts`) encodes the frames and
// builds the malformed bytes, and the C# test
// `CultMeshRealtimeWireProtocolTests.RealtimeFrameVectorsWrittenByTypeScriptDecodeHere`
// judges the committed file with the reference implementation only.
//
// The two files share one field shape, so each side reads both with one reader:
// an identity is a string or `{ repeat, byteLength }`, a payload is base64 or
// `{ ramp }`, and `encoding` is null when the frame is too large to sit in a
// committed contract file, where the digest carries the parity instead.
//
//   npm run build --workspace packages/cultmesh-ts
//   node scripts/write-cultmesh-realtime-frame-vectors.mjs
//
// Unlike the authority vectors, nothing here is randomised: rerunning on an
// unchanged codec rewrites the same bytes.

import { createHash } from "node:crypto";
import { existsSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const modulePath = join(repoRoot, "packages", "cultmesh-ts", "dist", "realtime-wire.js");
const outputPath = join(repoRoot, "contracts", "cultmesh", "realtime-frame-vectors.ts-written.json");
if (!existsSync(modulePath)) {
  throw new Error(`${modulePath} is missing; run 'npm run build --workspace packages/cultmesh-ts' first.`);
}
const { encodeRealtimeFrame, decodeRealtimeFrame, CULTMESH_REALTIME_MAX_PAYLOAD_BYTES } =
  createRequire(import.meta.url)(modulePath);

// Deliberately not the C# file's frames: different identities, different
// epochs, a different ramp length. Agreeing on a copy of the other side's
// inputs would prove less.
const WIDE_CHANNEL_UNIT = "\u00fc\u2603";
const WIDE_SCHEMA_UNIT = "\u2603\u00fc";
const WIDE_BODY_UNIT = "\u00e7\u2603";
const WIDE_IDENTITY_BYTES = 65535;
const INLINE_LIMIT = 4096;

const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");
const base64 = bytes => Buffer.from(bytes).toString("base64");

function repeat(unit, byteLength) {
  const unitBytes = Buffer.byteLength(unit, "utf8");
  if (byteLength % unitBytes !== 0) {
    throw new Error(`'${unit}' is ${unitBytes} UTF-8 bytes and does not divide ${byteLength}.`);
  }
  return unit.repeat(byteLength / unitBytes);
}

function ramp(length) {
  const bytes = new Uint8Array(length);
  for (let index = 0; index < length; index += 1) bytes[index] = index % 251;
  return bytes;
}

const frames = [
  {
    label: "ts reliable-ordered, small payload",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 21n, 22n, "reliable-ordered", Uint8Array.from([0, 1, 254, 255])),
  },
  {
    label: "ts latest-only, small payload",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 23n, 24n, "latest-only", Uint8Array.from([0x7f, 0x80])),
  },
  {
    label: "ts unreliable, small payload",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 25n, 26n, "unreliable", Uint8Array.from([0xa5])),
  },
  {
    label: "ts empty payload",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 0n, 0n, "latest-only", new Uint8Array(0)),
  },
  {
    label: "ts multi-byte identities",
    frame: frame(
      "ts.z\u00f8ne-\u2202", "cultmesh.realtime.\u00b51", "ts-b\u00f6dy-\ud83e\uddca", 27n, 28n, "unreliable",
      new TextEncoder().encode("ts-payload-\u2202"),
    ),
  },
  {
    label: "ts epoch and sequence at long.MaxValue",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 2n ** 63n - 1n, 2n ** 63n - 1n, "reliable-ordered", Uint8Array.from([7])),
  },
  {
    label: "ts identities at exactly 65535 UTF-8 bytes",
    frame: frame(
      repeat(WIDE_CHANNEL_UNIT, WIDE_IDENTITY_BYTES), repeat(WIDE_SCHEMA_UNIT, WIDE_IDENTITY_BYTES),
      repeat(WIDE_BODY_UNIT, WIDE_IDENTITY_BYTES), 29n, 30n, "latest-only", Uint8Array.from([0x33, 0x44]),
    ),
    identityRepeats: [WIDE_CHANNEL_UNIT, WIDE_SCHEMA_UNIT, WIDE_BODY_UNIT],
  },
  {
    label: "ts 64 MiB payload",
    frame: frame("ts.zone-a", "cultmesh.realtime.v1", "ts-body-1", 31n, 32n, "unreliable", ramp(CULTMESH_REALTIME_MAX_PAYLOAD_BYTES)),
    rampPayload: true,
  },
];

function frame(channelId, schemaId, bodyId, producerEpoch, sequence, delivery, payload) {
  return { channelId, schemaId, bodyId, producerEpoch, sequence, delivery, payload };
}

const good = encodeRealtimeFrame(frames[0].frame);
const spoil = mutate => { const copy = Uint8Array.from(good); mutate(copy); return copy; };
const malformed = [
  { label: "ts truncated header", bytes: good.subarray(0, 36) },
  { label: "ts wrong magic", bytes: spoil(bytes => { bytes[2] ^= 0xff; }) },
  { label: "ts wire version 2", bytes: spoil(bytes => { bytes[35] = 2; }) },
  { label: "ts header size 38", bytes: spoil(bytes => { bytes[31] = 38; }) },
  { label: "ts delivery 3", bytes: spoil(bytes => { bytes[4] = 3; }) },
  { label: "ts delivery 128", bytes: spoil(bytes => { bytes[4] = 128; }) },
  { label: "ts payload length -1", bytes: spoil(bytes => { bytes[30] = 0x80; }) },
  { label: "ts length sum off by one", bytes: Uint8Array.from([...good, 0]) },
];

const recorded = frames.map(entry => {
  const encoded = encodeRealtimeFrame(entry.frame);
  // A file the TypeScript codec cannot read back is not evidence of anything.
  const roundTrip = decodeRealtimeFrame(encoded);
  for (const key of ["channelId", "schemaId", "bodyId", "producerEpoch", "sequence", "delivery"]) {
    if (roundTrip[key] !== entry.frame[key]) throw new Error(`${entry.label}: ${key} did not round-trip`);
  }
  if (Buffer.compare(Buffer.from(roundTrip.payload), Buffer.from(entry.frame.payload)) !== 0) {
    throw new Error(`${entry.label}: payload did not round-trip`);
  }
  const identity = (value, unit) =>
    (unit === undefined ? value : { repeat: unit, byteLength: WIDE_IDENTITY_BYTES });
  const [channelUnit, schemaUnit, bodyUnit] = entry.identityRepeats ?? [];
  return {
    label: entry.label,
    fields: {
      channelId: identity(entry.frame.channelId, channelUnit),
      schemaId: identity(entry.frame.schemaId, schemaUnit),
      bodyId: identity(entry.frame.bodyId, bodyUnit),
      // JSON has one number type and these do not fit it; the C# reader takes
      // them as `long`, so they are written as JSON integers, not strings.
      producerEpoch: entry.frame.producerEpoch,
      sequence: entry.frame.sequence,
      delivery: entry.frame.delivery,
      payload: entry.rampPayload ? { ramp: entry.frame.payload.byteLength } : base64(entry.frame.payload),
    },
    encoding: encoded.byteLength > INLINE_LIMIT ? null : base64(encoded),
    encodedLength: encoded.byteLength,
    sha256OfEncoding: sha256(encoded),
  };
});

const refusals = malformed.map(entry => {
  let message = "<no refusal>";
  try {
    decodeRealtimeFrame(entry.bytes);
  } catch (error) {
    message = error.message;
  }
  if (message === "<no refusal>") throw new Error(`${entry.label} was not refused`);
  return { label: entry.label, encoding: base64(entry.bytes), message };
});

// `JSON.stringify` refuses a bigint, and the two i64 fields must land in the
// file as JSON integers for the C# reader. They are written through a placeholder
// and spliced back, rather than quoted as strings.
const bigints = new Map();
const text = JSON.stringify({ frames: recorded, malformed: refusals }, (_key, value) => {
  if (typeof value !== "bigint") return value;
  const token = `@@bigint:${bigints.size}@@`;
  bigints.set(token, value.toString());
  return token;
}, 2);
writeFileSync(outputPath, `${[...bigints].reduce((carry, [token, digits]) => carry.replace(`"${token}"`, digits), text)}\n`);
console.log(`wrote ${outputPath}`);
