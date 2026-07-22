#![allow(dead_code)]
use bevy_math::{Quat, Vec2, Vec3};

use crate::{Aabb, MaterialId, PolygonCategory, TriangleMesh};

const EPSILON: f32 = 1.0e-5;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Plane {
    pub normal: Vec3,
    pub distance: f32,
}

impl Plane {
    pub fn from_point_normal(point: Vec3, normal: Vec3) -> Self {
        let normal = normal.normalize_or_zero();
        Self {
            normal,
            distance: -normal.dot(point),
        }
    }

    pub fn signed_distance(self, point: Vec3) -> f32 {
        self.normal.dot(point) + self.distance
    }

    pub fn is_coplanar_with(self, other: Self) -> bool {
        self.normal.dot(other.normal).abs() > 1.0 - EPSILON
            && (self.distance - other.distance).abs() <= EPSILON
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ConvexPolygon {
    pub vertices: Vec<Vec3>,
    pub normal: Vec3,
    pub material: MaterialId,
    pub category: PolygonCategory,
    pub visible: bool,
    pub reversed: bool,
    pub bounds: Aabb,
}

impl ConvexPolygon {
    pub fn new(vertices: Vec<Vec3>, normal: Vec3, material: MaterialId) -> Self {
        Self {
            bounds: Aabb::from_points(&vertices),
            vertices,
            normal: normal.normalize_or_zero(),
            material,
            category: PolygonCategory::Aligned,
            visible: true,
            reversed: false,
        }
    }

    pub fn centroid(&self) -> Vec3 {
        if self.vertices.is_empty() {
            return Vec3::ZERO;
        }
        self.vertices.iter().copied().sum::<Vec3>() / self.vertices.len() as f32
    }

    fn with_vertices(&self, vertices: Vec<Vec3>) -> Self {
        Self {
            bounds: Aabb::from_points(&vertices),
            vertices,
            normal: self.normal,
            material: self.material,
            category: self.category,
            visible: self.visible,
            reversed: self.reversed,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ConvexSolid {
    pub polygons: Vec<ConvexPolygon>,
    pub clip_planes: Vec<Plane>,
    pub material: MaterialId,
    pub bounds: Aabb,
}

#[derive(Clone, Debug, Default)]
pub struct CategorizedPolygons {
    pub inside: Vec<ConvexPolygon>,
    pub outside: Vec<ConvexPolygon>,
    pub aligned: Vec<ConvexPolygon>,
    pub reverse_aligned: Vec<ConvexPolygon>,
}

#[derive(Default)]
pub struct PolygonRouteScratch {
    pieces: Vec<CategorizationPiece>,
    inside_pieces: Vec<CategorizationPiece>,
    cap_points: Vec<Vec3>,
}

impl ConvexSolid {
    pub fn from_aabb(bounds: Aabb, material: MaterialId) -> Self {
        Self::box_from_center_size(bounds.center(), bounds.size(), Quat::IDENTITY, material)
    }

    pub fn box_from_center_size(
        center: Vec3,
        size: Vec3,
        rotation: Quat,
        material: MaterialId,
    ) -> Self {
        let half = size * 0.5;
        let local_corners = [
            Vec3::new(-half.x, -half.y, -half.z),
            Vec3::new(half.x, -half.y, -half.z),
            Vec3::new(half.x, half.y, -half.z),
            Vec3::new(-half.x, half.y, -half.z),
            Vec3::new(-half.x, -half.y, half.z),
            Vec3::new(half.x, -half.y, half.z),
            Vec3::new(half.x, half.y, half.z),
            Vec3::new(-half.x, half.y, half.z),
        ];
        let corners = local_corners.map(|point| center + rotation * point);
        let faces = [
            ([0, 3, 2, 1], Vec3::NEG_Z),
            ([4, 5, 6, 7], Vec3::Z),
            ([0, 1, 5, 4], Vec3::NEG_Y),
            ([3, 7, 6, 2], Vec3::Y),
            ([0, 4, 7, 3], Vec3::NEG_X),
            ([1, 2, 6, 5], Vec3::X),
        ];

        let mut polygons = Vec::with_capacity(6);
        let mut clip_planes = Vec::with_capacity(6);
        for (indices, local_normal) in faces {
            let normal = rotation * local_normal;
            let vertices = indices.map(|index| corners[index]).to_vec();
            clip_planes.push(Plane::from_point_normal(vertices[0], normal));
            polygons.push(ConvexPolygon::new(vertices, normal, material));
        }

        Self {
            bounds: Aabb::from_points(&corners),
            polygons,
            clip_planes,
            material,
        }
    }

    pub fn subtract_convex(&self, cutter: &Self) -> Vec<Self> {
        self.clone().subtract_convex_owned(cutter)
    }

    pub fn subtract_convex_owned(self, cutter: &Self) -> Vec<Self> {
        let mut fragments = Vec::new();
        let mut remainder = Some(self);

        for plane in &cutter.clip_planes {
            let Some(current) = remainder.take() else {
                break;
            };

            match current.split_owned(*plane) {
                SplitResult::Front(front) => {
                    fragments.push(front);
                    break;
                }
                SplitResult::Back(back) => {
                    remainder = Some(back);
                }
                SplitResult::Both { front, back } => {
                    fragments.push(front);
                    remainder = Some(back);
                }
                SplitResult::Coplanar(solid) => {
                    remainder = Some(solid);
                }
            }
        }

        fragments
    }

    pub fn intersect_convex(&self, cutter: &Self) -> Option<Self> {
        self.clone().intersect_convex_owned(cutter)
    }

    pub fn intersect_convex_owned(self, cutter: &Self) -> Option<Self> {
        if cutter.contains_solid(&self) {
            return Some(self);
        }
        if self.contains_solid(cutter) {
            return Some(cutter.clone_with_material(self.material));
        }

        let mut remainder = self;

        for plane in &cutter.clip_planes {
            remainder = match remainder.split_owned(*plane) {
                SplitResult::Front(_) => return None,
                SplitResult::Back(back) | SplitResult::Coplanar(back) => back,
                SplitResult::Both { back, .. } => back,
            };
        }

        Some(remainder)
    }

    pub fn contains_point(&self, point: Vec3) -> bool {
        self.clip_planes
            .iter()
            .all(|plane| plane.signed_distance(point) <= EPSILON)
    }

    pub fn contains_solid(&self, other: &Self) -> bool {
        other
            .polygons
            .iter()
            .flat_map(|polygon| polygon.vertices.iter())
            .all(|point| self.contains_point(*point))
    }

    pub fn clone_with_material(&self, material: MaterialId) -> Self {
        let mut solid = self.clone();
        solid.material = material;
        for polygon in &mut solid.polygons {
            polygon.material = material;
        }
        solid
    }

    pub fn append_to_mesh(&self, mesh: &mut TriangleMesh) {
        for polygon in &self.polygons {
            if !polygon.visible || polygon.vertices.len() < 3 {
                continue;
            }
            let base = polygon.vertices[0];
            for i in 1..polygon.vertices.len() - 1 {
                let triangle = if polygon.reversed {
                    [base, polygon.vertices[i + 1], polygon.vertices[i]]
                } else {
                    [base, polygon.vertices[i], polygon.vertices[i + 1]]
                };
                let normal = if polygon.reversed {
                    -polygon.normal
                } else {
                    polygon.normal
                };
                mesh.append_triangle(
                    triangle,
                    normal,
                    [Vec2::ZERO, Vec2::X, Vec2::Y],
                    polygon.material,
                );
            }
        }
    }

    pub fn categorize_whole_polygons_against(&mut self, cutter: &Self) {
        for polygon in &mut self.polygons {
            if let Some(category) = whole_polygon_category_against(polygon, cutter) {
                polygon.category = category;
            }
        }
    }

    pub fn categorize_polygons_against(&self, cutter: &Self) -> CategorizedPolygons {
        PolygonRouteScratch::default().categorize_against(&self.polygons, cutter)
    }

    pub fn route_polygons_outside_of(
        polygons: Vec<ConvexPolygon>,
        cutter: &Self,
    ) -> Vec<ConvexPolygon> {
        let categorized = PolygonRouteScratch::default().categorize_against(&polygons, cutter);
        categorized.outside
    }

    pub fn route_polygons_inside_of(
        polygons: Vec<ConvexPolygon>,
        cutter: &Self,
    ) -> Vec<ConvexPolygon> {
        let categorized = PolygonRouteScratch::default().categorize_against(&polygons, cutter);
        let mut inside = categorized.inside;
        inside.extend(categorized.aligned);
        inside.extend(categorized.reverse_aligned);
        inside
    }

    pub fn subtract_convex_routed_polygons(&self, cutter: &Self) -> Vec<ConvexPolygon> {
        let mut polygons = Vec::new();
        let source = self.categorize_polygons_against(cutter);
        polygons.extend(source.outside);

        let cutter_inside_source = cutter.categorize_polygons_against(self);
        for mut polygon in cutter_inside_source.inside {
            polygon.reversed = !polygon.reversed;
            polygons.push(polygon);
        }
        for mut polygon in cutter_inside_source.aligned {
            polygon.reversed = !polygon.reversed;
            polygons.push(polygon);
        }
        for mut polygon in cutter_inside_source.reverse_aligned {
            polygon.reversed = !polygon.reversed;
            polygons.push(polygon);
        }

        polygons
    }

    pub fn append_polygons_to_mesh(polygons: &[ConvexPolygon], mesh: &mut TriangleMesh) {
        for polygon in polygons {
            if !polygon.visible || polygon.vertices.len() < 3 {
                continue;
            }
            let base = polygon.vertices[0];
            for i in 1..polygon.vertices.len() - 1 {
                let triangle = if polygon.reversed {
                    [base, polygon.vertices[i + 1], polygon.vertices[i]]
                } else {
                    [base, polygon.vertices[i], polygon.vertices[i + 1]]
                };
                let normal = if polygon.reversed {
                    -polygon.normal
                } else {
                    polygon.normal
                };
                mesh.append_triangle(
                    triangle,
                    normal,
                    [Vec2::ZERO, Vec2::X, Vec2::Y],
                    polygon.material,
                );
            }
        }
    }

    fn split_owned(self, plane: Plane) -> SplitResult {
        let mut saw_front = false;
        let mut saw_back = false;
        for polygon in &self.polygons {
            for vertex in &polygon.vertices {
                let distance = plane.signed_distance(*vertex);
                saw_front |= distance > EPSILON;
                saw_back |= distance < -EPSILON;
            }
        }

        match (saw_front, saw_back) {
            (true, false) => return SplitResult::Front(self),
            (false, true) => return SplitResult::Back(self),
            (false, false) => return SplitResult::Coplanar(self),
            (true, true) => {}
        }

        let mut front_polygons = Vec::with_capacity(self.polygons.len() + 1);
        let mut back_polygons = Vec::with_capacity(self.polygons.len() + 1);
        let mut cap_points = Vec::with_capacity(self.polygons.len() * 2);

        for polygon in &self.polygons {
            let front = clip_polygon(&polygon.vertices, plane, KeepSide::Front, &mut cap_points);
            if front.len() >= 3 {
                front_polygons.push(polygon.with_vertices(front));
            }

            let back = clip_polygon(&polygon.vertices, plane, KeepSide::Back, &mut cap_points);
            if back.len() >= 3 {
                back_polygons.push(polygon.with_vertices(back));
            }
        }

        let cap_points = unique_points_on_plane(cap_points);
        if cap_points.len() >= 3 {
            front_polygons.push(make_cap_polygon(&cap_points, -plane.normal, self.material));
            back_polygons.push(make_cap_polygon(&cap_points, plane.normal, self.material));
        }

        SplitResult::Both {
            front: Self {
                bounds: bounds_from_polygons(&front_polygons),
                clip_planes: planes_from_polygons(&front_polygons),
                polygons: front_polygons,
                material: self.material,
            },
            back: Self {
                bounds: bounds_from_polygons(&back_polygons),
                clip_planes: planes_from_polygons(&back_polygons),
                polygons: back_polygons,
                material: self.material,
            },
        }
    }
}

impl PolygonRouteScratch {
    pub fn categorize_against(
        &mut self,
        polygons: &[ConvexPolygon],
        cutter: &ConvexSolid,
    ) -> CategorizedPolygons {
        let mut categorized = CategorizedPolygons::default();

        for polygon in polygons {
            self.pieces.clear();
            self.pieces.push(CategorizationPiece {
                polygon: polygon.clone(),
                aligned_category: None,
            });

            for plane in &cutter.clip_planes {
                self.inside_pieces.clear();
                for piece in self.pieces.drain(..) {
                    classify_piece_against_plane(
                        piece,
                        *plane,
                        &mut self.inside_pieces,
                        &mut categorized,
                        &mut self.cap_points,
                    );
                }
                std::mem::swap(&mut self.pieces, &mut self.inside_pieces);
            }

            for piece in self.pieces.drain(..) {
                match piece.aligned_category {
                    Some(PolygonCategory::Aligned) => categorized.aligned.push(piece.polygon),
                    Some(PolygonCategory::ReverseAligned) => {
                        categorized.reverse_aligned.push(piece.polygon);
                    }
                    _ => categorized.inside.push(piece.polygon),
                }
            }
        }

        categorized
    }

    pub fn route_outside_of(
        &mut self,
        polygons: Vec<ConvexPolygon>,
        cutter: &ConvexSolid,
    ) -> Vec<ConvexPolygon> {
        self.categorize_against(&polygons, cutter).outside
    }

    pub fn route_inside_of(
        &mut self,
        polygons: Vec<ConvexPolygon>,
        cutter: &ConvexSolid,
    ) -> Vec<ConvexPolygon> {
        let categorized = self.categorize_against(&polygons, cutter);
        let mut inside = categorized.inside;
        inside.extend(categorized.aligned);
        inside.extend(categorized.reverse_aligned);
        inside
    }
}

enum SplitResult {
    Front(ConvexSolid),
    Back(ConvexSolid),
    Both {
        front: ConvexSolid,
        back: ConvexSolid,
    },
    Coplanar(ConvexSolid),
}

struct CategorizationPiece {
    polygon: ConvexPolygon,
    aligned_category: Option<PolygonCategory>,
}

#[derive(Clone, Copy)]
enum KeepSide {
    Front,
    Back,
}

fn classify_piece_against_plane(
    mut piece: CategorizationPiece,
    plane: Plane,
    inside_pieces: &mut Vec<CategorizationPiece>,
    categorized: &mut CategorizedPolygons,
    cap_points: &mut Vec<Vec3>,
) {
    let piece_plane = piece
        .polygon
        .vertices
        .first()
        .map(|point| Plane::from_point_normal(*point, piece.polygon.normal));

    if piece_plane.is_some_and(|piece_plane| piece_plane.is_coplanar_with(plane)) {
        piece.aligned_category = Some(if piece.polygon.normal.dot(plane.normal) >= 0.0 {
            PolygonCategory::Aligned
        } else {
            PolygonCategory::ReverseAligned
        });
        inside_pieces.push(piece);
        return;
    }

    let mut saw_front = false;
    let mut saw_back = false;
    for vertex in &piece.polygon.vertices {
        let distance = plane.signed_distance(*vertex);
        saw_front |= distance > EPSILON;
        saw_back |= distance < -EPSILON;
    }

    match (saw_front, saw_back) {
        (true, false) => {
            let mut polygon = piece.polygon;
            polygon.category = PolygonCategory::Outside;
            categorized.outside.push(polygon);
        }
        (false, true) | (false, false) => inside_pieces.push(piece),
        (true, true) => {
            cap_points.clear();
            let outside = clip_polygon(&piece.polygon.vertices, plane, KeepSide::Front, cap_points);
            if outside.len() >= 3 {
                let mut outside_polygon = piece.polygon.with_vertices(outside);
                outside_polygon.category = PolygonCategory::Outside;
                categorized.outside.push(outside_polygon);
            }

            let inside = clip_polygon(&piece.polygon.vertices, plane, KeepSide::Back, cap_points);
            if inside.len() >= 3 {
                inside_pieces.push(CategorizationPiece {
                    polygon: piece.polygon.with_vertices(inside),
                    aligned_category: piece.aligned_category,
                });
            }
        }
    }
}

fn clip_polygon(
    vertices: &[Vec3],
    plane: Plane,
    keep: KeepSide,
    cap_points: &mut Vec<Vec3>,
) -> Vec<Vec3> {
    let mut output = Vec::with_capacity(vertices.len() + 1);
    if vertices.is_empty() {
        return output;
    }

    let mut previous = *vertices.last().expect("checked non-empty");
    let mut previous_distance = plane.signed_distance(previous);
    let mut previous_inside = inside(previous_distance, keep);

    for &current in vertices {
        let current_distance = plane.signed_distance(current);
        let current_inside = inside(current_distance, keep);

        if current_inside != previous_inside {
            let t = previous_distance / (previous_distance - current_distance);
            let intersection = previous + (current - previous) * t;
            output.push(intersection);
            cap_points.push(intersection);
        }

        if current_inside {
            output.push(current);
        }

        previous = current;
        previous_distance = current_distance;
        previous_inside = current_inside;
    }

    output
}

fn inside(distance: f32, keep: KeepSide) -> bool {
    match keep {
        KeepSide::Front => distance >= -EPSILON,
        KeepSide::Back => distance <= EPSILON,
    }
}

fn make_cap_polygon(points: &[Vec3], normal: Vec3, material: MaterialId) -> ConvexPolygon {
    let center = points.iter().copied().sum::<Vec3>() / points.len() as f32;
    let axis = if normal.z.abs() < 0.9 {
        Vec3::Z
    } else {
        Vec3::Y
    };
    let tangent = normal.cross(axis).normalize_or_zero();
    let bitangent = normal.cross(tangent).normalize_or_zero();
    let mut vertices = points.to_vec();

    vertices.sort_by(|a, b| {
        let da = *a - center;
        let db = *b - center;
        let aa = da.dot(bitangent).atan2(da.dot(tangent));
        let ab = db.dot(bitangent).atan2(db.dot(tangent));
        aa.total_cmp(&ab)
    });

    if polygon_normal(&vertices).dot(normal) < 0.0 {
        vertices.reverse();
    }

    ConvexPolygon::new(vertices, normal, material)
}

fn unique_points_on_plane(points: Vec<Vec3>) -> Vec<Vec3> {
    let mut unique = Vec::new();
    'outer: for point in points {
        for seen in &unique {
            let delta: Vec3 = point - *seen;
            if delta.length_squared() < EPSILON * EPSILON {
                continue 'outer;
            }
        }
        unique.push(point);
    }
    unique
}

fn planes_from_polygons(polygons: &[ConvexPolygon]) -> Vec<Plane> {
    polygons
        .iter()
        .filter_map(|polygon| {
            polygon
                .vertices
                .first()
                .map(|point| Plane::from_point_normal(*point, polygon.normal))
        })
        .collect()
}

fn bounds_from_polygons(polygons: &[ConvexPolygon]) -> Aabb {
    polygons.iter().fold(Aabb::empty(), |bounds, polygon| {
        bounds.union(polygon.bounds)
    })
}

fn whole_polygon_category_against(
    polygon: &ConvexPolygon,
    cutter: &ConvexSolid,
) -> Option<PolygonCategory> {
    let polygon_plane = polygon
        .vertices
        .first()
        .map(|point| Plane::from_point_normal(*point, polygon.normal))?;

    for cutter_plane in &cutter.clip_planes {
        if polygon_plane.is_coplanar_with(*cutter_plane)
            && polygon
                .vertices
                .iter()
                .all(|vertex| cutter_plane.signed_distance(*vertex).abs() <= EPSILON)
        {
            return Some(if polygon.normal.dot(cutter_plane.normal) >= 0.0 {
                PolygonCategory::Aligned
            } else {
                PolygonCategory::ReverseAligned
            });
        }
    }

    let mut fully_inside = true;
    for cutter_plane in &cutter.clip_planes {
        let mut all_outside_this_plane = true;
        for vertex in &polygon.vertices {
            let distance = cutter_plane.signed_distance(*vertex);
            if distance > EPSILON {
                fully_inside = false;
            } else {
                all_outside_this_plane = false;
            }
        }
        if all_outside_this_plane {
            return Some(PolygonCategory::Outside);
        }
    }

    fully_inside.then_some(PolygonCategory::Inside)
}

fn polygon_normal(vertices: &[Vec3]) -> Vec3 {
    if vertices.len() < 3 {
        return Vec3::ZERO;
    }
    (vertices[1] - vertices[0])
        .cross(vertices[2] - vertices[0])
        .normalize_or_zero()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn assert_box_parity(solid: &ConvexSolid, material: MaterialId) {
        assert_eq!(solid.polygons.len(), 6);
        assert_eq!(solid.clip_planes.len(), 6);
        assert!(
            solid
                .polygons
                .iter()
                .all(|polygon| polygon.vertices.len() == 4)
        );
        assert!(
            solid
                .polygons
                .iter()
                .all(|polygon| polygon.material == material)
        );
    }

    #[test]
    fn parity_box_brush_from_planes_has_six_quad_polygons() {
        let solid = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(2.0),
            Quat::IDENTITY,
            MaterialId(9),
        );

        assert_box_parity(&solid, MaterialId(9));
    }

    #[test]
    fn subtract_center_box_emits_six_convex_fragments() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(2.0),
            Quat::IDENTITY,
            MaterialId(0),
        );

        let fragments = source.subtract_convex(&cutter);
        assert_eq!(fragments.len(), 6);
        assert!(
            fragments
                .iter()
                .all(|fragment| !fragment.polygons.is_empty())
        );
    }

    #[test]
    fn intersect_overlapping_boxes_emits_single_common_solid() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::X,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(0),
        );

