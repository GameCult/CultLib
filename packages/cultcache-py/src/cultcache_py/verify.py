from __future__ import annotations

import argparse
import importlib
import json
from importlib import resources
from typing import Any

from .benchmark import run_benchmark


EXPECTED_EXPORTS: dict[str, tuple[str, ...]] = {
    "cultcache_py": (
        "CultCache",
        "SingleFileMessagePackBackingStore",
        "define_database_entry_type",
        "define_document_type",
    ),
    "cultnet_py": (
        "CultNetClientAuthorityScope",
        "CultNetRawClient",
        "CultNetSimulationConsensus",
        "CultNetSimulationObservationHub",
        "apply_raw_snapshot",
        "apply_shard_log_response",
        "database_subscribe",
        "document_put_raw",
        "simulation_observation",
    ),
    "cultmesh_py": (
        "CultMesh",
        "CultMeshGameSession",
        "CultMeshSimulationFact",
        "CultMeshStreamCatalog",
        "simulation_fact_document",
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
