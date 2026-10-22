# Prior art: refuse or walk a container the identity walk does not own

For `variants:question:c2a-unowned-holders` (campaign `variants`, cut `c2a-element-id-holes` r5).
These are from Imagination's reading. No web fetch was made in this pass, so check the
pointers before quoting them outside the mind.

| System | Unknown or unsupported type in a payload | Extension point | Pointer |
|---|---|---|---|
| Protocol Buffers | Not expressible. A field is a scalar, a message, `repeated`, or `map<scalar, V>`. No other container exists. | None. You model the data as messages. | protobuf.dev, Language Guide (proto3), "Repeated fields" and "Maps" |
| Microsoft Bond | Fixed container set: `list`, `vector`, `set`, `map`, `nullable`, `blob`, `bonded`. Any other type is a codegen error. | Type aliases with explicit converters, declared per type. | microsoft.github.io/bond/manual/bond_cs.html, "Type aliases" and "Custom type mappings" |
| Orleans 7+ serializer | Throws `CodecNotFoundException` when it serializes a type with no codec (not `[GenerateSerializer]`, not registered). | Surrogates via `[RegisterConverter]`, or codecs registered per type. Built-in codecs cover the BCL immutable collections. | learn.microsoft.com/dotnet/orleans/host/configuration-guide/serialization |
| MessagePack-CSharp 3.1.7 | `DynamicObjectResolver` refuses an unannotated type (formatter null, the serializer throws) unless the caller opts into contractless. | Resolvers, `[MessagePackFormatter]`. | Probed: `scratchpad/imag-r5/probe1.log` (NkHold: std=null, contractless builds one) |
| System.Text.Json (source gen) | `NotSupportedException` for types outside the generated metadata. Polymorphism needs `[JsonDerivedType]` registration. | `JsonConverter<T>`, `[JsonDerivedType]`. | learn.microsoft.com/dotnet/standard/serialization/system-text-json/polymorphism |

## What the table says for CultCache's id walk

1. Every system that guarantees something about a payload's structure fails closed on a
   shape it does not own. The failure is loud and comes at build or first use. It never
   drops data silently. Protobuf and Bond refuse when the schema is written, and Orleans,
   MessagePack and STJ refuse when the type is first serialized. None of them walks an
   arbitrary wrapper on the chance that it is a container.
2. Each system supports a fixed set of containers and walks those natively. Extension is
   explicit and per type: Bond aliases, Orleans surrogates, STJ converters. The walker
   never infers it from a type's shape.
3. Orleans covers the BCL immutable collections with built-in codecs. Each added container
   is a deliberate, tested entry. A reasonable consumer therefore expects ImmutableArray to
   be supported when the library says so, and refused loudly when it does not. Nobody
   expects silent nil ids.

CultCache's id walk today is open-world: a shape it does not recognise passes as a leaf.
That is the opposite of every row above, and it is the common cause of findings s1-s4.