        let common = source.intersect_convex(&cutter).expect("intersection");

        assert!(common.bounds.is_valid());
        assert_eq!(common.polygons.len(), 6);
        assert!(common.bounds.contains_point(Vec3::ZERO));
        assert!(common.bounds.contains_point(Vec3::new(2.0, 0.0, 0.0)));
        assert!(!common.bounds.contains_point(Vec3::new(-2.0, 0.0, 0.0)));
    }

    #[test]
    fn categorize_crossing_coplanar_face_splits_visible_categories() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::new(0.0, 0.0, 1.0),
            Vec3::new(2.0, 2.0, 2.0),
            Quat::IDENTITY,
            MaterialId(0),
        );

        let categorized = source.categorize_polygons_against(&cutter);

        assert_eq!(categorized.aligned.len(), 1);
        assert!(categorized.outside.len() > 6);
        assert!(categorized.inside.is_empty());
        assert!(categorized.reverse_aligned.is_empty());
        assert!(
            categorized
                .aligned
                .iter()
                .all(|polygon| polygon.category == PolygonCategory::Aligned)
        );
        assert!(
            categorized
                .outside
                .iter()
                .all(|polygon| polygon.category == PolygonCategory::Outside)
        );
    }

    #[test]
    fn subtract_rotated_box_creates_non_axis_aligned_faces() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::new(2.0, 5.0, 2.0),
            Quat::from_rotation_z(std::f32::consts::FRAC_PI_4),
            MaterialId(0),
        );

        let fragments = source.subtract_convex(&cutter);
        assert!(fragments.len() > 2);
        assert!(fragments.iter().any(|fragment| {
            fragment
                .polygons
                .iter()
                .any(|polygon| polygon.normal.x.abs() > 0.01 && polygon.normal.y.abs() > 0.01)
        }));
    }

    #[test]
    fn routed_subtraction_emits_source_shell_and_reversed_cutter_shell() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(2.0),
            Quat::IDENTITY,
            MaterialId(0),
        );

        let routed = source.subtract_convex_routed_polygons(&cutter);

        assert_eq!(routed.len(), 24);
        assert_eq!(
            routed
                .iter()
                .filter(|polygon| polygon.material == MaterialId(1))
                .count(),
            18
        );
        assert_eq!(
            routed
                .iter()
                .filter(|polygon| polygon.material == MaterialId(0))
                .count(),
            6
        );
        assert_eq!(
            routed
                .iter()
                .filter(|polygon| polygon.material == MaterialId(0) && polygon.reversed)
                .count(),
            6
        );
    }

    #[test]
    fn routed_subtraction_clips_crossing_cutter_surfaces_to_source() {
        let source = ConvexSolid::box_from_center_size(
            Vec3::ZERO,
            Vec3::splat(4.0),
            Quat::IDENTITY,
            MaterialId(1),
        );
        let cutter = ConvexSolid::box_from_center_size(
            Vec3::new(1.5, 0.0, 0.0),
            Vec3::splat(2.0),
            Quat::IDENTITY,
            MaterialId(0),
        );

        let routed = source.subtract_convex_routed_polygons(&cutter);

        assert!(!routed.is_empty());
        assert!(
            routed
                .iter()
                .flat_map(|polygon| polygon.vertices.iter())
                .all(|vertex| source.bounds.contains_point(*vertex))
        );
        assert!(
            routed
                .iter()
                .any(|polygon| polygon.material == MaterialId(0) && polygon.reversed)
        );
    }
}
