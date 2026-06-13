from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass
from typing import Any

from cultcache_py import CultCache, CultCacheEnvelope, define_database_entry_type
from cultnet_py import apply_raw_snapshot, decode_frame, encode_frame, parse_message
from cultnet_py.messages import document_put_raw


@dataclass
class BenchItem:
    name: str
    category: str
    value: int


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultcache-py-benchmark")
    parser.add_argument("--records", type=int, default=5000)
    parser.add_argument("--json", action="store_true", dest="as_json")
    args = parser.parse_args(argv)
    if args.records <= 0:
        raise ValueError("--records must be greater than zero")
    result = run_benchmark(args.records)
    if args.as_json:
        print(json.dumps(result, sort_keys=True))
    else:
        print(f"records: {result['records']}")
        for metric in result["metrics"]:
            print(f"{metric['name']}: {metric['opsPerSecond']:.0f} ops/s ({metric['elapsedMs']:.2f} ms)")
    return 0


def run_benchmark(records: int) -> dict[str, Any]:
    document = define_database_entry_type(
        "bench.item",
        [
            ("name", 0),
            ("category", 1),
            ("value", 2, 0),
        ],
        cls=BenchItem,
    )
    values = [BenchItem(f"item-{index}", f"cat-{index % 8}", index) for index in range(records)]

    encoded_payloads, encode_metric = measure("database_entry_encode", records, lambda: [
        document.encode_payload(value)
        for value in values
    ])
    _, decode_metric = measure("database_entry_decode", records, lambda: [
        document.decode_payload(payload)
        for payload in encoded_payloads
    ])
    wire_messages, frame_metric = measure("cultnet_frame_parse", records, lambda: [
        parse_message(decode_frame(encode_frame(document_put_raw(
            message_id=f"put-{index}",
            key=f"item:{index}",
            schema_id=document.catalog_entry().schema_id,
            stored_at="2026-06-13T00:00:00Z",
            payload=encoded_payloads[index],
            source_runtime_id="python-benchmark",
            shard_id="bench",
            shard_epoch=1,
        ).to_bytes())))
        for index in range(records)
    ])

    snapshot = {
        "schemaVersion": "cultnet.snapshot_response_raw.v0",
        "messageId": "bench-snapshot",
        "documents": [
            {
                "schemaId": document.catalog_entry().schema_id,
                "recordKey": f"item:{index}",
                "storedAt": "2026-06-13T00:00:00Z",
                "payloadEncoding": "messagepack",
                "payload": encoded_payloads[index],
            }
            for index in range(records)
        ],
    }

    cache = CultCache()
    cache.register_document_type(document)
    cache.add_generic_store(InMemoryBackingStore())
    _, apply_metric = measure("raw_snapshot_apply", records, lambda: apply_raw_snapshot(cache, [document], snapshot))

    return {
        "records": records,
        "metrics": [
            encode_metric,
            decode_metric,
            frame_metric,
            apply_metric,
        ],
        "wireMessageCount": len(wire_messages),
    }


def measure(name: str, operations: int, action: Any) -> tuple[Any, dict[str, Any]]:
    started = time.perf_counter()
    result = action()
    elapsed = time.perf_counter() - started
    return result, {
        "name": name,
        "operations": operations,
        "elapsedMs": elapsed * 1000,
        "opsPerSecond": operations / elapsed if elapsed > 0 else float("inf"),
    }


class InMemoryBackingStore:
    def __init__(self) -> None:
        self._records: dict[tuple[str, str], CultCacheEnvelope] = {}

    def pull_all(self) -> list[CultCacheEnvelope]:
        return list(self._records.values())

    def push(self, envelope: CultCacheEnvelope) -> None:
        self._records[(envelope.type, envelope.key)] = envelope

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        for envelope in envelopes:
            self.push(envelope)

    def delete(self, type: str, key: str) -> None:
        self._records.pop((type, key), None)


if __name__ == "__main__":
    raise SystemExit(main())
