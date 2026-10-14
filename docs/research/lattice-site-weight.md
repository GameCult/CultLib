# Facts for `cultmath-tapes:question:lattice-site-weight-source`

Agent eyes-lattice-site-weight, session self-2026-10-14-eureka, 2026-10-14. Facts only, no recommendation.
Marks: [src] source code, [doc] repo doc, [web] web page, [abs] absence found, [probe] measured by the scripts beside this file.
The eureka-state view of the question id failed (`view` wants a PipelineRef, not the id string); the question text was taken from the brief.

## 1. What the fauna probe did (Aetheria 4354ef2c, read via `git show`)

- [src] `docs/research/2026-10-14-trophic-dag-probe/trophic_probe.py` `lattice_web` (lines 95-119): per lattice cell `(cx,cz)` in a (2*WIN+1)^2 window (WIN=2): jittered site `(c+.5)*LAT` plus a drifting offset; weight `wt = (hash*.5+.5) * (1 + BOOST*prox)`, `prox = max over wells of exp(-(|site - well| / (2.2*.103*r))^2)`, BOOST 1.6; `d = hypot(...) * wt`; F1, F2 and a polynomial smooth-min `m` accumulated in loop order; output `max(soft^2, exp(-((f2-f1)/.10)^2))`. Wells orbit with zone time, so the site weight is a function of time.
- [src] The weight is read at the site's lattice position `(cx+.5)*LAT`, not at the query point and not at the drifted site.
- [doc] `docs/aetheria-fauna-map.md` B34: largest presence step for `lattice` .124 (8 steps over .1); "not discontinuities: a traced member's web value rises .03 per step for 5 steps, a thin weighted border sweeping a fixed candidate"; window 7x7 changes nothing; boost .6 removes the steps (.047); reading the lattice drift at the member's cycle start removes them at full boost (.011, `cyc.log`).
- [doc] Map r4 gap (1): "a 2D weighted lattice cellular with the smooth-min blend (her node) on `pcg3d` cells and zone time"; "The search window follows the weight range ... The CultMath node states the weight range it supports" (map lines 438, 444-446).
- [doc] Prior art doc `docs/research/2026-10-14-trophic-dag-filaments-prior-art.md` 1.1.7 [M]: power diagram evaluated pointwise by the same neighbour search "only if the search window is widened with the weight range"; marked "Neither derivation checked against a source".

## 2. Literature and implementations

