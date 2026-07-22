# Distributed CDN Documents

CultMesh CDN artifacts use typed CultCache storage and a separate managed
content-delivery session. Manifests may travel through snapshots or shard logs;
artifact payload chunks remain in provider storage and cross the network through
`cultmesh.content.v1`. Unity, TypeScript, and other runtimes consume the same
manifest and verification contract without making snapshots a bulk byte lane.

This is the canonical path for moving Aetheria assets into CultLib. Starbridge
Unity and Starbridge Electron should consume the same records so UI textures,
map icons, environment layers, shaders, and build payloads do not fork by
client.

## Feature Overview

The CDN feature has four layers:

1. `CultMeshCdnArtifactManifest` describes one versioned payload.
2. `CultMeshCdnArtifactChunk` stores content-addressed payload bytes.
3. `CultMeshEntityPrefabPackage` describes a portable entity/prefab graph and
   references CDN artifact manifests for meshes, textures, materials, animation,
   audio, or other runtime payloads.
4. Mesh asset sharing moves those signed manifests and chunks through central
   servers, LAN peers, or internet peers without granting peers authority over
   game state.

This is not a Unity asset bundle system and not a raw FBX distribution system.
Blender is the authoring ground truth for entity prefabs. Brokkr can mirror a
legacy Unity prefab into Blender for migration, but the deployed CDN artifact is
the Brokkr/Blender-authored snapshot lowered into CultMesh documents.

Typical flow:

1. Unity publishes a one-time `brokkr.unity.prefab_mirror_snapshot.v0` for an
   existing prefab.
2. Brokkr Blender imports that snapshot into a collection so artists can
   configure renderable entity requirements in Blender.
3. Brokkr Blender publishes a `brokkr.prefab.snapshot.v0` deploy snapshot from
   the collection.
4. A deployer packages referenced meshes, textures, and materials as CDN
   artifacts.
5. The deployer writes a `CultMeshEntityPrefabPackage` that references those CDN
   artifact manifests.
6. Unity, TypeScript, and later clients fetch the same package and lower it into
   their runtime entity/component model.

## Document Shape

`CultMeshCdnArtifactManifest` is the versioned asset handle. It carries:

- `ArtifactId`: logical id, such as `aetheria/ui/icons/minimap`.
- `Kind`: `asset`, `build`, or `package`.
- `Version`: caller-defined release or content version.
- `ContentHash`: SHA-256 of the full materialized payload.
- `SizeBytes`: full payload size.
- `MimeType`: byte interpretation hint.
- `Chunks`: ordered `CultMeshCdnChunkRef` placements.
- `Tags` and `Metadata`: routing and domain hints.

`CultMeshCdnArtifactChunk` is content-addressed by SHA-256. It carries only the
chunk hash, byte count, and raw payload. Placement is deliberately in the
manifest reference, so equal chunk bytes can be reused across assets and build
updates.

## Runtime Contract

Clients should:

1. Fetch or receive a manifest by record key, tag, or index.
2. Give it to `CultMeshContentTransferService` with one or more content providers.
3. Let that owner request missing chunks through managed content sessions.
4. Use only the atomically promoted `<content-hash>.body` returned after chunk
   and full-body verification.
5. Interpret bytes according to `MimeType` plus domain metadata.

Single-channel data stays single-channel. For example, a gravity map can use
raw bytes with metadata such as `channels=r32` instead of pretending to be an
RGBA texture.

## Mesh Asset Sharing Policy

CultMesh asset distribution is allowed to use installed clients as a peer asset
cache because Aetheria cannot assume enough central infrastructure to serve all
game files authoritatively. This must be visible to players and configurable in
the client, but the initial product policy is opt-out rather than opt-in.

The default policy should be:

- Local cache is always enabled.
- LAN asset sharing is enabled by default.
- Internet peer asset sharing is enabled by default for signed asset chunks and
  prefab packages.
- Executable or build-update seeding is disabled by default until the UX and
  signing story is mature.
- Uploading is disabled or heavily throttled during gameplay.
- Uploading has conservative default rate and daily caps.
- Metered, cellular, battery-constrained, VPN, or corporate-network cases should
  disable or throttle peer upload when detectable.
- The client must expose a clear `Do not upload game assets to other players`
  control.
- The client must expose telemetry: cache size, current upload/download rate,
  bytes served to peers, and bytes received from peers.

Suggested runtime configuration:

```csharp
var policy = new MeshAssetSharingPolicy
{
    EnabledByDefault = true,
    AllowLanPeers = true,
    AllowInternetPeers = true,
    AllowBuildArtifacts = false,
    DisableDuringGameplay = true,
    DisableOnMeteredNetwork = true,
    RequireSignedManifests = true,
    ShowStatusTelemetry = true
};
```

Peers are never trusted as authority. They only serve bytes. Every chunk is
content-addressed, every artifact is reconstructed from a manifest, and game
clients must verify hashes and signatures before using the result.

## C# Publishing

```csharp
var artifact = CultMesh.PackCdnArtifact(
    "aetheria/environment/gravity/ragnarok",
    gravityBytes,
    new CultMeshCdnPackOptions
    {
        Kind = CultMeshCdnArtifactKinds.Asset,
        Version = "2026.06.27",
        MimeType = "application/octet-stream",
        Tags = ["aetheria", "environment", "gravity"],
        Metadata =
        {
            ["channels"] = "r32",
            ["width"] = "1024",
            ["height"] = "1024"
        }
    });

await CultMesh.PublishCdnArtifactAsync(node.Cache, artifact);
```

For build distribution, use the same API with `Kind = CultMeshCdnArtifactKinds.Build`
and metadata such as `platform=windows-x64` or `channel=internal`.

## CultNet Publication And Delivery

Publish manifests and chunks into the provider cache:

```csharp
await CultMesh.PublishCdnArtifactAsync(providerCache, artifact);
using var contentServer = new CultMeshContentServer(schemaServer, providerCache);
```

Snapshots may publish the manifest record, catalog, and body-control documents.
They must not publish `CultMeshCdnArtifactChunk` payload records for bulk
delivery. Consumers use `CultMeshSessionContentProvider` and
`CultMeshContentTransferService`; C# remains the reference implementation for
record keys, hashing, resumability, and atomic promotion.

## Entity Prefabs

Entity prefabs are distributed as Brokkr-authored snapshots, then deployed into
portable CultMesh prefab packages. Brokkr's Blender UI publishes
`brokkr.prefab.snapshot.v0` records for a collection-scoped prefab. A deployer
can consume that snapshot, publish meshes/textures/material payloads as CDN
artifacts, and write a `CultMeshEntityPrefabPackage` that references those
artifact manifests.

That keeps Blender as the authoring ground truth while avoiding Unity prefab
files as the interchange format. Unity, TypeScript, and later runtimes unpack
the same package graph into their native entity/component model.

## Client Responsibilities

Unity and TypeScript clients should implement the same rules:

- Fetch prefab packages by record key, tag, or version catalog.
- Fetch every referenced artifact manifest, then delegate payload transfer to
  the generic content-transfer owner.
- Verify signed manifests before trusting package membership.
- Verify chunk hashes and full artifact hashes before import.
- Cache artifacts locally by content hash.
- Lower package nodes into local runtime objects without writing back Unity-owned
  or Electron-owned prefab truth.
- Respect `MeshAssetSharingPolicy` for peer upload, status telemetry, and
  opt-out behavior.
