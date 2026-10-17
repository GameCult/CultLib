#!/usr/bin/env python3
"""Build-only export wrappers: one exported, uniquely named wrapper per top-level cultmath_ definition, so dxc keeps
every overload as its own function. The shaders are copied unchanged; the unique names exist only in <outdir>.

usage: wrappers.py <shaders> <outdir>
Writes <outdir>/cm_wrappers.hlsl, <outdir>/wrappers.tsv (wrapper, name, file, params, capacity) and
<outdir>/capacity.tsv (the guard default, the sentinel). cm_wrappers.hlsl defines CULTMATH_MAX_SITES as the sentinel,
a number no shader file spells, so the site arrays get an array length id of their own and no other array shares it. An argument is the
parameter's declarator name with any [..] suffix removed; capacity lists the indices of the parameters declared
[CULTMATH_MAX_SITES]. Definitions with a Texture or Sampler parameter get no wrapper. Refuses a file where the
definition-looking lines differ from the parsed ones, and a file that names CULTMATH_MAX_SITES anywhere but its
#ifndef/#define/#endif guard and array declarators."""
import os, re, shutil, sys

FILES = ['CultMath.hlsl', 'CultMath.Interval.hlsl', 'CultMath.Affine.hlsl', 'CultMath.Cycles.hlsl', 'CultMath.Phacelle.hlsl']
SIG = re.compile(r'(?m)^(\w+)[ \t]+(cultmath_\w+)[ \t]*\(([^)]*)\)')
LOOKS = re.compile(r'(?m)^\w+ cultmath_\w+\(')
GUARD = re.compile(r'#ifndef CULTMATH_MAX_SITES\n#define CULTMATH_MAX_SITES (\d+)\n#endif\n')
COMMENT = re.compile(r'//[^\n]*|/\*.*?\*/', re.S)
CAP = '[CULTMATH_MAX_SITES]'


def main(shaders, out):
    shutil.copytree(shaders, out, dirs_exist_ok=True)
    wrappers, rows, spelled, default = [], [], '', None
    for fn in FILES:
        text = open(os.path.join(shaders, fn)).read().replace('\r\n', '\n')
        spelled += COMMENT.sub('', text)
        default = default or GUARD.search(text)
        stray = GUARD.sub('', COMMENT.sub('', text)).replace(CAP, '')
        if 'CULTMATH_MAX_SITES' in stray:
            sys.exit('refuse: %s names CULTMATH_MAX_SITES outside its guard and array declarators' % fn)
        parsed = list(SIG.finditer(text))
        looks = len(LOOKS.findall(text))
        if looks != len(parsed):
            sys.exit('refuse: %s has %d definition-looking lines but %d parsed' % (fn, looks, len(parsed)))
        for m in parsed:
            ret, name, ps = m.groups()
            if re.search(r'Texture|Sampler', ps):
                continue
            params = [p.strip() for p in ps.split(',') if p.strip()]
            args = [re.split(r'\s+', p)[-1].split('[')[0] for p in params]
            cap = ','.join(str(i) for i, p in enumerate(params) if p.endswith(CAP))
            wn = 'cmw%03d_%s' % (len(rows), name)
            call = '%s(%s)' % (name, ', '.join(args))
            wrappers.append('export %s %s(%s) { %s%s; }' % (ret, wn, ', '.join(params), '' if ret == 'void' else 'return ', call))
            rows.append('\t'.join([wn, name, fn, ', '.join(params), cap]))
    if not default:
        sys.exit('refuse: no shader file carries the CULTMATH_MAX_SITES guard')
    sentinel = int(default.group(1)) + 1
    while re.search(r'(?<![\w.])%d(?![\w.])' % sentinel, spelled):
        sentinel += 1
    open(os.path.join(out, 'capacity.tsv'), 'w').write('%s\t%d\n' % (default.group(1), sentinel))
    wrappers.insert(0, '#define CULTMATH_MAX_SITES %d\n#include "CultMath.hlsl"' % sentinel)
    open(os.path.join(out, 'cm_wrappers.hlsl'), 'w').write('\n'.join(wrappers) + '\n')
    open(os.path.join(out, 'wrappers.tsv'), 'w').write('\n'.join(rows) + '\n')
    print('wrappers', len(rows))


if __name__ == '__main__':
    main(*sys.argv[1:3])
