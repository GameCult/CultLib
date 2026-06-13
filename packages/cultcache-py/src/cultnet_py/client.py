from __future__ import annotations

import socket
from dataclasses import dataclass
from typing import Any

import msgpack

from .framing import read_frame, write_frame
from .messages import (
    CultNetMessage,
    schema_catalog_request,
    shard_catalog_request,
    shard_log_request,
    snapshot_request,
)


@dataclass(frozen=True)
class CultNetRawClient:
    host: str
    port: int
    timeout_seconds: float = 4.0

    def request(self, message: CultNetMessage | dict[str, Any], *, expected_schema_version: str) -> dict[str, Any]:
        wire = message.to_wire() if isinstance(message, CultNetMessage) else message
        with socket.create_connection((self.host, self.port), timeout=self.timeout_seconds) as connection:
            connection.settimeout(self.timeout_seconds)
            stream = connection.makefile("rwb")
            write_frame(stream, msgpack.packb(wire, use_bin_type=True))
            stream.flush()
            response = msgpack.unpackb(read_frame(stream), raw=False)
        if not isinstance(response, dict):
            raise ValueError("CultNet response must be a MessagePack map")
        schema_version = response.get("schemaVersion")
        if schema_version != expected_schema_version:
            raise ValueError(f"Expected {expected_schema_version}, received {schema_version!r}")
        return response

    def fetch_schema_catalog(
        self,
        *,
        message_id: str = "cultnet-python-schema-catalog",
        include_schema_json: bool = False,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> dict[str, Any]:
        return self.request(
            schema_catalog_request(
                message_id=message_id,
                include_schema_json=include_schema_json,
                schema_ids=schema_ids,
                kinds=kinds,
            ),
            expected_schema_version="cultnet.schema_catalog_response.v0",
        )

    def fetch_snapshot(
        self,
        *,
        message_id: str = "cultnet-python-snapshot",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> dict[str, Any]:
        return self.request(
            snapshot_request(
                message_id=message_id,
                schema_ids=schema_ids,
                record_keys=record_keys,
                shard_id=shard_id,
                shard_epoch=shard_epoch,
            ),
            expected_schema_version="cultnet.snapshot_response_raw.v0",
        )

    def fetch_shard_catalog(
        self,
        *,
        message_id: str = "cultnet-python-shard-catalog",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> dict[str, Any]:
        return self.request(
            shard_catalog_request(message_id=message_id, schema_ids=schema_ids, record_keys=record_keys),
            expected_schema_version="cultnet.shard_catalog_response.v0",
        )

    def fetch_shard_log(
        self,
        *,
        shard_id: str,
        message_id: str = "cultnet-python-shard-log",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> dict[str, Any]:
        return self.request(
            shard_log_request(
                message_id=message_id,
                shard_id=shard_id,
                shard_epoch=shard_epoch,
                after_sequence=after_sequence,
                limit=limit,
            ),
            expected_schema_version="cultnet.shard_log_response.v0",
        )

