set -e
apt-get update -qq >/dev/null; apt-get install -y -qq --no-install-recommends python3 spirv-tools >/dev/null 2>&1
cat >/tmp/nc.py <<'PYEOF'
import re,sys
src,dst=sys.argv[1:3]
L=open(src).read().split('\n')
ids=[]
for l in L:
    m=re.match(r'\s*(%\w+) = Op(FMul|FAdd|FSub|VectorTimesScalar|Dot)\b',l)
    if m: ids.append(m.group(1))
i=next(k for k,l in enumerate(L) if re.search(r'= OpType',l))
L[i:i]=['               OpDecorate %s NoContraction'%x for x in ids]
open(dst,'w').write('\n'.join(L))
print(src,len(ids))
PYEOF
P=packages/cultmath/tools
python3 - <<'PY'
p='packages/cultmath/tools/device/run-lavapipe.sh'
s=open(p).read()
k='python3 "$DEV/gen.py" "$W/work" "$CM"\n'
s=s.replace(k,k+'sed -i -E "s/[.](cos|sin)([^A-Za-z0-9_])/._\\1\\2/g" "$W"/work/*.comp\n')
open(p,'w').write(s)
PY
bash $P/glsl/build-glsl.sh /tmp/glslout >/dev/null
GLSL_DIR=/tmp/glslout bash $P/device/run-lavapipe.sh 2>&1 | tail -4 || true
set +e
W=/tmp/device/work; R=/tmp/device
DXC=$(find $R/dxc -name dxc -type f | head -1); export LD_LIBRARY_PATH=$(dirname $DXC)/../lib:$(dirname $DXC)
CM=packages/cultmath
for d in plain ncod gis; do mkdir -p $R/$d/lavapipe-hlsl $R/$d/lavapipe-glsl; done
while IFS=$'\t' read -r tag _ _ n _ wout; do
 spirv-dis --raw-id $W/$tag.g.spv -o /tmp/g.dis; python3 /tmp/nc.py /tmp/g.dis /tmp/g.nc.dis >/dev/null; spirv-as --target-env vulkan1.2 /tmp/g.nc.dis -o /tmp/g.nc.spv
 $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -Od -I $CM/shaders $W/$tag.hlsl -Fo /tmp/hOd.spv >/dev/null 2>&1
 spirv-dis --raw-id /tmp/hOd.spv -o /tmp/hOd.dis; python3 /tmp/nc.py /tmp/hOd.dis /tmp/hOd.nc.dis >/dev/null; spirv-as --target-env vulkan1.2 /tmp/hOd.nc.dis -o /tmp/hOd.nc.spv
 $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -Gis -I $CM/shaders $W/$tag.hlsl -Fo /tmp/hGis.spv >/dev/null 2>&1
 spirv-dis --raw-id /tmp/hGis.spv -o /tmp/hGis.dis; python3 /tmp/nc.py /tmp/hGis.dis /tmp/hGis.nc.dis >/dev/null; spirv-as --target-env vulkan1.2 /tmp/hGis.nc.dis -o /tmp/hGis.nc.spv
 cp $R/results/lavapipe-hlsl/$tag.out $R/plain/lavapipe-hlsl/; cp $R/results/lavapipe-glsl/$tag.out $R/plain/lavapipe-glsl/
 $R/vkrun /tmp/hOd.nc.spv $W/$tag.in $R/ncod/lavapipe-hlsl/$tag.out $n $wout >/dev/null
 $R/vkrun /tmp/g.nc.spv $W/$tag.in $R/ncod/lavapipe-glsl/$tag.out $n $wout >/dev/null
 $R/vkrun /tmp/hGis.nc.spv $W/$tag.in $R/gis/lavapipe-hlsl/$tag.out $n $wout >/dev/null
 cp $R/ncod/lavapipe-glsl/$tag.out $R/gis/lavapipe-glsl/
 for d in plain ncod gis; do cmp -s $R/$d/lavapipe-hlsl/$tag.out $R/$d/lavapipe-glsl/$tag.out || echo "EQ-FAIL $d $tag"; done
done <$W/manifest.txt
export CULTMATH_DEVICE_WORK=$W CULTMATH_DEVICE_NAME=$(sort -u $R/device.txt | head -1 | sed 's/^device=//') CULTMATH_DEVICE_IDENTITY=x CULTMATH_WRITE_DEVICE_BOUNDS=1
B=packages/cultmath/tests/CultMath.Tests/fixtures/device-bounds-lavapipe.txt
for v in plain ncod gis; do
  export CULTMATH_DEVICE_RESULTS=$R/$v
  dotnet test packages/cultmath/tests/CultMath.Tests --no-build --filter "FullyQualifiedName~GlslMirrorTests.DeviceResultsHoldFixture" >/tmp/judge.$v.log 2>&1 || true
  grep -aE "Error Message" -A8 /tmp/judge.$v.log | head -14; ls -la $B; cp $B /tmp/bounds.$v; echo "judge $v: $(tail -3 /tmp/judge.$v.log | tr '\n' ' ')"
done
for v in plain ncod gis; do awk -F'\t' -v v=$v '{k=$1; u[k]+=$5; if($5+0>m[k]+0)m[k]=$5; c[k]++} END{for(k in c) print "BOUNDS",v,k,"families",c[k],"maxulp",m[k],"sumulp",u[k]}' /tmp/bounds.$v; done
for v in plain ncod; do awk -F'\t' '{print $1"/"$2"\t"$5}' /tmp/bounds.$v | sort > /tmp/b.$v; done
join -t$'\t' /tmp/b.plain /tmp/b.ncod | awk -F'\t' '$2!=$3{print "ULPDIFF",$1,"plain",$2,"ncod",$3}' | head -60
