import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import assert from "node:assert/strict";
import { encode } from "@msgpack/msgpack";
import { z } from "zod";
import { defineDocumentType } from "cultcache-ts";
import {
  CultNetDocumentRegistry,
  CultNetPeer,
  cultNetBuiltinSchemaRegistry,
  defineCultNetDocumentBinding,
} from "cultnet-ts";
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

const noteAliasDocument = defineDocumentType({
  type: "cultmesh.note.ui",
  schemaId: "cultmesh.note.v0",
  schema: z.object({
    noteId: z.string(),
    body: z.string(),
  }),
  name: "noteId",
});

const noteReceiptDocument = defineDocumentType({
  type: "cultmesh.note_receipt",
  schemaId: "cultmesh.note_receipt.v0",
  schema: z.object({ commandId: z.string(), state: z.literal("applied"), body: z.string() }),
  name: "commandId",
});

const foreignNoteDocument = defineDocumentType({
  type: "runtime.generated.cultmesh.note",
  schemaId: "runtime.generated.cultmesh.note.ui.42",
  schema: z.object({
    noteId: z.string(),
    body: z.string(),
  }),
  name: "noteId",
});

const incompatibleNoteDocument = defineDocumentType({
  type: "runtime.generated.incompatible-note",
  schemaId: "runtime.generated.incompatible-note.v1",
  schema: z.object({
    nope: z.string(),
  }),
});

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const startedAt = Date.now();
  while (!predicate()) {
    if (Date.now() - startedAt > 1_000) {
      throw new Error(`Timed out waiting for ${description}.`);
    }
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

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

test("CultMesh TS document handles hide local cache plumbing behind typed reactive reads", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-doc-")), "node.ccmp");
  const node = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });
  await node.put(noteDocument, "note:1", {
    noteId: "note:1",
    body: "initial",
  });

  const document = node.document(noteDocument, "note:1", { pollMs: 5 });
  const verse = CultMesh.verse("local", "browser-client", {
    routeHint: CultMesh.routeHint("shared-memory", "local cache"),
  });
  const bound = verse.bindDocument(document);
  const observed: string[] = [];
  const unsubscribe = bound.watch(note => observed.push(note.body));

  assert.equal(document.documentId, "cultmesh.note:note:1");
  assert.equal(document.canReplace, true);
  assert.equal(document.canSubmitPrediction, false);
  assert.equal(document.canSet, true);
  assert.equal((await bound.latest()).body, "initial");

  await bound.replace({
    noteId: "note:1",
    body: "updated",
  });
  await waitFor(() => observed.includes("updated"), "document handle update");
  unsubscribe();

  const alias = bound.asSchemaAlias(noteAliasDocument, {
    parse: value => noteAliasDocument.schema.parse(value),
  });
  assert.equal((await alias.latest()).body, "updated");

  const catalog = CultMesh.documents(document);
  assert.equal(catalog.canReplace(noteAliasDocument), true);
  assert.equal(catalog.canSet(noteAliasDocument), true);
  await catalog.set(noteAliasDocument, {
    noteId: "note:1",
    body: "catalog-updated",
  }, {
    parse: value => noteAliasDocument.schema.parse(value),
    context: "browser-client",
  });
  assert.equal(
    (await catalog.latest(noteAliasDocument, "browser-client", {
      parse: value => noteAliasDocument.schema.parse(value),
    })).body,
    "catalog-updated",
  );
  const incremented = await bound.update(value => ({
    ...value,
    body: `${value.body}:updated-through-bound-set`,
  }));
  assert.equal(incremented.body, "catalog-updated:updated-through-bound-set");
  assert.equal((await document.latest()).body, "catalog-updated:updated-through-bound-set");
  assert.throws(
    () => document.asSchemaAlias({ schemaId: "cultmesh.other.v0" }),
    /not compatible/,
  );
});

test("CultMesh TS document handles submit predictions through configured authority hooks", async () => {
  let current = {
    noteId: "note:prediction",
    body: "initial",
  };
  const predictions: string[] = [];
  const contexts: string[] = [];
  const document = CultMesh.document(
    "cultmesh.note:prediction",
    noteDocument,
    async () => current,
    {
      routeHint: CultMesh.routeHint("network", "CultNet prediction"),
      submitPrediction: async (context, value) => {
        contexts.push(`${context.runtimeId}:${context.routeHint.kind}`);
        predictions.push(value.body);
        current = value;
      },
    },
  );
  const verse = CultMesh.verse("starbridge", "pilot-a");
  const bound = document.bind(verse);
  const catalog = CultMesh.documents(document);

  assert.equal(document.canReplace, false);
  assert.equal(document.canSubmitPrediction, true);
  assert.equal(document.canSet, true);
  assert.equal(bound.canSubmitPrediction, true);
  assert.equal(bound.canSet, true);
  assert.equal(catalog.canSubmitPrediction(noteAliasDocument), true);
  assert.equal(catalog.canSet(noteAliasDocument), true);

  await catalog.set(noteAliasDocument, "pilot-a", {
    noteId: "note:prediction",
    body: "predicted",
  }, {
    parse: value => noteAliasDocument.schema.parse(value),
  });

  assert.deepEqual(contexts, ["pilot-a:network"]);
  assert.deepEqual(predictions, ["predicted"]);
  assert.equal((await bound.latest()).body, "predicted");

  const alias = bound.asSchemaAlias(noteAliasDocument, {
    parse: value => noteAliasDocument.schema.parse(value),
  });
  assert.equal(alias.canSubmitPrediction, true);
  await alias.set({
    noteId: "note:prediction",
    body: "alias-predicted",
  });

  assert.deepEqual(predictions, ["predicted", "alias-predicted"]);
  assert.equal((await document.latest("pilot-a")).body, "alias-predicted");
  const updated = await document.update("pilot-a", value => ({
    ...value,
    body: "updated-as-prediction",
  }));
  assert.equal(updated.body, "updated-as-prediction");
  assert.deepEqual(predictions, ["predicted", "alias-predicted", "updated-as-prediction"]);

  assert.throws(
    () => document.replace({
      noteId: "note:prediction",
      body: "not-authoritative",
    }),
    /does not support replacement/,
  );
});

test("CultMesh TS store document handles choose compatible foreign schema payloads", async () => {
  const store = {
    async pullAll() {
      return [
        {
          key: "note:foreign",
          type: "runtime.generated.incompatible-note",
          schemaId: "runtime.generated.incompatible-note.v1",
          storedAt: "2026-06-27T00:00:00Z",
          payload: encode({ nope: "not the requested shape" }),
          catalogEntry: {
            schemaId: "runtime.generated.incompatible-note.v1",
            schemaName: "runtime.generated.incompatible-note",
            schemaVersion: "runtime.generated.incompatible-note.v1",
            contentHash: "runtime.generated.incompatible-note.v1",
            canonicalSchemaJson: "{}",
            compatibleSchemaIds: ["runtime.generated.incompatible-note.v1"],
          },
        },
        {
          key: "note:foreign",
          type: "runtime.generated.cultmesh.note",
          schemaId: "runtime.generated.cultmesh.note.ui.42",
          storedAt: "2026-06-27T00:00:01Z",
          payload: encode({
            noteId: "note:foreign",
            body: "foreign schema id, local store shape",
          }),
          catalogEntry: {
            schemaId: "runtime.generated.cultmesh.note.ui.42",
            schemaName: "cultmesh.note",
            schemaVersion: "cultmesh.note.v1",
            contentHash: "runtime.generated.cultmesh.note.ui.42",
            canonicalSchemaJson: "{}",
            compatibleSchemaIds: ["runtime.generated.cultmesh.note.ui.42"],
          },
        },
      ];
    },
    async push() {},
    async delete() {},
  };

  const document = CultMesh.documentFromStore(store, noteAliasDocument, {
    documentId: "note:foreign.store",
  });

  assert.deepEqual(await document.latest(), {
    noteId: "note:foreign",
    body: "foreign schema id, local store shape",
  });
});

test("CultMesh TS reactive documents submit predictions from member writes", async () => {
  let current = {
    noteId: "note:reactive",
    body: "initial",
  };
  const predictions: string[] = [];
  const document = CultMesh.document(
    "cultmesh.note:reactive",
    noteDocument,
    async () => current,
    {
      routeHint: CultMesh.routeHint("network", "CultNet prediction"),
      submitPrediction: async (_context, value) => {
        predictions.push(value.body);
        current = value;
      },
    },
  );

  const reactive = document.reactive<typeof current>({
    watch: false,
  });
  await reactive.ready;
  reactive.current.body = "member-write-prediction";
  reactive.current.body = "member-write-prediction-final";
  await waitFor(
    () => predictions.includes("member-write-prediction-final"),
    "reactive member write prediction",
  );

  assert.equal(current.body, "member-write-prediction-final");
  assert.deepEqual(predictions, ["member-write-prediction-final"]);
  reactive.dispose();
  reactive.current.body = "after-dispose";
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.deepEqual(predictions, ["member-write-prediction-final"]);
});

test("CultMesh TS node opens same-schema aliases with one call", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-node-reactive-")), "node.ccmp");
  const node = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });
  await node.put(noteDocument, "note:node-reactive", {
    noteId: "note:node-reactive",
    body: "initial",
  });

  const aliasHandle = node.document(noteAliasDocument, "note:node-reactive", {
    pollMs: 5,
  });
  assert.equal(aliasHandle.documentId, "cultmesh.note.ui:note:node-reactive");
  assert.equal((await aliasHandle.latest()).body, "initial");
  await aliasHandle.set({
    noteId: "note:node-reactive",
    body: "edited-through-alias-handle",
  });
  assert.equal(
    node.getRequired(noteAliasDocument, "note:node-reactive").body,
    "edited-through-alias-handle",
  );

  await node.put(noteAliasDocument, "note:node-reactive", {
    noteId: "note:node-reactive",
    body: "edited-through-alias-put",
  });
  assert.equal(
    node.getRequired(noteDocument, "note:node-reactive").body,
    "edited-through-alias-put",
  );
  assert.deepEqual(
    (await node.collection(noteAliasDocument).latest()).map(note => note.body),
    ["edited-through-alias-put"],
  );

  const reactive = node.reactiveDocument(noteAliasDocument, "note:node-reactive", {
    context: CultMesh.queryContext("browser-client"),
    watch: false,
  });
  await reactive.ready;
  reactive.current.body = "edited-through-node-helper";
  await waitFor(
    () => node.getRequired(noteDocument, "note:node-reactive").body === "edited-through-node-helper",
    "node-level reactive alias write",
  );

  assert.equal(reactive.current.noteId, "note:node-reactive");
  assert.equal(
    node.document(noteDocument, "note:node-reactive").asSchemaAlias(noteAliasDocument, {
      parse: value => noteAliasDocument.schema.parse(value),
    }).canSet,
    true,
  );
  assert.equal(
    (await CultMesh.reactiveDocument(node, noteAliasDocument, "note:node-reactive", {
      watch: false,
    }).ready).body,
    "edited-through-node-helper",
  );
  reactive.dispose();
});

