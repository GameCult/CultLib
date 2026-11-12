//! A record whose payload carries unread slots at any depth is read-only: the C# reference's union-parity vectors
//! (README "Extra trailing slot" and "Slot stores") walked through the cache. This runtime has no compatible schema
//! ids, so every mark is unconsented and no `explained` outcome can arise.

mod common;

use std::path::Path;

use common::*;
use cultcache_rs::CultCache;
use cultcache_rs::CultCacheEnvelope;
use cultcache_rs::DatabaseEntry;
use cultcache_rs::SchemaConflictError;
use cultcache_rs::SingleFileMessagePackBackingStore;
use cultcache_rs::UnreadSlots;
use cultcache_rs::decode_payload_marked;

/// A row of `slot-outcomes.msgpack`: store, key, writable, explained, type, slots, declared, occurrences, flattenRefusedBy.
type SlotRow = (String, String, bool, Option<bool>, Option<String>, Option<usize>, Option<usize>, Option<usize>, Option<String>);

const SLOT_VALUE: &str = "extra-slot";

/// `tests.slot_unit`, a document `[Name, Outcome: UnitOutcome]`.
#[derive(Clone, Debug, PartialEq, DatabaseEntry)]
#[cultcache(type = "tests.slot_unit", schema = "tests.slot_unit.v1")]
struct SlotUnit {
    #[cultcache(key = 0)]
    name: String,
    #[cultcache(key = 1)]
    outcome: UnitOutcome,
}

/// Two declared slots, the second defaulted: a payload of one slot is short, not marked.
#[derive(Clone, Debug, PartialEq, DatabaseEntry)]
#[cultcache(type = "tests.shortened", schema = "tests.shortened.v1")]
struct Shortened {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1, default)]
    note: String,
}

fn registered() -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<UnionSurface>().unwrap();
    cache.register_entry_type::<SlotUnit>().unwrap();
    cache
}

