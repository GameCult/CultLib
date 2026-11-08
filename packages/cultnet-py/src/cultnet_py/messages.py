from __future__ import annotations

import hashlib
from dataclasses import dataclass
from typing import Any

import msgpack


@dataclass(frozen=True)
class CultNetMessage:
    schema_version: str
    body: dict[str, Any]

    def to_wire(self) -> dict[str, Any]:
        return {"schemaVersion": self.schema_version, **self.body}

    def to_bytes(self) -> bytes:
        return msgpack.packb(self.to_wire(), use_bin_type=True)


def parse_message(payload: bytes) -> CultNetMessage:
    decoded = msgpack.unpackb(payload, raw=False)
    if not isinstance(decoded, dict):
        raise ValueError("CultNet schema-v0 messages must be MessagePack maps")
    schema_version = decoded.get("schemaVersion")
    if not isinstance(schema_version, str) or not schema_version:
        raise ValueError("CultNet schema-v0 messages must declare schemaVersion")
    body = dict(decoded)
    del body["schemaVersion"]
    if schema_version == "cultnet.error.v0":
        _validate_error_body(body, "cultnet.error.v0", CULTNET_ERROR_CODES)
    elif schema_version == "cultnet.error.v1":
        _validate_error_body(body, "cultnet.error.v1", CULTNET_ERROR_V1_CODES)
    elif schema_version == "cultnet.write_admitted.v0":
        _validate_write_admitted_body(body)
    return CultNetMessage(schema_version=schema_version, body=body)


# Closed refusal-code set of cultnet.error.v0 (contracts/cultnet/cultnet.error.schema.json). Adding a
# code is one coordinated change across every runtime; a peer refuses a code it does not know.
CULTNET_ERROR_CODES: tuple[str, ...] = (
    "selection_invalid",
    "cursor_stale",
    "cursor_invalid",
    "reference_outside_target",
    "variant_unsupported",
    "unowned_schema",
    "write_not_authorized",
    "unsupported_schema_version",
    "message_invalid",
)

# cultnet.error.v1 (contracts/cultnet/interop/cultnet.error.v1.schema.json): v0's codes plus limit_reached.
CULTNET_ERROR_V1_CODES: tuple[str, ...] = (*CULTNET_ERROR_CODES, "limit_reached")

# The two frames a receipt-capable hello lists in supportedMessageVersions; a peer lists both or neither
# (contracts/cultnet/interop/write-receipt.md, "Capability: opting in").
CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS: tuple[str, ...] = ("cultnet.write_admitted.v0", "cultnet.error.v1")

_ERROR_DETAILS_KEYS = ("field", "value", "asOf", "current")


