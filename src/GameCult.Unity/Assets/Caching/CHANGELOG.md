# Changelog

All notable changes to this package are documented in this file.

## [2.0.0]

### Breaking

- Requires `org.gamecult.cultlib` 2.0.0. Unity Package Manager resolves one
  cultlib per project, so taking this release moves the project to cultlib 2.0.0.

### Added

- Studio grouping tree: types a document model groups appear under their group,
  with create in group.
- Dragging a source record onto a reference field sets the reference.

## [1.4.0]

### Added

- `[CultInspectorAssetGuid]` drawer: a `UnityEngine.Object` picker for a
  string member holding a stable Addressables-shaped asset key — the bare
  `.meta` GUID for a main asset, or `guid[subAssetName]` for a sub-asset.
- Sub-asset keys: picking a sub-asset (one sprite of a sheet, say) stores its
  name alongside the GUID and resolves it back through
  `AssetDatabase.LoadAllAssetRepresentationsAtPath`.
- Refusal of Unity's built-in resources: a picked object that is neither a
  main asset nor a sub-asset under `Assets/` or `Packages/` (Library's
  built-in default resources, e.g. the LegacyRuntime font or the Cube mesh)
  is refused rather than stored, since it has no Addressables entry.
- A stale-value warning when a stored key no longer resolves to an asset,
  with a Clear button beside it to blank the field without first picking a
  substitute asset. Drawing never rewrites a stored value on its own.
