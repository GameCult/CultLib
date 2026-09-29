//! The content plane (`src/content.rs`, the two chunk messages in `src/contracts.rs`), pinned against
//! the C# reference. `contracts/cultmesh/content-vectors.cs-written.json` is the reference's own
//! bytes (written by `CultMeshContentVectorTests`, including the answers `CultMeshLegacyRudpContentServer`
//! gives); `content-vectors.rs-written.json` is this binary's mirror, regenerated with
//! `CULTNET_WRITE_VECTORS=1`, and judged by the C# side.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use anyhow::Result;
use base64::Engine;
use cultnet_rs::{
    CultMeshCdnArtifactManifest, CultNetMessage, CultNetWireContract, answer_content_chunk_request,
    decode_cultnet_message_from_slice, encode_cultnet_message_for_wire, encode_cultnet_message_to_vec,
    fetch_content, normalize_hash, pack_content, validate_manifest,
};
use serde::{Deserialize, Serialize};

const CHUNK: usize = 262_144;
const BODY_LEN: usize = 1_315_551;
const CREATED: &str = "2026-09-22T00:00:00.0000000+00:00";
const SCHEMA_V0: CultNetWireContract = CultNetWireContract::CultNetSchemaV0;

fn body() -> Vec<u8> {
    (0..BODY_LEN).map(|i| (i % 251) as u8).collect()
}

fn pack(bytes: &[u8]) -> (CultMeshCdnArtifactManifest, Vec<cultnet_rs::CultMeshCdnChunk>) {
    pack_content(
        "eureka/content-vector",
        "asset",
        "1",
        "application/x-cultnet-vector",
        CREATED,
        bytes,
        CHUNK,
    )
    .expect("packs")
}

fn store(chunks: &[cultnet_rs::CultMeshCdnChunk]) -> HashMap<String, Vec<u8>> {
    chunks
        .iter()
        .map(|chunk| (chunk.chunk_hash.clone(), chunk.payload.clone()))
        .collect()
}

fn lookup_in<'a>(store: &'a HashMap<String, Vec<u8>>) -> impl Fn(&str) -> Option<&'a [u8]> {
    move |hash| store.get(hash).map(Vec::as_slice)
}

fn wire(message: &CultNetMessage) -> Vec<u8> {
    encode_cultnet_message_to_vec(message, SCHEMA_V0).expect("encodes")
}

fn unwire(bytes: &[u8]) -> Result<CultNetMessage> {
    decode_cultnet_message_from_slice(bytes, SCHEMA_V0)
}

fn manifest_bytes(manifest: &CultMeshCdnArtifactManifest) -> Vec<u8> {
    rmp_serde::to_vec_named(manifest).expect("encodes")
}

fn decode_manifest(bytes: &[u8]) -> CultMeshCdnArtifactManifest {
    rmp_serde::from_slice(bytes).expect("decodes")
}

/// The manifest's bytes with its metadata map replaced by `pairs`, written in exactly that order
/// (and with duplicates, if given): what a peer that is not this crate can put on the wire.
fn manifest_with_metadata(manifest: &CultMeshCdnArtifactManifest, pairs: &[(&str, &str)]) -> Vec<u8> {
    let encoded = manifest_bytes(manifest);
    let mut value = rmpv::decode::read_value(&mut encoded.as_slice()).expect("a msgpack value");
    let rmpv::Value::Array(fields) = &mut value else {
        panic!("the manifest is a positional array");
    };
    fields[9] = rmpv::Value::Map(pairs.iter().map(|(k, v)| (rmpv::Value::from(*k), rmpv::Value::from(*v))).collect());
    let mut out = Vec::new();
    rmpv::encode::write_value(&mut out, &value).expect("writes");
    out
}

fn request(id: &str, hash: &str, key: &str, size: i32) -> CultNetMessage {
    CultNetMessage::ContentChunkRequest {
        message_id: id.to_string(),
        chunk_hash: hash.to_string(),
        record_key: key.to_string(),
        expected_size_bytes: size,
    }
}

fn response_of(message: &CultNetMessage) -> (&str, bool, &str, &[u8], &str) {
    match message {
        CultNetMessage::ContentChunkResponse {
            message_id,
            found,
            chunk_hash,
            payload,
            error,
            ..
        } => (message_id, *found, chunk_hash, payload, error),
        other => panic!("not a content chunk response: {other:?}"),
    }
}

