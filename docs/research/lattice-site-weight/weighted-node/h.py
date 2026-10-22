import numpy as np, probe as P
g = np.linspace(-3200, 3200, 200); X, Z = np.meshgrid(g, g); q = np.stack([X, Z], -1)
rng = np.random.default_rng(3); ang = rng.uniform(0, 6.283, X.shape); nrm = np.stack([np.cos(ang), np.sin(ang)], -1)
for S in (1, 2):
    a = P.node(q, 100.0, 6, 1.6, S, nrm, True, "her"); b = P.node(q, 100.0, 6, 1.6, S, nrm, True, "f1kw")
    d = np.abs(a[3] - b[3]); print(f"S{S} look her vs f1kw: mean {d.mean():.4f} p99 {np.percentile(d,99):.3f} max {d.max():.3f}; mean out {a[3].mean():.3f}")