test("CultMesh TS reactive documents store reconciliation deltas after misprediction", async () => {
  let current = {
    noteId: "note:reconcile",
    body: "initial",
    revision: 1,
  };
  let watcher: ((value: typeof current) => void) | undefined;
  const predictions: typeof current[] = [];
  const document = CultMesh.document(
    "cultmesh.note:reconcile",
    { schemaId: "cultmesh.note.reconcile.v0" },
    async () => current,
    {
      routeHint: CultMesh.routeHint("network", "CultNet prediction"),
      submitPrediction: async (_context, value) => {
        predictions.push(value);
        current = value;
      },
      watchDocument: (_context, callback) => {
        watcher = callback;
        return () => {
          watcher = undefined;
        };
      },
    },
  );

  const reactive = document.reactive<typeof current>();
  await reactive.ready;
  reactive.current.body = "predicted";
  reactive.current.revision = 7;
  await waitFor(() => predictions.length === 1, "reactive reconciliation prediction");

  watcher?.({
    noteId: "note:reconcile",
    body: "authoritative",
    revision: 5,
  });

  assert.equal(reactive.current.body, "authoritative");
  assert.equal(reactive.current.revision, 5);
  assert.equal(reactive.reconciliation?.version, 1);
  assert.deepEqual(reactive.reconciliation?.canonical, {
    noteId: "note:reconcile",
    body: "authoritative",
    revision: 5,
  });
  assert.deepEqual(reactive.reconciliation?.predicted, {
    noteId: "note:reconcile",
    body: "predicted",
    revision: 7,
  });
  assert.deepEqual(reactive.reconciliation?.delta, {
    body: "predicted",
    revision: 2,
  });

  reactive.clearReconciliation();
  assert.equal(reactive.reconciliation, undefined);
  reactive.dispose();
});

test("CultMesh TS reactive documents coalesce same-frame member writes", async () => {
  let current = {
    noteId: "note:reactive-frame",
    body: "initial",
  };
  const predictions: string[] = [];
  const previousRequestAnimationFrame = globalThis.requestAnimationFrame;
  const frameCallbacks: FrameRequestCallback[] = [];
  globalThis.requestAnimationFrame = callback => {
    frameCallbacks.push(callback);
    return frameCallbacks.length;
  };
  const document = CultMesh.document(
    "cultmesh.note:reactive-frame",
    noteDocument,
    async () => current,
    {
      submitPrediction: async (_context, value) => {
        predictions.push(value.body);
        current = value;
      },
    },
  );

  const reactive = document.reactive<typeof current>({
    watch: false,
  });
  try {
    await reactive.ready;
    reactive.current.body = "frame-1";
    reactive.current.body = "frame-2";
    reactive.current.body = "frame-3";

    assert.equal(frameCallbacks.length, 1);
    frameCallbacks[0](performance.now());
    await waitFor(() => predictions.length === 1, "same-frame prediction");

    assert.deepEqual(predictions, ["frame-3"]);
    assert.equal(current.body, "frame-3");
  } finally {
    reactive.dispose();
    if (previousRequestAnimationFrame) {
      globalThis.requestAnimationFrame = previousRequestAnimationFrame;
    } else {
      delete (globalThis as unknown as {
        requestAnimationFrame?: (callback: FrameRequestCallback) => number;
      }).requestAnimationFrame;
    }
  }
});

test("CultMesh TS reactive documents queue edits made while a prediction is in flight", async () => {
  let current = {
    noteId: "note:reactive-in-flight",
    body: "initial",
  };
  let releaseFirstPrediction!: () => void;
  let resolveFirstPredictionStarted!: () => void;
  const firstPredictionStarted = new Promise<void>(resolve => {
    resolveFirstPredictionStarted = resolve;
  });
  const predictions: typeof current[] = [];
  const document = CultMesh.document(
    "cultmesh.note:reactive-in-flight",
    noteDocument,
    async () => current,
    {
      submitPrediction: async (_context, value) => {
        predictions.push(value);
        if (predictions.length === 1) {
          resolveFirstPredictionStarted();
          await new Promise<void>(release => {
            releaseFirstPrediction = release;
          });
        }
        current = value;
      },
    },
  );

  const reactive = document.reactive<typeof current>({
    watch: false,
    debounceMs: 0,
  });
  await reactive.ready;
  reactive.current.body = "first";
  await firstPredictionStarted;
  reactive.current.body = "second";
  releaseFirstPrediction();
  await waitFor(() => predictions.length === 2, "in-flight queued prediction");

  assert.deepEqual(predictions.map(value => value.body), ["first", "second"]);
  assert.equal(current.body, "second");
  reactive.dispose();
});

test("CultMesh TS reactive read-only documents surface mutation failures and keep watching", async () => {
  let current = {
    noteId: "note:reactive-readonly",
    body: "initial",
  };
  let watcher: ((value: typeof current) => void) | undefined;
  const errors: string[] = [];
  const document = CultMesh.document(
    "cultmesh.note:reactive-readonly",
    noteDocument,
    async () => current,
    {
      watchDocument: (_context, callback) => {
        watcher = callback;
        return () => {
          watcher = undefined;
        };
      },
    },
  );

  const reactive = document.reactive<typeof current>({
    onError: error => errors.push(String((error as Error).message ?? error)),
  });
  await reactive.ready;
  reactive.current.body = "local-readonly-edit";
  await waitFor(() => errors.length === 1, "read-only mutation error");

  current = {
    noteId: "note:reactive-readonly",
    body: "canonical-after-error",
  };
  watcher?.(current);

  assert.match(errors[0], /does not support mutation/);
  assert.equal(reactive.current.body, "canonical-after-error");
  reactive.dispose();
});

test("CultMesh TS document handles read schema publications from single-file stores", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-")), "publication.ccmp");
  const node = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });
  await node.put(noteDocument, "note:publication", {
    noteId: "note:publication",
    body: "published",
  });
  await node.flush();

  const document = CultMesh.documentFromPublication({
    kind: "single-file",
    path: filePath,
  }, noteDocument, "note:publication", {
    documentId: "daemon:cultmesh.note.latest",
    sourceId: "daemon:cultmesh.note.latest.v0",
    pollMs: 50,
  });
  const observed: string[] = [];
  const unsubscribe = document.watch(value => {
    observed.push(value.body);
  });

  try {
    assert.equal((await document.latest()).body, "published");
    assert.equal(document.schema.type, "cultmesh.note");

    const republisher = await CultMesh.startNode(filePath, {
      documents: [noteDocument],
    });
    await republisher.put(noteDocument, "note:publication", {
      noteId: "note:publication",
      body: "republished",
    });
    await waitFor(() => observed.includes("republished"), "single-file document watch update");
  } finally {
    unsubscribe();
  }

  assert.equal(document.documentId, "daemon:cultmesh.note.latest");
  assert.equal(document.routeHint.kind, "shared-memory");
  assert.deepEqual(document.sources.map(source => source.schemaId), [
    "cultmesh.note.v0",
  ]);
});

test("CultMesh TS projects live provider publications as typed document handles", async () => {
  let current: unknown = {
    noteId: "note:live-provider",
    body: "initial publication",
  };
  let watcher: ((value: unknown) => void) | undefined;
  const calls: string[] = [];

  const document = CultMesh.documentFromPublication(
    {
      kind: "live-publication",
      endpoint: "provider-session:aetheria",
      source: {
        latest: async (schemaId, recordKey) => {
          calls.push(`latest:${schemaId}:${recordKey}`);
          return current;
        },
        watch: (schemaId, recordKey, callback) => {
          calls.push(`watch:${schemaId}:${recordKey}`);
          watcher = callback;
          return () => {
            calls.push("unsubscribed");
            watcher = undefined;
          };
        },
      },
    },
    noteDocument,
    "note:live-provider",
    { documentId: "aetheria.note.live" },
  );

  assert.deepEqual(await document.latest(), current);
  assert.equal(document.routeHint.kind, "network");
  assert.equal(document.routeHint.description, "provider-session:aetheria");

  const observed: string[] = [];
  const unsubscribe = document.watch(value => observed.push(value.body));
  current = {
    noteId: "note:live-provider",
    body: "ordered live upsert",
  };
  watcher?.(current);
  unsubscribe();

  assert.deepEqual(observed, ["ordered live upsert"]);
  assert.deepEqual(calls, [
    "latest:cultmesh.note.v0:note:live-provider",
    "watch:cultmesh.note.v0:note:live-provider",
    "unsubscribed",
  ]);
  assert.equal(watcher, undefined);
});

test("CultMesh TS validates live provider publications through typed document schemas", async () => {
  const document = CultMesh.documentFromPublication(
    {
      kind: "live-publication",
      source: {
        latest: async () => ({ noteId: 42, body: "invalid" }),
        watch: () => () => undefined,
      },
    },
    noteDocument,
    "note:invalid-live-provider",
  );

  await assert.rejects(document.latest(), /noteId/);
});

test("CultMesh TS binds publication document catalogs from source resolvers", async () => {
  const root = await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-catalog-"));
  const firstPath = join(root, "first.ccmp");
  const secondPath = join(root, "second.ccmp");
  const first = await CultMesh.startNode(firstPath, {
    documents: [noteDocument],
  });
  const second = await CultMesh.startNode(secondPath, {
    documents: [noteDocument],
  });
  await first.put(noteDocument, "note:first", {
    noteId: "note:first",
    body: "first source",
  });
  await second.put(noteDocument, "note:second", {
    noteId: "note:second",
    body: "second source",
  });
  await first.flush();
  await second.flush();

  const bindings = [
    CultMesh.publicationDocument(noteDocument, "note:first", {
      documentId: "daemon:first",
      sourceId: "daemon:first.latest",
    }),
    CultMesh.publicationDocument(noteDocument, "note:second", {
      documentId: "daemon:second",
      sourceId: "daemon:second.latest",
    }),
  ];
  const paths = new Map([
    ["note:first", firstPath],
    ["note:second", secondPath],
  ]);
  const catalog = CultMesh.documentsFromPublication(
    binding => ({
      kind: "single-file",
      path: paths.get(binding.recordKey) ?? firstPath,
    }),
    bindings,
    {
      routeHint: CultMesh.routeHint("shared-memory", "publication catalog"),
      pollMs: 50,
    },
  );

  assert.deepEqual(
    catalog.documents.map(document => document.documentId),
    ["daemon:first", "daemon:second"],
  );
  assert.equal(
    (await catalog.document(noteAliasDocument, {
      parse: value => noteAliasDocument.schema.parse(value),
    }).latest()).body,
    "second source",
  );
  assert.equal(catalog.document(noteDocument).routeHint.description, "publication catalog");
});

test("CultMesh TS syncs configured publications into local node aliases", async () => {
  const sourcePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-sync-source-")), "source.ccmp");
  const targetPath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-sync-target-")), "target.ccmp");
  const source = await CultMesh.startNode(sourcePath, {
    documents: [noteDocument],
  });
  await source.put(noteDocument, "note:published", {
    noteId: "note:published",
    body: "publication source hydrates local alias",
  });
  await source.flush();
  const target = await CultMesh.startNode(targetPath, {
    documents: [noteDocument],
  });

  const synced = await target.syncDocumentFromPublication(
    {
      kind: "single-file",
      path: sourcePath,
    },
    noteAliasDocument,
    "note:published",
  );
  const facadeSynced = await CultMesh.syncDocumentFromPublication(
    target,
    {
      kind: "single-file",
      path: sourcePath,
    },
    noteAliasDocument,
    "note:published",
  );

  assert.deepEqual(synced, {
    noteId: "note:published",
    body: "publication source hydrates local alias",
  });
  assert.deepEqual(facadeSynced, synced);
  assert.equal(target.getRequired(noteDocument, "note:published").body, "publication source hydrates local alias");
  assert.equal(target.getRequired(noteAliasDocument, "note:published").body, "publication source hydrates local alias");
  assert.equal((await target.reactiveDocument(noteAliasDocument, "note:published", { watch: false }).ready).body, "publication source hydrates local alias");
});

