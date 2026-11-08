// The TypeScript write-receipt client (cultnet-ts WriteTracker and decoder, cultmesh-ts one-shot writer) against the two real
// receipt servers over loopback, as child processes on ephemeral ports:
//
//   rust    packages/cultmesh-rs/examples/receipt_interop_server.rs, the RUDP document server, reached over RUDP.
//   csharp  tests/GameCult.Networking.InteropPeer `receipt-serve`, CultNetDatabaseServer on Server (LiteNetLib). TypeScript has no
//           LiteNetLib client, so a second child, `receipt-relay`, carries bytes between the test and the server (4-byte
//           big-endian length, then the CultNet frame: CultNetPeer's own stream framing). It decodes and judges nothing.
//
// Every scenario runs against both targets; its outcome is read from the WriteTracker (admitted, refused, unconfirmed), and the
// server's own stdout facts say what was committed. Contract: contracts/cultnet/interop/write-receipt.md and its vectors.
//
// Needs cargo and dotnet; run it with `npm run test:interop` in packages/cultmesh-ts (CARGO and DOTNET override the commands;
// RECEIPT_INTEROP_PREBUILT=1 skips the two builds).
import assert from "node:assert/strict";
import { execFile, spawn, spawnSync, type ChildProcess } from "node:child_process";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { Duplex } from "node:stream";
import { after, before, describe, test } from "node:test";
import { promisify } from "node:util";
import { encode } from "@msgpack/msgpack";
import { z } from "zod";
import { defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS,
  CultNetPeer,
  WriteTracker,
  defineCultNetDocumentBinding,
  helloAdvertisesWriteReceipts,
  resultWindowMs,
  type CultNetErrorV1Message,
  type CultNetHelloMessage,
  type CultNetMessage,
  type WriteSettlement,
} from "@gamecult/cultnet-ts";
import { CultMesh, CultMeshRudpWriteRefusedError, CultMeshRudpWriteUnconfirmedError } from "../../src/index";

const execFileAsync = promisify(execFile);
// dist-test/test/interop -> the repository root
const repoRoot = resolve(__dirname, "..", "..", "..", "..", "..");
const cargo = process.env.CARGO ?? "cargo";
const dotnet = process.env.DOTNET ?? "dotnet";
const rustManifestDir = join(repoRoot, "packages", "cultmesh-rs");
const csharpProject = join(repoRoot, "tests", "GameCult.Networking.InteropPeer", "GameCult.Networking.InteropPeer.csproj");
const csharpDll = join(repoRoot, "bin", "GameCult.Networking.InteropPeer", "Debug", "net10.0", "GameCult.Networking.InteropPeer.dll");
// Set when the server binaries were built beforehand (the builds themselves cannot start under a tight address-space cap).
const prebuilt = process.env.RECEIPT_INTEROP_PREBUILT === "1";
const vectors = JSON.parse(readFileSync(join(repoRoot, "contracts", "cultnet", "interop", "write-receipt-vectors.json"), "utf8"));

const SCHEMA_ID = "receipt.interop_note.v1";
const UNREADABLE = "message_invalid: the message could not be read";
const sleep = (ms: number) => new Promise<void>((done) => setTimeout(done, ms));

async function waitFor(predicate: () => boolean, what: string, ms = 5_000): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > ms) throw new Error(`timed out waiting for ${what}`);
    await sleep(5);
  }
}

// ---- the servers -------------------------------------------------------------------------------------------------------

interface Fact { event: string; recordKey?: string; messageId?: string; [key: string]: unknown }

/** A running server child and the JSON facts it prints on stdout. */
class ServerProcess {
  readonly facts: Fact[] = [];
  stderr = "";
  port = 0;
  constructor(readonly child: ChildProcess) {
    let pending = "";
    child.stdout!.on("data", (chunk: Buffer) => {
      pending += chunk.toString("utf8");
      for (let at = pending.indexOf("\n"); at >= 0; at = pending.indexOf("\n")) {
        const line = pending.slice(0, at).trim();
        pending = pending.slice(at + 1);
        if (line) this.facts.push(JSON.parse(line) as Fact);
      }
    });
    child.stderr!.on("data", (chunk: Buffer) => { this.stderr += chunk.toString("utf8"); });
  }
  /** How many times `event` was printed for `recordKey`. */
  count(event: string, recordKey: string): number {
    return this.facts.filter((fact) => fact.event === event && fact.recordKey === recordKey).length;
  }
  async stop(): Promise<void> {
    this.child.stdin!.end();
    await new Promise<void>((done) => {
      const killer = setTimeout(() => this.child.kill("SIGKILL"), 5_000);
      this.child.once("exit", () => { clearTimeout(killer); done(); });
      if (this.child.exitCode !== null) done();
    });
  }
}

