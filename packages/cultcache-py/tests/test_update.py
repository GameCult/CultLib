"""CultCache.update (variants:cut_spec:cut-update-with-retry-py): read the record as its home store holds it, apply a function,
commit only if the record is unchanged, run the function again on a lost race. Mirrors C# UpdateTests and the TS update tests."""

from __future__ import annotations

import shutil
import tempfile
import threading
import time
import unittest
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

from cultcache_py import (
    CultCache,
    CultCacheEnvelope,
    JsonLinesBackingStore,
    SchemaConflictError,
    SingleFileMessagePackBackingStore,
    UpdateOutcome,
    UpdateResult,
    database_entry_field,
    define_database_entry_type,
    define_document_type,
    leaves as t,
)
from cultcache_py.cache import GLOBAL_KEY, CultCacheError
from cultcache_py.stores import _parse_stored_at, mint_stored_at, record_version, stored_at_over


@dataclass
class Item:
    name: str = "n"
    value: int = 0
    sku: str | None = None


def _doc(type: str, **extra):
    return define_document_type(type, encode=asdict, decode=lambda raw: Item(**raw), **extra)


item_doc = _doc("item")
other_doc = _doc("other")
sku_doc = _doc("skuitem", indexes={"sku": "sku"})
global_doc = _doc("gitem", global_document=True)

@dataclass
class Wide:
    name: str = ""
    extra: str = ""


@dataclass
class Narrow:
    name: str = ""


ALPHA = "2099-01-01T00:00:00.0000000Z"


