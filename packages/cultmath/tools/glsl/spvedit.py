#!/usr/bin/env python3
"""The one instruction-level SPIR-V edit of the GLSL build. Binary, never text.

usage: spvedit.py <in.spv> <out.spv> <wrappers.tsv>
1. Keeps only the capabilities Shader and Linkage and drops every OpExtension.
2. Deletes each wrapper function with its OpName and decorations, moves its LinkageAttributes Export to its callee, and
   sets the callee's OpName to the source name. Refuses a wrapper that is not exactly one OpFunctionCall.
3. By-value lowering: dxc -Od passes every parameter as a Function pointer and the caller copies in, so a callee that
   assigns its parameter comes out of SPIRV-Cross as `inout`. Each Function-pointer OpFunctionParameter becomes a value
   parameter with a new id; the old id stays as an entry-block OpVariable stored from it (OpName copied to the new id);
   OpTypeFunction is reused if the signature exists, else added; each OpFunctionCall loads the pointer it passed.
The header bound is last id + 1."""
import struct, sys

OP_NAME, OP_MEMBER_NAME, OP_CAPABILITY, OP_EXTENSION, OP_DECORATE = 5, 6, 17, 10, 71
OP_TYPE_POINTER, OP_TYPE_FUNCTION, OP_FUNCTION, OP_PARAMETER, OP_FUNCTION_END = 32, 33, 54, 55, 56
OP_CALL, OP_LABEL, OP_VARIABLE, OP_LOAD, OP_STORE = 57, 248, 59, 61, 62
CAP_SHADER, CAP_LINKAGE, DECORATION_LINKAGE, LINKAGE_EXPORT, FUNCTION_STORAGE = 1, 5, 41, 41, 7
TERMINATORS = (8, 317, 62, 246, 247, 249, 250, 253, 254, 252)


