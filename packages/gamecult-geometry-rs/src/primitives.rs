#![allow(dead_code)]
use bevy_math::{Vec2, Vec3};

use crate::{MaterialId, TriangleMesh};

#[derive(Clone, Copy, Debug)]
pub struct DomeCapZSpec {
    pub center: Vec3,
    pub radius: f32,
    pub height: f32,
    pub rings: usize,
    pub segments: usize,
    pub material: MaterialId,
}

#[derive(Clone, Copy, Debug)]
pub struct FloretArmSpec {
    pub anchor: Vec3,
    pub direction: Vec3,
    pub length: f32,
    pub root_width: f32,
    pub tip_width: f32,
    pub thickness: f32,
    pub tip_lift: f32,
    pub material: MaterialId,
}

pub fn append_cylinder_z(
    mesh: &mut TriangleMesh,
    center: Vec3,
    radius: f32,
    depth: f32,
    segments: usize,
    material: MaterialId,
) {
    let segments = segments.max(3);
    let half = depth * 0.5;
    let bottom_center = center - Vec3::Z * half;
    let top_center = center + Vec3::Z * half;

    for i in 0..segments {
        let a0 = ring_angle(i, segments);
        let a1 = ring_angle(i + 1, segments);
        let p0 = center + Vec3::new(a0.cos() * radius, a0.sin() * radius, -half);
        let p1 = center + Vec3::new(a1.cos() * radius, a1.sin() * radius, -half);
        let p2 = center + Vec3::new(a1.cos() * radius, a1.sin() * radius, half);
        let p3 = center + Vec3::new(a0.cos() * radius, a0.sin() * radius, half);
        let normal = Vec3::new(((a0 + a1) * 0.5).cos(), ((a0 + a1) * 0.5).sin(), 0.0);
        mesh.append_quad([p0, p1, p2, p3], normal, material);
        mesh.append_triangle(
            [bottom_center, p1, p0],
            Vec3::NEG_Z,
            [Vec2::ZERO, Vec2::X, Vec2::Y],
            material,
        );
        mesh.append_triangle(
            [top_center, p3, p2],
            Vec3::Z,
            [Vec2::ZERO, Vec2::X, Vec2::Y],
            material,
        );
    }
}

pub fn append_dome_cap_z(mesh: &mut TriangleMesh, spec: DomeCapZSpec) {
    let rings = spec.rings.max(2);
    let segments = spec.segments.max(6);
    let top = spec.center + Vec3::Z * spec.height;

    for ring in 0..rings {
        let t0 = ring as f32 / rings as f32;
        let t1 = (ring + 1) as f32 / rings as f32;
        let r0 = spec.radius * (1.0 - t0 * t0).sqrt();
        let r1 = spec.radius * (1.0 - t1 * t1).sqrt();
        let z0 = spec.center.z + spec.height * t0;
        let z1 = spec.center.z + spec.height * t1;

        for seg in 0..segments {
            let a0 = ring_angle(seg, segments);
            let a1 = ring_angle(seg + 1, segments);
            let p0 = Vec3::new(
                spec.center.x + a0.cos() * r0,
                spec.center.y + a0.sin() * r0,
                z0,
            );
            let p1 = Vec3::new(
                spec.center.x + a1.cos() * r0,
                spec.center.y + a1.sin() * r0,
                z0,
            );

            if ring + 1 == rings {
                let normal = (p0 - spec.center)
                    .cross(top - spec.center)
                    .normalize_or_zero();
                mesh.append_triangle(
                    [p0, p1, top],
                    normal,
                    [Vec2::ZERO, Vec2::X, Vec2::Y],
                    spec.material,
                );
            } else {
                let p2 = Vec3::new(
                    spec.center.x + a1.cos() * r1,
                    spec.center.y + a1.sin() * r1,
                    z1,
                );
                let p3 = Vec3::new(
                    spec.center.x + a0.cos() * r1,
                    spec.center.y + a0.sin() * r1,
                    z1,
                );
                let normal = ((p0 + p1 + p2 + p3) * 0.25 - spec.center).normalize_or_zero();
                mesh.append_quad([p0, p1, p2, p3], normal, spec.material);
            }
        }
    }
}

pub fn append_floret_arm(mesh: &mut TriangleMesh, spec: FloretArmSpec) {
    let forward = spec.direction.normalize_or_zero();
    let forward = if forward.length_squared() == 0.0 {
        Vec3::X
    } else {
        forward
    };
    let mut side = forward.cross(Vec3::Z).normalize_or_zero();
    if side.length_squared() == 0.0 {
        side = Vec3::Y;
    }

    let root_center = spec.anchor;
    let tip_center = spec.anchor + forward * spec.length + Vec3::Z * spec.tip_lift;
    let z = Vec3::Z * (spec.thickness * 0.5);
    let r = side * (spec.root_width * 0.5);
    let t = side * (spec.tip_width * 0.5);

    let bottom = [
        root_center - r - z,
        root_center + r - z,
        tip_center + t - z,
        tip_center - t - z,
    ];
    let top = [
        root_center - r + z,
        root_center + r + z,
        tip_center + t + z,
        tip_center - t + z,
    ];

    mesh.append_quad(
        [bottom[0], bottom[3], bottom[2], bottom[1]],
        Vec3::NEG_Z,
        spec.material,
    );
    mesh.append_quad([top[0], top[1], top[2], top[3]], Vec3::Z, spec.material);
    mesh.append_quad(
        [bottom[0], bottom[1], top[1], top[0]],
        -forward,
        spec.material,
    );
    mesh.append_quad(
        [bottom[3], top[3], top[2], bottom[2]],
        forward,
        spec.material,
    );
    mesh.append_quad([bottom[0], top[0], top[3], bottom[3]], -side, spec.material);
    mesh.append_quad([bottom[1], bottom[2], top[2], top[1]], side, spec.material);
}

fn ring_angle(index: usize, segments: usize) -> f32 {
    index as f32 * std::f32::consts::TAU / segments as f32
}
