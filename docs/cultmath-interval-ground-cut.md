# CultMath intervals and the gamecult.org ground: cut map

Status: Imagination pass 3, 2026-10-02. Pass 1's Q1–Q3 are answered (below);
the Self opened the `cultmath-tapes` campaign from pass 2; pass 3 (section
"Pass 3" near the end) revises the two CultLib cuts after their first Hands
reports and the operator's challenge to the march, and changes the site field
to envelope-then-noise. Where pass 3 and an earlier section disagree, pass 3
wins. Where this map and the Body disagree, the Body wins and this map is
stale.

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
| Tile pre-pass (pass 3), 1/64 res, `N = 8` | 40 k tiles × ≤ 144 probes, mostly envelope-only; in the void most sky tiles finish in a few probes; ≈ 0.4–2 snoise-equivalents per pixel, measured | 0.2–0.8 |
| March at `S = 8` | 2.6 M × 8 snoise, placed in unmasked cells only, plus the analytic sun light (one length, one pow per sample, ≈ 5%) | 3.3 |
| TAA resolve + DoF gather | ~19 texel reads per pixel, plus the depth-history fetch and one matrix multiply for the camera reprojection | 1.1 |
| Sun self-shadowing sample (named, not taken) | one envelope-only transmittance sample per primary sample, ≈ +25% of the march | +0.8, not on the phone |
| Stardust layer, half res, 4,096 particles | fill-bound, small | 0.2 |
| 8×8 downsample + PBO readback at 2 Hz | tiny | 0.05 |
| **Total at `S = 8`, the void scene (pass 3)** | | **≈ 4.9** |
| Total at `S = 6` / `S = 4` | | ≈ 4.0 / 3.2 |

Said plainly: at `S = 8` the void scene with the pre-pass, the analytic light
and the camera reprojection does not fit the 4 ms phone budget on the
estimate; `S = 6` sits at the line and `S = 4` is under it. The controller
will settle a mid-range phone at `S = 4–6`, which the TAA covers (convergence
in ~32 frames, about a second), and the pre-pass is what makes `S = 4` look
like more: the samples land only on the walls. What gives, in order, is the
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
`docs/cultmath-interval-ground-cut-glsl-lowering.r2.spec.json`.

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
| (e) the void, the shipped scene | the original's bowl and units, camera inside the bowl at `y = −105`, rays over the whole frustum | 2–3x: sky rays go from 96 envelope evaluations to a few probes per tile; wall rays cost the same on both sides; the mix decides. The noise ball proves nothing here either (`L r F0 ≥ 1.5` at the finest cell) |

The 2x contract is asserted on (a), (c) and (e) at `N = 8`; (b) and (d) are
printed. The headline is (e). A shortfall skips the assertion naming `saving-2x-scenarios`; the
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
Redux" rewrote it), the tint map's runtime contents, the video. The brush is
radial in `xz` and carves a bowl in a cloud floor; "spherical" in the
operator's words is the brush, not a 3D cavity (Q7).

**The site scene** (`site-ground` r3): the same units. `h(xz) = 75 + 40 (1 −
(r/256)²)^16`, `s = y + h − F`, `F = −16.46`, fog where `s' < 0`, `S = 50`,
`A = 20` (the original's 150 is too violent for a 40-deep bowl), `F0 = 0.01`.
The sun orbits the origin at radius 175, period 72 s, `y = −130`; the void
moves with it. The camera follows 150 behind along the tangent at `y = −105`:
inside the bowl, 14 below the rim, looking at the orbit centre at the sun's
depth, with a slow handheld drift. `worldAt(t)` in `ground.js` is the one
owner of sun, void and camera; nothing integrates motion. The flow is authored
around the void: a swirl `SWIRL · tangent · (1 − (r/Rw)²)²` (largest
mid-slope, zero at rim and centre) plus the global noise flow, rendered into a
world-space window of side `4 Rw` around the sun, so warp, hue, motion vectors
and the stardust read one texture (D4). The stardust (next cut) spawns in the
bowl with the original's height rule and is lit by the same pulse.

**Lighting, cheapest moody version:** the original's mechanism made 3D. At a
sample, `light = SUN · (1 − (d/Rl)²)^3` for `d = |p − sun| < Rl = 128`, plus
the blue ambient; `colour = density · (light + ambient) · hue(flow)`. Zero
extra `snoise`; the far cavity stays dark because the pulse is zero beyond
`Rl`; the sun is a small additive disc in the resolve, occluded by the
accumulated transmittance. A self-shadowing step (one envelope-only
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

## Questions

**Q6. The site ground's envelope look.** Answered outside the options by
`cultmath-tapes:ruling:operator-ground-void-brush`: the old main menu's void
(pass 3, "The site ground: the void").

**Q7. What "spherical" means for the brush.**
- (a) As the original: the brush is radial in `xz` and carves a bowl in a
  cloud floor; the sun sits at the bowl's bottom; clear sky above the rim.
  **Recommended**: it is the scene that existed, and its parameters survive.
- (b) A true 3D spherical cavity in a cloud volume (clouds above as well as
  below, the sun at the cavity's bottom). The culling is the same (the
  envelope is monotone in distance either way); the look is more enclosed and
  the sky, stardust and DoF composition changes.

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
