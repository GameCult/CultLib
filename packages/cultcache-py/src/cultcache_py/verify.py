from __future__ import annotations

import argparse
import importlib
import json
from importlib import resources
from typing import Any

from .benchmark import run_benchmark


EXPECTED_EXPORTS: dict[str, tuple[str, ...]] = {
    "cultcache_py": (
        "BackingStore",
        "CultCache",
        "CultCacheBuilder",
        "CultCacheEnvelope",
        "CultCacheSchemaCatalogEntry",
        "CultCacheSchemaCatalogMember",
        "DatabaseEntryField",
        "DocumentDefinition",
        "JsonLinesBackingStore",
        "SingleFileMessagePackBackingStore",
        "database_entry_field",
        "define_database_entry_type",
        "define_document_registry",
        "define_document_type",
    ),
    "cultnet_py": (
        "CultNetAppliedRecord",
        "CultNetDatabaseChange",
        "CultNetClientAuthorityScope",
        "CultNetDatabaseSubscription",
        "CultNetMessage",
        "CultNetRawClient",
        "CultNetRawDocumentRecord",
        "CultNetRawSnapshotResponse",
        "CultNetSchemaCatalog",
        "CultNetSchemaDescriptor",
        "CultNetShardCatalog",
        "CultNetShardDescriptor",
        "CultNetShardLogEntry",
        "CultNetShardLogResponse",
        "CultNetSimulationConsensus",
        "CultNetSimulationConsensusCandidate",
        "CultNetSimulationConsensusOptions",
        "CultNetSimulationObservation",
        "CultNetSimulationObservationHub",
        "INTEROP_WIRE_CONTRACT",
        "apply_raw_document_record",
        "apply_raw_snapshot",
        "apply_shard_log_response",
        "compute_simulation_claim_hash",
        "database_subscribe",
        "database_unsubscribe",
        "decode_frame",
        "decode_witness_artifact_bundle_payload",
        "document_delete",
        "document_put_raw",
        "encode_frame",
        "encode_witness_artifact_bundle_payload",
        "hello",
        "parse_message",
        "read_frame",
        "schema_catalog_request",
        "schema_document_map",
        "shard_catalog_request",
        "shard_log_request",
        "simulation_observation",
        "snapshot_request",
        "wire_message_schema_descriptors",
        "wire_message_schema_catalog",
        "wire_message_schema_json",
        "witness_artifact_bundle",
        "write_frame",
    ),
    "cultmesh_py": (
        "CultMeshAuthorityLease",
        "CultMeshAuthorityLeaseCatalog",
        "CultMesh",
        "CultMeshDatabase",
        "CultMeshDatabaseChange",
        "CultMeshDiscoveryClient",
        "CultMeshGameSession",
        "CultMeshGameSessionOptions",
        "CultMeshLocalServer",
        "CultMeshNode",
        "CultMeshPeerCard",
        "CultMeshPeerCatalog",
        "CultMeshPeerExchangeClient",
        "CultMeshPrediction",
        "CultMeshSessionChange",
        "CultMeshSimulationFact",
        "CultMeshSimulationFactCommit",
        "CultMeshSimulationFactCommitter",
        "CultMeshStreamCatalog",
        "CultMeshStreamConsumerProfile",
        "CultMeshStreamDescriptor",
        "CultMeshStreamFrameHandle",
        "CultMeshStreamNegotiation",
        "CultMeshVerseCatalog",
        "CultMeshVerseCompatibility",
        "CultMeshVerseDescriptor",
        "CultMeshVerseDiscoveryClient",
        "PEER_EXCHANGE_REQUEST",
        "PEER_EXCHANGE_RESPONSE",
        "SIMULATION_FACT_DOCUMENT_TYPE",
        "SIMULATION_FACT_SCHEMA_VERSION",
        "VERSE_CATALOG_REQUEST",
        "VERSE_CATALOG_RESPONSE",
        "create_node",
        "peer_exchange_request",
        "peer_from_wire",
        "simulation_fact_document",
        "verse_catalog_request",
        "verse_from_wire",
    ),
}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultcache-py-verify")
    parser.add_argument("--records", type=int, default=64)
    parser.add_argument("--json", action="store_true", dest="emit_json")
    args = parser.parse_args(argv)

    result = verify(records=args.records)
    if args.emit_json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"cultcache-py verify: {result['status']}")
        for check in result["checks"]:
            print(f"- {check['name']}: {check['status']}")
    return 0 if result["status"] == "ok" else 1


def verify(*, records: int = 64) -> dict[str, Any]:
    checks = [
        _check_exports(),
        _check_typed_markers(),
        _check_benchmark(records),
    ]
    return {
        "status": "ok" if all(check["status"] == "ok" for check in checks) else "failed",
        "checks": checks,
    }


def _check_exports() -> dict[str, Any]:
    missing: dict[str, list[str]] = {}
    for module_name, names in EXPECTED_EXPORTS.items():
        module = importlib.import_module(module_name)
        missing_names = [name for name in names if not hasattr(module, name)]
        if missing_names:
            missing[module_name] = missing_names
    return {
        "name": "public_exports",
        "status": "ok" if not missing else "failed",
        "missing": missing,
    }


def _check_typed_markers() -> dict[str, Any]:
    missing = []
    for module_name in EXPECTED_EXPORTS:
        package_root = resources.files(module_name)
        marker = package_root.joinpath("py.typed")
        if not marker.is_file():
            missing.append(module_name)
    return {
        "name": "typed_markers",
        "status": "ok" if not missing else "failed",
        "missing": missing,
    }


def _check_benchmark(records: int) -> dict[str, Any]:
    metrics = run_benchmark(records)
    required = [
        "database_entry_encode",
        "database_entry_decode",
        "cultnet_frame_parse",
        "raw_snapshot_apply",
        "cache_upsert",
        "cache_get",
    ]
    metric_map = {metric["name"]: metric for metric in metrics["metrics"]}
    missing = [
        name
        for name in required
        if name not in metric_map or float(metric_map[name].get("opsPerSecond", 0.0)) <= 0.0
    ]
    return {
        "name": "benchmark_sanity",
        "status": "ok" if not missing else "failed",
        "records": records,
        "missing": missing,
        "metrics": metrics,
    }


if __name__ == "__main__":
    raise SystemExit(main())