async function launch(command: string, args: string[]): Promise<ServerProcess> {
  const server = new ServerProcess(spawn(command, args, { stdio: ["pipe", "pipe", "pipe"] }));
  await waitFor(() => server.facts.some((fact) => fact.event === "listening") || server.child.exitCode !== null, "the server to listen", 60_000);
  const listening = server.facts.find((fact) => fact.event === "listening");
  if (!listening) throw new Error(`the server exited before listening:\n${server.stderr}`);
  server.port = listening.port as number;
  return server;
}

/** One connection to a server: the peer that speaks CultNet, and how to close it. */
interface Connection { peer: CultNetPeer; close(): void }

interface Target {
  name: string;
  /** Shard fields the server's database requires on a write, if any. */
  shard?: { shardId: string; shardEpoch: number };
  /** A document payload this target's store reads. */
  payload(text: string): Uint8Array;
  start(): Promise<ServerProcess>;
  connect(server: ServerProcess): Promise<Connection>;
}

let nextConnectionId = 0x20206000;

const rust: Target = {
  name: "rust",
  payload: (text) => encode({ text }),
  async start() {
    if (!prebuilt) await execFileAsync(cargo, ["build", "--example", "receipt_interop_server", "--message-format", "short"], { cwd: rustManifestDir });
    const metadata = spawnSync(cargo, ["metadata", "--format-version", "1", "--no-deps"], { cwd: rustManifestDir, encoding: "utf8" });
    if (metadata.status !== 0) throw new Error(`cargo metadata failed: ${metadata.stderr}`);
    const exe = join(JSON.parse(metadata.stdout).target_directory, "debug", "examples", `receipt_interop_server${process.platform === "win32" ? ".exe" : ""}`);
    return launch(exe, []);
  },
  async connect(server) {
    const peer = await CultMesh.createRudpPeer("receipt-interop", nextConnectionId++, { host: "127.0.0.1", port: server.port, uri: `rudp://127.0.0.1:${server.port}` });
    return { peer, close: () => peer.close() };
  },
};

const csharp: Target = {
  name: "csharp",
  shard: { shardId: "receipt-interop-shard", shardEpoch: 1 },
  payload: (text) => encode([text]),
  async start() {
    if (!prebuilt) await execFileAsync(dotnet, ["build", csharpProject, "-nologo", "-v", "q"], { cwd: repoRoot, maxBuffer: 64 * 1024 * 1024 });
    return launch(dotnet, [csharpDll, "receipt-serve"]);
  },
  async connect(server) {
    const relay = spawn(dotnet, [csharpDll, "receipt-relay", "--port", String(server.port)], { stdio: ["pipe", "pipe", "ignore"] });
    const peer = new CultNetPeer(Duplex.from({ readable: relay.stdout!, writable: relay.stdin! }), { wireContract: "cultnet.schema.v0" });
    return { peer, close: () => { relay.stdin!.end(); relay.kill(); } };
  },
};

// ---- a client session: one connection, the frames it saw, one WriteTracker per write -----------------------------------------

class Session {
  readonly frames: CultNetMessage[] = [];
  readonly invalid: Error[] = [];
  private readonly trackers = new Set<WriteTracker>();
  private serverReceipts = false;
  private constructor(readonly target: Target, private readonly connection: Connection) {
    connection.peer.on("message", (message: CultNetMessage) => {
      if (message.schemaVersion === "cultnet.hello.v0") this.serverReceipts = helloAdvertisesWriteReceipts(message);
      this.frames.push(message);
      for (const tracker of this.trackers) tracker.frame(message, Date.now());
    });
    connection.peer.on("invalidMessage", (error: Error) => this.invalid.push(error));
    connection.peer.on("close", () => { for (const tracker of this.trackers) tracker.sessionEnd(Date.now()); });
  }

  static async open(target: Target, server: ServerProcess): Promise<Session> {
    return new Session(target, await target.connect(server));
  }

