import assert from "node:assert/strict";
import test from "node:test";

import { documentPayloadBytes, documentPayloadFromBytes, type DocumentPayload } from "@gamecult/cultcache-ts";
import { decodeWireValue } from "cultnet-ts/wire";
import type { CultNetRawDocumentRecord } from "cultnet-ts";

import { decodeRealtimeFrame, encodeRealtimeFrame, type CultMeshRealtimeFrame } from "../src/realtime-wire";

// The rule: a document payload is read only through its definition. Every payload this package touches (a CultNet
// record's, a realtime frame's) is the opaque DocumentPayload, so tsc compiles this file only if each line carrying
// an expect-error directive is an error. The function is never called.
export function refusedReaders(record: CultNetRawDocumentRecord, frame: CultMeshRealtimeFrame): void {
  // @ts-expect-error the wire value decoder is for frames and values, not for a document's payload
  decodeWireValue(record.payload);
  // @ts-expect-error a frame's payload is not assignable to bytes
  const bytes: Uint8Array = frame.payload;
  // @ts-expect-error nor may a copy of it be taken as bytes
  new Uint8Array(record.payload);
  // @ts-expect-error bytes are not a payload until they enter by name
  const forged: DocumentPayload = new Uint8Array();
  void [bytes, forged];
}

test("a realtime frame carries its payload as the same bytes it was given by name", () => {
  const bytes = Uint8Array.from([1, 2, 3]);
  const frame: CultMeshRealtimeFrame = {
    channelId: "c", schemaId: "s", bodyId: "b", producerEpoch: 1n, sequence: 1n, delivery: "reliable-ordered",
    payload: documentPayloadFromBytes(bytes),
  };
  assert.deepEqual(documentPayloadBytes(decodeRealtimeFrame(encodeRealtimeFrame(frame)).payload), bytes);
});
