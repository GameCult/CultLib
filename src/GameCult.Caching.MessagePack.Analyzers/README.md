# GameCult.Caching.MessagePack.Analyzers

`GameCult.Caching.MessagePack.Analyzers` is the delivery mechanism for the MessagePack source generator used by the cache libraries.

## Scope

This project is primarily packaging infrastructure. It exists so consuming projects can receive generator output at compile time without treating the generator assembly as a normal runtime dependency.

## What It Does

- references the generator project
- arranges for the generator assembly to be packed under `analyzers/dotnet/cs`
- keeps generator assets out of the normal compile/runtime reference flow

## Practical Meaning

When a consuming project adds the analyzer package:

- the generator runs during compilation
- concrete `DatabaseEntry` types receive generated MessagePack formatters
- `GameCult.Caching.MessagePack` can then serialize those types without handwritten formatter code

## Current State

This project is a packaging host. It does not currently expose custom analyzer diagnostics of its own.
