import numpy as np, probe as P
g = np.linspace(-3200, 3200, 260); X, Z = np.meshgrid(g, g); q = np.stack([X, Z], -1)
rng = np.random.default_rng(3); ang = rng.uniform(0, 6.283, X.shape); nrm = np.stack([np.cos(ang), np.sin(ang)], -1)
for boost, S in [(1.6, 1), (1.6, 2), (4, 1), (4, 2), (9, 1)]:
    ref = P.node(q, 100.0, 9, boost, S, nrm)
    row = []
    for W in (2, 3, 4, 5):
        r = P.node(q, 100.0, W, boost, S, nrm)
        miss = np.mean((np.abs(r[0]-ref[0])>1e-12)|(np.abs(r[1]-ref[1])>1e-12))
        row.append(f"W{W}: miss {miss:.5f} dOut {np.abs(r[3]-ref[3]).max():.3f}")
    print(f"boost {boost} S {S} ratio {2*(1+boost):.1f}x{S}:", "; ".join(row))
