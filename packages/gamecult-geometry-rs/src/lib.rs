//! GameCult.Geometry's Rust contract and runtime surface.
//!
//! The v2 contract was mined from the MIT-licensed GameCult `vg-csg` crate at
//! commit `8f070f4`, then rewritten around typed vector values. This crate has
//! no runtime or build dependency on VibeGeometry or `vg-csg`.

mod assembler;
mod brush;
mod convex;
mod domain;
mod dsl;
mod frontier;
mod mesh;
mod primitives;
#[cfg(test)]
mod realtime_csg_parity;
mod tree;

#[allow(unused_imports)]
pub(crate) use assembler::{Assembler, BuildOutput, BuildReport, BuildWarning};
#[allow(unused_imports)]
pub(crate) use brush::{Aabb, Brush, BrushId, BrushOp, MaterialId, PolygonCategory, Primitive};
#[allow(unused_imports)]
pub(crate) use convex::{
    CategorizedPolygons, ConvexPolygon, ConvexSolid, Plane, PolygonRouteScratch,
};
#[allow(unused_imports)]
pub(crate) use domain::{
    ClaimLoweringTarget, ContributionManifest, ContributionRow, CsgClaimLowering, DomainChunkBuild,
    DomainFrame, DomainKey, DomainKind, DomainNode, DomainNodeSpec, DomainQuery, DomainSummary,
    FeatureClaim, FeatureClaimKind, FeatureClaimSpec, FeatureLoweringPolicy, FieldEncoding,
    FieldLayer, SelectedCut, SelectedCutManifest, TriangleChunk, TriangleChunkManifest,
    build_domain_chunks, lower_feature_claims_to_csg_tree, lower_selected_cut,
    lower_selected_cut_chunks, ragnarok_column_fixture, ragnarok_column_spec, select_domain_cut,
};
#[allow(unused_imports)]
pub(crate) use dsl::LevelDsl;
#[allow(unused_imports)]
pub(crate) use frontier::{DemandFrontier, DemandPair, DirtyDemandFrontier};
pub(crate) use mesh::TriangleMesh;
#[allow(unused_imports)]
pub(crate) use primitives::{
    DomeCapZSpec, FloretArmSpec, append_cylinder_z, append_dome_cap_z, append_floret_arm,
};
#[allow(unused_imports)]
pub(crate) use tree::{
    CsgBranchOp, CsgNode, CsgNodeId, CsgOperationType, CsgTree, CsgTreeArena, CsgTreeBranch,
    CsgTreeBrush,
};

use std::fmt::Write;

use serde::{Deserialize, Serialize, de::DeserializeOwned};
use sha2::{Digest, Sha256};

pub const GEOMETRY_DOMAIN_SCHEMA: &str = "gamecult.geometry.domain.v2";
pub const GEOMETRY_BUILD_REQUEST_SCHEMA: &str = "gamecult.geometry.build_request.v2";
pub const GEOMETRY_SELECTED_CUT_SCHEMA: &str = "gamecult.geometry.selected_cut.v2";
pub const GEOMETRY_CHUNK_ARTIFACT_SCHEMA: &str = "gamecult.geometry.chunk_artifact.v2";

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Float2(pub f32, pub f32);

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Float3(pub f32, pub f32, pub f32);

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub struct Quaternion(pub f32, pub f32, pub f32, pub f32);

impl Default for Quaternion {
    fn default() -> Self {
        Self(0.0, 0.0, 0.0, 1.0)
    }
}

pub trait GeometryDocument: Serialize + DeserializeOwned + Sized {
    const SCHEMA_VERSION: &'static str;
    fn record_key(&self) -> String;
    fn to_msgpack(&self) -> Result<Vec<u8>, rmp_serde::encode::Error> {
        rmp_serde::to_vec(self)
    }
    fn from_msgpack(payload: &[u8]) -> Result<Self, rmp_serde::decode::Error> {
        rmp_serde::from_slice(payload)
    }
}

/// Geometry-owned CSG facade. The implementation uses an internal math kernel,
/// but no `bevy_math` type crosses this public boundary.
#[derive(Default)]
pub struct GeometryBrushAssembler(Assembler);

