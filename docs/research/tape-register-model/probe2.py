# Register need of a stencil tree under two schedules; MPR-style greedy slot reuse (free at last use).
import sys
LEAF=12  # ops in a shared-input leaf subgraph chain (krill-like): leaf = chain with 3 live temps
def build(levels, taps=9):
    # node: ('leaf',) or ('mean', [children]) ; each tap child is an independent subgraph (offset input substituted)
    nodes=[]  # (operands)
    def leaf():
        # leaf subgraph: LEAF ops, each uses previous two results (width 2 live) -> 2 live values
        base=len(nodes); nodes.append([])  # input
        prev=[base]
        for i in range(LEAF):
            ops=prev[-2:]
            nodes.append(ops); prev.append(len(nodes)-1)
        return prev[-1]
    def mean(l):
        if l==0: return leaf()
        acc=mean(l-1)
        for _ in range(taps-1):
            c=mean(l-1)
            nodes.append([acc,c]); acc=len(nodes)-1
        return acc
    root=mean(levels)
    return nodes,root
def regs(nodes,order):
    pos={n:i for i,n in enumerate(order)}
    last={}
    for n in order:
        for o in nodes[n]: last[o]=max(last.get(o,-1),pos[n])
    live=0;peak=0;free=0;alloc=0
    for n in order:
        # free operands whose last use is here
        for o in set(nodes[n]):
            if last[o]==pos[n]: free+=1
        if free: free-=1
        else: alloc+=1
        if n not in last: free+=0
    return alloc
def dfs_order(nodes):
    return list(range(len(nodes)))  # construction order is postorder DFS
def bfs_order(nodes,root):
    # level order by depth from root (reverse), a bad schedule
    depth={root:0}
    users={}
    for n,ops in enumerate(nodes):
        for o in ops: users.setdefault(o,[]).append(n)
    d=[0]*len(nodes)
    for n in range(len(nodes)-1,-1,-1):
        d[n]=max([d[u]+1 for u in users.get(n,[])],default=0)
    return sorted(range(len(nodes)),key=lambda n:(-d[n],n))
for L in range(0,4):
    nodes,root=build(L)
    print(L,"ops",len(nodes),"slots_dfs",regs(nodes,dfs_order(nodes)),"slots_levelorder",regs(nodes,bfs_order(nodes,root)))
