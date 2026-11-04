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
| **C#** | [first call, tested](#c-first-call) | [package](../../packages/cultmath/README.md); snippet not yet | [package](../../src/GameCult.Geometry/README.md); snippet not yet | [package](../../src/GameCult.Networking); snippet not yet | [quickstart](../../src/GameCult.Mesh/docs/durable-node-quickstart.md); run by `scripts/verify-eve-getting-started.ps1` |
| **TypeScript** | [first call, tested](#typescript-first-call) | not yet | not yet | [package](../../packages/cultnet-ts/README.md); snippet not yet | [package](../../packages/cultmesh-ts/README.md); snippet not yet |
| **Rust** | [first call, tested](#rust-first-call) | [native core](../../packages/cultmath/native/cultmath-core); snippet not yet | not yet | [package](../../packages/cultnet-rs/README.md), [examples](../../packages/cultnet-rs/examples); snippet not yet | [package](../../packages/cultmesh-rs); snippet not yet |
| **Python** | [first call, tested](#python-first-call) | not yet | not yet | [package](../../packages/cultnet-py/README.md); snippet not yet | [package](../../packages/cultmesh-py/README.md); snippet not yet |

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
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using MessagePack;

[CultDocument("gamecult.first_call_item", "gamecult.first_call_item.v1")]
[MessagePackObject]
public sealed class Item
{
    [Key(0)] [CultName] public string Name = string.Empty;
    [Key(1)] [CultIndex] public string Category = string.Empty;
    [Key(2)] public int Value;
}

var store = new SingleFileMessagePackBackingStore("first-call.cc");
var cache = new CultCache();
cache.AddBackingStore(store);
var handle = await cache.AddAsync(new Item { Name = "Potion", Category = "Consumable", Value = 50 });
store.PushAll();                                   // write the .cc file

var reopened = new CultCache();
reopened.AddBackingStore(new SingleFileMessagePackBackingStore("first-call.cc"));
await reopened.PullAllBackingStoresAsync();        // read it back
var potion = reopened.Get<Item>(handle.Key);       // by key
var byName = reopened.GetByName<Item>("Potion");   // by [CultName]
```

The test's `Item` is nested and internal, so it carries `[MessagePackObject(AllowPrivate = true)]`; the snippet above is the public-class form and is not itself compiled. More: `src/GameCult.Caching/README.md`.

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
  indexes: { category: "category" },
});
const registry = defineDocumentRegistry(itemDocument);

const cache = CultCache.builder()
  .withRegistry(registry)
  .withGenericStore(new SingleFileMessagePackBackingStore("first-call.cc"))
  .build();
await cache.pullAllBackingStores();
await cache.put(itemDocument, "item:potion", { name: "Potion", category: "Consumable", value: 50 });

const reopened = CultCache.builder()
  .withRegistry(registry)
  .withGenericStore(new SingleFileMessagePackBackingStore("first-call.cc"))
  .build();
await reopened.pullAllBackingStores();
const potion = reopened.get(itemDocument, "item:potion");
const byName = reopened.getByName(itemDocument, "Potion");
```

The test imports from `../src/index`; an application imports `@gamecult/cultcache-ts`. More:
`packages/cultcache-ts/README.md`.

## Rust first call

Same shape with `derive(DatabaseEntry)`: slots are `#[cultcache(key = N)]`, so the `.cc` payload is the
slot-indexed array the C# runtime writes. The source is
[`packages/cultcache-rs/tests/first_call.rs`](../../packages/cultcache-rs/tests/first_call.rs), run by
`cargo test --test first_call` in `packages/cultcache-rs`; a second test in that file fails if the block
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

## Python first call

A document is a definition with an encoder and decoder; `name=` and `indexes=` register the name lookup
and indexes. The source is
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
    indexes={"category": "category"},
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

## Where next

- Smallest durable mesh path: `src/GameCult.Mesh/docs/durable-node-quickstart.md`.
- Application path, four chapters: `src/GameCult.Mesh/docs/getting-started/README.md`.
