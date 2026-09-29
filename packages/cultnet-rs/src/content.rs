//! CultMesh content plane: chunking, hashing, manifest shape and validation, answering one chunk
//! request, and verifying a fetched body. Mirrors `CultMeshCdn` (`CultMeshCdn.cs`) and
//! `CultMeshLegacyRudpContentServer` (`CultMeshContentSessions.cs`); the two chunk messages are
//! `CultNetMessage::ContentChunkRequest`/`ContentChunkResponse` in `contracts.rs`.
//!
//! This is the only owner of these rules in Rust. A consumer hands `fetch_content` a transport
//! closure and gets verified bytes; it never hashes, chunks or validates a manifest itself.

use anyhow::{Result, anyhow, bail};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::CultNetMessage;

/// One chunk of a packed body. `chunk_hash` is lowercase SHA-256 hex of `payload`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CultMeshCdnChunk {
    pub chunk_hash: String,
    pub payload: Vec<u8>,
}

/// `CultMeshCdnChunkRef`. Encodes as the reference's positional array under every encoder.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(from = "ChunkRefWire", into = "ChunkRefWire")]
pub struct CultMeshCdnChunkRef {
    pub chunk_hash: String,
    pub offset: i64,
    pub size_bytes: i32,
    pub record_key: String,
}

/// `CultMeshCdnArtifactManifest`. Encodes as the reference's positional array (keys 0-9).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(from = "ManifestWire", into = "ManifestWire")]
pub struct CultMeshCdnArtifactManifest {
    pub artifact_id: String,
    pub kind: String,
    pub version: String,
    pub content_hash: String,
    pub size_bytes: i64,
    pub mime_type: String,
    pub created_at_utc: String,
    pub chunks: Vec<CultMeshCdnChunkRef>,
    pub tags: Vec<String>,
    /// Caller metadata in wire order. The reference writes a `Dictionary` in insertion order and
    /// refuses a duplicate key at read, so this keeps the order it was read in and refuses a duplicate.
    pub metadata: Vec<(String, String)>,
}

// The tuple mirrors: `to_vec_named` would write a map for a named-field struct, but a tuple
// struct is an array under every serde encoder.
#[derive(Serialize, Deserialize)]
struct ChunkRefWire(String, i64, i32, String);

#[derive(Serialize, Deserialize)]
struct ManifestWire(
    String,
    String,
    String,
    String,
    i64,
    String,
    String,
    Vec<CultMeshCdnChunkRef>,
    Vec<String>,
    #[serde(with = "ordered_map")] Vec<(String, String)>,
);

/// A string map that keeps its key order and refuses a duplicate key, as `Dictionary<string, string>`
/// deserialises. `Vec<(String, String)>` is the in-memory shape; the wire shape is a msgpack map.
mod ordered_map {
    use std::fmt;

    use serde::de::{self, MapAccess, Visitor};
    use serde::{Deserializer, Serializer};

    pub fn serialize<S: Serializer>(pairs: &[(String, String)], serializer: S) -> Result<S::Ok, S::Error> {
        serializer.collect_map(pairs.iter().map(|(key, value)| (key, value)))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<(String, String)>, D::Error> {
        struct Pairs;
        impl<'de> Visitor<'de> for Pairs {
            type Value = Vec<(String, String)>;

            fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
                formatter.write_str("a map of strings")
            }

            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Self::Value, A::Error> {
                let mut seen = std::collections::HashSet::new();
                let mut pairs = Vec::new();
                while let Some((key, value)) = map.next_entry::<String, String>()? {
                    if !seen.insert(key.clone()) {
                        return Err(de::Error::custom(format!("duplicate metadata key '{key}'")));
                    }
                    pairs.push((key, value));
                }
                Ok(pairs)
            }
        }
        deserializer.deserialize_map(Pairs)
    }
}

impl From<CultMeshCdnChunkRef> for ChunkRefWire {
    fn from(value: CultMeshCdnChunkRef) -> Self {
        Self(value.chunk_hash, value.offset, value.size_bytes, value.record_key)
    }
}

