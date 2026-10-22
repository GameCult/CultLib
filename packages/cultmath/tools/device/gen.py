#!/usr/bin/env python3
"""Spell one compute shader per work item, in HLSL and in GLSL ES 3.10. It compares and judges nothing.

usage: gen.py <workdir> <cultmath-root>

<workdir>/manifest.txt (written by GlslMirrorTests.WriteDeviceWork) has one tab-separated line per item:
tag, fixture index, glsl signature, n, words in, words out. For each item this writes <tag>.hlsl (cs_6_0,
includes CultMath.hlsl) and <tag>.comp (#version 310 es, the committed GLSL files, then the body). An item
reads n argument tuples of 32-bit words from binding 0 and writes n result tuples to binding 1. Struct
arguments and results are built and flattened in field order, from the struct definitions in the HLSL."""
import glob
import os
import re
import sys

HLSL_VEC = re.compile(r'(float|int|uint)([234])$')
GLSL_VEC = re.compile(r'(i|u|)vec([234])$')
KIND = {'float': 'f', 'int': 'i', 'uint': 'u'}
ARRAY = re.compile(r'(\w+)\[(\d+)\]$')  # an array leaf, vec4[128]: N elements of one leaf type, N x its words


def structs(root):
    """struct name -> [(field, type, hlsl)] from every shader's struct definitions, GLSL type names."""
    found = {}
    for path in sorted(glob.glob(os.path.join(root, 'shaders', '*.hlsl'))):
        for name, body in re.findall(r'(?m)^struct (\w+)\s*\{(.*?)\};', open(path).read(), re.S):
            found[name] = [(f, t) for t, f in re.findall(r'(\w+)\s+(\w+);', body)]
    return found


def leaves(t, defs, path=''):
    """Flatten a type (GLSL spelling for scalars and vectors, HLSL for struct fields) to scalar leaves:
    (accessor path, kind) in field order. A vector's components are path[i]; a mat3 is read row by row."""
    if t in KIND:
        return [(path, KIND[t], None)]
    m = HLSL_VEC.match(t) or GLSL_VEC.match(t)
    if m:
        k = {'float': 'f', 'int': 'i', 'uint': 'u', '': 'f', 'i': 'i', 'u': 'u'}[m.group(1)]
        return [(path, k, i) for i in range(int(m.group(2)))]
    if t in ('mat3', 'float3x3'):
        return [(path, 'f', (i, j)) for i in range(3) for j in range(3)]
    if t in defs:
        out = []
        for field, ft in defs[t]:
            out += leaves(ft, defs, path + '.' + field)
        return out
    raise SystemExit('unsupported type ' + t)


def access(leaf, glsl):
    path, _, index = leaf
    if index is None:
        return path
    if isinstance(index, tuple):  # row i, column j; GLSL matrices index the column first
        return '%s[%d][%d]' % ((path,) + ((index[1], index[0]) if glsl else index))
    return '%s[%d]' % (path, index)


def read(kind, glsl, word):
    if glsl:
        return {'f': 'uintBitsToFloat(%s)', 'i': 'int(%s)', 'u': '%s'}[kind] % word
    return {'f': 'asfloat(%s)', 'i': 'asint(%s)', 'u': '%s'}[kind] % word


def write(kind, glsl, value):
    if glsl:
        return {'f': 'floatBitsToUint(%s)', 'i': 'uint(%s)', 'u': '%s'}[kind] % value
    return {'f': 'asuint(%s)', 'i': 'asuint(%s)', 'u': '%s'}[kind] % value


def hlsl_type(t):
    m = GLSL_VEC.match(t)
    if m:
        return {'': 'float', 'i': 'int', 'u': 'uint'}[m.group(1)] + m.group(2)
    return 'float3x3' if t == 'mat3' else t


