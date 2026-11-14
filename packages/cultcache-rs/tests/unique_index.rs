//! `#[cultcache(index)]` is unique (`variants:ruling:unique-indexes`; C# `RefuseIndexSharing`): within one type and one
//! alias no two records hold one non-blank value, judged on the result of each admission, on every write path and every
//! load. The refusal text is informational; the contract is the verdict, so most pins assert the verdict and the
//! shared vectors in `tests/vectors/unique-index` pin the text and the canonical index text.

use std::path::{Path, PathBuf};

use cultcache_rs::{
    CultCache, CultCacheEnvelope, CultCacheLoadRefused, CultCacheSoaTable, DatabaseEntry, SingleFileMessagePackBackingStore,
};
use serde::Deserialize;

/// The vectors' `tests.unique_item`: Sku is unique, Category a grouping.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "unique_item", schema = "tests.unique_item")]
struct Unique {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
    #[cultcache(key = 1)]
    category: String,
}

/// The same type with no index: a role, which writes the duplicates `Unique` refuses.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "unique_item", schema = "tests.unique_item")]
struct Twin {
    #[cultcache(key = 0)]
    sku: String,
    #[cultcache(key = 1)]
    category: String,
}

/// Two unique indexes declared out of alphabetical order, so no ordering is an accident of declaration.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "multi", schema = "Multi")]
struct Multi {
    #[cultcache(key = 0, index = "B")]
    b: Option<String>,
    #[cultcache(key = 1, index = "A")]
    a: Option<String>,
}

/// Another type sharing the alias `Sku`: types never conflict.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "other", schema = "Other")]
struct Other {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "plain", schema = "Plain")]
struct Plain {
    #[cultcache(key = 0)]
    value: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "vs", schema = "VS")]
struct VString {
    #[cultcache(key = 0, index = "V")]
    v: String,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "vi", schema = "VI")]
struct VInt {
    #[cultcache(key = 0, index = "V")]
    v: i64,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "vu", schema = "VU")]
struct VUint {
    #[cultcache(key = 0, index = "V")]
    v: u64,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "vb", schema = "VB")]
struct VBool {
    #[cultcache(key = 0, index = "V")]
    v: bool,
}

/// The refusals vector's other schemas: `Sku` is unique in each; `Alt` is a second unique index in `uq_pair` (alias
/// `Alt`) and `uq_alias` (alias `alt`, sorting after `Sku` only under an ordinal compare); alpha and beta are plain.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "uq_pair", schema = "tests.uq_pair")]
struct UqPair {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
    #[cultcache(key = 1, index = "Alt")]
    alt: String,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "uq_alias", schema = "tests.uq_alias")]
struct UqAlias {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
    #[cultcache(key = 1, index = "alt")]
    alt: String,
}
/// Aliases U+FF21 and U+10400: UTF-16 puts the astral alias first (D801 DC00 < FF21), code-point order the other way.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "uq_wide", schema = "tests.uq_wide")]
struct UqWide {
    #[cultcache(key = 0, index = "\u{FF21}")]
    sku: String,
    #[cultcache(key = 1, index = "\u{10400}")]
    alt: String,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "uq_alpha", schema = "tests.uq_alpha")]
struct UqAlpha {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
    #[cultcache(key = 1)]
    second: String,
}
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "uq_beta", schema = "tests.uq_beta")]
struct UqBeta {
    #[cultcache(key = 0, index = "Sku")]
    sku: String,
    #[cultcache(key = 1)]
    second: String,
}

fn unique(sku: &str) -> Unique {
    Unique { sku: sku.into(), category: "c".into() }
}

fn multi(a: Option<&str>, b: Option<&str>) -> Multi {
    Multi { a: a.map(Into::into), b: b.map(Into::into) }
}

fn refusal(k1: &str, k2: &str, schema: &str, alias: &str) -> String {
    format!("Records {k1} and {k2} ({schema}) both hold unique index '{alias}'; an index value names one record.")
}

fn cache_at(path: &Path) -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Unique>().unwrap();
    cache.register_entry_type::<Multi>().unwrap();
    cache.register_entry_type::<Other>().unwrap();
    cache.register_entry_type::<Plain>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    cache
}

fn memory_cache() -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Unique>().unwrap();
    cache.register_entry_type::<Multi>().unwrap();
    cache.register_entry_type::<Other>().unwrap();
    cache
}

fn twin_cache_at(path: &Path) -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Twin>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path)).unwrap();
    cache.pull_all_backing_stores().unwrap();
    cache
}

fn error_text(result: anyhow::Result<impl std::fmt::Debug>) -> String {
    format!("{:#}", result.unwrap_err())
}

