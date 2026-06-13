import assert from "node:assert/strict";
import { execFile, spawn, type ChildProcessByStdio } from "node:child_process";
import { createHash } from "node:crypto";
import { once } from "node:events";
import { existsSync, rmSync } from "node:fs";
import { connect as connectTcp, createServer } from "node:net";
import { homedir, networkInterfaces, tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import type { Readable } from "node:stream";
import { test } from "node:test";
import { promisify } from "node:util";
import { decode, encode } from "@msgpack/msgpack";
import { encodeFrame, LengthPrefixedMessageFramer } from "../../src/framing";
import { INTEROP_SCHEMA_VERSION } from "./cultnet-interop-shared";

const execFileAsync = promisify(execFile);

const cargoCommand = process.env.CARGO ?? (process.platform === "win32" ? join(homedir(), ".cargo", "bin", "cargo.exe") : "cargo");
const dotnetCommand = process.env.DOTNET ?? (process.platform === "win32" ? join("C:", "Program Files", "dotnet", "dotnet.exe") : "dotnet");
const pythonCommand = process.env.PYTHON ?? "python";
const cultNetTsRoot = resolve(__dirname, "../../..");
const cultLibRoot = findAncestor(cultNetTsRoot, "CultLib.sln") ?? resolve(cultNetTsRoot, "..", "CultLib");
const cultcachePyRoot = resolve(cultLibRoot, "packages", "cultcache-py");
const cultcachePySrc = resolve(cultcachePyRoot, "src");
const cultnetRsRoot = existsSync(resolve(cultLibRoot, "packages", "cultnet-rs"))
  ? resolve(cultLibRoot, "packages", "cultnet-rs")
  : resolve(cultNetTsRoot, "..", "cultnet-rs");

const tsPeerScript = resolve(cultNetTsRoot, "dist-test", "test", "interop", "cultnet-interop-peer.js");
const interopSchemaPath = resolve(cultNetTsRoot, "integration", "contracts", "cultnet.interop-note.schema.json");
const interopNoteSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note.schema.json";
const witnessArtifactBundleSchemaId = "https://github.com/GameCult/cultnet-ts/contracts/cultnet.witness-artifact-bundle.schema.json";
const csharpProjectPath = resolve(
  cultLibRoot,
  "tests",
  "GameCult.Networking.InteropPeer",
  "GameCult.Networking.InteropPeer.csproj",
);
const csharpDllPath = resolve(
  cultLibRoot,
  "bin",
  "GameCult.Networking.InteropPeer",
  "Debug",
  "net10.0",
  "GameCult.Networking.InteropPeer.dll",
);
const rustBinaryPath = resolve(
  cultnetRsRoot,
  "target",
  "debug",
  "examples",
  process.platform === "win32" ? "cultnet_interop_peer.exe" : "cultnet_interop_peer",
);

const discoveryGroup = "239.77.44.11";

test("CultNet TS/Rust/C#/Python peers discover each other and exchange raw state over the shared schema-v0 lane", async (t) => {
  await buildInteropPeers();
  cleanInteropStores([
    "rust-peer",
    "rust-client-dial",
    "ts-peer",
    "ts-client-dial",
    "ts-rust-dial",
    "ts-csharp-dial",
    "python-peer",
    "python-client-dial",
  ]);

  const discoveryPort = await getFreePort();
  const tsPort = await getFreePort();
  const rustPort = await getFreePort();
  const csharpPort = await getFreePort();
  const pythonPort = await getFreePort();
  const advertiseHost = findAdvertiseHost();

  const servers: RunningServeProcess[] = [];
  servers.push(await spawnServeProcess("rust", {
    command: rustBinaryPath,
    args: [
      "serve",
      "--runtime-id", "rust-peer",
      "--runtime-kind", "rust",
      "--display-name", "Rust Peer",
      "--agent-id", "rust-agent",
      "--bind-host", "127.0.0.1",
      "--advertise-host", advertiseHost,
      "--tcp-port", String(rustPort),
      "--discovery-port", String(discoveryPort),
      "--discovery-group", discoveryGroup,
      "--schema-path", interopSchemaPath,
    ],
    cwd: cultnetRsRoot,
  }));
  await servers[servers.length - 1].ready;

  servers.push(await spawnServeProcess("ts", {
    command: process.execPath,
    args: [
      tsPeerScript,
      "serve",
      "--runtime-id", "ts-peer",
      "--runtime-kind", "node",
      "--display-name", "TypeScript Peer",
      "--agent-id", "ts-agent",
      "--bind-host", "127.0.0.1",
      "--advertise-host", advertiseHost,
      "--tcp-port", String(tsPort),
      "--discovery-port", String(discoveryPort),
      "--discovery-group", discoveryGroup,
      "--schema-path", interopSchemaPath,
    ],
    cwd: cultNetTsRoot,
  }));
  await servers[servers.length - 1].ready;

  servers.push(await spawnServeProcess("csharp", {
    command: dotnetCommand,
    args: [
      csharpDllPath,
      "serve",
      "--runtime-id", "csharp-peer",
      "--runtime-kind", "dotnet",
      "--display-name", "CSharp Peer",
      "--agent-id", "csharp-agent",
      "--bind-host", "127.0.0.1",
      "--advertise-host", advertiseHost,
      "--tcp-port", String(csharpPort),
      "--discovery-port", String(discoveryPort),
      "--discovery-group", discoveryGroup,
      "--schema-path", interopSchemaPath,
    ],
    cwd: cultLibRoot,
  }));
  await servers[servers.length - 1].ready;

  servers.push(await spawnServeProcess("python", {
    command: pythonCommand,
    args: [
      "-m", "cultnet_py.interop_peer",
      "serve",
      "--runtime-id", "python-peer",
      "--runtime-kind", "python",
      "--display-name", "Python Peer",
      "--agent-id", "python-agent",
      "--bind-host", "127.0.0.1",
      "--advertise-host", advertiseHost,
      "--tcp-port", String(pythonPort),
      "--discovery-port", String(discoveryPort),
      "--discovery-group", discoveryGroup,
      "--schema-path", interopSchemaPath,
    ],
    cwd: cultcachePyRoot,
    env: { PYTHONPATH: cultcachePySrc },
  }));
  await servers[servers.length - 1].ready;

  t.after(async () => {
    await Promise.all(servers.map(stopProcess));
  });

  const tsDial = await runJsonCommand("ts-dial", process.execPath, [
    tsPeerScript,
    "dial",
    "--runtime-id", "ts-client",
    "--runtime-kind", "node",
    "--display-name", "TS Dialer",
    "--agent-id", "ts-client-agent",
    "--target-host", "127.0.0.1",
    "--target-port", String(rustPort),
    "--schema-path", interopSchemaPath,
  ], cultNetTsRoot);
  assert.equal(tsDial.remoteHello.runtimeId, "rust-peer");
  assert.equal(tsDial.hasInteropSchema, true);
  assert.equal(tsDial.retrievedNote.authorRuntimeId, "rust-peer");
  assert.ok(tsDial.mutatedNote.tags.includes("decorated:ts-client"));
  assert.equal(tsDial.mutationReceipt.accepted, true);
  assert.equal(tsDial.fireReceipt.shotsFired, 1);

  const rustDial = await runJsonCommand("rust-dial", rustBinaryPath, [
    "dial",
    "--runtime-id", "rust-client",
    "--runtime-kind", "rust",
    "--display-name", "Rust Dialer",
    "--agent-id", "rust-client-agent",
    "--target-host", "127.0.0.1",
    "--target-port", String(csharpPort),
    "--schema-path", interopSchemaPath,
  ], cultnetRsRoot);
  assert.equal(rustDial.remoteHello.runtimeId, "csharp-peer");
  assert.equal(rustDial.hasInteropSchema, true);
  assert.equal(rustDial.retrievedNote.authorRuntimeId, "csharp-peer");
  assert.ok(rustDial.mutatedNote.tags.includes("decorated:rust-client"));
  assert.equal(rustDial.fireReceipt.ammoRemaining, 29);

  const csharpDial = await runJsonCommand("csharp-dial", dotnetCommand, [
    csharpDllPath,
    "dial",
    "--runtime-id", "csharp-client",
    "--runtime-kind", "dotnet",
    "--display-name", "CSharp Dialer",
    "--agent-id", "csharp-client-agent",
    "--target-host", "127.0.0.1",
    "--target-port", String(tsPort),
    "--schema-path", interopSchemaPath,
  ], cultLibRoot);
  assert.equal(csharpDial.remoteHello.runtimeId, "ts-peer");
  assert.equal(csharpDial.hasInteropSchema, true);
  assert.equal(csharpDial.retrievedNote.authorRuntimeId, "ts-peer");
  assert.ok(csharpDial.mutatedNote.tags.includes("decorated:csharp-client"));
  assert.equal(csharpDial.fireReceipt.accepted, true);

  const pythonDial = await runJsonCommand("python-dial", pythonCommand, [
    "-m", "cultnet_py.interop_peer",
    "dial",
    "--runtime-id", "python-client",
    "--runtime-kind", "python",
    "--display-name", "Python Dialer",
    "--agent-id", "python-client-agent",
    "--target-host", "127.0.0.1",
    "--target-port", String(rustPort),
    "--schema-path", interopSchemaPath,
  ], cultcachePyRoot, { PYTHONPATH: cultcachePySrc });
  assert.equal(pythonDial.remoteHello.runtimeId, "rust-peer");
  assert.equal(pythonDial.hasInteropSchema, true);
  assert.equal(pythonDial.retrievedNote.authorRuntimeId, "rust-peer");
  assert.ok(pythonDial.mutatedNote.tags.includes("decorated:python-client"));
  assert.equal(pythonDial.fireReceipt.accepted, true);

  const tsDialPython = await runJsonCommand("ts-dial-python", process.execPath, [
    tsPeerScript,
    "dial",
    "--runtime-id", "ts-python-client",
    "--runtime-kind", "node",
    "--display-name", "TS Python Dialer",
    "--agent-id", "ts-python-client-agent",
    "--target-host", "127.0.0.1",
    "--target-port", String(pythonPort),
    "--schema-path", interopSchemaPath,
  ], cultNetTsRoot);
  assert.equal(tsDialPython.remoteHello.runtimeId, "python-peer");
  assert.equal(tsDialPython.hasInteropSchema, true);
  assert.equal(tsDialPython.retrievedNote.authorRuntimeId, "python-peer");
  assert.ok(tsDialPython.mutatedNote.tags.includes("decorated:ts-python-client"));
  assert.equal(tsDialPython.fireReceipt.ammoRemaining, 29);

  const pythonWireCatalog = await requestCultMeshFromPython(pythonPort, {
    schemaVersion: "cultnet.schema_catalog_request.v0",
    messageId: "python-wire-catalog",
    includeSchemaJson: true,
    kinds: ["wire_message"],
  });
  assert.equal(pythonWireCatalog.schemaVersion, "cultnet.schema_catalog_response.v0");
  assert.equal(pythonWireCatalog.messageId, "python-wire-catalog");
  const pythonWireVersions = new Set(pythonWireCatalog.schemas.map((schema: any) => schema.schemaVersion));
  for (const schemaVersion of [
    "cultnet.document_delete.v0",
    "cultnet.database_change_raw.v0",
    "cultnet.shard_log_response.v0",
    "cultnet.simulation_consensus_candidate.v0",
    "cultmesh.peer_exchange_response.v0",
  ]) {
    assert.ok(pythonWireVersions.has(schemaVersion), `Python wire catalog should include ${schemaVersion}`);
  }
  const pythonDeleteDescriptor = pythonWireCatalog.schemas.find((schema: any) => schema.schemaVersion === "cultnet.document_delete.v0");
  assert.equal(pythonDeleteDescriptor.kind, "wire_message");
  assert.ok(pythonDeleteDescriptor.wireContracts.includes("cultnet.schema.v0"));
  assert.ok(String(pythonDeleteDescriptor.schemaJson).includes("cultnet.document_delete.v0"));
  const pythonDeleteSchema = JSON.parse(String(pythonDeleteDescriptor.schemaJson));
  assert.deepEqual(pythonDeleteSchema.required, ["schemaVersion", "messageId", "schemaId", "recordKey"]);
  assert.equal(pythonDeleteSchema.properties.recordKey.type, "string");

  const pythonVerseCatalog = await requestCultMeshFromPython(pythonPort, {
    schemaVersion: "cultmesh.verse_catalog_request.v0",
    messageId: "python-verses",
    transportVersion: "cultmesh.v0",
  });
  assert.equal(pythonVerseCatalog.schemaVersion, "cultmesh.verse_catalog_response.v0");
  assert.equal(pythonVerseCatalog.messageId, "python-verses");
  assert.equal(pythonVerseCatalog.verses[0].verseId, "python-interop");
  assert.equal(pythonVerseCatalog.verses[0].compatibility.transportVersion, "cultmesh.v0");

  const pythonPeerExchange = await requestCultMeshFromPython(pythonPort, {
    schemaVersion: "cultmesh.peer_exchange_request.v0",
    messageId: "python-peers",
    verseId: "python-interop",
    roles: ["read-replica"],
  });
  assert.equal(pythonPeerExchange.schemaVersion, "cultmesh.peer_exchange_response.v0");
  assert.equal(pythonPeerExchange.messageId, "python-peers");
  assert.equal(pythonPeerExchange.peers[0].peerId, "python-peer");
  assert.ok(pythonPeerExchange.peers[0].roles.includes("read-replica"));

  const pythonCultMeshClient = await runPythonCultMeshClient(pythonPort);
  assert.equal(pythonCultMeshClient.verses[0].verseId, "python-interop");
  assert.equal(pythonCultMeshClient.verses[0].transportVersion, "cultmesh.v0");
  assert.equal(pythonCultMeshClient.peers[0].peerId, "python-peer");
  assert.ok(pythonCultMeshClient.peers[0].roles.includes("read-replica"));

  const pythonCultNetClient = await runPythonCultNetRawClient(pythonPort);
  assert.ok(pythonCultNetClient.wireSchemaCount >= 1);
  assert.ok(pythonCultNetClient.snapshotRecordKeys.includes("note:python-peer"));
  assert.equal(pythonCultNetClient.shards[0].shardId, "interop");

  const pythonChange = await requestPythonDatabaseChange(pythonPort, {
    schemaVersion: "cultnet.database_subscribe.v0",
    messageId: "python-subscribe",
    subscriptionId: "python-sub",
    schemaIds: [interopNoteSchemaId],
    recordKeys: ["note:ts-python-sub"],
    includeSnapshot: false,
  }, {
    schemaVersion: "cultnet.document_put_raw.v0",
    messageId: "python-sub-put",
    document: {
      schemaId: interopNoteSchemaId,
      recordKey: "note:ts-python-sub",
      storedAt: "2026-06-13T00:00:00Z",
      payloadEncoding: "messagepack",
      payload: encode([
        INTEROP_SCHEMA_VERSION,
        "note:ts-python-sub",
        "ts-python-sub",
        "Subscription note",
        "Python streamed this raw change through the CultNet database lane.",
        ["interop", "subscription"],
      ]),
      sourceRuntimeId: "ts-python-sub",
    },
  });
  assert.equal(pythonChange.schemaVersion, "cultnet.database_change_raw.v0");
  assert.equal(pythonChange.subscriptionId, "python-sub");
  assert.equal(pythonChange.changeKind, "added");
  assert.equal(pythonChange.document.schemaId, interopNoteSchemaId);
  assert.equal(pythonChange.document.recordKey, "note:ts-python-sub");

  const pythonShard = await requestPythonShardCatalogAndLog(pythonPort, {
    schemaVersion: "cultnet.shard_catalog_request.v0",
    messageId: "python-shards",
    schemaIds: [interopNoteSchemaId],
    recordKeys: ["note:ts-python-log"],
  }, {
    schemaVersion: "cultnet.document_put_raw.v0",
    messageId: "python-shard-put",
    document: {
      schemaId: interopNoteSchemaId,
      recordKey: "note:ts-python-log",
      storedAt: "2026-06-13T00:00:01Z",
      payloadEncoding: "messagepack",
      payload: encode([
        INTEROP_SCHEMA_VERSION,
        "note:ts-python-log",
        "ts-python-log",
        "Shard log note",
        "Python exposes accepted raw puts through the CultNet shard log lane.",
        ["interop", "shard-log"],
      ]),
      sourceRuntimeId: "ts-python-log",
    },
  }, {
    schemaVersion: "cultnet.shard_log_request.v0",
    messageId: "python-log",
    shardId: "interop",
    shardEpoch: 1,
    afterSequence: 0,
  });
  assert.equal(pythonShard.catalog.schemaVersion, "cultnet.shard_catalog_response.v0");
  assert.equal(pythonShard.catalog.messageId, "python-shards");
  assert.equal(pythonShard.catalog.shards[0].shardId, "interop");
  assert.equal(pythonShard.catalog.shards[0].ownerRuntimeId, "python-peer");
  assert.equal(pythonShard.catalog.shards[0].epoch, 1);
  assert.equal(pythonShard.log.schemaVersion, "cultnet.shard_log_response.v0");
  assert.equal(pythonShard.log.messageId, "python-log");
  assert.equal(pythonShard.log.shardId, "interop");
  assert.equal(pythonShard.log.shardEpoch, 1);
  assert.equal(pythonShard.log.resyncRequired, false);
  assert.ok(pythonShard.log.entries.some((entry: any) =>
    entry.changeKind === "added"
    && entry.put?.messageId === "python-shard-put"
    && entry.put?.document?.recordKey === "note:ts-python-log"
  ));

  const pythonDelete = await requestPythonDatabaseDelete(pythonPort, {
    schemaVersion: "cultnet.database_subscribe.v0",
    messageId: "python-delete-subscribe",
    subscriptionId: "python-delete-sub",
    schemaIds: [interopNoteSchemaId],
    recordKeys: ["note:ts-python-delete"],
    includeSnapshot: false,
  }, {
    schemaVersion: "cultnet.document_put_raw.v0",
    messageId: "python-delete-put",
    document: {
      schemaId: interopNoteSchemaId,
      recordKey: "note:ts-python-delete",
      storedAt: "2026-06-13T00:00:01Z",
      payloadEncoding: "messagepack",
      payload: encode([
        INTEROP_SCHEMA_VERSION,
        "note:ts-python-delete",
        "ts-python-delete",
        "Delete note",
        "Python should remove this through the CultNet delete lane.",
        ["interop", "delete"],
      ]),
      sourceRuntimeId: "ts-python-delete",
    },
  }, {
    schemaVersion: "cultnet.document_delete.v0",
    messageId: "python-delete",
    schemaId: interopNoteSchemaId,
    recordKey: "note:ts-python-delete",
    shardId: "interop",
    shardEpoch: 1,
  }, {
    schemaVersion: "cultnet.shard_log_request.v0",
    messageId: "python-delete-log",
    shardId: "interop",
    shardEpoch: 1,
    afterSequence: 0,
  });
  assert.equal(pythonDelete.change.schemaVersion, "cultnet.database_change_raw.v0");
  assert.equal(pythonDelete.change.subscriptionId, "python-delete-sub");
  assert.equal(pythonDelete.change.changeKind, "removed");
  assert.equal(pythonDelete.change.schemaId, interopNoteSchemaId);
  assert.equal(pythonDelete.change.recordKey, "note:ts-python-delete");
  assert.ok(pythonDelete.log.entries.some((entry: any) =>
    entry.changeKind === "removed"
    && entry.delete?.messageId === "python-delete"
    && entry.delete?.schemaId === interopNoteSchemaId
    && entry.delete?.recordKey === "note:ts-python-delete"
    && entry.delete?.shardEpoch === 1
  ));

  const claimHash = computeSimulationClaimHash("frame:42", "subject:python-target", "hit");
  const pythonCandidate = await requestPythonSimulationCandidate(pythonPort, {
    schemaVersion: "cultnet.simulation_observation.v0",
    messageId: "python-observation",
    observation: {
      witnessRuntimeId: "ts-witness",
      shardId: "interop",
      shardEpoch: 1,
      frame: 42,
      subjectId: "python-target",
      claimKind: "hit",
      claimHash,
      claimSummary: "python-target was hit by the TypeScript witness",
      weight: 1,
      observedAt: "2026-06-13T00:00:02Z",
    },
  });
  assert.equal(pythonCandidate.schemaVersion, "cultnet.simulation_consensus_candidate.v0");
  assert.equal(pythonCandidate.messageId, "python-observation");
  assert.equal(pythonCandidate.shardId, "interop");
  assert.equal(pythonCandidate.shardEpoch, 1);
  assert.equal(pythonCandidate.frame, 42);
  assert.equal(pythonCandidate.subjectId, "python-target");
  assert.equal(pythonCandidate.claimKind, "hit");
  assert.equal(pythonCandidate.claimHash, claimHash);
  assert.equal(pythonCandidate.witnessCount, 1);
  assert.equal(pythonCandidate.supportWeight, 1);
  assert.equal(pythonCandidate.totalWeight, 1);
  assert.equal(pythonCandidate.hasQuorum, true);
  assert.equal(pythonCandidate.confidence, 1);

  const pythonWitness = await putAndSnapshotPythonWitnessBundle(pythonPort, {
    schemaVersion: "cultnet.document_put_raw.v0",
    messageId: "python-witness-put",
    document: {
      schemaId: witnessArtifactBundleSchemaId,
      recordKey: "bundle:ts-python-witness",
      storedAt: "2026-06-13T00:00:03Z",
      payloadEncoding: "messagepack",
      payload: encode([
        "bundle:ts-python-witness",
        "interop-proof",
        "2026-06-13T00:00:03Z",
        {
          documentType: "cultnet.interop-note",
          subjectId: "note:python-peer",
          schemaVersion: INTEROP_SCHEMA_VERSION,
          schemaId: interopNoteSchemaId,
        },
        [{ role: "payload", schemaId: interopNoteSchemaId, schemaVersion: INTEROP_SCHEMA_VERSION }],
        [{ role: "log", uri: "cultcache://bundle:ts-python-witness/log", mediaType: "text/plain" }],
        [{ stage: "roundtrip", startedAt: "2026-06-13T00:00:03Z", completedAt: "2026-06-13T00:00:04Z", latencyMs: 1 }],
        { pipelineId: "interop", runId: "ts-python-witness", runtimeId: "ts-witness", agentId: "ts-agent" },
      ]),
      sourceRuntimeId: "ts-witness",
    },
  });
  assert.equal(pythonWitness.catalog.schemaVersion, "cultnet.schema_catalog_response.v0");
  assert.equal(pythonWitness.catalog.schemas[0].schemaId, witnessArtifactBundleSchemaId);
  assert.equal(pythonWitness.catalog.schemas[0].documentType, "cultnet.witness_artifact_bundle");
  assert.ok(String(pythonWitness.catalog.schemas[0].schemaJson).includes("CultNet Witness Artifact Bundle"));
  assert.equal(pythonWitness.snapshot.schemaVersion, "cultnet.snapshot_response_raw.v0");
  assert.equal(pythonWitness.snapshot.documents[0].schemaId, witnessArtifactBundleSchemaId);
  assert.equal(pythonWitness.snapshot.documents[0].recordKey, "bundle:ts-python-witness");
  const witnessSlots = decode(pythonWitness.snapshot.documents[0].payload) as any[];
  assert.equal(witnessSlots[0], "bundle:ts-python-witness");
  assert.equal(witnessSlots[1], "interop-proof");
  assert.equal(witnessSlots[3].subjectId, "note:python-peer");
  assert.equal(witnessSlots[7].runtimeId, "ts-witness");

  const expectedPeers = ["csharp-peer", "python-peer", "rust-peer", "ts-peer"];

  await expectProbePeers("ts-probe", process.execPath, [
    tsPeerScript,
    "probe",
    "--runtime-id", "ts-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "3000",
  ], cultNetTsRoot, {}, expectedPeers);

  await expectProbePeers("rust-probe", rustBinaryPath, [
    "probe",
    "--runtime-id", "rust-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "3000",
  ], cultnetRsRoot, {}, expectedPeers);

  await expectProbePeers("csharp-probe", dotnetCommand, [
    csharpDllPath,
    "probe",
    "--runtime-id", "csharp-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "3000",
  ], cultLibRoot, {}, expectedPeers);

  await expectProbePeers("python-probe", pythonCommand, [
    "-m", "cultnet_py.interop_peer",
    "probe",
    "--runtime-id", "python-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "3000",
  ], cultcachePyRoot, { PYTHONPATH: cultcachePySrc }, expectedPeers);
});

async function buildInteropPeers(): Promise<void> {
  await execFileAsync(cargoCommand, ["build", "--quiet", "--example", "cultnet_interop_peer"], {
    cwd: cultnetRsRoot,
  });
  await execFileAsync(dotnetCommand, ["build", csharpProjectPath, "-nologo"], {
    cwd: cultLibRoot,
  });
}

function cleanInteropStores(runtimeIds: readonly string[]): void {
  for (const runtimeId of runtimeIds) {
    rmSync(join(tmpdir(), `cultnet-rs-interop-${runtimeId}.msgpack`), { force: true });
    rmSync(join(tmpdir(), `cultnet-ts-interop-${runtimeId}.msgpack`), { force: true });
    rmSync(join(tmpdir(), `cultnet-py-interop-${runtimeId}.msgpack`), { force: true });
  }
}

function findAncestor(start: string, marker: string): string | undefined {
  let current = start;
  while (true) {
    if (existsSync(resolve(current, marker))) {
      return current;
    }

    const parent = dirname(current);
    if (parent === current) {
      return undefined;
    }

    current = parent;
  }
}

interface ServeCommand {
  command: string;
  args: string[];
  cwd: string;
  env?: NodeJS.ProcessEnv;
}

interface RunningServeProcess {
  name: string;
  child: ChildProcessByStdio<null, Readable, Readable>;
  ready: Promise<unknown>;
  stderr: string[];
}

async function spawnServeProcess(name: string, command: ServeCommand): Promise<RunningServeProcess> {
  const child = spawn(command.command, command.args, {
    cwd: command.cwd,
    env: { ...process.env, ...command.env },
    stdio: ["ignore", "pipe", "pipe"],
  });
  const stderr: string[] = [];
  let stdoutBuffer = "";

  const ready = new Promise<unknown>((resolve, reject) => {
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      stdoutBuffer += chunk;
      while (true) {
        const newline = stdoutBuffer.indexOf("\n");
        if (newline === -1) {
          break;
        }

        const line = stdoutBuffer.slice(0, newline).trim();
        stdoutBuffer = stdoutBuffer.slice(newline + 1);
        if (!line) {
          continue;
        }

        try {
          const parsed = JSON.parse(line) as { status?: string };
          if (parsed.status === "ready") {
            resolve(parsed);
            return;
          }
        } catch (error) {
          reject(new Error(`${name} emitted non-JSON stdout while starting: ${line}`));
          return;
        }
      }
    });

    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      stderr.push(chunk);
    });

    child.once("exit", (code, signal) => {
      reject(new Error(`${name} serve process exited before becoming ready (code=${code}, signal=${signal}).\n${stderr.join("")}`));
    });
    child.once("error", reject);
  });

  return { name, child, ready, stderr };
}

