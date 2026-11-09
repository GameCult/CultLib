use anyhow::Context;
use anyhow::Result;
use anyhow::anyhow;
use anyhow::bail;
use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use serde::Deserialize;
use serde::Serialize;

use crate::{Edge, RawDocumentHeader, Selection, SelectionDocumentRecord};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum CultNetWireContract {
    #[serde(rename = "cultnet.schema.v0")]
    CultNetSchemaV0,
    #[serde(rename = "gamecult.networking.v0")]
    GameCultNetworkingV0,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetSchemaKind {
    WireMessage,
    DocumentPayload,
    SharedContract,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetRawPayloadEncoding {
    Messagepack,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CultNetDocumentOperation {
    Snapshot,
    DocumentPut,
    DocumentDelete,
    IntentSubmit,
    ReceiptWatch,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CultNetMutationAuthority {
    ReadOnly,
    LocalUser,
    Coordinator,
    Runtime,
    Denied,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetDocumentMutationContract {
    pub document_type: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub payload_schema_version: Option<String>,
    pub operations: Vec<CultNetDocumentOperation>,
    pub authority: CultNetMutationAuthority,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub intent_document_types: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub receipt_document_types: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub notes: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetTransportProfile {
    pub schema_version: String,
    pub runtime_id: String,
    pub transports: Vec<CultNetTransportDescriptor>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetTransportDescriptor {
    pub transport_id: String,
    pub protocol: CultNetTransportProtocol,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub host: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub port: Option<u16>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub discovery_group: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wire_contracts: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reconnect_policy: Option<CultNetReconnectPolicy>,
    pub channels: Vec<CultNetTransportChannel>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetReconnectPolicy {
    pub schema_version: String,
    pub policy_id: String,
    pub base_delay_ms: u64,
    pub max_delay_ms: u64,
    pub max_jitter_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_attempts: Option<u32>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetTransportProtocol {
    TcpFramed,
    Litenetlib,
    Websocket,
    Rudp,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetTransportChannel {
    pub channel_id: String,
    pub delivery: CultNetTransportDelivery,
    pub ordering: CultNetTransportOrdering,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_payload_bytes: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_fragment_bytes: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_pending_reliable_packets: Option<u32>,
    /// Drop a reliable packet on this channel instead of retransmitting it once
    /// this many milliseconds have passed since the send. Omitted when absent,
    /// so a profile that does not set it is byte-identical to one from a
    /// runtime that does not know the field.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reliable_expire_after_ms: Option<u64>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetTransportDelivery {
    Reliable,
    Unreliable,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetTransportOrdering {
    Ordered,
    Unordered,
    Sequenced,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetRawDocumentRecord {
    pub schema_id: String,
    pub record_key: String,
    pub stored_at: String,
    pub payload_encoding: CultNetRawPayloadEncoding,
    #[serde(with = "serde_bytes")]
    pub payload: Vec<u8>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_runtime_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_agent_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_role: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tags: Option<Vec<String>>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetSchemaDescriptor {
    pub schema_id: String,
    pub kind: CultNetSchemaKind,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub document_type: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title: Option<String>,
    pub wire_contracts: Vec<CultNetWireContract>,
    pub content_hash: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_json: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetShardDescriptor {
    pub shard_id: String,
    pub owner_runtime_id: String,
    pub epoch: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub is_primary: Option<bool>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub schema_ids: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub key_prefix: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub primary_endpoints: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub replica_endpoints: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub read_replica_endpoints: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub region: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authority_lease_id: Option<String>,
}

impl CultNetShardDescriptor {
    pub fn serves(&self, schema_id: Option<&str>, record_key: Option<&str>) -> bool {
        if let Some(schema_id) = schema_id
            && !self.schema_ids.is_empty()
            && !self
                .schema_ids
                .iter()
                .any(|candidate| candidate == schema_id)
        {
            return false;
        }

        if let Some(record_key) = record_key
            && let Some(prefix) = self.key_prefix.as_deref()
            && !record_key.starts_with(prefix)
        {
            return false;
        }

        true
    }
}

/// Machine-readable `cultnet.error.v0` refusal code (R-N). Mirrors the strings
/// `contracts/cultnet/cultnet.error.schema.json` enumerates for `code`; a peer that receives a
/// string it does not know refuses to decode rather than guess a meaning for it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CultNetErrorCode {
    SelectionInvalid,
    CursorStale,
    CursorInvalid,
    ReferenceOutsideTarget,
    VariantUnsupported,
    UnownedSchema,
    WriteNotAuthorized,
    UnsupportedSchemaVersion,
    MessageInvalid,
    /// `cultnet.error.v1` only: the session's remembered-write limit is full. `cultnet.error.v0` neither
    /// encodes nor decodes it, so its enum stays the schema's.
    LimitReached,
}

impl CultNetErrorCode {
    fn as_wire_str(self) -> &'static str {
        match self {
            Self::SelectionInvalid => "selection_invalid",
            Self::CursorStale => "cursor_stale",
            Self::CursorInvalid => "cursor_invalid",
            Self::ReferenceOutsideTarget => "reference_outside_target",
            Self::VariantUnsupported => "variant_unsupported",
            Self::UnownedSchema => "unowned_schema",
            Self::WriteNotAuthorized => "write_not_authorized",
            Self::UnsupportedSchemaVersion => "unsupported_schema_version",
            Self::MessageInvalid => "message_invalid",
            Self::LimitReached => "limit_reached",
        }
    }

    fn from_wire_str(value: &str) -> Result<Self> {
        match value {
            "selection_invalid" => Ok(Self::SelectionInvalid),
            "cursor_stale" => Ok(Self::CursorStale),
            "cursor_invalid" => Ok(Self::CursorInvalid),
            "reference_outside_target" => Ok(Self::ReferenceOutsideTarget),
            "variant_unsupported" => Ok(Self::VariantUnsupported),
            "unowned_schema" => Ok(Self::UnownedSchema),
            "write_not_authorized" => Ok(Self::WriteNotAuthorized),
            "unsupported_schema_version" => Ok(Self::UnsupportedSchemaVersion),
            "message_invalid" => Ok(Self::MessageInvalid),
            "limit_reached" => Ok(Self::LimitReached),
            _ => Err(anyhow!("ErrorMessage.Code is not a recognized cultnet.error.v0 code")),
        }
    }
}

/// Code-specific structured detail carried on `cultnet.error.v0`'s `code`/`details` (R-N). Mirrors
/// `CultNetErrorDetails`: every field is populated only for the code that defines it and is nil
/// (present, not omitted) on the wire otherwise, matching the C# reference's always-five-key map.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CultNetErrorDetails {
    /// The selection field that failed (`selection_invalid`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub field: Option<String>,
    /// The offending value, when there is one string worth naming (`selection_invalid`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub value: Option<String>,
    /// The cursor's own minted asOf (`cursor_stale`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub as_of: Option<u64>,
    /// The answering server's current asOf (`cursor_stale`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub current: Option<u64>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "schemaVersion")]
pub enum CultNetMessage {
    #[serde(rename = "cultnet.hello.v0", rename_all = "camelCase")]
    Hello {
        runtime_id: String,
        runtime_kind: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        agent_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        role: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        display_name: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        supported_document_types: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        supported_mutation_contracts: Option<Vec<CultNetDocumentMutationContract>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        supported_message_versions: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        transport_profiles: Option<Vec<CultNetTransportProfile>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        supports_schema_catalog: Option<bool>,
    },
    #[serde(rename = "cultnet.login.v0", rename_all = "camelCase")]
    Login {
        nonce: String,
        auth: String,
        password: String,
    },
    #[serde(rename = "cultnet.register.v0", rename_all = "camelCase")]
    Register {
        nonce: String,
        email: String,
        password: String,
        name: String,
    },
    #[serde(rename = "cultnet.verify.v0", rename_all = "camelCase")]
    Verify { nonce: String, session: String },
    #[serde(rename = "cultnet.login_success.v0", rename_all = "camelCase")]
    LoginSuccess { nonce: String, session: String },
    // R-N (docs/cultnet-selection-cut.md, fix batch 3): `code`/`details` mirror the landed C#
    // reference (CultNetSchemaMessages.cs) exactly, including its always-five-key map shape
    // (`schemaVersion`, `error`, `routingHint`, `code`, `details`, every optional key nil rather
    // than omitted). `Error` is read and written only by the raw `cultnet.schema.v0` path
    // (parse_raw_cultnet_schema_message/encode_raw_cultnet_schema_message): `skip_deserializing`
    // makes the derive unable to decode it, so the raw reader is its one decoder and the derive
    // attributes describe `Serialize` only.
    #[serde(skip_deserializing)]
    #[serde(rename = "cultnet.error.v0", rename_all = "camelCase")]
    Error {
        error: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        code: Option<CultNetErrorCode>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        details: Option<CultNetErrorDetails>,
    },
    /// `cultnet.error.v1` (contracts/cultnet/interop/write-receipt.md): `cultnet.error.v0` plus the id of the
    /// request it answers. Sent only to a connection that declared the write-receipt capability.
    #[serde(skip_deserializing)]
    #[serde(rename = "cultnet.error.v1", rename_all = "camelCase")]
    ErrorV1 {
        error: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        code: Option<CultNetErrorCode>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        details: Option<CultNetErrorDetails>,
        message_id: Option<String>,
    },
    /// `cultnet.write_admitted.v0`: the receipt for one admitted put or delete, sent after its commit point.
    #[serde(rename = "cultnet.write_admitted.v0", rename_all = "camelCase")]
    WriteAdmitted {
        message_id: String,
        operation: String,
        schema_id: String,
        record_key: String,
    },
    #[serde(rename = "cultnet.sample.change_name.v0", rename_all = "camelCase")]
    SampleChangeName { name: String },
    #[serde(rename = "cultnet.sample.chat.v0", rename_all = "camelCase")]
    SampleChat { text: String },
    #[serde(rename = "cultnet.document_delete.v0", rename_all = "camelCase")]
    DocumentDelete {
        message_id: String,
        schema_id: String,
        record_key: String,
    },
    #[serde(skip_deserializing)]
    #[serde(rename = "cultnet.document_put_raw.v0", rename_all = "camelCase")]
    DocumentPutRaw {
        message_id: String,
        document: CultNetRawDocumentRecord,
    },
    #[serde(rename = "cultnet.snapshot_request.v0", rename_all = "camelCase")]
    SnapshotRequest {
        message_id: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        schema_ids: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        record_keys: Option<Vec<String>>,
    },
    #[serde(skip_deserializing)]
    #[serde(rename = "cultnet.snapshot_response_raw.v0", rename_all = "camelCase")]
    SnapshotResponseRaw {
        message_id: String,
        documents: Vec<CultNetRawDocumentRecord>,
    },
    /// CultNet typed selection, Cut 1 (docs/cultnet-selection-cut.md section 2/7). Mirrors
    /// `CultNetSnapshotRequestV1Message`.
    #[serde(rename = "cultnet.snapshot_request.v1", rename_all = "camelCase")]
    SnapshotRequestV1 {
        message_id: String,
        selection: Selection,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        shard_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        shard_epoch: Option<i64>,
    },
    /// Mirrors `CultNetDatabaseSubscribeV1Message`.
    #[serde(rename = "cultnet.database_subscribe.v1", rename_all = "camelCase")]
    DatabaseSubscribeV1 {
        message_id: String,
        subscription_id: String,
        selection: Selection,
        #[serde(default = "true_default")]
        include_snapshot: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        consumer_runtime_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        body_ids: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        supported_body_transports: Option<Vec<String>>,
    },
    /// `cultnet.snapshot_response_raw.v1` (section 2: `SelectionPage`). Decoded by the derive from
    /// the message bytes in one pass (rows and scalars, a duplicated key refused); encoded by
    /// `encode_raw_cultnet_schema_message` for the reference's fixed nil-keyed shape. Mirrors
    /// `CultNetSnapshotResponseRawV1Message`.
    #[serde(rename = "cultnet.snapshot_response_raw.v1", rename_all = "camelCase")]
    SnapshotResponseRawV1 {
        message_id: String,
        matched: u32,
        as_of: u64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        next: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        headers: Option<Vec<RawDocumentHeader>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        documents: Option<Vec<SelectionDocumentRecord>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        edges: Option<Vec<Edge>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        shard_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        shard_epoch: Option<i64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        shard_log_sequence: Option<i64>,
    },
    #[serde(rename = "cultnet.schema_catalog_request.v0", rename_all = "camelCase")]
    SchemaCatalogRequest {
        message_id: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        include_schema_json: Option<bool>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        schema_ids: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        kinds: Option<Vec<CultNetSchemaKind>>,
    },
    #[serde(
        rename = "cultnet.schema_catalog_response.v0",
        rename_all = "camelCase"
    )]
    SchemaCatalogResponse {
        message_id: String,
        schemas: Vec<CultNetSchemaDescriptor>,
    },
    #[serde(rename = "cultnet.shard_catalog_request.v0", rename_all = "camelCase")]
    ShardCatalogRequest {
        message_id: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        schema_ids: Option<Vec<String>>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        record_keys: Option<Vec<String>>,
    },
    #[serde(rename = "cultnet.shard_catalog_response.v0", rename_all = "camelCase")]
    ShardCatalogResponse {
        message_id: String,
        shards: Vec<CultNetShardDescriptor>,
    },
    #[serde(rename = "cultnet.operation_request.v0", rename_all = "camelCase")]
    OperationRequest {
        message_id: String,
        service_id: String,
        operation: String,
        payload_schema: String,
        #[serde(default = "messagepack_base64_encoding")]
        payload_encoding: String,
        payload: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        source_runtime_id: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target_runtime_id: Option<String>,
    },
    #[serde(rename = "cultnet.operation_response.v0", rename_all = "camelCase")]
    OperationResponse {
        message_id: String,
        service_id: String,
        operation: String,
        status: String,
        payload_schema: String,
        #[serde(default = "messagepack_base64_encoding")]
        payload_encoding: String,
        payload: String,
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        diagnostics: Vec<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        source_runtime_id: Option<String>,
    },
    /// `cultmesh.content_chunk_request.v1`. Mirrors `CultMeshContentChunkRequestMessage`; raw path.
    #[serde(skip_deserializing)]
    #[serde(rename = "cultmesh.content_chunk_request.v1", rename_all = "camelCase")]
    ContentChunkRequest {
        message_id: String,
        chunk_hash: String,
        record_key: String,
        expected_size_bytes: i32,
    },
    /// `cultmesh.content_chunk_response.v1`. Mirrors `CultMeshContentChunkResponseMessage`. `payload`
    /// is `bin`, so it travels the raw path; the serde attribute never runs on the wire.
    #[serde(skip_deserializing)]
    #[serde(rename = "cultmesh.content_chunk_response.v1", rename_all = "camelCase")]
    ContentChunkResponse {
        message_id: String,
        found: bool,
        chunk_hash: String,
        size_bytes: i32,
        #[serde(with = "serde_bytes")]
        payload: Vec<u8>,
        error: String,
    },
}

fn messagepack_base64_encoding() -> String {
    "messagepack-base64".to_string()
}

fn true_default() -> bool {
    true
}

pub fn encode_cultnet_message_for_wire(
    message: &CultNetMessage,
    contract: CultNetWireContract,
) -> Result<rmpv::Value> {
    validate_message(message)?;
    match contract {
        CultNetWireContract::CultNetSchemaV0 => match message {
            CultNetMessage::DocumentPutRaw { .. }
            | CultNetMessage::SnapshotResponseRaw { .. }
            | CultNetMessage::SnapshotResponseRawV1 { .. }
            | CultNetMessage::ContentChunkRequest { .. }
            | CultNetMessage::ContentChunkResponse { .. }
            | CultNetMessage::Error { .. }
            | CultNetMessage::ErrorV1 { .. }
            | CultNetMessage::WriteAdmitted { .. } => encode_raw_cultnet_schema_message(message),
            _ => Ok(cultcache_rs::decode_payload(&rmp_serde::to_vec(
                &serde_json::to_value(message)?,
            )?)?),
        },
        CultNetWireContract::GameCultNetworkingV0 => encode_gamecult_networking_message(message),
    }
}

pub fn encode_cultnet_message_to_vec(
    message: &CultNetMessage,
    contract: CultNetWireContract,
) -> Result<Vec<u8>> {
    let wire_value = encode_cultnet_message_for_wire(message, contract)?;
    Ok(rmp_serde::to_vec(&wire_value)?)
}

pub fn decode_cultnet_message_from_slice(
    bytes: &[u8],
    contract: CultNetWireContract,
) -> Result<CultNetMessage> {
    let wire_value: rmpv::Value = cultcache_rs::decode_payload(bytes)?;
    match contract {
        CultNetWireContract::CultNetSchemaV0 => {
            let message = match schema_version_from_wire_value(&wire_value)? {
                Some(
                    "cultnet.document_put_raw.v0"
                    | "cultnet.snapshot_response_raw.v0"
                    | "cultmesh.content_chunk_request.v1"
                    | "cultmesh.content_chunk_response.v1"
                    | "cultnet.error.v0"
                    | "cultnet.error.v1",
                ) => parse_raw_cultnet_schema_message(&wire_value)
                    .with_context(|| received_version_label(&wire_value))?,
                _ => cultcache_rs::decode_payload::<CultNetMessage>(bytes)
                    .with_context(|| received_version_label(&wire_value))?,
            };
            validate_message(&message)?;
            Ok(message)
        }
        CultNetWireContract::GameCultNetworkingV0 => parse_gamecult_networking_message(&wire_value),
    }
}

fn validate_message(message: &CultNetMessage) -> Result<()> {
    match message {
        CultNetMessage::Hello {
            runtime_id,
            runtime_kind,
            agent_id,
            role,
            display_name,
            supported_document_types,
            supported_mutation_contracts,
            supported_message_versions,
            transport_profiles: _,
            supports_schema_catalog: _,
        } => {
            require_non_empty(runtime_id, "runtimeId")?;
            require_non_empty(runtime_kind, "runtimeKind")?;
            require_optional_non_empty(agent_id.as_deref(), "agentId")?;
            require_optional_non_empty(role.as_deref(), "role")?;
            require_optional_non_empty(display_name.as_deref(), "displayName")?;
            require_optional_string_vec(
                supported_document_types.as_deref(),
                "supportedDocumentTypes",
            )?;
            if let Some(contracts) = supported_mutation_contracts {
                for contract in contracts {
                    validate_mutation_contract(contract)?;
                }
            }
            require_optional_string_vec(
                supported_message_versions.as_deref(),
                "supportedMessageVersions",
            )?;
        }
        CultNetMessage::Login {
            nonce,
            auth,
            password,
        } => {
            require_non_empty(nonce, "nonce")?;
            require_non_empty(auth, "auth")?;
            require_non_empty(password, "password")?;
        }
        CultNetMessage::Register {
            nonce,
            email,
            password,
            name,
        } => {
            require_non_empty(nonce, "nonce")?;
            require_non_empty(email, "email")?;
            require_non_empty(password, "password")?;
            require_non_empty(name, "name")?;
        }
        CultNetMessage::Verify { nonce, session }
        | CultNetMessage::LoginSuccess { nonce, session } => {
            require_non_empty(nonce, "nonce")?;
            require_non_empty(session, "session")?;
        }
        CultNetMessage::Error { error, code: _, details: _ } => require_non_empty(error, "error")?,
        CultNetMessage::ErrorV1 { error, message_id, .. } => {
            require_non_empty(error, "error")?;
            if let Some(message_id) = message_id {
                require_non_empty(message_id, "messageId")?;
            }
        }
        CultNetMessage::WriteAdmitted { message_id, operation, schema_id, record_key } => {
            require_admitted_message_id(message_id)?;
            if operation != "put" && operation != "delete" {
                return Err(anyhow!("operation must be put or delete"));
            }
            require_non_empty(schema_id, "schemaId")?;
            require_non_empty(record_key, "recordKey")?;
        }
        CultNetMessage::SampleChangeName { name } => require_non_empty(name, "name")?,
        CultNetMessage::SampleChat { text } => require_non_empty(text, "text")?,
        CultNetMessage::DocumentDelete {
            message_id,
            schema_id,
            record_key,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_non_empty(schema_id, "schemaId")?;
            require_non_empty(record_key, "recordKey")?;
        }
        CultNetMessage::DocumentPutRaw {
            message_id,
            document,
        } => {
            require_non_empty(message_id, "messageId")?;
            validate_raw_document_record(document)?;
        }
        CultNetMessage::SnapshotRequest {
            message_id,
            schema_ids,
            record_keys,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_optional_string_vec(schema_ids.as_deref(), "schemaIds")?;
            require_optional_string_vec(record_keys.as_deref(), "recordKeys")?;
        }
        CultNetMessage::SnapshotResponseRaw {
            message_id,
            documents,
        } => {
            require_non_empty(message_id, "messageId")?;
            for document in documents {
                validate_raw_document_record(document)?;
            }
        }
        CultNetMessage::SnapshotRequestV1 {
            message_id,
            // The selection's own declared-alias semantics need a schema registry this wire-level
            // deserialization does not have; the C# reference does not validate CultNetSelection at
            // this layer either (CultNetSchemaMessageSerialization.cs just deserializes it). A
            // server-side caller runs `selection::validate` against its RowSet before evaluating.
            selection: _,
            shard_id,
            shard_epoch: _,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_optional_non_empty(shard_id.as_deref(), "shardId")?;
        }
        CultNetMessage::DatabaseSubscribeV1 {
            message_id,
            subscription_id,
            selection: _,
            include_snapshot: _,
            consumer_runtime_id,
            body_ids,
            supported_body_transports,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_non_empty(subscription_id, "subscriptionId")?;
            require_optional_non_empty(consumer_runtime_id.as_deref(), "consumerRuntimeId")?;
            require_optional_string_vec(body_ids.as_deref(), "bodyIds")?;
            require_optional_string_vec(
                supported_body_transports.as_deref(),
                "supportedBodyTransports",
            )?;
        }
        CultNetMessage::SnapshotResponseRawV1 {
            message_id,
            matched: _,
            as_of: _,
            next: _,
            headers,
            documents,
            edges: _,
            shard_id,
            shard_epoch: _,
            shard_log_sequence: _,
        } => {
            require_non_empty(message_id, "messageId")?;
            if headers.is_some() == documents.is_some() {
                return Err(anyhow!(
                    "CultNet field exactly one of headers/documents must be present"
                ));
            }
            require_optional_non_empty(shard_id.as_deref(), "shardId")?;
        }
        CultNetMessage::SchemaCatalogRequest {
            message_id,
            include_schema_json: _,
            schema_ids,
            kinds: _,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_optional_string_vec(schema_ids.as_deref(), "schemaIds")?;
        }
        CultNetMessage::SchemaCatalogResponse {
            message_id,
            schemas,
        } => {
            require_non_empty(message_id, "messageId")?;
            for schema in schemas {
                validate_schema_descriptor(schema)?;
            }
        }
        CultNetMessage::ShardCatalogRequest {
            message_id,
            schema_ids,
            record_keys,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_optional_string_vec(schema_ids.as_deref(), "schemaIds")?;
            require_optional_string_vec(record_keys.as_deref(), "recordKeys")?;
        }
        CultNetMessage::ShardCatalogResponse { message_id, shards } => {
            require_non_empty(message_id, "messageId")?;
            for shard in shards {
                validate_shard_descriptor(shard)?;
            }
        }
        // The reference's serializer accepts any content chunk message. It refuses a bad request in
        // its handler and answers `found: false` (`answer_content_chunk_request`); a bad response is
        // refused by `fetch_content`'s verification. Refusing either here would leave a peer in silence.
        CultNetMessage::ContentChunkRequest { .. } | CultNetMessage::ContentChunkResponse { .. } => {}
        CultNetMessage::OperationRequest {
            message_id,
            service_id,
            operation,
            payload_schema,
            payload_encoding,
            payload,
            source_runtime_id,
            target_runtime_id,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_non_empty(service_id, "serviceId")?;
            require_non_empty(operation, "operation")?;
            require_non_empty(payload_schema, "payloadSchema")?;
            require_exact(payload_encoding, "messagepack-base64", "payloadEncoding")?;
            require_non_empty(payload, "payload")?;
            require_optional_non_empty(source_runtime_id.as_deref(), "sourceRuntimeId")?;
            require_optional_non_empty(target_runtime_id.as_deref(), "targetRuntimeId")?;
        }
        CultNetMessage::OperationResponse {
            message_id,
            service_id,
            operation,
            status,
            payload_schema,
            payload_encoding,
            payload,
            diagnostics: _,
            source_runtime_id,
        } => {
            require_non_empty(message_id, "messageId")?;
            require_non_empty(service_id, "serviceId")?;
            require_non_empty(operation, "operation")?;
            require_non_empty(status, "status")?;
            require_non_empty(payload_schema, "payloadSchema")?;
            require_exact(payload_encoding, "messagepack-base64", "payloadEncoding")?;
            require_non_empty(payload, "payload")?;
            require_optional_non_empty(source_runtime_id.as_deref(), "sourceRuntimeId")?;
        }
    }
    Ok(())
}

fn validate_mutation_contract(contract: &CultNetDocumentMutationContract) -> Result<()> {
    require_non_empty(&contract.document_type, "documentType")?;
    require_optional_non_empty(
        contract.payload_schema_version.as_deref(),
        "payloadSchemaVersion",
    )?;
    if contract.operations.is_empty() {
        return Err(anyhow!(
            "CultNet mutation contract for {:?} must advertise at least one operation",
            contract.document_type
        ));
    }
    require_optional_string_vec(
        contract.intent_document_types.as_deref(),
        "intentDocumentTypes",
    )?;
    require_optional_string_vec(
        contract.receipt_document_types.as_deref(),
        "receiptDocumentTypes",
    )?;
    require_optional_string_vec(contract.notes.as_deref(), "notes")?;
    Ok(())
}

fn validate_raw_document_record(document: &CultNetRawDocumentRecord) -> Result<()> {
    require_non_empty(&document.schema_id, "schemaId")?;
    require_non_empty(&document.record_key, "recordKey")?;
    require_non_empty(&document.stored_at, "storedAt")?;
    if document.payload.is_empty() {
        return Err(anyhow!(
            "CultNet field payload must contain non-empty MessagePack bytes"
        ));
    }
    require_optional_non_empty(document.source_runtime_id.as_deref(), "sourceRuntimeId")?;
    require_optional_non_empty(document.source_agent_id.as_deref(), "sourceAgentId")?;
    require_optional_non_empty(document.source_role.as_deref(), "sourceRole")?;
    require_optional_string_vec(document.tags.as_deref(), "tags")?;
    Ok(())
}

fn validate_schema_descriptor(schema: &CultNetSchemaDescriptor) -> Result<()> {
    require_non_empty(&schema.schema_id, "schemaId")?;
    require_optional_non_empty(schema.schema_version.as_deref(), "schemaVersion")?;
    require_optional_non_empty(schema.document_type.as_deref(), "documentType")?;
    require_optional_non_empty(schema.title.as_deref(), "title")?;
    require_non_empty(&schema.content_hash, "contentHash")?;
    require_optional_non_empty(schema.schema_json.as_deref(), "schemaJson")?;

    if schema.wire_contracts.is_empty() {
        return Err(anyhow!(
            "CultNet field wireContracts must contain at least one supported contract"
        ));
    }

    Ok(())
}

fn validate_shard_descriptor(shard: &CultNetShardDescriptor) -> Result<()> {
    require_non_empty(&shard.shard_id, "shardId")?;
    require_non_empty(&shard.owner_runtime_id, "ownerRuntimeId")?;
    require_optional_string_vec(Some(shard.schema_ids.as_slice()), "schemaIds")?;
    require_optional_non_empty(shard.key_prefix.as_deref(), "keyPrefix")?;
    require_optional_string_vec(Some(shard.primary_endpoints.as_slice()), "primaryEndpoints")?;
    require_optional_string_vec(Some(shard.replica_endpoints.as_slice()), "replicaEndpoints")?;
    require_optional_string_vec(
        Some(shard.read_replica_endpoints.as_slice()),
        "readReplicaEndpoints",
    )?;
    require_optional_non_empty(shard.region.as_deref(), "region")?;
    require_optional_non_empty(shard.authority_lease_id.as_deref(), "authorityLeaseId")?;
    Ok(())
}

fn require_non_empty(value: &str, field: &str) -> Result<()> {
    if value.trim().is_empty() {
        return Err(anyhow!("CultNet field {field} must be non-empty"));
    }
    Ok(())
}

// write-receipt.md: a write_admitted messageId is valid iff it holds a Unicode letter (\p{L}) or number (\p{N}) and no
// Default_Ignorable_Code_Point, compared raw (never trimmed). std has `char::is_numeric` (N) but no letter category
// (`is_alphabetic` is the Alphabetic property, which also takes combining marks), and no Default_Ignorable, so both tables
// are embedded: DerivedCoreProperties.txt ranges, Unicode 16.0 (unchanged since 15.1), and the general category L ranges of
// Unicode 15.1 (U+0378 is unassigned in every version, so it is refused either way). The Kotlin, Python and C# decoders embed
// the same Default_Ignorable ranges and take L/N from their platform; TypeScript uses its regex engine's tables.
// Invalid UTF-8 never reaches this check: the MessagePack str decode refuses it.
const DEFAULT_IGNORABLE: &[(u32, u32)] = &[
    (0x00AD, 0x00AD), (0x034F, 0x034F), (0x061C, 0x061C), (0x115F, 0x1160), (0x17B4, 0x17B5), (0x180B, 0x180F),
    (0x200B, 0x200F), (0x202A, 0x202E), (0x2060, 0x206F), (0x3164, 0x3164), (0xFE00, 0xFE0F), (0xFEFF, 0xFEFF),
    (0xFFA0, 0xFFA0), (0xFFF0, 0xFFF8), (0x1BCA0, 0x1BCA3), (0x1D173, 0x1D17A), (0xE0000, 0xE0FFF),
];

const LETTERS: &[(u32, u32)] = &[
    (0x41, 0x5A), (0x61, 0x7A), (0xAA, 0xAA), (0xB5, 0xB5), (0xBA, 0xBA), (0xC0, 0xD6), (0xD8, 0xF6), (0xF8, 0x2C1),
    (0x2C6, 0x2D1), (0x2E0, 0x2E4), (0x2EC, 0x2EC), (0x2EE, 0x2EE), (0x370, 0x374), (0x376, 0x377), (0x37A, 0x37D),
    (0x37F, 0x37F), (0x386, 0x386), (0x388, 0x38A), (0x38C, 0x38C), (0x38E, 0x3A1), (0x3A3, 0x3F5), (0x3F7, 0x481),
    (0x48A, 0x52F), (0x531, 0x556), (0x559, 0x559), (0x560, 0x588), (0x5D0, 0x5EA), (0x5EF, 0x5F2), (0x620, 0x64A),
    (0x66E, 0x66F), (0x671, 0x6D3), (0x6D5, 0x6D5), (0x6E5, 0x6E6), (0x6EE, 0x6EF), (0x6FA, 0x6FC), (0x6FF, 0x6FF),
    (0x710, 0x710), (0x712, 0x72F), (0x74D, 0x7A5), (0x7B1, 0x7B1), (0x7CA, 0x7EA), (0x7F4, 0x7F5), (0x7FA, 0x7FA),
    (0x800, 0x815), (0x81A, 0x81A), (0x824, 0x824), (0x828, 0x828), (0x840, 0x858), (0x860, 0x86A), (0x870, 0x887),
    (0x889, 0x88E), (0x8A0, 0x8C9), (0x904, 0x939), (0x93D, 0x93D), (0x950, 0x950), (0x958, 0x961), (0x971, 0x980),
    (0x985, 0x98C), (0x98F, 0x990), (0x993, 0x9A8), (0x9AA, 0x9B0), (0x9B2, 0x9B2), (0x9B6, 0x9B9), (0x9BD, 0x9BD),
    (0x9CE, 0x9CE), (0x9DC, 0x9DD), (0x9DF, 0x9E1), (0x9F0, 0x9F1), (0x9FC, 0x9FC), (0xA05, 0xA0A), (0xA0F, 0xA10),
    (0xA13, 0xA28), (0xA2A, 0xA30), (0xA32, 0xA33), (0xA35, 0xA36), (0xA38, 0xA39), (0xA59, 0xA5C), (0xA5E, 0xA5E),
    (0xA72, 0xA74), (0xA85, 0xA8D), (0xA8F, 0xA91), (0xA93, 0xAA8), (0xAAA, 0xAB0), (0xAB2, 0xAB3), (0xAB5, 0xAB9),
    (0xABD, 0xABD), (0xAD0, 0xAD0), (0xAE0, 0xAE1), (0xAF9, 0xAF9), (0xB05, 0xB0C), (0xB0F, 0xB10), (0xB13, 0xB28),
    (0xB2A, 0xB30), (0xB32, 0xB33), (0xB35, 0xB39), (0xB3D, 0xB3D), (0xB5C, 0xB5D), (0xB5F, 0xB61), (0xB71, 0xB71),
    (0xB83, 0xB83), (0xB85, 0xB8A), (0xB8E, 0xB90), (0xB92, 0xB95), (0xB99, 0xB9A), (0xB9C, 0xB9C), (0xB9E, 0xB9F),
    (0xBA3, 0xBA4), (0xBA8, 0xBAA), (0xBAE, 0xBB9), (0xBD0, 0xBD0), (0xC05, 0xC0C), (0xC0E, 0xC10), (0xC12, 0xC28),
    (0xC2A, 0xC39), (0xC3D, 0xC3D), (0xC58, 0xC5A), (0xC5D, 0xC5D), (0xC60, 0xC61), (0xC80, 0xC80), (0xC85, 0xC8C),
    (0xC8E, 0xC90), (0xC92, 0xCA8), (0xCAA, 0xCB3), (0xCB5, 0xCB9), (0xCBD, 0xCBD), (0xCDD, 0xCDE), (0xCE0, 0xCE1),
    (0xCF1, 0xCF2), (0xD04, 0xD0C), (0xD0E, 0xD10), (0xD12, 0xD3A), (0xD3D, 0xD3D), (0xD4E, 0xD4E), (0xD54, 0xD56),
    (0xD5F, 0xD61), (0xD7A, 0xD7F), (0xD85, 0xD96), (0xD9A, 0xDB1), (0xDB3, 0xDBB), (0xDBD, 0xDBD), (0xDC0, 0xDC6),
    (0xE01, 0xE30), (0xE32, 0xE33), (0xE40, 0xE46), (0xE81, 0xE82), (0xE84, 0xE84), (0xE86, 0xE8A), (0xE8C, 0xEA3),
    (0xEA5, 0xEA5), (0xEA7, 0xEB0), (0xEB2, 0xEB3), (0xEBD, 0xEBD), (0xEC0, 0xEC4), (0xEC6, 0xEC6), (0xEDC, 0xEDF),
    (0xF00, 0xF00), (0xF40, 0xF47), (0xF49, 0xF6C), (0xF88, 0xF8C), (0x1000, 0x102A), (0x103F, 0x103F),
    (0x1050, 0x1055), (0x105A, 0x105D), (0x1061, 0x1061), (0x1065, 0x1066), (0x106E, 0x1070), (0x1075, 0x1081),
    (0x108E, 0x108E), (0x10A0, 0x10C5), (0x10C7, 0x10C7), (0x10CD, 0x10CD), (0x10D0, 0x10FA), (0x10FC, 0x1248),
    (0x124A, 0x124D), (0x1250, 0x1256), (0x1258, 0x1258), (0x125A, 0x125D), (0x1260, 0x1288), (0x128A, 0x128D),
    (0x1290, 0x12B0), (0x12B2, 0x12B5), (0x12B8, 0x12BE), (0x12C0, 0x12C0), (0x12C2, 0x12C5), (0x12C8, 0x12D6),
    (0x12D8, 0x1310), (0x1312, 0x1315), (0x1318, 0x135A), (0x1380, 0x138F), (0x13A0, 0x13F5), (0x13F8, 0x13FD),
    (0x1401, 0x166C), (0x166F, 0x167F), (0x1681, 0x169A), (0x16A0, 0x16EA), (0x16F1, 0x16F8), (0x1700, 0x1711),
    (0x171F, 0x1731), (0x1740, 0x1751), (0x1760, 0x176C), (0x176E, 0x1770), (0x1780, 0x17B3), (0x17D7, 0x17D7),
    (0x17DC, 0x17DC), (0x1820, 0x1878), (0x1880, 0x1884), (0x1887, 0x18A8), (0x18AA, 0x18AA), (0x18B0, 0x18F5),
    (0x1900, 0x191E), (0x1950, 0x196D), (0x1970, 0x1974), (0x1980, 0x19AB), (0x19B0, 0x19C9), (0x1A00, 0x1A16),
    (0x1A20, 0x1A54), (0x1AA7, 0x1AA7), (0x1B05, 0x1B33), (0x1B45, 0x1B4C), (0x1B83, 0x1BA0), (0x1BAE, 0x1BAF),
    (0x1BBA, 0x1BE5), (0x1C00, 0x1C23), (0x1C4D, 0x1C4F), (0x1C5A, 0x1C7D), (0x1C80, 0x1C88), (0x1C90, 0x1CBA),
    (0x1CBD, 0x1CBF), (0x1CE9, 0x1CEC), (0x1CEE, 0x1CF3), (0x1CF5, 0x1CF6), (0x1CFA, 0x1CFA), (0x1D00, 0x1DBF),
    (0x1E00, 0x1F15), (0x1F18, 0x1F1D), (0x1F20, 0x1F45), (0x1F48, 0x1F4D), (0x1F50, 0x1F57), (0x1F59, 0x1F59),
    (0x1F5B, 0x1F5B), (0x1F5D, 0x1F5D), (0x1F5F, 0x1F7D), (0x1F80, 0x1FB4), (0x1FB6, 0x1FBC), (0x1FBE, 0x1FBE),
    (0x1FC2, 0x1FC4), (0x1FC6, 0x1FCC), (0x1FD0, 0x1FD3), (0x1FD6, 0x1FDB), (0x1FE0, 0x1FEC), (0x1FF2, 0x1FF4),
    (0x1FF6, 0x1FFC), (0x2071, 0x2071), (0x207F, 0x207F), (0x2090, 0x209C), (0x2102, 0x2102), (0x2107, 0x2107),
    (0x210A, 0x2113), (0x2115, 0x2115), (0x2119, 0x211D), (0x2124, 0x2124), (0x2126, 0x2126), (0x2128, 0x2128),
    (0x212A, 0x212D), (0x212F, 0x2139), (0x213C, 0x213F), (0x2145, 0x2149), (0x214E, 0x214E), (0x2183, 0x2184),
    (0x2C00, 0x2CE4), (0x2CEB, 0x2CEE), (0x2CF2, 0x2CF3), (0x2D00, 0x2D25), (0x2D27, 0x2D27), (0x2D2D, 0x2D2D),
    (0x2D30, 0x2D67), (0x2D6F, 0x2D6F), (0x2D80, 0x2D96), (0x2DA0, 0x2DA6), (0x2DA8, 0x2DAE), (0x2DB0, 0x2DB6),
    (0x2DB8, 0x2DBE), (0x2DC0, 0x2DC6), (0x2DC8, 0x2DCE), (0x2DD0, 0x2DD6), (0x2DD8, 0x2DDE), (0x2E2F, 0x2E2F),
    (0x3005, 0x3006), (0x3031, 0x3035), (0x303B, 0x303C), (0x3041, 0x3096), (0x309D, 0x309F), (0x30A1, 0x30FA),
    (0x30FC, 0x30FF), (0x3105, 0x312F), (0x3131, 0x318E), (0x31A0, 0x31BF), (0x31F0, 0x31FF), (0x3400, 0x4DBF),
    (0x4E00, 0xA48C), (0xA4D0, 0xA4FD), (0xA500, 0xA60C), (0xA610, 0xA61F), (0xA62A, 0xA62B), (0xA640, 0xA66E),
    (0xA67F, 0xA69D), (0xA6A0, 0xA6E5), (0xA717, 0xA71F), (0xA722, 0xA788), (0xA78B, 0xA7CA), (0xA7D0, 0xA7D1),
    (0xA7D3, 0xA7D3), (0xA7D5, 0xA7D9), (0xA7F2, 0xA801), (0xA803, 0xA805), (0xA807, 0xA80A), (0xA80C, 0xA822),
    (0xA840, 0xA873), (0xA882, 0xA8B3), (0xA8F2, 0xA8F7), (0xA8FB, 0xA8FB), (0xA8FD, 0xA8FE), (0xA90A, 0xA925),
    (0xA930, 0xA946), (0xA960, 0xA97C), (0xA984, 0xA9B2), (0xA9CF, 0xA9CF), (0xA9E0, 0xA9E4), (0xA9E6, 0xA9EF),
    (0xA9FA, 0xA9FE), (0xAA00, 0xAA28), (0xAA40, 0xAA42), (0xAA44, 0xAA4B), (0xAA60, 0xAA76), (0xAA7A, 0xAA7A),
    (0xAA7E, 0xAAAF), (0xAAB1, 0xAAB1), (0xAAB5, 0xAAB6), (0xAAB9, 0xAABD), (0xAAC0, 0xAAC0), (0xAAC2, 0xAAC2),
    (0xAADB, 0xAADD), (0xAAE0, 0xAAEA), (0xAAF2, 0xAAF4), (0xAB01, 0xAB06), (0xAB09, 0xAB0E), (0xAB11, 0xAB16),
    (0xAB20, 0xAB26), (0xAB28, 0xAB2E), (0xAB30, 0xAB5A), (0xAB5C, 0xAB69), (0xAB70, 0xABE2), (0xAC00, 0xD7A3),
    (0xD7B0, 0xD7C6), (0xD7CB, 0xD7FB), (0xF900, 0xFA6D), (0xFA70, 0xFAD9), (0xFB00, 0xFB06), (0xFB13, 0xFB17),
    (0xFB1D, 0xFB1D), (0xFB1F, 0xFB28), (0xFB2A, 0xFB36), (0xFB38, 0xFB3C), (0xFB3E, 0xFB3E), (0xFB40, 0xFB41),
    (0xFB43, 0xFB44), (0xFB46, 0xFBB1), (0xFBD3, 0xFD3D), (0xFD50, 0xFD8F), (0xFD92, 0xFDC7), (0xFDF0, 0xFDFB),
    (0xFE70, 0xFE74), (0xFE76, 0xFEFC), (0xFF21, 0xFF3A), (0xFF41, 0xFF5A), (0xFF66, 0xFFBE), (0xFFC2, 0xFFC7),
    (0xFFCA, 0xFFCF), (0xFFD2, 0xFFD7), (0xFFDA, 0xFFDC), (0x10000, 0x1000B), (0x1000D, 0x10026), (0x10028, 0x1003A),
    (0x1003C, 0x1003D), (0x1003F, 0x1004D), (0x10050, 0x1005D), (0x10080, 0x100FA), (0x10280, 0x1029C),
    (0x102A0, 0x102D0), (0x10300, 0x1031F), (0x1032D, 0x10340), (0x10342, 0x10349), (0x10350, 0x10375),
    (0x10380, 0x1039D), (0x103A0, 0x103C3), (0x103C8, 0x103CF), (0x10400, 0x1049D), (0x104B0, 0x104D3),
    (0x104D8, 0x104FB), (0x10500, 0x10527), (0x10530, 0x10563), (0x10570, 0x1057A), (0x1057C, 0x1058A),
    (0x1058C, 0x10592), (0x10594, 0x10595), (0x10597, 0x105A1), (0x105A3, 0x105B1), (0x105B3, 0x105B9),
    (0x105BB, 0x105BC), (0x10600, 0x10736), (0x10740, 0x10755), (0x10760, 0x10767), (0x10780, 0x10785),
    (0x10787, 0x107B0), (0x107B2, 0x107BA), (0x10800, 0x10805), (0x10808, 0x10808), (0x1080A, 0x10835),
    (0x10837, 0x10838), (0x1083C, 0x1083C), (0x1083F, 0x10855), (0x10860, 0x10876), (0x10880, 0x1089E),
    (0x108E0, 0x108F2), (0x108F4, 0x108F5), (0x10900, 0x10915), (0x10920, 0x10939), (0x10980, 0x109B7),
    (0x109BE, 0x109BF), (0x10A00, 0x10A00), (0x10A10, 0x10A13), (0x10A15, 0x10A17), (0x10A19, 0x10A35),
    (0x10A60, 0x10A7C), (0x10A80, 0x10A9C), (0x10AC0, 0x10AC7), (0x10AC9, 0x10AE4), (0x10B00, 0x10B35),
    (0x10B40, 0x10B55), (0x10B60, 0x10B72), (0x10B80, 0x10B91), (0x10C00, 0x10C48), (0x10C80, 0x10CB2),
    (0x10CC0, 0x10CF2), (0x10D00, 0x10D23), (0x10E80, 0x10EA9), (0x10EB0, 0x10EB1), (0x10F00, 0x10F1C),
    (0x10F27, 0x10F27), (0x10F30, 0x10F45), (0x10F70, 0x10F81), (0x10FB0, 0x10FC4), (0x10FE0, 0x10FF6),
    (0x11003, 0x11037), (0x11071, 0x11072), (0x11075, 0x11075), (0x11083, 0x110AF), (0x110D0, 0x110E8),
    (0x11103, 0x11126), (0x11144, 0x11144), (0x11147, 0x11147), (0x11150, 0x11172), (0x11176, 0x11176),
    (0x11183, 0x111B2), (0x111C1, 0x111C4), (0x111DA, 0x111DA), (0x111DC, 0x111DC), (0x11200, 0x11211),
    (0x11213, 0x1122B), (0x1123F, 0x11240), (0x11280, 0x11286), (0x11288, 0x11288), (0x1128A, 0x1128D),
    (0x1128F, 0x1129D), (0x1129F, 0x112A8), (0x112B0, 0x112DE), (0x11305, 0x1130C), (0x1130F, 0x11310),
    (0x11313, 0x11328), (0x1132A, 0x11330), (0x11332, 0x11333), (0x11335, 0x11339), (0x1133D, 0x1133D),
    (0x11350, 0x11350), (0x1135D, 0x11361), (0x11400, 0x11434), (0x11447, 0x1144A), (0x1145F, 0x11461),
    (0x11480, 0x114AF), (0x114C4, 0x114C5), (0x114C7, 0x114C7), (0x11580, 0x115AE), (0x115D8, 0x115DB),
    (0x11600, 0x1162F), (0x11644, 0x11644), (0x11680, 0x116AA), (0x116B8, 0x116B8), (0x11700, 0x1171A),
    (0x11740, 0x11746), (0x11800, 0x1182B), (0x118A0, 0x118DF), (0x118FF, 0x11906), (0x11909, 0x11909),
    (0x1190C, 0x11913), (0x11915, 0x11916), (0x11918, 0x1192F), (0x1193F, 0x1193F), (0x11941, 0x11941),
    (0x119A0, 0x119A7), (0x119AA, 0x119D0), (0x119E1, 0x119E1), (0x119E3, 0x119E3), (0x11A00, 0x11A00),
    (0x11A0B, 0x11A32), (0x11A3A, 0x11A3A), (0x11A50, 0x11A50), (0x11A5C, 0x11A89), (0x11A9D, 0x11A9D),
    (0x11AB0, 0x11AF8), (0x11C00, 0x11C08), (0x11C0A, 0x11C2E), (0x11C40, 0x11C40), (0x11C72, 0x11C8F),
    (0x11D00, 0x11D06), (0x11D08, 0x11D09), (0x11D0B, 0x11D30), (0x11D46, 0x11D46), (0x11D60, 0x11D65),
    (0x11D67, 0x11D68), (0x11D6A, 0x11D89), (0x11D98, 0x11D98), (0x11EE0, 0x11EF2), (0x11F02, 0x11F02),
    (0x11F04, 0x11F10), (0x11F12, 0x11F33), (0x11FB0, 0x11FB0), (0x12000, 0x12399), (0x12480, 0x12543),
    (0x12F90, 0x12FF0), (0x13000, 0x1342F), (0x13441, 0x13446), (0x14400, 0x14646), (0x16800, 0x16A38),
    (0x16A40, 0x16A5E), (0x16A70, 0x16ABE), (0x16AD0, 0x16AED), (0x16B00, 0x16B2F), (0x16B40, 0x16B43),
    (0x16B63, 0x16B77), (0x16B7D, 0x16B8F), (0x16E40, 0x16E7F), (0x16F00, 0x16F4A), (0x16F50, 0x16F50),
    (0x16F93, 0x16F9F), (0x16FE0, 0x16FE1), (0x16FE3, 0x16FE3), (0x17000, 0x187F7), (0x18800, 0x18CD5),
    (0x18D00, 0x18D08), (0x1AFF0, 0x1AFF3), (0x1AFF5, 0x1AFFB), (0x1AFFD, 0x1AFFE), (0x1B000, 0x1B122),
    (0x1B132, 0x1B132), (0x1B150, 0x1B152), (0x1B155, 0x1B155), (0x1B164, 0x1B167), (0x1B170, 0x1B2FB),
    (0x1BC00, 0x1BC6A), (0x1BC70, 0x1BC7C), (0x1BC80, 0x1BC88), (0x1BC90, 0x1BC99), (0x1D400, 0x1D454),
    (0x1D456, 0x1D49C), (0x1D49E, 0x1D49F), (0x1D4A2, 0x1D4A2), (0x1D4A5, 0x1D4A6), (0x1D4A9, 0x1D4AC),
    (0x1D4AE, 0x1D4B9), (0x1D4BB, 0x1D4BB), (0x1D4BD, 0x1D4C3), (0x1D4C5, 0x1D505), (0x1D507, 0x1D50A),
    (0x1D50D, 0x1D514), (0x1D516, 0x1D51C), (0x1D51E, 0x1D539), (0x1D53B, 0x1D53E), (0x1D540, 0x1D544),
    (0x1D546, 0x1D546), (0x1D54A, 0x1D550), (0x1D552, 0x1D6A5), (0x1D6A8, 0x1D6C0), (0x1D6C2, 0x1D6DA),
    (0x1D6DC, 0x1D6FA), (0x1D6FC, 0x1D714), (0x1D716, 0x1D734), (0x1D736, 0x1D74E), (0x1D750, 0x1D76E),
    (0x1D770, 0x1D788), (0x1D78A, 0x1D7A8), (0x1D7AA, 0x1D7C2), (0x1D7C4, 0x1D7CB), (0x1DF00, 0x1DF1E),
    (0x1DF25, 0x1DF2A), (0x1E030, 0x1E06D), (0x1E100, 0x1E12C), (0x1E137, 0x1E13D), (0x1E14E, 0x1E14E),
    (0x1E290, 0x1E2AD), (0x1E2C0, 0x1E2EB), (0x1E4D0, 0x1E4EB), (0x1E7E0, 0x1E7E6), (0x1E7E8, 0x1E7EB),
    (0x1E7ED, 0x1E7EE), (0x1E7F0, 0x1E7FE), (0x1E800, 0x1E8C4), (0x1E900, 0x1E943), (0x1E94B, 0x1E94B),
    (0x1EE00, 0x1EE03), (0x1EE05, 0x1EE1F), (0x1EE21, 0x1EE22), (0x1EE24, 0x1EE24), (0x1EE27, 0x1EE27),
    (0x1EE29, 0x1EE32), (0x1EE34, 0x1EE37), (0x1EE39, 0x1EE39), (0x1EE3B, 0x1EE3B), (0x1EE42, 0x1EE42),
    (0x1EE47, 0x1EE47), (0x1EE49, 0x1EE49), (0x1EE4B, 0x1EE4B), (0x1EE4D, 0x1EE4F), (0x1EE51, 0x1EE52),
    (0x1EE54, 0x1EE54), (0x1EE57, 0x1EE57), (0x1EE59, 0x1EE59), (0x1EE5B, 0x1EE5B), (0x1EE5D, 0x1EE5D),
    (0x1EE5F, 0x1EE5F), (0x1EE61, 0x1EE62), (0x1EE64, 0x1EE64), (0x1EE67, 0x1EE6A), (0x1EE6C, 0x1EE72),
    (0x1EE74, 0x1EE77), (0x1EE79, 0x1EE7C), (0x1EE7E, 0x1EE7E), (0x1EE80, 0x1EE89), (0x1EE8B, 0x1EE9B),
    (0x1EEA1, 0x1EEA3), (0x1EEA5, 0x1EEA9), (0x1EEAB, 0x1EEBB), (0x20000, 0x2A6DF), (0x2A700, 0x2B739),
    (0x2B740, 0x2B81D), (0x2B820, 0x2CEA1), (0x2CEB0, 0x2EBE0), (0x2EBF0, 0x2EE5D), (0x2F800, 0x2FA1D),
    (0x30000, 0x3134A), (0x31350, 0x323AF),
];

fn in_ranges(ranges: &[(u32, u32)], point: u32) -> bool {
    ranges.binary_search_by(|&(low, high)| {
        if point < low {
            std::cmp::Ordering::Greater
        } else if point > high {
            std::cmp::Ordering::Less
        } else {
            std::cmp::Ordering::Equal
        }
    })
    .is_ok()
}

fn require_admitted_message_id(value: &str) -> Result<()> {
    let mut has_letter_or_number = false;
    for ch in value.chars() {
        let point = ch as u32;
        if in_ranges(DEFAULT_IGNORABLE, point) {
            return Err(anyhow!("CultNet field messageId must not hold a Default_Ignorable code point"));
        }
        has_letter_or_number |= ch.is_numeric() || in_ranges(LETTERS, point);
    }
    if !has_letter_or_number {
        return Err(anyhow!("CultNet field messageId must contain a letter or number"));
    }
    Ok(())
}

fn require_exact(value: &str, expected: &str, field: &str) -> Result<()> {
    if value != expected {
        return Err(anyhow!("{field} must be {expected}"));
    }
    Ok(())
}

fn require_optional_non_empty(value: Option<&str>, field: &str) -> Result<()> {
    if let Some(value) = value {
        require_non_empty(value, field)?;
    }
    Ok(())
}

fn require_optional_string_vec(value: Option<&[String]>, field: &str) -> Result<()> {
    if let Some(values) = value {
        for item in values {
            require_non_empty(item, field)?;
        }
    }
    Ok(())
}

fn schema_version_from_wire_value(input: &rmpv::Value) -> Result<Option<&str>> {
    let Some(object) = input.as_map() else {
        return Ok(None);
    };

    Ok(object.iter().find_map(|(key, value)| {
        key.as_str()
            .filter(|candidate| *candidate == "schemaVersion")
            .and_then(|_| value.as_str())
    }))
}

/// True for a name shaped like a schema version (`cultnet.hello.v0`): a received value is named
/// in a refusal only when it cannot be anything else.
fn looks_like_schema_name(candidate: &str) -> bool {
    let mut parts = candidate.split('.');
    let segment = |part: &str| !part.is_empty() && part.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_');
    let first_ok = parts.next().is_some_and(|first| {
        first.bytes().next().is_some_and(|b| b.is_ascii_lowercase()) && segment(first)
    });
    let rest: Vec<&str> = parts.collect();
    first_ok && !rest.is_empty() && rest.iter().all(|part| segment(*part)) && candidate.len() <= 96
}

/// What a refusal says of the message it refused: its `schemaVersion` when that is shaped like a
/// schema name, else only the shape of what arrived. Never a received value that could be text.
fn received_version_label(wire_value: &rmpv::Value) -> String {
    match schema_version_from_wire_value(wire_value) {
        Ok(Some(version)) if looks_like_schema_name(version) => {
            format!("cultnet.schema.v0 message {version} refused")
        }
        Ok(Some(version)) => format!(
            "cultnet.schema.v0 message whose schemaVersion is not a schema name ({} bytes)",
            version.len()
        ),
        _ => "cultnet.schema.v0 message with no schemaVersion".to_string(),
    }
}

/// Refuses a map that spells a string key twice, so no reader resolves a duplicate by position.
/// Names the context, never the key (it is received text).
fn refuse_duplicate_keys(object: &[(rmpv::Value, rmpv::Value)], context: &str) -> Result<()> {
    let mut seen = std::collections::HashSet::new();
    for (key, _) in object {
        if let Some(name) = key.as_str() {
            if !seen.insert(name) {
                bail!("{context} spells a key twice");
            }
        }
    }
    Ok(())
}

fn parse_raw_cultnet_schema_message(input: &rmpv::Value) -> Result<CultNetMessage> {
    let object = input
        .as_map()
        .ok_or_else(|| anyhow!("cultnet.schema.v0 raw messages must be objects"))?;
    refuse_duplicate_keys(object, "cultnet.schema.v0 raw message")?;

    let get = |name: &str| -> Option<&rmpv::Value> {
        object.iter().find_map(|(key, value)| {
            key.as_str()
                .filter(|candidate| *candidate == name)
                .map(|_| value)
        })
    };

    let schema_version = require_legacy_string(get("schemaVersion"), "schemaVersion")?;
    match schema_version.as_str() {
        "cultnet.document_put_raw.v0" => Ok(CultNetMessage::DocumentPutRaw {
            message_id: require_legacy_string(get("messageId"), "messageId")?,
            document: require_raw_document_record(get("document"), "document")?,
        }),
        "cultnet.snapshot_response_raw.v0" => {
            let documents = get("documents")
                .and_then(rmpv::Value::as_array)
                .ok_or_else(|| anyhow!("documents must be an array"))?
                .iter()
                .enumerate()
                .map(|(index, value)| {
                    require_raw_document_record(value.into(), &format!("documents[{index}]"))
                })
                .collect::<Result<Vec<_>>>()?;

            Ok(CultNetMessage::SnapshotResponseRaw {
                message_id: require_legacy_string(get("messageId"), "messageId")?,
                documents,
            })
        }
        "cultmesh.content_chunk_request.v1" => Ok(CultNetMessage::ContentChunkRequest {
            message_id: legacy_string_or_nil(get("messageId"), "messageId")?,
            chunk_hash: legacy_string_or_nil(get("chunkHash"), "chunkHash")?,
            record_key: legacy_string_or_nil(get("recordKey"), "recordKey")?,
            expected_size_bytes: require_legacy_i32(get("expectedSizeBytes"), "expectedSizeBytes")?,
        }),
        "cultmesh.content_chunk_response.v1" => Ok(CultNetMessage::ContentChunkResponse {
            message_id: require_legacy_string(get("messageId"), "messageId")?,
            found: require_legacy_bool(get("found"), "found")?,
            chunk_hash: require_legacy_string(get("chunkHash"), "chunkHash")?,
            size_bytes: require_legacy_i32(get("sizeBytes"), "sizeBytes")?,
            payload: get("payload")
                .and_then(rmpv::Value::as_slice)
                .ok_or_else(|| anyhow!("payload must be binary data"))?
                .to_vec(),
            error: require_legacy_string(get("error"), "error")?,
        }),
        "cultnet.error.v0" | "cultnet.error.v1" => {
            let code = get("code")
                .filter(|value| !value.is_nil())
                .map(|value| {
                    value
                        .as_str()
                        .ok_or_else(|| anyhow!("ErrorMessage.Code must be a string"))
                        .and_then(CultNetErrorCode::from_wire_str)
                })
                .transpose()?;
            if schema_version == "cultnet.error.v0" && code == Some(CultNetErrorCode::LimitReached) {
                return Err(anyhow!("ErrorMessage.Code is not a recognized cultnet.error.v0 code"));
            }
            let details = require_error_details(get("details"), "ErrorMessage.Details")?;
            let error = require_legacy_string(get("error"), "ErrorMessage.Error")?;
            if schema_version == "cultnet.error.v1" {
                return Ok(CultNetMessage::ErrorV1 {
                    error,
                    code,
                    details,
                    message_id: match get("messageId") {
                        None | Some(rmpv::Value::Nil) => None,
                        other => Some(require_legacy_string(other, "messageId")?),
                    },
                });
            }
            Ok(CultNetMessage::Error { error, code, details })
        }
        _ => Err(anyhow!(
            "Unsupported raw cultnet.schema.v0 message"
        )),
    }
}

fn require_error_details(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Option<CultNetErrorDetails>> {
    let Some(value) = value else {
        return Ok(None);
    };
    if value.is_nil() {
        return Ok(None);
    }
    let object = value
        .as_map()
        .ok_or_else(|| anyhow!("{field_name} must be an object"))?;
    refuse_duplicate_keys(object, field_name)?;
    let get = |name: &str| -> Option<&rmpv::Value> {
        object.iter().find_map(|(key, value)| {
            key.as_str()
                .filter(|candidate| *candidate == name)
                .map(|_| value)
        })
    };
    Ok(Some(CultNetErrorDetails {
        field: require_legacy_optional_string(get("field"), "ErrorMessage.Details.Field")?,
        value: require_legacy_optional_string(get("value"), "ErrorMessage.Details.Value")?,
        as_of: require_legacy_optional_u64(get("asOf"), "ErrorMessage.Details.AsOf")?,
        current: require_legacy_optional_u64(get("current"), "ErrorMessage.Details.Current")?,
    }))
}

fn require_legacy_optional_u64(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Option<u64>> {
    let Some(value) = value else {
        return Ok(None);
    };
    if value.is_nil() {
        return Ok(None);
    }
    value
        .as_u64()
        .map(Some)
        .ok_or_else(|| anyhow!("{field_name} must be a non-negative integer"))
}

fn encode_raw_cultnet_schema_message(message: &CultNetMessage) -> Result<rmpv::Value> {
    if let CultNetMessage::Error { code: Some(CultNetErrorCode::LimitReached), .. } = message {
        return Err(anyhow!("limit_reached is a cultnet.error.v1 code; cultnet.error.v0 cannot carry it"));
    }
    Ok(rmpv::Value::Map(match message {
        // Spec field order (write-receipt.md), not serde_json's sorted map.
        CultNetMessage::WriteAdmitted { message_id, operation, schema_id, record_key } => vec![
            (rmpv::Value::from("schemaVersion"), rmpv::Value::from("cultnet.write_admitted.v0")),
            (rmpv::Value::from("messageId"), rmpv::Value::from(message_id.as_str())),
            (rmpv::Value::from("operation"), rmpv::Value::from(operation.as_str())),
            (rmpv::Value::from("schemaId"), rmpv::Value::from(schema_id.as_str())),
            (rmpv::Value::from("recordKey"), rmpv::Value::from(record_key.as_str())),
        ],
        CultNetMessage::DocumentPutRaw {
            message_id,
            document,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultnet.document_put_raw.v0"),
            ),
            (
                rmpv::Value::from("messageId"),
                rmpv::Value::from(message_id.as_str()),
            ),
            (
                rmpv::Value::from("document"),
                encode_raw_document_record(document),
            ),
        ],
        CultNetMessage::SnapshotResponseRaw {
            message_id,
            documents,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultnet.snapshot_response_raw.v0"),
            ),
            (
                rmpv::Value::from("messageId"),
                rmpv::Value::from(message_id.as_str()),
            ),
            (
                rmpv::Value::from("documents"),
                rmpv::Value::Array(documents.iter().map(encode_raw_document_record).collect()),
            ),
        ],
        CultNetMessage::SnapshotResponseRawV1 {
            message_id,
            matched,
            as_of,
            next,
            headers,
            documents,
            edges,
            shard_id,
            shard_epoch,
            shard_log_sequence,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultnet.snapshot_response_raw.v1"),
            ),
            (
                rmpv::Value::from("messageId"),
                rmpv::Value::from(message_id.as_str()),
            ),
            (rmpv::Value::from("matched"), rmpv::Value::from(*matched)),
            (rmpv::Value::from("asOf"), rmpv::Value::from(*as_of)),
            (
                rmpv::Value::from("next"),
                next.as_deref().map(rmpv::Value::from).unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("headers"),
                headers
                    .as_deref()
                    .map(|headers| rmpv::Value::Array(headers.iter().map(encode_typed).collect()))
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("documents"),
                documents
                    .as_deref()
                    .map(|documents| rmpv::Value::Array(documents.iter().map(encode_typed).collect()))
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("edges"),
                edges
                    .as_deref()
                    .map(|edges| rmpv::Value::Array(edges.iter().map(encode_typed).collect()))
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("shardId"),
                shard_id.as_deref().map(rmpv::Value::from).unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("shardEpoch"),
                shard_epoch.map(rmpv::Value::from).unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("shardLogSequence"),
                shard_log_sequence
                    .map(rmpv::Value::from)
                    .unwrap_or(rmpv::Value::Nil),
            ),
        ],
        CultNetMessage::ContentChunkRequest {
            message_id,
            chunk_hash,
            record_key,
            expected_size_bytes,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultmesh.content_chunk_request.v1"),
            ),
            (rmpv::Value::from("messageId"), rmpv::Value::from(message_id.as_str())),
            (rmpv::Value::from("chunkHash"), rmpv::Value::from(chunk_hash.as_str())),
            (rmpv::Value::from("recordKey"), rmpv::Value::from(record_key.as_str())),
            (
                rmpv::Value::from("expectedSizeBytes"),
                rmpv::Value::from(*expected_size_bytes),
            ),
        ],
        CultNetMessage::ContentChunkResponse {
            message_id,
            found,
            chunk_hash,
            size_bytes,
            payload,
            error,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultmesh.content_chunk_response.v1"),
            ),
            (rmpv::Value::from("messageId"), rmpv::Value::from(message_id.as_str())),
            (rmpv::Value::from("found"), rmpv::Value::from(*found)),
            (rmpv::Value::from("chunkHash"), rmpv::Value::from(chunk_hash.as_str())),
            (rmpv::Value::from("sizeBytes"), rmpv::Value::from(*size_bytes)),
            (rmpv::Value::from("payload"), rmpv::Value::Binary(payload.clone())),
            (rmpv::Value::from("error"), rmpv::Value::from(error.as_str())),
        ],
        // R-N: matches the landed C# `CultNetErrorMessage`
        // (src/GameCult.Networking/CultNetSchemaMessages.cs) key for key and in its declared
        // order - schemaVersion, error, routingHint, code, details - with every optional key
        // present as nil rather than omitted. Rust never mints a routingHint today, so that key is
        // always nil on encode; a peer that sent one is ignored on decode, same as any other v0
        // peer that does not know a field it did not ask for.
        CultNetMessage::Error {
            error,
            code,
            details,
        } => vec![
            (
                rmpv::Value::from("schemaVersion"),
                rmpv::Value::from("cultnet.error.v0"),
            ),
            (rmpv::Value::from("error"), rmpv::Value::from(error.as_str())),
            (rmpv::Value::from("routingHint"), rmpv::Value::Nil),
            (
                rmpv::Value::from("code"),
                code.as_ref()
                    .map(|code| rmpv::Value::from(code.as_wire_str()))
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("details"),
                details
                    .as_ref()
                    .map(encode_error_details)
                    .unwrap_or(rmpv::Value::Nil),
            ),
        ],
        CultNetMessage::ErrorV1 {
            error,
            code,
            details,
            message_id,
        } => vec![
            (rmpv::Value::from("schemaVersion"), rmpv::Value::from("cultnet.error.v1")),
            (rmpv::Value::from("error"), rmpv::Value::from(error.as_str())),
            (rmpv::Value::from("routingHint"), rmpv::Value::Nil),
            (
                rmpv::Value::from("code"),
                code.as_ref()
                    .map(|code| rmpv::Value::from(code.as_wire_str()))
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("details"),
                details
                    .as_ref()
                    .map(encode_error_details)
                    .unwrap_or(rmpv::Value::Nil),
            ),
            (
                rmpv::Value::from("messageId"),
                message_id
                    .as_deref()
                    .map(rmpv::Value::from)
                    .unwrap_or(rmpv::Value::Nil),
            ),
        ],
        _ => {
            return Err(anyhow!(
                "Message is not a raw cultnet.schema.v0 binary replication message"
            ));
        }
    }))
}

fn encode_error_details(details: &CultNetErrorDetails) -> rmpv::Value {
    rmpv::Value::Map(vec![
        (
            rmpv::Value::from("field"),
            details
                .field
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("value"),
            details
                .value
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("asOf"),
            details.as_of.map(rmpv::Value::from).unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("current"),
            details.current.map(rmpv::Value::from).unwrap_or(rmpv::Value::Nil),
        ),
    ])
}

fn require_raw_document_record(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<CultNetRawDocumentRecord> {
    let object = value
        .and_then(rmpv::Value::as_map)
        .ok_or_else(|| anyhow!("{field_name} must be an object"))?;
    refuse_duplicate_keys(object, field_name)?;

    let get = |name: &str| -> Option<&rmpv::Value> {
        object.iter().find_map(|(key, value)| {
            key.as_str()
                .filter(|candidate| *candidate == name)
                .map(|_| value)
        })
    };

    let payload_encoding = require_legacy_string(
        get("payloadEncoding"),
        &format!("{field_name}.payloadEncoding"),
    )?;

    Ok(CultNetRawDocumentRecord {
        schema_id: require_legacy_string(get("schemaId"), &format!("{field_name}.schemaId"))?,
        record_key: require_legacy_string(get("recordKey"), &format!("{field_name}.recordKey"))?,
        stored_at: require_legacy_string(get("storedAt"), &format!("{field_name}.storedAt"))?,
        payload_encoding: match payload_encoding.as_str() {
            "messagepack" => CultNetRawPayloadEncoding::Messagepack,
            _ => {
                return Err(anyhow!(
                    "{field_name}.payloadEncoding names an encoding other than messagepack"
                ));
            }
        },
        payload: get("payload")
            .and_then(rmpv::Value::as_slice)
            .map(|bytes| bytes.to_vec())
            .ok_or_else(|| anyhow!("{field_name}.payload must be binary MessagePack bytes"))?,
        source_runtime_id: require_legacy_optional_string(
            get("sourceRuntimeId"),
            &format!("{field_name}.sourceRuntimeId"),
        )?,
        source_agent_id: require_legacy_optional_string(
            get("sourceAgentId"),
            &format!("{field_name}.sourceAgentId"),
        )?,
        source_role: require_legacy_optional_string(
            get("sourceRole"),
            &format!("{field_name}.sourceRole"),
        )?,
        tags: require_legacy_optional_string_array(get("tags"), &format!("{field_name}.tags"))?,
    })
}

fn encode_raw_document_record(document: &CultNetRawDocumentRecord) -> rmpv::Value {
    rmpv::Value::Map(vec![
        (
            rmpv::Value::from("schemaId"),
            rmpv::Value::from(document.schema_id.as_str()),
        ),
        (
            rmpv::Value::from("recordKey"),
            rmpv::Value::from(document.record_key.as_str()),
        ),
        (
            rmpv::Value::from("storedAt"),
            rmpv::Value::from(document.stored_at.as_str()),
        ),
        (
            rmpv::Value::from("payloadEncoding"),
            rmpv::Value::from(match document.payload_encoding {
                CultNetRawPayloadEncoding::Messagepack => "messagepack",
            }),
        ),
        (
            rmpv::Value::from("payload"),
            rmpv::Value::Binary(document.payload.clone()),
        ),
        (
            rmpv::Value::from("sourceRuntimeId"),
            document
                .source_runtime_id
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("sourceAgentId"),
            document
                .source_agent_id
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("sourceRole"),
            document
                .source_role
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("tags"),
            legacy_optional_string_array(document.tags.as_deref()),
        ),
    ])
}

// R-M: `SelectionDocumentRecord`, `RawDocumentHeader`, `Edge` and `RecordRef` derive
// `Serialize`/`Deserialize` (selection.rs) with the camelCase field names this wire uses.
// `encode_typed` goes through `rmp_serde::to_vec_named` (the named-map encoding this wire always
// uses; `rmpv::ext::to_value` would write a struct positionally) and reads the bytes back into the
// `rmpv::Value` tree the rest of this codec builds by hand, through the decode door. Received rows
// are never re-encoded to become typed: the enum's derive reads them from the original bytes.

fn encode_typed<T: Serialize>(value: &T) -> rmpv::Value {
    let bytes = rmp_serde::to_vec_named(value).expect("selection wire types always encode to MessagePack");
    cultcache_rs::decode_payload(&bytes).expect("named-map MessagePack bytes always decode into rmpv::Value")
}

fn require_legacy_i32(value: Option<&rmpv::Value>, field_name: &str) -> Result<i32> {
    let number = value
        .and_then(rmpv::Value::as_i64)
        .ok_or_else(|| anyhow!("{field_name} must be an integer"))?;
    i32::try_from(number).map_err(|_| anyhow!("{field_name} does not fit in i32"))
}

fn parse_gamecult_networking_message(input: &rmpv::Value) -> Result<CultNetMessage> {
    let items = input.as_array().ok_or_else(|| {
        anyhow!("gamecult.networking.v0 messages must be a 2-element union array")
    })?;
    if items.len() != 2 {
        return Err(anyhow!(
            "gamecult.networking.v0 messages must be a 2-element union array"
        ));
    }
    let tag = items[0]
        .as_i64()
        .ok_or_else(|| anyhow!("gamecult.networking.v0 message tag must be an integer"))?;
    let payload = items[1]
        .as_array()
        .ok_or_else(|| anyhow!("gamecult.networking.v0 message payload must be an array"))?;
    match tag {
        0 => Ok(CultNetMessage::Login {
            nonce: require_legacy_bytes(payload.first(), "LoginMessage.Nonce")?,
            auth: require_legacy_bytes(payload.get(1), "LoginMessage.Auth")?,
            password: require_legacy_bytes(payload.get(2), "LoginMessage.Password")?,
        }),
        1 => Ok(CultNetMessage::Register {
            nonce: require_legacy_bytes(payload.first(), "RegisterMessage.Nonce")?,
            email: require_legacy_bytes(payload.get(1), "RegisterMessage.Email")?,
            password: require_legacy_bytes(payload.get(2), "RegisterMessage.Password")?,
            name: require_legacy_bytes(payload.get(3), "RegisterMessage.Name")?,
        }),
        2 => Ok(CultNetMessage::Verify {
            nonce: require_legacy_bytes(payload.first(), "VerifyMessage.Nonce")?,
            session: require_legacy_bytes(payload.get(1), "VerifyMessage.Session")?,
        }),
        3 => Ok(CultNetMessage::LoginSuccess {
            nonce: require_legacy_bytes(payload.first(), "LoginSuccessMessage.Nonce")?,
            session: require_legacy_bytes(payload.get(1), "LoginSuccessMessage.Session")?,
        }),
        4 => Ok(CultNetMessage::Error {
            error: require_legacy_string(payload.first(), "ErrorMessage.Error")?,
            // gamecult.networking.v0 is the legacy union tag/array contract, unrelated to R-N's
            // cultnet.error.v0 map shape; it never carried code/details before and does not gain
            // them now.
            code: None,
            details: None,
        }),
        5 => Ok(CultNetMessage::SampleChangeName {
            name: require_legacy_string(payload.first(), "ChangeNameMessage.Name")?,
        }),
        6 => Ok(CultNetMessage::SampleChat {
            text: require_legacy_string(payload.first(), "ChatMessage.Text")?,
        }),
        7 => Ok(CultNetMessage::SchemaCatalogRequest {
            message_id: require_legacy_string(
                payload.first(),
                "SchemaCatalogRequestMessage.MessageId",
            )?,
            include_schema_json: Some(require_legacy_bool(
                payload.get(1),
                "SchemaCatalogRequestMessage.IncludeSchemaJson",
            )?),
            schema_ids: require_legacy_optional_string_array(
                payload.get(2),
                "SchemaCatalogRequestMessage.SchemaIds",
            )?,
            kinds: require_legacy_optional_schema_kind_array(
                payload.get(3),
                "SchemaCatalogRequestMessage.Kinds",
            )?,
        }),
        8 => Ok(CultNetMessage::SchemaCatalogResponse {
            message_id: require_legacy_string(
                payload.first(),
                "SchemaCatalogResponseMessage.MessageId",
            )?,
            schemas: require_legacy_schema_descriptor_array(
                payload.get(1),
                "SchemaCatalogResponseMessage.Schemas",
            )?,
        }),
        _ => Err(anyhow!(
            "Unsupported gamecult.networking.v0 union tag"
        )),
    }
}

fn encode_gamecult_networking_message(message: &CultNetMessage) -> Result<rmpv::Value> {
    let pair = match message {
        CultNetMessage::Login {
            nonce,
            auth,
            password,
        } => vec![
            rmpv::Value::from(0),
            rmpv::Value::Array(vec![
                legacy_bytes(nonce, "LoginMessage.Nonce")?,
                legacy_bytes(auth, "LoginMessage.Auth")?,
                legacy_bytes(password, "LoginMessage.Password")?,
            ]),
        ],
        CultNetMessage::Register {
            nonce,
            email,
            password,
            name,
        } => vec![
            rmpv::Value::from(1),
            rmpv::Value::Array(vec![
                legacy_bytes(nonce, "RegisterMessage.Nonce")?,
                legacy_bytes(email, "RegisterMessage.Email")?,
                legacy_bytes(password, "RegisterMessage.Password")?,
                legacy_bytes(name, "RegisterMessage.Name")?,
            ]),
        ],
        CultNetMessage::Verify { nonce, session } => vec![
            rmpv::Value::from(2),
            rmpv::Value::Array(vec![
                legacy_bytes(nonce, "VerifyMessage.Nonce")?,
                legacy_bytes(session, "VerifyMessage.Session")?,
            ]),
        ],
        CultNetMessage::LoginSuccess { nonce, session } => vec![
            rmpv::Value::from(3),
            rmpv::Value::Array(vec![
                legacy_bytes(nonce, "LoginSuccessMessage.Nonce")?,
                legacy_bytes(session, "LoginSuccessMessage.Session")?,
            ]),
        ],
        CultNetMessage::Error { error, .. } => {
            vec![
                rmpv::Value::from(4),
                rmpv::Value::Array(vec![error.as_str().into()]),
            ]
        }
        CultNetMessage::SampleChangeName { name } => {
            vec![
                rmpv::Value::from(5),
                rmpv::Value::Array(vec![name.as_str().into()]),
            ]
        }
        CultNetMessage::SampleChat { text } => {
            vec![
                rmpv::Value::from(6),
                rmpv::Value::Array(vec![text.as_str().into()]),
            ]
        }
        CultNetMessage::SchemaCatalogRequest {
            message_id,
            include_schema_json,
            schema_ids,
            kinds,
        } => vec![
            rmpv::Value::from(7),
            rmpv::Value::Array(vec![
                message_id.as_str().into(),
                rmpv::Value::from(include_schema_json.unwrap_or(false)),
                legacy_optional_string_array(schema_ids.as_deref()),
                legacy_optional_schema_kind_array(kinds.as_deref()),
            ]),
        ],
        CultNetMessage::SchemaCatalogResponse {
            message_id,
            schemas,
        } => vec![
            rmpv::Value::from(8),
            rmpv::Value::Array(vec![
                message_id.as_str().into(),
                rmpv::Value::Array(
                    schemas
                        .iter()
                        .map(legacy_schema_descriptor)
                        .collect::<Result<Vec<_>>>()?,
                ),
            ]),
        ],
        _ => {
            return Err(anyhow!(
                "Message is not defined in the gamecult.networking.v0 contract"
            ));
        }
    };
    Ok(rmpv::Value::Array(pair))
}

fn require_legacy_bytes(value: Option<&rmpv::Value>, field_name: &str) -> Result<String> {
    let bytes = value
        .and_then(rmpv::Value::as_slice)
        .ok_or_else(|| anyhow!("{field_name} must be binary data in gamecult.networking.v0"))?;
    Ok(URL_SAFE_NO_PAD.encode(bytes))
}

/// A string the reference reads back as null when its writer set it to null (msgpack nil), which its
/// handlers treat as blank. Only content chunk requests use it: they are answered, never dropped.
fn legacy_string_or_nil(value: Option<&rmpv::Value>, field_name: &str) -> Result<String> {
    match value {
        Some(rmpv::Value::Nil) => Ok(String::new()),
        other => require_legacy_string(other, field_name),
    }
}

fn require_legacy_string(value: Option<&rmpv::Value>, field_name: &str) -> Result<String> {
    let value = value
        .and_then(rmpv::Value::as_str)
        .ok_or_else(|| anyhow!("{field_name} must be a string in gamecult.networking.v0"))?;
    Ok(value.to_string())
}

fn require_legacy_bool(value: Option<&rmpv::Value>, field_name: &str) -> Result<bool> {
    value
        .and_then(rmpv::Value::as_bool)
        .ok_or_else(|| anyhow!("{field_name} must be a boolean in gamecult.networking.v0"))
}

fn require_legacy_optional_string_array(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Option<Vec<String>>> {
    let Some(value) = value else {
        return Ok(None);
    };
    if value.is_nil() {
        return Ok(None);
    }
    let items = value
        .as_array()
        .ok_or_else(|| anyhow!("{field_name} must be an array in gamecult.networking.v0"))?;
    let values = items
        .iter()
        .map(|item| {
            item.as_str()
                .map(ToString::to_string)
                .ok_or_else(|| anyhow!("{field_name} must contain only strings"))
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(Some(values))
}

fn require_legacy_optional_schema_kind_array(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Option<Vec<CultNetSchemaKind>>> {
    let Some(values) = require_legacy_optional_string_array(value, field_name)? else {
        return Ok(None);
    };
    values
        .into_iter()
        .map(|value| match value.as_str() {
            "wire_message" => Ok(CultNetSchemaKind::WireMessage),
            "document_payload" => Ok(CultNetSchemaKind::DocumentPayload),
            "shared_contract" => Ok(CultNetSchemaKind::SharedContract),
            _ => Err(anyhow!("{field_name} has an unsupported schema kind")),
        })
        .collect::<Result<Vec<_>>>()
        .map(Some)
}

fn require_legacy_schema_descriptor_array(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Vec<CultNetSchemaDescriptor>> {
    let items = value
        .and_then(rmpv::Value::as_array)
        .ok_or_else(|| anyhow!("{field_name} must be an array in gamecult.networking.v0"))?;
    items
        .iter()
        .enumerate()
        .map(|(index, item)| {
            require_legacy_schema_descriptor(item, &format!("{field_name}[{index}]"))
        })
        .collect()
}

fn require_legacy_schema_descriptor(
    value: &rmpv::Value,
    field_name: &str,
) -> Result<CultNetSchemaDescriptor> {
    let object = value
        .as_map()
        .ok_or_else(|| anyhow!("{field_name} must be an object in gamecult.networking.v0"))?;
    refuse_duplicate_keys(object, field_name)?;

    let get = |name: &str| -> Option<&rmpv::Value> {
        object.iter().find_map(|(key, value)| {
            key.as_str()
                .filter(|candidate| *candidate == name)
                .map(|_| value)
        })
    };

    let schema_id = require_legacy_string(get("schemaId"), &format!("{field_name}.SchemaId"))?;
    let kind = require_legacy_string(get("kind"), &format!("{field_name}.Kind"))?;
    let schema_version = require_legacy_optional_string(
        get("schemaVersion"),
        &format!("{field_name}.SchemaVersion"),
    )?;
    let document_type =
        require_legacy_optional_string(get("documentType"), &format!("{field_name}.DocumentType"))?;
    let title = require_legacy_optional_string(get("title"), &format!("{field_name}.Title"))?;
    let wire_contracts = require_legacy_optional_wire_contracts(
        get("wireContracts"),
        &format!("{field_name}.WireContracts"),
    )?;
    let content_hash =
        require_legacy_string(get("contentHash"), &format!("{field_name}.ContentHash"))?;
    let schema_json =
        require_legacy_optional_string(get("schemaJson"), &format!("{field_name}.SchemaJson"))?;

    let kind = match kind.as_str() {
        "wire_message" => CultNetSchemaKind::WireMessage,
        "document_payload" => CultNetSchemaKind::DocumentPayload,
        "shared_contract" => CultNetSchemaKind::SharedContract,
        _ => {
            return Err(anyhow!(
                "{field_name}.Kind is not a supported schema kind"
            ));
        }
    };

    Ok(CultNetSchemaDescriptor {
        schema_id,
        kind,
        schema_version,
        document_type,
        title,
        wire_contracts,
        content_hash,
        schema_json,
    })
}

fn require_legacy_optional_string(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Option<String>> {
    let Some(value) = value else {
        return Ok(None);
    };
    if value.is_nil() {
        return Ok(None);
    }
    Ok(Some(require_legacy_string(Some(value), field_name)?))
}

fn require_legacy_optional_wire_contracts(
    value: Option<&rmpv::Value>,
    field_name: &str,
) -> Result<Vec<CultNetWireContract>> {
    let Some(values) = require_legacy_optional_string_array(value, field_name)? else {
        return Ok(Vec::new());
    };

    values
        .into_iter()
        .map(|value| match value.as_str() {
            "cultnet.schema.v0" => Ok(CultNetWireContract::CultNetSchemaV0),
            "gamecult.networking.v0" => Ok(CultNetWireContract::GameCultNetworkingV0),
            _ => Err(anyhow!(
                "{field_name} contains unsupported wire contract {value}"
            )),
        })
        .collect()
}

fn legacy_bytes(input: &str, field_name: &str) -> Result<rmpv::Value> {
    if input.trim().is_empty() {
        return Err(anyhow!("{field_name} must be a non-empty base64url string"));
    }
    Ok(rmpv::Value::Binary(URL_SAFE_NO_PAD.decode(input)?))
}

fn legacy_optional_string_array(input: Option<&[String]>) -> rmpv::Value {
    match input {
        Some(values) => rmpv::Value::Array(
            values
                .iter()
                .map(|value| rmpv::Value::from(value.as_str()))
                .collect(),
        ),
        None => rmpv::Value::Nil,
    }
}

fn legacy_optional_schema_kind_array(input: Option<&[CultNetSchemaKind]>) -> rmpv::Value {
    match input {
        Some(values) => rmpv::Value::Array(
            values
                .iter()
                .map(|value| {
                    rmpv::Value::from(match value {
                        CultNetSchemaKind::WireMessage => "wire_message",
                        CultNetSchemaKind::DocumentPayload => "document_payload",
                        CultNetSchemaKind::SharedContract => "shared_contract",
                    })
                })
                .collect(),
        ),
        None => rmpv::Value::Nil,
    }
}

fn legacy_schema_descriptor(schema: &CultNetSchemaDescriptor) -> Result<rmpv::Value> {
    Ok(rmpv::Value::Map(vec![
        (
            rmpv::Value::from("schemaId"),
            rmpv::Value::from(schema.schema_id.as_str()),
        ),
        (
            rmpv::Value::from("kind"),
            rmpv::Value::from(match schema.kind {
                CultNetSchemaKind::WireMessage => "wire_message",
                CultNetSchemaKind::DocumentPayload => "document_payload",
                CultNetSchemaKind::SharedContract => "shared_contract",
            }),
        ),
        (
            rmpv::Value::from("schemaVersion"),
            schema
                .schema_version
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("documentType"),
            schema
                .document_type
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("title"),
            schema
                .title
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
        (
            rmpv::Value::from("wireContracts"),
            rmpv::Value::Array(
                schema
                    .wire_contracts
                    .iter()
                    .map(|value| {
                        rmpv::Value::from(match value {
                            CultNetWireContract::CultNetSchemaV0 => "cultnet.schema.v0",
                            CultNetWireContract::GameCultNetworkingV0 => "gamecult.networking.v0",
                        })
                    })
                    .collect(),
            ),
        ),
        (
            rmpv::Value::from("contentHash"),
            rmpv::Value::from(schema.content_hash.as_str()),
        ),
        (
            rmpv::Value::from("schemaJson"),
            schema
                .schema_json
                .as_deref()
                .map(rmpv::Value::from)
                .unwrap_or(rmpv::Value::Nil),
        ),
    ]))
}

/// The content hash a write-receipt server binds to a `(actor, messageId)` dedupe key
/// (contracts/cultnet/interop/write-receipt.md, rule 5): SHA-256 of the MessagePack array
/// `[operation, schemaId, recordKey, payload]`, `payload` a `bin` for a put and nil for a delete.
pub fn write_content_hash(
    operation: &str,
    schema_id: &str,
    record_key: &str,
    payload: Option<&[u8]>,
) -> Result<[u8; 32]> {
    use sha2::{Digest, Sha256};
    let value = rmpv::Value::Array(vec![
        rmpv::Value::from(operation),
        rmpv::Value::from(schema_id),
        rmpv::Value::from(record_key),
        payload.map_or(rmpv::Value::Nil, |payload| rmpv::Value::Binary(payload.to_vec())),
    ]);
    let mut bytes = Vec::new();
    rmpv::encode::write_value(&mut bytes, &value)?;
    Ok(Sha256::digest(&bytes).into())
}

/// SHA-256 of a write's `messageId`: what a receipt server remembers in place of the id itself, so an
/// entry's memory does not depend on the id's length (write-receipt.md, "What is remembered").
pub fn message_id_digest(message_id: &str) -> [u8; 32] {
    use sha2::{Digest, Sha256};
    Sha256::digest(message_id.as_bytes()).into()
}

/// The `messageId` of a schema frame that does not decode as a message (write-receipt.md, rule 3: a refusal
/// carries the write's id whenever the frame is readable enough to find it). `None` when the bytes are not
/// a MessagePack map or hold no non-empty string `messageId`.
pub fn readable_message_id(bytes: &[u8]) -> Option<String> {
    let value = rmpv::decode::read_value(&mut &*bytes).ok()?;
    let rmpv::Value::Map(entries) = value else {
        return None;
    };
    entries.into_iter().find_map(|(key, value)| match (key.as_str(), value) {
        (Some("messageId"), rmpv::Value::String(id)) => id.into_str().filter(|id| !id.is_empty()),
        _ => None,
    })
}

/// The first required field (`document`, then `schemaId`) that a put or delete frame lacks or holds as nil
/// (write-receipt.md, "Required fields"), with the write's operation (`"put"` or `"delete"`). `None` when the
/// bytes are not such a frame, or both fields are present. A put's `schemaId` is its document's.
pub fn missing_required_write_field(bytes: &[u8]) -> Option<(&'static str, &'static str)> {
    let rmpv::Value::Map(entries) = rmpv::decode::read_value(&mut &*bytes).ok()? else {
        return None;
    };
    let field = |entries: &[(rmpv::Value, rmpv::Value)], name: &str| {
        entries
            .iter()
            .find(|(key, _)| key.as_str() == Some(name))
            .map(|(_, value)| value.clone())
            .filter(|value| !value.is_nil())
    };
    match field(&entries, "schemaVersion")?.as_str()? {
        "cultnet.document_put_raw.v0" => match field(&entries, "document") {
            Some(rmpv::Value::Map(document)) => {
                field(&document, "schemaId").is_none().then_some(("put", "schemaId"))
            }
            _ => Some(("put", "document")),
        },
        "cultnet.document_delete.v0" => field(&entries, "schemaId").is_none().then_some(("delete", "schemaId")),
        _ => None,
    }
}
