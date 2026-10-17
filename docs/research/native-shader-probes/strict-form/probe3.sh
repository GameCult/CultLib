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
DXC=$(find $R/dxc -name dxc -type f | head -1); export LD_LIBRARY_PATH=$(dirname $DXC)/../lib:$(dirname $DXC)
CM=packages/cultmath
cat >/tmp/cnt.py <<'PYEOF'
import sys,struct
a=open(sys.argv[1],'rb').read();b=open(sys.argv[2],'rb').read()
n=len(a)//4;x=struct.unpack('<%dI'%n,a);y=struct.unpack('<%dI'%n,b)
d=[i for i in range(n) if x[i]!=y[i]]
print(len(d),'of',n,'cols',sorted(set(i%6 for i in d)))
PYEOF
run() { # name spv tag n wout
  $R/vkrun $2 $W/$3.in /tmp/o.$1.$3 $4 $5 >/dev/null; }
OPT="--target-env=spv1.5 --eliminate-local-single-store --eliminate-dead-code-aggressive"
while IFS=$'	' read -r tag _ _ n _ wout; do
 for r in h g; do spirv-dis --raw-id $W/$tag.$r.spv -o /tmp/$tag.$r.dis; python3 /tmp/nc.py /tmp/$tag.$r.dis /tmp/$tag.$r.nc.dis >/dev/null; spirv-as --target-env vulkan1.2 /tmp/$tag.$r.nc.dis -o $W/$tag.$r.nc.spv; done
 # h variants
 $DXC -T cs_6_0 -E main -HV 2021 -spirv -fspv-target-env=vulkan1.2 -Od -I $CM/shaders $W/$tag.hlsl -Fo $W/$tag.hOd.spv >/dev/null 2>&1
 spirv-opt $OPT $W/$tag.hOd.spv -o $W/$tag.hOdO.spv
 for v in hOd hOdO; do spirv-dis --raw-id $W/$tag.$v.spv -o /tmp/$tag.$v.dis; python3 /tmp/nc.py /tmp/$tag.$v.dis /tmp/$tag.$v.nc.dis >/dev/null; spirv-as --target-env vulkan1.2 /tmp/$tag.$v.nc.dis -o $W/$tag.$v.nc.spv; done
 for v in h g hOd hOdO; do run $v $W/$tag.$v.spv $tag $n $wout; run $v.nc $W/$tag.$v.nc.spv $tag $n $wout; done
 for v in h hOd hOdO; do
  echo "$tag $v vs g: $(python3 /tmp/cnt.py /tmp/o.$v.$tag /tmp/o.g.$tag)"
  echo "$tag $v.nc vs g.nc: $(python3 /tmp/cnt.py /tmp/o.$v.nc.$tag /tmp/o.g.nc.$tag)"
 done
 echo "$tag h.nc vs h: $(python3 /tmp/cnt.py /tmp/o.h.nc.$tag /tmp/o.h.$tag)"
 echo "$tag g.nc vs g: $(python3 /tmp/cnt.py /tmp/o.g.nc.$tag /tmp/o.g.$tag)"
 echo "$tag hOd.nc vs hOd: $(python3 /tmp/cnt.py /tmp/o.hOd.nc.$tag /tmp/o.hOd.$tag)"
done <$W/manifest.txt
