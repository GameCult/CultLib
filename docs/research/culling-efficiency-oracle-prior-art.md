# Culling Efficiency Against an Oracle: Prior Art Survey

This is the facts file for Eureka question `cultmath-tapes:question:wells-ceiling-oracle`,
which interprets ruling `cultmath-tapes:ruling:operator-wells-contract-vs-ceiling`. That
ruling holds scenario (c) to "at least 90% of the probe-free ceiling (the cost of the
dense cells alone)". The question asks what "ceiling" means:

- (a) the pre-pass's own mask, under which a pre-pass that skips nothing scores 0.98;
- (b) an oracle mask, the cells where at least one ray samples nonzero density.

Under (b), Imagination measured 0.38 at warp 0 and 0.19 at warp D.

This survey covers how published work measures culling or skipping efficiency against
ground truth, and what efficiency figures conservative bounds reach. Eyes pass,
2026-10-03. It gives facts and evidence pointers only, with no recommendation.

Verification marks:

- **[fetched]** The claim was read in the cited source's full text during this pass.
  PDFs were converted with `pdftotext -layout`.
- **[search]** The claim came from a search-result summary, not a full read.
- **[not found]** The source was read, and the named kind of measurement is absent from it.

Reference kinds used below:

- **oracle**: an exact or ground-truth answer computed independently of the method,
  such as the exact visible set or the set of truly non-empty cells.
- **self**: the method's own result, or a baseline that does no culling.
- **peer**: another approximate method.

## 1. Visibility culling: conservative versus exact visible set

### 1.1 Survey vocabulary

- **[fetched]** Cohen-Or, Chrysanthou, Silva, Durand, *A Survey of Visibility for
  Walkthrough Applications* (IEEE TVCG 2003). Section 2 lists "Tightness of
  approximation" as a review criterion. It notes that few papers report the ratio
  between the size of the potentially visible set and the size of the visible set,
  and asks later work to report it. Most surveyed techniques are conservative, which
  means they overestimate the visible set.
  https://www.cs.tau.ac.il/~dcor/online_papers/papers/visibility-survey-ieee.pdf
- **[fetched]** Bittner, *Hierarchical Techniques for Visibility Computations* (PhD
  thesis, CTU Prague, 2002), chapter 2, equations 2.1 to 2.4. It defines a quality
  measure against the exact PVS `S_E`:
  - relative overestimation `Q_o = |S_A \ S_E| / |S_E|`;
  - relative underestimation `Q_u = |S_E \ S_A| / |S_E|`;
  - the expected quality, which is the expectation of these over inputs.

  The same text appears in Bittner and Wonka, *Visibility in Computer Graphics*
  (section 4.3).
  https://dcgi.fel.cvut.cz/home/bittner/publications/diss_noimages.pdf
  http://www.casa.ucl.ac.uk/mike-michigan-april1/mike's%20stuff/attach/bittner_wonka.pdf
- **[fetched]** Nirenstein, Blake, Gain, *Exact From-Region Visibility Culling* (EGWR
  2002), introduction and Table 1. The paper classifies algorithms as exact,
  conservative, aggressive or approximate. Conservative methods "incur a false
  visibility error". The paper states:
  - exact visibility "provides a benchmark for comparing other approaches";
  - the percentage of polygons culled is "a very crude and imperfect metric", because
    it ignores the type, distribution and magnitude of visibility error.

  Reported results (Table 2) are culling fractions of the whole scene: 99.45% for the
  town and 99.12% for the forest. No conservative method is measured against the
  exact set in this paper **[not found]**. A search summary attributed a 95.5% figure
  for extended projection to this paper, but that figure is not in the full text.
  https://people.cs.uct.ac.za/~jgain/wp-content/papercite-data/pdf/nirenstein2002.pdf

### 1.2 Measured conservative-versus-exact ratios

