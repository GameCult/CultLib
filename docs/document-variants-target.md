# CultCache Document Variants: Target

Date: 2026-09-17

Status: target, before any substrate map or cut map. This document owns the ends.
A cut map, written after an Imagination pass over the Body, will own the means.
Branch: `codex/document-variants`.

## Why

A CultCache record today is always complete. To make a bigger laser from a
laser, an author copies the record and re-specifies every stat. For a ship hull
with several hardpoint layouts, every layout is a full copy of the hull. When
the base changes, each copy has to be found and edited by hand, and copies drift
apart silently.

The first consumer is Aetheria's catalog (`F:\Projects\Aetheria\GameData\Aetheria.cc`),
edited in CultCache Studio:

- **Hull variants:** one hull with several hardpoint layout options. A variant
  replaces the whole `HullData.Hardpoints` member.
- **Item variants:** a bigger laser. A variant changes stats nested inside the
  design's behavior list, for example one behavior's `PerformanceStat.Max`.

## Rulings (operator, 2026-09-17)

- **This is a CultCache feature, not an Aetheria mechanism.** Operator: "Yes, this
  is absolutely a CultCache feature, write it up in CultLib".
- **A variant is stored as a delta from its base and resolved by the cache.** A
  record stores a reference to its base plus a sparse set of overrides. The
  cache resolves it; readers see complete documents. The operator chose this
  over two alternatives. Authoring-only variants in Studio over flattened stored
  copies leave a derived cache pretending to be data. Per-type variant records
  in each consumer rebuild the feature bespoke for every type.

## Ends

- **Readers see complete documents.** `Get`, `GetAll`, lookups by name and by
  index, watches and snapshots return resolved documents. No consumer outside
  the cache and its authoring tools handles a delta.
- **A base change reaches its variants.** Editing a base changes every variant
  that does not override the edited value. Watches, indexes and name lookups
  observe that change on the variants too, in the same commit that changed the
  base.
- **A variant is a record.** It has its own key and name, and references to it
  behave like references to any record. In Aetheria a lot's design and a
  product's design can name a variant directly.
- **Studio edits the delta.** It shows which values a variant inherits and which
  it overrides, and it can clear an override so the base value applies again.
- **Loading refuses a broken variant loudly and names the records.** Cases:
  - a cycle of bases;
  - a base that does not exist;
  - a base whose type the variant's type cannot inherit from;
  - an override naming a member that does not exist on the type.

  Deleting a base that still has variants is refused.

## Invariants that must survive

- **Wire parity** for the persisted format (`src/GameCult.Caching/Contracts/cultcache-persistence-format.md`,
  `docs/persisted-record-encoding.md`). Every runtime that reads a store holding
  variants resolves them identically, or refuses the store loudly. Which
  runtimes resolve is decided in the map, against
  `docs/runtime-parity-scope.md`; none may misread silently.
- **One home store per type**, and atomic single-store commit
  (`cultcache-store-composition.md`). A base change and the resulting variant
  changes are one commit, not a repair that runs afterwards.
- **Schema compatibility** (`cultcache-schema-compatibility.md`): overrides are
  subject to the same semantic identity, soft drift and hard rejection rules as
  full records.
- **Compare-exchange** (`Expect`, `ExpectUnchanged`) keeps a single meaning for
  variants. The map settles whether it tests the stored delta or the resolved
  document; it must not mean both.

## Fork rulings (operator, 2026-09-17)

- **1, granularity: agreed.** Index addressing into lists is out, and addressing reuses
  CultNet's member-path grammar. The Imagination pass still chooses between stable element
  identity and whole-element replacement, and that choice comes back as a fork.
- **2, where resolution lives: still open.** Here, resolution means computing a
  variant's complete document from its base chain and its overrides. The operator asked
  what the fork meant, and it has been explained.
- **3, cross-store bases: no.** A variant and its base live in the same store.
- **4, CultNet: deltas.**
- **5, runtimes: C# first.** TypeScript, Python, Rust and Kotlin refuse a store
  or snapshot containing variants, loudly, until each implements resolution.
- **6, type change: open.** The operator called it "a good question".
- **7, clearing and rebasing.**
  - Clearing an override removes that member from the variant's stored delta. A member
    that is not overridden is not stored.
  - Rebasing (changing a variant's base) is supported. Operator: "rebasing is a nice
    tool to have, things can get annoying otherwise".

## Open forks, for the Imagination pass to map and the operator to rule

1. **Override granularity.**
   - Top-level members are enough for hardpoint layouts.
   - A bigger laser needs overrides inside list elements. Addressing list
     elements by index is fragile: an insertion into the base silently retargets
     every variant's overrides.
   - Candidates: stable element identity (for example a behavior's type within its
     design); replacing whole list elements only; or addressing paths.
   - CultNet's selection work already resolves member paths per leaf type,
     inherited members included (`docs/cultnet-selection-cut.md`). A variant's
     delta addressing should reuse that grammar, not invent a second one.
2. **Where resolution lives:** at load in the cache core, lazily on read, or as a
   materialized view the cache maintains. This affects memory, watch cost, and
   the compare-exchange meaning.
3. **Base and variant across stores.** Can a variant in one store name a base in
   another (a run-store variant of a catalog record)? This interacts with
   one-home-store routing and read-only stores.
4. **CultNet.** Do variants travel as deltas, or resolved? A subscriber that
   receives only resolved documents needs no resolver; one that edits needs
   deltas.
5. **Runtime scope.** Which of C#, TypeScript, Python, Rust and Kotlin resolve,
   and which refuse a store containing variants.
6. **Type change.** May a variant's type be a subtype of its base's type,
   adding members, or must the two types match?
7. **Clearing and reparenting.** What clearing an override writes, and whether
   a variant can change its base.

## Not in scope

- Scaling or formula variants ("every stat times 1.5"). An override states a
  value. Generated authoring can compute values.
- Any Aetheria catalog content. Aetheria adopts the feature after a release, as
  its own cut.
