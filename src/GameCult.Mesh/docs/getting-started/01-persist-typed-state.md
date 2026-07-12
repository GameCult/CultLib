# 1. Persist Typed Application State

Start with a typed CultCache document and a durable CultMesh node. The complete
sample is [the durable node quickstart](../durable-node-quickstart.md).

At the end of that guide you should have:

- a document type with a stable schema id;
- a `CultMeshNode` whose database owns the live local document;
- a `.cc`/CultCache-backed durable state path;
- a restart proof that reads the same typed document.

Do not create an Eve-shaped copy of this state. The Eve surface published in
chapter 3 points at typed records and commands owned by the provider.

Verification:

```powershell
dotnet test tests/GameCult.Networking.Tests/GameCult.Networking.Tests.csproj --filter FullyQualifiedName~CultMeshNode_CanRoundTrip_DurableTypedDocument_Through_PublicDatabaseSurface
```

Next: [connect a client by stable identity](02-connect-by-identity.md).