- **[fetched]** Bittner, Wonka, Wimmer, *Fast Exact From-Region Visibility in Urban
  Scenes* (EGSR 2005), section 6. The exact 2.5D algorithm is used as the oracle, and
  the paper simulates conservative methods against it:
  - **Section 6.2, one penumbra wedge per occluder.** The conservative PVS was more
    than 47% larger than the exact PVS for 2 m view cells and more than 76% larger
    for 4 m view cells. As exact/conservative ratios these are ≤ 0.68 and ≤ 0.57.
  - **Section 6.3, object-space discretization** (Wonka et al.'s method, a 1400×1400
    grid, Vienna). The PVS overestimate averaged 17%, a ratio of about 0.85.
  - **Section 6.3, Table 4, line-space discretization** (Leyvand et al.). PVS sizes by
    grid resolution, with the exact PVS last:

    | Scene   | 512    | 1024   | 2048   | 16384 | exact |
    |---------|--------|--------|--------|-------|-------|
    | Vienna  | 633    | 535    | 489    | 455   | 235   |
    | Atlanta | 50,744 | 25,751 | 10,082 | 2,656 | 1,152 |

    The ratios exact/conservative below are computed from that table, not quoted from
    the paper:
    - Vienna: 0.37, 0.44, 0.48, 0.52;
    - Atlanta: 0.023, 0.045, 0.11, 0.43.

    The paper says the overestimation comes from conservative shrinking and from gaps
    "between formerly connected occluders".

  https://peterwonka.net/Publications/pdfs/2005.EGSR.Bittner.FastExactFromRegionVisibility.pdf

### 1.3 Run-time occlusion culling against an ideal renderer

- **[fetched]** Mattausch, Bittner, Wimmer, *CHC++: Coherent Hierarchical Culling
  Revisited* (Eurographics 2008), section 7 (Figure 7) and the conclusion. The
  reference algorithm "OPT" renders only the visible nodes of the hierarchy and
  issues no queries. It was implemented by recording visibility with an exact
  stop-and-wait pass, so it is an oracle at node granularity that is independent of
  the method under test. CHC++ is reported to be "for most cases within a few percent
  of the 'ideal' method".

  Caveat: occlusion queries are exact per-node tests, not conservative range bounds.
  The gap to OPT measures query overhead and latency, not bound looseness.

  The paper also separates out the "optimal" algorithm defined by Guthe et al.
  (NOHC-OPT), and shows CHC++ "clearly below" that optimum. That earlier optimum was a
  cost-model optimum, not an oracle.
  https://dcgi.fel.cvut.cz/home/bittner/publications/chc++.pdf
- **[fetched]** Zhang et al., *Visibility Culling using Hierarchical Occlusion Maps*
  (SIGGRAPH 1997). It reports "% culling" of the total model, and frame time
  normalized to rendering without HOM. Both are **self** references. No comparison
  with the exact visible set **[not found]**.
  https://www.cs.princeton.edu/courses/archive/spr01/cs598b/papers/zhang97.pdf
- **[search]** Wonka et al., *Guided Visibility Sampling* (SIGGRAPH 2006). This is
  an aggressive, sampling-based PVS method. Its Table 2 reports error as false pixels
  on a 1000×1000 screen, together with PVS size. Section 4.5 compares it to Bittner's
  exact algorithm on one scene, because exact solvers do not run on the larger test
  scenes. The text was extracted in this pass, but not read in full.
  https://peterwonka.net/Publications/pdfs/2006.SG.Wonka.GuidedVisibilitySampling.final.pdf

## 2. Empty-space skipping in volume rendering

- **[fetched]** Li, Mueller, Kaufman, *Empty Space Skipping and Occlusion Clipping for
  Texture-based Volume Rendering* (IEEE Vis 2003):
  - **Section 5, Table 1.** Empty-space skipping is measured as a frame-rate speedup
    over no skipping, 2.8× on average. This is a **self** reference.
  - **Section 5, Table 2.** It reports the percentage of non-empty voxels skipped
    through occlusion, 48% to 64% with a 56.2% average. The denominator is all
    non-empty voxels, not the truly occluded voxels. The authors write that "there
    are still many occluded voxels that have not been skipped" and call the number
    "smaller than we have expected". The truly occluded set is not computed
    **[not found]**.
  - **Section 5.** Overhead is reported separately. The opacity map and clipping cost
    about 10% of the empty-space-skipping render time, measured with a mode that pays
    the overhead but skips nothing ("ESS+M").

  https://www3.cs.stonybrook.edu/~mueller/papers/OcclusionClipping.pdf
