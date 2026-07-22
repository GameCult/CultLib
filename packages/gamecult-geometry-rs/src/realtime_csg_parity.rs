use crate::{
    Aabb, Assembler, ConvexSolid, CsgBranchOp, CsgOperationType, CsgTreeArena, MaterialId,
    PolygonCategory, Primitive,
};
use bevy_math::{Quat, Vec3};

#[test]
fn parity_polygon_category_vocabulary_matches_public_demo() {
    let categories = [
        PolygonCategory::Inside,
        PolygonCategory::Outside,
        PolygonCategory::Aligned,
        PolygonCategory::ReverseAligned,
    ];

    assert_eq!(categories.len(), 4);
}

#[test]
fn parity_operation_type_ordinals_match_public_api() {
    assert_eq!(CsgOperationType::Additive as u8, 0);
    assert_eq!(CsgOperationType::Subtractive as u8, 1);
    assert_eq!(CsgOperationType::Intersecting as u8, 2);
}

#[test]
fn parity_demo_branch_vocabulary_matches_public_demo() {
    let branches = [
        CsgBranchOp::Addition,
        CsgBranchOp::Subtraction,
        CsgBranchOp::Common,
    ];

    assert_eq!(branches.len(), 3);
}

#[test]
fn parity_tree_api_exposes_brush_branch_tree_handles() {
    let mut arena = CsgTreeArena::new();
    let brush = arena.generate_brush(
        "brush",
        CsgOperationType::Additive,
        Primitive::Box {
            bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::ONE),
        },
        MaterialId(1),
    );
    let branch = arena.generate_branch("branch", CsgBranchOp::Addition, [brush.node]);
    let tree = arena.generate_tree(branch.node);

    assert_eq!(arena.brush_count(), 1);
    assert_eq!(arena.branch_count(), 1);
    assert_eq!(arena.child_nodes(branch).expect("branch"), &[brush.node]);
    assert_eq!(tree.root, branch.node);
}

#[test]
fn parity_box_brush_observable_counts_match_realtime_csg_demo() {
    let solid = ConvexSolid::box_from_center_size(
        Vec3::ZERO,
        Vec3::splat(2.0),
        Quat::IDENTITY,
        MaterialId(3),
    );

    assert_eq!(solid.clip_planes.len(), 6);
    assert_eq!(solid.polygons.len(), 6);
    assert_eq!(
        solid
            .polygons
            .iter()
            .map(|polygon| polygon.vertices.len())
            .sum::<usize>(),
        24
    );
}

#[test]
fn parity_center_box_subtraction_has_six_surviving_regions() {
    let mut asm = Assembler::new();
    asm.solid_box(
        "source",
        Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
        MaterialId(1),
    );
    asm.cut_box("void", Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)));

    let output = asm.build();
    assert_eq!(output.report.emitted_convex_fragments, 6);
    assert_eq!(output.report.warnings.len(), 0);
}

#[test]
fn parity_identical_box_polygons_classify_as_aligned() {
    let cutter = ConvexSolid::box_from_center_size(
        Vec3::ZERO,
        Vec3::splat(2.0),
        Quat::IDENTITY,
        MaterialId(0),
    );
    let mut source = ConvexSolid::box_from_center_size(
        Vec3::ZERO,
        Vec3::splat(2.0),
        Quat::IDENTITY,
        MaterialId(1),
    );

    source.categorize_whole_polygons_against(&cutter);

    assert_eq!(source.polygons.len(), 6);
    assert!(
        source
            .polygons
            .iter()
            .all(|polygon| polygon.category == PolygonCategory::Aligned)
    );
}

#[test]
fn parity_outer_box_polygons_classify_as_outside_inner_box() {
    let cutter = ConvexSolid::box_from_center_size(
        Vec3::ZERO,
        Vec3::splat(2.0),
        Quat::IDENTITY,
        MaterialId(0),
    );
    let mut source = ConvexSolid::box_from_center_size(
        Vec3::ZERO,
        Vec3::splat(4.0),
        Quat::IDENTITY,
        MaterialId(1),
    );

    source.categorize_whole_polygons_against(&cutter);

    assert_eq!(source.polygons.len(), 6);
    assert!(
        source
            .polygons
            .iter()
            .all(|polygon| polygon.category == PolygonCategory::Outside)
    );
}

#[test]
fn parity_crossing_polygons_split_before_final_category() {
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
    assert!(categorized.outside.len() > source.polygons.len());
    assert_eq!(categorized.inside.len(), 0);
    assert_eq!(categorized.reverse_aligned.len(), 0);
}