fn skus(cache: &CultCache) -> Vec<(String, String)> {
    cache.get_all_with_keys::<Unique>().unwrap().into_iter().map(|(key, value)| (key, value.sku)).collect()
}

#[test]
fn the_derive_emits_the_unique_aliases() {
    fn aliases<T: DatabaseEntry>() -> Vec<&'static str> {
        T::UNIQUE_INDEXES.iter().map(|(alias, _)| *alias).collect()
    }
    assert_eq!(aliases::<Unique>(), ["Sku"]);
    assert_eq!(aliases::<Multi>(), ["B", "A"]);
    assert!(aliases::<Plain>().is_empty());
}

#[test]
fn a_put_of_a_held_value_at_another_key_is_refused_and_changes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("u.cc");
    let mut cache = cache_at(&path);
    cache.put("item:b", &unique("S-1")).unwrap();
    let bytes = std::fs::read(&path).unwrap();

    let text = error_text(cache.put("item:a", &unique("S-1")));
    assert_eq!(text, refusal("item:a", "item:b", "tests.unique_item", "Sku"));
    assert!(!text.contains("S-1"), "the refusal never names the value");
    assert_eq!(std::fs::read(&path).unwrap(), bytes, "the store is untouched");
    assert_eq!(cache.get::<Unique>("item:a").unwrap(), None);
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S-1").unwrap().unwrap().sku, "S-1");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S-1").unwrap(), cache.get::<Unique>("item:b").unwrap());
}

// The old bug: write b then a sharing a value used to answer whichever key sorted first.
#[test]
fn the_first_holder_keeps_the_value_whatever_the_key_order() {
    let mut cache = memory_cache();
    cache.put("item:z", &unique("S")).unwrap();
    assert!(cache.put("item:a", &unique("S")).is_err());
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap().unwrap(), unique("S"));
    assert_eq!(skus(&cache), vec![("item:z".to_string(), "S".to_string())]);
}

#[test]
fn a_record_rewriting_its_own_key_keeps_its_value_and_a_change_releases_the_old_one() {
    let mut cache = memory_cache();
    cache.put("k", &unique("S")).unwrap();
    cache.put("k", &Unique { sku: "S".into(), category: "other".into() }).unwrap();
    assert!(cache.get_by_index::<Unique>("Sku", "S").unwrap().is_some());

    cache.put("k", &unique("T")).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), None, "a stale value is not found");
    cache.put("j", &unique("S")).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap().unwrap().sku, "S");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "T").unwrap().unwrap().sku, "T");
    // Updating through `update` is the same rule.
    assert!(cache.update::<Unique, _>("k", |_| Some(unique("S"))).is_err());
    assert_eq!(cache.get::<Unique>("k").unwrap().unwrap().sku, "T");
}

#[test]
fn delete_releases_a_value() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("d.cc");
    let mut cache = cache_at(&path);
    cache.put("item:a", &unique("S")).unwrap();
    assert!(cache.put("item:b", &unique("S")).is_err());
    assert!(cache.delete::<Unique>("item:a").unwrap());
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), None);
    cache.put("item:b", &unique("S")).unwrap();
    assert!(cache.get_by_index::<Unique>("Sku", "S").unwrap().is_some());
    assert!(!cache.delete::<Unique>("item:a").unwrap(), "deleting an absent key releases nothing");
    assert!(cache.get_by_index::<Unique>("Sku", "S").unwrap().is_some());
    assert!(cache_at(&path).get_by_index::<Unique>("Sku", "S").unwrap().is_some());
}

#[test]
fn types_sharing_an_alias_and_a_value_do_not_conflict() {
    let mut cache = memory_cache();
    cache.put("a", &unique("S")).unwrap();
    cache.put("oa", &Other { sku: "S".into() }).unwrap();
    cache.put("ob", &Other { sku: "T".into() }).unwrap();
    assert!(cache.put("ob", &Other { sku: "S".into() }).is_err());
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap().unwrap(), unique("S"));
    assert_eq!(cache.get_by_index::<Other>("Sku", "S").unwrap().unwrap(), Other { sku: "S".into() });
}

#[test]
fn an_alias_the_type_does_not_declare_finds_nothing() {
    let mut cache = memory_cache();
    cache.put("a", &unique("S")).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("sku", "S").unwrap(), None);
    assert_eq!(cache.get_by_index::<Unique>("Category", "c").unwrap(), None);
}