def load(path):
    raw = open(path, 'rb').read()
    words = list(struct.unpack('<%dI' % (len(raw) // 4), raw))
    ins, i = [], 5
    while i < len(words):
        n = words[i] >> 16
        ins.append(words[i:i + n])
        i += n
    return words[:5], ins


def op(x): return x[0] & 0xffff
def mk(o, *ws): return [((len(ws) + 1) << 16) | o, *ws]


def decode(ws):
    b = struct.pack('<%dI' % len(ws), *ws)
    return b[:b.index(0)].decode()


def encode(s):
    b = s.encode() + b'\0'
    b += b'\0' * (-len(b) % 4)
    return list(struct.unpack('<%dI' % (len(b) // 4), b))


def functions(ins):
    """function id -> [first index, last index, callee ids, ids defined inside]"""
    fs, cur = {}, None
    for k, x in enumerate(ins):
        o = op(x)
        if o == OP_FUNCTION:
            cur = x[2]
            fs[cur] = [k, None, [], set()]
        if cur is None:
            continue
        if o == OP_CALL:
            fs[cur][2].append(x[3])
        if o == OP_LABEL:
            fs[cur][3].add(x[1])
        elif o not in TERMINATORS and len(x) > 2:
            fs[cur][3].add(x[2])
        if o == OP_FUNCTION_END:
            fs[cur][1] = k
            cur = None
    return fs


def export_edit(h, ins, rows):
    names = {x[1]: decode(x[2:]) for x in ins if op(x) == OP_NAME}
    fs = functions(ins)
    by_name = {v: k for k, v in names.items() if k in fs}
    drop_k, drop_ids, set_name, retarget = set(), set(), {}, {}
    for wn, name, _, _ in rows:
        fid = by_name.get(wn)
        callees = fs[fid][2] if fid else None
        if not callees or len(callees) != 1:
            sys.exit('refuse: wrapper %s is not exactly one OpFunctionCall' % wn)
        a, b, _, ids = fs[fid]
        drop_k.update(range(a, b + 1))
        drop_ids |= ids | {fid}
        set_name[callees[0]] = name
        retarget[fid] = callees[0]
    out, named = [], set()
    for k, x in enumerate(ins):
        if k in drop_k:
            continue
        o = op(x)
        if o == OP_CAPABILITY and x[1] not in (CAP_SHADER, CAP_LINKAGE):
            continue
        if o == OP_EXTENSION:
            continue
        if o == OP_NAME and x[1] in set_name:
            x = mk(OP_NAME, x[1], *encode(set_name[x[1]]))
            named.add(x[1])
        elif o in (OP_NAME, OP_MEMBER_NAME) and x[1] in drop_ids:
            continue
        if o == OP_DECORATE and x[1] in drop_ids:
            if x[1] in retarget and x[2] == DECORATION_LINKAGE:
                x = [x[0], retarget[x[1]]] + x[2:]
            else:
                continue
        out.append(x)
    for fid, name in set_name.items():
        if fid not in named:
            out.insert(next(i for i, x in enumerate(out) if op(x) == OP_NAME), mk(OP_NAME, fid, *encode(name)))
    return out, len(rows)


def by_value(h, ins):
    bound = h[3]
    ptr = {x[1]: (x[2], x[3]) for x in ins if op(x) == OP_TYPE_POINTER}      # id -> (storage, pointee)
    ftype = {x[1]: x for x in ins if op(x) == OP_TYPE_FUNCTION}
    fnparams, fntype, cur = {}, {}, None
    for x in ins:                                    # per function: which parameters are Function pointers
        if op(x) == OP_FUNCTION:
            cur = x[2]
            fnparams[cur], fntype[cur] = [], x[4]
        elif op(x) == OP_PARAMETER and cur is not None:
            t = x[1]
            conv = t in ptr and ptr[t][0] == FUNCTION_STORAGE
            fnparams[cur].append((x[2], t, ptr[t][1] if conv else None, bound if conv else None))
            bound += conv
    newid = {p[0]: p[3] for ps in fnparams.values() for p in ps if p[2]}
    newtype, typeins = {}, []
    sig = {tuple(x[2:]): x[1] for x in ins if op(x) == OP_TYPE_FUNCTION}
    for f, ps in fnparams.items():
        if any(p[2] for p in ps):
            key = (ftype[fntype[f]][2], *[p[2] if p[2] else p[1] for p in ps])
            if key not in sig:
                bound += 1
                sig[key] = bound
                typeins.append(mk(OP_TYPE_FUNCTION, bound, *key))
            newtype[f] = sig[key]
    out, cur, pend, need_stores, first, pc = [], None, [], False, True, 0
    for x in ins:
        o = op(x)
        if o == OP_FUNCTION and first:
            out += typeins
            first = False
        if need_stores and o != OP_VARIABLE:
            out += [mk(OP_STORE, p, q) for p, _, q in pend]
            pend, need_stores = [], False
        if o == OP_FUNCTION:
            cur, pc = x[2], 0
            if cur in newtype:
                x = x[:4] + [newtype[cur]] + x[5:]
        elif o == OP_PARAMETER:
            _, t, pointee, q = fnparams[cur][pc]
            pc += 1
            if pointee:
                out.append(mk(OP_PARAMETER, pointee, q))
                pend.append((x[2], t, q))
                continue
        elif o == OP_LABEL and pend:
            out.append(x)
            out += [mk(OP_VARIABLE, t, p, FUNCTION_STORAGE) for p, t, _ in pend]
            need_stores = True
            continue
        elif o == OP_CALL:
            args = []
            for a, (_, _, pointee, _) in zip(x[4:], fnparams[x[3]]):
                if pointee:
                    bound += 1
                    out.append(mk(OP_LOAD, pointee, bound, a))
                    args.append(bound)
                else:
                    args.append(a)
            x = mk(OP_CALL, *x[1:4], *args)
        out.append(x)
        if o == OP_NAME and x[1] in newid:
            out.append([x[0], newid[x[1]]] + x[2:])
    h[3] = bound + 1
    return out, len(newtype), len(newid)


def main(src, dst, tsv):
    h, ins = load(src)
    rows = [l.split('\t') for l in open(tsv).read().splitlines() if l]
    ins, wrappers = export_edit(h, ins, rows)
    ins, changed, params = by_value(h, ins)
    words = h + [v for x in ins for v in x]
    open(dst, 'wb').write(struct.pack('<%dI' % len(words), *words))
    print('spvedit: %d wrappers dropped; by-value: %d functions changed, %d parameters' % (wrappers, changed, params))


if __name__ == '__main__':
    main(*sys.argv[1:4])
