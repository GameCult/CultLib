# CultMesh MCP Bridge: Priority Campaign

Date: 2026-09-18

Status: named, not started. Operator (2026-09-18): "I'm kind of surprised we
don't have a generic CultMesh MCP bridge" / "Note it in CultLib as a priority
campaign, but yes, for another day". No cut map, no branch, nobody assigned.

## The gap

Nothing in GameCult bridges CultMesh to MCP. The only first-party MCP server is
VoidBot's retrieval server (repos, Discord history); every other MCP hit under
`F:\Projects` is a vendored copy inside stale `Epiphany\.epiphany-run` sandboxes.

So an agent that wants to read a daemon's state or drive it has no general path.
Each daemon needs bespoke tooling, which is why nobody has any.

## What made it urgent

Aetheria needs an editor rigged: create an object, attach a component, set a
transform, point a field at an asset. Brokkr (`F:\Projects\Brokkr`) already
executes exactly those commands in Unity as a CultMesh provider
(`surfaces/unity/Packages/com.gamecult.brokkr/Editor/BrokkrUnityCommandExecutor.cs`:
`createGameObject`, `attachComponent`, `setGameObjectTransform`,
`setGameObjectParent`, `setComponentProperty`, `instantiatePrefab`,
`createPrefabVariant`, `assignMaterial`, `createScriptableObject`), publishes a
host snapshot, and returns typed receipts. An agent still cannot send one: the
daemon has no CLI, and its surface is CultMesh documents.

The alternatives an agent falls back to are both bad, and both have drawn blood
in Aetheria: hand-edited scene YAML (a bad `sed` prefixed every line of a
committed map, `Aetheria 78fd617c`), or a one-shot editor script written blind
against a scene nobody can see.

Brokkr-specific tooling would treat the symptom. The missing organ is general.

## Why the contract is already there

`cross-runtime-primitives-roadmap.md` names MCP as a consumer three times, and
the two primitives a bridge needs are already specified there:

- **typed state pointer** — a stable typed reference that resolves, watches, and
  carries route hints and source schema (`:72-73`);
- **operation binding descriptor** — a typed operation id with request schema and
  route hints (`:75`).

A bridge lowers those, the way Eve lowers a composition graph. It does not invent
a second description of what a provider offers.

## Shape to map

- **Tools:** discover providers and surfaces (through Odin, the organ that
  already aggregates them); resolve and watch typed state at a pointer; invoke an
  operation and return its typed receipt.
- **MCP is a xenos boundary,** so JSON lives there and only there. Authority stays
  typed CultMesh state behind it.
- **Admission.** A read-only bridge is cheap. The moment it invokes operations it
  can mutate live Verse state, and it takes the same admission and receipt path as
  any other caller. It does not get a private door.

## Open, for the pass that takes this

- Where it lives: beside the Rust CultMesh in CultLib, inside Odin, or its own
  repo.
- Whether watches can reach an MCP client at all, or whether the first cut is
  resolve-and-invoke with polling.
- What a provider must publish before it is usable through the bridge, and whether
  Brokkr already publishes it.
