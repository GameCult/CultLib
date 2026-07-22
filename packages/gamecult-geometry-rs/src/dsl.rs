#![allow(dead_code)]
use bevy_math::{Quat, Vec3};

use crate::{
    Aabb, Assembler, BrushId, BrushOp, BuildOutput, DomeCapZSpec, FloretArmSpec, MaterialId,
    Primitive,
};

#[derive(Clone, Debug, Default)]
pub struct LevelDsl {
    assembler: Assembler,
}

impl LevelDsl {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn solid_box(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        size: Vec3,
        material: MaterialId,
    ) -> BrushId {
        self.assembler
            .solid_box(name, Aabb::from_center_size(center, size), material)
    }

    pub fn cut_box(&mut self, name: impl Into<String>, center: Vec3, size: Vec3) -> BrushId {
        self.assembler
            .cut_box(name, Aabb::from_center_size(center, size))
    }

    pub fn solid_oriented_box(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        size: Vec3,
        rotation: Quat,
        material: MaterialId,
    ) -> BrushId {
        self.assembler
            .solid_oriented_box(name, center, size, rotation, material)
    }

    pub fn cut_oriented_box(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        size: Vec3,
        rotation: Quat,
    ) -> BrushId {
        self.assembler
            .cut_oriented_box(name, center, size, rotation)
    }

    pub fn cylinder_z(
        &mut self,
        name: impl Into<String>,
        center: Vec3,
        radius: f32,
        depth: f32,
        segments: usize,
        material: MaterialId,
    ) -> BrushId {
        self.assembler.add_brush(
            name,
            BrushOp::Add,
            Primitive::CylinderZ {
                center,
                radius,
                depth,
                segments,
            },
            material,
        )
    }

    pub fn dome_cap_z(&mut self, name: impl Into<String>, spec: DomeCapZSpec) -> BrushId {
        self.assembler.add_brush(
            name,
            BrushOp::Add,
            Primitive::DomeCapZ {
                center: spec.center,
                radius: spec.radius,
                height: spec.height,
                rings: spec.rings,
                segments: spec.segments,
            },
            spec.material,
        )
    }

    pub fn floret_arm(&mut self, name: impl Into<String>, spec: FloretArmSpec) -> BrushId {
        self.assembler.add_brush(
            name,
            BrushOp::Add,
            Primitive::FloretArm {
                anchor: spec.anchor,
                direction: spec.direction,
                length: spec.length,
                root_width: spec.root_width,
                tip_width: spec.tip_width,
                thickness: spec.thickness,
                tip_lift: spec.tip_lift,
            },
            spec.material,
        )
    }

    pub fn assemble(&self) -> BuildOutput {
        self.assembler.build()
    }

    pub fn into_assembler(self) -> Assembler {
        self.assembler
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dsl_builds_room_and_habitat_primitives() {
        let mut dsl = LevelDsl::new();
        dsl.solid_box(
            "wall",
            Vec3::new(0.0, 0.0, 1.5),
            Vec3::new(5.0, 0.25, 3.0),
            MaterialId(1),
        );
        dsl.cut_box("door", Vec3::new(0.0, 0.0, 1.0), Vec3::new(1.0, 0.5, 2.0));
        dsl.dome_cap_z(
            "city dome",
            DomeCapZSpec {
                center: Vec3::new(0.0, 0.0, 0.0),
                radius: 3.0,
                height: 1.5,
                rings: 4,
                segments: 16,
                material: MaterialId(2),
            },
        );
        dsl.floret_arm(
            "solar petal",
            FloretArmSpec {
                anchor: Vec3::new(3.0, 0.0, 0.0),
                direction: Vec3::X,
                length: 12.0,
                root_width: 1.2,
                tip_width: 3.0,
                thickness: 0.08,
                tip_lift: 0.6,
                material: MaterialId(3),
            },
        );

        let output = dsl.assemble();
        assert!(output.mesh.triangle_count() > 12);
        assert!(output.report.warnings.is_empty());
    }
}
