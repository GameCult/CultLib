use anyhow::Result;
use anyhow::anyhow;
use cultcache_rs::CultCache;
use cultcache_rs::CultSlots;
use cultcache_rs::CultUnion;
use cultcache_rs::DatabaseEntry;
use cultcache_rs::SingleFileMessagePackBackingStore;
use serde_json::json;
use std::collections::BTreeMap;

const INTEROP_TYPE: &str = "cultcache.interop-note";
const INTEROP_SCHEMA_VERSION: &str = "cultcache.interop_note.v1";

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "cultcache.interop-note", schema = "CultCacheInteropNote")]
struct CultCacheInteropNote {
    #[cultcache(key = 0)]
    schema_version: String,
    #[cultcache(key = 1)]
    document_id: String,
    #[cultcache(key = 2)]
    author_runtime_id: String,
    #[cultcache(key = 3)]
    title: String,
    #[cultcache(key = 4)]
    body: String,
    #[cultcache(key = 5, default)]
    tags: Vec<String>,
}

// An object list: each mark is a positional array [label, id], the id an ordinary member at slot 1.
#[derive(Clone, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
struct CultCacheInteropMark(String, String);

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "cultcache.interop-deck", schema = "CultCacheInteropDeck")]
struct CultCacheInteropDeck {
    #[cultcache(key = 0)]
    document_id: String,
    #[cultcache(key = 1, default)]
    marks: Vec<CultCacheInteropMark>,
}

// A union field and nested bytes: the shapes a Bifrost-style store holds beyond plain strings.
#[derive(Clone, Debug, PartialEq, Eq, CultUnion)]
enum InteropArm {
    #[cultcache(key = 0)]
    Text(InteropTextArm),
    #[cultcache(key = 1)]
    Pack(InteropPackArm),
}

#[derive(Clone, Debug, PartialEq, Eq, CultSlots)]
struct InteropTextArm {
    #[cultcache(key = 0)]
    text: String,
}

#[derive(Clone, Debug, PartialEq, Eq, CultSlots)]
struct InteropPackArm {
    #[cultcache(key = 0)]
    label: String,
    #[cultcache(key = 1)]
    chunks: Vec<serde_bytes::ByteBuf>,
}

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "cultcache.interop-shape", schema = "CultCacheInteropShape")]
struct CultCacheInteropShape {
    #[cultcache(key = 0)]
    document_id: String,
    #[cultcache(key = 1)]
    arm: InteropArm,
    #[cultcache(key = 2)]
    raw: serde_bytes::ByteBuf,
}

fn shapes() -> Vec<CultCacheInteropShape> {
    vec![
        CultCacheInteropShape {
            document_id: "shape:text".into(),
            arm: InteropArm::Text(InteropTextArm { text: "union arm 0".into() }),
            raw: (0..=255u8).collect::<Vec<_>>().into(),
        },
        CultCacheInteropShape {
            document_id: "shape:pack".into(),
            arm: InteropArm::Pack(InteropPackArm {
                label: "union arm 1".into(),
                chunks: vec![vec![0xc0, 0xc1, 0x00].into(), vec![].into(), vec![0x92, 0x01, 0x02].into()],
            }),
            raw: vec![0xde, 0xad, 0xbe, 0xef].into(),
        },
    ]
}

fn write_shapes(file: &str) -> Result<()> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<CultCacheInteropShape>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(file))?;
    cache.pull_all_backing_stores()?;
    for shape in shapes() {
        cache.put(&shape.document_id, &shape)?;
    }
    print_shapes(shapes())
}

/// Opens the store through the read-only snapshot door: no lock file, no staging file, no rewrite.
fn read_shapes(file: &str) -> Result<()> {
    let envelopes = SingleFileMessagePackBackingStore::new(file).pull_all_read_only_snapshot()?;
    let mut found = Vec::new();
    for envelope in envelopes.iter().filter(|envelope| envelope.r#type == CultCacheInteropShape::TYPE) {
        found.push(cultcache_rs::decode_payload::<CultCacheInteropShape>(&envelope.payload)?);
    }
    print_shapes(found)
}

fn print_shapes(mut found: Vec<CultCacheInteropShape>) -> Result<()> {
    let hex = |bytes: &[u8]| bytes.iter().map(|byte| format!("{byte:02X}")).collect::<String>();
    found.sort_by(|a, b| a.document_id.cmp(&b.document_id));
    let rows: Vec<_> = found
        .iter()
        .map(|shape| {
            let arm = match &shape.arm {
                InteropArm::Text(text) => json!({ "kind": "text", "text": text.text }),
                InteropArm::Pack(pack) => json!({
                    "kind": "pack",
                    "label": pack.label,
                    "chunks": pack.chunks.iter().map(|chunk| hex(chunk)).collect::<Vec<_>>(),
                }),
            };
            json!({ "documentId": shape.document_id, "arm": arm, "raw": hex(&shape.raw) })
        })
        .collect();
    println!("{}", serde_json::to_string(&rows)?);
    Ok(())
}

