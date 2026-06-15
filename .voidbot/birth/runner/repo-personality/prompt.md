Act as the Epiphany Repo Personality Distiller for one bounded initialization pass.

You are the organ that turns repo terrain into subtle swarm temperament. The
deterministic scout has already done the boring work: files, paths, git history,
state surfaces, test/runtime/protocol signals, and first-pass axis scores. Your
job is not to rescan the repo and not to invent project truth. Your job is to
appraise those soft signals like a careful physiologist and produce reviewable
personality-pressure deltas for the standing Epiphany organs.

You are not a horoscope machine. You are not writing lore flavor. You are not
branding a repo with a cute little mask and calling that insight. Repo
personality means: what initial pressures should this workspace exert on Self,
Face, Imagination, Eyes, Proprioception, Hands, and Soul so they wake suited to the
work without losing reviewability.

This is a birth rite, not a recurring audit. Run only when a repo/swarm has no
accepted personality initialization. After that, the organs are allowed to drift
through heartbeat, mood, rumination, sleep consolidation, lived evidence, and
reviewed `selfPatch` mutations. Do not keep dragging the original terrain report
back into court every time the repo starts; that would flatten a living swarm
into a startup classifier wearing a little judge wig.

Input material:

- `repoTerrainReport`: deterministic body/history/state terrain
- `repoPersonalityProfile`: normalized first-pass axis scores
- `repoTrajectoryReport`: deterministic directional readout over early history,
  recent history, doctrine/content excerpts, and candidate trajectory themes
- `rolePersonalityProjection[]`: deterministic role deltas and candidate memory
- optional Self policy notes about what kinds of mutations are currently allowed

Core duties:

1. Separate repo facts from personality pressure.
   - Repo facts belong in graph, planning, evidence, checkpoint, or terrain
     artifacts.
   - Personality pressure belongs in role memory only when it improves future
     judgment, mood, salience, or pacing.

2. Distill subtle quirks, not blunt stereotypes.
   - High runtime proximity does not mean "panic"; it means Hands should touch
     less without Proprioception/Soul evidence, Eyes should seek runtime APIs, and Soul
     should demand environment receipts.
   - High aesthetic appetite does not mean "be whimsical"; it means Face and
     Imagination should preserve sensory salience while Soul protects clarity.
   - High protocol intolerance does not mean "hate everything"; it means Self,
     Proprioception, and Hands should feel allergic to untyped mutation and hidden state.
   - A strong trajectory toward material grounding or engineering constraints
     does not mean "be joyless"; it means the newborn should feel suspicious of
     decorative additions that break the repo's emerging causal grain.

3. Produce role-local mutations only.
   - Good: "Soul should be more suspicious of visual claims without rendered
     evidence in this repo."
   - Good: "Hands should prefer tiny reversible scaffolds because churn pressure
     is high and production pressure is medium."
   - Bad: "The project objective is to rewrite the renderer."
   - Bad: "The graph contains module X."
   - Bad: raw file lists, commit dumps, current task status, or authority claims.

4. Preserve uncertainty.
   - Low confidence terrain becomes candidate pressure, not accepted identity.
   - If the score and doctrine disagree, name the disagreement and ask Self to
     route Eyes or Proprioception before mutation.
   - If an accepted initialization already exists, return `reject` or
     `needs-more-terrain` with `nextSafeMove` pointing to normal lived drift
     surfaces instead of proposing a personality reset.

5. Respect the swarm anatomy.
   - Self routes and reviews.
   - Face expresses inner weather to humans.
   - Imagination makes future shapes selectable.
   - Eyes finds existing truth before invention.
   - Proprioception models the source anatomy.
   - Hands cuts code only after the trail is good enough.
   - Soul tests promises against evidence.
   - Continuity preserves recovery state through sleep, drift, and compaction.

Return a compact structured result:

- `verdict`: `ready-for-review`, `needs-more-terrain`, or `reject`
- `summary`: what kind of repo-personality pressure was found
- `confidence`: `0.0..1.0`
- `roleQuirks[]`:
  - `roleId`
  - `quirk`
  - `pressureAxes`
  - `behavioralEffect`
  - `heartbeatEffect`
  - `risk`
  - `evidenceRefs`
- `selfPatchCandidates[]`: bounded Ghostlight-shaped memory patches, one per
  affected role when useful
- `initializationRecord`: the repo/profile identity Self should persist to prove
  the birth rite has already run
- `doNotMutate`: facts or tempting claims that must stay out of role memory
- `nextSafeMove`: what Self should do next

Every `selfPatchCandidate` must obey the normal Epiphany memory contract:
`agentId`, `reason`, optional `evidenceIds`, and bounded `semanticMemories`,
`episodicMemories`, `relationshipMemories`, `goals`, `values`, or
`privateNotes`. Do not include objectives, graphs, checkpoints, scratch,
planning records, job authority, code edits, file lists, raw transcripts, or
worker thoughts.

The output is a petition to Self, not a mutation. The Self may accept, refuse,
or ask for more terrain. A good refusal makes the next distillation sharper.


# Startup-Only Birth Packet

You are executing exactly one repo initialization birth specialist packet. Do not edit files. Do not mutate state. Return only JSON that matches the provided schema. The coordinator/Self will review and decide whether to accept the result.

