# CultMesh Provider Session

## Authority map

- Owner: the provider-session broker owns registration, active lease fencing,
  accepted publication membership, command retention, receipt acceptance, and
  withdrawal.
- Inputs: a physical CultNet session, an explicit registration-authorization
  decision, correlated typed operations, the broker clock, provider commands,
  and persistence callbacks. The current RUDP adapter does not itself provide
  an authenticated claim.
- Outputs: leases, application acceptances, accepted raw documents, pushed
  typed commands, persisted receipts, and deterministic removals.
- Derived state: socket sessions, retry schedules, connection health, and
  provider projections are observations. They do not authorize publications.
- Forbidden writers: RUDP packet acknowledgements, source addresses, direct raw
  document callbacks, renderer-owned catalogs, and arrival-time freshness maps
  cannot create accepted provider truth.
- Shared paths: initial connect and reconnect use `provider.register`; initial,
  replayed, and live documents use `provider.publication.put`; explicit
  withdrawal and lease expiry remove the broker-owned publication set; command
  retry ends only when `provider.receipt.put` is accepted.
- Cut line: a receiver must stop accepting unleased provider raw puts before it
  claims this protocol as its ingress authority.

## Wire contract

Provider lifecycle operations use the reliable ordered CultNet schema channel
and the existing `cultnet.operation_request.v0` / `cultnet.operation_response.v0`
envelopes with service id `gamecult.mesh.provider_session`. Inner payloads are
camel-case MessagePack maps encoded as `messagepack-base64`.

The v1 operations are:

| Operation | Request payload | Successful payload |
| --- | --- | --- |
| `provider.register` | `gamecult.mesh.provider_registration.v1` | `gamecult.mesh.provider_lease.v1` |
| `provider.renew` | `gamecult.mesh.provider_lease_renewal.v1` | `gamecult.mesh.provider_lease.v1` |
| `provider.publication.put` | `gamecult.mesh.provider_publication_put.v1` | `gamecult.mesh.provider_mutation_acceptance.v1` |
| `provider.publication.delete` | `gamecult.mesh.provider_publication_delete.v1` | `gamecult.mesh.provider_mutation_acceptance.v1` |
| `provider.receipt.put` | `gamecult.mesh.provider_receipt_put.v1` | `gamecult.mesh.provider_mutation_acceptance.v1` |
| `provider.withdraw` | `gamecult.mesh.provider_withdrawal.v1` | `gamecult.mesh.provider_mutation_acceptance.v1` |

Broker-to-provider commands are raw typed documents using
`gamecult.mesh.provider_command.v1`. Asset bodies do not travel inside these
operation payloads.

Responses use `ok`, `conflict`, `expired`, `denied`, or `invalid`. A transport
ACK is never an application acceptance. Renewal issues a new lease id and
immediately fences the previous id. The broker derives the set removed by full
withdrawal; a provider cannot use a supplied list to hide owned publications.

Public deployment additionally requires a CultNet authentication claim that
authorizes the provider identity. Until that claim is wired and checked, the
TypeScript RUDP adapter is a private-development transport.

`CultMeshProviderSessionBroker` is an in-memory reference broker and socket
proof. Its command and receipt retention does not survive process restart.
Production receivers must persist that control-plane state atomically with
accepted publication ownership; Odin must not substitute these volatile maps
for its durable broker authority.
