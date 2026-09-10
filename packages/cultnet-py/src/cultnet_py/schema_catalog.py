from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass, field
from typing import Any

INTEROP_WIRE_CONTRACT = "cultnet.schema.v0"
CULTNET_SCHEMA_BASE = "https://github.com/GameCult/cultnet-ts/contracts"
VERSE_CATALOG_REQUEST = "cultmesh.verse_catalog_request.v0"
PEER_EXCHANGE_REQUEST = "cultmesh.peer_exchange_request.v0"
TRANSPORT_PROFILE_SCHEMA_VERSION = "cultnet.transport_profile.v0"
TRANSPORT_PROFILE_SCHEMA_ID = f"{CULTNET_SCHEMA_BASE}/cultnet.transport-profile.schema.json"

WIRE_MESSAGE_SCHEMA_VERSIONS = (
    ("cultnet.hello.v0", "CultNet Hello Message", f"{CULTNET_SCHEMA_BASE}/cultnet.hello.schema.json"),
    ("cultnet.error.v0", "CultNet Error Message", f"{CULTNET_SCHEMA_BASE}/cultnet.error.schema.json"),
    ("cultnet.login.v0", "CultNet Login Message", f"{CULTNET_SCHEMA_BASE}/cultnet.login.schema.json"),
    ("cultnet.register.v0", "CultNet Register Message", f"{CULTNET_SCHEMA_BASE}/cultnet.register.schema.json"),
    ("cultnet.verify.v0", "CultNet Verify Message", f"{CULTNET_SCHEMA_BASE}/cultnet.verify.schema.json"),
    ("cultnet.login_success.v0", "CultNet Login Success Message", f"{CULTNET_SCHEMA_BASE}/cultnet.login-success.schema.json"),
    ("cultnet.document_delete.v0", "CultNet Document Delete Message", f"{CULTNET_SCHEMA_BASE}/cultnet.document-delete.schema.json"),
    ("cultnet.document_put_raw.v0", "CultNet Raw Document Put Message", f"{CULTNET_SCHEMA_BASE}/cultnet.document-put-raw.schema.json"),
    ("cultnet.snapshot_request.v0", "CultNet Snapshot Request Message", f"{CULTNET_SCHEMA_BASE}/cultnet.snapshot-request.schema.json"),
    ("cultnet.snapshot_response_raw.v0", "CultNet Raw Snapshot Response Message", f"{CULTNET_SCHEMA_BASE}/cultnet.snapshot-response-raw.schema.json"),
    ("cultnet.schema_catalog_request.v0", "CultNet Schema Catalog Request Message", f"{CULTNET_SCHEMA_BASE}/cultnet.schema-catalog-request.schema.json"),
    ("cultnet.schema_catalog_response.v0", "CultNet Schema Catalog Response Message", f"{CULTNET_SCHEMA_BASE}/cultnet.schema-catalog-response.schema.json"),
    ("cultnet.database_subscribe.v0", "CultNet Database Subscribe Message", f"{CULTNET_SCHEMA_BASE}/cultnet.database-subscribe.schema.json"),
    ("cultnet.database_unsubscribe.v0", "CultNet Database Unsubscribe Message", f"{CULTNET_SCHEMA_BASE}/cultnet.database-unsubscribe.schema.json"),
    ("cultnet.database_change_raw.v0", "CultNet Raw Database Change Message", f"{CULTNET_SCHEMA_BASE}/cultnet.database-change-raw.schema.json"),
    ("cultnet.shard_catalog_request.v0", "CultNet Shard Catalog Request Message", f"{CULTNET_SCHEMA_BASE}/cultnet.shard-catalog-request.schema.json"),
    ("cultnet.shard_catalog_response.v0", "CultNet Shard Catalog Response Message", f"{CULTNET_SCHEMA_BASE}/cultnet.shard-catalog-response.schema.json"),
    ("cultnet.shard_log_request.v0", "CultNet Shard Log Request Message", f"{CULTNET_SCHEMA_BASE}/cultnet.shard-log-request.schema.json"),
    ("cultnet.shard_log_response.v0", "CultNet Shard Log Response Message", f"{CULTNET_SCHEMA_BASE}/cultnet.shard-log-response.schema.json"),
    ("cultnet.simulation_observation.v0", "CultNet Simulation Observation Message", f"{CULTNET_SCHEMA_BASE}/cultnet.simulation-observation.schema.json"),
    ("cultnet.simulation_consensus_candidate.v0", "CultNet Simulation Consensus Candidate Message", f"{CULTNET_SCHEMA_BASE}/cultnet.simulation-consensus-candidate.schema.json"),
    (VERSE_CATALOG_REQUEST, "CultMesh Verse Catalog Request Message", f"{CULTNET_SCHEMA_BASE}/cultmesh.verse-catalog-request.schema.json"),
    ("cultmesh.verse_catalog_response.v0", "CultMesh Verse Catalog Response Message", f"{CULTNET_SCHEMA_BASE}/cultmesh.verse-catalog-response.schema.json"),
    (PEER_EXCHANGE_REQUEST, "CultMesh Peer Exchange Request Message", f"{CULTNET_SCHEMA_BASE}/cultmesh.peer-exchange-request.schema.json"),
    ("cultmesh.peer_exchange_response.v0", "CultMesh Peer Exchange Response Message", f"{CULTNET_SCHEMA_BASE}/cultmesh.peer-exchange-response.schema.json"),
)

