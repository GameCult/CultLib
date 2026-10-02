# CultMath intervals and the gamecult.org ground: cut map

Status: Imagination pass 1, 2026-10-02. Nothing here is ruled except what the
operator said, quoted below. The campaign named here does not exist in the mind
yet; the Self admits it if the operator takes Q1. Where this map and the Body
disagree, the Body wins and this map is stale.

Pinned HEADs (every `file:line` below is against these):

| Repo | Ref | SHA |
| --- | --- | --- |
| CultLib | `main` | `c49c76ee` (CultMath 0.3.0 merged at `614fd445`) |
| gamecult-site | `main` | `535d170` (before `wash-home-only` lands) |
| Aetheria | local checkout | read only; `Volumetric.cginc`, `Raymarching/CloudShader.shader`, `Clouds.shader`, `Zone Display/VolumeCloudRenderer.cs` |

## The operator's words

1. "Can we replace the site's background with a fancy yet unobtrusive and
   inexpensive shader thing? It's odd that my main site wouldn't have that,
   considering my specialization. Would it be too much to have some optimized
   subset of Aetheria's volumetric clouds shader in there?"
2. After the Self proposed a quarter-resolution, 8–16 step, static-on-phones
   design: "map it as a cut that includes the GPU interval arithmetic we're
   borrowing from Fidget for a planned Asura campaign. I'd like this shader to
   be the proving ground for that - with the right math we can absolutely run
   this fullscreen on mobile, per pixel as long as the sample count is low and
   we're accumulating samples with TAA (Aetheria's TAA is honestly
   embarrassing and it still handles smooth 1080p clouds on my tired old
   1070)"

So: full screen, per pixel, on phones too; few samples per frame; TAA
accumulates them; interval arithmetic makes the few samples count. The
"static frame on phones" idea is overruled. The static frame survives only
under `prefers-reduced-motion`.

## Ownership

### Where the interval math lives

**CultLib, `packages/cultmath`.** This is the parked campaign's own first step
(`docs/cultmath-tape-target.md`, sketch step 1: "CultMath `interval` in C# and
HLSL with parity tests ... It stands alone, and is the first cut to take even
if the rest stays parked"). Nothing about that reasoning changed. The "planned
Asura campaign" in the operator's words is that document: the tapes campaign,
whose first consumer named there is Aetheria's volumetric raymarching. Asura
itself does not need intervals (`cultmath-tape-target.md:103`).

The site speaks WebGL2 GLSL ES 3.00, not HLSL. Three routes were weighed:

| Route | What | Why not |
| --- | --- | --- |
| A | Hand-port the needed functions to GLSL inside gamecult-site | A second noise authority with no parity test; the exact thing Aetheria's `GPU Noise` copies are (follow-up `aetheria-snoise-pin-bump`). |
| B | `dxc -spirv` then SPIRV-Cross to GLSL ES, using the existing `tools/compile-hlsl-spirv.ps1` chain | SPIRV-Cross emits one whole shader with mangled names; there is no library a site shader can call `cultmath_snoise` from. |
| C | **A textual lowering HLSL → GLSL in CultLib, with a complete, enumerated transformation list, the same discipline as the HLSL → C# mirror (`packages/cultmath/docs/design.md:92-134`)** | Chosen. |

Under C, `shaders/CultMath.glsl` is a committed generated artifact, like
`Swizzles.g.cs`. One implementation of the lowering lives in the test project,
a test pins the committed file equal to the lowering of `CultMath.hlsl`, and
the same test regenerates it when asked. GLSL's stricter typing (no implicit
`int`/`uint` → `float`, `step(vec4, float)` does not exist) is not patched in
the lowering: the HLSL is written in the common subset, which is legal HLSL
and bit-identical, and glslang compiles the lowered text in CultLib's tooling
so it stays there. Parity then runs in two legs, each with one owner:

- HLSL ⇔ C#: the existing bit-for-bit mirror test, automatic for every new
  function.
- GLSL ⇔ C#: a committed golden fixture (sample points and expected bit
  patterns, exported from C#) that the site's Hands evaluates on WebGL2 in the
  browser pane and reads back. CultLib has no GPU harness (follow-up
  `gpu-snoise-parity`); the browser pane is one. This cut does not close that
  follow-up, which asks for FXC-on-D3D11, but it gives it a second device.

The site vendors `CultMath.glsl` with a header naming the CultLib commit it was
copied from. The site is a xenos consumer of CultLib at that boundary; it does
not take a package.

### Campaign