/// A transport closure that answers from `chunks` through the wire, counting calls.
fn serving<'a>(
    chunks: &'a HashMap<String, Vec<u8>>,
    calls: &'a std::cell::Cell<usize>,
) -> impl FnMut(CultNetMessage) -> Result<CultNetMessage> + 'a {
    move |message| {
        calls.set(calls.get() + 1);
        let request = unwire(&wire(&message))?;
        unwire(&wire(&answer_content_chunk_request(&request, lookup_in(chunks))))
    }
}

fn flip_last_digit(hash: &str) -> String {
    let last = if hash.ends_with('0') { '1' } else { '0' };
    format!("{}{last}", &hash[..hash.len() - 1])
}

// ------------------------------------------------------------------------------------------
// Vector files
// ------------------------------------------------------------------------------------------

#[derive(Serialize, Deserialize)]
struct VectorFile {
    vectors: Vec<VectorEntry>,
}

#[derive(Serialize, Deserialize)]
struct VectorEntry {
    label: String,
    kind: String,
    #[serde(rename = "messagePackBase64")]
    message_pack_base64: String,
}

struct Vector {
    label: String,
    kind: &'static str,
    bytes: Vec<u8>,
}

fn vector_path(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("..")
        .join("..")
        .join("contracts")
        .join("cultmesh")
        .join(name)
}

fn read_vectors(name: &str) -> Vec<(String, String, Vec<u8>)> {
    let path = vector_path(name);
    let text = std::fs::read_to_string(&path).unwrap_or_else(|_| {
        panic!("{} is missing - write it first (CULTNET_WRITE_VECTORS=1)", path.display())
    });
    let file: VectorFile = serde_json::from_str(&text).expect("vector file parses");
    assert!(!file.vectors.is_empty(), "the vector file must carry at least one vector");
    file.vectors
        .into_iter()
        .map(|v| {
            let bytes = base64::engine::general_purpose::STANDARD
                .decode(&v.message_pack_base64)
                .expect("base64");
            (v.label, v.kind, bytes)
        })
        .collect()
}

/// The vectors this runtime writes: the same labelled set the C# writer produces, built from Rust
/// values and answered by Rust's `answer_content_chunk_request`.
fn local_vectors() -> Vec<Vector> {
    let (manifest, chunks) = pack(&body());
    let store = store(&chunks);
    let last = chunks.last().expect("chunks");
    let last_size = last.payload.len() as i32;
    let key = format!("mesh:cdn:chunk:{}", last.chunk_hash);
    let mut vectors = Vec::new();
    let mut push_message = |label: String, kind: &'static str, message: &CultNetMessage| {
        vectors.push(Vector { label, kind, bytes: wire(message) });
    };
    let answers = [
        ("answer_found", request("vector-found", &format!("SHA256:{}", last.chunk_hash), &key, last_size)),
        ("answer_not_found", request("vector-missing", &"0".repeat(64), "", 100)),
        (
            "answer_record_key_disagrees",
            request("vector-key", &last.chunk_hash, "mesh:cdn:chunk:other", last_size),
        ),
        ("answer_size_mismatch", request("vector-size", &last.chunk_hash, &key, last_size - 1)),
        ("answer_blank_message_id", request("", &last.chunk_hash, &key, last_size)),
        ("answer_whitespace_message_id", request("   ", &last.chunk_hash, &key, last_size)),
        ("answer_blank_hash", request("vector-blank-hash", "", "", 100)),
        ("answer_whitespace_hash", request("vector-space-hash", "   ", "", 100)),
        ("answer_prefix_only_hash", request("vector-prefix-hash", "sha256:   ", "", 100)),
        ("answer_negative_size", request("vector-negative", &last.chunk_hash, &key, -1)),
        (
            "answer_record_key_case",
            request("vector-key-case", &last.chunk_hash, &key.to_uppercase(), last_size),
        ),
        ("answer_blank_record_key", request("vector-key-blank", &last.chunk_hash, "   ", last_size)),
        (
            "answer_double_prefix",
            request("vector-double", &format!("sha256:sha256:{}", last.chunk_hash), "", last_size),
        ),
    ];
    for (name, request) in &answers {
        push_message(format!("{name}.request"), "request", request);
        let answer = answer_content_chunk_request(request, lookup_in(&store));
        push_message(format!("{name}.response"), "response", &answer);
    }
    push_message(
        "response_hash_last_digit".to_string(),
        "response",
        &CultNetMessage::ContentChunkResponse {
            message_id: "vector-digit".to_string(),
            found: true,
            chunk_hash: flip_last_digit(&last.chunk_hash),
            size_bytes: last_size,
            payload: last.payload.clone(),
            error: String::new(),
        },
    );
    let mut push_manifest = |label: &str, manifest: &CultMeshCdnArtifactManifest| {
        vectors.push(Vector {
            label: label.to_string(),
            kind: "manifest",
            bytes: manifest_bytes(manifest),
        });
    };
    push_manifest("manifest", &manifest);
    let mut shuffled = manifest.clone();
    shuffled.chunks = [3, 0, 5, 1, 4, 2].iter().map(|&i| manifest.chunks[i].clone()).collect();
    push_manifest("manifest_out_of_order", &shuffled);
    let tagged = decode_manifest(&manifest_with_metadata(&manifest, &[("zeta", "1"), ("alpha", "2"), ("mid", "3")]));
    push_manifest("manifest_metadata_order", &tagged);
    push_manifest("manifest_empty_body", &pack(&[]).0);
    vectors
}

