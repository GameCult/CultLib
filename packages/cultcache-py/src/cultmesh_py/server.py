from __future__ import annotations

import socket
import threading
from dataclasses import dataclass, field
from typing import Any

import msgpack

from cultnet_py import read_frame, wire_message_schema_descriptors, write_frame

from .node import CultMeshNode
from .wire import (
    PEER_EXCHANGE_REQUEST,
    VERSE_CATALOG_REQUEST,
    CultMeshPeerCatalog,
    CultMeshVerseCatalog,
)


@dataclass(frozen=True)
class _DatabaseSubscription:
    subscription_id: str
    schema_ids: set[str]
    record_keys: set[str]

    def matches(self, document: dict[str, Any]) -> bool:
        schema_id = str(document.get("schemaId") or "")
        record_key = str(document.get("recordKey") or "")
        return (
            (not self.schema_ids or schema_id in self.schema_ids)
            and (not self.record_keys or record_key in self.record_keys)
        )


@dataclass
class CultMeshLocalServer:
    node: CultMeshNode
    verse_catalog: CultMeshVerseCatalog = field(default_factory=CultMeshVerseCatalog)
    peer_catalog: CultMeshPeerCatalog = field(default_factory=CultMeshPeerCatalog)
    host: str = "127.0.0.1"
    port: int = 0
    display_name: str | None = None
    runtime_kind: str = "python"
    _socket: socket.socket | None = field(default=None, init=False, repr=False)
    _stop: threading.Event = field(default_factory=threading.Event, init=False, repr=False)
    _thread: threading.Thread | None = field(default=None, init=False, repr=False)

    def start(self) -> "CultMeshLocalServer":
        if self._socket is not None:
            return self
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind((self.host, self.port))
        server.listen()
        server.settimeout(0.2)
        self._socket = server
        self.port = int(server.getsockname()[1])
        self._stop.clear()
        self._thread = threading.Thread(target=self._accept_loop, daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._socket is not None:
            try:
                self._socket.close()
            except OSError:
                pass
            self._socket = None
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None

    def __enter__(self) -> "CultMeshLocalServer":
        return self.start()

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        self.stop()

    def handle_message(self, message: dict[str, Any]) -> dict[str, Any] | None:
        schema_version = message.get("schemaVersion")
        if schema_version == "cultnet.hello.v0":
            return self._hello_response()
        if schema_version == "cultnet.schema_catalog_request.v0":
            return self._schema_catalog_response(message)
        if schema_version == "cultnet.snapshot_request.v0":
            return self.node.database.create_snapshot_response(
                message_id=str(message.get("messageId") or ""),
                schema_ids=[str(value) for value in message.get("schemaIds") or []],
                record_keys=[str(value) for value in message.get("recordKeys") or []],
                shard_id=message.get("shardId"),
                shard_epoch=message.get("shardEpoch"),
            )
        if schema_version == "cultnet.shard_catalog_request.v0":
            return self._shard_catalog_response(message)
        if schema_version == "cultnet.shard_log_request.v0":
            return self.node.database.create_shard_log_response(
                message_id=str(message.get("messageId") or ""),
                shard_id=str(message.get("shardId") or ""),
                shard_epoch=message.get("shardEpoch"),
                after_sequence=int(message.get("afterSequence") or 0),
                limit=message.get("limit"),
            )
        if schema_version == VERSE_CATALOG_REQUEST:
            return self.verse_catalog.create_response(message)
        if schema_version == PEER_EXCHANGE_REQUEST:
            return self.peer_catalog.create_response(message)
        return None

    def _accept_loop(self) -> None:
        while not self._stop.is_set():
            server = self._socket
            if server is None:
                return
            try:
                client, _ = server.accept()
            except TimeoutError:
                continue
            except OSError:
                return
            threading.Thread(target=self._handle_connection, args=(client,), daemon=True).start()

    def _handle_connection(self, client: socket.socket) -> None:
        with client:
            stream = client.makefile("rwb")
            subscriptions: dict[str, _DatabaseSubscription] = {}
            while not self._stop.is_set():
                try:
                    message = msgpack.unpackb(read_frame(stream), raw=False)
                except EOFError:
                    return
                if not isinstance(message, dict):
                    continue
                responses = self._handle_connection_message(message, subscriptions)
                for response in responses:
                    write_frame(stream, msgpack.packb(response, use_bin_type=True))
                    stream.flush()

    def _handle_connection_message(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        schema_version = message.get("schemaVersion")
        if schema_version == "cultnet.database_subscribe.v0":
            subscription_id = str(message.get("subscriptionId") or message.get("messageId") or "")
            if not subscription_id:
                return []
            subscriptions[subscription_id] = _DatabaseSubscription(
                subscription_id=subscription_id,
                schema_ids={str(value) for value in message.get("schemaIds") or []},
                record_keys={str(value) for value in message.get("recordKeys") or []},
            )
            if message.get("includeSnapshot", True) is False:
                return []
            return [
                self.node.database.create_snapshot_response(
                    message_id=str(message.get("messageId") or ""),
                    schema_ids=list(subscriptions[subscription_id].schema_ids),
                    record_keys=list(subscriptions[subscription_id].record_keys),
                )
            ]
        if schema_version == "cultnet.database_unsubscribe.v0":
            subscriptions.pop(str(message.get("subscriptionId") or ""), None)
            return []
        if schema_version == "cultnet.document_put_raw.v0":
            return self._handle_raw_put(message, subscriptions)
        if schema_version == "cultnet.document_delete.v0":
            return self._handle_raw_delete(message, subscriptions)
        response = self.handle_message(message)
        return [] if response is None else [response]

    def _handle_raw_put(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        document_record = message.get("document")
        if not isinstance(document_record, dict):
            return []
        change = self.node.database.apply_raw_put_message(message)
        if change is None:
            return []
        return self._database_change_notifications(message, document_record, change.change_kind, subscriptions)

    def _handle_raw_delete(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        schema_id = str(message.get("schemaId") or "")
        record_key = str(message.get("recordKey") or "")
        if not schema_id or not record_key:
            return []
        change = self.node.database.apply_raw_delete_message(message)
        if change is None:
            return []
        return self._database_delete_notifications(message, subscriptions)

    def _database_change_notifications(
        self,
        message: dict[str, Any],
        document: dict[str, Any],
        change_kind: str,
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        notifications = []
        for subscription in subscriptions.values():
            if not subscription.matches(document):
                continue
            message_id = message.get("messageId") or document.get("recordKey") or "change"
            notifications.append({
                "schemaVersion": "cultnet.database_change_raw.v0",
                "messageId": f"{message_id}:{subscription.subscription_id}",
                "subscriptionId": subscription.subscription_id,
                "changeKind": change_kind,
                "document": document,
            })
        return notifications

    def _database_delete_notifications(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        schema_id = str(message.get("schemaId") or "")
        record_key = str(message.get("recordKey") or "")
        notifications = []
        document = {"schemaId": schema_id, "recordKey": record_key}
        for subscription in subscriptions.values():
            if not subscription.matches(document):
                continue
            message_id = message.get("messageId") or record_key or "change"
            notifications.append({
                "schemaVersion": "cultnet.database_change_raw.v0",
                "messageId": f"{message_id}:{subscription.subscription_id}",
                "subscriptionId": subscription.subscription_id,
                "changeKind": "removed",
                "schemaId": schema_id,
                "recordKey": record_key,
            })
        return notifications

    def _hello_response(self) -> dict[str, Any]:
        return {
            "schemaVersion": "cultnet.hello.v0",
            "runtimeId": self.node.runtime_id,
            "runtimeKind": self.runtime_kind,
            "displayName": self.display_name or self.node.runtime_id,
            "supportedDocumentTypes": [document.type for document in self.node.documents],
            "supportedMessageVersions": [
                "cultnet.hello.v0",
                "cultnet.schema_catalog_request.v0",
                "cultnet.snapshot_request.v0",
                "cultnet.database_subscribe.v0",
                "cultnet.database_unsubscribe.v0",
                "cultnet.document_put_raw.v0",
                "cultnet.document_delete.v0",
                "cultnet.shard_catalog_request.v0",
                "cultnet.shard_log_request.v0",
                VERSE_CATALOG_REQUEST,
                PEER_EXCHANGE_REQUEST,
            ],
            "supportsSchemaCatalog": True,
        }

    def _schema_catalog_response(self, request: dict[str, Any]) -> dict[str, Any]:
        requested_schema_ids = {str(value) for value in request.get("schemaIds") or []}
        requested_kinds = {str(value) for value in request.get("kinds") or []}
        include_schema_json = request.get("includeSchemaJson") is True
        schemas = []
        for descriptor in wire_message_schema_descriptors(include_schema_json):
            if requested_schema_ids and descriptor["schemaId"] not in requested_schema_ids:
                continue
            if requested_kinds and descriptor["kind"] not in requested_kinds:
                continue
            schemas.append(descriptor)
        for document in self.node.documents:
            entry = document.catalog_entry()
            if requested_schema_ids and entry.schema_id not in requested_schema_ids:
                continue
            if requested_kinds and "document_payload" not in requested_kinds:
                continue
            descriptor = {
                "schemaId": entry.schema_id,
                "kind": "document_payload",
                "schemaVersion": entry.schema_version,
                "documentType": document.type,
                "title": entry.schema_name,
                "wireContracts": ["cultnet.document_put_raw.v0", "cultnet.snapshot_response_raw.v0"],
                "contentHash": entry.content_hash,
            }
            if include_schema_json:
                descriptor["schemaJson"] = entry.canonical_schema_json
            schemas.append(descriptor)
        return {
            "schemaVersion": "cultnet.schema_catalog_response.v0",
            "messageId": request.get("messageId", ""),
            "schemas": schemas,
        }

    def _shard_catalog_response(self, request: dict[str, Any]) -> dict[str, Any]:
        requested_schema_ids = {str(value) for value in request.get("schemaIds") or []}
        schema_ids = [document.catalog_entry().schema_id for document in self.node.documents]
        if requested_schema_ids:
            schema_ids = [schema_id for schema_id in schema_ids if schema_id in requested_schema_ids]
        shards = []
        if schema_ids:
            shards.append({
                "shardId": "primary",
                "ownerRuntimeId": self.node.runtime_id,
                "epoch": self.node.database.shard_epoch("primary"),
                "isPrimary": True,
                "schemaIds": schema_ids,
                "keyPrefix": "",
                "primaryEndpoints": [f"cultnet://{self.host}:{self.port}"],
                "replicaEndpoints": [],
                "readReplicaEndpoints": [f"cultnet://{self.host}:{self.port}"],
                "region": "local",
            })
        return {
            "schemaVersion": "cultnet.shard_catalog_response.v0",
            "messageId": request.get("messageId", ""),
            "shards": shards,
        }