Recommend a **new campaign, `cultmath-tapes`**, repos `GameCult/CultLib` and
`GameCult/gamecult-site`, target doc `docs/cultmath-tape-target.md` unparked
for step 1 only (Q1). Not `asura`: Asura's target names intervals as not
needed, and its invariants are about planets. Not `site-masthead`: that
campaign is one repo and its target lists "the shader (a separate campaign if
ruled)" under `not_in_scope` (`masthead-campaign.md:822`). The site cut cites
`site-masthead:ruling:operator-chromeless-column` and depends on
`wash-home-only` landing first, because it rewrites the same six lines.

### Owner, consumers, invariant

- Owner: CultMath owns interval arithmetic and the noise bounds, in C# and
  HLSL, with GLSL as a derived lowering. The site owns its density field, its
  march, its TAA and its budget controller. CSS tokens on `body` own the
  ground's colours, lights and intensity; the shader derives its uniforms from
  them.
- Consumers: the site ground shader now; Aetheria's nebula raymarch at its
  CultMath pin bump; the tapes campaign's HLSL interpreter later.
- Protected invariant, **`intervals-enclose`**: every interval function
  returns a `[lo, hi]` that contains `f(x)` for every `x` in its input
  interval or ball, in C#, HLSL and GLSL alike. A consumer that skips a region
  because `hi < cutoff` has skipped nothing that was there. This is what the
  site proves and what the tapes campaign inherits.

## The authority map (all three cuts)

- Owner: `packages/cultmath` for `iv_*` and `iv_snoise_ball`/`iv_fbm_ball`;
  `GlslLowering` (test project) for `shaders/CultMath.glsl`; `custom.scss`
  `body` tokens for ground colour, light colour/alpha/position/radius and
  `--gamecult-wash`; `ground.js` for scheduling, budget and TAA; the two site
  GLSL files for the field and the resolve.
- Inputs: CultMath reads nothing new. The lowering reads `CultMath.hlsl` and
  `CultMath.Phacelle.hlsl` only. `ground.js` reads the computed `body` tokens,
  `prefers-reduced-motion`, `visibilityState`, canvas size and the measured
  GPU frame time. The field reads uniforms only.
- Outputs: `CultMath.glsl`; the interval functions; one canvas under the
  page; a per-session measurement log (console, debug flag only).
- Derived state: the shader's palette, light geometry and intensity are
  derived from the CSS tokens; nothing in JS or GLSL holds a colour. The CSS
  wash is the same tokens rendered by CSS: it is the fallback, not a second
  ground. `CultMath.glsl` is derived from `CultMath.hlsl`; editing it by hand
  fails the pin test. The TAA history is a cache; a reset costs 32 frames and
  no correctness.
- Forbidden writers: no colour literal, radius or position in `ground.js` or
  the GLSL; no user-agent, platform or touch-point sniffing anywhere; no
  class toggled on `body` by script; no second wash token; no page rule
  setting a radial alpha (carried from `wash-home-only`); no GLSL edited by
  hand; no `iv_*` function without a C# twin; no second noise copy in the
  site.
- Shared paths: every ground on the site, the ritual essays included, paints
  through the same tokens, in CSS and in the shader. Every route to a
  reduced budget (slow device, hidden tab, reduced motion, missing WebGL2 or
  half-float) goes through the one controller in `ground.js`; there is no
  second "mobile" path.
- Deletion line: in the site, the six literal radial colours and the three
  literal gradient stops become token references in the same commit the
  tokens are declared. In CultLib nothing is deleted; the GLSL file is new and
  the interval file is new. The subtraction this buys is upstream: at
  Aetheria's pin bump, `Assets/Plugins/GPU Noise/*.cginc` can go, and the
  tapes campaign's step 1 is already landed when it unparks.

## The shader design

### Density field (site-owned, in `cloud.frag.glsl`)

The subset of Aetheria taken: `Volumetric.cginc`'s structure of a fill term
plus a coverage term shaped by noise, and `CloudShader.shader`'s transmittance
integration (`IntegrateRaymarch`, `:83-104`). Not taken: the height-field
textures, the fluid flow map, `triNoise3d`, the tint LOD lookup, the packed
depth, and the three-pass Unity plumbing. `Clouds.shader`'s `1-abs(snoise)`
envelope over an fBm is the shape of the detail term. The noise is CultMath
`cultmath_snoise` via the lowering, and nothing else.

```text
camera at the origin, looking +z, static; the slab z in [Z0, Z1] fills the view
p(t)      = dir * t + drift * time                 (drift slow: ~0.02 units/s)
coverage  = snoise(p * F0)                         (one octave, F0 ~ 0.35)
occupancy = max(0, coverage - CUTOFF) * K          (CUTOFF ~ 0.25: most space is empty)
detail    = 1 - abs(snoise(p * F0 * 4 + 17))       (one octave; the dune envelope)
density   = occupancy * mix(0.6, 1.0, detail)
colour    = sum_i light_i.rgb * light_i.alpha * falloff_i(screen uv)   (the three CSS lights)
```

