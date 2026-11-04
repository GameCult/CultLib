//! The Rust CultCache first call from docs/start/README.md. The docs show the region between the
//! `snippet` markers verbatim, and `docs_show_the_tested_snippet` fails when they differ.

use cultcache_rs::{CultCache, DatabaseEntry, SingleFileMessagePackBackingStore};

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

#[test]
fn first_call_round_trips_through_a_second_cache() {
    first_call().unwrap();
}

#[test]
fn docs_show_the_tested_snippet() {
    let source = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/tests/first_call.rs")).unwrap();
    let begin = source.find("// snippet:begin\n").unwrap() + "// snippet:begin\n".len();
    let end = source.find("// snippet:end\n").unwrap();
    let docs = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../../docs/start/README.md")).unwrap();
    let block = format!("```rust\n{}```", &source[begin..end]);
    assert!(docs.contains(&block), "docs/start/README.md no longer shows tests/first_call.rs verbatim");
}
