import test from "node:test";
import assert from "node:assert/strict";

import { documentPayloadBytes, documentPayloadFromBytes, encodeDocumentPayload, type DocumentPayload } from "@gamecult/cultcache-ts";
import { encodeWireValue } from "cultnet-ts/wire";

import {
  cultMeshProviderSessionSchemas,
  decodeProviderCommandDocument,
  decodeProviderConnectEvidence,
  encodeProviderConnectEvidence,
  encodeProviderSessionPayload,
  providerCommandDocument,
} from "../src/provider-session-wire";

const REGISTRATION_GOLDEN = "hqpwcm92aWRlcklkqGFldGhlcmlhsXNlcnZpY2VJbnN0YW5jZUlkq2FldGhlcmlhLTQyqmVuZHBvaW50SWSvYWV0aGVyaWEtcHVibGljp3ZlcnNlSWSmcHVibGljuHJlcXVlc3RlZExlYXNlRHVyYXRpb25Nc811MLBhdXRob3JpdHlMZWFzZUlkq2F1dGhvcml0eS03";
const CONNECT_EVIDENCE_GOLDEN = "gq9jbGllbnRTZXNzaW9uSWSyYWV0aGVyaWEtY2xpZW50LTQyrHNlc3Npb25Ub2tlbrJvZGluLXNlc3Npb24tdG9rZW4=";
const TOKENLESS_CONNECT_EVIDENCE_GOLDEN = "gq9jbGllbnRTZXNzaW9uSWSyYWV0aGVyaWEtY2xpZW50LTQyrHNlc3Npb25Ub2tlbsA=";

test("provider registration matches the shared C# MessagePack golden payload", () => {
  assert.equal(encodeProviderSessionPayload({
    providerId: "aetheria",
    serviceInstanceId: "aetheria-42",
    endpointId: "aetheria-public",
    verseId: "public",
    requestedLeaseDurationMs: 30_000,
    authorityLeaseId: "authority-7",
  }), REGISTRATION_GOLDEN);
});

test("provider Connect evidence keeps transport generation separate from authority", () => {
  const evidence = {
    clientSessionId: "aetheria-client-42",
    sessionToken: "odin-session-token",
  };
  const encoded = encodeProviderConnectEvidence(evidence);
  assert.equal(Buffer.from(encoded).toString("base64"), CONNECT_EVIDENCE_GOLDEN);
  assert.deepEqual(
    decodeProviderConnectEvidence(encoded),
    evidence,
  );
  assert.equal(
    Buffer.from(encodeProviderConnectEvidence({
      clientSessionId: "aetheria-client-42",
      sessionToken: null,
    })).toString("base64"),
    TOKENLESS_CONNECT_EVIDENCE_GOLDEN,
  );
});

test("a provider command is a CultCache document: written and read by its definition, and a refusal names no value", () => {
  const command = { commandId: "c1", commandKind: "k", providerId: "p", serviceInstanceId: "s", payload: { n: 1 } };
  const payload = encodeDocumentPayload(providerCommandDocument, command);
  assert.deepEqual(documentPayloadBytes(payload), encodeWireValue(command), "the wire bytes are the plain map they always were");
  const record = (bytes: DocumentPayload) => ({
    schemaId: cultMeshProviderSessionSchemas.command, recordKey: "p/s/c1", storedAt: "2026-10-11T00:00:00Z",
    payloadEncoding: "messagepack" as const, payload: bytes,
  });
  assert.deepEqual(decodeProviderCommandDocument(record(payload)), command);

  const refused = documentPayloadFromBytes(encodeWireValue({ ...command, providerId: "canary-provider-5d2e", commandKind: "" }));
  assert.throws(
    () => decodeProviderCommandDocument(record(refused)),
    (error: Error) => /commandKind/.test(error.message) && !error.message.includes("canary-provider-5d2e"),
  );
  assert.throws(() => encodeDocumentPayload(providerCommandDocument, { ...command, commandId: " " }), /commandId/);
});

test("a provider command that is not a map, or lacks a required field, is refused naming the field and none of its values", () => {
  const command = { commandId: "c1", commandKind: "k", providerId: "p", serviceInstanceId: "s", payload: 1 };
  const record = (value: unknown) => ({
    schemaId: cultMeshProviderSessionSchemas.command, recordKey: "p/s/c1", storedAt: "2026-10-11T00:00:00Z",
    payloadEncoding: "messagepack" as const, payload: documentPayloadFromBytes(encodeWireValue(value)),
  });
  for (const field of ["commandId", "commandKind", "providerId", "serviceInstanceId"] as const) {
    assert.throws(
      () => decodeProviderCommandDocument(record({ ...command, [field]: "" })),
      (error: Error) => error.message === `CultMesh provider-session ${field} must be non-empty.`,
    );
  }
  assert.throws(
    () => decodeProviderCommandDocument(record(["canary-not-a-map"])),
    (error: Error) => error.message === "CultMesh provider command must be a MessagePack map.",
  );
});
