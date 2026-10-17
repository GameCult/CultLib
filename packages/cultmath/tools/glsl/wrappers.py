#!/usr/bin/env python3
"""Build-only export wrappers: one exported, uniquely named wrapper per top-level cultmath_ definition, so dxc keeps
every overload as its own function. The shaders are copied unchanged; the unique names exist only in <outdir>.

usage: wrappers.py <shaders> <outdir>
Writes <outdir>/cm_wrappers.hlsl and <outdir>/wrappers.tsv (wrapper, name, file, params). Definitions with a Texture
or Sampler parameter get no wrapper. Refuses a file where the definition-looking lines differ from the parsed ones."""
import os, re, shutil, sys

FILES = ['CultMath.hlsl', 'CultMath.Interval.hlsl', 'CultMath.Affine.hlsl', 'CultMath.Cycles.hlsl', 'CultMath.Phacelle.hlsl']
SIG = re.compile(r'(?m)^(\w+)[ \t]+(cultmath_\w+)[ \t]*\(([^)]*)\)')
LOOKS = re.compile(r'(?m)^\w+ cultmath_\w+\(')


def main(shaders, out):
    shutil.copytree(shaders, out, dirs_exist_ok=True)
    wrappers = ['#include "CultMath.hlsl"']
    rows = []
    for fn in FILES:
        text = open(os.path.join(shaders, fn)).read()
        parsed = list(SIG.finditer(text))
        looks = len(LOOKS.findall(text))
        if looks != len(parsed):
            sys.exit('refuse: %s has %d definition-looking lines but %d parsed' % (fn, looks, len(parsed)))
        for m in parsed:
            ret, name, ps = m.groups()
            if re.search(r'Texture|Sampler', ps):
                continue
            params = [p.strip() for p in ps.split(',') if p.strip()]
            args = [re.split(r'\s+', p)[-1] for p in params]
            wn = 'cmw%03d_%s' % (len(rows), name)
            call = '%s(%s)' % (name, ', '.join(args))
            wrappers.append('export %s %s(%s) { %s%s; }' % (ret, wn, ', '.join(params), '' if ret == 'void' else 'return ', call))
            rows.append('\t'.join([wn, name, fn, ', '.join(params)]))
    open(os.path.join(out, 'cm_wrappers.hlsl'), 'w').write('\n'.join(wrappers) + '\n')
    open(os.path.join(out, 'wrappers.tsv'), 'w').write('\n'.join(rows) + '\n')
    print('wrappers', len(rows))


if __name__ == '__main__':
    main(*sys.argv[1:3])
