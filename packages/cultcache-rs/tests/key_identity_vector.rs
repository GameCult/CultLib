//! `tests/vectors/key-identity`, written only by the C# reference: a cache holds one record per key whatever its type, and a
//! store holding a record no registered type claims loads, lists it as foreign and keeps its bytes
//! (`variants:ruling:cache-refuses-retype`, `write-ownership-by-written-type`, `unheld-removal-refused-at-commit`).
//! Every row is walked against this cache: each put row twice, once as `put` and once as a one-entry `put_prepared_batch`.

use std::path::{Path, PathBuf};

use cultcache_rs::{
    CacheBackingStore, CultCache, DatabaseEntry, SchemaConflictError, SingleFileMessagePackBackingStore,
};
use serde_json::Value;

// The ids C# writes in the store catalogs and on every record (the vector's README).
#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "sha256:b20703a98f49c253898404ab50ba2c630720f0b8a5e1a41614d5650be22799fe", schema = "tests.ki_alpha")]
struct Alpha {
    #[cultcache(key = 0)]
    text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "sha256:dee4b727cf2ee04e61ab635ff0c5f3dca7345a411dc5f1018e583ab16009cf94", schema = "tests.ki_beta")]
struct Beta {
    #[cultcache(key = 0)]
    text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "sha256:fbc26e351e00c9e7185e8de171c7486aba707c229c949f0aae6b889288af5f6d", schema = "tests.ki_gamma")]
struct Gamma {
    #[cultcache(key = 0)]
    text: String,
}

fn vectors() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/vectors/key-identity")
}

/// `[refusal key or nil, held [type, key, text], file [key, schema name] or nil, foreign keys, kept foreign keys]`.
type Expect = (Option<Vec<String>>, Vec<(String, String, String)>, Option<Vec<(String, String)>>, Vec<String>, Vec<String>);
type Row = (String, Vec<String>, Vec<Vec<Value>>, Expect);

fn text(value: &Value) -> &str {
    value.as_str().unwrap()
}

fn register(cache: &mut CultCache, names: &[String]) {
    for name in names {
        match name.as_str() {
            "alpha" => drop(cache.register_entry_type::<Alpha>().unwrap()),
            "beta" => drop(cache.register_entry_type::<Beta>().unwrap()),
            "gamma" => drop(cache.register_entry_type::<Gamma>().unwrap()),
            other => panic!("unknown type {other}"),
        }
    }
}

fn short(entry_type: &str) -> &'static str {
    match entry_type {
        t if t == Alpha::TYPE => "alpha",
        t if t == Beta::TYPE => "beta",
        t if t == Gamma::TYPE => "gamma",
        other => panic!("unknown entry type {other}"),
    }
}

fn put(cache: &mut CultCache, as_batch: bool, kind: &str, key: &str, body: &str) -> anyhow::Result<()> {
    macro_rules! write {
        ($t:ident) => {{
            let value = $t { text: body.to_string() };
            if as_batch {
                let (entry, _) = cache.prepare_entry(key, &value)?;
                cache.put_prepared_batch(vec![entry])
            } else {
                cache.put(key, &value).map(drop)
            }
        }};
    }
    match kind {
        "alpha" => write!(Alpha),
        "beta" => write!(Beta),
        "gamma" => write!(Gamma),
        other => panic!("unknown type {other}"),
    }
}

fn delete(cache: &mut CultCache, kind: &str, key: &str) -> anyhow::Result<bool> {
    match kind {
        "alpha" => cache.delete::<Alpha>(key),
        "beta" => cache.delete::<Beta>(key),
        "gamma" => cache.delete::<Gamma>(key),
        other => panic!("unknown type {other}"),
    }
}

fn open_cache(registered: &[String], path: Option<&Path>) -> anyhow::Result<CultCache> {
    let mut cache = CultCache::new();
    register(&mut cache, registered);
    if let Some(path) = path {
        cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path))?;
        cache.pull_all_backing_stores()?;
    }
    Ok(cache)
}

