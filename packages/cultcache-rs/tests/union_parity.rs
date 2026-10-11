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
    body: Option<std::boxed::Box<Node>>,
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
                    composition: Composition { name: "comp".into(), body: Some(std::boxed::Box::new(inner)) },
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
        ("a trailing element", rmp_serde::to_vec(&(1u64, (7u8,), 0u8)).unwrap()),
        ("a string key", rmp_serde::to_vec(&("CANARY-key", (7u8,))).unwrap()),
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
    cache.pull_all_backing_stores().unwrap();
    assert_eq!(cache.get::<UnionSurface>("surface").unwrap(), Some(constructed()));
}

// The arm key domain is the reference's Int32. Expected bytes are what MessagePack-CSharp writes
// for the same keys (WriteInt32: fixneg, uint32, int32); the refusal classes are CultUnionGuard's.
mod arm_keys {
    use cultcache_rs::CultSlots;
    use cultcache_rs::CultUnion;

    #[derive(Clone, Debug, PartialEq, CultSlots)]
    struct Count {
        #[cultcache(key = 0)]
        name: String,
        #[cultcache(key = 1)]
        count: i32,
    }

    #[derive(Clone, Debug, PartialEq, CultUnion)]
    enum Keyed {
        #[cultcache(key = -1)]
        Negative(Count),
        #[cultcache(key = 2147483647)]
        Max(Count),
        #[cultcache(key = -2147483648)]
        Min,
        #[cultcache(key = 7)]
        Plain(Count),
    }

    fn count() -> Count {
        Count { name: "n".into(), count: 1 }
    }

    fn message(bytes: &[u8]) -> String {
        rmp_serde::from_slice::<Keyed>(bytes).unwrap_err().to_string()
    }

    #[test]
    fn negative_and_int32_max_keys_are_declared_and_round_trip() {
        let cases: [(Keyed, &[u8]); 4] = [
            (Keyed::Negative(count()), &[0x92, 0xff, 0x92, 0xa1, b'n', 0x01]),
            (Keyed::Max(count()), &[0x92, 0xce, 0x7f, 0xff, 0xff, 0xff, 0x92, 0xa1, b'n', 0x01]),
            (Keyed::Min, &[0x92, 0xd2, 0x80, 0x00, 0x00, 0x00, 0x90]),
            (Keyed::Plain(count()), &[0x92, 0x07, 0x92, 0xa1, b'n', 0x01]),
        ];
        for (value, bytes) in cases {
            assert_eq!(rmp_serde::to_vec(&value).unwrap(), bytes, "{value:?}");
            assert_eq!(rmp_serde::from_slice::<Keyed>(bytes).unwrap(), value);
        }
    }

    // Every integer width of a negative key reads as the same arm.
    #[test]
    fn a_negative_key_reads_in_every_integer_width() {
        for key in [&[0xff][..], &[0xd0, 0xff], &[0xd1, 0xff, 0xff], &[0xd2, 0xff, 0xff, 0xff, 0xff], &[0xd3, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]] {
            let mut bytes = vec![0x92];
            bytes.extend_from_slice(key);
            bytes.extend_from_slice(&[0x92, 0xa1, b'n', 0x01]);
            assert_eq!(rmp_serde::from_slice::<Keyed>(&bytes).unwrap(), Keyed::Negative(count()), "{key:02x?}");
        }
    }

    fn pair(key: &[u8]) -> Vec<u8> {
        let mut bytes = vec![0x92];
        bytes.extend_from_slice(key);
        bytes.extend_from_slice(&[0x92, 0xa1, b'C', 0xa1, b'A']);
        bytes
    }

    // A negative key the union does not declare is an unknown arm naming the key, as in the reference.
    #[test]
    fn a_negative_undeclared_key_is_an_unknown_arm_naming_the_key() {
        for (key, shown) in [(&[0xe0][..], "-32"), (&[0xd0, 0xdf], "-33"), (&[0xd1, 0x80, 0x00], "-32768"), (&[0xd2, 0x80, 0x00, 0x00, 0x01], "-2147483647")] {
            let text = message(&pair(key));
            assert!(text.contains(&format!("unknown arm key {shown} ")), "{text}");
            assert!(text.contains("declared keys: -2147483648, -1, 7, 2147483647"), "{text}");
            assert!(!text.contains("CA"), "{text}");
        }
    }

    // Outside Int32 is its own class, naming neither the value nor the payload.
    #[test]
    fn keys_outside_int32_are_their_own_class_and_never_echoed() {
        let wide: [&[u8]; 6] = [
            &[0xce, 0x80, 0x00, 0x00, 0x00],
            &[0xcf, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
            &[0xcf, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
            &[0xd3, 0xff, 0xff, 0xff, 0xff, 0x7f, 0xff, 0xff, 0xff],
            &[0xd3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            &[0xcf, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
        ];
        for key in wide {
            let text = message(&pair(key));
            assert!(text.contains("an arm key outside Int32"), "{key:02x?}: {text}");
            assert!(text.contains("Keyed") && !text.contains("CA"), "{text}");
            for value in ["4294967296", "9223372036854775807", "9223372036854775808", "18446744073709551615"] {
                assert!(!text.contains(value), "{text}");
            }
        }
    }

    #[test]
    fn a_key_that_is_not_an_integer_is_its_own_class_and_never_echoed() {
        let keys: [&[u8]; 7] = [
            &[0xc0],
            &[0xc3],
            &[0xcb, 0x3f, 0xf0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            &[0xa6, b'C', b'A', b'N', b'A', b'R', b'Y'],
            &[0xc4, 0x02, b'C', b'A'],
            &[0x91, 0xa6, b'C', b'A', b'N', b'A', b'R', b'Y'],
            &[0x81, 0xa6, b'C', b'A', b'N', b'A', b'R', b'Y', 0x01],
        ];
        for key in keys {
            let text = message(&pair(key));
            assert!(text.contains("a non-integer arm key") && text.contains("Keyed"), "{key:02x?}: {text}");
            assert!(!text.contains("CANARY") && !text.contains("CA"), "{text}");
        }
    }

    // Anything that is not a two-element array is the arity class, whatever its first element is.
    #[test]
    fn a_pair_of_the_wrong_length_is_refused_before_its_key_is_read() {
        for bytes in [&[0x91, 0xa1, b'x'][..], &[0x93, 0x07, 0x90, 0x00], &[0x90]] {
            let text = message(bytes);
            assert!(text.contains("is not a [key, slots] pair"), "{bytes:02x?}: {text}");
        }
    }
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
