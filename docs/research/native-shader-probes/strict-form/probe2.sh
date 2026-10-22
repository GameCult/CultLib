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
s=s.replace(k,k+'sed -i -E "s/[.](cos|sin)([^A-Za-z0-9_])/._\1\2/g" "$W"/work/*.comp\ngrep -E "^f10[34]\t" "$W/work/manifest.txt" >"$W/work/m2"; mv "$W/work/m2" "$W/work/manifest.txt"\n')
open(p,'w').write(s)
PY
bash $P/glsl/build-glsl.sh /tmp/glslout >/dev/null
GLSL_DIR=/tmp/glslout bash $P/device/run-lavapipe.sh 2>&1 | tail -4 || true
W=/tmp/device/work; R=/tmp/device
mkdir -p $R/nc/lavapipe-hlsl $R/nc/lavapipe-glsl
while IFS=$'\t' read -r tag _ _ n _ wout; do
 for r in h g; do
  spirv-dis --raw-id $W/$tag.$r.spv -o /tmp/$tag.$r.dis
  python3 /tmp/nc.py /tmp/$tag.$r.dis /tmp/$tag.$r.nc.dis
  spirv-as --target-env vulkan1.2 /tmp/$tag.$r.nc.dis -o $W/$tag.$r.nc.spv
  spirv-val --target-env vulkan1.2 $W/$tag.$r.nc.spv
  d=hlsl; [ $r = g ] && d=glsl
  $R/vkrun $W/$tag.$r.nc.spv $W/$tag.in $R/nc/lavapipe-$d/$tag.out $n $wout >/dev/null
 done
 echo "$tag nc: hlsl==glsl $(cmp -s $R/nc/lavapipe-hlsl/$tag.out $R/nc/lavapipe-glsl/$tag.out && echo YES || echo NO); nc-hlsl==plain-hlsl $(cmp -s $R/nc/lavapipe-hlsl/$tag.out $R/results/lavapipe-hlsl/$tag.out && echo YES || echo NO); nc-glsl==plain-glsl $(cmp -s $R/nc/lavapipe-glsl/$tag.out $R/results/lavapipe-glsl/$tag.out && echo YES || echo NO)"
done <$W/manifest.txt
cp /tmp/device/vkrun /tmp/ 2>/dev/null || true
export CULTMATH_DEVICE_NAME=$(sort -u $R/device.txt | head -1 | sed 's/^device=//') CULTMATH_DEVICE_IDENTITY=x CULTMATH_WRITE_DEVICE_BOUNDS=1
for v in results nc; do
  export CULTMATH_DEVICE_RESULTS=$R/$v
  dotnet test packages/cultmath/tests/CultMath.Tests --no-build --filter "FullyQualifiedName~GlslMirrorTests.DeviceResultsHoldFixture" >/tmp/judge.$v.log 2>&1 || true
  echo "== judge $v"; grep -E "^(lavapipe-)" packages/cultmath/tests/CultMath.Tests/fixtures/device-bounds-lavapipe.txt | grep -E "	10[34]	" | cut -f1,2,5,6 || tail -5 /tmp/judge.$v.log
  tail -3 /tmp/judge.$v.log
done
