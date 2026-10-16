#!/usr/bin/env bash
# Probe job, runs in ubuntu:24.04 with /src = CultLib at the probed revision. Q1 library path, Q2 lavapipe runs.
set -u
P=/tmp/p; W=/tmp/w; mkdir -p $W/fam $W/tools; cd $W
step() { echo; echo "=== $*"; }
step apt
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends ca-certificates curl git cmake g++ make python3 \
  mesa-vulkan-drivers libvulkan-dev vulkan-tools glslang-tools spirv-tools >/dev/null 2>&1; echo "apt exit $?"
dpkg-query -W -f='${Package} ${Version}\n' mesa-vulkan-drivers glslang-tools spirv-tools libvulkan-dev
step dxc
url=$(curl -s https://api.github.com/repos/microsoft/DirectXShaderCompiler/releases/tags/v1.9.2607 | python3 -c "import json,sys;print([a['browser_download_url'] for a in json.load(sys.stdin)['assets'] if 'linux' in a['name'] and a['name'].endswith('.tar.gz')][0])")
echo "$url"; curl -sL --fail -o dxc.tgz "$url" && mkdir -p tools/dxc && tar xzf dxc.tgz -C tools/dxc; DXC=$(find tools/dxc -name dxc -type f | head -1); echo "DXC=$DXC"; $DXC --version 2>&1 | head -2
step spirv-cross
git clone -q --depth 1 https://github.com/KhronosGroup/SPIRV-Cross.git tools/sc && git -C tools/sc log -1 --format='SPIRV-Cross %H %cs' && \
  cmake -S tools/sc -B tools/scb -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_ENABLE_TESTS=OFF >/dev/null && cmake --build tools/scb --target spirv-cross -j4 >/dev/null 2>&1; SC=tools/scb/spirv-cross; ls -la $SC | cut -c1-120
SH=/src/packages/cultmath/shaders
python3 - "$SH" <<'PY'
import re,sys,os,shutil
src=sys.argv[1]; dst='/tmp/w/shx'; shutil.copytree(src,dst,dirs_exist_ok=True); n=0
for fn in os.listdir(dst):
    if fn.endswith('.hlsl'):
        t=open(os.path.join(dst,fn)).read(); t2,k=re.subn(r'(?m)^(?!static|export)(\w+(?:\dx\d)?\s+cultmath_\w+\s*\(([^)]*)\))', lambda m: m.group(1) if re.search('Texture2D|SamplerState', m.group(2)) else 'export ' + m.group(1), t); k=len(re.findall(r'(?m)^export ', t2)); n+=k; open(os.path.join(dst,fn),'w').write(t2)
print('export keywords added', n)
PY
step "L1 dxc lib_6_3 universal1.5, export on non-texture defs"
$DXC -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -I shx shx/CultMath.hlsl -Fo lib.spv 2>&1 | head -3; echo "exit ${PIPESTATUS[0]}"
spirv-dis lib.spv -o lib.spvasm; echo "exports $(grep -c 'LinkageAttributes.*Export' lib.spvasm); capabilities $(grep -c OpCapability lib.spvasm)"
step "L1b -default-linkage external, no export keyword"
$DXC -T lib_6_3 -HV 2021 -spirv -fspv-target-env=universal1.5 -default-linkage external -I $SH $SH/CultMath.hlsl -Fo libd.spv 2>&1 | head -3; echo "exit ${PIPESTATUS[0]}"
[ -s libd.spv ] && { spirv-dis libd.spv -o libd.spvasm; echo "exports $(grep -c 'LinkageAttributes.*Export' libd.spvasm)"; }
step "L2 strip every capability but Shader and Linkage, every extension; SPIRV-Cross ES 310 and 300"
grep -vP '^\s*OpCapability (?!Shader$|Linkage$)|^\s*OpExtension ' lib.spvasm > s.spvasm; spirv-as --target-env spv1.5 s.spvasm -o s.spv; echo "as exit $?"
for v in 310 300; do $SC s.spv --es --version $v --output s.$v.glsl 2>&1 | head -2; echo "es$v exit ${PIPESTATUS[0]}"; done
G=s.310.glsl; echo "G=$G"; [ -s "$G" ] || { echo PROBE-DONE; exit 0; }
step "L3 shape of $G"
echo "lines $(wc -l < $G); defs cultmath_: $(grep -cE '^\w+ cultmath_\w+\(' $G); distinct: $(grep -oE '^\w+ cultmath_\w+\(' $G | sed -E 's/^\w+ //' | sort -u | wc -l); renamed _N: $(grep -oE '^\w+ cultmath_\w+_[0-9]+\(' $G | wc -l); _NNN temporaries: $(grep -oE '\b_[0-9]+\b' $G | wc -l); non-cultmath defs: $(grep -cE '^\w+ (?!cultmath_)\w+\(' -P $G)"
grep -nE '^#|^layout|precision|^struct|^void main' $G | head -12
echo "--- renamed defs sample:"; grep -oE '^\w+ cultmath_\w+_[0-9]+\([^)]*\)' $G | head -6
echo "--- snoise(vec3):"; awk '/^(highp )?float cultmath_snoise\((highp )?vec3/{p=1} p{print; if(++n>=22) exit}' $G
sed -E '/^#version/d; /^layout\(local_size/d' $G | awk 'BEGIN{s=0} /^void main\(\)/{s=1} s==0{print} s==1&&/^\}/{s=0}' > lib.body.glsl
{ printf '#version 300 es\nprecision highp float;\nprecision highp int;\n'; cat lib.body.glsl; printf 'out highp vec4 c;\nvoid main() { c = vec4(cultmath_snoise(gl_FragCoord.xyz)); }\n'; } > es300.frag
glslangValidator es300.frag > es300.txt 2>&1; echo "glslang ES 3.00 fragment with the library: exit $? $(grep -m3 ERROR es300.txt | tr '\n' ' ' | cut -c1-300)"
step "L4 library through glslang on lavapipe"
gcc -O2 -o vkrun $P/vkrun.c -lvulkan 2>/dev/null; python3 $P/fam.py gen /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam >/dev/null
okl=0; for b in fam/*.body.glsl; do t=${b%.body.glsl}; tag=$(basename $t); n=$(python3 -c "import json;m={x['tag']:x for x in json.load(open('fam/manifest.json'))}['$tag'];print(m['n'],m['nout'])")
  { printf '#version 310 es\nprecision highp float;\nprecision highp int;\n'; cat lib.body.glsl $b; } > $t.l.comp
  glslangValidator -V --target-env vulkan1.2 -o $t.l.spv $t.l.comp >$t.l.log 2>&1 && ./vkrun $t.l.spv $t.in $t.lib.out $n && okl=$((okl+1)); done
echo "library path ran $okl of 105"; grep -h -m1 ERROR fam/*.l.log | sed -E 's/[0-9]+:/N:/g' | sort | uniq -c | sort -rn | head -6
CMP_TOP=8 python3 $P/fam.py cmp /src/packages/cultmath/tests/CultMath.Tests/fixtures/glsl-parity.json fam lib
echo "LIBGZ-BEGIN"; gzip -9c lib.body.glsl | base64 -w0; echo; echo "LIBGZ-END"
echo PROBE-DONE