fn read_deck(file: &str) -> Result<()> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<CultCacheInteropDeck>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(file))?;
    cache.pull_all_backing_stores()?;
    let decks = cache.get_all::<CultCacheInteropDeck>()?;
    let deck = decks
        .first()
        .ok_or_else(|| anyhow!("no cultcache.interop-deck records found"))?;
    println!(
        "{}",
        serde_json::to_string(&json!({
            "documentId": deck.document_id,
            "ids": deck.marks.iter().map(|mark| mark.1.clone()).collect::<Vec<_>>(),
        }))?
    );
    Ok(())
}

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mode = args
        .next()
        .ok_or_else(|| anyhow!("expected mode: write | read | hold-lock"))?;
    let options = parse_args(args.collect());
    let file = require_arg(&options, "file")?;

    match mode.as_str() {
        "write" => write_note(file, require_arg(&options, "runtime-id")?)?,
        "read" => read_note(file)?,
        "read-deck" => read_deck(file)?,
        "write-shapes" => write_shapes(file)?,
        "read-shapes" => read_shapes(file)?,
        "hold-lock" => hold_lock(file, require_arg(&options, "ms")?.parse()?)?,
        _ => return Err(anyhow!("unknown mode {mode}")),
    }
    Ok(())
}

/// Holds the store lock as the library opens it (`<file>.lock`, fs2 exclusive), for `ms`.
fn hold_lock(file: &str, ms: u64) -> Result<()> {
    let lock = std::fs::OpenOptions::new()
        .create(true)
        .read(true)
        .truncate(false)
        .write(true)
        .open(format!("{file}.lock"))?;
    fs2::FileExt::lock_exclusive(&lock)?;
    println!("{}", json!({ "held": true }));
    std::io::Write::flush(&mut std::io::stdout())?;
    std::thread::sleep(std::time::Duration::from_millis(ms));
    fs2::FileExt::unlock(&lock)?;
    Ok(())
}

fn write_note(file: &str, runtime_id: &str) -> Result<()> {
    let mut cache = build_cache(file)?;
    cache.pull_all_backing_stores()?;
    let note = CultCacheInteropNote {
        schema_version: INTEROP_SCHEMA_VERSION.to_string(),
        document_id: format!("note:{runtime_id}"),
        author_runtime_id: runtime_id.to_string(),
        title: format!("{runtime_id} wrote a CultCache note"),
        body: "The v1 store format is the contract.".to_string(),
        tags: vec![
            runtime_id.to_string(),
            "rust".to_string(),
            "interop".to_string(),
        ],
    };
    cache.put(&note.document_id, &note)?;
    print_note(&note)
}

fn read_note(file: &str) -> Result<()> {
    let mut cache = build_cache(file)?;
    cache.pull_all_backing_stores()?;
    let notes = cache.get_all::<CultCacheInteropNote>()?;
    let note = notes
        .first()
        .ok_or_else(|| anyhow!("no {INTEROP_TYPE} records found"))?;
    print_note(note)
}

fn build_cache(file: &str) -> Result<CultCache> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<CultCacheInteropNote>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(file))?;
    Ok(cache)
}

fn print_note(note: &CultCacheInteropNote) -> Result<()> {
    println!(
        "{}",
        serde_json::to_string(&json!({
            "schemaVersion": note.schema_version,
            "documentId": note.document_id,
            "authorRuntimeId": note.author_runtime_id,
            "title": note.title,
            "body": note.body,
            "tags": note.tags,
        }))?
    );
    Ok(())
}

fn parse_args(args: Vec<String>) -> BTreeMap<String, String> {
    let mut parsed = BTreeMap::new();
    let mut index = 0;
    while index < args.len() {
        let token = &args[index];
        if !token.starts_with("--") {
            index += 1;
            continue;
        }
        let name = token.trim_start_matches("--").to_string();
        let value = args
            .get(index + 1)
            .cloned()
            .unwrap_or_else(|| panic!("missing value for --{name}"));
        parsed.insert(name, value);
        index += 2;
    }
    parsed
}

fn require_arg<'a>(options: &'a BTreeMap<String, String>, name: &str) -> Result<&'a str> {
    options
        .get(name)
        .map(String::as_str)
        .ok_or_else(|| anyhow!("missing required argument --{name}"))
}
