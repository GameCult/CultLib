from __future__ import annotations

import argparse
import json
import selectors
import signal
import socket
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

import msgpack

from cultcache_py import (
    CultCache,
    CultCacheEnvelope,
    CultCacheSchemaCatalogEntry,
    SingleFileMessagePackBackingStore,
    define_document_type,
)
from cultcache_py.documents import DocumentDefinition
from cultmesh_py import (
    PEER_EXCHANGE_REQUEST,
    VERSE_CATALOG_REQUEST,
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshVerseCatalog,
    CultMeshVerseCompatibility,
    CultMeshVerseDescriptor,
)

from .framing import read_frame, write_frame

INTEROP_DOCUMENT_TYPE = "cultnet.interop-note"
INTEROP_SCHEMA_VERSION = "cultnet.interop_note.v0"
INTEROP_MUTATION_INTENT_DOCUMENT_TYPE = "cultnet.interop-note-mutation-intent"
INTEROP_MUTATION_INTENT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-intent.schema.json"
INTEROP_MUTATION_INTENT_SCHEMA_VERSION = "cultnet.interop_note_mutation_intent.v0"
INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE = "cultnet.interop-note-mutation-receipt"
INTEROP_MUTATION_RECEIPT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-receipt.schema.json"
INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION = "cultnet.interop_note_mutation_receipt.v0"
INTEROP_FIRE_COMMAND_DOCUMENT_TYPE = "cultnet.interop-fire-weapon-command"
INTEROP_FIRE_COMMAND_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-command.schema.json"
INTEROP_FIRE_COMMAND_SCHEMA_VERSION = "cultnet.interop_fire_weapon_command.v0"
INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE = "cultnet.interop-fire-weapon-receipt"
INTEROP_FIRE_RECEIPT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-receipt.schema.json"
INTEROP_FIRE_RECEIPT_SCHEMA_VERSION = "cultnet.interop_fire_weapon_receipt.v0"
INTEROP_WIRE_CONTRACT = "cultnet.schema.v0"
DISCOVERY_PROBE_SCHEMA_VERSION = "cultnet.discovery_probe.v0"
DISCOVERY_ANNOUNCE_SCHEMA_VERSION = "cultnet.discovery_announce.v0"


@dataclass(frozen=True)
class Binding:
    document: DocumentDefinition[dict[str, Any]]
    schema_id: str
    payload_schema_version: str


@dataclass
class PeerState:
    runtime_id: str
    runtime_kind: str
    display_name: str
    agent_id: str
    cache: CultCache
    bindings: dict[str, Binding]
    bindings_by_document_type: dict[str, Binding]
    bindings_by_schema_id: dict[str, Binding]
    note_schema_id: str
    interop_schema: dict[str, Any]
    interop_schema_json: str
    verse_catalog: CultMeshVerseCatalog
    peer_catalog: CultMeshPeerCatalog


@dataclass
class DatabaseSubscription:
    subscription_id: str
    schema_ids: set[str]
    record_keys: set[str]

    def matches(self, document: dict[str, Any]) -> bool:
        schema_id = document.get("schemaId")
        record_key = document.get("recordKey")
        if self.schema_ids and schema_id not in self.schema_ids:
            return False
        if self.record_keys and record_key not in self.record_keys:
            return False
        return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultnet-py-interop")
    sub = parser.add_subparsers(dest="mode", required=True)
    serve_parser = sub.add_parser("serve")
    add_common_runtime_args(serve_parser)
    serve_parser.add_argument("--bind-host", default="127.0.0.1")
    serve_parser.add_argument("--advertise-host", required=True)
    serve_parser.add_argument("--tcp-port", type=int, required=True)
    serve_parser.add_argument("--discovery-port", type=int, required=True)
    serve_parser.add_argument("--discovery-group", required=True)
    serve_parser.add_argument("--schema-path", required=True)

    probe_parser = sub.add_parser("probe")
    probe_parser.add_argument("--runtime-id", required=True)
    probe_parser.add_argument("--discovery-port", type=int, required=True)
    probe_parser.add_argument("--discovery-group", required=True)
    probe_parser.add_argument("--timeout-ms", type=int, default=1500)

    dial_parser = sub.add_parser("dial")
    add_common_runtime_args(dial_parser)
    dial_parser.add_argument("--target-host", required=True)
    dial_parser.add_argument("--target-port", type=int, required=True)
    dial_parser.add_argument("--schema-path", required=True)
    dial_parser.add_argument("--timeout-ms", type=int, default=4000)

    args = parser.parse_args(argv)
    if args.mode == "serve":
        serve(args)
    elif args.mode == "probe":
        probe(args)
    elif args.mode == "dial":
        dial(args)
    else:
        raise ValueError(f"unknown mode {args.mode}")
    return 0