#[test]
fn blank_and_none_values_never_conflict() {
    let mut cache = memory_cache();
    for (key, sku) in [("a", " "), ("b", ""), ("c", "\u{85}"), ("d", "\t\n\u{a0}\u{3000}")] {
        cache.put(key, &unique(sku)).unwrap();
    }
    cache.put("m1", &multi(None, None)).unwrap();
    cache.put("m2", &multi(None, Some(" "))).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", " ").unwrap(), None);
    // U+FEFF, U+001F, U+200B, U+180E are values, never trimmed.
    cache.put("e", &unique("\u{feff}")).unwrap();
    assert!(cache.put("f", &unique("\u{feff}")).is_err());
    cache.put("g", &unique(" S ")).unwrap();
    cache.put("h", &unique("S")).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", " S ").unwrap().unwrap().sku, " S ");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap().unwrap().sku, "S");
}

// C# `string.IsNullOrWhiteSpace` over every code unit: exactly these 25 are blank (probed at 7760219b, dotnet 10).
#[test]
fn the_blank_set_is_exactly_the_csharp_white_space() {
    let blank: Vec<u32> = (0x9..=0xD)
        .chain([0x20, 0x85, 0xA0, 0x1680])
        .chain(0x2000..=0x200A)
        .chain([0x2028, 0x2029, 0x202F, 0x205F, 0x3000])
        .collect();
    assert_eq!(blank.len(), 25);
    let mut cache = memory_cache();
    let mut found = Vec::new();
    for unit in (0..=0xFFFF_u32).filter(|unit| char::from_u32(*unit).is_some()) {
        let text = char::from_u32(unit).unwrap().to_string();
        cache.put("a", &unique(&text)).unwrap();
        let second = cache.put("b", &unique(&text));
        if second.is_ok() {
            found.push(unit);
            cache.delete::<Unique>("b").unwrap();
            assert_eq!(cache.get_by_index::<Unique>("Sku", &text).unwrap(), None, "U+{unit:04X} blank is never found");
        } else {
            assert_eq!(cache.get_by_index::<Unique>("Sku", &text).unwrap().unwrap().sku, text);
        }
    }
    assert_eq!(found, blank);
}

fn staged<T: DatabaseEntry>(cache: &CultCache, rows: &[(&str, T)]) -> Vec<CultCacheEnvelope> {
    rows.iter().map(|(key, value)| cache.prepare_entry(*key, value).unwrap().0).collect()
}

// Two unique indexes, collision on the second-declared, through every write path.
#[test]
fn every_write_path_refuses_and_changes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("paths.cc");
    let mut cache = cache_at(&path);
    cache.put("m:held", &multi(Some("a1"), Some("b1"))).unwrap();
    let bytes = std::fs::read(&path).unwrap();
    let before = cache.snapshot();

    let clash_b = multi(Some("a2"), Some("b1"));
    let clash_a = multi(Some("a1"), Some("b2"));
    for (clash, alias) in [(&clash_b, "B"), (&clash_a, "A")] {
        let pick = |text: &str| {
            let (first, second) = if "m:new" < "m:held" { ("m:new", "m:held") } else { ("m:held", "m:new") };
            assert_eq!(text, refusal(first, second, "Multi", alias));
        };
        pick(&error_text(cache.put("m:new", clash)));
        pick(&error_text(cache.update::<Multi, _>("m:new", |_| Some(clash.clone()))));
        let envelope = staged(&cache, &[("m:new", clash.clone())]).remove(0);
        pick(&error_text(cache.put_envelope::<Multi>(envelope.clone())));
        pick(&error_text(cache.put_raw_envelope(envelope.clone())));
        pick(&error_text(cache.put_prepared_batch(vec![envelope])));
        assert_eq!(std::fs::read(&path).unwrap(), bytes);
        assert_eq!(cache.snapshot(), before);
        assert_eq!(cache.get_by_index::<Multi>("B", "b2").unwrap(), None);
    }
    // A clean row through every path lands and is found by both aliases.
    let fine = multi(Some("a9"), Some("b9"));
    cache.put("m:1", &fine).unwrap();
    let envelope = staged(&cache, &[("m:2", multi(Some("a8"), Some("b8")))]).remove(0);
    cache.put_envelope::<Multi>(envelope).unwrap();
    let envelope = staged(&cache, &[("m:3", multi(Some("a7"), Some("b7")))]).remove(0);
    cache.put_raw_envelope(envelope).unwrap();
    cache.put_prepared_batch(staged(&cache, &[("m:4", multi(Some("a6"), Some("b6")))])).unwrap();
    for (alias, value, key) in [("A", "a9", 1), ("B", "b8", 2), ("A", "a7", 3), ("B", "b6", 4)] {
        let found = cache.get_by_index::<Multi>(alias, value).unwrap().unwrap();
        assert_eq!(Some(found), cache.get::<Multi>(&format!("m:{key}")).unwrap());
    }
    // Held values survive a reopen of the store: the load installs the same holders.
    let mut reopened = cache_at(&path);
    assert!(reopened.put("m:x", &multi(None, Some("b9"))).is_err());
    assert!(reopened.put("m:x", &multi(Some("a6"), None)).is_err());
    assert!(reopened.put("m:x", &multi(Some("zz"), Some("yy"))).is_ok());
}

