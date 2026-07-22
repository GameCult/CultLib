# GameCult.Mesh.Quic

`GameCult.Mesh.Quic` is the optional MsQuic-backed realtime state plane for
CultMesh daemons and .NET tools. It implements the transport-neutral contracts
from `GameCult.Mesh`; it does not own discovery, provider authorization, state
meaning, commands, receipts, schemas, or immutable content.

The package targets .NET 10. It uses `System.Net.Quic`, which requires a
supported MsQuic runtime and TLS 1.3. Unity runtimes do not consume this package;
their generic CultMesh lowerer needs a native MsQuic connector implementing the
same `ICultMeshRealtimeTransportConnector` contract.

## Provider

```csharp
using GameCult.Mesh.Quic;

await using var server = await CultMeshQuicRealtimeServer.ListenAsync(
    new CultMeshQuicRealtimeServerOptions
    {
        ListenEndPoint = new IPEndPoint(IPAddress.Any, 7443),
        ServerCertificate = providerCertificate
    });

await server.BroadcastAsync(new CultMeshRealtimeFrame
{
    ChannelId = "aetheria.entities",
    SchemaId = "eve.entity_soa.v1",
    BodyId = "body:aetheria:entities",
    ProducerEpoch = epoch,
    Sequence = sequence,
    Delivery = CultMeshRealtimeDelivery.LatestOnly,
    Payload = encodedSoa
});
```

Advertise the listener as `cultmesh-state+quic://host:port`. Certificate
identity and provider identity must be bound by the application trust policy;
accepting every certificate is appropriate only in isolated tests.
For directly trusted discovery records, a provider may append its uppercase
SHA-256 certificate fingerprint as `?cert-sha256=...`. The default connector
accepts a self-signed certificate only when its raw certificate bytes match that
advertised pin. A caller-supplied validator remains authoritative when present.

## Consumer

```csharp
var connector = new CultMeshQuicRealtimeTransportConnector(
    new CultMeshQuicRealtimeConnectorOptions
    {
        ValidateProviderCertificate = ValidateAdvertisedProvider
    });

using var sessions = new CultMeshSessionManager(
    discovery,
    schemaConnectors,
    contentConnectors,
    new ICultMeshRealtimeTransportConnector[] { connector });

var session = await sessions.ConnectRealtimeAsync(providerId);
var frame = await session.ReceiveAsync(cancellationToken);
```

## Delivery modes

- `ReliableOrdered` uses one ordered QUIC stream.
- `LatestOnly` uses independent streams and keeps at most one pending frame per
  channel and body. A newer `(producer epoch, sequence)` generation replaces
  queued state.
- `Unreliable` fails closed in this adapter because `System.Net.Quic` does not
  expose QUIC datagrams. Use a native MsQuic connector when datagram semantics
  are required.

Schemas, commands, receipts, manifests, and immutable content remain on their
TCP planes. Same-machine SoA should negotiate mapped/shared memory instead of
crossing QUIC.