SHARED_CONTRACT_SCHEMA_VERSIONS = (
    (TRANSPORT_PROFILE_SCHEMA_VERSION, "CultNet Transport Profile", TRANSPORT_PROFILE_SCHEMA_ID),
)


@dataclass(frozen=True)
class CultNetSchemaDescriptor:
    schema_id: str
    kind: str
    schema_version: str | None = None
    document_type: str | None = None
    title: str | None = None
    wire_contracts: tuple[str, ...] = ()
    content_hash: str | None = None
    schema_json: str | None = None

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetSchemaDescriptor":
        schema_id = str(value.get("schemaId") or "")
        kind = str(value.get("kind") or "")
        if not schema_id:
            raise ValueError("schema descriptor schemaId must be non-empty")
        if not kind:
            raise ValueError("schema descriptor kind must be non-empty")
        return cls(
            schema_id=schema_id,
            kind=kind,
            schema_version=_optional_string(value.get("schemaVersion")),
            document_type=_optional_string(value.get("documentType")),
            title=_optional_string(value.get("title")),
            wire_contracts=tuple(str(contract) for contract in value.get("wireContracts") or []),
            content_hash=_optional_string(value.get("contentHash")),
            schema_json=_optional_string(value.get("schemaJson")),
        )

    def to_wire(self, *, include_schema_json: bool | None = None) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "schemaId": self.schema_id,
            "kind": self.kind,
            "wireContracts": list(self.wire_contracts),
        }
        if self.schema_version is not None:
            wire["schemaVersion"] = self.schema_version
        if self.document_type is not None:
            wire["documentType"] = self.document_type
        if self.title is not None:
            wire["title"] = self.title
        if self.content_hash is not None:
            wire["contentHash"] = self.content_hash
        if self.schema_json is not None and include_schema_json is not False:
            wire["schemaJson"] = self.schema_json
        return wire


