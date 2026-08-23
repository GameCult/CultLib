from __future__ import annotations

import argparse
from collections import defaultdict
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
    SIMULATION_FACT_DOCUMENT_TYPE,
    VERSE_CATALOG_REQUEST,
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshSimulationFact,
    CultMeshVerseCatalog,
    CultMeshVerseCompatibility,
    CultMeshVerseDescriptor,
    simulation_fact_document,
)

from .schema_catalog import INTEROP_WIRE_CONTRACT, wire_message_schema_descriptors
from .client import create_rudp_schema_transport
from .transport import (
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
)

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
WITNESS_ARTIFACT_BUNDLE_DOCUMENT_TYPE = "cultnet.witness_artifact_bundle"
WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/contracts/cultnet.witness-artifact-bundle.schema.json"
WITNESS_ARTIFACT_BUNDLE_SCHEMA_VERSION = "cultnet.witness_artifact_bundle.v0"
SIMULATION_FACT_SCHEMA_ID = simulation_fact_document.catalog_entry().schema_id
DISCOVERY_PROBE_SCHEMA_VERSION = "cultnet.discovery_probe.v0"
DISCOVERY_ANNOUNCE_SCHEMA_VERSION = "cultnet.discovery_announce.v0"
RUDP_CONNECTION_ID = 0x43554C54
RUDP_INTEROP_MAX_FRAGMENT_BYTES = 1024


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
    shard_id: str
    shard_epoch: int
    shard_endpoint: str
    transport_profiles: list[dict[str, Any]]
    shard_log: list[dict[str, Any]]
    shard_log_lock: threading.Lock
    observations: dict[tuple[str, int, str, str], dict[str, dict[str, Any]]]
    observations_lock: threading.Lock


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
    serve_parser.add_argument("--rudp-port", type=int)
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
    dial_parser.add_argument("--target-port", type=int)
    dial_parser.add_argument("--target-rudp-port", type=int)
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
    rudp_port = int(args.rudp_port or args.tcp_port)
    state = build_state(
        args.runtime_id,
        args.runtime_kind,
        args.display_name,
        args.agent_id,
        args.schema_path,
        shard_endpoint=f"cultnet://{args.advertise_host}:{args.tcp_port}",
        transport_profiles=interop_transport_profiles(args.runtime_id, args.advertise_host, args.tcp_port, rudp_port),
    )
    state.cache.put(state.bindings["note"].document, f"note:{args.runtime_id}", build_interop_note(args.runtime_id, args.display_name))

    stop = threading.Event()
    tcp_server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    tcp_server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    tcp_server.bind((args.bind_host, args.tcp_port))
    tcp_server.listen()
    tcp_server.settimeout(0.2)

    rudp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rudp_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    rudp_socket.bind((args.bind_host, rudp_port))
    rudp_socket.settimeout(0.02)

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
        threading.Thread(target=rudp_loop, args=(rudp_socket, args.advertise_host, args.tcp_port, rudp_port, state, stop), daemon=True),
        threading.Thread(target=discovery_loop, args=(udp_socket, args.advertise_host, args.tcp_port, rudp_port, state, stop), daemon=True),
    ]
    for thread in threads:
        thread.start()

    def request_stop(*_: object) -> None:
        stop.set()

    signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)
    write_json({"status": "ready", "mode": "serve", "runtimeId": args.runtime_id, "runtimeKind": args.runtime_kind, "tcpPort": args.tcp_port, "rudpPort": rudp_port, "discoveryPort": args.discovery_port, "discoveryGroup": args.discovery_group})
    while not stop.is_set():
        time.sleep(0.1)
    tcp_server.close()
    rudp_socket.close()
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
        transport = TcpFramedTransportConnection(
            stream,
            profile=create_tcp_framed_transport_profile(f"{state.runtime_id}-interop-server"),
        )
        subscriptions: dict[str, DatabaseSubscription] = {}
        while True:
            try:
                frame = transport.receive()
            except EOFError:
                return
            message = msgpack.unpackb(frame.payload, raw=False)
            if not isinstance(message, dict):
                continue
            response_messages = handle_server_message(state, message, subscriptions)
            for response in response_messages:
                write_message(transport, response)