#[test]
fn a_batch_is_judged_whole() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("batch.cc");
    let mut cache = cache_at(&path);
    cache.put("a", &unique("X")).unwrap();
    cache.put("b", &unique("Y")).unwrap();

    // A swap inside one batch lands.
    cache.put_prepared_batch(staged(&cache, &[("a", unique("Y")), ("b", unique("X"))])).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", "Y").unwrap(), cache.get::<Unique>("a").unwrap());
    assert_eq!(cache.get_by_index::<Unique>("Sku", "X").unwrap(), cache.get::<Unique>("b").unwrap());
    assert_eq!(cache_at(&path).get_by_index::<Unique>("Sku", "X").unwrap().unwrap().sku, "X");

    // A batch taking a value a record being replaced just released lands; one holding a value twice is refused whole.
    cache.put_prepared_batch(staged(&cache, &[("a", unique("Z")), ("c", unique("Y"))])).unwrap();
    let bytes = std::fs::read(&path).unwrap();
    let before = cache.snapshot();
    let text = error_text(cache.put_prepared_batch(staged(&cache, &[("p", unique("N")), ("q", unique("N")), ("r", unique("W"))])));
    assert_eq!(text, refusal("p", "q", "tests.unique_item", "Sku"));
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
    assert_eq!(cache.snapshot(), before);

    // A key staged twice lands as its last envelope.
    cache.put_prepared_batch(staged(&cache, &[("d", unique("D1")), ("d", unique("D2")), ("e", unique("D1"))])).unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", "D2").unwrap(), cache.get::<Unique>("d").unwrap());
    assert_eq!(cache.get_by_index::<Unique>("Sku", "D1").unwrap(), cache.get::<Unique>("e").unwrap());
}

#[test]
fn a_load_of_a_duplicate_image_is_refused_and_keeps_the_prior_view() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("load.cc");
    let mut cache = cache_at(&path);
    cache.put("item:keep", &unique("K")).unwrap();

    let mut twin = twin_cache_at(&path);
    twin.put("item:b", &Twin { sku: "S".into(), category: "c".into() }).unwrap();
    twin.put("item:a", &Twin { sku: "S".into(), category: "c".into() }).unwrap();
    let bytes = std::fs::read(&path).unwrap();

    let before = cache.snapshot();
    assert_eq!(error_text(cache.pull_all_backing_stores()), refusal("item:a", "item:b", "tests.unique_item", "Sku"));
    assert_eq!(cache.snapshot(), before, "a refused pull admits nothing");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "K").unwrap().unwrap().sku, "K");
    assert_eq!(std::fs::read(&path).unwrap(), bytes);

    // load_soa of the same image through a cache with no store.
    let table: CultCacheSoaTable = twin.snapshot_soa();
    let mut soa = memory_cache();
    soa.put("item:keep", &unique("K")).unwrap();
    let before = soa.snapshot();
    assert_eq!(error_text(soa.load_soa(table)), refusal("item:a", "item:b", "tests.unique_item", "Sku"));
    assert_eq!(soa.snapshot(), before);
    assert!(soa.get_by_index::<Unique>("Sku", "K").unwrap().is_some());

    // A clean image loads and replaces the holders with the image's.
    let mut clean = twin_cache_at(&dir.path().join("clean.cc"));
    clean.put("item:x", &Twin { sku: "X".into(), category: "c".into() }).unwrap();
    soa.load_soa(clean.snapshot_soa()).unwrap();
    assert_eq!(soa.get_by_index::<Unique>("Sku", "K").unwrap(), None);
    assert!(soa.get_by_index::<Unique>("Sku", "X").unwrap().is_some());
    soa.put("item:y", &unique("K")).unwrap();
}

