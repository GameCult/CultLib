# Start Here

CultLib is one stack in several languages: **CultCache** (typed `.cc` persistence),
**CultNet** (schema-v0 wire messages and transports) on top of it, and **CultMesh** (Verse
discovery, replication, authority) on top of that. **CultMath** and **Geometry** sit beside the
stack as portable numerics and engine-neutral geometry. The C# runtime in `src/` is the
reference; the other runtimes keep wire parity with it.

If you want typed state on disk, start with CultCache. Everything else assumes it.

## Language x package

A cell links a snippet that a test in this repo runs, or says **not yet**. "Package" links
the package's own README or source, which is real but carries no tested first-call snippet from
this page.

| | CultCache | CultMath | Geometry | CultNet | CultMesh |
| --- | --- | --- | --- | --- | --- |
| **C#** | [first call, tested](#c-first-call); [update, tested](#c-update-with-retry) | [package](../../packages/cultmath/README.md); snippet not yet | [package](../../src/GameCult.Geometry/README.md); snippet not yet | [first call, tested](#c-cultnet-first-call); [package](../../src/GameCult.Networking) | [quickstart](../../src/GameCult.Mesh/docs/durable-node-quickstart.md); run by `scripts/verify-eve-getting-started.ps1` |
| **TypeScript** | [first call, tested](#typescript-first-call); [update, tested](#typescript-update-with-retry) | not yet | not yet | [first call, tested](#typescript-cultnet-first-call); [package](../../packages/cultnet-ts/README.md) | [package](../../packages/cultmesh-ts/README.md); snippet not yet; [recipe: two-client .io game](io-game.md), tested with two live clients, browser page not yet run |
| **Rust** | [first call, tested](#rust-first-call); [update, tested](#rust-update-with-retry) | [native core](../../packages/cultmath/native/cultmath-core); snippet not yet | not yet | [first call, tested](#rust-cultnet-first-call); [package](../../packages/cultnet-rs/README.md), [examples](../../packages/cultnet-rs/examples) | [package](../../packages/cultmesh-rs); snippet not yet |
| **Python** | [first call, tested](#python-first-call) | not yet | not yet | [first call, tested](#python-cultnet-first-call); [package](../../packages/cultnet-py/README.md) | [package](../../packages/cultmesh-py/README.md); snippet not yet |

For the whole
picture of what each piece is for, see the
[package chooser in the top-level README](../../README.md#which-package-do-i-want).

## C# first call

Define a typed document, put it in a cache backed by a `.cc` file, read it back through a second
cache. The source is
[`tests/GameCult.Caching.Tests/FirstCallTests.cs`](../../tests/GameCult.Caching.Tests/FirstCallTests.cs),
which is what `dotnet test tests/GameCult.Caching.Tests --filter FirstCallTests` runs, so the snippet
cannot drift without the test failing.

```csharp
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

[CultDocument("gamecult.first_call_item", "gamecult.first_call_item.v1")]
[MessagePackObject]
public sealed class Item
{
    [Key(0)] [CultName] public string Name = string.Empty;
    [Key(1)] public string Category = string.Empty;
    [Key(2)] public int Value;
}

public static class FirstCall
{
    public static async Task<Item?> Run(string path)
    {
        var store = new SingleFileMessagePackBackingStore(path);
        var cache = new CultCache();
        cache.AddBackingStore(store);
        var handle = await cache.AddAsync(new Item { Name = "Potion", Category = "Consumable", Value = 50 });
        store.PushAll();                                   // write the .cc file

        var reopened = new CultCache();
        reopened.AddBackingStore(new SingleFileMessagePackBackingStore(path));
        await reopened.PullAllBackingStoresAsync();        // read it back
        var potion = reopened.Get<Item>(handle.Key);       // by key
        var byName = reopened.GetByName<Item>("Potion");   // by [CultName]
        return byName ?? potion;
    }
}
```

`FirstCall.Run` is the call; a second test in that file fails if the block above stops matching it. More: `src/GameCult.Caching/README.md`.

## C# update with retry

Change a record you may not be the only one changing. `Update` reads the record as its store holds it, hands your function a fresh
copy of it, and commits the result only if the record is still the one it read. If another writer got there first, it
reads again and runs your function again, up to five times. The source is
[`tests/GameCult.Caching.Tests/UpdateCallTests.cs`](../../tests/GameCult.Caching.Tests/UpdateCallTests.cs); the
snippet reuses `Item` from the first call.

```csharp
using GameCult.Caching;
using GameCult.Caching.MessagePack;

public static class UpdateCall
{
    public static CultUpdateResult<Item> Restock(string path, CultRecordKey key)
    {
        var cache = CultCacheMessagePack.Create(path);
        return cache.Update<Item>(key, item =>
        {
            item ??= new Item { Name = "Potion", Category = "Consumable" };
            item.Value += 10;
            return item;
        });
    }
}
```

The write is durable when `Update` returns, with no `PushAll`. A lost race re-runs the function, so keep it free of
side effects; when the attempts run out the result says `LostRace` instead of throwing. `UpsertAsync` plus
`PushAll` is the blind opt-out: last writer wins.

## TypeScript first call

Same shape in TypeScript: the document is a name, a schema id and a `parse`-able schema (zod here),
and the `.cc` file is wire-compatible with the C# one. The source is
[`packages/cultcache-ts/test/first-call.test.ts`](../../packages/cultcache-ts/test/first-call.test.ts),
run by `npm test` in `packages/cultcache-ts` (or `node --test dist-test/test/first-call.test.js`
after `tsc`).

```ts
import { z } from "zod";
import {
  CultCache,
  SingleFileMessagePackBackingStore,
  defineDocumentRegistry,
  defineDocumentType,
} from "@gamecult/cultcache-ts";

const itemDocument = defineDocumentType({
  type: "item",
  schemaId: "gamecult.first_call_item.v1",
  schemaName: "gamecult.first_call_item",
  schemaVersion: "gamecult.first_call_item.v1",
  schema: z.object({ name: z.string(), category: z.string(), value: z.number().int() }),
  name: "name",
});
const registry = defineDocumentRegistry(itemDocument);

async function firstCall(path: string) {
  const cache = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await cache.pullAllBackingStores();
  await cache.put(itemDocument, "item:potion", { name: "Potion", category: "Consumable", value: 50 });

  const reopened = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await reopened.pullAllBackingStores();
  const potion = reopened.get(itemDocument, "item:potion");
  const byName = reopened.getByName(itemDocument, "Potion");
  return { potion, byName };
}
```

The test imports from `../src/index` where the block reads `@gamecult/cultcache-ts`; a second test in that file fails if the block stops matching the rest. More:
`packages/cultcache-ts/README.md`.

## TypeScript update with retry

`update` changes a record from what the store holds now and commits only if it is still that record. The source is the
second region of
[`packages/cultcache-ts/test/first-call.test.ts`](../../packages/cultcache-ts/test/first-call.test.ts), which uses the
definitions of the first call above; a test in that file fails if the block stops matching the file.

```ts
async function restock(path: string) {
  const cache = CultCache.builder()
    .withRegistry(registry)
    .withGenericStore(new SingleFileMessagePackBackingStore(path))
    .build();
  await cache.pullAllBackingStores();
  return cache.update(itemDocument, "item:potion", (item) => {
    const current = item ?? { name: "Potion", category: "Consumable", value: 0 };
    return { ...current, value: current.value + 10 };
  });
}
```

The write is durable when the promise resolves. A lost race re-runs the function, so keep it free of side effects; when the
attempts run out the result says `lostRace` instead of throwing. `put` is the blind opt-out: last writer wins.

## Rust first call

Same shape with `derive(DatabaseEntry)`: slots are `#[cultcache(key = N)]`, so the `.cc` payload is the
slot-indexed array the C# runtime writes. The source is
[`packages/cultcache-rs/tests/first_call.rs`](../../packages/cultcache-rs/tests/first_call.rs), run by
`cargo test --locked --test first_call` in `packages/cultcache-rs`; a second test in that file fails if the block
below stops matching the file. The Rust runtime has no name lookup, so the read-back is by key. More:
`packages/cultcache-rs/README.md`.

```rust
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
```

## Rust update with retry

`update` changes a record from what the store holds now and commits only if it is still that record. The source is the
second region of
[`packages/cultcache-rs/tests/first_call.rs`](../../packages/cultcache-rs/tests/first_call.rs), which uses the `Item` of
the first call above; a test in that file fails if the block stops matching the file.

```rust
fn restock(path: &Path) -> anyhow::Result<CultUpdateResult<Item>> {
    let mut cache = CultCache::new();
    cache.register_entry_type::<Item>()?;
    cache.add_generic_backing_store(SingleFileMessagePackBackingStore::new(path))?;
    cache.pull_all_backing_stores()?;
    cache.update::<Item, _>("item:potion", |item| {
        // The store's copy of the record, or nothing if it has none; this may run again on a lost race.
        let mut item = item.unwrap_or(Item {
            name: "Potion".to_string(),
            category: "Consumable".to_string(),
            value: 0,
        });
        item.value += 10;
        Some(item)
    })
}
```

The write is durable when `update` returns. A lost race re-runs the closure, so keep it free of side effects; when the
attempts run out (five, or the bound `update_with_attempts` takes) the outcome says `LostRace` instead of failing, and
returning `None` writes nothing and says `Declined`. `put` is the blind opt-out: last writer wins.

## Python first call

A document is a definition with an encoder and decoder; `name=` registers the name lookup, `indexes=` unique lookups and
`roles=` many-row selection aliases. The source is
[`packages/cultcache-py/tests/test_first_call.py`](../../packages/cultcache-py/tests/test_first_call.py),
run by `python -m unittest tests.test_first_call` in `packages/cultcache-py` with `src` on `PYTHONPATH`
and `msgpack` installed (the store needs it). A second test fails if the block below stops matching the
file. More: `packages/cultcache-py/README.md`.

```python
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path

from cultcache_py import CultCache, SingleFileMessagePackBackingStore, define_document_type


@dataclass
class Item:
    name: str
    category: str
    value: int


item_doc = define_document_type(
    "item",
    encode=lambda item: asdict(item),
    decode=lambda raw: Item(**raw),
    name="name",
)


def first_call() -> Item:
    path = Path(tempfile.mkdtemp()) / "first-call.cc"

    cache = (
        CultCache.builder()
        .register_document_type(item_doc)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    cache.pull_all_backing_stores()
    cache.put(item_doc, "item:potion", Item("Potion", "Consumable", 50))  # written to the .cc file

    reopened = (
        CultCache.builder()
        .register_document_type(item_doc)
        .add_generic_store(SingleFileMessagePackBackingStore(path))
        .build()
    )
    reopened.pull_all_backing_stores()  # read it back
    assert reopened.get_by_name(item_doc, "Potion") == reopened.get_required(item_doc, "item:potion")
    return reopened.get_required(item_doc, "item:potion")
```

## CultNet first calls

CultNet is schema-v0 messages in length-prefixed frames. The first call in each runtime is the same
shape: define a typed document, build a `cultnet.document_put_raw.v0` message carrying its MessagePack
payload, send one frame over a loopback pipe, and apply the received message to a second cache. Each
block is verbatim from the test that runs it; a second test in the same file fails if the block stops
matching.

### C# CultNet first call

Source: [`tests/GameCult.Networking.Tests/CultNetFirstCallTests.cs`](../../tests/GameCult.Networking.Tests/CultNetFirstCallTests.cs),
run by `dotnet test tests/GameCult.Networking.Tests --filter CultNetFirstCallTests`. More: `src/GameCult.Networking/README.md`.

```csharp
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

[CultDocument("gamecult.first_call_note", "gamecult.first_call_note.v1")]
[MessagePackObject]
public sealed class Note
{
    [Key(0)] [CultName] public string Title = string.Empty;
    [Key(1)] public string Body = string.Empty;
}

public static class CultNetFirstCall
{
    public static async Task<Note?> Run()
    {
        var inbox = new CultCache();                            // the receiver's cache
        var documents = new CultNetDocumentRegistry();
        var received = new TaskCompletionSource<bool>();

        // Receive: a loopback listener that applies each raw document put to the inbox.
        using var server = new TcpFramedCultNetSchemaServer(new TcpListener(IPAddress.Loopback, 0));
        server.OnCultNet<CultNetDocumentPutRawMessage>(async (put, peer) =>
        {
            await documents.ApplyRawDocumentPutMessageAsync<Note>(inbox, put);
            received.TrySetResult(true);
        });

        // Send: a typed document as a cultnet.document_put_raw.v0 message.
        using var client = new TcpFramedCultNetSchemaClient();
        client.Connect("127.0.0.1", server.LocalEndPoint.Port);
        var handle = new CultRecordHandle<Note>(new CultRecordKey("note:hello"));
        client.SendCultNet(documents.CreateRawDocumentPutMessage(
            "put-1", handle, new Note { Title = "Hello", Body = "sent over CultNet" }));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return inbox.Get<Note>(handle.Key);
    }
}
```

### TypeScript CultNet first call

Source: [`packages/cultnet-ts/test/first-call.test.ts`](../../packages/cultnet-ts/test/first-call.test.ts), run by
`npm test` in `packages/cultnet-ts` (build `packages/cultcache-ts` first: `npm run build` there). The test imports
from `../src` where the block reads `@gamecult/cultnet-ts`. More: `packages/cultnet-ts/README.md`.

```ts
import { createServer, connect } from "node:net";
import { z } from "zod";
import { CultCache, defineDocumentType } from "@gamecult/cultcache-ts";
import {
  CultNetDocumentRegistry,
  createTcpFramedCultNetPeer,
  defineCultNetDocumentBinding,
} from "@gamecult/cultnet-ts";

const noteDocument = defineDocumentType({
  type: "note",
  schemaId: "gamecult.first_call_note.v1",
  schemaName: "gamecult.first_call_note",
  schemaVersion: "gamecult.first_call_note.v1",
  schema: z.object({ title: z.string(), body: z.string() }),
  name: "title",
});
const documents = new CultNetDocumentRegistry([defineCultNetDocumentBinding({ definition: noteDocument })]);

async function firstCall() {
  const options = { runtimeId: "first-call", wireContract: "cultnet.schema.v0" } as const;
  const inbox = CultCache.builder().withDocumentType(noteDocument).build(); // the receiver's cache

  // Receiver: a loopback listener whose peer applies each document put to the inbox.
  const received = new Promise<void>((resolve, reject) => {
    const server = createServer((socket) => {
      const peer = createTcpFramedCultNetPeer(socket, options);
      peer.on("invalidMessage", reject);
      peer.on("message", async (message) => {
        if (message.schemaVersion !== "cultnet.document_put_raw.v0") return;
        await documents.applyRawDocumentPutMessage(inbox, message);
        peer.close();
        server.close();
        resolve();
      });
    }).listen(0, "127.0.0.1", () => {
      // Sender: connect, build a typed put message, send it.
      const { port } = server.address() as { port: number };
      const sender = createTcpFramedCultNetPeer(connect(port, "127.0.0.1"), options);
      sender.send(
        documents.createRawDocumentPutMessage(documents.get("note")!, "put-1", "note:hello", {
          title: "Hello",
          body: "sent over CultNet",
        }),
      );
      received.finally(() => sender.close());
    });
  });
  await received;
  return inbox.get(noteDocument, "note:hello");
}
```

### Rust CultNet first call

Source: [`packages/cultnet-rs/tests/first_call.rs`](../../packages/cultnet-rs/tests/first_call.rs), run by
`cargo test --locked --test first_call` in `packages/cultnet-rs`. More: `packages/cultnet-rs/README.md`.

```rust
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
```

### Python CultNet first call

Source: [`packages/cultnet-py/tests/test_first_call.py`](../../packages/cultnet-py/tests/test_first_call.py), run by
`python -m unittest tests.test_first_call` in `packages/cultnet-py` with both `src` and `../cultcache-py/src` on
`PYTHONPATH` and `msgpack` installed. The socket pair stands in for a TCP connection. More: `packages/cultnet-py/README.md`.

```python
import socket
from dataclasses import asdict, dataclass

from cultcache_py import CultCache, define_document_type
from cultnet_py import (
    apply_raw_document_record,
    document_put_raw,
    encode_frame,
    parse_message,
    read_frame,
    schema_document_map,
)


@dataclass
class Note:
    title: str
    body: str


note_doc = define_document_type(
    "note",
    encode=lambda note: asdict(note),
    decode=lambda raw: Note(**raw),
    name="title",
)


def first_call() -> Note:
    sender, receiver = socket.socketpair()  # an in-process pipe; use socket.create_connection for TCP

    # Send: a typed document as a cultnet.document_put_raw.v0 message in one length-prefixed frame.
    put = document_put_raw(
        message_id="put-1",
        key="note:hello",
        schema_id=note_doc.catalog_entry().schema_id,
        stored_at="2026-01-01T00:00:00Z",
        payload=note_doc.encode_payload(Note("Hello", "sent over CultNet")),
    )
    sender.sendall(encode_frame(put.to_bytes()))

    # Receive: read the frame, parse the message, apply its record to a second cache.
    message = parse_message(read_frame(receiver.makefile("rb")))
    assert message.schema_version == "cultnet.document_put_raw.v0"
    inbox = CultCache.builder().register_document_type(note_doc).build()
    apply_raw_document_record(inbox, schema_document_map([note_doc]), message.body["document"])
    sender.close()
    receiver.close()
    return inbox.get_required(note_doc, "note:hello")
```

## Where next

- Smallest durable mesh path: `src/GameCult.Mesh/docs/durable-node-quickstart.md`.
- Application path, four chapters: `src/GameCult.Mesh/docs/getting-started/README.md`.
