#![allow(dead_code)]
use std::cell::RefCell;

use bevy_math::Vec3;

use crate::{
    Aabb, Brush, BrushId, BrushOp, ConvexPolygon, ConvexSolid, DirtyDemandFrontier, MaterialId,
    PolygonRouteScratch, Primitive, TriangleMesh, append_cylinder_z, append_dome_cap_z,
    append_floret_arm,
    primitives::{DomeCapZSpec, FloretArmSpec},
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum BuildWarning {
    SubtractIgnoredForNonBox { brush: String },
    IntersectIgnoredForNonBox { brush: String },
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct BuildReport {
    pub input_brushes: usize,
    pub emitted_convex_fragments: usize,
    pub operator_brushes: usize,
    pub candidate_pairs: usize,
    pub rejected_pairs: usize,
    pub reused_mesh: bool,
    pub used_checkpoint: bool,
    pub checkpoint_rebuild_from: Option<usize>,
    pub direct_dirty_rebuild: bool,
    pub warnings: Vec<BuildWarning>,
}

#[derive(Clone, Debug)]
pub struct BuildOutput {
    pub mesh: TriangleMesh,
    pub report: BuildReport,
    pub generation: u64,
}

#[derive(Clone, Debug, Default)]
pub struct Assembler {
    brushes: Vec<Brush>,
    compiled: Vec<CompiledBrush>,
    cache: RefCell<Option<BuildOutput>>,
    box_cache: RefCell<Option<BoxBuildCache>>,
    general_cache: RefCell<Option<GeneralBuildCache>>,
    first_dirty_index: RefCell<Option<usize>>,
    next_id: u32,
    generation: u64,
}

#[derive(Clone, Debug)]
struct CompiledBrush {
    op: BrushOp,
    material: MaterialId,
    bounds: Aabb,
    geometry: CompiledGeometry,
    name: String,
}

#[derive(Clone, Debug)]
enum CompiledGeometry {
    Box(Aabb),
    Convex(ConvexSolid),
    CylinderZ {
        center: Vec3,
        radius: f32,
        depth: f32,
        segments: usize,
    },
    DomeCapZ {
        center: Vec3,
        radius: f32,
        height: f32,
        rings: usize,
        segments: usize,
    },
    FloretArm {
        anchor: Vec3,
        direction: Vec3,
        length: f32,
        root_width: f32,
        tip_width: f32,
        thickness: f32,
        tip_lift: f32,
    },
}

#[derive(Clone, Debug, Default)]
struct BoxBuildCache {
    generation: u64,
    valid_checkpoint_index: usize,
    checkpoints: Vec<BoxBuildState>,
}

#[derive(Clone, Debug, Default, PartialEq)]
struct BoxBuildState {
    boxes: Vec<(Aabb, MaterialId)>,
    report: BuildReport,
}

#[derive(Clone, Debug, Default)]
struct GeneralBuildCache {
    generation: u64,
    valid_checkpoint_index: usize,
    checkpoints: Vec<GeneralBuildState>,
}

#[derive(Clone, Debug, Default, PartialEq)]
struct GeneralBuildState {
    mesh: TriangleMesh,
    solids: Vec<ConvexSolid>,
    report: BuildReport,
}

impl Assembler {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn brushes(&self) -> &[Brush] {
        &self.brushes
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }

    pub fn brush_index(&self, id: BrushId) -> Option<usize> {
        self.brushes.iter().position(|brush| brush.id == id)
    }

    pub fn dirty_frontier_for_indices(
        &self,
        dirty_indices: impl IntoIterator<Item = usize>,
    ) -> DirtyDemandFrontier {
        DirtyDemandFrontier::from_dirty_indices(&self.brushes, dirty_indices)
    }

    pub fn dirty_frontier_for_brushes(
        &self,
        dirty_brushes: impl IntoIterator<Item = BrushId>,
    ) -> DirtyDemandFrontier {
        let indices = dirty_brushes
            .into_iter()
            .filter_map(|id| self.brush_index(id))
            .collect::<Vec<_>>();
        self.dirty_frontier_for_indices(indices)
    }

    pub fn add_brush(
        &mut self,
        name: impl Into<String>,
        op: BrushOp,
        primitive: Primitive,
        material: MaterialId,
    ) -> BrushId {
        let id = BrushId(self.next_id);
        self.next_id += 1;
        let index = self.brushes.len();
        let brush = Brush::new(id, name, op, primitive, material);
        self.compiled.push(CompiledBrush::from_brush(&brush));
        self.brushes.push(brush);
        self.generation = self.generation.wrapping_add(1);
        self.mark_dirty_index(index);
        id
    }

    pub fn set_brush_primitive(&mut self, id: BrushId, primitive: Primitive) -> bool {
        let Some(index) = self.brush_index(id) else {
            return false;
        };
        self.brushes[index].primitive = primitive;
        self.brushes[index].set_dirty();
        self.compiled[index] = CompiledBrush::from_brush(&self.brushes[index]);
        self.generation = self.generation.wrapping_add(1);
        self.mark_dirty_index(index);
        true
    }

    pub fn set_brush_op(&mut self, id: BrushId, op: BrushOp) -> bool {
        let Some(index) = self.brush_index(id) else {
            return false;
        };
        self.brushes[index].op = op;
        self.brushes[index].set_dirty();
        self.compiled[index] = CompiledBrush::from_brush(&self.brushes[index]);
        self.generation = self.generation.wrapping_add(1);
        self.mark_dirty_index(index);
        true
    }

    pub fn solid_box(
        &mut self,
        name: impl Into<String>,
        bounds: Aabb,
        material: MaterialId,
    ) -> BrushId {
        self.add_brush(name, BrushOp::Add, Primitive::Box { bounds }, material)
    }

    pub fn cut_box(&mut self, name: impl Into<String>, bounds: Aabb) -> BrushId {
        self.add_brush(
            name,
            BrushOp::Subtract,
            Primitive::Box { bounds },
            MaterialId::default(),
        )
    }

    pub fn solid_oriented_box(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        size: Vec3,
        rotation: bevy_math::Quat,
        material: MaterialId,
    ) -> BrushId {
        self.add_brush(
            name,
            BrushOp::Add,
            Primitive::OrientedBox {
                center,
                size,
                rotation,
            },
            material,
        )
    }

    pub fn cut_oriented_box(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        size: Vec3,
        rotation: bevy_math::Quat,
    ) -> BrushId {
        self.add_brush(
            name,
            BrushOp::Subtract,
            Primitive::OrientedBox {
                center,
                size,
                rotation,
            },
            MaterialId::default(),
        )
    }

    pub fn build(&self) -> BuildOutput {
        if let Some(output) = self
            .cache
            .borrow()
            .as_ref()
            .filter(|output| output.generation == self.generation)
        {
            return output.clone();
        }

        let output = self.build_with_checkpoints();
        *self.cache.borrow_mut() = Some(output.clone());
        *self.first_dirty_index.borrow_mut() = None;
        output
    }

    pub fn build_incremental(&self) -> BuildOutput {
        if let Some(output) = self
            .cache
            .borrow()
            .as_ref()
            .filter(|output| output.generation == self.generation)
        {
            return output.clone();
        }

        let first_dirty_index = *self.first_dirty_index.borrow();
        let output = self
            .try_build_axis_aligned_boxes_from_cache(first_dirty_index)
            .unwrap_or_else(|| self.build_general_from_cache(first_dirty_index));
        *self.cache.borrow_mut() = Some(output.clone());
        *self.first_dirty_index.borrow_mut() = None;
        output
    }

    pub fn rebuild_incremental_for_indices(
        &self,
        dirty_indices: impl IntoIterator<Item = usize>,
    ) -> BuildOutput {
        let first_dirty_index = self
            .dirty_frontier_for_indices(dirty_indices)
            .first_dirty_index;
        self.try_build_axis_aligned_boxes_from_cache(first_dirty_index)
            .unwrap_or_else(|| self.build_general_from_cache(first_dirty_index))
    }

    pub fn rebuild_incremental_for_brushes(
        &self,
        dirty_brushes: impl IntoIterator<Item = BrushId>,
    ) -> BuildOutput {
        let indices = dirty_brushes
            .into_iter()
            .filter_map(|id| self.brush_index(id))
            .collect::<Vec<_>>();
        self.rebuild_incremental_for_indices(indices)
    }

    pub fn rebuild(&self) -> BuildOutput {
        self.build_uncached()
    }

    pub fn rebuild_routed_surfaces(&self) -> BuildOutput {
        self.try_build_routed_surfaces()
            .unwrap_or_else(|| self.build_uncached())
    }

    pub fn supports_routed_surfaces(&self) -> bool {
        self.supports_routed_surface_subset()
    }

    fn mark_dirty_index(&self, index: usize) {
        let mut first_dirty_index = self.first_dirty_index.borrow_mut();
        *first_dirty_index = Some(first_dirty_index.map_or(index, |dirty| dirty.min(index)));
    }

    fn try_build_routed_surfaces(&self) -> Option<BuildOutput> {
        if !self.supports_routed_surface_subset() {
            return None;
        }

        let mut report = BuildReport {
            input_brushes: self.brushes.len(),
            ..BuildReport::default()
        };
        let mut source = None::<ConvexSolid>;
        let mut surfaces = Vec::<ConvexPolygon>::new();
        let mut previous_cutters = Vec::<ConvexSolid>::new();
        let mut scratch = PolygonRouteScratch::default();

        for brush in &self.compiled {
            match brush.op {
                BrushOp::Add => {
                    if source.is_some() {
                        return None;
                    }
                    let solid = brush.convex_cutter()?;
                    surfaces = solid.polygons.clone();
                    source = Some(solid);
                }
                BrushOp::Subtract => {
                    report.operator_brushes += 1;
                    let source_solid = source.as_ref()?;
                    if !source_solid.bounds.intersects(brush.bounds) {
                        report.rejected_pairs += 1;
                        continue;
                    }
                    let cutter = brush.convex_cutter()?;
                    report.candidate_pairs += 1;
                    surfaces = scratch.route_outside_of(surfaces, &cutter);

                    let mut caps = scratch.route_inside_of(cutter.polygons.clone(), source_solid);
                    for previous in &previous_cutters {
                        caps = scratch.route_outside_of(caps, previous);
                    }
                    for cap in &mut caps {
                        cap.material = source_solid.material;
                        cap.reversed = !cap.reversed;
                    }
                    surfaces.extend(caps);
                    previous_cutters.push(cutter);
                }
                BrushOp::Intersect => return None,
            }
        }

        let mut mesh = TriangleMesh::new();
        ConvexSolid::append_polygons_to_mesh(&surfaces, &mut mesh);
        report.emitted_convex_fragments = surfaces.len();

        Some(BuildOutput {
            mesh,
            report,
            generation: self.generation,
        })
    }

    fn supports_routed_surface_subset(&self) -> bool {
        let mut source = None::<Aabb>;
        let mut candidate_subtracts = 0usize;

        for brush in &self.compiled {
            match brush.op {
                BrushOp::Add => {
                    if source.is_some() || brush.convex_cutter().is_none() {
                        return false;
                    }
                    source = Some(brush.bounds);
                }
                BrushOp::Subtract => {
                    let Some(source_bounds) = source else {
                        return false;
                    };
                    if brush.convex_cutter().is_none() {
                        return false;
                    }
                    if source_bounds.intersects(brush.bounds) {
                        candidate_subtracts += 1;
                        if candidate_subtracts > 1 {
                            return false;
                        }
                    }
                }
                BrushOp::Intersect => return false,
            }
        }

        source.is_some() && candidate_subtracts == 1
    }

    fn build_uncached(&self) -> BuildOutput {
        if self
            .compiled
            .iter()
            .all(|brush| matches!(brush.geometry, CompiledGeometry::Box(_)))
        {
            return self.build_axis_aligned_boxes_direct();
        }

        self.build_general_direct()
    }

    fn build_with_checkpoints(&self) -> BuildOutput {
        self.try_build_axis_aligned_boxes_from_cache(Some(0))
            .unwrap_or_else(|| self.build_general_from_cache(Some(0)))
    }

    fn build_general_direct(&self) -> BuildOutput {
        let mut state = GeneralBuildState::default();
        for brush in &self.compiled {
            state.apply_compiled_brush(brush);
        }
        state.into_output(self.brushes.len(), self.generation)
    }

    fn build_axis_aligned_boxes_direct(&self) -> BuildOutput {
        let mut state = BoxBuildState::default();
        for brush in &self.compiled {
            state.apply_compiled_box(brush);
        }
        state.report.direct_dirty_rebuild = true;
        state.into_output_with_mesh(self.brushes.len(), self.generation, None)
    }

    fn try_build_axis_aligned_boxes_from_cache(
        &self,
        first_dirty_index: Option<usize>,
    ) -> Option<BuildOutput> {
        if !self
            .compiled
            .iter()
            .all(|brush| matches!(brush.geometry, CompiledGeometry::Box(_)))
        {
            return None;
        }

        let brush_count = self.compiled.len();
        let previous_output = self.cache.borrow().as_ref().cloned();
        if let Some(first_dirty_index) = first_dirty_index
            && first_dirty_index > 0
        {
            return Some(self.build_axis_aligned_boxes_direct());
        }
        let rebuild_from = match first_dirty_index {
            Some(index) => index.min(brush_count),
            None => self
                .box_cache
                .borrow()
                .as_ref()
                .filter(|cache| {
                    cache.generation == self.generation
                        && cache.valid_checkpoint_index == brush_count
                        && cache.checkpoints.len() == brush_count + 1
                })
                .map_or(0, |_| brush_count),
        };
        let cached_state = self
            .box_cache
            .borrow()
            .as_ref()
            .filter(|cache| rebuild_from <= cache.valid_checkpoint_index)
            .and_then(|cache| cache.checkpoints.get(rebuild_from).cloned());
        let (mut state, start_index) = if let Some(state) = cached_state {
            (state, rebuild_from)
        } else {
            (BoxBuildState::default(), 0)
        };
        if start_index > 0 {
            state.report.used_checkpoint = true;
            state.report.checkpoint_rebuild_from = Some(start_index);
        }

        let mut checkpoints = (start_index == 0).then(|| vec![BoxBuildState::default()]);

        let mut changed_geometry = start_index == 0;
        for brush in &self.compiled[start_index..] {
            changed_geometry |= state.apply_compiled_box(brush);
            if let Some(checkpoints) = &mut checkpoints {
                checkpoints.push(state.clone());
            }
        }

        if let Some(checkpoints) = checkpoints {
            *self.box_cache.borrow_mut() = Some(BoxBuildCache {
                generation: self.generation,
                valid_checkpoint_index: brush_count,
                checkpoints,
            });
        } else if let Some(cache) = self.box_cache.borrow_mut().as_mut() {
            cache.generation = self.generation;
            cache.valid_checkpoint_index = cache.valid_checkpoint_index.min(start_index);
        }

        let reused_mesh = (!changed_geometry)
            .then_some(previous_output)
            .flatten()
            .map(|output| output.mesh);
        Some(state.into_output_with_mesh(self.brushes.len(), self.generation, reused_mesh))
    }

    fn build_general_from_cache(&self, first_dirty_index: Option<usize>) -> BuildOutput {
        let brush_count = self.compiled.len();
        let previous_output = self.cache.borrow().as_ref().cloned();
        let rebuild_from = match first_dirty_index {
            Some(index) => index.min(brush_count),
            None => self
                .general_cache
                .borrow()
                .as_ref()
                .filter(|cache| {
                    cache.generation == self.generation
                        && cache.valid_checkpoint_index == brush_count
                        && cache.checkpoints.len() == brush_count + 1
                })
                .map_or(0, |_| brush_count),
        };
        let cached_state = self
            .general_cache
            .borrow()
            .as_ref()
            .filter(|cache| rebuild_from <= cache.valid_checkpoint_index)
            .and_then(|cache| cache.checkpoints.get(rebuild_from).cloned());
        let (mut state, start_index) = if let Some(state) = cached_state {
            (state, rebuild_from)
        } else {
            (GeneralBuildState::default(), 0)
        };
        if start_index > 0 {
            state.report.used_checkpoint = true;
            state.report.checkpoint_rebuild_from = Some(start_index);
        }

        let mut checkpoints = (start_index == 0).then(|| vec![GeneralBuildState::default()]);

        let mut changed_geometry = start_index == 0;
        for brush in &self.compiled[start_index..] {
            changed_geometry |= state.apply_compiled_brush(brush);
            if let Some(checkpoints) = &mut checkpoints {
                checkpoints.push(state.clone());
            }
        }

        if let Some(checkpoints) = checkpoints {
            *self.general_cache.borrow_mut() = Some(GeneralBuildCache {
                generation: self.generation,
                valid_checkpoint_index: brush_count,
                checkpoints,
            });
        } else if let Some(cache) = self.general_cache.borrow_mut().as_mut() {
            cache.generation = self.generation;
            cache.valid_checkpoint_index = cache.valid_checkpoint_index.min(start_index);
        }

        let reused_mesh = (!changed_geometry)
            .then_some(previous_output)
            .flatten()
            .map(|output| output.mesh);
        state.into_output_with_mesh(self.brushes.len(), self.generation, reused_mesh)
    }

    pub fn sample_room_with_door() -> Self {
        let wall = MaterialId(1);
        let floor = MaterialId(2);
        let mut asm = Self::new();
        asm.solid_box(
            "floor",
            Aabb::from_center_size(Vec3::new(0.0, 0.0, -0.1), Vec3::new(8.0, 8.0, 0.2)),
            floor,
        );
        asm.solid_box(
            "north wall",
            Aabb::from_center_size(Vec3::new(0.0, 4.0, 1.5), Vec3::new(8.0, 0.25, 3.0)),
            wall,
        );
        asm.cut_box(
            "door void",
            Aabb::from_center_size(Vec3::new(0.0, 4.0, 1.0), Vec3::new(1.2, 0.5, 2.0)),
        );
        asm
    }
}

impl CompiledBrush {
    fn from_brush(brush: &Brush) -> Self {
        let bounds = brush.bounds();
        let geometry = match &brush.primitive {
            Primitive::Box { bounds } => CompiledGeometry::Box(*bounds),
            Primitive::OrientedBox {
                center,
                size,
                rotation,
            } => CompiledGeometry::Convex(ConvexSolid::box_from_center_size(
                *center,
                *size,
                *rotation,
                brush.material,
            )),
            Primitive::CylinderZ {
                center,
                radius,
                depth,
                segments,
            } => CompiledGeometry::CylinderZ {
                center: *center,
                radius: *radius,
                depth: *depth,
                segments: *segments,
            },
            Primitive::DomeCapZ {
                center,
                radius,
                height,
                rings,
                segments,
            } => CompiledGeometry::DomeCapZ {
                center: *center,
                radius: *radius,
                height: *height,
                rings: *rings,
                segments: *segments,
            },
            Primitive::FloretArm {
                anchor,
                direction,
                length,
                root_width,
                tip_width,
                thickness,
                tip_lift,
            } => CompiledGeometry::FloretArm {
                anchor: *anchor,
                direction: *direction,
                length: *length,
                root_width: *root_width,
                tip_width: *tip_width,
                thickness: *thickness,
                tip_lift: *tip_lift,
            },
        };

        Self {
            op: brush.op,
            material: brush.material,
            bounds,
            geometry,
            name: brush.name.clone(),
        }
    }

    fn convex_cutter(&self) -> Option<ConvexSolid> {
        match &self.geometry {
            CompiledGeometry::Box(bounds) => Some(ConvexSolid::from_aabb(*bounds, self.material)),
            CompiledGeometry::Convex(solid) => Some(solid.clone()),
            _ => None,
        }
    }
}

impl BoxBuildState {
    fn apply_compiled_box(&mut self, brush: &CompiledBrush) -> bool {
        let CompiledGeometry::Box(bounds) = brush.geometry else {
            unreachable!("box build state received a non-box brush");
        };

        match brush.op {
            BrushOp::Add => {
                self.boxes.push((bounds, brush.material));
                true
            }
            BrushOp::Subtract => {
                self.report.operator_brushes += 1;
                if !bounds.intersects_any(self.boxes.iter().map(|(bounds, _)| *bounds)) {
                    self.report.rejected_pairs += self.boxes.len();
                    return false;
                }

                let mut out = Vec::with_capacity(self.boxes.len() + 4);
                for (solid, material) in self.boxes.drain(..) {
                    if solid.intersects(bounds) {
                        self.report.candidate_pairs += 1;
                        out.extend(
                            solid
                                .subtract_box(bounds)
                                .into_iter()
                                .map(|piece| (piece, material)),
                        );
                    } else {
                        self.report.rejected_pairs += 1;
                        out.push((solid, material));
                    }
                }
                self.boxes = out;
                true
            }
            BrushOp::Intersect => {
                self.report.operator_brushes += 1;
                if !bounds.intersects_any(self.boxes.iter().map(|(bounds, _)| *bounds)) {
                    let changed = !self.boxes.is_empty();
                    self.report.rejected_pairs += self.boxes.len();
                    self.boxes.clear();
                    return changed;
                }

                self.boxes = self
                    .boxes
                    .drain(..)
                    .filter_map(|(solid, material)| {
                        if let Some(hit) = solid.intersection(bounds) {
                            self.report.candidate_pairs += 1;
                            Some((hit, material))
                        } else {
                            self.report.rejected_pairs += 1;
                            None
                        }
                    })
                    .collect();
                true
            }
        }
    }

    fn into_output_with_mesh(
        mut self,
        input_brushes: usize,
        generation: u64,
        reused_mesh: Option<TriangleMesh>,
    ) -> BuildOutput {
        let mut mesh = TriangleMesh::new();
        self.report.input_brushes = input_brushes;
        self.report.emitted_convex_fragments = self.boxes.len();
        if let Some(reused_mesh) = reused_mesh {
            self.report.reused_mesh = true;
            mesh = reused_mesh;
        } else {
            for (bounds, material) in self.boxes {
                mesh.append_box(bounds, material);
            }
        }

        BuildOutput {
            mesh,
            report: self.report,
            generation,
        }
    }
}

impl GeneralBuildState {
    fn apply_compiled_brush(&mut self, brush: &CompiledBrush) -> bool {
        match brush.op {
            BrushOp::Add => {
                self.add_brush_geometry(brush);
                true
            }
            BrushOp::Subtract => {
                self.report.operator_brushes += 1;
                if !brush
                    .bounds
                    .intersects_any(self.solids.iter().map(|solid| solid.bounds))
                {
                    self.report.rejected_pairs += self.solids.len();
                    return false;
                }
                if let Some(cutter) = brush.convex_cutter() {
                    self.solids = subtract_from_solids(
                        std::mem::take(&mut self.solids),
                        &cutter,
                        &mut self.report,
                    );
                    true
                } else {
                    let has_candidate =
                        record_solid_pairs(&self.solids, brush.bounds, &mut self.report);
                    if has_candidate {
                        self.report
                            .warnings
                            .push(BuildWarning::SubtractIgnoredForNonBox {
                                brush: brush.name.clone(),
                            });
                    }
                    false
                }
            }
            BrushOp::Intersect => {
                self.report.operator_brushes += 1;
                if !brush
                    .bounds
                    .intersects_any(self.solids.iter().map(|solid| solid.bounds))
                {
                    let changed = !self.solids.is_empty();
                    self.report.rejected_pairs += self.solids.len();
                    self.solids.clear();
                    return changed;
                }
                if let Some(cutter) = brush.convex_cutter() {
                    self.solids = intersect_solids(
                        std::mem::take(&mut self.solids),
                        &cutter,
                        &mut self.report,
                    );
                    true
                } else {
                    let has_candidate =
                        record_solid_pairs(&self.solids, brush.bounds, &mut self.report);
                    let changed = !self.solids.is_empty();
                    self.solids.clear();
                    if has_candidate {
                        self.report
                            .warnings
                            .push(BuildWarning::IntersectIgnoredForNonBox {
                                brush: brush.name.clone(),
                            });
                    }
                    changed
                }
            }
        }
    }

    fn add_brush_geometry(&mut self, brush: &CompiledBrush) {
        match &brush.geometry {
            CompiledGeometry::Box(bounds) => {
                self.solids
                    .push(ConvexSolid::from_aabb(*bounds, brush.material));
            }
            CompiledGeometry::Convex(solid) => self.solids.push(solid.clone()),
            CompiledGeometry::CylinderZ {
                center,
                radius,
                depth,
                segments,
            } => append_cylinder_z(
                &mut self.mesh,
                *center,
                *radius,
                *depth,
                *segments,
                brush.material,
            ),
            CompiledGeometry::DomeCapZ {
                center,
                radius,
                height,
                rings,
                segments,
            } => append_dome_cap_z(
                &mut self.mesh,
                DomeCapZSpec {
                    center: *center,
                    radius: *radius,
                    height: *height,
                    rings: *rings,
                    segments: *segments,
                    material: brush.material,
                },
            ),
            CompiledGeometry::FloretArm {
                anchor,
                direction,
                length,
                root_width,
                tip_width,
                thickness,
                tip_lift,
            } => append_floret_arm(
                &mut self.mesh,
                FloretArmSpec {
                    anchor: *anchor,
                    direction: *direction,
                    length: *length,
                    root_width: *root_width,
                    tip_width: *tip_width,
                    thickness: *thickness,
                    tip_lift: *tip_lift,
                    material: brush.material,
                },
            ),
        }
    }

    fn into_output(self, input_brushes: usize, generation: u64) -> BuildOutput {
        self.into_output_with_mesh(input_brushes, generation, None)
    }

    fn into_output_with_mesh(
        mut self,
        input_brushes: usize,
        generation: u64,
        reused_mesh: Option<TriangleMesh>,
    ) -> BuildOutput {
        self.report.input_brushes = input_brushes;
        self.report.emitted_convex_fragments = self.solids.len();
        if let Some(reused_mesh) = reused_mesh {
            self.report.reused_mesh = true;
            self.mesh = reused_mesh;
        } else {
            for solid in self.solids {
                solid.append_to_mesh(&mut self.mesh);
            }
        }

        BuildOutput {
            mesh: self.mesh,
            report: self.report,
            generation,
        }
    }
}

fn subtract_from_solids(
    solids: Vec<ConvexSolid>,
    cutter: &ConvexSolid,
    report: &mut BuildReport,
) -> Vec<ConvexSolid> {
    let mut out = Vec::with_capacity(solids.len() + 4);
    for solid in solids {
        if solid.bounds.intersects(cutter.bounds) {
            report.candidate_pairs += 1;
            out.extend(solid.subtract_convex_owned(cutter));
        } else {
            report.rejected_pairs += 1;
            out.push(solid);
        }
    }
    out
}

fn intersect_solids(
    solids: Vec<ConvexSolid>,
    cutter: &ConvexSolid,
    report: &mut BuildReport,
) -> Vec<ConvexSolid> {
    let mut out = Vec::with_capacity(solids.len());
    for solid in solids {
        if !solid.bounds.intersects(cutter.bounds) {
            report.rejected_pairs += 1;
            continue;
        }
        report.candidate_pairs += 1;
        if let Some(fragment) = solid.intersect_convex_owned(cutter) {
            out.push(fragment);
        }
    }
    out
}

fn record_solid_pairs(solids: &[ConvexSolid], bounds: Aabb, report: &mut BuildReport) -> bool {
    let mut has_candidate = false;
    for solid in solids {
        if solid.bounds.intersects(bounds) {
            report.candidate_pairs += 1;
            has_candidate = true;
        } else {
            report.rejected_pairs += 1;
        }
    }
    has_candidate
}

#[cfg(test)]
mod tests {
    use bevy_math::Vec3;

    use super::*;

    #[test]
    fn centered_subtract_splits_box_into_six_fragments() {
        let source = Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0));
        let cutter = Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0));
        let pieces = source.subtract_box(cutter);

        assert_eq!(pieces.len(), 6);
        assert!(pieces.iter().all(|piece| piece.is_valid()));
        assert!(pieces.iter().all(|piece| !piece.intersects(cutter)));
    }

    #[test]
    fn door_cut_leaves_no_box_fragment_inside_void() {
        let output = Assembler::sample_room_with_door().build();
        let cutter = Aabb::from_center_size(Vec3::new(0.0, 4.0, 1.0), Vec3::new(1.2, 0.5, 2.0));

        assert!(output.report.emitted_convex_fragments > 2);
        for tri in output.mesh.indices.chunks_exact(3) {
            let center = (output.mesh.positions[tri[0] as usize]
                + output.mesh.positions[tri[1] as usize]
                + output.mesh.positions[tri[2] as usize])
                / 3.0;
            assert!(!cutter.contains_point_strict(center, 1.0e-4));
        }
    }

    #[test]
    fn rotated_door_cut_produces_diagonal_portal_faces() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "wall",
            Aabb::from_center_size(Vec3::new(0.0, 0.0, 1.5), Vec3::new(5.0, 0.5, 3.0)),
            MaterialId(1),
        );
        asm.cut_oriented_box(
            "angled void",
            Vec3::new(0.0, 0.0, 1.5),
            Vec3::new(1.5, 2.0, 2.0),
            bevy_math::Quat::from_rotation_z(std::f32::consts::FRAC_PI_4),
        );

        let output = asm.build();
        assert!(output.report.emitted_convex_fragments > 2);
        assert!(output.mesh.normals.iter().any(|normal| {
            normal.x.abs() > 0.2 && normal.y.abs() > 0.2 && normal.z.abs() < 0.1
        }));
    }

    #[test]
    fn mesh_box_emits_twelve_triangles() {
        let mut mesh = TriangleMesh::new();
        mesh.append_box(Aabb::from_center_size(Vec3::ZERO, Vec3::ONE), MaterialId(7));

        assert_eq!(mesh.triangle_count(), 12);
        assert_eq!(mesh.triangle_materials.len(), 12);
        assert_eq!(mesh.triangle_materials[0], MaterialId(7));
    }

    #[test]
    fn distant_subtract_skips_convex_split_and_keeps_source_solid() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            MaterialId(1),
        );
        asm.cut_box(
            "far void",
            Aabb::from_center_size(Vec3::splat(10.0), Vec3::splat(1.0)),
        );

        let output = asm.build();
        assert_eq!(output.report.emitted_convex_fragments, 1);
        assert_eq!(output.report.operator_brushes, 1);
        assert_eq!(output.report.candidate_pairs, 0);
        assert_eq!(output.report.rejected_pairs, 1);
        assert_eq!(output.mesh.triangle_count(), 12);
    }

    #[test]
    fn intersecting_box_keeps_common_convex_region() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "a",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        asm.add_brush(
            "b",
            BrushOp::Intersect,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::X, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );

        let output = asm.build();
        assert_eq!(output.report.emitted_convex_fragments, 1);
        assert_eq!(output.report.operator_brushes, 1);
        assert_eq!(output.report.candidate_pairs, 1);
        assert_eq!(output.report.rejected_pairs, 0);
        assert_eq!(output.report.warnings.len(), 0);
        assert_eq!(output.mesh.triangle_count(), 12);
    }

    #[test]
    fn cached_build_and_dirty_rebuild_emit_same_output() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "slab",
            Aabb::from_center_size(Vec3::ZERO, Vec3::new(8.0, 8.0, 2.0)),
            MaterialId(1),
        );
        asm.cut_box(
            "center void",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
        );
        asm.cut_oriented_box(
            "angled void",
            Vec3::new(1.0, 1.0, 0.0),
            Vec3::new(1.25, 4.0, 2.5),
            bevy_math::Quat::from_rotation_z(0.35),
        );

        let stable = asm.build();
        let cached = asm.build();
        let dirty = asm.rebuild();

        assert_eq!(
            stable.report.emitted_convex_fragments,
            dirty.report.emitted_convex_fragments
        );
        assert_eq!(
            stable.report.operator_brushes,
            dirty.report.operator_brushes
        );
        assert_eq!(stable.report.candidate_pairs, dirty.report.candidate_pairs);
        assert_eq!(stable.report.rejected_pairs, dirty.report.rejected_pairs);
        assert_eq!(stable.report.warnings, dirty.report.warnings);
        assert_eq!(stable.mesh.triangle_count(), dirty.mesh.triangle_count());
        assert_eq!(stable.mesh.vertex_count(), dirty.mesh.vertex_count());
        assert_eq!(stable.mesh.positions, dirty.mesh.positions);
        assert_eq!(stable.mesh.indices, dirty.mesh.indices);
        assert_eq!(cached.mesh.positions, stable.mesh.positions);
        assert_eq!(cached.mesh.indices, stable.mesh.indices);
    }

    #[test]
    fn routed_surface_rebuild_emits_clean_center_cut_boundary() {
        let mut asm = Assembler::new();
        let cutter = Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0));
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        asm.cut_box("void", cutter);

        let routed = asm.rebuild_routed_surfaces();

        assert_eq!(routed.report.operator_brushes, 1);
        assert_eq!(routed.report.candidate_pairs, 1);
        assert_eq!(routed.report.rejected_pairs, 0);
        assert_eq!(routed.report.emitted_convex_fragments, 24);
        assert!(routed.mesh.triangle_count() < asm.rebuild().mesh.triangle_count());
        for tri in routed.mesh.indices.chunks_exact(3) {
            let center = (routed.mesh.positions[tri[0] as usize]
                + routed.mesh.positions[tri[1] as usize]
                + routed.mesh.positions[tri[2] as usize])
                / 3.0;
            assert!(!cutter.contains_point_strict(center, 1.0e-4));
        }
    }

    #[test]
    fn routed_surface_rebuild_falls_back_for_intersections() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        asm.add_brush(
            "common",
            BrushOp::Intersect,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::X, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );

        let stable = asm.rebuild();
        let routed = asm.rebuild_routed_surfaces();

        assert_eq!(
            routed.report.emitted_convex_fragments,
            stable.report.emitted_convex_fragments
        );
        assert_eq!(routed.mesh.triangle_count(), stable.mesh.triangle_count());
        assert_eq!(routed.mesh.positions, stable.mesh.positions);
        assert_eq!(routed.mesh.indices, stable.mesh.indices);
    }

    #[test]
    fn routed_surface_rebuild_falls_back_for_multiple_candidate_cutters() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        asm.cut_oriented_box(
            "a",
            Vec3::new(-0.5, 0.0, 0.0),
            Vec3::new(1.0, 5.0, 5.0),
            bevy_math::Quat::from_rotation_z(0.25),
        );
        asm.cut_oriented_box(
            "b",
            Vec3::new(0.5, 0.0, 0.0),
            Vec3::new(1.0, 5.0, 5.0),
            bevy_math::Quat::from_rotation_z(-0.25),
        );

        let stable = asm.rebuild();
        let routed = asm.rebuild_routed_surfaces();

        assert_eq!(
            routed.report.emitted_convex_fragments,
            stable.report.emitted_convex_fragments
        );
        assert_eq!(routed.mesh.triangle_count(), stable.mesh.triangle_count());
        assert_eq!(routed.mesh.positions, stable.mesh.positions);
        assert_eq!(routed.mesh.indices, stable.mesh.indices);
    }

    #[test]
    fn routed_surface_rebuild_falls_back_when_no_cutter_touches_source() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        asm.cut_box(
            "far",
            Aabb::from_center_size(Vec3::splat(20.0), Vec3::splat(2.0)),
        );

        let stable = asm.rebuild();
        let routed = asm.rebuild_routed_surfaces();

        assert_eq!(
            routed.report.emitted_convex_fragments,
            stable.report.emitted_convex_fragments
        );
        assert_eq!(routed.mesh.triangle_count(), stable.mesh.triangle_count());
        assert_eq!(routed.mesh.positions, stable.mesh.positions);
        assert_eq!(routed.mesh.indices, stable.mesh.indices);
    }

    #[test]
    fn mutating_brush_primitive_invalidates_cache_and_exposes_dirty_frontier() {
        let mut asm = Assembler::new();
        let source = asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        let cutter = asm.cut_box("void", Aabb::from_center_size(Vec3::ZERO, Vec3::splat(1.0)));
        let before = asm.build();

        assert!(asm.set_brush_primitive(
            cutter,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            },
        ));
        let frontier = asm.dirty_frontier_for_brushes([cutter]);
        let after = asm.build();

        assert_eq!(frontier.first_dirty_index, Some(1));
        assert_eq!(frontier.operator_brushes, 1);
        assert_eq!(frontier.candidate_pairs(), 1);
        assert_eq!(frontier.rejected_pairs, 0);
        assert!(after.generation > before.generation);
        assert_ne!(after.mesh.positions, before.mesh.positions);
        assert_eq!(asm.brush_index(source), Some(0));
        assert_eq!(asm.brush_index(cutter), Some(1));
    }

    #[test]
    fn incremental_box_rebuild_reuses_prefix_and_matches_full_rebuild() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(8.0)),
            MaterialId(1),
        );
        asm.cut_box(
            "early",
            Aabb::from_center_size(Vec3::new(-2.0, 0.0, 0.0), Vec3::splat(2.0)),
        );
        let dirty = asm.cut_box(
            "dirty",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
        );
        asm.cut_box(
            "late",
            Aabb::from_center_size(Vec3::new(2.0, 0.0, 0.0), Vec3::splat(2.0)),
        );
        let before = asm.build();

        assert!(asm.set_brush_primitive(
            dirty,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(3.0)),
            },
        ));
        let incremental = asm.build_incremental();
        let full = asm.rebuild();

        assert!(!incremental.report.used_checkpoint);
        assert!(incremental.report.direct_dirty_rebuild);
        assert_eq!(incremental.mesh.positions, full.mesh.positions);
        assert_eq!(incremental.mesh.indices, full.mesh.indices);
        assert_eq!(
            incremental.report.candidate_pairs,
            full.report.candidate_pairs
        );
        assert_eq!(
            incremental.report.rejected_pairs,
            full.report.rejected_pairs
        );
        assert_eq!(incremental.generation, full.generation);
        assert_ne!(incremental.mesh.positions, before.mesh.positions);
    }

    #[test]
    fn explicit_incremental_box_rebuild_matches_full_rebuild() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(8.0)),
            MaterialId(1),
        );
        asm.cut_box(
            "early",
            Aabb::from_center_size(Vec3::new(-2.0, 0.0, 0.0), Vec3::splat(2.0)),
        );
        let dirty = asm.cut_box(
            "dirty",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
        );
        asm.cut_box(
            "late",
            Aabb::from_center_size(Vec3::new(2.0, 0.0, 0.0), Vec3::splat(2.0)),
        );
        asm.build_incremental();

        assert!(asm.set_brush_primitive(
            dirty,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(3.0)),
            },
        ));
        let incremental = asm.rebuild_incremental_for_brushes([dirty]);
        let full = asm.rebuild();

        assert_eq!(incremental.mesh.positions, full.mesh.positions);
        assert_eq!(incremental.mesh.indices, full.mesh.indices);
        assert_eq!(
            incremental.report.candidate_pairs,
            full.report.candidate_pairs
        );
        assert_eq!(
            incremental.report.rejected_pairs,
            full.report.rejected_pairs
        );
    }

    #[test]
    fn incremental_distant_tail_edit_reuses_previous_mesh() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(8.0)),
            MaterialId(1),
        );
        let far = asm.cut_oriented_box(
            "far",
            Vec3::splat(100.0),
            Vec3::splat(1.0),
            bevy_math::Quat::from_rotation_z(0.2),
        );
        let before = asm.build_incremental();

        assert!(asm.set_brush_primitive(
            far,
            Primitive::OrientedBox {
                center: Vec3::splat(120.0),
                size: Vec3::splat(1.0),
                rotation: bevy_math::Quat::from_rotation_z(0.2),
            },
        ));
        let after = asm.build_incremental();

        assert_eq!(after.mesh, before.mesh);
        assert!(after.report.reused_mesh);
        assert!(after.report.used_checkpoint);
        assert_eq!(after.report.checkpoint_rebuild_from, Some(1));
        assert!(!after.report.direct_dirty_rebuild);
        assert_eq!(after.report.candidate_pairs, 0);
        assert_eq!(after.report.rejected_pairs, 1);
    }

    #[test]
    fn incremental_convex_rebuild_matches_full_rebuild() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "slab",
            Aabb::from_center_size(Vec3::ZERO, Vec3::new(16.0, 16.0, 4.0)),
            MaterialId(1),
        );

        let mut tail = BrushId(0);
        for index in 0..12 {
            let angle = index as f32 * 0.217;
            let center = Vec3::new(angle.cos() * 4.0, angle.sin() * 4.0, 0.0);
            tail = asm.cut_oriented_box(
                format!("rotated_void_{index}"),
                center,
                Vec3::new(1.0, 6.0, 5.0),
                bevy_math::Quat::from_rotation_z(angle),
            );
        }
        asm.build_incremental();

        assert!(asm.set_brush_primitive(
            tail,
            Primitive::OrientedBox {
                center: Vec3::new(3.0, 0.0, 0.0),
                size: Vec3::new(1.25, 6.0, 5.0),
                rotation: bevy_math::Quat::from_rotation_z(0.5),
            },
        ));
        let incremental = asm.build_incremental();
        let full = asm.rebuild();

        assert!(incremental.report.used_checkpoint);
        assert_eq!(incremental.report.checkpoint_rebuild_from, Some(12));
        assert!(!incremental.report.direct_dirty_rebuild);
        assert_eq!(incremental.mesh.positions, full.mesh.positions);
        assert_eq!(incremental.mesh.indices, full.mesh.indices);
        assert_eq!(
            incremental.report.candidate_pairs,
            full.report.candidate_pairs
        );
        assert_eq!(
            incremental.report.rejected_pairs,
            full.report.rejected_pairs
        );
    }

    #[test]
    fn mutating_brush_operation_changes_ordered_output() {
        let mut asm = Assembler::new();
        let _source = asm.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        let other = asm.solid_box(
            "other",
            Aabb::from_center_size(Vec3::new(0.5, 0.0, 0.0), Vec3::splat(3.0)),
            MaterialId(1),
        );
        let before = asm.rebuild();

        assert!(asm.set_brush_op(other, BrushOp::Intersect));
        let after = asm.rebuild();

        assert_eq!(after.report.operator_brushes, 1);
        assert_eq!(after.report.candidate_pairs, 1);
        assert!(after.mesh.triangle_count() < before.mesh.triangle_count());
    }

    #[test]
    fn rotated_cut_stack_regression_preserves_ordered_output_shape() {
        let mut asm = Assembler::new();
        asm.solid_box(
            "slab",
            Aabb::from_center_size(Vec3::ZERO, Vec3::new(32.0, 32.0, 4.0)),
            MaterialId(1),
        );

        for index in 0..64 {
            let angle = index as f32 * 0.173;
            let radius = 11.0 + (index % 7) as f32 * 0.35;
            let center = Vec3::new(angle.cos() * radius, angle.sin() * radius, 0.0);
            asm.cut_oriented_box(
                format!("rotated_void_{index}"),
                center,
                Vec3::new(1.0 + (index % 3) as f32 * 0.3, 8.0, 5.0),
                bevy_math::Quat::from_rotation_z(angle),
            );
        }

        let output = asm.rebuild();

        assert_eq!(output.report.emitted_convex_fragments, 280);
        assert_eq!(output.mesh.triangle_count(), 3404);
        assert_eq!(output.report.candidate_pairs, 804);
        assert_eq!(output.report.rejected_pairs, 9417);
    }
}
