import msgpack, struct, sys
d = sys.argv[1]
rows = msgpack.unpackb(open(d+"/reads.msgpack","rb").read(), raw=False)
from collections import defaultdict
t = defaultdict(list)
for leaf, payload, ok, bits in rows:
    tok = payload[1:].hex()
    t[leaf].append((tok, ok, bits.hex()))
for leaf, cases in t.items():
    print(leaf, "accepts", sum(ok for _,ok,_ in cases), "of", len(cases))
    print("  refused:", [tok for tok,ok,_ in cases if not ok])
# float accepted from integer? int from float?
f = msgpack.unpackb(open(d+"/float32-nan.msgpack","rb").read(), raw=False)
for p, b in f:
    v = msgpack.unpackb(p)[0]
    bits = struct.pack(">d", v).hex()
    repacked = msgpack.packb([v], use_single_float=True).hex()
    print("nan in", p.hex(), "C# out", b.hex(), "py double", bits, "py repack f32", repacked)
