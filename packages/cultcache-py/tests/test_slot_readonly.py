from __future__ import annotations

import dataclasses
import shutil
import threading
import tempfile
import unittest
from pathlib import Path

import msgpack
from cultcache_py.cache import CultCacheError

from cultcache_py import (
    CultCache,
    CultCachePayloadDepthError,
    SlotCodec,
    SchemaConflictError,
    SingleFileMessagePackBackingStore,
    UnionArm,
    UnreadSlots,
    database_entry_field,
    define_database_entry_type,
    define_record_codec,
    leaves as t,
)
import test_union_parity as _parity
from test_union_parity import (
    VECTORS,
    _surface_value,
    _vector,
    surface_document,
    unit_outcome,
)

SLOT = "extra-slot"  # the canary: the value of every extra slot in the vectors; no refusal may carry it

# The vectors name the C# types; this runtime names a type by its codec's name, a document by its document type.
_NAMES = {"UnionSurface": surface_document.type, "SurfaceDeclaring": surface_document.type, "Denied2": "Denied"}

slot_unit_document = define_database_entry_type(
    "tests.slot_unit",
    [database_entry_field("name", 0, codec=t.string), database_entry_field("outcome", 1, codec=unit_outcome)],
    name="name",
)
# The reader of extra-slot-consent.cc: the surface document's slots under a new schema id that lists the old one compatible.
declaring_document = define_database_entry_type(
    "tests.union_surface",
    [
        database_entry_field("id", 0, codec=t.string),
        database_entry_field("version", 1, codec=t.int32),
        database_entry_field("root", 2, codec=_parity._node),
        database_entry_field("outcomes", 3, codec=t.list(_parity.outcome)),
        database_entry_field("part", 4, codec=t.nullable(_parity.item)),
        database_entry_field("crafted", 5, codec=t.nullable(_parity.crafted)),
        database_entry_field("missing", 6, codec=_parity._node),
    ],
    name="id",
    schema_id="tests.union_surface.v2",
    schema_name="tests.union_surface",
    compatible_schema_ids=("tests.union_surface.wide",),
)

# file -> (type, slots carried, slots declared, every mark at depth 0): the README table.
_PAYLOADS = {
    "extra-slot-record.msgpack": ("UnionSurface", 8, 7, True),
    "extra-slot-nested-record.msgpack": ("Composition", 3, 2, False),
    "extra-slot-arm.msgpack": ("Box", 3, 2, False),
    "extra-slot-list-element.msgpack": ("Text", 3, 2, False),
    "depth-skipped-at-limit.msgpack": ("UnionSurface", 8, 7, True),
}


def _copy_store_without_variants(source: Path, target: Path) -> None:
    """This runtime holds no variant records (a five-slot record makes a store unreadable here), so the store vector is
    walked with its variant row removed; every other byte is the C# writer's."""
    [snapshot] = list(msgpack.Unpacker(open(source, "rb"), raw=False))
    snapshot[2] = [record for record in snapshot[2] if len(record) <= 4]
    target.write_bytes(msgpack.packb(snapshot, use_bin_type=True))


def _open(path: Path, *documents) -> CultCache:
    builder = CultCache.builder()
    for document in documents:
        builder.register_document_type(document)
    cache = builder.add_generic_store(SingleFileMessagePackBackingStore(path)).build()
    cache.pull_all_backing_stores()
    return cache


def _rows():
    return msgpack.unpackb(_vector("slot-outcomes.msgpack"), raw=False)


