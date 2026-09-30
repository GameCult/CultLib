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
base = msgpack.unpackb(whole, raw=False)

vectors = {
    "zero-byte": b"",
    "empty-array": pack([]),
    # A legacy envelope array: an array of maps. Rust, TypeScript and Python read it; C# has no legacy reader.
    "legacy-envelopes": pack([{"key": "old", "type": "old.type", "payload": pack([]), "storedAt": "2026-01-01T00:00:00Z"}]),
    "legacy-trailing": None,  # filled below: the legacy array plus bytes after it
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
    # A header and a body of the wrong shape, for both readable headers.
    "bad-body-v1": pack(["cultcache.store.v1", 5, 6]),
    "bad-body-v3": pack(["cultcache.store.v3", 5, 6]),
    # A header and nothing else: not a store, whatever the header says.
    "header-only-v1": pack(["cultcache.store.v1"]),
    "header-only-v3": pack(["cultcache.store.v3"]),
    # v3-base with its schema catalog emptied: every record names a schema the store does not publish.
    "missing-schema-v3": pack([base[0], [], base[2]]),
    # The same, with each payload opening with its schema version: no runtime recovers a schema from a payload.
    "missing-schema-versioned-v3": pack([base[0], [], [record[:3] + [pack(["vectors.item.v1", "n", 1])] for record in base[2]]]),
    # The record names its schema by an id the catalog publishes only as a compatible id of an entry with another id.
    "compatible-id-only-v3": pack([base[0], [[base[1][0][0] + ".next"] + base[1][0][1:]], base[2]]),
    # v3-base plus a variant of alpha: a fifth record slot, [baseKey, overrides[]], over an empty payload, overriding the name. Only C# reads it.
    "variant-slot-v3": pack([base[0], base[1], base[2] + [["gamma"] + base[2][0][1:3] + [b"", ["alpha", [[0, [[0, ""]], "", "gamma"]]]]]]),
    # v3-base with a fourth top-level slot after the records.
    "extra-top-slot": pack(base + [0]),
}
vectors["legacy-trailing"] = vectors["legacy-envelopes"] + b"\x01\x02\x03"
for name, data in vectors.items():
    (here / f"{name}.bin").write_bytes(data)
print(len(vectors), "vectors")