impl GeometryBrushAssembler {
    pub fn new() -> Self {
        Self(Assembler::new())
    }
    pub fn add_box(
        &mut self,
        name: impl Into<String>,
        center: Float3,
        size: Float3,
        material: u32,
    ) {
        self.0
            .solid_box(name, internal_aabb(center, size), MaterialId(material));
    }
    pub fn subtract_box(&mut self, name: impl Into<String>, center: Float3, size: Float3) {
        self.0.cut_box(name, internal_aabb(center, size));
    }
    pub fn build(&self) -> GeometryTriangleMesh {
        internal_mesh(&self.0.build().mesh)
    }
}

fn internal_aabb(center: Float3, size: Float3) -> Aabb {
    use bevy_math::Vec3;
    Aabb::from_center_size(
        Vec3::new(center.0, center.1, center.2),
        Vec3::new(size.0, size.1, size.2),
    )
}

fn internal_mesh(mesh: &TriangleMesh) -> GeometryTriangleMesh {
    GeometryTriangleMesh(
        mesh.positions
            .iter()
            .map(|v| Float3(v.x, v.y, v.z))
            .collect(),
        mesh.normals.iter().map(|v| Float3(v.x, v.y, v.z)).collect(),
        mesh.uvs.iter().map(|v| Float2(v.x, v.y)).collect(),
        mesh.indices.clone(),
        mesh.triangle_materials.iter().map(|v| v.0).collect(),
    )
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryDomainDocument(
    pub String,
    pub String,
    pub String,
    pub GeometryDomainNode,
    pub String,
);

impl GeometryDocument for GeometryDomainDocument {
    const SCHEMA_VERSION: &'static str = GEOMETRY_DOMAIN_SCHEMA;
    fn record_key(&self) -> String {
        format!(
            "geometry:domain:{}",
            stable_hash(&[&self.1, &self.2, &self.3.stable_fingerprint()])
        )
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryDomainNode(
    pub String,
    pub String,
    pub Float3,
    pub Quaternion,
    pub u64,
    pub Vec<GeometryFeatureClaim>,
    pub Vec<GeometryDomainNode>,
);

impl GeometryDomainNode {
    pub fn stable_fingerprint(&self) -> String {
        let mut parts = vec![
            self.0.clone(),
            self.1.clone(),
            stable_float3(self.2),
            stable_quaternion(self.3),
            self.4.to_string(),
        ];
        parts.extend(self.5.iter().map(GeometryFeatureClaim::stable_fingerprint));
        parts.extend(self.6.iter().map(GeometryDomainNode::stable_fingerprint));
        stable_join(&parts)
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryFeatureClaim(
    pub String,
    pub Float3,
    pub Quaternion,
    pub Float3,
    pub Float3,
    pub String,
    pub u32,
    pub String,
);

impl GeometryFeatureClaim {
    pub fn stable_fingerprint(&self) -> String {
        stable_join(&[
            self.0.clone(),
            stable_float3(self.1),
            stable_quaternion(self.2),
            stable_float3(self.3),
            stable_float3(self.4),
            self.5.clone(),
            self.6.to_string(),
            self.7.clone(),
        ])
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryBuildRequest(
    pub String,
    pub String,
    pub String,
    pub Float3,
    pub Float3,
    pub Float3,
    pub f32,
    pub f32,
    pub f32,
    pub i32,
    pub i32,
    pub Vec<String>,
    pub Vec<String>,
    pub Vec<String>,
    pub String,
);

impl GeometryDocument for GeometryBuildRequest {
    const SCHEMA_VERSION: &'static str = GEOMETRY_BUILD_REQUEST_SCHEMA;
    fn record_key(&self) -> String {
        format!(
            "geometry:request:{}",
            stable_hash(&[
                &self.1,
                &self.2,
                &stable_float3(self.3),
                &stable_float3(self.4),
                &stable_float3(self.5),
                &stable_f32(self.6),
                &stable_f32(self.7),
                &stable_f32(self.8),
                &self.9.to_string(),
                &self.10.to_string(),
                &stable_join(&self.11),
                &stable_join(&self.12),
                &stable_join(&self.13),
            ])
        )
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometrySelectedCutManifest(
    pub String,
    pub String,
    pub Vec<String>,
    pub Vec<String>,
    pub Vec<String>,
    pub Vec<GeometryContributionRow>,
);

impl GeometryDocument for GeometrySelectedCutManifest {
    const SCHEMA_VERSION: &'static str = GEOMETRY_SELECTED_CUT_SCHEMA;
    fn record_key(&self) -> String {
        format!(
            "geometry:cut:{}",
            stable_hash(&[
                &self.1,
                &self.0,
                &stable_join(&self.2),
                &stable_join(&self.3),
                &stable_join(&self.4),
            ])
        )
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryContributionRow(
    pub String,
    pub String,
    pub f32,
    pub f32,
    pub f32,
    pub i32,
    pub i32,
    pub i32,
    pub bool,
    pub bool,
    pub bool,
    pub bool,
    pub bool,
);

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GeometryChunkArtifact(
    pub String,
    pub String,
    pub String,
    pub Float3,
    pub Float3,
    pub Vec<String>,
    pub Vec<String>,
    pub GeometryTriangleMesh,
    pub Option<GeometryTriangleMesh>,
    pub i32,
    pub i32,
    pub i32,
    pub u64,
    pub bool,
);

impl GeometryDocument for GeometryChunkArtifact {
    const SCHEMA_VERSION: &'static str = GEOMETRY_CHUNK_ARTIFACT_SCHEMA;
    fn record_key(&self) -> String {
        format!(
            "geometry:chunk:{}",
            stable_hash(&[
                &self.1,
                &self.0,
                &self.2,
                &stable_join(&self.5),
                &stable_join(&self.6),
                &self.7.stable_fingerprint(),
                self.8
                    .as_ref()
                    .map(GeometryTriangleMesh::stable_fingerprint)
                    .as_deref()
                    .unwrap_or(""),
            ])
        )
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct GeometryTriangleMesh(
    pub Vec<Float3>,
    pub Vec<Float3>,
    pub Vec<Float2>,
    pub Vec<u32>,
    pub Vec<u32>,
);

impl GeometryTriangleMesh {
    pub fn triangle_count(&self) -> usize {
        self.3.len() / 3
    }
    pub fn stable_fingerprint(&self) -> String {
        stable_hash(&[
            &stable_float3_array(&self.0),
            &stable_float3_array(&self.1),
            &stable_float2_array(&self.2),
            &stable_u32_array(&self.3),
            &stable_u32_array(&self.4),
        ])
    }
}

fn stable_hash(parts: &[&str]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(parts.join("\u{1f}").as_bytes());
    let mut output = String::with_capacity(64);
    for byte in hasher.finalize() {
        write!(&mut output, "{byte:02x}").expect("string write");
    }
    output
}

fn stable_join(parts: &[String]) -> String {
    parts.join("\u{1e}")
}
fn stable_f32(value: f32) -> String {
    format!("{:08x}", value.to_bits())
}
fn stable_float2(value: Float2) -> String {
    format!("{},{}", stable_f32(value.0), stable_f32(value.1))
}
fn stable_float3(value: Float3) -> String {
    format!(
        "{},{},{}",
        stable_f32(value.0),
        stable_f32(value.1),
        stable_f32(value.2)
    )
}
fn stable_quaternion(value: Quaternion) -> String {
    format!(
        "{},{},{},{}",
        stable_f32(value.0),
        stable_f32(value.1),
        stable_f32(value.2),
        stable_f32(value.3)
    )
}
fn stable_float2_array(values: &[Float2]) -> String {
    values
        .iter()
        .map(|v| stable_float2(*v))
        .collect::<Vec<_>>()
        .join(",")
}
fn stable_float3_array(values: &[Float3]) -> String {
    values
        .iter()
        .map(|v| stable_float3(*v))
        .collect::<Vec<_>>()
        .join(",")
}
fn stable_u32_array(values: &[u32]) -> String {
    values
        .iter()
        .map(u32::to_string)
        .collect::<Vec<_>>()
        .join(",")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn domain() -> GeometryDomainDocument {
        GeometryDomainDocument(
            "fixture-domain".into(),
            "fixture/root".into(),
            "gamecult-geometry-rs".into(),
            GeometryDomainNode(
                "root".into(),
                "Root".into(),
                Float3(1.0, -2.5, 0.0),
                Quaternion::default(),
                42,
                vec![],
                vec![],
            ),
            "2026-07-22T00:00:00Z".into(),
        )
    }

    fn chunk() -> GeometryChunkArtifact {
        GeometryChunkArtifact(
            "fixture/chunk".into(),
            "geometry:cut:fixture".into(),
            "cut-fixture".into(),
            Float3(-1.0, -2.0, -3.0),
            Float3(1.0, 2.0, 3.0),
            vec!["fixture/root".into()],
            vec!["fixture/root/claim".into()],
            GeometryTriangleMesh(
                vec![
                    Float3(0.0, 0.0, 0.0),
                    Float3(1.0, 0.0, 0.0),
                    Float3(0.0, 1.0, 0.0),
                ],
                vec![Float3(0.0, 0.0, 1.0); 3],
                vec![Float2(0.0, 0.0), Float2(1.0, 0.0), Float2(0.0, 1.0)],
                vec![0, 1, 2],
                vec![7],
            ),
            None,
            1,
            0,
            0,
            0x0123_4567_89ab_cdef,
            true,
        )
    }

    fn request() -> GeometryBuildRequest {
        GeometryBuildRequest(
            "request-fixture".into(),
            domain().record_key(),
            "workers".into(),
            Float3(1.0, 2.0, 3.0),
            Float3(-4.0, -5.0, -6.0),
            Float3(4.0, 5.0, 6.0),
            1080.0,
            1.0,
            0.25,
            100,
            50,
            vec!["Root".into()],
            vec![],
            vec![],
            "2026-07-22T00:00:01Z".into(),
        )
    }

    fn cut() -> GeometrySelectedCutManifest {
        GeometrySelectedCutManifest(
            "cut-fixture".into(),
            "geometry:request:fixture".into(),
            vec!["fixture/root".into()],
            vec![],
            vec![],
            vec![],
        )
    }

    #[test]
    fn schema_versions_are_v2() {
        assert_eq!(
            GeometryDomainDocument::SCHEMA_VERSION,
            "gamecult.geometry.domain.v2"
        );
        assert_eq!(
            GeometryBuildRequest::SCHEMA_VERSION,
            "gamecult.geometry.build_request.v2"
        );
        assert_eq!(
            GeometrySelectedCutManifest::SCHEMA_VERSION,
            "gamecult.geometry.selected_cut.v2"
        );
        assert_eq!(
            GeometryChunkArtifact::SCHEMA_VERSION,
            "gamecult.geometry.chunk_artifact.v2"
        );
    }

    #[test]
    fn domain_record_key_matches_csharp_v2_fixture() {
        let document = GeometryDomainDocument(
            "ragnarok-column".into(),
            "ragnarok-column".into(),
            "vg-csg".into(),
            GeometryDomainNode(
                "ragnarok-column".into(),
                "Root".into(),
                Float3::default(),
                Quaternion::default(),
                0x5eed,
                vec![],
                vec![GeometryDomainNode(
                    "stellarator-column-00".into(),
                    "Column".into(),
                    Float3::default(),
                    Quaternion::default(),
                    0xc011_0000,
                    vec![GeometryFeatureClaim(
                        "column-support-shell".into(),
                        Float3::default(),
                        Quaternion::default(),
                        Float3(0.0, 0.0, 45.0),
                        Float3(18.0, 18.0, 96.0),
                        "SupportShell".into(),
                        10,
                        "RenderAndCollider".into(),
                    )],
                    vec![],
                )],
            ),
            "2026-05-29T00:00:00.0000000Z".into(),
        );

        assert_eq!(
            document.record_key(),
            "geometry:domain:175899ea97548da0599e12bcaccd07fa1d6009ed450fadb3de229d47bab04431"
        );
    }

    #[test]
    fn mined_csg_performance_fixture_keeps_large_distant_cutters_bounded() {
        use bevy_math::Vec3;
        use std::time::{Duration, Instant};

        let mut assembler = Assembler::new();
        assembler.solid_box(
            "source",
            Aabb::from_center_size(Vec3::ZERO, Vec3::splat(4.0)),
            MaterialId(1),
        );
        for index in 0..512 {
            assembler.cut_box(
                format!("distant-{index}"),
                Aabb::from_center_size(Vec3::new(100.0 + index as f32 * 2.0, 0.0, 0.0), Vec3::ONE),
            );
        }
        let started = Instant::now();
        let output = assembler.build();
        assert_eq!(output.mesh.triangle_count(), 12);
        assert_eq!(output.report.candidate_pairs, 0);
        assert_eq!(output.report.rejected_pairs, 512);
        assert!(started.elapsed() < Duration::from_secs(2));
    }

    #[test]
    fn exact_v2_messagepack_and_record_key_witnesses() {
        let domain = domain();
        let chunk = chunk();
        let request = request();
        let cut = cut();
        assert_eq!(
            domain.record_key(),
            "geometry:domain:505c94e7393580a4e5b048b169aa4da955da606ffd28c054b766a4b94e50502e"
        );
        assert_eq!(
            request.record_key(),
            "geometry:request:36e25b80c04a6c54c8b3137abb4a8c961468a9b1c0e7dd4d5632cf5a6f847966"
        );
        assert_eq!(
            cut.record_key(),
            "geometry:cut:5614eae3fe2a17dca651d1e32f401c50e1c3b9cf941f5789655990325da64fb3"
        );
        assert_eq!(
            chunk.record_key(),
            "geometry:chunk:ca2874d3638800179c906087ec817ea715c43b70b99ead04edccce2a1b3d6ebb"
        );
        assert_eq!(
            domain.to_msgpack().unwrap(),
            [
                149, 174, 102, 105, 120, 116, 117, 114, 101, 45, 100, 111, 109, 97, 105, 110, 172,
                102, 105, 120, 116, 117, 114, 101, 47, 114, 111, 111, 116, 180, 103, 97, 109, 101,
                99, 117, 108, 116, 45, 103, 101, 111, 109, 101, 116, 114, 121, 45, 114, 115, 151,
                164, 114, 111, 111, 116, 164, 82, 111, 111, 116, 147, 202, 63, 128, 0, 0, 202, 192,
                32, 0, 0, 202, 0, 0, 0, 0, 148, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0,
                202, 63, 128, 0, 0, 42, 144, 144, 180, 50, 48, 50, 54, 45, 48, 55, 45, 50, 50, 84,
                48, 48, 58, 48, 48, 58, 48, 48, 90
            ]
        );
        assert_eq!(
            request.to_msgpack().unwrap(),
            [
                159, 175, 114, 101, 113, 117, 101, 115, 116, 45, 102, 105, 120, 116, 117, 114, 101,
                217, 80, 103, 101, 111, 109, 101, 116, 114, 121, 58, 100, 111, 109, 97, 105, 110,
                58, 53, 48, 53, 99, 57, 52, 101, 55, 51, 57, 51, 53, 56, 48, 97, 52, 101, 53, 98,
                48, 52, 56, 98, 49, 54, 57, 97, 97, 52, 100, 97, 57, 53, 53, 100, 97, 54, 48, 54,
                102, 102, 100, 50, 56, 99, 48, 53, 52, 98, 55, 54, 54, 97, 52, 98, 57, 52, 101, 53,
                48, 53, 48, 50, 101, 167, 119, 111, 114, 107, 101, 114, 115, 147, 202, 63, 128, 0,
                0, 202, 64, 0, 0, 0, 202, 64, 64, 0, 0, 147, 202, 192, 128, 0, 0, 202, 192, 160, 0,
                0, 202, 192, 192, 0, 0, 147, 202, 64, 128, 0, 0, 202, 64, 160, 0, 0, 202, 64, 192,
                0, 0, 202, 68, 135, 0, 0, 202, 63, 128, 0, 0, 202, 62, 128, 0, 0, 100, 50, 145,
                164, 82, 111, 111, 116, 144, 144, 180, 50, 48, 50, 54, 45, 48, 55, 45, 50, 50, 84,
                48, 48, 58, 48, 48, 58, 48, 49, 90
            ]
        );
        assert_eq!(
            cut.to_msgpack().unwrap(),
            [
                150, 171, 99, 117, 116, 45, 102, 105, 120, 116, 117, 114, 101, 184, 103, 101, 111,
                109, 101, 116, 114, 121, 58, 114, 101, 113, 117, 101, 115, 116, 58, 102, 105, 120,
                116, 117, 114, 101, 145, 172, 102, 105, 120, 116, 117, 114, 101, 47, 114, 111, 111,
                116, 144, 144, 144
            ]
        );
        assert_eq!(
            chunk.to_msgpack().unwrap(),
            [
                158, 173, 102, 105, 120, 116, 117, 114, 101, 47, 99, 104, 117, 110, 107, 180, 103,
                101, 111, 109, 101, 116, 114, 121, 58, 99, 117, 116, 58, 102, 105, 120, 116, 117,
                114, 101, 171, 99, 117, 116, 45, 102, 105, 120, 116, 117, 114, 101, 147, 202, 191,
                128, 0, 0, 202, 192, 0, 0, 0, 202, 192, 64, 0, 0, 147, 202, 63, 128, 0, 0, 202, 64,
                0, 0, 0, 202, 64, 64, 0, 0, 145, 172, 102, 105, 120, 116, 117, 114, 101, 47, 114,
                111, 111, 116, 145, 178, 102, 105, 120, 116, 117, 114, 101, 47, 114, 111, 111, 116,
                47, 99, 108, 97, 105, 109, 149, 147, 147, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0, 202, 0,
                0, 0, 0, 147, 202, 63, 128, 0, 0, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0, 147, 202, 0, 0,
                0, 0, 202, 63, 128, 0, 0, 202, 0, 0, 0, 0, 147, 147, 202, 0, 0, 0, 0, 202, 0, 0, 0,
                0, 202, 63, 128, 0, 0, 147, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0, 202, 63, 128, 0, 0,
                147, 202, 0, 0, 0, 0, 202, 0, 0, 0, 0, 202, 63, 128, 0, 0, 147, 146, 202, 0, 0, 0,
                0, 202, 0, 0, 0, 0, 146, 202, 63, 128, 0, 0, 202, 0, 0, 0, 0, 146, 202, 0, 0, 0, 0,
                202, 63, 128, 0, 0, 147, 0, 1, 2, 145, 7, 192, 1, 0, 0, 207, 1, 35, 69, 103, 137,
                171, 205, 239, 195
            ]
        );
        assert_eq!(
            GeometryDomainDocument::from_msgpack(&domain.to_msgpack().unwrap()).unwrap(),
            domain
        );
        assert_eq!(
            GeometryChunkArtifact::from_msgpack(&chunk.to_msgpack().unwrap()).unwrap(),
            chunk
        );
        assert_eq!(
            GeometryBuildRequest::from_msgpack(&request.to_msgpack().unwrap()).unwrap(),
            request
        );
        assert_eq!(
            GeometrySelectedCutManifest::from_msgpack(&cut.to_msgpack().unwrap()).unwrap(),
            cut
        );
    }

    #[test]
    fn scalar_bit_identity_is_stable_through_messagepack() {
        let mut signed_zero = request();
        signed_zero.6 = -0.0;
        signed_zero.7 = f32::from_bits(0x7fc0_1234);
        signed_zero.8 = f32::NEG_INFINITY;
        assert_eq!(
            signed_zero.record_key(),
            "geometry:request:978dd0913fc3e12c04766e065320d485d172d60d61be920a9a32637d1c439c81"
        );
        let decoded =
            GeometryBuildRequest::from_msgpack(&signed_zero.to_msgpack().unwrap()).unwrap();
        assert_eq!(decoded.6.to_bits(), 0x8000_0000);
        assert_eq!(decoded.7.to_bits(), 0x7fc0_1234);
        assert_eq!(decoded.8.to_bits(), f32::NEG_INFINITY.to_bits());
    }
}
