"use strict";
// Fix 2 regression probe: CultMeshQuicNativeRuntime.release() must await the
// pump loop's exit BEFORE calling cultmesh_quic_runtime_close, never after.
// The bridge header forbids any host call beginning once
// cultmesh_quic_runtime_close has started; the old order let a queued,
// not-yet-started nextEvent call begin after close and run against freed
// memory.
//
// Plain JS, not TypeScript: this file is spawned directly as a child `node`
// process (see ../release-uaf.test.ts) so that UV_THREADPOOL_SIZE and
// MALLOC_PERTURB_ are in effect before Node's own thread pool and allocator
// initialize, which setting them from within an already-running process
// cannot achieve. It requires the compiled dist/ output directly, exactly
// like the package's own compiled tests do.
//
// Kept to one scenario (bare open/release cycles, including a fresh open
// while the previous runtime is still closing) so the probe stays bounded to
// a few seconds; it is the scenario that exercises release()'s ordering
// directly, without a listener or a peer.
const path = require("node:path");
const { pbkdf2 } = require("node:crypto");

const root = path.join(__dirname, "..", "..");
const native = require(path.join(root, "dist", "realtime-quic-native.js"));
const RT = native.CultMeshQuicNativeRuntime;

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

let saturating = false;
function saturate(n) {
  saturating = true;
  const one = () => {
    if (!saturating) return;
    pbkdf2("x", "y", 20000, 32, "sha256", () => one());
  };
  for (let i = 0; i < n; i += 1) one();
}
function stopSaturate() {
  saturating = false;
}

const CYCLES = Number(process.env.UAF_PROBE_CYCLES || 20);

(async () => {
  const pool = Number(process.env.UV_THREADPOOL_SIZE || 4);
  saturate(pool * 4);
  try {
    for (let i = 0; i < CYCLES; i += 1) {
      const rt = await RT.open();
      if (i % 3 === 0) await sleep(Math.random() * 5);
      if (i % 2 === 0) {
        // Release the old runtime and open a fresh one without waiting for
        // the release to finish: the fresh open can land while the old
        // runtime is still closing, which is exactly the overlap fix 2's
        // ordering protects.
        const releasing = rt.release();
        const rt2 = await RT.open();
        await sleep(Math.random() * 3);
        await releasing;
        await rt2.release();
      } else {
        await rt.release();
      }
    }
  } finally {
    stopSaturate();
  }
  if (RT.refCount !== 0) {
    console.error(`FAIL: refCount=${RT.refCount} after ${CYCLES} open/release cycles, want 0`);
    process.exitCode = 1;
    return;
  }
  console.log(`OK: ${CYCLES} open/release cycles at pool=${pool}, refCount=0`);
})().catch((error) => {
  console.error("FAIL: probe threw", error);
  process.exitCode = 1;
});
