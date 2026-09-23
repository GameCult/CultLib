// Regression test for fix 2: CultMeshQuicNativeRuntime.release() must await
// the pump loop's exit before calling cultmesh_quic_runtime_close, never
// after. Reverting that order (close before awaiting the pump) reliably
// crashes this test under a saturated libuv thread pool and MALLOC_PERTURB_,
// at every pool size tried by hand while verifying this batch; restored
// immediately afterward, never committed.
//
// UV_THREADPOOL_SIZE and MALLOC_PERTURB_ must be set before Node starts, so
// this spawns a child `node` process running test/support/release-uaf-probe.js
// with that environment, once per pool size, and asserts it exits 0. Kept to
// one scenario (see the probe's own header) so the whole test stays bounded
// to a few seconds.

import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { join } from "node:path";
import test from "node:test";

import { nativeBridgeAvailable } from "./support/native-bridge";

const PROBE_SCRIPT = join(__dirname, "..", "..", "test", "support", "release-uaf-probe.js");

test("fix 2 regression: release()'s ordering survives a saturated thread pool at pool sizes 1, 2 and 4", async (t) => {
  if (!nativeBridgeAvailable()) return void t.skip("no native bridge available");

  for (const poolSize of [1, 2, 4]) {
    const result = spawnSync(process.execPath, [PROBE_SCRIPT], {
      env: {
        ...process.env,
        UV_THREADPOOL_SIZE: String(poolSize),
        MALLOC_PERTURB_: "165",
      },
      encoding: "utf8",
      timeout: 60_000,
    });
    assert.equal(
      result.status,
      0,
      `pool size ${poolSize}: probe exited status=${result.status} signal=${result.signal}\n` +
        `stdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
    );
  }
});