impl From<ChunkRefWire> for CultMeshCdnChunkRef {
    fn from(value: ChunkRefWire) -> Self {
        Self {
            chunk_hash: value.0,
            offset: value.1,
            size_bytes: value.2,
            record_key: value.3,
        }
    }
}

impl From<CultMeshCdnArtifactManifest> for ManifestWire {
    fn from(value: CultMeshCdnArtifactManifest) -> Self {
        Self(
            value.artifact_id,
            value.kind,
            value.version,
            value.content_hash,
            value.size_bytes,
            value.mime_type,
            value.created_at_utc,
            value.chunks,
            value.tags,
            value.metadata,
        )
    }
}

impl From<ManifestWire> for CultMeshCdnArtifactManifest {
    fn from(value: ManifestWire) -> Self {
        Self {
            artifact_id: value.0,
            kind: value.1,
            version: value.2,
            content_hash: value.3,
            size_bytes: value.4,
            mime_type: value.5,
            created_at_utc: value.6,
            chunks: value.7,
            tags: value.8,
            metadata: value.9,
        }
    }
}

fn sha256_hex(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

fn chunk_record_key(hash: &str) -> String {
    format!("mesh:cdn:chunk:{hash}")
}

/// `CultMeshCdn.NormalizeHash`: an optional case-insensitive `sha256:` prefix is stripped, then the
/// rest is trimmed and lowercased. A blank hash is refused.
pub fn normalize_hash(hash: &str) -> Result<String> {
    if hash.trim().is_empty() {
        bail!("Hash must be non-empty.");
    }
    let stripped = match hash.get(..7) {
        Some(prefix) if prefix.eq_ignore_ascii_case("sha256:") => &hash[7..],
        _ => hash,
    };
    Ok(stripped.trim().to_lowercase())
}

/// A hash the client will put on the wire or compare against: normalized and not empty.
fn non_empty_hash(hash: &str) -> Result<String> {
    let normalized = normalize_hash(hash)?;
    if normalized.is_empty() {
        bail!("Hash must be non-empty.");
    }
    Ok(normalized)
}

/// `CultMeshCdn.PackArtifact`: splits `bytes` into content-addressed chunks and builds the manifest.
/// An empty body packs as one zero-length chunk, as the reference does.
pub fn pack_content(
    artifact_id: &str,
    kind: &str,
    version: &str,
    mime_type: &str,
    created_at_utc: &str,
    bytes: &[u8],
    chunk_size: usize,
) -> Result<(CultMeshCdnArtifactManifest, Vec<CultMeshCdnChunk>)> {
    if artifact_id.trim().is_empty() {
        bail!("Artifact id must be non-empty.");
    }
    if chunk_size == 0 || i32::try_from(chunk_size).is_err() {
        bail!("ChunkSizeBytes must be greater than zero and fit in an int.");
    }
    let pieces: Vec<&[u8]> = if bytes.is_empty() {
        vec![bytes]
    } else {
        bytes.chunks(chunk_size).collect()
    };
    let mut offset = 0i64;
    let mut chunks = Vec::with_capacity(pieces.len());
    let mut refs = Vec::with_capacity(pieces.len());
    for piece in pieces {
        let chunk_hash = sha256_hex(piece);
        refs.push(CultMeshCdnChunkRef {
            record_key: chunk_record_key(&chunk_hash),
            chunk_hash: chunk_hash.clone(),
            offset,
            // A piece is at most `chunk_size` bytes, which fits an i32 (checked above).
            size_bytes: piece.len() as i32,
        });
        offset += piece.len() as i64;
        chunks.push(CultMeshCdnChunk {
            chunk_hash,
            payload: piece.to_vec(),
        });
    }
    let manifest = CultMeshCdnArtifactManifest {
        artifact_id: artifact_id.to_string(),
        kind: non_blank_or(kind, "asset"),
        version: version.to_string(),
        content_hash: sha256_hex(bytes),
        size_bytes: bytes.len() as i64,
        mime_type: non_blank_or(mime_type, "application/octet-stream"),
        created_at_utc: created_at_utc.to_string(),
        chunks: refs,
        tags: Vec::new(),
        metadata: Vec::new(),
    };
    validate_manifest(&manifest)?;
    Ok((manifest, chunks))
}

fn non_blank_or(value: &str, default: &str) -> String {
    if value.trim().is_empty() {
        default.to_string()
    } else {
        value.to_string()
    }
}

/// The chunk list in offset order, as the reference reads it (`OrderBy(chunk => chunk.Offset)`).
fn in_offset_order(manifest: &CultMeshCdnArtifactManifest) -> Vec<&CultMeshCdnChunkRef> {
    let mut refs: Vec<&CultMeshCdnChunkRef> = manifest.chunks.iter().collect();
    refs.sort_by_key(|chunk| chunk.offset);
    refs
}

/// `CultMeshCdn.ValidateManifestShape`: non-negative size, chunks contiguous from offset 0 once
/// sorted by offset, non-negative chunk sizes, and chunk sizes summing to the manifest size.
pub fn validate_manifest(manifest: &CultMeshCdnArtifactManifest) -> Result<()> {
    if manifest.size_bytes < 0 {
        bail!("CDN artifact manifest has a negative size.");
    }
    let mut expected_offset = 0i64;
    for chunk in in_offset_order(manifest) {
        if chunk.offset != expected_offset {
            bail!("CDN artifact chunks are not contiguous.");
        }
        if chunk.size_bytes < 0 {
            bail!("CDN artifact chunk has a negative size.");
        }
        expected_offset = expected_offset
            .checked_add(i64::from(chunk.size_bytes))
            .ok_or_else(|| anyhow!("CDN artifact chunk sizes overflow."))?;
    }
    if expected_offset != manifest.size_bytes {
        bail!("CDN artifact chunk sizes do not sum to the manifest size.");
    }
    Ok(())
}

/// `CultMeshCdn.ValidateChunk`: the chunk's size and full SHA-256 must be what the reference and
/// the chunk's own claim say. `ref_hash` is already normalized.
fn validate_chunk(
    ref_hash: &str,
    ref_size: i32,
    claimed_hash: &str,
    claimed_size: i64,
    payload: &[u8],
) -> Result<()> {
    if i64::try_from(payload.len()).ok() != Some(i64::from(ref_size)) || claimed_size != i64::from(ref_size) {
        bail!("CDN artifact chunk payload metadata does not match its manifest reference.");
    }
    let actual = sha256_hex(payload);
    if ref_hash != actual || normalize_hash(claimed_hash)? != actual {
        bail!("CDN artifact chunk hash does not match its payload.");
    }
    Ok(())
}

/// `CultMeshLegacyRudpContentServer.HandleAsync`, in its validation order: message id, hash
/// normalisation, record-key agreement with `mesh:cdn:chunk:<hash>`, lookup, then size and hash of
/// what is served. A failure is `found: false` with an empty payload and the reference's
/// `<ExceptionKind>: <message>` in `error`; it is never silence. `lookup` takes the normalized hash.
pub fn answer_content_chunk_request<'a>(
    request: &CultNetMessage,
    lookup: impl Fn(&str) -> Option<&'a [u8]>,
) -> CultNetMessage {
    let CultNetMessage::ContentChunkRequest {
        message_id,
        chunk_hash,
        record_key,
        expected_size_bytes,
    } = request
    else {
        return CultNetMessage::Error {
            error: "not a cultmesh.content_chunk_request.v1 message".to_string(),
            code: None,
            details: None,
        };
    };
    let served = (|| -> std::result::Result<(String, Vec<u8>), String> {
        if message_id.trim().is_empty() {
            return Err("InvalidDataException: Content chunk request requires a message identity.".to_string());
        }
        // The handler normalises the wire hash, then `CreateRecordKey` normalises that result again
        // (and spells its parameter `chunkHash`); the lookup, the size check and the hash check all
        // use the second result, and the answer echoes the first.
        let blank = |parameter: &str| format!("ArgumentException: Hash must be non-empty. (Parameter '{parameter}')");
        let hash = normalize_hash(chunk_hash).map_err(|_| blank("ChunkHash"))?;
        let canonical = normalize_hash(&hash).map_err(|_| blank("chunkHash"))?;
        if !record_key.trim().is_empty() && *record_key != chunk_record_key(&canonical) {
            return Err("InvalidDataException: Content chunk record key disagrees with its content hash.".to_string());
        }
        let payload = lookup(&canonical).ok_or_else(|| "FileNotFoundException: Content chunk is not available.".to_string())?;
        validate_chunk(&canonical, *expected_size_bytes, &canonical, payload.len() as i64, payload)
            .map_err(|error| format!("InvalidDataException: {error}"))?;
        Ok((hash, payload.to_vec()))
    })();
    match served {
        Ok((hash, payload)) => CultNetMessage::ContentChunkResponse {
            message_id: message_id.clone(),
            found: true,
            chunk_hash: hash,
            size_bytes: *expected_size_bytes,
            payload,
            error: String::new(),
        },
        Err(error) => CultNetMessage::ContentChunkResponse {
            message_id: message_id.clone(),
            found: false,
            chunk_hash: chunk_hash.clone(),
            size_bytes: 0,
            payload: Vec::new(),
            error,
        },
    }
}

