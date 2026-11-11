from __future__ import annotations

import itertools
import shutil
import tempfile
import unittest
from dataclasses import asdict, dataclass
from decimal import Decimal
from pathlib import Path

import msgpack

from cultcache_py import (
    CultCache,
    CultCacheEnvelope,
    SingleFileMessagePackBackingStore,
    define_database_entry_type,
    define_document_type,
    leaves as t,
    database_entry_field,
)
from cultcache_py.cache import CultCacheError, _index_text

# variants:ruling:unique-indexes. `indexes` are unique within one type, `roles` are selection aliases. The vectors in
# tests/vectors/unique-index are written by the C# reference; the rest of the behaviour is pinned here.
VECTORS = Path(__file__).resolve().parents[3] / "tests" / "vectors" / "unique-index"


@dataclass
class Item:
    sku: object = None
    serial: object = None
    group: object = None


def refusal(key_a: str, key_b: str, schema_name: str, alias: str) -> str:
    return f"Records {key_a} and {key_b} ({schema_name}) both hold unique index '{alias}'; an index value names one record."


def item_doc(type: str = "item", *, indexes=None, roles=None):
    return define_document_type(
        type,
        encode=asdict,
        decode=lambda raw: Item(**raw),
        indexes={"sku": "sku", "serial": "serial"} if indexes is None else indexes,
        roles={"group": "group"} if roles is None else roles,
    )


def envelope(document, key: str, item: Item) -> CultCacheEnvelope:
    return CultCacheEnvelope.create(key=key, type=document.type, payload=document.encode_payload(item))


def cache_of(*documents, store=None) -> CultCache:
    builder = CultCache.builder()
    for document in documents:
        builder.register_document_type(document)
    if store is not None:
        builder.add_generic_store(store)
    return builder.build()


class RecordingStore:
    """A store that counts what reaches it; the judge must refuse before any store call."""

    def __init__(self) -> None:
        self.pushed: list[str] = []

    def pull_all(self):
        return []

    def push(self, envelope) -> None:
        self.pushed.append(envelope.key)

    def push_all(self, envelopes) -> None:
        self.pushed.extend(envelope.key for envelope in envelopes)

    def delete(self, type, key) -> None:
        pass


class IndexTextTests(unittest.TestCase):
    def test_canonical_text(self) -> None:
        cases = [
            ("S-1", "S-1"),
            (" a ", " a "),
            ("﻿", "﻿"),
            ("\x1f", "\x1f"),
            ("\x1c", "\x1c"),
            ("\x1d", "\x1d"),
            ("\x1e", "\x1e"),
            ("᠎", "᠎"),
            ("​", "​"),
            (True, "True"),
            (False, "False"),
            (7, "7"),
            (-7, "-7"),
            (9223372036854775807, "9223372036854775807"),
            (18446744073709551615, "18446744073709551615"),
            (0.0, "0"),
            (-0.0, "0"),
            (2.0, "2"),
            (-3.0, "-3"),
            (1e21, "1000000000000000000000"),
            (0.1, "0.1000000000000000055511151231257827021181583404541015625"),
            (-0.5, "-0.5"),
            (2.5e-07, "0.00000024999999999999998868702795647156467140348468092270195484161376953125"),
        ]
        for value, text in cases:
            with self.subTest(value=value):
                self.assertEqual(_index_text(value), text)
                self.assertIsInstance(_index_text(value), str)

    def test_no_value(self) -> None:
        blank = ["", " ", "\t", "\n", "\x0b", "\x0c", "\r", "\x85", "\xa0", " ", " ", " ", " ",
                 " ", "　", " 　\x85 ", *map(chr, range(0x2000, 0x200B))]
        for value in [None, float("nan"), float("inf"), float("-inf"), *blank]:
            with self.subTest(value=repr(value)):
                self.assertIsNone(_index_text(value))

    def test_blank_set_is_exactly_the_csharp_set(self) -> None:
        csharp = {*range(0x09, 0x0E), 0x20, 0x85, 0xA0, 0x1680, *range(0x2000, 0x200B), 0x2028, 0x2029, 0x202F, 0x205F, 0x3000}
        self.assertEqual(len(csharp), 25)
        got = {code for code in range(0x110000) if 0xD800 > code or code > 0xDFFF if _index_text(chr(code)) is None}
        self.assertEqual(got, csharp)
        # Python's isspace is the wrong test: it also counts U+001C-001F.
        self.assertTrue("\x1f".isspace())
        self.assertEqual(_index_text("\x1f"), "\x1f")

    def test_floats_render_exactly_not_through_str(self) -> None:
        for value in (0.1, 1e-7, 123456789.123456789, 5e-324, 1.7976931348623157e308):
            with self.subTest(value=value):
                text = _index_text(value)
                self.assertNotIn("e", text.lower())
                self.assertEqual(Decimal(text), Decimal(value))
                self.assertFalse(text.endswith("0") and "." in text)

    def test_bool_is_not_an_integer(self) -> None:
        self.assertEqual(_index_text(True), "True")
        self.assertNotEqual(_index_text(True), "1")


