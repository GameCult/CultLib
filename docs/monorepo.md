# CultLib Monorepo Map

CultLib is now the compatibility home for the GameCult persistence and protocol
family. The C# projects remain under `src/`; language ports live beside them so
contracts can be changed and tested in one place.

## Layout

- `src/`: C# CultCache, CultNet, CultMesh, logging, and Unity-facing packages.
- `packages/cultcache-ts`: TypeScript CultCache package.
- `packages/cultnet-ts`: TypeScript CultNet package.
- `packages/cultmesh-ts`: TypeScript CultMesh local-first package.
- `crates/cultcache-rs`: Rust CultCache crate and derive macro.
- `crates/cultnet-rs`: Rust CultNet crate.
- `python/cultcache-py`: Python CultCache package.

## Ownership

- CultLib root owns cross-language compatibility, shared documentation, and
  coordinated verification commands.
- Each language package owns its local build, packaging, and ergonomic public
  API.
- Wire schemas and persistence formats must stay inspectable. Do not fork a
  schema silently inside one language package.

## First Verification Commands

```powershell
dotnet test CultLib.sln --no-restore
npm install
npm run test:ts
cargo test --workspace
python -m pytest python/cultcache-py/tests
```

## CultMesh TypeScript Boundary

`packages/cultmesh-ts` is intentionally local-first today. It gives VoidBot,
Mimir, and other TypeScript runtimes a CultMesh-branded durable node over
`cultcache-ts` plus a CultNet document registry bridge. It does not yet claim
the full networked shard authority, replica catch-up, or Verse discovery
transport behavior of the C# package.

That missing work should be added by moving the shared contract forward, not by
letting TypeScript invent a second mesh with similar nouns and different truth.
