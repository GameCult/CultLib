import numpy as np, probe as P
g = np.linspace(-3200, 3200, 200); X, Z = np.meshgrid(g, g); q = np.stack([X, Z], -1)
rng = np.random.default_rng(3); ang = rng.uniform(0, 6.283, X.shape); nrm = np.stack([np.cos(ang), np.sin(ang)], -1)
wmin = .5
for boost, S in [(1.6, 1), (1.6, 2), (4, 2), (9, 1)]:
    f1, f2, soft, out = P.node(q, 100.0, 6, boost, S, nrm)
    C = np.maximum(f2, f1 + P.KW)
    k = np.floor(C / wmin).astype(int) + 1          # first ring whose lower bound (k-1)*wmin >= C is excluded... rings visited 0..k
    k = np.ceil(C / wmin).astype(int)               # rings 0..k visited (ring k+1 has lb k*wmin >= C)
    cells = (2 * k + 1) ** 2
    wmax = 1.0 * (1 + boost); Usound = 1.58 * wmax * S
    Wsound = int(np.ceil((Usound + P.KW) / wmin))
    print(f"B{boost} S{S}: rings needed p50 {np.median(k):.0f} p99 {np.percentile(k,99):.0f} max {k.max()} -> cells p50 {np.median(cells):.0f} max {cells.max()}; worst-case sound W {Wsound} ({(2*Wsound+1)**2} cells)")
