from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

import msgpack

from cultcache_py import (
    CultCache,
    SchemaConflictError,
    SingleFileMessagePackBackingStore,
    database_entry_field,
    define_database_entry_type,
    leaves as t,
)

# variants:ruling:cache-refuses-retype, write-ownership-by-written-type, unheld-removal-refused-at-commit. The vector in
# tests/vectors/key-identity is written only by the C# reference; this walks every row against the Python cache.
VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "key-identity"

IDS = {
    "alpha": "sha256:b20703a98f49c253898404ab50ba2c630720f0b8a5e1a41614d5650be22799fe",
    "beta": "sha256:dee4b727cf2ee04e61ab635ff0c5f3dca7345a411dc5f1018e583ab16009cf94",
    "gamma": "sha256:fbc26e351e00c9e7185e8de171c7486aba707c229c949f0aae6b889288af5f6d",
}
DOCUMENTS = {
    label: define_database_entry_type(
        f"tests.ki_{label}",
        [database_entry_field("text", 0, codec=t.string)],
        schema_id=schema_id,
        schema_name=f"tests.ki_{label}",
    )
    for label, schema_id in IDS.items()
}
LABEL_OF = {document.type: label for label, document in DOCUMENTS.items()}


def file_records(path: Path) -> list[list[str]]:
    return sorted([envelope.key, envelope.type] for envelope in SingleFileMessagePackBackingStore(path).pull_all())


def stored(path: Path, key: str):
    for envelope in SingleFileMessagePackBackingStore(path).pull_all():
        if envelope.key == key:
            return envelope.schema_id, envelope.stored_at, envelope.payload
    return None


class KeyIdentityVectorTests(unittest.TestCase):
    def test_rows(self) -> None:
        rows = msgpack.unpackb((VECTORS / "rows.msgpack").read_bytes(), raw=False)
        walked = 0
        for label, registered, ops, expect in rows:
            with self.subTest(row=label), tempfile.TemporaryDirectory() as tmp:
                self.walk(Path(tmp), registered, ops, expect)
                walked += 1
        self.assertEqual(walked, 11)

    def walk(self, tmp: Path, registered: list[str], ops: list, expect: list) -> None:
        refusal, held, file, foreign, kept = expect
        state: dict = {"cache": None, "path": None, "before": None, "attached": []}

        def builder() -> CultCache.builder:
            made = CultCache.builder()
            for name in registered:
                made.register_document_type(DOCUMENTS[name])
            return made

        def open_cache(target) -> None:
            if target == "memory":
                state["cache"], state["path"] = builder().build(), None
                return
            path = tmp / "store.cc"
            if target is not None and target != path:
                shutil.copyfile(VECTORS / target, path)
                state["before"] = path.read_bytes()
            state["path"] = path
            made = builder().add_generic_store(SingleFileMessagePackBackingStore(path))
            state["cache"] = made.build()
            state["cache"].pull_all_backing_stores()

        def perform(op: list) -> None:
            cache = state["cache"]
            if op[0] == "open":
                open_cache(op[1])
            elif op[0] == "attach":
                path = tmp / f"attached-{len(state['attached'])}.cc"
                shutil.copyfile(VECTORS / op[1], path)
                state["attached"].append((path, path.read_bytes()))
                cache.add_backing_store(
                    SingleFileMessagePackBackingStore(path), [DOCUMENTS[name].type for name in op[2]]
                )
                cache.pull_all_backing_stores()
            elif op[0] == "reopen":
                open_cache(state["path"])  # the same file, no fresh copy
            elif op[0] == "put":
                cache.put(DOCUMENTS[op[1]], op[2], {"text": op[3]})
            elif op[0] == "delete":
                cache.delete(DOCUMENTS[op[1]], op[2])
            else:
                self.fail(f"unknown op {op[0]}")

        for op in ops[:-1]:
            perform(op)
        if refusal is None:
            perform(ops[-1])
        else:
            key = refusal[0]
            with self.assertRaises((SchemaConflictError, ValueError, RuntimeError)) as caught:
                perform(ops[-1])
            if ops[-1][0] == "attach":
                self.assertIn(key, str(caught.exception))
                for path, before in state["attached"]:
                    self.assertEqual(path.read_bytes(), before)
            else:
                self.assertIsInstance(caught.exception, SchemaConflictError)
                self.assertEqual(caught.exception.record_key, key)
                self.assertIn(key, str(caught.exception))
        cache = state["cache"]
        got_held = sorted(
            [LABEL_OF[type], key, value["text"]]
            for type, values in cache.snapshot().items()
            for key, value in values.items()
        )
        self.assertEqual(got_held, held)
        path = state["path"]
        if file is None:
            self.assertIsNone(path)
        else:
            self.assertEqual(file_records(path), file)
        self.assertEqual([record.key for record in cache.foreign_records()], foreign)
        if kept:
            vector = tmp / "vector-before.cc"
            vector.write_bytes(state["before"])
            for key in kept:
                self.assertIsNotNone(stored(vector, key))
                self.assertEqual(stored(path, key), stored(vector, key))
