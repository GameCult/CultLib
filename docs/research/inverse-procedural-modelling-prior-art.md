# Inverse procedural modelling: describing messy meshes as programs

Eyes pass, 2026-10-10. Operator: "What if the model had some trash geo and its goal is to describe it parametrically?" Context: Tripo hard-surface hulls; CultMath intervals and affine forms on main, tape/brush grammar parked (docs/cultmath-tape-target.md); the earlier GameCult/VibeGeometry attempt (2026-05). Facts only; tags mark FETCHED, SEARCH (search summary only) and KNOWLEDGE (unverified).

# Eyes facts: inverse procedural modelling (mesh -> parametric program)
Date 2026-10-10. Tags: FETCHED (page/file read this session), SEARCH (search-result digest only), KNOWLEDGE (unverified recall).
Search digests come from a small model; SEARCH numbers are not paper-verified.

## 0. What exists in GameCult
- FETCHED F:\Projects\CultLib\docs\cultmath-tape-target.md: steps 2-5 (tape, pruning, HLSL interpreter, meshing) PARKED (2026-10-02,
  ruling tape-target-unparks-when-asura-stable). Step 1 intervals + affine forms are on main (docs/cultmath-interval-ground-cut.md).
  The "grammar of nested brushes" is operator intent only (quote 2026-10-02: space station from "a grammar of nested brushes");
  doc says the station project is "a separate project with its own target". Chosen shape (c): port Fidget ideas into CultLib (Fidget =
  MPL-2.0 Rust, v0.5.1). Meshing plan: surface nets + Asura tile pass (Newton refinement onto f=0, crease snapping from field gradients);
  "Thin authored bevels can be shaded by their analytic normals". Tape gradients "reuse CultMath's analytic-gradient primitives".
- FETCHED grep CultLib\docs for brush/tape: "brush" hits in cultmath-interval-ground-cut.md are Aetheria noise/gravity-well field brushes
  (PowerBrush (1-x^2)^16 etc.), not CSG brushes. No CSG brush grammar or tape code exists in CultLib; only the parked doc.
- FETCHED (voidbot) separate repo GameCult/GameCult.Geometry.Csg ("CultGeometry CSG", Rust; local F:\Projects\GameCult.Geometry.Csg).
  Mesh-based convex plane-splitting CSG (not SDF), RealtimeCSG-shaped API: ordered Additive/Subtractive/Intersecting brush streams, boxes,
  oriented boxes, CylinderZ/DomeCapZ/FloretArm, dirty-frontier, OBJ output, plus domain-tree/selected-cut LOD schemas (src/cult_geometry).
  Doc text: "agents should revise intent by moving, adding, or renaming brushes, not by poking triangles"; "brush list is the durable
  design surface. The mesh is a cache."

## 0b. The earlier "vibe geometry" attempt
- FETCHED voidbot gamecult-site GameCult/Projects/VibeGeometry.md: repo GameCult/VibeGeometry, started 2026-05-04, last public push
  2026-05-07; "agent-authored Blender scene generation: geometry nodes, Python builders, semantic graph contracts, render evidence, and
  the ongoing war against decorative node spaghetti". Org ledger: "Started as architecture and persistence scaffolding, then dived almost
  immediately into CSG grammar, parity fixtures, native bridge probing, mesh generation, and performance work."
