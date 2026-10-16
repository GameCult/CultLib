import re,sys,os,collections
sh,g,out=sys.argv[1:4]; os.makedirs(out,exist_ok=True)
origin={}; owner={'CultMath.hlsl':'core','CultMath.Interval.hlsl':'core','CultMath.Affine.hlsl':'core','CultMath.Cycles.hlsl':'cycles','CultMath.Phacelle.hlsl':'phacelle'}
for fn,o in owner.items():
    t=open(os.path.join(sh,fn)).read()
    for m in re.finditer(r'(?m)^(?:export\s+)?\w+\s+(cultmath_\w+)\s*\(',t): origin.setdefault(m.group(1),o)
    for m in re.finditer(r'(?m)^struct\s+(\w+)',t): origin.setdefault(m.group(1),o)
text=open(g).read()
# top-level chunks: struct blocks and function definitions; everything else is prologue
lines=text.split('\n'); chunks=[]; i=0; pro=[]
while i<len(lines):
    l=lines[i]
    m=re.match(r'^struct (\w+)$',l) or re.match(r'^\w+ (\w+)\(.*\)$',l)
    if m and m.group(1)!='main':
        j=i
        while lines[j]!='};' and lines[j]!='}': j+=1
        chunks.append((m.group(1),'\n'.join(lines[i:j+1]))); i=j+1
    elif m:
        j=i
        while lines[j]!='}': j+=1
        i=j+1
    else:
        if l.strip(): pro.append(l)
        i+=1
print('prologue lines',len(pro),':',' | '.join(pro[:6])[:300])
cnt=collections.Counter(); unknown=[]; bad=collections.Counter(); files=collections.defaultdict(list)
rank={'core':0,'cycles':1,'phacelle':1}
for name,body in chunks:
    o=origin.get(name)
    if o is None: unknown.append(name); o='core'
    cnt[o]+=1; files[o].append(body)
    for callee in set(re.findall(r'\b(cultmath_\w+|[A-Z]\w+)\b',body))-{name}:
        co=origin.get(callee)
        if co and co!=o and not (rank[co]==0): bad[(o,co,name,callee)]+=1
print('chunks',len(chunks),dict(cnt),'unknown-origin',len(unknown),unknown[:8])
print('cross-licence calls (non-core callee):',len(bad),list(bad)[:6])
seen=collections.Counter(n for n,_ in chunks); print('duplicate chunk names',[n for n,c in seen.items() if c>1][:6])
for o,b in files.items(): open(os.path.join(out,o+'.glsl'),'w').write('\n'.join(b)+'\n')
print('split sizes', {o:len('\n'.join(b).split('\n')) for o,b in files.items()})