- **[fetched]** Zellmann, *Comparing Hierarchical Data Structures for Sparse Volume
  Rendering with Empty Space Skipping* (arXiv 1912.09596, 2019). It reports frame
  rates and build times, and compares only against naive ray marching and simple
  macrocell grids, as the paper itself states. No step count or sample count against
  an ideal traversal **[not found]**.
  https://arxiv.org/abs/1912.09596
- **[search]** Hadwiger et al., *SparseLeap* (IEEE TVCG 24(1), 2018). It builds
  per-pixel lists of ray segments by rasterizing occupancy geometry. The full text
  was not retrieved (the repository returned HTML), so it is unknown whether it
  reports skip efficiency against the true empty space.
  https://vccvisualization.org/research/sparseleap/
- **[search]** Deakin and Knackstedt, *Efficient ray casting of volumetric images using
  distance maps for empty space skipping* (Computational Visual Media 6(1), 2020). The
  full text was not retrieved.

## 3. Interval and affine arithmetic for implicits, noise and SDF marching

- **[fetched]** Keeter, *Massively Parallel Rendering of Complex Closed-Form Implicit
  Surfaces* (SIGGRAPH 2020):
  - **Section 3, Interval arithmetic.** Ambiguous regions "either contain the shape's
    boundary or are a false positive (due to interval arithmetic's conservative
    behavior)". The false-positive share is not measured **[not found]**.
  - **Section 2.1, Figure 5.** Reported efficiency is against **self** and peer
    baselines: a brute-force per-pixel interpreter (about 58 ms/MP) and a compiled
    kernel (about 3 ms/MP).
  - **Section 3.2, Figure 6.** Tape length is reduced 17× in 64² tiles and 216× in 8²
    subtiles, from 6056 clauses to means of 356 and 28.
  - **Section 5, Figures 9 and 10.** Heatmaps of work per pixel show extra work at
    model edges, where interval evaluation "can't skip entire regions". The text says
    many smooth blends lead "to less useful interval evaluation results".
  - **Section 6.** It names reduced affine arithmetic as a way "to more tightly bound
    intervals".

  https://www.mattkeeter.com/research/mpr/keeter_mpr20.pdf
- **[fetched]** Keeter, *Gradients are the new intervals* (blog, 2025-05-14). It
  compares interval evaluation with gradient-based pseudo-intervals by tape length
  (120.2 against 121.5 clauses) and by render time. These are peer references. There
  are no tile-classification counts and no ground truth **[not found]**.
  https://www.mattkeeter.com/blog/2025-05-14-gradients/
- **[fetched]** Heidrich, Slusallek, Seidel, *Sampling Procedural Shaders Using Affine
  Arithmetic* (ACM TOG 17(3), 1998):
  - **What "error" measures.** Error is the width of the computed range per pixel. It
    is not compared with the true range from dense sampling **[not found]**. The
    tightness against the true variation is argued qualitatively, from shader cross
    sections.
  - **Section 3.2.** Noise over an affine form that spans several lattice cells falls
    back to interval arithmetic, and a span of more than two cells returns the full
    noise range [0, 1]. For turbulence (blue marble), 81% of pixels lie within 5%
    error and 59% within 1%. The eroded shader, with high-frequency noise, gets 77%
    and 53%.
  - **Section 4.** Against AA, interval arithmetic gave errors "up to 50 percent where
    affine arithmetic produces errors below 1/256" (wood shader, Figures 6 and 7).

  https://vccimaging.org/Publications/Heidrich1998SPS/Heidrich1998SPS.pdf
