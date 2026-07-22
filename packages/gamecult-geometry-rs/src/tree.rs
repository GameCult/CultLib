#![allow(dead_code)]
use crate::{Aabb, Assembler, Brush, BrushId, BrushOp, MaterialId, Primitive};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct CsgNodeId(pub u32);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct CsgTreeBrush {
    pub node: CsgNodeId,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct CsgTreeBranch {
    pub node: CsgNodeId,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct CsgTree {
    pub root: CsgNodeId,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CsgOperationType {
    Additive = 0,
    Subtractive = 1,
    Intersecting = 2,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum CsgBranchOp {
    Addition,
    Subtraction,
    Common,
}

#[derive(Clone, Debug)]
pub enum CsgNode {
    Brush(Brush),
    Branch {
        name: String,
        op: CsgBranchOp,
        children: Vec<CsgNodeId>,
    },
}

#[derive(Clone, Debug, Default)]
pub struct CsgTreeArena {
    nodes: Vec<CsgNode>,
}

impl CsgTreeArena {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn nodes(&self) -> &[CsgNode] {
        &self.nodes
    }

    pub fn node(&self, id: CsgNodeId) -> Option<&CsgNode> {
        self.nodes.get(id.0 as usize)
    }

    pub fn node_mut(&mut self, id: CsgNodeId) -> Option<&mut CsgNode> {
        self.nodes.get_mut(id.0 as usize)
    }

    pub fn brush_count(&self) -> usize {
        self.nodes
            .iter()
            .filter(|node| matches!(node, CsgNode::Brush(_)))
            .count()
    }

    pub fn branch_count(&self) -> usize {
        self.nodes
            .iter()
            .filter(|node| matches!(node, CsgNode::Branch { .. }))
            .count()
    }

    pub fn generate_brush(
        &mut self,
        name: impl Into<String>,
        operation: CsgOperationType,
        primitive: Primitive,
        material: MaterialId,
    ) -> CsgTreeBrush {
        let id = CsgNodeId(self.nodes.len() as u32);
        let brush = Brush::new(
            BrushId(id.0),
            name,
            operation.into_brush_op(),
            primitive,
            material,
        );
        self.nodes.push(CsgNode::Brush(brush));
        CsgTreeBrush { node: id }
    }

    pub fn generate_branch(
        &mut self,
        name: impl Into<String>,
        op: CsgBranchOp,
        children: impl IntoIterator<Item = CsgNodeId>,
    ) -> CsgTreeBranch {
        let id = CsgNodeId(self.nodes.len() as u32);
        self.nodes.push(CsgNode::Branch {
            name: name.into(),
            op,
            children: children.into_iter().collect(),
        });
        CsgTreeBranch { node: id }
    }

    pub fn generate_tree(&self, root: CsgNodeId) -> CsgTree {
        CsgTree { root }
    }

    pub fn set_brush_operation_type(
        &mut self,
        brush: CsgTreeBrush,
        operation: CsgOperationType,
    ) -> bool {
        let Some(CsgNode::Brush(node)) = self.node_mut(brush.node) else {
            return false;
        };
        node.op = operation.into_brush_op();
        node.set_dirty();
        true
    }

    pub fn set_branch_operation_type(&mut self, branch: CsgTreeBranch, op: CsgBranchOp) -> bool {
        let Some(CsgNode::Branch { op: current_op, .. }) = self.node_mut(branch.node) else {
            return false;
        };
        *current_op = op;
        true
    }

    pub fn set_child_nodes(
        &mut self,
        branch: CsgTreeBranch,
        children: impl IntoIterator<Item = CsgNodeId>,
    ) -> bool {
        let Some(CsgNode::Branch {
            children: current_children,
            ..
        }) = self.node_mut(branch.node)
        else {
            return false;
        };
        *current_children = children.into_iter().collect();
        true
    }

    pub fn child_nodes(&self, branch: CsgTreeBranch) -> Option<&[CsgNodeId]> {
        let Some(CsgNode::Branch { children, .. }) = self.node(branch.node) else {
            return None;
        };
        Some(children)
    }

    pub fn node_bounds(&self, id: CsgNodeId) -> Option<Aabb> {
        match self.node(id)? {
            CsgNode::Brush(brush) => Some(brush.bounds()),
            CsgNode::Branch { op, children, .. } => self.branch_bounds(*op, children),
        }
    }

    pub fn compile_tree_to_assembler(&self, tree: CsgTree) -> Assembler {
        let mut assembler = Assembler::new();
        self.compile_node(tree.root, &mut assembler, None);
        assembler
    }

    fn compile_node(
        &self,
        id: CsgNodeId,
        assembler: &mut Assembler,
        operation_override: Option<CsgOperationType>,
    ) {
        let Some(node) = self.node(id) else {
            return;
        };

        match node {
            CsgNode::Brush(brush) => {
                let op = operation_override
                    .map(CsgOperationType::into_brush_op)
                    .unwrap_or(brush.op);
                assembler.add_brush(
                    brush.name.clone(),
                    op,
                    brush.primitive.clone(),
                    brush.material,
                );
            }
            CsgNode::Branch { op, children, .. } => match op {
                CsgBranchOp::Addition => {
                    for child in children {
                        self.compile_node(*child, assembler, operation_override);
                    }
                }
                CsgBranchOp::Subtraction => {
                    if let Some((first, rest)) = children.split_first() {
                        self.compile_node(*first, assembler, operation_override);
                        for child in rest {
                            self.compile_node(
                                *child,
                                assembler,
                                Some(CsgOperationType::Subtractive),
                            );
                        }
                    }
                }
                CsgBranchOp::Common => {
                    if let Some((first, rest)) = children.split_first() {
                        self.compile_node(*first, assembler, operation_override);
                        for child in rest {
                            self.compile_node(
                                *child,
                                assembler,
                                Some(CsgOperationType::Intersecting),
                            );
                        }
                    }
                }
            },
        }
    }

    fn branch_bounds(&self, op: CsgBranchOp, children: &[CsgNodeId]) -> Option<Aabb> {
        let mut child_bounds = children.iter().filter_map(|child| self.node_bounds(*child));
        match op {
            CsgBranchOp::Addition => child_bounds.reduce(Aabb::union),
            CsgBranchOp::Subtraction => child_bounds.next(),
            CsgBranchOp::Common => {
                let first = child_bounds.next()?;
                child_bounds.try_fold(first, |bounds, child| bounds.intersection(child))
            }
        }
    }
}

impl CsgOperationType {
    pub fn into_brush_op(self) -> BrushOp {
        match self {
            Self::Additive => BrushOp::Add,
            Self::Subtractive => BrushOp::Subtract,
            Self::Intersecting => BrushOp::Intersect,
        }
    }
}

impl From<BrushOp> for CsgOperationType {
    fn from(value: BrushOp) -> Self {
        match value {
            BrushOp::Add => Self::Additive,
            BrushOp::Subtract => Self::Subtractive,
            BrushOp::Intersect => Self::Intersecting,
        }
    }
}

#[cfg(test)]
mod tests {
    use bevy_math::Vec3;

    use super::*;
    use crate::{Aabb, Primitive};

    #[test]
    fn tree_subtraction_compiles_to_ordered_brush_stream() {
        let mut arena = CsgTreeArena::new();
        let solid = arena.generate_brush(
            "solid",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );
        let void = arena.generate_brush(
            "void",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            },
            MaterialId(0),
        );
        let branch = arena.generate_branch(
            "solid minus void",
            CsgBranchOp::Subtraction,
            [solid.node, void.node],
        );
        let assembler = arena.compile_tree_to_assembler(arena.generate_tree(branch.node));

        assert_eq!(assembler.brushes().len(), 2);
        assert_eq!(assembler.brushes()[0].op, BrushOp::Add);
        assert_eq!(assembler.brushes()[1].op, BrushOp::Subtract);
        assert_eq!(assembler.build().report.emitted_convex_fragments, 6);
    }

    #[test]
    fn tree_edit_api_replaces_children_and_operations() {
        let mut arena = CsgTreeArena::new();
        let a = arena.generate_brush(
            "a",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(2.0)),
            },
            MaterialId(1),
        );
        let b = arena.generate_brush(
            "b",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::X, Vec3::splat(1.0)),
            },
            MaterialId(1),
        );
        let branch = arena.generate_branch("branch", CsgBranchOp::Addition, [a.node]);

        assert!(arena.set_child_nodes(branch, [a.node, b.node]));
        assert_eq!(arena.child_nodes(branch).expect("branch").len(), 2);
        assert!(arena.set_branch_operation_type(branch, CsgBranchOp::Subtraction));
        assert!(arena.set_brush_operation_type(b, CsgOperationType::Subtractive));
        assert_eq!(arena.brush_count(), 2);
        assert_eq!(arena.branch_count(), 1);
    }

    #[test]
    fn tree_bounds_follow_boolean_branch_shape() {
        let mut arena = CsgTreeArena::new();
        let a = arena.generate_brush(
            "a",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );
        let b = arena.generate_brush(
            "b",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::X * 2.5, Vec3::splat(2.0)),
            },
            MaterialId(1),
        );
        let union = arena.generate_branch("union", CsgBranchOp::Addition, [a.node, b.node]);
        let common = arena.generate_branch("common", CsgBranchOp::Common, [a.node, b.node]);
        let subtract =
            arena.generate_branch("subtract", CsgBranchOp::Subtraction, [a.node, b.node]);

        assert_eq!(
            arena.node_bounds(union.node).expect("union"),
            Aabb::new(Vec3::splat(-2.0), Vec3::new(3.5, 2.0, 2.0))
        );
        assert_eq!(
            arena.node_bounds(common.node).expect("common"),
            Aabb::new(Vec3::new(1.5, -1.0, -1.0), Vec3::new(2.0, 1.0, 1.0))
        );
        assert_eq!(
            arena.node_bounds(subtract.node).expect("subtract"),
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0))
        );
    }

    #[test]
    fn tree_common_compiles_to_additive_source_then_intersections() {
        let mut arena = CsgTreeArena::new();
        let a = arena.generate_brush(
            "a",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );
        let b = arena.generate_brush(
            "b",
            CsgOperationType::Additive,
            Primitive::Box {
                bounds: Aabb::from_center_size(Vec3::X, Vec3::splat(4.0)),
            },
            MaterialId(1),
        );
        let branch = arena.generate_branch("common", CsgBranchOp::Common, [a.node, b.node]);

        let assembler = arena.compile_tree_to_assembler(arena.generate_tree(branch.node));
        let output = assembler.build();

        assert_eq!(assembler.brushes()[0].op, BrushOp::Add);
        assert_eq!(assembler.brushes()[1].op, BrushOp::Intersect);
        assert_eq!(output.report.emitted_convex_fragments, 1);
        assert_eq!(output.report.warnings.len(), 0);
        assert_eq!(output.mesh.triangle_count(), 12);
    }
}
