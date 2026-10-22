set -e
apt-get update -qq >/dev/null; apt-get install -y -qq --no-install-recommends python3 spirv-tools >/dev/null 2>&1
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
W=/tmp/device/work
for t in f103 f104; do for r in h g; do
  spirv-dis --raw-id $W/$t.$r.spv -o /tmp/$t.$r.dis
  echo "== $t $r: NoContraction $(grep -c NoContraction /tmp/$t.$r.dis) Fma $(grep -c ' Fma ' /tmp/$t.$r.dis) FMul $(grep -c OpFMul /tmp/$t.$r.dis) FAdd $(grep -c OpFAdd /tmp/$t.$r.dis) FSub $(grep -c OpFSub /tmp/$t.$r.dis) lines $(wc -l </tmp/$t.$r.dis)"
done; done
mkdir -p /tmp/dump; cp /tmp/f10?.?.dis /tmp/dump/; cp $W/f103.hlsl $W/f103.comp /tmp/dump/; cp /tmp/glslout/*.glsl /tmp/dump/
echo DUMP-BEGIN; tar czf - -C /tmp dump | base64 -w0; echo; echo DUMP-END
