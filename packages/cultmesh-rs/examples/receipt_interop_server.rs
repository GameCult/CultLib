//! The real Rust RUDP document server on an ephemeral loopback port, for the cross-runtime write-receipt interop
//! harness (packages/cultmesh-ts/test/interop/write-receipt-interop.test.ts). It is the production server with a
//! recording sink and nothing else: stdout carries one JSON line per fact the harness asserts on, and the server runs
//! until stdin closes.
//!
//!   {"event":"listening","port":N}
//!   {"event":"applied","op":"put","messageId":"...","schemaId":"...","recordKey":"..."}
//!
//! Record keys that start with `refuse:` are refused by the sink (authorize and commit), which is how the harness
//! asks for a refused write.
use anyhow::Result;
use cultmesh_rs::{
    CultMeshRudpDocumentServer, CultMeshRudpRawDocumentReceipt, CultMeshRudpRawDocumentSink,
    CultMeshRudpSnapshotQuery, CultMeshSystemClock,
};
use cultnet_rs::CultNetRawDocumentRecord;
use serde_json::json;
use std::io::Read;
use std::net::UdpSocket;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;

const REFUSED_PREFIX: &str = "refuse:";

struct RecordingSink;

impl CultMeshRudpRawDocumentSink for RecordingSink {
    fn authorize_raw_document(&mut self, receipt: &CultMeshRudpRawDocumentReceipt) -> Result<()> {
        if receipt.document.record_key.starts_with(REFUSED_PREFIX) {
            anyhow::bail!("not authorized");
        }
        Ok(())
    }

    fn accept_raw_document(&mut self, receipt: CultMeshRudpRawDocumentReceipt) -> Result<()> {
        if receipt.document.record_key.starts_with(REFUSED_PREFIX) {
            anyhow::bail!("not authorized");
        }
        println!(
            "{}",
            json!({
                "event": "applied",
                "op": "put",
                "messageId": receipt.message_id,
                "schemaId": receipt.document.schema_id,
                "recordKey": receipt.document.record_key,
            })
        );
        Ok(())
    }
}

fn main() -> Result<()> {
    let source = |_: &CultMeshRudpSnapshotQuery| -> Result<Vec<CultNetRawDocumentRecord>> { Ok(Vec::new()) };
    let mut server = CultMeshRudpDocumentServer::new(
        UdpSocket::bind("127.0.0.1:0")?,
        RecordingSink,
        source,
        CultMeshSystemClock::default(),
        Default::default(),
    )?;
    println!("{}", json!({ "event": "listening", "port": server.local_addr()?.port() }));
    let stop = Arc::new(AtomicBool::new(false));
    let watcher = Arc::clone(&stop);
    thread::spawn(move || {
        let mut sink = Vec::new();
        let _ = std::io::stdin().read_to_end(&mut sink);
        watcher.store(true, Ordering::SeqCst);
    });
    while !stop.load(Ordering::SeqCst) {
        server.poll_once()?;
        thread::sleep(Duration::from_millis(1));
    }
    Ok(())
}