test("CultMesh TS syncs configured publication catalogs into local node aliases", async () => {
  const firstPath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-sync-catalog-first-")), "first.ccmp");
  const secondPath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-sync-catalog-second-")), "second.ccmp");
  const targetPath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-publication-sync-catalog-target-")), "target.ccmp");
  const first = await CultMesh.startNode(firstPath, {
    documents: [noteDocument],
  });
  const second = await CultMesh.startNode(secondPath, {
    documents: [noteDocument],
  });
  await first.put(noteDocument, "note:first-sync", {
    noteId: "note:first-sync",
    body: "first synced publication",
  });
  await second.put(noteDocument, "note:second-sync", {
    noteId: "note:second-sync",
    body: "second synced publication",
  });
  await first.flush();
  await second.flush();
  const target = await CultMesh.startNode(targetPath, {
    documents: [noteDocument],
  });
  const bindings = [
    CultMesh.publicationDocument(noteDocument, "note:first-sync", {
      documentId: "local:first",
    }),
    CultMesh.publicationDocument(noteAliasDocument, "note:second-sync", {
      documentId: "local:second",
      source: {
        kind: "single-file",
        path: secondPath,
      },
    }),
  ];

  const catalog = await target.syncDocumentsFromPublication(
    {
      kind: "single-file",
      path: firstPath,
    },
    bindings,
    {
      routeHint: CultMesh.routeHint("shared-memory", "publication sync catalog"),
      pollMs: 5,
    },
  );
  const facadeCatalog = await CultMesh.syncDocumentsFromPublication(
    target,
    {
      kind: "single-file",
      path: firstPath,
    },
    bindings,
  );

  assert.deepEqual(
    catalog.documents.map(document => document.documentId),
    ["local:first", "local:second"],
  );
  assert.equal(
    await catalog.document(noteDocument, {
      parse: value => noteDocument.schema.parse(value),
    }).latest().then(value => value.body),
    "first synced publication",
  );
  assert.equal(
    await catalog.document(noteAliasDocument, {
      parse: value => noteAliasDocument.schema.parse(value),
    }).latest().then(value => value.body),
    "second synced publication",
  );
  assert.equal(catalog.document(noteAliasDocument).routeHint.description, "publication sync catalog");
  assert.equal(target.getRequired(noteDocument, "note:second-sync").body, "second synced publication");
  assert.equal(target.getRequired(noteAliasDocument, "note:second-sync").body, "second synced publication");
  assert.equal(
    await facadeCatalog.document(noteAliasDocument, {
      parse: value => noteAliasDocument.schema.parse(value),
    }).latest().then(value => value.body),
    "second synced publication",
  );
});

test("CultMesh TS document catalogs resolve semantic schema versions passed as schema ids", async () => {
  const current = {
    noteId: "note:semantic",
    body: "semantic alias",
  };
  const document = CultMesh.document(
    "daemon:semantic-note",
    {
      schemaId: "sha256:runtime-generated-note",
      schemaName: "cultmesh.note",
      schemaVersion: "cultmesh.note.v1",
    },
    async () => current,
    {
      routeHint: CultMesh.routeHint("shared-memory", "semantic alias catalog"),
    },
  );
  const catalog = CultMesh.documents(document);

  const alias = catalog.document({
    schemaId: "cultmesh.note.v1",
  });

  assert.equal(alias.documentId, "daemon:semantic-note");
  assert.equal(alias.routeHint.description, "semantic alias catalog");
  assert.deepEqual(await alias.latest(), current);
});

test("CultMesh TS collection handles expose typed snapshots and reset watches", async () => {
  const filePath = join(await mkdtemp(join(tmpdir(), "cultmesh-ts-coll-")), "node.ccmp");
  const node = await CultMesh.startNode(filePath, {
    documents: [noteDocument],
  });
  await node.put(noteDocument, "note:a", {
    noteId: "note:a",
    body: "alpha",
  });

  const collection = node.collection(noteDocument, { pollMs: 5 });
  const bound = CultMesh.bindCollection(
    CultMesh.verse("local", "rts-client").withRoute("shared-memory", "local cache"),
    collection,
  );
  const changes: string[] = [];
  const unsubscribe = bound.watchChanges(change => changes.push(change.kind));

  assert.deepEqual((await bound.latest()).map(note => note.body), ["alpha"]);

  await node.put(noteDocument, "note:b", {
    noteId: "note:b",
    body: "bravo",
  });
  await waitFor(() => changes.length >= 2, "collection reset after update");
  unsubscribe();

  assert.deepEqual(
    (await collection.asSchemaAlias(noteAliasDocument, {
      parse: value => noteAliasDocument.schema.parse(value),
    }).latest()).map(note => note.body).sort(),
    ["alpha", "bravo"],
  );
  const catalog = CultMesh.collections(collection);
  assert.deepEqual(
    (await catalog.latest(noteAliasDocument, "local", {
      parse: value => noteAliasDocument.schema.parse(value),
    })).map(note => note.body).sort(),
    ["alpha", "bravo"],
  );
  assert.equal(
    catalog.collection({ schemaId: "cultmesh.note.v0" }).collectionId,
    collection.collectionId,
  );
  assert.equal(catalog.tryCollection({ schemaId: "cultmesh.missing.v1" }), undefined);
  assert.ok(changes.every(kind => kind === "reset"));
});

test("CultMesh TS collection catalogs resolve same-schema alias watches", async () => {
  const current = [{
    noteId: "note:catalog-watch",
    body: "initial",
  }];
  let watcher: ((change: { kind: "updated"; value: typeof current[number] }) => void) | undefined;
  const collection = CultMesh.collection(
    "daemon:catalog-watch",
    {
      schemaId: "sha256:runtime-generated-note",
      schemaName: "cultmesh.note",
      schemaVersion: "cultmesh.note.v1",
    },
    async () => current,
    {
      routeHint: CultMesh.routeHint("shared-memory", "collection catalog"),
      watchCollection: (_context, callback) => {
        watcher = callback as typeof watcher;
        return () => {
          watcher = undefined;
        };
      },
    },
  );
  const catalog = CultMesh.collections(collection);
  const observed: string[] = [];
  const semanticAlias = { schemaId: "cultmesh.note.v1" };
  const unsubscribe = catalog.watchChanges<z.infer<typeof noteAliasDocument.schema>>(semanticAlias, change => {
    if (change.value) {
      observed.push(change.value.body);
    }
  }, {
    parse: value => noteAliasDocument.schema.parse(value),
  });

  watcher?.({
    kind: "updated",
    value: {
      noteId: "note:catalog-watch",
      body: "through-catalog-alias",
    },
  });
  unsubscribe();

  assert.deepEqual(observed, ["through-catalog-alias"]);
  assert.equal(watcher, undefined);
  assert.equal(catalog.collection(semanticAlias).routeHint.description, "collection catalog");
});

test("CultMesh TS local authority leases do not trust peer cards by contact alone", () => {
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  const peerUpdates: string[] = [];
  const unsubscribePeer = peers.watch((peerCard) => {
    peerUpdates.push(peerCard.peerId);
  });
  const peer = {
    peerId: "voidbot-local",
    verseId: "local",
    endpoints: ["cultmesh://localhost"],
    roles: ["shard-primary"],
    authorityLeaseId: "lease:voidbot-local",
  };

  peers.upsert(peer);
  unsubscribePeer();
  peers.upsert({
    peerId: "unused-peer",
    verseId: "local",
    endpoints: ["cultmesh://unused"],
  });

  assert.deepEqual(peerUpdates, ["voidbot-local"]);
  assert.equal(peers.get("voidbot-local"), peer);
  assert.deepEqual(peers.find("local", "shard-primary"), [peer]);
  assert.equal(leases.isAuthorized(peer, "shard-primary"), false);
  assert.deepEqual(
    peers.findAuthorized("local", "shard-primary", leases),
    [],
  );

  const leaseUpdates: string[] = [];
  const unsubscribeLease = leases.watch((lease) => {
    leaseUpdates.push(lease.leaseId);
  });
  const lease = {
    leaseId: "lease:voidbot-local",
    verseId: "local",
    peerId: "voidbot-local",
    roles: ["shard-primary"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  };
  leases.upsert(lease);
  unsubscribeLease();
  leases.upsert({ ...lease, roles: ["shard-primary", "schema"] });

  assert.deepEqual(leaseUpdates, ["lease:voidbot-local"]);
  assert.equal(leases.get("lease:voidbot-local")?.peerId, "voidbot-local");
  assert.deepEqual(leases.leases.map((lease) => lease.leaseId), [
    "lease:voidbot-local",
  ]);
  assert.equal(leases.isAuthorized(peer, "shard-primary"), true);
  assert.deepEqual(
    peers.findAuthorized("local", "shard-primary", leases),
    [peer],
  );
  assert.equal(
    peers.firstAuthorized("local", "shard-primary", leases),
    peer,
  );
  assert.equal(peers.firstAuthorized("local", "schema", leases), undefined);
});

test("CultMesh TS local Verse catalog exposes sorted views and watch ergonomics", () => {
  const verses = CultMesh.createVerseCatalog<{ verseId: string; label: string }>();
  const updates: string[] = [];
  const unsubscribe = verses.watch((verse) => {
    updates.push(verse.verseId);
  });

  const publicVerse = { verseId: "public", label: "Public Verse" };
  const privateVerse = { verseId: "private", label: "Private Verse" };
  verses.upsert(publicVerse.verseId, publicVerse);
  verses.upsert(privateVerse.verseId, privateVerse);
  unsubscribe();
  verses.upsert("scratch", { verseId: "scratch", label: "Scratch" });

  assert.deepEqual(updates, ["public", "private"]);
  assert.equal(verses.get("public"), publicVerse);
  assert.deepEqual(
    verses.verses.map((verse) => verse.verseId),
    ["private", "public", "scratch"],
  );
});

test("CultMesh TS exposes shared geometry query primitives", () => {
  const viewport = CultMesh.rect(CultMesh.vec2(10, -5), CultMesh.vec2(-2, 7));
  assert.deepEqual(viewport, {
    min: { x: -2, y: -5 },
    max: { x: 10, y: 7 },
  });

  assert.deepEqual(CultMesh.viewportRequest(viewport, [3, 1]), {
    minX: -2,
    minY: -5,
    maxX: 10,
    maxY: 7,
    controlledEntityIndices: [3, 1],
  });

  assert.deepEqual(CultMesh.rectFromBounds(4, 3, 2, 1), {
    min: { x: 2, y: 1 },
    max: { x: 4, y: 3 },
  });
});

test("CultMesh TS exposes typed operation primitives", async () => {
  const operation = CultMesh.operation<
    { actor: string; direction: { x: number; y: number } },
    { receipt: ReturnType<typeof CultMesh.operationReceipt>; actor: string }
  >("game.test.move", async (request, context) => {
    assert.equal(context.runtimeId, "rts-client");
    assert.deepEqual(context.claims, [
      { role: "simulation-authority", shardId: "zone:0", leaseId: "lease:zone:0" },
    ]);
    assert.deepEqual(context.routeHint, {
      kind: "network",
      description: "remote Verse node",
    });
    assert.equal(context.idempotencyKey, "move-1");
    return {
      receipt: CultMesh.operationReceipt("game.test.move", true, {
        route: context.routeHint,
      }),
      actor: request.actor,
    };
  });

  const response = await operation.invoke(
    { actor: "pawn:1", direction: CultMesh.vec2(1, 0) },
    CultMesh.operationContext("rts-client", {
      claims: [
        CultMesh.authorityClaim("simulation-authority", {
          shardId: "zone:0",
          leaseId: "lease:zone:0",
        }),
      ],
      routeHint: CultMesh.routeHint("network", "remote Verse node"),
      idempotencyKey: "move-1",
    }),
  );

  assert.equal(operation.operationId, "game.test.move");
  assert.equal(response.actor, "pawn:1");
  assert.deepEqual(CultMesh.describeOperationHandle(operation), {
    operationId: "game.test.move",
  });
  assert.deepEqual(response.receipt, {
    operationId: "game.test.move",
    accepted: true,
    route: {
      kind: "network",
      description: "remote Verse node",
    },
    diagnostic: undefined,
  });
});

test("CultMesh TS flattens and parses cross-runtime route records", () => {
  const record = CultMesh.routeRecord(
    CultMesh.routeHint("shared-memory", "co-located slab"),
  );
  const restored = CultMesh.routeFromRecord(record);
  const fromCsharp = CultMesh.routeFromRecord(
    {
      kind: "InProcess",
      description: "",
    },
    CultMesh.routeHint("network", "fallback route"),
  );
  const fallback = CultMesh.routeFromRecord(
    {
      kind: "not-real",
      description: "",
    },
    CultMesh.routeHint("ipc", "fallback route"),
  );

  assert.deepEqual(record, {
    kind: "shared-memory",
    description: "co-located slab",
  });
  assert.deepEqual(restored, {
    kind: "shared-memory",
    description: "co-located slab",
  });
  assert.deepEqual(fromCsharp, {
    kind: "in-process",
    description: "fallback route",
  });
  assert.deepEqual(fallback, {
    kind: "ipc",
    description: "fallback route",
  });
});

test("CultMesh TS exposes typed query primitives", async () => {
  const watchedCounts: number[] = [];
  const visibleObjects = CultMesh.query<
    { viewport: ReturnType<typeof CultMesh.viewportRequest> },
    { queryId: string; count: number }
  >("game.test.visible_objects", async (parameters, context) => {
    assert.equal(context.runtimeId, "browser-client");
    assert.deepEqual(context.routeHint, {
      kind: "shared-memory",
      description: "co-located daemon slab",
    });
    assert.equal(parameters.viewport.minX, -10);
    return {
      queryId: "game.test.visible_objects",
      count: 2,
    };
  }, {
    watchQuery: (parameters, context, callback) => {
      assert.equal(context.runtimeId, "browser-client");
      assert.equal(parameters.viewport.maxX, 10);
      callback({ queryId: "game.test.visible_objects", count: 3 });
      return () => watchedCounts.push(-1);
    },
  });

  const result = await visibleObjects.execute(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-10, -5, 10, 5)) },
    CultMesh.queryContext("browser-client", {
      routeHint: CultMesh.routeHint("shared-memory", "co-located daemon slab"),
    }),
  );

  assert.deepEqual(result, {
    queryId: "game.test.visible_objects",
    count: 2,
  });

  const diagnostic = CultMesh.describeQuerySurface(visibleObjects);
  assert.equal(diagnostic.queryId, "game.test.visible_objects");
  assert.equal(diagnostic.routeHint.kind, "automatic");
  assert.deepEqual(diagnostic.sources, []);
  assert.notEqual(diagnostic.sources, visibleObjects.sources);

  const unsubscribe = visibleObjects.watch(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-10, -5, 10, 5)) },
    "browser-client",
    (value) => watchedCounts.push(value.count),
  );
  unsubscribe();

  assert.deepEqual(watchedCounts, [3, -1]);
});