@dataclass
class RudpPeerConnection:
    session: CultNetRudpSession
    subscriptions: dict[str, DatabaseSubscription]


def rudp_loop(
    sock: socket.socket,
    advertise_host: str,
    tcp_port: int,
    rudp_port: int,
    state: PeerState,
    stop: threading.Event,
) -> None:
    peers: dict[tuple[str, int], RudpPeerConnection] = {}
    while not stop.is_set():
        try:
            wire, remote = sock.recvfrom(65535)
        except TimeoutError:
            poll_rudp_resends(sock, peers)
            continue
        except OSError:
            break
        try:
            packet = decode_rudp_packet(wire)
        except ValueError:
            continue
        if packet.connection_id != RUDP_CONNECTION_ID:
            continue
        peer = peers.get(remote)
        if packet.packet_type == CultNetRudpPacketType.CONNECT:
            peer = RudpPeerConnection(
                session=CultNetRudpSession(
                    CultNetRudpSessionOptions(
                        connection_id=RUDP_CONNECTION_ID,
                        resend_delay_ms=25,
                    )
                ),
                subscriptions={},
            )
            peers[remote] = peer
            send_rudp_packet(sock, remote, peer.session.accept_connect(packet, now_ms(), b"cultnet-interop-rudp"))
            continue
        if peer is None:
            continue
        try:
            result = peer.session.receive(packet, now_ms())
            if result.reply is not None:
                send_rudp_packet(sock, remote, result.reply)
            for ready in result.ready_to_send:
                send_rudp_packet(sock, remote, ready)
            if result.disconnected:
                peers.pop(remote, None)
                continue
            for frame in result.delivered:
                if frame.channel_id != "schema":
                    continue
                message = msgpack.unpackb(frame.payload, raw=False)
                if not isinstance(message, dict):
                    continue
                for response in handle_server_message(state, message, peer.subscriptions):
                    send_rudp_schema_frame(sock, remote, peer.session, response)
            if packet.reliable or packet.packet_type == CultNetRudpPacketType.DATA or result.delivered:
                send_rudp_packet(sock, remote, peer.session.create_ack_for(packet.sequence))
        except Exception as error:
            sys.stderr.write(json.dumps({
                "event": "rudpMessageError",
                "runtimeId": state.runtime_id,
                "error": str(error),
            }) + "\n")
            sys.stderr.flush()


def poll_rudp_resends(sock: socket.socket, peers: dict[tuple[str, int], RudpPeerConnection]) -> None:
    current = now_ms()
    for remote, peer in list(peers.items()):
        for packet in peer.session.due_resends(current):
            send_rudp_packet(sock, remote, packet)


def send_rudp_schema_frame(
    sock: socket.socket,
    remote: tuple[str, int],
    session: CultNetRudpSession,
    message: dict[str, Any],
) -> None:
    payload = msgpack.packb(message, use_bin_type=True)
    for packet in session.send_many(
        "schema",
        payload,
        CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=now_ms()),
        max_fragment_bytes=RUDP_INTEROP_MAX_FRAGMENT_BYTES,
    ):
        send_rudp_packet(sock, remote, packet)


def send_rudp_packet(sock: socket.socket, remote: tuple[str, int], packet: CultNetRudpPacket) -> None:
    sock.sendto(encode_rudp_packet(packet), remote)