  /** Declares the receipt capability and waits for the server's own hello. */
  async hello(versions: readonly string[] = CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS): Promise<CultNetHelloMessage> {
    this.connection.peer.sendHello({ schemaVersion: "cultnet.hello.v0", runtimeId: "receipt-interop", runtimeKind: "cultmesh-ts", supportedMessageVersions: [...versions] });
    await waitFor(() => this.frames.some((frame) => frame.schemaVersion === "cultnet.hello.v0"), "the server's hello");
    return this.frames.find((frame) => frame.schemaVersion === "cultnet.hello.v0") as CultNetHelloMessage;
  }

  put(messageId: string, recordKey: string, text: string, document: Record<string, unknown> = {}): CultNetMessage {
    return this.shard({
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId,
      document: {
        schemaId: SCHEMA_ID, recordKey, storedAt: new Date().toISOString(), payloadEncoding: "messagepack",
        payload: this.target.payload(text), ...document,
      },
    });
  }

  delete(messageId: string, recordKey: string): CultNetMessage {
    return this.shard({ schemaVersion: "cultnet.document_delete.v0", messageId, schemaId: SCHEMA_ID, recordKey });
  }

  private shard(message: unknown): CultNetMessage {
    return { ...(message as object), ...this.target.shard } as CultNetMessage;
  }

  /**
   * Sends one write and settles it with its own tracker, windowed by what the server advertised (5000 ms against a receipt
   * server, 500 ms otherwise). Only the tracker decides the outcome.
   */
  async write(message: CultNetMessage): Promise<WriteSettlement> {
    const messageId = (message as { messageId: string }).messageId;
    const tracker = new WriteTracker({ windowMs: resultWindowMs(this.serverReceipts) });
    this.trackers.add(tracker);
    tracker.send(messageId, Date.now());
    this.connection.peer.send(message);
    for (;;) {
      tracker.tick(Date.now());
      const state = tracker.state(messageId)!;
      if (state.outcome !== "pending") return state;
      await sleep(Math.min(20, tracker.nextDeadlineMs(Date.now()) ?? 20));
    }
  }

  /** Frames of `schemaVersion` that name `messageId`. */
  answers(messageId: string, schemaVersion: string): CultNetMessage[] {
    return this.frames.filter((frame) => frame.schemaVersion === schemaVersion && (frame as { messageId?: string }).messageId === messageId);
  }

  close(): void { this.connection.close(); }
}

function refusal(settlement: WriteSettlement): CultNetErrorV1Message {
  assert.equal(settlement.outcome, "refused", JSON.stringify(settlement));
  return (settlement as Extract<WriteSettlement, { outcome: "refused" }>).response as CultNetErrorV1Message;
}

// ---- the scenarios, run against each server -----------------------------------------------------------------------------------

