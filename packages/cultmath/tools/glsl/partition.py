#!/usr/bin/env python3
"""Partition the SPIRV-Cross GLSL of the whole library into the three files a consumer includes.

usage: partition.py <shaders> <spirv-cross.glsl> <outdir>
Each top-level struct and function chunk goes to the file whose HLSL defines it (CultMath, Interval and Affine to core;
Cycles; Phacelle). The library-header prologue and any main are dropped. Writes core.glsl, cycles.glsl and
phacelle.glsl bodies, with the 13 `static const float` constants of the HLSL as `const float NAME = <literal>;` at the
head of the file that defines them. Licence lines, include guards and the #version contract belong to the caller.
Refuses: a static const line that is not `static const float CULTMATH_X = <literal>;`, an unknown-origin chunk, a
duplicate chunk, an unexpected prologue line, and a call from a non-core chunk into a different non-core origin."""
import collections, os, re, sys

OWNER = {'CultMath.hlsl': 'core', 'CultMath.Interval.hlsl': 'core', 'CultMath.Affine.hlsl': 'core',
         'CultMath.Cycles.hlsl': 'cycles', 'CultMath.Phacelle.hlsl': 'phacelle'}
CONST = re.compile(r'^static const float (CULTMATH_\w+) = (-?\d+(?:\.\d*)?(?:[eE][+-]?\d+)?);$')
PROLOGUE = re.compile(r'^(#version |#extension |precision |#ifdef SPIRV_CROSS|#define SPIRV_CROSS|#endif)')
STRUCT, FUNC = re.compile(r'^struct (\w+)$'), re.compile(r'^\w+ (\w+)\(.*\)$')


def origins(shaders):
    origin, consts = {}, collections.defaultdict(list)
    for fn, o in OWNER.items():
        text = open(os.path.join(shaders, fn)).read()
        for m in re.finditer(r'(?m)^\w+\s+(cultmath_\w+)\s*\(', text):
            origin.setdefault(m.group(1), o)
        for m in re.finditer(r'(?m)^struct\s+(\w+)', text):
            origin.setdefault(m.group(1), o)
        for line in text.splitlines():
            if line.startswith('static const'):
                m = CONST.match(line)
                if not m:
                    sys.exit('refuse: %s has a static const line that is not `static const float CULTMATH_X = <literal>;`' % fn)
                consts[o].append('const float %s = %s;' % m.groups())
    return origin, consts


def chunks(lines):
    out, prologue, i = [], [], 0
    while i < len(lines):
        m = STRUCT.match(lines[i]) or FUNC.match(lines[i])
        if not m:
            if lines[i].strip():
                prologue.append(lines[i])
            i += 1
            continue
        j = i
        while lines[j] not in ('};', '}'):
            j += 1
        if m.group(1) != 'main':
            out.append((m.group(1), '\n'.join(lines[i:j + 1])))
        i = j + 1
    return out, prologue


def main(shaders, glsl, outdir):
    origin, consts = origins(shaders)
    parts, prologue = chunks(open(glsl).read().split('\n'))
    odd = [l for l in prologue if not PROLOGUE.match(l)]
    if odd:
        sys.exit('refuse: %d unexpected prologue lines, first at %r' % (len(odd), odd[0][:60]))
    seen = collections.Counter(b.split('
')[0] for _, b in parts)     # overloads share a name, never a signature
    dup = sorted(n for n, c in seen.items() if c > 1)
    if dup:
        sys.exit('refuse: duplicate chunks %s' % dup[:6])
    unknown = sorted(n for n, _ in parts if n not in origin)
    if unknown:
        sys.exit('refuse: %d unknown-origin chunks %s' % (len(unknown), unknown[:8]))
    files = collections.defaultdict(list)
    for name, body in parts:
        o = origin[name]
        for callee in set(re.findall(r'\b(cultmath_\w+|[A-Z]\w+)\b', body)) - {name}:
            co = origin.get(callee)
            if o != 'core' and co and co != 'core' and co != o:
                sys.exit('refuse: %s (%s) calls %s (%s)' % (name, o, callee, co))
        files[o].append(body)
    os.makedirs(outdir, exist_ok=True)
    for o in OWNER.values():
        text = '\n'.join(consts[o] + files[o]) + '\n'
        open(os.path.join(outdir, o + '.glsl'), 'w').write(text)
    print('partition:', {o: len(files[o]) for o in sorted(files)}, 'constants', sum(len(v) for v in consts.values()))


if __name__ == '__main__':
    main(*sys.argv[1:4])
