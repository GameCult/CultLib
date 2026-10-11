//! The Rust runtime held to tests/vectors/union-parity, written by the C# reference.
//! Bytes and outcomes are read from the committed files; only the constructed value is spelled here.

use std::path::PathBuf;

use cultcache_rs::CultCache;
use cultcache_rs::CultCacheEnvelope;
use cultcache_rs::MAX_PAYLOAD_DEPTH;
use cultcache_rs::PayloadTooDeep;
use cultcache_rs::check_payload_depth;
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
    #[cultcache(key = 6)]
    Wrap(WrapArm),
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
struct WrapArm {
    #[cultcache(key = 0)]
    inner: Option<std::boxed::Box<Node>>,
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
    #[cultcache(key = -2147483648)]
    Floor,
    #[cultcache(key = 2147483647)]
    Ceiling,
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

// Names a derive's generated code must not capture: the generic parameters and the local items of the
// expansion, used as user type names.
mod generated_names {
    use cultcache_rs::CultSlots;
    use cultcache_rs::CultUnion;
    use cultcache_rs::DatabaseEntry;

    #[derive(Clone, Debug, PartialEq, CultSlots)]
    struct D {
        #[cultcache(key = 0)]
        name: String,
    }

    #[derive(Clone, Debug, PartialEq, CultSlots)]
    struct S {
        #[cultcache(key = 0)]
        count: i32,
    }

    #[derive(Clone, Debug, PartialEq, CultSlots)]
    struct ArmKey {
        #[cultcache(key = 0)]
        id: i32,
    }

    #[derive(Clone, Debug, PartialEq, CultUnion)]
    enum A {
        #[cultcache(key = 1)]
        D(D),
        #[cultcache(key = 2)]
        S(S),
        #[cultcache(key = 3)]
        ArmKey(ArmKey),
        #[cultcache(key = 4)]
        E,
    }

    #[derive(Clone, Debug, PartialEq, CultUnion)]
    enum CultUnionVisitor {
        #[cultcache(key = 1)]
        A(ArmKey),
    }

    #[derive(Clone, Debug, PartialEq, DatabaseEntry)]
    #[cultcache(type = "tests.names")]
    struct E {
        #[cultcache(key = 0)]
        union: A,
        #[cultcache(key = 1)]
        other: CultUnionVisitor,
    }

    #[test]
    fn types_named_like_generated_generics_and_locals_round_trip() {
        let value = E {
            union: A::D(D { name: "n".into() }),
            other: CultUnionVisitor::A(ArmKey { id: 9 }),
        };
        let bytes = rmp_serde::to_vec(&value).unwrap();
        assert_eq!(rmp_serde::from_slice::<E>(&bytes).unwrap(), value);
        for union in [A::S(S { count: 2 }), A::ArmKey(ArmKey { id: 3 }), A::E] {
            let bytes = rmp_serde::to_vec(&union).unwrap();
            assert_eq!(rmp_serde::from_slice::<A>(&bytes).unwrap(), union);
        }
    }
}

// The reference's CultUnionGuard classes, from bounds-outcomes.msgpack; depth entries go through the
// cache because the nesting rule lives in its one decode.
const CANARY: &str = "CANARY-union-golden-4c2a";

fn through_cache(payload: &[u8]) -> Result<UnionSurface, String> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<UnionSurface>().unwrap();
    cache
        .put_raw_envelope(CultCacheEnvelope {
            key: "k".into(),
            r#type: UnionSurface::TYPE.into(),
            payload: payload.to_vec(),
            stored_at: "2026-01-01T00:00:00Z".into(),
            schema_id: None,
        })
        .unwrap();
    cache.get::<UnionSurface>("k").map_err(|error| format!("{error:#}"))
}

#[test]
fn bounds_vectors() {
    let entries: Vec<serde_json::Value> = rmp_serde::from_slice(&vector("bounds-outcomes.msgpack")).unwrap();
    assert_eq!(entries[0], serde_json::json!(["limit", 256]));
    assert_eq!(MAX_PAYLOAD_DEPTH, 256);
    let (mut accepted, mut refused, mut read_only) = (0, 0, 0);
    for entry in &entries[1..] {
        let fields: Vec<&str> = entry.as_array().unwrap().iter().filter_map(|field| field.as_str()).collect();
        let (file, outcome, union, detail) = (fields[0], fields[1], fields[2], fields[3]);
        let bytes = vector(file);
        let result = if union == "UnionSurface" { through_cache(&bytes) } else { decode(&bytes) };
        match outcome {
            "accepted" => {
                accepted += 1;
                let value = result.unwrap_or_else(|message| panic!("{file}: {message}"));
                assert_eq!(rmp_serde::to_vec(&value).unwrap(), vector(detail), "{file}");
                match file {
                    "arm-key-int32-min.msgpack" => assert_eq!(value.outcomes.last(), Some(&Outcome::Floor)),
                    "arm-key-int32-max.msgpack" | "arm-key-wide-coding.msgpack" => {
                        assert_eq!(value.outcomes.last(), Some(&Outcome::Ceiling))
                    }
                    _ => {}
                }
            }
            "read-only" => {
                read_only += 1;
                assert_eq!(result.unwrap_or_else(|message| panic!("{file}: {message}")), constructed(), "{file}");
            }
            "refused" => {
                refused += 1;
                let message = result.expect_err(file);
                assert!(message.contains(union), "{file}: {message}");
                let class = match detail {
                    "key-out-of-range" => "outside Int32",
                    "key-not-integer" => "non-integer",
                    "nil-slots" => "arm 1 has nil slots",
                    "depth" => "the limit is 256",
                    other => panic!("{file}: unknown class {other}"),
                };
                assert!(message.contains(class), "{file}: {message}");
                assert!(!message.contains(CANARY), "{file} echoed a value: {message}");
            }
            other => panic!("{file}: unknown outcome {other}"),
        }
    }
    assert_eq!((accepted, refused, read_only), (4, 7, 1));
}

