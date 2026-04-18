# GameCult.Caching.MessagePack.Generator

`GameCult.Caching.MessagePack.Generator` is a Roslyn incremental source generator that emits MessagePack formatters for concrete `DatabaseEntry` subclasses.

## Scope

This project is infrastructure for the caching stack. Application code usually does not reference it directly.

Its job is to:

- find non-abstract subclasses of `DatabaseEntry`
- generate concrete `IMessagePackFormatter<T>` implementations
- generate resolver mappings consumed by `DatabaseEntryResolver`

## What It Generates

For each discovered concrete `DatabaseEntry` subtype, the generator emits:

- a formatter type
- a resolver mapping entry

Member ordering is:

1. `ID` first
2. members with `[Key(...)]`, ordered by key
3. remaining non-ignored instance fields and properties

That gives predictable binary layout while still supporting manual `[Key]` ordering.

## Example

Given:

```csharp
[MessagePackObject]
public class PlayerData : DatabaseEntry, INamedEntry
{
    [Key(1)] public string Email = string.Empty;
    [Key(2)] public string PasswordHash = string.Empty;
    [Key(3)] public string Username = string.Empty;

    [IgnoreMember]
    public string EntryName
    {
        get => Username;
        set => Username = value;
    }
}
```

the generator emits the formatter plumbing needed for MessagePack serialization of `PlayerData` without manually implementing an `IMessagePackFormatter<PlayerData>`.

## When You Need To Think About This Package

Usually only when:

- formatter generation is not happening
- a cache-entry type is not serializing as expected
- you are debugging build-time source generation behavior

## Distribution

The generator is packaged for consumers through [../GameCult.Caching.MessagePack.Analyzers/README.md](../GameCult.Caching.MessagePack.Analyzers/README.md).
