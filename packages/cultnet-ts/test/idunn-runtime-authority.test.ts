import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import dgram from "node:dgram";
import fs from "node:fs";
import path from "node:path";
import { decode, encode } from "@msgpack/msgpack";

import {
  createIdunnRuntimePresencePublisher,
  createIdunnRuntimeSigner,
  loadIdunnRuntimeAuthorityFromEnvironment,
  type IdunnRuntimeAuthority,
} from "../src";
import { systemdListenPidMatches } from "../src/idunn-runtime-authority";
import type { CultNetSnapshotRequestMessage } from "../src/contracts";
import { CultNetRudpSession, decodeRudpPacket, encodeRudpPacket } from "../src/rudp";
import { RUNTIME_PRESENCE_SLOT } from "../src/runtime-presence-health";

// Every input below was written by the Rust owner; see fixtures/idunn-runtime/README.md.
// The only values this file pins are the ones the generator also pins.
const FIXTURES = path.join(__dirname, "..", "..", "test", "fixtures", "idunn-runtime");
const OBSERVED_AT = 1_790_000_000_000;
const CHALLENGE: CultNetSnapshotRequestMessage = {
  schemaVersion: "cultnet.snapshot_request.v0",
  messageId: "route-challenge-1",
};

type FixtureSet = "web" | "service" | "rudp-route" | "service-stateful";

function fixtureFile(set: FixtureSet, name: string): Buffer {
  return fs.readFileSync(path.join(FIXTURES, set, name));
}

/** The Expected fields the tests need, read from the Rust-written file rather than re-spelled. */
function expectedFacts(set: FixtureSet) {
  const store = decode(fixtureFile(set, "expected.cc")) as unknown[][][];
  const values = decode(store[2]![0]![3] as Uint8Array) as unknown[];
  const route = values[15] as string[];
  const capabilities = (values[16] as Array<[string, string, string, number]>).map(([capability, schema, compatibility, minimum]) => ({
    capability, schema, compatibility, capacity: minimum,
  }));
  const dependencies = values[17] as unknown[][];
  return {
    target: values[1] as string,
    healthContract: values[10] as string,
    candidate: route[3]!,
    capabilities,
    odinEndpoint: dependencies.map((dependency) => dependency[9] as string)[0],
  };
}

/** Points the process at one Rust-written bundle and its inherited descriptors, as Idunn's systemd unit would. */
function openAuthority(
  context: TestContext,
  set: FixtureSet,
  options: { machineId?: "etc" | "dbus"; lease?: string } = {},
): IdunnRuntimeAuthority {
  const facts = expectedFacts(set);
  const originalEnvironment = { ...process.env };
  context.after(() => {
    for (const key of Object.keys(process.env)) if (!(key in originalEnvironment)) delete process.env[key];
    Object.assign(process.env, originalEnvironment);
  });
  process.env.GAMECULT_IDUNN_RUNTIME_BUNDLE = path.join(FIXTURES, set);
  process.env.GAMECULT_IDUNN_CANDIDATE_BIND = facts.candidate;
  if (options.lease) process.env.GAMECULT_IDUNN_PROCESS_WRITE_LEASE = path.join(FIXTURES, set, options.lease);
  else delete process.env.GAMECULT_IDUNN_PROCESS_WRITE_LEASE;
  process.env.LISTEN_PID = String(process.pid);
  process.env.LISTEN_FDS = "2";
  process.env.LISTEN_FDNAMES = "gamecult-idunn-runtime-activation-key:gamecult-runtime-presence-identity";
  const machineId = fs.readFileSync(path.join(FIXTURES, "machine-id"), "utf8");
  const realReadFileSync = fs.readFileSync.bind(fs);
  context.mock.method(fs, "readFileSync", (filePath: fs.PathOrFileDescriptor, ...args: unknown[]) => {
    if (filePath === 3) return fixtureFile(set, "activation-key.seed");
    if (filePath === 4) return fixtureFile(set, "provider-identity.credential");
    if (filePath === "/etc/machine-id") {
      if (options.machineId === "dbus") throw Object.assign(new Error("ENOENT"), { code: "ENOENT" });
      return machineId;
    }
    if (filePath === "/var/lib/dbus/machine-id") return machineId;
    return realReadFileSync(filePath as never, ...args as never[]);
  });
  return loadIdunnRuntimeAuthorityFromEnvironment(facts.target, facts.healthContract);
}

