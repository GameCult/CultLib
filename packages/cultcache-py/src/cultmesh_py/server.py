from __future__ import annotations

import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Any

import msgpack

from cultnet_py import (
    CultNetSimulationObservationHub,
    CultNetRudpPacket,
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    TcpFramedTransportConnection,
    create_rudp_transport_profile,
    create_tcp_framed_transport_profile,
    decode_rudp_packet,
    encode_rudp_packet,
    wire_message_schema_descriptors,
)

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
class _RudpPeerConnection:
    session: CultNetRudpSession
    subscriptions: dict[str, _DatabaseSubscription] = field(default_factory=dict)


@dataclass
class CultMeshLocalServer:
    node: CultMeshNode
    verse_catalog: CultMeshVerseCatalog = field(default_factory=CultMeshVerseCatalog)
    peer_catalog: CultMeshPeerCatalog = field(default_factory=CultMeshPeerCatalog)
    observation_hub: CultNetSimulationObservationHub | None = None
    host: str = "127.0.0.1"
    port: int = 0
    display_name: str | None = None
    runtime_kind: str = "python"
    max_snapshot_documents: int | None = None
    max_snapshot_bytes: int | None = None
    enable_rudp: bool = True
    rudp_connection_id: int = 0x43554C54
    rudp_resend_delay_ms: int = 25
    _socket: socket.socket | None = field(default=None, init=False, repr=False)
    _rudp_socket: socket.socket | None = field(default=None, init=False, repr=False)
    _stop: threading.Event = field(default_factory=threading.Event, init=False, repr=False)
    _thread: threading.Thread | None = field(default=None, init=False, repr=False)
    _rudp_thread: threading.Thread | None = field(default=None, init=False, repr=False)

    def __post_init__(self) -> None:
        if self.max_snapshot_documents is not None and self.max_snapshot_documents < 0:
            raise ValueError("max_snapshot_documents must be non-negative")
        if self.max_snapshot_bytes is not None and self.max_snapshot_bytes < 0:
            raise ValueError("max_snapshot_bytes must be non-negative")

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
        if self.enable_rudp:
            rudp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            rudp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            rudp_socket.bind((self.host, self.port))
            rudp_socket.settimeout(0.02)
            self._rudp_socket = rudp_socket
            self._rudp_thread = threading.Thread(target=self._rudp_loop, daemon=True)
            self._rudp_thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._rudp_socket is not None:
            try:
                self._rudp_socket.close()
            except OSError:
                pass
            self._rudp_socket = None
        if self._socket is not None:
            try:
                self._socket.close()
            except OSError:
                pass
            self._socket = None
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None
        if self._rudp_thread is not None:
            self._rudp_thread.join(timeout=2.0)
            self._rudp_thread = None

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
            return self._snapshot_response(message)
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
        if schema_version == "cultnet.simulation_observation.v0":
            candidates = self._candidate_responses(message)
            return candidates[0] if candidates else None
        return self._error_response(
            f"Unsupported CultNet message schema: {schema_version!r}.",
            message_id=str(message.get("messageId") or ""),
            code="unsupported_schema_version",
            details={"schemaVersion": schema_version},
        )

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
            transport = TcpFramedTransportConnection(
                stream,
                profile=create_tcp_framed_transport_profile(
                    self.node.runtime_id,
                    transport_id="cultmesh-local-tcp",
                    host=self.host,
                    port=self.port,
                ),
            )
            subscriptions: dict[str, _DatabaseSubscription] = {}
            while not self._stop.is_set():
                try:
                    frame = transport.receive()
                    message = msgpack.unpackb(frame.payload, raw=False)
                except EOFError:
                    return
                if not isinstance(message, dict):
                    continue
                responses = self._handle_connection_message(message, subscriptions)
                for response in responses:
                    transport.send("schema", msgpack.packb(response, use_bin_type=True))

    def _rudp_loop(self) -> None:
        rudp_socket = self._rudp_socket
        if rudp_socket is None:
            return
        peers: dict[tuple[str, int], _RudpPeerConnection] = {}
        while not self._stop.is_set():
            try:
                wire, remote_addr = rudp_socket.recvfrom(65535)
            except TimeoutError:
                self._poll_rudp_resends(rudp_socket, peers)
                continue
            except OSError:
                return

            try:
                packet = decode_rudp_packet(wire)
            except ValueError:
                continue
            if packet.connection_id != self.rudp_connection_id:
                continue

            now_ms = _now_ms()
            peer = peers.get(remote_addr)
            if packet.packet_type == CultNetRudpPacketType.CONNECT:
                peer = _RudpPeerConnection(
                    CultNetRudpSession(
                        CultNetRudpSessionOptions(
                            connection_id=self.rudp_connection_id,
                            resend_delay_ms=self.rudp_resend_delay_ms,
                        )
                    )
                )
                peers[remote_addr] = peer
                self._send_rudp_packet(
                    rudp_socket,
                    remote_addr,
                    peer.session.accept_connect(packet, now_ms, b"cultmesh-local-rudp"),
                )
                continue
            if peer is None:
                continue

            result = peer.session.receive(packet, now_ms)
            if result.reply is not None:
                self._send_rudp_packet(rudp_socket, remote_addr, result.reply)
            if result.disconnected:
                peers.pop(remote_addr, None)
                continue
            for delivered in result.delivered:
                if delivered.channel_id != "schema":
                    continue
                try:
                    message = msgpack.unpackb(delivered.payload, raw=False)
                except (msgpack.ExtraData, msgpack.FormatError, msgpack.StackError, ValueError):
                    continue
                if not isinstance(message, dict):
                    continue
                responses = self._handle_connection_message(message, peer.subscriptions)
                for response in responses:
                    self._send_rudp_schema_frame(
                        rudp_socket,
                        remote_addr,
                        peer.session,
                        msgpack.packb(response, use_bin_type=True),
                    )
            if packet.packet_type == CultNetRudpPacketType.DATA or result.delivered:
                self._send_rudp_packet(rudp_socket, remote_addr, peer.session.create_ack())

    def _poll_rudp_resends(
        self,
        rudp_socket: socket.socket,
        peers: dict[tuple[str, int], _RudpPeerConnection],
    ) -> None:
        now_ms = _now_ms()
        for remote_addr, peer in list(peers.items()):
            for packet in peer.session.due_resends(now_ms):
                self._send_rudp_packet(rudp_socket, remote_addr, packet)

    def _send_rudp_schema_frame(
        self,
        rudp_socket: socket.socket,
        remote_addr: tuple[str, int],
        session: CultNetRudpSession,
        payload: bytes,
    ) -> None:
        for packet in session.send_many(
            "schema",
            payload,
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=_now_ms()),
        ):
            self._send_rudp_packet(rudp_socket, remote_addr, packet)

    @staticmethod
    def _send_rudp_packet(
        rudp_socket: socket.socket,
        remote_addr: tuple[str, int],
        packet: CultNetRudpPacket,
    ) -> None:
        rudp_socket.sendto(encode_rudp_packet(packet), remote_addr)

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
            snapshot_request = {
                "messageId": str(message.get("messageId") or ""),
                "schemaIds": list(subscriptions[subscription_id].schema_ids),
                "recordKeys": list(subscriptions[subscription_id].record_keys),
            }
            return [self._snapshot_response(snapshot_request)]
        if schema_version == "cultnet.database_unsubscribe.v0":
            subscriptions.pop(str(message.get("subscriptionId") or ""), None)
            return []
        if schema_version == "cultnet.document_put_raw.v0":
            return self._handle_raw_put(message, subscriptions)
        if schema_version == "cultnet.document_delete.v0":
            return self._handle_raw_delete(message, subscriptions)
        if schema_version == "cultnet.simulation_observation.v0":
            return self._candidate_responses(message)
        response = self.handle_message(message)
        return [] if response is None else [response]

    def _handle_raw_put(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        document_record = message.get("document")
        if not isinstance(document_record, dict):
            return [self._error_response(
                "Raw put messages must contain a document map.",
                message_id=str(message.get("messageId") or ""),
                code="malformed_document_put",
            )]
        change = self.node.database.apply_raw_put_message(message)
        if change is None:
            return [self._error_response(
                "Raw put message did not apply to a registered document.",
                message_id=str(message.get("messageId") or ""),
                code="unregistered_document_put",
            )]
        return self._database_change_notifications(message, document_record, change.change_kind, subscriptions)

    def _handle_raw_delete(
        self,
        message: dict[str, Any],
        subscriptions: dict[str, _DatabaseSubscription],
    ) -> list[dict[str, Any]]:
        schema_id = str(message.get("schemaId") or "")
        record_key = str(message.get("recordKey") or "")
        if not schema_id or not record_key:
            return [self._error_response(
                "Raw delete messages must contain schemaId and recordKey.",
                message_id=str(message.get("messageId") or ""),
                code="malformed_document_delete",
            )]
        change = self.node.database.apply_raw_delete_message(message)
        if change is None:
            return [self._error_response(
                "Raw delete message did not apply to a registered document.",
                message_id=str(message.get("messageId") or ""),
                code="unregistered_document_delete",
            )]
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
            "supportedMutationContracts": [
                {
                    "documentType": document.type,
                    "payloadSchemaVersion": document.catalog_entry().schema_version,
                    "operations": ["snapshot", "documentPut", "documentDelete", "shardLog"],
                    "authority": "runtime",
                }
                for document in self.node.documents
            ],
            "supportedMessageVersions": [
                "cultnet.hello.v0",
                "cultnet.error.v0",
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
            ] + (
                [
                    "cultnet.simulation_observation.v0",
                    "cultnet.simulation_consensus_candidate.v0",
                ]
                if self.observation_hub is not None
                else []
            ),
            "transportProfiles": [
                create_tcp_framed_transport_profile(
                    self.node.runtime_id,
                    transport_id="cultmesh-local-tcp",
                    host=self.host,
                    port=self.port,
                ),
                *(
                    [
                        create_rudp_transport_profile(
                            self.node.runtime_id,
                            transport_id="cultmesh-local-rudp",
                            host=self.host,
                            port=self.port,
                        )
                    ]
                    if self.enable_rudp
                    else []
                ),
            ],
            "supportsSchemaCatalog": True,
        }

    def _snapshot_response(self, request: dict[str, Any]) -> dict[str, Any]:
        response = self.node.database.create_snapshot_response(
            message_id=str(request.get("messageId") or ""),
            schema_ids=[str(value) for value in request.get("schemaIds") or []],
            record_keys=[str(value) for value in request.get("recordKeys") or []],
            shard_id=request.get("shardId"),
            shard_epoch=request.get("shardEpoch"),
        )
        return self._enforce_snapshot_limits(response)

    def _enforce_snapshot_limits(self, response: dict[str, Any]) -> dict[str, Any]:
        document_count = len(response.get("documents") or [])
        if self.max_snapshot_documents is not None and document_count > self.max_snapshot_documents:
            return self._error_response(
                f"Snapshot document limit exceeded: {document_count} > {self.max_snapshot_documents}.",
                message_id=str(response.get("messageId") or ""),
                code="snapshot_document_limit_exceeded",
                details={"documentCount": document_count, "maxSnapshotDocuments": self.max_snapshot_documents},
            )
        if self.max_snapshot_bytes is not None:
            response_bytes = len(msgpack.packb(response, use_bin_type=True))
            if response_bytes > self.max_snapshot_bytes:
                return self._error_response(
                    f"Snapshot byte limit exceeded: {response_bytes} > {self.max_snapshot_bytes}.",
                    message_id=str(response.get("messageId") or ""),
                    code="snapshot_byte_limit_exceeded",
                    details={"responseBytes": response_bytes, "maxSnapshotBytes": self.max_snapshot_bytes},
                )
        return response

    @staticmethod
    def _error_response(
        error: str,
        *,
        message_id: str = "",
        code: str | None = None,
        details: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        response: dict[str, Any] = {
            "schemaVersion": "cultnet.error.v0",
            "messageId": message_id,
            "error": error,
        }
        if code is not None:
            response["code"] = code
        if details is not None:
            response["details"] = details
        return response

    def _candidate_responses(self, message: dict[str, Any]) -> list[dict[str, Any]]:
        if self.observation_hub is None:
            return [self._error_response(
                "Simulation observations are not enabled for this CultMesh server.",
                message_id=str(message.get("messageId") or ""),
                code="simulation_observations_disabled",
                details={"schemaVersion": "cultnet.simulation_observation.v0"},
            )]
        message_id = str(message.get("messageId") or "")
        return [
            {
                **candidate.to_wire(),
                "messageId": message_id,
            }
            for candidate in self.observation_hub.submit_candidate_objects(message)
        ]

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
                "wireContracts": [
                    "cultnet.document_put_raw.v0",
                    "cultnet.document_delete.v0",
                    "cultnet.snapshot_response_raw.v0",
                    "cultnet.shard_log_response.v0",
                ],
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
        document_schema_ids = [document.catalog_entry().schema_id for document in self.node.documents]
        documents_by_schema_id = _schema_document_map(self.node.documents)
        shard_ids = self.node.database.shard_ids() or ["primary"]
        shards = []
        for shard_id in shard_ids:
            schema_ids = self.node.database.shard_schema_ids(shard_id) or document_schema_ids
            if requested_schema_ids:
                schema_ids = [
                    schema_id
                    for schema_id in schema_ids
                    if _schema_matches_request(
                        schema_id,
                        documents_by_schema_id.get(schema_id),
                        requested_schema_ids,
                    )
                ]
            if not schema_ids:
                continue
            shards.append({
                "shardId": shard_id,
                "ownerRuntimeId": self.node.runtime_id,
                "epoch": self.node.database.shard_epoch(shard_id),
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


def _now_ms() -> int:
    return int(time.time() * 1000)


def _schema_document_map(documents: list[Any]) -> dict[str, Any]:
    mapped: dict[str, Any] = {}
    for document in documents:
        entry = document.catalog_entry()
        mapped[entry.schema_id] = document
        mapped[entry.schema_name] = document
        for compatible_schema_id in entry.compatible_schema_ids:
            mapped[compatible_schema_id] = document
    return mapped


def _schema_matches_request(
    schema_id: str,
    document: Any | None,
    requested_schema_ids: set[str],
) -> bool:
    if not requested_schema_ids or schema_id in requested_schema_ids:
        return True
    if document is None:
        return False
    entry = document.catalog_entry()
    if entry.schema_id in requested_schema_ids or entry.schema_name in requested_schema_ids:
        return True
    if any(compatible_schema_id in requested_schema_ids for compatible_schema_id in entry.compatible_schema_ids):
        return True
    return any(_infer_schema_name(requested_schema_id) == entry.schema_name for requested_schema_id in requested_schema_ids)


def _infer_schema_name(schema_id: str) -> str | None:
    marker = schema_id.rfind(".v")
    if marker <= 0 or marker + 2 >= len(schema_id):
        return None
    version = schema_id[marker + 2:]
    return schema_id[:marker] if version.isdigit() else None