fn to_json(vectors: &[Vector]) -> String {
    let file = VectorFile {
        vectors: vectors
            .iter()
            .map(|v| VectorEntry {
                label: v.label.clone(),
                kind: v.kind.to_string(),
                message_pack_base64: base64::engine::general_purpose::STANDARD.encode(&v.bytes),
            })
            .collect(),
    };
    serde_json::to_string_pretty(&file).expect("serializes")
}

/// The committed rs-written file is what this binary encodes now. Under `CULTNET_WRITE_VECTORS=1`
/// it is rewritten instead, for the C# test `RustVectorsDecodeInTheReference` to judge.
#[test]
fn rust_written_vectors_are_current() {
    let fresh = local_vectors();
    let path = vector_path("content-vectors.rs-written.json");
    if std::env::var("CULTNET_WRITE_VECTORS").as_deref() == Ok("1") {
        std::fs::write(&path, to_json(&fresh)).expect("writes the vector file");
        return;
    }
    let committed = read_vectors("content-vectors.rs-written.json");
    assert_eq!(committed.len(), fresh.len());
    for ((label, kind, bytes), fresh) in committed.iter().zip(&fresh) {
        assert_eq!((label.as_str(), kind.as_str()), (fresh.label.as_str(), fresh.kind));
        assert_eq!(bytes, &fresh.bytes, "vector '{label}' drifted from this binary's encode");
    }
}

/// Byte identity, both ways, against the reference's own bytes: every vector the reference wrote
/// decodes here and this runtime's independently built value encodes to exactly those bytes. The
/// manifest vector also pins `pack_content` (same body and options, same manifest, byte for byte).
#[test]
fn reference_vectors_encode_byte_identically() {
    let local = local_vectors();
    let reference = read_vectors("content-vectors.cs-written.json");
    assert_eq!(
        reference.iter().map(|(l, _, _)| l.as_str()).collect::<Vec<_>>(),
        local.iter().map(|v| v.label.as_str()).collect::<Vec<_>>(),
        "both runtimes write the same labelled vectors"
    );
    for ((label, kind, reference_bytes), mine) in reference.iter().zip(&local) {
        assert_eq!(reference_bytes, &mine.bytes, "vector '{label}' is not byte identical");
        match kind.as_str() {
            "request" | "response" => {
                let decoded = unwire(reference_bytes).expect("the reference's bytes decode");
                assert_eq!(&wire(&decoded), reference_bytes, "vector '{label}' does not re-encode identically");
            }
            "manifest" => {
                let decoded: CultMeshCdnArtifactManifest =
                    rmp_serde::from_slice(reference_bytes).expect("the reference's manifest decodes");
                assert_eq!(&manifest_bytes(&decoded), reference_bytes, "manifest '{label}' does not re-encode identically");
            }
            other => panic!("unknown vector kind {other}"),
        }
    }
}

/// F1's failure mode: a chunk response carries `bin`, which the JSON-value path cannot carry.
#[test]
fn a_chunk_response_carries_bin_through_the_raw_path() {
    let payload: Vec<u8> = (0..=255u8).collect();
    let message = CultNetMessage::ContentChunkResponse {
        message_id: "m".to_string(),
        found: true,
        chunk_hash: "ab".to_string(),
        size_bytes: 256,
        payload: payload.clone(),
        error: String::new(),
    };
    let wire_value = encode_cultnet_message_for_wire(&message, SCHEMA_V0).expect("encodes");
    let entries = wire_value.as_map().expect("a map");
    let (_, value) = entries.iter().find(|(k, _)| k.as_str() == Some("payload")).expect("payload key");
    assert!(matches!(value, rmpv::Value::Binary(bytes) if *bytes == payload), "payload must be bin");
    assert_eq!(unwire(&wire(&message)).expect("decodes"), message);
}

