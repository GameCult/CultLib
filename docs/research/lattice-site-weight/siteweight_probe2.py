"""Follow-up: (1) continuity refinement: does the max time-jump of 'site' scale with dt? (2) cell size near vs far from wells,
border-pixel fraction per variant."""
import numpy as np, json, importlib.util, sys, io, contextlib
spec = importlib.util.spec_from_file_location('p', 'siteweight_probe.py')
src = open('siteweight_probe.py').read().replace('\nmain()\n', '\n')   # import without running main
ns = {}; exec(compile(src, 'p', 'exec'), ns)
node, prox, grid, wells = ns['node'], ns['prox'], ns['grid'], ns['wells']
res = {}
# (1) worst time step at dt=.01 for each variant, then refine dt inside +-.02 of it (continuity: step should shrink ~ with dt)
qs = [(2.3,2.8),(4.7,3.9),(3.5,5.5),(5.9,1.9)]
def series(v, qx, qz, tt):
    f1 = np.empty(len(tt)); out = np.empty(len(tt))
    for k, t in enumerate(tt):
        a,b,w,o = node(np.array([qx]), np.array([qz]), v, t); f1[k]=a[0]; out[k]=o[0]
    return f1, out
for v in ('site','qpt','cellc'):
    tt = np.arange(0, 20.0001, 0.01); best = (0, None, None)
    for qx, qz in qs:
        f1, out = series(v, qx, qz, tt); i = int(np.argmax(np.abs(np.diff(out))))
        if abs(out[i+1]-out[i]) > best[0]: best = (abs(out[i+1]-out[i]), (qx,qz), tt[i])
    _, q, t0 = best
    for dt in (0.01, 0.001, 0.0001, 0.00001):
        t2 = np.arange(t0-0.02, t0+0.03, dt); f1, out = series(v, q[0], q[1], t2)
        res[f"timejump {v} dt={dt}"] = dict(q=q, t_worst=float(t0), max_out_step=float(np.abs(np.diff(out)).max()), max_f1_step=float(np.abs(np.diff(f1)).max()))
        print(v, dt, res[f"timejump {v} dt={dt}"], flush=True)
# (2) cell area near vs far wells, border fraction
X, Z = grid(600)
w = wells(0.0)
near = np.min([np.hypot(X-w[k,0], Z-w[k,1])/ns['WS'][k] for k in range(4)], axis=0) < 1.0
for v in ('base','site','qpt','cellc','warp'):
    f1,f2,wid,out = node(X, Z, v, 0.0)
    ids, cnt = np.unique(wid, return_counts=True); area = dict(zip(ids, cnt))
    pa = np.array([area[i] for i in wid.ravel()]).reshape(wid.shape)
    res[f"shape {v}"] = dict(mean_cell_pixels_near_wells=float(pa[near].mean()), mean_cell_pixels_far=float(pa[~near].mean()),
                            border_frac_near=float((out[near]>.5).mean()), border_frac_far=float((out[~near]>.5).mean()))
    print(v, res[f"shape {v}"])
json.dump(res, open('probe_results2.json','w'), indent=1)
