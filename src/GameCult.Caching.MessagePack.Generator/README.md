# GameCult.Caching.MessagePack.Generator

A Roslyn incremental source generator that emits CultCache document metadata,
and MessagePack payload codecs where it can, for classes marked
`[CultDocument]`.

## Scope

Infrastructure for the caching stack. Application code does not reference it
directly; it arrives through `GameCult.Caching.MessagePack.Analyzers`. Without
it, `CultDocumentRegistry` builds descriptors by reflection, and the two differ:
reflective descriptors include inherited members and order unkeyed members by
declaration token; generated descriptors include only declared members and
order unkeyed members by name. For derived documents or unkeyed members the two
produce different schema ids. This is scheduled to be fixed in Cut 4.

## What It Generates

One `GeneratedCultDocumentMetadataProvider_<Assembly>` per assembly, registered
with `[assembly: CultGeneratedDocumentMetadataProvider]`, holding for each
`[CultDocument]` class:

- schema name, schema version, and whether the type is `[CultGlobal]`
- member definitions (name, slot, persisted type name, reference and
  cardinality, `[CultName]`, `[CultIndex]`)
- name and index accessors
- a payload serializer, and a deserializer when the type has a public or
  internal parameterless constructor

Members are the public, non-static, non-`[IgnoreMember]` fields and settable
properties **declared on the class itself**; inherited members are not
discovered. Members with `[Key(n)]` take slot `n`; unkeyed members follow the
highest key in ordinal name order. Annotate every persisted member: source
order is not a schema.

Payload codecs are emitted only when slots are dense (`0..n-1`) and the
compilation references MessagePack and `GameCult.Caching.MessagePack`.
Otherwise the definition carries no codec and serialization falls back to the
reflective path. A generated payload is an array of the members in slot order;
the deserializer leaves missing trailing slots at their initializer values and
skips unknown extra slots. Generated codecs read
`CultDocumentMessagePackSerialization.Options` at call time.

The generator has no `[Union]` handling.

## When You Need To Think About This Package

- a document's descriptor or schema id differs between generated and
  reflective builds
- a document serializes through the reflective path when you expected a codec
- you are debugging build-time source generation

## Distribution

Packaged for consumers through
[../GameCult.Caching.MessagePack.Analyzers/README.md](../GameCult.Caching.MessagePack.Analyzers/README.md).