#[test]
fn normalize_hash_matches_the_reference() {
    for (input, expected) in [
        ("SHA256:ABCdef", "abcdef"),
        ("sha256:abc", "abc"),
        ("sha256: ABC ", "abc"),
        ("  ABC  ", "abc"),
        (" sha256:abc", "sha256:abc"),
        ("sha256:", ""),
        ("plain", "plain"),
    ] {
        assert_eq!(normalize_hash(input).expect("normalizes"), expected, "input {input:?}");
    }
    for blank in ["", "   "] {
        assert!(normalize_hash(blank).is_err(), "{blank:?} must be refused");
    }
    // The fixture's uppercase-prefix request names the same chunk as its bare hash.
    let reference = read_vectors("content-vectors.cs-written.json");
    let (_, _, bytes) = reference.iter().find(|(l, _, _)| l == "answer_found.request").expect("vector");
    let CultNetMessage::ContentChunkRequest { chunk_hash, .. } = unwire(bytes).expect("decodes") else {
        panic!("not a request");
    };
    assert!(chunk_hash.starts_with("SHA256:"));
    let (_, chunks) = pack(&body());
    assert_eq!(normalize_hash(&chunk_hash).expect("normalizes"), chunks.last().unwrap().chunk_hash);
}

#[test]
fn a_manifest_out_of_offset_order_is_accepted_as_the_reference_accepts_it() {
    let reference = read_vectors("content-vectors.cs-written.json");
    let (_, _, bytes) = reference.iter().find(|(l, _, _)| l == "manifest_out_of_order").expect("vector");
    let manifest: CultMeshCdnArtifactManifest = rmp_serde::from_slice(bytes).expect("decodes");
    assert_ne!(manifest.chunks[0].offset, 0, "the fixture's chunk list is shuffled");
    validate_manifest(&manifest).expect("the reference accepts it");
    let (_, chunks) = pack(&body());
    let store = store(&chunks);
    let calls = std::cell::Cell::new(0);
    let fetched = fetch_content(&manifest, BODY_LEN as u64, serving(&store, &calls)).expect("fetches");
    assert_eq!(fetched, body());
    assert_eq!(calls.get(), 6);
}

#[test]
fn a_hostile_manifest_is_refused_before_any_request() {
    let (good, chunks) = pack(&body());
    let store = store(&chunks);
    let mut hostile = Vec::new();
    let mut negative = good.clone();
    negative.size_bytes = -1;
    hostile.push(negative);
    let mut gap = good.clone();
    gap.chunks[2].offset += 1;
    hostile.push(gap);
    let mut overlap = good.clone();
    overlap.chunks[3].offset -= 1;
    hostile.push(overlap);
    let mut short = good.clone();
    short.size_bytes -= 1;
    hostile.push(short);
    let mut negative_chunk = good.clone();
    negative_chunk.chunks[5].size_bytes = -4831;
    hostile.push(negative_chunk);
    for manifest in hostile {
        let calls = std::cell::Cell::new(0);
        assert!(fetch_content(&manifest, u64::MAX, serving(&store, &calls)).is_err());
        assert_eq!(calls.get(), 0, "a corrupt manifest must not make the client ask");
    }
}

#[test]
fn a_manifest_over_the_cap_is_refused_before_any_request() {
    let (manifest, chunks) = pack(&body());
    let store = store(&chunks);
    let size = BODY_LEN as u64;

    let calls = std::cell::Cell::new(0);
    let fetched = fetch_content(&manifest, size, serving(&store, &calls)).expect("exactly the cap is accepted");
    assert_eq!(fetched.len(), BODY_LEN);
    assert_eq!(calls.get(), 6);

    // One byte over, and sizes where doubling the cap would admit the body.
    for cap in [size - 1, size / 2 + 1, size / 2, 0] {
        let calls = std::cell::Cell::new(0);
        assert!(fetch_content(&manifest, cap, serving(&store, &calls)).is_err(), "cap {cap} must refuse");
        assert_eq!(calls.get(), 0, "cap {cap} must refuse before any request");
    }
}

