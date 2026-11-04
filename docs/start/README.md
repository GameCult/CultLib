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
| **Rust** | [package](../../packages/cultcache-rs/README.md); snippet not yet | [native core](../../packages/cultmath/native/cultmath-core); snippet not yet | not yet | [package](../../packages/cultnet-rs/README.md), [examples](../../packages/cultnet-rs/examples); snippet not yet | [package](../../packages/cultmesh-rs); snippet not yet |
| **Python** | [package](../../packages/cultcache-py/README.md); snippet not yet | not yet | not yet | [package](../../packages/cultnet-py/README.md); snippet not yet | [package](../../packages/cultmesh-py/README.md); snippet not yet |

The Rust and Python CultCache first calls are the next cut for this page. For the whole
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

## Where next

- Smallest durable mesh path: `src/GameCult.Mesh/docs/durable-node-quickstart.md`.
- Application path, four chapters: `src/GameCult.Mesh/docs/getting-started/README.md`.