def _is_uint(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _is_name(value: Any) -> bool:
    return isinstance(value, str) and value != ""


def _validate_write_admitted_body(body: dict[str, Any]) -> None:
    """Refuse a cultnet.write_admitted.v0 body that is not exactly the five keys of its closed schema."""
    expected = ("messageId", "operation", "schemaId", "recordKey")
    if set(body) != set(expected):
        raise ValueError(f"cultnet.write_admitted.v0 must carry exactly {list(expected)}, got {sorted(body)}")
    for key in ("messageId", "schemaId", "recordKey"):
        if not _is_name(body[key]):
            raise ValueError(f"cultnet.write_admitted.v0 {key} must be a non-empty string")
    if body["operation"] not in ("put", "delete"):
        raise ValueError("cultnet.write_admitted.v0 operation must be put or delete")


def write_content_hash(operation: str, schema_id: str, record_key: str, payload: bytes | None) -> bytes:
    """SHA-256 of the MessagePack array [operation, schemaId, recordKey, payload] (write-receipt.md, rule 5).

    payload is the raw document payload, packed as a MessagePack bin for a put and nil for a delete.
    """
    return hashlib.sha256(msgpack.packb([operation, schema_id, record_key, payload], use_bin_type=True)).digest()


def hello_advertises_write_receipts(hello_message: CultNetMessage) -> bool:
    """True when a hello lists both receipt frames: the only thing that makes a connection receipt-capable."""
    listed = hello_message.body.get("supportedMessageVersions")
    return isinstance(listed, list) and all(version in listed for version in CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS)


def _validate_error_body(body: dict[str, Any], version: str, codes: tuple[str, ...]) -> None:
    """Refuse a cultnet.error body that is not the full-key shape its closed schema requires."""
    expected = ("error", "routingHint", "code", "details") + (("messageId",) if version == "cultnet.error.v1" else ())
    if set(body) != set(expected):
        raise ValueError(f"{version} must carry exactly {list(expected)}, got {sorted(body)}")
    if not isinstance(body["error"], str) or not body["error"]:
        raise ValueError(f"{version} error must be a non-empty string")
    code = body["code"]
    if code is not None and (not isinstance(code, str) or code not in codes):
        raise ValueError(f"{version} code {code!r} is not a recognized refusal code")
    if "messageId" in body and body["messageId"] is not None and not _is_name(body["messageId"]):
        raise ValueError(f"{version} messageId must be a non-empty string or null")
    hint = body["routingHint"]
    if hint is not None:
        if (
            not isinstance(hint, dict)
            or set(hint) != {"shard", "reason"}
            or not isinstance(hint["reason"], str)
            or not (hint["shard"] is None or isinstance(hint["shard"], dict))
        ):
            raise ValueError(f"{version} routingHint must be null or {{shard, reason}}")
    details = body["details"]
    if details is not None:
        if not isinstance(details, dict) or set(details) != set(_ERROR_DETAILS_KEYS):
            raise ValueError(f"{version} details must be null or carry exactly {list(_ERROR_DETAILS_KEYS)}")
        for key in ("field", "value"):
            if details[key] is not None and not isinstance(details[key], str):
                raise ValueError(f"{version} details.{key} must be a string or null")
        for key in ("asOf", "current"):
            if details[key] is not None and not _is_uint(details[key]):
                raise ValueError(f"{version} details.{key} must be a non-negative integer or null")


def hello(
    *,
    runtime_id: str,
    supported_wire_contracts: list[str] | None = None,
    supported_schema_versions: list[str] | None = None,
    supported_mutation_contracts: list[dict[str, Any]] | None = None,
    transport_profiles: list[dict[str, Any]] | None = None,
    supported_message_versions: list[str] | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {
        "runtimeId": runtime_id,
        "supportedWireContracts": supported_wire_contracts or ["cultnet.schema.v0"],
        "supportedSchemaVersions": supported_schema_versions or [],
        "supportedMutationContracts": supported_mutation_contracts or [],
        "transportProfiles": transport_profiles or [],
    }
    if supported_message_versions is not None:
        body["supportedMessageVersions"] = supported_message_versions
    return CultNetMessage("cultnet.hello.v0", body)


def login(
    *,
    nonce: str,
    auth: str,
    password: str,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.login.v0",
        {
            "nonce": nonce,
            "auth": auth,
            "password": password,
        },
    )


def register(
    *,
    nonce: str,
    email: str,
    password: str,
    name: str,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.register.v0",
        {
            "nonce": nonce,
            "email": email,
            "password": password,
            "name": name,
        },
    )


def verify_session(
    *,
    nonce: str,
    session: str,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.verify.v0",
        {
            "nonce": nonce,
            "session": session,
        },
    )


def error_message(
    error: str,
    *,
    code: str | None = None,
    details: dict[str, Any] | None = None,
    routing_hint: dict[str, Any] | None = None,
) -> CultNetMessage:
    """Build cultnet.error.v0 with every key written (nil included), as the C# map formatter does.

    ``details`` may name any of field/value/asOf/current; the rest are written as null.
    """
    body = {
        "error": error,
        "routingHint": routing_hint,
        "code": code,
        "details": None if details is None else {key: details.get(key) for key in _ERROR_DETAILS_KEYS},
    }
    if details is not None and set(details) - set(_ERROR_DETAILS_KEYS):
        raise ValueError(f"cultnet.error.v0 details has unknown keys {sorted(set(details) - set(_ERROR_DETAILS_KEYS))}")
    _validate_error_body(body, "cultnet.error.v0", CULTNET_ERROR_CODES)
    return CultNetMessage("cultnet.error.v0", body)


def unsupported_schema_version_error(schema_version: Any) -> CultNetMessage:
    """The refusal for a message whose schemaVersion this peer does not speak (C# ForUnsupportedSchemaVersion)."""
    value = None if schema_version is None else str(schema_version)
    return error_message(
        f"unsupported_schema_version: {value or ''}",
        code="unsupported_schema_version",
        details={"field": "schemaVersion", "value": value},
    )


def message_invalid_error(field: str, reason: str) -> CultNetMessage:
    """The refusal for a request that lacks a required field or carries an ill-typed one (C# ForMessageInvalid)."""
    return error_message(f"message_invalid: {reason}", code="message_invalid", details={"field": field})


def login_success(
    *,
    nonce: str,
    session: str,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.login_success.v0",
        {
            "nonce": nonce,
            "session": session,
        },
    )


def document_put_raw(
    *,
    message_id: str = "",
    key: str,
    schema_id: str,
    stored_at: str,
    payload: bytes,
    source_runtime_id: str | None = None,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    record: dict[str, Any] = {
        "schemaId": schema_id,
        "recordKey": key,
        "storedAt": stored_at,
        "payloadEncoding": "messagepack",
        "payload": payload,
    }
    if source_runtime_id:
        record["sourceRuntimeId"] = source_runtime_id
    body: dict[str, Any] = {"messageId": message_id, "document": record}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.document_put_raw.v0", body)


def document_delete(
    *,
    message_id: str = "",
    schema_id: str,
    record_key: str,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {"messageId": message_id, "schemaId": schema_id, "recordKey": record_key}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.document_delete.v0", body)


def snapshot_request(
    *,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
    shard_id: str | None = None,
    shard_epoch: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {"messageId": message_id, "schemaIds": schema_ids or [], "recordKeys": record_keys or []}
    if shard_id is not None:
        body["shardId"] = shard_id
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    return CultNetMessage("cultnet.snapshot_request.v0", body)


def schema_catalog_request(
    *,
    message_id: str = "",
    include_schema_json: bool = False,
    schema_ids: list[str] | None = None,
    kinds: list[str] | None = None,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.schema_catalog_request.v0",
        {
            "messageId": message_id,
            "includeSchemaJson": include_schema_json,
            "schemaIds": schema_ids or [],
            "kinds": kinds or [],
        },
    )


def database_subscribe(
    *,
    subscription_id: str,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
    include_snapshot: bool = True,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.database_subscribe.v0",
        {
            "messageId": message_id,
            "subscriptionId": subscription_id,
            "schemaIds": schema_ids or [],
            "recordKeys": record_keys or [],
            "includeSnapshot": include_snapshot,
        },
    )


def database_unsubscribe(*, subscription_id: str, message_id: str = "") -> CultNetMessage:
    return CultNetMessage(
        "cultnet.database_unsubscribe.v0",
        {"messageId": message_id, "subscriptionId": subscription_id},
    )


def shard_catalog_request(
    *,
    message_id: str = "",
    schema_ids: list[str] | None = None,
    record_keys: list[str] | None = None,
) -> CultNetMessage:
    return CultNetMessage(
        "cultnet.shard_catalog_request.v0",
        {"messageId": message_id, "schemaIds": schema_ids or [], "recordKeys": record_keys or []},
    )


def shard_log_request(
    *,
    shard_id: str,
    message_id: str = "",
    shard_epoch: int | None = None,
    after_sequence: int = 0,
    limit: int | None = None,
) -> CultNetMessage:
    body: dict[str, Any] = {
        "messageId": message_id,
        "shardId": shard_id,
        "afterSequence": after_sequence,
    }
    if shard_epoch is not None:
        body["shardEpoch"] = shard_epoch
    if limit is not None:
        body["limit"] = limit
    return CultNetMessage("cultnet.shard_log_request.v0", body)


def compute_simulation_claim_hash(*parts: str) -> str:
    canonical = "\x1f".join(part or "" for part in parts)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def simulation_observation(
    *,
    message_id: str,
    witness_runtime_id: str,
    shard_id: str,
    shard_epoch: int,
    frame: int,
    subject_id: str,
    claim_kind: str,
    claim_hash: str,
    claim_summary: str | None = None,
    weight: float = 1.0,
    observed_at: str = "",
) -> CultNetMessage:
    observation: dict[str, Any] = {
        "witnessRuntimeId": witness_runtime_id,
        "shardId": shard_id,
        "shardEpoch": shard_epoch,
        "frame": frame,
        "subjectId": subject_id,
        "claimKind": claim_kind,
        "claimHash": claim_hash,
        "weight": weight,
        "observedAt": observed_at,
    }
    if claim_summary is not None:
        observation["claimSummary"] = claim_summary
    return CultNetMessage(
        "cultnet.simulation_observation.v0",
        {"messageId": message_id, "observation": observation},
    )


@dataclass(frozen=True)
class CultNetWitnessArtifactBundle:
    bundle_id: str
    witness_kind: str
    captured_at: str
    subject: dict[str, Any]
    contracts: tuple[dict[str, Any], ...]
    artifacts: tuple[dict[str, Any], ...]
    provenance: dict[str, Any]
    timing_witnesses: tuple[dict[str, Any], ...] = ()

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetWitnessArtifactBundle":
        return cls(
            bundle_id=str(value.get("bundleId") or ""),
            witness_kind=str(value.get("witnessKind") or ""),
            captured_at=str(value.get("capturedAt") or ""),
            subject=dict(value.get("subject") or {}),
            contracts=tuple(dict(contract) for contract in value.get("contracts") or [] if isinstance(contract, dict)),
            artifacts=tuple(dict(artifact) for artifact in value.get("artifacts") or [] if isinstance(artifact, dict)),
            timing_witnesses=tuple(
                dict(timing_witness)
                for timing_witness in value.get("timingWitnesses") or []
                if isinstance(timing_witness, dict)
            ),
            provenance=dict(value.get("provenance") or {}),
        )

    @classmethod
    def from_payload(cls, payload: bytes) -> "CultNetWitnessArtifactBundle":
        return cls.from_wire(decode_witness_artifact_bundle_payload(payload))

    def to_wire(self) -> dict[str, Any]:
        return {
            "bundleId": self.bundle_id,
            "witnessKind": self.witness_kind,
            "capturedAt": self.captured_at,
            "subject": dict(self.subject),
            "contracts": [dict(contract) for contract in self.contracts],
            "artifacts": [dict(artifact) for artifact in self.artifacts],
            "timingWitnesses": [dict(timing_witness) for timing_witness in self.timing_witnesses],
            "provenance": dict(self.provenance),
        }

    def to_payload(self) -> bytes:
        return encode_witness_artifact_bundle_payload(self)


def witness_artifact_bundle(
    *,
    bundle_id: str,
    witness_kind: str,
    captured_at: str,
    subject: dict[str, Any],
    contracts: list[dict[str, Any]],
    artifacts: list[dict[str, Any]],
    provenance: dict[str, Any],
    timing_witnesses: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    return CultNetWitnessArtifactBundle(
        bundle_id=bundle_id,
        witness_kind=witness_kind,
        captured_at=captured_at,
        subject=subject,
        contracts=tuple(contracts),
        artifacts=tuple(artifacts),
        timing_witnesses=tuple(timing_witnesses or []),
        provenance=provenance,
    ).to_wire()


def encode_witness_artifact_bundle_payload(bundle: dict[str, Any] | CultNetWitnessArtifactBundle) -> bytes:
    wire = bundle.to_wire() if isinstance(bundle, CultNetWitnessArtifactBundle) else bundle
    return msgpack.packb([
        wire["bundleId"],
        wire["witnessKind"],
        wire["capturedAt"],
        wire["subject"],
        wire["contracts"],
        wire["artifacts"],
        wire.get("timingWitnesses", []),
        wire["provenance"],
    ], use_bin_type=True)


def decode_witness_artifact_bundle_payload(payload: bytes) -> dict[str, Any]:
    slots = msgpack.unpackb(payload, raw=False)
    if not isinstance(slots, list) or len(slots) < 8:
        raise ValueError("CultNet witness artifact bundle payload must be an 8-slot MessagePack array")
    return {
        "bundleId": slots[0],
        "witnessKind": slots[1],
        "capturedAt": slots[2],
        "subject": slots[3],
        "contracts": slots[4],
        "artifacts": slots[5],
        "timingWitnesses": slots[6],
        "provenance": slots[7],
    }