function signerFor(context: TestContext, set: FixtureSet, capacityDelta = 0, lease?: string) {
  const authority = openAuthority(context, set, { lease });
  const capabilities = expectedFacts(set).capabilities.map((capability) => ({ ...capability, capacity: capability.capacity + capacityDelta }));
  return createIdunnRuntimeSigner({ authority, capabilities });
}

function freezeClock(context: TestContext): void {
  context.mock.method(Date, "now", () => OBSERVED_AT);
}

test("matches systemd's host PID for the private PID namespace init only", () => {
  assert.equal(systemdListenPidMatches(String(process.pid), process.pid), true);
  assert.equal(systemdListenPidMatches("1725346", 1), true);
  assert.equal(systemdListenPidMatches("1725346", 2), false);
  assert.equal(systemdListenPidMatches("not-a-pid", 1), false);
  assert.equal(systemdListenPidMatches(undefined, 1), false);
});

for (const set of ["web", "service", "rudp-route"] as const) {
  test(`opens the Rust-written ${set} bundle with no Odin endpoint, whatever route transport Rust admits`, (context) => {
    const authority = openAuthority(context, set);
    assert.equal(authority.expected.target, expectedFacts(set).target);
    assert.equal(authority.boundEndpoint, expectedFacts(set).candidate);
  });
}

test("a target that declares no Odin dependency opens and answers Idunn's challenge without one", (context) => {
  assert.equal(expectedFacts("web").odinEndpoint, undefined);
  const signer = signerFor(context, "web");
  assert.ok(signer.answerRouteObservation(CHALLENGE).payload.byteLength > 0);
});

test("finds the machine-id at the dbus path when /etc/machine-id is absent, as the Rust protector does", (context) => {
  assert.ok(openAuthority(context, "web", { machineId: "dbus" }));
});

test("rejects a credential file holding more than one envelope", (context) => {
  openAuthority(context, "web");
  const credential = decode(fixtureFile("web", "provider-identity.credential")) as unknown[][];
  const doubled = Buffer.from(encode([...credential, ...credential]));
  const realReadFileSync = fs.readFileSync;
  context.mock.method(fs, "readFileSync", (filePath: fs.PathOrFileDescriptor, ...args: unknown[]) =>
    filePath === 4 ? doubled : (realReadFileSync as (...all: unknown[]) => unknown)(filePath, ...args));
  assert.throws(
    () => loadIdunnRuntimeAuthorityFromEnvironment(expectedFacts("web").target, expectedFacts("web").healthContract),
    /exactly one envelope/,
  );
});

function presenceSlot(document: { payload: Uint8Array }, slot: number): unknown {
  return (decode(document.payload) as unknown[])[slot];
}

// Every committed vector is asserted, dead ones included. Stateless sets sign at sequence 1.
for (const set of ["web", "service", "rudp-route"] as const) {
  test(`${set}: a fresh signer answers warming, and after reportHealth('active') answers active, byte for byte as the Rust-verified vectors`, (context) => {
    freezeClock(context);
    assert.deepEqual(Buffer.from(signerFor(context, set).answerRouteObservation(CHALLENGE).payload), fixtureFile(set, "presence-warming.bin"));
    const active = signerFor(context, set);
    active.reportHealth("active");
    assert.deepEqual(Buffer.from(active.answerRouteObservation(CHALLENGE).payload), fixtureFile(set, "presence-active.bin"));
  });
}

test("a shortfall signs the vector Rust correlates to the typed capacity disagreement", (context) => {
  freezeClock(context);
  const signer = signerFor(context, "service", -1);
  signer.reportHealth("active");
  assert.deepEqual(Buffer.from(signer.answerRouteObservation(CHALLENGE).payload), fixtureFile("service", "presence-active-below-minimum.bin"));
});

test("the signer owns the reported health: a fresh signer is warming, reportHealth moves it, the sequence keeps counting", (context) => {
  freezeClock(context);
  const signer = signerFor(context, "web");
  const first = signer.answerRouteObservation(CHALLENGE);
  assert.equal(presenceSlot(first, RUNTIME_PRESENCE_SLOT.state), "warming");
  assert.equal(presenceSlot(signer.sign("detail").document, RUNTIME_PRESENCE_SLOT.state), "warming");
  signer.reportHealth("active");
  const third = signer.answerRouteObservation(CHALLENGE);
  assert.equal(presenceSlot(third, RUNTIME_PRESENCE_SLOT.state), "active");
  assert.equal(presenceSlot(third, RUNTIME_PRESENCE_SLOT.publisherSequence), 3);
});