#[test]
fn a_chunk_whose_hash_differs_in_the_last_digit_is_refused() {
    let reference = read_vectors("content-vectors.cs-written.json");
    let (_, _, bytes) = reference.iter().find(|(l, _, _)| l == "response_hash_last_digit").expect("vector");
    let CultNetMessage::ContentChunkResponse { chunk_hash: claimed, payload, .. } = unwire(bytes).expect("decodes") else {
        panic!("not a response");
    };
    // A one-chunk manifest for exactly that payload: its bytes are right, only the response's claim is off.
    let (manifest, chunks) = pack(&payload);
    let honest = chunks[0].chunk_hash.clone();
    assert_ne!(claimed, honest);
    assert_eq!(claimed[..claimed.len() - 1], honest[..honest.len() - 1], "they differ in the last digit only");
    let error = fetch_content(&manifest, u64::MAX, |message| {
        let CultNetMessage::ContentChunkRequest { message_id, .. } = message else {
            panic!("not a request");
        };
        let CultNetMessage::ContentChunkResponse { found, size_bytes, payload, error, .. } =
            unwire(bytes).expect("decodes")
        else {
            panic!("not a response");
        };
        Ok(CultNetMessage::ContentChunkResponse {
            message_id,
            found,
            chunk_hash: claimed.clone(),
            size_bytes,
            payload,
            error,
        })
    })
    .expect_err("a hash off by one digit is a different hash");
    assert!(error.to_string().contains("does not match its payload"), "{error}");
}

#[test]
fn a_body_whose_chunks_verify_but_whole_hash_does_not_is_refused() {
    let (mut manifest, chunks) = pack(&body());
    manifest.content_hash = pack(b"another body").0.content_hash;
    let store = store(&chunks);
    let calls = std::cell::Cell::new(0);
    let error = fetch_content(&manifest, u64::MAX, serving(&store, &calls)).expect_err("whole body must verify");
    assert_eq!(calls.get(), 6, "every chunk verified; only the whole body is wrong");
    assert!(error.to_string().contains("content hash"), "{error}");
}

#[test]
fn a_response_for_another_message_id_is_refused() {
    let (manifest, chunks) = pack(&body());
    let store = store(&chunks);
    let error = fetch_content(&manifest, u64::MAX, |message| {
        let mut answer = answer_content_chunk_request(&message, lookup_in(&store));
        if let CultNetMessage::ContentChunkResponse { message_id, .. } = &mut answer {
            *message_id = "someone-else".to_string();
        }
        Ok(answer)
    })
    .expect_err("an answer to another request is not this answer");
    assert!(error.to_string().contains("someone-else"), "{error}");
}

#[test]
fn an_empty_body_packs_one_zero_length_chunk_and_fetches() {
    let (manifest, chunks) = pack(&[]);
    assert_eq!((manifest.chunks.len(), chunks.len(), manifest.size_bytes), (1, 1, 0));
    let store = store(&chunks);
    let calls = std::cell::Cell::new(0);
    assert_eq!(fetch_content(&manifest, 0, serving(&store, &calls)).expect("fetches"), Vec::<u8>::new());
    assert_eq!(calls.get(), 1);
}

#[test]
fn answer_refuses_a_record_key_that_disagrees_with_its_hash() {
    let (_, chunks) = pack(&body());
    let store = store(&chunks);
    let last = chunks.last().unwrap();
    let asked = std::cell::Cell::new(false);
    let answer = answer_content_chunk_request(
        &request("m", &last.chunk_hash, "mesh:cdn:chunk:other", last.payload.len() as i32),
        |hash| {
            asked.set(true);
            lookup_in(&store)(hash)
        },
    );
    let (id, found, _, payload, error) = response_of(&answer);
    assert_eq!((id, found, payload.len()), ("m", false, 0));
    assert_eq!(error, "InvalidDataException: Content chunk record key disagrees with its content hash.");
    assert!(!asked.get(), "the key check precedes the lookup, as in HandleAsync");
}

/// Every failure is an answer (`found: false`, empty payload, the reference's spelling) and equals,
/// byte for byte, what `CultMeshLegacyRudpContentServer` answered to the same request bytes.
#[test]
fn answer_serves_found_false_with_the_reference_error_spelling() {
    let (_, chunks) = pack(&body());
    let store = store(&chunks);
    let reference = read_vectors("content-vectors.cs-written.json");
    let mut judged = 0;
    for (label, _, request_bytes) in reference.iter().filter(|(l, _, _)| l.ends_with(".request")) {
        let name = label.trim_end_matches(".request");
        let request = unwire(request_bytes).expect("decodes");
        let answer = answer_content_chunk_request(&request, lookup_in(&store));
        let (_, _, expected_bytes) = reference
            .iter()
            .find(|(l, _, _)| *l == format!("{name}.response"))
            .expect("paired response");
        assert_eq!(&wire(&answer), expected_bytes, "{name}: answer differs from the reference's");
        let (_, found, _, payload, error) = response_of(&answer);
        if ["answer_found", "answer_blank_record_key", "answer_double_prefix"].contains(&name) {
            assert!(found && error.is_empty() && !payload.is_empty());
        } else {
            assert!(!found && payload.is_empty(), "{name}");
            assert!(error.contains("Exception: "), "{name}: {error}");
        }
        judged += 1;
    }
    assert_eq!(judged, 13);
}

