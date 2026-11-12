from __future__ import annotations

import shutil
import tempfile
import unittest
from pathlib import Path

import msgpack

from cultcache_py import (
    CultCache,
    CultCachePayloadDepthError,
    SchemaConflictError,
    SingleFileMessagePackBackingStore,
    UnionArm,
    UnreadSlots,
    database_entry_field,
    define_database_entry_type,
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
