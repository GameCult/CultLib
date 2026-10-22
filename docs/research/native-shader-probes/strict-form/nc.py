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
