use anyhow::Result;
use anyhow::anyhow;
use cultcache_rs::DatabaseEntry;
use cultmesh_rs::CultMesh;
use cultmesh_rs::CultMeshNodeOptions;
use cultmesh_rs::cultmesh_documents;
use serde_json::json;
use std::collections::BTreeMap;

const INTEROP_TYPE: &str = "cultmesh.interop-note";
const INTEROP_SCHEMA_VERSION: &str = "cultmesh.interop_note.v0";

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "cultmesh.interop-note", schema = "CultMeshInteropNote")]
struct CultMeshInteropNote {
    #[cultcache(key = 0)]
    schema_version: String,
    #[cultcache(key = 1)]
    document_id: String,
    #[cultcache(key = 2)]
    author_runtime_id: String,
    #[cultcache(key = 3)]
    verse_id: String,
    #[cultcache(key = 4)]
    body: String,
    #[cultcache(key = 5, default)]
    tags: Vec<String>,
}

cultmesh_documents!(InteropDocuments {
    CultMeshInteropNote => INTEROP_SCHEMA_VERSION,
});

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mode = args
        .next()
        .ok_or_else(|| anyhow!("expected mode: write | read"))?;
    let options = parse_args(args.collect());
    let file = require_arg(&options, "file")?;

    match mode.as_str() {
        "write" => write_note(file, require_arg(&options, "runtime-id")?)?,
        "read" => read_note(file)?,
        _ => return Err(anyhow!("unknown mode {mode}")),
    }
    Ok(())
}

fn write_note(file: &str, runtime_id: &str) -> Result<()> {
    let mut node = CultMesh::create_node(
        file,
        InteropDocuments,
        CultMeshNodeOptions {
            runtime_id: runtime_id.to_string(),
            ..CultMeshNodeOptions::default()
        },
    )?;
    let note = CultMeshInteropNote {
        schema_version: INTEROP_SCHEMA_VERSION.to_string(),
        document_id: format!("note:{runtime_id}"),
        author_runtime_id: runtime_id.to_string(),
        verse_id: "verse:interop".to_string(),
        body: "CultMesh local node state uses the shared CultCache wire store.".to_string(),
        tags: vec![
            runtime_id.to_string(),
            "rust".to_string(),
            "interop".to_string(),
            "cultmesh".to_string(),
        ],
    };
    node.put(note.document_id.clone(), &note)?;
    node.flush()?;
    print_note(&note)
}

fn read_note(file: &str) -> Result<()> {
    let node = CultMesh::start_node(file, InteropDocuments)?;
    let notes = node.cache().get_all::<CultMeshInteropNote>()?;
    let note = notes
        .first()
        .ok_or_else(|| anyhow!("no {INTEROP_TYPE} records found"))?;
    print_note(note)
}

fn print_note(note: &CultMeshInteropNote) -> Result<()> {
    println!(
        "{}",
        serde_json::to_string(&json!({
            "schemaVersion": note.schema_version,
            "documentId": note.document_id,
            "authorRuntimeId": note.author_runtime_id,
            "verseId": note.verse_id,
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