// A pull refused for a duplicate unique-index value, or for one key held under two types in two stores, closes the same
// write gate a payload refusal does: the cache holds no image of its stores, so a write would lay the empty image over them.
#[test]
fn a_pull_refused_for_a_duplicate_index_or_a_retype_closes_every_write() {
    fn assert_gated(cache: &mut CultCache, path: &Path, door_label: &str) {
        let before = std::fs::read(path).unwrap();
        let errors = [
            ("put", cache.put("item:new", &unique("N")).unwrap_err()),
            ("put_raw_envelope", cache.put_raw_envelope(cache.prepare_entry("item:new", &unique("N")).unwrap().0).unwrap_err()),
            ("delete", cache.delete::<Unique>("item:keep").unwrap_err()),
        ];
        for (door, error) in errors {
            assert!(error.downcast_ref::<CultCacheLoadRefused>().is_some(), "{door_label} {door}: {error:#}");
        }
        assert_eq!(std::fs::read(path).unwrap(), before, "{door_label}: the store changed");
    }

    // Duplicate index value found in the stored image.
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("dup.cc");
    let mut cache = cache_at(&path);
    cache.put("item:keep", &unique("K")).unwrap();
    let mut twin = twin_cache_at(&path);
    twin.put("item:b", &Twin { sku: "S".into(), category: "c".into() }).unwrap();
    twin.put("item:a", &Twin { sku: "S".into(), category: "c".into() }).unwrap();
    assert!(error_text(cache.pull_all_backing_stores()).contains("both hold unique index 'Sku'"));
    assert_gated(&mut cache, &path, "duplicate index");

    // One key held under two types by two stores. The cache pulled cleanly first, so only the refused pull can close the gate.
    let (first, second) = (dir.path().join("first.cc"), dir.path().join("second.cc"));
    let mut cache = CultCache::new();
    cache.register_entry_type::<Plain>().unwrap();
    cache.register_entry_type::<Other>().unwrap();
    cache.register_entry_type::<Unique>().unwrap();
    cache.add_backing_store(SingleFileMessagePackBackingStore::new(&first), [Plain::TYPE]).unwrap();
    cache.add_backing_store(SingleFileMessagePackBackingStore::new(&second), [Other::TYPE]).unwrap();
    cache.pull_all_backing_stores().unwrap();
    cache.put("k", &Plain { value: "p".into() }).unwrap();
    let mut seed = CultCache::new();
    seed.register_entry_type::<Other>().unwrap();
    seed.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&second)).unwrap();
    seed.pull_all_backing_stores().unwrap();
    seed.put("k", &Other { sku: "o".into() }).unwrap();
    assert!(error_text(cache.pull_all_backing_stores()).contains("\"k\""));
    assert_gated(&mut cache, &first, "retype");
    assert_eq!(cache.get::<Plain>("k").unwrap().unwrap().value, "p", "a refused pull keeps the prior view");
}

#[test]
fn a_multi_index_store_with_a_duplicate_on_the_second_alias_refuses_to_open() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("multi.cc");
    {
        #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
        #[cultcache(type = "multi", schema = "Multi")]
        struct Role {
            #[cultcache(key = 0)]
            b: Option<String>,
            #[cultcache(key = 1)]
            a: Option<String>,
        }
        let mut cache = CultCache::new();
        cache.register_entry_type::<Role>().unwrap();
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
        cache.pull_all_backing_stores().unwrap();
        cache.put("m:1", &Role { b: Some("b".into()), a: Some("a1".into()) }).unwrap();
        cache.put("m:2", &Role { b: Some("b".into()), a: Some("a2".into()) }).unwrap();
    }
    let mut cache = CultCache::new();
    cache.register_entry_type::<Multi>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    assert_eq!(error_text(cache.pull_all_backing_stores()), refusal("m:1", "m:2", "Multi", "B"));
}

#[test]
fn a_bool_index_is_found_by_the_csharp_text() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<VBool>().unwrap();
    cache.put("t", &VBool { v: true }).unwrap();
    assert!(cache.put("u", &VBool { v: true }).is_err());
    cache.put("f", &VBool { v: false }).unwrap();
    assert_eq!(cache.get_by_index::<VBool>("V", "True").unwrap().unwrap().v, true);
    assert_eq!(cache.get_by_index::<VBool>("V", "False").unwrap().unwrap().v, false);
    assert_eq!(cache.get_by_index::<VBool>("V", "true").unwrap(), None);
    assert_eq!(cache.get_by_index::<VBool>("V", "false").unwrap(), None);
}

// ---- shared vectors, written by the C# reference ----

#[allow(dead_code)]
#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum Cell {
    Nil,
    Bool(bool),
    Int(i64),
    Uint(u64),
    Double(f64),
    Text(String),
}

fn vectors() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/vectors/unique-index")
}

type ValueRow = (String, String, Cell, Option<String>);

