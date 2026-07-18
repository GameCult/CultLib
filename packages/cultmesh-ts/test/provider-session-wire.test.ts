import test from "node:test";
import assert from "node:assert/strict";

import {
  decodeProviderConnectEvidence,
  encodeProviderConnectEvidence,
  encodeProviderSessionPayload,
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