test("answers locally: synchronously, with no socket opened", (context) => {
  const sockets = context.mock.method(dgram, "createSocket");
  const signer = signerFor(context, "web");
  const answer = signer.answerRouteObservation(CHALLENGE);
  assert.equal(typeof (answer as unknown as { then?: unknown }).then, "undefined");
  assert.equal(sockets.mock.callCount(), 0);
});

test("refuses a challenge that does not ask for this runtime's presence", (context) => {
  const signer = signerFor(context, "web");
  const cases: CultNetSnapshotRequestMessage[] = [
    { ...CHALLENGE, messageId: "" },
    { ...CHALLENGE, messageId: " padded" },
    { ...CHALLENGE, recordKeys: ["another-target"] },
    { ...CHALLENGE, schemaIds: ["another.schema"] },
    { ...CHALLENGE, shardId: "shard-1" },
  ];
  for (const request of cases) assert.throws(() => signer.answerRouteObservation(request), /route observation/);
});

async function startOdinPeer(context: TestContext, endpoint: string) {
  const peer = dgram.createSocket("udp4");
  const port = Number(endpoint.slice(endpoint.lastIndexOf(":") + 1));
  await new Promise<void>((resolve, reject) => {
    peer.once("error", reject);
    peer.bind(port, "127.0.0.1", resolve);
  });
  context.after(() => peer.close());
  const state = {
    reject: false,
    received: [] as Array<{ schemaVersion?: string; document: { payload: Uint8Array } }>,
    responseAck: undefined as number | undefined,
    failure: undefined as Error | undefined,
  };
  let serverSession: CultNetRudpSession | undefined;
  peer.on("message", (wire, remote) => {
    try {
      const packet = decodeRudpPacket(wire);
      if (packet.packetType === "connect") {
        serverSession = new CultNetRudpSession({ connectionId: packet.connectionId, initialSequence: 100 });
        peer.send(encodeRudpPacket(serverSession.acceptConnect(packet, Date.now())), remote.port, remote.address);
        return;
      }
      if (!serverSession) throw new Error("Publisher sent data before establishing RUDP.");
      const frame = serverSession.receive(packet, Date.now()).delivered.find((candidate) => candidate.channelId === "schema");
      if (!frame) return;
      state.received.push(decode(frame.payload) as never);
      const [response] = serverSession.sendMany("schema", encode(state.reject
        ? { schemaVersion: "cultnet.error.v0", error: "test admission denied", routingHint: null, code: null, details: null }
        : { schemaVersion: "cultnet.snapshot_response.v0", messageId: "ack", documents: [] }), {
        reliable: true,
        ordered: true,
        nowMs: Date.now(),
      });
      assert.ok(response);
      state.responseAck = response.ack;
      peer.send(encodeRudpPacket(response), remote.port, remote.address);
    } catch (error) {
      state.failure = error as Error;
    }
  });
  return state;
}

test("the Odin publisher publishes the signer's document to the endpoint Expected names and surfaces Odin's rejection", async (context) => {
  const endpoint = expectedFacts("service").odinEndpoint!;
  const peer = await startOdinPeer(context, endpoint);
  const signer = signerFor(context, "service");
  const publisher = createIdunnRuntimePresencePublisher({ signer, endpoint });

  await publisher.publish("accepted presence");
  assert.equal(peer.received[0]?.schemaVersion, "cultnet.document_put_raw.v0");
  const published = decode(peer.received[0]!.document.payload) as unknown[];
  assert.equal(published[RUNTIME_PRESENCE_SLOT.state], "warming");
  assert.equal(published[RUNTIME_PRESENCE_SLOT.publisherSequence], 1);
  assert.equal(peer.responseAck, 2, "the response data packet must acknowledge the publisher's reliable schema packet");

  peer.reject = true;
  signer.reportHealth("active");
  await assert.rejects(publisher.publish("denied presence"), /Odin rejected runtime presence: test admission denied/);
  assert.equal(peer.failure, undefined, peer.failure?.message);
});

