import { mock, test } from "node:test";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { decode, encode } from "@msgpack/msgpack";

import {
  loadIdunnRuntimeAuthorityFromEnvironment,
  signIdunnRuntimePresence,
} from "../src/idunn-runtime-authority";
import {
  runtimePresenceActivationSigningMessage,
  runtimePresenceProofPayload,
  runtimePresenceProviderSigningMessage,
} from "../src/runtime-presence-health";

const providerIdentityContext = "gamecult-provider-health-identity-v1";
const providerIdDomain = Buffer.from("gamecult.provider-health.identity.v1\0", "utf8");
const activationIdDomain = Buffer.from("idunn.runtime-activation.id.v1\0", "utf8");

test("opens Idunn's machine-bound identity and signs the exact Expected incarnation twice", (context) => {
  const directory = mkdtempSync(path.join(os.tmpdir(), "cultnet-runtime-authority-"));
  context.after(() => rmSync(directory, { recursive: true, force: true }));

  const machineId = "1234567890abcdef1234567890abcdef";
  const providerSeed = Buffer.alloc(32, 0x31);
  const activationSeed = Buffer.alloc(32, 0x72);
  const providerKey = privateKeyFromSeed(providerSeed);
  const activationKey = privateKeyFromSeed(activationSeed);
  const providerPublic = rawPublic(crypto.createPublicKey(providerKey));
  const activationPublic = rawPublic(crypto.createPublicKey(activationKey));
  const providerIdentity = sha256Hex(Buffer.concat([providerIdDomain, providerPublic]));
  const activationIdentity = sha256Hex(Buffer.concat([activationIdDomain, activationPublic]));
  const target = "streampixels-service";
  const contract = "streampixels-service.cultnet-rudp-service-health";
  const candidate = "http://127.0.0.1:18831";
  const expectedValues = [
    "idunn.expected_incarnation.v2", target, `sha256-${"1".repeat(64)}`, "incarnation-1",
    `sha256-${"2".repeat(64)}`, "https://github.com/GameCult/StreamPixels.git", "a".repeat(40),
    `sha256-${"3".repeat(64)}`, "streampixels-service-yggdrasil", providerIdentity, contract,
    `sha256-${"4".repeat(64)}`, "service-boundary-v1", `sha256-${"5".repeat(64)}`, true,
    ["streampixels-service-http", "http", "http://127.0.0.1:8831", candidate],
    [["streampixels.service.api", "streampixels.api.v1", "v1", 1]],
    [["shared-infrastructure", "odin.verse-rendezvous", "odin.verse-topology.v1", "v1", 1, "before-promotion", null, null, null, "rudp://127.0.0.1:17871"]],
  ];
  const expectedBytes = Buffer.from(encode(expectedValues));
  const expectedSha = `sha256-${sha256Hex(expectedBytes)}`;
  const activationValues = [
    "idunn.runtime_activation.v2", expectedSha, "streampixels-service-yggdrasil",
    `sha256-${"6".repeat(64)}`, activationIdentity, activationPublic, Date.now(),
    "7".repeat(64), "ed25519", new Uint8Array(64).fill(0x17),
  ];
  const bundle = path.join(directory, "bundle");
  fs.mkdirSync(bundle);
  writeFileSync(path.join(bundle, "expected.cc"), cultCacheStore(
    "idunn.expected_incarnation", "idunn.expected_incarnation.v2", "expected", expectedBytes,
  ));
  writeFileSync(path.join(bundle, "activation.cc"), cultCacheStore(
    "idunn.runtime_activation", "idunn.runtime_activation.v2", "activation", Buffer.from(encode(activationValues)),
  ));

  const binding = `${providerIdentityContext}:machine-id-sha256:${sha256Hex(Buffer.from(machineId))}`;
  const mask = crypto.createHash("sha256").update(Buffer.concat([
    Buffer.from("gamecult-linux-service-seed-v1\0", "utf8"),
    Buffer.from(providerIdentityContext, "utf8"),
    Buffer.from(binding, "utf8"),
  ])).digest();
  const protectedSeed = Buffer.from(providerSeed.map((value, index) => value ^ mask[index]!));
  const providerCredential = cultCacheStore(
    "gamecult.provider_health_identity", "gamecult.provider_health_identity.private.v1",
    "gamecult-provider-health-identity", Buffer.from(encode([
      "gamecult.provider_health_identity.private.v1", providerIdentity, providerPublic, protectedSeed,
      "linux_file_mode_machine_id_binding", binding, "v1", "os_installation_file_bound_cloneable_baseline",
      "2026-01-01T00:00:00Z", new Uint8Array(32).fill(0x2a),
    ])),
  );

  const originalEnvironment = { ...process.env };
  context.after(() => {
    for (const key of Object.keys(process.env)) if (!(key in originalEnvironment)) delete process.env[key];
    Object.assign(process.env, originalEnvironment);
  });
  process.env.GAMECULT_IDUNN_RUNTIME_BUNDLE = bundle;
  process.env.GAMECULT_IDUNN_CANDIDATE_BIND = candidate;
  process.env.GAMECULT_IDUNN_PROCESS_WRITE_LEASE = path.join(directory, "lease.cc");
  // systemd reports the host PID while Node inside PrivatePIDs sees PID 1.
  const listenPid = String(process.pid + 1000);
  process.env.LISTEN_PID = listenPid;
  process.env.LISTEN_FDS = "2";
  process.env.LISTEN_FDNAMES = "gamecult-idunn-runtime-activation-key:gamecult-runtime-presence-identity";
  const realReadFileSync = fs.readFileSync.bind(fs);
  mock.method(fs, "readFileSync", (filePath: fs.PathOrFileDescriptor, ...args: unknown[]) => {
    if (filePath === "/proc/self/status") return `Name:\tnode\nNSpid:\t${listenPid}\t1\n`;
    if (filePath === "/proc/self/fd/3") return activationSeed;
    if (filePath === "/proc/self/fd/4") return providerCredential;
    if (filePath === "/etc/machine-id") return machineId;
    return realReadFileSync(filePath as never, ...args as never[]);
  });

  const authority = loadIdunnRuntimeAuthorityFromEnvironment(target, contract, "127.0.0.1:17871");
  const signed = signIdunnRuntimePresence(authority, {
    capabilities: [{ capability: "streampixels.service.api", schema: "streampixels.api.v1", compatibility: "v1", capacity: 1 }],
    state: "warming",
    detail: "starting",
    writeLeaseSha256: null,
    publisherSequence: 1,
    observedAtUnixMillis: Date.now(),
  });
  const proof = runtimePresenceProofPayload(signed.presence);
  assert.equal((decode(expectedBytes) as unknown[]).length, 18);
  assert.equal(signed.presence.expectedProjectionSha256, expectedSha);
  const signedRecord = decode(signed.payload) as unknown[];
  assert.equal(crypto.verify(null, runtimePresenceProviderSigningMessage(proof), crypto.createPublicKey(providerKey), Buffer.from(signedRecord[21] as Uint8Array)), true);
  assert.equal(crypto.verify(null, runtimePresenceActivationSigningMessage(proof), crypto.createPublicKey(activationKey), Buffer.from(signedRecord[23] as Uint8Array)), true);
});

function cultCacheStore(type: string, schemaId: string, key: string, payload: Uint8Array): Buffer {
  return Buffer.from(encode([
    "cultcache.store.v1",
    [[schemaId, type, `${schemaId}.v1`, schemaId, "{}", [schemaId], []]],
    [[key, schemaId, "2026-01-01T00:00:00Z", payload]],
  ]));
}

function privateKeyFromSeed(seed: Uint8Array): crypto.KeyObject {
  return crypto.createPrivateKey({
    key: Buffer.concat([Buffer.from("302e020100300506032b657004220420", "hex"), Buffer.from(seed)]),
    format: "der",
    type: "pkcs8",
  });
}

function rawPublic(key: crypto.KeyObject): Buffer {
  const der = key.export({ type: "spki", format: "der" }) as Buffer;
  return Buffer.from(der.subarray(der.length - 32));
}

function sha256Hex(bytes: Uint8Array): string {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}