@dataclass
class CultNetSchemaCatalog:
    _descriptors: dict[str, CultNetSchemaDescriptor] = field(default_factory=dict)

    def upsert(self, descriptor: CultNetSchemaDescriptor | dict[str, Any]) -> CultNetSchemaDescriptor:
        value = descriptor if isinstance(descriptor, CultNetSchemaDescriptor) else CultNetSchemaDescriptor.from_wire(descriptor)
        self._descriptors[value.schema_id] = value
        return value

    def get(self, schema_id: str) -> CultNetSchemaDescriptor | None:
        return self._descriptors.get(schema_id)

    def list(
        self,
        *,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> list[CultNetSchemaDescriptor]:
        requested_schema_ids = set(schema_ids or [])
        requested_kinds = set(kinds or [])
        return [
            descriptor
            for descriptor in sorted(self._descriptors.values(), key=lambda item: item.schema_id)
            if (not requested_schema_ids or descriptor.schema_id in requested_schema_ids)
            and (not requested_kinds or descriptor.kind in requested_kinds)
        ]

    def apply_response(self, response: dict[str, Any]) -> list[CultNetSchemaDescriptor]:
        if response.get("schemaVersion") != "cultnet.schema_catalog_response.v0":
            raise ValueError(f"Expected cultnet.schema_catalog_response.v0, received {response.get('schemaVersion')!r}")
        applied = []
        for descriptor in response.get("schemas") or []:
            if not isinstance(descriptor, dict):
                continue
            applied.append(self.upsert(descriptor))
        return applied

    def create_response(
        self,
        *,
        message_id: str = "",
        include_schema_json: bool = False,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> dict[str, Any]:
        return {
            "schemaVersion": "cultnet.schema_catalog_response.v0",
            "messageId": message_id,
            "schemas": [
                descriptor.to_wire(include_schema_json=include_schema_json)
                for descriptor in self.list(schema_ids=schema_ids, kinds=kinds)
            ],
        }


def wire_message_schema_descriptors(include_schema_json: bool) -> list[dict[str, Any]]:
    descriptors = []
    for schema_version, title, schema_id in WIRE_MESSAGE_SCHEMA_VERSIONS:
        schema_json = wire_message_schema_json(schema_id, title, schema_version)
        descriptor: dict[str, Any] = {
            "schemaId": schema_id,
            "kind": "wire_message",
            "schemaVersion": schema_version,
            "title": title,
            "wireContracts": [INTEROP_WIRE_CONTRACT],
            "contentHash": hashlib.sha256(schema_json.encode("utf-8")).hexdigest(),
        }
        if include_schema_json:
            descriptor["schemaJson"] = schema_json
        descriptors.append(descriptor)
    for schema_version, title, schema_id in SHARED_CONTRACT_SCHEMA_VERSIONS:
        schema_json = shared_contract_schema_json(schema_id, title, schema_version)
        descriptor = {
            "schemaId": schema_id,
            "kind": "shared_contract",
            "schemaVersion": schema_version,
            "title": title,
            "wireContracts": [INTEROP_WIRE_CONTRACT],
            "contentHash": hashlib.sha256(schema_json.encode("utf-8")).hexdigest(),
        }
        if include_schema_json:
            descriptor["schemaJson"] = schema_json
        descriptors.append(descriptor)
    return descriptors


def wire_message_schema_catalog(*, include_schema_json: bool = True) -> CultNetSchemaCatalog:
    catalog = CultNetSchemaCatalog()
    for descriptor in wire_message_schema_descriptors(include_schema_json):
        catalog.upsert(descriptor)
    return catalog


def wire_message_schema_json(schema_id: str, title: str, schema_version: str) -> str:
    properties = {
        "schemaVersion": {"const": schema_version},
        **wire_message_schema_properties(schema_version),
    }
    return json.dumps({
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": schema_id,
        "title": title,
        "type": "object",
        "required": wire_message_required_fields(schema_version),
        "properties": properties,
        "$defs": wire_message_shared_defs(),
    }, separators=(",", ":"), sort_keys=True)


def shared_contract_schema_json(schema_id: str, title: str, schema_version: str) -> str:
    if schema_version != TRANSPORT_PROFILE_SCHEMA_VERSION:
        raise ValueError(f"unsupported shared contract schema version {schema_version}")
    return json.dumps({
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": schema_id,
        "title": title,
        "type": "object",
        "required": ["schemaVersion", "runtimeId", "transports"],
        "additionalProperties": False,
        "properties": {
            "schemaVersion": {"const": TRANSPORT_PROFILE_SCHEMA_VERSION},
            "runtimeId": {"type": "string", "minLength": 1},
            "transports": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["transportId", "protocol", "channels"],
                    "additionalProperties": False,
                    "properties": {
                        "transportId": {"type": "string", "minLength": 1},
                        "protocol": {"type": "string", "enum": ["tcp_framed", "litenetlib", "websocket", "rudp"]},
                        "host": {"type": "string", "minLength": 1},
                        "port": {"type": "integer", "minimum": 1, "maximum": 65535},
                        "path": {"type": "string", "minLength": 1},
                        "discoveryGroup": {"type": "string", "minLength": 1},
                        "wireContracts": {"type": "array", "items": {"type": "string", "minLength": 1}},
                        "reconnectPolicy": {
                            "type": "object",
                            "required": ["schemaVersion", "policyId", "baseDelayMs", "maxDelayMs", "maxJitterMs"],
                            "additionalProperties": False,
                            "properties": {
                                "schemaVersion": {"const": "cultnet.reconnect_policy.v0"},
                                "policyId": {"type": "string", "minLength": 1},
                                "baseDelayMs": {"type": "integer", "minimum": 0},
                                "maxDelayMs": {"type": "integer", "minimum": 0},
                                "maxJitterMs": {"type": "integer", "minimum": 0},
                                "maxAttempts": {"type": "integer", "minimum": 1},
                            },
                        },
                        "channels": {
                            "type": "array",
                            "items": {
                                "type": "object",
                                "required": ["channelId", "delivery", "ordering"],
                                "additionalProperties": False,
                                "properties": {
                                    "channelId": {"type": "string", "minLength": 1},
                                    "delivery": {"type": "string", "enum": ["reliable", "unreliable"]},
                                    "ordering": {"type": "string", "enum": ["ordered", "unordered", "sequenced"]},
                                    "maxPayloadBytes": {"type": "integer", "minimum": 1},
                                    "maxFragmentBytes": {"type": "integer", "minimum": 1},
                                    "maxPendingReliablePackets": {"type": "integer", "minimum": 1},
                                },
                            },
                        },
                    },
                },
            },
        },
    }, separators=(",", ":"), sort_keys=True)


