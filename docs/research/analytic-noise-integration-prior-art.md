# Analytic Noise Integration Along Warped Rays: Prior Art Survey

This is the facts file for an idea the operator raised on 2026-10-03:

> "I would be happy if we could just skip the space we know has no meaningful structure
> because it's outside of the noise warp range. But what you said about affine arithmetic
> makes me wonder if we can somehow, even within the distorted domain, figure out a series
> of optimal (in terms of extracting structure over multiple frames) spans for which we can
> analytically integrate the noise gradients over those ray segments."

Self's sketch, as given to this pass:

- Simplex noise along a straight ray is piecewise polynomial in t, so its line integral
  is closed-form per crossed cell.
- A domain warp p + w(p) is nearly affine over a short span, with an affine-arithmetic
  error bound, so within that span the warped ray is a straight line in noise space.
- Low octaves are integrated exactly. High octaves are replaced by their expectation,
  with a variance bound.
- Clamped density max(0, n - c) is integrated by root-finding per piece.
- Spans are cached and refined across frames.

Eyes pass, 2026-10-03. This file gives facts and evidence pointers only. It makes no
recommendation.

Verification marks:

- **[fetched]** The claim was read in the cited source's full text during this pass.
  PDFs were converted with `pdftotext -layout`.
- **[search]** The claim came from a search-result summary or abstract, not a full read.
- **[derivation]** The claim is elementary mathematics worked out in this pass from a
  fetched definition. It is not a claim made by any cited source.
- **[not found]** The pass searched for this kind of work and found nothing.

## 0. Premises of the sketch, checked against definitions

- **[fetched] + [derivation]** Simplex noise kernel degree. Gustavson, *Simplex noise
  demystified* (2005), gives the 3D reference code. Each of the four simplex corners
  contributes `t^4 * dot(g, d)` with `t = 0.6 - |d|^2`, and the contribution is zero
  when `t < 0`. On a straight ray `d` is affine in the ray parameter, so `|d|^2` is
  quadratic and each corner term is a polynomial of degree 8 + 1 = 9. The pieces have
  two kinds of breakpoint: the simplex faces the ray crosses (where the corner set
  changes), and the entry and exit of each corner's support sphere (`|d|^2 = 0.6`).
  Both kinds are roots of polynomials of degree 2 or lower in the ray parameter, so the
  breakpoints are closed-form. With the 0.6 radius, the support extends past the
  simplex, and the reference code truncates it at the simplex boundary. The resulting
  small discontinuity across faces is a known property of that constant. It does not
  change the piecewise-polynomial structure.
