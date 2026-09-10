from __future__ import annotations

import argparse
import json
import threading
import time
import tracemalloc
from typing import Any

from cultcache_py import define_document_type
from cultmesh_py import CultMeshReactiveDocumentOptions, create_node


def percentile(ordered: list[float], ratio: float) -> float:
    if not ordered:
        return 0.0
    index = max(0, min(len(ordered) - 1, int(len(ordered) * ratio + 0.999999) - 1))
    return ordered[index]


def measure(document_count: int, idle_seconds: float, active_seconds: float) -> dict[str, Any]:
    payload = "x" * (16 * 1024)
    changed_document_count = max(1, document_count // 100)
    document = define_document_type(
        f"gamecult.mesh.python_performance_probe_{document_count}",
        encode=lambda value: value,
        decode=lambda value: value,
    )
    node = create_node(runtime_id=f"python-performance-{document_count}")
    node.register_document(document)
    reactive_documents = []
    publish_events = [threading.Event() for _ in range(document_count)]
    update_started_at = [0.0] * document_count
    latencies: list[float] = []
    payload_bytes_published = 0
    publishes = 0
    measurement_lock = threading.Lock()

    for index in range(document_count):
        key = f"performance:{index}"
        node.put(document, key, {"id": key, "payload": payload, "revision": 1})
        writer = node.authoritative_writer(document, key)
        original_write = writer.write

        def measured_write(value: Any, document_index: int = index, write=original_write) -> None:
            nonlocal payload_bytes_published, publishes
            encoded_bytes = len(document.encode_payload(value))
            latency_ms = (time.perf_counter() - update_started_at[document_index]) * 1000
            with measurement_lock:
                payload_bytes_published += encoded_bytes
                publishes += 1
                latencies.append(latency_ms)
            write(value)
            publish_events[document_index].set()

        writer.write = measured_write  # type: ignore[method-assign]
        reactive_documents.append(
            writer.reactive(CultMeshReactiveDocumentOptions(flush_delay_seconds=0.0))
        )

    tracemalloc.start()
    idle_cpu_start = time.process_time()
    time.sleep(idle_seconds)
    idle_current_bytes, idle_peak_bytes = tracemalloc.get_traced_memory()
    idle_cpu_ms = (time.process_time() - idle_cpu_start) * 1000
    tracemalloc.reset_peak()
    active_current_start, _ = tracemalloc.get_traced_memory()
    active_cpu_start = time.process_time()
    active_started_at = time.perf_counter()
    frames = 0
    while time.perf_counter() - active_started_at < active_seconds:
        for index in range(changed_document_count):
            publish_events[index].clear()
            update_started_at[index] = time.perf_counter()
            reactive_documents[index].update(
                lambda draft: draft.__setitem__("revision", draft["revision"] + 1)
            )
        for index in range(changed_document_count):
            if not publish_events[index].wait(timeout=1.0):
                raise RuntimeError(f"Python reactive publication timed out for document {index}")
        frames += 1
        remaining = frames / 60 - (time.perf_counter() - active_started_at)
        if remaining > 0:
            time.sleep(remaining)

    active_elapsed_seconds = time.perf_counter() - active_started_at
    active_cpu_ms = (time.process_time() - active_cpu_start) * 1000
    active_current_bytes, active_peak_bytes = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    for reactive in reactive_documents:
        reactive.dispose()
    ordered = sorted(latencies)
    return {
        "documentCount": document_count,
        "changedDocumentCount": changed_document_count,
        "frames": frames,
        "expectedPublishes": frames * changed_document_count,
        "actualPublishes": publishes,
        "payloadBytesPublished": payload_bytes_published,
        "idleTracedCurrentBytes": idle_current_bytes,
        "idleTracedPeakBytes": idle_peak_bytes,
        "idleCpuMilliseconds": idle_cpu_ms,
        "activeTracedRetainedGrowthBytes": active_current_bytes - active_current_start,
        "activeTracedPeakGrowthBytes": active_peak_bytes - active_current_start,
        "activeCpuMilliseconds": active_cpu_ms,
        "publishedPayloadBytesPerSecond": payload_bytes_published / active_elapsed_seconds,
        "p50PublishLatencyMilliseconds": percentile(ordered, 0.50),
        "p95PublishLatencyMilliseconds": percentile(ordered, 0.95),
        "p99PublishLatencyMilliseconds": percentile(ordered, 0.99),
        "schedulerThreads": sum(
            thread.name == "cultmesh-reactive-scheduler" for thread in threading.enumerate()
        ),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--quick", action="store_true")
    args = parser.parse_args()
    idle_seconds = 0.25 if args.quick else 10.0
    active_seconds = 0.5 if args.quick else 10.0
    results = [measure(count, idle_seconds, active_seconds) for count in (1, 100, 1000)]
    print(json.dumps({
        "runtime": "python",
        "workload": {
            "documentCounts": [1, 100, 1000],
            "payloadBytes": 16 * 1024,
            "idleSeconds": idle_seconds,
            "activeSeconds": active_seconds,
            "updateRateHz": 60,
            "changedFraction": 0.01,
        },
        "results": results,
    }, indent=2))
    return 0 if all(
        result["actualPublishes"] == result["expectedPublishes"]
        and result["p99PublishLatencyMilliseconds"] < 250
        and result["activeTracedPeakGrowthBytes"] < 128 * 1024 * 1024
        and result["schedulerThreads"] == 1
        for result in results
    ) else 1


if __name__ == "__main__":
    raise SystemExit(main())
