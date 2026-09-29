"""Generates the shared store-readability vectors. Every runtime's tests read the committed bytes and the verdicts in
manifest.txt; nothing regenerates them at test time. Run: python generate.py  (needs msgpack).

A file may be replaced by a flush or a commit exactly when the runtime's own reader opens it, so one verdict per runtime
covers open, flush and commit. Each vector is the bytes named; the trailing and truncated ones are cut from v3-base.
"""
import pathlib

import msgpack

here = pathlib.Path(__file__).resolve().parent
whole = (here.parent / "v3-base.msgpack").read_bytes()
pack = lambda value: msgpack.packb(value, use_bin_type=True)

vectors = {
    "zero-byte": b"",
    "empty-array": pack([]),
    # A legacy envelope array: an array of maps. Rust, TypeScript and Python read it; C# has no legacy reader.
    "legacy-envelopes": pack([{"key": "old", "type": "old.type", "payload": pack([]), "storedAt": "2026-01-01T00:00:00Z"}]),
    "ints": pack([1, 2, 3]),
    "nil-first": pack([None, "x"]),
    "nested-array": pack([["cultcache.store.v9", [], []]]),
    "string-first": pack(["hello", 1]),
    "header-v9": pack(["cultcache.store.v9", [], []]),
    "scalar": b"\x01",
    "map": b"\x80",
    "truncated": whole[:-1],
    "trailing-bytes": whole + b"\x01\x02\x03",
    "trailing-store": whole + whole,
}
for name, data in vectors.items():
    (here / f"{name}.bin").write_bytes(data)
print(len(vectors), "vectors")