test("CultMesh TS polling watcher turns snapshots into a query watch", async () => {
  let value = 0;
  const observed: number[] = [];
  const query = CultMesh.query<void, number>(
    "game.test.counter",
    async () => value,
    {
      watchQuery: CultMesh.pollingQueryWatcher(async () => value, {
        intervalMs: 5,
        equals: (left, right) => left === right,
      }),
    },
  );

  const unsubscribe = query.watch(undefined, "browser-client", next => observed.push(next));
  await delay(15);
  value = 1;
  await delay(20);
  value = 1;
  await delay(12);
  value = 2;
  await delay(20);
  unsubscribe();
  value = 3;
  await delay(12);

  assert.deepEqual(observed, [0, 1, 2]);
});

test("CultMesh TS polling watcher can suppress the initial baseline", async () => {
  let value = "baseline";
  const observed: string[] = [];
  const watch = CultMesh.pollingQueryWatcher<void, string>(async () => value, {
    intervalMs: 5,
    emitInitial: false,
  });

  const unsubscribe = watch(undefined, CultMesh.queryContext("browser-client"), next => observed.push(next));
  await delay(15);
  value = "changed";
  await delay(15);
  unsubscribe();

  assert.deepEqual(observed, ["changed"]);
});

test("CultMesh TS exposes live feed primitives for reactive client views", async () => {
  let frameId = 10;
  const observed: number[] = [];
  const feed = CultMesh.liveFeed<
    { viewport: ReturnType<typeof CultMesh.viewportRequest> },
    { frameId: number; route: string }
  >(
    "aetheria.rts.viewport.feed",
    async (_parameters, context) => ({
      frameId,
      route: context.routeHint.kind,
    }),
    {
      sources: [
        CultMesh.projectionSource("daemon:aetheria.frame.latest.v1"),
        CultMesh.projectionSource("daemon:aetheria.health.latest.v1"),
      ],
      routeHint: CultMesh.routeHint("shared-memory", "co-located RTS cache"),
      watchFeed: CultMesh.pollingQueryWatcher(async (_parameters, context) => ({
        frameId,
        route: context.routeHint.kind,
      }), {
        intervalMs: 5,
        equals: (left, right) => left.frameId === right.frameId && left.route === right.route,
      }),
    },
  );

  assert.equal(feed.feedId, "aetheria.rts.viewport.feed");
  assert.equal(feed.routeHint.kind, "shared-memory");
  assert.deepEqual(feed.sources.map(source => source.sourceId), [
    "daemon:aetheria.frame.latest.v1",
    "daemon:aetheria.health.latest.v1",
  ]);

  const diagnostic = CultMesh.describeLiveFeed(feed);
  assert.equal(diagnostic.feedId, "aetheria.rts.viewport.feed");
  assert.equal(diagnostic.routeHint.description, "co-located RTS cache");
  assert.deepEqual(diagnostic.sources.map(source => source.sourceId), [
    "daemon:aetheria.frame.latest.v1",
    "daemon:aetheria.health.latest.v1",
  ]);
  assert.notEqual(diagnostic.sources, feed.sources);

  const snapshot = await feed.snapshot(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-1, -1, 1, 1)) },
    "browser-client",
  );

  assert.deepEqual(snapshot, {
    frameId: 10,
    route: "shared-memory",
  });

  const unsubscribe = feed.watch(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-1, -1, 1, 1)) },
    "browser-client",
    value => observed.push(value.frameId),
  );
  await delay(15);
  frameId = 11;
  await delay(15);
  unsubscribe();

  assert.deepEqual(observed, [10, 11]);
});

test("CultMesh TS exposes projection recipes as shared query affordances", async () => {
  const watchedRoutes: string[] = [];
  const recipe = CultMesh.projectionRecipe<
    { viewport: ReturnType<typeof CultMesh.viewportRequest> },
    { projectionId: string; sourceCount: number; route: string }
  >(
    "aetheria.zone.objects.visible",
    [
      CultMesh.projectionSource("daemon:aetheria.frame.latest.v1", {
        schemaId: "gamecult.aetheria.daemon_frame.v1",
        description: "latest daemon frame",
      }),
      CultMesh.projectionSource("daemon:aetheria.authority.policy.v1", {
        schemaId: "gamecult.aetheria.authority_policy.v1",
      }),
    ],
    async (_parameters, context) => ({
      projectionId: "aetheria.zone.objects.visible",
      sourceCount: 2,
      route: context.routeHint.kind,
    }),
    {
      routeHint: CultMesh.routeHint("shared-memory", "co-located frame slab"),
      watchProjection: (_parameters, context, callback) => {
        callback({
          projectionId: "aetheria.zone.objects.visible",
          sourceCount: 2,
          route: context.routeHint.kind,
        });
        return () => watchedRoutes.push("unsubscribed");
      },
    },
  );

  assert.equal(recipe.projectionId, "aetheria.zone.objects.visible");
  assert.equal(recipe.routeHint.kind, "shared-memory");
  assert.deepEqual(recipe.sources.map((source) => source.sourceId), [
    "daemon:aetheria.frame.latest.v1",
    "daemon:aetheria.authority.policy.v1",
  ]);

  const recipeDiagnostic = CultMesh.describeProjectionRecipe(recipe);
  assert.equal(recipeDiagnostic.projectionId, "aetheria.zone.objects.visible");
  assert.equal(recipeDiagnostic.routeHint.description, "co-located frame slab");
  assert.deepEqual(recipeDiagnostic.sources.map((source) => source.schemaId), [
    "gamecult.aetheria.daemon_frame.v1",
    "gamecult.aetheria.authority_policy.v1",
  ]);
  assert.notEqual(recipeDiagnostic.sources, recipe.sources);

  const projected = await recipe.project(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-10, -5, 10, 5)) },
    CultMesh.queryContextFor("browser-starfire")
      .route("wasm", "browser-local projection")
      .build(),
  );

  assert.deepEqual(projected, {
    projectionId: "aetheria.zone.objects.visible",
    sourceCount: 2,
    route: "wasm",
  });

  const query = recipe.asQuerySurface();
  assert.equal(query.routeHint.kind, "shared-memory");
  assert.deepEqual(query.sources.map((source) => source.schemaId), [
    "gamecult.aetheria.daemon_frame.v1",
    "gamecult.aetheria.authority_policy.v1",
  ]);

  const queried = await query.execute(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-1, -1, 1, 1)) },
    "unity-raven",
  );

  assert.equal(query.queryId, recipe.projectionId);
  assert.equal(queried.route, "shared-memory");

  const queryDiagnostic = CultMesh.describeQuerySurface(query);
  assert.equal(queryDiagnostic.queryId, "aetheria.zone.objects.visible");
  assert.equal(queryDiagnostic.routeHint.kind, "shared-memory");
  assert.deepEqual(queryDiagnostic.sources.map((source) => source.sourceId), [
    "daemon:aetheria.frame.latest.v1",
    "daemon:aetheria.authority.policy.v1",
  ]);
  assert.notEqual(queryDiagnostic.sources, query.sources);

  const unsubscribe = query.watch(
    { viewport: CultMesh.viewportRequest(CultMesh.rectFromBounds(-1, -1, 1, 1)) },
    "unity-raven",
    (value) => watchedRoutes.push(value.route),
  );
  unsubscribe();

  assert.deepEqual(watchedRoutes, ["shared-memory", "unsubscribed"]);
});