async function runJsonCommand(
  name: string,
  command: string,
  args: string[],
  cwd: string,
  env: NodeJS.ProcessEnv = {},
): Promise<any> {
  const { stdout, stderr } = await execFileAsync(command, args, {
    cwd,
    env: { ...process.env, ...env },
    timeout: 90_000,
  });
  const trimmed = stdout.trim();
  if (!trimmed) {
    throw new Error(`${name} produced no stdout.\n${stderr}`);
  }

  const lines = trimmed.split(/\r?\n/).filter(Boolean);
  try {
    return JSON.parse(lines.at(-1) as string);
  } catch (error) {
    throw new Error(`${name} did not end with JSON stdout.\nstdout:\n${stdout}\nstderr:\n${stderr}`);
  }
}

async function expectProbePeers(
  name: string,
  command: string,
  args: string[],
  cwd: string,
  env: NodeJS.ProcessEnv,
  expectedPeers: string[],
): Promise<void> {
  const found = new Set<string>();
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const probe = await runJsonCommand(`${name}-${attempt + 1}`, command, args, cwd, env);
    for (const peer of probe.peers as Array<{ runtimeId: string }>) {
      found.add(peer.runtimeId);
    }
    if (expectedPeers.every((peer) => found.has(peer))) {
      assert.deepEqual([...found].sort(), expectedPeers);
      return;
    }
  }

  assert.deepEqual([...found].sort(), expectedPeers);
}

