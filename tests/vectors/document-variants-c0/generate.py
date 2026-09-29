"""Generates the shared C0 refusal vectors from the pre-Cut 2 v1 store (written before any
variant code existed). Every runtime's tests read the committed bytes; nothing regenerates them
at test time. Run: python generate.py  (needs msgpack).

  unknown-header.msgpack          header cultcache.store.v9, otherwise the v1 store
  extra-slot-full-payload.msgpack v1 header, record item:anvil gains slot 4, payload stays full
  v1-base.msgpack                 NOT generated here: written once by the Python runtime at CultLib
                                  69a21bb (SingleFileMessagePackBackingStore.push of alpha and
                                  beta, type vectors.item, before any C0 code existed)
  variant-v2.msgpack              what C1 writes for a variant: header v2, record slot 4 =
                                  [baseKey, overrides[]], empty payload (override op Set = 0,
                                  path steps are [slot, elementId])
"""
import pathlib

import msgpack

here = pathlib.Path(__file__).resolve().parent
source = here.parent.parent / "GameCult.Caching.Tests" / "Fixtures" / "pre-cut2" / "single-file-v1" / "store.msgpack"
header, catalog, records = msgpack.unpackb(source.read_bytes(), raw=False)
assert header == "cultcache.store.v1"
anvil = records[0]
assert anvil[0] == "item:anvil"


def write(name, snapshot):
    (here / name).write_bytes(msgpack.packb(snapshot, use_bin_type=True))


write("unknown-header.msgpack", ["cultcache.store.v9", catalog, records])
write("extra-slot-full-payload.msgpack",
      [header, catalog, [anvil + [["item:bellows", []]]] + records[1:]])
variant = ["item:anvil-big", anvil[1], anvil[2], b"", ["item:anvil", [[0, [[1, ""]], "", 99]]]]
write("variant-v2.msgpack", ["cultcache.store.v2", catalog, records + [variant]])