#[test]
fn vectors_values_give_the_csharp_index_text() {
    let bytes = std::fs::read(vectors().join("values.msgpack")).unwrap();
    let rows: Vec<ValueRow> = rmp_serde::from_slice(&bytes).unwrap();
    assert!(!rows.is_empty());
    let mut checked = 0;
    for (label, kind, value, text) in rows {
        let mut cache = CultCache::new();
        cache.register_entry_type::<VString>().unwrap();
        cache.register_entry_type::<VInt>().unwrap();
        cache.register_entry_type::<VUint>().unwrap();
        cache.register_entry_type::<VBool>().unwrap();
        // `double` rows are skipped: CultLookupValue has no float impl, so Rust cannot index a double member.
        match (kind.as_str(), value) {
            ("string", Cell::Text(held)) => {
                cache.put("one", &VString { v: held.clone() }).unwrap();
                match text {
                    Some(text) => {
                        assert_eq!(cache.get_by_index::<VString>("V", &text).unwrap().unwrap().v, held, "{label}");
                        assert!(cache.put("two", &VString { v: held }).is_err(), "{label}");
                    }
                    None => {
                        cache.put("two", &VString { v: held.clone() }).unwrap();
                        assert_eq!(cache.get_by_index::<VString>("V", &held).unwrap(), None, "{label}");
                    }
                }
            }
            ("int64", Cell::Int(held)) => {
                cache.put("one", &VInt { v: held }).unwrap();
                assert_eq!(cache.get_by_index::<VInt>("V", &text.unwrap()).unwrap().unwrap().v, held, "{label}");
                assert!(cache.put("two", &VInt { v: held }).is_err(), "{label}");
            }
            ("uint64", Cell::Uint(held)) => {
                cache.put("one", &VUint { v: held }).unwrap();
                assert_eq!(cache.get_by_index::<VUint>("V", &text.unwrap()).unwrap().unwrap().v, held, "{label}");
                assert!(cache.put("two", &VUint { v: held }).is_err(), "{label}");
            }
            ("uint64", Cell::Int(held)) => {
                cache.put("one", &VUint { v: held as u64 }).unwrap();
                assert_eq!(cache.get_by_index::<VUint>("V", &text.unwrap()).unwrap().unwrap().v, held as u64, "{label}");
            }
            ("int64", Cell::Uint(held)) => {
                cache.put("one", &VInt { v: held as i64 }).unwrap();
                assert_eq!(cache.get_by_index::<VInt>("V", &text.unwrap()).unwrap().unwrap().v, held as i64, "{label}");
            }
            ("bool", Cell::Bool(held)) => {
                cache.put("one", &VBool { v: held }).unwrap();
                assert_eq!(cache.get_by_index::<VBool>("V", &text.unwrap()).unwrap().unwrap().v, held, "{label}");
            }
            ("double", Cell::Double(_) | Cell::Int(_) | Cell::Uint(_) | Cell::Nil) => continue,
            (kind, value) => panic!("{label}: unexpected vector row {kind} {value:?}"),
        }
        checked += 1;
    }
    assert!(checked > 10, "the vectors were read, not skipped: {checked}");
}

type OutcomeRow = (String, Option<String>, String, Option<String>, Option<String>);

#[test]
fn vectors_stores_hold_their_verdicts() {
    let bytes = std::fs::read(vectors().join("outcomes.msgpack")).unwrap();
    let rows: Vec<OutcomeRow> = rmp_serde::from_slice(&bytes).unwrap();
    assert!(rows.len() >= 3);
    let dir = tempfile::tempdir().unwrap();
    for (file, alias, verdict, k1, k2) in rows {
        let path = dir.path().join(&file);
        std::fs::copy(vectors().join(&file), &path).unwrap();
        let before = std::fs::read(&path).unwrap();
        let attach = |cache: &mut CultCache| {
            cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
            cache.pull_all_backing_stores()
        };
        let mut cache = CultCache::new();
        let opened = match alias.as_deref() {
            Some("Sku") => {
                cache.register_entry_type::<Unique>().unwrap();
                attach(&mut cache)
            }
            None => {
                cache.register_entry_type::<Twin>().unwrap();
                attach(&mut cache)
            }
            Some(other) => panic!("unexpected vector alias {other}"),
        };
        match verdict.as_str() {
            "open" => opened.unwrap_or_else(|error| panic!("{file} / {alias:?} should open: {error:#}")),
            "refused" => assert_eq!(
                error_text(opened),
                refusal(&k1.unwrap(), &k2.unwrap(), "tests.unique_item", "Sku"),
                "{file}"
            ),
            other => panic!("unexpected verdict {other}"),
        }
        assert_eq!(std::fs::read(&path).unwrap(), before, "{file} is byte-identical");
    }
}