#[test]
fn gamecult_networking_contract_refuses_content_messages() {
    let legacy = CultNetWireContract::GameCultNetworkingV0;
    let message = request("m", "abc", "", 1);
    assert!(encode_cultnet_message_for_wire(&message, legacy).is_err());
    assert!(decode_cultnet_message_from_slice(&wire(&message), legacy).is_err());
}

// ------------------------------------------------------------------------------------------
// BP-1 fixes
// ------------------------------------------------------------------------------------------

fn chunk_ref(hash: &str, offset: i64, size: i32) -> cultnet_rs::CultMeshCdnChunkRef {
    cultnet_rs::CultMeshCdnChunkRef {
        chunk_hash: hash.to_string(),
        offset,
        size_bytes: size,
        record_key: String::new(),
    }
}

/// A manifest with the given chunk list and stated size, everything else from a real pack.
fn manifest_of(size: i64, chunks: Vec<cultnet_rs::CultMeshCdnChunkRef>) -> CultMeshCdnArtifactManifest {
    let mut manifest = pack(&[]).0;
    manifest.size_bytes = size;
    manifest.chunks = chunks;
    manifest
}

// F1: a zero-size chunk is legal only as an empty body's single chunk.
#[test]
fn a_zero_size_chunk_flood_is_refused_before_any_request() {
    let (empty, empty_chunks) = pack(&[]);
    let empty_store = store(&empty_chunks);
    let flood = manifest_of(0, (0..100_000).map(|_| chunk_ref(&empty.content_hash, 0, 0)).collect());
    validate_manifest(&flood).expect("the shared shape rule accepts it, as the reference does");
    let calls = std::cell::Cell::new(0);
    assert!(fetch_content(&flood, u64::MAX, serving(&empty_store, &calls)).is_err());
    assert_eq!(calls.get(), 0, "100,000 empty chunks must not become 100,000 requests");

    // A zero-size chunk beside real ones is refused too, so skipping it is never the client's job.
    let (good, chunks) = pack(&body());
    let body_store = store(&chunks);
    let mut zero_inside = good.clone();
    let boundary = zero_inside.chunks[2].offset;
    zero_inside.chunks.insert(2, chunk_ref(&empty.content_hash, boundary, 0));
    validate_manifest(&zero_inside).expect("shape-valid");
    let calls = std::cell::Cell::new(0);
    assert!(fetch_content(&zero_inside, u64::MAX, serving(&body_store, &calls)).is_err());
    assert_eq!(calls.get(), 0);

    // An empty body still fetches, through its one zero-length chunk.
    let calls = std::cell::Cell::new(0);
    assert_eq!(fetch_content(&empty, 0, serving(&empty_store, &calls)).expect("fetches"), Vec::<u8>::new());
    assert_eq!(calls.get(), 1);
}

// F2, F5: every bad request is answered, in the reference's validation order and spelling. The
// byte-identity against the reference server's own answers is `answer_serves_found_false_...`.
#[test]
fn a_bad_request_is_answered_never_dropped() {
    let (_, chunks) = pack(&body());
    let store = store(&chunks);
    let last = chunks.last().unwrap();
    let size = last.payload.len() as i32;
    let size_mismatch = "InvalidDataException: CDN artifact chunk payload metadata does not match its manifest reference.";
    for (label, bad, spelling) in [
        (
            "blank id",
            request("", &last.chunk_hash, "", size),
            "InvalidDataException: Content chunk request requires a message identity.",
        ),
        ("blank hash", request("m", "", "", size), "ArgumentException: Hash must be non-empty. (Parameter 'ChunkHash')"),
        (
            "prefix-only hash",
            request("m", "sha256:   ", "", size),
            "ArgumentException: Hash must be non-empty. (Parameter 'chunkHash')",
        ),
        ("negative size", request("m", &last.chunk_hash, "", -1), size_mismatch),
    ] {
        let answer = answer_content_chunk_request(&bad, lookup_in(&store));
        let (_, found, _, payload, error) = response_of(&answer);
        assert!(!found && payload.is_empty(), "{label}");
        assert_eq!(error, spelling, "{label}");
        // And both travel: neither the request nor its answer is stopped at decode or encode.
        assert_eq!(unwire(&wire(&bad)).expect("the request decodes"), bad, "{label}");
        assert_eq!(unwire(&wire(&answer)).expect("the answer encodes and decodes"), answer, "{label}");
    }
}

