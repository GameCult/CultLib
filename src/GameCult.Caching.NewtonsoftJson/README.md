# GameCult.Caching.NewtonsoftJson

`GameCult.Caching.NewtonsoftJson` provides human-readable JSON backing stores for `GameCult.Caching` using Newtonsoft.Json.

## Scope

The package provides:

- `SingleFileNewtonsoftJsonBackingStore`
- `MultiFileNewtonsoftJsonBackingStore`
- known-type polymorphic `DatabaseEntry` serialization through closed discriminators

## Usage

```csharp
using GameCult.Caching;
using GameCult.Caching.NewtonsoftJson;

var cache = new CultCache();
var store = new MultiFileNewtonsoftJsonBackingStore("Data");

cache.AddBackingStore(store);
await cache.PullAllBackingStoresAsync();
```

## Type Discovery

The JSON backing stores build a known-type map from the assemblies already loaded in the current `AppDomain`. In the common case, this means entry types defined in the consumer's application assembly work without extra configuration once that assembly is loaded.

The stores do not deserialize arbitrary CLR type names from JSON. Each file contains a closed discriminator plus the entry payload, and deserialization succeeds only when the discriminator matches a registered concrete `DatabaseEntry` subtype.

For less typical setups, the stores also expose:

- `RegisterType<T>()`
- `RegisterAssembly(Assembly assembly)`
- `RefreshKnownTypes()`

Example:

```csharp
var store = new MultiFileNewtonsoftJsonBackingStore("Data");
store.RegisterAssembly(typeof(MyCustomEntry).Assembly);
```

## Notes

- Files are indented JSON for inspection and editing.
- Polymorphic entry types are serialized with a closed discriminator map rather than open-ended type-name handling.
- `MultiFileNewtonsoftJsonBackingStore` builds on the base multi-file store behavior in `GameCult.Caching`, including rename-safe writes.
