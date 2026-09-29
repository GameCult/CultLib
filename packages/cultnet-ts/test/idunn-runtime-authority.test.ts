import { test, type TestContext } from "node:test";
import assert from "node:assert/strict";
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

type FixtureSet = "web" | "service" | "rudp-route";

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
function openAuthority(context: TestContext, set: FixtureSet, options: { machineId?: "etc" | "dbus" } = {}): IdunnRuntimeAuthority {
  const facts = expectedFacts(set);
  const originalEnvironment = { ...process.env };
  context.after(() => {
    for (const key of Object.keys(process.env)) if (!(key in originalEnvironment)) delete process.env[key];
    Object.assign(process.env, originalEnvironment);
  });
  process.env.GAMECULT_IDUNN_RUNTIME_BUNDLE = path.join(FIXTURES, set);
  process.env.GAMECULT_IDUNN_CANDIDATE_BIND = facts.candidate;
  delete process.env.GAMECULT_IDUNN_PROCESS_WRITE_LEASE;
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

function signerFor(context: TestContext, set: FixtureSet, capacityDelta = 0) {
  const authority = openAuthority(context, set);
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
  assert.ok(signer.answerRouteObservation(CHALLENGE, "warming").payload.byteLength > 0);
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

test("answers a route challenge byte for byte as the Rust-verified vectors, in the state the app reports", (context) => {
  freezeClock(context);
  const signer = signerFor(context, "web");
  const warming = signer.answerRouteObservation(CHALLENGE, "warming");
  assert.deepEqual(Buffer.from(warming.payload), fixtureFile("web", "presence-warming.bin"));
  assert.equal((decode(warming.payload) as unknown[])[RUNTIME_PRESENCE_SLOT.state], "warming");
  const second = signer.answerRouteObservation(CHALLENGE, "active");
  assert.equal((decode(second.payload) as unknown[])[RUNTIME_PRESENCE_SLOT.publisherSequence], 2);
  // Sequence 2 cannot match a vector written at sequence 1; sign the same fields from a fresh signer.
  const fresh = signerFor(context, "web");
  assert.deepEqual(Buffer.from(fresh.answerRouteObservation(CHALLENGE, "active").payload), fixtureFile("web", "presence-active.bin"));
});

test("a service with an Odin dependency signs the Rust-verified vectors, including a shortfall Rust correlates to a capacity disagreement", (context) => {
  freezeClock(context);
  assert.deepEqual(
    Buffer.from(signerFor(context, "service").answerRouteObservation(CHALLENGE, "active").payload),
    fixtureFile("service", "presence-active.bin"),
  );
  assert.deepEqual(
    Buffer.from(signerFor(context, "service", -1).answerRouteObservation(CHALLENGE, "active").payload),
    fixtureFile("service", "presence-active-below-minimum.bin"),
  );
});

test("answers locally: synchronously, with no socket opened", (context) => {
  const sockets = context.mock.method(dgram, "createSocket");
  const signer = signerFor(context, "web");
  const answer = signer.answerRouteObservation(CHALLENGE, "warming");
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
  for (const request of cases) assert.throws(() => signer.answerRouteObservation(request, "active"), /route observation/);
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

  await publisher.publish("warming", "accepted presence");
  assert.equal(peer.received[0]?.schemaVersion, "cultnet.document_put_raw.v0");
  const published = decode(peer.received[0]!.document.payload) as unknown[];
  assert.equal(published[RUNTIME_PRESENCE_SLOT.state], "warming");
  assert.equal(published[RUNTIME_PRESENCE_SLOT.publisherSequence], 1);
  assert.equal(peer.responseAck, 2, "the response data packet must acknowledge the publisher's reliable schema packet");

  peer.reject = true;
  await assert.rejects(publisher.publish("active", "denied presence"), /Odin rejected runtime presence: test admission denied/);
  assert.equal(peer.failure, undefined, peer.failure?.message);
});

test("the Odin publisher leaves a capability shortfall to the Rust correlation instead of refusing to publish", async (context) => {
  const endpoint = expectedFacts("service").odinEndpoint!;
  const peer = await startOdinPeer(context, endpoint);
  const publisher = createIdunnRuntimePresencePublisher({ signer: signerFor(context, "service", -1), endpoint });
  await publisher.publish("active", "below minimum");
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
