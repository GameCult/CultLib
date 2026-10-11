"""Writes Python stores whose catalog carries the C# type names the proposed leaves would derive, for the C# reference to open."""
import sys, os
sys.path.insert(0, r"F:\Projects\CultLib\packages\cultcache-py\src"); sys.path.insert(0, os.path.dirname(__file__))
import proto_leaves as L
from cultcache_py import CultCache, SingleFileMessagePackBackingStore
from cultcache_py.documents import define_document_type
from cultcache_py.backing_store import CultCacheSchemaCatalogMember as M

NS = "GameCult.Caching.Tests.PyLeavesProbe"
LIST = "System.Collections.Generic.List<{}>"
MEMBERS = [  # slot, member, C# type name the leaf derives
    (0, "Id", "System.String"), (1, "Flag", "System.Boolean"), (2, "Blob", "System.Byte[]"),
    (3, "I", "System.Int32"), (4, "L", "System.Int64"), (5, "F", "System.Single"), (6, "D", "System.Double"),
    (7, "MaybeI", "System.Nullable<System.Int32>"), (8, "Tags", LIST.format("System.String")),
    (9, "Names", "System.String[]"), (10, "Any", "System.Object"), (11, "Nested", NS + ".ProbeInner"),
    (12, "Items", LIST.format(NS + ".ProbeInner")), (13, "Shape", NS + ".IProbeShape"),
    (14, "Floats", "System.Single[]"), (15, "MaybeF", "System.Nullable<System.Single>"),
]
F32 = lambda x: L.Float32(L.f32(x))
def payload(doc_id):
    inner = lambda n, w: [n, F32(w)]
    return [doc_id, True, b"\x01\x02", 7, 2**40, F32(0.1), 0.1, None, ["a", "b"], ["x"], {"k": [1, "two"]},
            inner("n", 1.25), [inner("i0", 0.5), inner("i1", 2.0)], [0, [F32(3.5)]], [F32(1.0), F32(0.1)], F32(2.5)]

def write(path, members, tree_for):
    doc = define_document_type(
        "cultcache.py-leaves-probe", encode=lambda v: v, decode=lambda v: v,
        payload_encoder=L.write, payload_decoder=lambda b: b, name=lambda v: v[0],
        schema_name="cultcache.py-leaves-probe", schema_version="cultcache.py_leaves_probe.v1",
        members=[M(slot=s, member_name=n, type_name=t, is_name=(s == 0)) for s, n, t in members])
    if os.path.exists(path): os.remove(path)
    cache = CultCache.builder().register_document_type(doc).add_generic_store(SingleFileMessagePackBackingStore(path)).build()
    cache.pull_all_backing_stores()
    tree = tree_for(os.path.basename(path))
    cache.put(doc, tree[0], tree)
    print(path, "payload", L.write(tree).hex())

out = sys.argv[1]
write(os.path.join(out, "a-leaf-catalog.cc"), MEMBERS, payload)
# negative control: the int32 slot cataloged as the int64 leaf would name it
wrong = [(s, n, "System.Int64" if s == 3 else t) for s, n, t in MEMBERS]
write(os.path.join(out, "b-wrong-leaf.cc"), wrong, payload)
