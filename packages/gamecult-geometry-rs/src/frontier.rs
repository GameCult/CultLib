#![allow(dead_code)]
use crate::{Brush, BrushOp};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct DemandPair {
    pub source_index: usize,
    pub operator_index: usize,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct DemandFrontier {
    pub pairs: Vec<DemandPair>,
    pub rejected_pairs: usize,
    pub operator_brushes: usize,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct DirtyDemandFrontier {
    pub first_dirty_index: Option<usize>,
    pub pairs: Vec<DemandPair>,
    pub rejected_pairs: usize,
    pub operator_brushes: usize,
    pub skipped_prefix_pairs: usize,
}

impl DemandFrontier {
    pub fn from_ordered_brushes(brushes: &[Brush]) -> Self {
        let mut frontier = Self::default();
        let mut active_sources = Vec::<usize>::new();

        for (operator_index, brush) in brushes.iter().enumerate() {
            match brush.op {
                BrushOp::Add => active_sources.push(operator_index),
                BrushOp::Subtract => {
                    frontier.operator_brushes += 1;
                    frontier.collect_pairs(brushes, &active_sources, operator_index);
                }
                BrushOp::Intersect => {
                    frontier.operator_brushes += 1;
                    let retained = frontier.collect_pairs(brushes, &active_sources, operator_index);
                    active_sources = retained;
                }
            }
        }

        frontier
    }

    pub fn candidate_pairs(&self) -> usize {
        self.pairs.len()
    }

    fn collect_pairs(
        &mut self,
        brushes: &[Brush],
        active_sources: &[usize],
        operator_index: usize,
    ) -> Vec<usize> {
        let operator_bounds = brushes[operator_index].bounds();
        let mut retained = Vec::new();

        for &source_index in active_sources {
            if brushes[source_index].bounds().intersects(operator_bounds) {
                self.pairs.push(DemandPair {
                    source_index,
                    operator_index,
                });
                retained.push(source_index);
            } else {
                self.rejected_pairs += 1;
            }
        }

        retained
    }
}

impl DirtyDemandFrontier {
    pub fn from_dirty_indices(
        brushes: &[Brush],
        dirty_indices: impl IntoIterator<Item = usize>,
    ) -> Self {
        let first_dirty_index = dirty_indices
            .into_iter()
            .filter(|index| *index < brushes.len())
            .min();
        let Some(first_dirty_index) = first_dirty_index else {
            return Self::default();
        };

        let mut frontier = Self {
            first_dirty_index: Some(first_dirty_index),
            ..Self::default()
        };
        let mut active_sources = Vec::<usize>::new();

        for (operator_index, brush) in brushes.iter().enumerate() {
            match brush.op {
                BrushOp::Add => active_sources.push(operator_index),
                BrushOp::Subtract => {
                    frontier.operator_brushes += usize::from(operator_index >= first_dirty_index);
                    frontier.collect_pairs(
                        brushes,
                        &active_sources,
                        operator_index,
                        first_dirty_index,
                    );
                }
                BrushOp::Intersect => {
                    frontier.operator_brushes += usize::from(operator_index >= first_dirty_index);
                    let retained = frontier.collect_pairs(
                        brushes,
                        &active_sources,
                        operator_index,
                        first_dirty_index,
                    );
                    active_sources = retained;
                }
            }
        }

        frontier
    }

    pub fn candidate_pairs(&self) -> usize {
        self.pairs.len()
    }

    fn collect_pairs(
        &mut self,
        brushes: &[Brush],
        active_sources: &[usize],
        operator_index: usize,
        first_dirty_index: usize,
    ) -> Vec<usize> {
        let operator_bounds = brushes[operator_index].bounds();
        let mut retained = Vec::new();

        for &source_index in active_sources {
            if brushes[source_index].bounds().intersects(operator_bounds) {
                if operator_index >= first_dirty_index {
                    self.pairs.push(DemandPair {
                        source_index,
                        operator_index,
                    });
                } else {
                    self.skipped_prefix_pairs += 1;
                }
                retained.push(source_index);
            } else if operator_index >= first_dirty_index {
                self.rejected_pairs += 1;
            } else {
                self.skipped_prefix_pairs += 1;
            }
        }

        retained
    }
}

#[cfg(test)]
mod tests {
    use bevy_math::{Quat, Vec3};

    use super::*;
    use crate::{Aabb, Brush, MaterialId, Primitive};

    fn brush(name: &str, op: BrushOp, bounds: Aabb) -> Brush {
        Brush::new(
            crate::BrushId(0),
            name,
            op,
            Primitive::Box { bounds },
            MaterialId(1),
        )
    }

    #[test]
    fn frontier_keeps_only_touching_ordered_pairs() {
        let brushes = vec![
            brush(
                "source",
                BrushOp::Add,
                Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            ),
            brush(
                "far cutter",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::splat(20.0), Vec3::ONE),
            ),
            brush(
                "near cutter",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::ZERO, Vec3::ONE),
            ),
        ];

        let frontier = DemandFrontier::from_ordered_brushes(&brushes);

        assert_eq!(frontier.operator_brushes, 2);
        assert_eq!(frontier.rejected_pairs, 1);
        assert_eq!(
            frontier.pairs,
            vec![DemandPair {
                source_index: 0,
                operator_index: 2,
            }]
        );
    }

    #[test]
    fn intersection_frontier_reduces_active_sources() {
        let brushes = vec![
            brush(
                "a",
                BrushOp::Add,
                Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            ),
            brush(
                "b",
                BrushOp::Add,
                Aabb::from_center_size(Vec3::new(20.0, 0.0, 0.0), Vec3::splat(2.0)),
            ),
            Brush::new(
                crate::BrushId(2),
                "common",
                BrushOp::Intersect,
                Primitive::OrientedBox {
                    center: Vec3::ZERO,
                    size: Vec3::splat(3.0),
                    rotation: Quat::IDENTITY,
                },
                MaterialId(1),
            ),
            brush(
                "cut",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::ZERO, Vec3::ONE),
            ),
        ];

        let frontier = DemandFrontier::from_ordered_brushes(&brushes);

        assert_eq!(frontier.operator_brushes, 2);
        assert_eq!(frontier.rejected_pairs, 1);
        assert_eq!(
            frontier.pairs,
            vec![
                DemandPair {
                    source_index: 0,
                    operator_index: 2,
                },
                DemandPair {
                    source_index: 0,
                    operator_index: 3,
                },
            ]
        );
    }

    #[test]
    fn dirty_frontier_preserves_prefix_before_first_dirty_brush() {
        let brushes = vec![
            brush(
                "source",
                BrushOp::Add,
                Aabb::from_center_size(Vec3::ZERO, Vec3::splat(8.0)),
            ),
            brush(
                "early",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::new(-2.0, 0.0, 0.0), Vec3::splat(2.0)),
            ),
            brush(
                "dirty",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            ),
            brush(
                "later",
                BrushOp::Subtract,
                Aabb::from_center_size(Vec3::new(2.0, 0.0, 0.0), Vec3::splat(2.0)),
            ),
        ];

        let frontier = DirtyDemandFrontier::from_dirty_indices(&brushes, [2]);

        assert_eq!(frontier.first_dirty_index, Some(2));
        assert_eq!(frontier.operator_brushes, 2);
        assert_eq!(frontier.skipped_prefix_pairs, 1);
        assert_eq!(frontier.candidate_pairs(), 2);
        assert_eq!(
            frontier.pairs,
            vec![
                DemandPair {
                    source_index: 0,
                    operator_index: 2,
                },
                DemandPair {
                    source_index: 0,
                    operator_index: 3,
                },
            ]
        );
    }

    #[test]
    fn dirty_frontier_ignores_out_of_range_dirty_indices() {
        let brushes = vec![brush(
            "source",
            BrushOp::Add,
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
        )];

        let frontier = DirtyDemandFrontier::from_dirty_indices(&brushes, [99]);

        assert_eq!(frontier.first_dirty_index, None);
        assert_eq!(frontier.candidate_pairs(), 0);
        assert_eq!(frontier.operator_brushes, 0);
    }
}