/// The records a store file holds, read by a fresh store: (key, schema name, schema id, stored-at, payload).
fn file_records(path: &Path) -> Vec<(String, String, Option<String>, String, Vec<u8>)> {
    let mut records: Vec<_> = SingleFileMessagePackBackingStore::new(path)
        .pull_all()
        .unwrap()
        .into_iter()
        .map(|entry| (entry.key, entry.r#type, entry.schema_id, entry.stored_at, entry.payload))
        .collect();
    records.sort_by(|left, right| left.0.encode_utf16().cmp(right.0.encode_utf16()));
    records
}

fn walk(row: &Row, as_batch: bool) -> Result<(), String> {
    let (label, registered, ops, expect) = row;
    let dir = tempfile::tempdir().unwrap();
    let mut path: Option<PathBuf> = None;
    let mut before = Vec::new();
    let mut cache: Option<CultCache> = None;
    let mut refused: Option<anyhow::Error> = None;
    let mut refused_key: Option<String> = None;
    let mut shared_key: Option<String> = None;
    let mut wrote = false;
    for (index, op) in ops.iter().enumerate() {
        let last = index == ops.len() - 1;
        let result: anyhow::Result<()> = match text(&op[0]) {
            "open" => {
                drop(cache.take());
                match &op[1] {
                    Value::String(file) if file == "memory" => open_cache(registered, None).map(|opened| cache = Some(opened)),
                    file => {
                        let fresh = dir.path().join("store.cc");
                        if let Value::String(file) = file {
                            std::fs::copy(vectors().join(file), &fresh).unwrap();
                        }
                        before = if fresh.exists() { file_records(&fresh) } else { Vec::new() };
                        let opened = open_cache(registered, Some(&fresh));
                        path = Some(fresh);
                        opened.map(|opened| cache = Some(opened))
                    }
                }
            }
            "reopen" => {
                drop(cache.take());
                open_cache(registered, path.as_deref()).map(|opened| cache = Some(opened))
            }
            "attach" => {
                let file = text(&op[1]);
                let attached = dir.path().join(file);
                std::fs::copy(vectors().join(file), &attached).unwrap();
                let held: Vec<String> = cache.as_ref().unwrap().snapshot().into_iter().map(|entry| entry.key).collect();
                let shared: Vec<String> = file_records(&attached).into_iter().map(|record| record.0).filter(|key| held.contains(key)).collect();
                let home: Vec<String> = op[2].as_array().unwrap().iter().map(|name| short_to_type(text(name)).to_string()).collect();
                let cache = cache.as_mut().unwrap();
                let attach = cache
                    .add_backing_store(SingleFileMessagePackBackingStore::new(&attached), home)
                    .and_then(|()| cache.pull_all_backing_stores());
                if attach.is_err() && last && shared.len() == 1 {
                    refused_key = Some(shared[0].clone());
                    shared_key = refused_key.clone();
                    assert_eq!(std::fs::read(&attached).unwrap(), std::fs::read(vectors().join(file)).unwrap(), "{label}: a refused attach leaves the store as it was");
                }
                attach
            }
            "put" => {
                wrote = true;
                put(cache.as_mut().unwrap(), as_batch, text(&op[1]), text(&op[2]), text(&op[3]))
            }
            "delete" => delete(cache.as_mut().unwrap(), text(&op[1]), text(&op[2])).map(drop),
            other => panic!("{label}: unknown op {other}"),
        };
        match result {
            Ok(()) => {}
            Err(error) if last => refused = Some(error),
            Err(error) => return Err(format!("{label}: op {index} failed: {error:#}")),
        }
    }
    if !wrote && as_batch {
        return Ok(()); // not a write row: the batch variant is the same walk
    }
    let Some(cache) = cache.as_ref() else {
        return Err(format!("{label} (batch {as_batch}): the cache did not open: {:#}", refused.unwrap()));
    };
    let last = ops.last().unwrap();
    if let Some(error) = &refused {
        if let Some(conflict) = error.downcast_ref::<SchemaConflictError>() {
            refused_key = Some(conflict.record_key.clone());
            let named = if text(&last[0]) == "attach" { shared_key.clone().unwrap() } else { text(&last[2]).to_string() };
            assert_eq!(named, conflict.record_key, "{label}: the refusal names the key written, removed or shared");
        }
        let key = refused_key.clone().unwrap_or_default();
        assert!(format!("{error:#}").contains(&key) && !key.is_empty(), "{label}: the refusal names the key: {error:#}");
    }
    let mut held: Vec<(String, String, String)> = cache
        .snapshot()
        .into_iter()
        .map(|entry| {
            let body = match short(&entry.r#type) {
                "alpha" => cache.get::<Alpha>(&entry.key).unwrap().unwrap().text,
                "beta" => cache.get::<Beta>(&entry.key).unwrap().unwrap().text,
                _ => cache.get::<Gamma>(&entry.key).unwrap().unwrap().text,
            };
            (short(&entry.r#type).to_string(), entry.key, body)
        })
        .collect();
    held.sort_by(|a, b| (a.0.as_str(), a.1.encode_utf16().collect::<Vec<_>>()).cmp(&(b.0.as_str(), b.1.encode_utf16().collect::<Vec<_>>())));
    let foreign: Vec<String> = cache.foreign_records().into_iter().map(|record| record.key).collect();
    let (file, kept) = match &path {
        None => (None, Vec::new()),
        Some(path) => {
            let after = file_records(path);
            let kept = foreign
                .iter()
                .filter(|key| {
                    let original = before.iter().find(|record| &record.0 == *key);
                    let now = after.iter().find(|record| &record.0 == *key);
                    original.is_some() && original.map(|r| (&r.2, &r.3, &r.4)) == now.map(|r| (&r.2, &r.3, &r.4))
                })
                .cloned()
                .collect();
            // This writer names a catalog entry by the record's type, which for these types is the id; C# names it by the
            // schema name. Which type the record at a key is, is the verdict, so the id reads as its schema name.
            let name = |described: String| match described.as_str() {
                t if t == Alpha::TYPE => Alpha::SCHEMA_NAME.to_string(),
                t if t == Beta::TYPE => Beta::SCHEMA_NAME.to_string(),
                t if t == Gamma::TYPE => Gamma::SCHEMA_NAME.to_string(),
                _ => described,
            };
            (Some(after.into_iter().map(|record| (record.0, name(record.1))).collect::<Vec<_>>()), kept)
        }
    };
    let observed: Expect = (refused_key.map(|key| vec![key]), held, file, foreign, kept);
    if &observed == expect { Ok(()) } else { Err(format!("{label} (batch {as_batch}): committed {expect:?} but this cache answers {observed:?}")) }
}

fn short_to_type(name: &str) -> &'static str {
    match name {
        "alpha" => Alpha::TYPE,
        "beta" => Beta::TYPE,
        "gamma" => Gamma::TYPE,
        other => panic!("unknown type {other}"),
    }
}

#[test]
fn key_identity_rows_hold_their_verdicts() {
    let bytes = std::fs::read(vectors().join("rows.msgpack")).unwrap();
    let rows: Vec<Row> = rmp_serde::from_slice(&bytes).unwrap();
    let mut walked = 0;
    let mut failed = Vec::new();
    for row in &rows {
        let mut ok = true;
        for as_batch in [false, true] {
            if let Err(message) = walk(row, as_batch) {
                failed.push(message);
                ok = false;
            }
        }
        walked += usize::from(ok);
    }
    assert!(failed.is_empty(), "{}", failed.join("\n"));
    assert_eq!(walked, 11, "rows walked");
}
