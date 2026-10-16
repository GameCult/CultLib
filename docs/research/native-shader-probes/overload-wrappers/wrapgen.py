#!/usr/bin/env python3
"""Probe: build-only export wrappers. One exported, uniquely named wrapper per top-level cultmath_ definition
(texture-taking ones skipped). Source is copied unchanged. usage: wrapgen.py <shaders> <outdir>
Writes <outdir>/cm_wrappers.hlsl and <outdir>/wrappers.tsv (wrapper, name, file, params)."""
import os, re, sys, shutil
sh, out = sys.argv[1:3]; shutil.copytree(sh, out, dirs_exist_ok=True)
SIG = re.compile(r'(?m)^(\w+)\s+(cultmath_\w+)\s*\(([^)]*)\)')
files = ['CultMath.hlsl', 'CultMath.Interval.hlsl', 'CultMath.Affine.hlsl', 'CultMath.Cycles.hlsl', 'CultMath.Phacelle.hlsl']
w = ['#include "CultMath.hlsl"']; rows = []; k = 0
for fn in files:
    for m in SIG.finditer(open(os.path.join(sh, fn)).read()):
        ret, name, ps = m.group(1), m.group(2), m.group(3)
        if re.search(r'Texture|Sampler', ps): continue
        params = [p.strip() for p in ps.split(',') if p.strip()]
        args = [re.split(r'\s+', p)[-1] for p in params]
        wn = 'cmw%03d_%s' % (k, name); k += 1
        call = '%s(%s)' % (name, ', '.join(args))
        w.append('export %s %s(%s) { %s%s; }' % (ret, wn, ', '.join(params), '' if ret == 'void' else 'return ', call))
        rows.append('\t'.join([wn, name, fn, ', '.join(params)]))
open(os.path.join(out, 'cm_wrappers.hlsl'), 'w').write('\n'.join(w) + '\n')
open(os.path.join(out, 'wrappers.tsv'), 'w').write('\n'.join(rows) + '\n')
print('wrappers', k)
