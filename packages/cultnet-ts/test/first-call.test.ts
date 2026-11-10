import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

// The TypeScript CultNet first-call snippet that docs/start/README.md shows: define a typed document, send it
// over a loopback TCP pipe as a cultnet.document_put_raw.v0 message, receive it into a second cache. The docs show
// the region between the `snippet` markers verbatim, except that the package import reads
// "@gamecult/cultnet-ts" where this file reads "../src"; the docs test below fails when they differ.
// snippet:begin
import { createServer, connect } from "node:net";
import { z } from "zod";
import { CultCache, defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  createTcpFramedCultNetPeer,
  defineCultNetDocumentBinding,
} from "../src";

const noteDocument = defineDocumentType({
  type: "note",
  schemaId: "gamecult.first_call_note.v1",
  schemaName: "gamecult.first_call_note",
  schemaVersion: "gamecult.first_call_note.v1",
  schema: z.object({ title: z.string(), body: z.string() }),
  name: "title",
});
const documents = new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: noteDocument })]);

async function firstCall() {
  const options = { runtimeId: "first-call", wireContract: "cultnet.schema.v0" } as const;
  const inbox = CultCache.builder().withDocumentType(noteDocument).build(); // the receiver's cache

  // Receiver: a loopback listener whose peer applies each document put to the inbox.
  const received = new Promise<void>((resolve, reject) => {
    const server = createServer((socket) => {
      const peer = createTcpFramedCultNetPeer(socket, options);
      peer.on("invalidMessage", reject);
      peer.on("message", async (message) => {
        if (message.schemaVersion !== "cultnet.document_put_raw.v0") return;
        await documents.applyRawDocumentPutMessage(inbox, message);
        peer.close();
        server.close();
        resolve();
      });
    }).listen(0, "127.0.0.1", () => {
      // Sender: connect, build a typed put message, send it.
      const { port } = server.address() as { port: number };
      const sender = createTcpFramedCultNetPeer(connect(port, "127.0.0.1"), options);
      sender.send(
        documents.createRawDocumentPutMessage(documents.get("note")!, "put-1", "note:hello", {
          title: "Hello",
          body: "sent over CultNet",
        }),
      );
      received.finally(() => sender.close());
    });
  });
  await received;
  return inbox.get(noteDocument, "note:hello");
}
// snippet:end

test("send a typed document over loopback and receive it into a second cache", async () => {
  const note = await firstCall();
  assert.deepEqual(note, { title: "Hello", body: "sent over CultNet" });
});

test("docs show the tested snippet", async () => {
  // Compiled to dist-test/test/first-call.test.js, so the package root is two levels up.
  const source = await readFile(join(__dirname, "..", "..", "test", "first-call.test.ts"), "utf8");
  const snippet = source
    .split("// snippet:begin\n")[1]
    .split("// snippet:end\n")[0]
    .replace('"../src"', '"@gamecult/cultnet-ts"');
  const docs = (await readFile(join(__dirname, "..", "..", "..", "..", "docs", "start", "README.md"), "utf8")).replace(/\r\n/g, "\n");
  assert.ok(docs.includes("```ts\n" + snippet + "```"), "docs/start/README.md no longer shows test/first-call.test.ts");
});