/// One op of a refusals row: `["open", file-or-nil]`, `["pull", file]` or `["commit", [[key, schema, sku, second], ...]]`.
#[derive(Deserialize)]
#[serde(untagged)]
enum Arg {
    Records(Vec<(String, String, String, String)>),
    File(Option<String>),
}

type RefusalRow = (String, Vec<(String, Arg)>, Option<(String, String, String, String)>);

/// Rows `[label, ops, refusal]`: the pair a refusal names is a function of the state the admission would leave
/// (`tests/vectors/unique-index/README.md`, RULE), whatever the staging, file or pull order, key case or alias case.
#[test]
fn vectors_refusals_hold_the_rule() {
    let bytes = std::fs::read(vectors().join("refusals.msgpack")).unwrap();
    let rows: Vec<RefusalRow> = rmp_serde::from_slice(&bytes).unwrap();
    let dir = tempfile::tempdir().unwrap();
    let mut walked = 0;
    for (index, (label, ops, refused)) in rows.into_iter().enumerate() {
        let path = dir.path().join(format!("row-{index}.cc"));
        let mut cache: Option<CultCache> = None;
        let mut failure = None;
        for (at, (kind, arg)) in ops.iter().enumerate() {
            let last = at + 1 == ops.len();
            let mut before = path.exists().then(|| std::fs::read(&path).unwrap());
            let result = match (kind.as_str(), arg) {
                ("open", Arg::File(file)) => {
                    if let Some(file) = file {
                        std::fs::copy(vectors().join(file), &path).unwrap();
                    }
                    let mut opened = CultCache::new();
                    opened.register_entry_type::<Unique>().unwrap();
                    opened.register_entry_type::<UqPair>().unwrap();
                    opened.register_entry_type::<UqAlias>().unwrap();
                    opened.register_entry_type::<UqWide>().unwrap();
                    opened.register_entry_type::<UqAlpha>().unwrap();
                    opened.register_entry_type::<UqBeta>().unwrap();
                    opened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
                    let pulled = opened.pull_all_backing_stores();
                    cache = Some(opened);
                    pulled
                }
                ("pull", Arg::File(Some(file))) => {
                    std::fs::copy(vectors().join(file), &path).unwrap();
                    before = Some(std::fs::read(&path).unwrap());
                    cache.as_mut().unwrap().pull_all_backing_stores()
                }
                ("commit", Arg::Records(records)) => {
                    let cache = cache.as_mut().unwrap();
                    let staged: Vec<_> = records
                        .iter()
                        .map(|(key, schema, sku, second)| {
                            let (sku, second) = (sku.clone(), second.clone());
                            match schema.as_str() {
                                "tests.unique_item" => cache.prepare_entry(key, &Unique { sku, category: second }).map(|(envelope, _)| envelope),
                                "tests.uq_pair" => cache.prepare_entry(key, &UqPair { sku, alt: second }).map(|(envelope, _)| envelope),
                                "tests.uq_alias" => cache.prepare_entry(key, &UqAlias { sku, alt: second }).map(|(envelope, _)| envelope),
                                "tests.uq_wide" => cache.prepare_entry(key, &UqWide { sku, alt: second }).map(|(envelope, _)| envelope),
                                "tests.uq_alpha" => cache.prepare_entry(key, &UqAlpha { sku, second }).map(|(envelope, _)| envelope),
                                "tests.uq_beta" => cache.prepare_entry(key, &UqBeta { sku, second }).map(|(envelope, _)| envelope),
                                other => panic!("{label}: unexpected schema {other}"),
                            }
                            .unwrap()
                        })
                        .collect();
                    cache.put_prepared_batch(staged)
                }
                other => panic!("{label}: unexpected op {:?}", other.0),
            };
            if last {
                failure = Some((result, before));
            } else {
                result.unwrap_or_else(|error| panic!("{label}: op {at} should succeed: {error:#}"));
            }
        }
        let (result, before) = failure.unwrap();
        match refused {
            None => result.unwrap_or_else(|error| panic!("{label}: should succeed: {error:#}")),
            Some((k1, k2, schema, alias)) => {
                let error = result.err().unwrap_or_else(|| panic!("{label}: should refuse"));
                assert_eq!(format!("{error:#}"), refusal(&k1, &k2, &schema, &alias), "{label}");
                if let Some(before) = before {
                    assert_eq!(std::fs::read(&path).unwrap(), before, "{label}: the file is byte-identical");
                }
            }
        }
        walked += 1;
    }
    assert_eq!(walked, 37);
}

