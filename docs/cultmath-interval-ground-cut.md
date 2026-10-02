# CultMath intervals and the gamecult.org ground: cut map

Status: Imagination pass 2, 2026-10-02. Pass 1's Q1–Q3 are answered (below);
the Self opens the `cultmath-tapes` campaign from this revision. Where this
map and the Body disagree, the Body wins and this map is stale.

Pinned HEADs (every `file:line` below is against these):

| Repo | Ref | SHA |
| --- | --- | --- |
| CultLib | `main` | `c49c76ee` (CultMath 0.3.0 merged at `614fd445`) |
| gamecult-site | `main` | `535d170` (before `wash-home-only` lands) |
| Aetheria | local checkout | read only; `Volumetric.cginc`, `Raymarching/CloudShader.shader`, `Compute/Stardust/Stardust.{compute,shader,cs}`, `CustomDoF/DepthOfField.hlsl`, `Zone Display/VolumeCloudRenderer.cs` |

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
3. On pass 1's Q3 (ground tokens as the shader's input): "I like that, but the
   background should be nice and dynamic and we should sample the wash from
   the result instead. Is there any point sending you video? I don't think
   your vision model has anything in it to encode motion. Anyway, this video
   really blew people away, with its combination of the clouds, the flowing
   stardust and my hacked DoF shader where I replaced the circle of confusion
   calculation with an output from the raymarching shader, I think it was
   final transmittance? Doesn't matter, looks great to have the clouds blur
   what's behind them. This does remind me we're totally gonna need that
   stardust too, it really sells the motion of the nebulae"

Rulings from pass 1: **Q1** new campaign `cultmath-tapes`. **Q2** the slow
ladder is samples, then resolution, then the CSS wash. **Q3** answered outside
the options: the shader owns the ground and it moves; the CSS wash is sampled
from the shader's result; the ground tokens are seed and fallback only, never
the shader's input; stardust and the raymarch-driven DoF are in scope.

## Ownership

### Where the interval math lives