- FETCHED local git F:\Projects\VibeGeometry-cultgeometry-rename (93 commits). Commit 09bff8c (2026-05-29, "Focus VibeGeometry on vg-csg")
  deleted the scene-generation tree; recoverable at 09bff8c^: docs/research/procedural-doctrine.md (~1000-line "spatial reasoning
  playbook"), dream-machine-grammar.md, geonodes-translation-corpus.md, ircss-french-houses-* (translations of Blender node graphs into
  Geometry Script Python), builds/{aetheria-bloom-habitat, lucent-tether-habitat, dream-machine-level, industrial-maintenance-loop}.md,
  notes/architecture-rationale.md, a "reusable procedural helper library" (aa4d49d). The DSL was Blender Python via Geometry Script
  (fork F:\Projects\geometry-script; upstream PR carson-katri/geometry-script#69 linked by Metacrat 2026-05-04) emitting geometry-node
  graphs, plus the Rust brush DSL (LevelDsl: solid_box, cut_box, solid_oriented_box, dome_cap_z, floret_arm).
- FETCHED notes/architecture-rationale.md @09bff8c^, section "Core Failure": "The central failure mode is local plausibility without global
  coherence. An agent can write plausible Blender Python, create a large node graph, and still lose the visual machine ... the script
  runs, the graph exists, the render has objects in it, and somehow the target has been replaced by decorative procedural fog".
  "Seductive proxies": valid Python, non-empty renders, high node counts, pretty materials, parameter sliders.
  procedural-doctrine.md: "Ornament in the wrong frame is not detail. It is a confident lie". Commit titles record frame errors:
  "Correct Bloom terrace slum orientation", "Correct Lucent dome spin-gravity frame", "Move Lucent ray florets outside dome".
- FETCHED VibeGeometry README: "failed experiments are useful without being allowed to hold a steering wheel"; old mission "deleted
  from the live tree"; live mission narrowed to runtime CSG (domain tree -> LOD CSG -> selected cut -> triangles).
- NOT FOUND: no indexed operator or agent statement that models "weren't good enough at spatial reasoning to use the DSL". voidbot
  search_history (Discord) returned only unrelated hits. The repo docs above are the only failure statements in project words
  (coherence and frame errors; the phrase "spatial reasoning" appears only in the playbook title and its body).

## 1. Inverse CSG / shape-program synthesis
Most train or test on DeepCAD / ShapeNet / ABC / Infinigen; none retrieved was evaluated on noisy AI-generated hard-surface meshes.
- InverseCSG (Du et al., SIGGRAPH Asia 2018, MIT). SEARCH: mesh/point samples -> CSG program; geometric processing (RANSAC-style primitive
  detection) turns the mixed discrete/continuous problem into a purely discrete one; careful point samples guide a discrete search;
  divide-and-conquer segmentation; program synthesis (solver) per segment. Claims >100 primitive parts, compact programs, beats prior
  methods on compactness/runtime. D2CSG authors (SEARCH) criticise it: RANSAC needs tuning, noisy initial primitives, long search.
  Project page inversecsg.csail.mit.edu lists code; licence not checked.
- CSGNet (Sharma 2018): KNOWLEDGE only; supervised, voxel/2D -> CSG sequence.
- UCSG-Net (Kania, NeurIPS 2020). SEARCH: unsupervised; predicts box/sphere params; differentiable indicator on SDF; several CSG layers with
  soft operator choice; structure and params learned jointly by gradient descent; autoencoding scope (2D/3D), no mesh-to-program at test
  for unseen exotic shapes beyond the trained net.
- CAPRI-Net (CVPR 2022). SEARCH: axis-aligned quadric primitives, fixed 3-layer assembly; ABC point-cloud Chamfer 0.09 vs UCSG 1.43; ~5.3 vs
  12.92 convex shapes. D2CSG (2023), CSG-Stump (2021): SEARCH, unsupervised compact trees. SECAD-Net (CVPR 2023): SEARCH, sketch-extrude,
  editable; CD 2.94/4.20 (ABC/Fusion) vs UCSG 3.14/4.45.
- Point2CAD (Liu et al.): SEARCH only that a repo exists (jofcodeusal/point2cad mirror, Docker toshas/point2cad); method not retrieved.
  Point2Cyl (CVPR 2022): SEARCH, supervised net to extrusion cylinders. DeepCAD (Wu 2021): KNOWLEDGE, ~178k sketch-extrude sequences.
- ShapeAssembly (Jones 2020, TOG): SEARCH; programs of cuboid proxies attached to each other with free parameters; extraction from PartNet.
  ShapeMOD (2021): SEARCH; discovers macros across a corpus minimising calls and free parameters. Repos/licences not found by search
  (KNOWLEDGE: github rkjones4/ShapeAssembly exists; unverified).
- CAD-Recode (Rukhovich et al., ICCV 2025, arXiv 2412.14042). FETCHED abstract: point cloud -> CadQuery Python (sketch-extrude); point-cloud
  projector + small pretrained LLM decoder; trained on 1M procedurally generated CAD sequences; eval DeepCAD, Fusion360, CC3D (real scans);
  output usable by off-the-shelf LLMs for editing and QA. SEARCH (unverified): Qwen 0.5B/1.5B decoders; DeepCAD mean CD 3.43 -> 0.30, IoU
  77.6 -> 92.0 vs CAD-SIGNet; test time: 10 sampled candidates executed, min-Chamfer kept. Code github.com/filaPro/cad-recode (SEARCH);
  HF dataset filapro/cad-recode is CC-BY-NC-4.0 (SEARCH); repo licence unchecked. Structure by autoregressive decoding, params as literal
  tokens, re-ranking by Chamfer. Abstract says nothing on fillets/chamfers.
- MeshCoder (Dai et al., NeurIPS 2025, arXiv 2508.14879). FETCHED: 16,384-point cloud -> triplane tokenizer -> Llama-3.2-1B (LoRA) ->
  Blender Python. API: cube/cylinder/UV-sphere/cone/torus, Translation (sweep 2D section along 3D trajectory), Bridge Loop, Boolean
  (union/intersect/diff), Array, Fill Grid, Spoon, Fork. Data: ~10M part-code pairs, ~1M object-code pairs, 41 categories (Infinigen
  Indoor). Table 2 averages CD x1e-2 / IoU%: Shape2Prog 6.00/45.0, PLAD 1.87/67.6, MeshCoder 0.06/86.75. Worst: Fork 58.9 IoU, CeilingLight
  65.8, Array parts 78.9. Limits: human-made objects only; geometry only; training objects kept only if every part CD < 5e-3. LLM does not
  edit; edits are manual param changes; GPT-4o used only for QA on the code. Output is part-segmented. Licence not stated in paper.
- CADCodeVerify (Alrashedy, ICLR 2025, arXiv 2410.05340). SEARCH: text prompt -> CadQuery (GPT-4); VLM writes/answers validation questions
  about renders and refines; CADPrompt benchmark (200 objects); GPT-4 point-cloud distance -7.30%, compile rate +5.5% (v2; v1 said 5.0%
  success). Target is a prompt, not a mesh.
- Newer render-loop agents (SEARCH): SEIG "Thinking in Blender" (arXiv 2606.02580): single reference image -> staged Blender code (geometry,
  materials, composition, lighting), VLM generator + verifier per stage; FETCHED abstract gives no numbers, no code link. LL3M (2508.08228):
  text -> Blender with critic reading renders. ShapeCraft (2510.17603): renders bbox/part/global views as feedback. SceneCraft (2403.01248):
  render-critique-revise. Not found by search: "3D-Premise", "LLMto3D", Img2CAD/Text2CAD specifics.

## 2. Classical primitive fitting as a front end
- Efficient RANSAC (Schnabel/Wahl/Klein, CGF 2007): SEARCH/FETCHED page text: planes, spheres, cylinders, cones, tori in unorganised clouds;
  multi-million-point sets "within less than a minute". CGAL Shape Detection implements it plus Region Growing (Lafarge&Mallet). CGAL is
  dual GPL/LGPL + commercial; the Shape Detection package's own licence NOT confirmed (digest guessed GPL).
- GlobFit (Li/Wu/Chrysanthou/Sharf/Cohen-Or/Mitra, SIGGRAPH 2011): SEARCH; starts from RANSAC primitives, discovers global relations
  (orientation, placement, equality; symmetry) and enforces them by constrained optimisation; assumes man-made, repeated, globally aligned
  parts. Code: archived code.google.com/archive/p/globfit, "research use only" wording.
- Mitra 2006 symmetry detection, SPFN (2019): KNOWLEDGE/SEARCH only. ParSeNet (2020): SEARCH; B-spline patch decomposition, beats RANSAC
  and SPFN baselines. No repos/licences retrieved.
- What fits supply as data: exact primitive parameters with inlier sets and residuals, segment labels, relation graph (GlobFit).
  InverseCSG uses the primitives to make the problem discrete. Recorded weakness: noisy primitives, parameter tuning (SEARCH).

## 3. Differentiable fitting of SDF/CSG programs
- UCSG-Net (above): soft operator selection; structure learned jointly.
- Fuzzy Boolean differentiable CSG (dgp.toronto.edu/~hsuehtil/pdf/fuzzyBoolean.pdf): SEARCH; gradient descent over boolean and primitive
  parameters; notes prior work pre-fixes tree structure and operators and optimises only primitive params; replaces min/max with a unified
  fuzzy operator so the operation type is optimised. Costs listed: analytic SDF per primitive type, per-iteration SDF evaluation, image
  losses need differentiable sphere tracing.
- DiffCSG (arXiv 2409.01421): SEARCH; differentiable rasterisation of mesh CSG, image-space gradients to primitive params; image-guided
  editing, not mesh fitting.
- ResFit, Residual Primitive Fitting with SuperFrusta (arXiv 2512.09201): SEARCH; one analytic SDF primitive with 8 params (dilation, taper,
  bulge, onion hollowing, profile roundness, axial scaling); alternates global shape analysis and local primitive optimisation; code "to be
  open-sourced upon acceptance".
- Evolutionary mesh -> CSG abstraction (SEARCH; authors not retrieved): fast, coarse. Sigmoid-over-SDF soft occupancy with alternating
  add-primitive / joint-refine loop (SEARCH, primitive-scene paper).
- Not found: a published gradient fit of an SDF program with bevel/round parameters to a messy hard-surface mesh.
- Handling of discrete structure across sources: soft selection (UCSG, CSG-Stump, fuzzy Boolean); pre-fixed structure (InverseCSG after
  RANSAC; CAPRI-Net fixed layers); greedy add/prune (ResFit); neural token decoding (CAD-Recode, MeshCoder).

## 4. LLM spatial reasoning in 3D code generation
- BlenderGym (Gu et al., CVPR 2025, arXiv 2504.01786). FETCHED: 245 start-goal Blender scene pairs, five tasks (placement, lighting,
  procedural material, blend shape, procedural geometry); the VLM sees start and goal and edits Blender code (target-matching setting).
  Table 1 best-VLM vs human: geometry Chamfer 1.120 (GPT-4-Turbo) vs 0.334; geometry photometric loss 6.747 vs 1.269; placement CD 8.324 vs
  1.532; blend shape CD 0.904 vs 0.399. Paper: VLMs "particularly poorly in procedural material and geometry editing"; all open-source
  models except MiniCPM failed to produce executable code on >75% of geometry instances. Verifier agreement with humans: human 0.79,
  Claude 3.5 Sonnet 0.66, random 0.5. Scaling a small verifier (InternVL2-8B, k 1 -> 64 over 32 candidates) beat unscaled GPT-4o and
  Claude 3.5 on blend shape. Low compute: less verification share (0.33); high compute: more (0.62-0.73). Models are 2024-25 era. Code
  licence not in retrieved text.
- BenchCAD (arXiv 2605.10865): SEARCH; 17,900 CadQuery programs, 106 part families; frontier models recover coarse outer geometry but fail
  faithful parametric programs; sweeps/lofts/twist-extrudes replaced by sketch-extrude; single-literal errors (5.6 vs 5.85) at IoU >= 0.9.
  BenchCAD-Edit: text instruction sets the upper bound; the image is "at best a clarifier and at worst a distractor" for thinking models.
- Text2CAD-Bench (2605.18430): SEARCH; execution failures even at easy level; sweep/loft/shell widely fail; executing != correct.
- Text-to-B-Rep paper (2603.11831): SEARCH; spatial misalignment of primitives, omitted fillets, geometry contradicting described layout;
  authors blame weak precise 3D spatial reasoning.
- "Foundation Models for Automatic CAD Generation" (2607.05573): SEARCH, single source; numeric analytic feedback peaked at round 1; VLM
  critique of renders kept improving in rounds 2-3 for DeepSeek/Qwen; VLM critics struggle on rotationally symmetric geometry.
- Multi-view conditioning beat single-view and sketch on Chamfer/F-score in a B-Rep paper (SEARCH, not CadQuery).
- Target vs description: BlenderGym gives a target and still shows a large human gap; CAD-Recode and MeshCoder avoid prompting by training
  point-cloud encoders; no controlled prompted-LLM study of target-vs-description was found.
- MindCube spatial study (SEARCH): errors 20% final reasoning step, 13% 3D reconstruction, 6% generated programs.

## 5. Hard-surface specifics
- SDF rounding (Quilez distfunctions, SEARCH): sdRoundBox = box SDF minus r, an iso-offset so the shape grows by r. Polynomial smin with k
  as blend band; Quilez marks smin as "bound" not exact. Chamfer/fillet operators not retrieved; KNOWLEDGE: hg_sdf (Mercury) has
  fOpUnionChamfer / fOpUnionRound / fOpUnionStairs (unverified here).
- MeshCoder (FETCHED): no fillet/chamfer primitive; booleans, sweeps, bridge-loop, arrays only.
- BenchCAD / text-to-B-Rep (SEARCH): omitted fillets/refinement features named as failure; sweeps/lofts degrade to extrudes.
- CAD-Recode/DeepCAD data are sketch-extrude (SEARCH/KNOWLEDGE); fillet/chamfer not covered in what was retrieved.
- InverseCSG output: boolean of primitives, no blends (KNOWLEDGE). CAPRI-Net quadrics: smooth surface in one primitive (SEARCH). ResFit
  primitive has a profile-roundness parameter (SEARCH).

## Gaps
- Operator/agent words on why VibeGeometry failed: not indexed (see 0b).
- Not read at body level: CAD-Recode fillet handling, InverseCSG experiments, GlobFit relation list, SEIG numbers, ResFit results.
- Licences unverified: InverseCSG, ShapeAssembly, CAD-Recode repo, MeshCoder, GlobFit, CGAL Shape Detection, Point2CAD.
