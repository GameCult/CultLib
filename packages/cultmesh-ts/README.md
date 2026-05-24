# cultmesh-ts

`cultmesh-ts` is the TypeScript CultMesh surface for local GameCult runtimes.
It opens a durable `cultcache-ts` store, registers typed documents, and exposes a
small CultMesh-branded node API that VoidBot and Mimir can use before the full
networked CultMesh authority model is ported.

```ts
import { CultMesh } from "cultmesh-ts";

const node = await CultMesh.startNode("state/voidbot.ccmp", {
  documents: [voidSelfProfileDocument],
});

await node.put(voidSelfProfileDocument, "self", profile);
const loaded = node.get(voidSelfProfileDocument, "self");
await node.flush();
```

This package owns the local TypeScript entrypoint. It does not yet own shard
authority, replica catch-up, or networked Verse discovery.
