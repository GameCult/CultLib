# CultMath tapes: interval-pruned field evaluation on CPU and GPU

Status: steps 2-5 (tape, pruning, HLSL interpreter, meshing) are **parked**
(2026-10-02; ruling tape-target-unparks-when-asura-stable). The affine-forms
piece was unparked on 2026-10-03 (ruling affine-forms-unparked) and is on main
(156782da), alongside step 1's intervals; its map and as-built record are in
`docs/cultmath-interval-ground-cut.md`. This note records the direction for the
parked steps so they can be picked up without re-deriving it.

## Intent

Evaluate large implicit fields, meaning deeply nested CSG trees of brushes with
thousands of nodes, cheaply enough to mesh at many levels of detail and to
raymarch. The operator's words (2026-10-02): "I would love to design a space
station out of a grammar of nested brushes, and have the ability to
automatically mesh it at multiple levels of detail so I can get arbitrarily
close to its tiny details without melting my GPU."

GameCult has no deep CSG trees today because nothing could evaluate them
cheaply. The capability comes first, and the content follows.

## Prior art

[Fidget](https://www.mattkeeter.com/projects/fidget/) by Matt Keeter
(MPL-2.0, Rust, v0.5.1 on 2026-09-27; repo `mkeeter/fidget`).
- It compiles an expression to a register-allocated bytecode tape.
- It evaluates that tape as points, SIMD batches, intervals, and forward-mode
  gradients.
- The central trick is interval evaluation over a region. When it proves that
  one operand of a `min` or `max` always wins, the tape for that region is
  rewritten without the other branch. In the project's example, the tape
  shrinks from 254 clauses to about 20 at pixel level.
- `fidget-jit` targets x86-64 and AArch64.
- `fidget-wgpu` has a WGSL tape interpreter, interval ops and tape
  simplification on the GPU (`interval_ops.wgsl`, `tape_interpreter.wgsl`,
  `tape_simplify.wgsl`).
- `fidget-mesh` implements Manifold Dual Contouring. It is known to be weak on
  thin features.
- Halfspace is the IDE codesigned with `fidget-wgpu` (Rust, egui and wgpu,
  MPL-2.0).

## Chosen shape (option c)

The ideas are ported into CultLib and owned here. Fidget is not a dependency.
Unity renders HLSL on its own graphics backend, so `fidget-wgpu` cannot run
inside it.

Three options were weighed on 2026-10-02:

| Option | What it is | Trade |
|---|---|---|
| a | Fidget as a Rust native plugin in Unity | Fastest demo; costs per-platform native builds, an FFI boundary, and an upstream that warns of breaking changes |
| b | Offline bake with a Rust tool; ship meshes | Simplest; the baked depth caps how close a viewer can get |
| c | CultLib owns tape, interval and pruning in C# and HLSL | Most coherent end state; the largest code liability we would own |

The operator parked (c). An (a) spike to validate authoring and LOD behaviour
before owning code was suggested but not ruled.

Sketch, to be replaced by the target pass:

1. **CultMath `interval`** in C# and HLSL with parity tests:
   - arithmetic, `min`/`max`, `smin`, `sqrt`, `clamp` and `smoothstep`;
   - interval or Lipschitz bounds for every CultMath noise primitive.

   This step needs no tape. It stands alone, and is the first cut to take even
   if the rest stays parked.
2. **Tape**: an expression graph lowered to bytecode, with point, interval and
   gradient evaluators in C#. Gradients reuse CultMath's analytic-gradient
   primitives, which Asura cut 2a built.
3. **Pruning**: per-region tape simplification driven by interval results.
4. **HLSL interpreter** for the same bytecode, with interval ops and pruning.
   Fidget's MPL-2.0 WGSL is the reference. MPL code keeps its own MPL-headed
   files, as Phacelle does.
5. **Meshing**: an octree of chunks with seams stitched between LOD levels.
   - The default is surface nets (GameCult.Geometry, Asura Cut 2) plus
     Asura's tile pass: Newton refinement onto `f = 0`, then crease snapping
     from field gradients (Asura Cut 6c).
   - The operator's lean (2026-10-02) is that gradient work beats dual
     contouring here. Thin authored bevels can be shaded by their analytic
     normals, and the refined positions can be iterated further.
   - Dual contouring is not planned. The tile pass on station brushes is its
     test.

## Truth and lowerings

The field is data: a typed CultCache document holding the expanded tree. A
generator, such as a brush grammar, expands into that document, and everything
downstream caches it. The CPU evaluator, the HLSL interpreter, the mesher and
collision are all lowerings of that one document. This is how "one
implementation of the field" survives having both CPU and GPU evaluators.
Asura's invariant 2 had to forbid CPU evaluation because its field is HLSL
source.

## Known consumers

- **Aetheria volumetric raymarching** (clouds, nebulae). Step 1 alone gives it
  empty-space skipping. Coverage and shape terms carry the culling, because
  detail octaves only bound to ±amplitude.
  - Prerequisite: Aetheria's shaders must use CultMath noise, not their own
    copies. That is already owed at Aetheria's CultMath pin bump (see the Asura
    cut map, snoise release follow-ups).
- **Space stations from a brush grammar**, meshed in LOD chunks around the
  camera. This is a separate project with its own target. This campaign
  supplies its evaluator.
- Asura does not need this campaign. Its field is noise-dominated, and its
  `A_max` shell and band-limiting already give the cheap culling.

## Open before unparking

- Run the (a) spike first, or go straight to (c)?
- Pick the interval representation (plain intervals or affine arithmetic) and
  the noise bound strategy.
  Affine forms for noise and warp were unparked on 2026-10-03 (ruling
  `cultmath-tapes:ruling:affine-forms-unparked`): one shared symbol, mapped in
  `docs/cultmath-interval-ground-cut.md`, "Pass 5".
- Choose the bytecode register model for the HLSL interpreter, given GPU
  register pressure.
- Decide whether the brush-grammar station project lives in Aetheria or in
  its own repo.
