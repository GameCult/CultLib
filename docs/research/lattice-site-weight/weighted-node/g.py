import numpy as np, probe as P, sys
fold = sys.argv[1]; rng = np.random.default_rng(2); jm = js = 0; nsw = 0
for k in range(3):
    o = rng.uniform(-2500, 2500, 2); a = rng.uniform(0, 3.1416); d = np.array([np.cos(a), np.sin(a)])
    s = np.arange(0, 15 * 260, .5); P.node((o + s[:, None] * d)[None], 100.0, 3, 1.6, 1.0, None, True, fold); a1 = P.node.a1[0].copy()
    idx = np.nonzero(np.diff(a1))[0][:40]
    lo = s[idx]; hi = s[idx + 1]; ref = a1[idx]
    for _ in range(40):
        mid = .5 * (lo + hi); P.node((o + mid[:, None] * d)[None], 100.0, 3, 1.6, 1.0, None, True, fold)
        same = P.node.a1[0] == ref; lo = np.where(same, mid, lo); hi = np.where(same, hi, mid)
    ra = P.node((o + lo[:, None] * d)[None], 100.0, 3, 1.6, 1.0, None, True, fold); ma = P.node.m[0].copy()
    rb = P.node((o + hi[:, None] * d)[None], 100.0, 3, 1.6, 1.0, None, True, fold); mb = P.node.m[0]
    jm = max(jm, np.abs(ma - mb).max()); js = max(js, np.abs(ra[2] - rb[2]).max()); nsw += len(idx)
print(fold, f"F1 swaps {nsw}: max jump m {jm:.2e}, soft {js:.2e}")
