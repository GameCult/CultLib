#![allow(dead_code)]
use bevy_math::{Quat, Vec3};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct BrushId(pub u32);

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Default)]
pub struct MaterialId(pub u32);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BrushOp {
    Add,
    Subtract,
    Intersect,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum PolygonCategory {
    Inside,
    Outside,
    Aligned,
    ReverseAligned,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Aabb {
    pub min: Vec3,
    pub max: Vec3,
}

impl Aabb {
    pub fn empty() -> Self {
        Self {
            min: Vec3::splat(f32::INFINITY),
            max: Vec3::splat(f32::NEG_INFINITY),
        }
    }

    pub fn from_center_size(center: Vec3, size: Vec3) -> Self {
        let half = size * 0.5;
        Self {
            min: center - half,
            max: center + half,
        }
    }

    pub fn new(min: Vec3, max: Vec3) -> Self {
        Self { min, max }
    }

    pub fn from_points(points: &[Vec3]) -> Self {
        let mut bounds = Self::empty();
        for point in points {
            bounds.include_point(*point);
        }
        bounds
    }

    pub fn include_point(&mut self, point: Vec3) {
        self.min = self.min.min(point);
        self.max = self.max.max(point);
    }

    pub fn union(self, other: Self) -> Self {
        if !self.has_finite_extents() {
            return other;
        }
        if !other.has_finite_extents() {
            return self;
        }
        Self {
            min: self.min.min(other.min),
            max: self.max.max(other.max),
        }
    }

    pub fn size(self) -> Vec3 {
        self.max - self.min
    }

    pub fn center(self) -> Vec3 {
        (self.min + self.max) * 0.5
    }

    pub fn is_valid(self) -> bool {
        self.max.x > self.min.x && self.max.y > self.min.y && self.max.z > self.min.z
    }

    pub fn has_finite_extents(self) -> bool {
        self.min.is_finite() && self.max.is_finite()
    }

    pub fn intersects(self, other: Self) -> bool {
        self.min.x < other.max.x
            && self.max.x > other.min.x
            && self.min.y < other.max.y
            && self.max.y > other.min.y
            && self.min.z < other.max.z
            && self.max.z > other.min.z
    }

    pub fn intersects_any(self, others: impl IntoIterator<Item = Self>) -> bool {
        others.into_iter().any(|other| self.intersects(other))
    }

    pub fn intersection(self, other: Self) -> Option<Self> {
        if !self.intersects(other) {
            return None;
        }

        let min = self.min.max(other.min);
        let max = self.max.min(other.max);
        let hit = Self { min, max };
        hit.is_valid().then_some(hit)
    }

    pub fn contains_point(self, point: Vec3) -> bool {
        point.x >= self.min.x
            && point.x <= self.max.x
            && point.y >= self.min.y
            && point.y <= self.max.y
            && point.z >= self.min.z
            && point.z <= self.max.z
    }

    pub fn contains_point_strict(self, point: Vec3, epsilon: f32) -> bool {
        point.x > self.min.x + epsilon
            && point.x < self.max.x - epsilon
            && point.y > self.min.y + epsilon
            && point.y < self.max.y - epsilon
            && point.z > self.min.z + epsilon
            && point.z < self.max.z - epsilon
    }

    pub fn subtract_box(self, cutter: Self) -> Vec<Self> {
        let Some(hit) = self.intersection(cutter) else {
            return vec![self];
        };

        let mut pieces = Vec::with_capacity(6);

        push_valid(
            &mut pieces,
            Self::new(self.min, Vec3::new(hit.min.x, self.max.y, self.max.z)),
        );
        push_valid(
            &mut pieces,
            Self::new(Vec3::new(hit.max.x, self.min.y, self.min.z), self.max),
        );

        let x_min = self.min.x.max(hit.min.x);
        let x_max = self.max.x.min(hit.max.x);

        push_valid(
            &mut pieces,
            Self::new(
                Vec3::new(x_min, self.min.y, self.min.z),
                Vec3::new(x_max, hit.min.y, self.max.z),
            ),
        );
        push_valid(
            &mut pieces,
            Self::new(
                Vec3::new(x_min, hit.max.y, self.min.z),
                Vec3::new(x_max, self.max.y, self.max.z),
            ),
        );

        let y_min = self.min.y.max(hit.min.y);
        let y_max = self.max.y.min(hit.max.y);

        push_valid(
            &mut pieces,
            Self::new(
                Vec3::new(x_min, y_min, self.min.z),
                Vec3::new(x_max, y_max, hit.min.z),
            ),
        );
        push_valid(
            &mut pieces,
            Self::new(
                Vec3::new(x_min, y_min, hit.max.z),
                Vec3::new(x_max, y_max, self.max.z),
            ),
        );

        pieces
    }
}

fn push_valid(pieces: &mut Vec<Aabb>, bounds: Aabb) {
    if bounds.is_valid() {
        pieces.push(bounds);
    }
}

#[derive(Clone, Debug)]
pub enum Primitive {
    Box {
        bounds: Aabb,
    },
    OrientedBox {
        center: Vec3,
        size: Vec3,
        rotation: Quat,
    },
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

#[derive(Clone, Debug)]
pub struct Brush {
    pub id: BrushId,
    pub name: String,
    pub op: BrushOp,
    pub primitive: Primitive,
    pub material: MaterialId,
    pub rotation: Quat,
    pub generation: u64,
}

impl Brush {
    pub fn new(
        id: BrushId,
        name: impl Into<String>,
        op: BrushOp,
        primitive: Primitive,
        material: MaterialId,
    ) -> Self {
        Self {
            id,
            name: name.into(),
            op,
            primitive,
            material,
            rotation: Quat::IDENTITY,
            generation: 0,
        }
    }

    pub fn set_dirty(&mut self) {
        self.generation = self.generation.wrapping_add(1);
    }

    pub fn as_box(&self) -> Option<Aabb> {
        match self.primitive {
            Primitive::Box { bounds } => Some(bounds),
            _ => None,
        }
    }

    pub fn bounds(&self) -> Aabb {
        self.primitive.bounds()
    }
}

impl Primitive {
    pub fn bounds(&self) -> Aabb {
        match *self {
            Self::Box { bounds } => bounds,
            Self::OrientedBox {
                center,
                size,
                rotation,
            } => oriented_box_bounds(center, size, rotation),
            Self::CylinderZ {
                center,
                radius,
                depth,
                ..
            } => Aabb::new(
                center - Vec3::new(radius, radius, depth * 0.5),
                center + Vec3::new(radius, radius, depth * 0.5),
            ),
            Self::DomeCapZ {
                center,
                radius,
                height,
                ..
            } => Aabb::new(
                center - Vec3::new(radius, radius, 0.0),
                center + Vec3::new(radius, radius, height),
            ),
            Self::FloretArm {
                anchor,
                direction,
                length,
                root_width,
                tip_width,
                thickness,
                tip_lift,
            } => floret_bounds(
                anchor, direction, length, root_width, tip_width, thickness, tip_lift,
            ),
        }
    }
}

fn oriented_box_bounds(center: Vec3, size: Vec3, rotation: Quat) -> Aabb {
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
    Aabb::from_points(&corners)
}

fn floret_bounds(
    anchor: Vec3,
    direction: Vec3,
    length: f32,
    root_width: f32,
    tip_width: f32,
    thickness: f32,
    tip_lift: f32,
) -> Aabb {
    let forward = direction.normalize_or_zero();
    let forward = if forward.length_squared() == 0.0 {
        Vec3::X
    } else {
        forward
    };
    let mut side = forward.cross(Vec3::Z).normalize_or_zero();
    if side.length_squared() == 0.0 {
        side = Vec3::Y;
    }

    let root_center = anchor;
    let tip_center = anchor + forward * length + Vec3::Z * tip_lift;
    let z = Vec3::Z * (thickness * 0.5);
    let r = side * (root_width * 0.5);
    let t = side * (tip_width * 0.5);
    Aabb::from_points(&[
        root_center - r - z,
        root_center + r - z,
        root_center - r + z,
        root_center + r + z,
        tip_center - t - z,
        tip_center + t - z,
        tip_center - t + z,
        tip_center + t + z,
    ])
}
