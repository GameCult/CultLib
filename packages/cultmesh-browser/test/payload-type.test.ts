import assert from "node:assert/strict";
import test from "node:test";

import { documentPayloadBytes, documentPayloadFromBytes, type DocumentPayload } from "@gamecult/cultcache-ts";
import { decodeWireValue } from "cultnet-ts/wire";
import type { CultNetRawDocumentRecord } from "cultnet-ts/contracts";

import { decodeCultNetPayload } from "../src/index.js";

// The rule: a document payload is read only through its definition. A CultNet record's payload is the opaque
// DocumentPayload, so tsc compiles this file only if each line carrying an expect-error directive is an error. The function is
// never called.
export function refusedReaders(record: CultNetRawDocumentRecord): void {
  // @ts-expect-error the wire value decoder is for frames and values, not for a document's payload
  decodeWireValue(record.payload);
  // @ts-expect-error a record's payload is not assignable to bytes
  const bytes: Uint8Array = record.payload;
  // @ts-expect-error the browser reader needs the definition that says how the bytes are laid out
  decodeCultNetPayload(record);
  // @ts-expect-error bytes are not a payload until they enter by name
  const forged: DocumentPayload = new Uint8Array();
  void [bytes, forged];
}

test("a record's payload is the same bytes it entered with by name", () => {
  const bytes = Uint8Array.from([0x90]);
  assert.equal(documentPayloadBytes(documentPayloadFromBytes(bytes)), bytes);
});
