# CultMesh Transport Planes

CultMesh selects a transport per workload. It does not ask one wire protocol to
be equally good at files, realtime state, and same-machine memory access.

## Authority map

**Owner:** `CultMeshSessionManager` selects an advertised route through a
registered connector for each traffic class. A connector owns only its physical
connection and byte movement.

**Inputs:** stable endpoint identity, advertised physical candidates, connector
support and priority, caller cancellation, and locality evidence for body
negotiation.

**Outputs:**

| Workload | Preferred plane | Contract |
| --- | --- | --- |
| Schemas, commands, receipts, manifests | TCP | `cultnet+tcp://` through `ICultMeshTransportConnector` |
| Immutable chunks and other file-shaped bodies | TCP byte stream | `cultmesh-content+tcp://` through `ICultMeshContentTransportConnector` |
| Realtime state | QUIC streams or datagrams | `ICultMeshRealtimeTransportConnector` with an explicit delivery mode |
| Same-machine SoA and large bodies | mapped/shared memory | body transport negotiation |

The TCP content plane sends typed CultNet request and response headers, then
streams raw chunk bytes outside the schema envelope. The transfer service owns
range hashing, resume checkpoints, provider failover, full-body verification,
and atomic promotion regardless of the selected content connector.
`CultMeshClient` installs the TCP schema and TCP content connectors by default.
Passing explicit connector lists replaces those defaults.

Realtime frames declare whether they require reliable ordered delivery,
latest-only delivery, or unreliable delivery. The optional
`GameCult.Mesh.Quic` package provides the .NET 10/MsQuic connector and server.
It maps reliable ordered frames to one persistent stream and latest-only frames
to independent streams with keyed `(producer epoch, sequence)` pending-state
replacement. Its
`System.Net.Quic` adapter fails closed for unreliable frames because that API
does not expose QUIC datagrams. `GameCult.Mesh.Quic.Native` owns the generic
Unity/client boundary through the MsQuic C API. The Unity package carries its
managed connector and the pinned Windows x64 Schannel runtime; consumers
register `CultMeshNativeQuicRealtimeTransportConnector`, then ask CultMesh for
the advertised realtime route. The connector currently receives provider state
and fails closed if used to send; commands and receipts remain on the typed TCP
control plane. No RUDP implementation is installed implicitly in its place.

Local mapped bodies remain the first state plane. A consumer opens the
advertised mapping when it is actually reachable; otherwise it connects to the
advertised QUIC route. Latest-only receivers must replace pending generations
per body rather than queueing render debt.

Provider simulation owns state generation, not socket completion. Latest-only
broadcast enqueues into a keyed, coalescing outbox owned by each physical peer
and returns without waiting for network delivery. The peer's transport worker
owns stream writes and eviction after disconnect or send failure. Thus a slow,
stalled, or departed client cannot backpressure a simulation tick, while a
healthy peer still converges on the newest generation. Reliable-ordered
broadcast remains completion-bearing because dropping or replacing it would
violate its declared delivery contract; caller cancellation can stop that
operation.

**Derived state:** connector choice, physical endpoint, connection health, and
transport diagnostics. None of these grant provider authority or become content
identity.

**Forbidden writers:** applications and renderers do not choose unadvertised
endpoints, implement reconnect loops, or bypass verification. Schema messages
must not carry bulk bodies. RUDP/LiteNetLib cannot become an implicit default
for any traffic class. QUIC does not own schemas or immutable files merely
because it can transport bytes.

**Shared paths:** discovery and identity resolution remain common. Each traffic
class has its own connector set, connection cache, priority tiers, and failure
state. All content connectors feed the same transfer and promotion owner.

**Cut line:** the former RUDP content server is
`CultMeshLegacyRudpContentServer`, and its connector must be registered
explicitly. It has basement priority and exists for compatibility and parity
measurement. The legacy datagram schema connector is likewise explicit and
lower priority than TCP. Existing hot-state publishers that still package
payloads as schema messages must move to the realtime connector contract; the
new seam does not pretend that migration has already occurred.

## Security and release gate

The current TCP control and content connectors are plaintext and therefore
restricted to local or otherwise trusted deployment. Adding TLS must not move
discovery, authorization, content verification, or application semantics into
the TCP adapter.

The QUIC adapter always uses TLS 1.3. Its certificate validation callback is
given the advertised endpoint identity so the consumer can bind transport
authentication to provider authorization. Test-only trust-all callbacks are not
a release configuration. A trusted discovery record may carry
`cert-sha256=<fingerprint>` on its QUIC route; absent a caller validator, the
connector accepts a self-signed provider certificate only when that pin matches.

## Local parity evidence

On 2026-07-19, the parity harness moved a 13 MiB payload on loopback using
1 MiB content chunks. Raw TCP reached 127.4 MiB/s, verified CultMesh TCP content
reached 59.0 MiB/s, raw MsQuic reached 51.5 MiB/s, and legacy CultMesh RUDP
reached 10.7 MiB/s while emitting 18,112,867 wire bytes. CultMesh TCP includes
chunk hashing, durable checkpoints, final hashing, and atomic promotion, so this
is not a claim of raw protocol parity. It is a repeatable workload comparison
and leaves the remaining framework overhead visible.

The same harness now has a state-only workload. On 2026-07-19, 1,000 latest-only
frames with 16 KiB payloads produced the following loopback result:

| Plane | Offered rate | Delivered | Highest generation | Settled time |
| --- | ---: | ---: | ---: | ---: |
| CultMesh QUIC latest-only | 23.8 MiB/s | 989 / 1,000 | 999 | 655.4 ms |
| CultNet RUDP latest | 45.6 MiB/s | 373 / 1,000 | 516 | 602.7 ms |

Offered rate measures publisher-side enqueue/write completion, not successful
delivery. RUDP accepted bytes faster but failed to deliver the terminal
generation after a 250 ms drain; that is a delivery failure, not useful
throughput. QUIC's reliable independent streams reached the terminal generation
while the receiver coalesced eleven superseded frames. Reproduce the workload
with:

```text
dotnet run --project tools/GameCult.TransportParity -- --state-only --state-bytes 16384 --state-frames 1000
```
