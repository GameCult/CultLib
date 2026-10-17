#!/usr/bin/env python3
"""Probe: by-value parameter lowering on a SPIR-V module. dxc -Od passes every parameter as a Function pointer and the
caller copies in; a callee that assigns its parameter then comes out of SPIRV-Cross as `inout`. This pass makes each
Function-pointer parameter a value parameter: the old param id becomes a local variable in the entry block, stored from the
new value parameter; each call loads the pointer it passed. usage: byvalue.py <in.spv> <out.spv>"""
import struct, sys
def load(p):
    b = open(p, 'rb').read(); w = list(struct.unpack('<%dI' % (len(b) // 4), b)); ins = []; i = 5
    while i < len(w): n = w[i] >> 16; ins.append(w[i:i + n]); i += n
    return w[:5], ins
def op(x): return x[0] & 0xffff
def mk(o, *ws): return [((len(ws) + 1) << 16) | o, *ws]
h, ins = load(sys.argv[1]); bound = h[3]
ptr = {x[1]: (x[2], x[3]) for x in ins if op(x) == 32}            # id -> (storage, pointee)
ftype = {x[1]: x for x in ins if op(x) == 33}
fnparams = {}; cur = None; fntype = {}
for x in ins:                                                       # analysis: per function, which params are Function pointers
    if op(x) == 54: cur = x[2]; fnparams[cur] = []; fntype[cur] = x[4]
    elif op(x) == 55 and cur is not None:
        t = x[1]; conv = t in ptr and ptr[t][0] == 7
        fnparams[cur].append((x[2], t, ptr[t][1] if conv else None, bound if conv else None)); bound += conv
newtype = {}; typeins = []; sig = {tuple(x[2:]): x[1] for x in ins if op(x) == 33}
for f, ps in fnparams.items():
    if any(p[2] for p in ps):
        ft = ftype[fntype[f]]; key = (ft[2], *[p[2] if p[2] else p[1] for p in ps])
        if key not in sig: bound += 1; sig[key] = bound; typeins.append(mk(33, bound, *key))
        newtype[f] = sig[key]
names = {x[1]: x for x in ins if op(x) == 5}
out = []; cur = None; pend = []; need_stores = False; first = True; pc = 0
for x in ins:
    o = op(x)
    if o == 54 and first: out += typeins; first = False
    if need_stores and o != 59:
        out += [mk(62, p, q) for p, _, q in pend]; pend = []; need_stores = False
    if o == 54:
        cur = x[2]; pc = 0
        if cur in newtype: x = x[:4] + [newtype[cur]] + x[5:]
    elif o == 55:
        _, t, pointee, q = fnparams[cur][pc]; pc += 1
        if pointee: out.append(mk(55, pointee, q)); pend.append((x[2], t, q)); continue
    elif o == 248 and pend:
        out.append(x); out += [mk(59, t, p, 7) for p, t, _ in pend]; need_stores = True; continue
    elif o == 57:
        callee = x[3]; args = x[4:]; new = []
        for a, (_, _, pointee, _) in zip(args, fnparams[callee]):
            if pointee: bound += 1; out.append(mk(61, pointee, bound, a)); new.append(bound)
            else: new.append(a)
        x = x[:4] + new; x[0] = (len(x) << 16) | 57
    out.append(x)
    if o == 5 and x[1] in [p[0] for ps in fnparams.values() for p in ps if p[2]]:
        q = next(p[3] for ps in fnparams.values() for p in ps if p[0] == x[1]); out.append([x[0], q] + x[2:])
h[3] = bound + 1; w = h + [v for x in out for v in x]; open(sys.argv[2], 'wb').write(struct.pack('<%dI' % len(w), *w))
print('byvalue: %d functions changed, %d params' % (len(newtype), sum(1 for ps in fnparams.values() for p in ps if p[2])))
