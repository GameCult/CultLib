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
        if name == "answer_found" {
            assert!(found && error.is_empty() && !payload.is_empty());
        } else {
            assert!(!found && payload.is_empty(), "{name}");
            assert!(error.contains("Exception: "), "{name}: {error}");
        }
        judged += 1;
    }
    assert_eq!(judged, 4);
}

#[test]
fn content_messages_refuse_what_the_reference_never_produces() {
    let refused = |message: CultNetMessage| assert!(encode_cultnet_message_for_wire(&message, SCHEMA_V0).is_err(), "{message:?}");
    refused(request("", "abc", "", 1));
    refused(request("m", "sha256:  ", "", 1));
    refused(request("m", "abc", "", -1));
    let response = |found: bool, size: i32, payload: &[u8], error: &str| CultNetMessage::ContentChunkResponse {
        message_id: "m".to_string(),
        found,
        chunk_hash: "abc".to_string(),
        size_bytes: size,
        payload: payload.to_vec(),
        error: error.to_string(),
    };
    refused(response(true, 3, &[1, 2], ""));
    refused(response(true, 2, &[1, 2], "boom"));
    refused(response(false, 0, &[1], "boom"));
    assert!(encode_cultnet_message_for_wire(&response(true, 2, &[1, 2], ""), SCHEMA_V0).is_ok());
    assert!(encode_cultnet_message_for_wire(&response(false, 0, &[], "boom"), SCHEMA_V0).is_ok());
}

#[test]
fn gamecult_networking_contract_refuses_content_messages() {
    let legacy = CultNetWireContract::GameCultNetworkingV0;
    let message = request("m", "abc", "", 1);
    assert!(encode_cultnet_message_for_wire(&message, legacy).is_err());
    assert!(decode_cultnet_message_from_slice(&wire(&message), legacy).is_err());
}