- **[fetched]** Gamito and Maddock, *Ray casting implicit fractal surfaces with reduced
  affine arithmetic* (The Visual Computer 23(3), 2007). Section 5, Tables 1 to 3, use
  a sphere hypertextured with three octaves of noise. The accuracy proxy is the
  average number of interval evaluations per ray, a **peer** comparison with no oracle
  count. The rows below are reconstructed from a mangled text layout, and the
  reconstruction agrees with the paper's prose.

  | Noise                  | IA evals/ray | IA time   | Standard AA evals/ray | Reduced AA evals/ray | Reduced AA + interval optimisation, evals/ray | Its time  |
  |------------------------|--------------|-----------|-----------------------|----------------------|-----------------------------------------------|-----------|
  | Perlin gradient        | 78.40        | 8m27.5s   | 34.60                 | 34.60                | 20.81                                         | 3m43.2s   |
  | Lewis sparse convolution | 48.44      | 26m31.0s  | 23.25                 | 23.25                | 13.46                                         | 6m29.3s   |
  | Worley cellular        | 45.70        | 25m54.7s  | 32.56                 | 43.78                | 21.29                                         | 14m25.9s  |

  The text cites "the excessive conservativeness of IA estimates", and says IA
  compensates by being fast.
  https://eprints.whiterose.ac.uk/id/eprint/3762/1/gamito_newraycastvisual.pdf
- **[fetched]** Knoll, Hijazi, Kensler, Schott, Hansen, Hagen, *Fast Ray Tracing of
  Arbitrary Implicit Surfaces with Interval and Affine Arithmetic* (CGF 28(1), 2009).
  Section 6.1.1 and Tables 1 and 2 report frames per second for IA against reduced
  AA:
  - RAA is "generally 1.5 to 2× faster", and "3 to 4 times faster" for functions
    "with high bound overestimation";
  - IA is faster for superquadrics.

  These are **peer** references, with no oracle count of wasted subdivisions
  **[not found]**.
  https://sci.utah.edu/~knolla/cgrtia.pdf
- **[fetched]** Sharp and Jacobson, *Spelunking the Deep: Guaranteed Queries on General
  Neural Implicit Surfaces via Range Analysis* (SIGGRAPH 2022), section 3.4 and Table 1.
  Tightness is measured as "the typical size of a region which can be bounded away
  from the zero":

  | Method        | 1D length | 3D volume       | Ray-cast time (relative to the fastest) |
  |---------------|-----------|-----------------|-----------------------------------------|
  | interval      | 0.011     | < 0.001 × 10⁻³  | 34.3×                                   |
  | affine (full) | 0.821     | 3.499 × 10⁻³    | 8.4×                                    |
  | affine (fixed)| 0.306     | 0.267 × 10⁻³    | 1.0×                                    |

  The prose says interval arithmetic "yields extremely pessimistic bounds". These are
  **peer** references, with no oracle region size **[not found]**.
  https://arxiv.org/abs/2202.02444
- **[fetched]** Mitchell, *Robust Ray Intersection with Interval Arithmetic* (Graphics
  Interface 1990). It reports root-finding timings for its method and for peer
  root finders. No pruning-against-oracle measure **[not found]**.
  http://graphicsinterface.org/wp-content/uploads/gi1990-8.pdf
- Duff, *Interval Arithmetic and Recursive Subdivision for Implicit Functions and
  Constructive Solid Geometry* (SIGGRAPH 1992), and Snyder, *Interval Analysis for
  Computer Graphics* (SIGGRAPH 1992), were not retrieved in this pass.
  https://doi.org/10.1145/142920.134027

## 4. Conservative rasterization and tile classification

- **[fetched]** Hasselgren, Akenine-Möller, Ohlsson, *Conservative Rasterization*
  (GPU Gems 2, chapter 42):
  - **Section 42.1.** It defines overestimated rasterization as every pixel whose cell
    intersects the polygon, and underestimated rasterization as every pixel whose
    cell lies wholly inside it.
  - **Section 42.5.** Overdraw is discussed qualitatively: it depends on triangle
    acuity and resolution. Figure 42-7 compares the two proposed algorithms only. No
    count of excess fragments against the exact overlap set **[not found]**.

  https://developer.nvidia.com/gpugems/gpugems2/part-v-image-oriented-computing/chapter-42-conservative-rasterization
