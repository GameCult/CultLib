# An integration-native volumetric noise domain: probe

Campaign `cultmath-tapes`, Imagination probe, 2026-10-03, under ruling
`cultmath-tapes:ruling:integration-native-noise-domain`. This is research under the
parked tape target. It has no spec, no shipped-field change and no committed code.

It follows `analytic-integration-feasibility.md`, which found that exact noise
integrals are cheap but that the shipped densities are nonlinear in the noise. The
question here is the reverse one. Suppose a domain is designed so that span
integration holds by construction. Does it buy more visual detail per unit cost
than the shipped fields?

On the operator's instruction (relayed by Self), the reference is physical, not
Aetheria's tuned compositing:
- transmittance is Beer–Lambert, `T = exp(-τ)`;
- the images use single scattering with Henyey–Greenstein `g = 0.6` and albedo 1;
- both fields are integrated the same way.

The shipped look is a comparison, not a target.

## Verdict

**On the void, yes, by a wide margin. On the wells, modestly.** At or below the
shipped march's cost, the integration-native field carries much more resolvable
detail, and it is exact. The catches:
- The look changes character. The default cascade reads as fibres or brush strokes,
  not as soft cloud.
- The cost is traversal, not integration.
- The field cannot be ray-marched at all. Every consumer, shadow rays included,
  must use the exact path.

Headline numbers, 512 x 256, single-threaded C# on Yggdrasil:

| Void, transmittance image | Cost per pixel | vs shipped march | msf (grad²/var) | Power above 1/8 cycle/px | Pixels with \|∇T\| > 0.05 |
|---|---|---|---|---|---|
| Shipped field, converged reference (595 samples) | n/a | n/a | 0.0111 | 0.20% | 0.00% |
| Shipped march (11.4 samples) | 28.9 µs (44 snoise3) | 1.00x | 0.0178 | 0.71% | 0.00% |
| Integration-native, 2 levels, exact | 13.9 µs | **0.48x** | **0.0755** | **2.95%** | 41.9% |
| Integration-native, 3 levels, exact | 35.7 µs | 1.24x | 0.125 | 9.15% | 54.7% |
| Integration-native, 6 levels (LOD stops at 4 to 5), exact | 74.2 µs | 2.57x | 0.159 | 12.5% | 60.9% |

- **Mean density is matched.**
  - The integration-native field places mass from the shipped field's noise-averaged
    density.
  - Mean window optical depth is 1.62 to 1.74 against the shipped 1.81.
  - Mean T is 0.25 to 0.40 against 0.17. The difference comes from intermittency:
    the image std is 0.29 against 0.05, at a near-equal mean.
- **msf is normalised by variance.** It measures spatial frequency, not contrast.
- **Single-scattering images, void.** The images are 256 x 128 with a point light at
  the sun, and both are tonemapped with the same exposure. The shipped image's msf
  is 0.0023, with 0.22% of power above 1/8. The integration-native image's msf is
  0.101, with 16.9% of power above 1/8.
- **Wells.**
  - Transmittance is a near-opaque silhouette for both fields: equal msf (0.011),
    so it carries no signal.
  - Single scattering at 6 levels (1.20x the march cost):
    - msf is 0.073 against 0.069;
    - power above 1/8 is 10.1% against 7.0%;
    - the edge fraction is 16.7% against 9.5%.
  - At 4 levels the cost is 0.77x the march.

**Exactness.** Against a converged march of the same field at 0.02 world:
- The relative τ error is a median 3e-5 and a maximum of 1.0e-4 on the void, and a
  median 1e-5 and a maximum of 1.0e-4 on the wells.
- The binned gather against brute force over all roots differs by at most 1.3e-15.
- The shipped march against its own converged reference: |ΔT| is a median 0.0055
  (p90 0.013) on the void and a median 0.0006 (max 0.83 on grazing rays) on the
  wells.

**The detail is only reachable by integration.**
- Marching the same integration-native field at the shipped step gives a void |ΔT|
  median of 0.14, a p90 of 0.48 and a maximum of 0.83.
- A field this detailed is beyond the march at its step. That is the operator's
  premise, measured: the shipped fields' softness fits the regime that renders them.

## The primitive set

The primitive set has one design rule: nonlinearity lives in kernel placement, and
linearity lives in the integration. The density is a sum of nonnegative,
compact-support kernels. Everything nonlinear is evaluated once per kernel, at its
centre, and never per sample. That covers envelopes, clamps, `pow` profiles, the
flow warp and placement.

### Bases (closed-form line integral over a segment)