/// Any MessagePack value a store holds, enough to drop one record from a store and write the rest back.
#[derive(serde::Serialize, serde::Deserialize)]
#[serde(untagged)]
enum Packed {
    Nil(()),
    Bool(bool),
    Int(i64),
    Text(String),
    Bin(#[serde(with = "serde_bytes")] Vec<u8>),
    Array(Vec<Packed>),
}

/// The committed store without its `marked-variant` record. A variant is a five-slot record, which this runtime's
/// store reader refuses whole (`the_variant_row_...`), so the other rows are walked from a copy without it.
fn without_variant_row(store: &[u8]) -> Vec<u8> {
    let Packed::Array(mut parts) = rmp_serde::from_slice(store).unwrap() else { panic!("a store is an array") };
    let Packed::Array(records) = parts.last_mut().unwrap() else { panic!("a store ends in its records") };
    let before = records.len();
    records.retain(|record| !matches!(record, Packed::Array(fields) if matches!(fields.first(), Some(Packed::Text(key)) if key == "marked-variant")));
    assert_eq!(records.len(), before - 1);
    rmp_serde::to_vec(&parts).unwrap()
}

/// A copy of a committed store, opened with both types registered; the vector itself is never written. The slot store
/// is read without its variant row.
fn open_copy(file: &str, dir: &Path) -> (CultCache, std::path::PathBuf) {
    let path = dir.join(file);
    let bytes = if file == "extra-slot-store.cc" { without_variant_row(&vector(file)) } else { vector(file) };
    std::fs::write(&path, bytes).unwrap();
    let mut cache = registered();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    (cache, path)
}

fn envelope(key: &str, payload: Vec<u8>) -> CultCacheEnvelope {
    CultCacheEnvelope {
        key: key.into(),
        r#type: UnionSurface::TYPE.into(),
        payload,
        stored_at: "2026-01-01T00:00:00Z".into(),
        schema_id: None,
    }
}

fn conflict(error: &anyhow::Error) -> &SchemaConflictError {
    error.downcast_ref::<SchemaConflictError>().unwrap_or_else(|| panic!("not a schema conflict: {error:#}"))
}

fn mark(type_name: &str, slot_count: usize, declared_count: usize, occurrences: usize) -> UnreadSlots {
    UnreadSlots { type_name: type_name.into(), slot_count, declared_count, occurrences }
}

/// The marks the README table pins, by `detail`. The reference names an arm by its class; here an arm is the
/// `...Arm` struct the union wraps.
fn expected_mark(detail: &str) -> UnreadSlots {
    match detail {
        "record" | "depth" => mark("UnionSurface", 8, 7, 1),
        "nested-record" => mark("Composition", 3, 2, 1),
        "arm" => mark("BoxArm", 3, 2, 1),
        "list-element" => mark("TextArm", 3, 2, 1),
        other => panic!("unknown detail {other}"),
    }
}

fn assert_refused_with(error: &anyhow::Error, key: &str, expected: &UnreadSlots) {
    let refusal = conflict(error);
    assert_eq!(refusal.unread_slots.as_ref(), Some(expected));
    assert_eq!(refusal.record_key, key);
    assert_eq!(refusal.schema_names.len(), 1);
    let message = error.to_string();
    assert!(message.contains(&expected.type_name), "{message}");
    assert!(message.contains(&expected.slot_count.to_string()) && message.contains(&expected.declared_count.to_string()), "{message}");
    assert!(!message.contains(SLOT_VALUE), "a refusal echoed the slot value: {message}");
}

#[test]
fn every_read_only_vector_decodes_marked_and_is_not_writable() {
    let rows: Vec<(String, String, String, Option<i64>, String)> = rmp_serde::from_slice(&vector("outcomes.msgpack")).unwrap();
    assert_eq!(rows.len(), 9);
    let bounds: Vec<serde_json::Value> = rmp_serde::from_slice(&vector("bounds-outcomes.msgpack")).unwrap();
    let mut cases: Vec<(String, String)> = rows
        .into_iter()
        .filter(|(_, outcome, ..)| outcome == "read-only")
        .map(|(file, _, _, _, detail)| (file, detail))
        .collect();
    for entry in &bounds[1..] {
        let fields: Vec<&str> = entry.as_array().unwrap().iter().filter_map(|field| field.as_str()).collect();
        if fields[1] == "read-only" {
            cases.push((fields[0].to_string(), fields[3].to_string()));
        }
    }
    assert_eq!(cases.len(), 5);
    for (file, detail) in cases {
        let payload = vector(&file);
        let (value, found) = decode_payload_marked::<UnionSurface>(&payload).unwrap_or_else(|error| panic!("{file}: {error:#}"));
        assert_eq!(value, constructed(), "{file}");
        assert_eq!(found, Some(expected_mark(&detail)), "{file}");

        let mut cache = registered();
        let dir = tempfile::tempdir().unwrap();
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(dir.path().join("s.cc"))).unwrap();
        cache.put_raw_envelope(envelope("k", payload.clone())).unwrap();
        assert_eq!(cache.get::<UnionSurface>("k").unwrap(), Some(constructed()), "{file}");
        assert!(!cache.is_writable(UnionSurface::TYPE, "k").unwrap(), "{file}");
        assert!(cache.is_writable(UnionSurface::TYPE, "unheld").unwrap(), "{file}");
        let error = cache.put("k", &constructed()).unwrap_err();
        assert_refused_with(&error, "k", &expected_mark(&detail));
    }
}

#[test]
fn clean_and_short_payloads_are_writable_and_unmarked() {
    let (value, found) = decode_payload_marked::<UnionSurface>(&vector("union-surface.msgpack")).unwrap();
    assert_eq!((value, found), (constructed(), None));
    // A payload short of its declared slots keeps its defaults and does not mark.
    let (value, found) = decode_payload_marked::<Shortened>(&rmp_serde::to_vec(&("only",)).unwrap()).unwrap();
    assert_eq!((value.id.as_str(), value.note.as_str(), found), ("only", "", None));
}

#[test]
fn a_payload_past_the_nesting_limit_is_refused_never_marked() {
    for file in ["depth-skipped-past-limit.msgpack", "depth-past-limit.msgpack"] {
        let error = decode_payload_marked::<UnionSurface>(&vector(file)).unwrap_err();
        assert!(error.to_string().contains("the limit is 256"), "{file}: {error:#}");
    }
}

