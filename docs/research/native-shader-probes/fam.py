#!/usr/bin/env python3
"""Probe: per fixture family, generate an HLSL and a GLSL ES 3.10 compute shader that call the family's
function on the fixture's argument bits, and compare device results with the fixture's result bits.
usage: fam.py gen <fixture> <outdir> | fam.py cmp <fixture> <outdir> <tag>"""
import json, re, struct, sys, os

SCAL = {'float': ('float', 1, 'f'), 'int': ('int', 1, 'i'), 'uint': ('uint', 1, 'u')}
def ty(t):
    if t in SCAL: return SCAL[t]
    m = re.fullmatch(r'(i|u|)vec([234])', t)
    if m:
        k = {'': 'f', 'i': 'i', 'u': 'u'}[m.group(1)]; n = int(m.group(2))
        return ({'f': 'float', 'i': 'int', 'u': 'uint'}[k] + str(n), n, k)
    if t == 'mat3': return ('float3x3', 9, 'f')
    return None

def parse(sig):
    m = re.fullmatch(r'(\w+)\s+(\w+)\((.*)\)', sig)
    ps = [p.strip() for p in m.group(3).split(',') if p.strip()]
    return m.group(1), m.group(2), ps

def hlsl_read(k, words):
    return {'f': 'asfloat', 'i': 'asint', 'u': ''}[k] + '(' + words + ')'

def gen(fx, out):
    d = json.load(open(fx)); man = []; skipped = []
    for idx, f in enumerate(d['functions']):
        r, name, ps = parse(f['glsl'])
        rt = ty(r); pts = [ty(p) for p in ps]
        if rt is None or any(p is None for p in pts):
            skipped.append(f['glsl']); continue
        nin = sum(p[1] for p in pts); nout = rt[1]
        tag = 'f%03d' % idx
        words = []
        for c in f['cases']:
            a, b = c.split('->'); aw = a.split(); assert len(aw) == nin, (f['glsl'], len(aw), nin)
            words += [int(x, 16) for x in aw]
        open(os.path.join(out, tag + '.in'), 'wb').write(struct.pack('<%dI' % len(words), *words) if words else b'\0\0\0\0')
        # HLSL
        hl, gl, off = [], [], 0
        for j, (p, (ht, n, k)) in enumerate(zip(ps, pts)):
            idxs = ', '.join('In[b + %d]' % (off + t) for t in range(n))
            gidx = ', '.join('In.a[b + %du]' % (off + t) for t in range(n))
            if ht == 'float3x3': raise SystemExit('mat3 arg unsupported')
            vec = ('uint%d(%s)' % (n, idxs)) if n > 1 else idxs
            hl.append('    %s a%d = %s;' % (ht, j, hlsl_read(k, vec) if k != 'u' else vec))
            gvec = ('uvec%d(%s)' % (n, gidx)) if n > 1 else gidx
            gfun = {'f': 'uintBitsToFloat', 'i': ('ivec%d' % n if n > 1 else 'int'), 'u': ''}[k]
            gl.append('    %s a%d = %s(%s);' % (p, j, gfun, gvec))
            off += n
        args = ', '.join('a%d' % j for j in range(len(ps)))
        hl.append('    %s r = %s(%s);' % (rt[0], name, args))
        gl.append('    %s r = %s(%s);' % (r, name, args))
        if rt[0] == 'float3x3':
            comps = ['r[%d][%d]' % (i, j) for i in range(3) for j in range(3)]
        elif rt[1] == 1: comps = ['r']
        else: comps = ['r[%d]' % i for i in range(rt[1])]
        for i, c in enumerate(comps):
            hl.append('    Out[o + %d] = %s;' % (i, {'f': 'asuint', 'i': 'asuint', 'u': ''}[rt[2]] + '(' + c + ')'))
            gl.append('    Out.a[o + %du] = %s;' % (i, {'f': 'floatBitsToUint', 'i': 'uint', 'u': ''}[rt[2]] + '(' + c + ')'))
        n = len(f['cases'])
        open(os.path.join(out, tag + '.hlsl'), 'w').write(
            '#include "CultMath.hlsl"\n[[vk::binding(0)]] StructuredBuffer<uint> In;\n[[vk::binding(1)]] RWStructuredBuffer<uint> Out;\n'
            '[numthreads(64, 1, 1)] void main(uint3 id : SV_DispatchThreadID) {\n    uint i = id.x; if (i >= %du) return; uint b = i * %du; uint o = i * %du;\n%s\n}\n'
            % (n, nin, nout, '\n'.join(hl)))
        open(os.path.join(out, tag + '.body.glsl'), 'w').write(
            'layout(local_size_x = 64) in;\nlayout(std430, binding = 0) readonly buffer IB { uint a[]; } In;\nlayout(std430, binding = 1) buffer OB { uint a[]; } Out;\n'
            'void main() {\n    uint i = gl_GlobalInvocationID.x; if (i >= %du) return; uint b = i * %du; uint o = i * %du;\n%s\n}\n'
            % (n, nin, nout, '\n'.join(gl)))
        man.append({'tag': tag, 'glsl': f['glsl'], 'n': n, 'nout': nout, 'kind': rt[2], 'tol': f['tolerance'], 'platform': f.get('platform')})
    json.dump(man, open(os.path.join(out, 'manifest.json'), 'w'))
    print('generated', len(man), 'families; skipped (struct types)', len(skipped), ':', '; '.join(skipped))