def add_common_runtime_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--runtime-id", required=True)
    parser.add_argument("--runtime-kind", required=True)
    parser.add_argument("--display-name", required=True)
    parser.add_argument("--agent-id", required=True)


def serve(args: argparse.Namespace) -> None:
    state = build_state(args.runtime_id, args.runtime_kind, args.display_name, args.agent_id, args.schema_path)
    state.cache.put(state.bindings["note"].document, f"note:{args.runtime_id}", build_interop_note(args.runtime_id, args.display_name))

    stop = threading.Event()
    tcp_server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    tcp_server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    tcp_server.bind((args.bind_host, args.tcp_port))
    tcp_server.listen()
    tcp_server.settimeout(0.2)

    udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    udp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    udp_socket.bind(("", args.discovery_port))
    group = socket.inet_aton(args.discovery_group)
    udp_socket.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, group + socket.inet_aton("0.0.0.0"))
    udp_socket.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
    udp_socket.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)
    udp_socket.settimeout(0.2)

    threads = [
        threading.Thread(target=accept_loop, args=(tcp_server, state, stop), daemon=True),
        threading.Thread(target=discovery_loop, args=(udp_socket, args.advertise_host, args.tcp_port, state, stop), daemon=True),
    ]
    for thread in threads:
        thread.start()

    def request_stop(*_: object) -> None:
        stop.set()

    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    write_json({"status": "ready", "mode": "serve", "runtimeId": args.runtime_id, "runtimeKind": args.runtime_kind, "tcpPort": args.tcp_port, "discoveryPort": args.discovery_port, "discoveryGroup": args.discovery_group})
    while not stop.is_set():
        time.sleep(0.1)
    tcp_server.close()
    udp_socket.close()


def accept_loop(server: socket.socket, state: PeerState, stop: threading.Event) -> None:
    while not stop.is_set():
        try:
            client, _ = server.accept()
        except TimeoutError:
            continue
        except OSError:
            break
        threading.Thread(target=handle_connection, args=(client, state), daemon=True).start()


def handle_connection(client: socket.socket, state: PeerState) -> None:
    with client:
        stream = client.makefile("rwb", buffering=0)
        subscriptions: dict[str, DatabaseSubscription] = {}
        while True:
            try:
                payload = read_frame(stream)
            except EOFError:
                return
            message = msgpack.unpackb(payload, raw=False)
            if not isinstance(message, dict):
                continue
            response_messages = handle_server_message(state, message, subscriptions)
            for response in response_messages:
                write_message(stream, response)