#[test]
fn a_plain_serde_decode_marks_nothing_and_does_not_leak_a_scope() {
    // No scope is open: the visitors note into nothing.
    let value: UnionSurface = rmp_serde::from_slice(&vector("extra-slot-record.msgpack")).unwrap();
    assert_eq!(value, constructed());
    // A marked decode followed by a clean one: the first mark does not survive into the second.
    assert!(decode_payload_marked::<UnionSurface>(&vector("extra-slot-record.msgpack")).unwrap().1.is_some());
    assert_eq!(decode_payload_marked::<UnionSurface>(&vector("union-surface.msgpack")).unwrap().1, None);
}

#[test]
fn the_slot_store_rows_walk_through_the_cache() {
    let rows: Vec<SlotRow> =
        rmp_serde::from_slice(&vector("slot-outcomes.msgpack")).unwrap();
    assert_eq!(rows.len(), 8);
    let dir = tempfile::tempdir().unwrap();
    let mut walked = 0;
    for (store, key, writable, _explained, type_name, slots, declared, occurrences, flatten_refused_by) in rows {
        // The variant row is a record this runtime cannot hold: see the variant test. The consent store's `writable` and
        // `explained` answer the compatible-id declaration, which this runtime has none of; its marks are walked below.
        if flatten_refused_by.is_some() || store == "extra-slot-consent.cc" {
            continue;
        }
        let case = dir.path().join(&key);
        std::fs::create_dir_all(&case).unwrap();
        let (mut cache, _) = open_copy(&store, &case);
        let type_id = if key == "unit-arm" { SlotUnit::TYPE } else { UnionSurface::TYPE };
        assert_eq!(cache.is_writable(type_id, &key).unwrap(), writable, "{store} {key}");
        let removal = if key == "unit-arm" { cache.delete::<SlotUnit>(&key) } else { cache.delete::<UnionSurface>(&key) };
        match writable {
            true => assert!(removal.unwrap()),
            false => {
                let found = conflict(&removal.unwrap_err()).unread_slots.clone().unwrap();
                // The reference names the unit arm's class, Denied2; here the variant.
                let expected_type = match type_name.as_deref().unwrap() {
                    "Denied2" => "Denied",
                    other => other,
                };
                assert_eq!((found.type_name.as_str(), Some(found.slot_count), Some(found.declared_count), Some(found.occurrences)), (expected_type, slots, declared, occurrences));
                assert!(cache.snapshot_envelopes().iter().any(|held| held.key == key), "a refused removal removed {key}");
            }
        }
        walked += 1;
    }
    assert_eq!(walked, 3);
}

#[test]
fn the_consent_store_marks_are_unconsented_here() {
    let rows: Vec<SlotRow> =
        rmp_serde::from_slice(&vector("slot-outcomes.msgpack")).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let mut found = 0;
    for (store, key, _writable, _explained, _type_name, slots, declared, occurrences, _) in rows {
        if store != "extra-slot-consent.cc" {
            continue;
        }
        let (cache, _) = open_copy(&store, dir.path());
        let held = cache.snapshot_envelopes().into_iter().find(|held| held.key == key).unwrap();
        let (_, mark) = decode_payload_marked::<UnionSurface>(&held.payload).unwrap();
        match slots {
            // The reference's `consented` row is writable there (a listed compatible id covers it); here every mark refuses.
            None => assert_eq!(mark, None, "{key}"),
            Some(slots) => {
                let mark = mark.unwrap_or_else(|| panic!("{key} carried no mark"));
                // The first type read is the document, whose reference name is SurfaceDeclaring.
                assert_eq!(mark.type_name, "UnionSurface", "{key}");
                assert_eq!((Some(mark.slot_count), Some(mark.declared_count), Some(mark.occurrences)), (Some(slots), declared, occurrences), "{key}");
                assert!(!cache.is_writable(UnionSurface::TYPE, &key).unwrap(), "{key}");
            }
        }
        found += 1;
    }
    assert_eq!(found, 4);
}

