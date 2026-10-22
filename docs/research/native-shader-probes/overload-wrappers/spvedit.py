#!/usr/bin/env python3
"""Probe: instruction-level SPIR-V edit (binary, no text regex).
stats <spv> <wrappers.tsv>                 how each wrapper looks after dxc
edit <in> <out> <wrappers.tsv> <mode>      mode drop: strip caps (keep Shader, Linkage) and extensions, delete
  wrapper functions and their names/decorations, set each callee's OpName to the overloaded source name.
  mode export: as drop, but each wrapper's Export decoration moves to its callee instead of being deleted."""
import struct, sys, collections
def load(p):
    b = open(p, 'rb').read(); w = list(struct.unpack('<%dI' % (len(b) // 4), b)); ins = []; i = 5
    while i < len(w): n = w[i] >> 16; ins.append(w[i:i + n]); i += n
    return w[:5], ins
def s_dec(ws):
    b = struct.pack('<%dI' % len(ws), *ws); return b[:b.index(0)].decode()
def s_enc(s):
    b = s.encode() + b'\0'; b += b'\0' * (-len(b) % 4); return list(struct.unpack('<%dI' % (len(b) // 4), b))
def op(x): return x[0] & 0xffff
def funcs(ins):
    fs = {}; cur = None
    for k, x in enumerate(ins):
        if op(x) == 54: cur = x[2]; fs[cur] = [k, None, [], set()]
        if cur is not None:
            if op(x) == 57: fs[cur][2].append(x[3])
            o = op(x)
            if o == 248: fs[cur][3].add(x[1])
            elif o not in (8, 317, 62, 246, 247, 249, 250, 253, 254, 252) and len(x) > 2: fs[cur][3].add(x[2])
            if op(x) == 56: fs[cur][1] = k; cur = None
    return fs
def names(ins): return {x[1]: s_dec(x[2:]) for x in ins if op(x) == 5}
def wrappers(tsv): return [l.split('\t') for l in open(tsv).read().splitlines() if l]
def analyse(ins, tsv):
    nm = names(ins); fs = funcs(ins); byname = {v: k for k, v in nm.items() if k in fs}; res = []
    for wn, name, fn, ps in wrappers(tsv):
        fid = byname.get(wn); callees = fs[fid][2] if fid else None
        res.append((wn, name, fid, callees))
    return nm, fs, res
if sys.argv[1] == 'stats':
    h, ins = load(sys.argv[2]); nm, fs, res = analyse(ins, sys.argv[3])
    missing = [r[0] for r in res if r[2] is None]; inl = [r[0] for r in res if r[2] and not r[3]]
    multi = [r[0] for r in res if r[3] and len(r[3]) != 1]
    callee = collections.Counter(r[3][0] for r in res if r[3] and len(r[3]) == 1)
    wrong = [(r[0], nm.get(r[3][0])) for r in res if r[3] and len(r[3]) == 1 and nm.get(r[3][0]) != r[1]]
    print('functions %d, wrappers found %d of %d, missing %d, inlined (no call) %d, not-one-call %d' % (len(fs), len(res) - len(missing), len(res), len(missing), len(inl), len(multi)))
    print('distinct callees %d, callee shared by >1 wrapper %d' % (len(callee), sum(1 for c in callee.values() if c > 1)))
    print('callee OpName != source name: %d %s' % (len(wrong), wrong[:4]))
    calls = sum(len(f[2]) for f in fs.values()); print('total OpFunctionCall %d; non-wrapper functions %d' % (calls, len(fs) - len(res) + len(missing)))
    print('sample', [(r[0], nm.get(r[3][0]) if r[3] else None) for r in res[:3]], 'missing sample', missing[:3], 'inlined sample', inl[:3])
    sys.exit(0)
_, src, dst, tsv, mode = sys.argv[1:6]
h, ins = load(src); nm, fs, res = analyse(ins, tsv)
drop_k = set(); drop_ids = set(); setname = {}; retarget = {}
for wn, name, fid, callees in res:
    if fid is None or not callees or len(callees) != 1: sys.exit('refuse: wrapper %s is not one call' % wn)
    a, b, _, ids = fs[fid]; drop_k.update(range(a, b + 1)); drop_ids |= ids | {fid}
    setname[callees[0]] = name; retarget[fid] = callees[0]
drop_ids |= set(retarget)
outi = []; named = set()
for k, x in enumerate(ins):
    if k in drop_k: continue
    o = op(x)
    if o == 17 and x[1] not in (1, 5): continue
    if o == 10: continue
    if o == 5 and x[1] in setname: x = [0] + [x[1]] + s_enc(setname[x[1]]); x[0] = (len(x) << 16) | 5; named.add(x[1])
    elif o in (5, 6) and x[1] in drop_ids: continue
    if o == 71 and x[1] in drop_ids:
        if mode == 'export' and x[1] in retarget and x[2] == 41: x = [x[0], retarget[x[1]]] + x[2:]
        else: continue
    outi.append(x)
for fid, name in setname.items():
    if fid not in named: outi.insert(next(i for i, x in enumerate(outi) if op(x) == 5), [((2 + len(s_enc(name))) << 16) | 5, fid] + s_enc(name))
w = h + [v for x in outi for v in x]; open(dst, 'wb').write(struct.pack('<%dI' % len(w), *w))
print('edit %s: dropped %d wrapper functions, renamed %d callees, kept %d instructions' % (mode, len(res), len(setname), len(outi)))
