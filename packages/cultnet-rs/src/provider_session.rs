use anyhow::{Result, anyhow};
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use chrono::DateTime;
use rmpv::Value;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

use crate::{CultNetMessage, CultNetRawDocumentRecord};

pub const CULTMESH_PROVIDER_SESSION_SERVICE_ID: &str = "gamecult.mesh.provider_session";
pub const CULTMESH_PROVIDER_SESSION_PAYLOAD_ENCODING: &str = "messagepack-base64";

pub const CULTMESH_PROVIDER_REGISTER_OPERATION: &str = "provider.register";
pub const CULTMESH_PROVIDER_RENEW_OPERATION: &str = "provider.renew";
pub const CULTMESH_PROVIDER_PUBLICATION_PUT_OPERATION: &str = "provider.publication.put";
pub const CULTMESH_PROVIDER_PUBLICATION_DELETE_OPERATION: &str = "provider.publication.delete";
pub const CULTMESH_PROVIDER_RECEIPT_PUT_OPERATION: &str = "provider.receipt.put";
pub const CULTMESH_PROVIDER_WITHDRAW_OPERATION: &str = "provider.withdraw";

pub const CULTMESH_PROVIDER_REGISTRATION_SCHEMA: &str = "gamecult.mesh.provider_registration.v1";
pub const CULTMESH_PROVIDER_LEASE_SCHEMA: &str = "gamecult.mesh.provider_lease.v1";
pub const CULTMESH_PROVIDER_LEASE_RENEWAL_SCHEMA: &str = "gamecult.mesh.provider_lease_renewal.v1";
pub const CULTMESH_PROVIDER_PUBLICATION_PUT_SCHEMA: &str =
    "gamecult.mesh.provider_publication_put.v1";
pub const CULTMESH_PROVIDER_PUBLICATION_DELETE_SCHEMA: &str =
    "gamecult.mesh.provider_publication_delete.v1";
pub const CULTMESH_PROVIDER_COMMAND_SCHEMA: &str = "gamecult.mesh.provider_command.v1";
pub const CULTMESH_PROVIDER_RECEIPT_PUT_SCHEMA: &str = "gamecult.mesh.provider_receipt_put.v1";
pub const CULTMESH_PROVIDER_WITHDRAWAL_SCHEMA: &str = "gamecult.mesh.provider_withdrawal.v1";
pub const CULTMESH_PROVIDER_MUTATION_ACCEPTANCE_SCHEMA: &str =
    "gamecult.mesh.provider_mutation_acceptance.v1";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CultMeshProviderOperationStatus {
    Ok,
    Conflict,
    Expired,
    Denied,
    Invalid,
}

impl CultMeshProviderOperationStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Ok => "ok",
            Self::Conflict => "conflict",
            Self::Expired => "expired",
            Self::Denied => "denied",
            Self::Invalid => "invalid",
        }
    }

    pub fn parse(value: &str) -> Result<Self> {
        match value {
            "ok" => Ok(Self::Ok),
            "conflict" => Ok(Self::Conflict),
            "expired" => Ok(Self::Expired),
            "denied" => Ok(Self::Denied),
            "invalid" => Ok(Self::Invalid),
            _ => Err(anyhow!("Unsupported provider-session status {value:?}")),
        }
    }
}

pub trait CultMeshProviderSessionPayload {
    fn validate(&self) -> Result<()>;
}

/// Provider-owned evidence carried verbatim in the RUDP Connect payload.
/// `client_session_id` distinguishes process/session generations; the optional
/// token remains opaque authorization evidence for the receiving service.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderConnectEvidenceWire {
    pub client_session_id: String,
    #[serde(default)]
    pub session_token: Option<String>,
}

impl CultMeshProviderSessionPayload for CultMeshProviderConnectEvidenceWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.client_session_id, "clientSessionId")?;
        require_optional_text(self.session_token.as_deref(), "sessionToken")
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderRegistrationWire {
    pub provider_id: String,
    pub service_instance_id: String,
    pub endpoint_id: String,
    pub verse_id: String,
    pub requested_lease_duration_ms: i32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authority_lease_id: Option<String>,
}

