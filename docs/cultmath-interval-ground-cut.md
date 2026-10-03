# CultMath intervals and the gamecult.org ground: cut map

Status: Imagination pass 4, 2026-10-03. Pass 1's Q1–Q3 are answered (below);
the Self opened the `cultmath-tapes` campaign from pass 2; pass 3 revises the
two CultLib cuts after their first Hands reports and the operator's challenge
to the march, and changes the site field to envelope-then-noise; pass 4
(section "Pass 4") revises `interval-ops` to r3 after its second Hands report
and Soul's verdict, and names the one shipped void (Q9). Pass 5 (section "Pass 5") maps
affine forms against CultLib `main` at `a7966142`. Where a later pass and
an earlier section disagree, the later pass wins. Where this map and the Body
disagree, the Body wins and this map is stale.

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

Pass 3 wraps this field in an envelope (a nebula floor with authored wells and
clear sky above it); `coverage` below becomes the noise that displaces the
floor, as in Aetheria. The flow, the phases and the hue stand.

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

Pass 3 supersedes this section's march: the probe is per screen tile in a
pre-pass, the field has an analytic envelope the probe tests first, and the
loop below becomes the pre-pass's loop over cell ranges. The ball and the warp
enlargement stand.

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
| Tile pre-pass (pass 3), 1/64 res, `N = 8` | 40 k tiles × ≈ 30 probes in the cavity (interior retired in a few, the wall's ≈ 15 cells at 1.5 probes each), envelope-only because the noise ball cannot prove anything at this scale; ≈ 0.1 snoise-equivalents per pixel; worst case ≤ 192 probes per tile | 0.1 (≤ 0.8) |
| March at `S = 8` | 2.6 M × 8 snoise per frame, every pixel marches a wall; LOD and the mask decide where the frame's samples land, not how many (the controller does) | 3.3 |
| TAA resolve + DoF gather | ~19 texel reads per pixel, plus the depth-history fetch and one matrix multiply for the camera reprojection | 1.1 |
| Sun self-shadowing sample (named, not taken) | one envelope-only transmittance sample per primary sample, ≈ +25% of the march | +0.8, not on the phone |
| Stardust layer, half res, 4,096 particles | fill-bound, small | 0.2 |
| 8×8 downsample + PBO readback at 2 Hz | tiny | 0.05 |
| **Total at `S = 8`, the cavity (pass 3)** | | **≈ 4.8** |
| Total at `S = 6` / `S = 4` | | ≈ 3.9 / 3.1 |

Said plainly: LOD does not change the per-frame cost, which the controller
fixes by `S`; it changes how much a frame's samples are worth. At `S = 8` the
cavity does not fit the 4 ms phone budget on the estimate (≈ 4.8); `S = 6`
sits at the line and `S = 4` is under it. What gives is `S`, and with the
mask and LOD that is nearly free: a converged wall needs about 12 snoise per
pixel in total, so `S = 4` converges in about three frames through the TAA
instead of thirty, and `S = 8` in two. Then `dpr`, as before. What gives, in order, is the
sample count, then the resolution; the stardust and the DoF are never dropped
before the clouds' samples; the self-shadowing sample is not taken on any
device until measured. On desktop at `S = 16` the stack is ≈ 1.9 ms.

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
5. **The saving is a number** (amended in pass 3): a C# test marches tiles of
   rays through four scenarios, an envelope-then-noise field with authored
   empty space among them, with the dense march and with the tile-amortized
   interval march, and asserts ≥ 2× lower combined cost at equal
   transmittance on the authored scenarios; the uniform slab is printed as
   the worst case. The shipped field's evals per pixel and unfinished
   fraction come from the browser.
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
`docs/cultmath-interval-ground-cut-site-stardust.spec.json`. Revision 2 of
the two CultLib cuts (pass 3):
`docs/cultmath-interval-ground-cut-interval-ops.r2.spec.json`,
`docs/cultmath-interval-ground-cut-glsl-lowering.r2.spec.json`. Revision 3 of
`interval-ops` (pass 4): `docs/cultmath-interval-ground-cut-interval-ops.r3.spec.json`;
`glsl-lowering` r2 and `site-ground` r3 now depend on it.

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

## Pass 3: the envelope, the scenarios, the tile pre-pass, the split Phacelle

Pinned: CultLib `main` `d0ea37f8`; `hands/cultmath-interval-ops` at
`c9bd003b` (report `cut-interval-ops.h1`); `hands/cultmath-glsl-lowering` at
`5ae207e8` (report `cut-glsl-lowering.h1`, branched from interval-ops).
Rulings: `operator-tile-amortized-march`, `operator-split-phacelle`,
`operator-compiler-downloads`, `self-exp-measure-in-site`,
`operator-ground-void-brush` (answers Q6; the void scene below).

### The operator's words

On the march: "I'm skeptical about the interval march, both in implementation
and measurement. Are we testing it in scenarios where we're actually giving
it empty space to skip? Surely if the pre distortion SDF represents a clear
sky with the camera above the fog and aimed up, the interval math can quickly
tell us not to bother marching very far, no? In Aetheria the levels are
authored to place deep gravity wells for the player to gaze across, with
dynamic action spaced around the well, and finding the empty space there is
the whole point of using intervals, no?"

He is right. r1's saving test marched one `snoise` octave against a cutoff
through a slab with coverage everywhere: the worst case for intervals, and
0.93x said nothing about real scenes.

### What r1 measured, and what was wrong in the march

| Finding (`NoiseBoundTests.cs:344-376` at `c9bd003b`) | Effect | r2 |
| --- | --- | --- |
| Every probe charged one `snoise` per ray | a failed probe cost a third of a dense sample | one probe per tile (the ruling) |
| Coarse probes attempted where the ball could never prove emptiness: `hi = n(c) + L r` clamps to 1, so `L r F0 ≥ 1 + cutoff` is a guaranteed failure; at `StepMax = 2`, `L r F0 = 3.7` | every coarse probe wasted; the 0.74x row | a noise probe is attempted only when `L r F0` leaves room to prove `hi < cutoff`; otherwise the level is "maybe" for free |
| Growth capped at `StepMax = 8 Step0` | clear space cost `Depth / StepMax` probes | ranges double to the grid's end |
| The dense sample evaluated the detail octave even at zero occupancy | the baseline was dearer than a sane shader | coverage first, detail only when density can be nonzero (Aetheria's `if (dist < _SafetyDistance)`) |
| The finest-level probe before each dense cell | fine; amortized it is `1/N²` for a chance at 3 snoise | kept |
| No field structure | nothing to skip | envelope-then-noise, below |

### The field, from Aetheria

`Volumetric.cginc:165-221` with `:91-102`, `Zone.cs:369-397`
(`GetHeight`), `Zone.cs:426` (`PowerPulse`), `Settings.asset`
(`DefaultEnvironment`, `PlanetSettings`):

```text
s(p)      = p.y + h(p.xz)                                  the pre-distortion SDF; h ≥ 0 the well map
h(xz)     = PowerPulse(|xz| / 2R, 2) · 64 + Σ_b PowerPulse(|xz − c_b| / r_b, 16) · d_b
PowerPulse(x, e) = (1 − 4x²)^e on [0, ½], 0 beyond;  r_b = 500 M^0.25, d_b = 30 M^0.175
fade(s)   = 1 − smoothstep(0.75 S, S, s)                   S = SafetyDistance 30: no noise above it
s'(p)     = s + A · fade(s) · n(warp(p))                   n = the two-phase warped coverage + ½ detail
density   = max(0, (F − s') / B)                            F = FloorOffset −20, B = FloorBlend 10
```

Above the safety distance the density is exactly 0 and the noise is never
evaluated, by the dense march too. The warp applies to the noise argument
only; the height is read at the unwarped `xz` (Aetheria reads
`_NebulaSurfaceHeight` at `pos.xz` and warps inside `triNoise3d`).

### The composed bound (invariant `intervals-enclose`, kept)

Over a tile's frustum slice `[z0, z1]`, every step an existing `iv_*` op:

- Envelope over the slice's exact box, not the ball: `y = iv_mul([z0, z1],
  m_y)`, `x = iv_mul([z0, z1], m_x)` with the tile's slope intervals;
  `iv_h` = Σ over bowls of `[pulse(far), pulse(near)]`, `near`/`far` the
  least and greatest distance from the box to the bowl centre (monotone, so
  exact per bowl). `iv_s = iv_add(y, iv_h)`; `iv_fade` its ordered endpoints.
- Noise over the ball: `iv_n = iv_snoise_ball(ball.xyz · F0, ball.w · F0)`,
  `ball = iv_frustum_ball(m_c, z0, z1, N / (√2 f), D)`: centre on the tile's
  central ray at mid-depth, radius `(Δ/2)·|(m_c, 1)| + z1·N/(√2 f) + D`.
  The warp enlarges only this radius; the envelope's box takes none.
- `iv_s' = iv_add(iv_s, iv_scale(iv_mul(iv_fade, iv_n), A))`; the slice is
  provably empty iff `iv_s'.lo ≥ F`. When `iv_fade.hi = 0` the noise term is
  identically zero and `iv_snoise_ball` is not called: the envelope proves
  the slice empty at no `snoise` cost. This is why tiles compound: one
  envelope probe per tile can retire a whole ray length.

Two tests pin it: `TileBallEnclosesEveryRaySegment` (every ray's points,
sub-pixel jitter and warp included, inside the ball) and
`EnvelopeBoundEnclosesDensity` (the composed bound encloses `density` at
warped points of 2,000 slices, no tolerance).

### Scenarios and predictions (combined cost, `e = 0.2` snoise per envelope evaluation, `N = 8`)

| Scenario | Setup | Prediction |
| --- | --- | --- |
| (a) height fog | `h = 0`, camera at `y = S + 30`, rays level and up | > 50x: dense pays 256 envelope evaluations per ray; the tile about ten probes shared by 64 rays; 0 snoise either way |
| (b) inside the fog | `h = 0`, camera at `y = F − 10`, rays level | 0.8–1.0x: every ray saturates in a few cells; the pre-pass still probes the whole grid |
| (c) Aetheria-like | zone bowl `R = 2000` + four wells (masses 100, 1000, 10000, 1000), camera above the fog in the bowl, rays gazing across (`m_y ∈ [−0.25, 0.1]`), Aetheria's units and 256-sample quadratic grid | 2–4x: the clear cells before the fog surface collapse to a few probes per tile; the fog cells cost the same on both sides. The noise ball proves nothing at Aetheria's scale (`L r F0 ≥ 1.6` at the finest cell with `D = 60`), so it is not evaluated; the saving is the envelope's |
| (d) uniform slab | r1's field, kept as the worst case | 1.3–1.5x: the ceiling at `L = 10.099261` with the cheaper baseline |
| (e) the void, the shipped scene | the true cavity, hollow radius `Rh = 198`, ramp `S = 50`, camera 186 from the centre, low; every ray crosses interior and ends in a wall; three marches (fixed-step reference, masked fixed-step, masked LOD) | **steps per pixel ≈ 35 → 6 (5–8x fewer), combined cost 1.7x, snoise alone 1.25x**, from the model below. The noise ball proves nothing (`L r F0 ≥ 1.5`); the interior is retired by the envelope, the ramp is sampled at the content limit |

The 2x contract on combined cost is asserted on (a) and (c) at `N = 8`; (b)
and (d) are printed. (e) is the headline: its contract is steps per pixel
(below), and it prints cost and snoise beside it.

### Footprint LOD, and the cavity cost as a function

The operator's words: "Surely this depends on the size of the cavity and the
camera placement, no? The slope of the wall's density gradient offset
determines the region we have to sample in. How much we actually sample that
region depends on how close it is to the camera. A 10-wide gradient 1,000
units away only needs one raymarching step worth of detail, right?"

Right. Pass 3b costed every wall at a fixed step. The march is now
footprint-aware, in the site and in the saving test:

- `θ_px = 2 tan(fov/2) / H`; the footprint at distance `z` is `z θ_px`.
- Step `Δ(z) = max(k z θ_px, min(S/4, λ_fine(z)/2))`, `k = 4`: never finer
  than four footprints, otherwise the finer of the two content limits (four
  samples across the ramp, two per wavelength of the finest live octave).
- Octave `i` of wavelength `λ_i` carries weight `w_i(z) = 1 −
  smoothstep(λ_i/2m, λ_i/m, z θ_px)`, `m = 4`: one smooth blend per octave,
  no popping; TAA hides the per-frame sparsity.
- The weights are part of the field, so the bound encloses the truncated
  field exactly: the noise interval over a slice is `Σ_i iv_mul([w_i(z1),
  w_i(z0)], iv_snoise_ball(c F_i, r F_i))`, `w_i` monotone in `z`.
  `intervals-enclose` holds for what the shader evaluates.
- The pre-pass says where each ray's ramp begins; LOD says how finely to
  sample it once there. They compound. The reference a march that does not
  know where the wall is must pay is the fixed step `min(10, S/4)` everywhere.

The cavity is parametrised by the hollow radius `Rh` and the ramp width `S`;
the brush `(1 − (d/Rc)²)^e` with `CARVE` gives `Rh = Rc √(1 − CARVE^(−1/e))`
and `S` from the exponent (`e = 4` gives `S ≈ 1.3 Rh`, `e = 0.25` gives
`S ≈ 0.12 Rh`), so `(Rh, S)` are the knobs and `(Rc, e)` derived. Shipped:
`Rh = 198`, `S = 50`, so `Rc = Rh + S = 248` and `e = −ln CARVE / ln(1 −
(Rh/Rc)²) = 0.40`, `K = 1/30` (pass 4 corrects this line's `e ≈ 0.6, Rc ≈
220`, which did not satisfy the formula; Q9 holds the look question).

Imagination's model (scratchpad `cavity_model.py`; the test confirms or
refutes), 1080p, `N = 8`, per pixel for a converged frame; "ref" is the
fixed-step reference's steps:

| `Rh` | camera | `S = 10` steps / snoise / cost ratio | `S = 50` | `S = 150` |
| --- | --- | --- | --- | --- |
| 100 | low | 6 / 12 / 2.6x | 6 / 12 / 1.6x | 14 / 36 / 1.3x |
| 200 | low (**shipped**, ref ≈ 35 steps) | 6 / 12 / 3.2x | **6 / 12 / 1.7x** | 14 / 36 / 1.4x |
| 400 | low | 6 / 12 / 4.0x | 6 / 12 / 1.9x | 14 / 36 / 1.5x |
| 1000 | low | 5 / 9 / 8.2x | 6 / 12 / 2.5x | 14 / 36 / 1.7x |
| 1000 | centred | 4.3 / 7 / 12.9x | 6 / 12 / 2.9x | 14 / 36 / 1.8x |

Centred and mid offsets differ from low by under 10% at every point. What
the table says: within the frustum the footprint is under 4 units, so the
step is set by the content limits (`S/4`, and the detail octave's 12.5-unit
half-wavelength), not by distance; the "10-wide gradient 1,000 units away"
row is where the footprint finally bites (4.3 steps, 7 snoise). The sample
count is nearly flat in `Rh` and camera offset and rises with `S` only once
the detail wavelength, not the ramp, sets the step. What makes the shipped
scene cheaper without losing the mood, in order: a lower per-frame sample
budget, which LOD and the mask make nearly lossless (`S_budget = 4` converges
in about three frames at the walls); a thinner ramp (`S = 10` is 3.2x on cost
but reads as a surface; 25–50 keeps the fog); a longer detail wavelength or
no detail octave at the far wall; a larger hollow, which changes the
composition and costs nothing per pixel.

**Q8 retired.** At the shipped point the shipped march takes 5–8x fewer steps
than the fixed-step reference and 1.7x less combined cost; the contract for
(e) is the number that scales with GPU time, steps per pixel: ≥ 2x fewer than
the fixed-step reference at the shipped parameters, with snoise per pixel and
combined cost printed beside it and promised nothing. The test asserts that;
the browser measures it (`?ground=debug` against `?ground=dense`). A shortfall skips the assertion naming `saving-2x-scenarios`; the
fields, grids and `e` are not tuned. The honest summary: at a Lipschitz bound
near 10 the noise ball is weak everywhere; what intervals buy on these fields
is the envelope's empty space, found exactly, and the tiles make finding it
nearly free.

### The site ground: the void (ruling `operator-ground-void-brush`)

The operator's words: "There was an old main menu visual with a purely
authored field, first thing I did all the way back when the shader was new. I
believe it was a single negative pseudo gaussian spherical brush creating a
void in the clouds, with a sun gently nestled at the bottom lighting the
cavity and orbiting along a track with the camera following low, very moody,
gives us lots of options for authoring the flow field and stardust
distribution"

**The original, found.** Aetheria `e6181c11` ("Main Menu", 2021-03-13):
`Assets/Scenes/Main Menu.unity`, the `Sun` prefab instance under a `Center`
pivot, `Materials/Brushes/Gravity Well.mat` and
`Main Menu Sun Fog Tint.mat`, `Fog Marching Main Menu.mat` on
`VolumeRender.shader` of that commit. What survives, in its units:

| Element | Original value |
| --- | --- |
| The brush | `PowerBrush` `(1 − x²)^16`, depth 40, radius 256 (half depth at r = 52; a Gaussian of σ ≈ 45); the sun's own gravity-well child. A boundary bowl (depth 75, power 0.25, radius 1500) is flat across the scene: a constant 75 |
| The floor | offset −16.46, blend 39.6: the fog top is `y = −91` outside the void, `−131` at the sun |
| The sun | sphere radius 12.5 at `y = −130`: the bottom of the void |
| The orbit | `Center` rotates 5°/s about y (72 s period); the sun at radius 175 |
| The camera | Cinemachine vcam following the sun (framing transposer, distance 150, damping 10, handheld noise amplitude 1 at 0.25 Hz), looking at `Look Target` `(0, −130, 0)`, the orbit centre at the sun's depth; saved at `y = −27` |
| The light | authored, not marched: the sun's tint brush `(1 − x²)^3`, depth 2, radius 128, orange `(1, .4, .1)`, plus a scene-wide blue ambient tint `(0.18, 0.59, 1)` × 0.01 |
| Stardust | 256² particles, spacing 2, floor −60, ceiling 0, height exponent `800 / (ceiling + wellDepth)`: more headroom where the well is deeper, so the void fills with stardust |
| Noise | strength 150, frequency 0.01, safety 50 |

Not surviving: the 2021 `d()` displacement details (the 2021-05-30 "Volumetric
Redux" rewrote it), the tint map's runtime contents, the video. The 2021
brush was radial in `xz` and carved a bowl in a cloud floor; the operator
ruled (Q7, `operator-void-true-cavity`) that the site's void is a true 3D
hollow inside a cloud volume that surrounds it on every side, including
above. Everything else recovered carries over.

**The site scene** (`site-ground` r3; the brush parameters in this paragraph,
`Rc = 640`, `e = 4`, are superseded by pass 4's one shipped void, `Rh = 198`,
`S = 50`, `Rc = 248`, `e = 0.40`, pending Q9): the original's units. Outer volume: an
unbounded uniform fill (chosen over a shell: every ray then ends in a wall
and the march terminates by opacity, never by a far plane; `Z1 = 2 Rc` bounds
the pre-pass range only). The carve: `d = |p − c_v|`, `g(d) = (1 −
(d/Rc)²)^4` for `d < Rc`, `Rc = 640`, `CARVE = 1.5`; `density = K · max(0,
1 − CARVE · g(d'))`, exactly 0 inside the hollow radius `Rh = Rc √(1 −
CARVE^(−1/4)) = 198`, half at `0.49 Rc`, full at `Rc`; `K = 1/30`, so a wall
is opaque within ≈ 150 units of entry. Noise displaces the wall only:
`fade(d) = smoothstep(Rh − S, Rh, d)`, `S = 50`, `d' = d + A · fade · n`,
`A = 20`, `F0 = 0.01`. Its bound over a tile's slice: `d`'s interval is the
box's least and greatest distance to `c_v` (exact); `g`, `fade` and the carve
are monotone in it; the slice is provably empty iff `d_hi + A · fade(d_hi) ·
n_hi ≤ Rh`, and deep inside (`fade = 0`) no noise is evaluated. `Rc` grew
from the brush's 256 because a 3D hollow must hold the camera.

The sun orbits the origin at radius 175, period 72 s; the cavity centre is
`c_v = sun + (0, Rh − 12.5, 0)`, so the sun sphere rests on the hollow's
bottom and the hollow moves with it. The camera follows 120 behind the sun
along the tangent and 45 above it: 186 from `c_v`, 12 inside the hollow, low,
looking at `sun + (0, 60, 0)` so the lit lower wall and the dark upper wall
share the frame. The 2021 follow distance 150 and height 25 do not fit a
hollow a camera can stand in; 120 and 45 are the re-derivation. There is no
sky: behind a fully transmissive pixel the resolve shows the base token, and
only an unfinished ray (budget exhausted before the wall, measured, under 1%)
reaches it. `worldAt(t)` in `ground.js` is the one
owner of sun, cavity and camera; nothing integrates motion. The flow is authored
around the void: a swirl `SWIRL · tangent · (1 − (r/Rw)²)²` (largest
mid-slope, zero at rim and centre) plus the global noise flow, rendered into a
world-space window of side `4 Rw` around the sun, so warp, hue, motion vectors
and the stardust read one texture (D4). The stardust (next cut) spawns in the
bowl with the original's height rule and is lit by the same pulse.

**Lighting, cheapest moody version:** the original's mechanism made 3D. At a
sample, `light = SUN · (1 − (d/Rl)²)^3` for `d = |p − sun| < Rl = Rh = 198`
(the original's 128 scaled with the hollow), plus the blue ambient; `colour =
density · (light + ambient) · hue(flow)`. Zero extra `snoise`; the lower wall
within `Rl` of the sun glows and the upper wall stays dark because the pulse
is zero beyond `Rl`, which is the moody split; the sun inside the hollow is
occluded by nothing, so it is a small additive disc in the resolve, occluded
only by the accumulated transmittance along its ray. A self-shadowing step (one envelope-only
transmittance sample toward the sun per primary sample, no noise, ≈ +25% on
the march) is named, not taken: desktop-only if measurement ever allows.

**TAA with a moving camera:** the main pass writes the depth-weighted mean
march distance to an `R16F` history channel; the resolve reconstructs the
world point at that depth, moves it back by the field motion (`p + flow·dt`),
projects it with the previous frame's view-projection and fetches history
there. Same 3×3 clamp, same `α`, same resets. Colour, transmittance and depth
are reprojected by the one motion vector.

**Fit with the tokens:** `--gamecult-nebula-0` (orange) is the sun's colour,
`--gamecult-nebula-2` (sky) the ambient, `--gamecult-nebula-1` the cloud hue's
second pole, the base the sky. Outputs as pass 2, with one change:
`--gamecult-light-0-at` is written from the sun's projected position, so the
CSS wash's main light follows the sun; its seed is the sun's mean screen
position, so the fallback is a still of the same composition.

**Soundness with a moving camera:** each frame's pre-pass evaluates
`iv_frustum_ball` from that frame's camera; no ball, mask or probe result is
carried across frames. The only cross-frame state is the TAA history, which
is colour, not a soundness input. The saving test draws each tile's camera
independently and says so.

The march: a **low-resolution pre-pass** at `W/8 × H/8` runs the tile loop
over the slab's cell ranges (the envelope first, the noise ball only when the
envelope leaves `fade.hi > 0` and `L r F0` leaves room) and writes a per-tile
skip mask of the dense cells not proven empty into an `RGBA32UI` texel (128
bits; 96 cells at the slab's fine step). The main pass reads one texel
(`texelFetch`) and places its `S` samples in unmasked cells only, stratified
over the unmasked length; masked cells contribute exactly zero, so this is
exact importance sampling, not an approximation. The fragment cannot share a
probe across pixels (derivatives reach a 2×2 quad at best), so the pre-pass
is the cheaper and the only sound option in WebGL2. Cost at `N = 8` is in the
budget table: ≤ 144 probes per tile worst case (nothing skipped, hierarchy
descended everywhere), mostly envelope-only, ≈ 0.4–2 snoise-equivalents per
pixel; `N = 16` quarters it but makes the ball's lateral term equal the fine
half-length at the slab's far plane. **Predicted `N = 8`.** The site's own
ratio is measured in the browser against a dense reference with the same
envelope gate (the proving-ground contract's second half).

### The split Phacelle and the compile checks (glsl-lowering r2)

Pass 4: the glsl branch rebases onto interval-ops **r3**'s head, because r3
changes `cultmath_iv_frustum_ball`'s radius and the fixture family with it.

- Transformation step 1 gains one licence-keyed entry: an include under its
  own licence is lowered into its own file. `CultMath.glsl` (MIT: `CultMath.hlsl`
  + `CultMath.Interval.hlsl`) and `CultMath.Phacelle.glsl` (MPL-2.0,
  concatenated after it only by consumers that call `cultmath_phacelle`; the
  site does not). Both pinned; `THIRD-PARTY-NOTICES.md` lists the MPL files
  and no longer calls `CultMath.glsl` MPL.
- `iv_exp` joins the fixture with a platform tolerance (1 ulp, the generating
  OS named), because `self-exp-measure-in-site` has the site measure WebGL2
  `exp` against it. The fixture is regenerated for `cultmath_iv_frustum_ball`.
- glslang 16.6.0 and dxc are downloaded pinned and run: the MIT-only wrapper,
  the two-file wrapper, a `lerp(` negative control, and a `cultmath_phacelle`
  call against the MIT-only wrapper that must fail (proves the split).
- The glsl branch rebases onto interval-ops r2's head; its first commit is the
  regeneration alone.

Carried, still out of scope: the Unity `CultMath.dll` rebuild (no release in
the sequence); the fixture's ulp bounds on WebGL2 (site cut); Stryker's
timeouts are reported as timeouts, not kills.

## Pass 4: Soul's five findings, the analytic body, the one shipped void

Pinned: CultLib `main` `780d8cfa` (docs only since `d0ea37f8`);
`hands/cultmath-interval-ops` at `43183276` (report `cut-interval-ops.h2`,
verdict `cut-interval-ops.s1`, worktree `F:\Projects\CultLib-iv`). Rulings new
in this pass: `operator-wells-contract-vs-ceiling` ((c) is held to at least 90%
of its probe-free ceiling, not 2x: the ceiling is what intervals control; the
fog band's cost belongs to the field) and `operator-analytic-wall-body` (where
the envelope interval proves the density constant, one closed-form
Beer-Lambert step instead of sampling at the content limits). Spec:
`docs/cultmath-interval-ground-cut-interval-ops.r3.spec.json`.

### What r2 measured

| Scenario | r2 result | Pass 3 prediction |
| --- | --- | --- |
| (a) height fog | 16384x | > 50x |
| (b) inside the fog | 0.99x | 0.8–1.0x |
| (c) Aetheria wells | 1.41x at warp D (3.32x at warp 0); probe-free ceiling 1.42x (3.46x) | 2–4x |
| (d) uniform slab | 1.23x | 1.3–1.5x |
| (e) void, shipped | steps per pixel 36.34 → 16.45 (2.21x fewer), cost 1.34x, snoise 26.6 | 35 → 6 (5–8x), cost 1.7x |

Two things the pass 3 model got wrong, both now owned by r3: the footprint
step kept its content limits (`S/4`, the detail half-wavelength 12.5) inside
the wall's constant-density body, which the model had costed at two steps; and
the noise band does not end at `Rc`: with `A = 20` and the free noise interval
`[−1.5, 1.5]`, the density is provably `K` only from `d ≥ Rc + 30`. The ruling
answers the first; the second is content and stays.

### Soul's findings and what r3 does with each

| Finding | Severity | r3 |
| --- | --- | --- |
| `frustum-ball-float-crossing` | Medium | the radius is widened by a derived float32 bound (below); seven extreme families join the enclosure test; the strictness sentence leaves the C#, the HLSL and `design.md` |
| `aetheria-far-plane-miscited` | Low | `far = 2048`, the Main Camera's (`ARPG.unity:31777`, `:31830`); the Tint camera's 1000 was the wrong camera |
| `shipped-void-two-scenes` | Medium | one shipped void, `(Rh, S) = (198, 50)`, `(Rc, e) = (248, 0.40)` derived; the map, the site-ground spec and the test agree; the look question is Q9 |
| `lod-weights-never-exercised` | Low | configuration "LOD far" (`Rh = 4000`, `S = 50`, low camera, `f = 467`) reaches weights in `(0, 1)` and at 0; the `[w(z1), 1]` bound is recorded as a deviation with its soundness argument |
| `stryker-timeout-survivor` | Low | 2525 named equivalent; ball radii to `1e3` so `L/r` dies to an enclosure test; every timeout hand-run; the report claims only demonstrated kills |

### The float widening rule (finding 1)

`iv_frustum_ball` returns a float32 ball; its promise is that every exact
point `(m z, z)` of every ray of the tile, `m` in the footprint, `z ∈ [z0,
z1]`, moved by at most the warp, lies inside it. Soul showed the r2 ball misses
such points by up to `5.7e-4 r` when `z0 == z1`, `3.3e-4 r` at `z ≈ 1e7` and
19% of `r` at `|m_c| ≈ 1000`: the triangle inequality is an equality on the
degenerate slice and the centre's own rounding crosses it. The rule, in C# and
both HLSL copies, after the r2 radius:

```text
radius += (|c.x| + |c.y| + |c.z| + radius) · 2^-20        (9.5367431640625e-7, exact)
```

Derivation with `u = 2^-24`: the centre `c = (m_c z_m, z_m)` carries at most
two roundings per component (`z_m = fl(z0 + z1) · 0.5`, then the product), so
`|c_float − c_exact| ≤ 2u |c|₁`; the radius carries at most seven first-order
roundings (`z1 − z0`; the axis `sqrt(mx² + my² + 1)` at `3u`; the product; `z1
· footprint`; the sum; `+ warp`), so `r_exact ≤ r_float (1 + 7u)`; the
widening's own addition rounds once more. `2^-20 = 16u` covers `8u (|c|₁ + r)`
twice over. fma contraction removes roundings and only tightens, so dxc's HLSL
and any driver's GLSL are covered by the same bound. Cost at the shipped
scales: `|c|₁ ≈ 600`, `r ≈ 5` → `6e-4` units, 0.01% of the radius. The
enclosure test computes distances in double from the float ball and the float
ray, no tolerance, over the r2 domain and the extreme families (`z0 == z1`;
`z1 − z0 = 1e-6 z1`; `|m_c| ≤ 1000`; `z ≤ 1e7`; `z0 = 0, z1 ≥ 1e-3`; `f ≥ 1`;
warp to `1e6`), corners at both depth ends with the flow pointed outward. The
derivation pin becomes two-sided (`r_exact ≤ ball.w ≤ r_exact + 2^-19 (r_exact
+ |c|₁)`), so a ball that forgets the widening fails and one that grows past
the rule fails. The caller's world transform and its own point rounding remain
the caller's; the function says so in its doc instead of a false proof.

### The one shipped void (finding 3) and Q9

The map held two voids: the Footprint-LOD section's `(Rh, S) = (198, 50)`
(with an `e ≈ 0.6, Rc ≈ 220` that did not satisfy its own formula) and the site
section's `Rc = 640, e = 4` (a 442-wide ramp with a 50-wide fade band). Hands
implemented the first with the arithmetic corrected (`Rc = Rh + S = 248`, `e =
0.40`) and measured 2.21x; Soul measured the second at 1.93x. Under the
analytic body the second gains nothing: across a 442-wide ramp the optical
depth exceeds 4 before the density is provably constant, so rays end inside the
ramp and the body is never proven. The map's own rule says `(Rh, S)` are the
knobs, so the honest choice is the first; it changes the look relative to the
site section, hence Q9 (below). For `CARVE = 1.5` the ramp width is tied to the
exponent (`S/Rh = 1/√(1 − 1.5^(−1/e)) − 1`: `e = 0.4 → 0.25`, `e = 0.7 →
0.5`, `e = 1 → 0.73`, `e = 4 → 2.2`), so "pseudo-Gaussian" and "narrow" cannot
both be had without a third knob, and a narrow wall at `e = 4` needs
`CARVE ≈ 58`, which is a hard wall again. The ramp width is the look-cost knob.

### The analytic body (ruling `operator-analytic-wall-body`)

Consumer-side, no new CultMath function; the saving test and the site mirror
each other.

- **When it applies.** The consumer's `Bound` over the whole remaining range
  `[z, z_end]`, gated, is a point interval with positive value: for the void,
  the envelope with the free noise interval `[−1.5, 1.5]` proves `fade = 1`
  and `d_lo + A · (fade n)_lo ≥ Rc`, so every point of every ray of the tile
  over the rest of the grid has density exactly `K` for any noise value. For
  (a)–(d) no bound is a positive point (the fog's density varies with `s'`),
  so the step never applies there, and the test asserts it.
- **Pre-pass.** `MarchTile` returns `(dense cells, bodyStart, bodyDensity)`:
  when a single cell is not provably empty and its own bound is a positive
  point, one more probe over `[z_j, z_end]`; if that is a positive point too,
  `bodyStart = j` and every remaining cell is appended dense without probing
  (always sound; it also drops the body's probes). The site writes the cell in
  the mask texel's fourth word, `0xFFFFFFFF` when none.
- **Main pass.** Unmasked cells before `bodyStart` as before; at `bodyStart`,
  `T *= exp(−K · (z_end − z) · |dir|)`, one step, stop. The grid end for every
  void configuration becomes `|camera − c_v| + Rc + 6/K` (was `5/K`): the
  body starts at `d ≥ Rc + 30` plus box slack, so `6/K` leaves at least 4.9 of
  optical depth and `T ≤ 0.0075`. No probe in the main pass: the constant is
  the field's pointwise envelope value at that cell.
- **Pin.** `AnalyticBodyMatchesFineMarch`: wherever a body is reported, the
  bound over the rest of the grid is a positive point and the full and the
  truncated density at 64 sampled points of the body equal it exactly; the
  analytic factor equals the fixed-step product over the same cells within
  `1e-4` relative; no (a)–(d) tile reports a body. The masked fixed-step march
  stays the mask's soundness pin (exact agreement with the reference); only
  the LOD march takes the step. Hand mutation that must fail: in
  `VoidField.Compose`, `CarveRadius → HollowRadius` in the lo test.

### Footprint LOD, exercised (finding 4)

At `f = 935` every octave weight is 1 inside every r2 grid (the fine weight
drops below 1 past `z = 2922`; the largest grid ends at 2240). `VoidField`
takes a focal length, and one configuration reaches the weights: `Rh = 4000`,
`S = 50`, low camera, `f = 467` (a 98° vertical FOV at 1080 rows): fine weight
below 1 past 1460 and 0 past 2920, coarse below 1 past 5840, grid to ≈ 7990;
the footprint term `4 z θ` also exceeds 12.5 past 1460, so `LodStep` is
footprint-set there. It joins the enclosure test's field list and is printed by
the saving test, not asserted. Deviation recorded: the bound takes each weight
over `[w(z1), 1]`, not `[w(z1), w(z0)]`, because one `Bound` serves the full
field (`w = 1`) and the truncated one (`w ∈ [w(z1), w(z0)]`); `[w(z1), 1]`
contains both and `iv_mul` is monotone in its interval argument, so the
composed interval encloses both fields; the slack is only in far tiles whose
step the footprint already sets. Hand mutation that must fail the truncated
check there: `w(z0)` for `w(z1)`.

### Contracts after r3 (asserted at `N = 8`)

- (a): at least 2x combined cost. Measured 16384x.
- (c): at least 0.90 of its probe-free ceiling (`Ratio / Ceiling`, the lower
  of warp 0 and D). Measured 0.99 at warp D, 0.96 at warp 0.
- (e): at least 2x fewer LOD steps per pixel than the fixed-step reference at
  the one shipped void; the masked march exact; the LOD march within 0.02.
- (b), (d), the (e) grid and LOD-far: printed only.

### Predictions (Imagination's rule, not a measurement)

From r2's printed rows: the LOD steps that evaluated noise are about
`snoise / 2.8` plus two pre-band steps; the rest were constant-body steps,
which the analytic step makes one. Ref/LOD steps:

| `Rh`, camera | `S = 10` r2 → r3 | `S = 50` r2 → r3 | `S = 150` r2 → r3 |
| --- | --- | --- | --- |
| 100, low | 2.1 → 5.3x | 1.8 → 2.5x | 1.6 → 1.6x |
| 200, low | 2.7 → 5.8x | 2.2 → 2.8x | 1.8 → 1.8x |
| 400, low | 3.4 → 6.2x | 2.9 → 3.5x | 2.2 → 2.2x |
| 1000, low | 6.8 → 10.9x | 4.5 → 5.2x | 3.3 → 3.3x |
| 1000, centred | 12.4 → 30x | | |
| **shipped** (198, low, 50) | | **2.21 → ≈ 2.9x (12.5 steps), cost 1.34 → 1.38x, snoise 26.6 unchanged** | |

At `S = 150` rays end inside the ramp before the body is proven, so nothing
changes there. If the shipped point lands under 2.6x, the report says which
assumption failed: the pre-band's 0-snoise steps, or a body start later than
`Rc + 30` by box slack.

### Authority map delta (against pass 3's)

- `iv_frustum_ball` is now the owner of its own float32 rounding: the
  widening is part of the function. Consumers add nothing for it and remain
  owners of their world transform.
- The body proof is derived from the same `Bound` the mask is derived from, by
  one more probe; the main pass reads it and never decides it. The Beer-Lambert
  step is the consumer's integration, not a CultMath function.
- `(Rc, e)` are no longer authored; they are derived from `(Rh, S)`. The site
  section's `Rc = 640, e = 4` is no longer an owner of the look (Q9 pending).
- (c)'s contract is derived from its own run (0.90 of the ceiling); no absolute
  target is written for (c).
- Deletion line: the false strictness sentence, the Tint camera citation, the
  (c) 2x assertion and the second shipped void leave before their replacements
  are written. Net in `src`: one line and a doc comment.

### Dependent specs

`glsl-lowering` r2 now depends on `interval-ops` r3 and rebases onto its head
(the fixture family for `cultmath_iv_frustum_ball` changes with the widening).
`site-ground` r3 now depends on `interval-ops` r3; its field item carries the
one shipped void with `(Rc, e)` derived, its pre-pass writes the body-start
cell in the texel's fourth word, its main pass takes the one analytic step,
and `Z1 = |camera − c_v| + Rc + 6/K` (614 at the shipped point, 62 cells of
10; the mask has 96 bits). Both files are edited in place on `main`; the
mind's copies need re-admission by Self.

## Questions

**Q9. The one shipped void** (pass 4; finding `shipped-void-two-scenes`).
Authored as `(Rh, S)` with `(Rc, e)` derived; `CARVE = 1.5`, `A = 20`,
`F0 = 0.01`, `K = 1/30`, the camera as site-ground r3.
- (a) `Rh = 198`, `S = 50` → `Rc = 248`, `e = 0.40`. The scene r2 measured
  (2.21x; predicted ≈ 2.9x with the analytic body). A 50-wide noisy wall whose
  outer edge has a kink (`e < 1`), softened by the ±20 noise displacement and
  the TAA. **Recommended**: the map's own rule, the measured scene and the
  ruling's analytic body all point at it.
- (b) `Rc = 640`, `e = 4` (`Rh = 198`, a 442-wide pseudo-Gaussian ramp, fade
  band 50): the site section's soft wall. Measured 1.93x; the body is never
  proven, so the analytic step never fires; the (e) contract fails unless it
  is dropped to printed or the detail octave is removed where the ramp is
  wide, both of which are the operator's to decide.
- (c) `Rh = 198`, `S = 100` → `Rc = 298`, `e = 0.70`: the compromise, a
  softer 100-wide wall; predicted ≈ 2.4x; not in the r2 grid, so unmeasured.

Hands codes (a); another ruling moves only the shipped constants here, in the
r3 spec and in the site-ground spec.

**Q6. The site ground's envelope look.** Answered outside the options by
`cultmath-tapes:ruling:operator-ground-void-brush`: the old main menu's void
(pass 3, "The site ground: the void").

**Q7. What "spherical" means for the brush.** Ruled
`cultmath-tapes:ruling:operator-void-true-cavity`: a true 3D hollow inside a
cloud volume that surrounds it on every side, including above; not the 2021
bowl.

**Q8. `cavity-contract`.** Retired in pass 3d (see "Footprint LOD"): the
contract for (e) is steps per pixel against the fixed-step reference at the
shipped parameters, ≥ 2x fewer, predicted 5–8x; cost and snoise are printed
beside it and promised nothing.

Specs for pass 3: `docs/cultmath-interval-ground-cut-site-ground.r3.spec.json`
beside the two CultLib r2 specs.

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

## Pass 5: affine forms (ruling `affine-forms-unparked`)

Imagination pass 5, 2026-10-03. It maps the affine-arithmetic piece of the
tape target, unparked on its own by ruling
`cultmath-tapes:ruling:affine-forms-unparked`. It answers follow-up
`cultmath-tapes:follow_up:wells-oracle-shortfall-affine-forms`. No tape,
pruning or interpreter is built; tape steps 2-5 stay parked under
`tape-target-unparks-when-asura-stable`. Pinned head: CultLib `main` at
`a7966142`. Every `file:line` in this section is against that commit.

### What was measured (body facts)

The probe is a renamed copy of `NoiseBoundTests.cs` with five more bounds for
`FogField`. It lives on the local branch `scratch/imagination-affine-probe`
(`5fa8e9ce`). That branch was never pushed to origin and is not for merging.
It was run on Yggdrasil through the ygg-verify stopgap (`dotnet/sdk:10.0`,
about 3 minutes). It re-runs `Run(FogField.Wells(w), 8, 64, 0x5A7E + 8)`, so
the tiles, rays and seeds are those of the landed (c) row at `N = 8`. V0
reproduces the landed figures exactly (0.378 and 0.194).

Constants the probe needed, each sampled over 2e5 seeded points with a 1.25
margin (probe grade, not provenance):
- the Frobenius norm of the Hessian of `snoise(float3)`, max 55.15, so
  `M = 68.94`;
- the gradient length of `snoise(float2)`, max 7.35, so `L2 = 9.19`;
- for comparison, the mean `|snoise_grad|` is 2.59, against `L = 10.10`.

The bounds:
- **V0**: the landed bound.
- **V1**: V0 with the detail octave's ball no longer enlarged by the warp. The
  detail reads the unwarped point (`NoiseBoundTests.cs:860`, `Noise.Detail(p, ...)` of the unwarped `p`), yet `Bound`
  hands it `ball.w`, which carries `D` (`:875`, `:890`).
- **V2**: V1 with the warp centred. Each phase's coverage ball is centred at
  `c + flow(c) shift_k`, with radius `r + |shift_k| rho`, where
  `rho = FlowAmplitude L2 FlowFrequency r sqrt 2` bounds how far the flow moves
  over the ball. This is the mean-value form of the warp: interval arithmetic
  only, no affine noise.
- **V3**: V2 with the centred form of the noise,
  `n(c) +- min(L R, |g(c)| R + M R^2 / 2)` from one `snoise_grad`.
- **V4**: V3 with a reduced affine form. Three shared symbols (depth `t`,
  slopes `u` and `v`) carry the envelope's plane term and every noise term's
  linear part, and one error symbol takes the rest. It applies where the fade
  is the point 1, and is intersected with V3.
- **V5**: V4 with one shared symbol, depth `t`, plus one error symbol:
  Gamito's `t0 + t1 e1 + t2 e2`.

A gradient evaluation is counted as 2 snoise, an estimate. V3 to V5 gate a
probe on the best result any centre value and gradient could give.

Range ceiling. A cell is range-nonempty if the density is positive at any of
5 x 5 slopes over the tile's slope square (corners included) times 9 depths
over the cell, or if it is in the oracle mask. Its efficiency is the oracle
march's cost over the cost of marching the range mask, with probes free. The
sampling can miss nonzero points, so this figure is an upper estimate. No
sound enclosure can beat it.

| (c), `N = 8` | efficiency (oracle) | cost ratio | dense cells/ray | probe overhead | cull |
| --- | --- | --- | --- | --- | --- |
| warp 0, V0 landed interval | 0.378 | 2.74x | 12.40 | 0.961 | 0.952 |
| warp 0, V2 centred interval | 0.368 | 2.67x | 12.40 | 0.937 | 0.952 |
| warp 0, V3 centred noise | 0.466 | 3.38x | 8.67 | 0.868 | 0.974 |
| warp 0, V5 affine, one symbol | **0.511** | **3.71x** | 7.81 | 0.859 | 0.979 |
| warp 0, V4 affine, three symbols | 0.534 | 3.88x | 7.37 | 0.853 | 0.981 |
| warp 0, range ceiling | 0.859 | | | | 0.995 |
| warp 60, V0 landed interval | 0.194 | 1.39x | 31.55 | 0.993 | 0.842 |
| warp 60, V1 detail unwarped | 0.205 | 1.47x | 27.74 | 0.963 | 0.864 |
| warp 60, V2 centred interval | 0.347 | 2.49x | 13.71 | 0.938 | 0.945 |
| warp 60, V3 centred noise | 0.441 | 3.16x | 9.52 | 0.870 | 0.969 |
| warp 60, V5 affine, one symbol | **0.477** | **3.42x** | 8.71 | 0.864 | 0.974 |
| warp 60, V4 affine, three symbols | 0.502 | 3.60x | 8.16 | 0.856 | 0.977 |
| warp 60, range ceiling | 0.854 | | | | 0.995 |

Every variant's transmittance equals the dense march's (max |dT| 0). No
variant's pre-pass proved a cell empty where any of the 225 samples per cell
was nonzero (0 enclosure violations).

What this establishes:

1. **The oracle cannot be reached by any sound bound.** The oracle mask
   samples one depth per ray. A bound must cover the whole slice of the tile,
   so the best any exact enclosure could do is about 0.86 of the oracle. That
   matches the 0.57-0.85 that conservative visibility methods reach against
   exact sets (`docs/research/culling-efficiency-oracle-prior-art.md`). An
   oracle target of 0.90 is unreachable by construction.
2. **The warp diagnosis was half right.** At warp `D` most of the gap is the
   warp enlarging the radius by its maximum (V0 to V2: 0.194 to 0.347). That
   is fixed by evaluating the flow at the ball's centre and enlarging the
   radius only by how far the flow can move across the ball. It takes no
   affine noise, and it costs one more snoise per probe, for the second phase
   ball. The detail octave's spurious warp (V1) is worth little on its own.
3. **Affine noise buys the rest:** the centred form, then the shared depth
   symbol. Against the centred interval, affine with one symbol closes about a fifth
   of the oracle gap at warp 0 and at warp `D` (efficiency 0.368 to 0.511 and
   0.347 to 0.477). Its combined cost ratio is 1.39x and 1.37x the centred
   interval's, and 1.35x and 2.46x the landed interval's. It reaches 0.59 and
   0.56 of the range ceiling.
4. **Three symbols buy about 5% over one** (0.534 against 0.511, 0.502
   against 0.477). One symbol fits a `float3`. Three need a struct or a
   `float4` plus a `float`, and the HLSL mirror's harness compares struct
   returns but has no struct parameters (`packages/cultmath/docs/design.md:126-135`).
5. **The probe overhead falls** from 0.96 to 0.86 under every centred or
   affine bound. Each probe costs a gradient and proves more, so the total
   cost still drops by 1.35x to 2.46x. Today's (c) assertion, overhead at
   least 0.90 (`NoiseBoundTests.cs:1558`, `:1588`), would reject the cheaper
   march. That is question `affine-wells-contract`.

### The model

An affine form is a transient value, not a persistent kind. The persistent
things this pass adds or changes:

| Thing | Identity | Lifecycle | Authority |
| --- | --- | --- | --- |
| `SNOISE_HESSIAN` (C#), `CULTMATH_SNOISE_HESSIAN` (HLSL, lowered to GLSL) | The constant's name; one value in `math.Affine.cs`, mirrored | Measured once by `NoiseBoundTests.MeasureHessian` (slow, explicit), as `SNOISE_LIPSCHITZ` is. It is re-measured and re-pinned whenever the `snoise` kernel changes. | `MeasureHessian` sets it; `HessianConstantPinsSampledCurvature` pins it; no consumer carries its own |
| `af_*` families in `fixtures/glsl-parity.json` | Family signature string, appended after the last existing family | Regenerated only by `CULTMATH_WRITE_GLSL=1`; earlier cases stay byte-identical | `GlslMirrorTests` generator |
| The (c) contracts | `IntervalSkipHalvesEvaluations` assertions | Changed only by a ruling | Operator (question `affine-wells-contract`) |
| `SNOISE2_LIPSCHITZ` (C#), mirrored in HLSL | The constant's name in `math.Interval.cs`, beside `SNOISE_LIPSCHITZ`, with an `iv_snoise_ball(float2, float)` overload | Measured by `MeasureLipschitz2` (slow, explicit) and pinned, as `L` is | CultMath. The (c) field's flow is `snoise(float2)`, and a bound on a CultMath function belongs to CultMath, not to a test-local helper. A consumer's own flow, such as the site's texture, keeps its own variation bound |

### Representation

**A reduced affine form with one shared symbol: `float3(x0, a, e)`, meaning
`x0 + a·ε + e·δ`.** Here `ε ∈ [-1, 1]` is shared by every form over the same
region, and `δ ∈ [-1, 1]` is a private error, with `e ≥ 0`.
- This is Messine's AF1 as Gamito and Maddock use it for gradient noise,
  `t0 + t1 e1 + t2 e2`.
- Sharp and Jacobson's measurements show that this fixed-symbol form is the
  fast one on GPUs: 1.0x against 8.4x for full affine.
- It fits one `float3` register, as an interval fits a `float2`. It needs no
  struct parameter, so the mirror, lowering and fixture machinery take it
  unchanged.
- V4 against V5 prices the two extra symbols at about 5% efficiency.

For a tile probe `ε` is the depth parameter: `z = z_m + h ε` with
`h = (z1 - z0) / 2`. The footprint goes into the error. For a single ray it is
Gamito's form exactly.

A point in space is not a new type. It is the three arguments
`(centre: float3, axis: float3, radius: float)`, the set
`{centre + axis ε + w : |w| ≤ radius}`. `af_snoise` and `af_fbm` take a point
in that form, and `af_frustum_axis` and `af_frustum_ball` produce one for a
tile slice.

Ops, named by the (c) consumer, plus `af_mul` because affine arithmetic is
defined by its product, as `iv_mul` is for intervals:
- `af_point`, `af_symbol(x0, a)`, `af_from_iv`, `af_range` (the bridge to
  `iv_*`);
- `af_add`, `af_sub`, `af_neg`, `af_scale`, `af_add_iv`, `af_mul`;
- `af_snoise`, `af_fbm`;
- `af_frustum_axis`, `af_frustum_ball`.

No others until a consumer names one, which is the rule `design.md` already
states for intervals.

The interval side gains `SNOISE2_LIPSCHITZ` and the overload
`iv_snoise_ball(float2, float)`. The (c) field's flow is `snoise(float2)`, and
centring the warp needs a bound on it, which belongs to its owner.

`af_snoise(centre, axis, radius)` makes one `snoise_grad` call. With
`R = |axis| + radius`, it returns the narrower of two sound forms:
- the Lipschitz form `(n, 0, L R)`;
- the centred form `(n, g · axis, |g| radius + M R² / 2)`.

`e` absorbs the op's own float rounding and an allowance for a GPU's gradient
error. The allowance is derived in the doc comment and measured on WebGL2 by
the fixture's enclosure check, as `iv_frustum_ball`'s is.

The warp stays the consumer's, so CultMath owns no flow. The rule, stated in
`design.md` and tested for the (c) field: a warp whose value at the centre is
known, and whose variation over the ball is at most `ρ`, is enclosed by moving
the centre by `w(c)` and adding `ρ` to the radius. Interval balls benefit from
it too (V2).

### Authority map

- **Owner.** `src/CultMath/math.Affine.cs` owns affine-form semantics on the
  CPU. `shaders/CultMath.Affine.hlsl` mirrors it bit for bit, and is the shader
  semantic authority. `shaders/CultMath.glsl` is derived from the HLSL.
- **Inputs.**
  - `snoise_grad`, whose `.w` is bit-equal to `snoise`;
  - `SNOISE_LIPSCHITZ`;
  - `SNOISE_HESSIAN`;
  - the caller's forms and points.
- **Outputs.** `float3` forms, and `float2` intervals through `af_range`.
- **Derived state.**
  - The GLSL text is generated by `GlslLowering`, unchanged.
  - The fixture families are generated.
  - The (c) efficiency rows are printed measurements, not state.
- **Forbidden writers.**
  - A consumer linearising noise itself: the probe's V4 and V5 did this by
    hand, and Hands must call `af_snoise`.
  - A second curvature constant anywhere.
  - Hand-edited `CultMath.glsl`.
  - An affine or interval evaluator over `CultMath.Expr` (the parked tape's,
    see collisions below).
  - A flow or envelope function in CultMath. Consumers compose those, as for
    intervals.
- **Shared paths.** The test field's `FogField`, the site ground and
  Aetheria's nebula all reach affine noise through `af_snoise` or `af_fbm`,
  and a tile slice through `af_frustum_*`.
- **Deletion line.**
  - In `FogField.Bound` (`NoiseBoundTests.cs:875`), the warp-enlarged ball is
    replaced by the centred warp, and the detail ball stops carrying the warp
    (`:890`).
  - The (c) probe-overhead assertion (`:1558`, `:1588`) is replaced as the
    ruling on `affine-wells-contract` says.
  - `design.md`'s "What they do not: ... affine forms" (`:584-587`) and "That
    gap belongs to the tape target's affine forms" (`:623-624`) are rewritten.
  - The tape target's open item on representation is closed.

### Cut order

1. **`affine-forms`, the one she can see.**
   - It adds C# `math.Affine.cs` and `AffineTests`, plus `SNOISE_HESSIAN`'s
     provenance.
   - The (c) field gains the centred warp and the affine bound.
   - (c) is re-measured with three bound rows on the same tiles: landed
     interval, centred interval, affine. The range ceiling is printed beside
     the oracle.
   - An explicit, env-gated test writes a cull-map PNG of one (c) frame: dense
     cells per ray for each bound, and the transmittance. Self publishes it.
   - `design.md` is updated.
   - It is blocked on `affine-wells-contract` only for the assertion's shape.
2. **`affine-shaders`.**
   - It adds `CultMath.Affine.hlsl` and its Unity copy and `.meta`, the
     include, and the regenerated `CultMath.glsl`.
   - Fixture families are appended, with an enclosure check for `af_snoise`
     and `af_frustum_ball`.
   - The dxc and glslang compiles and the WebGL2 golden-fixture run happen on
     Starfire.
   - Both cuts share the branch `hands/cultmath-affine-forms` and merge
     together after cut 2's Soul pass. Main never carries a C#-only `af_*`, so
     three-runtimes-agree holds at every merged commit.

Writing the C# before the HLSL inverts the usual order, because HLSL is the
semantic authority. The cost is small: the ops are plain arithmetic in the
common subset. Cut 2 changes the C# if the HLSL dialect forces something,
never the reverse.

### Verification venue

Heavy work runs on Yggdrasil through the stopgap: `dotnet test`, Stryker scoped
to `math.Affine.cs`, the (c) re-measure and the cull-map render. Starfire runs
only the Windows and GPU legs, one job at a time:
- `dxc -T lib_6_3 -HV 2021` on `CultMath.hlsl`;
- `glslangValidator` 16.6.0 on the lowered GLSL (ruling
  `operator-compiler-downloads`);
- the WebGL2 golden-fixture run on the GTX 1070, under ANGLE D3D11 in a
  browser. It is seeded from the glsl-lowering Soul pass's `gpu.js`
  (follow-up `glsl-browser-parity-run`).

### Collisions

- **`cycles-converters` r1** edits the same seams:
  - the include list (`CultMath.hlsl:509-510`);
  - `HlslMirrorTests.cs:64-67`;
  - `build-unity-package.ps1:82-87`;
  - the fixture's family list.

  Whichever merges second rebases, regenerates `CultMath.glsl` and the fixture
  (`CULTMATH_WRITE_GLSL=1`), and appends its families after the first's.
  Nothing semantic collides.
- **`expr-ir` r1 and question `material-ir-in-cultmath`.** No file collides,
  and neither cut waits on the other.
  - Under `cultmath-ir-now`, the parked tape's step 2 will add interval and
    affine evaluators over `ExprGraph`, and those will call `iv_*` and `af_*`
    as op semantics. That is one more reason the affine ops are plain value
    functions over `float3`, with no struct state.
  - Under `aetheria-local`, nothing here changes.
  - The `expr-ir` spec's negative check forbids `Interval(Evaluator)?` under
    `Expr`. Self may add `Affine` to it at its next revision.
- **`site-ground` r3** (not dispatched): the void (e) and the site's flow
  texture are not measured here. Follow-up `site-ground-affine-and-centred-warp`
  carries both. Centring the warp in the site needs a bound on the flow
  texture's variation, and the texture stays the one motion owner (it is
  sampled at the centre, never re-derived).

### Pass 5b: the deep well (ruling `wells-scale-order-of-magnitude`)

The operator's words: "There should be a well pushing down with both a radius
and depth an order of magnitude larger than the span of the bit that's
expensive to sample, the fog gradient, otherwise there's no meaningful skipping
being measured".

**Her diagnosis holds.** The expensive band is the stretch of the
pre-distortion height `s` where only the noise decides whether there is
density.
- `FogField` has `F = -20` and `A = 20`, and the noise reaches ±1.5. With the
  fade at 1, a slice is provably empty only where `s >= F + 1.5 A = 10`.
- It is dense whatever the noise does where `s < F - 1.5 A = -50`.
- So the band is `G = 60` units.

Today's (c) wells, from `NoiseBoundTests.cs:753-776`:
- **Depths** are `d = 30 M^0.175`: 67, 101, 150 and 101. That is 1.1 to 2.5
  times `G`.
- **Half-depth radii** are about `0.103 x 500 M^0.25` for a `PowerPulse`
  exponent of 16: 163, 290, 515 and 290. That is 2.7 to 8.6 times `G`.
- **The zone bowl** is 64 deep, 1.07 times `G`.

No well is ten times `G` deep. The camera sits just above the band
(`y = max(0, S + 10 - h)`) and grazes it, so most of the measured cost is the
band itself, and little of it is empty space.

**The probe.** The probe added a field, `(c') deep well`, on the scratch
branch at `bf0148b8`:
- the zone bowl plus one well at its centre, with depth 600 and half-depth
  radius 600 (`PowerPulse` exponent 2, scale 2220), so each is `10 G`;
- the camera's xz within 300 of the centre, at `y = F - h / 2`, halfway
  between the sunken fog surface and the rim;
- slopes as (c), `m_y` in [-0.25, 0.1].

A level ray crosses about 600 units of empty well before the wall, and the
band there is about 73 units along the ray. The run used the same run
parameters, seeds and constants as Pass 5, on Yggdrasil.

| `N = 8` | efficiency vs oracle | cost ratio | dense cells/ray | probe overhead | cull |
| --- | --- | --- | --- | --- | --- |
| deep well, warp 0, landed interval | 0.591 | 3.10x | 4.14 | 0.943 | 0.986 |
| deep well, warp 0, centred interval | 0.579 | 3.04x | 4.14 | 0.924 | 0.986 |
| deep well, warp 0, affine (one symbol) | **0.662** | **3.47x** | 3.27 | 0.879 | 0.993 |
| deep well, warp 0, range ceiling | 0.859 | (oracle ceiling 5.24x) | | | 0.997 |
| deep well, warp 60, landed interval | 0.462 | 2.45x | 6.58 | 0.970 | 0.967 |
| deep well, warp 60, centred interval | 0.555 | 2.95x | 4.34 | 0.926 | 0.984 |
| deep well, warp 60, affine (one symbol) | **0.644** | **3.42x** | 3.33 | 0.880 | 0.992 |
| deep well, warp 60, range ceiling | 0.848 | (oracle ceiling 5.31x) | | | 0.996 |

There were no enclosure violations, and transmittance equals the dense march
in every row.

What changes:
- Where the empty space is real, the interval march already skips most of it.
  It reaches 0.59 at warp 0, and 0.46, or 0.56 with the warp centred, at warp
  `D`.
- Affine's gain over the centred interval shrinks to 1.14x and 1.16x in
  combined cost, against 1.39x and 1.37x on the band-dominated wells.
- Against the landed interval it is 1.12x and 1.40x.
- Affine reaches 0.77 and 0.76 of the range ceiling. What it wins is in the
  wall's band, which is where the shallow wells spend everything.
- Probe overhead is 0.88 in both rows, still under the 0.90 rule.

Decision for the cut (`affine-forms` r2): **(c) becomes the deep well, which
carries the contract.** The four mass-derived wells are kept as a printed row,
`(c-band)`. They are not the skipping measurement the ruling asks for, but they
are Aetheria's own geometry and the band-dominated case, where affine gains
most. Dropping them would hide where affine pays. The cull-map image renders a
deep-well camera at warp `D`.

The contract question is re-asked against these numbers as
`cultmath-tapes:question:affine-wells-contract-r2`. The first question was
withdrawn in its favour, and `affine-forms` r2 supersedes r1.
