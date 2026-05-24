import assert from "node:assert/strict";
import { exec, execFile } from "node:child_process";
import { access, mkdtemp, readFile } from "node:fs/promises";
import { homedir, tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { test } from "node:test";
import { promisify } from "node:util";

import { decode } from "@msgpack/msgpack";

import { CultMesh } from "../../src/index";
import {
  INTEROP_BODY,
  buildInteropNote,
  interopNoteDocument,
  type InteropNote,
} from "./cultmesh-interop-shared";

const execFileAsync = promisify(execFile);
const execAsync = promisify(exec);
const cargoCommand = process.env.CARGO ?? (process.platform === "win32" ? join(homedir(), ".cargo", "bin", "cargo.exe") : "cargo");
const dotnetCommand = process.env.DOTNET ?? (process.platform === "win32" ? join("C:", "Program Files", "dotnet", "dotnet.exe") : "dotnet");
const cultMeshTsRoot = resolve(__dirname, "../../..");
const cultLibRoot = resolve(cultMeshTsRoot, "..", "..");
const cultmeshRsRoot = resolve(cultLibRoot, "crates", "cultmesh-rs");
const rustInteropBinary = resolve(
  cultLibRoot,
  "target",
  "debug",
  "examples",
  process.platform === "win32" ? "cultmesh_interop.exe" : "cultmesh_interop",
);
const csharpInteropProject = resolve(
  cultLibRoot,
  "tests",
  "GameCult.Mesh.InteropPeer",
  "GameCult.Mesh.InteropPeer.csproj",
);
const csharpInteropDll = resolve(
  cultLibRoot,
  "bin",
  "GameCult.Mesh.InteropPeer",
  "Debug",
  "net10.0",
  "GameCult.Mesh.InteropPeer.dll",
);

test("CultMesh local nodes read and write one MessagePack store across TS, Rust, and C#", async () => {
  await buildInteropPeers();
  const tempDir = await mkdtemp(join(tmpdir(), "cultmesh-interop-"));
  const writers = [
    {
      name: "ts",
      write: async (file: string) => writeTsInteropStore(file, "ts-writer"),
    },
    {
      name: "rust",
      write: async (file: string) => runJsonCommand("rust-write", rustInteropBinary, [
        "write",
        "--file", file,
        "--runtime-id", "rust-writer",
      ], cultmeshRsRoot),
    },
    {
      name: "csharp",
      write: async (file: string) => runJsonCommand("csharp-write", dotnetCommand, [
        csharpInteropDll,
        "write",
        "--file", file,
        "--runtime-id", "csharp-writer",
      ], cultLibRoot),
    },
  ];
  const readers = [
    {
      name: "ts",
      read: async (file: string) => readTsInteropStore(file),
    },
    {
      name: "rust",
      read: async (file: string) => runJsonCommand("rust-read", rustInteropBinary, [
        "read",
        "--file", file,
      ], cultmeshRsRoot),
    },
    {
      name: "csharp",
      read: async (file: string) => runJsonCommand("csharp-read", dotnetCommand, [
        csharpInteropDll,
        "read",
        "--file", file,
      ], cultLibRoot),
    },
  ];

  for (const writer of writers) {
    const file = join(tempDir, `${writer.name}.msgpack`);
    const written = await writer.write(file);
    const decoded = decode(await readFile(file)) as unknown[];
    assert.equal(decoded[0], "cultcache.store.v1");
    assert.ok(Array.isArray(decoded[1]), `${writer.name} did not write a schema catalog`);
    assert.ok(Array.isArray(decoded[2]), `${writer.name} did not write records`);

    for (const reader of readers) {
      const read = await reader.read(file);
      assert.equal(read.documentId, written.documentId, `${reader.name} failed to read ${writer.name}`);
      assert.equal(read.authorRuntimeId, written.authorRuntimeId);
      assert.equal(read.verseId, "verse:interop");
      assert.equal(read.body, INTEROP_BODY);
      assert.ok(read.tags.includes("interop"));
      assert.ok(read.tags.includes("cultmesh"));
    }
  }
});

async function writeTsInteropStore(file: string, runtimeId: string): Promise<InteropNote> {
  const node = await CultMesh.startNode(file, {
    documents: [interopNoteDocument],
  });
  const note = buildInteropNote(runtimeId, "ts");
  await node.put(interopNoteDocument, note.documentId, note);
  await node.flush();
  return note;
}

async function readTsInteropStore(file: string): Promise<InteropNote> {
  const node = await CultMesh.startNode(file, {
    documents: [interopNoteDocument],
  });
  const note = node.cache.getAll(interopNoteDocument)[0];
  if (!note) {
    throw new Error("No cultmesh.interop-note records found.");
  }
  return note;
}

async function buildInteropPeers(): Promise<void> {
  if (!(await exists(rustInteropBinary))) {
    await execAsync(`"${cargoCommand}" build --quiet --example cultmesh_interop`, {
      cwd: cultmeshRsRoot,
    });
  }
  if (!(await exists(csharpInteropDll))) {
    await execAsync(`"${dotnetCommand}" build "${csharpInteropProject}" -nologo`, {
      cwd: cultLibRoot,
    });
  }
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function runJsonCommand(
  name: string,
  command: string,
  args: string[],
  cwd: string,
): Promise<InteropNote> {
  const { stdout, stderr } = await execFileAsync(command, args, { cwd, timeout: 30_000 });
  const trimmed = stdout.trim();
  if (!trimmed) {
    throw new Error(`${name} produced no stdout.\n${stderr}`);
  }

  return JSON.parse(trimmed.split(/\r?\n/).at(-1) as string) as InteropNote;
}
