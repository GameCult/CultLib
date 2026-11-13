import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { copyFile, mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";

import { decode, encode } from "@msgpack/msgpack";

import { documentPayloadBytes, documentPayloadFromBytes } from "../src/document";
import { SingleFileMessagePackBackingStore, readSingleFileStore, recordVersion } from "../src/single-file-messagepack-backing-store";
import { schemaIdForEnvelope } from "../src/store-format";
import type { CultCacheEnvelope } from "../src/types";

// The store owns the content version and the conditional verdict (variants:ruling:write-set-content-version-declared-ownership):
// read reports a record with its version, commitIf writes only while the version holds, and a write that replaces a record
// is stamped later than it (variants:ruling:storedat-on-change).

function findVectors(from: string): string {
  for (let dir = from; ; dir = resolve(dir, "..")) {
    if (existsSync(join(dir, "tests", "vectors", "content-version"))) return join(dir, "tests", "vectors", "content-version");
    if (resolve(dir, "..") === dir) throw new Error("tests/vectors/content-version not found above the test.");
  }
}

const entry = (key: string, byte: number, storedAt = "2026-01-01T00:00:00.000Z", type = "t"): CultCacheEnvelope => ({
  key,
  type,
  payload: documentPayloadFromBytes(Uint8Array.of(0x91, byte)),
  storedAt,
});

async function scratch(): Promise<string> {
  return mkdtemp(join(tmpdir(), "cultcache-conditional-"));
}

test("the content version of every record of the C#-written store equals the version the reference read", async () => {
  const vectors = findVectors(__dirname);
  const rows = decode(readFileSync(join(vectors, "versions.msgpack"))) as Array<[string, string]>;
  assert.equal(rows.length, 2);
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    await copyFile(join(vectors, "store.cc"), path);
    const store = new SingleFileMessagePackBackingStore(path);
    for (const [key, version] of rows) {
      const found = await store.read(key);
      assert.equal(found?.version, version, `read(${key}).version`);
      assert.equal(recordVersion(found!.envelope), version, `recordVersion(${key})`);
    }
    assert.equal(await store.read("absent"), undefined);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("the version is the SHA-256 of the record's persisted encoding and changes with every field of it", async () => {
  const base = entry("k", 1);
  const hex = (e: CultCacheEnvelope) =>
    createHash("sha256").update(encode([e.key, schemaIdForEnvelope(e), e.storedAt, documentPayloadBytes(e.payload)])).digest("hex");
  assert.equal(recordVersion(base), hex(base));
  const versions = new Set([base, entry("k", 2), entry("k", 1, "2026-01-01T00:00:00.001Z"), entry("j", 1), { ...base, schemaId: "sha256:other" }].map(recordVersion));
  assert.equal(versions.size, 5);
});

test("commitIf writes nothing when the version does not hold, and commits when it does", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const store = new SingleFileMessagePackBackingStore(path);
    // null expects no record: it commits over an absent key and mismatches over a present one.
    assert.deepEqual(await store.commitIf(entry("k", 1), "not-a-version"), { outcome: "mismatch" });
    assert.equal(existsSync(path), false, "a mismatch creates no file");
    assert.equal((await store.commitIf(entry("k", 1), null)).outcome, "committed");
    const first = (await store.read("k"))!;
    const before = await readFile(path);
    assert.deepEqual(await store.commitIf(entry("k", 2), null), { outcome: "mismatch" });
    assert.deepEqual(await store.commitIf(entry("k", 2), "0".repeat(64)), { outcome: "mismatch" });
    assert.deepEqual([...(await readFile(path))], [...before], "a mismatch leaves the bytes identical");
    assert.equal((await store.commitIf(entry("k", 2), first.version)).outcome, "committed");
    const second = (await store.read("k"))!;
    assert.notEqual(second.version, first.version);
    assert.deepEqual([...documentPayloadBytes(second.envelope.payload)], [0x91, 2]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("commitIf judges the record at the key whatever its type, and a record of another key does not matter", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    await store.push(entry("a", 1, undefined, "u"));
    await store.push(entry("b", 1));
    const other = (await store.read("a"))!;
    assert.deepEqual(await store.commitIf(entry("b", 2), other.version), { outcome: "mismatch" });
    assert.equal((await store.commitIf(entry("b", 2), recordVersion((await store.read("b"))!.envelope))).outcome, "committed");
    assert.deepEqual([...documentPayloadBytes((await store.read("a"))!.envelope.payload)], [0x91, 1], "the record at another key is untouched");
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a write that replaces a record is stored strictly later than it, whatever the write's own storedAt", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    const at = "2026-03-04T05:06:07.100Z";
    await store.push(entry("k", 1, at));
    // Not later (equal, earlier, unparseable) is written one millisecond after the record it replaces, by push, pushAll and commitIf.
    await store.push(entry("k", 2, at));
    assert.equal((await store.read("k"))!.envelope.storedAt, "2026-03-04T05:06:07.101Z");
    await store.push(entry("k", 3, "2020-01-01T00:00:00.000Z"));
    assert.equal((await store.read("k"))!.envelope.storedAt, "2026-03-04T05:06:07.102Z");
    await store.pushAll([entry("k", 4, "not a time")]);
    assert.equal((await store.read("k"))!.envelope.storedAt, "2026-03-04T05:06:07.103Z");
    const committed = await store.commitIf(entry("k", 5, "2020-01-01T00:00:00.000Z"), (await store.read("k"))!.version);
    assert.deepEqual(committed, { outcome: "committed", storedAt: "2026-03-04T05:06:07.104Z" });
    assert.equal((await store.read("k"))!.envelope.storedAt, "2026-03-04T05:06:07.104Z");
    // A later stamp is kept as it is, and a key with no record before it is stamped as it is.
    await store.push(entry("k", 6, "2030-01-01T00:00:00.000Z"));
    await store.push(entry("fresh", 1, "2000-01-01T00:00:00.000Z"));
    assert.equal((await store.read("k"))!.envelope.storedAt, "2030-01-01T00:00:00.000Z");
    assert.equal((await store.read("fresh"))!.envelope.storedAt, "2000-01-01T00:00:00.000Z");
    assert.equal(readSingleFileStore(join(directory, "store.cc")).envelopes.length, 2);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a replacing write that would stamp past year 9999 is refused and writes nothing", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    const last = "9999-12-31T23:59:59.999Z";
    await store.push(entry("k", 1, last));
    await assert.rejects(() => store.push(entry("k", 2, last)), RangeError);
    assert.equal((await store.read("k"))!.envelope.storedAt, last);
    assert.deepEqual([...documentPayloadBytes((await store.read("k"))!.envelope.payload)], [0x91, 1]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("a write that restores an earlier value does not restore its version", async () => {
  const directory = await scratch();
  try {
    const store = new SingleFileMessagePackBackingStore(join(directory, "store.cc"));
    const at = "2026-03-04T05:06:07.100Z";
    await store.push(entry("k", 1, at));
    const first = (await store.read("k"))!.version;
    await store.push(entry("k", 5, at));
    await store.push(entry("k", 1, at));
    assert.notEqual((await store.read("k"))!.version, first, "an A-B-A within one millisecond keeps no version");
    assert.deepEqual(await store.commitIf(entry("k", 9, at), first), { outcome: "mismatch" });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("two stores on one file never both commit against the same version", async () => {
  const directory = await scratch();
  try {
    const path = join(directory, "store.cc");
    const left = new SingleFileMessagePackBackingStore(path);
    const right = new SingleFileMessagePackBackingStore(path);
    await left.push(entry("k", 0));
    const version = (await left.read("k"))!.version;
    const outcomes = await Promise.all(Array.from({ length: 6 }, (_, i) => (i % 2 === 0 ? left : right).commitIf(entry("k", i + 1), version)));
    assert.equal(outcomes.filter((o) => o.outcome === "committed").length, 1);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