| Primitive | Why it integrates | Cost per span (measured unless marked) |
|---|---|---|
| Compact polynomial ellipsoid kernel, `(1 − u)^k` with `u = \|A⁻¹(x − μ)\|²` | On a line, u is quadratic in t, so the kernel is a polynomial of degree 2k between the two roots of `u = 1`, which are closed form. Same algebra as the simplex lattice integral in the first probe | k = 0: 5.5 to 7.4 ns; k = 1: 8 to 9.5 ns; k = 2: 11.7 ns. About 0.01 to 0.02 snoise3 (snoise3 is 610 to 650 ns in C#) |
| Truncated Gaussian ellipsoid, `exp(−κu)` on `u ≤ 1` | `exp` of a quadratic is a Gaussian in t, giving an erf difference. Truncation only clips the limits | 32 to 36 ns with A&S 7.1.26 erf (error ≤ 1.5e-7), about 0.05 snoise3 |
| k = 0 ellipsoid (constant density) | Chord length; this is EVER's primitive (Mai et al. 2024 [memory]) | 5.5 ns |
| Gabor kernel (Gaussian × cosine) | Complex erf, any affine transform (Gabor Fields 2026, eq. 9 [fetched in the prior-art file]) | Not probed. Up to 16 Taylor terms [paper] |
| Cosine sum (Balint 2019) | Integral is a sum of sines | Not probed. Two noise-sized evaluations per segment [paper]. Signed, so it needs an offset; low contrast |
| Simplex lattice (CultMath `snoise`) | First probe: degree 9 per lattice point | 0.16 snoise3 per point, 3.44 points per noise unit. Signed: only usable where density is affine in it |

### Warps

| Warp | Why it stays exact | Cost |
|---|---|---|
| Per-kernel affine warp: centre moved by `f(μ)·s`, shape by `(I + J·s)` | An affine image of an ellipsoid is an ellipsoid, so the ray stays straight and the kernel stays closed form. The warp *defines* the field, so there is no approximation error. It is evaluated once per root per frame (7 flow evaluations with the Jacobian) | Frame build 39 ms (void, 2540 roots) and 57 ms (wells, 16947 roots), or 19 and 27 ns per 1080p pixel amortised |
| Children inherit the parent's affine frame | The cascade has descendants that are exact affine images, so the warp is evaluated only at the roots | One 3x3 product per child, from a table |
| Not allowed | A per-point warp `x + w(x)` inside a span: the warped ray curves, which was the first probe's problem | n/a |

### Transfer and placement

| Rule | Status |
|---|---|
| Any function of the kernel centre: the shipped clamp, `pow`, the smoothstep fade, the noise-averaged density `E_n[ρ(x, n)]` from 16 empirical noise quantiles, through a 1D LUT over d (void) or s (wells) | Exact by construction, at one lookup per kernel |
| Affine transfer of the sum (`a + b Σ K_i`) | Exact |
| Any 1D function of an *affine* coordinate, `∫ f(a + bt) dt = ΔF / b` (pre-integration, Engel 2001) | Exact [derivation, not probed]: height fog `exp(−y/h)` and ramps in y |
| Polynomial or exp of a *quadric* coordinate; an indicator or clamp of a quadric (chord through roots) | Exact [derivation, not probed]: spherical shells and paraboloid bowls as envelopes |
| Product of the sum with a polynomial envelope in t | Exact, polynomial times polynomial [derivation, not probed] |
| Forbidden inside a span: `f(Σ K_i)` for nonlinear f, `max`/`min` of fields, a clamp of a signed sum, or noise displacing an envelope's argument | These are exactly what broke the first probe |

### Composition: the mass-conserving multiplicative cascade

- **Roots** sit on a jittered grid: spacing H0 = 30 world on the void and 40 on the
  wells, one root per cell.
  - Mass is `P(μ)·H0³`, so the mean density equals the shipped noise-averaged
    density.
  - The ellipsoid is elongated along the flow direction projected on the shell or
    fog layer (aspect 1.3 to 2.4), with ±0.6 rad of jitter, and flattened along the
    normal (×0.5 void, ×0.4 wells).
  - There are two copies per root, at the shipped two-phase shifts with weights w0
    and 1 − w0. Time animation is therefore the shipped crossfade, exact under
    summation.
- **Children.** Each node keeps 25% of its mass and gives 75% to Nc = 5 children.
  - Child weights are `u²·P(child)/P(parent)`, normalised. The `u²` makes the cascade
    intermittent; the P ratio sharpens it toward the envelope.
  - A child is scale 0.4, with aspect up to 1.6 and ±0.6 rad of rotation.
  - Children sit along the parent's major axis: offset 0.6 along it, 0.21 across.
  - All of this comes from a 256-entry table indexed by hash. A descendant bound of
    1.77 times the ellipsoid holds the subtree, so the parent is a BVH node.
- **LOD.** A node is a leaf when its children would be smaller than 0.5 × the pixel
  footprint at that depth. It then keeps its full mass. Because the cascade conserves
  mass, truncation is a mean-preserving prefilter: the expectation step of the first
  probe, exact here instead of approximate.
- **Gather.**
  - Roots are binned by their subtree AABB into a hash grid of H0 cells.
  - The ray walks the grid with Amanatides–Woo DDA and mailboxing, clipped to the
    shipped active windows, so both fields integrate the same span.
  - It exits early once Στ ≥ 7, which is valid because every term is nonnegative.

## "Juice" under these constraints

| Effect | Mechanism here | Measured or prior art |
|---|---|---|
| Filaments, wisps | Anisotropic children chained along the parent's major axis, which follows the flow | Visible in `void-T-IN-L6.png` as streaks along the flow. Gabor Fields uses anisotropic Gabor kernels for orientation-selective detail [fetched] |
| Sharp edges | Low-order kernels: k = 1 is the default, k = 0 gives hard ellipsoids | k = 0 (`L6-hard`) changes msf by −9% on the void. Edges come from kernel boundaries, not clamps |
| Voids | Regions with no kernel mass are exactly zero and are skipped by the gather at no cost | Structural |
| Turbulent intermittency | Random multiplicative cascade, the p-model of turbulent dissipation (Meneveau & Sreenivasan 1987 [memory]); clustered kernels, as in a Neyman–Scott process [memory] | Image std 0.29 against 0.05 at near-equal mean OD |
| Flow and animation | Per-kernel warp at the shipped flow and phases. Alternatives are Lagrangian advection of kernel centres with lifetimes (advected textures, Neyret 2003 [memory]) and Gabor phase for oscillatory detail (flow noise, Perlin & Neyret 2001 [memory]) | Only the two-phase crossfade was measured |
| Fine detail riding on structure | Children exist only inside parents, so detail appears only where matter is. This is Schneider's erosion idea (2015 [fetched]) without the per-sample clamp | Structural |

Wavelet turbulence (Kim et al. 2008 [memory]) and curl noise (Bridson 2007 [memory])
were not used. A per-point warp is ruled out above. Curl noise could orient kernels,
evaluated per kernel, at no cost to exactness.

## Measurements

**Setup.**
- Probe source: scratch clone `probe-clone@5ec9f529`,
  `packages/cultmath/probe/DomainProbe/Program.cs`, never merged.
- Run on Yggdrasil through the verify stopgap: dotnet 10 container, 4 CPUs, tiered
  JIT off.
- Full log: Self's scratchpad, `dom-full1.log`.
- The scenes are those of the first probe, with fixed cameras:
  - Void: the shipped orbiting camera at time 20, at the 1920-wide f = 935 field of
    view, rendered at 512 x 256.
  - Wells: warp D = 60, camera at xz (1200, 0) looking at the central well, slopes
    x ±1 and y from −0.85 to 0.15.
- The window is the shipped field's active intervals. Every method integrates the
  same window.

**Cost.**
- Single-threaded C#, best of three passes over the same 2048 random pixels.
- The host is shared, and absolute timings moved by up to 2x between the smoke run
  and the full run; Flow went from 125 to 660 ns. Ratios within one run are the
  reliable numbers.
- Flow is 47% of a shipped sample in this run, so a flow-cached march would cost
  about 15 µs on the void and 31 µs on the wells.

| Config (void) | Cost vs march | Roots visited / nodes tested / kernels integrated per pixel | Kernels per level |
|---|---|---|---|
| L1 | 0.12x | 43 / 8 / 8 | 8 |
| L2 | 0.48x | 59 / 25 / 20 | 8, 12 |
| L3 | 1.24x | 74 / 42 / 28 | 8, 12, 8 |
| L4 | 1.81x | 82 / 60 / 35 | 8, 12, 8, 7 |
| L6 (default) | 2.57x | 93 / 86 / 36 | 8, 12, 8, 7, 1, 0 |
| L6, no child placement | 1.79x | 93 / 91 / 39 | same metrics as L6 |
| L6, no LOD | 2.95x | 93 / 103 / 49 | 8, 12, 8, 7, 7, 7 |

| Config (wells) | Cost vs march | Kernels integrated per pixel |
|---|---|---|
| L1 | 0.04x | 5 |
| L2 | 0.23x | 11 |
| L3 | 0.49x | 16 |
| L4 | 0.77x | 20 |
| L6 | 1.20x | 25 |
| L6, no child placement | 0.62x | 27 |

**Where the time goes.**
- The integrals are about 36 kernels × 10 ns, or 0.4 µs of a 74 µs pixel.
- The rest is traversal:
  - roots visited but missed: 93 visited for 8 hits, because AABBs of elongated
    rotated ellipsoids are loose;
  - child generation;
  - placement lookups. The wells' placement costs a 5-bowl `Sdf`, and dropping
    child placement halves the wells cost with identical metrics.

**Aliasing.** One sample per pixel against four on the default void field: |ΔT| has
an RMS of 0.015 (p90 0.022). At 4 spp the msf is 0.146 against 0.159. So about 92%
of the measured detail is resolved structure, not sparkle. Exact along the ray is
not the same thing as antialiased across the pixel; the LOD rule is the prefilter.

**Variants, void L6 (msf / power above 1/8).**
- k = 1 default: 0.159 / 12.5%.
- Hard k = 0: 0.145 / 11.0%.
- Gaussian (erf): 0.174 / 14.2%, at 0.89x the polynomial cost here. The erf kernel is
  3x the polynomial kernel in isolation, but the kernel is not where the time goes.

## Images

All images are in Self's session scratchpad,
`C:\Users\Meta\AppData\Local\Temp\claude\F--Projects-CultLib\b7e842a1-2045-474e-a51b-240d832b770b\scratchpad\integration-domain-images\`:

- **Transmittance, 512 x 256.** Shown as `sqrt(1 − T)`, white for opaque.
  - `void-T-shipped-ref.png`, `void-T-shipped-march.png`.
  - `void-T-IN-L1.png`, `void-T-IN-L3.png`, `void-T-IN-L6.png`,
    `void-T-IN-L6-hard.png`, `void-T-IN-L6-gauss.png`.
  - The wells equivalents, which are silhouettes and uninformative.
- **Single scattering, 256 x 128.**
  - Method: HG g = 0.6 and albedo 1. The point light is at the void's sun with 1/r²
    falloff; the wells use a directional sun with a sky background.
  - Each view sample takes a shadow ray: a 2-world march for the shipped field, an
    exact traversal for the integration-native field.
  - Tonemap: `1 − exp(−1.5 L / p99(shipped))`, then gamma 2.2, the same exposure for
    both fields.
  - `void-SS-shipped.png`, `void-SS-IN-L6.png`, `wells-SS-shipped.png`,
    `wells-SS-IN-L6.png`.

What they show:
- The shipped void is a soft, low-contrast haze.
- The integration-native void is dense flow-aligned streaks with deep gaps, like
  hair or brush strokes.
- The shipped wells are smooth rolling dunes. The integration-native wells are
  layered pancakes with streaked detail.

## The catches

1. **The character is a design parameter, and this probe picked one.**
   - High anisotropy, axis-chained children and an intermittent `u²` split give
     fibres.
   - A cloudier look would need rounder children, wider offsets and less
     intermittency. That trades away some measured detail and was not swept.
   - The detail metrics measure spatial frequency, not taste. The operator has to
     look at the images.
2. **The cost is a BVH, not arithmetic.**
   - Integration is nearly free. Traversal, child generation and placement dominate.
   - Tighter root bounds (OBB tests before AABB bins), cheaper placement and fewer
     children are obvious levers, but none was measured.
   - On a GPU, variable-length kernel lists per ray diverge. 3DGRT (Moënne-Loccoz et
     al. 2024 [memory]) puts the same problem on hardware ray tracing.
   - CPU C# ratios do not transfer to the GPU, where snoise is relatively cheaper.
3. **It is all or nothing.**
   - This field cannot be marched. At the shipped step, |ΔT| is a median 0.14.
   - Shadow rays, light probes and any sampler consumer need the exact traversal too.
     The single-scattering pass here cost an exact shadow traversal per view sample:
     257 s for both void SS images together, against 54 s for the whole 512 x 256
     shipped transmittance set.
   - Lighting has no closed form. The in-scatter integral is still quadrature. What
     the domain makes exact is every optical depth inside it. A per-kernel lighting
     cache would amortise shadows, but that is a different shading model (splat-like),
     and so a product fork.
4. **Exact along the ray, not across the pixel.** The LOD truncation is the
   prefilter. 8% of the measured detail at 1 spp is aliasing.
5. **The wells gain little** because the shipped wells fog is near-opaque. Its look
   is the surface silhouette, and that is set at root scale. The domain pays most
   where the medium is semi-transparent, as in the void.
6. **Timing noise.** Absolute costs vary up to 2x between runs on the shared host.
   The cost ratios above are within one run.

## What would change the verdict

- A sweep toward a cloud-like character: round children, Nc = 8, wider offsets,
  Gaussian kernels. It should be judged on the single-scattering images against the
  same cost.
- Tight root culling: an exact ellipsoid-versus-bin test, or roots stored by
  oriented bound. If roots visited fall from 93 to near the 8 hits, the void L3 cost
  would fall well under the march.
- A GPU port of one level-limited configuration, to replace the C# ratios with
  shader timings.