/// The client half: fetches every chunk of `manifest` through `ask`, one in flight, in offset
/// order, and returns the body only after each chunk and then the whole body verify. `max_bytes`
/// is checked against the manifest before the first request. `ask` sends a request on the
/// caller's session and returns the answer; it owns transport, this function owns verification.
pub fn fetch_content(
    manifest: &CultMeshCdnArtifactManifest,
    max_bytes: u64,
    mut ask: impl FnMut(CultNetMessage) -> Result<CultNetMessage>,
) -> Result<Vec<u8>> {
    validate_manifest(manifest)?;
    if manifest.size_bytes as u64 > max_bytes {
        bail!(
            "CDN artifact of {} bytes exceeds the {max_bytes}-byte cap.",
            manifest.size_bytes
        );
    }
    // Everything the client will send or compare is settled before the first request. A zero-size
    // chunk is legal only as an empty body's single chunk (the reference packs it so); anywhere else
    // it is a request that yields nothing, and a manifest of them is a request flood.
    let ordered = in_offset_order(manifest);
    if ordered.len() > 1 && ordered.iter().any(|chunk| chunk.size_bytes == 0) {
        bail!("CDN artifact has a zero-size chunk beside other chunks.");
    }
    let content_hash = non_empty_hash(&manifest.content_hash)?;
    let hashes = ordered
        .iter()
        .map(|chunk| non_empty_hash(&chunk.chunk_hash))
        .collect::<Result<Vec<_>>>()?;
    let mut body = Vec::new();
    for (chunk, hash) in ordered.into_iter().zip(hashes) {
        let message_id = uuid::Uuid::new_v4().simple().to_string();
        let record_key = if chunk.record_key.trim().is_empty() {
            chunk_record_key(&hash)
        } else {
            chunk.record_key.clone()
        };
        let answer = ask(CultNetMessage::ContentChunkRequest {
            message_id: message_id.clone(),
            chunk_hash: hash.clone(),
            record_key,
            expected_size_bytes: chunk.size_bytes,
        })?;
        let CultNetMessage::ContentChunkResponse {
            message_id: answered,
            found,
            chunk_hash,
            size_bytes,
            payload,
            error,
        } = answer
        else {
            bail!("chunk '{hash}' was answered with something other than a content chunk response");
        };
        if answered != message_id {
            bail!("chunk '{hash}' was answered for message '{answered}', not '{message_id}'");
        }
        if !found {
            bail!("content provider rejected chunk '{hash}': {error}");
        }
        validate_chunk(&hash, chunk.size_bytes, &chunk_hash, i64::from(size_bytes), &payload)?;
        body.extend_from_slice(&payload);
    }
    if sha256_hex(&body) != content_hash {
        bail!("CDN artifact content hash does not match its manifest.");
    }
    Ok(body)
}
