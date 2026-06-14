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
        "CultNetWitnessArtifactBundle",
        "CultNetRawClient",
        "CultNetRawDocumentRecord",
        "CultNetRawSnapshotResponse",
        "CultNetSchemaCatalog",
        "CultNetSchemaDescriptor",
        "CultNetShardCatalog",
        "CultNetShardDescriptor",
        "CultNetShardLogEntry",
        "CultNetShardLogResponse",
        "CultNetFileShardReplicaCursorStore",
        "CultNetFileShardMutationLogStore",
        "CultNetInMemoryShardReplicaCursorStore",
        "CultNetSchemaWriteForwarder",
        "CultNetSchemaShardLogFetcher",
        "CultNetSchemaShardSnapshotFetcher",
        "CultNetShardLogFetcher",
        "CultNetShardMutationLogStore",
        "CultNetShardWriteForwarder",
        "CultNetShardReplicaCursor",
        "CultNetShardReplicaCursorStore",
        "CultNetShardReplicator",
        "CultNetShardReplicatorOptions",
        "CultNetShardSnapshotFetcher",
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
        "CultMeshNodeOptions",
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
        _check_local_cultmesh_wire_smoke(),
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


def _check_local_cultmesh_wire_smoke() -> dict[str, Any]:
    try:
        from cultcache_py import define_database_entry_type
        from cultmesh_py import CultMesh
        from cultnet_py import hello

        document = define_database_entry_type(
            "verify.mesh_note",
            [("body", 0)],
            schema_id="verify.mesh_note.v1",
        )
        node = CultMesh.create_node(runtime_id="verify-python")
        node.database.register_document(document)
        node.database.put_raw_message(
            document,
            "note:verify",
            {"body": "wire smoke"},
            shard_id="verify",
            shard_epoch=1,
        )
        server = CultMesh.serve_node(node, display_name="Verify Python")
        try:
            client = CultMesh.create_client("127.0.0.1", server.port, timeout_seconds=2.0)
            hello_response = client.request(hello(runtime_id="verify-client"), expected_schema_version="cultnet.hello.v0")
            schema_response = client.fetch_schema_catalog(schema_ids=["verify.mesh_note.v1"])
            snapshot_response = client.fetch_snapshot_response(schema_ids=["verify.mesh_note.v1"])
            shard_catalog = client.fetch_shard_descriptors(schema_ids=["verify.mesh_note.v1"])
            shard_log = client.fetch_shard_log_response(shard_id="verify", shard_epoch=1)
        finally:
            server.stop()

        failures = []
        if hello_response.get("runtimeId") != "verify-python":
            failures.append("hello_runtime")
        supported_versions = set(hello_response.get("supportedMessageVersions") or ())
        for schema_version in (
            "cultnet.document_put_raw.v0",
            "cultnet.document_delete.v0",
            "cultnet.shard_log_request.v0",
        ):
            if schema_version not in supported_versions:
                failures.append(f"hello_supported_message:{schema_version}")
        mutation_contracts = hello_response.get("supportedMutationContracts") or []
        if not any(
            contract.get("documentType") == "verify.mesh_note"
            and {"snapshot", "documentPut", "documentDelete", "shardLog"}.issubset(
                set(contract.get("operations") or ())
            )
            for contract in mutation_contracts
        ):
            failures.append("hello_mutation_contract")
        if not schema_response.get("schemas"):
            failures.append("schema_catalog")
        else:
            wire_contracts = set(schema_response["schemas"][0].get("wireContracts") or ())
            for schema_version in (
                "cultnet.snapshot_response_raw.v0",
                "cultnet.document_put_raw.v0",
                "cultnet.document_delete.v0",
                "cultnet.shard_log_response.v0",
            ):
                if schema_version not in wire_contracts:
                    failures.append(f"schema_wire_contract:{schema_version}")
        if not snapshot_response.documents or snapshot_response.documents[0].record_key != "note:verify":
            failures.append("snapshot")
        if not shard_catalog or shard_catalog[0].shard_id != "verify":
            failures.append("shard_catalog")
        if shard_log.resync_required or shard_log.last_sequence != 1:
            failures.append("shard_log")
        return {
            "name": "local_cultmesh_wire_smoke",
            "status": "ok" if not failures else "failed",
            "failures": failures,
        }
    except Exception as exc:
        return {
            "name": "local_cultmesh_wire_smoke",
            "status": "failed",
            "error": str(exc),
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
