//! Lookups parity with the C# reference: `GetByName`, `GetByIndex` and `GetGlobal`
//! (`src/GameCult.Caching/CultCache.cs`) and the one-global rule its commit enforces.

use cultcache_rs::{
    CacheBackingStore, CultCache, CultCacheSecondGlobal, CultCacheSoaTable, DatabaseEntry,
    SingleFileMessagePackBackingStore,
};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "item")]
struct Item {
    #[cultcache(key = 0, name)]
    name: String,
    #[cultcache(key = 1)]
    category: String,
    #[cultcache(key = 2, index = "sku")]
    code: Option<String>,
    #[cultcache(key = 3, default)]
    value: u32,
}

fn item(name: &str, category: &str, code: Option<&str>) -> Item {
    Item { name: name.into(), category: category.into(), code: code.map(Into::into), value: 0 }
}

/// Same names and values as `Item`, a different type: lookups never cross types.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "gadget")]
struct Gadget {
    #[cultcache(key = 0, name)]
    name: String,
    #[cultcache(key = 1, index)]
    category: String,
}

/// Sorts after `Item` and has its exact shape: a lookup that ran past the end of its own type would answer from it.
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "zed")]
struct Zed {
    #[cultcache(key = 0, name)]
    name: String,
    #[cultcache(key = 1)]
    category: String,
    #[cultcache(key = 2, index = "sku")]
    code: Option<String>,
    #[cultcache(key = 3, default)]
    value: u32,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "settings", global)]
struct Settings {
    #[cultcache(key = 0)]
    value: String,
}

fn settings(value: &str) -> Settings {
    Settings { value: value.into() }
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "plain")]
struct Plain {
    #[cultcache(key = 0)]
    value: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "numbered")]
struct Numbered {
    #[cultcache(key = 0, name)]
    id: u32,
    #[cultcache(key = 1, index)]
    owner: u64,
}

fn cache() -> CultCache {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Item>().unwrap();
    cache.register_entry_type::<Gadget>().unwrap();
    cache.register_entry_type::<Zed>().unwrap();
    cache.register_entry_type::<Settings>().unwrap();
    cache.register_entry_type::<Plain>().unwrap();
    cache.register_entry_type::<Numbered>().unwrap();
    cache
}

fn reopened(path: &std::path::Path) -> CultCache {
    let mut reopened = cache();
    reopened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path)).unwrap();
    reopened.pull_all_backing_stores().unwrap();
    reopened
}

fn stored_cache(path: &std::path::Path) -> CultCache {
    let mut stored = cache();
    stored.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path)).unwrap();
    stored.pull_all_backing_stores().unwrap();
    stored
}

// The C# FirstCall snippet: a record written to a .cc file is found by its [CultName] through a second cache.
#[test]
fn a_record_is_found_by_name_and_by_index_after_a_reopen() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("lookups.cc");
    let mut written = stored_cache(&path);
    written.put("item:potion", &Item { value: 50, ..item("Potion", "Consumable", Some("P-1")) }).unwrap();

    let reopened = reopened(&path);
    let by_name = reopened.get_by_name::<Item>("Potion").unwrap().unwrap();
    assert_eq!(by_name.value, 50);
    assert_eq!(reopened.get_by_index::<Item>("sku", "P-1").unwrap(), Some(by_name));
}

#[test]
fn a_missing_name_or_value_is_none_and_an_unregistered_type_is_refused() {
    let mut cache = cache();
    cache.put("item:a", &item("A", "cat", Some("a-1"))).unwrap();

    assert_eq!(cache.get_by_name::<Item>("B").unwrap(), None);
    assert_eq!(cache.get_by_name::<Item>("a").unwrap(), None, "names compare ordinally");
    assert_eq!(cache.get_by_index::<Item>("sku", "other").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("nosuchalias", "a-1").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("category", "cat").unwrap(), None, "category is a grouping, not an index");
    assert_eq!(cache.get_by_index::<Item>("code", "a-1").unwrap(), None, "the alias is sku, not the member name");

    let bare = CultCache::new();
    assert!(bare.get_by_name::<Item>("A").is_err());
    assert!(bare.get_by_index::<Item>("sku", "a-1").is_err());
    assert!(bare.get_global::<Settings>().is_err());
}

#[test]
fn an_index_without_an_alias_is_named_by_its_member() {
    let mut cache = cache();
    cache.put("numbered:1", &Numbered { id: 1, owner: 7 }).unwrap();
    assert!(cache.get_by_index::<Numbered>("owner", "7").unwrap().is_some());
}

