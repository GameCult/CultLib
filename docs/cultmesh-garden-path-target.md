# CultMesh garden path: target

Status: draft target for campaign `cultmesh-garden-path`, 2026-10-27. It holds the
ends, not the means. The code below is illustrative: it shows what a developer
should have to write, and no name in it is decided.

## The problem

Operator, 2026-10-27: "the value is in solving the multiplayer scaling problem
for indie developers in an ergonomic fashion. Having one authoritative server is
fine, up to a point, but there's no real open source playbook on how to ship an
MMO, and existing solutions like SpacetimeDB have a huge ergonomics gap. I want
CultMesh to feel like magic: as a garden path, you annotate your domain POCOs and
simulation code and declare your authority structure ... and it'll all just work
without the developer needing to understand networking deeply."

Prior art: `research/cultmesh-garden-path/prior-art-2026-10-27.md` (frameworks,
ergonomics, scale-out) and `research/witness-authoritative-networking/` (consensus
and P2P anti-cheat). What the field shows, in short:

- One API over several authority modes ships today (Photon Fusion: Server, Host,
  Shared; Unity NGO: client-server and distributed authority), and in both the
  modes leak into the developer's code (`IsServer` against `HasAuthority`,
  features missing in one mode).
- SpacetimeDB gives one machine per database. Scaling past it, client
  prediction, and the reducer-shaped rewrite of game code are left to the
  developer.
- Determinism, where it is required at all, is required of the whole simulation
  (Croquet/Multisynq, Photon Quantum), and that tax is the main ergonomic cost.
- Shipped world-scale designs treat a spatial unit as indivisible and move
  players between units (EVE, Pax Dei, Folia); nobody publishes an open,
  end-to-end playbook (Astron, the nearest, is discontinued).

## What a developer writes (illustrative)

The reference game is a small browser .io game (operator, 2026-10-27: "the .io
game, Aetheria later"): players steer orbs, eat pellets, and shoot.

```csharp
[CultDocument("orbs.player", "orbs.player.v1")]
public sealed class Player
{
    [CultIndex] public string Id { get; set; } = "";
    public Fix2 Position { get; set; }
    public Fix2 Velocity { get; set; }
    public int Mass { get; set; }
}

[CultDocument("orbs.shot", "orbs.shot.v1")]
public sealed class Shot
{
    [CultIndex] public string Id { get; set; } = "";
    public string Shooter { get; set; } = "";
    public Fix2 Origin { get; set; }
    public Fix2 Direction { get; set; }
}

// Runs every tick. Predicted on the owning client, corrected by authority.
public sealed class Movement : CultSystem
{
    [Predicted]
    public void Step(Player player, PlayerInput input, Tick tick) { /* ... */ }
}

// A non-owner may re-run this to settle a dispute, so it must be
// deterministic. The analyzer enforces that on this system only.
[Adjudicable]
public sealed class Hits : CultSystem
{
    public HitResult Resolve(Shot shot, Rewind<Player> target) { /* ... */ }
}

var game = CultMesh.Game("orbs")
    .Documents<Player, Pellet, Shot>()
    .Systems<Movement, Eating, Hits>()
    .Authority(Authority.Server);
//  .Authority(Authority.TrustedCoop);
//  .Authority(Authority.Witnessed(w => w.Challenger(Party.Victim).Quorum(3).Arbiter(Party.Server)));
//  .Authority(Authority.Regions(r => r.Grid(512).Handoff()));   // later: past one server
```

Changing the authority line is the whole migration between modes, unless the new
mode re-executes a system on a non-owner and that system is not yet
deterministic. Then the analyzer names exactly those systems.

## Invariants

- `one-model`: server-authoritative, trusted co-op, witnessed and regioned
  authority are configurations of one authority declaration, answering per
  document: who writes, who checks, who arbitrates. No mode has its own API for
  state, systems or queries.
- `modes-do-not-leak`: game code does not branch on the authority mode or ask
  "am I the server"; a system states what it does and the runtime decides where
  it runs. Where a mode cannot offer a capability, the declaration is refused at
  startup with a reason, never degraded silently at runtime.
- `determinism-is-per-system`: only code a non-owner may re-execute must be
  deterministic. That requirement is marked on the system and checked by an
  analyzer at build time, not by documentation or by desync in production.
- `state-is-cultcache`: game state is ordinary CultCache documents; the
  framework adds no second state model and no JSON on the load-bearing path.
- `authority-is-typed-state`: who holds authority over what is CultMesh state
  (leases, shard primaries), inspectable and revocable, never inferred from
  transport success or peer catalogs.
- `garden-path-needs-no-networking`: the reference game is written without the
  developer touching sockets, serialisation, tick scheduling, prediction or
  reconciliation code.
- `honest-guarantees`: each mode states its fault model (trusted server, trusted
  peers, honest-majority sampled witnesses with server arbitration) and its
  documentation and tests claim nothing past it.
- `browser-is-first-class`: the reference game's client runs in a browser and can
  write (submit inputs, own predicted state), not only watch.
- `built-for-load`: what the garden path ships that is database-shaped (the
  tick commit, the batch door, logs, subscriptions) is architected for
  unreasonable load from the start (operator 2026-10-27,
  `cultlib-gaps:ruling:database-shaped-built-for-load`): "if what we're shipping
  is database shaped, people will use it as such".

## Sequence (each a campaign or stage of its own; one foundation at a time)

1. Server-authoritative garden path for the .io game: documents, systems,
   ticked scheduling, client prediction and reconciliation, browser client that
   writes. This is the proof of ergonomics.
2. Trusted co-op: authority moves to the owning peer; same game code.
3. Deterministic dispute kernel (CultMath fixed-point hit tests, cross-runtime
   vectors) and the `[Adjudicable]` analyzer.
4. Witnessed authority: signed, hash-chained inputs; victim challenge; sampled
   network-near witnesses; server arbitration; browser-to-browser channels.
   Demonstration: a cheating client caught and corrected.
5. Regions: spatial authority handoff and interest management past one server.
6. Aetheria on the same declaration.

## Not in scope

- Aetheria until stage 6. Eve and Gjallar (deprecated).
- A new CultCache storage engine (the paged single-file store stays postponed,
  `variants:ruling:page-tree-store-postponed`). The garden path fills gaps in
  their owners, built for load.
- Asynchronous Byzantine total ordering, zero-knowledge proofs, and chains of
  any kind.
- The existing witness aggregator (`CultNetSimulationConsensus`, unsigned and
  weighted by observed votes) as a base for stage 4: it is the trusted co-op
  first stage by design and will be replaced, not extended.

## Open forks (to the Fork Desk, each with prior art first)

1. Where simulation code runs in the browser: C# compiled to WebAssembly (one
   implementation everywhere, which also shrinks the determinism problem to
   fixed-point maths, at the cost of runtime download size), C# on the server
   with TypeScript systems in the browser (two implementations of each predicted
   system), or another arrangement.
2. Server transport for the browser: WebSocket as today, or WebTransport
   (QUIC datagrams) on the provider side.
3. Tick model: fixed tick with a declared rate, or per-system rates.
