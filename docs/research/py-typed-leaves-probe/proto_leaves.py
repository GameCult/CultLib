"""Scratch prototype of cultcache_py.leaves, to establish mechanisms against the C# vectors. Not for the repo."""
import math, struct, msgpack

class Refused(ValueError): pass

def kind(v):
    if v is None: return "nil"
    if isinstance(v, bool): return "a boolean"
    if isinstance(v, int): return "an integer"
    if isinstance(v, float): return "a float"
    if isinstance(v, str): return "a string"
    if isinstance(v, (bytes, bytearray, memoryview)): return "bytes"
    if isinstance(v, (list, tuple)): return "an array"
    if isinstance(v, dict): return "a map"
    return type(v).__name__

def refuse(what, v): raise Refused(f"expected {what}, found {kind(v)}")

class Float32:  # tree marker: written as MessagePack float 32
    __slots__ = ("value",)
    def __init__(self, v): self.value = v

def int_to_f32(v: int) -> float:
    if abs(v) <= 2**53:
        return f32(float(v))
    m = abs(v); excess = m.bit_length() - 24
    kept = m >> excess; rest = m - (kept << excess); half = 1 << (excess - 1)
    if rest > half or (rest == half and kept & 1): kept += 1
    r = float(kept) * 2.0**excess  # kept<=2^24 exact; scaling exact unless overflow
    return f32(-r if v < 0 else r)

def f32(x: float) -> float:
    try:
        return struct.unpack(">f", struct.pack(">f", x))[0]
    except OverflowError:
        return math.copysign(math.inf, x)

class Leaf:
    def __init__(self, type_name, enc, dec, value_type=False):
        self.type_name, self.encode, self.decode, self.value_type = type_name, enc, dec, value_type

def _int_leaf(name, what, lo, hi):
    def enc(v):
        if not isinstance(v, int) or isinstance(v, bool): refuse(what, v)
        if not lo <= v <= hi: raise Refused(f"expected {what}, found an integer out of range")
        return v
    return Leaf(name, enc, enc, True)

int32 = _int_leaf("System.Int32", "an int32", -2**31, 2**31-1)
int64 = _int_leaf("System.Int64", "an int64", -2**63, 2**63-1)

def _f32_enc(v):
    if isinstance(v, bool) or not isinstance(v, (int, float)): refuse("a float32", v)
    return Float32(int_to_f32(v) if isinstance(v, int) else f32(v))
def _f32_dec(v):
    if isinstance(v, bool) or not isinstance(v, (int, float)): refuse("a float32", v)
    return int_to_f32(v) if isinstance(v, int) else f32(v)
float32 = Leaf("System.Single", _f32_enc, _f32_dec, True)
def _f64(v):
    if isinstance(v, bool) or not isinstance(v, (int, float)): refuse("a float64", v)
    return float(v)  # int > 2^53 rounds once (Python int->float is correctly rounded)
float64 = Leaf("System.Double", _f64, _f64, True)
def _str(v):
    if v is not None and not isinstance(v, str): refuse("a string", v)
    return v
string = Leaf("System.String", _str, _str)
def _bool(v):
    if not isinstance(v, bool): refuse("a boolean", v)
    return v
boolean = Leaf("System.Boolean", _bool, _bool, True)

# writer: msgpack-python per value, float32 through a single-float packer, containers by header
_P = msgpack.Packer(use_bin_type=True)
_P32 = msgpack.Packer(use_bin_type=True, use_single_float=True)
def write(tree) -> bytes:
    out = bytearray()
    stack = [tree]
    while stack:
        n = stack.pop()
        if isinstance(n, Float32): out += _P32.pack(n.value)
        elif isinstance(n, list):
            out += _P.pack_array_header(len(n)); stack.extend(reversed(n))
        elif isinstance(n, dict):
            out += _P.pack_map_header(len(n))
            for k, v in reversed(list(n.items())): stack.append(v); stack.append(k)
        else: out += _P.pack(n)
    return bytes(out)