async function requestCultMeshFromPython(port: number, request: Record<string, unknown>): Promise<any> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error(`Timed out waiting for Python CultMesh response to ${request.schemaVersion}.`));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        clearTimeout(timeout);
        socket.end();
        resolve(decode(frame));
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode(request)));
  });
}

async function runPythonCultMeshClient(port: number): Promise<any> {
  const script = [
    "import json",
    "from cultmesh_py import CultMeshDiscoveryClient",
    `client = CultMeshDiscoveryClient("127.0.0.1", ${port}, timeout_seconds=4.0)`,
    "verses = client.fetch_verses(transport_version='cultmesh.v0')",
    "peers = client.fetch_peers(verse_id='python-interop', roles=['read-replica'])",
    "print(json.dumps({",
    "  'verses': [{'verseId': verse.verse_id, 'transportVersion': verse.compatibility.transport_version} for verse in verses],",
    "  'peers': [{'peerId': peer.peer_id, 'roles': list(peer.roles)} for peer in peers],",
    "}))",
  ].join("\n");
  const { stdout } = await execFileAsync(pythonCommand, ["-c", script], {
    cwd: cultcachePyRoot,
    env: {
      ...process.env,
      PYTHONPATH: cultcachePySrc,
    },
    timeout: 8000,
  });
  return JSON.parse(stdout);
}