// A blank name is no name; a blank index value is not indexed; a nil member holds no value.
#[test]
fn blank_and_nil_values_are_not_found() {
    let mut cache = cache();
    cache.put("item:blank", &item("", "  ", Some("  "))).unwrap();
    assert_eq!(cache.get_by_name::<Item>("").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("sku", "  ").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("sku", "").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("sku", "").unwrap(), None);

    // Only emptiness voids a name: whitespace is a name, as in C# (Length > 0).
    cache.put("item:space", &item(" ", "x", None)).unwrap();
    assert_eq!(cache.get_by_name::<Item>(" ").unwrap().unwrap().category, "x");
}

#[test]
fn lookups_never_cross_types() {
    let mut cache = cache();
    cache.put("item:a", &item("same", "same", Some("same"))).unwrap();
    cache.put("gadget:a", &Gadget { name: "same".into(), category: "same".into() }).unwrap();
    cache.put("zed:a", &Zed { name: "same".into(), category: "same".into(), code: None, value: 0 }).unwrap();

    assert_eq!(cache.get_by_name::<Gadget>("same").unwrap().unwrap().name, "same");
    cache.delete::<Item>("item:a").unwrap();
    assert_eq!(cache.get_by_name::<Item>("same").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("sku", "same").unwrap(), None);
    assert!(cache.get_by_name::<Gadget>("same").unwrap().is_some());
    assert!(cache.get_by_index::<Gadget>("category", "same").unwrap().is_some());
}

#[test]
fn a_name_and_index_follow_a_rewritten_record_and_leave_a_removed_one() {
    let mut cache = cache();
    cache.put("item:a", &item("old", "cat", Some("c1"))).unwrap();
    cache.put("item:a", &item("new", "cat", Some("c2"))).unwrap();

    assert_eq!(cache.get_by_name::<Item>("old").unwrap(), None, "a stale name is not found");
    assert_eq!(cache.get_by_index::<Item>("sku", "c1").unwrap(), None, "a stale value is not found");
    assert!(cache.get_by_name::<Item>("new").unwrap().is_some());
    assert!(cache.get_by_index::<Item>("sku", "c2").unwrap().is_some());

    cache.delete::<Item>("item:a").unwrap();
    assert_eq!(cache.get_by_name::<Item>("new").unwrap(), None);
    assert_eq!(cache.get_by_index::<Item>("sku", "c2").unwrap(), None);
}

#[test]
fn non_string_members_are_found_by_their_text() {
    let mut cache = cache();
    let owner = 161_u64;
    cache.put("numbered:1", &Numbered { id: 42, owner }).unwrap();
    assert!(cache.get_by_name::<Numbered>("42").unwrap().is_some());
    assert!(cache.get_by_index::<Numbered>("owner", "161").unwrap().is_some());
}

#[test]
fn records_sharing_a_name_find_the_smallest_key() {
    let mut cache = cache();
    cache.put("item:b", &Item { value: 2, ..item("twin", "c", None) }).unwrap();
    cache.put("item:a", &Item { value: 1, ..item("twin", "c", None) }).unwrap();
    assert_eq!(cache.get_by_name::<Item>("twin").unwrap().unwrap().value, 1);
    cache.delete::<Item>("item:a").unwrap();
    assert_eq!(cache.get_by_name::<Item>("twin").unwrap().unwrap().value, 2);
}

// BackingStoreTests.HydratesPersistedGlobal: opening a store invents no global; a written one survives a reopen.
#[test]
fn a_global_is_none_until_written_and_survives_a_reopen() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("global.cc");
    {
        let mut seed = stored_cache(&path);
        assert_eq!(seed.get_global::<Settings>().unwrap(), None, "opening a store invented a global");
        seed.put("global:settings", &settings("durable authority")).unwrap();
        assert_eq!(seed.get_global::<Settings>().unwrap(), Some(settings("durable authority")));
    }
    assert_eq!(reopened(&path).get_global::<Settings>().unwrap(), Some(settings("durable authority")));
}

#[test]
fn a_type_that_is_not_a_global_has_no_global() {
    let mut cache = cache();
    cache.put("plain:one", &Plain { value: "v".into() }).unwrap();
    assert_eq!(cache.get_global::<Plain>().unwrap(), None);
}

#[test]
fn a_second_global_under_another_key_is_refused_and_writes_nothing() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("second.cc");
    let mut cache = stored_cache(&path);
    cache.put("g1", &settings("one")).unwrap();

    let refusal = cache.put("g2", &settings("two")).unwrap_err();
    let second = refusal.downcast_ref::<CultCacheSecondGlobal>().expect("a typed refusal");
    assert_eq!((second.existing_key.as_str(), second.key.as_str()), ("g1", "g2"));
    assert!(refusal.to_string().contains("is a global and already has record g1; g2 would be a second."));
    assert_eq!(cache.get_global::<Settings>().unwrap(), Some(settings("one")));
    assert_eq!(reopened(&path).get_global::<Settings>().unwrap(), Some(settings("one")), "nothing reached the store");

    // The same key is the same record, not a second.
    cache.put("g1", &settings("one again")).unwrap();
    assert_eq!(cache.get_global::<Settings>().unwrap(), Some(settings("one again")));
}

