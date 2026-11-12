//! The union-parity vectors' types and constructed value, shared by the test files that walk them.
#![allow(dead_code)]

use std::path::PathBuf;

use cultcache_rs::CultSlots;
use cultcache_rs::CultUnion;
use cultcache_rs::DatabaseEntry;


#[derive(Clone, Debug, PartialEq, CultUnion)]
pub enum Node {
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
pub struct WrapArm {
    #[cultcache(key = 0)]
    pub inner: Option<std::boxed::Box<Node>>,
    #[cultcache(key = 1)]
    pub id: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct BoxArm {
    #[cultcache(key = 0)]
    pub id: String,
    #[cultcache(key = 1)]
    pub children: Vec<Node>,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct TextArm {
    #[cultcache(key = 0)]
    pub id: String,
    #[cultcache(key = 1)]
    pub content: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct UseArm {
    #[cultcache(key = 0)]
    pub id: String,
    #[cultcache(key = 1)]
    pub composition: Composition,
    #[cultcache(key = 2)]
    pub slots: Vec<Option<Node>>,
}

// Slot 2 is retired: a gap written nil.
#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct ImageArm {
    #[cultcache(key = 0)]
    pub id: String,
    #[cultcache(key = 1)]
    pub uri: String,
    #[cultcache(key = 3)]
    pub alt: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct Composition {
    #[cultcache(key = 0)]
    pub name: String,
    #[cultcache(key = 1)]
    pub body: Option<std::boxed::Box<Node>>,
}

#[derive(Clone, Debug, PartialEq, CultUnion)]
pub enum Outcome {
    #[cultcache(key = 0)]
    Pending(PendingArm),
    #[cultcache(key = 1)]
    Completed(CompletedArm),
    #[cultcache(key = 2)]
    Denied(IdArm),
    #[cultcache(key = -2147483648)]
    Floor(IdArm),
    #[cultcache(key = 2147483647)]
    Ceiling(IdArm),
}

// The codec's unit arm (no members, written [key, []]), kept apart from the golden: a document list's arms carry ids.
#[derive(Clone, Debug, PartialEq, CultUnion)]
pub enum UnitOutcome {
    #[cultcache(key = 2)]
    Denied,
    #[cultcache(key = -2147483648)]
    Floor,
    #[cultcache(key = 2147483647)]
    Ceiling,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct PendingArm {
    #[cultcache(key = 0)]
    pub message: String,
    #[cultcache(key = 1)]
    pub id: String,
}

// An arm with no members of its own: just its element id.
#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct IdArm {
    #[cultcache(key = 0)]
    pub id: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct CompletedArm {
    #[cultcache(key = 0)]
    pub source_version: i32,
    #[cultcache(key = 1)]
    pub id: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct NamedArm {
    #[cultcache(key = 0)]
    pub name: String,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct UsesArm {
    #[cultcache(key = 0)]
    pub name: String,
    #[cultcache(key = 1)]
    pub uses: i32,
}

#[derive(Clone, Debug, PartialEq, CultSlots)]
pub struct CountArm {
    #[cultcache(key = 0)]
    pub name: String,
    #[cultcache(key = 1)]
    pub count: i32,
}

#[derive(Clone, Debug, PartialEq, CultUnion)]
pub enum Item {
    #[cultcache(key = 0)]
    Gem(NamedArm),
    #[cultcache(key = 1)]
    Tool(UsesArm),
    #[cultcache(key = 2)]
    Part(CountArm),
}

// The same arms as Item under its own keys: key 2 is unknown to it.
#[derive(Clone, Debug, PartialEq, CultUnion)]
pub enum CraftedItem {
    #[cultcache(key = 0)]
    Tool(UsesArm),
    #[cultcache(key = 1)]
    Part(CountArm),
}

#[derive(Clone, Debug, PartialEq, DatabaseEntry)]
#[cultcache(type = "tests.union_surface", schema = "tests.union_surface.v1")]
pub struct UnionSurface {
    #[cultcache(key = 0)]
    pub id: String,
    #[cultcache(key = 1)]
    pub version: i32,
    #[cultcache(key = 2)]
    pub root: Option<Node>,
    #[cultcache(key = 3)]
    pub outcomes: Vec<Outcome>,
    #[cultcache(key = 4)]
    pub part: Option<Item>,
    #[cultcache(key = 5)]
    pub crafted: Option<CraftedItem>,
    #[cultcache(key = 6)]
    pub missing: Option<Node>,
}

pub fn text(id: &str, content: &str) -> Node {
    Node::Text(TextArm { id: id.into(), content: content.into() })
}

pub fn image(id: &str, uri: &str, alt: &str) -> Node {
    Node::Image(ImageArm { id: id.into(), uri: uri.into(), alt: alt.into() })
}

pub fn boxed(id: &str, children: Vec<Node>) -> Node {
    Node::Box(BoxArm { id: id.into(), children })
}

pub fn constructed() -> UnionSurface {
    let inner = boxed("0000000000d0", vec![text("0000000000d1", "x"), image("0000000000d2", "uri://a", "alt")]);
    UnionSurface {
        id: "surface".into(),
        version: 3,
        root: Some(boxed(
            "0000000000c0",
            vec![
                text("0000000000c1", "hello"),
                Node::Use(UseArm {
                    id: "0000000000c2".into(),
                    composition: Composition { name: "comp".into(), body: Some(std::boxed::Box::new(inner)) },
                    slots: vec![Some(text("0000000000e1", "slot")), None, Some(boxed("0000000000e2", vec![]))],
                }),
                image("0000000000c3", "uri://b", ""),
            ],
        )),
        outcomes: vec![
            Outcome::Pending(PendingArm { message: "wait".into(), id: "0000000000a1".into() }),
            Outcome::Completed(CompletedArm { source_version: 7, id: "0000000000a2".into() }),
            Outcome::Denied(id_arm("0000000000a3")),
        ],
        part: Some(Item::Tool(UsesArm { name: "hammer".into(), uses: 12 })),
        crafted: Some(CraftedItem::Part(CountArm { name: "gear".into(), count: 4 })),
        missing: None,
    }
}

pub fn id_arm(id: &str) -> IdArm {
    IdArm { id: id.into() }
}

pub fn vector(name: &str) -> Vec<u8> {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../tests/vectors/union-parity")
        .join(name);
    std::fs::read(&path).unwrap_or_else(|error| panic!("{}: {error}", path.display()))
}