def wire_message_required_fields(schema_version: str) -> list[str]:
    required = {
        "cultnet.hello.v0": ["schemaVersion", "runtimeId"],
        "cultnet.error.v0": ["schemaVersion", "error"],
        "cultnet.login.v0": ["schemaVersion", "nonce", "auth", "password"],
        "cultnet.register.v0": ["schemaVersion", "nonce", "email", "password", "name"],
        "cultnet.verify.v0": ["schemaVersion", "nonce", "session"],
        "cultnet.login_success.v0": ["schemaVersion", "nonce", "session"],
        "cultnet.document_delete.v0": ["schemaVersion", "messageId", "schemaId", "recordKey"],
        "cultnet.document_put_raw.v0": ["schemaVersion", "messageId", "document"],
        "cultnet.snapshot_request.v0": ["schemaVersion", "messageId"],
        "cultnet.snapshot_response_raw.v0": ["schemaVersion", "messageId", "documents"],
        "cultnet.schema_catalog_request.v0": ["schemaVersion", "messageId"],
        "cultnet.schema_catalog_response.v0": ["schemaVersion", "messageId", "schemas"],
        "cultnet.database_subscribe.v0": ["schemaVersion", "messageId", "subscriptionId"],
        "cultnet.database_unsubscribe.v0": ["schemaVersion", "messageId", "subscriptionId"],
        "cultnet.database_change_raw.v0": ["schemaVersion", "messageId", "subscriptionId", "changeKind"],
        "cultnet.shard_catalog_request.v0": ["schemaVersion", "messageId"],
        "cultnet.shard_catalog_response.v0": ["schemaVersion", "messageId", "shards"],
        "cultnet.shard_log_request.v0": ["schemaVersion", "messageId", "shardId", "afterSequence"],
        "cultnet.shard_log_response.v0": ["schemaVersion", "messageId", "shardId", "shardEpoch", "entries", "resyncRequired"],
        "cultnet.simulation_observation.v0": ["schemaVersion", "messageId", "observation"],
        "cultnet.simulation_consensus_candidate.v0": [
            "schemaVersion",
            "messageId",
            "shardId",
            "shardEpoch",
            "frame",
            "subjectId",
            "claimKind",
            "claimHash",
            "witnessCount",
            "supportWeight",
            "totalWeight",
            "hasQuorum",
            "confidence",
        ],
        VERSE_CATALOG_REQUEST: ["schemaVersion", "messageId"],
        "cultmesh.verse_catalog_response.v0": ["schemaVersion", "messageId", "verses"],
        PEER_EXCHANGE_REQUEST: ["schemaVersion", "messageId"],
        "cultmesh.peer_exchange_response.v0": ["schemaVersion", "messageId", "peers"],
    }
    return required.get(schema_version, ["schemaVersion"])


