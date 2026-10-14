"""Scratch probe: where does a weighted lattice Voronoi cell get its weight?
2D, lattice cell = 1 unit, wells with a proximity field, numpy only.
Variants of the per-site weight wt_i (multiplies the distance to site i, as the fauna probe's lattice_web):
  base   : wt = hashed .5..1, no field                      (reference, the unweighted-by-field node)
  site   : wt = base*(1+B*prox(site position, t))            (fauna probe, 4354ef2c)
  qpt    : wt = base*(1+B*prox(query position, t))           (common factor for every candidate site)
  cellc  : wt = base*(1+B*prox(lattice cell centre, t))      (node calls the field at the integer cell id)
  site_t0: as site, but field read at t=0 (stale time) while wells have moved
  warp   : base weights, query warped q' = q*(1+B*prox(q)) (domain scaling, a different construct)
"""
import numpy as np, sys, json
rng_seed = 7
def hash01(ix, iz, k):
    # integer hash (lowbias32-style), stand-in for pcg3d; deterministic, vectorised
    x = (ix.astype(np.uint64)*np.uint64(73856093) ^ iz.astype(np.uint64)*np.uint64(19349663) ^ np.uint64(k*83492791+rng_seed)) & np.uint64(0xFFFFFFFF)
    x ^= x >> np.uint64(16); x = (x*np.uint64(0x7feb352d)) & np.uint64(0xFFFFFFFF)
    x ^= x >> np.uint64(15); x = (x*np.uint64(0x846ca68b)) & np.uint64(0xFFFFFFFF)
    x ^= x >> np.uint64(16)
    return (x >> np.uint64(8)).astype(np.float64)/16777216.0

W0 = np.array([[2.0,2.5],[5.5,4.0],[3.5,6.0],[6.5,1.5]]); WS = np.array([0.8,1.2,0.6,1.0])
OM = np.array([0.30,-0.22,0.17,-0.41])      # well orbit rate, rad per unit t (wells orbit a centre)
CEN = np.array([4.0,4.0])
def wells(t):
    r = W0-CEN; c, s = np.cos(OM*t), np.sin(OM*t)
    return CEN + np.stack([r[:,0]*c-r[:,1]*s, r[:,0]*s+r[:,1]*c], 1)
def prox(x, z, t):
    w = wells(t); out = 0
    for k in range(len(w)):
        out = np.maximum(out, np.exp(-(np.hypot(x-w[k,0], z-w[k,1])/WS[k])**2))
    return out

KW=.35
def node(qx, qz, variant, t=0.0, B=1.6, WIN=2, tfield=None):
    """returns f1, f2, winning cell id (ix*10007+iz), soft border value"""
    qx0, qz0 = qx, qz
    if variant == 'warp':
        s = 1+B*prox(qx, qz, t); qx, qz = qx*s, qz*s
    nx, nz = np.floor(qx), np.floor(qz)
    m = np.full(qx.shape, 8.0); f1 = m.copy(); f2 = m.copy(); wid = np.zeros(qx.shape)
    pq = prox(qx0, qz0, t)
    tf = t if tfield is None else tfield
    for j in range(-WIN, WIN+1):
        for i in range(-WIN, WIN+1):
            cx, cz = nx+i, nz+j
            ox, oz = hash01(cx, cz, 11), hash01(cx, cz, 12)
            base = hash01(cx, cz, 13)*.5+.5
            sx, sz = cx+ox, cz+oz
            if variant in ('base','warp'): wt = base
            elif variant == 'site': wt = base*(1+B*prox(sx, sz, tf))
            elif variant == 'site_t0': wt = base*(1+B*prox(sx, sz, 0.0))
            elif variant == 'cellc': wt = base*(1+B*prox(cx+.5, cz+.5, tf))
            elif variant == 'qpt': wt = base*(1+B*pq)
            d = np.hypot(sx-qx, sz-qz)*wt
            upd1 = d < f1; upd2 = (~upd1) & (d < f2)
            wid = np.where(upd1, cx*10007+cz, wid)
            f2 = np.where(upd1, f1, np.where(upd2, d, f2)); f1 = np.where(upd1, d, f1)
            h = (lambda u: u*u*(3-2*u))(np.clip(.5+.5*(m-d)/KW, 0, 1))
            m = m+(d-m)*h-h*(1-h)*KW/(1+3*KW)
    gap0 = .25*KW/(1+3*KW)
    soft = np.clip((f1-m)/gap0, 0, 1)
    out = np.maximum(soft**2, np.exp(-((f2-f1)/.10)**2))
    return f1, f2, wid, out

