#!/usr/bin/env bash
# Imagination probe, lavapipe leg: every fixture family through three routes on lavapipe.
# h = HLSL by dxc cs_6_0, g = committed GLSL by glslang, r = restored overloaded library GLSL (lib.body.glsl) by glslang.
set -u; P=/tmp/p; W=/tmp/w; cd $W; SH=/src/packages/cultmath/shaders; FX=/src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json
echo; echo "=== 2b lavapipe: same computation"
mkdir -p fam; gcc -O2 -o vkrun $P/vkrun.c -lvulkan && python3 $P/fam.py gen $FX fam >/dev/null
cat $SH/CultMath.glsl $SH/CultMath.Cycles.glsl $SH/CultMath.Phacelle.glsl > all.glsl
ok=(0 0 0); first=1
for b in fam/*.body.glsl; do t=${b%.body.glsl}; tag=$(basename $t)
  n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$tag'];print(m['n'],m['nout'])")
  $DXC_PATH -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -I $SH $t.hlsl -Fo $t.h.spv >$t.h.log 2>&1 && ./vkrun $t.h.spv $t.in $t.h.out $n && ok[0]=$((ok[0]+1))
  for r in g r; do src=all.glsl; [ $r = r ] && src=lib.body.glsl
    { printf '#version 310 es\nprecision highp float;\nprecision highp int;\n'; cat $src $b; } > $t.$r.comp
    glslangValidator -V --target-env vulkan1.2 -o $t.$r.spv $t.$r.comp >$t.$r.log 2>&1 && ./vkrun $t.$r.spv $t.in $t.$r.out $n && { [ $r = g ] && ok[1]=$((ok[1]+1)) || ok[2]=$((ok[2]+1)); }
  done
done
echo "ran on lavapipe: hlsl ${ok[0]}, committed glsl ${ok[1]}, restored glsl ${ok[2]} (of $(ls fam/*.body.glsl | wc -l))"
grep -h -m1 ERROR fam/*.r.log | sed -E 's/[0-9]+:/N:/g' | sort | uniq -c | sort -rn | head -5
python3 - <<'PY'
import os,filecmp
d='/tmp/w/fam'; tags=sorted(f[:-10] for f in os.listdir(d) if f.endswith('.body.glsl'))
def same(a,b): return [t for t in tags if os.path.exists(f'{d}/{t}.{a}.out') and os.path.exists(f'{d}/{t}.{b}.out') and filecmp.cmp(f'{d}/{t}.{a}.out',f'{d}/{t}.{b}.out',shallow=False)]
rg=same('r','g'); rh=same('r','h'); gh=same('g','h')
print('bit-identical: restored vs committed glsl',len(rg),'| restored vs hlsl',len(rh),'| committed glsl vs hlsl',len(gh),'of',len(tags))
print('restored differs from committed glsl in:',[t for t in tags if t not in rg][:12])
print('restored differs from hlsl but committed glsl matches hlsl:',[t for t in gh if t not in rh][:12])
PY

CMP_TOP=10 python3 $P/fam.py cmp $FX fam r 2>&1 | head -16