fn wrap_chain(wraps: usize, leaf: Node) -> UnionSurface {
    let mut node = leaf;
    for _ in 0..wraps {
        node = Node::Wrap(WrapArm { inner: Some(std::boxed::Box::new(node)) });
    }
    UnionSurface { root: Some(node), ..constructed() }
}

// Each Wrap adds a pair and a slots array; the surface array is depth 1, so a Box leaf (pair, slots,
// children) ends at 2 * (wraps + 1) + 2 and a Text leaf at 2 * (wraps + 1) + 1.
#[test]
fn the_constructed_chains_write_the_vector_bytes() {
    let at_limit = wrap_chain(126, boxed("leaf", vec![]));
    assert_eq!(rmp_serde::to_vec(&at_limit).unwrap(), vector("depth-at-limit.msgpack"));
    let past_limit = wrap_chain(127, text("leaf", CANARY));
    assert_eq!(rmp_serde::to_vec(&past_limit).unwrap(), vector("depth-past-limit.msgpack"));
}

#[test]
fn the_canary_is_in_the_committed_bytes() {
    let bytes = vector("depth-past-limit.msgpack");
    assert!(bytes.windows(CANARY.len()).any(|window| window == CANARY.as_bytes()));
}

#[test]
fn write_refused_past_limit() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("depth.cc");
    let mut cache = CultCache::new();
    cache.register_entry_type::<UnionSurface>().unwrap();
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(&path)).unwrap();

    let at_limit = wrap_chain(126, boxed("leaf", vec![]));
    assert_eq!(cache.put("at", &at_limit).unwrap(), at_limit);
    let stored = cache.snapshot_envelopes();
    assert_eq!(stored.len(), 1);
    assert_eq!(stored[0].payload, vector("depth-at-limit.msgpack"));
    let file_before = std::fs::read(&path).unwrap();

    let error = cache.put("past", &wrap_chain(127, text("leaf", CANARY))).unwrap_err();
    assert_eq!(error.downcast_ref::<PayloadTooDeep>(), Some(&PayloadTooDeep { depth: 257, limit: 256 }));
    assert!(!format!("{error:#}").contains(CANARY), "{error:#}");
    assert_eq!(cache.get::<UnionSurface>("past").unwrap(), None);
    assert_eq!(cache.snapshot_envelopes(), stored);
    assert_eq!(std::fs::read(&path).unwrap(), file_before);
}

// A debug build on a 2 MiB thread carries 405 containers of this document; the bound keeps every
// accepted payload below that, and every refusal is an error, not an abort.
#[test]
fn stack_budget_debug_2mib() {
    std::thread::Builder::new()
        .stack_size(2 * 1024 * 1024)
        .spawn(|| {
            assert_eq!(
                through_cache(&vector("depth-at-limit.msgpack")).unwrap(),
                wrap_chain(126, boxed("leaf", vec![]))
            );
            let refused = through_cache(&vector("depth-past-limit.msgpack")).unwrap_err();
            assert!(refused.contains("the limit is 256"), "{refused}");
            let mut cache = CultCache::new();
            cache.register_entry_type::<UnionSurface>().unwrap();
            let value = wrap_chain(126, boxed("leaf", vec![]));
            assert_eq!(cache.put("at", &value).unwrap(), value);
            assert_eq!(cache.get::<UnionSurface>("at").unwrap(), Some(value));
            let mut deep = vec![0x91u8; 100_000];
            deep.push(0xc0);
            let refused = through_cache(&deep).unwrap_err();
            assert!(refused.contains("the limit is 256"), "{refused}");
        })
        .unwrap()
        .join()
        .unwrap();
}

// The walk is iterative: 100000 nested arrays are refused on a 256 KiB stack.
#[test]
fn the_walk_does_not_recurse() {
    std::thread::Builder::new()
        .stack_size(256 * 1024)
        .spawn(|| {
            let mut deep = vec![0x91u8; 100_000];
            deep.push(0xc0);
            assert_eq!(check_payload_depth(&deep), Err(PayloadTooDeep { depth: 257, limit: 256 }));
        })
        .unwrap()
        .join()
        .unwrap();
}