def handle_server_message(state: PeerState, message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    schema_version = message.get("schemaVersion")
    if schema_version == "cultnet.hello.v0":
        return [hello_message(state)]
    if schema_version == "cultnet.schema_catalog_request.v0":
        return [catalog_response(state, message)]
    if schema_version == "cultnet.snapshot_request.v0":
        return [raw_snapshot_response(state, message)]
    if schema_version == "cultnet.shard_catalog_request.v0":
        return [shard_catalog_response(state, message)]
    if schema_version == "cultnet.shard_log_request.v0":
        return [shard_log_response(state, message)]
    if schema_version == "cultnet.simulation_observation.v0":
        return simulation_candidate_responses(state, message)
    if schema_version == "cultnet.database_subscribe.v0":
        return handle_database_subscribe(state, message, subscriptions)
    if schema_version == "cultnet.database_unsubscribe.v0":
        subscriptions.pop(str(message.get("subscriptionId") or ""), None)
        return []
    if schema_version == "cultnet.document_put_raw.v0":
        return handle_raw_put(state, message, subscriptions)
    if schema_version == "cultnet.document_delete.v0":
        return handle_document_delete(state, message, subscriptions)
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
    append_shard_log_put(state, message, document)
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


def handle_document_delete(state: PeerState, message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
    schema_id = str(message.get("schemaId") or "")
    record_key = str(message.get("recordKey") or "")
    binding = state.bindings_by_schema_id.get(schema_id)
    if binding is None or not record_key:
        return []
    if state.cache.get(binding.document, record_key) is None:
        return []
    state.cache.delete(binding.document, record_key)
    append_shard_log_delete(state, message)
    return database_delete_notifications(message, subscriptions)


def shard_catalog_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    schema_ids = {str(value) for value in request.get("schemaIds") or []}
    record_keys = {str(value) for value in request.get("recordKeys") or []}
    include_shard = not schema_ids or state.note_schema_id in schema_ids
    if record_keys and not any(key.startswith("note:") for key in record_keys):
        include_shard = False
    return {
        "schemaVersion": "cultnet.shard_catalog_response.v0",
        "messageId": request.get("messageId", ""),
        "shards": [shard_descriptor(state)] if include_shard else [],
    }


def shard_descriptor(state: PeerState) -> dict[str, Any]:
    return {
        "shardId": state.shard_id,
        "ownerRuntimeId": state.runtime_id,
        "epoch": state.shard_epoch,
        "isPrimary": True,
        "schemaIds": [state.note_schema_id],
        "keyPrefix": "note:",
        "primaryEndpoints": [state.shard_endpoint],
        "replicaEndpoints": [],
        "readReplicaEndpoints": [state.shard_endpoint],
        "region": "local",
    }


def shard_log_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    requested_shard = str(request.get("shardId") or "")
    message_id = request.get("messageId", "")
    if requested_shard != state.shard_id:
        return {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": message_id,
            "shardId": requested_shard,
            "shardEpoch": 0,
            "entries": [],
            "resyncRequired": True,
            "reason": "unknown_shard",
        }
    requested_epoch = request.get("shardEpoch")
    if isinstance(requested_epoch, int) and requested_epoch != state.shard_epoch:
        return {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": message_id,
            "shardId": state.shard_id,
            "shardEpoch": state.shard_epoch,
            "entries": [],
            "resyncRequired": True,
            "reason": "stale_epoch",
        }
    after_sequence = request.get("afterSequence")
    if not isinstance(after_sequence, int):
        after_sequence = 0
    limit = request.get("limit")
    with state.shard_log_lock:
        entries = [entry for entry in state.shard_log if entry["sequence"] > after_sequence]
        if isinstance(limit, int) and limit >= 0:
            entries = entries[:limit]
    return {
        "schemaVersion": "cultnet.shard_log_response.v0",
        "messageId": message_id,
        "shardId": state.shard_id,
        "shardEpoch": state.shard_epoch,
        "entries": entries,
        "resyncRequired": False,
    }


def append_shard_log_put(state: PeerState, message: dict[str, Any], document: dict[str, Any]) -> None:
    with state.shard_log_lock:
        sequence = len(state.shard_log) + 1
        put_message = {
            "schemaVersion": "cultnet.document_put_raw.v0",
            "messageId": message.get("messageId", ""),
            "document": document,
            "shardId": state.shard_id,
            "shardEpoch": state.shard_epoch,
        }
        state.shard_log.append({
            "sequence": sequence,
            "committedAt": now_iso(),
            "changeKind": "added",
            "put": put_message,
        })


def append_shard_log_delete(state: PeerState, message: dict[str, Any]) -> None:
    with state.shard_log_lock:
        sequence = len(state.shard_log) + 1
        delete_message = {
            "schemaVersion": "cultnet.document_delete.v0",
            "messageId": message.get("messageId", ""),
            "schemaId": message.get("schemaId", ""),
            "recordKey": message.get("recordKey", ""),
            "shardId": state.shard_id,
            "shardEpoch": state.shard_epoch,
        }
        state.shard_log.append({
            "sequence": sequence,
            "committedAt": now_iso(),
            "changeKind": "removed",
            "delete": delete_message,
        })


def simulation_candidate_responses(state: PeerState, message: dict[str, Any]) -> list[dict[str, Any]]:
    observation = message.get("observation")
    if not isinstance(observation, dict):
        return []
    group = (
        str(observation.get("shardId") or ""),
        int(observation.get("frame") or 0),
        str(observation.get("subjectId") or ""),
        str(observation.get("claimKind") or ""),
    )
    witness_id = str(observation.get("witnessRuntimeId") or "")
    if not all(group) or not witness_id:
        return []
    with state.observations_lock:
        witnesses = state.observations.setdefault(group, {})
        witnesses[witness_id] = observation
        candidates = build_simulation_candidates(message.get("messageId", ""), list(witnesses.values()))
    commit_simulation_facts(state, candidates)
    return candidates


def commit_simulation_facts(state: PeerState, candidates: list[dict[str, Any]]) -> None:
    binding = state.bindings_by_schema_id[SIMULATION_FACT_SCHEMA_ID]
    for candidate in candidates:
        if not candidate.get("hasQuorum"):
            continue
        key = CultMeshSimulationFact.create_record_key(candidate)
        if state.cache.get(binding.document, key) is not None:
            continue
        state.cache.put(binding.document, key, CultMeshSimulationFact.from_candidate(candidate))


def build_simulation_candidates(message_id: str, observations: list[dict[str, Any]]) -> list[dict[str, Any]]:
    by_claim: dict[str, list[dict[str, Any]]] = defaultdict(list)
    total_weight = sum(observation_weight(observation) for observation in observations)
    for observation in observations:
        by_claim[str(observation.get("claimHash") or "")].append(observation)

    candidates = []
    for claim_hash in sorted(by_claim):
        claim_observations = by_claim[claim_hash]
        sample = claim_observations[0]
        support_weight = sum(observation_weight(observation) for observation in claim_observations)
        witness_count = len({str(observation.get("witnessRuntimeId") or "") for observation in claim_observations})
        confidence = support_weight / total_weight if total_weight > 0 else 0.0
        candidates.append({
            "schemaVersion": "cultnet.simulation_consensus_candidate.v0",
            "messageId": message_id,
            "shardId": str(sample.get("shardId") or ""),
            "shardEpoch": int(sample.get("shardEpoch") or 0),
            "frame": int(sample.get("frame") or 0),
            "subjectId": str(sample.get("subjectId") or ""),
            "claimKind": str(sample.get("claimKind") or ""),
            "claimHash": claim_hash,
            "claimSummary": sample.get("claimSummary"),
            "witnessCount": witness_count,
            "supportWeight": support_weight,
            "totalWeight": total_weight,
            "hasQuorum": witness_count >= 1 and support_weight >= 1.0 and confidence >= 0.5,
            "confidence": confidence,
        })
    candidates.sort(key=lambda candidate: (-candidate["supportWeight"], candidate["claimHash"]))
    return candidates


def observation_weight(observation: dict[str, Any]) -> float:
    weight = observation.get("weight")
    if isinstance(weight, (int, float)):
        return float(weight)
    return 1.0


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
            "changeKind": "added",
            "document": document,
        })
    return notifications