// F3: metadata keeps the order it was read in and refuses a duplicate key.
#[test]
fn manifest_metadata_keeps_wire_order_and_refuses_a_duplicate_key() {
    let reference = read_vectors("content-vectors.cs-written.json");
    let (_, _, bytes) = reference.iter().find(|(l, _, _)| l == "manifest_metadata_order").expect("vector");
    let decoded = decode_manifest(bytes);
    assert_eq!(&manifest_bytes(&decoded), bytes, "zeta, alpha, mid re-encode as zeta, alpha, mid");

    let base = pack(&body()).0;
    let duplicated = manifest_with_metadata(&base, &[("k", "1"), ("other", "2"), ("k", "3")]);
    assert!(rmp_serde::from_slice::<CultMeshCdnArtifactManifest>(&duplicated).is_err());
    let unique = manifest_with_metadata(&base, &[("k", "1"), ("other", "2")]);
    assert_eq!(manifest_bytes(&decode_manifest(&unique)), unique);

    // Metadata that is not a map is refused, and says what was expected.
    let mut value = rmpv::decode::read_value(&mut manifest_bytes(&base).as_slice()).expect("a msgpack value");
    let rmpv::Value::Array(fields) = &mut value else {
        panic!("the manifest is a positional array");
    };
    fields[9] = rmpv::Value::from(5);
    let mut not_a_map = Vec::new();
    rmpv::encode::write_value(&mut not_a_map, &value).expect("writes");
    let error = rmp_serde::from_slice::<CultMeshCdnArtifactManifest>(&not_a_map).expect_err("not a map");
    assert!(error.to_string().contains("a map of strings"), "{error}");
}

// F4: hashes are settled before the first request.
#[test]
fn a_blank_hash_is_refused_before_any_request() {
    let (good, chunks) = pack(&body());
    let store = store(&chunks);
    let mut blank_content = good.clone();
    blank_content.content_hash = "   ".to_string();
    let mut prefix_content = good.clone();
    prefix_content.content_hash = "sha256:   ".to_string();
    let mut blank_chunk = good.clone();
    blank_chunk.chunks[3].chunk_hash = "   ".to_string();
    let mut prefix_chunk = good.clone();
    prefix_chunk.chunks[3].chunk_hash = "SHA256:  ".to_string();
    for (label, manifest) in [
        ("blank content hash", blank_content),
        ("prefix-only content hash", prefix_content),
        ("blank chunk hash at 3", blank_chunk),
        ("prefix-only chunk hash at 3", prefix_chunk),
    ] {
        let calls = std::cell::Cell::new(0);
        assert!(fetch_content(&manifest, u64::MAX, serving(&store, &calls)).is_err(), "{label}");
        assert_eq!(calls.get(), 0, "{label}: refused before any request");
    }
}

// F6: `pack_content` refuses a chunk size the reference's int cannot hold, without panicking.
#[test]
fn pack_content_refuses_an_unusable_chunk_size() {
    for size in [0usize, i32::MAX as usize + 1, usize::MAX] {
        let refused = std::panic::catch_unwind(|| pack_content("a", "", "", "", "", b"abc", size));
        assert!(matches!(refused, Ok(Err(_))), "chunk size {size} must be refused cleanly");
    }
    let (manifest, _) =
        pack_content("a", "", "", "", "", b"abc", i32::MAX as usize).expect("i32::MAX is a valid chunk size");
    assert_eq!(manifest.chunks.len(), 1);
}

// F6: `PackArtifact` defaults a blank kind and mime type and otherwise uses the value untouched.
#[test]
fn pack_defaults_only_a_blank_kind_and_mime_type_and_never_trims() {
    let packed = |kind: &str, mime: &str| pack_content("a", kind, "v", mime, CREATED, b"abc", 2).expect("packs").0;
    for blank in ["", " ", "\t \n"] {
        let manifest = packed(blank, blank);
        assert_eq!(
            (manifest.kind.as_str(), manifest.mime_type.as_str()),
            ("asset", "application/octet-stream"),
            "{blank:?}"
        );
    }
    let manifest = packed("  build ", " text/plain ");
    assert_eq!((manifest.kind.as_str(), manifest.mime_type.as_str()), ("  build ", " text/plain "));
}

