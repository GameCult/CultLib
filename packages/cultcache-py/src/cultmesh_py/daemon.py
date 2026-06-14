from __future__ import annotations

import argparse
import json
import signal
import sys
import threading
from pathlib import Path
from typing import Any

from cultcache_py.interop import INTEROP_SCHEMA_VERSION, interop_note_document
from cultnet_py import CultNetSimulationConsensusOptions, CultNetSimulationObservationHub, hello

from .facade import CultMesh
from .server import CultMeshLocalServer
from .wire import (
    CultMeshPeerCard,
    CultMeshPeerCatalog,
    CultMeshVerseCatalog,
    CultMeshVerseCompatibility,
    CultMeshVerseDescriptor,
)

READY_SCHEMA_VERSION = "cultmesh.daemon_ready.v0"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultmesh-py-daemon")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=3075)
    parser.add_argument("--runtime-id", default="python-runtime")
    parser.add_argument("--display-name")
    parser.add_argument("--cache-file")
    parser.add_argument("--enable-durable-shard-logs", action="store_true")
    parser.add_argument("--shard-log-file")
    parser.add_argument("--max-snapshot-documents", type=int)
    parser.add_argument("--max-snapshot-bytes", type=int)
    parser.add_argument("--ready-file")
    parser.add_argument("--register-interop-note", action="store_true")
    parser.add_argument("--seed-interop-note", action="store_true")
    parser.add_argument("--seed-shard-id", default="interop")
    parser.add_argument("--seed-shard-epoch", type=int, default=1)
    parser.add_argument("--enable-simulation-observations", action="store_true")
    parser.add_argument("--simulation-minimum-witnesses", type=int, default=1)
    parser.add_argument("--simulation-quorum-ratio", type=float, default=0.5)
    parser.add_argument("--verse-id")
    parser.add_argument("--verse-display-name")
    parser.add_argument("--role", action="append", dest="roles")
    args = parser.parse_args(argv)

    stop = threading.Event()
    install_signal_handlers(stop)
    server = start_server(args)
    try:
        ready = daemon_ready_document(server)
        publish_ready(ready, ready_file=args.ready_file)
        while not stop.wait(0.2):
            pass
    finally:
        server.stop()
    return 0


def start_server(args: argparse.Namespace) -> CultMeshLocalServer:
    node = CultMesh.create_node(
        args.cache_file,
        runtime_id=args.runtime_id,
        enable_durable_shard_logs=args.enable_durable_shard_logs,
        shard_log_path=args.shard_log_file,
    )
    if args.register_interop_note or args.seed_interop_note:
        node.database.register_document(interop_note_document)
        if args.cache_file:
            node.database.pull()
    if args.seed_interop_note:
        node.database.put_raw_message(
            interop_note_document,
            f"note:{args.runtime_id}",
            {
                "schemaVersion": INTEROP_SCHEMA_VERSION,
                "documentId": f"note:{args.runtime_id}",
                "authorRuntimeId": args.runtime_id,
                "title": f"{args.runtime_id} wrote a CultMesh daemon note",
                "body": "The Python CultMesh daemon is serving typed state over CultNet.",
                "tags": [args.runtime_id, "python", "cultmesh", "daemon"],
            },
            message_id=f"daemon-seed:{args.runtime_id}",
            shard_id=args.seed_shard_id,
            shard_epoch=args.seed_shard_epoch,
        )
    verse_catalog = CultMeshVerseCatalog()
    peer_catalog = CultMeshPeerCatalog()
    observation_hub = None
    if args.enable_simulation_observations:
        observation_hub = CultNetSimulationObservationHub(
            CultNetSimulationConsensusOptions(
                minimum_witnesses=args.simulation_minimum_witnesses,
                quorum_ratio=args.simulation_quorum_ratio,
            )
        )
    server = CultMesh.serve_node(
        node,
        verse_catalog=verse_catalog,
        peer_catalog=peer_catalog,
        observation_hub=observation_hub,
        host=args.host,
        port=args.port,
        display_name=args.display_name,
        max_snapshot_documents=args.max_snapshot_documents,
        max_snapshot_bytes=args.max_snapshot_bytes,
    )
    advertise_self(server, verse_catalog, peer_catalog, args)
    return server


def advertise_self(
    server: CultMeshLocalServer,
    verse_catalog: CultMeshVerseCatalog,
    peer_catalog: CultMeshPeerCatalog,
    args: argparse.Namespace,
) -> None:
    verse_id = args.verse_id
    if not verse_id:
        return
    endpoint = f"cultnet://{server.host}:{server.port}"
    shard_ids = tuple(server.node.database.shard_ids())
    roles = list(args.roles or ("read-replica",))
    if server.observation_hub is not None and "simulation-observer" not in roles:
        roles.append("simulation-observer")
    verse_catalog.upsert(CultMeshVerseDescriptor(
        verse_id=verse_id,
        display_name=args.verse_display_name or verse_id,
        authority_model="local",
        compatibility=CultMeshVerseCompatibility(
            transport_version="cultmesh.v0",
            rules_hash="python-daemon",
        ),
        discovery_endpoints=(endpoint,),
        authority_runtime_ids=(server.node.runtime_id,),
    ))
    peer_catalog.upsert(CultMeshPeerCard(
        peer_id=server.node.runtime_id,
        verse_id=verse_id,
        endpoints=(endpoint,),
        roles=tuple(roles),
        shard_ids=shard_ids,
    ))


def daemon_ready_document(server: CultMeshLocalServer) -> dict[str, Any]:
    hello_response = server.handle_message(hello(runtime_id="daemon-ready-probe").to_wire()) or {}
    return {
        "schemaVersion": READY_SCHEMA_VERSION,
        "runtimeId": server.node.runtime_id,
        "runtimeKind": server.runtime_kind,
        "displayName": hello_response.get("displayName") or server.node.runtime_id,
        "host": server.host,
        "port": server.port,
        "endpoint": f"cultnet://{server.host}:{server.port}",
        "shardIds": server.node.database.shard_ids(),
        "supportedDocumentTypes": [document.type for document in server.node.documents],
        "supportedMessageVersions": list(hello_response.get("supportedMessageVersions") or ()),
        "supportedMutationContracts": list(hello_response.get("supportedMutationContracts") or ()),
        "transportProfiles": list(hello_response.get("transportProfiles") or ()),
        "snapshotLimits": {
            "maxSnapshotDocuments": server.max_snapshot_documents,
            "maxSnapshotBytes": server.max_snapshot_bytes,
        },
        "verses": [verse.to_wire() for verse in server.verse_catalog.verses],
        "peers": [peer.to_wire() for peer in server.peer_catalog.peers],
    }


def publish_ready(document: dict[str, Any], *, ready_file: str | None = None) -> None:
    text = json.dumps(document, separators=(",", ":"), sort_keys=True)
    if ready_file:
        Path(ready_file).write_text(text, encoding="utf-8")
    print(text, flush=True)


def install_signal_handlers(stop: threading.Event) -> None:
    def request_stop(signum: int, frame: Any) -> None:
        stop.set()

    for signal_name in ("SIGINT", "SIGTERM"):
        signal_value = getattr(signal, signal_name, None)
        if signal_value is None:
            continue
        try:
            signal.signal(signal_value, request_stop)
        except (OSError, ValueError):
            continue


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