def database_delete_notifications(message: dict[str, Any], subscriptions: dict[str, DatabaseSubscription]) -> list[dict[str, Any]]:
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


def discovery_loop(sock: socket.socket, advertise_host: str, tcp_port: int, rudp_port: int, state: PeerState, stop: threading.Event) -> None:
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
            "transportProfiles": interop_transport_profiles(state.runtime_id, advertise_host, tcp_port, rudp_port),
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
    if args.target_port is None and args.target_rudp_port is None:
        raise ValueError("dial mode requires --target-port or --target-rudp-port")
    state = build_state(args.runtime_id, args.runtime_kind, args.display_name, args.agent_id, args.schema_path, store_suffix="-dial")
    tcp_client: socket.socket | None = None
    if args.target_rudp_port is not None:
        transport = create_rudp_schema_transport(
            host=args.target_host,
            port=args.target_rudp_port,
            connection_id=RUDP_CONNECTION_ID,
            timeout_seconds=args.timeout_ms / 1000,
            runtime_id=f"{args.runtime_id}-interop-rudp-dial",
            transport_id="interop-rudp",
            max_fragment_bytes=RUDP_INTEROP_MAX_FRAGMENT_BYTES,
        )
    else:
        tcp_client = socket.create_connection((args.target_host, args.target_port), timeout=args.timeout_ms / 1000)
        stream = tcp_client.makefile("rwb", buffering=0)
        transport = TcpFramedTransportConnection(
            stream,
            profile=create_tcp_framed_transport_profile(
                f"{args.runtime_id}-interop-dial",
                host=args.target_host,
                port=args.target_port,
            ),
        )
    try:
        target_port = args.target_rudp_port if args.target_rudp_port is not None else args.target_port
        write_message(transport, hello_message(state))
        remote_hello = read_until(transport, lambda message: message.get("schemaVersion") == "cultnet.hello.v0", args.timeout_ms)

        write_message(transport, {"schemaVersion": "cultnet.schema_catalog_request.v0", "messageId": f"{args.runtime_id}-catalog", "includeSchemaJson": True})
        catalog = read_until(transport, lambda message: message.get("schemaVersion") == "cultnet.schema_catalog_response.v0", args.timeout_ms)

        write_message(transport, {"schemaVersion": "cultnet.snapshot_request.v0", "messageId": f"{args.runtime_id}-snapshot", "schemaIds": [state.note_schema_id]})
        snapshot = read_until(transport, lambda message: message.get("schemaVersion") == "cultnet.snapshot_response_raw.v0", args.timeout_ms)
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
        write_message(transport, raw_document_put(state.bindings_by_schema_id[INTEROP_MUTATION_INTENT_SCHEMA_ID], f"{args.runtime_id}-decorate-put", intent["intentId"], intent, state))
        mutation_receipt: dict[str, Any] | None = None
        mutated_note: dict[str, Any] | None = None
        while mutation_receipt is None or mutated_note is None:
            message = read_until(transport, lambda candidate: candidate.get("schemaVersion") == "cultnet.document_put_raw.v0", args.timeout_ms)
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
        write_message(transport, raw_document_put(state.bindings_by_schema_id[INTEROP_FIRE_COMMAND_SCHEMA_ID], f"{args.runtime_id}-fire-put", command["commandId"], command, state))
        fire_receipt_message = read_until(transport, lambda candidate: candidate.get("schemaVersion") == "cultnet.document_put_raw.v0" and candidate.get("document", {}).get("schemaId") == INTEROP_FIRE_RECEIPT_SCHEMA_ID, args.timeout_ms)
        fire_receipt = apply_raw_document_put(state, fire_receipt_message["document"])
    finally:
        transport.close()
        if tcp_client is not None:
            tcp_client.close()

    write_json({
        "mode": "dial",
        "runtimeId": args.runtime_id,
        "targetHost": args.target_host,
        "targetPort": target_port,
        "transport": "rudp" if args.target_rudp_port is not None else "tcp_framed",
        "remoteHello": remote_hello,
        "hasInteropSchema": has_schema,
        "retrievedNote": note,
        "mutatedNote": mutated_note,
        "mutationReceipt": mutation_receipt,
        "fireReceipt": fire_receipt,
    })