impl CultMeshProviderSessionPayload for CultMeshProviderRegistrationWire {
    fn validate(&self) -> Result<()> {
        require_identity(
            &self.provider_id,
            &self.service_instance_id,
            &self.endpoint_id,
            &self.verse_id,
        )?;
        require_positive(self.requested_lease_duration_ms, "requestedLeaseDurationMs")?;
        require_optional_text(self.authority_lease_id.as_deref(), "authorityLeaseId")
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderLeaseWire {
    pub provider_id: String,
    pub service_instance_id: String,
    pub endpoint_id: String,
    pub verse_id: String,
    pub lease_id: String,
    pub valid_from_utc: String,
    pub expires_at_utc: String,
}

impl CultMeshProviderSessionPayload for CultMeshProviderLeaseWire {
    fn validate(&self) -> Result<()> {
        require_identity(
            &self.provider_id,
            &self.service_instance_id,
            &self.endpoint_id,
            &self.verse_id,
        )?;
        require_text(&self.lease_id, "leaseId")?;
        let valid_from = require_timestamp(&self.valid_from_utc, "validFromUtc")?;
        let expires_at = require_timestamp(&self.expires_at_utc, "expiresAtUtc")?;
        if expires_at <= valid_from {
            return Err(anyhow!("Provider lease must expire after validFromUtc"));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderLeaseRenewalWire {
    pub lease_id: String,
    pub requested_lease_duration_ms: i32,
}

impl CultMeshProviderSessionPayload for CultMeshProviderLeaseRenewalWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.lease_id, "leaseId")?;
        require_positive(self.requested_lease_duration_ms, "requestedLeaseDurationMs")
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderPublicationPutWire {
    pub lease_id: String,
    pub publication_id: String,
    pub document: CultNetRawDocumentRecord,
}

impl CultMeshProviderSessionPayload for CultMeshProviderPublicationPutWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.lease_id, "leaseId")?;
        require_text(&self.publication_id, "publicationId")?;
        validate_raw_document(&self.document)
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderPublicationDeleteWire {
    pub lease_id: String,
    pub publication_id: String,
    pub schema_id: String,
    pub record_key: String,
}

impl CultMeshProviderSessionPayload for CultMeshProviderPublicationDeleteWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.lease_id, "leaseId")?;
        require_text(&self.publication_id, "publicationId")?;
        require_text(&self.schema_id, "schemaId")?;
        require_text(&self.record_key, "recordKey")
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderCommandWire {
    pub command_id: String,
    pub command_kind: String,
    pub provider_id: String,
    pub service_instance_id: String,
    pub payload: Value,
}

impl CultMeshProviderSessionPayload for CultMeshProviderCommandWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.command_id, "commandId")?;
        require_text(&self.command_kind, "commandKind")?;
        require_text(&self.provider_id, "providerId")?;
        require_text(&self.service_instance_id, "serviceInstanceId")
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum CultMeshProviderReceiptStateWire {
    Applied,
    Rejected,
    Failed,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderCommandReceiptWire {
    pub receipt_id: String,
    pub command_id: String,
    pub command_kind: String,
    pub provider_id: String,
    pub service_instance_id: String,
    pub state: CultMeshProviderReceiptStateWire,
    pub completed_at_utc: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

impl CultMeshProviderSessionPayload for CultMeshProviderCommandReceiptWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.receipt_id, "receiptId")?;
        require_text(&self.command_id, "commandId")?;
        require_text(&self.command_kind, "commandKind")?;
        require_text(&self.provider_id, "providerId")?;
        require_text(&self.service_instance_id, "serviceInstanceId")?;
        require_timestamp(&self.completed_at_utc, "completedAtUtc")?;
        require_optional_text(self.error.as_deref(), "error")
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderReceiptPutWire {
    pub lease_id: String,
    pub receipt: CultMeshProviderCommandReceiptWire,
}

impl CultMeshProviderSessionPayload for CultMeshProviderReceiptPutWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.lease_id, "leaseId")?;
        self.receipt.validate()
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderWithdrawalWire {
    pub lease_id: String,
}

impl CultMeshProviderSessionPayload for CultMeshProviderWithdrawalWire {
    fn validate(&self) -> Result<()> {
        require_text(&self.lease_id, "leaseId")
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultMeshProviderMutationAcceptanceWire {
    pub accepted_at_utc: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lease_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub publication_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub command_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub receipt_id: Option<String>,
}

impl CultMeshProviderSessionPayload for CultMeshProviderMutationAcceptanceWire {
    fn validate(&self) -> Result<()> {
        require_timestamp(&self.accepted_at_utc, "acceptedAtUtc")?;
        require_optional_text(self.lease_id.as_deref(), "leaseId")?;
        require_optional_text(self.publication_id.as_deref(), "publicationId")?;
        require_optional_text(self.command_id.as_deref(), "commandId")?;
        require_optional_text(self.receipt_id.as_deref(), "receiptId")
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct CultMeshProviderErrorWire {}

impl CultMeshProviderSessionPayload for CultMeshProviderErrorWire {
    fn validate(&self) -> Result<()> {
        Ok(())
    }
}

pub fn encode_provider_session_payload<T>(value: &T) -> Result<String>
where
    T: CultMeshProviderSessionPayload + Serialize,
{
    value.validate()?;
    Ok(STANDARD.encode(rmp_serde::to_vec_named(value)?))
}

pub fn encode_provider_connect_evidence(
    evidence: &CultMeshProviderConnectEvidenceWire,
) -> Result<Vec<u8>> {
    evidence.validate()?;
    Ok(rmp_serde::to_vec_named(evidence)?)
}

pub fn decode_provider_connect_evidence(
    payload: &[u8],
) -> Result<CultMeshProviderConnectEvidenceWire> {
    if payload.is_empty() {
        return Err(anyhow!(
            "Provider Connect evidence must be a non-empty MessagePack map"
        ));
    }
    let evidence: CultMeshProviderConnectEvidenceWire = rmp_serde::from_slice(payload)?;
    evidence.validate()?;
    Ok(evidence)
}

pub fn decode_provider_session_payload<T>(payload: &str) -> Result<T>
where
    T: CultMeshProviderSessionPayload + DeserializeOwned,
{
    require_text(payload, "payload")?;
    let bytes = STANDARD
        .decode(payload)
        .map_err(|error| anyhow!("Provider-session payload must be MessagePack base64: {error}"))?;
    let value: T = rmp_serde::from_slice(&bytes)?;
    value.validate()?;
    Ok(value)
}

pub fn decode_provider_command_document(
    document: &CultNetRawDocumentRecord,
) -> Result<CultMeshProviderCommandWire> {
    if document.schema_id != CULTMESH_PROVIDER_COMMAND_SCHEMA {
        return Err(anyhow!(
            "Expected provider command schema {:?}, received {:?}",
            CULTMESH_PROVIDER_COMMAND_SCHEMA,
            document.schema_id
        ));
    }
    let command: CultMeshProviderCommandWire = rmp_serde::from_slice(&document.payload)?;
    command.validate()?;
    Ok(command)
}

pub fn create_provider_session_request<T>(
    message_id: impl Into<String>,
    operation: impl Into<String>,
    payload_schema: impl Into<String>,
    payload: &T,
    source_runtime_id: Option<String>,
    target_runtime_id: Option<String>,
) -> Result<CultNetMessage>
where
    T: CultMeshProviderSessionPayload + Serialize,
{
    let message_id = message_id.into();
    let operation = operation.into();
    let payload_schema = payload_schema.into();
    require_text(&message_id, "messageId")?;
    validate_operation_schema(&operation, &payload_schema)?;
    Ok(CultNetMessage::OperationRequest {
        message_id,
        service_id: CULTMESH_PROVIDER_SESSION_SERVICE_ID.to_string(),
        operation,
        payload_schema,
        payload_encoding: CULTMESH_PROVIDER_SESSION_PAYLOAD_ENCODING.to_string(),
        payload: encode_provider_session_payload(payload)?,
        source_runtime_id,
        target_runtime_id,
    })
}

pub fn decode_provider_session_request<T>(
    request: &CultNetMessage,
    expected_operation: &str,
    expected_payload_schema: &str,
) -> Result<T>
where
    T: CultMeshProviderSessionPayload + DeserializeOwned,
{
    let CultNetMessage::OperationRequest {
        service_id,
        operation,
        payload_schema,
        payload_encoding,
        payload,
        ..
    } = request
    else {
        return Err(anyhow!("Expected a CultNet operation request"));
    };
    require_envelope(
        service_id,
        operation,
        payload_schema,
        payload_encoding,
        expected_operation,
        expected_payload_schema,
    )?;
    decode_provider_session_payload(payload)
}

pub fn create_provider_session_response<T>(
    request: &CultNetMessage,
    status: CultMeshProviderOperationStatus,
    payload_schema: impl Into<String>,
    payload: &T,
    source_runtime_id: Option<String>,
) -> Result<CultNetMessage>
where
    T: CultMeshProviderSessionPayload + Serialize,
{
    let CultNetMessage::OperationRequest {
        message_id,
        operation,
        ..
    } = request
    else {
        return Err(anyhow!("Expected a CultNet operation request"));
    };
    require_text(message_id, "messageId")?;
    require_text(operation, "operation")?;
    let payload_schema = payload_schema.into();
    require_text(&payload_schema, "payloadSchema")?;
    Ok(CultNetMessage::OperationResponse {
        message_id: message_id.clone(),
        service_id: CULTMESH_PROVIDER_SESSION_SERVICE_ID.to_string(),
        operation: operation.clone(),
        status: status.as_str().to_string(),
        payload_schema,
        payload_encoding: CULTMESH_PROVIDER_SESSION_PAYLOAD_ENCODING.to_string(),
        payload: encode_provider_session_payload(payload)?,
        diagnostics: Vec::new(),
        source_runtime_id,
    })
}

pub fn create_provider_session_error_response(
    request: &CultNetMessage,
    status: CultMeshProviderOperationStatus,
    diagnostics: Vec<String>,
    source_runtime_id: Option<String>,
) -> Result<CultNetMessage> {
    if status == CultMeshProviderOperationStatus::Ok {
        return Err(anyhow!(
            "Provider-session error responses require a non-ok status"
        ));
    }
    if diagnostics.is_empty() {
        return Err(anyhow!(
            "Provider-session error responses require diagnostics"
        ));
    }
    for diagnostic in &diagnostics {
        require_text(diagnostic, "diagnostics[]")?;
    }
    let mut response = create_provider_session_response(
        request,
        status,
        CULTMESH_PROVIDER_MUTATION_ACCEPTANCE_SCHEMA,
        &CultMeshProviderErrorWire {},
        source_runtime_id,
    )?;
    let CultNetMessage::OperationResponse {
        diagnostics: response_diagnostics,
        ..
    } = &mut response
    else {
        unreachable!("provider-session response helper returned a request")
    };
    *response_diagnostics = diagnostics;
    Ok(response)
}

pub fn decode_provider_session_response<T>(
    response: &CultNetMessage,
    expected_operation: &str,
    expected_payload_schema: &str,
) -> Result<(CultMeshProviderOperationStatus, T)>
where
    T: CultMeshProviderSessionPayload + DeserializeOwned,
{
    let CultNetMessage::OperationResponse {
        service_id,
        operation,
        status,
        payload_schema,
        payload_encoding,
        payload,
        ..
    } = response
    else {
        return Err(anyhow!("Expected a CultNet operation response"));
    };
    require_envelope(
        service_id,
        operation,
        payload_schema,
        payload_encoding,
        expected_operation,
        expected_payload_schema,
    )?;
    Ok((
        CultMeshProviderOperationStatus::parse(status)?,
        decode_provider_session_payload(payload)?,
    ))
}

fn validate_operation_schema(operation: &str, payload_schema: &str) -> Result<()> {
    let expected = match operation {
        CULTMESH_PROVIDER_REGISTER_OPERATION => CULTMESH_PROVIDER_REGISTRATION_SCHEMA,
        CULTMESH_PROVIDER_RENEW_OPERATION => CULTMESH_PROVIDER_LEASE_RENEWAL_SCHEMA,
        CULTMESH_PROVIDER_PUBLICATION_PUT_OPERATION => CULTMESH_PROVIDER_PUBLICATION_PUT_SCHEMA,
        CULTMESH_PROVIDER_PUBLICATION_DELETE_OPERATION => {
            CULTMESH_PROVIDER_PUBLICATION_DELETE_SCHEMA
        }
        CULTMESH_PROVIDER_RECEIPT_PUT_OPERATION => CULTMESH_PROVIDER_RECEIPT_PUT_SCHEMA,
        CULTMESH_PROVIDER_WITHDRAW_OPERATION => CULTMESH_PROVIDER_WITHDRAWAL_SCHEMA,
        _ => {
            return Err(anyhow!(
                "Unsupported provider-session operation {operation:?}"
            ));
        }
    };
    if payload_schema != expected {
        return Err(anyhow!(
            "Provider-session operation {operation:?} requires payload schema {expected:?}"
        ));
    }
    Ok(())
}

fn require_envelope(
    service_id: &str,
    operation: &str,
    payload_schema: &str,
    payload_encoding: &str,
    expected_operation: &str,
    expected_payload_schema: &str,
) -> Result<()> {
    if service_id != CULTMESH_PROVIDER_SESSION_SERVICE_ID {
        return Err(anyhow!(
            "Unexpected provider-session service id {service_id:?}"
        ));
    }
    if operation != expected_operation {
        return Err(anyhow!(
            "Unexpected provider-session operation {operation:?}"
        ));
    }
    if payload_schema != expected_payload_schema {
        return Err(anyhow!(
            "Unexpected provider-session payload schema {payload_schema:?}"
        ));
    }
    if payload_encoding != CULTMESH_PROVIDER_SESSION_PAYLOAD_ENCODING {
        return Err(anyhow!(
            "Unexpected provider-session payload encoding {payload_encoding:?}"
        ));
    }
    Ok(())
}

fn validate_raw_document(document: &CultNetRawDocumentRecord) -> Result<()> {
    require_text(&document.schema_id, "document.schemaId")?;
    require_text(&document.record_key, "document.recordKey")?;
    Ok(())
}

fn require_identity(
    provider_id: &str,
    service_instance_id: &str,
    endpoint_id: &str,
    verse_id: &str,
) -> Result<()> {
    require_text(provider_id, "providerId")?;
    require_text(service_instance_id, "serviceInstanceId")?;
    require_text(endpoint_id, "endpointId")?;
    require_text(verse_id, "verseId")
}

fn require_text(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(anyhow!("Provider-session {field} must not be empty"));
    }
    Ok(())
}

fn require_optional_text(value: Option<&str>, field: &str) -> Result<()> {
    if let Some(value) = value {
        require_text(value, field)?;
    }
    Ok(())
}

fn require_positive(value: i32, field: &str) -> Result<()> {
    if value <= 0 {
        return Err(anyhow!(
            "Provider-session {field} must be greater than zero"
        ));
    }
    Ok(())
}

fn require_timestamp(value: &str, field: &str) -> Result<DateTime<chrono::FixedOffset>> {
    require_text(value, field)?;
    DateTime::parse_from_rfc3339(value)
        .map_err(|error| anyhow!("Provider-session {field} must be RFC3339: {error}"))
}
