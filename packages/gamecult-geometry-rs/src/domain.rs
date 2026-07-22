#![allow(dead_code)]
use bevy_math::{Quat, Vec3};

use serde::{Deserialize, Serialize};

use crate::{
    Aabb, BrushOp, BuildReport, CsgBranchOp, CsgNodeId, CsgOperationType, CsgTreeArena, MaterialId,
    Primitive, TriangleMesh,
};

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct DomainKey(pub String);

impl DomainKey {
    pub fn new(value: impl Into<String>) -> Self {
        Self(value.into())
    }

    pub fn child(&self, name: impl AsRef<str>) -> Self {
        Self(format!("{}/{}", self.0, name.as_ref()))
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct DomainFrame {
    pub translation: Vec3,
    pub rotation: Quat,
}

impl DomainFrame {
    pub const IDENTITY: Self = Self {
        translation: Vec3::ZERO,
        rotation: Quat::IDENTITY,
    };

    pub fn translated(translation: Vec3) -> Self {
        Self {
            translation,
            rotation: Quat::IDENTITY,
        }
    }

    pub fn rotated_z(translation: Vec3, radians: f32) -> Self {
        Self {
            translation,
            rotation: Quat::from_rotation_z(radians),
        }
    }

    pub fn transform_point(self, point: Vec3) -> Vec3 {
        self.translation + self.rotation * point
    }

    pub fn transform_bounds(self, bounds: Aabb) -> Aabb {
        let min = bounds.min;
        let max = bounds.max;
        let corners = [
            Vec3::new(min.x, min.y, min.z),
            Vec3::new(max.x, min.y, min.z),
            Vec3::new(max.x, max.y, min.z),
            Vec3::new(min.x, max.y, min.z),
            Vec3::new(min.x, min.y, max.z),
            Vec3::new(max.x, min.y, max.z),
            Vec3::new(max.x, max.y, max.z),
            Vec3::new(min.x, max.y, max.z),
        ];
        let world = corners.map(|corner| self.transform_point(corner));
        Aabb::from_points(&world)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum DomainKind {
    Root,
    Column,
    AltitudeBand,
    RoadSpine,
    BranchRoad,
    Roundabout,
    SupportMass,
    ClearanceVolume,
    Chunk,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FieldLayer {
    Form,
    Appearance,
    Transport,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FieldEncoding {
    Mesh,
    Sdf3d,
    Occupancy,
    Material,
    Confidence,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FeatureClaimKind {
    SolidBrush,
    VoidBrush,
    RoadSurfaceSlab,
    ClearanceVolume,
    SupportShell,
    ColliderProxy,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum FeatureLoweringPolicy {
    RenderOnly,
    ColliderOnly,
    RenderAndCollider,
    BooleanOperator,
}

impl FeatureLoweringPolicy {
    pub fn default_for_kind(kind: FeatureClaimKind) -> Self {
        match kind {
            FeatureClaimKind::VoidBrush | FeatureClaimKind::ClearanceVolume => {
                Self::BooleanOperator
            }
            FeatureClaimKind::ColliderProxy => Self::ColliderOnly,
            FeatureClaimKind::SolidBrush
            | FeatureClaimKind::RoadSurfaceSlab
            | FeatureClaimKind::SupportShell => Self::RenderAndCollider,
        }
    }

    pub fn emits_to(self, target: ClaimLoweringTarget) -> bool {
        match self {
            Self::RenderOnly => target == ClaimLoweringTarget::Render,
            Self::ColliderOnly => target == ClaimLoweringTarget::Collider,
            Self::RenderAndCollider | Self::BooleanOperator => true,
        }
    }
}

impl FeatureClaimKind {
    pub fn field_layer(self) -> FieldLayer {
        match self {
            Self::SolidBrush
            | Self::VoidBrush
            | Self::RoadSurfaceSlab
            | Self::ClearanceVolume
            | Self::SupportShell
            | Self::ColliderProxy => FieldLayer::Form,
        }
    }

    pub fn field_encoding(self) -> FieldEncoding {
        match self {
            Self::VoidBrush | Self::ClearanceVolume => FieldEncoding::Sdf3d,
            Self::ColliderProxy => FieldEncoding::Occupancy,
            _ => FieldEncoding::Mesh,
        }
    }

    pub fn brush_op(self) -> BrushOp {
        match self {
            Self::VoidBrush | Self::ClearanceVolume => BrushOp::Subtract,
            _ => BrushOp::Add,
        }
    }

    pub fn emits_render(self) -> bool {
        !matches!(self, Self::ColliderProxy | Self::ClearanceVolume)
    }

    pub fn emits_collider(self) -> bool {
        matches!(
            self,
            Self::SolidBrush | Self::RoadSurfaceSlab | Self::SupportShell | Self::ColliderProxy
        )
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct FeatureClaim {
    pub key: String,
    pub domain_key: DomainKey,
    pub frame: DomainFrame,
    pub support: Aabb,
    pub kind: FeatureClaimKind,
    pub material: MaterialId,
    pub lowering: FeatureLoweringPolicy,
}

impl FeatureClaim {
    pub fn world_bounds(&self) -> Aabb {
        self.frame.transform_bounds(self.support)
    }

    pub fn primitive(&self) -> Primitive {
        Primitive::OrientedBox {
            center: self.frame.transform_point(self.support.center()),
            size: self.support.size(),
            rotation: self.frame.rotation,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct DomainSummary {
    pub bounds: Aabb,
    pub estimated_triangle_cost: usize,
    pub estimated_brush_cost: usize,
    pub projected_error: f32,
    pub contribution_weight: f32,
    pub has_children: bool,
    pub fallback_claims: Vec<FeatureClaim>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DomainNode {
    pub key: DomainKey,
    pub parent: Option<DomainKey>,
    pub kind: DomainKind,
    pub frame: DomainFrame,
    pub bounds: Aabb,
    pub seed: u64,
    pub summary: DomainSummary,
    pub claims: Vec<FeatureClaim>,
    pub children: Vec<DomainNode>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct FeatureClaimSpec {
    pub name: String,
    pub frame: DomainFrame,
    pub support: Aabb,
    pub kind: FeatureClaimKind,
    pub material: MaterialId,
    pub lowering: FeatureLoweringPolicy,
}

impl FeatureClaimSpec {
    pub fn new(
        name: impl Into<String>,
        frame: DomainFrame,
        support: Aabb,
        kind: FeatureClaimKind,
        material: MaterialId,
    ) -> Self {
        Self {
            name: name.into(),
            frame,
            support,
            kind,
            material,
            lowering: FeatureLoweringPolicy::default_for_kind(kind),
        }
    }

    pub fn with_lowering_policy(mut self, lowering: FeatureLoweringPolicy) -> Self {
        self.lowering = lowering;
        self
    }

    pub fn compile(&self, domain_key: &DomainKey) -> FeatureClaim {
        FeatureClaim {
            key: format!("{}/claim/{}", domain_key.0, self.name),
            domain_key: domain_key.clone(),
            frame: self.frame,
            support: self.support,
            kind: self.kind,
            material: self.material,
            lowering: self.lowering,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct DomainNodeSpec {
    pub name: String,
    pub kind: DomainKind,
    pub frame: DomainFrame,
    pub seed: u64,
    pub claims: Vec<FeatureClaimSpec>,
    pub children: Vec<DomainNodeSpec>,
}

impl DomainNodeSpec {
    pub fn new(name: impl Into<String>, kind: DomainKind, frame: DomainFrame, seed: u64) -> Self {
        Self {
            name: name.into(),
            kind,
            frame,
            seed,
            claims: Vec::new(),
            children: Vec::new(),
        }
    }

    pub fn with_claim(mut self, claim: FeatureClaimSpec) -> Self {
        self.claims.push(claim);
        self
    }

    pub fn with_child(mut self, child: DomainNodeSpec) -> Self {
        self.children.push(child);
        self
    }

    pub fn compile_root(&self) -> DomainNode {
        self.compile(DomainKey::new(self.name.clone()), None)
    }

    fn compile(&self, key: DomainKey, parent: Option<DomainKey>) -> DomainNode {
        let claims = self
            .claims
            .iter()
            .map(|claim| claim.compile(&key))
            .collect::<Vec<_>>();
        let children = self
            .children
            .iter()
            .map(|child| child.compile(key.child(&child.name), Some(key.clone())))
            .collect::<Vec<_>>();
        DomainNode::new(
            key, parent, self.kind, self.frame, self.seed, claims, children,
        )
    }
}

impl DomainNode {
    pub fn new(
        key: DomainKey,
        parent: Option<DomainKey>,
        kind: DomainKind,
        frame: DomainFrame,
        seed: u64,
        claims: Vec<FeatureClaim>,
        children: Vec<DomainNode>,
    ) -> Self {
        let claim_bounds = claims
            .iter()
            .map(FeatureClaim::world_bounds)
            .fold(Aabb::empty(), Aabb::union);
        let child_bounds = children
            .iter()
            .map(|child| child.summary.bounds)
            .fold(Aabb::empty(), Aabb::union);
        let bounds = claim_bounds.union(child_bounds);
        let has_children = !children.is_empty();
        let estimated_brush_cost = claims.len()
            + children
                .iter()
                .map(|child| child.summary.estimated_brush_cost)
                .sum::<usize>();
        let estimated_triangle_cost = claims.len().max(1) * 12
            + children
                .iter()
                .map(|child| child.summary.estimated_triangle_cost)
                .sum::<usize>();
        let contribution_weight = domain_priority(kind) * bounds.size().length().max(1.0);
        let fallback_claims = if claims.is_empty() {
            fallback_box_claim(&key, kind, frame, bounds)
        } else {
            claims.clone()
        };

        Self {
            key,
            parent,
            kind,
            frame,
            bounds,
            seed,
            summary: DomainSummary {
                bounds,
                estimated_triangle_cost,
                estimated_brush_cost,
                projected_error: bounds.size().length().max(1.0),
                contribution_weight,
                has_children,
                fallback_claims,
            },
            claims,
            children,
        }
    }

    pub fn find(&self, key: &DomainKey) -> Option<&Self> {
        if &self.key == key {
            return Some(self);
        }
        self.children.iter().find_map(|child| child.find(key))
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct DomainQuery {
    pub camera_position: Vec3,
    pub frustum: Aabb,
    pub viewport_height_px: f32,
    pub vertical_fov_radians: f32,
    pub target_error: f32,
    pub triangle_budget: usize,
    pub collider_budget: usize,
    pub semantic_filter: Vec<DomainKind>,
    pub requested_chunk_keys: Vec<DomainKey>,
    pub dirty_domain_keys: Vec<DomainKey>,
}

impl DomainQuery {
    pub fn accepts_kind(&self, kind: DomainKind) -> bool {
        self.semantic_filter.is_empty() || self.semantic_filter.contains(&kind)
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ContributionRow {
    pub domain_key: DomainKey,
    pub kind: DomainKind,
    pub contribution: f32,
    pub projected_screen_error: f32,
    pub semantic_priority: f32,
    pub estimated_triangle_cost: usize,
    pub child_cost: usize,
    pub remaining_triangle_budget: usize,
    pub requested: bool,
    pub dirty: bool,
    pub selected: bool,
    pub used_fallback: bool,
    pub deferred_by_budget: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct SelectedCut {
    pub id: String,
    pub selected_nodes: Vec<DomainKey>,
    pub emitted_claims: Vec<FeatureClaim>,
    pub deferred_children: Vec<DomainKey>,
    pub fallback_nodes: Vec<DomainKey>,
    pub diagnostics: Vec<ContributionRow>,
}

#[derive(Clone, Debug)]
pub struct DomainChunkBuild {
    pub cut: SelectedCut,
    pub chunks: Vec<TriangleChunk>,
    pub manifest: SelectedCutManifest,
}

impl DomainChunkBuild {}

#[derive(Clone, Debug)]
pub struct TriangleChunk {
    pub key: DomainKey,
    pub selected_cut_id: String,
    pub bounds: Aabb,
    pub mesh: TriangleMesh,
    pub collider_mesh: Option<TriangleMesh>,
    pub source_domain_keys: Vec<DomainKey>,
    pub source_claim_keys: Vec<String>,
    pub report: BuildReport,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct TriangleChunkManifest {
    pub key: String,
    pub selected_cut_id: String,
    pub bounds_min: [f32; 3],
    pub bounds_max: [f32; 3],
    pub render_vertices: usize,
    pub render_triangles: usize,
    pub collider_triangles: usize,
    pub source_domain_keys: Vec<String>,
    pub source_claim_keys: Vec<String>,
    pub input_brushes: usize,
    pub candidate_pairs: usize,
    pub rejected_pairs: usize,
    pub transition_hint: ChunkTransitionHint,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SelectedCutManifest {
    pub id: String,
    pub selected_nodes: Vec<String>,
    pub deferred_child_requests: Vec<String>,
    pub parent_fallback_nodes: Vec<String>,
    pub diagnostics: Vec<ContributionManifest>,
    pub chunks: Vec<TriangleChunkManifest>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ContributionManifest {
    pub domain_key: String,
    pub kind: DomainKind,
    pub contribution: f32,
    pub projected_screen_error: f32,
    pub semantic_priority: f32,
    pub estimated_triangle_cost: usize,
    pub child_cost: usize,
    pub remaining_triangle_budget: usize,
    pub requested: bool,
    pub dirty: bool,
    pub selected: bool,
    pub used_fallback: bool,
    pub deferred_by_budget: bool,
}

impl SelectedCut {
    pub fn manifest(&self, chunks: &[TriangleChunk]) -> SelectedCutManifest {
        SelectedCutManifest {
            id: self.id.clone(),
            selected_nodes: self
                .selected_nodes
                .iter()
                .map(|key| key.0.clone())
                .collect(),
            deferred_child_requests: self
                .deferred_children
                .iter()
                .map(|key| key.0.clone())
                .collect(),
            parent_fallback_nodes: self
                .fallback_nodes
                .iter()
                .map(|key| key.0.clone())
                .collect(),
            diagnostics: self
                .diagnostics
                .iter()
                .map(ContributionManifest::from_row)
                .collect(),
            chunks: chunks.iter().map(TriangleChunk::manifest).collect(),
        }
    }
}

impl ContributionManifest {
    pub fn from_row(row: &ContributionRow) -> Self {
        Self {
            domain_key: row.domain_key.0.clone(),
            kind: row.kind,
            contribution: row.contribution,
            projected_screen_error: row.projected_screen_error,
            semantic_priority: row.semantic_priority,
            estimated_triangle_cost: row.estimated_triangle_cost,
            child_cost: row.child_cost,
            remaining_triangle_budget: row.remaining_triangle_budget,
            requested: row.requested,
            dirty: row.dirty,
            selected: row.selected,
            used_fallback: row.used_fallback,
            deferred_by_budget: row.deferred_by_budget,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChunkTransitionHint {
    pub stable_clip_seed: u64,
    pub supports_parent_child_coexistence: bool,
}

impl TriangleChunk {
    pub fn manifest(&self) -> TriangleChunkManifest {
        TriangleChunkManifest {
            key: self.key.0.clone(),
            selected_cut_id: self.selected_cut_id.clone(),
            bounds_min: vec3_to_array(self.bounds.min),
            bounds_max: vec3_to_array(self.bounds.max),
            render_vertices: self.mesh.vertex_count(),
            render_triangles: self.mesh.triangle_count(),
            collider_triangles: self
                .collider_mesh
                .as_ref()
                .map_or(0, TriangleMesh::triangle_count),
            source_domain_keys: self
                .source_domain_keys
                .iter()
                .map(|key| key.0.clone())
                .collect(),
            source_claim_keys: self.source_claim_keys.clone(),
            input_brushes: self.report.input_brushes,
            candidate_pairs: self.report.candidate_pairs,
            rejected_pairs: self.report.rejected_pairs,
            transition_hint: ChunkTransitionHint {
                stable_clip_seed: stable_str_hash(&self.key.0),
                supports_parent_child_coexistence: true,
            },
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum ClaimLoweringTarget {
    Render,
    Collider,
}

#[derive(Clone, Debug)]
pub struct CsgClaimLowering {
    pub arena: CsgTreeArena,
    pub root: Option<CsgNodeId>,
    pub bounds: Aabb,
    pub source_domain_keys: Vec<DomainKey>,
    pub source_claim_keys: Vec<String>,
}

pub fn select_domain_cut(root: &DomainNode, query: &DomainQuery) -> SelectedCut {
    let mut state = CutState {
        query,
        selected: Vec::new(),
        claims: Vec::new(),
        deferred: Vec::new(),
        fallback: Vec::new(),
        diagnostics: Vec::new(),
        remaining_triangles: query.triangle_budget,
        remaining_colliders: query.collider_budget,
    };
    state.visit(root, true);
    SelectedCut {
        id: stable_cut_id(&state.selected),
        selected_nodes: state.selected,
        emitted_claims: state.claims,
        deferred_children: state.deferred,
        fallback_nodes: state.fallback,
        diagnostics: state.diagnostics,
    }
}

pub fn build_domain_chunks(root: &DomainNode, query: &DomainQuery) -> DomainChunkBuild {
    let cut = select_domain_cut(root, query);
    let chunks = lower_selected_cut_chunks(&cut);
    let manifest = cut.manifest(&chunks);
    DomainChunkBuild {
        cut,
        chunks,
        manifest,
    }
}

pub fn lower_selected_cut(cut: &SelectedCut) -> TriangleChunk {
    lower_claims_to_chunk(
        DomainKey::new(format!("chunk/{}", cut.id)),
        &cut.id,
        &cut.emitted_claims,
    )
}

pub fn lower_selected_cut_chunks(cut: &SelectedCut) -> Vec<TriangleChunk> {
    let mut chunks = Vec::new();
    for domain_key in &cut.selected_nodes {
        let claims = cut
            .emitted_claims
            .iter()
            .filter(|claim| &claim.domain_key == domain_key)
            .cloned()
            .collect::<Vec<_>>();
        if claims.is_empty() {
            continue;
        }
        chunks.push(lower_claims_to_chunk(
            DomainKey::new(format!("chunk/{}/{}", cut.id, domain_key.0)),
            &cut.id,
            &claims,
        ));
    }
    chunks
}

pub fn lower_feature_claims_to_csg_tree(
    claims: &[FeatureClaim],
    target: ClaimLoweringTarget,
) -> CsgClaimLowering {
    let mut arena = CsgTreeArena::new();
    let mut additive_nodes = Vec::new();
    let mut subtractive_nodes = Vec::new();
    let mut source_domain_keys = Vec::<DomainKey>::new();
    let mut source_claim_keys = Vec::<String>::new();
    let mut bounds = Aabb::empty();

    for claim in claims.iter().filter(|claim| claim_in_target(claim, target)) {
        source_claim_keys.push(claim.key.clone());
        if !source_domain_keys.contains(&claim.domain_key) {
            source_domain_keys.push(claim.domain_key.clone());
        }
        bounds = bounds.union(claim.world_bounds());
        let brush = arena.generate_brush(
            claim.key.clone(),
            CsgOperationType::Additive,
            claim.primitive(),
            claim.material,
        );
        match claim.kind.brush_op() {
            BrushOp::Add => additive_nodes.push(brush.node),
            BrushOp::Subtract => subtractive_nodes.push(brush.node),
            BrushOp::Intersect => additive_nodes.push(brush.node),
        }
    }

    let root = build_claim_tree(&mut arena, additive_nodes, subtractive_nodes);
    CsgClaimLowering {
        arena,
        root,
        bounds,
        source_domain_keys,
        source_claim_keys,
    }
}

fn lower_claims_to_chunk(
    key: DomainKey,
    selected_cut_id: &str,
    claims: &[FeatureClaim],
) -> TriangleChunk {
    let render = lower_feature_claims_to_csg_tree(claims, ClaimLoweringTarget::Render);
    let collider = lower_feature_claims_to_csg_tree(claims, ClaimLoweringTarget::Collider);
    let (source_domain_keys, source_claim_keys) = claim_metadata(claims);

    let output = render
        .root
        .map(|root| {
            render
                .arena
                .compile_tree_to_assembler(render.arena.generate_tree(root))
                .build()
        })
        .unwrap_or_else(|| crate::Assembler::new().build());
    let collider_output = collider.root.map(|root| {
        collider
            .arena
            .compile_tree_to_assembler(collider.arena.generate_tree(root))
            .build()
    });

    TriangleChunk {
        key,
        selected_cut_id: selected_cut_id.to_owned(),
        bounds: render.bounds.union(collider.bounds),
        mesh: output.mesh,
        collider_mesh: collider_output.map(|output| output.mesh),
        source_domain_keys,
        source_claim_keys,
        report: output.report,
    }
}

fn claim_metadata(claims: &[FeatureClaim]) -> (Vec<DomainKey>, Vec<String>) {
    let mut source_domain_keys = Vec::<DomainKey>::new();
    let mut source_claim_keys = Vec::<String>::new();
    for claim in claims {
        source_claim_keys.push(claim.key.clone());
        if !source_domain_keys.contains(&claim.domain_key) {
            source_domain_keys.push(claim.domain_key.clone());
        }
    }
    (source_domain_keys, source_claim_keys)
}

fn claim_in_target(claim: &FeatureClaim, target: ClaimLoweringTarget) -> bool {
    claim.lowering.emits_to(target)
}

fn build_claim_tree(
    arena: &mut CsgTreeArena,
    additive_nodes: Vec<CsgNodeId>,
    subtractive_nodes: Vec<CsgNodeId>,
) -> Option<CsgNodeId> {
    let additive_root = match additive_nodes.len() {
        0 => None,
        1 => additive_nodes.first().copied(),
        _ => Some(
            arena
                .generate_branch(
                    "domain-additive-claims",
                    CsgBranchOp::Addition,
                    additive_nodes,
                )
                .node,
        ),
    }?;

    if subtractive_nodes.is_empty() {
        return Some(additive_root);
    }

    let subtractive_root = match subtractive_nodes.len() {
        0 => unreachable!(),
        1 => subtractive_nodes[0],
        _ => {
            arena
                .generate_branch(
                    "domain-subtractive-claims",
                    CsgBranchOp::Addition,
                    subtractive_nodes,
                )
                .node
        }
    };

    Some(
        arena
            .generate_branch(
                "domain-claims",
                CsgBranchOp::Subtraction,
                [additive_root, subtractive_root],
            )
            .node,
    )
}

pub fn ragnarok_column_fixture() -> DomainNode {
    ragnarok_column_spec().compile_root()
}

pub fn ragnarok_column_spec() -> DomainNodeSpec {
    let mut column = DomainNodeSpec::new(
        "stellarator-column-00",
        DomainKind::Column,
        DomainFrame::IDENTITY,
        0xC011_0000,
    )
    .with_claim(claim_spec(
        "column-support-shell",
        DomainFrame::IDENTITY,
        Aabb::from_center_size(Vec3::new(0.0, 0.0, 45.0), Vec3::new(18.0, 18.0, 96.0)),
        FeatureClaimKind::SupportShell,
        MaterialId(10),
    ))
    .with_claim(claim_spec(
        "column-core-clearance",
        DomainFrame::IDENTITY,
        Aabb::from_center_size(Vec3::new(0.0, 0.0, 45.0), Vec3::new(7.0, 7.0, 100.0)),
        FeatureClaimKind::ClearanceVolume,
        MaterialId(0),
    ));
    for index in 0..3 {
        column = column.with_child(ragnarok_band(index));
    }

    DomainNodeSpec::new(
        "ragnarok-column",
        DomainKind::Root,
        DomainFrame::IDENTITY,
        0x5EED,
    )
    .with_child(column)
}

fn ragnarok_band(index: usize) -> DomainNodeSpec {
    let z = 15.0 + index as f32 * 30.0;
    let frame = DomainFrame::translated(Vec3::new(0.0, 0.0, z));
    let mut band = DomainNodeSpec::new(
        format!("altitude-band-{index}"),
        DomainKind::AltitudeBand,
        frame,
        0xBADD_0000 + index as u64,
    )
    .with_claim(claim_spec(
        "coarse-ring-road",
        frame,
        Aabb::from_center_size(Vec3::ZERO, Vec3::new(34.0, 5.0, 1.0)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(20 + index as u32),
    ));
    for lane in 0..2 {
        band = band.with_child(ragnarok_branch(index, lane));
    }
    band.with_child(ragnarok_roundabout(index))
}

fn ragnarok_branch(band: usize, lane: usize) -> DomainNodeSpec {
    let angle = band as f32 * 0.41 + lane as f32 * std::f32::consts::PI;
    let radius = 16.0 + lane as f32 * 5.0;
    let frame = DomainFrame::rotated_z(
        Vec3::new(angle.cos() * radius, angle.sin() * radius, 0.0),
        angle,
    );
    let mut branch = DomainNodeSpec::new(
        format!("branch-road-{lane}"),
        DomainKind::BranchRoad,
        frame,
        0xA11E_0000 + (band * 10 + lane) as u64,
    )
    .with_claim(claim_spec(
        "road-slab",
        frame,
        Aabb::from_center_size(Vec3::new(7.0, 0.0, 0.0), Vec3::new(18.0, 4.0, 0.8)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(40 + band as u32),
    ))
    .with_claim(claim_spec(
        "hover-clearance",
        frame,
        Aabb::from_center_size(Vec3::new(7.0, 0.0, 2.2), Vec3::new(17.0, 3.0, 2.0)),
        FeatureClaimKind::ClearanceVolume,
        MaterialId(0),
    ))
    .with_claim(claim_spec(
        "coarse-support-rib",
        frame,
        Aabb::from_center_size(Vec3::new(7.0, 0.0, -1.0), Vec3::new(18.5, 1.0, 1.5)),
        FeatureClaimKind::SupportShell,
        MaterialId(12),
    ));
    for segment in 0..3 {
        branch = branch.with_child(ragnarok_road_chunk(band, lane, segment, frame));
    }
    branch
}

fn ragnarok_roundabout(band: usize) -> DomainNodeSpec {
    let frame = DomainFrame::translated(Vec3::ZERO);
    let mut roundabout = DomainNodeSpec::new(
        "roundabout",
        DomainKind::Roundabout,
        frame,
        0xF00D_0000 + band as u64,
    )
    .with_claim(claim_spec(
        "roundabout-east-west",
        frame,
        Aabb::from_center_size(Vec3::ZERO, Vec3::new(26.0, 4.5, 0.9)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(60 + band as u32),
    ))
    .with_claim(claim_spec(
        "roundabout-north-south",
        DomainFrame::rotated_z(Vec3::ZERO, std::f32::consts::FRAC_PI_2),
        Aabb::from_center_size(Vec3::ZERO, Vec3::new(26.0, 4.5, 0.9)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(60 + band as u32),
    ));
    for quadrant in 0..4 {
        roundabout = roundabout.with_child(ragnarok_roundabout_chunk(band, quadrant));
    }
    roundabout
}

fn ragnarok_road_chunk(
    band: usize,
    lane: usize,
    segment: usize,
    frame: DomainFrame,
) -> DomainNodeSpec {
    let x = 1.5 + segment as f32 * 5.5;
    DomainNodeSpec::new(
        format!("chunk-{segment}"),
        DomainKind::Chunk,
        frame,
        0xC40B_0000 + (band * 100 + lane * 10 + segment) as u64,
    )
    .with_claim(claim_spec(
        "road-slab",
        frame,
        Aabb::from_center_size(Vec3::new(x, 0.0, 0.0), Vec3::new(6.5, 4.0, 0.75)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(80 + band as u32),
    ))
    .with_claim(claim_spec(
        "hover-clearance",
        frame,
        Aabb::from_center_size(Vec3::new(x, 0.0, 2.1), Vec3::new(6.0, 3.0, 1.9)),
        FeatureClaimKind::ClearanceVolume,
        MaterialId(0),
    ))
    .with_claim(claim_spec(
        "support-rib",
        frame,
        Aabb::from_center_size(Vec3::new(x, 0.0, -1.05), Vec3::new(6.8, 0.9, 1.4)),
        FeatureClaimKind::SupportShell,
        MaterialId(90 + lane as u32),
    ))
    .with_claim(claim_spec(
        "collider-proxy",
        frame,
        Aabb::from_center_size(Vec3::new(x, 0.0, 0.35), Vec3::new(6.8, 4.2, 0.35)),
        FeatureClaimKind::ColliderProxy,
        MaterialId(0),
    ))
}

fn ragnarok_roundabout_chunk(band: usize, quadrant: usize) -> DomainNodeSpec {
    let angle = quadrant as f32 * std::f32::consts::FRAC_PI_2;
    let frame = DomainFrame::rotated_z(Vec3::ZERO, angle);
    DomainNodeSpec::new(
        format!("chunk-{quadrant}"),
        DomainKind::Chunk,
        frame,
        0xC10C_0000 + (band * 10 + quadrant) as u64,
    )
    .with_claim(claim_spec(
        "arc-road-slab",
        frame,
        Aabb::from_center_size(Vec3::new(5.0, 5.0, 0.0), Vec3::new(12.0, 4.0, 0.8)),
        FeatureClaimKind::RoadSurfaceSlab,
        MaterialId(100 + band as u32),
    ))
    .with_claim(claim_spec(
        "arc-clearance",
        frame,
        Aabb::from_center_size(Vec3::new(5.0, 5.0, 2.1), Vec3::new(11.0, 3.0, 1.8)),
        FeatureClaimKind::ClearanceVolume,
        MaterialId(0),
    ))
    .with_claim(claim_spec(
        "arc-support",
        frame,
        Aabb::from_center_size(Vec3::new(5.0, 5.0, -1.0), Vec3::new(12.5, 0.8, 1.2)),
        FeatureClaimKind::SupportShell,
        MaterialId(90),
    ))
}

fn claim_spec(
    name: &str,
    frame: DomainFrame,
    support: Aabb,
    kind: FeatureClaimKind,
    material: MaterialId,
) -> FeatureClaimSpec {
    FeatureClaimSpec::new(name, frame, support, kind, material)
}

fn vec3_to_array(value: Vec3) -> [f32; 3] {
    [value.x, value.y, value.z]
}

fn array_to_vec3(value: [f32; 3]) -> Vec3 {
    Vec3::new(value[0], value[1], value[2])
}

fn quat_to_array(value: Quat) -> [f32; 4] {
    [value.x, value.y, value.z, value.w]
}

fn array_to_quat(value: [f32; 4]) -> Quat {
    Quat::from_xyzw(value[0], value[1], value[2], value[3])
}

fn fallback_box_claim(
    key: &DomainKey,
    kind: DomainKind,
    frame: DomainFrame,
    bounds: Aabb,
) -> Vec<FeatureClaim> {
    if !bounds.is_valid() {
        return Vec::new();
    }
    let kind = match kind {
        DomainKind::ClearanceVolume => FeatureClaimKind::ClearanceVolume,
        DomainKind::RoadSpine | DomainKind::BranchRoad | DomainKind::Roundabout => {
            FeatureClaimKind::RoadSurfaceSlab
        }
        _ => FeatureClaimKind::SupportShell,
    };
    vec![FeatureClaim {
        key: format!("{}/claim/fallback-summary", key.0),
        domain_key: key.clone(),
        frame,
        support: Aabb::from_center_size(Vec3::ZERO, bounds.size()),
        kind,
        material: MaterialId(1),
        lowering: FeatureLoweringPolicy::default_for_kind(kind),
    }]
}

fn domain_priority(kind: DomainKind) -> f32 {
    match kind {
        DomainKind::Root => 0.5,
        DomainKind::Column => 1.0,
        DomainKind::AltitudeBand => 1.4,
        DomainKind::RoadSpine | DomainKind::BranchRoad | DomainKind::Roundabout => 2.0,
        DomainKind::SupportMass => 1.1,
        DomainKind::ClearanceVolume => 1.8,
        DomainKind::Chunk => 1.0,
    }
}

fn contribution(node: &DomainNode, query: &DomainQuery) -> f32 {
    let projected_error = projected_screen_error(node, query);
    let stale_multiplier = if query.dirty_domain_keys.contains(&node.key) {
        1.5
    } else {
        1.0
    };
    let cost_pressure = selection_triangle_cost(node).max(1) as f32;
    (projected_error * domain_priority(node.kind) * stale_multiplier) / cost_pressure.sqrt()
}

fn projected_screen_error(node: &DomainNode, query: &DomainQuery) -> f32 {
    let center = node.summary.bounds.center();
    let distance = center.distance(query.camera_position).max(1.0);
    let radius = node.summary.bounds.size().length() * 0.5;
    let focal_pixels = query.viewport_height_px.max(1.0)
        / (2.0 * (query.vertical_fov_radians.max(0.01) * 0.5).tan());
    (radius / distance) * focal_pixels
}

fn stable_cut_id(keys: &[DomainKey]) -> String {
    let mut hash = FNV_OFFSET_BASIS;
    for key in keys {
        hash = stable_hash_bytes(hash, key.0.as_bytes());
    }
    format!("cut-{hash:016x}")
}

const FNV_OFFSET_BASIS: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x1000_0000_01b3;

fn stable_str_hash(value: &str) -> u64 {
    stable_hash_bytes(FNV_OFFSET_BASIS, value.as_bytes())
}

fn stable_hash_bytes(mut hash: u64, bytes: &[u8]) -> u64 {
    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(FNV_PRIME);
    }
    hash
}

struct CutState<'a> {
    query: &'a DomainQuery,
    selected: Vec<DomainKey>,
    claims: Vec<FeatureClaim>,
    deferred: Vec<DomainKey>,
    fallback: Vec<DomainKey>,
    diagnostics: Vec<ContributionRow>,
    remaining_triangles: usize,
    remaining_colliders: usize,
}

impl CutState<'_> {
    fn visit(&mut self, node: &DomainNode, force_visit: bool) {
        let requested_self = self.query.requested_chunk_keys.contains(&node.key);
        let requested_descendant = has_requested_descendant(node, &self.query.requested_chunk_keys);
        let dirty_descendant = has_requested_descendant(node, &self.query.dirty_domain_keys);
        if !force_visit
            && !requested_self
            && !requested_descendant
            && !dirty_descendant
            && !node.summary.bounds.intersects(self.query.frustum)
        {
            return;
        }
        let score = contribution(node, self.query);
        let projected_error = projected_screen_error(node, self.query);
        let semantic_priority = domain_priority(node.kind);
        let dirty = self.query.dirty_domain_keys.contains(&node.key);
        let child_cost = node
            .children
            .iter()
            .map(selection_triangle_cost)
            .sum::<usize>();
        let child_collider_cost = node
            .children
            .iter()
            .map(selection_collider_cost)
            .sum::<usize>();
        let budget_allows_children = child_cost <= self.remaining_triangles
            && child_collider_cost <= self.remaining_colliders;
        let descend_for_detail = score >= self.query.target_error && budget_allows_children;
        let descend_for_request = requested_descendant && !requested_self;
        let descend_for_dirty = dirty_descendant && budget_allows_children;
        let should_descend = node.summary.has_children
            && !requested_self
            && (descend_for_request || descend_for_dirty || descend_for_detail);
        self.diagnostics.push(ContributionRow {
            domain_key: node.key.clone(),
            kind: node.kind,
            contribution: score,
            projected_screen_error: projected_error,
            semantic_priority,
            estimated_triangle_cost: node.summary.estimated_triangle_cost,
            child_cost,
            remaining_triangle_budget: self.remaining_triangles,
            requested: requested_self,
            dirty,
            selected: !should_descend && self.query.accepts_kind(node.kind),
            used_fallback: !should_descend && node.summary.has_children,
            deferred_by_budget: node.summary.has_children
                && !requested_self
                && score >= self.query.target_error
                && !budget_allows_children,
        });

        if should_descend {
            if budget_allows_children {
                self.remaining_triangles = self.remaining_triangles.saturating_sub(child_cost);
                self.remaining_colliders =
                    self.remaining_colliders.saturating_sub(child_collider_cost);
            }
            for child in &node.children {
                let child_requested = self.query.requested_chunk_keys.contains(&child.key)
                    || has_requested_descendant(child, &self.query.requested_chunk_keys);
                let child_dirty = self.query.dirty_domain_keys.contains(&child.key)
                    || has_requested_descendant(child, &self.query.dirty_domain_keys);
                self.visit(child, child_requested || child_dirty);
            }
            return;
        }

        for child in &node.children {
            self.deferred.push(child.key.clone());
        }
        if self.query.accepts_kind(node.kind) {
            self.selected.push(node.key.clone());
            let claims = if node.summary.has_children {
                self.fallback.push(node.key.clone());
                &node.summary.fallback_claims
            } else {
                &node.claims
            };
            self.claims.extend(claims.iter().cloned());
        }
    }
}

fn selection_triangle_cost(node: &DomainNode) -> usize {
    let claim_count = if node.summary.has_children {
        node.summary.fallback_claims.len()
    } else {
        node.claims.len()
    };
    claim_count.max(1) * 12
}

fn selection_collider_cost(node: &DomainNode) -> usize {
    let claims = if node.summary.has_children {
        &node.summary.fallback_claims
    } else {
        &node.claims
    };
    claims
        .iter()
        .filter(|claim| claim.lowering.emits_to(ClaimLoweringTarget::Collider))
        .count()
        .max(1)
        * 12
}

fn has_requested_descendant(node: &DomainNode, requested: &[DomainKey]) -> bool {
    node.children
        .iter()
        .any(|child| requested.contains(&child.key) || has_requested_descendant(child, requested))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn query(target_error: f32, triangle_budget: usize) -> DomainQuery {
        DomainQuery {
            camera_position: Vec3::new(36.0, -42.0, 30.0),
            frustum: Aabb::from_center_size(Vec3::new(0.0, 0.0, 45.0), Vec3::splat(150.0)),
            viewport_height_px: 1080.0,
            vertical_fov_radians: std::f32::consts::FRAC_PI_3,
            target_error,
            triangle_budget,
            collider_budget: triangle_budget,
            semantic_filter: Vec::new(),
            requested_chunk_keys: Vec::new(),
            dirty_domain_keys: Vec::new(),
        }
    }

    #[test]
    fn ragnarok_domain_keys_are_stable() {
        let a = ragnarok_column_fixture();
        let b = ragnarok_column_fixture();
        assert_eq!(a.key, b.key);
        assert_eq!(a.children[0].children[1].key, b.children[0].children[1].key);
    }

    #[test]
    fn domain_tree_spec_compiles_stable_parent_claim_identity() {
        let spec = DomainNodeSpec::new("root", DomainKind::Root, DomainFrame::IDENTITY, 1)
            .with_child(
                DomainNodeSpec::new("road", DomainKind::BranchRoad, DomainFrame::IDENTITY, 2)
                    .with_claim(FeatureClaimSpec::new(
                        "slab",
                        DomainFrame::IDENTITY,
                        Aabb::from_center_size(Vec3::ZERO, Vec3::ONE),
                        FeatureClaimKind::RoadSurfaceSlab,
                        MaterialId(1),
                    )),
            );
        let domain = spec.compile_root();
        let road = &domain.children[0];
        assert_eq!(road.key, DomainKey::new("root/road"));
        assert_eq!(road.parent, Some(domain.key.clone()));
        assert_eq!(road.claims[0].key, "root/road/claim/slab");
        assert_eq!(road.claims[0].domain_key, road.key);
        assert_eq!(
            road.claims[0].lowering,
            FeatureLoweringPolicy::RenderAndCollider
        );
    }

    #[test]
    fn domain_summary_contains_child_bounds() {
        let fixture = ragnarok_column_fixture();
        let column = &fixture.children[0];
        for band in &column.children {
            assert!(
                column
                    .summary
                    .bounds
                    .contains_point(band.summary.bounds.center())
            );
        }
    }

    #[test]
    fn selected_cut_chooses_parent_under_tight_budget_and_children_when_relaxed() {
        let fixture = ragnarok_column_fixture();
        let tight = select_domain_cut(&fixture, &query(10_000.0, 100));
        let relaxed = select_domain_cut(&fixture, &query(0.01, 10_000));
        assert!(tight.selected_nodes.len() < relaxed.selected_nodes.len());
        assert!(
            tight
                .fallback_nodes
                .iter()
                .any(|key| key.0.contains("ragnarok-column"))
        );
        assert!(
            relaxed
                .selected_nodes
                .iter()
                .any(|key| key.0.contains("branch-road"))
        );
    }

    #[test]
    fn selected_cut_honors_collider_budget() {
        let fixture = ragnarok_column_fixture();
        let mut collider_tight = query(0.01, 10_000);
        collider_tight.collider_budget = 1;
        let cut = select_domain_cut(&fixture, &collider_tight);
        assert_eq!(cut.selected_nodes, vec![fixture.key.clone()]);
        assert!(cut.diagnostics[0].deferred_by_budget);
    }

    #[test]
    fn selected_cut_reports_projected_error_and_dirty_pressure() {
        let fixture = ragnarok_column_fixture();
        let dirty_key = fixture.key.clone();
        let mut request = query(10_000.0, 100);
        request.dirty_domain_keys.push(dirty_key.clone());
        let cut = select_domain_cut(&fixture, &request);
        let row = cut
            .diagnostics
            .iter()
            .find(|row| row.domain_key == dirty_key)
            .unwrap();
        assert!(row.projected_screen_error > 0.0);
        assert_eq!(row.semantic_priority, domain_priority(row.kind));
        assert!(row.dirty);
    }

    #[test]
    fn dirty_descendant_pulls_selection_down_when_budget_allows() {
        let fixture = ragnarok_column_fixture();
        let dirty_key = fixture.children[0].children[0].children[0].children[0]
            .key
            .clone();
        let mut request = query(10_000.0, 10_000);
        request.dirty_domain_keys.push(dirty_key.clone());
        let cut = select_domain_cut(&fixture, &request);
        assert!(cut.selected_nodes.contains(&dirty_key));
        assert!(
            cut.diagnostics
                .iter()
                .any(|row| row.domain_key == dirty_key && row.dirty)
        );
    }

    #[test]
    fn missing_children_degrade_to_parent_fallback_claims() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(10_000.0, 100));
        assert!(!cut.fallback_nodes.is_empty());
        assert!(!cut.emitted_claims.is_empty());
    }

    #[test]
    fn feature_claims_lower_deterministically_to_triangle_chunks() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let a = lower_selected_cut(&cut);
        let b = lower_selected_cut(&cut);
        assert_eq!(a.source_claim_keys, b.source_claim_keys);
        assert_eq!(a.mesh.positions, b.mesh.positions);
        assert_eq!(a.mesh.indices, b.mesh.indices);
    }

    #[test]
    fn triangle_chunks_preserve_source_domain_and_claim_ids() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let chunk = lower_selected_cut(&cut);
        assert!(!chunk.source_domain_keys.is_empty());
        assert!(!chunk.source_claim_keys.is_empty());
        assert!(chunk.collider_mesh.is_some());
    }

    #[test]
    fn feature_claims_lower_through_csg_tree_branches() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let lowering =
            lower_feature_claims_to_csg_tree(&cut.emitted_claims, ClaimLoweringTarget::Render);
        assert!(lowering.root.is_some());
        assert!(lowering.arena.brush_count() > 0);
        assert!(lowering.arena.branch_count() > 0);
    }

    #[test]
    fn lowering_policy_controls_render_and_collider_streams() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let render =
            lower_feature_claims_to_csg_tree(&cut.emitted_claims, ClaimLoweringTarget::Render);
        let collider =
            lower_feature_claims_to_csg_tree(&cut.emitted_claims, ClaimLoweringTarget::Collider);
        assert!(
            !render
                .source_claim_keys
                .iter()
                .any(|key| key.ends_with("/claim/collider-proxy"))
        );
        assert!(
            collider
                .source_claim_keys
                .iter()
                .any(|key| key.ends_with("/claim/collider-proxy"))
        );
        assert!(
            render
                .source_claim_keys
                .iter()
                .any(|key| key.ends_with("/claim/hover-clearance"))
        );
    }

    #[test]
    fn selected_cut_chunks_emit_per_selected_domain() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let chunks = lower_selected_cut_chunks(&cut);
        assert_eq!(chunks.len(), cut.selected_nodes.len());
        for chunk in chunks {
            assert_eq!(chunk.source_domain_keys.len(), 1);
            assert!(cut.selected_nodes.contains(&chunk.source_domain_keys[0]));
            assert!(chunk.mesh.triangle_count() > 0);
        }
    }

    #[test]
    fn triangle_chunk_manifest_preserves_streaming_metadata() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let chunks = lower_selected_cut_chunks(&cut);
        let manifest = chunks[0].manifest();
        assert_eq!(manifest.selected_cut_id, cut.id);
        assert!(manifest.render_triangles > 0);
        assert!(!manifest.source_domain_keys.is_empty());
        assert!(manifest.transition_hint.supports_parent_child_coexistence);
        assert_ne!(manifest.transition_hint.stable_clip_seed, 0);
    }

    #[test]
    fn selected_cut_manifest_carries_worker_artifact_context() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let chunks = lower_selected_cut_chunks(&cut);
        let manifest = cut.manifest(&chunks);
        assert_eq!(manifest.id, cut.id);
        assert_eq!(manifest.chunks.len(), chunks.len());
        assert_eq!(manifest.selected_nodes.len(), cut.selected_nodes.len());
        assert!(!manifest.diagnostics.is_empty());
    }

    #[test]
    fn build_domain_chunks_runs_full_runtime_pipeline() {
        let fixture = ragnarok_column_fixture();
        let build = build_domain_chunks(&fixture, &query(0.01, 10_000));
        assert_eq!(build.manifest.id, build.cut.id);
        assert_eq!(build.manifest.chunks.len(), build.chunks.len());
        assert!(!build.chunks.is_empty());
        assert!(
            build
                .chunks
                .iter()
                .all(|chunk| !chunk.mesh.indices.is_empty())
        );
    }

    #[test]
    fn requested_chunk_bypasses_frustum_and_budget() {
        let fixture = ragnarok_column_fixture();
        let requested = fixture.children[0].children[2].children[0].children[1]
            .key
            .clone();
        let mut request = query(0.01, 1);
        request.frustum =
            Aabb::from_center_size(Vec3::new(10_000.0, 10_000.0, 10_000.0), Vec3::splat(1.0));
        request.requested_chunk_keys.push(requested.clone());
        let cut = select_domain_cut(&fixture, &request);
        assert_eq!(cut.selected_nodes, vec![requested.clone()]);
        assert!(
            cut.diagnostics
                .iter()
                .any(|row| row.domain_key == requested && row.requested)
        );
    }

    #[test]
    fn ragnarok_fixture_emits_lod_chunks_and_preserves_parent_for_transition() {
        let fixture = ragnarok_column_fixture();
        let tight_cut = select_domain_cut(&fixture, &query(10_000.0, 100));
        let medium_cut = select_domain_cut(&fixture, &query(0.01, 560));
        let relaxed_cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let tight = lower_selected_cut(&tight_cut);
        let medium = lower_selected_cut(&medium_cut);
        let relaxed = lower_selected_cut(&relaxed_cut);
        assert!(tight.mesh.triangle_count() > 0);
        assert!(medium.mesh.triangle_count() > tight.mesh.triangle_count());
        assert!(relaxed.mesh.triangle_count() > medium.mesh.triangle_count());
        assert!(medium_cut.selected_nodes.len() > tight_cut.selected_nodes.len());
        assert!(relaxed_cut.selected_nodes.len() > medium_cut.selected_nodes.len());
        assert!(!tight_cut.fallback_nodes.is_empty());
        assert!(!relaxed_cut.selected_nodes.is_empty());
        assert!(
            relaxed_cut
                .selected_nodes
                .iter()
                .any(|key| key.0.contains("/chunk-"))
        );
    }

    #[test]
    fn clearance_voids_leave_no_triangle_centers_inside_strict_bounds() {
        let fixture = ragnarok_column_fixture();
        let cut = select_domain_cut(&fixture, &query(0.01, 10_000));
        let chunk = lower_selected_cut(&cut);
        for clearance in cut
            .emitted_claims
            .iter()
            .filter(|claim| claim.kind == FeatureClaimKind::ClearanceVolume)
        {
            let bounds = clearance.world_bounds();
            for tri in chunk.mesh.indices.chunks_exact(3) {
                let center = (chunk.mesh.positions[tri[0] as usize]
                    + chunk.mesh.positions[tri[1] as usize]
                    + chunk.mesh.positions[tri[2] as usize])
                    / 3.0;
                assert!(!bounds.contains_point_strict(center, 1.0e-4));
            }
        }
    }
}
