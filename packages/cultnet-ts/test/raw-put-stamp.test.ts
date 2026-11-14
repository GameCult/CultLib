// A peer's raw put is stored under the stamp it carries; one that is no stamp is refused and nothing is minted or stored
// (variants:ruling:raw-put-without-stamp-refused). The registry is the node-level owner: every row of the shared vector.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { decode } from "@msgpack/msgpack";
import { z } from "zod";
import { CultCache, defineDocumentType, encodeDocumentPayload } from "@gamecult/cultcache-ts";
import { CultNetDocumentRegistry, defineCultNetDocumentBinding, parseCultNetMessage, type CultNetMessage } from "../src";

const VECTOR = join(__dirname, "..", "..", "..", "..", "tests", "vectors", "stored-at-stamp", "stamps.msgpack");
const note = defineDocumentType({
  type: "tests.stamped_note",
  schemaId: "tests.stamped_note.v0",
  schema: z.object({ noteId: z.string(), text: z.string() }),
  name: "noteId",
});
const registry = new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: note })]);

function putOf(key: string, storedAt: unknown): CultNetMessage {
  const payload = encodeDocumentPayload(note, { noteId: key, text: "peer" });
  const document: Record<string, unknown> = { schemaId: note.schemaId, recordKey: key, payloadEncoding: "messagepack", payload };
  if (storedAt !== undefined) document.storedAt = storedAt;
  return { schemaVersion: "cultnet.document_put_raw.v0", messageId: `put-${key}`, document } as unknown as CultNetMessage;
}

test("the registry stores a raw put only under a stamp, for every row of the vector and for a missing or mistyped one", async () => {
  const rows = decode(readFileSync(VECTOR)) as [string, boolean][];
  const cases: [string, unknown, boolean][] = [...rows.map(([text, parses]): [string, unknown, boolean] => [JSON.stringify(text), text, parses]),
    ["missing", undefined, false], ["number", 1_700_000_000_000, false], ["null", null, false]];
  const cache = CultCache.builder().withDocumentType(note).build();
  for (const [label, storedAt, parses] of cases) {
    const message = putOf("k", storedAt);
    // The parser lets a put through to its judges: the server and the registry refuse it, naming the document.
    parseCultNetMessage(message, "cultnet.schema.v0");
    const before = cache.getEnvelope(note, "k");
    if (parses) {
      await registry.applyRawDocumentPutMessage(cache, message as never);
      assert.equal(cache.getRequiredEnvelope(note, "k").storedAt.length > 0, true, label);
    } else {
      await assert.rejects(registry.applyRawDocumentPutMessage(cache, message as never), /storedAt stamp/, label);
      assert.deepEqual(cache.getEnvelope(note, "k"), before, `${label}: nothing minted or stored`);
    }
  }
  // The refused puts left the store writable.
  await registry.applyRawDocumentPutMessage(cache, putOf("after", "2026-11-07T00:00:00Z") as never);
  assert.equal(cache.getRequired(note, "after").text, "peer");
});

test("a refused put does not serve either: createRawSnapshotResponseForPut refuses it too", () => {
  assert.throws(() => registry.createRawSnapshotResponseForPut(putOf("k", "yesterday") as never, "m"), /storedAt stamp/);
});
