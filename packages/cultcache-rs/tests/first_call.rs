//! The Rust CultCache first call and update from docs/start/README.md. The docs show each region between its
//! `snippet` markers verbatim, and `docs_show_the_tested_snippets` fails when they differ.

use std::path::Path;

use cultcache_rs::{CultCache, CultUpdateResult, DatabaseEntry, SingleFileMessagePackBackingStore};

// snippet:begin
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "item")]
struct Item {
    #[cultcache(key = 0)]
    name: String,
    #[cultcache(key = 1)]
    category: String,
    #[cultcache(key = 2, default)]
    value: u32,
}

fn first_call() -> anyhow::Result<()> {
    let dir = tempfile::tempdir()?;
    let path = dir.path().join("first-call.cc");

    let mut cache = CultCache::new();
    cache.register_entry_type::<Item>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path));
    cache.pull_all_backing_stores()?;
    cache.put("item:potion", &Item {
        name: "Potion".to_string(),
        category: "Consumable".to_string(),
        value: 50,
    })?; // written to the .cc file before this returns

    let mut reopened = CultCache::new();
    reopened.register_entry_type::<Item>()?;
    reopened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path));
    reopened.pull_all_backing_stores()?; // read it back
    let potion = reopened.get_required::<Item>("item:potion")?;
    assert_eq!(potion.name, "Potion");
    assert_eq!(potion.value, 50);
    Ok(())
}
// snippet:end

// snippet:update:begin
fn restock(path: &Path) -> anyhow::Result<CultUpdateResult<Item>> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Item>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path))?;
    cache.pull_all_backing_stores()?;
    cache.update::<Item, _>("item:potion", |item| {
        // The store's copy of the record, or nothing if it has none; this may run again on a lost race.
        let mut item = item.unwrap_or(Item {
            name: "Potion".to_string(),
            category: "Consumable".to_string(),
            value: 0,
        });
        item.value += 10;
        Some(item)
    })
}
// snippet:update:end

#[test]
fn first_call_round_trips_through_a_second_cache() {
    first_call().unwrap();
}

#[test]
fn restock_twice_reads_twenty_through_a_second_cache() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("restock.cc");
    assert_eq!(restock(&path).unwrap().outcome, cultcache_rs::CultUpdateOutcome::Updated);
    let second = restock(&path).unwrap();
    assert_eq!(second.outcome, cultcache_rs::CultUpdateOutcome::Updated);
    assert_eq!(second.document.unwrap().value, 20);

    let mut reopened = CultCache::new();
    reopened.register_entry_type::<Item>().unwrap();
    reopened.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    reopened.pull_all_backing_stores().unwrap();
    assert_eq!(reopened.get_required::<Item>("item:potion").unwrap().value, 20);
}

/// The docs show the region between `// snippet:<region>begin` and `end` of this file as one fenced block.
fn docs_show_region(region: &str) {
    let source = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/tests/first_call.rs")).unwrap();
    let (begin_marker, end_marker) = (format!("// snippet:{region}begin\n"), format!("// snippet:{region}end\n"));
    let begin = source.find(&begin_marker).unwrap() + begin_marker.len();
    let end = source.find(&end_marker).unwrap();
    let docs = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../../docs/start/README.md")).unwrap();
    let block = format!("```rust\n{}```", &source[begin..end]);
    assert!(docs.contains(&block), "docs/start/README.md no longer shows the {region:?} region of tests/first_call.rs verbatim");
}

#[test]
fn docs_show_the_tested_snippets() {
    docs_show_region("");
    docs_show_region("update:");
}
