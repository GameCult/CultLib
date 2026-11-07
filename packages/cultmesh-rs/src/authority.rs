//! CultMesh authority route verification.
//!
//! Port of `src/GameCult.Mesh/CultMeshAuthorityProof.cs` (the reference) and of
//! the route constructors in `CultMeshVerse.cs`. Rule for rule, in the same
//! order, the same input yields the same refusal text as
//! `CultMeshAuthorityTrustPolicy.Verify`: unsigned, channel protection, root
//! lookup, validity window, signature.
//!
//! Shared vectors: `contracts/cultmesh/authority-route-vectors.json` (C# written).

use base64::alphabet::STANDARD as STANDARD_ALPHABET;
use base64::engine::{DecodePaddingMode, GeneralPurpose, GeneralPurposeConfig};
use base64::Engine;
use p256::ecdsa::signature::Verifier;
use p256::ecdsa::{Signature, VerifyingKey};
use std::collections::BTreeMap;
use std::fmt;

const ROUTE_DOMAIN: &str = "gamecult.cultmesh.route-certificate.v1";
const SESSION_DOMAIN: &str = "gamecult.cultmesh.session-proof.v1";

// `Convert.FromBase64String`: standard alphabet, padded, tolerant of trailing bits.
const DOTNET_BASE64: GeneralPurpose = GeneralPurpose::new(
    &STANDARD_ALPHABET,
    GeneralPurposeConfig::new()
        .with_decode_padding_mode(DecodePaddingMode::RequireCanonical)
        .with_decode_allow_trailing_bits(true),
);

fn base64_decode(value: &str) -> Option<Vec<u8>> {
    let stripped: String = value.chars().filter(|c| !matches!(c, ' ' | '\t' | '\r' | '\n')).collect();
    DOTNET_BASE64.decode(stripped).ok()
}

/// .NET `char.IsWhiteSpace` is the Unicode White_Space property, as is `char::is_whitespace`.
fn trim(value: &str) -> &str {
    value.trim_matches(char::is_whitespace)
}

fn is_blank(value: &str) -> bool {
    trim(value).is_empty()
}

fn require(value: &str, name: &str) -> Result<String, String> {
    if is_blank(value) {
        Err(format!("Value must be non-empty. (Parameter '{name}')"))
    } else {
        Ok(trim(value).to_owned())
    }
}

/// A route or proof the consumer's trust policy refuses. Mirrors
/// `CultMeshSessionFailure` with reason `Authentication`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuthorityRouteRefusal {
    pub message: String,
    pub endpoint: String,
}

impl AuthorityRouteRefusal {
    pub const REASON: &'static str = "authentication";
}

impl fmt::Display for AuthorityRouteRefusal {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for AuthorityRouteRefusal {}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct P256PublicKey {
    pub key_id: String,
    pub x: String,
    pub y: String,
}

impl P256PublicKey {
    pub fn new(key_id: &str, x: &str, y: &str) -> Result<Self, String> {
        let key = Self {
            key_id: require(key_id, "keyId")?,
            x: require(x, "x")?,
            y: require(y, "y")?,
        };
        for (name, value) in [("x", &key.x), ("y", &key.y)] {
            if base64_decode(value).is_none() {
                return Err(format!("Coordinate must be base64. (Parameter '{name}')"));
            }
        }
        Ok(key)
    }