- **[derivation]** Classic Perlin noise (Perlin 2002 quintic fade `6t^5 - 15t^4 + 10t^3`,
  read in Gustavson's text **[fetched]**) is, within one lattice cube along a straight
  ray, a polynomial of degree 3 x 5 + 1 = 16. Its breakpoints are the cube faces.
- **[fetched]** Piece count grows with frequency. Balint (CESCG 2019, section 2)
  states that many noise functions are piecewise integrable, but the number of pieces
  is linear in the noise frequency. Their cost is therefore similar to numerical
  integration. Balint designed cosine noise to escape this limit (section 1.1 below).
- **[derivation]** For the clamp `max(0, n - c)` on a degree-9 or degree-16 piece,
  the roots of `n - c` are needed. Polynomials of degree 5 or more have no general
  closed-form roots (Abel-Ruffini), so the clamp needs numerical root isolation per
  piece, for example Sturm sequences, Descartes' rule or Bernstein-basis subdivision.
  The integral between the isolated roots is closed-form.

## 1. Closed-form line integrals of noise, and analytic optical depth

### 1.1 Cosine noise: Balint, *Closed Form Transmittance in Heterogeneous Media Using Cosine Noise*, CESCG 2019 (non-peer-reviewed)

- **[fetched]** It computes noise defined as an offset plus a sum of cosines of
  `x . S_i`. The paper uses 24 quasirandom vectors S_i, with Sobol directions on the
  sphere and lengths in [0.8, 1.2], to look roughly like Perlin noise. Fractal octaves
  are further cosine sets. Its line integral is a sum of sines, so the optical depth
  over any segment is exact. The paper says this takes two noise samples, one at each
  segment end. It also gives closed-form distance sampling, by inverting the integral
  with binary search.
- **[fetched]** Cost: two noise-sized evaluations per segment, independent of octave
  count. Table 1 reports transmittance evaluation at 179 ms closed-form against
  1250 ms for delta tracking, about 7x faster. A real-time demo with 16 samples ran
  at 80 fps at 675 x 1200 (RTX 2080 Ti).
- **[fetched]** Error: exact, with no error term, for the bare sum. The limits are
  stated in section 6. Multiplying by a shape function, or composing with a shaping
  function, generally destroys the elementary antiderivative. The workarounds
  recover exactness only piecewise: constant multipliers between bounding isosurfaces,
  or a cubic polynomial interpolant of the multiplier along the ray, since the product
  of a polynomial and cosines has an elementary antiderivative. Negative densities from
  a low offset are handled by clamping samples and integrals, which is not an exact
  `max(0, .)` integral. Domain warp is not discussed.

### 1.2 Gabor kernels: Condor, Hermann, Yurtsever, Didyk, *Gabor Fields: Orientation-Selective Level-of-Detail for Volume Rendering*, ACM TOG 45(4) art. 62, 2026 (arXiv 2602.05081)

- **[fetched]** It computes closed-form definite line integrals of anisotropic 3D Gabor
  kernels along an arbitrary ray segment (eq. 9). Each kernel is whitened into its
  canonical space with a ZCA matrix, so any covariance, that is any affine-transformed
  kernel, uses the same formula. The segment integral is the real part of a
  complex-erf (Faddeeva) difference.
- **[fetched]** Cost: the complex erf is evaluated by a truncated Taylor series with
  early exit, capped at 16 terms. Segments shorter than 1e-4 in whitened units fall back
  to a single midpoint evaluation. Kernels are spatially clamped with an adaptive extent
  that shrinks with frequency (eq. 15). The paper also renders procedural clouds as
  Gaussians modulated by Gabor noise.
- **[fetched]** Error: each segment integral is exact apart from the erf truncation.
  Orientation-selective masking of kernels whose line integral is near zero trades a
  small error, or variance under stochastic selection, for speed. The paper reports
  about 1.78x to 1.96x speedups. Per segment the density-integral estimator is
  unbiased, but transmittance is biased by Jensen's inequality. Eq. 19 gives the
  second-order bias as `0.5 * Var(tau) * exp(-E[tau])`.

### 1.3 Analytically integrable media, older

- **[search]** Iser, *Real-time Light Transport in Analytically Integrable
  Quasi-heterogeneous Media*, CESCG 2018. Balint cites it **[fetched]** as using only
  exponential and spherical density functions.
- **[fetched via Gabor Fields related work]** The closed-form Gaussian-primitive
  transmittance line (Knoll et al. 2021; Condor et al. 2025, *Don't Splat your
  Gaussians*) is the base that Gabor Fields extends.

### 1.4 Piecewise-polynomial segment integration in scientific volume rendering

- **[fetched]** Engel, Kraus, Ertl, *High-Quality Pre-Integrated Volume Rendering Using
  Hardware-Accelerated Pixel Shading*, Graphics Hardware 2001. The scalar field along
  the ray is assumed piecewise linear between samples. The volume rendering integral of
  each linear segment, through an arbitrary nonlinear transfer function, is then one
  lookup in a table indexed by front scalar, back scalar and segment length. Cost: one
  texture lookup per segment, plus recomputing the table when the transfer function
  changes. Error: exact for the piecewise-linear model. The model error is the
  scalar field's deviation from linear between samples.
- **[search]** Williams, Max, Stein, *A High Accuracy Volume Renderer for Unstructured
  Data*, IEEE TVCG 4(1), 1998. Exact integration through tetrahedra with linearly
  varying scalar and piecewise-linear transfer functions.

### 1.5 Polynomial noise along a ray

- **[not found]** No published closed-form segment integral of Perlin or simplex noise
  was found, as a method or a code library. Balint's remark in section 0 is the only
  published statement found on the piecewise approach, and it dismisses it on cost
  grounds.

## 2. Noise designed for analytic filtering or integration

- **[fetched]** Lagae, Lefebvre, Drettakis, Dutré, *Procedural Noise using Sparse Gabor
  Convolution*, ACM TOG 28(3), 2009. It computes a sparse-convolution noise of Poisson
  impulses with Gabor kernels. Each kernel is a Gaussian in frequency, so filtering with
  a Gaussian pixel filter multiplies Gaussians, which gives an analytically filtered
  kernel. Cost: the paper typically uses 100 impulses per kernel, or 25 to 50 at lower
  densities. Table 2 (GTX 280) reports, at 20 impulses per cell, 290 fps for 2D noise,
  111 fps for unfiltered surface noise and 50 fps for filtered surface noise. Error:
  the filtering is exact for a Gaussian filter. The noise statistics converge with
  impulse density.
- **[fetched]** Lagae, Drettakis, *Filtering Solid Gabor Noise*, ACM TOG 30(4), 2011. It
  slices solid Gabor noise, filters each slice of each Gabor kernel, and gets a
  generalisation of the projection-slice theorem. It also states that wavelet noise
  integrates solid noise perpendicular to the surface. That is a line integral of noise
  along the normal, done in the basis.
- **[search]** Cook, DeRose, *Wavelet Noise*, ACM TOG 24(3), 2005. A quadratic B-spline
  noise band, which is almost band-limited, built from a precomputed coefficient tile.
  Its 3D-to-surface projection integrates the noise along the normal. The original PDF
  was not retrievable in this pass. Pixar and ACM both returned HTML.
- **[fetched, survey]** Lagae et al., *A Survey of Procedural Noise Functions*, CGF 2010.
  - Lewis's sparse convolution noise has a power spectrum equal to the kernel's,
    scaled.
  - Frequency clamping (Norton, Rockwood, Skolmoski 1982) cancels bands above the filter
    rate. It works only for narrowly band-pass noise, and Perlin noise is only weakly
    band-pass.
  - Nonlinear functions of noise, such as thresholds and colour maps, are not filtered
    by filtering the noise. That is unsolved in general.
  - Heidrich, Slusallek, Seidel 1998 (below) computes an area average of a procedural
    shader with an error bound, using affine arithmetic.
  - Perlin noise's amplitude distribution is visibly non-Gaussian, because it is zero at
    every lattice point and has few gradients. The other noises surveyed are
    approximately Gaussian.
- **[fetched, via the survey above]** "Filtering procedural textures" lineage:
  - Rhoades et al. 1992 filters the colour table over `[N - e, N + e]`.
  - Hart et al. 1999 sets `e` from the local noise gradient.
  - Lagae 2009 sets the range from the analytically known loss of variance.

## 3. Affine arithmetic, reduced AA, Taylor models, and bounds on warps

### 3.1 Gamito and Maddock

- **[fetched]** Gamito, Maddock, *Ray Casting Implicit Fractal Surfaces with Reduced
  Affine Arithmetic*, The Visual Computer 23:155-165, 2007. The full method is read in
  Gamito's PhD thesis (Sheffield), sections 5.3 to 5.4.
  - **Computes:** a root of `g(t) = 0` along a ray, by subdividing intervals of t. Each
    interval is tested with an inclusion `G(T)` computed in reduced AA (Messine's AF1).
    The form is `t0 + t1 e1 + t2 e2`: e1 is the shared ray-parameter symbol, and e2
    absorbs every new nonlinearity by condensation after each operation.
  - **Noise in RAA:** gradient-noise kernels lose no correlation under RAA, because the
    three multiplications are local to one kernel and the cubic Hermite fade is done by
    a single Chebyshev affine approximation. The same holds for sparse convolution
    noise. Cellular noise loses accuracy, because `min` needs cancellation between
    condensed terms, and the F2 and F3 kernels were not implementable.
  - **Interval optimisation:** the parallelogram `g1/t1` slope plus or minus `g2`
    shrinks `[ta, tb]` before subdividing (eq. 5.27).
  - **Cost:** Table 5.2, gradient-noise hypertexture, average evaluations per ray and
    time:

    | Method | Evaluations per ray | Time |
    |---|---|---|
    | IA | 116.57 | 7m35s |
    | Standard AA | 61.69 | 40m41s |
    | RAA | 61.69 | 6m43s |
    | RAA + interval optimisation | 46.39 | 5m04s |

    RAA costs three numbers per quantity against two for IA. Standard AA is tight
    but slow because its error-symbol lists grow.
  - **Error bound:** conservative inclusion, as proven for AA by Comba and Stolfi 1993.
    Rounding error is ignored.
- **[fetched]** Gamito thesis, chapter 9, *Surface Deformation with Flow Mapping*.
  - Domain deformation `f(d(x))` with `d` defined by streamlines, that is by an ODE.
    This is the case closest to a domain warp in this survey.
  - Gamito states that a robust and tight AA estimate of the deformed ray `s(t)` was not
    obtainable through the numerical ODE solution, so robustness was given up.
  - The replacement test uses Lipschitz unbounding spheres at `s(ta)` and `s(tb)`. A
    span is skipped if `|s(tb) - s(ta)| < min(ra, rb)` (eq. 9.23). A sharp turn of the
    deformed ray between the endpoints can make this test wrong.
  - He names truncated Taylor-series range estimation (Nedialkov et al. 2004) as the
    possible fix, at higher cost and complexity.

### 3.2 Other affine and interval arithmetic sources

- **[fetched, bibliographic]** Comba, Stolfi, *Affine Arithmetic and its Applications to
  Computer Graphics*, SIBGRAPI 1993. de Figueiredo, Stolfi, *Affine Arithmetic: Concepts
  and Applications*, Numerical Algorithms 37, 2004. Gamito cites both. AA keeps
  first-order correlations through shared noise symbols. Each nonaffine operation adds
  one symbol, whose coefficient bounds the linearisation error. Chebyshev or min-range
  affine approximations give that coefficient for unary functions.
- **[search]** Knoll, Hijazi, Kensler, Schott, Hansen, Hagen, *Fast Ray Tracing of
  Arbitrary Implicit Surfaces with Interval and Affine Arithmetic*, CGF 28(1), 2009.
  Inclusion-preserving reduced AA, SIMD CPU at interactive rates, and a stackless GPU
  version.
- **[fetched, abstract]** Fryazinov, Pasko, Comninos, *Fast Reliable Ray-tracing of
  Procedurally Defined Implicit Surfaces Using Revised Affine Arithmetic*, Bournemouth
  TR-NCCA-2009-04. Revised AA, which also keeps one accumulated error symbol, is
  reported fastest among the IA and AA variants compared, including on pseudo-random
  procedural objects.
- **[fetched]** Sharp, Jacobson, *Spelunking the Deep: Guaranteed Queries on General
  Neural Implicit Surfaces via Range Analysis*, ACM TOG 41(4), 2022. This is AA through
  long compositions of nonlinear layers, which is structurally like noise composed with
  a warp. Table 1 compares the variants. Time is relative to one scalar evaluation,
  length is the 1D region that can be certified, and raycast time is relative to the
  best method:

  | Variant | Time | Length | Raycast time |
  |---|---|---|---|
  | Interval | 2.4x | 0.011 | 34.3x |
  | Affine-full | 95.3x | 0.821 | 8.4x |
  | Affine-fixed (no new symbols kept, extra error condensed into one) | 4.7x | 0.306 | 1.0x |

  The paper uses affine-fixed for ray casting and affine-full for 3D queries.
- **[fetched, survey quote of abstract]** Heidrich, Slusallek, Seidel, *Sampling of
  Procedural Shaders using Affine Arithmetic*, ACM TOG 17(3):158-176, 1998. It computes
  the average of a procedural shader over a finite area, with an AA error bound.
- **[fetched, project page]** Keeter, *Massively Parallel Rendering of Complex
  Closed-Form Implicit Surfaces*, ACM TOG 39(4), 2020. Interval arithmetic over a
  shallow, high-branching tile hierarchy does two things: it skips empty tiles, and it
  prunes the expression tape per tile. One benchmark drops the expression size by about
  two orders of magnitude. It requires only C0 continuity, so warps and blends that break
  Lipschitz continuity are allowed.

### 3.3 Lipschitz bounds through warps

- **[fetched]** Galin, Guérin, Paris, Peytavie, *Segment Tracing Using Local Lipschitz
  Bounds*, CGF 39(2), 2020. It computes a local Lipschitz bound over a ray segment
  instead of a global one. Section 4.4 covers warps `f o w^-1`, and bounds the gradient
  by the product of the field gradient bound over the transformed segment and the
  Jacobian norm bound over the segment. Cost: fewer field queries than sphere tracing,
  with no acceleration structure. Error: conservative, if the local bounds are
  conservative.
- **[fetched]** Seyb, Jacobson, Nowrouzezahrai, Jarosz, *Non-linear Sphere Tracing for
  Rendering Deformed Signed Distance Fields*, ACM TOG 38(6), 2019.
  - **Computes:** the ray in deformed space as a curve in undeformed space, by
    integrating an ODE whose derivative is the inverse-deformation Jacobian times the ray
    direction. An initial value comes from rasterising a deformed hull mesh.
  - **Error control:** step requirements for the curve are separated from those for the
    SDF. Adaptive Runge-Kutta (RKF45 and DP54 evaluated) is used far from the surface,
    and forward Euler near it, against an error tolerance.
  - **Scope:** surfaces only. There is no density integration.
- **[fetched]** Moinet, Neyret, *Fast Sphere Tracing of Procedural Volumetric Noise for
  Very Large and Detailed Scenes*, CGF 2025 (Eurographics), HAL hal-05046040.
  - **Computes:** sphere tracing of FBM density fields, using first- and second-order
    (2-Lipschitz) forward inclusion functions. The FBM partial sums act as a nested
    bounding volume hierarchy: the remaining octaves are bounded by their amplitude sum
    `s(i+1, n)`, so evaluation stops early once a point is provably outside.
  - **Cost:** up to 12x faster than classical sphere tracing on opaque or constant-density
    scenes. Only up to 1.8x on non-constant density, and that gain comes from empty-space
    skipping alone. One example frame: 16 ms against 110 ms naive at 1024 x 1024 on a
    4080 Ti.
  - **Stated limits:** FBM used as a domain warp is not supported, nor are nonlinear
    transforms of FBM. The paper names analytic estimation of the contribution of
    sub-pixel detail as an open problem.

### 3.4 Taylor models

- **[search]** Berz, Makino, *Verified Integration of ODEs and Flows Using Differential
  Algebraic Methods on High-Order Taylor Models*, Reliable Computing 4, 1998. Makino,
  Berz, *Taylor Models and Other Validated Functional Inclusion Methods*, IJPAM 2003.
  - **Computes:** a Taylor model is a polynomial of order n plus an interval remainder
    bound, which encloses any function given as code over a box. The sharpness of the
    remainder scales with the box width to the power n + 1. The method also gives
    verified ODE flows.
  - **Verified integration:** Berz and Makino also published *New Methods for
    High-Dimensional Verified Quadrature* (Reliable Computing 1999). There the integral
    of the polynomial part is exact, and the remainder bound times the domain measure
    bounds the error.
  - First-order AA is the n = 1 case in spirit: an affine part plus a bounded remainder.

### 3.5 Affine bound on a warp over a span

- **[derivation]** Over a segment of half-length r, the best affine approximation
  (Chebyshev) of a C2 coordinate function has sup error at most `r^2 / 4` times the
  maximum of its second derivative along the segment. Linear interpolation of the
  endpoints has `r^2 / 2` times that maximum. The noise error caused by a displacement
  error of size `delta` is at most `L_noise * delta`. This is the same chain-rule
  structure Galin uses in section 4.4.

## 4. Statistical or expected-value treatment of high-frequency detail

- **[fetched]** Heitz, Nowrouzezahrai, Poulin, Neyret, *Filtering Color Mapped Textures
  and Surfaces*, I3D 2013. It computes the filtered value of a colour map applied to a
  procedural function as `<C, N(mu, sigma^2)>`. The noise's footprint distribution is
  replaced by a Gaussian with the footprint's mean and variance. This equals the colour
  map convolved with `N(0, sigma^2)` and evaluated at the mean, which is precomputed into
  a 2D table indexed by (mu, sigma). Cost: one lookup after mu and sigma are computed.
  Error: exact when the driving function's footprint distribution is Gaussian. For a
  specific noise instance it converges as the footprint grows, and the paper states this
  limit.
- **[derivation]** For `X ~ N(mu, sigma^2)`, `E[max(0, X - c)] = (mu - c) Phi(z) +
  sigma phi(z)` with `z = (mu - c) / sigma`. This is the rectified-Gaussian mean, and the
  scalar instance of the Heitz 2013 construction for the map `max(0, . - c)`. The
  survey's fetched finding that Perlin noise is non-Gaussian bears on how accurate it is.
- **[search]** Olano, Baker, *LEAN Mapping*, I3D 2010. Dupuy et al., *LEADR Mapping*, ACM
  TOG 32(6), 2013. They store the first and second moments of slopes in mipmaps. Linear
  filtering of the moments gives a Gaussian or Beckmann slope distribution per
  footprint. Cost: two extra texture channels and linear filtering. Error: the Gaussian
  assumption.
- **[fetched]** Gabor Fields (1.2): band and orientation masking of high-frequency
  kernels, with the Jensen bias of transmittance `~ 0.5 Var(tau) exp(-E[tau])`.
- **[search]** Vicini, Jakob, Kaplanyan, *A Non-exponential Transmittance Model for
  Volumetric Scene Representations*, ACM TOG 40(4), 2021. Bitterli et al., *A Radiative
  Transfer Framework for Non-exponential Media*, ACM TOG 37(6), 2018. Jarabo, Aliaga,
  Gutierrez 2018, arXiv 1805.02651. Unresolved, correlated sub-voxel density makes
  transmittance non-exponential. Positive correlation gives slower decay than exponential.
- **[search]** Kettunen, d'Eon, Pantaleoni, Novák, *An Unbiased Ray-Marching
  Transmittance Estimator*, ACM TOG 40(4), 2021. A power-series estimator built around a
  control variate. The control variate is a ray-marched mean-density estimate, and rare
  higher-order terms remove the bias. It is relevant as the known way to make a
  deterministic or expected-value optical depth unbiased.
- **[fetched]** Schneider, *The Real-time Volumetric Cloudscapes of Horizon: Zero Dawn*,
  SIGGRAPH 2015 Advances course. The high-detail noise is applied only as erosion where
  the low-detail sample is non-zero. Rays take cheap large steps until the low-detail
  isosurface is hit, step back once, and then take full samples. After several zero
  samples they return to cheap stepping. This is an octave-level skip heuristic with no
  bound. Moinet (3.3) is the bounded version for FBM.

## 5. Temporal reuse of per-pixel or per-tile raymarch structure

- **[fetched]** Schneider 2015 (above). The full raymarch cost about 20 ms. Each frame
  renders 1 of 16 pixels per 4 x 4 block into a quarter-resolution buffer, and
  reprojects the previous frame. Where reprojection fails, for example at screen edges,
  a low-resolution buffer is substituted. The paper reports 10x or more faster, with a
  target of about 2 ms on PS4.
- **[search]** Klein, Strengert, Stegmaier, Ertl, *Exploiting Frame-to-Frame Coherence
  for Accelerating High-Quality Volume Raycasting on Graphics Hardware*, IEEE
  Visualization 2005. First-hit depths of the previous frame are reprojected by point
  splatting to estimate per-pixel ray start points. About 2x on typical datasets.
- **[search]** Yagel, Shi, *Accelerating Volume Animation by Space-Leaping*, IEEE
  Visualization 1993. A C-buffer of first non-transparent voxel coordinates, updated
  across frames. Gudmundsson, Randén 1990 introduced inter-frame empty-space skipping.
- **[search]** Scherzer et al., *Temporal Coherence Methods in Real-Time Rendering*,
  CGF 2012 STAR (Eurographics 2011). Reprojection caches, with depth-tolerance validity
  tests for disocclusion.
- **[search]** Hillaire, *Physically Based Sky, Atmosphere and Cloud Rendering in
  Frostbite*, SIGGRAPH 2016 course. Temporal reprojection for volumetric clouds. Details
  were not read in this pass.
- **[fetched]** Gamito thesis, chapter 7: progressive refinement rendering of
  Lipschitz-bounded and AA-bounded surfaces. It refines across passes within one image,
  not across animated frames.
- **[not found]** No source was found that caches per-ray span partitions, affine
  forms or certified intervals across frames and refines them.

## 6. Warp linearisation combined with exact segment integration

- **[not found]** No source was found that combines piecewise affine linearisation of a
  domain warp, with an error bound, and exact closed-form integration of noise over the
  resulting segments.
- Nearest pieces, each covering part of the combination:

  | Source | What it covers | What it leaves out |
  |---|---|---|
  | Gamito ch. 9 | Bounding a deformed ray | No integral. AA gave up on the ODE warp |
  | Seyb 2019 | ODE curved ray with adaptive error control | Surfaces only |
  | Galin 2020 | Jacobian-norm local bounds through warps | Surfaces, not integration |
  | Keeter 2020 | Interval pruning that tolerates warps | Surfaces |
  | Gabor Fields 2026 | Closed form for any affine-transformed Gabor kernel along a segment | No warp |
  | Balint 2019 | Closed form for cosine noise | Composition and warps break it |
  | Pre-integration (Engel 2001) | Exact per-segment integral | Under a piecewise-linear model of the scalar |
  | Moinet 2025 | Bounded FBM octave skipping | Excludes FBM used as a domain warp |

- **[search]** Rezk-Salama et al., *Fast Volumetric Deformation on General Purpose
  Hardware*, Graphics Hardware 2001. Fetched text: piecewise-trilinear deformation
  patches with adaptive subdivision, rendered by object-aligned slicing. The
  approximate inverse is `x + O(delta^2)`. A search summary describes the deformed rays
  as polylines, but the fetched text uses slicing, not per-ray exact integration.

## Sources

- Balint 2019: https://www.cl.cam.ac.uk/~rkm38/pdfs/balint2019cosine_noise.pdf
- Condor et al. 2026, Gabor Fields: https://arxiv.org/pdf/2602.05081
- Gamito PhD thesis: https://staffwww.dcs.shef.ac.uk/people/S.Maddock/phd_theses/gamito/thesis_small.pdf
- Gamito, Maddock 2007: https://link.springer.com/article/10.1007/s00371-006-0090-7
- Lagae et al. 2009, Gabor noise: https://www-sop.inria.fr/reves/Basilic/2009/LLDD09/LLDD09PNSGC_paper.pdf
- Lagae, Drettakis 2011: https://inria.hal.science/inria-00606907/file/paper_0049_preprint.pdf
- Lagae et al. 2010 survey: https://www-sop.inria.fr/reves/Basilic/2010/LLCDDELPZ10/LLCDDELPZ10STARPNF.pdf
- Heitz et al. 2013: https://inria.hal.science/hal-00765799
- Sharp, Jacobson 2022: https://arxiv.org/pdf/2202.02444
- Seyb et al. 2019: https://cs.dartmouth.edu/~wjarosz/publications/seyb19nonlinear.html
- Galin et al. 2020: https://hal.science/hal-02507361
- Moinet, Neyret 2025: https://inria.hal.science/hal-05046040
- Keeter 2020: https://www.mattkeeter.com/research/mpr/
- Fryazinov et al. 2009: https://eprints.bournemouth.ac.uk/11708/1/TR-NCCA-2009-04.pdf
- Knoll et al. 2009: https://onlinelibrary.wiley.com/doi/10.1111/j.1467-8659.2008.01189.x
- Berz, Makino 1998: https://link.springer.com/article/10.1023/A:1024467732637
- Engel et al. 2001: https://www3.cs.stonybrook.edu/~mueller/teaching/cse616/engel.pdf
- Gustavson 2005: https://cgvr.cs.uni-bremen.de/teaching/cg_literatur/simplexnoise.pdf
- Schneider 2015: https://advances.realtimerendering.com/s2015/
- Kettunen et al. 2021: https://jannovak.info/publications/URM/URM.pdf
- Vicini et al. 2021: https://dvicini.github.io/non-exponential-representation/
- Rezk-Salama et al. 2001: https://www3.cs.stonybrook.edu/~mueller/teaching/cse616/p17-rezk-salama.pdf
- Klein et al. 2005: https://www.researchgate.net/publication/4188150