#[test]
fn every_write_entry_refuses_a_marked_key_before_any_store_is_touched() {
    let dir = tempfile::tempdir().unwrap();
    let (mut cache, path) = open_copy("extra-slot-store.cc", dir.path());
    let expected = mark("Composition", 3, 2, 1);
    let before = std::fs::read(&path).unwrap();
    let held = cache.snapshot_envelopes().into_iter().find(|held| held.key == "marked").unwrap();
    let clean = cache.snapshot_envelopes().into_iter().find(|held| held.key == "clean").unwrap();
    assert!(!cache.is_writable(UnionSurface::TYPE, "marked").unwrap());
    assert!(cache.is_writable(UnionSurface::TYPE, "clean").unwrap());

    let replacing = envelope("marked", rmp_serde::to_vec(&constructed()).unwrap());
    assert_refused_with(&cache.put("marked", &constructed()).unwrap_err(), "marked", &expected);
    assert_refused_with(&cache.put_envelope::<UnionSurface>(replacing.clone()).unwrap_err(), "marked", &expected);
    assert_refused_with(&cache.put_raw_envelope(replacing.clone()).unwrap_err(), "marked", &expected);
    assert_refused_with(&cache.put_prepared_batch(vec![replacing]).unwrap_err(), "marked", &expected);
    assert_refused_with(&cache.delete::<UnionSurface>("marked").unwrap_err(), "marked", &expected);
    assert_refused_with(
        &cache.update::<UnionSurface, _>("marked", |current| current.unwrap()).unwrap_err(),
        "marked",
        &expected,
    );

    // A batch with a marked key refuses whole: its clean member is not written either.
    let mut rewritten = clean.clone();
    rewritten.payload = rmp_serde::to_vec(&UnionSurface { version: 9, ..constructed() }).unwrap();
    let batch = vec![rewritten, envelope("marked", rmp_serde::to_vec(&constructed()).unwrap())];
    assert_refused_with(&cache.put_prepared_batch(batch).unwrap_err(), "marked", &expected);

    assert_eq!(std::fs::read(&path).unwrap(), before, "a refused write touched the store");
    assert_eq!(cache.snapshot_envelopes().into_iter().find(|entry| entry.key == "marked").unwrap(), held);
    assert_eq!(cache.snapshot_envelopes().into_iter().find(|entry| entry.key == "clean").unwrap(), clean);

    // A write to another key succeeds, and the marked record's bytes, schema id and stored-at are carried unchanged.
    cache.put("clean", &UnionSurface { version: 9, ..constructed() }).unwrap();
    cache.put("other", &constructed()).unwrap();
    assert!(cache.delete::<UnionSurface>("other").unwrap());
    let mut reopened = registered();
    reopened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    reopened.pull_all_backing_stores().unwrap();
    let carried = reopened.snapshot_envelopes().into_iter().find(|entry| entry.key == "marked").unwrap();
    assert_eq!(carried, held);
    assert_eq!(carried.payload, vector("extra-slot-nested-record.msgpack"));
    assert_eq!(reopened.get::<UnionSurface>("clean").unwrap().unwrap().version, 9);
}

#[test]
fn a_unit_arm_with_slots_decodes_as_the_unit_arm_and_marks() {
    let (document, found) = decode_payload_marked::<SlotUnit>(&unit_arm_payload()).unwrap();
    assert_eq!(document, SlotUnit { name: "unit-arm".into(), outcome: UnitOutcome::Denied });
    assert_eq!(found, Some(mark("Denied", 1, 0, 1)));

    let dir = tempfile::tempdir().unwrap();
    let (mut cache, _) = open_copy("extra-slot-store.cc", dir.path());
    assert_eq!(cache.get::<SlotUnit>("unit-arm").unwrap(), Some(document.clone()));
    assert!(!cache.is_writable(SlotUnit::TYPE, "unit-arm").unwrap());
    let error = cache.put("unit-arm", &document).unwrap_err();
    assert_refused_with(&error, "unit-arm", &mark("Denied", 1, 0, 1));
    assert_refused_with(&cache.delete::<SlotUnit>("unit-arm").unwrap_err(), "unit-arm", &mark("Denied", 1, 0, 1));

    // Several elements are still one marked object, and a clean unit arm is unmarked.
    let (_, found) = decode_payload_marked::<UnitOutcome>(&rmp_serde::to_vec(&(2u64, ("a", "b", "c"))).unwrap()).unwrap();
    assert_eq!(found, Some(mark("Denied", 3, 0, 1)));
    let (_, found) = decode_payload_marked::<UnitOutcome>(&vector("unit-arm-denied.msgpack")).unwrap();
    assert_eq!(found, None);
}

