# Ground motion law: prior art (facts only)

Serves fork: cultmath-tapes, site-ground motion law (invariant `one-motion-owner`, target `cultmath-tapes:target:r2`), blocks `cut-site-ground`.
Gathered by Eyes, session self-2026-10-14b-eureka, 2026-10-14. No recommendation. Committed by Self before the fork was put.

Method: primary PDFs were downloaded to the session scratchpad and read with pdftotext ("read"). "[secondary]" marks facts taken from a web-search summary, forum or blog, not read at the primary source. "[not verified]" marks things looked for and not reached.

## 1. How shipped volumetric clouds move, and what breaks

### 1.1 Horizon Zero Dawn, Schneider and Vos, SIGGRAPH 2015
Source: https://www.advances.realtimerendering.com/s2015/The%20Real-time%20Volumetric%20Cloudscapes%20of%20Horizon%20-%20Zero%20Dawn%20-%20ARTR.pdf (99 slides, read)
- Density = low-frequency 3D Perlin-Worley base shape times height gradient and coverage, eroded by a high-frequency 3D Worley texture (slides ~34-37).
- A 128^2, 3-channel 2D curl noise texture "is non divergent and is used to fake fluid motion"; it distorts the cloud shapes and distorts the sample position of the second (detail) 3D noise "to fake the swirly distortions from atmospheric turbulence" (slides 33, 37). The curl texture is a static distortion of the detail lookup, not a time-integrated flow.
- Summary slide (48): noises are custom, "made from Perlin, Worley and Curl noise", and "all animated in a given wind direction". Wind direction varies over altitude (slide 21). High cirrus/alto layers are 2D textures "tiling and scrolling at different speeds and directions" (slide 87).
- Large-scale evolution is not noise motion: coverage, precipitation and cloud type come from a 2D weather map driven by a simulation, with a wind-speed control (slides 39-44).
- Reprojection (slides 92-93): quarter-res buffer updates 1 of 16 pixels per 4x4 block each frame; "We reproject the previous frame to ensure we have something persistent"; where reprojection fails (screen edges) the low-res result is substituted. Stated as making the shader "10x faster or more"; raw cost ~20 ms, target ~2 ms.

### 1.2 Nubis, Evolved, Schneider, SIGGRAPH 2022
Source: https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-NubisEvolved-NoVideos.pdf (read)
- Slide "Skies / Vertical Profile Model / Animating Density": the motion law for sky density is a sample-position offset, `noise_sample_position = sample_position - wind_direction * scroll_offset` (comment "Deform sample coordinates").
- Slide family "VFX / Animating Superstorm Density / The Mesocyclone": each ring samples noise at `GetRotatedPosition(sample_position, superstorm_center, time_offset * RingRotationSpeed[n])`, later with a per-ring `ring_skew[n]`: different rotation rates per layer about one center, no flow field.
- Slide "VFX / Animating Cloud Density For Skies" (code listing): the reprojection pass reads a stored per-pixel `cloud_distance` from a "CloudAttrWorkingBuffer", reconstructs `cloud_world_space = invView * (eyeRay * cloud_distance)`, applies the SAME motion law to that world position (rotate about the storm center blended by `superstorm_mask`; scroll `cloud_world_space + scroll_direction_2D * deltaTime`), then projects it with `inReprojectionMatrix` to get the previous-frame UV. The history lookup is displaced by the density's own motion, using one depth per pixel written by the march.
- The 2022 and 2023 decks show an "Aero Cloud Solver" frame (Apply Buoyant Force, Advect Density, Advect Velocity, Non-Divergence, Sourcing) among the history slides, as the offline VFX approach.

### 1.3 Nubis3 (Nubis Cubed), Schneider, SIGGRAPH 2023
Source: https://advances.realtimerendering.com/s2023/Nubis%20Cubed%20(Advances%202023).pdf (read)
- Voxel clouds, slide "Sampling Density": `inSamplePosition -= float3(cCloudWindOffset.x, cCloudWindOffset.y, 0.0) * voxel_cloud_animation_speed`. Wind offset only; no curl, no flow field.
- A comparison table lists "Evolution: Yes" for the Vertical Profile method and "Pseudomotion Only" for the Envelope method, and "Pseudomotion Only" in later tables for the voxel methods.
- March is SDF-accelerated: "Hybrid SDF, Adaptive and Jittered samples"; jitter is `animated_hash` within 250 m of camera, `static_hash` beyond.
- No temporal reprojection slide found in the extracted text.