test("CultMesh TS describes typed surface catalogs for tools and generated bindings", () => {
  const source = CultMesh.projectionSource("daemon:aetheria.frame.latest.v1", {
    schemaId: "gamecult.aetheria.daemon_frame.v1",
  });
  const routeHint = CultMesh.routeHint("shared-memory", "co-located frame slab");
  const query = CultMesh.query<void, string>(
    "aetheria.zone.objects.visible",
    async () => "objects",
    {
      sources: [source],
      routeHint,
    },
  );
  const feed = CultMesh.liveFeed<void, string>(
    "aetheria.rts.viewport.feed",
    async () => "frame",
    {
      sources: [source],
      routeHint,
    },
  );
  const operation = CultMesh.operation<void, string>(
    "aetheria.pilot.set_move_vector",
    async () => "accepted",
  );
  const pointer = CultMesh.statePointer(
    "aetheria.selection.current",
    async () => "entity:ship:1",
    undefined,
    {
      sources: [source],
      routeHint,
    },
  );
  const document = CultMesh.document(
    "daemon:aetheria.frame.latest.v1",
    { schemaId: "gamecult.aetheria.daemon_frame.v1" },
    async () => ({ frameId: 1 }),
    {
      sources: [source],
      routeHint,
    },
  );
  const collection = CultMesh.collection(
    "daemon:aetheria.contacts.v1",
    { schemaId: "gamecult.aetheria.contact.v1" },
    async () => [],
    {
      sources: [source],
      routeHint,
    },
  );
  const nativeView = CultMesh.nativeSliceView(
    "aetheria.zone.render",
    "gamecult.aetheria.render_body.v1",
    128,
    [CultMesh.nativeSliceColumn("position", "CultMath.float2", 8)],
    { route: routeHint },
  );

  const querySurface = CultMesh.describeSurface(query);
  const feedSurface = CultMesh.describeSurface(feed);
  const operationSurface = CultMesh.describeSurface(operation);
  const pointerSurface = CultMesh.describeSurface(pointer);
  const documentSurface = CultMesh.describeSurface(document);
  const collectionSurface = CultMesh.describeSurface(collection);
  const nativeViewSurface = CultMesh.describeSurface(nativeView);
  const catalog = CultMesh.describeSurfaceCatalog("gamecult.aetheria.rts.surfaces.v1", [
    querySurface,
    feedSurface,
    operationSurface,
    documentSurface,
    collectionSurface,
    pointerSurface,
    nativeViewSurface,
  ]);

  assert.equal(querySurface.kind, "query");
  assert.equal(feedSurface.kind, "live-feed");
  assert.equal(documentSurface.kind, "document");
  assert.equal(collectionSurface.kind, "collection");
  assert.equal(pointerSurface.kind, "state-pointer");
  assert.equal(pointerSurface.routeHint.kind, "shared-memory");
  assert.deepEqual(pointerSurface.sources.map(next => next.schemaId), [
    "gamecult.aetheria.daemon_frame.v1",
  ]);
  assert.equal(nativeViewSurface.kind, "native-slice-view");
  assert.equal(catalog.catalogId, "gamecult.aetheria.rts.surfaces.v1");
  assert.deepEqual(catalog.surfaces.map(surface => surface.surfaceId), [
    "aetheria.zone.objects.visible",
    "aetheria.rts.viewport.feed",
    "aetheria.pilot.set_move_vector",
    "daemon:aetheria.frame.latest.v1",
    "daemon:aetheria.contacts.v1",
    "aetheria.selection.current",
    "aetheria.zone.render",
  ]);
  assert.equal(catalog.surfaces[0].routeHint.kind, "shared-memory");
  assert.deepEqual(catalog.surfaces[0].sources.map(next => next.schemaId), [
    "gamecult.aetheria.daemon_frame.v1",
  ]);
  assert.notEqual(catalog.surfaces[0], querySurface);
  assert.notEqual(catalog.surfaces[0].sources, querySurface.sources);
  assert.equal(catalog.surfaces[6].routeHint.kind, "shared-memory");

  assert.equal(
    CultMesh.findSurface(catalog, "aetheria.selection.current")?.kind,
    "state-pointer",
  );
  assert.equal(
    CultMesh.findSurface(catalog, "daemon:aetheria.frame.latest.v1")?.kind,
    "document",
  );
  assert.equal(CultMesh.findSurface(catalog, "missing"), undefined);

  const operations = CultMesh.surfacesByKind(catalog, "operation");
  assert.deepEqual(operations.map(surface => surface.surfaceId), [
    "aetheria.pilot.set_move_vector",
  ]);
  assert.notEqual(operations, catalog.surfaces);
  assert.notEqual(operations[0], operationSurface);

  const index = CultMesh.surfaceCatalogIndex(catalog);
  assert.equal(index.catalogId, "gamecult.aetheria.rts.surfaces.v1");
  assert.deepEqual(index.queries.map(surface => surface.surfaceId), [
    "aetheria.zone.objects.visible",
  ]);
  assert.deepEqual(index.liveFeeds.map(surface => surface.surfaceId), [
    "aetheria.rts.viewport.feed",
  ]);
  assert.deepEqual(index.operations.map(surface => surface.surfaceId), [
    "aetheria.pilot.set_move_vector",
  ]);
  assert.deepEqual(index.documents.map(surface => surface.surfaceId), [
    "daemon:aetheria.frame.latest.v1",
  ]);
  assert.deepEqual(index.collections.map(surface => surface.surfaceId), [
    "daemon:aetheria.contacts.v1",
  ]);
  assert.deepEqual(index.statePointers.map(surface => surface.surfaceId), [
    "aetheria.selection.current",
  ]);
  assert.deepEqual(index.nativeSliceViews.map(surface => surface.surfaceId), [
    "aetheria.zone.render",
  ]);
  assert.deepEqual(index.projectionRecipes, []);
  assert.notEqual(index.operations, operations);
  assert.notEqual(index.operations[0], operations[0]);
});

test("CultMesh TS exposes fluent context builders for typed handles", async () => {
  const operationContext = CultMesh.operationContextFor("unity-client")
    .claim("pilot-authority", { shardId: "zone:raven" })
    .route("shared-memory", "co-located Verse")
    .idempotency("move:raven:1")
    .build();

  assert.deepEqual(operationContext, {
    runtimeId: "unity-client",
    claims: [{ role: "pilot-authority", shardId: "zone:raven", leaseId: undefined }],
    routeHint: { kind: "shared-memory", description: "co-located Verse" },
    idempotencyKey: "move:raven:1",
  });

  const queryContext = CultMesh.queryContextFor("browser-client")
    .route("wasm", "browser-local projection")
    .build();

  assert.deepEqual(queryContext, {
    runtimeId: "browser-client",
    routeHint: { kind: "wasm", description: "browser-local projection" },
  });
});

test("CultMesh TS Verse context lets generated domain sugar use shared typed contexts", async () => {
  const verse = await CultMesh.connectVerse("starbridge", "browser-starfire", {
    routeHint: CultMesh.routeHint("network", "remote Verse peer"),
    claims: [
      CultMesh.authorityClaim("commander-control", {
        shardId: "zone:frontier",
        leaseId: "lease:starfire",
      }),
    ],
  });
  const queryVerse = verse.withRoute("shared-memory", "local projection slab");
  const commandVerse = verse.withRoute("network", "remote command route");

  const aetheria = queryVerse.use((queryContext) => {
    const moveOperation = commandVerse.bindOperation(CultMesh.operation<
      { entityId: number; direction: { x: number; y: number } },
      ReturnType<typeof CultMesh.operationReceipt>
    >("aetheria.entity.pilot.move", async (request, operationContext) => {
      assert.equal(operationContext.runtimeId, "browser-starfire");
      assert.equal(operationContext.claims[0]?.role, "commander-control");
      assert.equal(operationContext.idempotencyKey, "move:starfire:1");
      return CultMesh.operationReceipt("aetheria.entity.pilot.move", request.entityId === 7, {
        route: operationContext.routeHint,
      });
    }));
    const objectsVisible = CultMesh.bindQuery(queryContext, CultMesh.query<
      { viewport: ReturnType<typeof CultMesh.viewportRequest> },
      { runtimeId: string; zoneId: string; route: string; minX: number }
    >("aetheria.zone.objects.visible", async (parameters, context) => ({
      runtimeId: context.runtimeId,
      zoneId: "zone:frontier",
      route: context.routeHint.kind,
      minX: parameters.viewport.minX,
    })));

    return {
    entity: (entityId: number) => ({
      pilot: {
        move: (direction: { x: number; y: number }, idempotencyKey: string) =>
          moveOperation.invoke({ entityId, direction }, { idempotencyKey }),
      },
    }),
    zone: (zoneId: string) => ({
      objects: {
        visibleWithin: async (viewport: ReturnType<typeof CultMesh.viewportRequest>) => ({
          ...await objectsVisible.execute({ viewport }),
          zoneId,
        }),
      },
    }),
  };
  });

  const receipt = await aetheria
    .entity(7)
    .pilot
    .move(CultMesh.vec2(1, 0), "move:starfire:1");
  const viewport = await aetheria
    .zone("zone:frontier")
    .objects
    .visibleWithin(CultMesh.viewportRequest(CultMesh.rectFromBounds(-16, -8, 16, 8)));

  assert.equal(verse.verseId, "starbridge");
  assert.equal(verse.runtimeId, "browser-starfire");
  assert.deepEqual(verse.operationContext({ idempotencyKey: "move:starfire:2" }), {
    runtimeId: "browser-starfire",
    claims: [{ role: "commander-control", shardId: "zone:frontier", leaseId: "lease:starfire" }],
    routeHint: { kind: "network", description: "remote Verse peer" },
    idempotencyKey: "move:starfire:2",
  });
  assert.deepEqual(queryVerse.queryContext(), {
    runtimeId: "browser-starfire",
    routeHint: { kind: "shared-memory", description: "local projection slab" },
  });
  assert.equal(commandVerse.bindOperation(CultMesh.operation("noop", async (_request, context) =>
    CultMesh.operationReceipt("noop", true, { route: context.routeHint }))).operationId, "noop");
  assert.deepEqual(receipt, {
    operationId: "aetheria.entity.pilot.move",
    accepted: true,
    route: {
      kind: "network",
      description: "remote command route",
    },
    diagnostic: undefined,
  });
  assert.deepEqual(viewport, {
    runtimeId: "browser-starfire",
    zoneId: "zone:frontier",
    route: "shared-memory",
    minX: -16,
  });
});

test("CultMesh TS exposes typed state pointers for UI and tools", async () => {
  const updates: string[] = [];
  let value = "initial";
  const pointer = CultMesh.statePointer(
    "aetheria.selection.current",
    async () => value,
    (callback: (value: string) => void) => {
      callback(value);
      return () => updates.push("unsubscribed");
    },
    {
      sources: [
        CultMesh.projectionSource("daemon:aetheria.selection.current.v1", {
          schemaId: "gamecult.aetheria.selection.v1",
        }),
      ],
      routeHint: CultMesh.routeHint("shared-memory", "co-located selection cache"),
    },
  );

  assert.equal(pointer.pointerId, "aetheria.selection.current");
  assert.equal(await pointer.resolve(), "initial");
  assert.deepEqual(CultMesh.describeStatePointer(pointer), {
    pointerId: "aetheria.selection.current",
    routeHint: {
      kind: "shared-memory",
      description: "co-located selection cache",
    },
    sources: [
      {
        sourceId: "daemon:aetheria.selection.current.v1",
        schemaId: "gamecult.aetheria.selection.v1",
        description: undefined,
      },
    ],
  });

  value = "selected:ship:1";
  const unsubscribe = pointer.watch((next) => updates.push(next));
  unsubscribe();

  assert.deepEqual(updates, ["selected:ship:1", "unsubscribed"]);

  const contextualUpdates: string[] = [];
  const contextualPointer = CultMesh.statePointer(
    "aetheria.daemon.frame.latest",
    async (context) => `${context.runtimeId}:${context.routeHint.kind}`,
    (context, callback) => {
      callback(`${context.runtimeId}:frame:12:${context.routeHint.kind}`);
      return () => contextualUpdates.push("contextual-unsubscribed");
    },
    {
      sources: [
        CultMesh.projectionSource("daemon:aetheria.frame.latest.v1", {
          schemaId: "gamecult.aetheria.daemon_frame.v1",
        }),
      ],
      routeHint: CultMesh.routeHint("shared-memory", "co-located daemon frame"),
    },
  );
  const verse = CultMesh.verse("aetheria.local", "bifrost-tool")
    .withRoute("ipc", "tool bridge");
  const bound = CultMesh.bindStatePointer(verse, contextualPointer);

  assert.equal(await bound.resolve(), "bifrost-tool:ipc");
  const contextualUnsubscribe = bound.watch((next) => contextualUpdates.push(next));
  contextualUnsubscribe();

  assert.equal(bound.pointerId, "aetheria.daemon.frame.latest");
  assert.deepEqual(bound.sources.map(source => source.schemaId), [
    "gamecult.aetheria.daemon_frame.v1",
  ]);
  assert.deepEqual(contextualUpdates, [
    "bifrost-tool:frame:12:ipc",
    "contextual-unsubscribed",
  ]);
});

