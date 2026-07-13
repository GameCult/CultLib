export const CULTMATH_PLANETARY_TILE_VERSION = 1;

export interface PlanetaryTileHeader {
  fieldVersion: bigint;
  projectionVersion: number;
  layerId: number;
  projectionKind: number;
  centerLongitude: number;
  centerLatitude: number;
  projectionScale: number;
  level: number;
  x: number;
  y: number;
  interiorSize: number;
  borderSize: number;
  footprintMeters: number;
  maximumUnresolvedHeight: number;
}

export interface PlanetaryTileSample {
  direction: readonly [number, number, number];
  radius: number;
  radialDisplacement: number;
  tangentGradient: readonly [number, number, number];
  surfaceNormal: readonly [number, number, number];
  slope: number;
  ridge: number;
  gully: number;
  finestResolvedWavelength: number;
  unresolvedHeightBound: number;
}

export interface PlanetarySurfaceTile {
  header: PlanetaryTileHeader;
  storageSize: number;
  samples: readonly (PlanetaryTileSample | null)[];
}

export function decodePlanetarySurfaceTile(bytes: ArrayBuffer): PlanetarySurfaceTile {
  const view = new DataView(bytes);
  let offset = 0;
  const u8 = () => view.getUint8(offset++);
  const u32 = () => { const value = view.getUint32(offset, true); offset += 4; return value; };
  const i32 = () => { const value = view.getInt32(offset, true); offset += 4; return value; };
  const f32 = () => { const value = view.getFloat32(offset, true); offset += 4; return value; };
  const f64 = () => { const value = view.getFloat64(offset, true); offset += 8; return value; };
  const u64 = () => { const value = view.getBigUint64(offset, true); offset += 8; return value; };
  const vector3 = (): [number, number, number] => [f32(), f32(), f32()];
  const requireRemaining = (count: number) => {
    if (offset + count > view.byteLength) throw new Error("Truncated CultMath planetary tile");
  };

  requireRemaining(8);
  if (String.fromCharCode(u8(), u8(), u8(), u8()) !== "CMPT") throw new Error("Not a CultMath planetary tile");
  const version = u32();
  if (version !== CULTMATH_PLANETARY_TILE_VERSION) throw new Error(`Unsupported CultMath planetary tile version ${version}`);
  requireRemaining(76);
  const header: PlanetaryTileHeader = {
    fieldVersion: u64(),
    projectionVersion: u32(),
    layerId: u32(),
    projectionKind: i32(),
    centerLongitude: f64(),
    centerLatitude: f64(),
    projectionScale: f64(),
    level: i32(), x: i32(), y: i32(), interiorSize: i32(), borderSize: i32(),
    footprintMeters: f32(), maximumUnresolvedHeight: f32(),
  };
  const count = i32();
  const storageSize = header.interiorSize + header.borderSize * 2;
  if (count !== storageSize * storageSize) throw new Error("CultMath planetary tile sample count does not match layout");
  const samples: (PlanetaryTileSample | null)[] = new Array(count);
  for (let index = 0; index < count; index++) {
    requireRemaining(1);
    if (u8() === 0) { samples[index] = null; continue; }
    requireRemaining(64);
    samples[index] = {
      direction: vector3(),
      radius: f32(),
      radialDisplacement: f32(),
      tangentGradient: vector3(),
      surfaceNormal: vector3(),
      slope: f32(), ridge: f32(), gully: f32(),
      finestResolvedWavelength: f32(), unresolvedHeightBound: f32(),
    };
  }
  if (offset !== view.byteLength) throw new Error("CultMath planetary tile has trailing bytes");
  return { header, storageSize, samples };
}
