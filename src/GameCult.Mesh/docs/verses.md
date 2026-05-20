# CultMesh Verses

A Verse is a rule-bearing consensus graph.

That phrasing is intentionally dry. It keeps the system honest.

The same transport can carry many Verses:

- `aetheria-main`: public Aetheria, operated by GameCult regional simulators.
- `aetheria-community-hardcore`: compatible transport, different rules.
- `aetheria-modded-skylands`: compatible branch with required runtime plugins.
- `p2p-arena-nightly`: peer-to-peer authority for a small experimental game.

## Descriptor

`CultMeshVerseDescriptor` owns:

- `VerseId`: stable id
- `DisplayName`: public-facing name
- `AuthorityModel`: operator cluster, federated cluster, peer-to-peer, or
  subscribed overlay
- `Compatibility`: transport version, rules hash, compatible Verse ids, and
  plugin requirements
- `DiscoveryEndpoints`: endpoints where nodes for the Verse can be found
- `AuthorityRuntimeIds`: known authoritative runtimes when cluster-shaped
- `ParentVerseId`: source Verse for overlays or branches

## Compatibility

Two Verses can share a transport without sharing rules.

Transfer is allowed when:

- transport versions match, and
- rules hashes match, or the destination explicitly lists the source Verse as
  compatible

That lets a modded branch choose its boundary. It can accept transfers from
Aetheria Main, require a plugin pack, and still reject another branch with
incompatible rules.

## Authority

Authority is a Verse-level policy, not a transport accident.

- `OperatorCluster`: one known operator cluster is final authority.
- `FederatedCluster`: several regional or organizational clusters share
  authority.
- `PeerToPeer`: participating peers directly contribute authority.
- `SubscribedOverlay`: the Verse follows another Verse and adds local rules,
  mods, or presentation layers.

## Discovery

`CultMeshVerseCatalog` is a local reactive catalog. It can be filled from
static config, cloud discovery, peer gossip, or mod portals. It does not care
where the descriptor came from. That keeps discovery pluggable while the Verse
model stays stable.

Peer exchange is the torrent-shaped layer under Verse discovery. A Verse
catalog says which graph exists; a peer catalog says which nodes can currently
serve or observe parts of that graph. Peer cards can advertise discovery,
primary shard, replica, read replica, and simulation observer roles, but those
cards do not grant authority by themselves. Public operator Verses should treat
authority lease ids and signatures as mandatory before accepting committed
state from a peer.

Authority leases are the narrow bridge between discovery and trust. A lease
binds a peer id to a Verse, role, shard scope, issuer, validity window, and
signature. Peer exchange can spread cards quickly; lease validation decides
whether a card is allowed to touch committed state.