Two `snoise` per dense sample, one per empty probe. The lights are the three
CSS radials with the same centre, radius and colour; a point in the volume is
"lit" by the radial falloff at its screen position, so the clouds are the wash
made structured, with no 3D lighting.

### Interval culling along the ray

`cultmath_iv_snoise_ball(c, r)` returns `[n(c) - L r, n(c) + L r] ∩ [-1, 1]`,
where `L` is CultMath's committed Lipschitz constant for `snoise(float3)`. Over
a ray segment `[t, t + Δ]` the ball is centred at `t + Δ/2` with radius `Δ/2`,
scaled by `F0`. The march is adaptive subdivision, which is Fidget's region
test on a 1-D region:

```text
Δ = Δ0; t = Z0/dir.z + jitter * Δ0
while t < t_exit and evals < S:
    iv = iv_snoise_ball(p(t + Δ/2) * F0, F0 * Δ/2)          -- 1 eval
    if iv.hi < CUTOFF:   t += Δ; Δ = min(Δ * 2, ΔMAX)      -- provably empty: skip, grow
    elif Δ > ΔDENSE:     Δ = Δ / 2                           -- maybe dense: refine
    else:                integrate density(p(t + Δ/2)) over Δ; t += Δ    -- 1 more eval
    if transmittance < 0.02: break
if t < t_exit: integrate one coarse sample over the rest     -- the budget ran out; measured, must be rare
```

The `iv.hi < CUTOFF` test is the interval proof that the `0` branch of
`max(0, coverage - CUTOFF)` wins on the whole segment: Fidget's `min`/`max`
pruning rule at its smallest, applied to a two-clause expression by hand. Tape
and automatic pruning stay parked; this cut proves the arithmetic they rest on.

### Step budget

`S` is the per-pixel, per-frame evaluation budget (each `snoise` call is one
eval, so a dense sample costs 2). `S = 16` on desktop, `S = 8` on a phone,
`S = 4` the floor; the controller sets it from measurement, not from the
device. A debug mode writes `evals / S` and an "unfinished ray" flag into the
colour so Hands can read back the mean and the unfinished fraction. The
contract: at the chosen `S` on the shipped field, under 1% of rays are
unfinished.

### TAA

The camera never moves and the canvas is fixed, so there is no reprojection:
history is the same pixel. What remains is temporal supersampling with
rejection:

- Jitter: a Halton(2,3) frame offset for the ray start, plus interleaved
  gradient noise per pixel, rotated per frame, so neighbours decorrelate.
- Accumulation: `α = max(1 / frames, 1/32)`: the first frames converge fast,
  then settle. The accumulated value is colour plus transmittance.
- Rejection: a 3×3 min/max box of the current frame clamps the history
  (AABB clamp, not variance clipping). The field's drift is slow enough that
  the clamp only bites where the field really changed.
- Reset on resize, on a token change (route change: Quartz has `enableSPA:
  false` at `quartz.config.ts:15`, so a route change is a page load and the
  reset is free) and on the first frame.
- Buffers: two RGBA16F history targets at canvas size. 8-bit history stalls
  convergence at `α = 1/32` for a ground this dark, so half-float is required;
  if `EXT_color_buffer_half_float` is missing, the controller falls back to
  the CSS wash.

What is not copied from Aetheria's `CloudShader.shader` pass 2: variance
clipping at `γ = 0.5` on an undersampled input (`:303-308`), a fixed 5% blend
with out-of-bounds forced to 100% (`:311`), the packed depth/density float
(`:181`, `:270`), and the bug at `:311`, which blends the alpha with
`density2`, the last 3×3 neighbour's density from the loop above, not the
pixel's own.

### Resolution

Full device pixels: `canvas.width = innerWidth * devicePixelRatio`. The
controller's steps are in order: lower `S` (16 → 8 → 4), then render at CSS
pixels (`dpr = 1`), then the CSS wash. The second step is Q2.

### Palette and intensity

Fifteen tokens on `body` in `custom.scss`, read by CSS and by `ground.js`:

```text
--gamecult-ground-0/1/2         three gradient stop colours (#03070d #07111a #09141f)
--gamecult-light-N              "255 138 42"  (N = 0..2)
--gamecult-light-N-a            0.18
--gamecult-light-N-at           "78% 14%"
--gamecult-light-N-r            "18%"   (or a length; the ritual ground uses rem)
--gamecult-wash                 from wash-home-only: 1 on /, 0.5 elsewhere
```

