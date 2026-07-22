#![allow(dead_code)]
use bevy_math::{Vec2, Vec3};

use crate::{Aabb, MaterialId};

#[derive(Clone, Debug, Default, PartialEq)]
pub struct TriangleMesh {
    pub positions: Vec<Vec3>,
    pub normals: Vec<Vec3>,
    pub uvs: Vec<Vec2>,
    pub indices: Vec<u32>,
    pub triangle_materials: Vec<MaterialId>,
}

impl TriangleMesh {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn triangle_count(&self) -> usize {
        self.indices.len() / 3
    }

    pub fn vertex_count(&self) -> usize {
        self.positions.len()
    }

    pub fn append_box(&mut self, bounds: Aabb, material: MaterialId) {
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

        self.append_quad(
            [corners[0], corners[3], corners[2], corners[1]],
            Vec3::NEG_Z,
            material,
        );
        self.append_quad(
            [corners[4], corners[5], corners[6], corners[7]],
            Vec3::Z,
            material,
        );
        self.append_quad(
            [corners[0], corners[1], corners[5], corners[4]],
            Vec3::NEG_Y,
            material,
        );
        self.append_quad(
            [corners[3], corners[7], corners[6], corners[2]],
            Vec3::Y,
            material,
        );
        self.append_quad(
            [corners[0], corners[4], corners[7], corners[3]],
            Vec3::NEG_X,
            material,
        );
        self.append_quad(
            [corners[1], corners[2], corners[6], corners[5]],
            Vec3::X,
            material,
        );
    }

    pub fn append_quad(&mut self, points: [Vec3; 4], normal: Vec3, material: MaterialId) {
        let base = self.positions.len() as u32;
        self.positions.extend(points);
        self.normals.extend([normal; 4]);
        self.uvs.extend([
            Vec2::new(0.0, 0.0),
            Vec2::new(1.0, 0.0),
            Vec2::new(1.0, 1.0),
            Vec2::new(0.0, 1.0),
        ]);
        self.indices
            .extend([base, base + 1, base + 2, base, base + 2, base + 3]);
        self.triangle_materials.extend([material; 2]);
    }

    pub fn append_triangle(
        &mut self,
        points: [Vec3; 3],
        normal: Vec3,
        uvs: [Vec2; 3],
        material: MaterialId,
    ) {
        let base = self.positions.len() as u32;
        self.positions.extend(points);
        self.normals.extend([normal; 3]);
        self.uvs.extend(uvs);
        self.indices.extend([base, base + 1, base + 2]);
        self.triangle_materials.push(material);
    }
}