class PayloadMarkTests(unittest.TestCase):
    def test_every_read_only_payload_decodes_and_names_its_mark(self) -> None:
        for file, (type_name, carried, declared, depth0) in _PAYLOADS.items():
            with self.subTest(file=file):
                value, mark = surface_document.decode_payload_marked(_vector(file))
                self.assertEqual(value, _surface_value())
                self.assertEqual(mark, UnreadSlots(_NAMES.get(type_name, type_name), carried, declared, 1, depth0))
                self.assertEqual(surface_document.decode_payload(_vector(file)), _surface_value())

    def test_the_outcomes_manifest_still_counts_nine_rows_and_four_read_only(self) -> None:
        rows = msgpack.unpackb(_vector("outcomes.msgpack"), raw=False)
        self.assertEqual(len(rows), 9)
        self.assertEqual([row[0] for row in rows if row[1] == "read-only"], [f for f in _PAYLOADS if f != "depth-skipped-at-limit.msgpack"])

    def test_a_clean_payload_and_a_short_one_carry_no_mark(self) -> None:
        self.assertEqual(surface_document.decode_payload_marked(_vector("union-surface.msgpack")), (_surface_value(), None))
        slots = msgpack.unpackb(_vector("union-surface.msgpack"), raw=False)
        slots[2] = [1, ["only-id"]]  # a Text arm with a missing trailing slot: defaults, no mark
        _, mark = surface_document.decode_payload_marked(msgpack.packb(slots, use_bin_type=True))
        self.assertIsNone(mark)

    def test_a_retired_slot_below_the_highest_key_does_not_mark(self) -> None:
        slots = msgpack.unpackb(_vector("union-surface.msgpack"), raw=False)
        # Image's declared slots are 0, 1, 3 (2 retired): four carried, four declared.
        image = slots[2][1][1][2]
        self.assertEqual(image[0], 5)
        image[1][2] = "retired-slot-value"
        _, mark = surface_document.decode_payload_marked(msgpack.packb(slots, use_bin_type=True))
        self.assertIsNone(mark)

    def test_the_depth_check_runs_before_the_mark(self) -> None:
        with self.assertRaises(CultCachePayloadDepthError):
            surface_document.decode_payload_marked(_vector("depth-skipped-past-limit.msgpack"))

    def test_the_unit_arm_with_extra_slots_is_read_past_and_marked(self) -> None:
        payload = msgpack.packb(["unit-arm", [2, [SLOT]]], use_bin_type=True)
        value, mark = slot_unit_document.decode_payload_marked(payload)
        self.assertEqual(value, {"name": "unit-arm", "outcome": UnionArm("Denied")})
        self.assertEqual(mark, UnreadSlots("Denied", 1, 0, 1, False))
        self.assertEqual(slot_unit_document.decode_payload_marked(msgpack.packb(["ok", [2, []]]))[1], None)

    def test_a_codec_decoded_outside_a_payload_decode_notes_nothing(self) -> None:
        self.assertEqual(unit_outcome.decode([2, [SLOT]]), UnionArm("Denied"))

    def test_the_scope_belongs_to_its_thread(self) -> None:
        def decode_elsewhere(raw):
            thread = threading.Thread(target=lambda: unit_outcome.decode([2, [SLOT]]))
            thread.start()
            thread.join()
            return raw

        outer = define_database_entry_type("tests.threaded", [database_entry_field("a", 0, codec=SlotCodec(lambda v: v, decode_elsewhere))])
        self.assertIsNone(outer.decode_payload_marked(msgpack.packb(["x"]))[1])

    def test_a_nested_payload_decode_restores_the_outer_scope(self) -> None:
        def decode_clean_inside(raw):
            surface_document.decode_payload_marked(_vector("union-surface.msgpack"))
            return raw

        inner = define_record_codec("Inner", [database_entry_field("x", 0, codec=t.string)])
        outer = define_database_entry_type(
            "tests.nested",
            [
                database_entry_field("a", 0, codec=SlotCodec(lambda v: v, decode_clean_inside)),
                database_entry_field("b", 1, codec=inner),
            ],
        )
        _, mark = outer.decode_payload_marked(msgpack.packb(["x", ["v", SLOT]]))
        self.assertEqual(mark, UnreadSlots("Inner", 2, 1, 1, False))


class StoreRowTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def _store(self, name: str) -> tuple[Path, CultCache]:
        path = self.tmp / name
        _copy_store_without_variants(VECTORS / name, path)
        documents = (declaring_document,) if name == "extra-slot-consent.cc" else (surface_document, slot_unit_document)
        return path, _open(path, *documents)

    def _document(self, store: str, key: str):
        if key == "unit-arm":
            return slot_unit_document
        return declaring_document if store == "extra-slot-consent.cc" else surface_document

    def test_every_row_is_writable_exactly_where_the_vector_says_and_refuses_with_its_mark(self) -> None:
        walked = 0
        for store, key, writable, explained, type_name, carried, declared, occurrences, flatten in _rows():
            if flatten is not None:
                continue  # a variant: this runtime holds none (deviation)
            walked += 1
            with self.subTest(store=store, key=key):
                path, cache = self._store(store)
                document = self._document(store, key)
                before = path.read_bytes()
                self.assertEqual(cache.is_writable(document, key), writable)
                held = cache.get(document, key)
                self.assertIsNotNone(held)
                if writable:
                    continue
                self.assertEqual(explained, False)
                for attempt in (
                    lambda: cache.put(document, key, held),
                    lambda: cache.delete(document, key),
                    lambda: cache.put_envelope(document, cache.get_required_envelope(document, key)),
                    lambda: cache.put_envelopes(document, [cache.get_required_envelope(document, key)]),
                    lambda: cache.update(document, key, lambda value: value),
                ):
                    with self.assertRaises(SchemaConflictError) as caught:
                        attempt()
                    refusal = caught.exception
                    self.assertEqual(
                        refusal.unread_slots,
                        UnreadSlots(_NAMES.get(type_name, type_name), carried, declared, occurrences, refusal.unread_slots.depth0_only),
                    )
                    self.assertEqual(refusal.record_key, key)
                    message = str(refusal)
                    self.assertNotIn(SLOT, message)
                    self.assertIn(refusal.unread_slots.type_name, message)
                    self.assertIn(f"{carried} slots", message)
                    self.assertIn(f"declares {declared}", message)
                    self.assertEqual(path.read_bytes(), before)
                self.assertTrue(cache.get(document, key) is held)
        self.assertEqual(walked, 7)  # eight rows, the variant not walked

    def test_a_key_no_store_holds_and_another_key_are_writable_while_a_marked_one_is_held(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        self.assertFalse(cache.is_writable(surface_document, "marked"))
        self.assertTrue(cache.is_writable(surface_document, "nothing-here"))
        cache.put(surface_document, "other", cache.get_required(surface_document, "clean"))
        self.assertEqual(cache.get(surface_document, "other"), _surface_value())

    def test_consent_lands_an_explained_mark_and_refuses_the_others(self) -> None:
        path, cache = self._store("extra-slot-consent.cc")
        self.assertTrue(cache.is_writable(declaring_document, "consented"))
        cache.put(declaring_document, "consented", cache.get_required(declaring_document, "consented"))
        [stored] = [e for e in SingleFileMessagePackBackingStore(path).pull_all() if e.key == "consented"]
        self.assertEqual(stored.schema_id, "tests.union_surface.v2")
        self.assertEqual(len(msgpack.unpackb(stored.payload, raw=False)), 7)  # the consented slot is gone
        self.assertTrue(cache.is_writable(declaring_document, "consented"))
        for key in ("over-consent", "nested"):
            self.assertFalse(cache.is_writable(declaring_document, key), key)
            with self.assertRaises(SchemaConflictError):
                cache.put(declaring_document, key, cache.get_required(declaring_document, key))

    def test_a_reload_marks_again_and_a_write_of_a_clean_envelope_over_a_marked_key_is_refused(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        envelope = cache.get_required_envelope(surface_document, "clean")
        with self.assertRaises(SchemaConflictError):
            cache.put_envelope(surface_document, type(envelope)(**{**envelope.__dict__, "key": "marked"}))
        cache.pull_all_backing_stores()
        self.assertFalse(cache.is_writable(surface_document, "marked"))

    def test_a_written_envelope_that_carries_extra_slots_is_held_read_only(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        envelope = cache.get_required_envelope(surface_document, "clean")
        carrying = type(envelope)(**{**envelope.__dict__, "key": "arrived", "payload": _vector("extra-slot-arm.msgpack")})
        cache.put_envelope(surface_document, carrying)
        self.assertFalse(cache.is_writable(surface_document, "arrived"))
        with self.assertRaises(SchemaConflictError):
            cache.delete(surface_document, "arrived")
        cache.pull_all_backing_stores()
        self.assertFalse(cache.is_writable(surface_document, "arrived"))

    def test_a_batch_of_envelopes_that_carry_extra_slots_is_held_read_only(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        envelope = cache.get_required_envelope(surface_document, "clean")
        batch = [
            type(envelope)(**{**envelope.__dict__, "key": key, "payload": _vector(file)})
            for key, file in (("b1", "extra-slot-arm.msgpack"), ("b2", "union-surface.msgpack"))
        ]
        cache.put_envelopes(surface_document, batch)
        self.assertEqual([cache.is_writable(surface_document, key) for key in ("b1", "b2")], [False, True])

    def test_a_mark_under_an_id_the_reader_does_not_list_compatible_is_not_consented(self) -> None:
        unlisted = define_database_entry_type(
            "tests.union_surface",
            [database_entry_field("id", 0, codec=t.string)] + [database_entry_field(n, i, codec=c) for n, i, c in (
                ("version", 1, t.int32), ("root", 2, _parity._node), ("outcomes", 3, t.list(_parity.outcome)),
                ("part", 4, t.nullable(_parity.item)), ("crafted", 5, t.nullable(_parity.crafted)), ("missing", 6, _parity._node))],
            name="id",
            schema_id="tests.union_surface.v2",
            schema_name="tests.union_surface",
        )
        path = self.tmp / "unlisted.cc"
        _copy_store_without_variants(VECTORS / "extra-slot-consent.cc", path)
        cache = _open(path, unlisted)
        self.assertFalse(cache.is_writable(unlisted, "consented"))

    def test_a_written_envelope_is_judged_on_the_id_it_is_stored_under_so_a_reload_agrees(self) -> None:
        path, cache = self._store("extra-slot-consent.cc")
        arrived = next(e for e in SingleFileMessagePackBackingStore(path).pull_all() if e.key == "consented")
        self.assertEqual(arrived.schema_id, "tests.union_surface.wide")
        cache.put_envelope(declaring_document, dataclasses.replace(arrived, key="via-envelope"))
        cache.put_envelopes(declaring_document, [dataclasses.replace(arrived, key="via-batch")])
        in_memory = [cache.is_writable(declaring_document, k) for k in ("via-envelope", "via-batch")]
        cache.pull_all_backing_stores()
        self.assertEqual([cache.is_writable(declaring_document, k) for k in ("via-envelope", "via-batch")], in_memory)
        self.assertEqual(in_memory, [False, False])

    def test_is_writable_agrees_with_the_write_for_a_key_a_store_carries_foreign(self) -> None:
        path = self.tmp / "foreign.cc"
        _copy_store_without_variants(VECTORS / "extra-slot-store.cc", path)
        cache = _open(path, surface_document)  # tests.slot_unit is not registered: its record is carried foreign
        self.assertEqual([r.key for r in cache.foreign_records()], ["unit-arm"])
        self.assertFalse(cache.is_writable(surface_document, "unit-arm"))
        with self.assertRaises(SchemaConflictError):
            cache.put(surface_document, "unit-arm", _surface_value())
        with self.assertRaises(SchemaConflictError):
            cache.delete(surface_document, "unit-arm")

    def test_every_key_of_a_batch_is_checked_wherever_the_marked_one_stands(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        before = path.read_bytes()
        envelope = cache.get_required_envelope(surface_document, "clean")
        fresh = dataclasses.replace(envelope, key="fresh")
        marked = dataclasses.replace(envelope, key="marked")
        for batch in ([fresh, marked], [marked, fresh], [fresh, dataclasses.replace(envelope, key="f2"), marked]):
            with self.assertRaises(SchemaConflictError) as caught:
                cache.put_envelopes(surface_document, batch)
            self.assertEqual(caught.exception.record_key, "marked")
            self.assertEqual(path.read_bytes(), before)
            self.assertIsNone(cache.get(surface_document, "fresh"))

    def test_only_the_marks_that_hold_a_record_read_only_are_kept_by_key(self) -> None:
        _, store = self._store("extra-slot-store.cc")
        self.assertEqual({k: v[0] for k, v in store._state.unread.items()}, {"marked": surface_document.type, "unit-arm": "tests.slot_unit"})
        _, consent = self._store("extra-slot-consent.cc")
        self.assertEqual(sorted(consent._state.unread), ["nested", "over-consent"])  # the consented mark is no state

    def test_an_arrival_with_no_catalog_entry_under_a_listed_id_is_not_consented(self) -> None:
        wide = next(e for e in SingleFileMessagePackBackingStore(VECTORS / "extra-slot-consent.cc").pull_all() if e.key == "consented")
        bare = dataclasses.replace(wide, catalog_entry=None)

        class Held:
            def pull_all(self):
                return [bare]

            def push(self, envelope): ...

            def push_all(self, envelopes): ...

            def delete(self, type, key): ...

        cache = CultCache.builder().register_document_type(declaring_document).add_generic_store(Held()).build()
        cache.pull_all_backing_stores()
        self.assertFalse(cache.is_writable(declaring_document, "consented"))

    def test_a_removal_is_refused_where_is_writable_says_no(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        before = path.read_bytes()
        other = define_database_entry_type("tests.other", [database_entry_field("a", 0, codec=t.string)], name="a")
        cache2 = _open(self.tmp / "extra-slot-store.cc", surface_document, slot_unit_document, other)
        self.assertFalse(cache2.is_writable(other, "clean"))
        with self.assertRaises(SchemaConflictError):
            cache2.delete(other, "clean")
        self.assertEqual(path.read_bytes(), before)
        self.assertIsNotNone(cache2.get(surface_document, "clean"))
        self.assertTrue(cache2.is_writable(other, "nothing-here"))

    def test_a_global_document_is_not_writable_at_a_key_other_than_the_global_one(self) -> None:
        solo = define_database_entry_type("tests.solo", [database_entry_field("a", 0, codec=t.string)], global_document=True)
        cache = CultCache.builder().register_document_type(solo).build()
        self.assertFalse(cache.is_writable(solo, "elsewhere"))
        with self.assertRaises(CultCacheError):
            cache.put(solo, "elsewhere", {"a": "x"})
        with self.assertRaises(CultCacheError):
            cache.delete(solo, "elsewhere")
        self.assertTrue(cache.is_writable(solo, CultCache.GLOBAL_KEY))

    def test_a_reload_replaces_the_marks_wholesale(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        self.assertFalse(cache.is_writable(surface_document, "marked"))
        # The store is repaired by its owner: the marked record is rewritten clean. The reload must forget the old mark.
        [snapshot] = list(msgpack.Unpacker(open(path, "rb"), raw=False))
        clean = next(r for r in snapshot[2] if r[0] == "clean")
        for record in snapshot[2]:
            if record[0] in ("marked", "unit-arm"):
                record[3] = clean[3] if record[0] == "marked" else record[3]
        snapshot[2] = [r for r in snapshot[2] if r[0] != "unit-arm"]
        path.write_bytes(msgpack.packb(snapshot, use_bin_type=True))
        cache.pull_all_backing_stores()
        self.assertTrue(cache.is_writable(surface_document, "marked"))
        self.assertEqual(cache._state.unread, {})

    def test_a_listed_id_whose_catalog_reports_no_extra_slot_does_not_consent(self) -> None:
        wide = next(e for e in SingleFileMessagePackBackingStore(VECTORS / "extra-slot-consent.cc").pull_all() if e.key == "consented")
        same = dataclasses.replace(wide, catalog_entry=declaring_document.catalog_entry())  # the stored entry has no slot past the reader's

        class Held:
            def pull_all(self):
                return [same]

            def push(self, envelope): ...

            def push_all(self, envelopes): ...

            def delete(self, type, key): ...

        cache = CultCache.builder().register_document_type(declaring_document).add_generic_store(Held()).build()
        cache.pull_all_backing_stores()
        self.assertFalse(cache.is_writable(declaring_document, "consented"))

    def test_a_record_stored_under_the_readers_own_id_is_never_consented(self) -> None:
        own = dataclasses.replace(
            next(e for e in SingleFileMessagePackBackingStore(VECTORS / "extra-slot-consent.cc").pull_all() if e.key == "consented"),
            schema_id="tests.union_surface.v2",
        )
        wider = dataclasses.replace(own.catalog_entry, schema_id="tests.union_surface.v2", compatible_schema_ids=("tests.union_surface.v2",))
        listing = dataclasses.replace(
            declaring_document, schema_id="tests.union_surface.v2", compatible_schema_ids=("tests.union_surface.wide", "tests.union_surface.v2")
        )

        class Held:
            def pull_all(self):
                return [dataclasses.replace(own, catalog_entry=wider)]

            def push(self, envelope): ...

            def push_all(self, envelopes): ...

            def delete(self, type, key): ...

        cache = CultCache.builder().register_document_type(listing).add_generic_store(Held()).build()
        cache.pull_all_backing_stores()
        self.assertFalse(cache.is_writable(listing, "consented"))

    def test_a_removal_by_the_owner_of_a_key_held_as_another_type_removes_it_as_a_write_would_replace_it(self) -> None:
        a = define_database_entry_type(
            "tests.owned_a", [database_entry_field("a", 0, codec=t.string)], name="a", indexes={"by_a": "a"}
        )
        b = define_database_entry_type(
            "tests.owned_b",
            [database_entry_field("a", 0, codec=t.string)],
            compatible_schema_ids=(a.catalog_entry().schema_id,),
        )
        path = self.tmp / "owned.cc"
        writer = _open(path, a)
        writer.put(a, "k", {"a": "x"})
        writer.put(a, "other", {"a": "y"})
        cache = _open(path, a, b)
        self.assertTrue(cache.is_writable(b, "k"))
        cache.delete(b, "k")
        self.assertIsNone(cache.get(a, "k"))
        self.assertIsNone(cache.get_by_index(a, "by_a", "x"))
        self.assertEqual([e.key for e in SingleFileMessagePackBackingStore(path).pull_all()], ["other"])
        self.assertTrue(cache.is_writable(b, "k"))

    def test_an_owners_removal_is_one_store_call_so_the_cache_and_the_store_agree_when_it_fails(self) -> None:
        a = define_database_entry_type("tests.one_a", [database_entry_field("a", 0, codec=t.string)], name="a")
        b = define_database_entry_type(
            "tests.one_b", [database_entry_field("a", 0, codec=t.string)], compatible_schema_ids=(a.catalog_entry().schema_id,)
        )
        path = self.tmp / "one.cc"
        _open(path, a).put(a, "k", {"a": "x"})

        class SecondDeleteFails(SingleFileMessagePackBackingStore):
            calls = 0

            def delete(self, type, key):
                self.calls += 1
                if self.calls > 1:
                    raise OSError("second delete")
                super().delete(type, key)

        store = SecondDeleteFails(path)
        cache = CultCache.builder().register_document_type(a).register_document_type(b).add_generic_store(store).build()
        cache.pull_all_backing_stores()
        cache.delete(b, "k")
        self.assertEqual(store.calls, 1)
        self.assertIsNone(cache.get(a, "k"))
        self.assertEqual(SingleFileMessagePackBackingStore(path).pull_all(), [])

    def test_writing_another_record_and_flushing_carries_the_marked_one_unchanged(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        original = {e.key: e for e in SingleFileMessagePackBackingStore(path).pull_all()}
        cache.put(surface_document, "clean", {**_surface_value(), "version": 9})
        after = {e.key: e for e in SingleFileMessagePackBackingStore(path).pull_all()}
        for key in ("marked", "unit-arm"):
            self.assertEqual(after[key].payload, original[key].payload, key)
            self.assertEqual(after[key].schema_id, original[key].schema_id, key)
            self.assertEqual(after[key].stored_at, original[key].stored_at, key)
        self.assertNotEqual(after["clean"].payload, original["clean"].payload)
        self.assertEqual(after["marked"].catalog_entry, original["marked"].catalog_entry)

    def test_the_vector_store_loads_under_the_reference_documents_and_marks_nothing_else(self) -> None:
        path, cache = self._store("extra-slot-store.cc")
        self.assertTrue(cache.is_writable(surface_document, "clean"))
        self.assertFalse(cache.is_writable(surface_document, "marked"))
        self.assertFalse(cache.is_writable(slot_unit_document, "unit-arm"))
        self.assertEqual(
            cache.get(slot_unit_document, "unit-arm"), {"name": "unit-arm", "outcome": UnionArm("Denied")}
        )


if __name__ == "__main__":
    unittest.main()