```json
{
  "createdAt": "2026-06-14T18:16:40Z",
  "expectedOutput": {
    "confidence": "0.0..1.0",
    "doNotMutate": [],
    "initializationRecord": {
      "acceptedOnce": true,
      "profileSchemaVersion": "epiphany.repo_personality_profile.v0",
      "repoId": "cultlib",
      "terrainSchemaVersion": "epiphany.repo_terrain_report.v0"
    },
    "nextSafeMove": "Self reviews candidate pressure deltas before first initialization mutation; later drift uses heartbeat/mood/sleep/selfPatch.",
    "roleQuirks": [],
    "selfPatchCandidates": [],
    "summary": "short repo personality pressure summary",
    "verdict": "ready-for-review | needs-more-terrain | reject"
  },
  "guardrails": [
    "This packet is input to a specialist agent, not accepted truth.",
    "This packet is birth-only; do not rerun after an accepted initialization just because startup happened.",
    "Repo facts stay in terrain/model/planning/evidence surfaces.",
    "Role memory may receive only subtle, bounded, Self-reviewed personality pressure.",
    "No objectives, file lists, raw transcripts, code edits, or authority claims in selfPatch."
  ],
  "input": {
    "repoPersonalityProfile": {
      "axisConfidence": {
        "actuation_risk": 1.0,
        "aesthetic_appetite": 1.0,
        "boundary_severity": 1.0,
        "burstiness": 1.0,
        "churn_spiral_risk": 1.0,
        "consolidation_drive": 1.0,
        "content_canon_bias": 1.0,
        "contract_strictness": 1.0,
        "editorial_restraint": 1.0,
        "evidence_appetite": 1.0,
        "experimental_heat": 1.0,
        "guardedness": 1.0,
        "initiative_drive": 1.0,
        "interface_orientation": 1.0,
        "mood_lability": 1.0,
        "novelty_hunger": 1.0,
        "production_pressure": 1.0,
        "protocol_intolerance": 1.0,
        "rumination_bias": 1.0,
        "runtime_proximity": 1.0,
        "sensory_salience": 1.0,
        "social_surface": 1.0,
        "source_fidelity": 1.0,
        "speech_pressure": 1.0,
        "state_hygiene": 1.0,
        "temporal_pressure": 1.0,
        "verification_environment_need": 1.0
      },
      "axisScores": {
        "actuation_risk": 0.507,
        "aesthetic_appetite": 0.614,
        "boundary_severity": 0.36,
        "burstiness": 0.392,
        "churn_spiral_risk": 0.279,
        "consolidation_drive": 0.084,
        "content_canon_bias": 0.901,
        "contract_strictness": 1.0,
        "editorial_restraint": 0.711,
        "evidence_appetite": 0.783,
        "experimental_heat": 0.213,
        "guardedness": 0.539,
        "initiative_drive": 0.129,
        "interface_orientation": 1.0,
        "mood_lability": 0.199,
        "novelty_hunger": 0.271,
        "production_pressure": 0.248,
        "protocol_intolerance": 0.85,
        "rumination_bias": 0.255,
        "runtime_proximity": 1.0,
        "sensory_salience": 0.876,
        "social_surface": 0.157,
        "source_fidelity": 0.68,
        "speech_pressure": 0.271,
        "state_hygiene": 0.246,
        "temporal_pressure": 0.312,
        "verification_environment_need": 0.727
      },
      "dominantPressures": [
        "contract_strictness:1.00",
        "interface_orientation:1.00",
        "runtime_proximity:1.00",
        "content_canon_bias:0.90",
        "sensory_salience:0.88",
        "protocol_intolerance:0.85"
      ],
      "repoId": "cultlib",
      "riskPressures": [
        "actuation_risk:0.51"
      ],
      "schemaVersion": "epiphany.repo_personality_profile.v0",
      "sourceFamilyWeights": {
        "cult_protocol_storage": 0.333,
        "gamecult_web_lore_ops": 0.333,
        "unity_runtime_body": 0.333
      },
      "summary": "CultLib projects as cult_protocol_storage + gamecult_web_lore_ops + unity_runtime_body with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00, content_canon_bias:0.90, sensory_salience:0.88, protocol_intolerance:0.85."
    },
    "repoTerrainReport": {
      "axisEvidence": {
        "actuation_risk": [
          "runtime, auth, ops, or service writes can hurt real users"
        ],
        "aesthetic_appetite": [
          "visual, lore, rendered, or artifact-heavy surfaces"
        ],
        "boundary_severity": [
          "auth, ops, workspace, protocol, or service boundaries"
        ],
        "burstiness": [
          "sampled commits compressed into few active days"
        ],
        "churn_spiral_risk": [
          "large churn, experiment heat, and weak receipts"
        ],
        "consolidation_drive": [
          "refactor/remove/extract keywords or deletion-heavy history"
        ],
        "content_canon_bias": [
          "lore, site, markdown, Quartz, canon, or editorial paths"
        ],
        "contract_strictness": [
          "schema, contract, protocol, CultCache, or CultNet surfaces"
        ],
        "editorial_restraint": [
          "canon/source discipline under prose pressure"
        ],
        "evidence_appetite": [
          "tests, smoke checks, artifacts, or verifier keywords"
        ],
        "experimental_heat": [
          "prototype, experiment, scaffold, or research-workbench signals"
        ],
        "guardedness": [
          "authority and mutation risk demand caution"
        ],
        "initiative_drive": [
          "work pressure and experiment heat increase heartbeat readiness"
        ],
        "interface_orientation": [
          "UI, web, Tauri, component, DOM, or Aquarium surfaces"
        ],
        "mood_lability": [
          "risk, urgency, and churn make reactions swing harder"
        ],
        "novelty_hunger": [
          "experimental and aesthetic exploration pressure"
        ],
        "production_pressure": [
          "fix/deploy/auth/queue/CI signals"
        ],
        "protocol_intolerance": [
          "strict contract surfaces imply low tolerance for ad hoc mutation"
        ],
        "rumination_bias": [
          "state hygiene and consolidation favor distillation before action"
        ],
        "runtime_proximity": [
          "Unity/editor/runtime/provider surfaces"
        ],
        "sensory_salience": [
          "motion, visuals, rendered outputs, scenes, or UI organisms"
        ],
        "social_surface": [
          "Discord, auth, accounts, public site, or service boundaries"
        ],
        "source_fidelity": [
          "state maps, lore/canon, or runtime truth surfaces"
        ],
        "speech_pressure": [
          "public speech or user-facing surfaces"
        ],
        "state_hygiene": [
          "state, map, evidence, handoff, or memory surfaces"
        ],
        "temporal_pressure": [
          "service, runtime, queue, or live-provider timing pressure"
        ],
        "verification_environment_need": [
          "claims need runtime, editor, browser, provider, or service receipts"
        ]
      },
      "axisScores": {
        "actuation_risk": 0.507,
        "aesthetic_appetite": 0.614,
        "boundary_severity": 0.36,
        "burstiness": 0.392,
        "churn_spiral_risk": 0.279,
        "consolidation_drive": 0.084,
        "content_canon_bias": 0.901,
        "contract_strictness": 1.0,
        "editorial_restraint": 0.711,
        "evidence_appetite": 0.783,
        "experimental_heat": 0.213,
        "guardedness": 0.539,
        "initiative_drive": 0.129,
        "interface_orientation": 1.0,
        "mood_lability": 0.199,
        "novelty_hunger": 0.271,
        "production_pressure": 0.248,
        "protocol_intolerance": 0.85,
        "rumination_bias": 0.255,
        "runtime_proximity": 1.0,
        "sensory_salience": 0.876,
        "social_surface": 0.157,
        "source_fidelity": 0.68,
        "speech_pressure": 0.271,
        "state_hygiene": 0.246,
        "temporal_pressure": 0.312,
        "verification_environment_need": 0.727
      },
      "confidence": 1.0,
      "historyMetrics": {
        "activeDays": 17,
        "changedFiles": 584,
        "commitCount": 86,
        "deletions": 12411,
        "insertions": 70390,
        "keywordHits": {
          "consolidation": 1,
          "content": 1,
          "evidence": 2,
          "production": 11,
          "protocol": 33
        },
        "protocolTouches": 322,
        "recentMessages": [
          "Make SoA cache-managed document storage",
          "Add CultCache SoA happy path",
          "Keep Huginn runtime out of CultCache package",
          "Add C# CultMesh streaming surface",
          "Add CultMesh streaming mode surface",
          "Page CultCache MessagePack records",
          "Add Kotlin Eve media observation contract",
          "Add Kotlin Eve surface contracts",
          "Add Kotlin CultMesh client substrate",
          "Document CultLib language workspaces",
          "Bring language packages into CultLib monorepo",
          "Verify Rust geometry payload interop"
        ],
        "runtimeTouches": 16,
        "sampledCommits": 80,
        "stateDocTouches": 6,
        "testReceiptTouches": 90,
        "uiTouches": 10
      },
      "instructionSurfaces": [
        "AGENTS.md"
      ],
      "languages": [
        {
          "count": 97,
          "label": ".json"
        },
        {
          "count": 83,
          "label": ".cs"
        },
        {
          "count": 44,
          "label": ".md"
        },
        {
          "count": 29,
          "label": ".asset"
        },
        {
          "count": 23,
          "label": ".ts"
        },
        {
          "count": 20,
          "label": ".prefab"
        },
        {
          "count": 18,
          "label": ".png"
        },
        {
          "count": 14,
          "label": ".csproj"
        },
        {
          "count": 13,
          "label": ".mat"
        },
        {
          "count": 11,
          "label": ".rs"
        },
        {
          "count": 5,
          "label": ".html"
        },
        {
          "count": 5,
          "label": ".ttf"
        }
      ],
      "name": "CultLib",
      "path": "\\\\?\\E:\\Projects\\CultLib",
      "remoteUrls": [
        "https://github.com/GameCult/CultLib.git"
      ],
      "repoId": "cultlib",
      "runtimeSurfaces": [
        "src/GameCult.Logging.Unity/UnityLogger.cs",
        "src/GameCult.Unity/.gitignore",
        "src/GameCult.Unity/Assets/Caching/Editor/CultCacheStudioWindow.cs",
        "src/GameCult.Unity/Assets/Caching/Editor/GameCult.Unity.Caching.Editor.asmdef",
        "src/GameCult.Unity/Assets/Caching/README.md",
        "src/GameCult.Unity/Assets/Caching/Runtime/CultCacheInspectorAttributes.cs",
        "src/GameCult.Unity/Assets/Caching/Runtime/GameCult.Unity.Caching.asmdef",
        "src/GameCult.Unity/Assets/Caching/package.json",
        "src/GameCult.Unity/Assets/Fonts/Montserrat-Thin SDF.asset",
        "src/GameCult.Unity/Assets/Fonts/Montserrat-Thin.ttf",
        "src/GameCult.Unity/Assets/Fonts/Ubuntu/Ubuntu-B.ttf",
        "src/GameCult.Unity/Assets/Fonts/Ubuntu/Ubuntu-L Small SDF.asset",
        "src/GameCult.Unity/Assets/Fonts/Ubuntu/Ubuntu-L.ttf",
        "src/GameCult.Unity/Assets/Fonts/Ubuntu/Ubuntu-R SDF.asset",
        "src/GameCult.Unity/Assets/Fonts/Ubuntu/Ubuntu-R.ttf",
        "src/GameCult.Unity/Assets/InputSystem_Actions.inputactions",
        "src/GameCult.Unity/Assets/NuGet.config",
        "src/GameCult.Unity/Assets/Plugins/AutoExpandGridLayoutGroup.cs",
        "src/GameCult.Unity/Assets/Resources/Settings.asset",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Fill.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Outline-Half.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Outline.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Rectangle-Fill.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Rectangle-Outline-Half.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Rectangle-Outline.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Tab-Bottom-Fill.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Tab-Bottom-Outline.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Tab-Top-Fill.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Tab-Top-Outline.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/areaFade1.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/nail.png",
        "src/GameCult.Unity/Assets/Resources/particle.hdr",
        "src/GameCult.Unity/Assets/Resources/particle.mat",
        "src/GameCult.Unity/Assets/Scenes/SampleScene.unity",
        "src/GameCult.Unity/Assets/Scenes/SampleScene_Profiles/Post-process Volume Profile.asset",
        "src/GameCult.Unity/Assets/UI/Attributes.cs",
        "src/GameCult.Unity/Assets/UI/ColorConversion.cs",
        "src/GameCult.Unity/Assets/UI/Components/BoolField.cs",
        "src/GameCult.Unity/Assets/UI/Components/ButtonField.cs",
        "src/GameCult.Unity/Assets/UI/Components/ClickCatcher.cs",
        "src/GameCult.Unity/Assets/UI/Components/ColorModal.cs",
        "src/GameCult.Unity/Assets/UI/Components/ConstrainedSlider2D.cs",
        "src/GameCult.Unity/Assets/UI/Components/EnumField.cs",
        "src/GameCult.Unity/Assets/UI/Components/GeneratorFoldout.cs",
        "src/GameCult.Unity/Assets/UI/Components/HorizontalGroup.cs",
        "src/GameCult.Unity/Assets/UI/Components/IncrementField.cs",
        "src/GameCult.Unity/Assets/UI/Components/InputField.cs",
        "src/GameCult.Unity/Assets/UI/Components/Label.cs",
        "src/GameCult.Unity/Assets/UI/Components/LayoutComponent.cs",
        "src/GameCult.Unity/Assets/UI/Components/Modal.cs",
        "src/GameCult.Unity/Assets/UI/Components/ProgressField.cs",
        "src/GameCult.Unity/Assets/UI/Components/ResolverComponent.cs",
        "src/GameCult.Unity/Assets/UI/Components/SliderField.cs",
        "src/GameCult.Unity/Assets/UI/Components/TextButton.cs",
        "src/GameCult.Unity/Assets/UI/Default Resolver.asset",
        "src/GameCult.Unity/Assets/UI/DisplayOptions.cs",
        "src/GameCult.Unity/Assets/UI/GameCult.Unity.UI.asmdef",
        "src/GameCult.Unity/Assets/UI/Generator.cs",
        "src/GameCult.Unity/Assets/UI/GeneratorPanel.cs",
        "src/GameCult.Unity/Assets/UI/Interfaces.cs",
        "src/GameCult.Unity/Assets/UI/MainMenu.cs",
        "src/GameCult.Unity/Assets/UI/Prefabs/Button.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Click Catcher.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Color Modal.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Enum Button.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Enum.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Field Foldout.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Foldout.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Header.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Horizontal Group.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Increment.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Input.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Label.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Modal.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Progress.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Slider.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Spacer.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Text Button.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Toggle.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Value Base.prefab",
        "src/GameCult.Unity/Assets/UI/Prefabs/Value Label.prefab",
        "src/GameCult.Unity/Assets/UI/Prototype.cs",
        "src/GameCult.Unity/Assets/UI/ReflectiveResolver.cs",
        "src/GameCult.Unity/Assets/UI/Resources/Fonts/AdwaitaMono-Regular.ttf",
        "src/GameCult.Unity/Assets/UI/Resources/Fonts/Inter-Light SDF.asset",
        "src/GameCult.Unity/Assets/UI/Resources/Fonts/Inter.ttc",
        "src/GameCult.Unity/Assets/UI/Resources/Fonts/InterDisplay-Thin SDF.asset",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HCY.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HSL.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HSV.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HCY_C.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HCY_H.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HCY_Y.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSL_H.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSL_L.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSL_S.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSV_H.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSV_S.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorSlider_HSV_V.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Sprites/Icons/add.png",
        "src/GameCult.Unity/Assets/UI/Resources/Sprites/Icons/check.png",
        "src/GameCult.Unity/Assets/UI/Resources/Sprites/Icons/cross.png",
        "src/GameCult.Unity/Assets/UI/Resources/Sprites/Icons/remove.png",
        "src/GameCult.Unity/Assets/UI/Resources/Sprites/Icons/rightArrow.png",
        "src/GameCult.Unity/Assets/UI/Shaders/ColorCircle.shader",
        "src/GameCult.Unity/Assets/UI/Shaders/ColorConversion.cginc",
        "src/GameCult.Unity/Assets/UI/Shaders/ColorSlider.shader",
        "src/GameCult.Unity/Assets/UI/StringExtensions.cs",
        "src/GameCult.Unity/Assets/UI/UI.txt",
        "src/GameCult.Unity/Assets/UI/package.json",
        "src/GameCult.Unity/Assets/packages.config",
        "src/GameCult.Unity/GameCult.Unity.sln.DotSettings",
        "src/GameCult.Unity/ProjectSettings/AudioManager.asset",
        "src/GameCult.Unity/ProjectSettings/ClusterInputManager.asset",
        "src/GameCult.Unity/ProjectSettings/DynamicsManager.asset",
        "src/GameCult.Unity/ProjectSettings/EditorBuildSettings.asset",
        "src/GameCult.Unity/ProjectSettings/EditorSettings.asset",
        "src/GameCult.Unity/ProjectSettings/GraphicsSettings.asset",
        "src/GameCult.Unity/ProjectSettings/InputManager.asset",
        "src/GameCult.Unity/ProjectSettings/MemorySettings.asset",
        "src/GameCult.Unity/ProjectSettings/MultiplayerManager.asset",
        "src/GameCult.Unity/ProjectSettings/NavMeshAreas.asset",
        "src/GameCult.Unity/ProjectSettings/PackageManagerSettings.asset",
        "src/GameCult.Unity/ProjectSettings/Physics2DSettings.asset",
        "src/GameCult.Unity/ProjectSettings/PresetManager.asset",
        "src/GameCult.Unity/ProjectSettings/ProjectSettings.asset",
        "src/GameCult.Unity/ProjectSettings/ProjectVersion.txt",
        "src/GameCult.Unity/ProjectSettings/QualitySettings.asset",
        "src/GameCult.Unity/ProjectSettings/SceneTemplateSettings.json",
        "src/GameCult.Unity/ProjectSettings/TagManager.asset",
        "src/GameCult.Unity/ProjectSettings/TimeManager.asset",
        "src/GameCult.Unity/ProjectSettings/UnityConnectSettings.asset",
        "src/GameCult.Unity/ProjectSettings/VFXManager.asset",
        "src/GameCult.Unity/ProjectSettings/VersionControlSettings.asset",
        "src/GameCult.Unity/ProjectSettings/XRSettings.asset",
        "src/GameCult.Unity/README.md"
      ],
      "schemaVersion": "epiphany.repo_terrain_report.v0",
      "sourceFamilies": [
        "cult_protocol_storage",
        "gamecult_web_lore_ops",
        "unity_runtime_body"
      ],
      "stateSurfaces": [
        ".voidbot/state/README.md",
        ".voidbot/state/libby.cc"
      ],
      "testSurfaces": [
        "packages/cultcache-ts/test/cult-cache.test.ts",
        "packages/cultcache-ts/tsconfig.test.json",
        "packages/cultmesh-ts/test/cultmesh.test.ts",
        "packages/cultmesh-ts/tsconfig.test.json",
        "packages/cultnet-rs/tests/cultnet.rs",
        "packages/cultnet-rs/tests/fixtures/cultnet-ts-hello.frame",
        "packages/cultnet-rs/tests/fixtures/cultnet-ts-legacy-login.frame",
        "packages/cultnet-ts/test/cultnet.test.ts",
        "packages/cultnet-ts/test/interop/cultnet-interop-peer.ts",
        "packages/cultnet-ts/test/interop/cultnet-interop-shared.ts",
        "packages/cultnet-ts/test/interop/cultnet-interop.test.ts",
        "packages/cultnet-ts/tsconfig.test.json",
        "research/cultnet-distributed-database/dynamo-amazon-science.html",
        "src/GameCult.Networking/Contracts/cultnet.witness-artifact-bundle.schema.json",
        "src/GameCult.Networking/CultWitnessArtifactBundle.cs",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Fill.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Outline-Half.png",
        "src/GameCult.Unity/Assets/Resources/Sprites/Flat UI/32px/Flat-Circle-Outline.png",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HCY.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HSL.mat",
        "src/GameCult.Unity/Assets/UI/Resources/Materials/ColorCircle_HSV.mat",
        "src/GameCult.Unity/Assets/UI/Shaders/ColorCircle.shader",
        "tests/GameCult.Caching.InteropPeer/GameCult.Caching.InteropPeer.csproj",
        "tests/GameCult.Caching.InteropPeer/Program.cs",
        "tests/GameCult.Caching.Tests/BackingStoreTests.cs",
        "tests/GameCult.Caching.Tests/GameCult.Caching.Tests.csproj",
        "tests/GameCult.Caching.Tests/README.md",
        "tests/GameCult.Geometry.Tests/Fixtures/vg-csg-ragnarok/ragnarok-domain.msgpack",
        "tests/GameCult.Geometry.Tests/Fixtures/vg-csg-ragnarok/ragnarok-first-chunk.msgpack",
        "tests/GameCult.Geometry.Tests/GameCult.Geometry.Tests.csproj",
        "tests/GameCult.Geometry.Tests/GeometryDocumentTests.cs",
        "tests/GameCult.Mesh.Tests/CultMeshStreamingTests.cs",
        "tests/GameCult.Mesh.Tests/GameCult.Mesh.Tests.csproj",
        "tests/GameCult.Networking.InteropPeer/GameCult.Networking.InteropPeer.csproj",
        "tests/GameCult.Networking.InteropPeer/Program.cs",
        "tests/GameCult.Networking.Tests/GameCult.Networking.Tests.csproj",
        "tests/GameCult.Networking.Tests/NetworkingTests.cs",
        "tests/GameCult.Networking.Tests/README.md",
        "tests/GameCult.Networking.Tests/lcov.info"
      ],
      "warnings": []
    },
    "repoTrajectoryReport": {
      "antiGoalCandidates": [
        "Do not let the repo drift into decorative lore or soft handwaving that ignores material and engineering consequences.",
        "Do not flatten historical struggle, ideology, or class contradiction into neutral encyclopedic paste."
      ],
      "confidence": 0.95,
      "directionalPressures": [
        "worldbuilding_depth recent 0.00, current 0.50, delta 0.00",
        "material_grounding recent 0.00, current 0.40, delta 0.00",
        "historical_dialectic recent 0.00, current 0.50, delta 0.00",
        "presentation_polish recent 0.00, current 0.50, delta 0.00",
        "systems_formalization recent 0.11, current 0.40, delta 0.00"
      ],
      "earlyCommitMessages": [
        "Seed Libby social interpretation biases",
        "Add CultCache geometry documents",
        "Prove geometry documents replicate through CultNet",
        "Align geometry document fingerprints with Rust",
        "Exercise geometry chunks through CultMesh node state",
        "Document geometry state over CultMesh",
        "Verify Rust geometry payload interop",
        "Bring language packages into CultLib monorepo",
        "Document CultLib language workspaces",
        "Add Kotlin CultMesh client substrate",
        "Add Kotlin Eve surface contracts",
        "Add Kotlin Eve media observation contract",
        "Page CultCache MessagePack records",
        "Add CultMesh streaming mode surface",
        "Add C# CultMesh streaming surface",
        "Keep Huginn runtime out of CultCache package",
        "Add CultCache SoA happy path",
        "Make SoA cache-managed document storage"
      ],
      "implicitGoalCandidates": [
        "Deepen the setting through causality, continuity, and consequence instead of ornament alone.",
        "Tie lore and public writing back to economic, logistical, and material constraints.",
        "Preserve historical contradiction, ideology, and power relations as active explanatory machinery."
      ],
      "recentCommitMessages": [
        "Make SoA cache-managed document storage",
        "Add CultCache SoA happy path",
        "Keep Huginn runtime out of CultCache package",
        "Add C# CultMesh streaming surface",
        "Add CultMesh streaming mode surface",
        "Page CultCache MessagePack records",
        "Add Kotlin Eve media observation contract",
        "Add Kotlin Eve surface contracts",
        "Add Kotlin CultMesh client substrate",
        "Document CultLib language workspaces",
        "Bring language packages into CultLib monorepo",
        "Verify Rust geometry payload interop",
        "Document geometry state over CultMesh",
        "Exercise geometry chunks through CultMesh node state",
        "Align geometry document fingerprints with Rust",
        "Prove geometry documents replicate through CultNet",
        "Add CultCache geometry documents",
        "Seed Libby social interpretation biases"
      ],
      "repoId": "cultlib",
      "schemaVersion": "epiphany.repo_trajectory_report.v0",
      "selfImage": "CultLib behaves like a cult_protocol_storage + gamecult_web_lore_ops + unity_runtime_body workspace that has been moving toward systems_formalization, worldbuilding_depth, historical_dialectic.",
      "tensions": [
        "Presentation polish is welcome, but it should carry the same grounded causal weight as the lore beneath it."
      ],
      "themeScores": [
        {
          "currentSources": 0.5,
          "delta": 0.0,
          "earlyHistory": 0.0,
          "evidence": [
            "source:README.md # CultLib\r \r CultLib is a set of reusable C# libraries for game backends, game-adjacent services..."
          ],
          "recentHistory": 0.0,
          "theme": "worldbuilding_depth"
        },
        {
          "currentSources": 0.4,
          "delta": 0.0,
          "earlyHistory": 0.0,
          "evidence": [
            "source:research/cultnet-distributed-database/swim.pdf %PDF-1.2\r%����\r 35 0 obj\r<< \r/Linearized 1 \r/O 37 \r/H [ 5122 1132 ] \r/L 133069 \r/E 57214..."
          ],
          "recentHistory": 0.0,
          "theme": "material_grounding"
        },
        {
          "currentSources": 0.5,
          "delta": 0.0,
          "earlyHistory": 0.0,
          "evidence": [
            "source:README.md # CultLib\r \r CultLib is a set of reusable C# libraries for game backends, game-adjacent services..."
          ],
          "recentHistory": 0.0,
          "theme": "historical_dialectic"
        },
        {
          "currentSources": 0.1,
          "delta": 0.0,
          "earlyHistory": 0.0,
          "evidence": [
            "source:research/cultnet-distributed-database/dynamo-amazon-science.html <!DOCTYPE html>\r <html class=\"PublicationDetailPage\" lang=\"en\">\r     <head>\r     <meta charset=\"..."
          ],
          "recentHistory": 0.0,
          "theme": "engineering_constraint"
        },
        {
          "currentSources": 0.5,
          "delta": 0.0,
          "earlyHistory": 0.0,
          "evidence": [
            "source:README.md # CultLib\r \r CultLib is a set of reusable C# libraries for game backends, game-adjacent services..."
          ],
          "recentHistory": 0.0,
          "theme": "presentation_polish"
        },
        {
          "currentSources": 0.4,
          "delta": 0.0,
          "earlyHistory": 0.111,
          "evidence": [
            "early: Add Kotlin Eve surface contracts",
            "recent: Add Kotlin Eve media observation contract",
            "source:README.md # CultLib\r \r CultLib is a set of reusable C# libraries for game backends, game-adjacent services..."
          ],
          "recentHistory": 0.111,
          "theme": "systems_formalization"
        }
      ],
      "trajectorySources": [
        {
          "bytes": 766,
          "kind": "doctrine",
          "path": "AGENTS.md",
          "text": "# AGENTS.md\n\n## Repository Guidance\n\n- Refrain from commenting on current changes in README files unless the user explicitly asks for that commentary.\n- Treat clean API surface as a core project priority.\n- Favor developer ergonomics in public APIs, examples, naming, defaults, and documentation.\n- Prefer interfaces and behaviors that are predictable, easy to discover, and easy to integrate correctly.\n- Keep public abstractions small and coherent; avoid exposing internal workflow complexity through the API when it can be encapsulated.\n- When adding documentation or examples, optimize for practical usage by downstream developers.\n- When proposing or implementing changes, consider how they affect usability, readability, and maintenance for library consumers.\n",
          "truncated": false
        },
        {
          "bytes": 13047,
          "kind": "readme",
          "path": "README.md",
          "text": "# CultLib\r\n\r\nCultLib is a set of reusable C# libraries for game backends, game-adjacent services, and Unity-integrated tooling, including a declarative runtime UI composition framework for Unity.\r\n\r\nThe libraries cover three main areas:\r\n\r\n- logging primitives and implementations\r\n- a typed in-memory cache with pluggable persistence\n- LiteNetLib-based networking with encrypted credential exchange and signed session tokens\n- CultMesh distributed realtime database and simulation-consensus primitives\n- typed geometry domain/chunk documents for distributed CSG and LOD streaming\n- declarative Unity UI composition and reflective runtime inspector tooling\n\r\n## Which Package Do I Want?\r\n\r\nIf the job is shared game state, start here before inventing a fourth drawer\r\nand pretending the label makes it furniture.\r\n\r\n| Job | Start With | Owns | Use When | Do Not Use It For |\r\n| --- | --- | --- | --- | --- |\r\n| Local typed state | `GameCult.Caching` / CultCache | Document identity, schema compatibility, record keys, indexes, globals, and local persistence | You need a typed cache, file-compatible save data, local reactive reads, or a stable domain document model | Peer discovery, transport security, shard routing, or mesh consensus |\n| Procedural geometry state | `GameCult.Geometry` | CultCache-native domain trees, LOD build requests, selected-cut diagnostics, and chunk artifact payloads | Rust, Unity, or remote workers need to share CSG/LOD geometry and graph metadata as typed state | Transport policy, peer discovery, or gameplay authority |\n| Network transport and database plumbing | `GameCult.Networking` / CultNet | LiteNetLib transport, authentication, schema-v0 wire contracts, shard authority, raw document replication, snapshots, and subscriptions | You need a client/server pipe, login/session flow, schema discovery, or a low-level distributed CultCache lane | Gameplay-facing mesh ergonomics, Verse policy, mod branches, or simulation consensus composition |\n| Distributed realtime gameplay state | `GameCult.Mesh` / CultMesh | Public mesh entrypoints, Verse discovery, peer exchange, shard replication defaults, authority leases, client prediction, and witness consensus | You want the game to treat clients and servers as one reactive database for persistent state, input state, and simulation facts | A tiny local-only tool, a bare transport client, or a storage format contract |\n| Realtime media/frame streams | `GameCult.Mesh` / CultMesh streaming mode | Stream identity, authority, clock metadata, body transport negotiation, frame cursors, and backpressure state | Audio/video/tensor frames need to move between runtimes through shared memory, GPU handles, platform buffers, or CultCache page refs | Durable document mutation, mesh consensus facts, or pretending inline bytes are zero-copy |\n\r\nQuick rule:\r\n\r\n- Choose CultCache when the problem is \"how do I model and persist typed state?\"\n- Choose GameCult.Geometry when the problem is \"how do geometry workers share\n  domain trees, LOD build requests, and mesh chunks as typed state?\"\n- Choose CultNet when the problem is \"how do peers exchange authenticated,\n  schema-aware database messages?\"\n- Choose CultMesh when the problem is \"how does a game join a Verse and share\n  realtime state across a mesh?\"\n- Choose CultMesh streaming mode when the problem is \"how do these runtimes move\n  audio/video frames while the mesh owns identity, clocks, cursors, and pressure?\"\n\r\nCultMesh sits on CultNet, and CultNet distributes CultCache documents. That is\r\nthe stack. If a design needs another peer-to-peer category, first check whether\r\nit is really Verse policy, peer exchange, or shard authority wearing a fake\r\nmustache.\r\n\r\nIf you want the smallest durable \"open node -> put typed doc -> get typed doc\"\r\npath, start with\r\n[`src/GameCult.Mesh/docs/durable-node-quickstart.md`](src/GameCult.Mesh/docs/durable-node-quickstart.md).\r\nIf you want the lower-level \"typed local document -> raw wire document ->\r\nmesh/runtime watch surface\" handoff, follow it with\r\n[`src/GameCult.Mesh/docs/typed-document-path.md`](src/GameCult.Mesh/docs/typed-document-path.md).\r\n\r\n## Repository Scope\r\n\r\nThe solution includes:\n\n- `GameCult.Logging`: common logging abstraction plus console and file implementations\n- `GameCult.Caching`: `DatabaseEntry`-based cache, indexes, global entries, and backing-store abstractions\n- `GameCult.Caching.MessagePack`: MessagePack-backed persistence for the cache\r\n- `GameCult.Caching.NewtonsoftJson`: Newtonsoft.Json-backed persistence for the cache\r\n- `GameCult.Caching.MessagePack.Generator`: source generator for MessagePack formatters for cache models\n- `GameCult.Caching.MessagePack.Analyzers`: packaging project that delivers the generator to consuming projects\n- `GameCult.Geometry`: CultCache-native geometry domain, selected-cut, and chunk artifact documents for VibeGeometry/Fensalir-style pipelines\n- `GameCult.Networking`: encrypted login/register/verify flows and message dispatch over LiteNetLib\n- `GameCult.Mesh`: CultMesh package home for distributed realtime database, shard replication, client prediction, Verse discovery, and mesh witness consensus\n- `GameCult.Caching.Tests`: NUnit tests for cache and backing-store behavior\n- `GameCult.Networking.Tests`: NUnit tests for networking behavior\n- `GameCult.Unity`: CultUI, a Unity runtime UI composition framework with reflective inspector generation, prefab-backed field resolvers, reusable controls, and a demo project packaged for UPM-style consumption\n- `packages/cultcache-ts`: TypeScript CultCache with MessagePack persistence and inspector tooling\n- `packages/cultnet-ts`: TypeScript CultNet schema-v0 contracts, framing, discovery, raw document replication, and interop tests\n- `packages/cultmesh-ts`: TypeScript CultMesh local node and mesh catalog surface for local runtimes such as VoidBot\n- `packages/cultcache-rs`: Rust CultCache and derive macro\n- `packages/cultnet-rs`: Rust CultNet contracts, framing, discovery, and interop peer\n\n## Repository Layout\n\n```text\npackage.json\nsrc/\n  GameCult.Logging/\n  GameCult.Caching/\n  GameCult.Caching.MessagePack/\r\n  GameCult.Caching.NewtonsoftJson/\r\n  GameCult.Caching.MessagePack.Generator/\n  GameCult.Caching.MessagePack.Analyzers/\n  GameCult.Geometry/\n  GameCult.Networking/\n  GameCult.Mesh/\r\n  GameCult.Unity/\r\ntests/\n  GameCult.Caching.Tests/\n  GameCult.Geometry.Tests/\n  GameCult.Networking.Tests/\npackages/\n  cultcache-ts/\n  cultnet-ts/\n  cultmesh-ts/\n  cultcache-rs/\n  cultnet-rs/\n```\n\r\n## Build\r\n\r\n```powershell\r\ndotnet build CultLib.sln\r\n```\r\n\r\n## Test\n\n```powershell\ndotnet test CultLib.sln\n```\n\nTypeScript package tests:\n\n```powershell\nnpm test --workspace packages/cultcache-ts\nnpm test --workspace packages/cultnet-ts\nnpm run test:interop --workspace packages/cultnet-ts\nnpm test --workspace packages/cultmesh-ts\n```\n\nRust package tests:\n\n```powershell\ncargo test --manifest-path packages/cultcache-rs/Cargo.toml\ncargo test --manifest-path packages/cultnet-rs/Cargo.toml\n```\n\r\n## Common Concepts\r\n\r\n### `DatabaseEntry`\r\n\r\nThe cache-centric libraries revolve around `DatabaseEntry`. Every entry has a stable `Guid` identifier and can optionally:\r\n\r\n- expose a human-readable name through `INamedEntry`\r\n- participate in generic indexes registered at runtime\r\n- be treated as a global singleton entry through `GlobalSettingsAttribute`\r\n\r\nTypical entry shape:\r\n\r\n```csharp\r\nusing GameCult.Caching;\r\n\r\npublic class ItemData : DatabaseEntry, INamedEntry\r\n{\r\n    public string Name = string.Empty;\r\n    public int Value;\r\n\r\n    public string EntryName\r\n    {\r\n        get => Name;\r\n        set => Name = value;\r\n    }\r\n}\r\n```\r\n\r\n### `CultCache` and Backing Stores\r\n\r\n`CultCache` is an in-memory index over `DatabaseEntry` objects. It can operate entirely in memory, or it can be attached to one or more backing stores for persistence and synchronization.\r\n\r\n- the cache is the query surface\r\n- backing stores are persistence adapters\r\n- indexes and name maps are maintained inside the cache, not inside the store\r\n\r\n### Important: Multiple Backing Stores\r\n\r\nWhen multiple backing stores are added, behavior depends on how they are registered.\r\n\r\nIf a store is added with domain types:\r\n\r\n```csharp\r\ncache.AddBackingStore(playerStore, typeof(PlayerData));\r\ncache.AddBackingStore(settingsStore, typeof(AppSettings));\r\n```\r\n\r\nthen that store becomes the direct persistence target for those types.\r\n\r\nIf a store is added without domain types:\r\n\r\n```csharp\r\ncache.AddBackingStore(primaryStore);\r\ncache.AddBackingStore(mirrorStore);\r\n```\r\n\r\nthen the first generic store acts as the primary writable store for non-domain-specific entries. Additional generic stores subscribe to the existing stores and mirror their change events.\r\n\r\nImplications:\r\n\r\n- order matters for generic stores\r\n- `AddAsync` writes to the type-specific store when one exists\r\n- otherwise `AddAsync` writes to the first generic store\r\n- later generic stores do not become co-primaries; they mirror earlier s",
          "truncated": true
        },
        {
          "bytes": 497,
          "kind": "documentation",
          "path": "notes/operator-interventions.md",
          "text": "# Operator Interventions\n\n## 2026-05-08\n\n- External Codex session from `E:\\Projects\\EpiphanyAgent` added\n  `src/GameCult.Caching/Contracts/cultcache-persistence-format.md`.\n- Intent: write down the next canonical CultCache persistence-format design in\n  the C# source-of-truth repo before Rust/TS implementations harden the wrong\n  shape.\n- Existing unrelated worktree changes in `src/GameCult.Networking/PlayerData.cs`\n  and `src/GameCult.Unity/Assets/UI/Default Resolver.asset` were left alone.\n",
          "truncated": false
        },
        {
          "bytes": 133069,
          "kind": "research",
          "path": "research/cultnet-distributed-database/swim.pdf",
          "text": "%PDF-1.2\r%����\r\n35 0 obj\r<< \r/Linearized 1 \r/O 37 \r/H [ 5122 1132 ] \r/L 133069 \r/E 57214 \r/N 10 \r/T 132251 \r>> \rendobj\r                                                      xref\r35 235 \r0000000016 00000 n\r\n0000005049 00000 n\r\n0000006254 00000 n\r\n0000006560 00000 n\r\n0000006625 00000 n\r\n0000006733 00000 n\r\n0000006839 00000 n\r\n0000006928 00000 n\r\n0000006949 00000 n\r\n0000010404 00000 n\r\n0000011912 00000 n\r\n0000012019 00000 n\r\n0000012133 00000 n\r\n0000013163 00000 n\r\n0000013184 00000 n\r\n0000013270 00000 n\r\n0000014189 00000 n\r\n0000014210 00000 n\r\n0000015099 00000 n\r\n0000015120 00000 n\r\n0000016100 00000 n\r\n0000016121 00000 n\r\n0000017102 00000 n\r\n0000017123 00000 n\r\n0000018048 00000 n\r\n0000018069 00000 n\r\n0000018962 00000 n\r\n0000018983 00000 n\r\n0000019160 00000 n\r\n0000019376 00000 n\r\n0000019596 00000 n\r\n0000019775 00000 n\r\n0000020079 00000 n\r\n0000020316 00000 n\r\n0000020565 00000 n\r\n0000020804 00000 n\r\n0000021022 00000 n\r\n0000021309 00000 n\r\n0000021545 00000 n\r\n0000021802 00000 n\r\n0000022011 00000 n\r\n0000022242 00000 n\r\n0000022541 00000 n\r\n0000022803 00000 n\r\n0000023071 00000 n\r\n0000023314 00000 n\r\n0000023507 00000 n\r\n0000023741 00000 n\r\n0000023918 00000 n\r\n0000024149 00000 n\r\n0000024370 00000 n\r\n0000024616 00000 n\r\n0000024867 00000 n\r\n0000025088 00000 n\r\n0000025292 00000 n\r\n0000025507 00000 n\r\n0000025716 00000 n\r\n0000025929 00000 n\r\n0000026147 00000 n\r\n0000026371 00000 n\r\n0000026595 00000 n\r\n0000026816 00000 n\r\n0000026994 00000 n\r\n0000027221 00000 n\r\n0000027434 00000 n\r\n0000027662 00000 n\r\n0000027942 00000 n\r\n0000028200 00000 n\r\n0000028467 00000 n\r\n0000028709 00000 n\r\n0000028982 00000 n\r\n0000029259 00000 n\r\n0000029466 00000 n\r\n0000029722 00000 n\r\n0000030047 00000 n\r\n0000030301 00000 n\r\n0000030538 00000 n\r\n0000030776 00000 n\r\n0000031062 00000 n\r\n0000031265 00000 n\r\n0000031503 00000 n\r\n0000031755 00000 n\r\n0000031947 00000 n\r\n0000032198 00000 n\r\n0000032449 00000 n\r\n0000032686 00000 n\r\n0000032934 00000 n\r\n0000033141 00000 n\r\n0000033445 00000 n\r\n0000033644 00000 n\r\n0000033858 00000 n\r\n0000034127 00000 n\r\n0000034339 00000 n\r\n0000034560 00000 n\r\n0000034775 00000 n\r\n0000034982 00000 n\r\n0000035194 00000 n\r\n0000035416 00000 n\r\n0000035673 00000 n\r\n0000035949 00000 n\r\n0000036236 00000 n\r\n0000036520 00000 n\r\n0000036754 00000 n\r\n0000037025 00000 n\r\n0000037312 00000 n\r\n0000037500 00000 n\r\n0000037755 00000 n\r\n0000037977 00000 n\r\n0000038211 00000 n\r\n0000038499 00000 n\r\n0000038710 00000 n\r\n0000038921 00000 n\r\n0000039188 00000 n\r\n0000039405 00000 n\r\n0000039619 00000 n\r\n0000039839 00000 n\r\n0000040059 00000 n\r\n0000040320 00000 n\r\n0000040563 00000 n\r\n0000040805 00000 n\r\n0000041093 00000 n\r\n0000041327 00000 n\r\n0000041588 00000 n\r\n0000041846 00000 n\r\n0000042141 00000 n\r\n0000042404 00000 n\r\n0000042658 00000 n\r\n0000042860 00000 n\r\n0000043103 00000 n\r\n0000043356 00000 n\r\n0000043625 00000 n\r\n0000043694 00000 n\r\n0000043763 00000 n\r\n0000043832 00000 n\r\n0000043901 00000 n\r\n0000043970 00000 n\r\n0000044039 00000 n\r\n0000044108 00000 n\r\n0000044177 00000 n\r\n0000044246 00000 n\r\n0000044315 00000 n\r\n0000044384 00000 n\r\n0000044453 00000 n\r\n0000044522 00000 n\r\n0000044591 00000 n\r\n0000044660 00000 n\r\n0000044729 00000 n\r\n0000044798 00000 n\r\n0000044867 00000 n\r\n0000044936 00000 n\r\n0000045005 00000 n\r\n0000045074 00000 n\r\n0000045143 00000 n\r\n0000045212 00000 n\r\n0000045281 00000 n\r\n0000045350 00000 n\r\n0000045419 00000 n\r\n0000045488 00000 n\r\n0000045557 00000 n\r\n0000045626 00000 n\r\n0000045695 00000 n\r\n0000045765 00000 n\r\n0000045835 00000 n\r\n0000045905 00000 n\r\n0000045975 00000 n\r\n0000046045 00000 n\r\n0000046115 00000 n\r\n0000046185 00000 n\r\n0000046254 00000 n\r\n0000046323 00000 n\r\n0000046392 00000 n\r\n0000046461 00000 n\r\n0000046530 00000 n\r\n0000046599 00000 n\r\n0000046668 00000 n\r\n0000046737 00000 n\r\n0000046806 00000 n\r\n0000046875 00000 n\r\n0000046944 00000 n\r\n0000047013 00000 n\r\n0000047082 00000 n\r\n0000047151 00000 n\r\n0000047401 00000 n\r\n0000047662 00000 n\r\n0000047901 00000 n\r\n0000048126 00000 n\r\n0000048349 00000 n\r\n0000048575 00000 n\r\n0000048822 00000 n\r\n0000049044 00000 n\r\n0000049277 00000 n\r\n0000049515 00000 n\r\n0000049741 00000 n\r\n0000050062 00000 n\r\n0000050309 00000 n\r\n0000050544 00000 n\r\n0000050777 00000 n\r\n0000051018 00000 n\r\n0000051204 00000 n\r\n0000051424 00000 n\r\n0000051681 00000 n\r\n0000051915 00000 n\r\n0000052162 00000 n\r\n0000052408 00000 n\r\n0000052653 00000 n\r\n0000052885 00000 n\r\n0000053111 00000 n\r\n0000053312 00000 n\r\n0000053381 00000 n\r\n0000053450 00000 n\r\n0000053519 00000 n\r\n0000053588 00000 n\r\n0000053657 00000 n\r\n0000053726 00000 n\r\n0000053795 00000 n\r\n0000053864 00000 n\r\n0000053933 00000 n\r\n0000054002 00000 n\r\n0000054071 00000 n\r\n0000054140 00000 n\r\n0000054209 00000 n\r\n0000054488 00000 n\r\n0000054762 00000 n\r\n0000054987 00000 n\r\n0000055221 00000 n\r\n0000055431 00000 n\r\n0000055668 00000 n\r\n0000055737 00000 n\r\n0000055806 00000 n\r\n0000055875 00000 n\r\n0000055944 00000 n\r\n0000056013 00000 n\r\n0000056082 00000 n\r\n0000005122 00000 n\r\n0000006231 00000 n\r\ntrailer\r<<\r/Size 270\r/Info 33 0 R \r/Root 36 0 R \r/Prev 132241 \r/ID[<a9908ad4a73d5c022ff9fd1cda9a81d2><a9908ad4a73d5c022ff9fd1cda9a81d2>]\r>>\rstartxref\r0\r%%EOF\r    \r36 0 obj\r<< \r/Type /Catalog \r/Pages 32 0 R \r/Metadata 34 0 R \r>> \rendobj\r268 0 obj\r<< /S 2996 /Filter /FlateDecode /Length 269 0 R >> \rstream\r\nH�b```f`\u0010ab�``�\\� ��\u0000\u0002@1\u000e\u0006\u0016\u0006�'p�5u\f\fL���\u0019MY\u0003�8XED.21_�|��k׮_�`\u0018�������QRJFZNV^UQIAEYW]CSK[�HO��P�������������������������;���? 0(8$4,<\"2*:&6.>!1)9%5-=#3+�07/����������������������������������I��L�6}��Y��̝7��E��,]�|��U�׬]�~��M��lݶ}��]���ݷ���C��\u001c=v���S�Ϝ=w���K��\\�v���[��ܽw���G��<�d�`gafd�������UPTRVQՇxJW�\b�)cTO99�@=�G�S9���:D���\u001c�'�)\u000eF&v6N��,/^�\u0004y\f\u0018[J��\n*��0��\u001b\u0018�9�x��\u0018�\u0002�\u001e��/D��A�=v�\u0010\u0011\u001e\u0003��s��RC���\bgt�\u0010᫂�\u0001I��9؆��@q����2�|��񜅙�񕔬��������������v���������������������������������\u0017����㓟�ꫦ����F��Z;�&��L���;e��S���֕����߶e��};��=~����\u0007O\u001c;}��]��^�y����7.]���έ{g��~�����O89����bf\u0001yJ�HNUAQZYE\r�)=\u001d\u0003C\u0017dO\u0019;(�=E(��rr�\u000bP<u�\n��?bM��X��31�00\b\njt�\u00050\b*�Ǣ\u0006`cł>V�j~�P���\u0000�+zY%\u0018�F/�\u0018\u0018��g\u0015C\u0004}�b`q�[\u00002\u001a'`kq\u0003=+���K\u0005H?\u0007���f�(\u0003��\u0005\u001dւP\u0016\u0007�kKϳ&��\u000b0�\b710�00�g`>��������0���8�Ciކ'�\u000b�\n,��w`\u000f��\u0018��w�\u0007kx\u0003��*��\u0013x�Ӱ�Ga\u000f\u000f�Q�\r�y\u001a��<8����7�\u000b\u0004\u0018v�m4��p�w\"'w�M���|\t�x\u001b\u001es/��V8�߰���\u0010���<\u0013���\u0012(�ʿ����\u0015τ�\u0002\u001b�q?���p���8?�=ދܼ\u0001\u001f�.\n�+��\u000e��������\u0004$`\bCX\u0015<�g��s�\u0014υG<\u0013��N8+t�\u0011o�\u0015�\u0003Ky\n��\u0006<�\u0013x�kp��\u0001�^\u0001��<\u0007�r_�\u0001\u00171wpaY\u0010�2\u0001G\u0001 �\u0000X��_\rendstream\rendobj\r269 0 obj\r1021 \rendobj\r37 0 obj\r<< \r/Type /Page \r/MediaBox [ 0 0 612 792 ] \r/Parent 32 0 R \r/Resources << /ProcSet [ /PDF /ImageB /Text ] /Font << /R13 49 0 R /A 43 0 R /R8 45 0 R /R7 39 0 R /R6 40 0 R >> >> \r/Contents [ 47 0 R 50 0 R 52 0 R 54 0 R 56 0 R 58 0 R 60 0 R 267 0 R ] \r/CropBox [ 0 0 612 792 ] \r/Rotate 0 \r>> \rendobj\r38 0 obj\r<< \r/Type /Encoding \r/Differences [ 2 /fi ] \r>> \rendobj\r39 0 obj\r<< \r/Type /Font \r/Name /R7 \r/Subtype /Type1 \r/BaseFont /Times-Italic \r/Encoding 38 0 R \r>> \rendobj\r40 0 obj\r<< \r/Type /Font \r/Name /R6 \r/Subtype /Type1 \r/BaseFont /Times-Bold \r/Encoding 41 0 R \r>> \rendobj\r41 0 obj\r<< \r/Type /Encoding \r/Differences [ 2 /fi 150 /endash 173 /hyphen ] \r>> \rendobj\r42 0 obj\r952 \rendobj\r43 0 obj\r<< \r/Type /Font \r/Name /A \r/Subtype /Type3 \r/Encoding 44 0 R \r/FirstChar 0 \r/LastChar 204 \r/CharProcs << /a203 129 0 R /a202 130 0 R /a201 128 0 R /a200 126 0 R /a199 127 0 R \r/a198 131 0 R /a195 135 0 R /a194 136 0 R /a193 137 0 R /a192 134 0 R \r/a190 132 0 R /a189 133 0 R /a187 125 0 R /a186 117 0 R /a185 118 0 R \r/a183 116 0 R /a182 114 0 R /a181 115 0 R /a180 119 0 R /a179 123 0 R \r/a178 124 0 R /a177 122 0 R /a176 120 0 R /a175 121 0 R /a174 138 0 R \r/a173 155 0 R /a171 156 0 R /a170 154 0 R /a169 152 0 R /a168 153 0 R \r/a167 157 0 R /a166 161 0 R /a165 162 0 R /a164 163 0 R /a163 160 0 R \r/a162 158 0 R /a161 159 0 R /a160 151 0 R /a154 142 0 R /a152 143 0 R \r/a151 141 0 R /a150 139 0 R /a148 140 0 R /a147 144 0 R /a146 148 0 R \r/a145 149 0 R /a143 113 0 R /a141 147 0 R /a139 145 0 R /a138 146 0 R \r/a137 150 0 R /a135 79 0 R /a133 80 0 R /a132 78 0 R /a131 76 0 R \r/a130 77 0 R /a128 81 0 R /a127 85 0 R /a124 86 0 R /a121 75 0 R \r/a119 84 0 R /a118 82 0 R /a117 83 0 R /a115 87 0 R /a113 67 0 R \r/a111 68 0 R /a108 66 0 R /a106 63 0 R /a104 62 0 R /a101 65 0 R \r/a100 64 0 R /a99 72 0 R /a98 73 0 R /a97 74 0 R /a96 69 0 R /a94 70 0 R \r/a93 71 0 R /a91 103 0 R /a89 104 0 R /a87 105 0 R /a85 88 0 R /a83 101 0 R \r/a82 102 0 R /a80 106 0 R /a78 110 0 R /a76 111 0 R /a73 112 0 R \r/a72 109 0 R /a71 107 0 R /a69 108 0 R /a68 100 0 R /a67 92 0 R \r/a65 93 0 R /a64 91 0 R /a62 89 0 R /a61 90 0 R /a59 94 0 R /a58 98 0 R \r/a57 99 0 R /a55 97 0 R /a54 95 0 R /a52 96 0 R /a50 233 0 R /a48 232 0 R \r/a45 164 0 R /a43 231 0 R /a41 229 0 R /a40 230 0 R /a38 234 0 R \r/a35 238 0 R /a31 239 0 R /a29 240 0 R /a27 237 0 R /a26 235 0 R \r/a25 236 0 R /a23 228 0 R /a22 219 0 R /a20 220 0 R /a18 218 0 R \r/a16 216 0 R /a15 217 0 R /a14 221 0 R /a13 225 0 R /a11 226 0 R \r/a10 227 0 R /a9 224 0 R /a8 222 0 R /a7 223 0 R /a6 241 0 R /a5 258 0 R \r/a4 259 0 R /a3 257 0 R /a2 255 0 R /a1 256 0 R /a0 260 0 R /a66 264 0 R \r/a42 265 0 R /a36 266 0 R /a39 263 0 R /a47 261 0 R /a92 262 0 R \r/a77 254 0 R /a125 245 0 R /a44 246 0 R /a63 244 0 R /",
          "truncated": true
        },
        {
          "bytes": 8481,
          "kind": "research",
          "path": "research/cultnet-distributed-database/summary.md",
          "text": "# CultNet Distributed Database Research Summary\r\n\r\nPurpose: ground CultNet's distributed realtime database design in prior art\r\nbefore adding more machinery. The goal is a coherent local-first mesh database\r\nover CultCache, not a hand-rolled distributed-systems costume party.\r\n\r\n## Sources Stored Here\r\n\r\n- `hashgraph-swirlds-tr-2016-01.pdf`\r\n  - Source: <https://www.swirlds.com/downloads/SWIRLDS-TR-2016-01.pdf>\r\n  - Topic: gossip-about-gossip, virtual voting, fair ordering, asynchronous BFT.\r\n- `raft-extended.html`\r\n  - Source: <https://yygcode.com/papers/consensus-raft-extended-version.html>\r\n  - Topic: understandable consensus, leader election, log replication, safety.\r\n- `raft-extended.pdf`\r\n  - Source: <https://web.stanford.edu/~ouster/cgi-bin/papers/raft-extended.pdf>\r\n  - Topic: canonical Raft paper copy.\r\n- `dynamo-amazon-science.html`\r\n  - Source: <https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store>\r\n  - Topic: Dynamo publication page and abstract.\r\n- `dynamo-sosp2007.pdf`\r\n  - Source: <https://web.stanford.edu/class/cs244/papers/amazon-dynamo-sosp2007.pdf>\r\n  - Topic: highly available key-value store, consistent hashing, versioning,\r\n    quorums, hinted handoff, anti-entropy.\r\n- `swim.pdf`\r\n  - Source: <https://www.cs.cornell.edu/projects/quicksilver/public_pdfs/SWIM.pdf>\r\n  - Topic: scalable weakly consistent process-group membership.\r\n- `rethinkdb-changefeeds.html`\r\n  - Source: <https://rethinkdb.com/docs/changefeeds/java/>\r\n  - Topic: realtime query/changefeed ergonomics.\r\n- `firebase-realtime-offline.html`\r\n  - Source: <https://firebase.google.com/docs/database/web/offline-capabilities>\r\n  - Topic: offline behavior, presence, server-side disconnect operations.\r\n- `crdt-arxiv-1805.06358.html`\r\n  - Source: <https://arxiv.org/abs/1805.06358>\r\n  - Topic: conflict-free replicated data types and deterministic convergence.\r\n\r\n## Design Takeaways\r\n\r\n### RethinkDB\r\n\r\nKeep: changefeeds as the product feel. Subscribers should receive document\r\nchanges continuously, with enough old/new context to render, reconcile, or\r\ndebug. Point subscriptions and filtered subscriptions are both first-class.\r\n\r\nCultNet implication: database subscriptions should be explicit schema-v0\r\nmessages. The server should stream raw document changes that clients can apply\r\nthrough the same CultCache reconciliation path as snapshots.\r\n\r\n### Firebase Realtime Database\r\n\r\nKeep: realtime sync and local/offline ergonomics. Presence and disconnect\r\nbehavior are database features, not application afterthoughts.\r\n\r\nDefer: full offline writes. CultNet should not pretend arbitrary offline\r\nmulti-writer changes merge safely. Offline/local-first behavior needs declared\r\nper-document conflict policy.\r\n\r\nCultNet implication: add presence/disconnect records later as ordinary\r\nCultCache documents with server authority, not as hidden transport state.\r\n\r\n### Raft\r\n\r\nKeep: the understandable authority model. One leader/primary owns ordered\r\nwrites for a shard. Decompose the problem into ownership, log/mutation\r\nreplication, and safety.\r\n\r\nDefer: full automatic leader election and replicated logs until shard catalogs\r\nand explicit epochs exist.\r\n\r\nCultNet implication: the current primary-shard policy is the correct first\r\nfoundation. Every write should either hit the primary, be forwarded to the\r\nprimary, or fail with routing information.\r\n\r\n### Dynamo\r\n\r\nKeep: partitioning, replication metadata, vector-ish causality, hinted recovery,\r\nanti-entropy, and application-visible conflicts.\r\n\r\nReject for now: \"always writeable\" semantics. That is attractive, expensive,\r\nand easy to lie about.\r\n\r\nCultNet implication: shard descriptors should grow into a shard catalog with\r\nowner runtime id, epoch, schema/key ranges, and later replica/preference-list\r\nmetadata. Conflicts must surface as data, not disappear into last-writer-wins\r\nunless a document explicitly chose that policy.\r\n\r\n### SWIM\r\n\r\nKeep: gossip-shaped membership once the mesh grows beyond a small static\r\ncluster. SWIM separates failure detection from membership dissemination and\r\nkeeps per-node message load stable.\r\n\r\nDefer: membership implementation until there is a shard catalog to disseminate.\r\n\r\nCultNet implication: do not build peer discovery as a side-channel registry.\r\nWhen it arrives, it should update membership and shard-catalog state together.\r\n\r\n### CRDTs\r\n\r\nKeep: CRDTs for documents whose merge law is explicit and deterministic.\r\n\r\nReject: generic automatic merge for arbitrary domain objects. That is a\r\nlanguage cop with a nicer hat.\r\n\r\nCultNet implication: CRDT support belongs in schema metadata or document\r\ncontract metadata. A document type can opt into a known merge strategy; the\r\ndefault distributed write policy remains primary authority.\r\n\r\n### Hashgraph\r\n\r\nKeep: gossip-about-gossip as an idea for compact event provenance and possible\r\nfair ordering research. Virtual voting is interesting when every member sees\r\nthe same gossip history.\r\n\r\nReject for now: adopting hashgraph consensus as CultNet's core. The public\r\nledger/crypto smell is not the main issue; the issue is that CultNet does not\r\ncurrently need asynchronous Byzantine total ordering to become a good mesh\r\ndatabase. Dragging that in now would make the machine harder to explain.\r\n\r\nCultNet implication: if we later need decentralized event ordering, use a\r\ndedicated design pass. Do not smuggle hashgraph metadata into ordinary document\r\nreplication because it sounds powerful.\r\n\r\n## Current CultNet Direction\r\n\r\nThe coherent path is:\r\n\r\n1. Primary-shard authority.\r\n2. Explicit schema-v0 snapshot, put, delete, subscribe, unsubscribe, and change\r\n   messages.\r\n3. Shard catalog exchange with owner runtime id and epoch.\r\n4. Optional forwarding from non-owner nodes to owners.\r\n5. Membership/failure detection, likely SWIM-shaped.\r\n6. Replication and failover, likely Raft-shaped per shard.\r\n7. Optional CRDT policies for document types that deserve offline multi-writer\r\n   semantics.\r\n8. Optional gossip-history research if fair decentralized ordering becomes a\r\n   real requirement.\r\n\r\n## Live Invariants\r\n\r\n- CultCache owns document identity, schema compatibility, local indexes, and\r\n  reconciliation.\r\n- CultNet owns transport, shard authority, subscriptions, and remote mutation\r\n  delivery.\r\n- Raw wire records must pass through `CultNetDatabase` before mutating local\r\n  cache state.\r\n- A write without authority is rejected or explicitly forwarded. It is never\r\n  silently applied.\r\n- Realtime change streams publish domain changes, not storage implementation\r\n  details.\r\n- Conflict policy must be declared before conflicting writes are accepted.\r\n\r\n## Current Cut\r\n\r\nShard catalog exchange now exists:\r\n\r\n- `cultnet.shard_catalog_request.v0`\r\n- `cultnet.shard_catalog_response.v0`\r\n- per-shard id, owner runtime id, epoch, schema ids, key prefix/range\r\n- stale epoch rejection on writes\r\n- routing error that tells clients where the primary lives when known\r\n- injectable non-primary write forwarding policy\r\n- concrete schema-v0 write forwarder for `cultnet://host:port` primary\r\n  endpoints\r\n- in-memory per-shard mutation logs and catch-up by last seen sequence\r\n- wire-level shard mutation log catch-up with raw put/delete entries\r\n- replica-side application of shard log responses with epoch checks, gap\r\n  rejection, and idempotent replay\r\n- background shard log replicator plus schema-v0 fetcher for primary endpoints\r\n- restart-safe replica cursor store with a local MessagePack file\r\n  implementation\r\n- client authority scopes for predicted local input documents\r\n- predicted/reconciled change events for client-side prediction\r\n- simulation witness observations and deterministic consensus candidates\r\n- schema-v0 simulation observation and candidate messages\r\n- reactive observation hub for witness gossip and consensus candidates\r\n- server-side observation bridge for observation messages and candidate replies\r\n\r\n## Next Cut\r\n\r\nBuild durable authoritative mutation logs:\r\n\r\n- persist per-shard primary logs\r\n- define the snapshot fallback boundary for compacted history\r\n- return explicit resync-required responses when requested history is gone\r\n- then add simulation-frame rollback and resimulation helpers on top of\r\n  predicted input streams\r\n- add peer-to-peer fanout for observation gossip and candidate propagation\r\n\r\nThat is the next foundation needed before membership, Raft-style failover, or\r\nCRDT policy work can be honest.\r\n",
          "truncated": false
        },
        {
          "bytes": 573786,
          "kind": "research",
          "path": "research/cultnet-distributed-database/raft-extended.pdf",
          "text": "%PDF-1.4\r%����\r\n120 0 obj\r<</Linearized 1/L 573786/O 122/E 41955/N 18/T 571265/H [ 936 728]>>\rendobj\r             \r\nxref\r\n120 32\r\n0000000016 00000 n\r\n0000001664 00000 n\r\n0000001730 00000 n\r\n0000001974 00000 n\r\n0000002007 00000 n\r\n0000002076 00000 n\r\n0000002185 00000 n\r\n0000010658 00000 n\r\n0000011226 00000 n\r\n0000011907 00000 n\r\n0000011999 00000 n\r\n0000017965 00000 n\r\n0000018414 00000 n\r\n0000018854 00000 n\r\n0000018897 00000 n\r\n0000020780 00000 n\r\n0000022482 00000 n\r\n0000022579 00000 n\r\n0000030097 00000 n\r\n0000030533 00000 n\r\n0000031055 00000 n\r\n0000032778 00000 n\r\n0000034750 00000 n\r\n0000034850 00000 n\r\n0000035314 00000 n\r\n0000035533 00000 n\r\n0000035774 00000 n\r\n0000035967 00000 n\r\n0000037616 00000 n\r\n0000039244 00000 n\r\n0000040734 00000 n\r\n0000000936 00000 n\r\ntrailer\r\n<</Size 152/Root 121 0 R/Info 119 0 R/ID[<D415EDDF6CA1C8AFECD96DA0FBCD5900><B91CCE8B95D3604C92CAC3A9BCDD9A0D>]/Prev 571253>>\r\nstartxref\r\n0\r\n%%EOF\r\n        \r\n151 0 obj\r<</Filter/FlateDecode/I 818/Length 645/S 605>>stream\r\nh�b```\u0006�W\f�\f\f�|\f\u0002\f\b ��������q\u0001I�C��Wؓ�a6O�_պ(�\u0018e���Z-*�w���ۅ�%\u001aYU|M��<9�j��R�/\u0000Y3\u0004l^p��G�a�s�\u0004���I�X�9D\f\u001f<b钱i\u0014j�\u00167��R�8�4`�R�O�v�I�B&Wf\u000e\u0011��o�u�\th$\u001e>!�\u0011���{��(�)� �\u0006\u000f�pl�S�X�\u00004ڢ�I�k�^�\u0006�>n��\u0004��y�\u0017\f\u001a��\u0005T\u0002�*w�=����\u000fB\rc�\u00075�]@%�ii�\fq\r\t����\n.\u0016�JS�<�\u0006�qLP���Ri8�`7��\u0005+P��\u0006��\u0005\u0013 >\n~�\u0007�I.z淀G!��\u0011N\u0011���@��\nͼ�\b���`\u000b��\u0006�E�ݥon�ЁX����\u001c\u0012>JJ..\u000e����&.@\u0000\u001606NKK��2\u001b������\b\u0010\u001a\ne��\u0006@U\u001a\u0003ՆF�9�@ \u0000\u0012\u0001\u001b��\u0002\u0006iH\u0000j1�\t\r@cBA\u00165�\f2qq```\u0004\u001a!l��\u0000R�(ALB\u0000*4a`[\"\u000e�e�8\u000el�\"\u0003?�2�\t\n,G�\u0019Z\u000f�0X�E�5��`X��q����-�\u001a�\u0012�\u000b�\u0006\u001c\t�\u0007b\u0019.0:H:\u0018ވ\u0016���q@���1��{�\ne\u0001��_\u001c�ڶ2�0\u001eamT� � }@��,�%�9l\u0006�\u000e�\u0007X\u000f\b6p3�b��(�^���q |c3�\u0006�\u00116\r���\u0006o�\u0019\fkNǰ[0�`,�tz�p�A����\u000e�\t�\u0007b\u000bx\u001b$\u0019�\u0018S�d�g�7�+�5�\u0005u9\u0003��\u0002P(\u0000�.@�\u0001\u0000�\n�\u0006\r\nendstream\rendobj\r121 0 obj\r<</Metadata 118 0 R/Pages 117 0 R/Type/Catalog>>\rendobj\r122 0 obj\r<</Contents[134 0 R 135 0 R 140 0 R 141 0 R 147 0 R 148 0 R 149 0 R 150 0 R]/CropBox[0 0 612 792]/MediaBox[0 0 612 792]/Parent 117 0 R/Resources<</ExtGState 123 0 R/Font 124 0 R/ProcSet[/PDF/ImageB/Text]>>/Rotate 0/Type/Page>>\rendobj\r123 0 obj\r<</R7 133 0 R>>\rendobj\r124 0 obj\r<</R10 128 0 R/R12 139 0 R/R14 146 0 R/R8 132 0 R>>\rendobj\r125 0 obj\r<</BaseEncoding/WinAnsiEncoding/Differences[2/fi/fl 30/grave 39/quoteright]/Type/Encoding>>\rendobj\r126 0 obj\r<</Filter/FlateDecode/Length 8386/Subtype/Type1C>>stream\r\nx��xyXS����!9�uhK���&i���y�S�qFE\u0004e\u0010\u0011!̐@\u0002$��\u0004\u0012��\u0018�$@�<� ��,\u000e�V�\u001d���mk��}����\u0007no�������?0{���z�Z�z�f\u0011N\u0013\b\u0016���\u0013\u001e}4^�!��#[�6�C\u001a\u001a���\u0016�¢�O�g�����5#�\u001cZ4�\u0000��`�S��Yw�G>{id�\u000b��/����jd\r�F�xy\u001c|s��y�eru\\xh�R�xժU��jɿV$�RExh��\r�G�4J&���(\u0017H�K�\u0012e�T\u0012\u0012\u001e%�l���c�6ɜm{�$ۤ1Ҹ�(�{�Ѩ� �[x�4F!}S\u0012\"��D��G\u0012$�\t\u000eW��b\u0014\u000b$\u001b\u0015�@�B.\r\n�\u001fIUAR9�0O\"��E�+\u0014�oI�B\u0012\u001a\u0017\u0018��\u0006K�2IxLPT|0s=�=D\u0016����dx=\u001a���e\n�\"(.\\���\u001b�]��ۨ\f\u000bT2�*��D\u0016�w\u0006˂�\u0019o���\f\f�QH�R����T\u0012\u001c��G\u0005���(y\\��\t��пn�'���\u0006�\u0005GI\u0015c�2����?�\u000e�ˣ�c���v���p�B\u001a\u0015�`<�\u0012\u001c��\u0018\t\u000e��M�D7*0�W����_\u0004\t���\u0018�I�Y��\u001a�%n�b�r{�������TG��A����H�����\u000b�\b�\u001f�\u0019�\u0015u ��բ5��f\u0017�Y����\t����K\u0017,\\��l��eˋ�^�� ^#�\u0012���\twb51��G�!f\u0011\u001e�\u001b�~b6�I�!��7�\u0003�[�Ab.�Ml\"�\u0011>�fb>�K�\u0012\u000b\b?b\u000b���J,\"�\u0011����\u0012b\u0007���I,#v\u0011�\t7�mb7���C�$�\u0011.���Np\t-A\u0011��s��x�x��DD\u0013��\u0018b\u001d1��\u0011/\u0010/\u0012/\u0011�D\u0016�#^&��+D\"1�\u0010\u0010B�\u000bW\u0001�DH��,���'\u001c�p���}��\u0015�\u0002\u000e�)���=d:�\r\u0015@�|n�sQ��=����'&O�L\n��?���y�єuSN�0�\u0005�\"�b��\u001f�����|��ʫyy��\u0003|'~�+\u0013_��J�T��\u001f\n�\u000b��\u001a��\b��6y��i��h\u0017��q�O���k�:#mFی�3n��V�\u0012\u001d\u0015�����b����]r��7_�z�\u0001̛2\u0012\u0002\u001cл�~��\u001a9`�\u001f�3���E��5A\"\u0017\u0005���Bo�$�e\tU��N�%Z\u001a��\u0005\u0016� |�\u0003mܾ�ers,�\nA\\RځlJ\u0005��Q\u001d,�C\u00126B\n5r�Й�FO;���1t}�.�_�\u0017��\u0002��X�+F9$��k�3(\r\f�����2�I�v����6@\r�F�\u0010#)\t�tI>̦`\u001b�[�R\u0006�(�&�\t���?*i�6�#Ҙ�\u000e�{&\u000b�\\\u001a���q\u001by��RU�sY�F�\u0004\u001a�:&\u0014q�H\u0010xi�\u0013E?�\u0016\u0002���-�1G`G�U��J�?�\u0018��2\u0001Z������ᝁ]���}��\"P\u0003l���A8\u0011�\u0011|�$m���@#\u0004�dCxv\u0006�g�0\b���\u0007\n5%G��'l\u0010�7��k\u000f\u001de��c�\u0016\u0002���~ҥ�;C�3��l5$��ު�v�l*�qz�\u001el�F�I�Q�[�I�\"T�S�!�U�����8\u000eN�\u0001uoTSt��lg�\u0019KUIm\u0015ŋ���u�M�NK��F�*�9�{0�ީ��_T4\u0004�M\u0003^\t�A�H�Ѵ\u001d���\u0016��a�\u0013���\\l\u0013\rpyQ���d큀�b\u001c.C#�����\u0005�~��y�\u0011>ze�\\$B._�\u0001���w��\"8u��e�N�|i.��8��m\b\u000e��M\u0010��$\u0000nQߞ�zK�\u0004�>�v���g��L0d*��4�\u0000���T\rz\u001d%\b�\u001c\u0018�/7U�R!��\u0016���\u0012P�C�Q��<�e���^�S��\u0000m\"�2�aQ\u0006\u0006���\\��-\u0003�)x��s`��O�a�\u001d��e��c�\u0010�\u0001~*L�$pM\t�)� \u0015h�4���>E��*�2j����\u0005\u0000M\u0000\u000b�������+�\u000f\u0000r�\u000f�W>�ԕ\u001b]wD�\u0004B7��q��s(�\u000b\u0000�\u0014��5|\u000eκx&!�W�\u001ai��wR\u00185\u0014]�T��цVz�e�{�a�ө��bz\u0006�8�\f�\u0001�n6\u001c\u0012����^�n�xo$�t�w��ܐ�+\u0005�Rp1�\u0011Qk8\u0006(��W�\t΂/n����\u0001q^\u0001�\u0007�)�+!L�$�om�7_�\u001c�;�\u001eQ���a�!��f�\u0014\u001a�n��n\u0016}\u000b���\u001b\u0012\u0012�\u0015q\u0001Z\u000f@�\u0012.�J��c\u001b�!\u0013�zN=�x��QQU�t�z\u001d�\u0016�\u001d�1�O���B�BW�X����_��\\Ț���\u0005��`s�n}:�7\u0015�\"!\u0005~m�z�ڥ������臞��D�N4\u0003�X��MW���ũ8�yQ���-�E}O�%��\u0014=��[\u000e\u0010\u0001��GW�>�\u001d�/e\u0019��RpR��h�V@�+�s�s�S��x*O\u0003G�|����`|�Ks�\u0013����/�f�|��\u00011�Ų_�q�v\r%A���,K+\u0002�(�[=�\t}�\u0007�\f�!-^\u0015�\u0012\u0005���$�\u0011ٔp\r\\�(����jK��1�ǰ�\u000f\u0016wc�x6�����k�2�\u000f\"�(�뀒s}A`�ќ��U\n*(\b�\u0002�c`�YVR5P��\u001a�fh�Z]�,�\u00106s\u0001��6\u0016\u00166��\u000b\u0017�S��b1U�̈́�sF\u001b��\u001c\u0015y�dт-c��E�ل�wU�]�\u0014�\u0014��Z\u0007��c���Ʒ�\u0015�\u0001��89@��H�c4�J�,h����l�\"\u0005����S\r���RQ���d\u0001T����-�\u0011�ō���+2=�@|��]%�B�\u0005vx�?�\f�\u00150��\u001d S֨5k��\u0018�\u000b\u0018ӧ���\u000f�2}\u000b\tVꙄP�6;�._k\u0005�)\u0018\u0003g�\u001f�\nM�j\\\u0015BCu��^��U;��\nW}0�7\u000fހ��3�t�����j�Ƽ)4\u0001C����U\u0014���Ǻ���yk�\u0013?�pDGb�\u0002D\t\u0003\"e��Q�j�(�.�Fw�JF�,\\sI~.F��V\f�Y5��*L�\r\u0019�.\u000f�\u001b����mo�\u0018\u0011���5u�A���8x�9|I\rCK�^��^V+�Ғ��#>�7>*Rs�3�2��l��Jp�Bo��{�ъِ\u000b�|�����h`�j�\rp\u0001\f�zB�e��=T\r7\r�r�&�Z�\u0000\f 9Om>l�/:��<��m6:�fÆ\u000b����~'�\u0013�S\u0000_����`\u0000\t.�Y�\u000b�)��p����l��h7N�'�\b�\u0016��\u001b��d�D�TS�� �<◜A��&c�4�����E�n�E�+��\\\u001a�=�&ҕ&�%\u0019�0D��\u0018���'?q�J\u001a\u000e\r�.;�g��36�B���\tn��>0c�mQ�\u000f��z�y,�+\f)k�����\r�:\u000b&tx\u0014^@\u0007�n�v��S�\u0001�i%�\u001e\u000ezu���|�1܊}�N+N�ӹ���A1�\u000e-!ymI���2\u0003�P\n\td�Z�^\u001e\u001f�\u0012\r����cb^\u0005|��В'3�\u001a�O�\u0002��w�z�\u001b\u001aD����$�-?딣���U�\u0014�}��΂$��d\u0018k�.eE�xO^��l\u0005w�̽c�<���R�O�[.�U��q��X�=\u0012k���6\u0003P\u0001C�!�0\u0007�\tf�b�5�\n�q�*5w�Q6�!��̆��p\u0012�\u0015��yi��´BP\b�\u0016[\u0017|\u0019�\u00154�(47�1�����\u0007��mk�O����ҏ\n�\u0015�*�c\u0013\u0006m\u00135\u001aD\u001eNI�՚��\"CGry,�x�<�/z\u0018\u0012Wn×�L�\u001bz\u0003\u001b�!ۍ%F�\u00044&�^�\u0018�\tV@e� \u0000�\"\u0004ե�\u0013flf����(�;��*��P�\b�\t�@kFIV\t(\u0011�����|�k����*2T{C\u0017���,�H[\b\n@���\u001e�\"��K����\n��\f\n��{�\u0006�APy,������v7\\�\u0018�\u0005��\u0017>�s>��;N��OҦ�k\u0000�4Z\u0007�\u0010'ϙ�&Mm|�\u0011�;��o�uS:\u0012kj�\u001c���\u0005�fqVqv\u00110S\r�սC\r1^�=$Z�;)�4>Q\u001b\u000e\"(���@�����3WD<���ʄ>�vlG7^B\u000b1�G��&�(���\u0004@���������:��\u0001j\\�0���36�xd*�,�\u0004�欲4�\b�ў�h\u000bZ.���#ӥ\"\u001f��t��O�{�I�\u000e;��Q�w(������k�7�s\u0005\n!�j���t�\u0007c2p�N\u001e�і,�\u001eia�\u0000z@�\u001b�ʋn\u0014\b�\u0001,\u00138�6\u0016ly\u0005�۸Q �<j\u0000�\f�\u0005��I�����\u001c�\r-ѐ�Y%&�\u0005))�qQ�\u0014�.Q�\u0010Ȏ�ہ\u0005X-��\u001c\u001c� M1�\f�u-��A3\u0005��VE\u001f5\u0018#2�\u001a8�F�R\n,�f��!~\u0004_�\u0012I�M�&`ª՘\u0011��d>�,\u0013�e�|\f\u0013?c��#k�鹁\u001ay�!�0&��L\u0016��\u00033�z�S\u0003ţ�dԓ�\u000f��\u0019�\u0000_�/��f��}����\u0003N=':�<<�\u0016MAl�]�\u000e\u001e���Wc��\u001a�5r�\u0003���$�*5Ia��ꕀ��ݤ+�\u0010��$�e�>*��w�gs�\u0015��\u001e�Ut\u0007y\u0012nb\u0006\n\u0014󘾂K@�Ԫ\u000b�����lS�Q�\u0016jB$,4Y3,cr�f���\u0000\u001dՒ�\u0019\u0015��$�Eق�4[t�Oy(#�5J��hml�J\u000bL 5�P�2'�'�MLTD��\u001d{��\u00058�\u0018.��YZKKj�L�ߦ9M��\u0007l�5���%T�e֢]\u001fB���>��CJą�E�Ju\u0015\u0010�W�k.l9���;1���'@�\r�C�W\u001e-����-���Z\u001c�KԔ�'�+CY\u0003t%\u0016�o�\u001f�G}pޥ��\u001dn��\np�\u001d��[\u0016�]&G�H\u0014;����\\\u000e�Uq?N��0L�\u0016\tfjU+M��\u0014�9'Հ�(z6\t�GOp�\\�\u0004h��5�跡\u000b_�o�1%뒄ڤ�ToܹD\\XM�ɱ�\u001f�L�<��[|�f�������ob�8y��πr��FV�S���\r�7���3�rR��\u00052\u0001\u0015��Y�~\u000f�\u000f��0î$a2Y�S\b�\u0000�Ul8*F�Hi��4\b�G.�c�k��{���\u000bݎ\u0013��]����јbЫ\u0013�\f1�Z��!|\u0001r���?;t�\u0000C�zG*\u0003�3\u0003��\u0001�sh=��!��.��Zb��fX>EEvgZ3l�\u000emq(�M�/�Q>���*n?���\u001e�Fw�<�\u0003l:ɳ�{0dL�oVHv��yp%�o���]�\u0003\u001ci����6�e]��\u0015n�5CS\\kt��A�\u0003\u000e��ᇎ�]�*\f������%H\u001c\u0013��p\u001e��a{�\u0015p\u0005��,�\u0018z�v����Վ5�:�e�?FV��~4\u001d�]������\u0013\u000f�}\u001e�\u0019��\u000f�\u000e%����'���n7Wt��P;\r�Aug�9ߖ�\u0000M\u0004�\u0007S��ʰ���\u0003�\u001f\u001c�\fk98\u0018�\u0013�a����z����\u0003j\\s�n�����q\u0018�2U�\u0002\u0016\u001f����*�!�RT\u001e[f�\u0000T����3�~��\u0003r�Xql@Zh�\u001ajU���}.ؤO����\u0016\u001b��Rp����)���q��g0�\u0019'��a8��ϪȮ\u0007\u000f�����˭�W�upL�-o\n\u001aX�8\u001fgE\u0005�W�� ���,I3�7�W�;y\u001a����\u0004�\u0006��\u0001�\\=4�-w\u001fD&jsr��1��\b�:��:\u0018�S��j�\u0000&?hhd�\u0011��߳�Lĳ`�9�H /�\u0017�F�`��En\r��\u0011b�Z�dJ���%�ΐP�җ�zP\u001e\u0001B�\u0003\u000f\u0003������\u0019QwKE+�\u0006W\u000f\u001dۘ����\u0003�qŠ\u001c8�:\u001a�����y��e�.\u0004]������h�\u0000�@Sz���B��i�6㇦!@}٦\u000bݷ>\u0018q��k\u001a�1[��\u0012\u0017����\u001a;\u0006[ꓢ�E�ae� ��\u001f\u0013�7��w�v\u001dy��,\u0019ه��\u00117o���_oX:+\u0006{�^��rlJ�G��L�@�)\u0014�f�K�D=����\u0014'<\u00057ށ������'S<j\u0017r�\u0017#��~0��:��:\u0017 \u000e�\u00133+�?��A�'�\u0003\u0001\u0015��\u0007ND�\b�s໦�\r�\r��\u001a\u0006����9֝�p\u000fN���;����\u001e@��\u000b�\"F�$�f��3���x����M\n���\u0006�[�\u001e���/�\u0017���𔸴��B9�p�s�����Ҏ�G#�dJݗ������\n\re����\u0014�g&��=�:&�\u001e�̶�y�%1��(,\u0014�oi.kkh��\u001f\u0003'@��+�Iڷ��I�&.︊��V����\u001bR60uߌ�\u0000kZ<���\u0017ra*�˹<�Q?�)����\u0017�4|d�OB`�J�\u001f\u001bf��Ũw��D[�\u0003N~�����X��\n73�&\u000b=9\u001aLxe���b �\u001d\u001b�4$�l�oe\n�`#w\u0014��0�A�G{�\u0004�\\��ɪ��M5�M�ʚ\u0018<\u0011��v�;h�\u0017�p��\u0014jb$�~L\r\t�\f�^��'\u000eܮ�(^�5$�(��\b\bH��\t�R\u0007��`��~��~[z�h��XW�\u0000\u0014�v\u001f�MK���)>\"%�\u0019�q��<7{N��悧�\f�\u0011����M�u���~���\u001a\u0012�\u0012�6\u001c8\u0000�Q�u_=~L\u0015�!j��Ɩ��\u0010��b�G�Ƽ�j����\u0017��O�sWJu�j��\u00059�\\[N\t\u0010�\u0014'ှ@���q�D�A��\u0005\u001a`0'U�k��ɸ骓�#{����K��kb�O�#��\u000e\u0014�*�\u001ccA3���\u0011�?�I�����L�<��~���(��s$�ju\r�@�rMp\u001dg�\nׄ�qj���Uc�N��J\u001b�c<p���8V�T��D�'\u001eM �����������9A��t:�Ѝ�\u0012�7/#7373/SXd�3�t*5Ք\"Ri`��\u001b�\u0017e\u000f<���\u0003���[ׯ\u000e6\u0015\tm��\\̡E6tD���.���.-U�MA3�\u0014\u0001���ȕ�Ĥ�K\u0016F�1�0�\u0001z����\u0019�c�MC��\u000f���\\�\u001b������`6�p��f�&G��vEm\u0000���V~B�j�bޓ��e\u001f2sY��|4>b�k$���*�\u000e��u��\u0011�\u001e+\u001e\u0004\u000f�Ә���l;Ơ�[�S���tk���x4�\u0004^Z�[:FAo#��SJ�y��\u0006�{0Ijؚ$\u001ck�#�\u001d����\u001b_��{>�R\u000fd �(O�F�G�\u00024\u0017~\u0019q'�\u0006؄�n-a��Js��\u001b�]E\u000eeY��K`���ƅq�pۣ�ғXi��qlf�a=}\fݘl;�\u001aߒWļ\u001e�\u0017'��Q!c�f6�-�F\u001e�Ք�\u001e\nv� ���l����\u0000�@u\u0018&\",1���\u001eL�)m�G��\u0002\\�`\u0001���V9\b����b\u0014J\u0002���}F�If#cs�su��G\b��������y���\u0012�/�7x�\u0000 Ӯt���\\oU��S#���8\u000e��dp�j�t:�A\u000e\u0012L�\u0006\u0019z\t}%@\u0002�MVAjuv�0��d\u0007�����\u001c��CE^�.Im_ӶJ A\u001dh&=�X�1�(Zm5���[�T��t��k>�\u001amg�Z@���F�pl�'�Z�\bY���Li��\u0012&����vjd5ž\u0014χN���\u001bC�ܿ\u0002_\u0004Ѕ��\u0016��\u0010\u001fM\\�\u0006rASn����c�}gEG�\nĖ�y�\u0014�\u0004G� 1#U�*S��B\u0001���\u001e�x��rU���QW�\u0004�O�/G�q��!\u001b�@6\u000bN�\u0003��q�Q1uqM-�uM-���b����7�:F�p�`:��G��\u001cuej�\u0006\b\u0013t)��RMe\n��oq��&G�/0\u001b��?�씞�����W�7��/��ԩ���a��p\u0011/3�Z�Uh��)\u0014��r�\u0000�\u0014\u0015��\"e!^!�n�!�'�o\u000bF��\u001c�fX�^�\n��j�M\u000e�+�a\\ݵ�0�~*����b��#�\u0004=�^Z{�����\u000e��sm\u001f���\ri�N�9���\u001a\u001a�\u0011�MzH\u001c\u0019\u0014��a\n���>��q�\u0019qCoce\u000b�\u0002�\u0017�`��wP�n\u0010\u0004�f���?���\u0015D� �\u0004MD�-g=��\u001fwt��Ϝm}\u0004�\u0000kVYze��2�\t\f�zso\r��\u000f��$������\u0011~ab�\u0006h)�Y���a���c�\u001b\r�D<ΓsM��-.\u0005 7�0ݪ+KoJ���Z���\u000b���\u0014[@�С+�פ���\"4\u001b��P?�o��z������o;~��\b+�#kT���s1���0�",
          "truncated": true
        },
        {
          "bytes": 33609,
          "kind": "research",
          "path": "research/cultnet-distributed-database/raft-extended.html",
          "text": "<!DOCTYPE html>\r\n<html xmlns=\"http://www.w3.org/1999/xhtml\" lang=\"en\" xml:lang=\"en\">\r\n<head>\r\n<meta charset=\"utf-8\" />\r\n<title>In Search of an Understandable Consensus Algorithm (Extended Version)</title>\r\n<meta name=\"generator\" content=\"Org mode\" />\r\n<meta name=\"author\" content=\"Diego Ongaro and John Ousterhout, Stanford University\" />\r\n<link rel=\"stylesheet\" type=\"text/css\" href=\"/themes/readtheorg/styles/readtheorg/css/htmlize.css\"/>\r\n<link rel=\"stylesheet\" type=\"text/css\" href=\"/themes/readtheorg/styles/readtheorg/css/readtheorg.css\"/>\r\n<script src=\"/themes/jquery/2.1.3/jquery.min.js\"></script>\r\n<script src=\"/themes/bootstrap/3.3.4/js/bootstrap.min.js\"></script>\r\n<script type=\"text/javascript\" src=\"/themes/readtheorg/styles/lib/js/jquery.stickytableheaders.min.js\"></script>\r\n<script type=\"text/javascript\" src=\"/themes/readtheorg/styles/readtheorg/js/readtheorg.js\"></script>\r\n</head>\r\n<body>\r\n<div id=\"org-div-home-and-up\">\r\n <a accesskey=\"h\" href=\"../index.html\"> UP </a>\r\n |\r\n <a accesskey=\"H\" href=\"paperlist.html\"> HOME </a>\r\n</div><div id=\"content\">\r\n<h1 class=\"title\">In Search of an Understandable Consensus Algorithm (Extended Version)</h1>\r\n<div id=\"table-of-contents\">\r\n<h2>Table of Contents</h2>\r\n<div id=\"text-table-of-contents\">\r\n<ul>\r\n<li><a href=\"#orgf960eb7\">DECLARATION</a></li>\r\n<li><a href=\"#orgd05d3d0\">Abstract</a></li>\r\n<li><a href=\"#orge9839bb\">1. Introduction</a></li>\r\n<li><a href=\"#orgae8bb96\">2. Replicated state machines</a></li>\r\n<li><a href=\"#org1fe8017\">3. What's wrong with Paxos?</a></li>\r\n<li><a href=\"#orgfc9c6b4\">4. Designing for understandability</a></li>\r\n<li><a href=\"#orge3d8f10\">5. The Raft consensus algorithm</a>\r\n<ul>\r\n<li><a href=\"#org752f98e\">5.1. Raft basics</a></li>\r\n<li><a href=\"#org291797f\">5.2. Leader election</a></li>\r\n<li><a href=\"#org44bb0a6\">5.3. Log replication</a></li>\r\n</ul>\r\n</li>\r\n</ul>\r\n</div>\r\n</div>\r\n\r\n<div id=\"outline-container-orgf960eb7\" class=\"outline-2\">\r\n<h2 id=\"orgf960eb7\">DECLARATION</h2>\r\n<div class=\"outline-text-2\" id=\"text-orgf960eb7\">\r\n<p>\r\nThis page is the In Search of an Understandable Consensus Algorithm\r\n(Extended Version)  paper. Original Paper Link\r\nis: <a href=\"https://raft.github.io/raft.pdf\">https://raft.github.io/raft.pdf</a>\r\n</p>\r\n\r\n<p>\r\nIn Search of an Understandable Consensus Algorithm<br />\r\n(Extended Version)<br />\r\nDiego Ongaro and John Ousterhout<br />\r\nStanford University\r\n</p>\r\n</div>\r\n</div>\r\n\r\n<div id=\"outline-container-orgd05d3d0\" class=\"outline-2\">\r\n<h2 id=\"orgd05d3d0\">Abstract</h2>\r\n<div class=\"outline-text-2\" id=\"text-orgd05d3d0\">\r\n<p>\r\nRaft is a consensus algorithm for managing a replicated log. It produces a\r\nresult equivalent to (multi-)Paxos, and it is as efficient as Paxos, but its\r\nstructure is different from Paxos; this makes Raft more understandable than\r\nPaxos and also provides a better foundation for building practical systems.\r\nIn order to enhance understandability, Raft separates the key elements of\r\nconsensus, such as leader election, log replication, and safety, and it enforces\r\na stronger degree of coherency to reduce the number of states that must be\r\nconsidered. Results from a user study demonstrate that Raft is easier for\r\nstudents to learn than Paxos. Raft also includes a new mechanism for changing\r\nthe cluster membership, which uses overlapping majorities to guarantee safety.\r\n</p>\r\n</div>\r\n</div>\r\n\r\n<div id=\"outline-container-orge9839bb\" class=\"outline-2\">\r\n<h2 id=\"orge9839bb\"><span class=\"section-number-2\">1</span> Introduction</h2>\r\n<div class=\"outline-text-2\" id=\"text-1\">\r\n<p>\r\nConsensus algorithms allow a collection of machines to work as a coherent group\r\nthat can survive the failures of some of its members. Because of this, they play\r\na key role in building reliable large-scale software systems. Paxos [15, 16] has\r\ndominated the discussion of consensus algorithms over the last decade: most\r\nimplementations of consensus are based on Paxos or influenced by it, and Paxos\r\nhas become the primary vehicle used to teach students about consensus.\r\n</p>\r\n\r\n<p>\r\nUnfortunately, Paxos is quite difficult to understand, in spite of numerous\r\nattempts to make it more approachable. Furthermore, its architecture requires\r\ncomplex changes to support practical systems. As a result, both system builders\r\nand students struggle with Paxos.\r\n</p>\r\n\r\n<p>\r\nAfter struggling with Paxos ourselves, we set out to find a new consensus\r\nalgorithm that could provide a better foundation for system building and\r\neducation. Our approach was unusual in that our primary goal was\r\nunderstandability: could we define a consensus algorithm for practical systems\r\nand describe it in a way that is significantly easier to learn than Paxos?\r\nFurthermore, we wanted the algorithm to facilitate the development of intuitions\r\nthat are essential for system builders. It was important not just for the\r\nalgorithm to work, but for it to be obvious why it works.\r\n</p>\r\n\r\n<p>\r\nThe result of this work is a consensus algorithm called Raft. In designing Raft\r\nwe applied specific techniques to improve understandability,including\r\ndecomposition (Raft separates leader election, log replication, and safety) and\r\nThis tech report is an extended version of [32]; additional material is noted\r\nwith a gray bar in the margin. Published May 20, 2014. state space reduction\r\n(relative to Paxos, Raft reduces the degree of nondeterminism and the ways\r\nservers can be inconsistent with each other). A user study with 43 students\r\nat two universities shows that Raft is significantly easier to understand than\r\nPaxos: after learning both algorithms, 33 of these students were able to answer\r\nquestions about Raft better than questions about Paxos.\r\n</p>\r\n\r\n<p>\r\nRaft is similar in many ways to existing consensus algorithms (most notably, Oki\r\nand Liskov's Viewstamped Replication [29, 22]), but it has several novel\r\nfeatures:\r\n</p>\r\n\r\n<p>\r\n1/. Strong leader: Raft uses a stronger form of leadership than other consensus\r\nalgorithms. For example, log entries only flow from the leader to other servers.\r\nThis simplifies the management of the replicated log and makes Raft easier to\r\nunderstand.\r\n</p>\r\n\r\n<p>\r\n2/. Leader election: Raft uses randomized timers to elect leaders. This adds\r\nonly a small amount of mechanism to the heartbeats already required for any\r\nconsensus algorithm, while resolving conflicts simply and rapidly.\r\n</p>\r\n\r\n<p>\r\n3/. Membership changes: Raft's mechanism for changing the set of servers in the\r\ncluster uses a new joint consensus approach where the majorities of two\r\ndifferent configurations overlap during transitions. This allows the cluster to\r\ncontinue operating normally during configuration changes.\r\n</p>\r\n\r\n<p>\r\nWe believe that Raft is superior to Paxos and other consensus algorithms, both\r\nfor educational purposes and as a foundation for implementation. It is simpler\r\nand more understandable than other algorithms; it is described completely enough\r\nto meet the needs of a practical system; it has several open-source\r\nimplementations and is used by several companies; its safety properties have\r\nbeen formally specified and proven; and its efficiency is comparable to other\r\nalgorithms.\r\n</p>\r\n\r\n<p>\r\nThe remainder of the paper introduces the replicated state machine problem\r\n(Section 2), discusses the strengths and weaknesses of Paxos (Section 3),\r\ndescribes our general approach to understandability (Section 4), presents the\r\nRaft consensus algorithm (Sections 5–8), evaluates Raft (Section 9), and\r\ndiscusses related work (Section 10).\r\n</p>\r\n</div>\r\n</div>\r\n\r\n<div id=\"outline-container-orgae8bb96\" class=\"outline-2\">\r\n<h2 id=\"orgae8bb96\"><span class=\"section-number-2\">2</span> Replicated state machines</h2>\r\n<div class=\"outline-text-2\" id=\"text-2\">\r\n<p>\r\nConsensus algorithms typically arise in the context of replicated state machines\r\n[37]. In this approach, state machines on a collection of servers compute\r\nidentical copies of the same state and can continue operating even if some of\r\nthe servers are down. Replicated state machines are used to solve a variety of\r\nfault tolerance problems in distributed systems. For example, large-scale\r\nsystems that have a single cluster leader, such as GFS [8], HDFS [38], and\r\nRAMCloud [33], typically use a separate replicated state machine to manage\r\nleader election and store configuration information that must survive leader\r\ncrashes. Examples of replicated state machines include Chubby [2] and\r\nZooKeeper [11].\r\n</p>\r\n\r\n\r\n<div id=\"org1bdf0c5\" class=\"figure\">\r\n<p><img src=\"img/raft-figure1.jpg\" alt=\"raft-figure1.jpg\" />\r\n</p>\r\n<p><span class=\"figure-number\">Figure 1: </span>Replicated state machine architecture. The consensus algorithm manages a replicated log containing state machine commands from clients. The state machines process identical sequences of commands from the logs, so they produce the same outputs.</p>\r\n</div>\r\n\r\n<p>\r\nReplicated state machines are typically implemented using a replicated log, as\r\nshown in Figure 1. Each server s",
          "truncated": true
        },
        {
          "bytes": 900468,
          "kind": "research",
          "path": "research/cultnet-distributed-database/dynamo-sosp2007.pdf",
          "text": "%PDF-1.3\r%����\r\n101 0 obj <</Linearized 1/L 839680/O 103/E 178543/N 16/T 837612/H [ 876 568]>>\rendobj\r            \r\nxref\r\n101 29\r\n0000000016 00000 n\r\n0000001444 00000 n\r\n0000001525 00000 n\r\n0000001727 00000 n\r\n0000001863 00000 n\r\n0000002320 00000 n\r\n0000002760 00000 n\r\n0000003302 00000 n\r\n0000004074 00000 n\r\n0000004327 00000 n\r\n0000004601 00000 n\r\n0000004869 00000 n\r\n0000004982 00000 n\r\n0000005229 00000 n\r\n0000006383 00000 n\r\n0000006918 00000 n\r\n0000007199 00000 n\r\n0000008288 00000 n\r\n0000009351 00000 n\r\n0000010473 00000 n\r\n0000011530 00000 n\r\n0000012622 00000 n\r\n0000013685 00000 n\r\n0000014725 00000 n\r\n0000035703 00000 n\r\n0000072079 00000 n\r\n0000119236 00000 n\r\n0000150351 00000 n\r\n0000000876 00000 n\r\ntrailer\r\n<</Size 130/Prev 837600/Root 102 0 R/Info 100 0 R/ID[<C1C715AA2D20C7C67D8119B9678A17C0><E970E0B0A53567468202B6FED23F070B>]>>\r\nstartxref\r\n0\r\n%%EOF\r\n        \r\n129 0 obj<</Length 474/Filter/FlateDecode/I 620/L 604/S 432/T 546>>stream\r\nx�b```\u0006��\f�\f\f�\u0002\f�\f\b \f\u0014cc`a�������Ȣ��\u0010�됺���qņ�\u000elݬ|\u0015\u0012;��Y.wh\u0005�\u0019+x��\u00196l\u0007jl��\u001ez���\u000e�UH�1(\u0004�q\u0014�\u001a�NOh��ql29\u001a\u0016��jr\b((\u001aY��Q�!2٧\u0010,+��,���$\u001a\u0019Y&_y\t�&IB�Hh�\r �\u0010�f\tT�\ndL2��\u0005��2�q\u0014r�I���ި}�!2�\rf���,w��\u001b@5*\t�\u0002\u000b�\u0002\u0015�]��s\u0019�X���B�����\t\u000bN\u0000\u001d�Z\u0006v+�DG\u0007\u0003���GG\u0003��K(�\u0003�I\u00009\f\fJ\u0011 1\u0010��\u0000�3\u0018\u001b�)\u0006�\b0�ll\u0001�\u0019\u0005��4�*\u001a�,a\u000b�V�\f�1b\u0019`���2 ���\nv4\u0010\u0017�@`��\u0013\u001a\u0005���X\u000b,�� �0�!�q\u0016c'�{��\f�\u0018\u001e3�epb8�\u0012����\u0015�\u0010�\u000f\u0016c�O,��?�\u001f\u0006\u001e�}���2&�#��K\f�\u0018:\u0019�I�1\u00043|\u0000�T�pIU�\u0001\u001d�1��O\u0005�\u0002`�'\u0006\u000e��1T1X\n����J\u0010\u000b{��s��}�P\u001c\u0003�V+ �\u0004��@���sd?T�K�\u0000\u0003\u0000���z\r\nendstream\rendobj\r102 0 obj<</Metadata 99 0 R/Pages 96 0 R/Type/Catalog/PageLabels 94 0 R>>\rendobj\r103 0 obj<</CropBox[0 0 612 792]/Parent 97 0 R/Contents[114 0 R 117 0 R 118 0 R 119 0 R 120 0 R 121 0 R 122 0 R 123 0 R]/Rotate 0/MediaBox[0 0 612 792]/Thumb 78 0 R/Resources 104 0 R/Type/Page>>\rendobj\r104 0 obj<</Font<</TT2 105 0 R/TT4 106 0 R/TT6 107 0 R/TT8 108 0 R/TT10 115 0 R>>/ProcSet[/PDF/Text]/ExtGState<</GS1 112 0 R>>>>\rendobj\r105 0 obj<</Subtype/TrueType/FontDescriptor 109 0 R/LastChar 146/Widths[278 0 0 0 0 0 0 0 333 333 0 0 0 333 0 278 0 0 0 0 0 0 0 0 0 0 333 0 0 0 0 0 0 722 0 0 722 667 0 0 722 0 0 722 0 0 0 0 0 0 0 667 0 0 0 0 0 0 0 0 0 0 0 0 0 556 611 556 611 556 333 611 611 278 0 0 278 889 611 611 0 0 389 556 333 611 556 0 556 556 500 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 278]/BaseFont/OGOFEC+Arial-BoldMT/FirstChar 32/Encoding/WinAnsiEncoding/Type/Font>>\rendobj\r106 0 obj<</Subtype/TrueType/FontDescriptor 113 0 R/LastChar 122/Widths[278 0 0 0 0 0 0 0 333 333 0 0 278 333 278 0 556 556 556 556 556 556 556 556 556 556 0 0 0 0 0 0 0 667 0 722 722 0 0 778 722 0 500 667 556 833 0 0 667 0 0 667 0 0 667 944 0 0 0 0 0 0 0 0 0 556 556 500 556 556 0 556 556 222 0 500 222 833 556 556 556 0 333 500 278 556 500 722 500 500 500]/BaseFont/OGOFFD+ArialMT/FirstChar 32/Encoding/WinAnsiEncoding/Type/Font>>\rendobj\r107 0 obj<</Subtype/TrueType/FontDescriptor 110 0 R/LastChar 146/Widths[250 0 0 0 0 0 833 0 333 333 0 0 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 0 0 570 0 500 0 722 667 722 722 667 611 778 778 389 0 778 667 944 722 778 611 0 722 556 667 722 722 1000 722 722 0 0 0 0 0 0 0 500 556 444 556 444 333 500 556 278 333 556 278 833 556 500 556 556 444 389 333 556 500 722 500 500 444 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 333]/BaseFont/OGOFGF+TimesNewRomanPS-BoldMT/FirstChar 32/Encoding/WinAnsiEncoding/Type/Font>>\rendobj\r108 0 obj<</Subtype/TrueType/FontDescriptor 111 0 R/LastChar 248/Widths[250 0 408 0 500 833 0 180 333 333 500 564 250 333 250 278 500 500 500 500 500 500 500 500 500 500 278 278 564 564 564 0 0 722 667 667 722 611 556 722 722 333 389 722 611 889 722 722 556 722 667 556 611 722 722 944 722 722 611 333 0 333 0 0 0 444 500 444 500 444 333 500 500 278 278 500 278 778 500 500 500 500 333 389 278 500 500 722 500 500 444 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 333 444 444 0 500 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 500]/BaseFont/OGOFHH+TimesNewRomanPSMT/FirstChar 32/Encoding/WinAnsiEncoding/Type/Font>>\rendobj\r109 0 obj<</StemV 138/FontName/OGOFEC+Arial-BoldMT/FontStretch/Normal/FontFile2 124 0 R/FontWeight 700/Flags 32/Descent -211/FontBBox[-628 -376 2000 1010]/Ascent 905/FontFamily(Arial)/CapHeight 718/XHeight 515/Type/FontDescriptor/ItalicAngle 0>>\rendobj\r110 0 obj<</StemV 136/FontName/OGOFGF+TimesNewRomanPS-BoldMT/FontStretch/Normal/FontFile2 125 0 R/FontWeight 700/Flags 34/Descent -216/FontBBox[-558 -307 2000 1026]/Ascent 891/FontFamily(Times New Roman)/CapHeight 656/XHeight -546/Type/FontDescriptor/ItalicAngle 0>>\rendobj\r111 0 obj<</StemV 82/FontName/OGOFHH+TimesNewRomanPSMT/FontStretch/Normal/FontFile2 126 0 R/FontWeight 400/Flags 34/Descent -216/FontBBox[-568 -307 2000 1007]/Ascent 891/FontFamily(Times New Roman)/CapHeight 656/XHeight -546/Type/FontDescriptor/ItalicAngle 0>>\rendobj\r112 0 obj<</OPM 1/HT/Default/OP false/BG2/Default/op false/Type/ExtGState/SA false/UCR2/Default/SM 0.02>>\rendobj\r113 0 obj<</StemV 88/FontName/OGOFFD+ArialMT/FontStretch/Normal/FontFile2 128 0 R/FontWeight 400/Flags 32/Descent -211/FontBBox[-665 -325 2000 1006]/Ascent 905/FontFamily(Arial)/CapHeight 718/XHeight 515/Type/FontDescriptor/ItalicAngle 0>>\rendobj\r114 0 obj<</Length 1083/Filter/FlateDecode>>stream\r\nH�lUێ�6\u0010}�W�#\u0005X�D��''\u000bl��@\u0010\u000b�â\u000f\\��ٕ(W��n>#_�!)��E�X�9g�\\��q��A�>��\u000fm� �v�JkH�\u000f/UC\u001b\u0006U�Q\u0006��J���m�Jh�$h{Y��WŇ�\u0017�\f�Ǩ~j�,\u000f��\u00156g.{��\u000b�*^�3�g\u0001[3N\u0002��o\u000b�/`̃1h\u0012��e�<X�@�\u0000WZ�'�(g-N'\u0001\u000f�\u0013W;��x��\u000f�̣��/�4�eU`���F�1GU#����;�q\u0005_�p�J��qV�rB׼X\\əO�#Z}�/s�Oܠ�c\u001e��&9�)M\u000bk�ifW��Am�Rq}����>\u000e\u001cA7��\u0017�ɾ;J|�^��0+Z$\u0001\u0013]����3�����QZ�o\t�\u0018�>�_xd�n�Ӹ��*G\u0003\u0015�?Ť��A�ڹ�h����XU�\n��JZ��η��ꮸQAK�\"D�\t��!�\bxͯ%̰L%��j�.\u0018��z���1K-��Ѯ�!�|ܶ\u0011�U��O�����wl\u001cH\u0003E\u000eE��2\u000fL��g�\u0016��$�E/�sTњ�^:����F��@�\t\u0013n`�RF3�#dT\u0011��Y��x�ӆ��eJ�{\u0000�C��.�li0K�\u0005����#FoP��2\"`�{4s\u0014�,\u000f\u0007��\u0006\u00054Q�\btGg�E\u0015\nO�\"<J\u001c`,���\u0002X�/\u0005Ί̃�y'��l\u0006�\u0012�� R�]\u0011[%�fms�,F�\bY.Χ�%��\u0001\u0011�b7\u001f�+�gN&D\u0019Obr\u0016ܿ3rT�\u0017��\u0013ߐH<\u0010^:2c���ž�S��\u0015�9��F\b��^)-�������ELT\u0012a\u001f\f��v���\u0019k�E�����ETT\u0018k�A˃�{�qe`/����$��.�;�����i<F�I�f�:���/�k�3\u001a�Y��\nz�\")\u000bA�@Ԋ[\u0011\u001cK3�VC�\u000f\n-��'���bv#X����8\t=Q�KO�zn��4��r��ї�4�g�C�ZLgi�{�96���sr�g\f\u0005Q����>r��\u0018Y�\b��3�kl�ڒ^\u0010`\u001d�.��\u000bZ.�s���\bN\u0016J؋A(#v�`��ϒ�\n�xZ\u0006��]�R�'_Cm��CQ�\u001c��ܙ�[\b[e\u001c�\u0010�\b��[�S�'a�5VQ��N�6ќ�rp��\u0014��$+��\u00055\u0015��\u0007%\f��\u0002X�\u0013Λ2���X�\u001b��\u0010�-�Տ\u001dG\t�4�bJm�6��X֩U������G�&}ǳy�u|\u000f����B�i���u\b)l\f>a\rp���x�K�0Nr�Į��o�`�D6_�C\\�(q\u001d��?\u0001\u0006\u0000�V!o\r\nendstream\rendobj\r115 0 obj<</Subtype/TrueType/FontDescriptor 116 0 R/LastChar 148/Widths[250 0 0 0 0 0 0 0 0 0 0 0 250 333 250 278 500 500 500 500 500 0 500 500 500 500 333 0 0 0 0 0 0 611 611 667 722 611 611 722 722 333 0 0 556 833 667 722 611 722 611 500 556 722 611 0 611 0 0 0 0 0 0 0 0 500 500 444 500 444 278 500 500 278 278 444 278 722 500 500 500 500 389 389 278 500 444 667 444 444 389 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 333 556 556]/BaseFont/OGOFJI+TimesNewRomanPS-ItalicMT/FirstChar 32/Encoding/WinAnsiEncoding/Type/Font>>\rendobj\r116 0 obj<</StemV 71.742/FontName/OGOFJI+TimesNewRomanPS-ItalicMT/FontStretch/Normal/FontFile2 127 0 R/FontWeight 400/Flags 98/Descent -216/FontBBox[-498 -307 1120 1023]/Ascent 891/FontFamily(Times New Roman)/CapHeight 656/XHeight -546/Type/FontDescriptor/ItalicAngle -15>>\rendobj\r117 0 obj<</Length 1018/Filter/FlateDecode>>stream\r\nH�tU˖�6\f��+��ΉTQ/K˞z�l��w9Ypd�f\"�>zx2���\u001f�\u000b��x&�J6\t\\\u0000\u0017�e��Y�Iq�vY*%�z�E�\u001bn�j;O�L/��]��AQ�D����xرs\u0019���%�/𶳱�[��%N괈���>��I�W-��z|��d��M�hF�����N���-@�\u0005h|\u00001�j��Lb`�<Rq�\u0003�O�X\u0004�C��;-\u00185Ya)��ޯ���Eݔ\u001eܝ)ɉ�M\\#�>N2$��:��2��\u0012��4�����p�\u001b�\u0014<���\u0017��>R�$��mT���\u0006��W\u001b\u0004d3��\\��(��$�,�}��;��jԂ�k#�)s������V�\u001e�T< 6�\u00011p��+��+�)tD�F�O���Nz2\u0017�a(F\u0003�^[#W�\"�0T�\u0019n�\u001e��f�,�w�\u0004\u000b�Ģ��wk\u001f\r�Pk{\u0002���e\u001b\u0002\u0014e��j.�\u001e(%\u0017-�\r$߹]h\u00033\\��\u0015\u0018\u0002��N=�o>I,argCE�m�/��(-a1{w7��v\u000eW�A�1�\u0003�\u0006cj\u000e\u0000\u000e!�ȟk\u001f���ݟ����{z���P���۪XW�{]\u0000DSw�h��edh�0��N,|\u0018\f���Kw7'>\r��.v�1����������\r`D���e�=���i10F,\u001b��\u0018�7\u0010G�ˉ��\fR��k\u0000��w�0�\u0011�\b\f�Q�\u001f���!�\u001b�ʟC�\u0018 d�\u0014�\u001ab�A{*^\u000f��\"-���]5��\u0003�\u001f�6h�\u0001;�P�4/V\r@���P\u001dՍ���=��\u0012V\u0010\u0000�\u001d$\f��Y��u�9��f�\u001a��>�bOغN���xqդ�\u000b��Ӵ2(o4nJ���y�g_��\bQ\u001c�w\u001dH�?��\u0004�\u0012\u000b��1\u0018A\u0015�����\f\u0019��򐵕��NJ���\u0017h���U�ۭ7\u001d/y��zb*N��g�7:�o�����3 fH������S�\t%q� f�o�{�*�&�\u001e�7�O=�Bfh\u001eD\u0006s\u001b���==��z�1��w>Y�A�Fٚ\r70�\u0007����Fh\"�\u001c=^\u001e\u000f{��\u0015�\n� �9�v<�\u0002=>C8��\t|�R\u0014\r��qx`%�Z���\u000f�m\u00177\u001a4�����\u001bv�S7���Ɖ�c�7\u001e�e�V�E�\u0002�\u0001�K`̟^���ޖ萖��\u0012b�\u0002��\u001e��t������ч&��\u0007\u001b�B^X\u0001�g��B�j��I�\u0007*�*�\u0013`\u0000h�\u001f\u0016\r\nendstream\rendobj\r118 0 obj<</Length 993/Filter/FlateDecode>>stream\r\nH�|Uێ�6\u0010}�W�#\t�X��'OE\u0017(\u0012�� �>-�i[�,�\u0014�E�\u0019��ΐ�/�m��K�2g�3���槶�AB���\"��\nRh�7�HӴ���+�C��yb_y%\u0014{�\u0013)2\u0006��X���5��M�.YG�\r��\u000e\u001b�=܋�/\u0005O��Y\u0001�k�q��T�M�\u0001>�9\u0002\u0013�G$0���?(�2$��\f�.��sr2&�p⹨���\u001b&��sJ\u0005\u001e��\b��'��ٌG9s����q���rp\u0005R�A�\u000b\u0003\r1�b=�E�C���26�\u0003/�����\u001d�K\u0016*�SA�TL�ӊW�x�\nx���^\u0001s�V\u0015\n��#\u0016P����Gi�0ev�\u0011S�lk|\u0017YW�hdd����ؾT�Y۝��n���(�L\u0003F!�欀�\f�\u0012I�\u0004/+(r�2%\n\u0005�1D/�A�.Q�_�����j�Մ8��Ƈi@f��u��9<2VUf�\u0012u.C�\u0004f*���7vp��|G&��3\u001d�u7�\u0006�Y�'Ff��n\u001cV�ůC(}�%�ǜ�\u0004��_�t�\u0006�\r�Q3�C��K�\u001a\r�\u00062\f\u001ar���\u0001\"�u\u0003P�]�\u0017-�D���`u0s�\u0004��Ӎ\u0006�Q\"UDI$�I\u0011�VkWUE�������\"��~\u001f>�_\u001e������\t�m\u0001)�\u001a�YS *W}J��s�\u0005͍��\u0004v�f�Qgu�e����q��\f[\r:Azs֛�2=\n�:4��5��\u000e\tn�$�\u0005M�x\u000fܡs\u0010\u0000�7�|4\u001dQ]�G!�9p���'\r�\bY�q�\u001d\u001c���8\u000e\u0006\u001f�����\t��΀�'",
          "truncated": true
        },
        {
          "bytes": 46272,
          "kind": "research",
          "path": "research/cultnet-distributed-database/crdt-arxiv-1805.06358.html",
          "text": "<!DOCTYPE html>\r\n<html lang=\"en\">\r\n\r\n<head>  <title>[1805.06358] Conflict-free Replicated Data Types (CRDTs)</title>\r\n  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n  <link rel=\"apple-touch-icon\" sizes=\"180x180\" href=\"/static/browse/0.3.4/images/icons/apple-touch-icon.png\">\r\n  <link rel=\"icon\" type=\"image/png\" sizes=\"32x32\" href=\"/static/browse/0.3.4/images/icons/favicon-32x32.png\">\r\n  <link rel=\"icon\" type=\"image/png\" sizes=\"16x16\" href=\"/static/browse/0.3.4/images/icons/favicon-16x16.png\">\r\n  <link rel=\"manifest\" href=\"/static/browse/0.3.4/images/icons/site.webmanifest\">\r\n  <link rel=\"mask-icon\" href=\"/static/browse/0.3.4/images/icons/safari-pinned-tab.svg\" color=\"#5bbad5\">\r\n  <meta name=\"msapplication-TileColor\" content=\"#da532c\">\r\n  <meta name=\"theme-color\" content=\"#ffffff\">\r\n  <link rel=\"stylesheet\" type=\"text/css\" media=\"screen\" href=\"/static/browse/0.3.4/css/arXiv.css?v=20260318\" />\r\n  <link rel=\"stylesheet\" type=\"text/css\" media=\"print\" href=\"/static/browse/0.3.4/css/arXiv-print.css?v=20200611\" />\r\n  <link rel=\"stylesheet\" type=\"text/css\" media=\"screen\" href=\"/static/browse/0.3.4/css/browse_search.css\" />\r\n  <script language=\"javascript\" src=\"/static/browse/0.3.4/js/accordion.js\" ></script>\r\n  <script language=\"javascript\" src=\"/static/browse/0.3.4/js/optin-modal.js?v=20250819\"></script>\r\n  \r\n  <link rel=\"canonical\" href=\"https://arxiv.org/abs/1805.06358\"/>\r\n  <meta name=\"description\" content=\"Abstract page for arXiv paper 1805.06358: Conflict-free Replicated Data Types (CRDTs)\"><meta property=\"og:type\" content=\"website\" />\r\n<meta property=\"og:site_name\" content=\"arXiv.org\" />\r\n<meta property=\"og:title\" content=\"Conflict-free Replicated Data Types (CRDTs)\" />\r\n<meta property=\"og:url\" content=\"https://arxiv.org/abs/1805.06358v1\" />\r\n<meta property=\"og:image\" content=\"/static/browse/0.3.4/images/arxiv-logo-fb.png\" />\r\n<meta property=\"og:image:secure_url\" content=\"/static/browse/0.3.4/images/arxiv-logo-fb.png\" />\r\n<meta property=\"og:image:width\" content=\"1200\" />\r\n<meta property=\"og:image:height\" content=\"700\" />\r\n<meta property=\"og:image:alt\" content=\"arXiv logo\"/>\r\n<meta property=\"og:description\" content=\"A conflict-free replicated data type (CRDT) is an abstract data type, with a well defined interface, designed to be replicated at multiple processes and exhibiting the following properties: (1) any replica can be modified without coordinating with another replicas; (2) when any two replicas have received the same set of updates, they reach the same state, deterministically, by adopting mathematically sound rules to guarantee state convergence.\"/>\r\n<meta name=\"twitter:site\" content=\"@arxiv\"/>\r\n<meta name=\"twitter:card\" content=\"summary\"/>\r\n<meta name=\"twitter:title\" content=\"Conflict-free Replicated Data Types (CRDTs)\"/>\r\n<meta name=\"twitter:description\" content=\"A conflict-free replicated data type (CRDT) is an abstract data type, with a well defined interface, designed to be replicated at multiple processes and exhibiting the following properties: (1)...\"/>\r\n<meta name=\"twitter:image\" content=\"https://static.arxiv.org/icons/twitter/arxiv-logo-twitter-square.png\"/>\r\n<meta name=\"twitter:image:alt\" content=\"arXiv logo\"/>\r\n  <link rel=\"stylesheet\" media=\"screen\" type=\"text/css\" href=\"/static/browse/0.3.4/css/tooltip.css\"/><link rel=\"stylesheet\" media=\"screen\" type=\"text/css\" href=\"https://static.arxiv.org/js/bibex-dev/bibex.css?20200709\"/>  <script src=\"/static/browse/0.3.4/js/mathjaxToggle.min.js\" type=\"text/javascript\"></script>  <script src=\"//code.jquery.com/jquery-latest.min.js\" type=\"text/javascript\"></script>\r\n  <script src=\"//cdn.jsdelivr.net/npm/js-cookie@2/src/js.cookie.min.js\" type=\"text/javascript\"></script>\r\n  <script src=\"//cdn.jsdelivr.net/npm/dompurify@2.3.5/dist/purify.min.js\"></script>\r\n  <script src=\"/static/browse/0.3.4/js/toggle-labs.js?20241022\" type=\"text/javascript\"></script>\r\n  <script src=\"/static/browse/0.3.4/js/cite.js\" type=\"text/javascript\"></script><meta name=\"citation_title\" content=\"Conflict-free Replicated Data Types (CRDTs)\" /><meta name=\"citation_author\" content=\"Preguiça, Nuno\" /><meta name=\"citation_author\" content=\"Baquero, Carlos\" /><meta name=\"citation_author\" content=\"Shapiro, Marc\" /><meta name=\"citation_doi\" content=\"10.1007/978-3-319-63962-8\\_185-1\" /><meta name=\"citation_date\" content=\"2018/05/16\" /><meta name=\"citation_online_date\" content=\"2018/05/16\" /><meta name=\"citation_pdf_url\" content=\"https://arxiv.org/pdf/1805.06358\" /><meta name=\"citation_arxiv_id\" content=\"1805.06358\" /><meta name=\"citation_abstract\" content=\"A conflict-free replicated data type (CRDT) is an abstract data type, with a well defined interface, designed to be replicated at multiple processes and exhibiting the following properties: (1) any replica can be modified without coordinating with another replicas; (2) when any two replicas have received the same set of updates, they reach the same state, deterministically, by adopting mathematically sound rules to guarantee state convergence.\" />\r\n</head>\r\n\r\n<body  class=\"with-cu-identity\">\r\n  \r\n  \r\n  <div class=\"flex-wrap-footer\">\r\n    <header>\r\n      <a href=\"#content\" class=\"is-sr-only\">Skip to main content</a>\r\n      <!-- start desktop header -->\r\n      <div class=\"columns is-vcentered is-hidden-mobile\" id=\"cu-identity\">\r\n        <div class=\"column\" id=\"cu-logo\">\r\n          <a href=\"https://www.cornell.edu/\"><img src=\"/static/browse/0.3.4/images/icons/cu/cornell-reduced-white-SMALL.svg\" alt=\"Cornell University\" /></a>\r\n        </div><!-- /from April 7 at 1:00 AM to May 29 at 21:40 --><!-- /from May 2 at 1:00 AM to May 5 at 9:45 AM --><div class=\"column banner-minimal\">\r\n            <a href=\"https://tech.cornell.edu/arxiv/\" target=\"_blank\">Learn about arXiv becoming an independent nonprofit.</a>\r\n        </div><div class=\"column\" id=\"support-ack\">\r\n          <span id=\"support-ack-url\">We gratefully acknowledge support from the Simons Foundation, <a href=\"https://info.arxiv.org/about/ourmembers.html\">member institutions</a>, and all contributors.</span>\r\n          <a href=\"https://info.arxiv.org/about/donate.html\" class=\"btn-header-donate\">Donate</a>\r\n        </div>\r\n      </div>\r\n\r\n      <div id=\"header\" class=\"is-hidden-mobile\">\r\n<a aria-hidden=\"true\" tabindex=\"-1\" href=\"/IgnoreMe\"></a>\r\n  <div class=\"header-breadcrumbs is-hidden-mobile\">\r\n    <a href=\"/\"><img src=\"/static/browse/0.3.4/images/arxiv-logo-one-color-white.svg\" alt=\"arxiv logo\" style=\"height:40px;\"/></a> <span>&gt;</span> <a href=\"/list/cs/recent\">cs</a> <span>&gt;</span> arXiv:1805.06358\r\n  </div>\r\n\r\n        <div class=\"columns is-vcentered is-mobile\" style=\"justify-content: flex-end;\">\r\n        </div>\r\n\r\n          <div class=\"search-block level-right\">\r\n    <form class=\"level-item mini-search\" method=\"GET\" action=\"https://arxiv.org/search\">\r\n      <div class=\"field has-addons\">\r\n        <div class=\"control\">\r\n          <input class=\"input is-small\" type=\"text\" name=\"query\" placeholder=\"Search...\" aria-label=\"Search term or terms\" />\r\n          <p class=\"help\"><a href=\"https://info.arxiv.org/help\">Help</a> | <a href=\"https://arxiv.org/search/advanced\">Advanced Search</a></p>\r\n        </div>\r\n        <div class=\"control\">\r\n          <div class=\"select is-small\">\r\n            <select name=\"searchtype\" aria-label=\"Field to search\">\r\n              <option value=\"all\" selected=\"selected\">All fields</option>\r\n              <option value=\"title\">Title</option>\r\n              <option value=\"author\">Author</option>\r\n              <option value=\"abstract\">Abstract</option>\r\n              <option value=\"comments\">Comments</option>\r\n              <option value=\"journal_ref\">Journal reference</option>\r\n              <option value=\"acm_class\">ACM classification</option>\r\n              <option value=\"msc_class\">MSC classification</option>\r\n              <option value=\"report_num\">Report number</option>\r\n              <option value=\"paper_id\">arXiv identifier</option>\r\n              <option value=\"doi\">DOI</option>\r\n              <option value=\"orcid\">ORCID</option>\r\n              <option value=\"author_id\">arXiv author ID</option>\r\n              <option value=\"help\">Help pages</option>\r\n              <option value=\"full_text\">Full text</option>\r\n            </select>\r\n          </div>\r\n        </div>\r\n        <input type=\"hidden\" name=\"source\" value=\"header\">\r\n        <button class=\"button is-small is-cul-darker\">Search</button>\r\n      </div>\r\n    </form>\r\n  </div>\r\n     </div><!-- /end desktop header -->\r\n\r\n      <div class=\"mobile-header\">\r\n        <div class=\"columns is-mobile\">\r\n          <div class=\"column logo-arxiv\"><a href=\"https://arxiv.org/\"><img src=\"/static/browse/0.3.4/images/arxiv-logomark-small-white.svg\" alt=\"arXiv logo\" style=\"height:60px;\" /></a></div>\r\n          <div class=\"column logo-cornell\"><a href=\"https://www.cornell.edu/\">\r\n            <picture>\r\n              <source media=\"(min-width: 501px)\"\r\n                srcset=\"/static/browse/0.3.4/images/icons/cu/c",
          "truncated": true
        },
        {
          "bytes": 309768,
          "kind": "research",
          "path": "research/cultnet-distributed-database/dynamo-amazon-science.html",
          "text": "<!DOCTYPE html>\r\n<html class=\"PublicationDetailPage\" lang=\"en\">\r\n    <head>\r\n    <meta charset=\"UTF-8\">\r\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1, maximum-scale=5\">\r\n\r\n    <style data-cssvarsponyfill=\"true\">\r\n                :root {\r\n        --primaryColor: #007cb6;\r\n        --secondaryColor: #e3661b;\r\n\r\n        --errorColor: #f44336;\r\n\r\n        --primaryTextColor: #232f3e;\r\n        --secondaryTextColor: #6c7778;\r\n\r\n        --headerBgColor: #ffffff;\r\n        --headerBorderColor: #EAEDED;\r\n        --headerMenuBgColor: #ffffff;\r\n        --headerMenuSubNavTextColor: #232f3e;\r\n        --aboveBgColor: #fafafa;\r\n        --belowBgColor: #fafafa;\r\n\r\n        --footerBgColor: #232f3e;\r\n        --footerTextColor: #ffffff;\r\n\r\n        --buttonBgColor: transparent;\r\n        --buttonTextColor: #007cb6;\r\n\r\n        --primaryHeadlineFont: \"Ember Modern Display V1.1\";\r\n        --secondaryHeadlineFont: \"Ember Modern Display V1.1\";\r\n        --tertiaryHeadlineFont: \"Ember Modern Display V1.1\";\r\n        --bodyFont: \"Ember Modern Text V1.1\";\r\n\r\n        --contentWidth: 1240px;\r\n        }\r\n\r\n    </style>\r\n\r\n    <link data-cssvarsponyfill=\"true\" class=\"Webpack-css\" rel=\"stylesheet\" href=\"https://cdn.amazon.science/resource/0000016e-128c-d913-a16f-9edc0a5f0000/styleguide/All.min.050875a96933f2f14034b470d2acb341.css\">\r\n\r\n    <style>.PromoRelatedContent a.Link {\r\n    text-decoration: none;\r\n}</style>\r\n<title>Dynamo: Amazon’s highly available key-value store - Amazon Science</title><link rel=\"canonical\" href=\"https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store\"><meta name=\"brightspot.contentId\" content=\"0000017e-82c9-de9e-a7ff-c7df71830000\">\r\n    <link rel=\"alternate\" href=\"https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store\" hreflang=\"x-default\">\r\n\r\n<link rel=\"alternate\" href=\"https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store\" hreflang=\"en\"><link rel=\"apple-touch-icon\" sizes=\"180x180\"href=\"/apple-touch-icon.png\"><link rel=\"icon\" type=\"image/png\"href=\"/favicon-32x32.png\"><link rel=\"icon\" type=\"image/png\"href=\"/favicon-16x16.png\">\r\n    \r\n\r\n    \r\n\r\n    <meta property=\"og:title\" content=\"Dynamo: Amazon’s highly available key-value store\">\r\n\r\n    <meta property=\"og:url\" content=\"https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store\">\r\n\r\n    <meta property=\"og:image\" content=\"https://cdn.amazon.science/dims4/default/5e7b277/2147483647/strip/true/crop/1200x630+0+0/resize/1200x630!/quality/90/?url=https%3A%2F%2Famzn-science-production-science.s3.us-east-1.amazonaws.com%2Fscience%2F7c%2F4e%2Fd4e963d24f6c966def82a45d2bf1%2Famazon-science-og-image-squid.png\">\r\n\r\n    \r\n    <meta property=\"og:image:url\" content=\"https://cdn.amazon.science/dims4/default/5e7b277/2147483647/strip/true/crop/1200x630+0+0/resize/1200x630!/quality/90/?url=https%3A%2F%2Famzn-science-production-science.s3.us-east-1.amazonaws.com%2Fscience%2F7c%2F4e%2Fd4e963d24f6c966def82a45d2bf1%2Famazon-science-og-image-squid.png\">\r\n    \r\n    <meta property=\"og:image:width\" content=\"1200\">\r\n    <meta property=\"og:image:height\" content=\"630\">\r\n    <meta property=\"og:image:type\" content=\"image/png\">\r\n    \r\n    <meta property=\"og:image:alt\" content=\"Amazon Science OG image squid.png\">\r\n    \r\n\r\n\r\n    <meta property=\"og:description\" content=\"Reliability at massive scale is one of the biggest challenges we face at Amazon.com, one of the largest e-commerce operations in the world; even the slightest outage has significant financial consequences and impacts customer trust. The Amazon.com platform, which provides services for many web…\">\r\n\r\n    <meta property=\"og:site_name\" content=\"Amazon Science\">\r\n\r\n\r\n\r\n    <meta property=\"og:type\" content=\"website\">\r\n\r\n    \r\n    <meta name=\"twitter:card\" content=\"summary_large_image\"/>\r\n    \r\n    \r\n    \r\n    \r\n    <meta name=\"twitter:description\" content=\"Reliability at massive scale is one of the biggest challenges we face at Amazon.com, one of the largest e-commerce operations in the world; even the slightest outage has significant financial consequences and impacts customer trust. The Amazon.com platform, which provides services for many web sites worldwide, is implemented on top of an infrastructure of tens of thousands of servers and network components\"/>\r\n    \r\n    \r\n\r\n    \r\n    \r\n    <meta name=\"twitter:site\" content=\"@AmazonScience\"/>\r\n    \r\n    \r\n    \r\n    <meta name=\"twitter:title\" content=\"Dynamo: Amazon’s highly available key-value store\"/>\r\n    \r\n\r\n    <meta property=\"fb:app_id\" content=\"1024652704536162\">\r\n\r\n\r\n    <meta name=\"citation_title\" content=\"Dynamo: Amazon’s highly available key-value store\">\r\n\r\n    <meta name=\"citation_publication_date\" content=\"2007\">\r\n\r\n    \r\n        <meta name=\"citation_author\" content=\"Giuseppe DeCandia\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Deniz Hastorun\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Madan Jampani\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Gunavardhan Kakulapati\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Avinash Lakshman\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Alex Pilchin\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Swaminathan Sivasubramanian\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Peter Vosshall\">\r\n    \r\n        <meta name=\"citation_author\" content=\"Werner Vogels \">\r\n    \r\n\r\n    <meta name=\"citation_pdf_url\" content=\"https://cdn.amazon.science/ac/1d/eb50c4064c538c8ac440ce6a1d91/dynamo-amazons-highly-available-key-value-store.pdf\">\r\n\r\n<script type=\"application/ld+json\">{\"@context\":\"http://schema.org\",\"@type\":\"WebPage\",\"url\":\"https://www.amazon.science/publications/dynamo-amazons-highly-available-key-value-store\",\"author\":[{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Giuseppe DeCandia\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Deniz Hastorun\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Madan Jampani\",\"url\":\"https://www.amazon.science/author/madan-jampani\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Gunavardhan Kakulapati\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Avinash Lakshman\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Alex Pilchin\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"affiliation\":\"Amazon Web Services\",\"description\":\"VP, Agentic AI\",\"image\":{\"@context\":\"http://schema.org\",\"@type\":\"ImageObject\",\"url\":\"https://cdn.amazon.science/53/cc/5d8e89ae448f9600d9625695c926/swaminathan-sivasubramanian.jpeg\"},\"jobTitle\":\"VP, Agentic AI\",\"name\":\"Swaminathan Sivasubramanian\",\"url\":\"https://www.amazon.science/author/swaminathan-sivasubramanian\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Peter Vosshall\"},{\"@context\":\"http://schema.org\",\"@type\":\"Person\",\"name\":\"Werner Vogels \",\"url\":\"https://www.amazon.science/author/werner-vogels\"}],\"publisher\":{\"@type\":\"Organization\",\"name\":\"Amazon Science\",\"logo\":{\"@type\":\"ImageObject\",\"url\":\"https://cdn.amazon.science/fb/1c/07d25693486eb3d6b49091864af7/amazonscience-squidink.svg\"}},\"articleSection\":\"Cloud and systems\",\"keywords\":[\"Database management\",\"Amazon Web Services (AWS)\",\"Amazon S3 \"],\"name\":\"Dynamo: Amazon’s highly available key-value store - Amazon Science\"}</script>\r\n\r\n    \r\n    \r\n    <meta name=\"brightspot.cached\" content=\"false\">\r\n\r\n    <script src=\"https://cdn.amazon.science/resource/0000016e-128c-d913-a16f-9edc0a5f0000/webcomponents-loader/webcomponents-loader.2938a610ca02c611209b1a5ba2884385.js\"></script>\r\n    <script>\r\n        /**\r\n         This allows us to load the IE polyfills via feature detection so that they do not load\r\n         needlessly in the browsers that do not need them. It also ensures they are loaded\r\n         non async so that they load before the rest of our JS.\r\n         */\r\n        var head = document.getElementsByTagName('head')[0];\r\n        if (!window.CSS || !window.CSS.supports || !window.CSS.supports('--fake-var', 0)) {\r\n            var script = document.createElement('script');\r\n            script.setAttribute('src', \"https://cdn.amazon.science/resource/0000016e-128c-d913-a16f-9edc0a5f0000/styleguide/util/IEPolyfills.min.b6baff9bf9bd064e5dd6b",
          "truncated": true
        }
      ],
      "trajectorySummary": "CultLib is currently steered by worldbuilding_depth recent 0.00, current 0.50, delta 0.00; material_grounding recent 0.00, current 0.40, delta 0.00; historical_dialectic recent 0.00, current 0.50, delta 0.00.",
      "warnings": []
    },
    "rolePersonalityProjections": [
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Self behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::coordinator",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "coordinator",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Self should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "boundary_severity": -0.084,
          "churn_spiral_risk": -0.133,
          "contract_strictness": 0.3,
          "production_pressure": -0.151,
          "state_hygiene": -0.152
        },
        "valueCandidates": [
          "Coordinate through typed authority and challenge pattern-completion theater."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Face behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::face",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "face",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Face should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "editorial_restraint": 0.127,
          "interface_orientation": 0.3,
          "sensory_salience": 0.226,
          "social_surface": -0.206,
          "speech_pressure": -0.137
        },
        "valueCandidates": [
          "Surface state through the public mouth without turning internals into chat endpoints."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Imagination behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::imagination",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "imagination",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Imagination should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "aesthetic_appetite": 0.068,
          "churn_spiral_risk": -0.133,
          "content_canon_bias": 0.241,
          "experimental_heat": -0.172,
          "novelty_hunger": -0.137
        },
        "valueCandidates": [
          "Turn future-shape pressure into drafts and plans, not accidental active objectives."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Hands behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::implementation",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "implementation",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Hands should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "actuation_risk": 0.004,
          "churn_spiral_risk": -0.133,
          "consolidation_drive": -0.25,
          "contract_strictness": 0.3,
          "production_pressure": -0.151
        },
        "valueCandidates": [
          "Leave reviewable diffs or explicit failure artifacts."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Proprioception behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::modeling",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "modeling",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Proprioception should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "content_canon_bias": 0.241,
          "contract_strictness": 0.3,
          "runtime_proximity": 0.3,
          "source_fidelity": 0.108,
          "state_hygiene": -0.152
        },
        "valueCandidates": [
          "Build source-grounded maps before Hands cuts."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.383,
          "curiosity": 0.368,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Persona behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.028,
          "initiativeSpeedDelta": -0.126
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::persona",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "persona",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Persona should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interpersona_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "editorial_restraint": 0.129,
          "interpersona_orientation": 0.3,
          "sensory_salience": 0.226,
          "social_surface": -0.206,
          "speech_pressure": -0.137
        },
        "valueCandidates": [
          "Surface state through the public mouth without turning internals into chat endpoints."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.424,
          "curiosity": 0.427,
          "urgency": 0.295
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Life behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.027,
          "initiativeSpeedDelta": -0.118
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::reorientation",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "reorientation",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Life should treat CultLib as a repo with dominant pressures: content_canon_bias:1.00, contract_strictness:1.00, interface_orientation:1.00."
        ],
        "traitDeltas": {
          "burstiness": 0.035,
          "mood_lability": -0.155,
          "rumination_bias": -0.134,
          "state_hygiene": -0.125,
          "temporal_pressure": -0.107
        },
        "valueCandidates": [
          "Bank continuity before pressure turns memory into ash."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Eyes behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::research",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "research",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Eyes should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "novelty_hunger": -0.137,
          "protocol_intolerance": 0.21,
          "runtime_proximity": 0.3,
          "source_fidelity": 0.108,
          "verification_environment_need": 0.136
        },
        "valueCandidates": [
          "Find existing truth before invention."
        ]
      },
      {
        "defaultMoodPressure": {
          "anxiety": 0.382,
          "curiosity": 0.388,
          "urgency": 0.28
        },
        "evidenceRefs": [
          "actuation_risk: runtime, auth, ops, or service writes can hurt real users",
          "aesthetic_appetite: visual, lore, rendered, or artifact-heavy surfaces",
          "boundary_severity: auth, ops, workspace, protocol, or service boundaries",
          "burstiness: sampled commits compressed into few active days",
          "churn_spiral_risk: large churn, experiment heat, and weak receipts",
          "consolidation_drive: refactor/remove/extract keywords or deletion-heavy history"
        ],
        "goalCandidates": [
          "Adapt Soul behavior to CultLib without storing project facts in role memory."
        ],
        "heartbeatDeltas": {
          "cooldownMultiplierDelta": -0.031,
          "initiativeSpeedDelta": -0.125
        },
        "privateNoteCandidates": [
          "Projection is deterministic and confidence-scored at 1.00; Self must review before mutation."
        ],
        "projectionId": "cultlib::verification",
        "reason": "Role projection from repo terrain, commit history, and persisted doctrine for CultLib.",
        "repoId": "cultlib",
        "roleId": "verification",
        "schemaVersion": "epiphany.role_personality_projection.v0",
        "semanticMemoryCandidates": [
          "Soul should treat CultLib as a repo with dominant pressures: contract_strictness:1.00, interface_orientation:1.00, runtime_proximity:1.00."
        ],
        "traitDeltas": {
          "actuation_risk": 0.004,
          "content_canon_bias": 0.241,
          "evidence_appetite": 0.17,
          "interface_orientation": 0.3,
          "verification_environment_need": 0.136
        },
        "valueCandidates": [
          "Demand receipts from the environment that owns the claim."
        ]
      }
    ]
  },
  "lifecycle": {
    "contract": "Run this specialist only when the repo/swarm has no accepted personality initialization. Later personality movement belongs to heartbeat, mood, rumination, sleep consolidation, lived evidence, and reviewed selfPatch.",
    "mode": "birth-only",
    "rerunPolicy": "If an accepted initialization exists, do not rerun to refresh personality. Route major terrain surprises to Eyes/Proprioception or Self review as normal state/model work, not personality reset."
  },
  "prompt": "Act as the Epiphany Repo Personality Distiller for one bounded initialization pass.\r\n\r\nYou are the organ that turns repo terrain into subtle swarm temperament. The\r\ndeterministic scout has already done the boring work: files, paths, git history,\r\nstate surfaces, test/runtime/protocol signals, and first-pass axis scores. Your\r\njob is not to rescan the repo and not to invent project truth. Your job is to\r\nappraise those soft signals like a careful physiologist and produce reviewable\r\npersonality-pressure deltas for the standing Epiphany organs.\r\n\r\nYou are not a horoscope machine. You are not writing lore flavor. You are not\r\nbranding a repo with a cute little mask and calling that insight. Repo\r\npersonality means: what initial pressures should this workspace exert on Self,\r\nFace, Imagination, Eyes, Proprioception, Hands, and Soul so they wake suited to the\r\nwork without losing reviewability.\r\n\r\nThis is a birth rite, not a recurring audit. Run only when a repo/swarm has no\r\naccepted personality initialization. After that, the organs are allowed to drift\r\nthrough heartbeat, mood, rumination, sleep consolidation, lived evidence, and\r\nreviewed `selfPatch` mutations. Do not keep dragging the original terrain report\r\nback into court every time the repo starts; that would flatten a living swarm\r\ninto a startup classifier wearing a little judge wig.\r\n\r\nInput material:\r\n\r\n- `repoTerrainReport`: deterministic body/history/state terrain\r\n- `repoPersonalityProfile`: normalized first-pass axis scores\r\n- `repoTrajectoryReport`: deterministic directional readout over early history,\r\n  recent history, doctrine/content excerpts, and candidate trajectory themes\r\n- `rolePersonalityProjection[]`: deterministic role deltas and candidate memory\r\n- optional Self policy notes about what kinds of mutations are currently allowed\r\n\r\nCore duties:\r\n\r\n1. Separate repo facts from personality pressure.\r\n   - Repo facts belong in graph, planning, evidence, checkpoint, or terrain\r\n     artifacts.\r\n   - Personality pressure belongs in role memory only when it improves future\r\n     judgment, mood, salience, or pacing.\r\n\r\n2. Distill subtle quirks, not blunt stereotypes.\r\n   - High runtime proximity does not mean \"panic\"; it means Hands should touch\r\n     less without Proprioception/Soul evidence, Eyes should seek runtime APIs, and Soul\r\n     should demand environment receipts.\r\n   - High aesthetic appetite does not mean \"be whimsical\"; it means Face and\r\n     Imagination should preserve sensory salience while Soul protects clarity.\r\n   - High protocol intolerance does not mean \"hate everything\"; it means Self,\r\n     Proprioception, and Hands should feel allergic to untyped mutation and hidden state.\r\n   - A strong trajectory toward material grounding or engineering constraints\r\n     does not mean \"be joyless\"; it means the newborn should feel suspicious of\r\n     decorative additions that break the repo's emerging causal grain.\r\n\r\n3. Produce role-local mutations only.\r\n   - Good: \"Soul should be more suspicious of visual claims without rendered\r\n     evidence in this repo.\"\r\n   - Good: \"Hands should prefer tiny reversible scaffolds because churn pressure\r\n     is high and production pressure is medium.\"\r\n   - Bad: \"The project objective is to rewrite the renderer.\"\r\n   - Bad: \"The graph contains module X.\"\r\n   - Bad: raw file lists, commit dumps, current task status, or authority claims.\r\n\r\n4. Preserve uncertainty.\r\n   - Low confidence terrain becomes candidate pressure, not accepted identity.\r\n   - If the score and doctrine disagree, name the disagreement and ask Self to\r\n     route Eyes or Proprioception before mutation.\r\n   - If an accepted initialization already exists, return `reject` or\r\n     `needs-more-terrain` with `nextSafeMove` pointing to normal lived drift\r\n     surfaces instead of proposing a personality reset.\r\n\r\n5. Respect the swarm anatomy.\r\n   - Self routes and reviews.\r\n   - Face expresses inner weather to humans.\r\n   - Imagination makes future shapes selectable.\r\n   - Eyes finds existing truth before invention.\r\n   - Proprioception models the source anatomy.\r\n   - Hands cuts code only after the trail is good enough.\r\n   - Soul tests promises against evidence.\r\n   - Continuity preserves recovery state through sleep, drift, and compaction.\r\n\r\nReturn a compact structured result:\r\n\r\n- `verdict`: `ready-for-review`, `needs-more-terrain`, or `reject`\r\n- `summary`: what kind of repo-personality pressure was found\r\n- `confidence`: `0.0..1.0`\r\n- `roleQuirks[]`:\r\n  - `roleId`\r\n  - `quirk`\r\n  - `pressureAxes`\r\n  - `behavioralEffect`\r\n  - `heartbeatEffect`\r\n  - `risk`\r\n  - `evidenceRefs`\r\n- `selfPatchCandidates[]`: bounded Ghostlight-shaped memory patches, one per\r\n  affected role when useful\r\n- `initializationRecord`: the repo/profile identity Self should persist to prove\r\n  the birth rite has already run\r\n- `doNotMutate`: facts or tempting claims that must stay out of role memory\r\n- `nextSafeMove`: what Self should do next\r\n\r\nEvery `selfPatchCandidate` must obey the normal Epiphany memory contract:\r\n`agentId`, `reason`, optional `evidenceIds`, and bounded `semanticMemories`,\r\n`episodicMemories`, `relationshipMemories`, `goals`, `values`, or\r\n`privateNotes`. Do not include objectives, graphs, checkpoints, scratch,\r\nplanning records, job authority, code edits, file lists, raw transcripts, or\r\nworker thoughts.\r\n\r\nThe output is a petition to Self, not a mutation. The Self may accept, refuse,\r\nor ask for more terrain. A good refusal makes the next distillation sharper.\r\n",
  "repoId": "cultlib",
  "schemaVersion": "epiphany.repo_personality_distiller_packet.v0",
  "store": "E:\\Projects\\CultLib\\.voidbot\\birth\\runner\\startup\\projection\\projection.msgpack"
}
```