// A pull replaces the holder table with the stored image's: a value another writer changed is released, not kept.
#[test]
fn a_second_pull_replaces_the_holders_with_the_stored_image() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("pull.cc");
    let mut cache = cache_at(&path);
    cache.put("a", &unique("S")).unwrap();

    cache_at(&path).put("a", &unique("T")).unwrap();
    cache.pull_all_backing_stores().unwrap();

    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), None, "the replaced value is no longer held");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "T").unwrap(), cache.get::<Unique>("a").unwrap());
    cache.put("b", &unique("S")).unwrap();
    assert!(cache.put("c", &unique("T")).is_err());
}

// The projector decides what held records hold: declaring an index for a type that already has records derives
// their holders, or refuses the registration when they collide.
#[test]
fn registering_an_index_over_held_records_derives_their_holders() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Twin>().unwrap();
    cache.put("a", &Twin { sku: "S".into(), category: "x".into() }).unwrap();
    cache.put("b", &Twin { sku: "T".into(), category: "x".into() }).unwrap();

    cache.register_entry_type::<Unique>().unwrap();

    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), cache.get::<Unique>("a").unwrap());
    assert!(cache.get_by_index::<Unique>("Sku", "S").unwrap().is_some());
    assert!(cache.put("c", &unique("S")).is_err(), "a held value is refused");
    cache.put("c", &unique("U")).unwrap();
}

#[test]
fn registering_an_index_over_colliding_held_records_is_refused_and_changes_nothing() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Twin>().unwrap();
    cache.put("a", &Twin { sku: "S".into(), category: "x".into() }).unwrap();
    cache.put("b", &Twin { sku: "S".into(), category: "y".into() }).unwrap();

    let text = error_text(cache.register_entry_type::<Unique>());
    assert_eq!(text, refusal("a", "b", "tests.unique_item", "Sku"));

    // Still the unindexed declaration: duplicates keep landing, nothing is looked up.
    cache.put("c", &Twin { sku: "S".into(), category: "z".into() }).unwrap();
    assert_eq!(cache.get_all_with_keys::<Twin>().unwrap().len(), 3);
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), None);
}

/// The same type and schema declaring Sku as an integer: the held text cannot be decoded as one.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "unique_item", schema = "tests.unique_item")]
struct Retyped {
    #[cultcache(key = 0, index = "Sku")]
    sku: i64,
}

/// A refused re-registration leaves the previous declaration in force: it keeps judging, so a later duplicate is refused.
#[test]
fn a_refused_reregistration_keeps_the_previous_index_judging() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Unique>().unwrap();
    cache.put("a", &unique("S")).unwrap();
    cache.put("b", &unique("T")).unwrap();

    // A different declaration over the same type (Category is the unique alias) collides on held records.
    #[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
    #[cultcache(type = "unique_item", schema = "tests.unique_item")]
    struct ByCat {
        #[cultcache(key = 0)]
        sku: String,
        #[cultcache(key = 1, index = "Cat")]
        category: String,
    }
    assert!(cache.register_entry_type::<ByCat>().is_err(), "a and b both hold category c");

    assert!(cache.put("z", &unique("S")).is_err(), "the previous index still judges");
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), cache.get::<Unique>("a").unwrap());
    cache.put("z", &unique("U")).unwrap();
}

// A held record the new declaration cannot decode refuses the registration; the unindexed declaration stays.
#[test]
fn registering_an_index_over_an_undecodable_held_record_is_refused_and_changes_nothing() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Twin>().unwrap();
    cache.put("a", &Twin { sku: "S".into(), category: "x".into() }).unwrap();

    assert!(cache.register_entry_type::<Retyped>().is_err());

    cache.put("b", &Twin { sku: "S".into(), category: "y".into() }).unwrap();
    assert_eq!(cache.get_all_with_keys::<Twin>().unwrap().len(), 2, "no index judges the duplicate");
}

// Dropping the declaration releases the holders.
#[test]
fn registering_without_the_index_releases_the_holders() {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Unique>().unwrap();
    cache.put("a", &unique("S")).unwrap();
    cache.register_entry_type::<Twin>().unwrap();
    assert_eq!(cache.get_by_index::<Unique>("Sku", "S").unwrap(), None, "no declaration, no holders");
    cache.put("b", &Twin { sku: "S".into(), category: "x".into() }).unwrap();
    assert!(cache.register_entry_type::<Unique>().is_err(), "the duplicate now written is a held violation");
}
