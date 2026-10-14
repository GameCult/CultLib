import json, sys, msgpack
print(sys.version.split()[0], msgpack.version)
def pk(v): return msgpack.packb(v, use_bin_type=True)
def up(b): return msgpack.unpackb(b, raw=False)
def jd(v): return json.dumps(v, ensure_ascii=False, sort_keys=True).encode()
cases = {"dict": {"schema":"x","n":1}, "list":[1,2], "str":"hi", "digit":5, "neg":-3, "true":True, "null":None, "float":1.5, "bigdigit":12}
print("== msgpack.unpackb on JSON payloads")
for k,v in cases.items():
    b = jd(v)
    try: r = up(b); print(k, b, "->ACCEPTED", repr(r))
    except Exception as e: print(k, b, "->", type(e).__module__+"."+type(e).__name__, isinstance(e, ValueError), str(e)[:60])
print("== json.loads on msgpack payloads")
for k,v in cases.items():
    b = pk(v)
    try: r = json.loads(b.decode("utf-8")); print(k, b, "->ACCEPTED", repr(r))
    except Exception as e: print(k, b, "->", type(e).__name__, str(e)[:50])
print("== semantic differences, value -> json roundtrip vs msgpack roundtrip")
diffs = {"intkeys": {1:"a"}, "tuple": (1,2), "bytes": b"ab", "bigint": 2**70, "nan": float("nan"), "boolkey": {True:1}, "nested_intkey": {"a":{2:3}}, "set": {1,2}, "order": {"b":1,"a":2}}
for k,v in diffs.items():
    out=[]
    try: out.append("json:"+repr(json.loads(jd(v))))
    except Exception as e: out.append("json:"+type(e).__name__)
    try: b=pk(v)
    except Exception as e: out.append("pack:"+type(e).__name__+":"+str(e)[:40]); print(k, out); continue
    try: out.append("mp:"+repr(up(b)))
    except Exception as e: out.append("unpack:"+type(e).__name__+":"+str(e)[:60])
    try: out.append("mp_nonstrict:"+repr(msgpack.unpackb(b, raw=False, strict_map_key=False)))
    except Exception as e: out.append("ns:"+type(e).__name__)
    print(k, out)
print("== custom node classes")
import collections, types
class OnlyIter:
    def __iter__(self): return iter([1])
for k,v in {"deque":collections.deque([1]), "mappingproxy":types.MappingProxyType({"a":1}), "userlist":collections.UserList([1]), "onlyiter":OnlyIter(), "frozenset":frozenset([1]), "userdict":collections.UserDict({"a":1})}.items():
    r=[]
    for name,f in (("pack",pk),("json",jd)):
        try: f(v); r.append(name+":ok")
        except Exception as e: r.append(name+":"+type(e).__name__)
    print(k, r)
print("== JSON read recursion threshold")
for d in (900, 1000, 3000, 5000, 10000, 100000):
    try: json.loads("["*d + "]"*d); print(d, "ok")
    except RecursionError: print(d, "RecursionError")
print("== msgpack read deep")
for d in (300, 1000, 2000, 100000):
    try: up(b"\x91"*d + b"\xc0"); print(d,"ok")
    except Exception as e: print(d, type(e).__module__, type(e).__name__)