for (const target of [rust, csharp]) {
  describe(`write receipt, TypeScript client against the real ${target.name} server`, () => {
    let server: ServerProcess;
    let session: Session | undefined;
    before(async () => { server = await target.start(); }, { timeout: 180_000 });
    after(async () => { session?.close(); await server?.stop(); });

    /** A fresh receipt session (hello sent, the server's hello read) for a scenario. */
    async function receiptSession(): Promise<Session> {
      session?.close();
      session = await Session.open(target, server);
      const hello = await session.hello();
      assert.ok(helloAdvertisesWriteReceipts(hello), "the server's hello lists both receipt frames");
      return session;
    }

    test("hello listing both frames: the server answers with its own hello listing both, then a put is receipted after it", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      assert.equal(s.frames[0].schemaVersion, "cultnet.hello.v0");
      const settlement = await s.write(s.put("hello-1", "hello:a", "one"));
      assert.deepEqual(settlement, { outcome: "admitted" });
      assert.deepEqual(s.frames.map((frame) => frame.schemaVersion), ["cultnet.hello.v0", "cultnet.write_admitted.v0"]);
    });

    test("a hello listing only one of the two frames does not opt in: no receipt, and the put is applied", { timeout: 30_000 }, async () => {
      for (const listed of CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS) {
        session?.close();
        session = await Session.open(target, server);
        session.hello([listed]).catch(() => undefined); // a server may stay silent to a hello that does not opt in
        await sleep(300);
        const settlement = await session.write(session.put(`half-${listed}`, `half:${listed}`, "one"));
        assert.deepEqual(settlement, { outcome: "unconfirmed", reason: "window_elapsed" }, `listing only ${listed}`);
        assert.ok(!session.frames.some((frame) => frame.schemaVersion === "cultnet.write_admitted.v0" || frame.schemaVersion === "cultnet.error.v1"));
        await waitFor(() => server.count("applied", `half:${listed}`) === 1, "the put to be applied");
      }
    });

    test("put admitted: the receipt echoes the write and the store has it once", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      const settlement = await s.write(s.put("put-1", "put:a", "one"));
      assert.deepEqual(settlement, { outcome: "admitted" });
      assert.deepEqual(s.answers("put-1", "cultnet.write_admitted.v0"), [
        { schemaVersion: "cultnet.write_admitted.v0", messageId: "put-1", operation: "put", schemaId: SCHEMA_ID, recordKey: "put:a" },
      ]);
      await waitFor(() => server.count("applied", "put:a") === 1, "the put to be applied");
    });

    // The Rust RUDP document server has no delete: its sink only accepts documents, and every delete on a receipt session is
    // answered with a refusal (rudp_document_server.rs, deletes_on_a_receipt_session_are_refused_one_by_one). The spec
    // (Server rules 1 to 3) has a delete admitted and receipted like a put.
    test("delete admitted: the receipt says operation delete and the store drops the record", {
      timeout: 30_000,
      todo: target.name === "rust" ? "the Rust RUDP document server refuses every delete; there is no delete port in CultMeshRudpRawDocumentSink" : false,
    }, async () => {
      const s = await receiptSession();
      assert.deepEqual(await s.write(s.put("del-put", "del:a", "one")), { outcome: "admitted" });
      assert.deepEqual(await s.write(s.delete("del-1", "del:a")), { outcome: "admitted" });
      assert.deepEqual(s.answers("del-1", "cultnet.write_admitted.v0"), [
        { schemaVersion: "cultnet.write_admitted.v0", messageId: "del-1", operation: "delete", schemaId: SCHEMA_ID, recordKey: "del:a" },
      ]);
      await waitFor(() => server.count("removed", "del:a") === 1, "the delete to be applied");
    });

    test("refused write: an error.v1 carrying the write's id, and nothing is applied", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      const response = refusal(await s.write(s.put("deny-1", "refuse:a", "one")));
      assert.equal(response.schemaVersion, "cultnet.error.v1");
      assert.equal(response.messageId, "deny-1");
      assert.equal(response.code, "write_not_authorized");
      // The session is kept: the next write on it is admitted.
      assert.deepEqual(await s.write(s.put("deny-2", "deny:b", "one")), { outcome: "admitted" });
      assert.equal(server.count("applied", "refuse:a"), 0);
    });

    test("resend of the same id and content on the same session: re-receipted, applied once", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      assert.deepEqual(await s.write(s.put("resend-1", "resend:a", "one")), { outcome: "admitted" });
      assert.deepEqual(await s.write(s.put("resend-1", "resend:a", "one")), { outcome: "admitted" });
      const receipts = s.answers("resend-1", "cultnet.write_admitted.v0");
      assert.equal(receipts.length, 2);
      assert.deepEqual(receipts[0], receipts[1]);
      await sleep(200); // a second application would be printed by now
      assert.equal(server.count("applied", "resend:a"), 1);
    });

    test("same id with different content: message_invalid naming the id, never a receipt, the first write untouched", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      assert.deepEqual(await s.write(s.put("clash-1", "clash:a", "one")), { outcome: "admitted" });
      const response = refusal(await s.write(s.put("clash-1", "clash:a", "other")));
      assert.equal(response.code, "message_invalid");
      assert.equal(response.messageId, "clash-1");
      assert.equal(response.details?.field, "messageId");
      assert.equal(s.answers("clash-1", "cultnet.write_admitted.v0").length, 1, "no receipt for the changed write");
      await sleep(200);
      assert.equal(server.count("applied", "clash:a"), 1);
    });

    // Each case of the vectors' blankFields section: a document that names nothing is refused as an unreadable frame, with the
    // write's id and the fixed text, and never applied; a document that is merely missing optional fields is admitted.
    for (const blank of vectors.blankFields.cases as { name: string; set: Record<string, unknown>; outcome: "admitted" | "refused" }[]) {
      test(`blank-field vector ${blank.name}: ${blank.outcome}`, { timeout: 30_000 }, async () => {
        const s = await receiptSession();
        const recordKey = `blank:${blank.name}`;
        const { payloadHex, ...fields } = blank.set;
        const message = s.put(`blank-${blank.name}`, recordKey, "one", fields);
        const sent = (message as unknown as { document: Record<string, unknown> }).document;
        for (const [key, value] of Object.entries(fields)) if (value === null) delete sent[key]; // nil means absent on the wire here
        if (payloadHex === "") sent.payload = new Uint8Array();
        const settlement = await s.write(message);
        if (blank.outcome === "admitted") {
          assert.deepEqual(settlement, { outcome: "admitted" });
          await waitFor(() => server.count("applied", recordKey) === 1, "the put to be applied");
          return;
        }
        const response = refusal(settlement);
        assert.equal(response.code, "message_invalid");
        assert.equal(response.error, UNREADABLE);
        assert.equal(response.details?.field, "message");
        assert.equal(response.messageId, `blank-${blank.name}`);
        await sleep(200);
        assert.equal(server.count("applied", recordKey), 0);
        assert.equal(server.count("applied", "blank:"), 0);
      });
    }

    test("a delete that names nothing is refused as unreadable with its id", { timeout: 30_000 }, async () => {
      const s = await receiptSession();
      const message = s.delete("blank-del", "blank:del");
      (message as unknown as { schemaId: string }).schemaId = "";
      const response = refusal(await s.write(message));
      assert.equal(response.code, "message_invalid");
      assert.equal(response.error, UNREADABLE);
      assert.equal(response.details?.field, "message");
      assert.equal(response.messageId, "blank-del");
    });

    test("legacy client (no hello): no receipt frames, the write is applied, and its refusal is the old error.v0", { timeout: 30_000 }, async () => {
      session?.close();
      session = await Session.open(target, server);
      assert.deepEqual(await session.write(session.put("legacy-1", "legacy:a", "one")), { outcome: "unconfirmed", reason: "window_elapsed" });
      await waitFor(() => server.count("applied", "legacy:a") === 1, "the put to be applied");
      const refused = (await session.write(session.put("legacy-2", "refuse:legacy", "one"))) as Extract<WriteSettlement, { outcome: "refused" }>;
      assert.equal(refused.outcome, "refused");
      assert.equal(refused.response.schemaVersion, "cultnet.error.v0");
      assert.deepEqual(session.frames.map((frame) => frame.schemaVersion), ["cultnet.error.v0"]);
      assert.equal(session.invalid.length, 0);
    });
  });
}

