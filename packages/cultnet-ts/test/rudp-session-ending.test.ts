// A write belongs to the session generation it was issued in. Every way a
// generation ends drops the writes it still owed, so none is retransmitted
// into a later session where the new peer would deliver it.
import assert from "node:assert/strict";
import test from "node:test";
import { CultNetRudpSession } from "../src";

const connectionId = 0x10203070;
const enc = (text: string) => new TextEncoder().encode(text);

function clientWithALostWrite(): { client: CultNetRudpSession; server: CultNetRudpSession } {
  const client = new CultNetRudpSession({ connectionId, initialSequence: 1 });
  const server = new CultNetRudpSession({ connectionId, initialSequence: 500 });
  client.receive(server.acceptConnect(client.createConnect(0), 0), 0);
  client.send("schema", enc("owed to the old session"), { reliable: true, ordered: true });
  // A fragmented write larger than the send window leaves part of it queued.
  client.sendMany("schema", new Uint8Array(40 * 8), { reliable: true, ordered: true, maxFragmentBytes: 8 });
  assert.equal(client.outstandingReliablePacketCount, 41);
  assert.ok(client.queuedReliablePacketCount > 0);
  return { client, server };
}

const endings: Record<string, (client: CultNetRudpSession, server: CultNetRudpSession) => void> = {
  "peer Disconnect": (client, server) => {
    client.receive(server.createDisconnect(enc("bye")), 1);
  },
  "local disconnect": (client) => {
    client.createDisconnect(enc("bye"));
  },
  timeout: (client) => {
    assert.equal(client.checkTimeout(1_000, 10), true);
  },
  reset: (client) => {
    client.resetPeerState();
  },
};

for (const [name, end] of Object.entries(endings)) {
  test(`a ${name} drops the writes the session owed`, () => {
    const { client, server } = clientWithALostWrite();
    end(client, server);

    assert.equal(client.outstandingReliablePacketCount, 0, "the write survived the end");

    const nextServer = new CultNetRudpSession({ connectionId, initialSequence: 900 });
    nextServer.acceptConnect(client.createConnect(2_000), 2_000);
    for (const resend of client.dueResends(60_000)) {
      assert.deepEqual(
        nextServer.receive(resend, 60_000).delivered,
        [],
        "the old session's write was delivered in the next session",
      );
    }
  });
}

test("an ending does not forget what was received from the peer", () => {
  const client = new CultNetRudpSession({ connectionId, initialSequence: 1 });
  const server = new CultNetRudpSession({ connectionId, initialSequence: 500 });
  client.receive(server.acceptConnect(client.createConnect(0), 0), 0);
  const s1 = client.send("schema", enc("s1"), { reliable: true, ordered: true });
  const s2 = client.send("schema", enc("s2"), { reliable: true, ordered: true });
  assert.deepEqual(server.receive(s2, 1).delivered, [], "s1 is missing, so s2 is held");

  assert.equal(server.checkTimeout(1_000, 10), true);
  assert.deepEqual(
    server.receive(s1, 1_001).delivered.map((frame) => new TextDecoder().decode(frame.payload)),
    ["s1", "s2"],
    "the ending forgot the held frame",
  );
  assert.deepEqual(server.receive(s2, 1_002).delivered, [], "the ending forgot what was received");
});
