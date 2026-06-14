from __future__ import annotations

import argparse
import json
import signal
import sys
import threading
from pathlib import Path
from typing import Any

from cultcache_py.interop import INTEROP_SCHEMA_VERSION, interop_note_document

from .facade import CultMesh
from .server import CultMeshLocalServer

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
    return CultMesh.serve_node(
        node,
        host=args.host,
        port=args.port,
        display_name=args.display_name,
        max_snapshot_documents=args.max_snapshot_documents,
        max_snapshot_bytes=args.max_snapshot_bytes,
    )


def daemon_ready_document(server: CultMeshLocalServer) -> dict[str, Any]:
    return {
        "schemaVersion": READY_SCHEMA_VERSION,
        "runtimeId": server.node.runtime_id,
        "runtimeKind": server.runtime_kind,
        "host": server.host,
        "port": server.port,
        "endpoint": f"cultnet://{server.host}:{server.port}",
        "supportedDocumentTypes": [document.type for document in server.node.documents],
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
