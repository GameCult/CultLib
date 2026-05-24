import assert from "node:assert/strict";
import { execFile, spawn, type ChildProcessByStdio } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:net";
import { networkInterfaces } from "node:os";
import { resolve } from "node:path";
import type { Readable } from "node:stream";
import { test } from "node:test";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

const cultNetTsRoot = resolve(__dirname, "../../..");
const cultnetRsRoot = resolve(cultNetTsRoot, "..", "cultnet-rs");
const cultLibRoot = resolve(cultNetTsRoot, "..", "CultLib");

const tsPeerScript = resolve(cultNetTsRoot, "dist-test", "test", "interop", "cultnet-interop-peer.js");
const interopSchemaPath = resolve(cultNetTsRoot, "integration", "contracts", "cultnet.interop-note.schema.json");
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

test("CultNet TS/Rust/C# peers discover each other and exchange raw state over the shared schema-v0 lane", async (t) => {
  await buildInteropPeers();

  const discoveryPort = await getFreePort();
  const tsPort = await getFreePort();
  const rustPort = await getFreePort();
  const csharpPort = await getFreePort();
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
    command: "dotnet",
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

  const csharpDial = await runJsonCommand("csharp-dial", "dotnet", [
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

  const expectedPeers = ["csharp-peer", "rust-peer", "ts-peer"];

  const tsProbe = await runJsonCommand("ts-probe", process.execPath, [
    tsPeerScript,
    "probe",
    "--runtime-id", "ts-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "1500",
  ], cultNetTsRoot);
  assert.deepEqual(tsProbe.peers.map((peer: { runtimeId: string }) => peer.runtimeId).sort(), expectedPeers);

  const rustProbe = await runJsonCommand("rust-probe", rustBinaryPath, [
    "probe",
    "--runtime-id", "rust-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "1500",
  ], cultnetRsRoot);
  assert.deepEqual(rustProbe.peers.map((peer: { runtimeId: string }) => peer.runtimeId).sort(), expectedPeers);

  const csharpProbe = await runJsonCommand("csharp-probe", "dotnet", [
    csharpDllPath,
    "probe",
    "--runtime-id", "csharp-prober",
    "--discovery-port", String(discoveryPort),
    "--discovery-group", discoveryGroup,
    "--timeout-ms", "1500",
  ], cultLibRoot);
  assert.deepEqual(csharpProbe.peers.map((peer: { runtimeId: string }) => peer.runtimeId).sort(), expectedPeers);
});

async function buildInteropPeers(): Promise<void> {
  await execFileAsync("cargo", ["build", "--quiet", "--example", "cultnet_interop_peer"], {
    cwd: cultnetRsRoot,
  });
  await execFileAsync("dotnet", ["build", csharpProjectPath, "-nologo"], {
    cwd: cultLibRoot,
  });
}

interface ServeCommand {
  command: string;
  args: string[];
  cwd: string;
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
): Promise<any> {
  const { stdout, stderr } = await execFileAsync(command, args, { cwd, timeout: 30_000 });
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
