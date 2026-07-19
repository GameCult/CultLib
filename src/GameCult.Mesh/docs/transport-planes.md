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
does not expose QUIC datagrams. A native MsQuic connector must own that mode and
the generic Unity runtime boundary. No RUDP implementation is installed
implicitly in its place.

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

The current TCP connectors are a local-development transport and are plaintext.
Remote release requires authenticated encryption and binding the authenticated
peer to the advertised provider identity. Adding TLS must not move discovery,
authorization, content verification, or application semantics into the TCP
adapter.

The QUIC adapter always uses TLS 1.3. Its certificate validation callback is
given the advertised endpoint identity so the consumer can bind transport
authentication to provider authorization. Test-only trust-all callbacks are not
a release configuration.

## Local parity evidence

On 2026-07-19, the parity harness moved a 13 MiB payload on loopback using
1 MiB content chunks. Raw TCP reached 127.4 MiB/s, verified CultMesh TCP content
reached 59.0 MiB/s, raw MsQuic reached 51.5 MiB/s, and legacy CultMesh RUDP
reached 10.7 MiB/s while emitting 18,112,867 wire bytes. CultMesh TCP includes
chunk hashing, durable checkpoints, final hashing, and atomic promotion, so this
is not a claim of raw protocol parity. It is a repeatable workload comparison
and leaves the remaining framework overhead visible.
