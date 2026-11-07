//! Judges `contracts/cultmesh/authority-route-vectors*.json` in Rust.
//!
//! `authority-route-vectors.json` is written by the C# reference (its signatures
//! are captured C# bytes); the `.ts-signed.json` file is signed by TypeScript
//! over awkward inputs. Each negative case fails when the matching check is
//! removed from `cultmesh_rs::authority`.

use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use cultmesh_rs::authority::*;
use p256::ecdsa::Signature;
use serde_json::Value;
use std::path::PathBuf;

struct Vector {
    now: i64,
    root: P256PublicKey,
    verse_id: String,
    route: AuthorityRoute,
    request: SessionOpen,
    accepted: SessionAccepted,
    trust: AuthorityTrustPolicy,
}

fn s<'a>(value: &'a Value, key: &str) -> &'a str {
    value[key].as_str().unwrap_or_else(|| panic!("missing string {key}"))
}

fn key(value: &Value) -> P256PublicKey {
    P256PublicKey::new(s(value, "keyId"), s(value, "x"), s(value, "y")).unwrap()
}

fn load(name: &str) -> Vector {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../contracts/cultmesh")
        .join(name);
    let doc: Value = serde_json::from_str(&std::fs::read_to_string(path).unwrap()).unwrap();
    let r = &doc["route"];
    let c = &r["certificate"];
    let certificate = RouteCertificate::new(
        key(&c["providerKey"]),
        s(c, "odinKeyId"),
        c["issuedAtUnixMilliseconds"].as_i64().unwrap(),
        c["expiresAtUnixMilliseconds"].as_i64().unwrap(),
        s(c, "signature"),
    )
    .unwrap();
    let protocols: Vec<&str> = r["protocolIds"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
    let route = AuthorityRoute::new(
        s(r, "authorityRuntimeId"),
        s(r, "endpoint"),
        &protocols,
        r["priority"].as_i64().unwrap() as i32,
        Some(s(r, "generation")),
        Some(certificate),
    )
    .unwrap();
    let p = &doc["sessionProof"];
    let q = &p["request"];
    let root = key(&doc["odinRoot"]);
    Vector {
        now: doc["nowUnixMilliseconds"].as_i64().unwrap(),
        verse_id: s(r, "verseId").to_owned(),
        route,
        request: SessionOpen {
            client_nonce: s(q, "clientNonce").into(),
            message_id: s(q, "messageId").into(),
            source_runtime_id: s(q, "sourceRuntimeId").into(),
            verse_id: s(q, "verseId").into(),
            authority_runtime_id: s(q, "authorityRuntimeId").into(),
            protocol_id: s(q, "protocolId").into(),
            route_generation: s(q, "routeGeneration").into(),
        },
        accepted: SessionAccepted {
            client_nonce: s(q, "clientNonce").into(),
            provider_key_id: s(p, "providerKeyId").into(),
            provider_signature: s(p, "providerSignature").into(),
        },
        trust: AuthorityTrustPolicy::new(TrustMode::AuthenticatedRemote, vec![root.clone()]).unwrap(),
        root,
    }
}

impl Vector {
    fn certificate(&self) -> &RouteCertificate {
        self.route.certificate.as_ref().unwrap()
    }
    fn with_certificate(&self, edit: impl FnOnce(&mut RouteCertificate)) -> AuthorityRoute {
        let mut route = self.route.clone();
        edit(route.certificate.as_mut().unwrap());
        route
    }
    fn refusal(&self, route: &AuthorityRoute, now: i64) -> String {
        let error = self.trust.validate(&self.verse_id, route, now).expect_err("route must be refused");
        assert_eq!(AuthorityRouteRefusal::REASON, "authentication");
        assert_eq!(route.endpoint, error.endpoint);
        error.message
    }
    fn proof(&self, request: &SessionOpen, accepted: &SessionAccepted) -> bool {
        verify_session_proof(request, accepted, &self.verse_id, &self.route, &self.trust, self.now).unwrap()
    }
}

const INVALID: &str = "The Odin route certificate signature is invalid.";

#[test]
fn cs_written_vector_verifies_route_and_session_proof() {
    let v = load("authority-route-vectors.json");
    v.trust.validate(&v.verse_id, &v.route, v.now).unwrap();
    assert!(v.proof(&v.request, &v.accepted));
}

#[test]
fn ts_signed_vector_verifies_and_tamper_is_refused() {
    let v = load("authority-route-vectors.ts-signed.json");
    v.trust.validate(&v.verse_id, &v.route, v.now).unwrap();
    assert!(v.proof(&v.request, &v.accepted));
    let mut tampered = v.route.clone();
    tampered.priority += 1;
    assert_eq!(INVALID, v.refusal(&tampered, v.now));
}

#[test]
fn canonical_route_is_length_prefixed_and_sorted() {
    let v = load("authority-route-vectors.json");
    let bytes = canonical_route(&v.verse_id, &v.route).unwrap();
    assert!(bytes.starts_with(b"\x00\x00\x00\x26gamecult.cultmesh.route-certificate.v1\x00\x00\x00\x08aetheria"));
    let needle = b"cultmesh.content.v1\x1fcultmesh.documents.v1";
    assert!(bytes.windows(needle.len()).any(|w| w == needle));
}

#[test]
fn tampered_payload_fields_are_refused() {
    let v = load("authority-route-vectors.json");
    let edits: Vec<Box<dyn Fn(&mut AuthorityRoute)>> = vec![
        Box::new(|r| r.priority = 8),
        Box::new(|r| r.endpoint = "wss://evil.example/mesh".into()),
        Box::new(|r| r.protocol_ids = vec!["cultmesh.documents.v1".into()]),
        Box::new(|r| r.generation = "other".into()),
        Box::new(|r| r.certificate.as_mut().unwrap().expires_at_unix_milliseconds += 1),
    ];
    for edit in edits {
        let mut route = v.route.clone();
        edit(&mut route);
        assert_eq!(INVALID, v.refusal(&route, v.now));
    }
    let error = v.trust.validate("other-verse", &v.route, v.now).unwrap_err();
    assert_eq!(INVALID, error.message);
}

#[test]
fn wrong_root_key_is_refused() {
    let v = load("authority-route-vectors.json");
    // The provider's key is a real P-256 key that did not sign this route.
    let provider = &v.certificate().provider_key;
    let impostor = P256PublicKey::new(&v.root.key_id, &provider.x, &provider.y).unwrap();
    let trust = AuthorityTrustPolicy::new(TrustMode::AuthenticatedRemote, vec![impostor]).unwrap();
    assert_eq!(INVALID, trust.validate(&v.verse_id, &v.route, v.now).unwrap_err().message);
}

#[test]
fn untrusted_odin_key_id_is_refused() {
    let v = load("authority-route-vectors.json");
    let other = P256PublicKey::new("someone-else", &v.root.x, &v.root.y).unwrap();
    let trust = AuthorityTrustPolicy::new(TrustMode::AuthenticatedRemote, vec![other]).unwrap();
    assert_eq!(
        "Odin key 'odin-vector' is not trusted by this consumer.",
        trust.validate(&v.verse_id, &v.route, v.now).unwrap_err().message
    );
}

#[test]
fn expired_and_not_yet_valid_are_refused() {
    let v = load("authority-route-vectors.json");
    let window = "The Odin route certificate is not currently valid.";
    let (issued, expires) = (v.certificate().issued_at_unix_milliseconds, v.certificate().expires_at_unix_milliseconds);
    v.trust.validate(&v.verse_id, &v.route, expires - 1).unwrap();
    v.trust.validate(&v.verse_id, &v.route, issued).unwrap();
    assert_eq!(window, v.refusal(&v.route, expires));
    assert_eq!(window, v.refusal(&v.route, issued - 1));
}

#[test]
fn unsigned_and_unprotected_routes_are_refused() {
    let v = load("authority-route-vectors.json");
    let unsigned_message = "Remote CultMesh routes require an Odin-signed authority certificate.";
    let mut unsigned = v.route.clone();
    unsigned.certificate = None;
    assert_eq!(unsigned_message, v.refusal(&unsigned, v.now));
    let blank = v.with_certificate(|c| c.signature = "  ".into());
    assert_eq!(unsigned_message, v.refusal(&blank, v.now));
    let mut plain = v.route.clone();
    plain.endpoint = "ws://provider.example/mesh".into();
    assert_eq!(
        "Authenticated remote CultMesh routes require TLS or QUIC channel protection.",
        v.refusal(&plain, v.now)
    );
}

#[test]
fn local_development_admits_only_unsigned_loopback() {
    let v = load("authority-route-vectors.json");
    let dev = AuthorityTrustPolicy::new(TrustMode::LocalDevelopment, vec![]).unwrap();
    let route = |endpoint: &str| AuthorityRoute::new("a", endpoint, &[], 0, None, None).unwrap();
    let loopback = route("ws://127.0.0.1:9/mesh");
    dev.validate(&v.verse_id, &loopback, v.now).unwrap();
    assert!(verify_session_proof(&v.request, &v.accepted, &v.verse_id, &loopback, &dev, v.now).unwrap());
    for endpoint in ["ws://provider.example/mesh", "ws://127.0.0.1./mesh", "ws:127.0.0.1"] {
        assert!(dev.validate(&v.verse_id, &route(endpoint), v.now).is_err(), "{endpoint}");
    }
    let remote = AuthorityTrustPolicy::new(TrustMode::AuthenticatedRemote, vec![]).unwrap();
    assert!(remote.validate(&v.verse_id, &loopback, v.now).is_err());
}

#[test]
fn malformed_signatures_get_their_own_refusals() {
    let v = load("authority-route-vectors.json");
    let not_base64 = v.with_certificate(|c| c.signature = "!!not base64!!".into());
    assert_eq!("The Odin route signature is not base64.", v.refusal(&not_base64, v.now));
    let short = v.with_certificate(|c| c.signature = STANDARD.encode([b'x'; 63]));
    assert_eq!("The Odin route signature is not IEEE P1363 P-256.", v.refusal(&short, v.now));
}

#[test]
fn high_s_signature_verifies_as_it_does_in_dotnet() {
    let v = load("authority-route-vectors.json");
    let raw = STANDARD.decode(&v.certificate().signature).unwrap();
    let signature = Signature::from_slice(&raw).unwrap();
    let flipped = Signature::from_scalars(*signature.r().as_ref(), -*signature.s().as_ref()).unwrap();
    let high = v.with_certificate(|c| c.signature = STANDARD.encode(flipped.to_bytes()));
    v.trust.validate(&v.verse_id, &high, v.now).unwrap();
}

#[test]
fn session_proof_rejects_replay_wrong_key_and_forgery() {
    let v = load("authority-route-vectors.json");
    let mut replayed = v.request.clone();
    replayed.client_nonce = "replayed".into();
    let mut accepted = v.accepted.clone();
    accepted.client_nonce = "replayed".into();
    assert!(!v.proof(&replayed, &accepted));
    let mut wrong_id = v.accepted.clone();
    wrong_id.provider_key_id = "other".into();
    assert!(!v.proof(&v.request, &wrong_id));
    let mut wrong_nonce = v.accepted.clone();
    wrong_nonce.client_nonce = "other".into();
    assert!(!v.proof(&v.request, &wrong_nonce));
    let mut empty = v.accepted.clone();
    empty.provider_signature = String::new();
    assert!(!v.proof(&v.request, &empty));
    let mut raw = STANDARD.decode(&v.accepted.provider_signature).unwrap();
    raw[0] ^= 1;
    let mut forged = v.accepted.clone();
    forged.provider_signature = STANDARD.encode(raw);
    assert!(!v.proof(&v.request, &forged));
    let mut moved = v.request.clone();
    moved.protocol_id = "cultmesh.content.v1".into();
    assert!(!v.proof(&moved, &v.accepted));
    let expires = v.certificate().expires_at_unix_milliseconds;
    assert!(verify_session_proof(&v.request, &v.accepted, &v.verse_id, &v.route, &v.trust, expires).is_err());
}