def wire_message_schema_properties(schema_version: str) -> dict[str, Any]:
    string_array = {"type": "array", "items": {"type": "string"}}
    common: dict[str, dict[str, Any]] = {
        "messageId": {"type": "string"},
        "schemaId": {"type": "string"},
        "schemaIds": string_array,
        "recordKey": {"type": "string"},
        "recordKeys": string_array,
        "shardId": {"type": "string"},
        "shardEpoch": {"type": "integer"},
        "subscriptionId": {"type": "string"},
    }
    by_version: dict[str, dict[str, Any]] = {
        "cultnet.hello.v0": {
            "runtimeId": {"type": "string"},
            "runtimeKind": {"type": "string"},
            "agentId": {"type": "string"},
            "displayName": {"type": "string"},
            "supportedDocumentTypes": string_array,
            "supportedMutationContracts": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "documentType": {"type": "string"},
                        "payloadSchemaVersion": {"type": "string"},
                        "operations": string_array,
                        "authority": {"type": "string"},
                        "intentDocumentTypes": string_array,
                        "receiptDocumentTypes": string_array,
                    },
                    "additionalProperties": True,
                },
            },
            "supportedMessageVersions": string_array,
            "transportProfiles": {
                "type": "array",
                "items": {
                    "$ref": TRANSPORT_PROFILE_SCHEMA_ID,
                },
            },
            "supportsSchemaCatalog": {"type": "boolean"},
        },
        "cultnet.error.v0": {
            "messageId": common["messageId"],
            "error": {"type": "string"},
            "code": {"type": "string"},
            "details": {"type": "object", "additionalProperties": True},
        },
        "cultnet.login.v0": {
            "nonce": {"type": "string", "minLength": 1},
            "auth": {"type": "string", "minLength": 1},
            "password": {"type": "string", "minLength": 1},
        },
        "cultnet.register.v0": {
            "nonce": {"type": "string", "minLength": 1},
            "email": {"type": "string", "minLength": 1},
            "password": {"type": "string", "minLength": 1},
            "name": {"type": "string", "minLength": 1},
        },
        "cultnet.verify.v0": {
            "nonce": {"type": "string", "minLength": 1},
            "session": {"type": "string", "minLength": 1},
        },
        "cultnet.login_success.v0": {
            "nonce": {"type": "string", "minLength": 1},
            "session": {"type": "string", "minLength": 1},
        },
        "cultnet.document_delete.v0": {
            **common,
        },
        "cultnet.document_put_raw.v0": {
            "messageId": common["messageId"],
            "document": {"$ref": "#/$defs/rawDocumentRecord"},
        },
        "cultnet.snapshot_request.v0": {
            **common,
        },
        "cultnet.snapshot_response_raw.v0": {
            **common,
            "documents": {"type": "array", "items": {"$ref": "#/$defs/rawDocumentRecord"}},
            "shardLogSequence": {"type": "integer"},
        },
        "cultnet.schema_catalog_request.v0": {
            "messageId": common["messageId"],
            "includeSchemaJson": {"type": "boolean"},
            "schemaIds": common["schemaIds"],
            "kinds": string_array,
        },
        "cultnet.schema_catalog_response.v0": {
            "messageId": common["messageId"],
            "schemas": {"type": "array", "items": {"$ref": "#/$defs/schemaDescriptor"}},
        },
        "cultnet.database_subscribe.v0": {
            **common,
            "includeSnapshot": {"type": "boolean"},
        },
        "cultnet.database_unsubscribe.v0": {
            "messageId": common["messageId"],
            "subscriptionId": common["subscriptionId"],
        },
        "cultnet.database_change_raw.v0": {
            "messageId": common["messageId"],
            "subscriptionId": common["subscriptionId"],
            "changeKind": {"enum": ["added", "updated", "removed"]},
            "document": {"$ref": "#/$defs/rawDocumentRecord"},
            "schemaId": common["schemaId"],
            "recordKey": common["recordKey"],
        },
        "cultnet.shard_catalog_request.v0": {
            **common,
        },
        "cultnet.shard_catalog_response.v0": {
            "messageId": common["messageId"],
            "shards": {"type": "array", "items": {"$ref": "#/$defs/shardDescriptor"}},
        },
        "cultnet.shard_log_request.v0": {
            **common,
            "afterSequence": {"type": "integer"},
            "limit": {"type": "integer"},
        },
        "cultnet.shard_log_response.v0": {
            **common,
            "entries": {"type": "array", "items": {"$ref": "#/$defs/shardLogEntry"}},
            "resyncRequired": {"type": "boolean"},
            "reason": {"type": "string"},
            "compactedThrough": {"type": "integer"},
        },
        "cultnet.simulation_observation.v0": {
            "messageId": common["messageId"],
            "observation": {"$ref": "#/$defs/simulationObservation"},
        },
        "cultnet.simulation_consensus_candidate.v0": {
            **common,
            "frame": {"type": "integer"},
            "subjectId": {"type": "string"},
            "claimKind": {"type": "string"},
            "claimHash": {"type": "string"},
            "claimSummary": {"type": "string"},
            "witnessCount": {"type": "integer"},
            "supportWeight": {"type": "number"},
            "totalWeight": {"type": "number"},
            "hasQuorum": {"type": "boolean"},
            "confidence": {"type": "number"},
        },
        VERSE_CATALOG_REQUEST: {
            "messageId": common["messageId"],
            "transportVersion": {"type": "string"},
        },
        "cultmesh.verse_catalog_response.v0": {
            "messageId": common["messageId"],
            "verses": {"type": "array", "items": {"$ref": "#/$defs/verseDescriptor"}},
        },
        PEER_EXCHANGE_REQUEST: {
            "messageId": common["messageId"],
            "verseId": {"type": "string"},
            "roles": string_array,
            "shardIds": string_array,
        },
        "cultmesh.peer_exchange_response.v0": {
            "messageId": common["messageId"],
            "peers": {"type": "array", "items": {"$ref": "#/$defs/peerCard"}},
        },
    }
    return by_version.get(schema_version, {})


