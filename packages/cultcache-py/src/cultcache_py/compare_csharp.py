from __future__ import annotations

import argparse
import json
import statistics
import subprocess
import sys
from pathlib import Path
from typing import Any

from .benchmark import run_benchmark


DEFAULT_SAMPLE_COUNT = 3


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultcache-py-compare-csharp")
    parser.add_argument("--records", type=int, default=5000)
    parser.add_argument("--samples", type=int, default=DEFAULT_SAMPLE_COUNT)
    parser.add_argument("--repo-root", type=Path, default=None)
    parser.add_argument("--json", action="store_true", dest="emit_json")
    args = parser.parse_args(argv)
    if args.records <= 0:
        raise ValueError("--records must be greater than zero")
    if args.samples <= 0:
        raise ValueError("--samples must be greater than zero")

    result = compare_with_csharp(records=args.records, samples=args.samples, repo_root=args.repo_root)
    if args.emit_json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        print(f"records: {result['records']}")
        print(f"csharpStatus: {result['csharpStatus']}")
        for runtime in ("python", "csharp"):
            benchmark = result.get(runtime)
            if not benchmark:
                continue
            print(runtime)
            for metric in benchmark["metrics"]:
                print(f"- {metric['name']}: {metric['opsPerSecond']:.0f} ops/s ({metric['elapsedMs']:.2f} ms)")
    return 0 if result["csharpStatus"] == "ok" else 1


def compare_with_csharp(*, records: int, samples: int = DEFAULT_SAMPLE_COUNT, repo_root: Path | None = None) -> dict[str, Any]:
    resolved_root = _resolve_repo_root(repo_root)
    python_samples = [run_benchmark(records) for _ in range(samples)]
    python_result = _median_result(python_samples)
    csharp_samples: list[dict[str, Any]] = []
    status = "ok"
    error = None
    for _ in range(samples):
        csharp_result, status, error = _run_csharp_benchmark(resolved_root, records)
        if csharp_result is None:
            break
        csharp_samples.append(csharp_result)
    csharp_result = _median_result(csharp_samples) if csharp_samples else None
    comparison = _compare_common_metrics(python_result, csharp_result) if csharp_result else []
    result: dict[str, Any] = {
        "records": records,
        "samples": samples,
        "repoRoot": str(resolved_root),
        "python": python_result,
        "pythonSamples": python_samples,
        "csharpStatus": status,
        "comparison": comparison,
    }
    if csharp_result is not None:
        result["csharp"] = csharp_result
        result["csharpSamples"] = csharp_samples
    if error:
        result["csharpError"] = error
    return result


def _resolve_repo_root(repo_root: Path | None) -> Path:
    if repo_root is not None:
        return repo_root.resolve()
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "CultLib.sln").is_file():
            return parent
    raise FileNotFoundError("Could not locate CultLib.sln; pass --repo-root.")


def _run_csharp_benchmark(repo_root: Path, records: int) -> tuple[dict[str, Any] | None, str, str | None]:
    project = repo_root / "packages" / "cultcache-py" / "tools" / "GameCult.Caching.Benchmark" / "GameCult.Caching.Benchmark.csproj"
    if not project.is_file():
        return None, "missing", f"C# benchmark project not found: {project}"

    completed = subprocess.run(
        ["dotnet", "run", "--project", str(project), "-c", "Release", "--", "--records", str(records), "--json"],
        cwd=repo_root,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if completed.returncode != 0:
        return None, "failed", completed.stderr.strip() or completed.stdout.strip()
    try:
        payload = next(line for line in reversed(completed.stdout.splitlines()) if line.strip())
        return json.loads(payload), "ok", None
    except json.JSONDecodeError as exc:
        return None, "invalid-json", f"{exc}: {completed.stdout}"
    except StopIteration:
        return None, "invalid-json", "C# benchmark produced no stdout."


def _compare_common_metrics(python_result: dict[str, Any], csharp_result: dict[str, Any]) -> list[dict[str, Any]]:
    python_metrics = {metric["name"]: metric for metric in python_result["metrics"]}
    csharp_metrics = {metric["name"]: metric for metric in csharp_result["metrics"]}
    comparison = []
    for name in sorted(set(python_metrics).intersection(csharp_metrics)):
        python_ops = float(python_metrics[name]["opsPerSecond"])
        csharp_ops = float(csharp_metrics[name]["opsPerSecond"])
        comparison.append({
            "name": name,
            "pythonOpsPerSecond": python_ops,
            "csharpOpsPerSecond": csharp_ops,
            "pythonToCsharpRatio": python_ops / csharp_ops if csharp_ops > 0 else None,
        })
    return comparison


def _median_result(samples: list[dict[str, Any]]) -> dict[str, Any]:
    if not samples:
        raise ValueError("Cannot summarize zero benchmark samples")
    if len(samples) == 1:
        return samples[0]
    metric_names = [metric["name"] for metric in samples[0]["metrics"]]
    metrics = []
    for name in metric_names:
        matching = [_metric_by_name(sample, name) for sample in samples]
        metrics.append({
            "name": name,
            "operations": int(statistics.median(metric["operations"] for metric in matching)),
            "elapsedMs": statistics.median(float(metric["elapsedMs"]) for metric in matching),
            "opsPerSecond": statistics.median(float(metric["opsPerSecond"]) for metric in matching),
        })
    result = dict(samples[0])
    result["metrics"] = metrics
    result["sampleCount"] = len(samples)
    return result


def _metric_by_name(sample: dict[str, Any], name: str) -> dict[str, Any]:
    for metric in sample["metrics"]:
        if metric["name"] == name:
            return metric
    raise KeyError(f"Benchmark sample is missing metric {name!r}")


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