    /// IEEE P1363 (r || s) ECDSA-SHA256 verification; false on any malformation.
    pub fn verify(&self, payload: &[u8], signature_base64: &str) -> bool {
        let (Some(x), Some(y), Some(signature)) = (
            base64_decode(&self.x),
            base64_decode(&self.y),
            base64_decode(signature_base64),
        ) else {
            return false;
        };
        if x.len() != 32 || y.len() != 32 || signature.len() != 64 {
            return false;
        }
        let mut sec1 = Vec::with_capacity(65);
        sec1.push(4);
        sec1.extend_from_slice(&x);
        sec1.extend_from_slice(&y);
        let (Ok(key), Ok(signature)) = (
            VerifyingKey::from_sec1_bytes(&sec1),
            Signature::from_slice(&signature),
        ) else {
            return false;
        };
        key.verify(payload, &signature).is_ok()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RouteCertificate {
    pub provider_key: P256PublicKey,
    pub odin_key_id: String,
    pub issued_at_unix_milliseconds: i64,
    pub expires_at_unix_milliseconds: i64,
    pub signature: String,
}

impl RouteCertificate {
    pub fn new(
        provider_key: P256PublicKey,
        odin_key_id: &str,
        issued_at_unix_milliseconds: i64,
        expires_at_unix_milliseconds: i64,
        signature: &str,
    ) -> Result<Self, String> {
        if is_blank(odin_key_id) {
            return Err("Odin key identity is required. (Parameter 'odinKeyId')".into());
        }
        if issued_at_unix_milliseconds < 0 {
            return Err("issuedAtUnixMilliseconds must not be negative.".into());
        }
        if expires_at_unix_milliseconds <= issued_at_unix_milliseconds {
            return Err("expiresAtUnixMilliseconds must be after issuedAtUnixMilliseconds.".into());
        }
        Ok(Self {
            provider_key,
            odin_key_id: trim(odin_key_id).to_owned(),
            issued_at_unix_milliseconds,
            expires_at_unix_milliseconds,
            signature: trim(signature).to_owned(),
        })
    }
}

/// `CultMeshAuthorityRoute`: construction cleans every field as the C# does.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuthorityRoute {
    pub authority_runtime_id: String,
    pub endpoint: String,
    pub protocol_ids: Vec<String>,
    pub priority: i32,
    pub generation: String,
    pub certificate: Option<RouteCertificate>,
}

impl AuthorityRoute {
    pub fn new(
        authority_runtime_id: &str,
        endpoint: &str,
        protocol_ids: &[&str],
        priority: i32,
        generation: Option<&str>,
        certificate: Option<RouteCertificate>,
    ) -> Result<Self, String> {
        let authority_runtime_id = require(authority_runtime_id, "authorityRuntimeId")?;
        let endpoint = require(endpoint, "endpoint")?;
        if priority < 0 {
            return Err("priority must not be negative.".into());
        }
        let mut protocols: Vec<String> = protocol_ids
            .iter()
            .filter(|value| !is_blank(value))
            .map(|value| trim(value).to_owned())
            .collect();
        // StringComparer.Ordinal is UTF-16 code-unit order.
        protocols.sort_by_key(|value| value.encode_utf16().collect::<Vec<u16>>());
        protocols.dedup();
        let generation = match generation {
            Some(value) if !is_blank(value) => trim(value).to_owned(),
            _ => format!("{authority_runtime_id}@{endpoint}"),
        };
        Ok(Self {
            authority_runtime_id,
            endpoint,
            protocol_ids: protocols,
            priority,
            generation,
            certificate,
        })
    }
}

/// The fields of `CultMeshSessionOpenMessage` the session proof covers.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SessionOpen {
    pub client_nonce: String,
    pub message_id: String,
    pub source_runtime_id: String,
    pub verse_id: String,
    pub authority_runtime_id: String,
    pub protocol_id: String,
    pub route_generation: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SessionAccepted {
    pub client_nonce: String,
    pub provider_key_id: String,
    pub provider_signature: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TrustMode {
    AuthenticatedRemote,
    LocalDevelopment,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerifiedRoute {
    pub route: AuthorityRoute,
    pub provider_key: Option<P256PublicKey>,
    pub is_local_development: bool,
}

pub fn canonical_fields(values: &[&str]) -> Vec<u8> {
    let mut out = Vec::new();
    for value in values {
        out.extend_from_slice(&(value.len() as u32).to_be_bytes());
        out.extend_from_slice(value.as_bytes());
    }
    out
}

pub fn canonical_route(verse_id: &str, route: &AuthorityRoute) -> Result<Vec<u8>, String> {
    let certificate = route.certificate.as_ref().ok_or("Route certificate is required.")?;
    let protocols = route.protocol_ids.join("\u{1f}");
    let priority = route.priority.to_string();
    let issued = certificate.issued_at_unix_milliseconds.to_string();
    let expires = certificate.expires_at_unix_milliseconds.to_string();
    Ok(canonical_fields(&[
        ROUTE_DOMAIN,
        verse_id,
        &route.authority_runtime_id,
        &route.endpoint,
        &protocols,
        &priority,
        &route.generation,
        &certificate.provider_key.key_id,
        &certificate.provider_key.x,
        &certificate.provider_key.y,
        &certificate.odin_key_id,
        &issued,
        &expires,
    ]))
}

pub fn canonical_session(request: &SessionOpen, endpoint: &str) -> Vec<u8> {
    canonical_fields(&[
        SESSION_DOMAIN,
        &request.client_nonce,
        &request.message_id,
        &request.source_runtime_id,
        &request.verse_id,
        &request.authority_runtime_id,
        &request.protocol_id,
        endpoint,
        &request.route_generation,
    ])
}

/// `(scheme, lowercase host)`, or `None` where `System.Uri` refuses the spelling.
/// A lenient parse could call a refused endpoint loopback or protected, so the
/// shape rule is the same strict one the TypeScript port applies.
fn split_endpoint(value: &str) -> Option<(String, String)> {
    let raw = value.trim_matches(|c: char| c <= ' ');
    if raw.contains('\\') {
        return None;
    }
    let (scheme, rest) = raw.split_once("://")?;
    let mut scheme_chars = scheme.chars();
    if !scheme_chars.next()?.is_ascii_alphabetic()
        || !scheme_chars.all(|c| c.is_ascii_alphanumeric() || matches!(c, '+' | '-' | '.'))
    {
        return None;
    }
    let authority = &rest[..rest.find(['/', '?', '#']).unwrap_or(rest.len())];
    if authority.is_empty() {
        return None;
    }
    let after_user = &authority[authority.rfind('@').map_or(0, |i| i + 1)..];
    let host = if after_user.starts_with('[') {
        &after_user[..after_user.find(']').map_or(after_user.len(), |i| i + 1)]
    } else {
        after_user.split(':').next().unwrap_or("")
    };
    if host.is_empty() || host.chars().any(|c| !('\u{21}'..='\u{7e}').contains(&c)) || host.ends_with('.') {
        return None;
    }
    Some((
        scheme.to_ascii_lowercase(),
        host.trim_matches(['[', ']']).to_ascii_lowercase(),
    ))
}

pub fn is_loopback_endpoint(endpoint: &str) -> bool {
    let Some((_, host)) = split_endpoint(endpoint) else {
        return false;
    };
    let octets: Vec<&str> = host.split('.').collect();
    matches!(host.as_str(), "localhost" | "loopback" | "::1" | "::ffff:127.0.0.1")
        || (octets.len() == 4
            && octets[0] == "127"
            && octets[1..].iter().all(|o| (1..=3).contains(&o.len()) && o.bytes().all(|b| b.is_ascii_digit())))
}

pub fn is_protected_endpoint(endpoint: &str) -> bool {
    split_endpoint(endpoint)
        .is_some_and(|(scheme, _)| scheme == "wss" || scheme == "https" || scheme.contains("quic"))
}

/// Consumer-owned policy. Fails closed unless a route is valid under it.
pub struct AuthorityTrustPolicy {
    mode: TrustMode,
    roots: BTreeMap<String, P256PublicKey>,
}

impl AuthorityTrustPolicy {
    pub fn new(mode: TrustMode, odin_roots: Vec<P256PublicKey>) -> Result<Self, String> {
        let mut roots = BTreeMap::new();
        for root in odin_roots {
            let key_id = root.key_id.clone();
            if roots.insert(key_id.clone(), root).is_some() {
                return Err(format!(
                    "Odin root key id '{key_id}' is listed more than once in the trust policy."
                ));
            }
        }
        Ok(Self { mode, roots })
    }

    pub fn validate(&self, verse_id: &str, route: &AuthorityRoute, now_unix_milliseconds: i64) -> Result<(), AuthorityRouteRefusal> {
        self.verify(verse_id, route, now_unix_milliseconds).map(|_| ())
    }

    pub fn verify(&self, verse_id: &str, route: &AuthorityRoute, now_unix_milliseconds: i64) -> Result<VerifiedRoute, AuthorityRouteRefusal> {
        let refuse = |message: &str| AuthorityRouteRefusal {
            message: message.to_owned(),
            endpoint: route.endpoint.clone(),
        };
        let local = self.mode == TrustMode::LocalDevelopment && is_loopback_endpoint(&route.endpoint);
        let certificate = match &route.certificate {
            Some(certificate) if !is_blank(&certificate.signature) => certificate,
            _ if local => {
                return Ok(VerifiedRoute { route: route.clone(), provider_key: None, is_local_development: true });
            }
            _ => return Err(refuse("Remote CultMesh routes require an Odin-signed authority certificate.")),
        };
        if !is_protected_endpoint(&route.endpoint) && !local {
            return Err(refuse("Authenticated remote CultMesh routes require TLS or QUIC channel protection."));
        }
        let Some(root) = self.roots.get(&certificate.odin_key_id) else {
            return Err(refuse(&format!(
                "Odin key '{}' is not trusted by this consumer.",
                certificate.odin_key_id
            )));
        };
        if now_unix_milliseconds < certificate.issued_at_unix_milliseconds
            || now_unix_milliseconds >= certificate.expires_at_unix_milliseconds
        {
            return Err(refuse("The Odin route certificate is not currently valid."));
        }
        let Some(signature) = base64_decode(&certificate.signature) else {
            return Err(refuse("The Odin route signature is not base64."));
        };
        if signature.len() != 64 {
            return Err(refuse("The Odin route signature is not IEEE P1363 P-256."));
        }
        let canonical = canonical_route(verse_id, route).map_err(|message| refuse(&message))?;
        if !root.verify(&canonical, &certificate.signature) {
            return Err(refuse("The Odin route certificate signature is invalid."));
        }
        Ok(VerifiedRoute {
            route: route.clone(),
            provider_key: Some(certificate.provider_key.clone()),
            is_local_development: false,
        })
    }
}

/// Verifies the Odin route certificate (refusing) and the provider nonce proof.
pub fn verify_session_proof(
    request: &SessionOpen,
    response: &SessionAccepted,
    verse_id: &str,
    route: &AuthorityRoute,
    trust: &AuthorityTrustPolicy,
    now_unix_milliseconds: i64,
) -> Result<bool, AuthorityRouteRefusal> {
    let verified = trust.verify(verse_id, route, now_unix_milliseconds)?;
    if verified.is_local_development {
        return Ok(true);
    }
    let Some(key) = verified.provider_key else {
        return Ok(false);
    };
    if response.provider_key_id != key.key_id
        || response.client_nonce != request.client_nonce
        || is_blank(&response.provider_signature)
    {
        return Ok(false);
    }
    Ok(key.verify(&canonical_session(request, &route.endpoint), &response.provider_signature))
}
