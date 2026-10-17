import numpy as np, probe as P
rng = np.random.default_rng(9); n = 15000
kx = rng.integers(-12, 12, n); z = rng.uniform(-3100, 3100, n); eps = 1e-6
qa = np.stack([kx * 260.0 - eps, z], -1)[None]; qb = np.stack([kx * 260.0 + eps, z], -1)[None]
for W, fold, boost, S in [(6,"f1kw",1.6,2),(6,"her",1.6,2),(6,"f1kw",4,2)]:
    ang = rng.uniform(0, 6.283, n); nrm = np.stack([np.cos(ang), np.sin(ang)], -1)[None]
    a = P.node(qa, 100.0, W, boost, S, nrm, True, fold); b = P.node(qb, 100.0, W, boost, S, nrm, True, fold)
    j = np.abs(a[3] - b[3]); js = np.abs(a[2]-b[2]); jf = np.abs(a[0]-b[0])
    print(f"W{W} {fold} B{boost} S{S}: cell-boundary jump out max {j.max():.4f} (>.01: {np.mean(j>.01):.4f}), soft max {js.max():.4f}, F1 max {jf.max():.2e}")