def grid(n=500, lo=1.0, hi=7.0):
    xs = np.linspace(lo, hi, n); X, Z = np.meshgrid(xs, xs); return X, Z

def main():
    R = {}
    X, Z = grid()
    for t in (0.0, 8.0):
        ref = {v: node(X, Z, v, t) for v in ('base','site','qpt','cellc','warp')}
        ref['site_t0'] = node(X, Z, 'site_t0', t)
        # large-window truth for 'site' (exact argmin unless window too small)
        truth = node(X, Z, 'site', t, WIN=5)[2]
        key = f"t={t}"; R[key] = {}
        for v, (f1, f2, wid, out) in ref.items():
            R[key][v] = dict(
              winner_differs_from_site=float((wid != ref['site'][2]).mean()),
              winner_differs_from_base=float((wid != ref['base'][2]).mean()),
              out_absdiff_vs_site_mean=float(np.abs(out-ref['site'][3]).mean()),
              out_absdiff_vs_site_max=float(np.abs(out-ref['site'][3]).max()),
              n_cells=int(len(np.unique(wid))))
        R[key]['site_window2_vs_window5_winner_differs'] = float((ref['site'][2] != truth).mean())
    # cell count density near wells for site vs base
    # pop measure: dense line, step 1e-3 units; also jumps in time at fixed q
    ts = np.linspace(1.0, 7.0, 6001)   # 1e-3
    lines = {'horizontal z=2.6 (near well 0)': (ts, np.full_like(ts, 2.6)),
             'diagonal': (ts, 1.0+ts*0.9-0.5)}
    P = {}
    for ln, (lx, lz) in lines.items():
        P[ln] = {}
        for t in (0.0, 8.0):
          for v in ('base','site','qpt','cellc','warp'):
            for B, WIN in ((1.6,2),(1.6,1),(6.0,2),(6.0,3)):
              if v in ('base',) and B != 1.6: continue
              f1, f2, wid, out = node(lx, lz, v, t, B, WIN)
              ref = node(lx, lz, v, t, B, 6)
              P[ln][f"{v} t={t} B={B} WIN={WIN}"] = dict(
                maxjump_f1_per_step=float(np.abs(np.diff(f1)).max()),
                maxjump_out_per_step=float(np.abs(np.diff(out)).max()),
                maxjump_f1_with_window6=float(np.abs(np.diff(ref[0])).max()),
                f1_err_vs_win6=float(np.abs(f1-ref[0]).max()))
    # time pop: fixed query points, t stepped at 1e-2; wells move
    tt = np.linspace(0, 20, 2001)
    qs = [(2.3,2.8),(4.7,3.9),(3.5,5.5),(5.9,1.9)]
    T = {}
    for v in ('base','site','qpt','cellc','site_t0'):
        mj = 0; mo = 0; changes = 0
        for (qx, qz) in qs:
            f1s=[];outs=[];wids=[]
            for t in tt:
                a,b,w,o = node(np.array([qx]), np.array([qz]), v, t)
                f1s.append(a[0]); outs.append(o[0]); wids.append(w[0])
            mj=max(mj,np.abs(np.diff(f1s)).max()); mo=max(mo,np.abs(np.diff(outs)).max())
            changes += int((np.diff(wids)!=0).sum())
        T[v] = dict(maxjump_f1_per_dt=float(mj), maxjump_out_per_dt=float(mo), winner_changes_total=changes)
    json.dump(dict(grid=R, line_pop=P, time_pop=T), open('probe_results.json','w'), indent=1)
    print(json.dumps(R, indent=1)); print(json.dumps(T, indent=1))
    for ln in P:
        print(ln)
        for k,v in P[ln].items(): print(' ', k, {a: round(b,5) for a,b in v.items()})
main()
