# CultCache C# AOT / source-generator probe, 2026-10-27

Serves `cultlib-gaps:follow_up:cs-aot-safe-registration`. Probe pass, nothing admitted or committed. Session `self-2026-10-27-eureka`.

Method: a scratch console (`aot-probe`, kept at `C:\Users\Meta\AppData\Local\Temp\claude\F--Projects-CultLib\dfe49a4b-e0ab-440c-81c7-7325d2eb7405\scratchpad\aot-probe\`) placed at `tests/aot-probe` in a dangling commit on CultLib main (base `e3fac9c2`, never on a branch), built and run on Yggdrasil with `ygg-verify.sh` in `mcr.microsoft.com/dotnet/sdk:10.0` (net10.0, MessagePack 3.1.7, .NET 10.0.12, `apt install clang zlib1g-dev` works in the image, so real NativeAOT linux-x64 publishes ran). Logs: `job2.log`, `job3.log`, `job4.log` beside the probe. Each claim names its probe. Probe documents: `PlainDoc`, `PlainPartialDoc`, `RefDoc` (CultRecordRef<T> member and ref-keyed dictionary), `UnionMemberDoc` ([Union] interface member), `RootDoc`/`ArmDoc` (document that is a union arm), `MathDoc` (CultMath float3/float4), and a nested `internal` `[MessagePackObject(AllowPrivate = true)]` doc as CultLib's tests declare them.

## 1. Where the document formatter comes from

| Claim | Probe |
|---|---|
| A consumer that references only `GameCult.Caching.MessagePack` (as `tests/GameCult.Caching.ConsumerBuild` does) gets **no source generator at all**: 0 generated files. Every document formatter is `MessagePack.Formatters.Probe_*Formatter` in the dynamic assembly `MessagePack.Resolvers.DynamicObjectResolver` (Reflection.Emit). Cause: the library's `MessagePackAnalyzer` reference is `PrivateAssets="all"`, so the generator does not flow to consumers. | job2 "JIT A": `generated files (A): 0`; `Describe(opts.Resolver.GetFormatter<T>())` for all 8 doc types printed `dynamic=True`; `src/GameCult.Caching.MessagePack/GameCult.Caching.MessagePack.csproj` |
| When the consumer itself has `<PackageReference Include="MessagePack" Version="3.1.7">`, MessagePack's generator runs and emits a formatter per `[MessagePackObject]` type (12 files), into `MessagePack.GeneratedMessagePackResolver`, plus `[assembly: GeneratedAssemblyMessagePackResolverAttribute]`. | job3 "JIT E/F": `generated files: 12` |
| Those generated formatters are used with **no hook**: CultLib's composite options resolve `PlainDoc`, `PlainPartialDoc`, `RefDoc`, `UnionMemberDoc`, `ArmDoc`, `MathDoc`, and the nested internal doc to `MessagePack.GeneratedMessagePackResolver+Probe+*Formatter` (`dynamic=False`). `StandardResolver` finds them itself through `SourceGeneratedFormatterResolver`. `[assembly: CultCacheFormatterResolver(...)]` is not needed for this. | job3 JIT E/F `composite(...)` lines |
| What the type must look like: **not** `partial`, **not** required `public`. A public non-partial sealed class, a partial class, and a nested `internal` class with `[MessagePackObject(AllowPrivate = true)]` and public fields all got generated formatters. `[MessagePackObject]` with `[Key]` is the only requirement. | job3 JIT E/F |
| **Generator breakage: any `CultRecordRef<T>` member, or a `CultRecordRef<T>` dictionary key, makes the generated resolver fail to compile** (`CS0426: The type name 'GameCult' does not exist in the type 'GeneratedMessagePackResolver'`). The generator emits `new global::MessagePack.GeneratedMessagePackResolver.GameCult.Caching.CultRecordRefFormatter<PlainDoc>()`, a namespace-mangled name for the library's open-generic formatter. Reproduced with Target member only (G), dictionary key only (H), and both (B). The member attribute `[MessagePackFormatter(typeof(CultRecordRefFormatter<T>))]`, which `src/GameCult.Geometry/CultGeometryDocuments.cs:156,231,316` already carries, fixes a member (E) but a dictionary key cannot take one (D: attribute on Target, dictionary kept, still fails). | job2 B, job3 B/D/E/F, job4 G/H; `gen/.../MessagePack.GeneratedMessagePackResolver.g.cs` line 64-65 and `RefDocFormatter` read in job4 G |
| Union interface member, union-root abstract class, document-as-arm: the document formatters are generated; the union type itself resolves to `CultUnionGuard+Formatter<T>` over the generated `IShapeFormatter`/`RootDocFormatter` (the guard wraps whatever the inner chain gives). | job3 JIT E/F |
| CultMath members: `MathDoc` gets a generated formatter whose `float3`/`float4` members resolve at runtime through `CultMathResolver` (`GameCult.Caching.MessagePack.CultMathResolver+ArrayShape<float3>`, hand-written, no Emit, no generator). Round trip returned the same values. | job3 JIT F `float3` line, `plain=... math=1,2,3` |
| Byte identity: document payloads from dynamic (A) and generated (F) formatters are identical for PlainDoc, PlainPartialDoc, ArmDoc, MathDoc, UnionMemberDoc, nested doc (`93A1700792A161A162`, `92A27070CB3FF8...`, `92A175920091CA40200000`, ...). | job2 A hex vs job3 FR hex |

## 2. NativeAOT publish (`PublishAot`, `IsTrimmable`, `EnableTrimAnalyzer`, `TrimmerSingleWarn=false`)

| Claim | Probe |
|---|---|
| Publish succeeds with warnings (exit 0) in every variant. ~109 `IL` warning lines; 100 are inside MessagePack's `Dynamic*`/Emit machinery (still linked because `StandardResolver` references `DynamicObjectResolver`), 9 distinct CultLib source sites (below). The netstandard2.1 CultLib projects have no source-level trim analyzer (reference assemblies carry no annotations); the warnings come from ILC at publish, with file(line) of the first sequence point. | job3 `--- CultLib-source warnings` |
| Default publish, dynamic-fallback formatters (A): the app fails at runtime in `CultDocumentRegistry.Refresh()` -> `DiscoverMembers` with "has no constructor MessagePack can call": ILC trimmed constructor metadata that `ConstructorArity` reads reflectively. Registration is not AOT-safe even before serialization. | job2 AOT A run |
| With the probe assembly rooted (`TrimmerRootAssembly`) and dynamic fallback (AR): registration passes, **serialization fails** for every doc (`TargetInvocationException`; `IsDynamicCodeSupported=False`; `DynamicObjectResolver.GetFormatter<T>` throws `TypeInitializationException`), flush fails. | job3 AOT AR run |
| With rooted assembly and generated formatters (variants F: no ref members; E: ref member with the attribute workaround): all 7 registrations OK, all payloads, upsert, flush, reopen and read OK. **Payload lines identical to the JIT build** for both variants. Whole-file bytes differ by design (stamps), so identity is on document payloads. | job3 `F vs FR`, `E vs ER`: IDENTICAL |
| So registration needs the assembly rooted today, because of the reflection below; formatters need the consumer to reference MessagePack for the generator and to avoid `CultRecordRef` dictionary keys. | job3 |

## 3. Runtime-reflection sites on the registration and serialization path (CultLib main)

Warnings (job3, `CultLib-source warnings`) cover rows marked W. Others come from the source read.

Registry enumeration
- `ReflectionExtensions.cs:12-23`: `AppDomain.GetAssemblies()` + `Assembly.GetTypes()` + `GetCustomAttribute<CultDocumentAttribute>()` (W IL2026 at :23). Called by `CultDocumentRegistry.Refresh()` (`CultCache.cs:495`), run from the registry constructor.
- `CultCache.cs:511`, `:691`, `:727`: `GetCustomAttribute<CultDocumentAttribute>` / `<CultGlobalAttribute>` in `GetRequired` and `BuildDescriptor` (`BuildDescriptor` is `CultCache.cs:689`).

`DiscoverMembers` (`CultCache.cs:1087`)
- `:1090` `GetCustomAttribute<MessagePackObjectAttribute>(true)`.
- `:1098` `GetFields`, `:1105` `GetProperties`, `:1138` non-public `GetFields`/`GetProperties` (W IL2070 x4).
- `:1235`, `:1299`, `:1545`, `:1549`, `:1599`, `:1601`, `:1609`, `:1676` attribute reads on members (`CultReference`, `CultIndex`, `IgnoreMember`, `Key`, `CultDocument` of the target, `CultName`); `:1086` `GetCustomAttributes` up the override chain.
- `:1268` `OverrideChain` `GetProperty` (W IL2075).
- `ConstructorArity` `:1277`, `:1282` `GetConstructors(Public|NonPublic)` + `IsDefined(SerializationConstructor)` (W IL2070; the AOT-A runtime failure).
- `:1254` member getters built as `field.GetValue` / `PropertyInfo.GetValue` method groups; `:1649-1650` `GetValue` on dictionary entries at upsert, i.e. reflection stays on the hot path after registration, not only at registration.
- `:1306-1330` `IsEnumerableOfRecordRef` / `TryGetDictionaryRefTypes`: `GetInterfaces()` (W IL2070 :1310, :1323), `typeof(KeyValuePair<,>).MakeGenericType` at `:1330` (W IL3050); `:1689-1715` `ResolveReferenceTarget`: `GetInterfaces()`, `GetGenericArguments()` (W IL2070 :1702).
- `CultSchemaTypeNames.cs:13-26`: `GetElementType`, `GetGenericTypeDefinition`, `GetGenericArguments` (type-name rendering; shape-only, not flagged).

MessagePack-side wiring
- `CultDocumentMessagePackSerialization.cs:22-25`: `CultDocumentResolver.GetFormatter<T>` does `typeof(CultRecordRefFormatter<>).MakeGenericType(...)` + `Activator.CreateInstance` for every `CultRecordRef<T>` (W IL3050 :24). This one is not covered by the generator; the generator's own attempt at `CultRecordRef<T>` is the broken one in section 1.
- `:49` `Assembly.GetCustomAttributes<CultCacheFormatterResolverAttribute>()`; `:69-71` `CreateResolver`: `GetField("Instance")`, `GetProperty("Instance")`, `GetConstructor(Type.EmptyTypes)`, `Activator.CreateInstance(resolverType)` (W IL2067 + IL2070 x3 at :69).
- `CultMessagePackSecurity.cs:27-28`: `typeof(RecordRefComparer<>).MakeGenericType` + `Activator.CreateInstance`, for `CultRecordRef<T>` dictionary keys (W IL3050 :27).
- `CultUnionGuard.cs:20`: `typeof(T).GetCustomAttributes<UnionAttribute>(false)` per union type (not flagged; attribute read on a closed `T`).

Not on the registration path but in the library: `CultInspectorModel.cs` (`Activator.CreateInstance`, `MakeGenericType`, `GetMembers` at :37, :165-168, :377, :424-431, :540, :597, :757-821), reached only by the inspector.

## Shape of the fix these probes support (advice, not a decision)

- The reflective registration needs a generated descriptor per `[CultDocument]` type (members, slots, constructor arity, getters, attributes), the same shape the generator already proves possible for formatters. A generator is not free, since MessagePack's generator is the consumer's, not the library's.
- `CultRecordRef<T>` needs a non-reflective formatter path (`CultDocumentResolver` and `CultMessagePackSecurity` both `MakeGenericType`), and the upstream generator bug with the library's open-generic formatter must be avoided, or the dictionary-key shape will stay uncompilable for generator consumers.
- Consumers need to reference MessagePack (generator) directly; the library's `PrivateAssets="all"` analyzer does not reach them.