async function runPythonCultNetRawClient(port: number): Promise<any> {
  const script = [
    "import json",
    "from cultnet_py import CultNetRawClient",
    `client = CultNetRawClient("127.0.0.1", ${port}, timeout_seconds=4.0)`,
    "catalog = client.fetch_schema_catalog(kinds=['wire_message'])",
    `snapshot = client.fetch_snapshot(schema_ids=['${interopNoteSchemaId}'])`,
    `shards = client.fetch_shard_catalog(schema_ids=['${interopNoteSchemaId}'])`,
    "print(json.dumps({",
    "  'wireSchemaCount': len(catalog.get('schemas', [])),",
    "  'snapshotRecordKeys': [document.get('recordKey') for document in snapshot.get('documents', [])],",
    "  'shards': [{'shardId': shard.get('shardId'), 'epoch': shard.get('epoch')} for shard in shards.get('shards', [])],",
    "}))",
  ].join("\n");
  const { stdout } = await execFileAsync(pythonCommand, ["-c", script], {
    cwd: cultcachePyRoot,
    env: {
      ...process.env,
      PYTHONPATH: cultcachePySrc,
    },
    timeout: 8000,
  });
  return JSON.parse(stdout);
}

async function requestPythonDatabaseChange(port: number, subscribe: Record<string, unknown>, put: Record<string, unknown>): Promise<any> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Timed out waiting for Python database change response."));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        const decoded = decode(frame) as any;
        if (decoded?.schemaVersion === "cultnet.database_change_raw.v0") {
          clearTimeout(timeout);
          socket.end();
          resolve(decoded);
        }
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode(subscribe)));
    socket.write(encodeFrame(encode(put)));
  });
}