test("CultMesh TS mutable state pointers read watch and replace through a Verse", async () => {
  let stored = "frame:0";
  let watcher: ((value: string) => void) | undefined;
  const contexts: string[] = [];
  const pointer = CultMesh.mutableStatePointer(
    "aetheria.daemon.frame.latest",
    async (context) => {
      contexts.push(`read:${context.runtimeId}:${context.routeHint.kind}`);
      return stored;
    },
    (context, callback) => {
      contexts.push(`watch:${context.runtimeId}:${context.routeHint.kind}`);
      watcher = (value) => callback(`${context.runtimeId}:${value}:${context.routeHint.kind}`);
      return () => {
        watcher = undefined;
      };
    },
    async (context, value) => {
      contexts.push(`replace:${context.runtimeId}:${context.routeHint.kind}`);
      stored = value;
      watcher?.(value);
    },
    {
      routeHint: CultMesh.routeHint("shared-memory", "co-located daemon frame"),
      sources: [
        CultMesh.projectionSource("daemon:aetheria.frame.latest.v1", {
          schemaId: "gamecult.aetheria.daemon_frame.v1",
        }),
      ],
    },
  );

  const verse = CultMesh.verse("aetheria.local", "unity-raven", {
    routeHint: CultMesh.routeHint("ipc", "tool bridge"),
  });
  const bound = verse.bindMutableStatePointer(pointer);

  let observed = "";
  const unsubscribe = bound.watch((value) => {
    observed = value;
  });
  assert.equal(await bound.read(), "frame:0");
  await bound.replace("frame:1");
  unsubscribe();

  assert.equal(stored, "frame:1");
  assert.equal(observed, "unity-raven:frame:1:ipc");
  assert.deepEqual(contexts, [
    "watch:unity-raven:ipc",
    "read:unity-raven:ipc",
    "replace:unity-raven:ipc",
  ]);
  assert.deepEqual(CultMesh.describeStatePointer(pointer), {
    pointerId: "aetheria.daemon.frame.latest",
    routeHint: {
      kind: "shared-memory",
      description: "co-located daemon frame",
    },
    sources: [
      {
        sourceId: "daemon:aetheria.frame.latest.v1",
        schemaId: "gamecult.aetheria.daemon_frame.v1",
        description: undefined,
      },
    ],
  });
  assert.deepEqual(CultMesh.stateBinding("frame", pointer), {
    targetProp: "frame",
    pointerId: "aetheria.daemon.frame.latest",
    sourceId: "daemon:aetheria.frame.latest.v1",
    schemaId: "gamecult.aetheria.daemon_frame.v1",
    routeHint: {
      kind: "shared-memory",
      description: "co-located daemon frame",
    },
  });
});

test("CultMesh TS describes UI state bindings from typed state pointers", async () => {
  const pointer = CultMesh.statePointer(
    "aetheria.selection.current",
    async () => "entity:station:0",
    undefined,
    {
      sources: [
        CultMesh.projectionSource("daemon:aetheria.selection.current.v1", {
          schemaId: "gamecult.aetheria.selection.v1",
        }),
      ],
      routeHint: CultMesh.routeHint("shared-memory", "co-located selection cache"),
    },
  );

  const binding = CultMesh.stateBinding("value", pointer);
  const explicitBinding = CultMesh.stateBinding(
    "label",
    "aetheria.selection.label",
    {
      sourceId: "daemon:aetheria.selection.label.v1",
      schemaId: "gamecult.aetheria.selection_label.v1",
      routeHint: CultMesh.routeHint("ipc", "tool bridge"),
    },
  );

  assert.deepEqual(binding, {
    targetProp: "value",
    pointerId: "aetheria.selection.current",
    sourceId: "daemon:aetheria.selection.current.v1",
    schemaId: "gamecult.aetheria.selection.v1",
    routeHint: {
      kind: "shared-memory",
      description: "co-located selection cache",
    },
  });
  assert.deepEqual(explicitBinding, {
    targetProp: "label",
    pointerId: "aetheria.selection.label",
    sourceId: "daemon:aetheria.selection.label.v1",
    schemaId: "gamecult.aetheria.selection_label.v1",
    routeHint: {
      kind: "ipc",
      description: "tool bridge",
    },
  });
});

test("CultMesh TS flattens and rehydrates UI state binding records", () => {
  const binding = CultMesh.stateBinding(
    "status",
    "aetheria.current.status",
    {
      sourceId: "daemon:aetheria.frame.latest.v1",
      schemaId: "gamecult.aetheria.daemon_frame.v1",
      routeHint: CultMesh.routeHint("shared-memory", "co-located frame slab"),
    },
  );

  assert.deepEqual(CultMesh.stateBindingRecord(binding), {
    targetProp: "status",
    pointerId: "aetheria.current.status",
    sourceId: "daemon:aetheria.frame.latest.v1",
    schemaId: "gamecult.aetheria.daemon_frame.v1",
    routeKind: "shared-memory",
    routeDescription: "co-located frame slab",
  });

  assert.deepEqual(CultMesh.stateBindingFromRecord({
    targetProp: "value",
    pointerId: "aetheria.selection.current",
    sourceId: "daemon:aetheria.selection.current.v1",
    schemaId: "gamecult.aetheria.selection.v1",
    routeKind: "InProcess",
    routeDescription: "embedded tool host",
  }), {
    targetProp: "value",
    pointerId: "aetheria.selection.current",
    sourceId: "daemon:aetheria.selection.current.v1",
    schemaId: "gamecult.aetheria.selection.v1",
    routeHint: {
      kind: "in-process",
      description: "embedded tool host",
    },
  });
});

test("CultMesh TS describes UI command bindings from typed operations", async () => {
  const operation = CultMesh.operation<
    { actor: string },
    ReturnType<typeof CultMesh.operationReceipt>
  >("aetheria.entity.pilot.move", async (_request, context) =>
    CultMesh.operationReceipt("aetheria.entity.pilot.move", true, {
      route: context.routeHint,
    }));

  const binding = CultMesh.operationBinding(operation, {
    label: "Move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: CultMesh.routeHint("network", "remote Verse peer"),
  });
  const explicitBinding = CultMesh.operationBinding("aetheria.surface.refresh", {
    label: "Refresh",
  });

  assert.deepEqual(binding, {
    operationId: "aetheria.entity.pilot.move",
    label: "Move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: {
      kind: "network",
      description: "remote Verse peer",
    },
  });
  assert.deepEqual(explicitBinding, {
    operationId: "aetheria.surface.refresh",
    label: "Refresh",
    schemaId: "",
    routeHint: {
      kind: "automatic",
    },
  });
});

test("CultMesh TS flattens and rehydrates UI operation binding records", () => {
  const binding = CultMesh.operationBinding("aetheria.entity.pilot.move", {
    label: "Move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: CultMesh.routeHint("network", "remote Verse peer"),
  });

  assert.deepEqual(CultMesh.operationBindingRecord(binding), {
    operationId: "aetheria.entity.pilot.move",
    label: "Move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeKind: "network",
    routeDescription: "remote Verse peer",
  });

  assert.deepEqual(CultMesh.operationBindingFromRecord({
    operationId: "aetheria.surface.refresh",
    label: "Refresh",
    schemaId: "gamecult.aetheria.refresh.v1",
    routeKind: "SharedMemory",
    routeDescription: "co-located command boundary",
  }), {
    operationId: "aetheria.surface.refresh",
    label: "Refresh",
    schemaId: "gamecult.aetheria.refresh.v1",
    routeHint: {
      kind: "shared-memory",
      description: "co-located command boundary",
    },
  });
});

test("CultMesh TS carries UI command invocations as typed operation descriptors", () => {
  const binding = CultMesh.operationBinding("aetheria.entity.pilot.move", {
    label: "Move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: CultMesh.routeHint("ipc", "local Verse node"),
  });

  const invocation = CultMesh.operationInvocation(binding, {
    idempotencyKey: "move:42",
  });
  const explicitInvocation = CultMesh.operationInvocation(
    "aetheria.surface.refresh",
    {
      routeHint: CultMesh.routeHint("network", "remote Verse peer"),
    },
  );

  assert.deepEqual(invocation, {
    operationId: "aetheria.entity.pilot.move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: {
      kind: "ipc",
      description: "local Verse node",
    },
    idempotencyKey: "move:42",
  });
  assert.deepEqual(explicitInvocation, {
    operationId: "aetheria.surface.refresh",
    schemaId: "",
    routeHint: {
      kind: "network",
      description: "remote Verse peer",
    },
    idempotencyKey: undefined,
  });
});

test("CultMesh TS reads common operation payload scalar fields", () => {
  const payload = CultMesh.operationPayload({
    value: "42.5",
    tierIndex: 3,
    enabled: "on",
    name: "Starfire",
  });
  const updated = payload.with("enabled", false);

  assert.equal(payload.getString("name"), "Starfire");
  assert.equal(payload.getString("missing", "fallback"), "fallback");
  assert.equal(payload.getInt("tierIndex", -1), 3);
  assert.equal(payload.getInt("missing", -1), -1);
  assert.equal(payload.getDouble("value", -1), 42.5);
  assert.equal(payload.getBoolean("enabled"), true);
  assert.equal(payload.getBoolean("missing", true), true);
  assert.equal(updated.getBoolean("enabled", true), false);
  assert.equal(updated.getString("name"), "Starfire");
  assert.deepEqual(payload.fields, {
    value: "42.5",
    tierIndex: "3",
    enabled: "on",
    name: "Starfire",
  });
  assert.deepEqual(payload.toRecord(), payload.fields);
});

test("CultMesh TS flattens and rehydrates operation invocation records", () => {
  const invocation = CultMesh.operationInvocation("aetheria.entity.pilot.move", {
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeHint: CultMesh.routeHint("ipc", "local Verse node"),
    idempotencyKey: "move:42",
  });

  const record = CultMesh.operationInvocationRecord(invocation);
  const restored = CultMesh.operationInvocationFromRecord(record, {
    fallbackOperationId: "fallback.operation",
    fallbackSchemaId: "fallback.schema",
    fallbackRouteHint: CultMesh.routeHint("network", "fallback route"),
  });
  const csharpRecord = CultMesh.operationInvocationFromRecord(
    {
      operationId: "",
      schemaId: "",
      routeKind: "InProcess",
      routeDescription: "",
      idempotencyKey: "",
    },
    {
      fallbackOperationId: "fallback.operation",
      fallbackSchemaId: "fallback.schema",
      fallbackRouteHint: CultMesh.routeHint("network", "fallback route"),
      fallbackIdempotencyKey: "fallback-key",
    },
  );

  assert.deepEqual(record, {
    operationId: "aetheria.entity.pilot.move",
    schemaId: "gamecult.aetheria.pilot_move.v1",
    routeKind: "ipc",
    routeDescription: "local Verse node",
    idempotencyKey: "move:42",
  });
  assert.deepEqual(restored, invocation);
  assert.deepEqual(csharpRecord, {
    operationId: "fallback.operation",
    schemaId: "fallback.schema",
    routeHint: {
      kind: "in-process",
      description: "fallback route",
    },
    idempotencyKey: "fallback-key",
  });
});

