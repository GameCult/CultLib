//! The Rust CultNet first call from docs/start/README.md. The docs show the region between the
//! `snippet` markers verbatim, and `docs_show_the_tested_snippet` fails when they differ.

use cultcache_rs::{CultCache, DatabaseEntry};

// snippet:begin
use std::net::{TcpListener, TcpStream};
use std::thread;

use cultnet_rs::{
    CultNetDocumentBinding, CultNetDocumentPutOptions, CultNetDocumentRegistry, CultNetWireContract,
    TcpFramedTransportConnection, create_tcp_framed_transport_profile,
    decode_cultnet_message_from_slice, encode_cultnet_message_to_vec,
};

#[derive(Clone, Debug, PartialEq, Eq, DatabaseEntry)]
#[cultcache(type = "note")]
struct Note {
    #[cultcache(key = 0)]
    title: String,
    #[cultcache(key = 1)]
    body: String,
}

fn first_call() -> anyhow::Result<Note> {
    let mut documents = CultNetDocumentRegistry::new();
    documents.register(CultNetDocumentBinding::for_entry_with_schema_id::<Note>(
        "gamecult.first_call_note.v1".to_string(),
        "gamecult.first_call_note.v1".to_string(),
    ))?;
    let profile = |runtime_id| create_tcp_framed_transport_profile(runtime_id, Default::default());

    // Receiver: a loopback listener that applies the document put it receives to its own cache.
    let listener = TcpListener::bind("127.0.0.1:0")?;
    let port = listener.local_addr()?.port();
    let receiver = thread::spawn({
        let (documents, profile) = (documents.clone(), profile("receiver"));
        move || -> anyhow::Result<Note> {
            let (stream, _) = listener.accept()?;
            let frame = TcpFramedTransportConnection::new(stream, profile).receive()?;
            let message = decode_cultnet_message_from_slice(&frame.payload, CultNetWireContract::CultNetSchemaV0)?;
            let mut inbox = CultCache::new();
            inbox.register_entry_type::<Note>()?;
            documents.apply_raw_document_put_message::<Note>(&mut inbox, &message)
        }
    });

    // Sender: build a typed put message, encode it for the wire, send one frame.
    let put = documents.create_raw_document_put_message(
        "put-1",
        "note:hello",
        &Note { title: "Hello".to_string(), body: "sent over CultNet".to_string() },
        CultNetDocumentPutOptions::default(),
    )?;
    let bytes = encode_cultnet_message_to_vec(&put, CultNetWireContract::CultNetSchemaV0)?;
    TcpFramedTransportConnection::new(TcpStream::connect(("127.0.0.1", port))?, profile("sender"))
        .send("schema", &bytes)?;

    receiver.join().expect("receiver thread")
}
// snippet:end

#[test]
fn a_typed_document_crosses_loopback_into_a_second_cache() {
    let note = first_call().unwrap();
    assert_eq!(note, Note { title: "Hello".to_string(), body: "sent over CultNet".to_string() });
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
