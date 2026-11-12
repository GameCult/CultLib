"""A sibling package resolves through the workspace's editable install, so a package that starts importing a new name from
cultcache-py passes whatever range it declares. A consumer outside the workspace installs the newest release the range
admits. This is the Python form of scripts/test-declared-dependency-ranges.mjs: for every tagged cultcache-py release
(`cultcache-py-vX.Y.Z`) that the `cultcache-py>=` bound of cultnet-py or cultmesh-py admits, every name those packages
import from cultcache_py must be defined by that release's source. It needs git with the release tags and skips without
them. Only `>=` bounds are read; star imports and names reached by attribute access are not checked."""

from __future__ import annotations

import ast
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PACKAGES = ROOT / "packages"
CONSUMERS = ("cultnet-py", "cultmesh-py")


def _version(text: str) -> tuple[int, ...]:
    return tuple(int(part) for part in text.split("."))


def _git(*args: str) -> str | None:
    try:
        done = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True)
    except (OSError, subprocess.CalledProcessError):
        return None
    return done.stdout


def _lower_bound(package: str) -> tuple[int, ...]:
    manifest = (PACKAGES / package / "pyproject.toml").read_text(encoding="utf-8")
    match = re.search(r'"cultcache-py>=(\d+\.\d+\.\d+)"', manifest)
    assert match, f"{package} declares no cultcache-py>= bound"
    return _version(match.group(1))


def _imports(package: str) -> dict[str, set[str]]:
    """module -> names imported from it, for every `from cultcache_py[.x] import names` under the package's src."""
    found: dict[str, set[str]] = {}
    for source in (PACKAGES / package / "src").rglob("*.py"):
        for node in ast.walk(ast.parse(source.read_text(encoding="utf-8-sig"))):
            if isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                if node.module == "cultcache_py" or node.module.startswith("cultcache_py."):
                    found.setdefault(node.module, set()).update(alias.name for alias in node.names if alias.name != "*")
    return found


def _defined(tag: str, module: str) -> set[str] | None:
    base = "packages/cultcache-py/src/" + module.replace(".", "/")
    for path in (base + ".py", base + "/__init__.py"):
        text = _git("show", f"{tag}:{path}")
        if text is None:
            continue
        names: set[str] = set()
        for node in ast.parse(text.lstrip("\ufeff")).body:
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                names.add(node.name)
            elif isinstance(node, (ast.Import, ast.ImportFrom)):
                names.update((alias.asname or alias.name).split(".")[0] for alias in node.names)
            elif isinstance(node, (ast.Assign, ast.AnnAssign)):
                for target in node.targets if isinstance(node, ast.Assign) else [node.target]:
                    if isinstance(target, ast.Name):
                        names.add(target.id)
        return names
    return None


class DeclaredDependencyRangeTests(unittest.TestCase):
    def test_every_admitted_cultcache_release_defines_what_the_consumers_import(self) -> None:
        listed = _git("tag", "--list", "cultcache-py-v*")
        tags = sorted((t for t in (listed or "").split() if re.fullmatch(r"cultcache-py-v\d+\.\d+\.\d+", t)),
                      key=lambda t: _version(t.rsplit("v", 1)[1]))
        if not tags:
            self.skipTest("git with the cultcache-py release tags is not available")
        current = re.search(r'^version = "(\d+\.\d+\.\d+)"', (PACKAGES / "cultcache-py" / "pyproject.toml").read_text(encoding="utf-8"), re.M)
        assert current
        for package in CONSUMERS:
            bound = _lower_bound(package)
            self.assertLessEqual(bound, _version(current.group(1)), f"{package}'s bound is above the workspace cultcache-py")
            imports = _imports(package)
            for tag in tags:
                if _version(tag.rsplit("v", 1)[1]) < bound:
                    continue
                for module, names in imports.items():
                    defined = _defined(tag, module)
                    self.assertIsNotNone(defined, f"{package} imports {module}, which {tag} does not have")
                    self.assertEqual(sorted(names - defined), [], f"{package} imports names {tag} lacks from {module}")


if __name__ == "__main__":
    unittest.main()