test("CultMesh TS composes named state ref resolvers for surfaces", () => {
  const daemon = CultMesh.stateRefResolver(
    "aetheria.daemon.refs",
    (stateRef: string, context) =>
      stateRef === "aetheria.daemon/frame/id"
        ? `${context.runtimeId}:${context.routeHint.kind}:42`
        : "",
    {
      sources: [
        CultMesh.projectionSource("daemon:aetheria.frame.latest.v1", {
          schemaId: "gamecult.aetheria.daemon_frame.v1",
        }),
      ],
      routeHint: CultMesh.routeHint("in-process", "embedded renderer"),
    },
  );
  const itemStats = CultMesh.stateRefResolver(
    "aetheria.item_stats.refs",
    (stateRef: string) =>
      stateRef === "aetheria.item_stats/laser/damage" ? "12.5" : "",
  );
  const resolver = daemon.or(itemStats);

  assert.equal(
    resolver.resolve(
      "aetheria.daemon/frame/id",
      CultMesh.queryContext("unity-raven", {
        routeHint: CultMesh.routeHint("network", "remote peer"),
      }),
    ),
    "unity-raven:network:42",
  );
  assert.equal(resolver.resolve("aetheria.item_stats/laser/damage"), "12.5");
  assert.deepEqual(resolver.tryResolve("missing"), {
    resolved: false,
    value: "",
  });
  assert.equal(resolver.asFunction()("aetheria.item_stats/laser/damage"), "12.5");

  assert.deepEqual(CultMesh.describeStateRefResolver(resolver), {
    resolverId: "aetheria.daemon.refs|aetheria.item_stats.refs",
    routeHint: {
      kind: "in-process",
      description: "embedded renderer",
    },
    sources: [
      {
        sourceId: "daemon:aetheria.frame.latest.v1",
        schemaId: "gamecult.aetheria.daemon_frame.v1",
        description: undefined,
      },
    ],
  });
});

test("CultMesh TS exposes native slice descriptors", () => {
  const view = CultMesh.nativeSliceView(
    "aetheria.zone.render",
    "gamecult.aetheria.zone_render.v1",
    12,
    [
      CultMesh.nativeSliceColumn("position", "CultMath.float3", 12),
      CultMesh.nativeSliceColumn("rotationRadians", "float32", 4),
      CultMesh.nativeSliceColumn("renderGroupId", "uint32", 4),
    ],
    {
      route: CultMesh.routeHint("shared-memory", "CultCache slab"),
      nativeHandle: "aetheria-zone-render-001",
    },
  );

  assert.equal(CultMesh.denseRowStrideBytes(view), 20);
  assert.deepEqual(CultMesh.findNativeSliceColumn(view, "position"), {
    name: "position",
    valueType: "CultMath.float3",
    elementSizeBytes: 12,
  });
  assert.equal(CultMesh.findNativeSliceColumn(view, "missing"), undefined);

  const diagnostic = CultMesh.describeNativeSliceView(view);
  assert.equal(diagnostic.viewId, "aetheria.zone.render");
  assert.equal(diagnostic.schemaId, "gamecult.aetheria.zone_render.v1");
  assert.equal(diagnostic.rowCount, 12);
  assert.equal(diagnostic.route.kind, "shared-memory");
  assert.equal(diagnostic.nativeHandle, "aetheria-zone-render-001");
  assert.equal(diagnostic.denseRowStrideBytes, 20);
  assert.deepEqual(diagnostic.columns.map(column => column.name), [
    "position",
    "rotationRadians",
    "renderGroupId",
  ]);
  assert.notEqual(diagnostic.columns, view.columns);
});

test("CultMesh TS branded facade exposes schema and shard catalogs", () => {
  const schemaCatalog = CultMesh.createSchemaCatalog();
  const schemaUpdates: string[] = [];
  const unsubscribeSchema = schemaCatalog.watch((descriptor) => {
    schemaUpdates.push(descriptor.schemaId);
  });
  schemaCatalog.upsert({
    schemaId: "cultmesh.ts.note.v1",
    kind: "document_payload",
    documentType: "cultmesh.ts-note",
    wireContracts: ["cultnet.schema.v0"],
    contentHash: "note-hash",
  });
  unsubscribeSchema();
  schemaCatalog.upsert({
    schemaId: "cultmesh.ts.other.v1",
    kind: "document_payload",
    documentType: "cultmesh.ts-other",
    wireContracts: ["cultnet.schema.v0"],
    contentHash: "other-hash",
  });

  assert.deepEqual(schemaUpdates, ["cultmesh.ts.note.v1"]);
  assert.equal(
    schemaCatalog.get("cultmesh.ts.note.v1")?.documentType,
    "cultmesh.ts-note",
  );

  const builtIns = CultMesh.createBuiltInSchemaCatalog();
  assert.equal(
    builtIns.get("cultnet.shard_catalog_request.v0")?.schemaVersion,
    "cultnet.shard_catalog_request.v0",
  );
  assert.equal(
    builtIns.get("cultnet.shard_catalog_response.v0")?.kind,
    "wire_message",
  );
  assert.equal(
    builtIns.get(
      "https://github.com/GameCult/cultnet-ts/contracts/cultnet.transport-profile.schema.json",
    )?.schemaVersion,
    "cultnet.transport_profile.v0",
  );

  const shardCatalog = CultMesh.createShardCatalog();
  const shardUpdates: string[] = [];
  const unsubscribeShard = shardCatalog.watch((descriptor) => {
    shardUpdates.push(descriptor.shardId);
  });
  shardCatalog.upsert({
    shardId: "notes-a",
    ownerRuntimeId: "ts-runtime",
    epoch: 7,
    schemaIds: ["cultmesh.ts.note.v1"],
    keyPrefix: "note:",
  });
  unsubscribeShard();
  shardCatalog.upsert({
    shardId: "notes-b",
    ownerRuntimeId: "ts-runtime",
    epoch: 8,
  });

  assert.deepEqual(shardUpdates, ["notes-a"]);
  assert.equal(shardCatalog.get("notes-a")?.ownerRuntimeId, "ts-runtime");
  assert.deepEqual(
    shardCatalog
      .list({ schemaIds: ["cultmesh.ts.note.v1"], recordKeys: ["note:1"] })
      .map((shard) => shard.shardId),
    ["notes-a"],
  );
});

test("CultMesh TS branded facade creates RUDP clients from peer endpoints", async () => {
  const connectionId = 0x10203044;
  const server = await CultMesh.createRudpServer(
    "cultmesh-ts-rudp-server",
    connectionId,
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );
  const serverPort = server.profile.transports[0]?.port;
  assert.equal(typeof serverPort, "number");
  const endpoint = CultMesh.parseRudpEndpoint(`rudp://127.0.0.1:${serverPort}`);
  assert.equal(endpoint.host, "127.0.0.1");
  assert.equal(endpoint.port, serverPort);

  const peer = {
    peerId: "cultmesh-ts-rudp-server",
    verseId: "local",
    endpoints: [endpoint.uri],
    roles: ["schema"],
    authorityLeaseId: "lease:cultmesh-ts-rudp-server",
  };
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  peers.upsert(peer);
  await assert.rejects(
    CultMesh.createRudpClientForAuthorizedPeer(
      "cultmesh-ts-rudp-client",
      connectionId,
      peers,
      leases,
      "local",
      "schema",
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
      },
    ),
    /No authorized RUDP peer/,
  );
  leases.upsert({
    leaseId: "lease:cultmesh-ts-rudp-server",
    verseId: "local",
    peerId: "cultmesh-ts-rudp-server",
    roles: ["schema"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  });
  const client = await CultMesh.createRudpClientForAuthorizedPeer(
    "cultmesh-ts-rudp-client",
    connectionId,
    peers,
    leases,
    "local",
    "schema",
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );

  try {
    const serverFrame = new Promise<{ channelId: string; payload: Uint8Array }>(
      (resolve, reject) => {
        server.once("frame", resolve);
        server.once("error", reject);
      },
    );
    client.connect(Buffer.from("join", "utf8"));
    await waitFor(
      () => client.connected && server.connected,
      "CultMesh RUDP handshake",
    );
    client.send("schema", Buffer.from("client-state", "utf8"));
    const receivedByServer = await serverFrame;
    assert.equal(receivedByServer.channelId, "schema");
    assert.equal(Buffer.from(receivedByServer.payload).toString("utf8"), "client-state");
    assert.equal(server.profile.transports[0]?.protocol, "rudp");
    assert.equal(client.profile.transports[0]?.protocol, "rudp");
  } finally {
    client.close();
    server.close();
  }
});

test("CultMesh TS creates connected RUDP schema peers for catalog requests", async () => {
  const connectionId = 0x10203045;
  const serverTransport = await CultMesh.createRudpServer(
    "cultmesh-ts-rudp-schema-server",
    connectionId,
    {
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );
  const serverPort = serverTransport.profile.transports[0]?.port;
  assert.equal(typeof serverPort, "number");
  const serverPeer = new CultNetPeer(serverTransport, {
    wireContract: "cultnet.schema.v0",
  });
  serverPeer.on("message", (message) => {
    if (message.schemaVersion === "cultnet.schema_catalog_request.v0") {
      serverPeer.sendSchemaCatalogResponse(
        cultNetBuiltinSchemaRegistry.createCatalogResponse(message),
      );
    }
  });

  const peer = {
    peerId: "cultmesh-ts-rudp-schema-server",
    verseId: "local",
    endpoints: [`rudp://127.0.0.1:${serverPort}`],
    roles: ["schema"],
    authorityLeaseId: "lease:cultmesh-ts-rudp-schema-server",
  };
  const peers = CultMesh.createPeerCatalog();
  const leases = CultMesh.createAuthorityLeaseCatalog();
  peers.upsert(peer);
  leases.upsert({
    leaseId: "lease:cultmesh-ts-rudp-schema-server",
    verseId: "local",
    peerId: "cultmesh-ts-rudp-schema-server",
    roles: ["schema"],
    validFrom: new Date(Date.now() - 1000),
    expiresAt: new Date(Date.now() + 1000),
  });

  let clientPeer: CultNetPeer | undefined;
  try {
    clientPeer = await CultMesh.createRudpPeerForAuthorizedPeer(
      "cultmesh-ts-rudp-schema-client",
      connectionId,
      peers,
      leases,
      "local",
      "schema",
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );
    const synced = CultMesh.createSchemaCatalog();
    const applied = await clientPeer.syncSchemaCatalog(synced, {
      messageId: "cultmesh-ts-rudp-schema-catalog",
      kinds: ["document_payload"],
      includeSchemaJson: true,
      timeoutMs: 1_000,
    });

    assert.ok(applied.some((descriptor) => descriptor.documentType === "ghostlight.agent-state"));
    assert.equal(clientPeer.transportProfile?.transports[0]?.protocol, "rudp");
    assert.equal(serverPeer.transportProfile?.transports[0]?.protocol, "rudp");
  } finally {
    clientPeer?.close();
    serverPeer.close();
  }
});

test("CultMesh TS RUDP document server accepts raw document puts from multiple peers", async () => {
  const connectionId = 0x10203046;
  const received: string[] = [];
  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-document-server",
    connectionId,
    {
      documents: new CultNetDocumentRegistry(),
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
      onDocumentPutRaw: (document) => {
        received.push(`${document.sourceRuntimeId}:${document.recordKey}:${document.payload}`);
      },
    },
  );

  let firstPeer: CultNetPeer | undefined;
  let secondPeer: CultNetPeer | undefined;
  try {
    await server.start();
    firstPeer = await CultMesh.createRudpPeer(
      "cultmesh-ts-document-client-a",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );
    secondPeer = await CultMesh.createRudpPeer(
      "cultmesh-ts-document-client-b",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );

    firstPeer.send({
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: "raw-put-a",
      document: {
        schemaId: "cultmesh.note.v0",
        recordKey: "note:a",
        storedAt: new Date().toISOString(),
        payloadEncoding: "messagepack",
        payload: encode("first"),
        sourceRuntimeId: "client-a",
      },
    });
    secondPeer.send({
      schemaVersion: "cultnet.document_put_raw.v0",
      messageId: "raw-put-b",
      document: {
        schemaId: "cultmesh.note.v0",
        recordKey: "note:b",
        storedAt: new Date().toISOString(),
        payloadEncoding: "messagepack",
        payload: encode("second"),
        sourceRuntimeId: "client-b",
      },
    });

    await waitFor(() => received.length === 2, "two RUDP document puts");
    assert.deepEqual(received.sort(), [
      "client-a:note:a:first",
      "client-b:note:b:second",
    ]);
  } finally {
    firstPeer?.close();
    secondPeer?.close();
    server.close();
  }
});