// RetypeEvictionTests.EvictingAGlobalFreesItsSlot: removing the global frees the slot for another key.
#[test]
fn removing_a_global_frees_its_slot() {
    let mut cache = cache();
    cache.put("g1", &settings("one")).unwrap();
    assert!(cache.put("g2", &settings("two")).is_err());
    cache.delete::<Settings>("g1").unwrap();
    assert_eq!(cache.get_global::<Settings>().unwrap(), None);
    cache.put("g2", &settings("two")).unwrap();
    assert_eq!(cache.get_global::<Settings>().unwrap(), Some(settings("two")));
}

#[test]
fn every_write_path_refuses_a_second_global() {
    let mut cache = cache();
    cache.put("g1", &settings("one")).unwrap();
    let (second, _) = cache.prepare_entry("g2", &settings("two")).unwrap();

    assert!(cache.put_envelope::<Settings>(second.clone()).unwrap_err().is::<CultCacheSecondGlobal>());
    assert!(cache.put_raw_envelope(second.clone()).unwrap_err().is::<CultCacheSecondGlobal>());
    let (plain, _) = cache.prepare_entry("plain:1", &Plain { value: "v".into() }).unwrap();
    assert!(cache.put_prepared_batch(vec![plain, second]).unwrap_err().is::<CultCacheSecondGlobal>());
    assert_eq!(cache.get::<Plain>("plain:1").unwrap(), None, "a refused batch lands none of it");
    assert_eq!(cache.get_global::<Settings>().unwrap(), Some(settings("one")));
}

#[test]
fn a_batch_that_stages_two_globals_is_refused_whole() {
    let mut cache = cache();
    let (one, _) = cache.prepare_entry("g1", &settings("one")).unwrap();
    let (two, _) = cache.prepare_entry("g2", &settings("two")).unwrap();
    assert!(cache.put_prepared_batch(vec![one, two]).unwrap_err().is::<CultCacheSecondGlobal>());
    assert_eq!(cache.get_global::<Settings>().unwrap(), None);
}

// StoreRoutingTests: a store holding two globals is refused on load rather than answering one of them.
#[test]
fn loading_a_store_that_holds_two_globals_is_refused() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("two.cc");
    let source = cache();
    let mut store = SingleFileMessagePackBackingStore::new(&path);
    for (key, value) in [("g1", "one"), ("g2", "two")] {
        let (entry, _) = source.prepare_entry(key, &settings(value)).unwrap();
        store.push(&entry).unwrap();
    }

    let mut loading = cache();
    loading.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    assert!(loading.pull_all_backing_stores().unwrap_err().is::<CultCacheSecondGlobal>());
    assert_eq!(loading.get_global::<Settings>().unwrap(), None, "a refused load installs nothing");
}

// load_soa installs a whole table at once, so the one-global rule has to hold there too: a table holding two
// records of a global type is refused whole and the prior view survives.
#[test]
fn loading_an_soa_table_that_holds_two_globals_is_refused() {
    let source = cache();
    let (first, _) = source.prepare_entry("g1", &settings("one")).unwrap();
    let (second, _) = source.prepare_entry("g2", &settings("two")).unwrap();

    let mut loading = cache();
    loading.put("g0", &settings("prior")).unwrap();
    loading.put("plain:1", &Plain { value: "prior".into() }).unwrap();

    let refusal = loading
        .load_soa(CultCacheSoaTable::from_envelopes(vec![first.clone(), second]))
        .unwrap_err();
    let second_global = refusal.downcast_ref::<CultCacheSecondGlobal>().expect("a typed refusal");
    let mut named = [second_global.existing_key.as_str(), second_global.key.as_str()];
    named.sort();
    assert_eq!(named, ["g1", "g2"], "the refusal names both records");
    assert_eq!(loading.get_global::<Settings>().unwrap(), Some(settings("prior")), "a refused load keeps the prior view");
    assert_eq!(loading.get::<Plain>("plain:1").unwrap(), Some(Plain { value: "prior".into() }));

    // One global in the table is the rule's own case, not a second.
    loading.load_soa(CultCacheSoaTable::from_envelopes(vec![first])).unwrap();
    assert_eq!(loading.get_global::<Settings>().unwrap(), Some(settings("one")));
    assert_eq!(loading.get::<Plain>("plain:1").unwrap(), None, "a load replaces the view");
}
