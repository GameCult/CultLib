//! The Rust runtime held to tests/vectors/union-parity, written by the C# reference.
//! Bytes and outcomes are read from the committed files; only the constructed value is spelled here.

use std::path::PathBuf;

use cultcache_rs::CultCache;
use cultcache_rs::CultSlots;
use cultcache_rs::CultUnion;
use cultcache_rs::DatabaseEntry;
use cultcache_rs::SingleFileMessagePackBackingStore;

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum Node {
    #[cultcache(key = 0)]
    Box(BoxArm),
    #[cultcache(key = 1)]
    Text(TextArm),
    #[cultcache(key = 2)]
    Use(UseArm),
    #[cultcache(key = 5)]
    Image(ImageArm),
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct BoxArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    children: Vec<Node>,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct TextArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    content: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct UseArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    composition: Composition,
    #[cultcache(key = 2)]
    slots: Vec<Option<Node>>,
}

// Slot 2 is retired: a gap written nil.
#[derive(Clone, Debug, PartialEq, CultSlots)]
struct ImageArm {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    uri: String,
    #[cultcache(key = 3)]
    alt: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct Composition {
    #[cultcache(key = 0)]
    name: String,
    #[cultcache(key = 1)]
    body: Option<Node>,
}

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum Outcome {
    #[cultcache(key = 0)]
    Pending(PendingArm),
    #[cultcache(key = 1)]
    Completed(CompletedArm),
    #[cultcache(key = 2)]
    Denied,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct PendingArm {
    #[cultcache(key = 0)]
    message: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct CompletedArm {
    #[cultcache(key = 0)]
    source_version: i32,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct NamedArm {
    #[cultcache(key = 0)]
    name: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct UsesArm {
    #[cultcache(key = 0)]
    name: String,
    #[cultcache(key = 1)]
    uses: i32,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct CountArm {
    #[cultcache(key = 0)]
    name: String,
    #[cultcache(key = 1)]
    count: i32,
}

#[derive(Clone, Debug, PartialEq, CultUnion)]
enum Item {
    #[cultcache(key = 0)]
    Gem(NamedArm),
    #[cultcache(key = 1)]
    Tool(UsesArm),
    #[cultcache(key = 2)]
    Part(CountArm),
}

// The same arms as Item under its own keys: key 2 is unknown to it.
#[derive(Clone, Debug, PartialEq, CultUnion)]
enum CraftedItem {
    #[cultcache(key = 0)]
    Tool(UsesArm),
    #[cultcache(key = 1)]
    Part(CountArm),
}

#[derive(Clone, Debug, PartialEq, DatabaseEntry)]
#[cultcache(type = "tests.union_surface", schema = "tests.union_surface.v1")]
struct UnionSurface {
    #[cultcache(key = 0)]
    id: String,
    #[cultcache(key = 1)]
    version: i32,
    #[cultcache(key = 2)]
    root: Option<Node>,
    #[cultcache(key = 3)]
    outcomes: Vec<Outcome>,
    #[cultcache(key = 4)]
    part: Option<Item>,
    #[cultcache(key = 5)]
    crafted: Option<CraftedItem>,
    #[cultcache(key = 6)]
    missing: Option<Node>,
}

fn text(id: &str, content: &str) -> Node {
    Node::Text(TextArm { id: id.into(), content: content.into() })
}

fn image(id: &str, uri: &str, alt: &str) -> Node {
    Node::Image(ImageArm { id: id.into(), uri: uri.into(), alt: alt.into() })
}

fn boxed(id: &str, children: Vec<Node>) -> Node {
    Node::Box(BoxArm { id: id.into(), children })
}

fn constructed() -> UnionSurface {
    let inner = boxed("inner", vec![text("deep", "x"), image("img", "uri://a", "alt")]);
    UnionSurface {
        id: "surface".into(),
        version: 3,
        root: Some(boxed(
            "root",
            vec![
                text("t1", "hello"),
                Node::Use(UseArm {
                    id: "u1".into(),
                    composition: Composition { name: "comp".into(), body: Some(inner) },
                    slots: vec![Some(text("s1", "slot")), None, Some(boxed("b2", vec![]))],
                }),
                image("i2", "uri://b", ""),
            ],
        )),
        outcomes: vec![
            Outcome::Pending(PendingArm { message: "wait".into() }),
            Outcome::Completed(CompletedArm { source_version: 7 }),
            Outcome::Denied,
        ],
        part: Some(Item::Tool(UsesArm { name: "hammer".into(), uses: 12 })),
        crafted: Some(CraftedItem::Part(CountArm { name: "gear".into(), count: 4 })),
        missing: None,
    }
}

fn vector(name: &str) -> Vec<u8> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/vectors/union-parity")
        .join(name);
    std::fs::read(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()))
}

fn decode(bytes: &[u8]) -> Result<UnionSurface, String> {
    rmp_serde::from_slice(bytes).map_err(|error| error.to_string())
}

#[test]
fn union_surface_decodes_and_reencodes() {
    let payload = vector("union-surface.msgpack");
    let value = constructed();
    assert_eq!(decode(&payload).unwrap(), value);
    assert_eq!(rmp_serde::to_vec(&value).unwrap(), payload);
}

#[test]
fn a_unit_arm_is_a_key_and_an_empty_slots_array() {
    assert_eq!(rmp_serde::to_vec(&Outcome::Denied).unwrap(), [0x92, 0x02, 0x90]);
    assert_eq!(rmp_serde::from_slice::<Outcome>(&[0x92, 0x02, 0x90]).unwrap(), Outcome::Denied);
}

#[test]
fn union_refusal_vectors() {
    let outcomes: Vec<(String, String, String, Option<u64>, String)> =
        rmp_serde::from_slice(&vector("outcomes.msgpack")).unwrap();
    let (mut refused, mut read_only) = (0, 0);
    for (file, outcome, union, key, detail) in outcomes {
        let result = decode(&vector(&file));
        match outcome.as_str() {
            "refused" => {
                refused += 1;
                let message = result.expect_err(&file);
                assert!(message.contains(&union), "{file}: {message}");
                if detail == "unknown-arm" {
                    let key = key.expect(&file);
                    assert!(message.contains(&format!("unknown arm key {key} ")), "{file}: {message}");
                }
                for canary in ["root", "hello", "uri://", "surface"] {
                    assert!(!message.contains(canary), "{file} echoed a value: {message}");
                }
            }
            "read-only" => {
                read_only += 1;
                assert_eq!(result.expect(&file), constructed(), "{file}");
            }
            other => panic!("{file}: unknown outcome {other}"),
        }
    }
    assert_eq!((refused, read_only), (5, 4));
}

#[test]
fn an_unknown_arm_lists_the_declared_keys_and_never_echoes_the_payload() {
    // Shaped like a real arm, an array of strings, under an undeclared key.
    let bytes = rmp_serde::to_vec(&(99u64, ("CANARY-7f3a", "CANARY-91bc"))).unwrap();
    let message = rmp_serde::from_slice::<Node>(&bytes).unwrap_err().to_string();
    assert!(message.contains("Node") && message.contains("99") && message.contains("0, 1, 2, 5"), "{message}");
    assert!(!message.contains("CANARY"), "{message}");
}

#[test]
fn a_union_that_is_not_a_key_and_slots_pair_is_refused_naming_the_enum() {
    let cases: [(&str, Vec<u8>); 4] = [
        ("an empty array", vec![0x90]),
        ("a trailing element", rmp_serde::to_vec(&(1u64, ("a",), 0u8)).unwrap()),
        ("a string key", rmp_serde::to_vec(&("CANARY-key", ("a",))).unwrap()),
        ("a unit arm with slots", rmp_serde::to_vec(&(2u64, ("CANARY-slot",))).unwrap()),
    ];
    for (label, bytes) in cases {
        let message = rmp_serde::from_slice::<Outcome>(&bytes).unwrap_err().to_string();
        assert!(message.contains("Outcome"), "{label}: {message}");
        assert!(!message.contains("CANARY"), "{label}: {message}");
    }
}

#[test]
fn the_key_decides_the_arm() {
    // Tool and Part carry different slots under different keys in the two unions.
    let bytes = rmp_serde::to_vec(&(1u64, ("hammer", 3u8))).unwrap();
    assert_eq!(
        rmp_serde::from_slice::<Item>(&bytes).unwrap(),
        Item::Tool(UsesArm { name: "hammer".into(), uses: 3 })
    );
    assert_eq!(
        rmp_serde::from_slice::<CraftedItem>(&bytes).unwrap(),
        CraftedItem::Part(CountArm { name: "hammer".into(), count: 3 })
    );
}

#[test]
fn union_store_reads() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("union-surface.cc");
    std::fs::write(&path, vector("union-surface.cc")).unwrap();
    let mut cache = CultCache::new();
    cache.register_entry_type::<UnionSurface>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();
    assert_eq!(cache.get::<UnionSurface>("surface").unwrap(), Some(constructed()));
}

mod serde_enum_bytes {
    use cultcache_rs::DatabaseEntry;

    #[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
    enum External {
        Unit,
        Newtype(u32),
        Struct { n: u8 },
    }

    #[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize)]
    #[serde(tag = "kind")]
    enum Internal {
        A { n: u8 },
    }

    #[derive(Clone, Debug, PartialEq, DatabaseEntry)]
    #[cultcache(type = "tests.plain_enums")]
    struct PlainEnums {
        #[cultcache(key = 0)]
        unit: External,
        #[cultcache(key = 1)]
        newtype: External,
        #[cultcache(key = 2)]
        structured: External,
        #[cultcache(key = 3)]
        tagged: Internal,
    }

    // Computed with the derive and serde at b0f40577, before CultUnion existed.
    const AT_BASE: &[u8] = &[
        0x94, 0xa4, b'U', b'n', b'i', b't', 0x81, 0xa7, b'N', b'e', b'w', b't', b'y', b'p', b'e', 0x05, 0x81,
        0xa6, b'S', b't', b'r', b'u', b'c', b't', 0x91, 0x01, 0x92, 0xa1, b'A', 0x01,
    ];

    #[test]
    fn serde_enum_bytes_stable() {
        let value = PlainEnums {
            unit: External::Unit,
            newtype: External::Newtype(5),
            structured: External::Struct { n: 1 },
            tagged: Internal::A { n: 1 },
        };
        let bytes = rmp_serde::to_vec(&value).unwrap();
        assert_eq!(bytes, AT_BASE);
        assert_eq!(rmp_serde::from_slice::<PlainEnums>(AT_BASE).unwrap(), value);
    }
}