test("CultMesh TS publishes one registered document to a RUDP catalog", async () => {
  const connectionId = 0x10203047;
  let received: { schemaId: string; recordKey: string; payload: unknown; sourceRuntimeId: string | null } | undefined;
  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-odin-catalog",
    connectionId,
    {
      documents: new CultNetDocumentRegistry(),
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
      onDocumentPutRaw: (document) => {
        received = {
          schemaId: document.schemaId,
          recordKey: document.recordKey,
          payload: document.payload,
          sourceRuntimeId: document.sourceRuntimeId,
        };
      },
    },
  );

  try {
    await server.start();
    await CultMesh.publishRudpDocumentOnce(
      "cultmesh-ts-muninn",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      defineCultNetDocumentBinding({ definition: noteDocument }),
      "note:odin",
      {
        noteId: "note:odin",
        body: "pay respects once",
      },
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
        flushTimeoutMs: 25,
        sourceRole: "test-provider",
      },
    );

    await waitFor(() => received !== undefined, "published RUDP document");
    assert.deepEqual(received, {
      schemaId: "cultmesh.note.v0",
      recordKey: "note:odin",
      payload: {
        noteId: "note:odin",
        body: "pay respects once",
      },
      sourceRuntimeId: "cultmesh-ts-muninn",
    });
  } finally {
    server.close();
  }
});

test("CultMesh TS returns a correlated provider receipt after a RUDP document mutation", async () => {
  const connectionId = 0x10203050;
  const commandBinding = defineCultNetDocumentBinding({ definition: noteDocument });
  const receiptBinding = defineCultNetDocumentBinding({ definition: noteReceiptDocument });
  const server = CultMesh.createRudpDocumentServer("cultmesh-ts-receipt-server", connectionId, {
    documents: new CultNetDocumentRegistry([commandBinding, receiptBinding]), bindHost: "127.0.0.1", bindPort: 0,
    resendDelayMs: 25, resendPollMs: 5,
    onDocumentPutRaw: async (document) => ({
      binding: receiptBinding, recordKey: document.recordKey,
      value: { commandId: document.recordKey, state: "applied", body: (document.payload as { body: string }).body },
    }),
  });
  try {
    await server.start();
    const receipt = await CultMesh.publishRudpDocumentAndWaitForReceipt(
      "cultmesh-ts-receipt-client", connectionId, `rudp://127.0.0.1:${server.bind.port}`,
      commandBinding, "command:note", { noteId: "command:note", body: "provider applied this" }, receiptBinding,
      { messageId: "command:note", receiptTimeoutMs: 1_000, resendPollMs: 5 },
    );
    assert.deepEqual(receipt, { commandId: "command:note", state: "applied", body: "provider applied this" });
  } finally { server.close(); }
});

test("CultMesh TS reads remote RUDP snapshots through document handles", async () => {
  const connectionId = 0x10203048;
  const node = await CultMesh.startNode(
    join(await mkdtemp(join(tmpdir(), "cultmesh-ts-rudp-snapshot-")), "node.ccmp"),
    {
      documents: [noteDocument],
    },
  );
  await node.put(noteDocument, "note:remote", {
    noteId: "note:remote",
    body: "remote handles feel local",
  });

  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-rudp-snapshot-server",
    connectionId,
    {
      documents: new CultNetDocumentRegistry([
        defineCultNetDocumentBinding({ definition: noteDocument }),
      ]),
      getCache: () => node.cache,
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );

  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer(
      "cultmesh-ts-rudp-snapshot-client",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );

    const document = CultMesh.documentFromPublication(
      {
        kind: "peer-snapshot",
        peer,
        endpoint: `rudp://127.0.0.1:${server.bind.port}`,
      },
      noteDocument,
      "note:remote",
      {
        documentId: "note:remote",
        timeoutMs: 1_000,
      },
    );

    const latest = await document.latest();
    assert.equal(latest.body, "remote handles feel local");
    assert.deepEqual(latest, {
      noteId: "note:remote",
      body: "remote handles feel local",
    });
    assert.equal(document.schema.type, "cultmesh.note");

    const alias = CultMesh.documentFromPeerSnapshot(
      peer,
      "cultmesh.note.alias.v1",
      "note:remote",
      {
        documentId: "note:remote.alias",
        timeoutMs: 1_000,
      },
    );

    assert.deepEqual(await alias.latest(), {
      noteId: "note:remote",
      body: "remote handles feel local",
    });
  } finally {
    peer?.close();
    server.close();
  }
});

test("CultMesh TS syncs remote RUDP snapshots into a local node with same-schema aliases", async () => {
  const connectionId = 0x1020304a;
  const source = await CultMesh.startNode(
    join(await mkdtemp(join(tmpdir(), "cultmesh-ts-rudp-sync-source-")), "node.ccmp"),
    {
      documents: [noteDocument],
    },
  );
  await source.put(noteDocument, "note:sync-remote", {
    noteId: "note:sync-remote",
    body: "synced once, read as local",
  });
  const target = await CultMesh.startNode(
    join(await mkdtemp(join(tmpdir(), "cultmesh-ts-rudp-sync-target-")), "node.ccmp"),
    {
      documents: [noteDocument],
    },
  );

  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-rudp-sync-server",
    connectionId,
    {
      documents: new CultNetDocumentRegistry([
        defineCultNetDocumentBinding({ definition: noteDocument }),
      ]),
      getCache: () => source.cache,
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );

  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer(
      "cultmesh-ts-rudp-sync-client",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );

    const synced = await target.syncDocumentFromPeerSnapshot(
      peer,
      noteAliasDocument,
      "note:sync-remote",
      {
        timeoutMs: 1_000,
      },
    );
    const facadeSynced = await CultMesh.syncDocumentFromPeerSnapshot(
      target,
      peer,
      noteAliasDocument,
      "note:sync-remote",
      {
        timeoutMs: 1_000,
      },
    );

    assert.deepEqual(synced, {
      noteId: "note:sync-remote",
      body: "synced once, read as local",
    });
    assert.deepEqual(facadeSynced, synced);
    assert.equal(target.getRequired(noteDocument, "note:sync-remote").body, "synced once, read as local");
    assert.equal(target.getRequired(noteAliasDocument, "note:sync-remote").body, "synced once, read as local");
    assert.equal((await target.document(noteAliasDocument, "note:sync-remote").latest()).body, "synced once, read as local");
  } finally {
    peer?.close();
    server.close();
  }
});

test("CultMesh TS peer snapshot handles choose compatible foreign schema payloads", async () => {
  const connectionId = 0x10203049;
  const node = await CultMesh.startNode(
    join(await mkdtemp(join(tmpdir(), "cultmesh-ts-rudp-foreign-snapshot-")), "node.ccmp"),
    {
      documents: [incompatibleNoteDocument, foreignNoteDocument],
    },
  );
  await node.put(incompatibleNoteDocument, "note:foreign", {
    nope: "not the requested shape",
  });
  await node.put(foreignNoteDocument, "note:foreign", {
    noteId: "note:foreign",
    body: "foreign schema id, local shape",
  });

  const server = CultMesh.createRudpDocumentServer(
    "cultmesh-ts-rudp-foreign-snapshot-server",
    connectionId,
    {
      documents: new CultNetDocumentRegistry([
        defineCultNetDocumentBinding({ definition: incompatibleNoteDocument }),
        defineCultNetDocumentBinding({ definition: foreignNoteDocument }),
      ]),
      getCache: () => node.cache,
      bindHost: "127.0.0.1",
      bindPort: 0,
      resendDelayMs: 25,
      resendPollMs: 5,
      maxFragmentBytes: 1024,
      maxPendingReliablePackets: 16,
    },
  );

  let peer: CultNetPeer | undefined;
  try {
    await server.start();
    peer = await CultMesh.createRudpPeer(
      "cultmesh-ts-rudp-foreign-snapshot-client",
      connectionId,
      `rudp://127.0.0.1:${server.bind.port}`,
      {
        resendDelayMs: 25,
        resendPollMs: 5,
        maxFragmentBytes: 1024,
        maxPendingReliablePackets: 16,
        connectTimeoutMs: 1_000,
      },
    );

    const alias = CultMesh.documentFromPeerSnapshot(
      peer,
      noteAliasDocument,
      "note:foreign",
      {
        documentId: "note:foreign.alias",
        timeoutMs: 1_000,
      },
    );

    assert.deepEqual(await alias.latest(), {
      noteId: "note:foreign",
      body: "foreign schema id, local shape",
    });
  } finally {
    peer?.close();
    server.close();
  }
});

test("CultMesh TS negotiates streaming frame body transports explicitly", () => {
  const streams = CultMesh.createStreamCatalog();
  const streamUpdates: string[] = [];
  const frameUpdates: bigint[] = [];
  const unsubscribeStream = streams.watch((stream) => {
    streamUpdates.push(stream.streamId);
  });
  const unsubscribeFrame = streams.watchFrames((frame) => {
    frameUpdates.push(frame.sequence);
  });
  const stream = {
    streamId: "mimir:kiyo-pro",
    verseId: "studio",
    ownerPeerId: "starfire",
    kind: "video",
    label: "Kiyo Pro",
    clock: {
      clockDomainId: "starfire-qpc",
      confidence: 0.25,
      evidenceKind: "provisional-clock-domain-edge-fit",
    },
    video: {
      width: 1920,
      height: 1080,
      pixelFormat: "YUY2",
      framesPerSecond: 30,
    },
    preferredTransports: [
      "shared-d3d12-texture",
      "shared-memory",
      "cultcache-page",
    ],
    maxInFlightFrames: 3,
  } as const;
  streams.declare(stream);

  const negotiation = streams.negotiate("mimir:kiyo-pro", {
    peerId: "fensalir",
    verseId: "studio",
    supportedTransports: ["shared-d3d12-texture", "cultcache-page"],
    acceptedKinds: ["video"],
    canImportGpuHandles: true,
    maxInFlightFrames: 2,
  });

  assert.deepEqual(negotiation, {
    streamId: "mimir:kiyo-pro",
    producerPeerId: "starfire",
    consumerPeerId: "fensalir",
    transport: "shared-d3d12-texture",
    access: "read",
    maxInFlightFrames: 2,
    copyBudget: "zero-copy-target",
  });
  assert.deepEqual(streamUpdates, ["mimir:kiyo-pro"]);

  const frame = {
    streamId: "mimir:kiyo-pro",
    sequence: 42n,
    timestampNs: 1_000_000_000n,
    durationNs: 33_333_334n,
    transport: "shared-d3d12-texture",
    nativeHandle: "0xfeed",
    fenceHandle: "0xbeef",
    fenceValue: 7n,
    unavoidableCopyCount: 0,
  } as const;
  streams.publishFrame(frame);

  assert.deepEqual(frameUpdates, [42n]);
  assert.equal(streams.latestFrame("mimir:kiyo-pro")?.sequence, 42n);
  unsubscribeStream();
  unsubscribeFrame();
  streams.declare({ ...stream, streamId: "mimir:kiyo-pro-alt" });
  streams.publishFrame({ ...frame, sequence: 43n });
  assert.deepEqual(streamUpdates, ["mimir:kiyo-pro"]);
  assert.deepEqual(frameUpdates, [42n]);
});

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}