async function requestPythonDatabaseDelete(
  port: number,
  subscribe: Record<string, unknown>,
  put: Record<string, unknown>,
  remove: Record<string, unknown>,
  logRequest: Record<string, unknown>,
): Promise<{ change: any; log: any }> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const result: { change?: any; log?: any } = {};
    let sawAdded = false;
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Timed out waiting for Python delete change/log responses."));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        const decoded = decode(frame) as any;
        if (decoded?.schemaVersion === "cultnet.database_change_raw.v0" && decoded.changeKind === "added") {
          sawAdded = true;
          socket.write(encodeFrame(encode(remove)));
          socket.write(encodeFrame(encode(logRequest)));
        } else if (decoded?.schemaVersion === "cultnet.database_change_raw.v0" && decoded.changeKind === "removed") {
          result.change = decoded;
        } else if (decoded?.schemaVersion === "cultnet.shard_log_response.v0") {
          result.log = decoded;
        }
        if (sawAdded && result.change && result.log) {
          clearTimeout(timeout);
          socket.end();
          resolve({ change: result.change, log: result.log });
        }
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode(subscribe)));
    socket.write(encodeFrame(encode(put)));
  });
}

async function requestPythonShardCatalogAndLog(
  port: number,
  catalogRequest: Record<string, unknown>,
  put: Record<string, unknown>,
  logRequest: Record<string, unknown>,
): Promise<{ catalog: any; log: any }> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const result: { catalog?: any; log?: any } = {};
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Timed out waiting for Python shard catalog/log responses."));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        const decoded = decode(frame) as any;
        if (decoded?.schemaVersion === "cultnet.shard_catalog_response.v0") {
          result.catalog = decoded;
          socket.write(encodeFrame(encode(put)));
          socket.write(encodeFrame(encode(logRequest)));
        } else if (decoded?.schemaVersion === "cultnet.shard_log_response.v0") {
          result.log = decoded;
        }
        if (result.catalog && result.log) {
          clearTimeout(timeout);
          socket.end();
          resolve({ catalog: result.catalog, log: result.log });
        }
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode(catalogRequest)));
  });
}

