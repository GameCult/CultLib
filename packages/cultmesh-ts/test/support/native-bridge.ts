// Shared by every QUIC test file that skips instead of failing when no native
// bridge is built: true only when both the bridge and its `msquic` dependency
// are present under `CULTMESH_QUIC_NATIVE_DIR`.

import { readFileSync } from "node:fs";
import { join } from "node:path";

export function nativeBridgeAvailable(): boolean {
  const dir = process.env.CULTMESH_QUIC_NATIVE_DIR;
  if (!dir) return false;
  try {
    const bridge = process.platform === "win32" ? "gamecult_mesh_quic_native.dll" : "libgamecult_mesh_quic_native.so";
    const dependency = process.platform === "win32" ? "msquic.dll" : "libmsquic.so.2";
    readFileSync(join(dir, bridge));
    readFileSync(join(dir, dependency));
    return true;
  } catch {
    return false;
  }
}
