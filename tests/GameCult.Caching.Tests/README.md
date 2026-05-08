# GameCult.Caching.Tests

`GameCult.Caching.Tests` contains NUnit tests for the caching library and its
single-file MessagePack persistence path.

## Scope

The tests currently cover:

- `SingleFileMessagePackBackingStore` round-trips
- explicit Cult document payload codecs
- hand-written MessagePack store snapshot/record/catalog serialization

## Run

```powershell
dotnet test tests\GameCult.Caching.Tests\GameCult.Caching.Tests.csproj
```
