import assert from "node:assert/strict";
import test from "node:test";

import {
  decodeDocumentPayload,
  defineDocumentType,
  documentPayloadBytes,
  documentPayloadFromBytes,
  type DocumentPayload,
} from "@gamecult/cultcache-ts";
import { decodeWireValue } from "cultnet-ts/wire";
import type { CultNetRawDocumentRecord } from "cultnet-ts";

import type { CultMeshRudpDocumentPut } from "../src/index";
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

// The one seam where a DocumentPayload reaches a consumer: a put for a schema the registry holds no definition for. The
// owner read of the undecoded branch needs no cast, and the decoded branch is not a DocumentPayload.
const noteDefinition = defineDocumentType({ type: "t.note", schemaId: "t.note.v0", schema: { parse: (value: unknown) => value } });
export function readPut(put: CultMeshRudpDocumentPut): unknown {
  if (!put.decoded) return decodeDocumentPayload(noteDefinition, put.payload);
  // @ts-expect-error a decoded put's payload is the document, not a DocumentPayload
  decodeDocumentPayload(noteDefinition, put.payload);
  return put.payload;
}

test("a realtime frame carries its payload as the same bytes it was given by name", () => {
  const bytes = Uint8Array.from([1, 2, 3]);
  const frame: CultMeshRealtimeFrame = {
    channelId: "c", schemaId: "s", bodyId: "b", producerEpoch: 1n, sequence: 1n, delivery: "reliable-ordered",
    payload: documentPayloadFromBytes(bytes),
  };
  assert.deepEqual(documentPayloadBytes(decodeRealtimeFrame(encodeRealtimeFrame(frame)).payload), bytes);
});