def handle_server_message(state: PeerState, message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    schema_version = message.get("schemaVersion")
    if schema_version == "cultnet.hello.v0":
        return [hello_message(state)]
    if schema_version == "cultnet.schema_catalog_request.v0":
        return [catalog_response(state, message)]
    if schema_version == "cultnet.snapshot_request.v0":
        return [raw_snapshot_response(state, message)]
    if schema_version == "cultnet.database_subscribe.v0":
        return handle_database_subscribe(state, message, subscriptions)
    if schema_version == "cultnet.database_unsubscribe.v0":
        subscriptions.pop(str(message.get("subscriptionId") or ""), None)
        return []
    if schema_version == "cultnet.document_put_raw.v0":
        return handle_raw_put(state, message, subscriptions)
    if schema_version == VERSE_CATALOG_REQUEST:
        return [state.verse_catalog.create_response(message)]
    if schema_version == PEER_EXCHANGE_REQUEST:
        return [state.peer_catalog.create_response(message)]
    return []


def handle_database_subscribe(state: PeerState, message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    subscription_id = str(message.get("subscriptionId") or message.get("messageId") or "")
    if not subscription_id:
        return []
    subscriptions[subscription_id] = DatabaseSubscription(
        subscription_id=subscription_id,
        schema_ids={str(value) for value in message.get("schemaIds") or []},
        record_keys={str(value) for value in message.get("recordKeys") or []},
    )
    if message.get("includeSnapshot", True) is False:
        return []
    return [raw_snapshot_response(state, message)]


def handle_raw_put(state: PeerState, message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    document = message.get("document")
    if not isinstance(document, dict):
        return []
    applied = apply_raw_document_put(state, document)
    responses = database_change_notifications(message, document, subscriptions)
    schema_id = document.get("schemaId")
    if schema_id == INTEROP_MUTATION_INTENT_SCHEMA_ID:
        note_binding = state.bindings_by_schema_id[state.note_schema_id]
        intent = applied
        note = state.cache.get_required(note_binding.document, intent["targetDocumentId"])
        mutated = {
            **note,
            "body": f'{note["body"]}{intent["appendBody"]}',
            "tags": [*note["tags"], intent["appendTag"]],
        }
        state.cache.put(note_binding.document, mutated["documentId"], mutated)
        receipt = {
            "schemaVersion": INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION,
            "intentId": intent["intentId"],
            "accepted": True,
            "documentId": mutated["documentId"],
            "body": mutated["body"],
            "tags": mutated["tags"],
        }
        return [
            *responses,
            raw_document_put(state.bindings_by_schema_id[INTEROP_MUTATION_RECEIPT_SCHEMA_ID], f"{state.runtime_id}-mutation-receipt", receipt["intentId"], receipt, state),
            raw_document_put(note_binding, f"{state.runtime_id}-mutated-note", mutated["documentId"], mutated, state),
        ]
    if schema_id == INTEROP_FIRE_COMMAND_SCHEMA_ID:
        command = applied
        receipt = {
            "schemaVersion": INTEROP_FIRE_RECEIPT_SCHEMA_VERSION,
            "commandId": command["commandId"],
            "accepted": True,
            "characterId": command["characterId"],
            "weaponId": command["weaponId"],
            "shotsFired": 1,
            "ammoRemaining": 29,
        }
        return [*responses, raw_document_put(state.bindings_by_schema_id[INTEROP_FIRE_RECEIPT_SCHEMA_ID], f"{state.runtime_id}-fire-receipt", receipt["commandId"], receipt, state)]
    return responses


def database_change_notifications(message: dict[str, Any], document: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    notifications = []
    for subscription in subscriptions.values():
        if not subscription.matches(document):
            continue
        message_id = message.get("messageId") or document.get("recordKey") or "change"
        notifications.append({
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": f"{message_id}:{subscription.subscription_id}",
            "subscriptionId": subscription.subscription_id,
            "changeKind": "put",
            "document": document,
        })
    return notifications


def discovery_loop(sock: socket.socket, advertise_host: str, tcp_port: int, state: PeerState, stop: threading.Event) -> None:
    while not stop.is_set():
        try:
            packet, remote = sock.recvfrom(65536)
        except TimeoutError:
            continue
        except OSError:
            break
        try:
            message = msgpack.unpackb(packet, raw=False)
        except Exception:
            continue
        if not isinstance(message, dict) or message.get("schemaVersion") != DISCOVERY_PROBE_SCHEMA_VERSION:
            continue
        announce = {
            "schemaVersion": DISCOVERY_ANNOUNCE_SCHEMA_VERSION,
            "messageId": message.get("messageId"),
            "runtimeId": state.runtime_id,
            "runtimeKind": state.runtime_kind,
            "displayName": state.display_name,
            "agentId": state.agent_id,
            "tcpHost": advertise_host,
            "tcpPort": tcp_port,
            "wireContract": INTEROP_WIRE_CONTRACT,
            "supportedDocumentTypes": [INTEROP_DOCUMENT_TYPE],
            "supportsSchemaCatalog": True,
        }
        sock.sendto(msgpack.packb(announce, use_bin_type=True), remote)


def probe(args: argparse.Namespace) -> None:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)
    sock.bind(("", 0))
    message_id = f"{args.runtime_id}-{int(time.time() * 1000)}"
    probe_message = {
        "schemaVersion": DISCOVERY_PROBE_SCHEMA_VERSION,
        "messageId": message_id,
        "requesterRuntimeId": args.runtime_id,
    }
    sock.sendto(msgpack.packb(probe_message, use_bin_type=True), (args.discovery_group, args.discovery_port))
    deadline = time.monotonic() + (args.timeout_ms / 1000)
    peers: dict[str, dict[str, Any]] = {}
    sock.setblocking(False)
    selector = selectors.DefaultSelector()
    selector.register(sock, selectors.EVENT_READ)
    while time.monotonic() < deadline:
        for key, _ in selector.select(timeout=max(0, deadline - time.monotonic())):
            packet, _ = key.fileobj.recvfrom(65536)
            try:
                announce = msgpack.unpackb(packet, raw=False)
            except Exception:
                continue
            if isinstance(announce, dict) and announce.get("schemaVersion") == DISCOVERY_ANNOUNCE_SCHEMA_VERSION and announce.get("messageId") == message_id:
                peers[str(announce["runtimeId"])] = announce
    sock.close()
    write_json({"mode": "probe", "runtimeId": args.runtime_id, "peers": sorted(peers.values(), key=lambda item: item["runtimeId"])})


def dial(args: argparse.Namespace) -> None:
    state = build_state(args.runtime_id, args.runtime_kind, args.display_name, args.agent_id, args.schema_path, store_suffix="-dial")
    with socket.create_connection((args.target_host, args.target_port), timeout=args.timeout_ms / 1000) as client:
        stream = client.makefile("rwb", buffering=0)
        write_message(stream, hello_message(state))
        remote_hello = read_until(stream, lambda message: message.get("schemaVersion") == "cultnet.hello.v0", args.timeout_ms)

        write_message(stream, {"schemaVersion": "cultnet.schema_catalog_request.v0", "messageId": f"{args.runtime_id}-catalog", "includeSchemaJson": True})
        catalog = read_until(stream, lambda message: message.get("schemaVersion") == "cultnet.schema_catalog_response.v0", args.timeout_ms)

        write_message(stream, {"schemaVersion": "cultnet.snapshot_request.v0", "messageId": f"{args.runtime_id}-snapshot", "schemaIds": [state.note_schema_id]})
        snapshot = read_until(stream, lambda message: message.get("schemaVersion") == "cultnet.snapshot_response_raw.v0", args.timeout_ms)
        apply_raw_snapshot(state, snapshot)
        note = state.cache.get_required(state.bindings_by_schema_id[state.note_schema_id].document, f'note:{remote_hello["runtimeId"]}')
        has_schema = any(schema.get("schemaId") == state.note_schema_id and schema.get("documentType") == INTEROP_DOCUMENT_TYPE for schema in catalog.get("schemas", []))

        intent = {
            "schemaVersion": INTEROP_MUTATION_INTENT_SCHEMA_VERSION,
            "intentId": f"{args.runtime_id}-decorate",
            "targetDocumentId": note["documentId"],
            "appendBody": f" Decorated by {args.runtime_id}.",
            "appendTag": f"decorated:{args.runtime_id}",
        }
        write_message(stream, raw_document_put(state.bindings_by_schema_id[INTEROP_MUTATION_INTENT_SCHEMA_ID], f"{args.runtime_id}-decorate-put", intent["intentId"], intent, state))
        mutation_receipt: dict[str, Any] | None = None
        mutated_note: dict[str, Any] | None = None
        while mutation_receipt is None or mutated_note is None:
            message = read_until(stream, lambda candidate: candidate.get("schemaVersion") == "cultnet.document_put_raw.v0", args.timeout_ms)
            applied = apply_raw_document_put(state, message["document"])
            schema_id = message["document"]["schemaId"]
            if schema_id == INTEROP_MUTATION_RECEIPT_SCHEMA_ID:
                mutation_receipt = applied
            elif schema_id == state.note_schema_id:
                mutated_note = applied

        command = {
            "schemaVersion": INTEROP_FIRE_COMMAND_SCHEMA_VERSION,
            "commandId": f"{args.runtime_id}-fire",
            "characterId": remote_hello["runtimeId"],
            "weaponId": "interop-rifle",
        }
        write_message(stream, raw_document_put(state.bindings_by_schema_id[INTEROP_FIRE_COMMAND_SCHEMA_ID], f"{args.runtime_id}-fire-put", command["commandId"], command, state))
        fire_receipt_message = read_until(stream, lambda candidate: candidate.get("schemaVersion") == "cultnet.document_put_raw.v0" and candidate.get("document", {}).get("schemaId") == INTEROP_FIRE_RECEIPT_SCHEMA_ID, args.timeout_ms)
        fire_receipt = apply_raw_document_put(state, fire_receipt_message["document"])

    write_json({
        "mode": "dial",
        "runtimeId": args.runtime_id,
        "targetHost": args.target_host,
        "targetPort": args.target_port,
        "remoteHello": remote_hello,
        "hasInteropSchema": has_schema,
        "retrievedNote": note,
        "mutatedNote": mutated_note,
        "mutationReceipt": mutation_receipt,
        "fireReceipt": fire_receipt,
    })


def build_state(runtime_id: str, runtime_kind: str, display_name: str, agent_id: str, schema_path: str, store_suffix: str = "") -> PeerState:
    schema_json = Path(schema_path).read_text(encoding="utf-8")
    schema = json.loads(schema_json)
    note_schema_id = schema["$id"]
    bindings = define_interop_documents(note_schema_id, schema_json)
    cache = CultCache()
    for binding in bindings.values():
        cache.register_document_type(binding.document)
    cache.add_generic_store(SingleFileMessagePackBackingStore(runtime_store_path(runtime_id + store_suffix)))
    return PeerState(
        runtime_id=runtime_id,
        runtime_kind=runtime_kind,
        display_name=display_name,
        agent_id=agent_id,
        cache=cache,
        bindings=bindings,
        bindings_by_document_type={binding.document.type: binding for binding in bindings.values()},
        bindings_by_schema_id={binding.schema_id: binding for binding in bindings.values()},
        note_schema_id=note_schema_id,
        interop_schema=schema,
        interop_schema_json=schema_json,
        verse_catalog=default_verse_catalog(runtime_id),
        peer_catalog=default_peer_catalog(runtime_id),
    )


def default_verse_catalog(runtime_id: str) -> CultMeshVerseCatalog:
    catalog = CultMeshVerseCatalog()
    catalog.upsert(
        CultMeshVerseDescriptor(
            verse_id="python-interop",
            display_name="Python Interop Verse",
            authority_model="OperatorCluster",
            compatibility=CultMeshVerseCompatibility(
                transport_version="cultmesh.v0",
                rules_hash="python-interop-rules",
                required_plugin_ids=("core",),
            ),
            discovery_endpoints=(f"cultnet://{runtime_id}",),
            authority_runtime_ids=(runtime_id,),
            description="Python CultMesh interop surface",
        )
    )
    return catalog


def default_peer_catalog(runtime_id: str) -> CultMeshPeerCatalog:
    catalog = CultMeshPeerCatalog()
    catalog.upsert(
        CultMeshPeerCard(
            peer_id=runtime_id,
            verse_id="python-interop",
            endpoints=(f"cultnet://{runtime_id}",),
            roles=("discovery", "read-replica", "shard-primary"),
            shard_ids=("interop",),
            region="local",
            authority_lease_id=f"lease:{runtime_id}",
        )
    )
    return catalog


def define_interop_documents(note_schema_id: str, note_schema_json: str) -> dict[str, Binding]:
    return {
        "note": binding(INTEROP_DOCUMENT_TYPE, note_schema_id, INTEROP_SCHEMA_VERSION, note_schema_json, note_slots, note_from_slots),
        "mutationIntent": binding(INTEROP_MUTATION_INTENT_DOCUMENT_TYPE, INTEROP_MUTATION_INTENT_SCHEMA_ID, INTEROP_MUTATION_INTENT_SCHEMA_VERSION, "{}", mutation_intent_slots, mutation_intent_from_slots),
        "mutationReceipt": binding(INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE, INTEROP_MUTATION_RECEIPT_SCHEMA_ID, INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION, "{}", mutation_receipt_slots, mutation_receipt_from_slots),
        "fireCommand": binding(INTEROP_FIRE_COMMAND_DOCUMENT_TYPE, INTEROP_FIRE_COMMAND_SCHEMA_ID, INTEROP_FIRE_COMMAND_SCHEMA_VERSION, "{}", fire_command_slots, fire_command_from_slots),
        "fireReceipt": binding(INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE, INTEROP_FIRE_RECEIPT_SCHEMA_ID, INTEROP_FIRE_RECEIPT_SCHEMA_VERSION, "{}", fire_receipt_slots, fire_receipt_from_slots),
    }


def binding(
    document_type: str,
    schema_id: str,
    schema_version: str,
    canonical_schema_json: str,
    encode_slots: Callable[[dict[str, Any]], list[Any]],
    decode_slots: Callable[[list[Any]], dict[str, Any]],
) -> Binding:
    document = define_document_type(
        document_type,
        encode=encode_slots,
        decode=decode_slots,
        payload_encoder=lambda value: msgpack.packb(value, use_bin_type=True),
        payload_decoder=lambda payload: msgpack.unpackb(payload, raw=False),
        schema_id=schema_id,
        schema_name=document_type,
        schema_version=schema_version,
        content_hash=schema_id,
        canonical_schema_json=canonical_schema_json,
        compatible_schema_ids=(schema_id,),
    )
    return Binding(document=document, schema_id=schema_id, payload_schema_version=schema_version)


def hello_message(state: PeerState) -> dict[str, Any]:
    return {
        "schemaVersion": "cultnet.hello.v0",
        "runtimeId": state.runtime_id,
        "runtimeKind": state.runtime_kind,
        "agentId": state.agent_id,
        "displayName": state.display_name,
        "supportedDocumentTypes": [INTEROP_DOCUMENT_TYPE],
        "supportedMutationContracts": [{
            "documentType": INTEROP_DOCUMENT_TYPE,
            "payloadSchemaVersion": INTEROP_SCHEMA_VERSION,
            "operations": ["snapshot", "documentPut", "intentSubmit", "receiptWatch"],
            "authority": "runtime",
            "intentDocumentTypes": [INTEROP_MUTATION_INTENT_DOCUMENT_TYPE, INTEROP_FIRE_COMMAND_DOCUMENT_TYPE],
            "receiptDocumentTypes": [INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE, INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE],
        }],
        "supportedMessageVersions": [INTEROP_SCHEMA_VERSION],
        "supportsSchemaCatalog": True,
    }


def catalog_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    schema_ids = set(request.get("schemaIds") or [])
    kinds = set(request.get("kinds") or [])
    include_schema_json = request.get("includeSchemaJson") is True
    schemas = []
    if (not schema_ids or state.note_schema_id in schema_ids) and (not kinds or "document_payload" in kinds):
        schemas.append({
            "schemaId": state.note_schema_id,
            "kind": "document_payload",
            "schemaVersion": INTEROP_SCHEMA_VERSION,
            "documentType": INTEROP_DOCUMENT_TYPE,
            "title": state.interop_schema.get("title"),
            "wireContracts": [INTEROP_WIRE_CONTRACT],
            "contentHash": state.note_schema_id,
            "schemaJson": state.interop_schema_json if include_schema_json else None,
        })
    return {"schemaVersion": "cultnet.schema_catalog_response.v0", "messageId": request.get("messageId", ""), "schemas": schemas}


def raw_snapshot_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    schema_ids = set(request.get("schemaIds") or [])
    record_keys = set(request.get("recordKeys") or [])
    documents = []
    for envelope in state.cache.snapshot_envelopes():
        binding = state.bindings_by_document_type.get(envelope.type)
        if binding is None:
            continue
        schema_id = envelope.schema_id or binding.schema_id
        if schema_ids and schema_id not in schema_ids:
            continue
        if record_keys and envelope.key not in record_keys:
            continue
        documents.append(raw_record_from_envelope(envelope, schema_id))
    return {"schemaVersion": "cultnet.snapshot_response_raw.v0", "messageId": request.get("messageId", ""), "documents": documents}


def raw_document_put(binding: Binding, message_id: str, key: str, value: dict[str, Any], state: PeerState) -> dict[str, Any]:
    payload = binding.document.encode_payload(value)
    return {
        "schemaVersion": "cultnet.document_put_raw.v0",
        "messageId": message_id,
        "document": {
            "schemaId": binding.schema_id,
            "recordKey": key,
            "storedAt": now_iso(),
            "payloadEncoding": "messagepack",
            "payload": payload,
            "sourceRuntimeId": state.runtime_id,
            "sourceAgentId": state.agent_id,
            "sourceRole": "peer",
            "tags": ["mutation", state.runtime_id],
        },
    }


def apply_raw_snapshot(state: PeerState, response: dict[str, Any]) -> None:
    for document in response.get("documents", []):
        if isinstance(document, dict):
            apply_raw_document_put(state, document)


def apply_raw_document_put(state: PeerState, document: dict[str, Any]) -> dict[str, Any]:
    schema_id = document["schemaId"]
    binding = state.bindings_by_schema_id[schema_id]
    envelope = CultCacheEnvelope(
        key=document["recordKey"],
        type=binding.document.type,
        schema_id=schema_id,
        payload=bytes(document["payload"]),
        stored_at=document["storedAt"],
        catalog_entry=CatalogEntry(binding),
    )
    return state.cache.put_envelope(binding.document, envelope)


def CatalogEntry(binding: Binding) -> CultCacheSchemaCatalogEntry:
    return CultCacheSchemaCatalogEntry(
        schema_id=binding.schema_id,
        schema_name=binding.document.type,
        schema_version=binding.payload_schema_version,
        content_hash=binding.schema_id,
        canonical_schema_json=binding.document.canonical_schema_json or "{}",
        compatible_schema_ids=(binding.schema_id,),
    )


def raw_record_from_envelope(envelope: CultCacheEnvelope, schema_id: str) -> dict[str, Any]:
    return {
        "schemaId": schema_id,
        "recordKey": envelope.key,
        "storedAt": envelope.stored_at,
        "payloadEncoding": "messagepack",
        "payload": envelope.payload,
    }


def write_message(stream: Any, message: dict[str, Any]) -> None:
    write_frame(stream, msgpack.packb(message, use_bin_type=True))


def read_until(stream: Any, predicate: Callable[[dict[str, Any]], bool], timeout_ms: int) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_ms / 1000
    while time.monotonic() < deadline:
        payload = read_frame(stream)
        message = msgpack.unpackb(payload, raw=False)
        if isinstance(message, dict) and predicate(message):
            return message
    raise TimeoutError(f"Timed out waiting for CultNet message after {timeout_ms}ms")


def build_interop_note(runtime_id: str, display_name: str) -> dict[str, Any]:
    return {
        "schemaVersion": INTEROP_SCHEMA_VERSION,
        "documentId": f"note:{runtime_id}",
        "authorRuntimeId": runtime_id,
        "title": f"{display_name} keeps a little note",
        "body": f"{runtime_id} can move CultNet state without begging the gods for translation.",
        "tags": [runtime_id, "interop", "cultnet"],
    }


def note_slots(value: dict[str, Any]) -> list[Any]:
    return [value["schemaVersion"], value["documentId"], value["authorRuntimeId"], value["title"], value["body"], value["tags"]]


def note_from_slots(slots: list[Any]) -> dict[str, Any]:
    return {"schemaVersion": slots[0], "documentId": slots[1], "authorRuntimeId": slots[2], "title": slots[3], "body": slots[4], "tags": slots[5] if len(slots) > 5 and isinstance(slots[5], list) else []}


def mutation_intent_slots(value: dict[str, Any]) -> list[Any]:
    return [value["schemaVersion"], value["intentId"], value["targetDocumentId"], value["appendBody"], value["appendTag"]]


def mutation_intent_from_slots(slots: list[Any]) -> dict[str, Any]:
    return {"schemaVersion": slots[0], "intentId": slots[1], "targetDocumentId": slots[2], "appendBody": slots[3], "appendTag": slots[4]}


def mutation_receipt_slots(value: dict[str, Any]) -> list[Any]:
    return [value["schemaVersion"], value["intentId"], value["accepted"], value["documentId"], value["body"], value["tags"], value.get("error")]


def mutation_receipt_from_slots(slots: list[Any]) -> dict[str, Any]:
    result = {"schemaVersion": slots[0], "intentId": slots[1], "accepted": slots[2], "documentId": slots[3], "body": slots[4], "tags": slots[5] if len(slots) > 5 and isinstance(slots[5], list) else []}
    if len(slots) > 6 and isinstance(slots[6], str):
        result["error"] = slots[6]
    return result


def fire_command_slots(value: dict[str, Any]) -> list[Any]:
    return [value["schemaVersion"], value["commandId"], value["characterId"], value["weaponId"]]


def fire_command_from_slots(slots: list[Any]) -> dict[str, Any]:
    return {"schemaVersion": slots[0], "commandId": slots[1], "characterId": slots[2], "weaponId": slots[3]}


def fire_receipt_slots(value: dict[str, Any]) -> list[Any]:
    return [value["schemaVersion"], value["commandId"], value["accepted"], value["characterId"], value["weaponId"], value["shotsFired"], value["ammoRemaining"], value.get("error")]


def fire_receipt_from_slots(slots: list[Any]) -> dict[str, Any]:
    result = {"schemaVersion": slots[0], "commandId": slots[1], "accepted": slots[2], "characterId": slots[3], "weaponId": slots[4], "shotsFired": slots[5], "ammoRemaining": slots[6]}
    if len(slots) > 7 and isinstance(slots[7], str):
        result["error"] = slots[7]
    return result


def runtime_store_path(runtime_id: str) -> str:
    return str(Path(tempfile.gettempdir()) / f"cultnet-py-interop-{runtime_id}.msgpack")


def now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def write_json(value: dict[str, Any]) -> None:
    print(json.dumps(value, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(json.dumps({"event": "fatal", "error": str(error)}), file=sys.stderr, flush=True)
        raise
