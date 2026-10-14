import sys, struct, msgpack, math
sys.path.insert(0, sys.argv[2])
import proto_leaves as L
d = sys.argv[1]
leaves = {"int32": L.int32, "int64": L.int64, "float32": L.float32, "float64": L.float64}
rows = msgpack.unpackb(open(d+"/reads.msgpack","rb").read(), raw=False)
bad = 0
for leaf, payload, ok, bits in rows:
    raw = msgpack.unpackb(payload, raw=False)[0]
    try:
        v = leaves[leaf].decode(raw); got = True
        b = struct.pack(">q", v) if leaf.startswith("int") else struct.pack(">d", v)
    except L.Refused: got, b = False, b""
    if got != ok or (ok and b != bits):
        bad += 1; print("READ DIFF", leaf, payload.hex(), "C#", ok, bits.hex(), "py", got, b.hex())
print("reads", len(rows), "diffs", bad)
wr = msgpack.unpackb(open(d+"/writes.msgpack","rb").read(), raw=False)
bad = 0
for leaf, text, want in wr:
    v = int(text) if leaf.startswith("int") else struct.unpack(">d", bytes.fromhex(text))[0]
    got = L.write([leaves[leaf].encode(v)])
    if got != want: bad += 1; print("WRITE DIFF", leaf, text, want.hex(), got.hex())
print("writes", len(wr), "diffs", bad)
mixed = L.write([L.string.encode("n1"), L.int32.encode(7), L.int64.encode(8), L.float32.encode(1.5), L.float64.encode(2.5)])
print("mixed equal:", mixed == open(d+"/mixed-record.msgpack","rb").read(), mixed.hex())
