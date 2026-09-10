from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class CultNetShardDescriptor:
    shard_id: str
    owner_runtime_id: str
    epoch: int = 0
    is_primary: bool = False
    schema_ids: tuple[str, ...] = ()
    key_prefix: str | None = None
    primary_endpoints: tuple[str, ...] = ()
    replica_endpoints: tuple[str, ...] = ()
    read_replica_endpoints: tuple[str, ...] = ()
    region: str | None = None
    authority_lease_id: str | None = None

    @classmethod
    def from_wire(cls, value: dict[str, Any]) -> "CultNetShardDescriptor":
        shard_id = str(value.get("shardId") or "")
        owner_runtime_id = str(value.get("ownerRuntimeId") or "")
        if not shard_id:
            raise ValueError("shard descriptor shardId must be non-empty")
        if not owner_runtime_id:
            raise ValueError("shard descriptor ownerRuntimeId must be non-empty")
        return cls(
            shard_id=shard_id,
            owner_runtime_id=owner_runtime_id,
            epoch=int(value.get("epoch") or 0),
            is_primary=value.get("isPrimary") is True,
            schema_ids=_string_tuple(value.get("schemaIds")),
            key_prefix=_optional_string(value.get("keyPrefix")),
            primary_endpoints=_string_tuple(value.get("primaryEndpoints")),
            replica_endpoints=_string_tuple(value.get("replicaEndpoints")),
            read_replica_endpoints=_string_tuple(value.get("readReplicaEndpoints")),
            region=_optional_string(value.get("region")),
            authority_lease_id=_optional_string(value.get("authorityLeaseId")),
        )

    def to_wire(self) -> dict[str, Any]:
        wire: dict[str, Any] = {
            "shardId": self.shard_id,
            "ownerRuntimeId": self.owner_runtime_id,
            "epoch": self.epoch,
            "isPrimary": self.is_primary,
            "schemaIds": list(self.schema_ids),
            "primaryEndpoints": list(self.primary_endpoints),
            "replicaEndpoints": list(self.replica_endpoints),
            "readReplicaEndpoints": list(self.read_replica_endpoints),
        }
        if self.key_prefix is not None:
            wire["keyPrefix"] = self.key_prefix
        if self.region is not None:
            wire["region"] = self.region
        if self.authority_lease_id is not None:
            wire["authorityLeaseId"] = self.authority_lease_id
        return wire

    def serves(self, *, schema_id: str | None = None, record_key: str | None = None) -> bool:
        if schema_id is not None and self.schema_ids and not _schema_ids_match_any(self.schema_ids, (schema_id,)):
            return False
        if record_key is not None and self.key_prefix and not record_key.startswith(self.key_prefix):
            return False
        return True


@dataclass
class CultNetShardCatalog:
    _shards: dict[str, CultNetShardDescriptor] = field(default_factory=dict)

    def upsert(self, descriptor: CultNetShardDescriptor | dict[str, Any]) -> CultNetShardDescriptor:
        value = descriptor if isinstance(descriptor, CultNetShardDescriptor) else CultNetShardDescriptor.from_wire(descriptor)
        self._shards[value.shard_id] = value
        return value

    def get(self, shard_id: str) -> CultNetShardDescriptor | None:
        return self._shards.get(shard_id)

    def list(
        self,
        *,
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> list[CultNetShardDescriptor]:
        requested_schema_ids = set(schema_ids or [])
        requested_record_keys = list(record_keys or [])
        return [
            shard
            for shard in sorted(self._shards.values(), key=lambda item: item.shard_id)
            if _shard_matches(shard, requested_schema_ids, requested_record_keys)
        ]

    def apply_response(self, response: dict[str, Any]) -> list[CultNetShardDescriptor]:
        if response.get("schemaVersion") != "cultnet.shard_catalog_response.v0":
            raise ValueError(f"Expected cultnet.shard_catalog_response.v0, received {response.get('schemaVersion')!r}")
        applied = []
        for shard in response.get("shards") or []:
            if not isinstance(shard, dict):
                continue
            applied.append(self.upsert(shard))
        return applied

    def create_response(
        self,
        *,
        message_id: str = "",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> dict[str, Any]:
        return {
            "schemaVersion": "cultnet.shard_catalog_response.v0",
            "messageId": message_id,
            "shards": [shard.to_wire() for shard in self.list(schema_ids=schema_ids, record_keys=record_keys)],
        }


def _shard_matches(
    shard: CultNetShardDescriptor,
    requested_schema_ids: set[str],
    requested_record_keys: list[str],
) -> bool:
    if requested_schema_ids and not _schema_ids_match_any(shard.schema_ids, requested_schema_ids):
        return False
    if requested_record_keys and not any(shard.serves(record_key=record_key) for record_key in requested_record_keys):
        return False
    return True


def _schema_ids_match_any(
    advertised_schema_ids: tuple[str, ...],
    requested_schema_ids: set[str] | tuple[str, ...],
) -> bool:
    return any(
        _schema_ids_match(advertised, requested)
        for advertised in advertised_schema_ids
        for requested in requested_schema_ids
    )


def _schema_ids_match(left: str, right: str) -> bool:
    if left == right:
        return True
    return (_infer_schema_name(left) or left) == (_infer_schema_name(right) or right)


def _infer_schema_name(schema_id: str) -> str | None:
    marker = schema_id.rfind(".v")
    if marker <= 0 or marker + 2 >= len(schema_id):
        return None
    version = schema_id[marker + 2:]
    return schema_id[:marker] if version.isdigit() else None


def _string_tuple(value: Any) -> tuple[str, ...]:
    return tuple(str(item) for item in value or [])


def _optional_string(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value)
    return text if text else None