test("the Odin publisher leaves a capability shortfall to the Rust correlation instead of refusing to publish", async (context) => {
  const endpoint = expectedFacts("service").odinEndpoint!;
  const peer = await startOdinPeer(context, endpoint);
  const signer = signerFor(context, "service", -1);
  signer.reportHealth("active");
  const publisher = createIdunnRuntimePresencePublisher({ signer, endpoint });
  await publisher.publish("below minimum");
  const capabilities = (decode(peer.received[0]!.document.payload) as unknown[])[RUNTIME_PRESENCE_SLOT.capabilities] as unknown[][];
  assert.equal(capabilities[0]![3], expectedFacts("service").capabilities[0]!.capacity - 1);
});

test("the Odin publisher takes only the endpoint Expected names", (context) => {
  assert.throws(
    () => createIdunnRuntimePresencePublisher({ signer: signerFor(context, "service"), endpoint: "rudp://127.0.0.1:1" }),
    /does not match Idunn Expected dependency authority/,
  );
  assert.throws(
    () => createIdunnRuntimePresencePublisher({ signer: signerFor(context, "web"), endpoint: expectedFacts("service").odinEndpoint! }),
    /does not match Idunn Expected dependency authority/,
  );
});

test("the route answer and the Odin publish report the same state, because the signer owns it", async (context) => {
  const endpoint = expectedFacts("service").odinEndpoint!;
  const peer = await startOdinPeer(context, endpoint);
  const signer = signerFor(context, "service");
  const publisher = createIdunnRuntimePresencePublisher({ signer, endpoint });

  await publisher.publish("first");
  assert.equal(presenceSlot(peer.received[0]!.document, RUNTIME_PRESENCE_SLOT.state), "warming");
  assert.equal(presenceSlot(signer.answerRouteObservation(CHALLENGE), RUNTIME_PRESENCE_SLOT.state), "warming");

  signer.reportHealth("active");
  await publisher.publish("second");
  assert.equal(presenceSlot(peer.received[1]!.document, RUNTIME_PRESENCE_SLOT.state), "active");
  assert.equal(presenceSlot(signer.answerRouteObservation(CHALLENGE), RUNTIME_PRESENCE_SLOT.state), "active");
  // One sequence across both carriers.
  assert.deepEqual(
    [1, 2].map((index) => presenceSlot(peer.received[index - 1]!.document, RUNTIME_PRESENCE_SLOT.publisherSequence)),
    [1, 3],
  );
});

// The lease-bound set: Rust issued lease.cc naming presence-warming.bin's digest, and presence-active.bin
// and presence-degraded.bin carry that lease's digest.
const STATEFUL = "service-stateful" as const;

function statefulSigner(context: TestContext, lease = "lease.cc") {
  return signerFor(context, STATEFUL, 0, lease);
}

test("stateful: warming, active and degraded match the Rust-verified vectors byte for byte, active and degraded bound to Rust's lease", (context) => {
  freezeClock(context);
  const signer = statefulSigner(context);
  assert.deepEqual(Buffer.from(signer.answerRouteObservation(CHALLENGE).payload), fixtureFile(STATEFUL, "presence-warming.bin"));
  signer.reportHealth("active");
  const active = signer.answerRouteObservation(CHALLENGE);
  assert.deepEqual(Buffer.from(active.payload), fixtureFile(STATEFUL, "presence-active.bin"));
  assert.match(String(presenceSlot(active, RUNTIME_PRESENCE_SLOT.writeLeaseSha256)), /^sha256-[0-9a-f]{64}$/);

  const degraded = statefulSigner(context);
  degraded.answerRouteObservation(CHALLENGE);
  degraded.reportHealth("degraded");
  assert.deepEqual(Buffer.from(degraded.answerRouteObservation(CHALLENGE).payload), fixtureFile(STATEFUL, "presence-degraded.bin"));
});

test("a warming answered on the route becomes a lease target: Rust's lease naming it is accepted", (context) => {
  freezeClock(context);
  const signer = statefulSigner(context);
  assert.throws(() => signer.assertWriteLease(), /does not match the current Expected incarnation/, "no warming signed yet");
  signer.answerRouteObservation(CHALLENGE);
  assert.equal(signer.assertWriteLease(), presenceSlot({ payload: fixtureFile(STATEFUL, "presence-active.bin") }, RUNTIME_PRESENCE_SLOT.writeLeaseSha256));
});

