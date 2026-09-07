// Cross-fork conformance for `gamecult.runtime_presence_health.v2`.
//
// The vectors below were produced by `cultnet-rs`, not written by hand:
//
//   cargo run --example runtime_presence_vector -p cultnet-rs
//
// That matters. A fixture authored for both sides at once agrees with itself
// and proves nothing; the contract is only real if one fork emits the bytes and
// the other reproduces them. Regenerate these vectors from the Rust example if
// the record ever changes, and never by editing them to match this code.

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

import {
  RUNTIME_PRESENCE_FIELD_COUNT,
  encodeRuntimePresenceHealth,
  runtimePresenceProofPayload,
  validateRuntimePresence,
  type RuntimePresenceHealth,
} from "../src/runtime-presence-health";

// Read from the source tree, not a copy beside the compiled test: the vectors
// are generated output and must have exactly one home.
const VECTORS = JSON.parse(
  readFileSync(
    join(__dirname, "..", "..", "test", "fixtures", "runtime-presence-vectors.json"),
    "utf8",
  ),
) as { proof: string; signed: string };

/** The same statement the Rust example builds. */
function referencePresence(): RuntimePresenceHealth {
  return {
    target: "heimdall",
    expectedProjectionSha256: `sha256-${"a".repeat(64)}`,
    planId: `sha256-${"b".repeat(64)}`,
    incarnationId: "service-incarnation-1",
    sealedReleaseId: `sha256-${"c".repeat(64)}`,
    activationWitnessSha256: `sha256-${"d".repeat(64)}`,
    stateSchemaGeneration: "v1",
    stateContractSha256: `sha256-${"e".repeat(64)}`,
    runtimeId: "heimdall-yggdrasil",
    runtimeInstanceId: `sha256-${"f".repeat(64)}`,
    boundEndpoint: "http://127.0.0.1:14101",
    capabilities: [
      {
        capability: "heimdall.access",
        schema: "heimdall.access.v1",
        compatibility: "v1",
        capacity: 2,
      },
      { capability: "zeta.runtime", schema: "zeta.runtime.v1", compatibility: "v1", capacity: 1 },
    ],
    healthContract: "heimdall.cultnet-rudp-provider-health",
    state: "warming",
    detail: "Heimdall candidate warming",
    writeLeaseSha256: null,
    signerIdentityId: "1".repeat(64),
    publisherSequence: 7,
    observedAtUnixMillis: 1_757_000_000_000,
    activationSignerIdentityId: "2".repeat(64),
  };
}

function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("hex");
}

test("the proof payload is byte-identical to the one cultnet-rs signs", () => {
  assert.equal(hex(runtimePresenceProofPayload(referencePresence())), VECTORS.proof);
});

test("the signed record carries both proofs in their own slots", () => {
  const signed = encodeRuntimePresenceHealth(
    referencePresence(),
    new Uint8Array(64).fill(0x11),
    new Uint8Array(64).fill(0x22),
  );
  // Byte-identical to the fully signed record cultnet-rs emits, so the proofs
  // land in their own slots rather than being appended.
  assert.equal(hex(signed), VECTORS.signed);
});

test("capabilities are ordered canonically regardless of caller order", () => {
  const presence = referencePresence();
  const reversed = { ...presence, capabilities: [...presence.capabilities].reverse() };
  assert.equal(
    hex(runtimePresenceProofPayload(reversed)),
    hex(runtimePresenceProofPayload(presence)),
  );
});

test("the tuple is always the full contract width", () => {
  assert.equal(RUNTIME_PRESENCE_FIELD_COUNT, 24);
});

test("statements the contract forbids are refused before they are signed", () => {
  const warmingWithLease = {
    ...referencePresence(),
    writeLeaseSha256: `sha256-${"0".repeat(64)}`,
  };
  assert.throws(() => validateRuntimePresence(warmingWithLease), /cannot claim a process write/);

  const statelessWithLease = {
    ...referencePresence(),
    state: "active" as const,
    stateSchemaGeneration: null,
    stateContractSha256: null,
    writeLeaseSha256: `sha256-${"0".repeat(64)}`,
  };
  assert.throws(() => validateRuntimePresence(statelessWithLease), /stateless runtime presence/);

  assert.throws(
    () => validateRuntimePresence({ ...referencePresence(), publisherSequence: 0 }),
    /publisher sequence/,
  );

  const partialLineage = { ...referencePresence(), stateContractSha256: null };
  assert.throws(() => validateRuntimePresence(partialLineage), /state lineage is partial/);
});