- [web] Wikipedia, Weighted Voronoi diagram (https://en.wikipedia.org/wiki/Weighted_Voronoi_diagram): multiplicative = distance divided by positive site weight (circular Dirichlet tessellation, edges circular arcs and line segments); additive = weight subtracted from distance (hyperbolic Dirichlet tessellation, hyperbola arcs and segments); power diagram = weight subtracted from squared distance. A multiplicatively weighted cell may be non-convex, disconnected or hold holes. The page says nothing on continuity.
- [web] Sangil Lee, "Understanding the Variations of Cellular Noise" (https://sangillee.com/_posts/2025-04-18-Cellular-noises/): weighted Voronoi as effective distance `(1/w)*|x - seed|` with `w = 0.2 + 0.8 * random3(cell).z`, i.e. the per-site weight is a component of the hash of the lattice cell id, computed inside the noise function; the weighted example loops 5x5 instead of 3x3 (no reason given).
- [web] Inigo Quilez, Voronoise (https://iquilezles.org/articles/voronoise/): `hash3(cell id)` yields jitter in `.xy` and a per-cell value in `.z`; 5x5 window; notes `min()` is discontinuous and the smooth blend fixes that. No weighted variant.
- [web] Worley noise, Wikipedia and The Book of Shaders (https://thebookofshaders.com/12/): the standard is current cell plus neighbours (3x3), with a wider pass for exact borders; Worley's original used a variable number of points per cell. Nothing found on per-point weight attributes beyond the Lee article; the continuity guidance in the search results was general-knowledge reasoning, not sourced.
- [abs] No source found stating a continuity theorem for weighted Worley; no source found for a node that takes its per-site weight from an external field function. The only per-site weight mechanism found in shader writing is the hash of the cell id.
- [probe] Continuity as observed (section 4): F1 stays continuous as long as the window contains the true winner; failure occurs when it does not.

## 3. CultMath today

- [src] `F:\Projects\CultLib\packages\cultmath\src\CultMath\math.cs:909` `public static CultCellular cellular(float3 p)`: one argument, 3D, unweighted; sites `neighbor + jitter` with jitter from `pcg3d(int3(neighbor))` through `cellular_unit`; fixed 5x5x5 window with a per-offset lower-bound prune (`max(0,|d|-1)`), whose exactness proof (comment above the function) relies on the jitter being in [0,1) and on equal weights; returns `CultCellular { nearest = (grad F1, F1), edge = (grad(F2-F1), F2-F1), id = pcg4d of the winning cell mapped to [0,1) }` (`CultCellular.cs`). Hash is of the integer cell, never the float bits.
- [src] `math.cs:831-847` `smin_grad(float4 a, float4 b, float k)`: quadratic smooth-min with analytic gradient (value-and-gradient form).
- [src] `Voronoi.cs` (`Voronoi.SampleTones`, native `cultmath_apollonian_voronoi_tones`) is a UI-tone colour sampler over a hash-jittered field with a hard-coded 5x5 loop; takes x, y, tone, span; not a weighted-cell function.
- [abs] No function in CultMath takes a weight, a callback, or a site span; no weighted cellular, no per-site hash output exposed besides `id`. (Map B28 states the same.) HLSL twin at `shaders/CultMath.hlsl:440-503`.
- [doc] `packages/cultmath/docs/design.md` ~265-340: `cellular` section, search-radius proof and pruned/unpruned test pin.
- Fact for composition: `CultCellular.id` is already a per-cell hash scalar available to the caller after the call, but only for the winning cell, after F1/F2 are decided.

## 4. Probe: where does the weight come from, 2D, 4 orbiting wells

Scripts: `siteweight_probe.py`, `siteweight_probe2.py`; raw numbers `probe_results.json`, `probe_results2.json`. numpy, scratch; hash is a lowbias32-style integer hash (stand-in for pcg3d). Units: lattice cell = 1; domain [1,7]^2, 500x500 grid (600 for shape rows); wells with radii .6-1.2 cells orbiting a centre; weight `wt = base(.5..1) * (1 + B*prox)`, B = 1.6, WIN = 2, the fauna probe's smooth-min and border output.

Correction (2026-10-14, from the Aetheria Self's body-Voronoi probe, Aetheria 05a44541): the fauna probe's `trophic_probe.py` weights at the cell centre `(cx+.5)*LAT` (section 1 above), which is the `cellc` variant below, not `site`. An earlier label here called `site` the fauna probe's method.

Variants: `base` (no field); `site` (field at the jittered site position); `qpt` (field at the query point, same factor on every candidate); `cellc` (node calls the field at the integer cell centre, `cell + .5`, no jitter); `site_t0` (as `site` but field read at t=0 while wells have moved); `warp` (extra: query scaled `q*(1+B*prox(q))`).

### 4a. Winner identity, fraction of grid pixels whose winning cell differs (t=0 / t=8)

| variant | vs `site` | vs `base` |
|---|---|---|
| base | .147 / .156 | 0 / 0 |
| qpt | .147 / .156 | 0 / 0 (identical argmin to base) |
| cellc | .127 / .114 | .185 / .147 |
| site_t0 | 0 / .226 | .147 / .147 |
| warp | .675 / .646 | .668 / .639 |
| site | 0 / 0 | .147 / .156 |

Mean absolute difference of the border output vs `site`: base .169/.186, qpt .168/.180, cellc .164/.156, site_t0 0/.234, warp .310/.280. Number of distinct cells on the grid: 53 for base, site and qpt; 54 cellc; 143/128 warp.

[probe] `qpt` leaves every winner unchanged because the factor is common to all candidates; it changes F1, F2 and the smooth-min scale, which moves the border line (border pixel fraction near wells .164 vs .300 for base, far .250 vs .281).

### 4b. Cell size and border near wells (pixels per cell, mean; "near" = within 1 well radius), t=0

| | near | far | border fraction near / far |
|---|---|---|---|
| base | 10951 | 10515 | .300 / .281 |
| site | 9672 | 11439 | .270 / .253 |
| qpt | 10951 | 10515 | .164 / .250 |
| cellc | 11562 | 12274 | .274 / .262 |
| warp | 3447 | 8566 | .291 / .302 |

### 4c. Pop measure, dense spatial line (6001 points over 6 cells, step 1e-3 cell), max |step| of F1 and of output, window sweep vs window 6 reference

- With the true winner inside the window, every variant's max F1 step is small (site .0010-.0033, qpt .0019-.0079, cellc .0009-.0041, warp .0054-.022, base .0007-.0013 per 1e-3).
- WIN=1 (3x3): `site` F1 error vs window 6 up to .174 (diagonal line, B=1.6; max output step .95); `cellc` .155-.194; `base` .0065; `qpt` .0092-.0113; `warp` none seen. At WIN=2 and B=1.6 all variants' F1 error is 0.
- Weight range: `site` at B=6 (weight ratio about 12:1), t=8, horizontal line: WIN=2 error .0169 (output step .371); WIN=3 error 0.
- Note: even `base` (weights .5-1, ratio 2) shows F1 error .0065 at WIN=1.

### 4d. Pop measure, time at fixed query points (wells move), step refinement (worst point from a dt=.01 scan, refined)

| variant | dt .01 max output step | dt 1e-3 | dt 1e-4 | dt 1e-5 | max F1 step at dt 1e-5 |
|---|---|---|---|---|---|
| site | .1083 | .01125 | .00113 | .000113 | 2.9e-6 |
| qpt | .00461 | .000461 | 4.6e-5 | 4.6e-6 | 4.1e-6 |
| cellc | .1060 | .0204 | .00206 | .000207 | 5.2e-6 |

[probe] All three steps scale linearly with dt: no discontinuity in time for any variant when the window holds the winner. Winner-id changes over t in [0,20] at 4 query points (dt .01): base 0, qpt 0, site 17, cellc 15, site_t0 0.

### 4e. Caveats of the probe
Static smooth proximity field, 4 wells, 2D, one seed of the hash; 3 boost values in the pop table only B=1.6 and 6.0; no gradient-bearing outputs measured; `cellc` field is sampled at the cell centre, so it differs from `site` by the jitter distance; no visual images rendered, only the numeric measures above. The `warp` variant is an addition outside the three asked and is reported for completeness.
