import numpy as np, probe as P
g = (np.arange(-12, 12) + .5) * 260; X, Z = np.meshgrid(g, g); c = np.stack([X, Z], -1).reshape(-1, 2)
for ex in (False, True):
    prev = None; worst = 0; big = 0
    for t in np.arange(0, 300, 1/60):
        Pw = np.exp(-(P.border(c, P.bodies(t), ex) / P.FALL) ** 2)
        if prev is not None:
            s = np.abs(Pw - prev); worst = max(worst, s.max()); big += (s > .01).sum()
        prev = Pw
    print("exact" if ex else "first-order", f"cell-centre profile: max step per 1/60 s {worst:.4f}, steps > .01: {big} (576 centres x 18000 ticks)")