def ordered(u):  # float bits -> monotonic int for ulp distance
    return u if u < 0x80000000 else 0x80000000 - u

def cmp(fx, out, tag):
    d = json.load(open(fx)); man = json.load(open(os.path.join(out, 'manifest.json')))
    rows = []; buckets = {'exact': 0, '<=4': 0, '<=64': 0, '>64': 0, 'missing': 0}
    for m in man:
        p = os.path.join(out, m['tag'] + '.' + tag + '.out')
        if not os.path.exists(p): buckets['missing'] += 1; rows.append((m['glsl'], 'MISSING', '')); continue
        got = struct.unpack('<%dI' % (m['n'] * m['nout']), open(p, 'rb').read())
        f = d['functions'][int(m['tag'][1:])]
        worst = 0; exact = 0; nanmis = 0
        for ci, c in enumerate(f['cases']):
            exp = [int(x, 16) for x in c.split('->')[1].split()]
            g = got[ci * m['nout']:(ci + 1) * m['nout']]
            if list(g) == exp: exact += 1; continue
            for e, h in zip(exp, g):
                if m['kind'] != 'f': worst = max(worst, 1 << 40); continue
                en = (e & 0x7f800000) == 0x7f800000 and e & 0x7fffff; hn = (h & 0x7f800000) == 0x7f800000 and h & 0x7fffff
                if en or hn:
                    if not (en and hn): nanmis += 1
                    continue
                fe = struct.unpack('<f', struct.pack('<I', e))[0]; fh = struct.unpack('<f', struct.pack('<I', h))[0]
                if fe != fe or fh != fh or abs(fe) == float('inf') or abs(fh) == float('inf'):
                    if not (fe == fh or (fe != fe and fh != fh)): nanmis += 1
                    continue
                worst = max(worst, abs(fe - fh) / max(abs(fe), 1.0) / 2.0 ** -23)
        b = 'exact' if exact == m['n'] else '<=4' if worst <= 4 and not nanmis else '<=64' if worst <= 64 and not nanmis else '>64'
        if worst >= 1 << 40: b = '>64'
        buckets[b] += 1
        rows.append((m['glsl'], '%d/%d exact' % (exact, m['n']), 'max err %s ulp(max(|v|,1))%s' % (('%.1f' % worst) if worst < 1 << 40 else 'int-mismatch', ', nan-mismatch %d' % nanmis if nanmis else '')))
    print(tag, 'buckets', buckets)
    for r in sorted(rows, key=lambda r: r[1])[:0]: pass
    rows.sort(key=lambda r: -float(re.search(r'max err ([0-9.]+)', r[2]).group(1)) if re.search(r'max err ([0-9.]+)', r[2]) else -1e18)
    for r in rows[:int(os.environ.get('CMP_TOP', '25'))]:
        if not r[1].startswith('256/256'): print('  ', r[0], '|', r[1], '|', r[2])

if __name__ == '__main__':
    {'gen': lambda: gen(sys.argv[2], sys.argv[3]), 'cmp': lambda: cmp(sys.argv[2], sys.argv[3], sys.argv[4])}[sys.argv[1]]()
