# Brokkr-shaped flow at CultLib 9d7b5842: a store written by today's JSON default, then read by a MessagePack-default definition.
import sys, tempfile, os, json, msgpack, timeit, dataclasses
sys.path.insert(0, r"F:/Projects/CultLib/packages/cultcache-py/src")
import cultcache_py as cc
from cultcache_py.stores import SingleFileMessagePackBackingStore
d = tempfile.mkdtemp(); path = os.path.join(d, "blender-editor.ccmp")
def open_cache(defn):
    c = cc.CultCache.builder().register_document_type(defn).add_generic_store(SingleFileMessagePackBackingStore(path)).build()
    c.pull_all_backing_stores(); return c
old = cc.define_document_type("brokkr.sync.var.v0")
value = {"schema": "brokkr.sync.var.v0", "syncVarId": "v1", "enabled": True, "kind": "transform", "updatedAt": "2026-10-11T00:00:00Z"}
c = open_cache(old); c.put(old, "sync/vars/v1", value)
raw = msgpack.unpackb(open(path, "rb").read(), raw=False)
print("store header", raw[0], "catalog entry", raw[1][0][:5] if raw[1] else None)
print("record", raw[2][0][:3], "payload", raw[2][0][3][:40])
# MessagePack-default reader, today's _messagepack pair standing in for the proposed default
new = cc.define_document_type("brokkr.sync.var.v0", payload_encoder=lambda t: msgpack.packb(t, use_bin_type=True), payload_decoder=lambda b: msgpack.unpackb(b, raw=False))
try: open_cache(new); print("new reader: ACCEPTED")
except Exception as e: print("new reader refused:", type(e).__module__, type(e).__name__, str(e)[:80])
# opt-in JSON pair reads it
print("json opt-in reads:", open_cache(old).get(old, "sync/vars/v1") == value)
# migration: read with JSON definition, write with MessagePack definition, same key
v = open_cache(old).get(old, "sync/vars/v1"); c2 = cc.CultCache.builder().register_document_type(new).add_generic_store(SingleFileMessagePackBackingStore(path)).build()
c2._state  # not pulled: pull would fail on the JSON doc
try:
    c2.put(new, "sync/vars/v1", v); print("put without pull ok")
except Exception as e: print("put without pull:", type(e).__name__, str(e)[:80])
print("after migration new reader reads:", open_cache(new).get(new, "sync/vars/v1") == value)
# size and speed, one Brokkr-shaped document
jb = json.dumps(value, ensure_ascii=False, sort_keys=True).encode(); mb = msgpack.packb(value, use_bin_type=True)
print("bytes json", len(jb), "msgpack", len(mb))
print("encode us json %.2f msgpack %.2f" % (timeit.timeit(lambda: json.dumps(value, ensure_ascii=False, sort_keys=True).encode(), number=100000)*10, timeit.timeit(lambda: msgpack.packb(value, use_bin_type=True), number=100000)*10))
print("decode us json %.2f msgpack %.2f" % (timeit.timeit(lambda: json.loads(jb.decode()), number=100000)*10, timeit.timeit(lambda: msgpack.unpackb(mb, raw=False), number=100000)*10))