**CultLib, `packages/cultmath`.** This is the parked campaign's own first step
(`docs/cultmath-tape-target.md`, sketch step 1: "CultMath `interval` in C# and
HLSL with parity tests ... It stands alone, and is the first cut to take even
if the rest stays parked"). The "planned Asura campaign" in the operator's
words is that document; its first named consumer is Aetheria's volumetric
raymarching, and Asura itself does not need intervals
(`cultmath-tape-target.md:103`).

The site speaks WebGL2 GLSL ES 3.00. Three routes were weighed:

| Route | What | Why not |
| --- | --- | --- |
| A | Hand-port the needed functions to GLSL inside gamecult-site | A second noise authority with no parity test; the exact thing Aetheria's `GPU Noise` copies are (follow-up `aetheria-snoise-pin-bump`). |
| B | `dxc -spirv` then SPIRV-Cross to GLSL ES, using the existing `tools/compile-hlsl-spirv.ps1` chain | SPIRV-Cross emits one whole shader with mangled names; there is no library a site shader can call `cultmath_snoise` from. |
| C | **A textual lowering HLSL → GLSL in CultLib with a complete, enumerated transformation list, the discipline of the HLSL → C# mirror (`packages/cultmath/docs/design.md:92-134`)** | Chosen. |

Under C, `shaders/CultMath.glsl` is a committed generated artifact, like
`Swizzles.g.cs`. One implementation of the lowering lives in the test project;
a test pins the committed file equal to the lowering of `CultMath.hlsl` and
regenerates it when asked. GLSL's stricter typing is not patched in the
lowering: the HLSL is written in the common subset (legal, bit-identical) and
glslang compiles the lowered text in CultLib's tooling. Parity runs in two
legs, each with one owner: HLSL ⇔ C# by the existing bit-for-bit mirror;
GLSL ⇔ C# by a committed golden fixture evaluated on WebGL2 in the browser
pane and read back. CultLib has no GPU harness (follow-up
`gpu-snoise-parity`); the browser pane is one. The site vendors
`CultMath.glsl` with the CultLib commit in its header.

### Campaign

`cultmath-tapes` (Q1), repos `GameCult/CultLib` and `GameCult/gamecult-site`,
target doc `docs/cultmath-tape-target.md` unparked for step 1 only. The site
cuts cite `site-masthead:ruling:operator-chromeless-column` and depend on
`wash-home-only` landing first, because they rewrite the same lines.

### Owner, consumers, invariant

- Owner: CultMath owns interval arithmetic and the noise bounds in C# and HLSL,
  GLSL derived. The site owns its field, flow, march, TAA, stardust, DoF and
  budget controller. `custom.scss` owns the brand palette the shader reads
  (four nebula tokens) and the seed values of the ground tokens. `ground.js`
  owns the sampled ground tokens while it runs.
- Consumers: the site ground now; Aetheria's nebula raymarch at its CultMath
  pin bump; the tapes campaign's HLSL interpreter later.
- Protected invariant, **`intervals-enclose`**: every interval function returns
  a `[lo, hi]` containing `f(x)` for every `x` in its input region, in C#, HLSL
  and GLSL alike. A consumer that skips a region because `hi < cutoff` has
  skipped nothing that was there. The domain warp does not weaken it: a warped
  point lies in the ball enlarged by the warp bound, and a test says so.

## The authority map (all four cuts)

- Owner: `packages/cultmath` for `iv_*` and the ball bounds; `GlslLowering`
  (test project) for `shaders/CultMath.glsl`; `custom.scss` for the four
  nebula input tokens, `--gamecult-wash`, and the seed values of the fifteen
  ground tokens; `ground.js` for the flow texture, scheduling, budget, TAA,
  the stardust and DoF passes, and the sampled ground tokens; the site GLSL
  files for the field, the particles and the resolve.
- Inputs: CultMath reads nothing new. The lowering reads the two HLSL files.
  `ground.js` reads the nebula tokens, `--gamecult-wash`,
  `prefers-reduced-motion`, `visibilityState`, canvas size, device pixel
  ratio, the measured GPU frame time and its own 8×8 readback. The field
  reads uniforms and the flow texture only.
- Outputs: `CultMath.glsl`; the interval functions; one canvas under the
  page; the fifteen ground tokens written on `:root` from the readback; a
  debug log behind a flag.
- Derived state: the ground tokens are derived from the shader's result while
  it runs and are seed values otherwise; the CSS wash renders them; nothing
  reads them back into the shader. The flow texture is the one owner of
  motion: the warp, the TAA motion vectors and the stardust all read it.
  `CultMath.glsl` is derived from `CultMath.hlsl`. The TAA history is a cache.
  `S`, `dpr` and the particle count are derived from measurement within the
  session.
- Forbidden writers: no colour literal in `ground.js` or any GLSL; the shader
  never reads a ground token; `ground.js` never reads the ground tokens except
  to seed before its first readback; no user-agent, platform or touch-point
  sniffing; no class on `body` or `html` by script; no second wash token; no
  second flow function (particles and clouds read one texture); no GLSL edited
  by hand; no `iv_*` without a C# twin; no noise defined in the site.
- Shared paths: every page's ground, the ritual essays included, is one
  canvas and one token set, in CSS and in the shader. Every reduced budget
  (slow device, hidden tab, reduced motion, missing WebGL2 or half-float) goes
  through the one controller. The 8×8 readback is the one path from the
  shader to CSS.
- Deletion line: in the site, the six literal radial colours and three
  literal stops become token references and the ritual block's own ground
  goes, in the same commit the tokens are declared. In CultLib nothing is
  deleted. The subtraction this buys is upstream: Aetheria's
  `Assets/Plugins/GPU Noise/*.cginc` at its pin bump, and the tapes campaign's
  step 1 already landed.

## The shader design

### What Aetheria contributes, and what the web version keeps

| Aetheria | Mechanism | Web subset |
| --- | --- | --- |
| `Volumetric.cginc:165-221` density | fill + coverage, domain-warped by `flow()` in two half-period phases cross-faded with `tri2` (a flow map; the advection never accumulates) | kept, one coverage octave and one detail octave, CultMath noise |
| `Volumetric.cginc:130-135` `globalFlow` | `Tri3D(p/scale − scroll) · amplitude` | replaced by two `snoise` at low frequency, rendered once per frame into a quarter-res flow texture |
| `CloudShader.shader:83-104` | transmittance integration | kept |
| `CloudShader.shader:261-314` TAA | reprojection by `_PrevVP`, γ=0.5 variance clip, fixed 5% blend, packed depth, the `density2` bug at `:311` | not kept; see TAA |
| `Stardust.compute:118-147` | stateless particles: grid cell hashed to a spawn point, `position −= flow · lifetime · period`, size `parabola(lifetime, 2)`, colour from a hue ramp × nebula tint | kept as an instanced vertex shader reading the flow texture; `gl_InstanceID` is the cell |
| `DepthOfField.hlsl:29-32` `FragCoC` | the CoC is read straight from a global `_DoFBlurTex` (its writer is not in this checkout; the operator recalls final transmittance), then the bokeh gather `FragBlur` `:150-201` | a gather on the stardust layer with radius ∝ cloud opacity at that pixel |

### Density field (site-owned, `cloud.frag.glsl`)

```text
camera at the origin, looking +z, static; the slab z in [Z0, Z1] fills the view
flow(uv)     = A · (snoise(q·Ff + o1), snoise(q·Ff + o2), 0)   q = slab mid-plane point; quarter-res texture, once per frame
phase_k(t)   = fract(t / PERIOD + k/2),  w_k = tri2(phase_k)        k = 0,1
warp_k(p)    = p + flow · (phase_k − 0.5) · PERIOD
coverage(p)  = Σ_k w_k · snoise(warp_k(p) · F0)                     two snoise
occupancy    = max(0, coverage − CUTOFF) · K
detail(p)    = 1 − |snoise(p · 4F0 + 17)|                           one snoise; the dune envelope
density      = occupancy · mix(0.6, 1.0, detail)
hue(uv)      = mix of the three nebula colours by flow.xy / A       free: the flow texture's own channels
colour       = hue · intensity
```

Three `snoise` per dense sample, one per empty probe. The pattern moves at
velocity `−flow` everywhere, because both phases advance at the same rate;
the cross-fade only changes content. That is what makes the motion vectors
below exact.

### Interval culling along the ray, with the warp

`cultmath_iv_snoise_ball(c, r)` returns `[n(c) − L r, n(c) + L r] ∩ [−1, 1]`
with `L` the committed Lipschitz constant. Over a segment `[t, t+Δ]` the
unwarped ball is centred at `t + Δ/2` with radius `Δ/2`; the warp displaces
any point by at most `D = |flow|max · PERIOD / 2`, so the ball `(c, Δ/2 + D)`
contains both phases' warped points, and one evaluation bounds the whole
coverage term. The march is adaptive subdivision, Fidget's region test on a
1-D region:

```text
Δ = Δ0; t = Z0/dir.z + jitter · Δ0
while t < t_exit and evals < S:
    iv = iv_snoise_ball(p(t + Δ/2) · F0, F0 · (Δ/2 + D))            1 eval
    if iv.hi < CUTOFF:   t += Δ; Δ = min(2Δ, ΔMAX)                  provably empty: skip, grow
    elif Δ > ΔDENSE:     Δ = Δ / 2                                   maybe dense: refine
    else:                integrate density(p(t + Δ/2)) over Δ; t += Δ    3 evals
    if transmittance < 0.02: break
if t < t_exit: integrate one coarse sample over the rest             the budget ran out; measured, must be rare
```

`iv.hi < CUTOFF` is the interval proof that the `0` branch of
`max(0, coverage − CUTOFF)` wins on the whole segment: Fidget's `min`/`max`
pruning at its smallest, applied by hand to a two-clause expression. Tape and
automatic pruning stay parked; this proves the arithmetic they rest on.

### Step budget

`S` counts `snoise` evaluations per pixel per frame. `S = 16` desktop, `8`
phone, `4` floor; the controller sets it from measurement. A debug mode writes
`evals / S` and an unfinished flag into the colour. Contract: under 1% of rays
unfinished at the chosen `S` on the shipped field.

### TAA, motion-aware

The camera is static but the field moves, so history is reprojected by the
known motion, which the flow texture owns:

- Motion vector: `mv = project(−flow(uv) · dt)` in pixels; history is fetched
  at `uv − mv`. Exact for the pattern between cross-fades (see above); the
  cross-fade and the detail term are handled by rejection.
- Jitter: Halton(2,3) frame offset on the ray start plus interleaved gradient
  noise per pixel, rotated per frame.
- Accumulation: `α = max(1/frames, 1/32)` on colour and transmittance.
- Rejection: a 3×3 min/max box of the current frame clamps the reprojected
  history (AABB clamp, not variance clipping); out-of-bounds fetches take
  `α = 1`.
- Reset on resize, on the first frame and on a budget step.
- Buffers: two RGBA16F history targets at canvas size (8-bit stalls
  convergence at `α = 1/32` on a ground this dark); missing half-float falls
  back to the CSS wash through the controller.

### Stardust (cut `site-stardust`)

Stateless, as Aetheria's: instance `i` is a cell of an `N×N` grid over the
slab's mid-plane, hashed by `cultmath_pcg3d` to a spawn point and a lifetime
offset; `lifetime = fract(t/PERIOD_P + offset)`; position = spawn − flow(uv
of spawn) · lifetime · PERIOD_P, read from the flow texture in the vertex
shader; size = `parabola(lifetime, 2)` × a hashed size; colour = the nebula
hue at its screen position × a hashed brightness. Rendered as instanced
billboards (one 6-vertex quad, `gl_InstanceID`) with additive blending into a
half-resolution RGBA16F stardust layer, with the `powerPulse` falloff of
`Stardust.shader:74-78`. Count: 16,384 desktop, 4,096 phone, controller-scaled
with `S`. The particles and the clouds move as one because they read the same
texture; there is no second flow.

### Depth of field driven by the raymarch (cut `site-stardust`)

Aetheria's hack: the CoC is a raymarch output, so clouds blur what is behind
them. Web version: in the resolve pass, the stardust layer is gathered with an
8-tap Poisson disk whose radius is `RMAX · opacity(uv)`, where
`opacity = 1 − T` is the TAA-accumulated cloud transmittance at that pixel
(the stable, converged value, which is why it is read from history and not
from the current frame). Composite: `ground + clouds + stardust_blurred · T`.
All stardust is treated as behind the whole cloud layer; a per-depth
transmittance would cost a second march and is not in scope.

### Sampled wash: the CSS reads the shader

After the resolve, a tiny pass downsamples the final frame to 8×8 (RGBA8).
`ground.js` reads it back asynchronously: `readPixels` into a
`PIXEL_PACK_BUFFER`, a `fenceSync`, then `getBufferSubData` on a later frame
once the fence has signalled. Never a synchronous readback. At most twice a
second, and only written when a value moved by more than 1/255:

- `--gamecult-ground-0/1/2`: the mean of rows 0–1, 3–4 and 6–7.
- `--gamecult-light-N`, `-a`: the colour at the seed light's centre texel,
  split into the hue (normalised) and the excess over the ground stop there.
- `--gamecult-light-N-at`, `-r`: the seed values; positions are not fitted.
- `--gamecult-ground-mean`: the 8×8 mean, for any tinted UI.

These are written to `:root`'s inline style, so they beat the stylesheet's
seed declarations. The seeds live on `:root` in `custom.scss` (today's
literal wash), with the ritual essays' seeds under
`:root:has(body[data-slug="Blog/..."])`. The body background and the ritual
ground read the tokens. When WebGL2 is absent or the controller removes the
canvas, the seeds are what paints. Across page loads the last sample may be
carried as a cache (Q5).

### Palette input

The shader reads four input tokens from `custom.scss`, which is where the
brand says colour is defined: `--gamecult-nebula-0/1/2` (today orange
`255 138 42`, violet `109 96 255`, sky `89 183 255`) and
`--gamecult-nebula-base` (`#07111a`), plus `--gamecult-wash` as intensity.
Inputs and outputs are different tokens, so there is no cycle (Q4).

### Resolution and the slow ladder (Q2)

Full device pixels. The controller's ladder: `S` 16 → 8 → 4, then `dpr → 1`
(the stardust layer and DoF follow the canvas), then the CSS wash. Never a
step up within a session.

### Pausing and fallbacks

- `visibilityState !== "visible"`: no frames; on return, history is kept and
  the motion vector uses the real elapsed `dt`, clamped to one frame.
- `prefers-reduced-motion: reduce`: time is frozen (phase, flow and particle
  lifetimes fixed), render until 32 accumulated frames, stop; resize
  re-renders. The clouds and stardust are there, still.
- No WebGL2, context lost, no half-float: canvas removed; the seeds paint.
- Too slow: measured; see below.
- The canvas is `position: fixed; inset: 0; z-index: -1; pointer-events:
  none; aria-hidden`. The root background paints first, so the canvas covers
  the CSS wash only once drawn.

### Text contrast

The resolve clamps relative luminance at 0.035 (about today's orange hotspot,
`#07111a` + `rgba(255,138,42,.18)` ≈ `(53, 42, 34)`, luminance 0.025), after
stardust and DoF are composited. Body text `#b7c7d9` (0.55) keeps ≥ 7:1 on any
ground pixel. A readback test asserts the max after 32 frames on `/`.

## Budget and how it is measured

| Device | Pixels | GPU per frame at 30 fps | Proxy |
| --- | --- | --- | --- |
| Desktop, GTX 1070 class, 2560×1440 | 3.7 M | ≤ 2.0 ms | 6% GPU duty |
| Laptop iGPU, Iris Xe, 1920×1080 at dpr 1.25 | 3.2 M | ≤ 3.5 ms | 10% duty |
| Mid-range phone, Pixel 6a / Galaxy A54 class, 1080×2400 | 2.6 M | ≤ 4.0 ms | ≤ 12% duty; no upward drift of the median over 5 minutes |

Cost model for the full stack on the phone (one `snoise` ≈ 150 flops, ~1
TFLOP available; estimates, replaced by measurement):

| Pass | Work | ms |
| --- | --- | --- |
| Flow texture, quarter res | 160 k texels × 2 snoise | 0.05 |
| March at `S = 8` | 2.6 M × 8 snoise | 3.1 |
| TAA resolve + DoF gather | ~19 texel reads per pixel | 1.0 |
| Stardust layer, half res, 4,096 particles | fill-bound, small | 0.2 |
| 8×8 downsample + PBO readback at 2 Hz | tiny | 0.05 |
| **Total at `S = 8`** | | **≈ 4.4** |
| Total at `S = 6` / `S = 4` | | ≈ 3.6 / 2.9 |

Said plainly: at `S = 8` the full stack does not fit the 4 ms phone budget on
the estimate. The controller will settle a mid-range phone at `S = 4–6`,
which the TAA covers (convergence in ~32 frames, about a second). What gives,
in order, is the sample count, then the resolution; the stardust and the DoF
are never dropped before the clouds' samples, because they cost a quarter of
the march. On desktop at `S = 16` the stack is ≈ 1.6 ms.

Measurement, in this order, all in `ground.js` behind a debug flag and used by
the controller: `EXT_disjoint_timer_query_webgl2` where present (Chrome
desktop) per pass; else a `fenceSync` after the resolve, polled (universal on
WebGL2, phones and Safari included; the queue of a background page is
otherwise empty); else `requestAnimationFrame` cadence. Hands proves it in the
browser pane: desktop numbers with the timer query, the phone's pixel count
from the mobile preset, the phone's GPU time from the operator's phone.

## The proving-ground contract (what the tapes campaign inherits)

1. **Enclosure tests in C#** (and HLSL by the mirror): every `iv_*` op over
   random intervals and points; `iv_snoise_ball`/`iv_fbm_ball` over 2,000
   balls × 64 points. A mutant that shrinks any bound dies.
2. **The warp is enclosed**: for random flows bounded by `D`, every point
   `warp_k(p)` over the segment lies in the enlarged ball and `snoise` of it
   lies in `iv_snoise_ball(c, r + D)`. This is the test the site's march rests
   on.
3. **The Lipschitz constant has provenance**: the max of `|snoise_grad|` over
   10⁶ points refined by gradient ascent, × 1.10, measured by a committed
   test and pinned by another. Empirical with a margin, not a proof; the
   enclosure tests are the defence.
4. **Tightness is a number**: mean interval width over true range, printed
   by a committed test, for a later affine cut to beat.
5. **The saving is a number**: a C# test marches 1,000 rays through a
   representative warped coverage field with and without interval skipping
   and asserts ≥ 2× fewer evaluations at equal transmittance; the shipped
   field's evals per pixel and unfinished fraction come from the browser.
6. **Three runtimes agree**: the GLSL golden fixture passes on WebGL2, with
   tolerance class and device named in the report.

Not proved here, said plainly: tape evaluation, automatic pruning, `min`/`max`
choice tracking, affine arithmetic. Those unpark with steps 2–4.

## Cut order

```text
glsl-lowering (CultLib) ─┐
interval-ops  (CultLib) ─┴─> site-ground (clouds, flow, TAA, sampled wash) ─> site-stardust (particles, DoF)
                                  ^ after site-masthead wash-home-only
```

Four cuts. The two CultLib cuts are as in pass 1, except `interval-ops` gains
the warp enclosure test. `site-ground` grew (flow texture, reprojection, the
readback and token restructure); `site-stardust` is split out because its
Soul vocabulary is different (particle statistics, blur quality, the composite)
and because the ground must be measured on its own before the stardust is
charged against the same budget. No CultMath release is in the sequence; the
site consumes a commit.

Specs: `docs/cultmath-interval-ground-cut-glsl-lowering.spec.json`,
`docs/cultmath-interval-ground-cut-interval-ops.spec.json`,
`docs/cultmath-interval-ground-cut-site-ground.spec.json`,
`docs/cultmath-interval-ground-cut-site-stardust.spec.json`.

## Standing design decisions (means; Self may overrule)

- D1. Intervals are `float2(lo, hi)`. Affine arithmetic is not taken; the
  tightness number says whether it is ever worth it.
- D2. No `iv_*` function has a value-only or gradient twin; the bounds take
  the point value only.
- D3. The site vendors `CultMath.glsl` by CultLib commit, SHA in the header, a
  drift check in the site cut.
- D4. The flow texture is the one owner of motion. Warp, motion vectors, hue
  and particles read it; nothing evaluates flow a second way.
- D5. Routes are page loads (`enableSPA: false`, `quartz.config.ts:15`); if
  SPA is ever enabled, `ground.js` listens for Quartz's `nav` event and
  re-reads the input tokens.
- D6. The budget verdict is session-only; a stored verdict would outlive a
  driver update.
- D7. Inputs and outputs are different tokens: four nebula inputs, fifteen
  ground outputs with seeds. The shader never reads an output; CSS never reads
  an input except through the shader's result.
- D8. The ground tokens are written on `:root` inline, so the seed in the
  stylesheet loses to the sample without a class or a second rule.
- D9. The DoF blurs all stardust by the final opacity; per-depth transmittance
  is out of scope.
- D10. Stardust and DoF land after the ground has been measured alone, so the
  report can say what each costs.

## Questions

**Q4. Where the shader's palette input comes from.**
- (a) Four new input tokens in `custom.scss` (`--gamecult-nebula-0/1/2`,
  `--gamecult-nebula-base`), seeded with today's wash colours.
  **Recommended.** The nebula palette can be tuned without moving the UI
  accent, and the brand doc's rule (colour is defined in `custom.scss`) holds.
- (b) Reuse `--secondary` and `--tertiary` from `quartz.config.ts` and promote
  the violet to a config token. Fewer tokens; ties the nebula to the UI accent
  for good.

**Q5. Carrying the last sampled wash across page loads.**
- (a) `ground.js` keeps the last written token set in `sessionStorage` and
  applies it before the first paint on the next page; the seeds apply when
  nothing is stored. A cache, never authority. **Recommended.** Without it,
  every navigation shows the seed wash for the first frames, then the shader.
- (b) No storage; the seed paints first on every page. Simpler; a visible
  shift on every navigation.

## Unsettled

- The Lipschitz constant is empirical with a margin; a per-cell polynomial
  proof is possible and not in this cut.
- `_DoFBlurTex`'s writer is not in the Aetheria checkout; the web DoF takes the
  operator's recollection (final transmittance) as the driver. If stills of
  the video arrive, Hands can match the blur radius against them, but the
  mechanism does not depend on it.
- The ritual ground's radii are `rem` lengths and its first gradient stop is
  unpositioned (`custom.scss:124`); Hands normalises it when tokenising.
- Whether GameCult-Quartz's bundler permits a dynamic `import()` from a
  component's `afterDOMLoaded` or needs a script tag; one line either way.
- `:root:has(body[data-slug])` for the ritual seeds: supported in every
  current browser; a browser without `:has` gets the default seeds, which is
  acceptable for a fallback of a fallback.