for (const [lease, reason] of [["lease-other-warming.cc", "names another warming digest"], ["lease-other-target.cc", "names another target"]] as const) {
  test(`a lease that ${reason} is refused, and the signer cannot sign active under it`, (context) => {
    freezeClock(context);
    const signer = statefulSigner(context, lease);
    signer.answerRouteObservation(CHALLENGE);
    assert.throws(() => signer.assertWriteLease(), /does not match the current Expected incarnation/);
    signer.reportHealth("active");
    assert.throws(() => signer.answerRouteObservation(CHALLENGE), /does not match the current Expected incarnation/);
  });
}

test("one signer per loaded authority", (context) => {
  const authority = openAuthority(context, "web");
  const capabilities = expectedFacts("web").capabilities;
  createIdunnRuntimeSigner({ authority, capabilities });
  assert.throws(() => createIdunnRuntimeSigner({ authority, capabilities }), /already has a signer/);
});

test("the loaded authority is frozen: the bind, contract, Expected, Activation and identities cannot change, and the signer signs the loaded endpoint", (context) => {
  freezeClock(context);
  const authority = openAuthority(context, "web");
  const loaded = expectedFacts("web");
  const open = authority as unknown as { [key: string]: any };
  const mutations: Array<[string, () => void]> = [
    ["boundEndpoint", () => { open.boundEndpoint = "http://127.0.0.1:1"; }],
    ["providerSignerIdentityId", () => { open.providerSignerIdentityId = "forged"; }],
    ["activationSignerIdentityId", () => { open.activationSignerIdentityId = "forged"; }],
    ["expected.healthContract", () => { open.expected.healthContract = "forged"; }],
    ["expected.target", () => { open.expected.target = "forged"; }],
    ["expected.canonicalSha256", () => { open.expected.canonicalSha256 = "forged"; }],
    ["expected.route.candidateEndpoint", () => { open.expected.route.candidateEndpoint = "http://127.0.0.1:1"; }],
    ["expected.capabilities[0].minimumCapacity", () => { open.expected.capabilities[0].minimumCapacity = 0; }],
    ["expected.capabilities.push", () => { open.expected.capabilities.push({}); }],
    ["activation.runtimeInstanceId", () => { open.activation.runtimeInstanceId = "forged"; }],
    ["activation.canonicalSha256", () => { open.activation.canonicalSha256 = "forged"; }],
  ];
  for (const [name, mutate] of mutations) assert.throws(mutate, TypeError, name);
  const signer = createIdunnRuntimeSigner({ authority, capabilities: loaded.capabilities });
  assert.equal(presenceSlot(signer.answerRouteObservation(CHALLENGE), RUNTIME_PRESENCE_SLOT.boundEndpoint), loaded.candidate);
});

// A web app must be able to import the signer without pulling the Odin publisher and dgram.
function loadUnderDgramStub(modulePath: string): string {
  const script = [
    'const Module = require("node:module");',
    "const load = Module._load;",
    'Module._load = function (request, ...rest) { if (request === "dgram" || request === "node:dgram") throw new Error("dgram was loaded"); return load.call(this, request, ...rest); };',
    "try { require(process.argv[1]); process.stdout.write(\"loaded\"); } catch (error) { process.stdout.write(\"failed: \" + error.message); }",
  ].join("\n");
  const run = spawnSync(process.execPath, ["-e", script, modulePath], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
  return run.stdout;
}

test("the idunn-runtime subpath loads the signer and loader without loading dgram; the package root does load it", () => {
  const subpath = require.resolve("cultnet-ts/idunn-runtime");
  assert.equal(loadUnderDgramStub(subpath), "loaded");
  assert.equal(loadUnderDgramStub(require.resolve("cultnet-ts")), "failed: dgram was loaded");
  const exported = require(subpath) as Record<string, unknown>;
  assert.equal(typeof exported.createIdunnRuntimeSigner, "function");
  assert.equal(typeof exported.loadIdunnRuntimeAuthorityFromEnvironment, "function");
  assert.equal(exported.createIdunnRuntimePresencePublisher, undefined);
  assert.equal(exported.signIdunnRuntimePresence, undefined);
  assert.equal((require("cultnet-ts") as Record<string, unknown>).signIdunnRuntimePresence, undefined);
});
