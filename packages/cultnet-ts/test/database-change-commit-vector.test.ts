import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import Ajv2020 from "ajv/dist/2020.js";
import { decodeMulti } from "@msgpack/msgpack";

import { cultNetSchemas, parseCultNetMessage } from "../src/index";

// tests/vectors/database-change-commit/two-change-commit.msgpack is written only by the C# reference: two removed
// database_change_raw messages of one commit, every key present, nil where unset (document included).
const vector = join(__dirname, "..", "..", "..", "..", "tests", "vectors", "database-change-commit", "two-change-commit.msgpack");
const changes = () => [...decodeMulti(readFileSync(vector))] as Record<string, unknown>[];

test("a two-change commit written by C# decodes with its commitId, commitCount and changes", () => {
  const messages = changes().map((message) => parseCultNetMessage(structuredClone(message)));
  assert.equal(messages.length, 2);
  for (const message of messages) {
    assert.equal(message.schemaVersion, "cultnet.database_change_raw.v0");
    assert.equal((message as { commitId?: unknown }).commitId, "9f1c0f0e4b7a4c1f9d2e");
    assert.equal((message as { commitCount?: unknown }).commitCount, 2);
    assert.equal((message as { changeKind?: unknown }).changeKind, "removed");
    assert.equal((message as { subscriptionId?: unknown }).subscriptionId, "sub-1");
    assert.equal((message as { schemaId?: unknown }).schemaId, "schema-note");
  }
  assert.deepEqual(messages.map((message) => (message as { recordKey?: unknown }).recordKey), ["note:a", "note:b"]);
});

test("the database_change_raw schema accepts the C# vector's removed changes (document: nil) and still refuses an added change without a document", () => {
  const ajv = new Ajv2020({ allErrors: true, strict: false });
  ajv.addSchema(cultNetSchemas.rawDocumentRecordSchema);
  const validate = ajv.compile(cultNetSchemas.databaseChangeRawSchema);
  for (const change of changes()) {
    assert.equal(change.document, null, "the C# writer wrote the key, nil");
    assert.equal(validate(change), true, JSON.stringify(validate.errors));
    assert.equal(validate({ ...change, changeKind: "added" }), false, "an added change needs a document");
  }
});