fn nested(open: &[u8], levels: usize, leaf: &[u8]) -> Vec<u8> {
    let mut bytes = open.repeat(levels);
    bytes.extend_from_slice(leaf);
    bytes
}

const TOO_DEEP: PayloadTooDeep = PayloadTooDeep { depth: 257, limit: 256 };

// Every container header width counts one level, and the bound sits between 256 and 257 for each.
#[test]
fn every_container_header_counts_one_level() {
    let openers: [(&str, &[u8]); 6] = [
        ("fixarray", &[0x91]),
        ("array16", &[0xdc, 0x00, 0x01]),
        ("array32", &[0xdd, 0x00, 0x00, 0x00, 0x01]),
        ("fixmap", &[0x81, 0x01]),
        ("map16", &[0xde, 0x00, 0x01, 0x01]),
        ("map32", &[0xdf, 0x00, 0x00, 0x00, 0x01, 0x01]),
    ];
    for (name, open) in openers {
        assert_eq!(check_payload_depth(&nested(open, 256, &[0xc0])), Ok(()), "{name} at the limit");
        assert_eq!(check_payload_depth(&nested(open, 257, &[0xc0])), Err(TOO_DEEP), "{name} past the limit");
    }
    // An empty container still counts the level it sits on.
    assert_eq!(check_payload_depth(&nested(&[0x91], 255, &[0x90])), Ok(()));
    assert_eq!(check_payload_depth(&nested(&[0x91], 256, &[0x90])), Err(TOO_DEEP));
    assert_eq!(check_payload_depth(&nested(&[0x91], 255, &[0x80])), Ok(()));
    assert_eq!(check_payload_depth(&nested(&[0x91], 256, &[0x80])), Err(TOO_DEEP));
}

// A scalar skipped by the wrong length would misplace the chain after it, so the chain's depth
// reads the same only when every scalar width is skipped exactly.
#[test]
fn every_scalar_width_is_skipped_by_its_length() {
    let scalars: Vec<Vec<u8>> = vec![
        vec![0x00],
        vec![0xff],
        vec![0xc0],
        vec![0xc2],
        vec![0xc3],
        vec![0xcc, 1],
        vec![0xcd, 0, 1],
        vec![0xce, 0, 0, 0, 1],
        vec![0xcf, 0, 0, 0, 0, 0, 0, 0, 1],
        vec![0xd0, 1],
        vec![0xd1, 0, 1],
        vec![0xd2, 0, 0, 0, 1],
        vec![0xd3, 0, 0, 0, 0, 0, 0, 0, 1],
        vec![0xca, 0, 0, 0, 0],
        vec![0xcb, 0, 0, 0, 0, 0, 0, 0, 0],
        vec![0xa2, b'a', b'b'],
        vec![0xd9, 3, b'a', b'b', b'c'],
        vec![0xda, 0, 3, b'a', b'b', b'c'],
        vec![0xdb, 0, 0, 0, 3, b'a', b'b', b'c'],
        vec![0xc4, 3, b'a', b'b', b'c'],
        vec![0xc5, 0, 3, b'a', b'b', b'c'],
        vec![0xc6, 0, 0, 0, 3, b'a', b'b', b'c'],
        vec![0xd4, 1, 0],
        vec![0xd5, 1, 0, 0],
        vec![0xd6, 1, 0, 0, 0, 0],
        vec![0xd7, 1, 0, 0, 0, 0, 0, 0, 0, 0],
        [vec![0xd8, 1], vec![0; 16]].concat(),
        vec![0xc7, 3, 1, b'a', b'b', b'c'],
        vec![0xc8, 0, 3, 1, b'a', b'b', b'c'],
        vec![0xc9, 0, 0, 0, 3, 1, b'a', b'b', b'c'],
    ];
    let with_chain = |levels: usize| {
        let mut bytes = vec![0xdd];
        bytes.extend_from_slice(&u32::try_from(scalars.len() + 1).unwrap().to_be_bytes());
        for scalar in &scalars {
            bytes.extend_from_slice(scalar);
        }
        bytes.extend(nested(&[0x91], levels, &[0xc0]));
        bytes
    };
    assert_eq!(check_payload_depth(&with_chain(255)), Ok(()));
    assert_eq!(check_payload_depth(&with_chain(256)), Err(TOO_DEEP));
}

// An ending cut short is the decoder's to refuse, never a panic or a depth error.
#[test]
fn a_payload_cut_short_is_not_a_depth_error() {
    for bytes in [&[0xdc, 0x00][..], &[0xdd, 0, 0], &[0xd9], &[0xc5, 0x00], &[0xde], &[0xdf, 0, 0, 0], &[0x91], &[]] {
        assert_eq!(check_payload_depth(bytes), Ok(()), "{bytes:02x?}");
    }
    assert_eq!(check_payload_depth(&[0xdb, 0xff, 0xff, 0xff, 0xff, b'a']), Ok(()));
}

#[test]
fn the_refusal_names_depth_and_limit_only() {
    assert_eq!(TOO_DEEP.to_string(), "payload nests 257 MessagePack containers deep; the limit is 256");
}