def body(sig, n, win, wout, defs, glsl):
    m = re.fullmatch(r'(\w+)\s+(\w+)\((.*)\)', sig)
    ret, name, params = m.group(1), m.group(2), [p.strip() for p in m.group(3).split(',') if p.strip()]
    lines, offset, args = [], 0, []
    for j, p in enumerate(params):
        array = ARRAY.match(p)
        if array:
            elem, size = array.group(1), int(array.group(2))
            words = leaves(elem, defs)
            lines.append('    %s a%d[%d];' % (elem if glsl else hlsl_type(elem), j, size))
            lines.append('    for (uint e = 0u; e < %du; e++) {' % size)
            for k, leaf in enumerate(words):
                word = ('In.a[b + %du + e * %du]' if glsl else 'In[b + %du + e * %du]') % (offset + k, len(words))
                lines.append('        a%d[e]%s = %s;' % (j, access(leaf, glsl), read(leaf[1], glsl, word)))
            lines.append('    }')
            offset += size * len(words)
            args.append('a%d' % j)
            continue
        ptype = p if glsl else hlsl_type(p)
        lines.append('    %s a%d;' % (ptype, j))
        for leaf in leaves(p, defs):
            word = ('In.a[b + %du]' if glsl else 'In[b + %d]') % offset
            lines.append('    a%d%s = %s;' % (j, access(leaf, glsl), read(leaf[1], glsl, word)))
            offset += 1
        args.append('a%d' % j)
    rtype = ret if glsl else hlsl_type(ret)
    lines.append('    %s r = %s(%s);' % (rtype, name, ', '.join(args)))
    produced = leaves(ret, defs)
    for k, leaf in enumerate(produced):
        target = ('Out.a[o + %du]' if glsl else 'Out[o + %d]') % k
        lines.append('    %s = %s;' % (target, write(leaf[1], glsl, 'r' + access(leaf, glsl))))
    if offset != win or len(produced) != wout:
        raise SystemExit('%s: %d words in and %d out, the manifest says %d and %d' % (sig, offset, len(produced), win, wout))
    head = 'uint i = id.x; if (i >= %du) return; uint b = i * %du; uint o = i * %du;' % (n, win, wout)
    return head, '\n'.join(lines)


def main(work, root):
    defs = structs(root)
    shaders = os.path.join(root, 'shaders')
    built = os.environ.get('GLSL_DIR')
    parts = [os.path.join(built, f) for f in ('core.glsl', 'cycles.glsl', 'phacelle.glsl')] if built else         [os.path.join(shaders, f) for f in ('CultMath.glsl', 'CultMath.Cycles.glsl', 'CultMath.Phacelle.glsl')]
    library = ''.join(open(p).read() for p in parts)
    count = 0
    for line in open(os.path.join(work, 'manifest.txt')):
        tag, _, sig, n, win, wout = line.rstrip('\n').split('\t')
        n, win, wout = int(n), int(win), int(wout)
        head, code = body(sig, n, win, wout, defs, False)
        open(os.path.join(work, tag + '.hlsl'), 'w').write(
            '#include "CultMath.hlsl"\n[[vk::binding(0)]] StructuredBuffer<uint> In;\n[[vk::binding(1)]] RWStructuredBuffer<uint> Out;\n'
            '[numthreads(64, 1, 1)] void main(uint3 id : SV_DispatchThreadID) {\n    %s\n%s\n}\n' % (head, code))
        head, code = body(sig, n, win, wout, defs, True)
        open(os.path.join(work, tag + '.comp'), 'w').write(
            '#version 310 es\nprecision highp float;\nprecision highp int;\n' + library +
            'layout(local_size_x = 64) in;\nlayout(std430, binding = 0) readonly buffer IB { uint a[]; } In;\n'
            'layout(std430, binding = 1) buffer OB { uint a[]; } Out;\n'
            'void main() {\n    uint i = gl_GlobalInvocationID.x; %s\n%s\n}\n' % (head.replace('uint i = id.x; ', ''), code))
        count += 1
    print('generated', count, 'work items')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
