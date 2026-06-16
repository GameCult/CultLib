from __future__ import annotations

import socket
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Protocol

import msgpack

from .messages import (
    CultNetMessage,
    database_subscribe,
    database_unsubscribe,
    schema_catalog_request,
    shard_catalog_request,
    shard_log_request,
    snapshot_request,
)
from .schema_catalog import CultNetSchemaCatalog, CultNetSchemaDescriptor
from .shard_catalog import CultNetShardCatalog, CultNetShardDescriptor
from .shard_log import CultNetShardLogResponse
from .snapshot import CultNetRawSnapshotResponse
from .subscription import CultNetDatabaseChange
from .transport import TcpFramedTransportConnection, create_tcp_framed_transport_profile


class CultNetSchemaTransport(Protocol):
    def __enter__(self) -> "CultNetSchemaTransport": ...
    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None: ...
    def send(self, channel_id: str, payload: bytes) -> None: ...
    def receive(self) -> Any: ...
    def close(self) -> None: ...


CultNetSchemaTransportFactory = Callable[[], CultNetSchemaTransport]


class CultNetPeerError(RuntimeError):
    def __init__(self, response: dict[str, Any]) -> None:
        self.response = dict(response)
        self.peer_error = str(response.get("error") or "CultNet peer returned an error")
        super().__init__(self.peer_error)