def wire_message_shared_defs() -> dict[str, Any]:
    return {
        "rawDocumentRecord": {
            "type": "object",
            "required": ["schemaId", "recordKey", "payload"],
            "properties": {
                "schemaId": {"type": "string"},
                "recordKey": {"type": "string"},
                "payload": {},
                "authorRuntimeId": {"type": "string"},
                "updatedAtUnixMs": {"type": "integer"},
            },
        },
        "schemaDescriptor": {
            "type": "object",
            "required": ["schemaId", "kind", "schemaVersion"],
            "properties": {
                "schemaId": {"type": "string"},
                "kind": {"type": "string"},
                "schemaVersion": {"type": "string"},
                "documentType": {"type": "string"},
                "title": {"type": "string"},
                "wireContracts": {"type": "array", "items": {"type": "string"}},
                "contentHash": {"type": "string"},
                "schemaJson": {"type": "string"},
            },
        },
        "shardDescriptor": {
            "type": "object",
            "required": ["shardId", "epoch", "ownerRuntimeId"],
            "properties": {
                "shardId": {"type": "string"},
                "epoch": {"type": "integer"},
                "ownerRuntimeId": {"type": "string"},
                "endpoint": {"type": "string"},
                "schemaIds": {"type": "array", "items": {"type": "string"}},
                "recordKeyRanges": {"type": "array"},
            },
        },
        "shardLogEntry": {
            "type": "object",
            "required": ["sequence", "changeKind"],
            "properties": {
                "sequence": {"type": "integer"},
                "changeKind": {"enum": ["added", "updated", "removed"]},
                "put": {"type": "object"},
                "delete": {"type": "object"},
            },
        },
        "simulationObservation": {
            "type": "object",
            "required": ["shardId", "shardEpoch", "frame", "subjectId", "claimKind", "claimHash", "witnessRuntimeId"],
            "properties": {
                "shardId": {"type": "string"},
                "shardEpoch": {"type": "integer"},
                "frame": {"type": "integer"},
                "subjectId": {"type": "string"},
                "claimKind": {"type": "string"},
                "claimHash": {"type": "string"},
                "claimSummary": {"type": "string"},
                "witnessRuntimeId": {"type": "string"},
                "supportWeight": {"type": "number"},
            },
        },
        "verseDescriptor": {"type": "object"},
        "peerCard": {"type": "object"},
    }


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