The CSS radials and the linear gradient reference the tokens. The ritual essays'
block overrides the tokens instead of painting its own ground, which deletes
its three literal radials. `ground.js` resolves `%` radii as CSS does (circle,
farthest corner of the body's background positioning area) and lengths through
the root font size. The parity test: with density forced to 0 the canvas equals
the CSS wash within 2/255 per channel at 16 probe points, home and ritual.
Intensity multiplies the cloud term; at 0 both paths show the plain gradient.

### Pausing and fallbacks

- `visibilityState !== "visible"`: no frames. On return, keep history.
- `prefers-reduced-motion: reduce`: drift is 0, render until 32 accumulated
  frames, then stop; resize re-renders. The clouds are still there, still.
- No WebGL2, context lost, no half-float: canvas removed, CSS wash shows.
- Too slow: measured, see below. Never a user-agent branch.
- The canvas is `position: fixed; inset: 0; z-index: -1; pointer-events: none;
  aria-hidden`. The root background paints first, so the canvas covers the CSS
  wash only once it has drawn; until then, and if it never does, the wash is
  what the reader sees.

### Text contrast

The shader clamps its output so relative luminance never exceeds 0.035, about
the current orange hotspot (`#07111a` plus `rgba(255,138,42,.18)` is roughly
`(53, 42, 34)`, luminance 0.025). Body text `#b7c7d9` (luminance 0.55) keeps a
contrast ratio of at least 7:1 against any ground pixel. A readback test
asserts the max over the canvas after 32 frames on `/` at intensity 1.

## Budget and how it is measured

| Device | Pixels | GPU per frame at 30 fps | Proxy |
| --- | --- | --- | --- |
| Desktop, GTX 1070 class, 2560×1440 | 3.7 M | ≤ 1.0 ms | 3% GPU duty |
| Laptop iGPU, Iris Xe, 1920×1080 at dpr 1.25 | 3.2 M | ≤ 3.0 ms | 9% duty |
| Mid-range phone, Pixel 6a / Galaxy A54 class, 1080×2400 | 2.6 M | ≤ 4.0 ms | ≤ 12% duty; no upward drift of the median over 5 minutes (a thermal detector by measurement, not a sensor) |

The cost model that sizes `S`: one 3D simplex is ~150 flops; a phone GPU
delivers ~1 TFLOP; 2.6 M × 8 evals × 150 is 3.1 GFLOP, about 3 ms, plus ~1 ms
for the resolve pass (10 texel reads per pixel). Those are estimates; the
measurements replace them.

Measurement, in this order of preference, all in `ground.js` behind a debug
flag and used by the controller:

1. `EXT_disjoint_timer_query_webgl2` where present (Chrome desktop):
   per-frame GPU time of the march pass and the resolve pass.
2. A `fenceSync` after the resolve pass, polled; the time from issue to
   signalled is the GPU time when the queue is otherwise empty, which a
   background page's queue is. Available on every WebGL2, including Safari
   and phones.
3. `requestAnimationFrame` cadence as the last signal: if frames are
   delivered under 24 fps for 30 consecutive frames while the page is idle,
   the controller steps down.

Hands proves it in the browser pane: desktop numbers from the pane with the
timer query, phone numbers from the pane's mobile viewport emulation for the
pixel count and from the operator's phone for the real GPU time (operator
verification). Each landed number goes into the cut report, not into prose.

## The proving-ground contract (what Asura and the tapes campaign inherit)

1. **Enclosure tests in C#** (and so in HLSL by the mirror): for every `iv_*`
   op, random intervals and random points inside them, `f(x) ∈ op(I)`. For
   `iv_snoise_ball` and `iv_fbm_ball`, 2,000 random balls with 64 points each.
   A mutant that shrinks any bound dies.
2. **The Lipschitz constant has provenance**: `CULTMATH_SNOISE_LIPSCHITZ` is
   the max of `|snoise_grad|` over 10⁶ random points refined by gradient
   ascent, times a stated margin (1.10). The test that measures it is
   committed and the constant is pinned by a test that fails if any sampled
   gradient exceeds it. It is empirical with a margin, not a proof; the map
   says so, and the enclosure tests are the defence.
3. **Tightness is a number**: the mean ratio of interval width to the true
   range over the sampled balls, printed by a committed test and quoted in
   the cut report, so a later affine-arithmetic cut has something to beat.
4. **The saving is a number**: a C# test marches 1,000 random rays through a
   representative coverage field with and without interval skipping and
   asserts at least a 2× reduction in evaluations; the real field's mean
   evals per pixel and unfinished-ray fraction come from the browser readback.