def build_state(
    runtime_id: str,
    runtime_kind: str,
    display_name: str,
    agent_id: str,
    schema_path: str,
    store_suffix: str = "",
    shard_endpoint: str | None = None,
    transport_profiles: list[dict[str, Any]] | None = None,
) -> PeerState:
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
        shard_id="interop",
        shard_epoch=1,
        shard_endpoint=shard_endpoint or f"cultnet://{runtime_id}",
        transport_profiles=transport_profiles or [],
        shard_log=[],
        shard_log_lock=threading.Lock(),
        observations={},
        observations_lock=threading.Lock(),
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
    simulation_entry = simulation_fact_document.catalog_entry()
    return {
        "note": binding(INTEROP_DOCUMENT_TYPE, note_schema_id, INTEROP_SCHEMA_VERSION, note_schema_json, note_slots, note_from_slots),
        "mutationIntent": binding(INTEROP_MUTATION_INTENT_DOCUMENT_TYPE, INTEROP_MUTATION_INTENT_SCHEMA_ID, INTEROP_MUTATION_INTENT_SCHEMA_VERSION, "{}", mutation_intent_slots, mutation_intent_from_slots),
        "mutationReceipt": binding(INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE, INTEROP_MUTATION_RECEIPT_SCHEMA_ID, INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION, "{}", mutation_receipt_slots, mutation_receipt_from_slots),
        "fireCommand": binding(INTEROP_FIRE_COMMAND_DOCUMENT_TYPE, INTEROP_FIRE_COMMAND_SCHEMA_ID, INTEROP_FIRE_COMMAND_SCHEMA_VERSION, "{}", fire_command_slots, fire_command_from_slots),
        "fireReceipt": binding(INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE, INTEROP_FIRE_RECEIPT_SCHEMA_ID, INTEROP_FIRE_RECEIPT_SCHEMA_VERSION, "{}", fire_receipt_slots, fire_receipt_from_slots),
        "witnessArtifactBundle": binding(
            WITNESS_ARTIFACT_BUNDLE_DOCUMENT_TYPE,
            WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID,
            WITNESS_ARTIFACT_BUNDLE_SCHEMA_VERSION,
            witness_artifact_bundle_schema_json(),
            witness_artifact_bundle_slots,
            witness_artifact_bundle_from_slots,
        ),
        "simulationFact": Binding(
            document=simulation_fact_document,
            schema_id=simulation_entry.schema_id,
            payload_schema_version=simulation_entry.schema_version,
        ),
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
    message = {
        "schemaVersion": "cultnet.hello.v0",
        "runtimeId": state.runtime_id,
        "runtimeKind": state.runtime_kind,
        "agentId": state.agent_id,
        "displayName": state.display_name,
        "supportedDocumentTypes": [INTEROP_DOCUMENT_TYPE, WITNESS_ARTIFACT_BUNDLE_DOCUMENT_TYPE, SIMULATION_FACT_DOCUMENT_TYPE],
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
    if state.transport_profiles:
        message["transportProfiles"] = state.transport_profiles
    return message


def interop_transport_profiles(runtime_id: str, host: str, tcp_port: int, rudp_port: int) -> list[dict[str, Any]]:
    return [
        create_tcp_framed_transport_profile(
            runtime_id,
            transport_id="interop-tcp",
            host=host,
            port=tcp_port,
        ),
        create_rudp_transport_profile(
            runtime_id,
            transport_id="interop-rudp",
            host=host,
            port=rudp_port,
        ),
    ]


def catalog_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    schema_ids = set(request.get("schemaIds") or [])
    kinds = set(request.get("kinds") or [])
    include_schema_json = request.get("includeSchemaJson") is True
    schemas = []
    for descriptor in wire_message_schema_descriptors(include_schema_json):
        if schema_ids and descriptor["schemaId"] not in schema_ids:
            continue
        if kinds and descriptor["kind"] not in kinds:
            continue
        schemas.append(descriptor)
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
    if (not schema_ids or WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID in schema_ids) and (not kinds or "document_payload" in kinds):
        schemas.append({
            "schemaId": WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID,
            "kind": "document_payload",
            "schemaVersion": WITNESS_ARTIFACT_BUNDLE_SCHEMA_VERSION,
            "documentType": WITNESS_ARTIFACT_BUNDLE_DOCUMENT_TYPE,
            "title": "CultNet Witness Artifact Bundle",
            "wireContracts": [INTEROP_WIRE_CONTRACT],
            "contentHash": WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID,
            "schemaJson": witness_artifact_bundle_schema_json() if include_schema_json else None,
        })
    if (not schema_ids or SIMULATION_FACT_SCHEMA_ID in schema_ids) and (not kinds or "document_payload" in kinds):
        entry = simulation_fact_document.catalog_entry()
        schemas.append({
            "schemaId": entry.schema_id,
            "kind": "document_payload",
            "schemaVersion": entry.schema_version,
            "documentType": SIMULATION_FACT_DOCUMENT_TYPE,
            "title": "CultMesh Simulation Fact",
            "wireContracts": [INTEROP_WIRE_CONTRACT],
            "contentHash": entry.content_hash,
            "schemaJson": entry.canonical_schema_json if include_schema_json else None,
        })
    return {"schemaVersion": "cultnet.schema_catalog_response.v0", "messageId": request.get("messageId", ""), "schemas": schemas}


def raw_snapshot_response(state: PeerState, request: dict[str, Any]) -> dict[str, Any]:
    schema_ids = set(request.get("schemaIds") or [])
    record_keys = set(request.get("recordKeys") or [])
    requested_shard_id = request.get("shardId")
    shard_record_keys: set[tuple[str, str]] | None = None
    if requested_shard_id == state.shard_id:
        shard_record_keys = live_shard_record_keys(state)
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
        if shard_record_keys is not None and (schema_id, envelope.key) not in shard_record_keys:
            continue
        documents.append(raw_record_from_envelope(envelope, schema_id))
    response: dict[str, Any] = {"schemaVersion": "cultnet.snapshot_response_raw.v0", "messageId": request.get("messageId", ""), "documents": documents}
    if requested_shard_id == state.shard_id:
        with state.shard_log_lock:
            response["shardId"] = state.shard_id
            response["shardEpoch"] = state.shard_epoch
            response["shardLogSequence"] = len(state.shard_log)
    return response


def live_shard_record_keys(state: PeerState) -> set[tuple[str, str]]:
    records: set[tuple[str, str]] = set()
    with state.shard_log_lock:
        for entry in state.shard_log:
            put = entry.get("put")
            if isinstance(put, dict) and isinstance(put.get("document"), dict):
                document = put["document"]
                schema_id = document.get("schemaId")
                record_key = document.get("recordKey")
                if schema_id and record_key:
                    records.add((str(schema_id), str(record_key)))
            delete = entry.get("delete")
            if isinstance(delete, dict):
                schema_id = delete.get("schemaId")
                record_key = delete.get("recordKey")
                if schema_id and record_key:
                    records.discard((str(schema_id), str(record_key)))
    return records


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


def write_message(transport: TcpFramedTransportConnection, message: dict[str, Any]) -> None:
    transport.send("schema", msgpack.packb(message, use_bin_type=True))


def read_until(transport: TcpFramedTransportConnection, predicate: Callable[[dict[str, Any]], bool], timeout_ms: int) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_ms / 1000
    while time.monotonic() < deadline:
        frame = transport.receive()
        message = msgpack.unpackb(frame.payload, raw=False)
        if isinstance(message, dict) and predicate(message):
            return message
    raise TimeoutError(f"Timed out waiting for CultNet message after {timeout_ms}ms")


def now_ms() -> int:
    return int(time.time() * 1000)


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


def witness_artifact_bundle_slots(value: dict[str, Any]) -> list[Any]:
    return [
        value["bundleId"],
        value["witnessKind"],
        value["capturedAt"],
        value["subject"],
        value["contracts"],
        value["artifacts"],
        value.get("timingWitnesses", []),
        value["provenance"],
    ]


def witness_artifact_bundle_from_slots(slots: list[Any]) -> dict[str, Any]:
    return {
        "bundleId": slots[0],
        "witnessKind": slots[1],
        "capturedAt": slots[2],
        "subject": slots[3],
        "contracts": slots[4] if len(slots) > 4 and isinstance(slots[4], list) else [],
        "artifacts": slots[5] if len(slots) > 5 and isinstance(slots[5], list) else [],
        "timingWitnesses": slots[6] if len(slots) > 6 and isinstance(slots[6], list) else [],
        "provenance": slots[7] if len(slots) > 7 and isinstance(slots[7], dict) else {},
    }


def witness_artifact_bundle_schema_json() -> str:
    return json.dumps({
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": WITNESS_ARTIFACT_BUNDLE_SCHEMA_ID,
        "title": "CultNet Witness Artifact Bundle",
        "type": "object",
        "required": ["bundleId", "witnessKind", "capturedAt", "subject", "contracts", "artifacts", "timingWitnesses", "provenance"],
        "additionalProperties": False,
        "properties": {
            "bundleId": {"type": "string", "minLength": 1},
            "witnessKind": {"type": "string", "minLength": 1},
            "capturedAt": {"type": "string", "minLength": 1},
            "subject": {"$ref": "#/$defs/subject_pin"},
            "contracts": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/contract_pin"}},
            "artifacts": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/artifact_entry"}},
            "timingWitnesses": {"type": "array", "items": {"$ref": "#/$defs/timing_entry"}},
            "provenance": {"$ref": "#/$defs/provenance"},
        },
        "$defs": {
            "subject_pin": {
                "type": "object",
                "required": ["documentType", "subjectId"],
                "additionalProperties": False,
                "properties": {
                    "documentType": {"type": "string", "minLength": 1},
                    "subjectId": {"type": "string", "minLength": 1},
                    "schemaVersion": {"type": "string", "minLength": 1},
                    "schemaId": {"type": "string", "minLength": 1},
                    "contentHash": {"type": "string", "minLength": 1},
                },
            },
            "contract_pin": {
                "type": "object",
                "required": ["role", "schemaId"],
                "additionalProperties": False,
                "properties": {
                    "role": {"type": "string", "minLength": 1},
                    "schemaId": {"type": "string", "minLength": 1},
                    "schemaVersion": {"type": "string", "minLength": 1},
                    "contentHash": {"type": "string", "minLength": 1},
                },
            },
            "artifact_entry": {
                "type": "object",
                "required": ["role", "uri", "mediaType"],
                "additionalProperties": False,
                "properties": {
                    "role": {"type": "string", "minLength": 1},
                    "uri": {"type": "string", "minLength": 1},
                    "mediaType": {"type": "string", "minLength": 1},
                    "contentHash": {"type": "string", "minLength": 1},
                    "byteLength": {"type": "integer", "minimum": 0},
                    "producedAt": {"type": "string", "minLength": 1},
                },
            },
            "timing_entry": {
                "type": "object",
                "required": ["stage", "startedAt", "completedAt", "latencyMs"],
                "additionalProperties": False,
                "properties": {
                    "stage": {"type": "string", "minLength": 1},
                    "startedAt": {"type": "string", "minLength": 1},
                    "completedAt": {"type": "string", "minLength": 1},
                    "latencyMs": {"type": "number", "minimum": 0},
                    "witnessArtifactUri": {"type": "string", "minLength": 1},
                },
            },
            "provenance": {
                "type": "object",
                "required": ["pipelineId", "runId", "runtimeId"],
                "additionalProperties": False,
                "properties": {
                    "pipelineId": {"type": "string", "minLength": 1},
                    "runId": {"type": "string", "minLength": 1},
                    "runtimeId": {"type": "string", "minLength": 1},
                    "agentId": {"type": "string", "minLength": 1},
                    "agentRole": {"type": "string", "minLength": 1},
                    "toolVersion": {"type": "string", "minLength": 1},
                },
            },
        },
    }, separators=(",", ":"))


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
