import test from "node:test";
import assert from "node:assert/strict";

import { encodeProviderSessionPayload } from "../src/provider-session-wire";

const REGISTRATION_GOLDEN = "hqpwcm92aWRlcklkqGFldGhlcmlhsXNlcnZpY2VJbnN0YW5jZUlkq2FldGhlcmlhLTQyqmVuZHBvaW50SWSvYWV0aGVyaWEtcHVibGljp3ZlcnNlSWSmcHVibGljuHJlcXVlc3RlZExlYXNlRHVyYXRpb25Nc811MLBhdXRob3JpdHlMZWFzZUlkq2F1dGhvcml0eS03";

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
