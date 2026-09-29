import test from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import { decode } from "@msgpack/msgpack";

import {
  signedDaemonHealthPayload,
  unsignedDaemonHealthPayload,
  type SignedDaemonHealthPublisher,
} from "../src/signed-daemon-health";

function publisher(): SignedDaemonHealthPublisher {
  return {
    daemonId: "heimdall",
    endpoint: { host: "127.0.0.1", port: 1 },
    healthContract: "contract",
    publisherIncarnationId: "incarnation",
    publisherSequence: 0,
    sourceRuntimeId: "runtime",
    contract: {
      connectionId: 1,
      signedSchemaId: "signed.schema",
      unsignedSchemaId: "unsigned.schema",
    } as SignedDaemonHealthPublisher["contract"],
    privateKey: crypto.generateKeyPairSync("ed25519").privateKey,
    signerIdentityId: "signer",
  };
}

const observedAt = "2026-01-01T00:00:00.000Z";
const detail = "é".repeat(300);

function assertBounded(text: unknown) {
  assert.equal(typeof text, "string");
  const bytes = Buffer.from(text as string, "utf8");
  assert.ok(bytes.length <= 512, `detail is ${bytes.length} bytes`);
  assert.equal(bytes.toString("utf8"), text);
}

test("unsigned daemon health detail is bounded by UTF-8 bytes", () => {
  const fields = decode(unsignedDaemonHealthPayload(publisher(), { state: "ok", detail }, observedAt)) as unknown[];
  assertBounded(fields[2]);
});

test("signed daemon health detail is bounded by UTF-8 bytes", () => {
  const { statement } = signedDaemonHealthPayload(publisher(), { state: "ok", detail }, observedAt);
  assertBounded(statement[5]);
});