5. **Three runtimes agree**: the GLSL golden fixture (256 points, bit
   patterns from C#) passes on WebGL2 within 1 ulp for the integer-hash paths
   and a stated tolerance for the transcendental ones, with the tolerance and
   the device named in the report.

What this cut does **not** prove, said plainly: tape evaluation, automatic
pruning, the `min`/`max` choice tracking, and interval ops over anything but
plain `[lo, hi]`. Those unpark with steps 2–4.

## Cut order

```text
glsl-lowering (CultLib) ─┐
interval-ops  (CultLib) ─┴─> site-ground (gamecult-site, after wash-home-only)
```

Three cuts, not one. One cut would be three repos' worth of concerns and
three Soul vocabularies (a text transformer, numerical bounds, a browser
budget); CultMath cuts have overrun 2× when they carried two concerns
(`Asura cut-map.md:131`). `glsl-lowering` and `interval-ops` are independent
and can run in parallel worktrees; `interval-ops` gets its GLSL for free from
the lowering test once both are on `main`. No CultMath release is in this
sequence: the site consumes a commit, not a package. The next CultMath release
carries the intervals to Unity consumers.

Specs: `docs/cultmath-interval-ground-cut-glsl-lowering.spec.json`,
`docs/cultmath-interval-ground-cut-interval-ops.spec.json`,
`docs/cultmath-interval-ground-cut-site-ground.spec.json`.

## Standing design decisions (means; Self may overrule)

- D1. Intervals are `float2(lo, hi)`. Affine arithmetic is not taken; the
  tightness number from the contract says whether it is ever worth it.
- D2. No `iv_*` function has a value-only or gradient twin. The bounds take
  the point value only; `snoise_grad` is used by the Lipschitz test, not by
  the bound.
- D3. The site vendors `CultMath.glsl` by CultLib commit, with the SHA in the
  file header and a check in the site cut that the bytes equal CultLib's at
  that SHA. A build-time fetch would make the site's build depend on another
  repo being reachable.
- D4. The ground shader is opaque and reproduces the gradient and the lights
  from the tokens. A transparent canvas over the CSS wash would need the CSS
  radials hidden by a class (two ground states, a flash on load), and parsing
  the computed `background-image` string would make the palette authority a
  serializer's whim.
- D5. The history reset on token change is a page load, because SPA is off. If
  SPA is ever enabled, `ground.js` listens for Quartz's `nav` event and
  re-reads the tokens; the spec names the hook so it is not forgotten.
- D6. The budget controller holds state for the session only (no storage).
  Measuring costs under a second and a stored verdict would outlive a driver
  update.

## Questions

**Q1. Campaign home.**
- (a) New campaign `cultmath-tapes`, target `docs/cultmath-tape-target.md`
  unparked for step 1, repos CultLib and gamecult-site. **Recommended.** It is
  the campaign the operator called "planned", and the site is its first
  consumer.
- (b) File the CultLib cuts under `asura` as 2a-iii and the site cut under
  `site-masthead`. Rejected: both targets say this work is not theirs.

**Q2. The second step of the slow-device ladder.**
- (a) After `S` hits its floor, render at CSS pixels (`dpr = 1`) before giving
  up to the CSS wash. **Recommended.** It is still a measured response, and a
  2× pixel cut is a 2× GPU cut, which keeps clouds on more phones.
- (b) Never drop below device pixels; a device that fails at `S = 4` gets the
  CSS wash. Purer to the order, but it loses the phones one notch too slow.

**Q3. Ground authority in CSS.**
- (a) Fifteen tokens on `body`, referenced by the CSS gradients and read by
  the shader; the ritual block overrides tokens. **Recommended.** One
  definition of the ground, two renderers.
- (b) Keep the literals in CSS and duplicate the palette in `ground.js`.
  Rejected: the brand doc says the brand is defined in `custom.scss`; a copy
  in JS is a second authority that drifts.

## Unsettled

- The Lipschitz constant is empirical with a margin. If a reader wants a
  proof, the Ashima kernel's gradient is a bounded polynomial per simplex
  cell and a proof is possible; it is not in this cut.
- The ritual ground's radii are `rem` lengths and its gradient has three
  stops but the first has no position (`custom.scss:124`). Hands normalises
  it to three positioned stops when tokenising; the appearance is unchanged.
- Whether GameCult-Quartz's bundler lets a component's `afterDOMLoaded` do a
  dynamic `import()` of a static module, or whether the loader must be a
  classic script tag in `Head`. Hands reads the engine; both are one line.