### 1.4 Frostbite, Hillaire, SIGGRAPH 2016
Source: https://media.contentapi.ea.com/content/dam/eacom/frostbite/files/s2016_pbs_frostbite_sky_clouds.pdf (read; section 5.5.3 and the cloud intro)
- 5.5.3: clouds rendered in a single pass; each frame samples are randomly offset within their step range; current frame blended with previous "according to a constant blend factor" (EMA); "For the final frame to not look blurry when the camera is moving/rotating very fast, we re-project the previous result according to previous and current camera properties". Camera-only reprojection; no density-motion term stated.
- Text cites Guerette's visual-flow technique to give a panoramic cloud texture the illusion of motion along the wind direction, noting it has no variation of cloud shape, weather or lighting.
- Frostbite volumetric fog temporal blend, as reproduced by Flax Engine: Halton jitter, 7% current / rest history (https://flaxengine.com/?p=941) [secondary]. Godot docs: higher blend "makes ghosting much worse", moving fog volumes and lights leave a trail (https://docs.godotengine.org/en/stable/tutorials/3d/volumetric_fog.html) [secondary].
- Hillaire 2015 (Frostbite unified volumetrics) temporal section: [not verified] (158 MB PPTX; SlideShare copy cuts off before it).

### 1.5 Toft, Bowles, Zimmermann (Studio Gobo), "Optimisations for Real-Time Volumetric Cloudscapes", arXiv 2016
Source: https://arxiv.org/abs/1609.05344 (PDF read)
- Calls the Horizon 1/16-pixel scheme "a fairly complex technique which may introduce visual artifacts as a result of the 4x4 pattern by which pixels are updated".
- Alternative: far fewer march steps + random per-pixel start offset + analytic integration per step + Playdead-style TAA (Pedersen, INSIDE, 2016) on a low-res buffer. Timings, 1080p GTX 1080 (Fig. 3): half res 128 steps 128.0 ms; half res 8 steps jitter+TAA 7.5 ms; quarter res 8 steps jitter+TAA 2.4 ms.

### 1.6 UE Volumetric Clouds
- Official component docs list View/Reflection/Shadow sample-count scales and a transmittance stop threshold; nothing on temporal behavior: https://dev.epicgames.com/documentation/unreal-engine/volumetric-cloud-component-properties-in-unreal-engine [secondary].
- Forum: Mode 2 traces 1 pixel per 4x4 block and reprojects 15, "lots of latency", cloud "would leak after reprojection over opaque meshes": https://forums.unrealengine.com/t/volumetriccloud/688516 [secondary].
- Forum hypothesis (unconfirmed): a VolumetricCloud writes neither depth nor velocity, so TSR has nothing to reject history on, causing trailing and banding inside a cloud: https://forums.unrealengine.com/t/tsr-ghosting-against-a-volumetriccloud-the-player-stands-inside-every-rejection-knob-measured-none-help/2837437 [secondary].
- Bug UE-198490 (fixed 5.4): horizontal flicker lines from the temporal component; workaround `r.VolumetricRenderTarget.Mode=1`: https://issues.unrealengine.com/issue/UE-198490 [secondary].

### 1.7 Red Dead Redemption 2 clouds (Bauer, SIGGRAPH 2019)
Talk exists ("Creating the Atmospheric World of Red Dead Redemption 2: A Complete and Integrated Solution", Advances in Real-Time Rendering in Games 2019). Slides [not verified]: not among the PDF/PPTX links on https://www.advances.realtimerendering.com/s2019/index.htm; a guessed URL returned 404. Nothing about its motion or reprojection was read.

### 1.8 Shadertoy-scale nebulae
[not verified]. No Shadertoy source was read in this pass.

### 1.9 What the sources say breaks
- Pulsing and repetition: Vlachos names these as the two problems of two-phase flow-map animation (3.1).
- Stretching: a texture advected by a non-uniform flow stretches without bound unless regenerated (Neyret, 3.3).
- 4x4 update-pattern artifacts (Toft et al., 1.5); latency and leaking over opaque geometry in 1/16 modes (UE forum, 1.6).
- Camera-only reprojection fails when content moves: https://www.wayline.io/blog/altos-1-1-better-temporal-reprojection-for-volumetric-clouds-in-unity ("When the player moves their camera, the history becomes useless") [secondary]. A hobby engine skips a velocity buffer "since the clouds move slowly", uses bilinear history sampling and EMA: https://handmade.network/p/75/monter/blog/p/7201-engine_optimization__fast_high-quality_cloudscape_through_noise_and_reprojection [secondary].

## 2. Temporal reprojection of raymarched volumes whose density moves

- A volume has no single surface depth. Nubis 2022 writes a per-pixel `cloud_distance` during the march, rebuilds a world point from it, applies the density motion law to that point, then projects with the previous view-projection (1.2). This is the only shipped density-motion-aware reprojection found at a primary source.
- Horizon 2015 and Frostbite 2016 reproject with camera properties only (1.1, 1.4).
- AtmoPlugin (hobby project, GitHub PR #15): raymarch writes a "contribution-weighted" reprojection depth into alpha, taken where opacity exceeds 5%, with fallbacks surface depth, scene depth, mid-atmosphere, infinity; temporal pass reprojects the pixel center with previous camera and planet transforms: https://github.com/Upa2sdo2l/AtmoPlugin/pull/15 [secondary].
- Two hybrid depth-of-field papers reproject semi-transparent regions by average world position of far-field hits, with a 3x3 neighborhood fallback when there is no hit, and lerp by latest hit ratio during motion to reduce ghosting: https://arxiv.org/pdf/2210.04981 and https://arxiv.org/pdf/2210.06158 [secondary].
- A search for a published "transmittance-weighted depth" reprojection for clouds returned nothing; it is not an established term in anything reached.
- Reported costs and artefacts of history: higher history weight = more ghosting for moving volumes (Godot, 1.4); EMA "stable in static scenes but performs poorly under dynamic volumetric lighting", and variance clipping plus low-discrepancy jitter tested on froxel data, implemented in Frostbite (a thesis at https://gupea.ub.gu.se/items/e05613c2-e1d4-48b0-b41e-c32f4b0ecbae, blocked by bot protection; only a search summary was seen) [secondary]; UE TSR trailing when no depth/velocity is written (1.6).
- Playdead's TAA (Pedersen, GDC 2016) is the history-reprojection reference cited by both Frostbite and Toft et al.

## 3. Flow maps, curl noise, advected textures

### 3.1 Flow maps, Vlachos, Valve, SIGGRAPH 2010 "Water Flow in Portal 2"
Source: https://www.advances.realtimerendering.com/s2010/Vlachos-Waterflow(SIGGRAPH%202010%20Advanced%20RealTime%20Rendering%20Course).pdf (read)
- Builds on Max and Becker 1995 "Flow visualization using moving textures": offset the UV by the 2D flow vector scaled by time; "distortion looks reasonable for the first 1/3 of uv space".
- "Blend the short animated segment in two layers. Each layer is offset half a phase so we can hide the restart for each layer."
- Two named problems: repetition ("the same normals will flow through the same point") and pulsing ("the surface appears to pulse in a repeating pattern"). Repetition solved by offsetting the second layer's sample; pulsing solved by a noise texture that offsets phase per pixel. Cost vs two scrolling normal maps: +2 texture fetches, +21 ALU instructions.
- Colors (Portal 2): distortion interval offset to -fraction..+fraction so the peak is at rest, favouring less distortion than for normals. Listed future work: dynamic per-frame flow vectors.
- Catlike Coding (https://catlikecoding.com/unity/tutorials/flow/texture-distortion/) [secondary]: p=frac(time), weight `1 - |1 - 2p|`, second pattern shifted half a period so weights sum to 1; Perlin noise in the flow map's A channel added to time staggers the pulse as a wave; per-phase "jump" vector times floor(time) shifts UVs only while weight is zero, limited to about +/-0.25, loop length is LCM of jump denominators; strong deformation makes compressed flow maps blocky; large-area flow maps have low effective resolution.
- Golus flow-map notes: [not verified].

### 3.2 Curl noise, Bridson, Hourihan, Nordenstam, TOG 26(3) 2007
Source: https://www.cs.ubc.ca/~rbridson/docs/bridson-siggraph2007-curlnoise.pdf (read, 3 pages)
- Velocity = curl of a noise-valued potential (2D scalar potential rotated 90 degrees; 3D three noise functions, "the same noise function evaluated at large offsets"). Divergence-free by construction: "No sources or sinks ('gutters')"; contrasted with plain noise as velocity, which "will generally contain many sinks" where particles accumulate.
- Derivatives by finite difference (displacement 10^-4 of the domain "works fine in single precision"). With noise at length scale L: vortices of diameter about L, speeds O(1/L). Several octaves with power-law amplitude falloff resemble Kolmogorov turbulence.
- Time variation: "achieved very simply by using time-varying noise"; pseudo-advection of Flow Noise mentioned as untried.
- Boundaries: potential multiplied by smooth ramp of distance to solids, `ramp(r) = 15/8 r - 10/8 r^3 + 3/8 r^5`.
- The paper names particle advection as a primary use. It says Perlin and Neyret's Flow Noise textures "cannot be used for moving particle systems".
- Perlin and Neyret, "Flow Noise" (SIGGRAPH 2001 sketch): rotating gradients over time plus pseudo-advection of small octaves by large ones; needs noise with analytic derivatives. https://evasion.inrialpes.fr/~Fabrice.Neyret/flownoise/index.gb.html ; Gustavson's simplex noise code exposes the derivative and a rotation parameter `t` for it (https://www.itn.liu.se/~stegu76/simplexnoise/DSOnoises.html) [secondary].

### 3.3 Advected textures, Neyret, SCA 2003
Source: https://evasion.inrialpes.fr/Publications/2003/Ney03/neyret161.pdf (read)
- A noise texture advected by a flow stretches; periodic regeneration prevents stretch from growing on steady flows, but the regeneration rate (latency) is hard to tune and blending regenerated layers causes ghosting and contrast loss.
- Method: accumulate local deformation (time integral of the strain-tensor norm) per layer since its last regeneration; blend layers of different latency by a criterion on that accumulated deformation against a user target (analogous to MIP-mapping); each layer blends three phase-shifted regenerated textures; proposes a procedural-texture blending scheme that avoids contrast loss.
- Needs a velocity field to compute deformation; the paper's flow came from a low-resolution fluid simulation.

### 3.4 Does a span / Lipschitz bound survive advection or a time-dependent warp
No source found states it. What was read:
- Seyb, Jacobson, Nowrouzezahrai, Jarosz, "Non-linear sphere tracing for rendering deformed signed distance fields", TOG 38(6) 2019 (PDF read): stepping in deformed space needs the Lipschitz constant of S o D^-1; "for affine transformations this is trivial [Stander and Hart 1994], but for more complicated deformations Lipschitz constants can be tedious or even impossible to compute analytically"; a global constant degrades step size where it is large; local constants "require a non-negligible computation whenever the deformation changes, i.e., every frame". Their remedy: trace in undeformed space as an ODE, avoiding the inverse deformation. https://par.nsf.gov/servlets/purl/10172295
- Galin et al., "Segment Tracing Using Local Lipschitz Bounds", CGF 39(2) 2020: local Lipschitz bounds per ray segment, bounds derived per operator and primitive (abstract/search summary only) https://diglib.eg.org/handle/10.1111/cgf13951 [secondary].
- Bridson 2007: speed of a noise-derived velocity is O(1/L) per octave (informal amplitude bound).
- Neyret 2003: the quantity that grows under advection is the time-integrated strain-tensor norm.
- A bound on the time derivative of a warped field, or a space-time Lipschitz constant: [not found].

## 4. Particle layers coherent with a volumetric field
- No shipped system found that drives volumetric density and a particle layer from one stated shared motion law.
- Niagara Curl Noise Force: reads a pre-baked, medium-resolution tiling curl-noise field by default, or computes a Perlin-derived curl at higher cost; docs do not state the divergence-free property: https://dev.epicgames.com/documentation/unreal-engine/particle-spawn-group-reference-for-niagara-effects-in-unreal-engine [secondary].
- Frostbite 2015 couples particles to fog by voxelizing particle extinction into the same froxel volume, so they are lit together (shared volume, not shared velocity): https://www.ea.com/news/physically-based-unified-volumetric-rendering-in-frostbite [secondary].
- Flax: volume particles write into the fog's 3D textures: https://docs.flaxengine.com/manual/graphics/fog-effects/volumetric-fog.html [secondary]. Godot demo of particle-faked fog animated with wind/attractors; "billboards make the fog appear to rotate with the camera": https://github.com/Bauxitedev/volumetric-fog-demo [secondary].
- Bridson's curl noise is built for particle advection and the divergence-free property is its stated reason (3.2).
- Nubis 2022 / Nubis3 slides read carry no particle layer sharing the density motion law.

## 5. Not reached
RDR2 (Bauer 2019) slides; Hillaire 2015 temporal section; Golus flow-map notes; any Shadertoy nebula source; UE internals beyond forum reports; any source bounding the time derivative of a warped noise field.