@dataclass(frozen=True)
class CultNetRawClient:
    host: str
    port: int
    timeout_seconds: float = 4.0
    create_transport: CultNetSchemaTransportFactory | None = None

    def send(self, message: CultNetMessage | dict[str, Any]) -> None:
        wire = message.to_wire() if isinstance(message, CultNetMessage) else message
        with self.open_transport() as transport:
            transport.send("schema", msgpack.packb(wire, use_bin_type=True))

    def request(self, message: CultNetMessage | dict[str, Any], *, expected_schema_version: str) -> dict[str, Any]:
        wire = message.to_wire() if isinstance(message, CultNetMessage) else message
        with self.open_transport() as transport:
            transport.send("schema", msgpack.packb(wire, use_bin_type=True))
            response = msgpack.unpackb(transport.receive().payload, raw=False)
        if not isinstance(response, dict):
            raise ValueError("CultNet response must be a MessagePack map")
        schema_version = response.get("schemaVersion")
        if schema_version != expected_schema_version:
            _raise_peer_error(response)
            raise ValueError(f"Expected {expected_schema_version}, received {schema_version!r}")
        return response

    def open_transport(self) -> CultNetSchemaTransport:
        if self.create_transport is not None:
            return self.create_transport()
        return create_tcp_framed_schema_transport(
            host=self.host,
            port=self.port,
            timeout_seconds=self.timeout_seconds,
            runtime_id="cultnet-python-client",
        )

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

    def fetch_schema_descriptors(
        self,
        *,
        message_id: str = "cultnet-python-schema-catalog",
        include_schema_json: bool = False,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> list[CultNetSchemaDescriptor]:
        response = self.fetch_schema_catalog(
            message_id=message_id,
            include_schema_json=include_schema_json,
            schema_ids=schema_ids,
            kinds=kinds,
        )
        return CultNetSchemaCatalog().apply_response(response)

    def sync_schema_catalog(
        self,
        catalog: CultNetSchemaCatalog,
        *,
        message_id: str = "cultnet-python-schema-catalog",
        include_schema_json: bool = False,
        schema_ids: list[str] | None = None,
        kinds: list[str] | None = None,
    ) -> list[CultNetSchemaDescriptor]:
        response = self.fetch_schema_catalog(
            message_id=message_id,
            include_schema_json=include_schema_json,
            schema_ids=schema_ids,
            kinds=kinds,
        )
        return catalog.apply_response(response)

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

    def fetch_snapshot_response(
        self,
        *,
        message_id: str = "cultnet-python-snapshot",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        shard_id: str | None = None,
        shard_epoch: int | None = None,
    ) -> CultNetRawSnapshotResponse:
        return CultNetRawSnapshotResponse.from_wire(self.fetch_snapshot(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
            shard_id=shard_id,
            shard_epoch=shard_epoch,
        ))

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

    def fetch_shard_descriptors(
        self,
        *,
        message_id: str = "cultnet-python-shard-catalog",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> list[CultNetShardDescriptor]:
        response = self.fetch_shard_catalog(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
        )
        return CultNetShardCatalog().apply_response(response)

    def sync_shard_catalog(
        self,
        catalog: CultNetShardCatalog,
        *,
        message_id: str = "cultnet-python-shard-catalog",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
    ) -> list[CultNetShardDescriptor]:
        response = self.fetch_shard_catalog(
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
        )
        return catalog.apply_response(response)

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

    def fetch_shard_log_response(
        self,
        *,
        shard_id: str,
        message_id: str = "cultnet-python-shard-log",
        shard_epoch: int | None = None,
        after_sequence: int = 0,
        limit: int | None = None,
    ) -> CultNetShardLogResponse:
        return CultNetShardLogResponse.from_wire(self.fetch_shard_log(
            shard_id=shard_id,
            message_id=message_id,
            shard_epoch=shard_epoch,
            after_sequence=after_sequence,
            limit=limit,
        ))

    def subscribe_database(
        self,
        *,
        subscription_id: str,
        message_id: str = "cultnet-python-subscribe",
        schema_ids: list[str] | None = None,
        record_keys: list[str] | None = None,
        include_snapshot: bool = True,
    ) -> "CultNetDatabaseSubscription":
        return CultNetDatabaseSubscription(
            host=self.host,
            port=self.port,
            timeout_seconds=self.timeout_seconds,
            create_transport=self.create_transport,
            subscription_id=subscription_id,
            message_id=message_id,
            schema_ids=schema_ids,
            record_keys=record_keys,
            include_snapshot=include_snapshot,
        )


@dataclass
class CultNetDatabaseSubscription:
    host: str
    port: int
    timeout_seconds: float
    subscription_id: str
    message_id: str = "cultnet-python-subscribe"
    schema_ids: list[str] | None = None
    record_keys: list[str] | None = None
    include_snapshot: bool = True
    create_transport: CultNetSchemaTransportFactory | None = None
    _transport: CultNetSchemaTransport | None = None

    def __enter__(self) -> "CultNetDatabaseSubscription":
        self._transport = (
            self.create_transport()
            if self.create_transport is not None
            else create_tcp_framed_schema_transport(
                host=self.host,
                port=self.port,
                timeout_seconds=self.timeout_seconds,
                runtime_id="cultnet-python-subscription",
            )
        )
        self.send(database_subscribe(
            message_id=self.message_id,
            subscription_id=self.subscription_id,
            schema_ids=self.schema_ids,
            record_keys=self.record_keys,
            include_snapshot=self.include_snapshot,
        ))
        return self

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        try:
            if self._transport is not None:
                self.send(database_unsubscribe(
                    message_id=f"{self.message_id}-unsubscribe",
                    subscription_id=self.subscription_id,
                ))
        finally:
            if self._transport is not None:
                self._transport.close()
                self._transport = None

    def send(self, message: CultNetMessage | dict[str, Any]) -> None:
        if self._transport is None:
            raise RuntimeError("CultNet database subscription is not open")
        wire = message.to_wire() if isinstance(message, CultNetMessage) else message
        self._transport.send("schema", msgpack.packb(wire, use_bin_type=True))

    def read_next(self) -> dict[str, Any]:
        if self._transport is None:
            raise RuntimeError("CultNet database subscription is not open")
        response = msgpack.unpackb(self._transport.receive().payload, raw=False)
        if not isinstance(response, dict):
            raise ValueError("CultNet subscription response must be a MessagePack map")
        schema_version = response.get("schemaVersion")
        _raise_peer_error(response)
        if schema_version not in {"cultnet.snapshot_response_raw.v0", "cultnet.database_change_raw.v0"}:
            raise ValueError(f"Unexpected CultNet subscription message {schema_version!r}")
        return response

    def read_next_change(self) -> CultNetDatabaseChange:
        message = self.read_next()
        return CultNetDatabaseChange.from_wire(message)

    def read_next_snapshot_response(self) -> CultNetRawSnapshotResponse:
        message = self.read_next()
        return CultNetRawSnapshotResponse.from_wire(message)

    def iter_messages(self, *, max_messages: int | None = None) -> Any:
        received = 0
        while max_messages is None or received < max_messages:
            yield self.read_next()
            received += 1

    def iter_snapshot_responses(self, *, max_messages: int | None = None) -> Any:
        received = 0
        while max_messages is None or received < max_messages:
            yield self.read_next_snapshot_response()
            received += 1

    def iter_changes(self, *, max_messages: int | None = None) -> Any:
        received = 0
        while max_messages is None or received < max_messages:
            yield self.read_next_change()
            received += 1


def _raise_peer_error(response: dict[str, Any]) -> None:
    if response.get("schemaVersion") == "cultnet.error.v0":
        raise CultNetPeerError(response)


def create_tcp_framed_schema_transport(
    *,
    host: str,
    port: int,
    timeout_seconds: float = 4.0,
    runtime_id: str = "cultnet-python-client",
) -> TcpFramedTransportConnection:
    connection = socket.create_connection((host, port), timeout=timeout_seconds)
    connection.settimeout(timeout_seconds)
    stream = connection.makefile("rwb")
    return _OwnedTcpFramedTransportConnection(
        stream,
        connection,
        profile=create_tcp_framed_transport_profile(runtime_id, host=host, port=port),
    )


class _OwnedTcpFramedTransportConnection(TcpFramedTransportConnection):
    def __init__(self, stream: Any, connection: socket.socket, *, profile: dict[str, Any]) -> None:
        super().__init__(stream, profile=profile)
        self._connection = connection

    def close(self) -> None:
        try:
            super().close()
        finally:
            self._connection.close()
