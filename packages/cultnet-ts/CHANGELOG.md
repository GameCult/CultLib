# Changelog

All notable changes to `@gamecult/cultnet-ts` are recorded here, following
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

### Added

- `cultNetTextError(error)`: the text-only `cultnet.error.v0` refusal, with `routingHint`, `code` and `details` null. The legacy-union error decode uses it, so there is one place that spells the null members.
- `CultNetRudpSocketTransportServer`: one UDP socket serving many peers, each remote address and port with its own reliable session (`peerConnected`, `frame`, `peerDisconnected` events; `send(peer, ...)` answers on the peer a frame came from). A second client no longer replaces the first; Connects beyond `maxPeers` (default 256) are dropped, and `close()` resolves once every peer's goodbye has left the socket. The shape follows the Rust `CultNetRudpServerHub` and C# `CultNetRudpSocketTransportServer`, and the wire format is unchanged. The optional `peerTimeoutMs` (idle peers ended and told so; default off) exists only in TypeScript. `CultNetRudpSocketTransportConnection` in `server` mode still serves exactly one peer.

- The `cultnet.snapshot_request.v1`, `cultnet.database_subscribe.v1` and `cultnet.snapshot_response_raw.v1` carriers and the selection payload types (`CultNetSelection`, `CultNetRawDocumentHeader`, `CultNetEdge`). `parseCultNetMessage` accepts them (absent members arrive as absent keys; a page carries exactly one of `headers`/`documents`; an empty or blank `schemas`/`keys` entry throws `CultNetSelectionInvalidError` naming its field), and `encodeCultNetMessageForWire` writes the reference's nil-keyed, declaration-ordered map. Judged against bytes the C# reference wrote (`contracts/cultnet/interop/carrier-vectors.cs-written.json`) and the selection vectors C# and Rust wrote. The selection engine (evaluation, cursors, the declaration-dependent door checks) is not ported.

### Breaking

- The package is renamed `cultnet-ts` to `@gamecult/cultnet-ts` (the repo already scopes `@gamecult/cultcache-ts`), so the unscoped name cannot be claimed by someone else. Import from `@gamecult/cultnet-ts` and its subpaths; the API is unchanged.
- `CultNetErrorMessage` carries `routingHint`, `code` and `details` (null when absent), as the reference writes them and `cultnet.error.schema.json` requires; the interface previously dropped them. New `CultNetErrorCode`, `CultNetErrorDetails` and `CultNetShardRoutingHint` types.

- `CultMeshCdnChunkRef` carries `id` (slot 4), the reference's element id. `encodeManifest` writes five-slot chunk references and `packContent` sets the decimal offset as the id, as the reference does; a manifest written before ids reads `""` and is rewritten with the slot. Content vectors regenerated from the reference.

- Needs the cultcache-ts release that ships with this change (its manifest range moves with that release): the Idunn runtime authority reader calls `readSingleFileStore`, which 0.14.x does not export. An absent authority file is now a plain `Error` without a code.

## [0.3.0]

### Added
- `cultnet-ts/content`: the CultMesh CDN content plane. Manifest and chunk
  types, canonical manifest encode and decode, `packContent`, SHA-256 helpers,
  and the chunk and artifact record keys, with wire parity to the C# reference.

### Changed
- Depends on `@gamecult/cultcache-ts` `^0.14.1`, which exports
  `checkPayloadDepth`.
