import sys, time, msgpack
sys.path.insert(0, sys.argv[1]); import proto_leaves as L
# a HullData-like record: 20 float32 slots, strings, nested list of 200 small records
rec = lambda i: ["part%d" % i, i, [L.Float32(0.5*i), L.Float32(1.5), L.Float32(2.5)], None]
tree = ["id", 3, [L.Float32(float(i)) for i in range(20)], [rec(i) for i in range(200)]]
plain = ["id", 3, [float(i) for i in range(20)], [["part%d" % i, i, [0.5*i,1.5,2.5], None] for i in range(200)]]
N=300
t=time.perf_counter(); [L.write(tree) for _ in range(N)]; a=time.perf_counter()-t
t=time.perf_counter(); [msgpack.packb(plain, use_bin_type=True) for _ in range(N)]; b=time.perf_counter()-t
t=time.perf_counter(); [msgpack.unpackb(msgpack.packb(plain)) for _ in range(N)]; c=time.perf_counter()-t
print(f"walk-writer {a/N*1e6:.0f} us/rec, packb {b/N*1e6:.0f} us/rec, ratio {a/b:.1f}x; size {len(L.write(tree))} vs {len(msgpack.packb(plain))}")
def pack_tree(t):
    try: return msgpack.packb(t, use_bin_type=True)
    except TypeError: return L.write(t)
assert pack_tree(plain) == L.write(plain) == msgpack.packb(plain, use_bin_type=True)
assert pack_tree(tree) == L.write(tree)
t=time.perf_counter(); [pack_tree(plain) for _ in range(N)]; p=time.perf_counter()-t
t=time.perf_counter(); [pack_tree(tree) for _ in range(N)]; q=time.perf_counter()-t
print(f"pack_tree plain {p/N*1e6:.0f} us, float32 tree {q/N*1e6:.0f} us")
try:
    pack_tree([object()])
except TypeError as e: print("custom object:", e)