fn unit_arm_payload() -> Vec<u8> {
    let dir = tempfile::tempdir().unwrap();
    let (cache, _) = open_copy("extra-slot-store.cc", dir.path());
    cache.snapshot_envelopes().into_iter().find(|held| held.key == "unit-arm").unwrap().payload
}

#[test]
fn the_variant_row_of_the_slot_store_is_refused_whole_by_the_store_reader() {
    // `marked-variant` is a five-slot record (a variant): this runtime holds no variants, and its v1 snapshot reader
    // refuses such a record, so the store as written cannot load here and the other rows are walked without it.
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("extra-slot-store.cc");
    std::fs::write(&path, vector("extra-slot-store.cc")).unwrap();
    let mut cache = registered();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    let error = cache.pull_all_backing_stores().unwrap_err();
    assert!(format!("{error:#}").contains("is not decodable"), "{error:#}");
}

fn slot_unit_envelope(key: &str, payload: Vec<u8>) -> CultCacheEnvelope {
    CultCacheEnvelope { r#type: SlotUnit::TYPE.into(), ..envelope(key, payload) }
}

#[test]
fn a_held_payload_the_type_cannot_read_is_read_only_and_refused_by_name() {
    let mut cache = registered();
    // A newer writer's record: an arm this build lacks and an extra root slot. get cannot read it, and it is the data
    // read-only exists to protect.
    let newer = rmp_serde::to_vec(&("newer", (99u64, ("x",)), "extra")).unwrap();
    cache.put_raw_envelope(slot_unit_envelope("n", newer)).unwrap();
    assert!(cache.get::<SlotUnit>("n").is_err());
    assert!(!cache.is_writable(SlotUnit::TYPE, "n").unwrap());
    let replacement = SlotUnit { name: "n".into(), outcome: UnitOutcome::Denied };
    let held = cache.snapshot_envelopes();
    for error in [
        cache.put("n", &replacement).unwrap_err(),
        cache.put_raw_envelope(slot_unit_envelope("n", rmp_serde::to_vec(&replacement).unwrap())).unwrap_err(),
        cache.delete::<SlotUnit>("n").unwrap_err(),
    ] {
        let message = format!("{error:#}");
        assert!(message.contains("\"n\"") && message.contains("tests.slot_unit") && message.contains("read-only"), "{message}");
        assert!(!message.contains("newer") && !message.contains("extra"), "{message}");
    }
    assert_eq!(cache.snapshot_envelopes(), held);
}

#[test]
fn a_corrupt_held_payload_is_refused_naming_the_key_never_its_bytes() {
    let mut cache = registered();
    cache.put_raw_envelope(envelope("broken", rmp_serde::to_vec(&("CANARY-id", "CANARY-version")).unwrap())).unwrap();
    assert!(!cache.is_writable(UnionSurface::TYPE, "broken").unwrap());
    let error = cache.delete::<UnionSurface>("broken").unwrap_err();
    let message = format!("{error:#}");
    assert!(message.contains("\"broken\"") && message.contains("repair"), "{message}");
    assert!(!message.contains("CANARY"), "{message}");
    assert!(cache.is_writable("tests.unregistered", "broken").is_err());
}

fn held_marked_and_clean() -> (CultCache, tempfile::TempDir, std::path::PathBuf) {
    let dir = tempfile::tempdir().unwrap();
    let (cache, path) = open_copy("extra-slot-store.cc", dir.path());
    (cache, dir, path)
}

#[test]
fn load_soa_does_not_replace_or_drop_a_marked_record() {
    use cultcache_rs::CultCacheSoaTable;
    let (mut cache, _dir, path) = held_marked_and_clean();
    let all = cache.snapshot_envelopes();
    let held = all.iter().find(|held| held.key == "marked").unwrap().clone();
    let without: Vec<_> = all.iter().filter(|held| held.key != "marked").cloned().collect();
    let mut replaced = all.clone();
    replaced.iter_mut().find(|held| held.key == "marked").unwrap().payload = rmp_serde::to_vec(&constructed()).unwrap();
    for table in [without, replaced] {
        let error = cache.load_soa(CultCacheSoaTable::from_envelopes(table)).unwrap_err();
        assert_refused_with(&error, "marked", &mark("Composition", 3, 2, 1));
        assert_eq!(cache.snapshot_envelopes(), all, "a refused load changed the image");
    }
    // The same record carried unchanged, with another key added, loads.
    let mut kept = all.clone();
    kept.push(envelope("added", rmp_serde::to_vec(&constructed()).unwrap()));
    cache.load_soa(CultCacheSoaTable::from_envelopes(kept)).unwrap();
    assert_eq!(cache.snapshot_envelopes().iter().find(|held| held.key == "marked"), Some(&held));
    // A clean record may be dropped by a load.
    let no_clean: Vec<_> = cache.snapshot_envelopes().into_iter().filter(|held| held.key != "clean").collect();
    cache.load_soa(CultCacheSoaTable::from_envelopes(no_clean)).unwrap();
    // The marked record is still on disk after the next batch.
    let rewrite = envelope("added", rmp_serde::to_vec(&UnionSurface { version: 5, ..constructed() }).unwrap());
    cache.put_prepared_batch(vec![rewrite]).unwrap();
    let mut reopened = registered();
    reopened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    reopened.pull_all_backing_stores().unwrap();
    assert_eq!(reopened.snapshot_envelopes().iter().find(|held| held.key == "marked"), Some(&held));
}

#[test]
fn is_writable_agrees_with_the_writes_for_foreign_keys_and_other_types() {
    let dir = tempfile::tempdir().unwrap();
    // Only UnionSurface registered: the store's unit-arm record is foreign here.
    let path = dir.path().join("extra-slot-store.cc");
    std::fs::write(&path, without_variant_row(&vector("extra-slot-store.cc"))).unwrap();
    let mut cache = CultCache::new();
    cache.register_entry_type::<UnionSurface>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    assert!(cache.put("unit-arm", &constructed()).is_err());
    assert!(!cache.is_writable(UnionSurface::TYPE, "unit-arm").unwrap());
    assert!(cache.is_writable(UnionSurface::TYPE, "unheld").unwrap());

    // Another registered type at a key a record of this one holds.
    let (mut cache, _dir, _) = held_marked_and_clean();
    let other = SlotUnit { name: "clean".into(), outcome: UnitOutcome::Denied };
    assert!(cache.put("clean", &other).is_err());
    assert!(!cache.is_writable(SlotUnit::TYPE, "clean").unwrap());
    assert!(!cache.is_writable(SlotUnit::TYPE, "marked").unwrap());
    assert!(cache.is_writable(UnionSurface::TYPE, "clean").unwrap());
}

#[test]
fn a_marked_key_refuses_a_batch_wherever_it_sits() {
    let (mut cache, _dir, path) = held_marked_and_clean();
    let before = std::fs::read(&path).unwrap();
    let marked = envelope("marked", rmp_serde::to_vec(&constructed()).unwrap());
    let clean = |key: &str| envelope(key, rmp_serde::to_vec(&constructed()).unwrap());
    for batch in [
        vec![marked.clone(), clean("a"), clean("b")],
        vec![clean("a"), marked.clone(), clean("b")],
        vec![clean("a"), clean("b"), marked.clone()],
    ] {
        let error = cache.put_prepared_batch(batch).unwrap_err();
        assert_refused_with(&error, "marked", &mark("Composition", 3, 2, 1));
        assert_eq!(std::fs::read(&path).unwrap(), before);
        assert!(cache.snapshot_envelopes().iter().all(|held| held.key != "a" && held.key != "b"));
    }
}

#[test]
fn a_marked_decode_leaves_no_scope_open() {
    assert_eq!(cultcache_rs::__enter_slots(), 0);
    decode_payload_marked::<UnionSurface>(&vector("extra-slot-record.msgpack")).unwrap();
    assert_eq!(cultcache_rs::__enter_slots(), 0, "a marked decode left its scope open on this thread");
    let _ = decode_payload_marked::<UnionSurface>(&vector("depth-skipped-past-limit.msgpack"));
    assert_eq!(cultcache_rs::__enter_slots(), 0);
}