class Fixture(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.path = self.dir / "state.cc"

    def cache(self, *documents, path: Path | None = None, store=True) -> CultCache:
        builder = CultCache.builder()
        for document in documents or (item_doc,):
            builder.register_document_type(document)
        if store:
            builder.add_generic_store(SingleFileMessagePackBackingStore(path or self.path))
        cache = builder.build()
        if store:
            cache.pull_all_backing_stores()
        return cache

    def stored(self, key: str = "k", document=item_doc):
        """What a fresh cache reads at the key."""
        return self.cache(document).get(document, key)

    def bytes(self) -> bytes | None:
        return self.path.read_bytes() if self.path.exists() else None


class Counting:
    """An apply that records what it was given."""

    def __init__(self, fn=None) -> None:
        self.seen: list = []
        self.fn = fn or (lambda item, calls: Item("n", (item.value if item else 0) + 1))

    def __call__(self, item):
        self.seen.append(None if item is None else item.value)
        # An always-losing apply must fail by assertion when the attempt bound is gone, not hang.
        assert len(self.seen) <= 10, "apply ran past the attempt bound"
        return self.fn(item, len(self.seen))

    @property
    def calls(self) -> int:
        return len(self.seen)


class UpdateTests(Fixture):
    def test_two_writers_updating_lose_no_increment(self) -> None:
        results: list[UpdateResult] = []
        calls = [0]
        lock = threading.Lock()
        a, b = self.cache(), self.cache()
        a.put(item_doc, "k", Item("n", 0))

        def bump(item):
            with lock:
                calls[0] += 1
            time.sleep(0)
            return Item("n", item.value + 1)

        def run(cache) -> None:
            for _ in range(50):
                results.append(cache.update(item_doc, "k", bump, max_attempts=200))

        threads = [threading.Thread(target=run, args=(cache,)) for cache in (a, b)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        self.assertTrue(all(result.outcome is UpdateOutcome.UPDATED for result in results))
        self.assertEqual(self.stored().value, 100)
        self.assertGreater(calls[0], 100)

    def test_a_lost_attempt_reruns_apply_on_the_fresh_value(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()
        apply = Counting(lambda item, calls: (rival.put(item_doc, "k", Item("n", 100)) if calls == 1 else None) or Item("n", item.value + 1))
        result = cache.update(item_doc, "k", apply)
        self.assertEqual(apply.seen, [1, 100])
        self.assertIs(result.outcome, UpdateOutcome.UPDATED)
        self.assertEqual(result.document.value, 101)
        self.assertEqual(self.stored().value, 101)

    def test_exhausted_attempts_return_lost_race(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()
        apply = Counting(lambda item, calls: rival.put(item_doc, "k", Item("n", 100 * calls)) or Item("n", -1))
        result = cache.update(item_doc, "k", apply, max_attempts=3)
        self.assertEqual(apply.calls, 3)
        self.assertIs(result.outcome, UpdateOutcome.LOST_RACE)
        self.assertEqual(result.document.value, 300)  # the pull before LOST_RACE shows the winner
        self.assertEqual(self.stored().value, 300)

    def test_max_attempts_bounds(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()

        def always_losing(item, calls):
            rival.put(item_doc, "k", Item("n", 100 + calls))
            return Item("n", -1)

        apply = Counting(always_losing)
        result = cache.update(item_doc, "k", apply, max_attempts=1)
        self.assertEqual((apply.calls, result.outcome), (1, UpdateOutcome.LOST_RACE))
        for bad, error in ((0, ValueError), (-1, ValueError), (True, TypeError), (1.5, TypeError), (None, TypeError)):
            with self.subTest(max_attempts=bad):
                untouched = Counting()
                with self.assertRaises(error):
                    cache.update(item_doc, "k", untouched, max_attempts=bad)
                self.assertEqual(untouched.calls, 0)
        with self.assertRaises(TypeError):
            cache.update(item_doc, "k", "not callable")
        with self.assertRaises(TypeError):
            cache.update(item_doc, "k", None)

    def test_apply_gets_a_fresh_copy(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()
        arguments: list[Item] = []

        def apply(item):
            arguments.append(item)
            self.assertIsNot(item, cache.get(item_doc, "k"))
            if len(arguments) == 1:
                item.value = 777  # changing the copy in place changes nothing the cache or the next attempt sees
                rival.put(item_doc, "k", Item("n", 5))
                self.assertEqual(cache.get(item_doc, "k").value, 1)
            return Item("n", item.value + 1)

        result = cache.update(item_doc, "k", apply)
        self.assertEqual([item.value for item in arguments], [777, 5])
        self.assertIsNot(arguments[0], arguments[1])
        self.assertEqual(result.document.value, 6)

    def test_an_in_place_change_to_the_held_value_is_neither_applied_nor_persisted(self) -> None:
        cache = self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        cache.get(item_doc, "k").value = 999
        apply = Counting(lambda item, calls: Item("n", item.value + 1))
        result = cache.update(item_doc, "k", apply)
        self.assertEqual(apply.seen, [1])
        self.assertEqual(self.stored().value, 2)
        result.document.value = 999
        cache.update(item_doc, "k", apply)
        self.assertEqual(apply.seen, [1, 2])
        self.assertEqual(self.stored().value, 3)

        bare = self.cache(store=False)  # a cache with no store copies its held instance
        bare.put(item_doc, "k", Item("n", 1))
        bare.get(item_doc, "k").value = 999
        seen = Counting(lambda item, calls: Item("n", item.value + 1))
        self.assertEqual(bare.update(item_doc, "k", seen).document.value, 2)
        self.assertEqual(seen.seen, [1])

    def test_none_declines(self) -> None:
        cache = self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        before = self.bytes()
        result = cache.update(item_doc, "k", lambda item: None)
        self.assertIs(result.outcome, UpdateOutcome.DECLINED)
        self.assertEqual(result.document, cache.get(item_doc, "k"))
        self.assertEqual(self.bytes(), before)
        absent = cache.update(item_doc, "missing", lambda item: None)
        self.assertEqual((absent.outcome, absent.document), (UpdateOutcome.DECLINED, None))
        self.assertEqual(self.bytes(), before)
        empty = Fixture.cache(self, path=self.dir / "empty.cc")
        self.assertEqual(empty.update(item_doc, "k", lambda item: None).outcome, UpdateOutcome.DECLINED)
        self.assertFalse((self.dir / "empty.cc").exists())

    def test_a_declined_or_lost_attempt_installs_nothing(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()
        rival.put(item_doc, "k", Item("n", 5))
        self.assertIs(cache.update(item_doc, "k", lambda item: None).outcome, UpdateOutcome.DECLINED)
        self.assertEqual(cache.get(item_doc, "k").value, 1)
        cache.pull_all_backing_stores()
        self.assertEqual(cache.get(item_doc, "k").value, 5)

        during: list[int] = []

        def apply(item, calls):
            during.append(cache.get(item_doc, "k").value)
            if calls == 1:
                rival.put(item_doc, "k", Item("n", 9))
            return Item("n", item.value + 1)

        cache.update(item_doc, "k", Counting(apply))
        self.assertEqual(during, [5, 5])  # the lost attempt published nothing mid-update

    def test_absent_key_rival_removal_and_a_store_that_holds_nothing(self) -> None:
        a, b = self.cache(), self.cache()
        bump = lambda item: Item("n", (item.value if item else 0) + 1)  # noqa: E731
        self.assertIs(a.update(item_doc, "k", bump).outcome, UpdateOutcome.UPDATED)
        self.assertIs(b.update(item_doc, "k", bump).outcome, UpdateOutcome.UPDATED)
        self.assertEqual(self.stored().value, 2)

        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "d", Item("n", 1))
        rival.pull_all_backing_stores()
        apply = Counting(lambda item, calls: (rival.delete(item_doc, "d") if calls == 1 else None) or Item("n", (item.value if item else 0) + 1))
        cache.update(item_doc, "d", apply)
        self.assertEqual(apply.seen, [1, None])
        self.assertEqual(self.stored("d").value, 1)

        holder = self.cache()
        holder.put(item_doc, "h", Item("n", 3))
        rival.pull_all_backing_stores()
        rival.delete(item_doc, "h")
        nothing = Counting(lambda item, calls: Item("n", 1))
        holder.update(item_doc, "h", nothing)
        self.assertEqual(nothing.seen, [None])

    def test_a_rival_writing_and_restoring_the_value_still_reruns_apply(self) -> None:
        cache, rival = self.cache(), self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        rival.pull_all_backing_stores()

        def apply(item, calls):
            if calls == 1:
                rival.put(item_doc, "k", Item("n", 5))
                rival.put(item_doc, "k", Item("n", 1))
            return Item("n", item.value + 1)

        counting = Counting(apply)
        cache.update(item_doc, "k", counting)
        self.assertEqual(counting.seen, [1, 1])
        self.assertEqual(self.stored().value, 2)

    def test_a_stale_caches_first_attempt_reads_the_store(self) -> None:
        a, b = self.cache(), self.cache()
        a.put(item_doc, "k", Item("n", 1))
        b.pull_all_backing_stores()
        a.put(item_doc, "k", Item("n", 7))
        apply = Counting(lambda item, calls: Item("n", item.value + 1))
        b.update(item_doc, "k", apply)
        self.assertEqual(apply.seen, [7])
        self.assertEqual(self.stored().value, 8)


class UpdateRefusalTests(Fixture):
    def refused(self, cache: CultCache, key: str = "k", document=item_doc, *errors) -> None:
        before = self.bytes()
        apply = Counting()
        with self.assertRaises(errors or (CultCacheError, SchemaConflictError)):
            cache.update(document, key, apply)
        self.assertEqual(apply.calls, 0)
        self.assertEqual(self.bytes(), before)

    def test_refusals_before_apply(self) -> None:
        writer = self.cache(item_doc, other_doc)
        writer.put(other_doc, "k", Item("n", 1))
        self.refused(writer)  # the record at the key is another document's, held
        self.refused(self.fresh(item_doc, other_doc))  # unpulled: the store's record is another document's
        self.refused(self.cache(item_doc))  # foreign: the store holds a type this cache never registered
        self.refused(self.fresh(item_doc))
        storeless = self.cache(item_doc, other_doc, store=False)
        storeless.put(other_doc, "k", Item("n", 1))
        apply = Counting()
        with self.assertRaises((CultCacheError, SchemaConflictError)):
            storeless.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 0)

    def fresh(self, *documents) -> CultCache:
        """A cache that has not pulled: whatever it reads, it reads from the store at update."""
        builder = CultCache.builder()
        for document in documents:
            builder.register_document_type(document)
        return builder.add_generic_store(SingleFileMessagePackBackingStore(self.path)).build()

    def test_a_home_without_read_and_commit_if_refuses(self) -> None:
        jsonl = JsonLinesBackingStore(self.dir / "state.jsonl")
        cache = CultCache.builder().register_document_type(item_doc).add_generic_store(jsonl).build()
        cache.pull_all_backing_stores()
        cache.put(item_doc, "k", Item("n", 1))
        before = (self.dir / "state.jsonl").read_bytes()
        apply = Counting()
        with self.assertRaisesRegex(CultCacheError, "cannot write conditionally"):
            cache.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 0)
        self.assertEqual((self.dir / "state.jsonl").read_bytes(), before)
        self.assertFalse(hasattr(JsonLinesBackingStore, "read") or hasattr(JsonLinesBackingStore, "commit_if"))

        class Bare:
            def pull_all(self):
                return []

            def push(self, envelope):
                raise AssertionError("update wrote through a store that cannot write conditionally")

            def push_all(self, envelopes, soft=False):
                raise AssertionError("push_all")

            def delete(self, type, key):
                raise AssertionError("delete")

        bare = CultCache.builder().register_document_type(item_doc).add_generic_store(Bare()).build()
        with self.assertRaisesRegex(CultCacheError, "Bare"):
            bare.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 0)

    def test_read_only_records_refuse_before_apply(self) -> None:
        wide = define_database_entry_type(
            "tests.rw",
            [database_entry_field("name", 0, codec=t.string), database_entry_field("extra", 1, codec=t.string)],
            cls=Wide,
            schema_id="tests.rw.v1",
        )
        narrow = define_database_entry_type("tests.rw", [database_entry_field("name", 0, codec=t.string)], cls=Narrow, schema_id="tests.rw.v1")
        self.cache(wide).put(wide, "k", Wide("n", "x"))
        before = self.bytes()
        for pulled in (True, False):  # held read-only, then a store record the cache never loaded
            with self.subTest(pulled=pulled):
                cache = self.cache(narrow) if pulled else self.fresh(narrow)
                apply = Counting()
                with self.assertRaises(SchemaConflictError) as caught:
                    cache.update(narrow, "k", apply)
                self.assertEqual(caught.exception.record_key, "k")
                self.assertEqual(apply.calls, 0)
                self.assertEqual(self.bytes(), before)

    def test_a_non_global_key_for_a_global_document_refuses(self) -> None:
        cache = self.cache(global_doc)
        apply = Counting()
        with self.assertRaises(CultCacheError):
            cache.update(global_doc, "not-global", apply)
        self.assertEqual(apply.calls, 0)

    def test_refusals_after_apply_are_not_retried(self) -> None:
        cache = self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        before = self.bytes()
        boom = Counting(lambda item, calls: (_ for _ in ()).throw(RuntimeError("boom")))
        with self.assertRaisesRegex(RuntimeError, "boom"):
            cache.update(item_doc, "k", boom)
        self.assertEqual(boom.calls, 1)
        self.assertEqual(self.bytes(), before)

        indexed = self.cache(sku_doc, path=self.dir / "sku.cc")
        indexed.put(sku_doc, "a", Item("n", 1, "X"))
        indexed.put(sku_doc, "b", Item("n", 1, "Y"))
        before = (self.dir / "sku.cc").read_bytes()
        clash = Counting(lambda item, calls: Item("n", 2, "X"))
        with self.assertRaisesRegex(CultCacheError, "unique index"):
            indexed.update(sku_doc, "b", clash)
        self.assertEqual(clash.calls, 1)
        self.assertEqual((self.dir / "sku.cc").read_bytes(), before)


class UpdateOwnWritesTests(Fixture):
    def test_own_writes_and_the_held_instance(self) -> None:
        cache = self.cache()
        cache.put(item_doc, "k", Item("n", 1))
        apply = Counting(lambda item, calls: Item("n", item.value + 1))
        cache.update(item_doc, "k", apply)
        second = cache.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 2)
        self.assertIs(second.document, cache.get(item_doc, "k"))
        self.assertEqual(second.document.value, 3)

        def sideways(item, calls):
            cache.put(item_doc, "side", Item("n", 5))  # apply runs outside the cache's hold
            return Item("n", item.value + 1)

        sideways_apply = Counting(sideways)
        result = cache.update(item_doc, "k", sideways_apply)
        self.assertEqual((result.outcome, sideways_apply.calls), (UpdateOutcome.UPDATED, 1))
        self.assertEqual(self.stored("side").value, 5)

    def test_store_less_cache(self) -> None:
        cache = self.cache(store=False)
        cache.put(item_doc, "k", Item("n", 0))
        results: list[UpdateResult] = []

        def bump(item):
            time.sleep(0)
            return Item("n", item.value + 1)

        def run() -> None:
            for _ in range(50):
                results.append(cache.update(item_doc, "k", bump, max_attempts=500))

        threads = [threading.Thread(target=run) for _ in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        self.assertTrue(all(result.outcome is UpdateOutcome.UPDATED for result in results))
        self.assertEqual(cache.get(item_doc, "k").value, 100)

        # A store that becomes the type's home while apply runs holds a record the cache never read: the commit loses.
        cache = self.cache(store=False)  # holds no record of the type, so a store may become its home
        other = self.cache(path=self.dir / "late.cc")
        other.put(item_doc, "fresh", Item("n", 40))
        late = SingleFileMessagePackBackingStore(self.dir / "late.cc")

        def attach(item, calls):
            if calls == 1:
                cache.add_generic_store(late)
            return Item("n", (item.value if item else 0) + 1)

        apply = Counting(attach)
        result = cache.update(item_doc, "fresh", apply)
        self.assertEqual(apply.seen, [None, 40])
        self.assertIs(result.outcome, UpdateOutcome.UPDATED)
        self.assertEqual(self.cache(path=self.dir / "late.cc").get(item_doc, "fresh").value, 41)

    def test_a_home_attached_while_apply_runs_is_reread_or_refused(self) -> None:
        """The cache's home for the type changes between the read and the commit (attaching is allowed while it holds no record
        of the type): the version read from the old home is no condition on the new one."""
        cache = self.cache(path=self.dir / "generic.cc")
        late = SingleFileMessagePackBackingStore(self.dir / "late.cc")

        def attach(item, calls):
            if calls == 1:
                cache.add_backing_store(late, ["item"])
            return Item("n", 1)

        apply = Counting(attach)
        self.assertIs(cache.update(item_doc, "k", apply).outcome, UpdateOutcome.UPDATED)
        self.assertEqual(apply.seen, [None, None])  # reread from the new home, not committed with the old home's version
        self.assertEqual(self.cache(path=self.dir / "late.cc").get(item_doc, "k").value, 1)

        cache = self.cache(path=self.dir / "generic2.cc")
        jsonl = JsonLinesBackingStore(self.dir / "late.jsonl")

        def attach_jsonl(item, calls):
            cache.add_backing_store(jsonl, ["item"])
            return Item("n", 1)

        apply = Counting(attach_jsonl)
        with self.assertRaisesRegex(CultCacheError, "cannot write conditionally"):
            cache.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 1)
        self.assertFalse((self.dir / "late.jsonl").exists())

    def test_ledger_under_contention(self) -> None:
        caches = [self.cache() for _ in range(3)]
        outcomes: list[UpdateOutcome] = []

        def run(cache) -> None:
            for _ in range(25):
                outcomes.append(cache.update(item_doc, "k", lambda item: Item("n", (item.value if item else 0) + 1)).outcome)

        threads = [threading.Thread(target=run, args=(cache,)) for cache in caches]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()
        self.assertEqual(self.stored().value, outcomes.count(UpdateOutcome.UPDATED))
        self.assertEqual(len(outcomes), 75)


class UpdateLegacyGlobalTests(Fixture):
    def plant(self, key: str = "legacy") -> None:
        entry = global_doc.catalog_entry()
        SingleFileMessagePackBackingStore(self.path).push(
            CultCacheEnvelope.create(key=key, type=global_doc.type, payload=global_doc.encode_payload(Item("n", 1)), schema_id=entry.schema_id, catalog_entry=entry)
        )

    def test_update_global_updates_the_global_record(self) -> None:
        cache = self.cache(global_doc)
        result = cache.update_global(global_doc, lambda item: Item("n", (item.value if item else 0) + 1))
        self.assertIs(result.outcome, UpdateOutcome.UPDATED)
        self.assertEqual(self.cache(global_doc).get(global_doc, GLOBAL_KEY).value, 1)

    def test_a_legacy_global_is_refused_until_put_global_retires_it(self) -> None:
        self.plant()
        cache = self.cache(global_doc)  # loaded from the legacy key
        before = self.bytes()
        for call in (lambda a: cache.update_global(global_doc, a), lambda a: cache.update(global_doc, GLOBAL_KEY, a)):
            apply = Counting()
            with self.assertRaisesRegex(CultCacheError, "put_global"):
                call(apply)
            self.assertEqual(apply.calls, 0)
        self.assertEqual(self.bytes(), before)
        # Judged from the cache's load, not only from the store: the legacy record is gone from the store, the cache still holds it.
        SingleFileMessagePackBackingStore(self.path).delete(global_doc.type, "legacy")
        apply = Counting()
        with self.assertRaisesRegex(CultCacheError, "put_global"):
            cache.update_global(global_doc, apply)
        self.assertEqual(apply.calls, 0)
        cache.put_global(global_doc, Item("n", 5))
        self.assertEqual(cache.update_global(global_doc, lambda item: Item("n", item.value + 1)).document.value, 6)

    def test_a_pull_inside_apply_that_loads_a_legacy_global_fails_the_commit(self) -> None:
        cache = self.cache(global_doc)

        def apply(item):
            self.plant()
            cache.pull_all_backing_stores()
            return Item("n", 9)

        with self.assertRaisesRegex(CultCacheError, "put_global"):
            cache.update_global(global_doc, apply)
        self.assertEqual([envelope.key for envelope in SingleFileMessagePackBackingStore(self.path).pull_all()], ["legacy"])

    def test_an_unpulled_cache_judges_the_legacy_global_from_the_store(self) -> None:
        self.plant()
        before = self.bytes()
        cache = SingleFileMessagePackBackingStore(self.path)
        unpulled = CultCache.builder().register_document_type(global_doc).add_generic_store(cache).build()
        apply = Counting()
        with self.assertRaisesRegex(CultCacheError, "put_global"):
            unpulled.update_global(global_doc, apply)
        self.assertEqual(apply.calls, 0)
        self.assertEqual(self.bytes(), before)


class StoreRestampTests(Fixture):
    def envelope(self, value: int, stored_at: str = ALPHA, key: str = "k") -> CultCacheEnvelope:
        entry = item_doc.catalog_entry()
        return CultCacheEnvelope(key, "item", item_doc.encode_payload(Item("n", value)), stored_at, entry.schema_id, entry)

    def stamp_on_disk(self, store, key: str = "k") -> str:
        return next(envelope.stored_at for envelope in store.pull_all() if envelope.key == key)

    def later_than_alpha(self, text: str) -> bool:
        return _parse_stored_at(text) > _parse_stored_at(ALPHA)

    def test_commit_if_and_the_restamp_at_the_store(self) -> None:
        store = SingleFileMessagePackBackingStore(self.path)
        self.assertIsNone(store.read("k"))
        stamp = store.commit_if(self.envelope(1), None)  # None against an absent key commits
        self.assertEqual(stamp, ALPHA)
        envelope, version = store.read("k")
        self.assertEqual(version, record_version(envelope))
        before = self.bytes()
        self.assertIsNone(store.commit_if(self.envelope(2, "2100-01-01T00:00:00Z"), None))  # a record is there: not absent
        self.assertIsNone(store.commit_if(self.envelope(2, "2100-01-01T00:00:00Z"), "0" * 64))
        self.assertEqual(self.bytes(), before)
        written = store.commit_if(self.envelope(2), version)  # a staged stamp that is no later is minted over
        self.assertTrue(self.later_than_alpha(written))
        self.assertEqual(self.stamp_on_disk(store), written)
        self.assertEqual(stamp_of(store, "k"), written)

    def test_every_replacing_write_is_stored_strictly_later(self) -> None:
        for make in (SingleFileMessagePackBackingStore, JsonLinesBackingStore):
            for name in ("push", "push_all", "commit_if"):
                if name == "commit_if" and make is JsonLinesBackingStore:
                    continue
                with self.subTest(store=make.__name__, write=name):
                    path = self.dir / f"{make.__name__}-{name}"
                    store = make(path)
                    store.push(self.envelope(1))
                    if name == "push":
                        reported = store.push(self.envelope(2))
                    elif name == "push_all":
                        reported = store.push_all([self.envelope(2)])["k"]
                    else:
                        reported = store.commit_if(self.envelope(2), store.read("k")[1])
                    self.assertTrue(self.later_than_alpha(reported))
                    self.assertEqual(self.stamp_on_disk(store), reported)

    def test_the_cache_holds_the_stamp_the_store_wrote(self) -> None:
        for name in ("put", "put_envelope", "put_envelopes", "update"):
            with self.subTest(write=name):
                path = self.dir / name
                SingleFileMessagePackBackingStore(path).push(self.envelope(1))
                cache = self.cache(path=path)
                self.assertEqual(cache.get_envelope(item_doc, "k").stored_at, ALPHA)
                if name == "put":
                    cache.put(item_doc, "k", Item("n", 2))
                elif name == "put_envelope":
                    cache.put_envelope(item_doc, self.envelope(2))
                elif name == "put_envelopes":
                    cache.put_envelopes(item_doc, [self.envelope(2)])
                else:
                    cache.update(item_doc, "k", lambda item: Item("n", 2))
                held = cache.get_envelope(item_doc, "k").stored_at
                self.assertTrue(self.later_than_alpha(held))
                self.assertEqual(held, self.stamp_on_disk(SingleFileMessagePackBackingStore(path)))

    def test_a_store_less_cache_stamps_a_replacing_write_later(self) -> None:
        cache = self.cache(store=False)
        cache.put_envelope(item_doc, self.envelope(1))
        cache.put_envelope(item_doc, self.envelope(2, "2000-01-01T00:00:00Z"))
        self.assertTrue(self.later_than_alpha(cache.get_envelope(item_doc, "k").stored_at))

    def test_only_the_stamp_shape_the_runtimes_write_parses(self) -> None:
        """An RFC 3339 date-time with T, a fraction of 1 to 9 digits and Z or a numeric offset, on every interpreter (the shape is
        checked here, not by fromisoformat), so a stamp reads the same on 3.10 and 3.13. The edges follow RFC 3339: offset hours 00-23,
        minutes 00-59."""
        utc = timezone.utc
        accepted = {
            "2026-01-01T00:00:00Z": datetime(2026, 1, 1, tzinfo=utc),
            "2026-01-01T00:00:00.1Z": datetime(2026, 1, 1, 0, 0, 0, 100000, utc),
            "2026-01-01T00:00:00.123456789Z": datetime(2026, 1, 1, 0, 0, 0, 123456, utc),
            "2026-01-01T00:00:00.1234567+00:00": datetime(2026, 1, 1, 0, 0, 0, 123456, utc),
            "2026-01-01T05:30:00+05:30": datetime(2026, 1, 1, tzinfo=utc),
            "2025-12-31T19:00:00-05:00": datetime(2026, 1, 1, tzinfo=utc),
            "2026-01-01T23:59:00+23:59": datetime(2026, 1, 1, 0, 0, tzinfo=utc),
        }
        for text, instant in accepted.items():
            with self.subTest(text=text):
                self.assertEqual(_parse_stored_at(text), instant)
        for text in (
            "2026-01-01", "20260101T000000Z", "2026-W01-1T00:00:00Z", "2026-01-01 00:00:00Z", "2026-01-01T00:00:00,5Z",
            "2026-01-01T00Z", "2026-01-01T00:00:00", "2026-01-01T00:00:00.Z", "2026-01-01T00:00:00.1234567890Z",
            "2026-01-01T00:00:00z", " 2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z ", "2026-01-01T24:00:00Z",
            "2026-13-01T00:00:00Z", "2026-01-01T00:00:00+24:00", "2026-01-01T00:00:00+05:60", "2026-01-01T00:00:00+05:75", "2026-01-01T00:00Z", "2026-01-01T00:00+00:00",
            "026-01-01T00:00:00Z", "12026-01-01T00:00:00Z", "2026-01-01T00:00:00+5:00", "2026-01-01T00:00:00+0000", "\u0662026-01-01T00:00:00Z",
            "", "garbage",
        ):
            with self.subTest(text=text):
                self.assertIsNone(_parse_stored_at(text))

    def test_a_stamp_outside_the_range_is_unparseable_and_one_at_the_edge_refuses_a_later_stamp(self) -> None:
        for text in ("9999-12-31T23:30:00-05:00", "0001-01-01T00:00:00+05:00"):
            with self.subTest(text=text):
                self.assertIsNone(_parse_stored_at(text))
        now = datetime(2030, 1, 1, tzinfo=timezone.utc)
        self.assertEqual(mint_stored_at("9999-12-31T23:30:00-05:00", now), "2030-01-01T00:00:00.000000+00:00")
        edge = "9999-12-31T23:59:59.9999999Z"
        with self.assertRaisesRegex(ValueError, "last instant"):
            mint_stored_at(edge, now)
        with self.assertRaisesRegex(ValueError, "last instant"):
            stored_at_over("2000-01-01T00:00:00Z", edge)
        # A write over a record stamped at the edge is refused by the store and writes nothing, from push, commit_if and update.
        store = SingleFileMessagePackBackingStore(self.path)
        entry = item_doc.catalog_entry()
        record = lambda value, at: CultCacheEnvelope("k", "item", item_doc.encode_payload(Item("n", value)), at, entry.schema_id, entry)  # noqa: E731
        store.push(record(1, edge))
        before = self.bytes()
        version = store.read("k")[1]
        for write in (lambda: store.push(record(2, ALPHA)), lambda: store.push_all([record(2, ALPHA)]), lambda: store.commit_if(record(2, ALPHA), version)):
            with self.assertRaises(ValueError):
                write()
        cache = self.cache()
        apply = Counting()
        with self.assertRaises(ValueError):
            cache.update(item_doc, "k", apply)
        self.assertEqual(apply.calls, 1)
        self.assertEqual(self.bytes(), before)
        self.assertEqual(cache.get_envelope(item_doc, "k").stored_at, edge)

    def test_stamp_parsing_and_install(self) -> None:
        for text in ("2026-01-01T00:00:00.1234567+00:00", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00.123456+00:00"):
            with self.subTest(text=text):
                self.assertIsNotNone(_parse_stored_at(text))
                self.assertEqual(_parse_stored_at(text).tzinfo, timezone.utc)
        self.assertIsNone(_parse_stored_at("garbage"))
        self.assertIsNone(_parse_stored_at(None))
        now = datetime(2030, 1, 1, tzinfo=timezone.utc)
        self.assertEqual(mint_stored_at("garbage", now), "2030-01-01T00:00:00.000000+00:00")
        self.assertEqual(stored_at_over("2000-01-01T00:00:00Z", "2000-01-01T00:00:00Z")[:4], str(datetime.now(timezone.utc).year))  # no later: minted at now
        self.assertEqual(mint_stored_at("2030-01-01T00:00:00Z", now), "2030-01-01T00:00:00.000001+00:00")  # equal is not later
        self.assertEqual(mint_stored_at("2099-01-01T00:00:00.1234567Z", now), "2099-01-01T00:00:00.123457+00:00")
        self.assertEqual(stored_at_over("2000-01-01T00:00:00Z", "2099-01-01T00:00:00.1234567Z"), "2099-01-01T00:00:00.123457+00:00")

        for reported in (True, 1, "garbage"):
            with self.subTest(reported=reported):
                class Lying(SingleFileMessagePackBackingStore):
                    def push(self, envelope):
                        super().push(envelope)
                        return reported

                cache = CultCache.builder().register_document_type(item_doc).add_generic_store(Lying(self.dir / f"lying-{reported}")).build()
                cache.pull_all_backing_stores()
                cache.put_envelope(item_doc, self.envelope(1, "2026-02-02T00:00:00.000000+00:00"))
                self.assertEqual(cache.get_envelope(item_doc, "k").stored_at, "2026-02-02T00:00:00.000000+00:00")


def stamp_of(store, key: str) -> str:
    return store.read(key)[0].stored_at


if __name__ == "__main__":
    unittest.main()