async function requestPythonSimulationCandidate(port: number, observation: Record<string, unknown>): Promise<any> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Timed out waiting for Python simulation consensus candidate."));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        const decoded = decode(frame) as any;
        if (decoded?.schemaVersion === "cultnet.simulation_consensus_candidate.v0") {
          clearTimeout(timeout);
          socket.end();
          resolve(decoded);
        }
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode(observation)));
  });
}

function computeSimulationClaimHash(...parts: string[]): string {
  return createHash("sha256").update(parts.join("\x1f"), "utf8").digest("hex");
}

async function putAndSnapshotPythonWitnessBundle(port: number, put: Record<string, unknown>): Promise<{ catalog: any; snapshot: any }> {
  const socket = connectTcp(port, "127.0.0.1");
  const framer = new LengthPrefixedMessageFramer();
  await once(socket, "connect");

  return await new Promise((resolve, reject) => {
    const result: { catalog?: any; snapshot?: any } = {};
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Timed out waiting for Python witness artifact bundle roundtrip."));
    }, 5000);

    socket.on("data", (chunk) => {
      for (const frame of framer.push(chunk)) {
        const decoded = decode(frame) as any;
        if (decoded?.schemaVersion === "cultnet.schema_catalog_response.v0") {
          result.catalog = decoded;
          socket.write(encodeFrame(encode(put)));
          socket.write(encodeFrame(encode({
            schemaVersion: "cultnet.snapshot_request.v0",
            messageId: "python-witness-snapshot",
            schemaIds: [witnessArtifactBundleSchemaId],
            recordKeys: ["bundle:ts-python-witness"],
          })));
        } else if (decoded?.schemaVersion === "cultnet.snapshot_response_raw.v0") {
          result.snapshot = decoded;
        }
        if (result.catalog && result.snapshot) {
          clearTimeout(timeout);
          socket.end();
          resolve({ catalog: result.catalog, snapshot: result.snapshot });
        }
      }
    });
    socket.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    socket.write(encodeFrame(encode({
      schemaVersion: "cultnet.schema_catalog_request.v0",
      messageId: "python-witness-catalog",
      includeSchemaJson: true,
      schemaIds: [witnessArtifactBundleSchemaId],
    })));
  });
}

async function stopProcess(processState: RunningServeProcess): Promise<void> {
  if (processState.child.killed || processState.child.exitCode !== null) {
    return;
  }

  processState.child.kill("SIGTERM");
  await once(processState.child, "exit");
}

async function getFreePort(): Promise<number> {
  const server = createServer();
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Failed to allocate an ephemeral port.");
  }

  const { port } = address;
  await new Promise<void>((resolve, reject) => {
    server.close((error) => {
      if (error) {
        reject(error);
        return;
      }

      resolve();
    });
  });

  return port;
}

function findAdvertiseHost(): string {
  const interfaces = networkInterfaces();
  for (const entries of Object.values(interfaces)) {
    for (const entry of entries ?? []) {
      if (entry.family === "IPv4" && !entry.internal) {
        return entry.address;
      }
    }
  }

  return "127.0.0.1";
}
