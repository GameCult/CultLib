"""Weighted lattice cellular probe (Imagination, lattice-weighted-cellular). numpy, float64.
Bodies orbit (period = distance s). Cell weight = base(hash)*(1+BOOST*exp(-(border(centre)/FALL)^2)),
border over the bodies' power scores (site.w = sigma). Her fold: row-major, m from 8; variant m0f1 starts m at F1."""
import numpy as np, sys
LAT, FALL, KW, EW, OMEGA = 260.0, 120.0, .35, .10, .05
GAP0 = .25 * KW / (1 + 3 * KW)
rng = np.random.default_rng(7)
NB = 6
DIST = np.array([0.0, 700, 1200, 1800, 2400, 3000]); PH = rng.uniform(0, 6.283, NB)
SIG = np.array([1500, 300, 450, 350, 900, 600]) / np.sqrt(128.0) * 4  # sigma ratio 5:1, edges well inside
def bodies(t):
    a = PH + np.where(DIST > 0, 2 * np.pi * t / np.maximum(DIST, 1), 0)
    return np.stack([np.cos(a) * DIST, np.sin(a) * DIST], -1)

def h32(x):
    x = x.astype(np.uint32)
    x ^= x >> 16; x *= np.uint32(0x7feb352d); x ^= x >> 15; x *= np.uint32(0x846ca68b); x ^= x >> 16
    return x
def hash01(cx, cz, s):
    k = ((cx.astype(np.int64) * 73856093) ^ (cz.astype(np.int64) * 19349663) ^ (s * 83492791)) & 0xffffffff
    return (h32(h32(k)) >> 8) / 16777216.0

def border(q, B, exact):
    """q: (...,2). Power border distance over bodies B."""
    sc = ((q[..., None, :] - B) ** 2).sum(-1) - SIG ** 2
    o = np.argsort(sc, -1); i1 = o[..., 0]; i2 = o[..., 1]
    s1 = B[i1]
    if not exact:
        s2 = B[i2]; return (np.take_along_axis(sc, i2[..., None], -1)[..., 0] - np.take_along_axis(sc, i1[..., None], -1)[..., 0]) / (2 * np.linalg.norm(s2 - s1, axis=-1))
    best = np.full(q.shape[:-1], 1e30)
    w1 = SIG[i1]
    for k in range(NB):
        d = B[k] - s1; L = np.linalg.norm(d, axis=-1)
        pl = np.maximum((((s1 - q) * d).sum(-1) + .5 * ((d * d).sum(-1) + w1 ** 2 - SIG[k] ** 2)) / np.where(L > 0, L, 1), 0)
        best = np.where((L > 0) & (pl < best), pl, best)
    return best

def node(q, t, W, boost, S=1.0, nrm=None, exact=True, fold="her"):
    B = bodies(t); p = q / LAT; n = np.floor(p); f = p - n
    if nrm is None: nrm = np.zeros_like(q); nrm[..., 0] = 1
    ds = []
    for j in range(-W, W + 1):
        for i in range(-W, W + 1):
            cx, cz = n[..., 0] + i, n[..., 1] + j
            base = .5 + .5 * hash01(cx, cz, 1)
            P = np.exp(-(border(np.stack([(cx + .5) * LAT, (cz + .5) * LAT], -1), B, exact) / FALL) ** 2)
            wt = base * (1 + boost * P)
            ox = .5 + .5 * np.sin(OMEGA * t + 6.2831 * hash01(cx, cz, 2)); oz = .5 + .5 * np.sin(OMEGA * t + 6.2831 * hash01(cx, cz, 3))
            vx, vz = i - f[..., 0] + ox, j - f[..., 1] + oz
            a = vx * nrm[..., 0] + vz * nrm[..., 1]; c = -vx * nrm[..., 1] + vz * nrm[..., 0]
            ds.append(np.hypot(S * a, c) * wt)
    D = np.stack(ds, -1); srt = np.sort(D, -1); f1, f2 = srt[..., 0], srt[..., 1]; a1 = np.argmin(D, -1)
    m = np.full(f1.shape, 8.0) if fold == "her" else (f1 + KW if fold == "f1kw" else f1.copy())
    for k in range(D.shape[-1]):
        d = D[..., k]
        h = np.clip(.5 + .5 * (m - d) / KW, 0, 1); h = h * h * (3 - 2 * h)
        mn = m + (d - m) * h - h * (1 - h) * KW / (1 + 3 * KW)
        m = np.where((fold == "m0f1") & (a1 == k), m, mn)
    soft = np.clip((f1 - m) / GAP0, 0, 1); node.m = m; node.a1 = a1
    return f1, f2, soft, np.maximum(soft ** 2, np.exp(-((f2 - f1) / EW) ** 2))