/// The honest answer to `request`, taken apart so a test can lie in one field.
fn honest(request: &CultNetMessage, store: &HashMap<String, Vec<u8>>) -> (String, bool, String, i32, Vec<u8>, String) {
    match answer_content_chunk_request(request, lookup_in(store)) {
        CultNetMessage::ContentChunkResponse { message_id, found, chunk_hash, size_bytes, payload, error } => {
            (message_id, found, chunk_hash, size_bytes, payload, error)
        }
        other => panic!("{other:?}"),
    }
}

// F6: the two halves of the per-chunk check, each on its own.
#[test]
fn a_chunk_whose_length_or_claim_disagrees_with_the_manifest_is_refused() {
    let (manifest, chunks) = pack(&body());
    let store = store(&chunks);
    // Each lie maps (honest size, honest payload) to (claimed size, payload served).
    type Lie = fn(i32, Vec<u8>) -> (i32, Vec<u8>);
    let lies: [(&str, Lie); 4] = [
        ("a payload of the claimed length that is not the manifest's length", |size, mut p| {
            p.truncate(p.len() - 1);
            (size - 1, p)
        }),
        ("a payload whose length is not its claimed size, the claim being the manifest's", |size, mut p| {
            p.truncate(p.len() - 1);
            (size, p)
        }),
        ("the right payload under a wrong claimed size", |size, p| (size - 1, p)),
        ("a longer payload of its claimed length", |size, mut p| {
            p.push(0);
            (size + 1, p)
        }),
    ];
    for (label, lie) in lies {
        let error = fetch_content(&manifest, u64::MAX, |message| {
            let (message_id, found, chunk_hash, honest_size, payload, error) = honest(&message, &store);
            let (size_bytes, payload) = lie(honest_size, payload);
            Ok(CultNetMessage::ContentChunkResponse { message_id, found, chunk_hash, size_bytes, payload, error })
        })
        .expect_err(label);
        assert!(error.to_string().contains("payload metadata does not match"), "{label}: {error}");
    }
}

// F6: `[5@0, 0@0]` is not contiguous under the reference's stable sort by offset alone.
#[test]
fn equal_offsets_keep_their_manifest_order() {
    let hash = "0".repeat(64);
    let five_then_zero = manifest_of(5, vec![chunk_ref(&hash, 0, 5), chunk_ref(&hash, 0, 0)]);
    assert!(validate_manifest(&five_then_zero).is_err(), "the stable sort keeps 5@0 first, so 0@0 lands at offset 5");
    let zero_then_five = manifest_of(5, vec![chunk_ref(&hash, 0, 0), chunk_ref(&hash, 0, 5)]);
    validate_manifest(&zero_then_five).expect("0@0 first leaves 5@0 at offset 0");
}

// F6: what goes on the wire is the normalized hash and the canonical record key.
#[test]
fn the_client_sends_the_normalized_hash_and_canonical_record_key() {
    let (mut manifest, chunks) = pack(&body());
    let store = store(&chunks);
    let honest_hashes: Vec<String> = manifest.chunks.iter().map(|c| c.chunk_hash.clone()).collect();
    manifest.content_hash = format!("SHA256:{}", manifest.content_hash.to_uppercase());
    for (index, chunk) in manifest.chunks.iter_mut().enumerate() {
        chunk.chunk_hash = format!("sha256:{}", chunk.chunk_hash.to_uppercase());
        // Alternate chunks name their key; the rest leave it blank for the client to derive.
        chunk.record_key =
            if index % 2 == 0 { format!("mesh:cdn:chunk:{}", honest_hashes[index]) } else { "  ".to_string() };
    }
    let mut sent = Vec::new();
    let fetched = fetch_content(&manifest, u64::MAX, |message| {
        sent.push(message.clone());
        Ok(answer_content_chunk_request(&message, lookup_in(&store)))
    })
    .expect("fetches");
    assert_eq!(fetched, body());
    assert_eq!(sent.len(), honest_hashes.len());
    for (message, hash) in sent.iter().zip(&honest_hashes) {
        let CultNetMessage::ContentChunkRequest { chunk_hash, record_key, .. } = message else {
            panic!("not a request");
        };
        assert_eq!(chunk_hash, hash, "the normalized hash is what is sent");
        assert_eq!(record_key, &format!("mesh:cdn:chunk:{hash}"), "and the canonical key");
    }
}
