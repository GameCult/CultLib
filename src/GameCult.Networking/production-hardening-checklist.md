# GameCult.Networking Production Hardening Checklist

This library is readying itself for trusted local mesh use: localhost, LAN, or
controlled self-hosted runtime clusters such as Epiphany and Aquarium. It is
not yet claiming sainthood on the public internet.

## Local-mesh baseline now covered

- separate connection-attempt and auth-attempt throttles in `Server`
- versioned session tokens that supersede older issued tokens
- bounded reconnect backoff with jitter in `Client`
- raw payload logging gated behind explicit diagnostic opt-in
- schema-first raw document/snapshot lane with exact `schemaId` routing

## Missing hostile-network organs

### Transport posture

- LiteNetLib remains the transport substrate.
- There is no transport-level TLS story in this library today.
- Before internet exposure, decide whether LiteNetLib stays and gains a real
  outer protection model, or whether another transport becomes the canonical
  public edge.

### Credential and replay posture

- login/register payloads are AES-GCM encrypted with per-message nonces
- session tokens are signed and versioned
- replay resistance is still scoped to token expiry + supersession semantics,
  not a broader anti-replay protocol

Before hostile exposure:

- define replay expectations explicitly
- add adversarial tests around token reuse, stale reconnects, and reordered
  message delivery

### Session rotation and revocation

- superseded tokens are rejected once a newer session version is issued
- there is still no broader account-session management surface for:
  - explicit logout-all
  - administrative revocation
  - multi-device policy

### Input abuse and malformed payload handling

- malformed MessagePack is rejected by the serializer path
- there is no explicit message-size policy or backpressure contract yet

Before hostile exposure:

- add payload-size ceilings
- add stress tests for oversized and malformed inputs
- decide what gets dropped, logged, or disconnected

### Replication abuse cases

- raw snapshot/document replication assumes peers already share payload schema
  and trust boundaries
- there is not yet an adversarial policy for untrusted replication partners

Before hostile exposure:

- define peer authorization for raw snapshot/document lanes
- test unknown schema spam, snapshot flooding, and incompatible payload storms

### Missing tests

Still wanted before claiming internet-grade readiness:

- live client/server auth integration tests
- reconnect storm tests
- session supersession across multiple clients
- message-size and malformed-frame abuse tests
- load and soak tests for rate limiting and session refresh