class UniqueIndexTests(unittest.TestCase):
    def test_a_write_of_a_held_value_at_another_key_is_refused_by_every_write_path(self) -> None:
        for alias in ("sku", "serial"):  # two unique indexes: each is judged
            other = "serial" if alias == "sku" else "sku"
            paths = {
                "put": lambda cache, d, k, i: cache.put(d, k, i),
                "put_envelope": lambda cache, d, k, i: cache.put_envelope(d, envelope(d, k, i)),
                "put_envelopes": lambda cache, d, k, i: cache.put_envelopes(d, [envelope(d, k, i)]),
                "update": lambda cache, d, k, i: cache.update(d, k, lambda _held: i),
            }
            for name, write in paths.items():
                with self.subTest(alias=alias, path=name):
                    document = item_doc()
                    store = RecordingStore()
                    cache = cache_of(document, store=store)
                    cache.put(document, "item:b", Item(sku="CANARY-b", serial="canary-b"))
                    cache.put(document, "item:a", Item(sku="CANARY-a", serial="canary-a"))
                    before = cache.snapshot()
                    pushed = list(store.pushed)
                    taken = "CANARY-b" if alias == "sku" else "canary-b"
                    landing = Item(**{alias: taken, other: "fresh"})
                    with self.assertRaises(CultCacheError) as caught:
                        write(cache, document, "item:a", landing)
                    self.assertEqual(str(caught.exception), refusal("item:a", "item:b", "item", alias))
                    self.assertNotIn("anary", str(caught.exception))
                    self.assertEqual(cache.snapshot(), before)
                    self.assertEqual(store.pushed, pushed)
                    self.assertEqual(cache.get_key_by_index(document, alias, taken), "item:b")
                    self.assertIsNone(cache.get_key_by_index(document, other, "fresh"))

    def test_a_record_rewriting_its_own_key_keeps_its_value(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku="S", serial="1", group="g"))
        cache.put(document, "item:a", Item(sku="S", serial="2", group="h"))
        self.assertEqual(cache.get_key_by_index(document, "sku", "S"), "item:a")
        self.assertIsNone(cache.get_key_by_index(document, "serial", "1"))
        self.assertEqual(cache.get_key_by_index(document, "serial", "2"), "item:a")

    def test_the_two_smallest_keys_are_named_in_ordinal_order(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:m", Item(sku="S"))
        with self.assertRaises(CultCacheError) as caught:
            cache.put_envelopes(document, [envelope(document, "item:z", Item(sku="S")), envelope(document, "item:A", Item(sku="S"))])
        self.assertEqual(str(caught.exception), refusal("item:A", "item:m", "item", "sku"))

    def test_put_envelopes_holding_one_value_twice_is_refused_whole(self) -> None:
        document = item_doc()
        store = RecordingStore()
        cache = cache_of(document, store=store)
        with self.assertRaises(CultCacheError) as caught:
            cache.put_envelopes(
                document,
                [
                    envelope(document, "item:b", Item(sku="S")),
                    envelope(document, "item:c", Item(sku="T")),
                    envelope(document, "item:a", Item(sku="S")),
                ],
            )
        self.assertEqual(str(caught.exception), refusal("item:a", "item:b", "item", "sku"))
        self.assertEqual(cache.snapshot(), {})
        self.assertEqual(store.pushed, [])
        self.assertIsNone(cache.get_key_by_index(document, "sku", "T"))

    def test_verdict_does_not_depend_on_batch_order(self) -> None:
        document = item_doc()
        rows = [("item:a", Item(sku="S", serial="1")), ("item:b", Item(sku="T", serial="1")), ("item:c", Item(sku="U"))]
        for order in itertools.permutations(rows):
            with self.subTest(order=[key for key, _ in order]):
                cache = cache_of(document)
                with self.assertRaises(CultCacheError) as caught:
                    cache.put_envelopes(document, [envelope(document, key, item) for key, item in order])
                self.assertEqual(str(caught.exception), refusal("item:a", "item:b", "item", "serial"))
                self.assertEqual(cache.snapshot(), {})

    def test_put_envelopes_swapping_two_records_values_lands(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku="S", serial="1"))
        cache.put(document, "item:b", Item(sku="T", serial="2"))
        cache.put_envelopes(
            document,
            [envelope(document, "item:a", Item(sku="T", serial="2")), envelope(document, "item:b", Item(sku="S", serial="1"))],
        )
        self.assertEqual(cache.get_key_by_index(document, "sku", "S"), "item:b")
        self.assertEqual(cache.get_key_by_index(document, "sku", "T"), "item:a")
        self.assertEqual(cache.get_key_by_index(document, "serial", "1"), "item:b")
        self.assertEqual(cache.get_key_by_index(document, "serial", "2"), "item:a")

    def test_a_batch_may_take_a_value_the_batch_releases(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku="S"))
        cache.put_envelopes(document, [envelope(document, "item:a", Item(sku="T")), envelope(document, "item:b", Item(sku="S"))])
        self.assertEqual(cache.get_key_by_index(document, "sku", "S"), "item:b")
        self.assertEqual(cache.get_key_by_index(document, "sku", "T"), "item:a")

    def test_delete_frees_the_value(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku="S"))
        cache.delete(document, "item:a")
        self.assertIsNone(cache.get_key_by_index(document, "sku", "S"))
        cache.put(document, "item:b", Item(sku="S"))
        self.assertEqual(cache.get_key_by_index(document, "sku", "S"), "item:b")

    def test_blank_values_never_conflict(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        blanks = ["", " ", "\x85", "　", None]
        for number, blank in enumerate(blanks):
            cache.put(document, f"item:blank{number}", Item(sku=blank, serial=blank))
        cache.put_envelopes(document, [envelope(document, "item:more", Item(sku="  ")), envelope(document, "item:more2", Item(sku=""))])
        for blank in ("", " ", "\x85"):
            self.assertIsNone(cache.get_key_by_index(document, "sku", blank))
        # U+001F and U+FEFF are values.
        cache.put(document, "item:us", Item(sku="\x1f"))
        cache.put(document, "item:bom", Item(sku="﻿"))
        self.assertEqual(cache.get_key_by_index(document, "sku", "\x1f"), "item:us")
        self.assertEqual(cache.get_key_by_index(document, "sku", "﻿"), "item:bom")
        with self.assertRaises(CultCacheError):
            cache.put(document, "item:us2", Item(sku="\x1f"))

    def test_values_compare_as_the_canonical_text(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku=1))
        for same in (1, 1.0, "1"):
            with self.subTest(same=same), self.assertRaises(CultCacheError):
                cache.put(document, "item:b", Item(sku=same))
        cache.put(document, "item:true", Item(sku=True))
        self.assertEqual(cache.get_key_by_index(document, "sku", "True"), "item:true")
        self.assertEqual(cache.get_key_by_index(document, "sku", "1"), "item:a")
        cache.put(document, "item:f", Item(sku=0.1))
        self.assertEqual(cache.get_key_by_index(document, "sku", "0.1000000000000000055511151231257827021181583404541015625"), "item:f")

    def test_two_types_may_share_an_alias_and_a_value(self) -> None:
        one, two = item_doc("one"), item_doc("two")
        cache = cache_of(one, two)
        cache.put(one, "k:1", Item(sku="S", serial="1"))
        cache.put(two, "k:2", Item(sku="S", serial="1"))
        self.assertEqual(cache.get_key_by_index(one, "sku", "S"), "k:1")
        self.assertEqual(cache.get_key_by_index(two, "sku", "S"), "k:2")
        cache.delete(one, "k:1")
        self.assertEqual(cache.get_key_by_index(two, "sku", "S"), "k:2")

    def test_roles_may_share_values_and_are_not_looked_up(self) -> None:
        document = item_doc()
        cache = cache_of(document)
        cache.put(document, "item:a", Item(sku="1", group="g"))
        cache.put(document, "item:b", Item(sku="2", group="g"))
        self.assertIsNone(cache.get_key_by_index(document, "group", "g"))
        self.assertIsNone(cache.get_by_index(document, "group", "g"))

    def test_an_alias_in_both_indexes_and_roles_is_refused(self) -> None:
        with self.assertRaisesRegex(ValueError, "both an index and a role"):
            define_document_type("bad", indexes={"a": "a"}, roles={"a": "a"})
        with self.assertRaisesRegex(ValueError, "both an index and a role"):
            define_database_entry_type("bad", [("a", 0)], indexes={"a": "a"}, roles={"a": "a"})
        define_document_type("fine", indexes={"a": "a"}, roles={"b": "a"})

    def test_register_index_over_held_duplicates_installs_nothing(self) -> None:
        document = item_doc(indexes={}, roles={})
        cache = cache_of(document)
        cache.put(document, "item:b", Item(sku="S", serial="1"))
        cache.put(document, "item:a", Item(sku="S", serial="2"))
        with self.assertRaises(CultCacheError) as caught:
            cache.register_index(document, "sku", "sku")
        self.assertEqual(str(caught.exception), refusal("item:a", "item:b", "item", "sku"))
        self.assertIsNone(cache.get_key_by_index(document, "sku", "S"))
        cache.register_index(document, "serial", "serial")  # an installed alias is not poisoned by the refused one
        self.assertEqual(cache.get_key_by_index(document, "serial", "2"), "item:a")
        # Nothing was installed, so a write sharing the refused alias's value is still admitted.
        cache.put(document, "item:c", Item(sku="S", serial="3"))

    def test_registration_names_an_installed_alias_a_caller_mutated_into_a_collision(self) -> None:
        document = item_doc(indexes={"sku": "sku"}, roles={})
        cache = cache_of(document)
        cache.put(document, "item:x", Item(sku="1", serial="s1"))
        cache.put(document, "item:y", Item(sku="2", serial="s2"))
        cache.get_required(document, "item:y").sku = "1"  # the cache hands out its held value
        with self.assertRaises(CultCacheError) as caught:
            cache.register_index(document, "serial", "serial")
        self.assertEqual(str(caught.exception), refusal("item:x", "item:y", "item", "sku"))

    def test_derive_names_the_ordinal_first_alias_then_value(self) -> None:
        document = item_doc(indexes={}, roles={})
        cache = cache_of(document)
        cache.put(document, "item:1", Item(sku="a", serial="p", group="x"))
        cache.put(document, "item:2", Item(sku="a", serial="p", group="x"))
        cache.put(document, "item:3", Item(sku="b", serial="q", group="y"))
        cache.put(document, "item:4", Item(sku="b", serial="q", group="y"))
        with self.assertRaises(CultCacheError) as caught:
            cache.register_index(document, "serial", "serial")
        self.assertEqual(str(caught.exception), refusal("item:1", "item:2", "item", "serial"))

    def test_a_document_in_two_unique_indexes_loads_and_registers_through_every_path(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "items.cc"
            document = item_doc()
            cache = cache_of(document, store=SingleFileMessagePackBackingStore(path))
            cache.put(document, "item:a", Item(sku="S", serial="1"))
            cache.put(document, "item:b", Item(sku="T", serial="2"))
            # a store written by a looser definition, then opened by this one
            loose = item_doc(indexes={}, roles={})
            writer = cache_of(loose, store=SingleFileMessagePackBackingStore(path))
            writer.pull_all_backing_stores()
            writer.put(loose, "item:c", Item(sku="U", serial="2"))
            before = path.read_bytes()
            reader = cache_of(document, store=SingleFileMessagePackBackingStore(path))
            with self.assertRaises(CultCacheError) as caught:
                reader.pull_all_backing_stores()
            self.assertEqual(str(caught.exception), refusal("item:b", "item:c", "item", "serial"))
            self.assertEqual(path.read_bytes(), before)
            self.assertEqual(reader.snapshot(), {})

    def test_a_refused_load_keeps_the_held_state(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "items.cc"
            loose = item_doc(indexes={}, roles={})
            writer = cache_of(loose, store=SingleFileMessagePackBackingStore(path))
            writer.put(loose, "item:a", Item(sku="S"))
            writer.put(loose, "item:b", Item(sku="S"))
            document = item_doc()
            cache = cache_of(document, store=SingleFileMessagePackBackingStore(path))
            with self.assertRaises(CultCacheError):
                cache.pull_all_backing_stores()
            self.assertIsNone(cache.get_key_by_index(document, "sku", "S"))
            self.assertEqual(cache.get_all(document), [])


class SimulationFactTests(unittest.TestCase):
    def test_simulation_fact_catalog_entry_is_unchanged_by_indexes_to_roles(self) -> None:
        import re

        from cultnet_py.cultmesh_contracts import SIMULATION_FACT_DOCUMENT_TYPE, simulation_fact_document as document

        aliases = {"shard": "shard_id", "frame": "frame", "subject": "subject_id", "claim_kind": "claim_kind"}
        self.assertEqual(document.roles, aliases)
        self.assertEqual(document.indexes, {})
        entry = document.catalog_entry()
        snake = lambda name: re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()  # noqa: E731
        twin = define_database_entry_type(
            SIMULATION_FACT_DOCUMENT_TYPE,
            [(snake(m.member_name), m.slot) for m in entry.members],
            name="fact_id",
            indexes=aliases,
            schema_name=entry.schema_name,
            schema_version=entry.schema_version,
        )
        self.assertEqual(entry.canonical_schema_json, twin.catalog_entry().canonical_schema_json)
        self.assertEqual(entry.content_hash, twin.catalog_entry().content_hash)
        self.assertEqual(entry.members, twin.catalog_entry().members)
        self.assertEqual({m.index_alias for m in entry.members if m.index_alias}, set(aliases))

    def test_facts_sharing_every_role_value_are_all_admitted(self) -> None:
        from cultnet_py.cultmesh_contracts import CultMeshSimulationFact, simulation_fact_document as document

        cache = cache_of(document)
        for number in range(3):
            fact = CultMeshSimulationFact.from_candidate(
                {"shardId": "s", "shardEpoch": 1, "frame": 4, "subjectId": "x", "claimKind": "k", "claimHash": f"h{number}",
                 "witnessCount": 1, "supportWeight": 1.0, "totalWeight": 1.0, "confidence": 1.0}
            )
            cache.put(document, f"simulation:{number}", fact)
        self.assertEqual(len(cache.get_all(document)), 3)
        self.assertIsNone(cache.get_key_by_index(document, "shard", "s"))


class VectorTests(unittest.TestCase):
    def test_values_rows(self) -> None:
        document = item_doc(indexes={"sku": "sku"}, roles={})
        for label, kind, value, text in msgpack.unpackb((VECTORS / "values.msgpack").read_bytes(), raw=False):
            with self.subTest(label=label):
                self.assertEqual(_index_text(value), text)
                cache = cache_of(document)
                cache.put(document, "item:1", Item(sku=value))
                if text is None:
                    cache.put(document, "item:2", Item(sku=value))  # blank: two records commit
                    self.assertEqual(len(cache.get_all(document)), 2)
                else:
                    self.assertEqual(cache.get_key_by_index(document, "sku", text), "item:1")
                    with self.assertRaises(CultCacheError):
                        cache.put(document, "item:2", Item(sku=value))

    def test_store_verdicts(self) -> None:
        unit = define_database_entry_type
        outcomes = msgpack.unpackb((VECTORS / "outcomes.msgpack").read_bytes(), raw=False)
        self.assertEqual(len(outcomes), 3)
        for name, alias, verdict, key_a, key_b in outcomes:
            for roles_first in (False, True):  # the verdict does not depend on declaration order
                with self.subTest(file=name, alias=alias, roles_first=roles_first), tempfile.TemporaryDirectory() as tmp:
                    path = Path(tmp) / name
                    shutil.copyfile(VECTORS / name, path)
                    before = path.read_bytes()
                    fields = [database_entry_field("sku", 0, codec=t.string), database_entry_field("category", 1, codec=t.string)]
                    declared = {"indexes": {alias: "sku"} if alias else {}, "roles": {} if alias else {"Sku": "sku"}}
                    if roles_first:
                        declared = dict(reversed(list(declared.items())))
                    document = unit(
                        "tests.unique_item",
                        fields,
                        schema_id="sha256:93bb890de83069061af696b23f94fd32fb3c2dad3c91ee645e8add56085f1882",
                        schema_name="tests.unique_item",
                        **declared,
                    )
                    cache = cache_of(document, store=SingleFileMessagePackBackingStore(path))
                    if verdict == "open":
                        cache.pull_all_backing_stores()
                        self.assertGreaterEqual(len(cache.get_all(document)), 2)
                    else:
                        with self.assertRaises(CultCacheError) as caught:
                            cache.pull_all_backing_stores()
                        self.assertEqual(str(caught.exception), refusal(key_a, key_b, "tests.unique_item", alias))
                        self.assertNotIn("S-1", str(caught.exception))
                        self.assertEqual(cache.snapshot(), {})
                    self.assertEqual(path.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
