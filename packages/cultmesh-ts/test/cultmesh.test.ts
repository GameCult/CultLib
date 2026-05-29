import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";
import { CultMesh } from "../src/index";

const noteDocument = defineDocumentType({
  type: "cultmesh.note",
  schemaId: "cultmesh.note.v0",
  schema: z.object({
    noteId: z.string(),
    body: z.string(),
  }),
  name: "noteId",
});

test("CultMesh TS opens a durable local node and registers document bindings", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-")), "node.ccmp");
  const first = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });

  await first.put(noteDocument, "note:intro", {
    noteId: "note:intro",
    body: "hello from local CultMesh TS",
  });
  await first.flush();

  const reopened = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });

  assert.equal(
    reopened.getRequired(noteDocument, "note:intro").body,
    "hello from local CultMesh TS",
  );
  assert.ok(reopened.documents.get("cultmesh.note"));
  assert.ok(reopened.documents.getBySchemaId("cultmesh.note.v0"));
});

test("CultMesh TS local authority leases do not trust peer cards by contact alone", () => {
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  const peer = {
    peerId: "voidbot-local",
    verseId: "local",
    endpoints: ["cultmesh://localhost"],
    roles: ["shard-primary"],
    authorityLeaseId: "lease:voidbot-local",
  };

  peers.upsert(peer);

  assert.equal(leases.isAuthorized(peer, "shard-primary"), false);

  leases.upsert({
    leaseId: "lease:voidbot-local",
    verseId: "local",
    peerId: "voidbot-local",
    roles: ["shard-primary"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  });

  assert.equal(leases.isAuthorized(peer, "shard-primary"), true);
});