// ---- the one-shot writer, which is the real cultmesh-ts client path, against the Rust RUDP server ----------------------------
// (The C# server speaks LiteNetLib, not RUDP: no RUDP client path reaches it.)

describe("write receipt, cultmesh-ts publishRudpDocumentOnce against the real rust server", () => {
  const note = defineDocumentType({ type: "receipt.interop_note", schemaId: SCHEMA_ID, schema: z.object({ text: z.string() }), name: "text" });
  const binding = defineCultNetDocumentBinding({ definition: note });
  let server: ServerProcess;
  before(async () => { server = await rust.start(); }, { timeout: 180_000 });
  after(async () => { await server?.stop(); });
  const endpoint = () => ({ host: "127.0.0.1", port: server.port, uri: `rudp://127.0.0.1:${server.port}` });

  test("writeReceipts: the put resolves admitted on the receipt, after the server applied it", { timeout: 30_000 }, async () => {
    const outcome = await CultMesh.publishRudpDocumentOnce("receipt-interop", nextConnectionId++, endpoint(), binding, "once:a", { text: "one" }, { writeReceipts: true, messageId: "once-1" });
    assert.equal(outcome, "admitted");
    await waitFor(() => server.count("applied", "once:a") === 1, "the put to be applied");
  });

  test("writeReceipts: a refused put rejects with the server's error.v1 naming the put", { timeout: 30_000 }, async () => {
    await assert.rejects(
      CultMesh.publishRudpDocumentOnce("receipt-interop", nextConnectionId++, endpoint(), binding, "refuse:once", { text: "one" }, { writeReceipts: true, messageId: "once-2" }),
      (error: unknown) => {
        assert.ok(error instanceof CultMeshRudpWriteRefusedError);
        assert.equal(error.response.schemaVersion, "cultnet.error.v1");
        assert.equal((error.response as CultNetErrorV1Message).messageId, "once-2");
        return true;
      },
    );
  });

  test("without writeReceipts the server stays silent: the put is unconfirmed, not admitted, though the server applied it", { timeout: 30_000 }, async () => {
    await assert.rejects(
      CultMesh.publishRudpDocumentOnce("receipt-interop", nextConnectionId++, endpoint(), binding, "once:legacy", { text: "one" }, { messageId: "once-3" }),
      (error: unknown) => error instanceof CultMeshRudpWriteUnconfirmedError && error.reason === "window_elapsed",
    );
    await waitFor(() => server.count("applied", "once:legacy") === 1, "the put to be applied");
  });
});