- **[search]** Antochi, Juurlink, Vassiliadis, Liuha, *Scene management models and
  overlap tests for tile-based rendering* (DSD 2004), and the companion *Efficient
  tile-aware bounding-box overlap test*. In a search summary, the bounding-box
  triangle-to-tile test "can generate up to 30% false positives" on common workloads,
  measured against exact overlap. The full text could not be retrieved (the TU Delft
  host refused the connection, and CiteSeerX returned HTML).
  https://citeseerx.ist.psu.edu/document?doi=5c78f05f185b9513bf930588dd2a4ae334090620

## 5. Summary of reference kinds and reported figures

| Source | Reference | Figure reported |
|--------|-----------|-----------------|
| Bittner et al. 2005, §6.2 | oracle (exact PVS) | conservative PVS +47% / +76% (ratio ≤ 0.68 / ≤ 0.57) |
| Bittner et al. 2005, §6.3 | oracle | object discretization +17% (≈ 0.85); line-space 0.023–0.52 by scene and resolution |
| Bittner thesis, ch. 2 | oracle | defines `Q_o`, `Q_u` relative to `|S_E|` |
| Cohen-Or et al. 2003 | — | notes the PVS/visible-set ratio is rarely reported |
| Nirenstein et al. 2002 | oracle (its own exact) | percent culled; calls that metric crude |
| CHC++ 2008 | oracle (OPT, node level) | within a few percent of OPT (exact queries, not bounds) |
| HOM 1997 | self | % of model culled, normalized frame time |
| Li, Mueller, Kaufman 2003 | self / all non-empty | 2.8× speedup; 56% of non-empty voxels skipped; overhead ≈ 10% reported separately |
| Zellmann 2019 | self | fps against no skipping |
| Keeter 2020 | self (brute force, compiled) | 17× / 216× tape reduction; false-positive share not measured |
| Heidrich et al. 1998 | none (range width) | IA error up to 50% where AA < 1/256 |
| Gamito, Maddock 2007 | peer | IA 2.1–3.8× more evaluations per ray than reduced AA + optimisation on noise |
| Knoll et al. 2009 | peer | RAA 1.5–4× faster than IA |
| Sharp, Jacobson 2022 | peer | IA provable region about 75× smaller in 1D than full AA |
| GPU Gems 2, ch. 42 | peer | qualitative overdraw |
| Antochi et al. 2004 [search] | oracle (exact overlap) | bounding box up to 30% false positives |

On the interval side (section 3), none of the sources read in this pass reports a
pruning or culling fraction against an oracle mask, such as the truly empty cells or
the truly non-ambiguous tiles. They report only against self or peer baselines. The
oracle-referenced ratios found are all on the visibility side (sections 1.2 and 1.3),
and the one from rasterization (section 4) is search-only. No published figure was
found for interval-arithmetic culling of noise or warped-noise fields as a fraction of
an oracle.

## 6. Local context (from the eureka mind, not re-measured)

- **Question** `cultmath-tapes:question:wells-ceiling-oracle` (admitted 2026-10-03,
  Imagination). At 3ac7250a it gives:
  - warp 0: oracle efficiency 0.38, mask ceiling 0.96, cull fraction 0.95;
  - warp D = 60: oracle efficiency 0.19, mask ceiling 0.99, cull fraction 0.84;
  - a pre-pass that skips nothing: 0.14 against the oracle.
- **Finding** `cultmath-tapes:finding:cut-interval-ops.s2.wells-ceiling-contract-vacuous`
  (Soul, confirmed by mutation). The mask-referenced (c) check reports 0.98 with the
  wells skip disabled. Locations are in
  `packages/cultmath/tests/CultMath.Tests/NoiseBoundTests.cs`: lines 1257–1260, 1437
  and 1461.
