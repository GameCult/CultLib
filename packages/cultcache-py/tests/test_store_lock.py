from __future__ import annotations

import os
import select
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

from cultcache_py import SingleFileMessagePackBackingStore, stores
from cultcache_py.stores import StoreLinkedError, store_write_lock
from cultcache_py.backing_store import CultCacheEnvelope

REPO = Path(__file__).resolve().parents[3]
TS_STORE_FILE = REPO / "packages" / "cultcache-ts" / "dist" / "store-file.js"
HOLD_SECONDS = 1.5
HELD_WAIT_SECONDS = 3

# A holder that takes the lock the way the Rust runtime does (an exclusive file lock), not through this package.
PY_HOLDER = """
import fcntl, os, sys, time
fd = os.open(sys.argv[1] + '.lock', os.O_RDWR | os.O_CREAT, 0o666)
fcntl.flock(fd, fcntl.LOCK_EX)
print('HELD', flush=True)
time.sleep(float(sys.argv[2]))
"""
# A holder that is the TypeScript runtime's own withStoreWriteLock, built from packages/cultcache-ts.
TS_HOLDER = """
const { withStoreWriteLock } = require(process.argv[1]);
withStoreWriteLock(process.argv[2], async () => {
  console.log('HELD');
  await new Promise((r) => setTimeout(r, Number(process.argv[3]) * 1000));
});
"""


def envelope(key: str) -> CultCacheEnvelope:
    return CultCacheEnvelope(key=key, type="t", payload=b"\x01", stored_at="2026-11-06T00:00:00Z")


def start_holder(argv: list[str]) -> subprocess.Popen[str]:
    holder = subprocess.Popen(argv, stdout=subprocess.PIPE, text=True)
    assert holder.stdout is not None
    ready, _, _ = select.select([holder.stdout], [], [], HELD_WAIT_SECONDS)  # a holder blocked on a leaked lock never prints
    if not ready:
        holder.kill()
        holder.wait()
        holder.stdout.close()
        raise AssertionError(f"the holder never took {argv[-2]}.lock within {HELD_WAIT_SECONDS} s: an earlier writer leaked the lock")
    assert holder.stdout.readline().strip() == "HELD"
    holder.held_at = time.monotonic()  # type: ignore[attr-defined]
    return holder


@unittest.skipIf(sys.platform == "win32", "the foreign holders here use flock; Windows is covered by the TS holder")
class StoreLockTests(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.path = self.dir / "state.cc"

    def assert_write_waits_for(self, holder: subprocess.Popen[str]) -> None:
        self.addCleanup(holder.wait)
        self.addCleanup(holder.stdout.close)  # type: ignore[union-attr]
        refused = SingleFileMessagePackBackingStore(self.path, lock_timeout=0.2)
        with self.assertRaises(TimeoutError):
            refused.push(envelope("blocked"))
        self.assertFalse(self.path.exists(), "a write that could not take the lock must not touch the store")

        SingleFileMessagePackBackingStore(self.path, lock_timeout=10).push(envelope("after"))
        self.assertGreaterEqual(time.monotonic() - holder.held_at, HOLD_SECONDS - 0.1, "the write finished while the holder still held the lock")  # type: ignore[attr-defined]
        self.assertEqual(["after"], [e.key for e in SingleFileMessagePackBackingStore(self.path).pull_all()])

    def test_write_waits_while_a_foreign_process_holds_the_lock(self) -> None:
        self.assert_write_waits_for(start_holder([sys.executable, "-c", PY_HOLDER, str(self.path), str(HOLD_SECONDS)]))

    @unittest.skipUnless(shutil.which("node") and TS_STORE_FILE.exists(), "needs node and a built packages/cultcache-ts (npm run build)")
    def test_write_waits_while_the_typescript_runtime_holds_the_lock(self) -> None:
        holder = start_holder(["node", "-e", TS_HOLDER, str(TS_STORE_FILE), str(self.path), str(HOLD_SECONDS)])
        self.assert_write_waits_for(holder)

    def test_a_link_put_while_a_writer_waits_on_the_lock_is_refused_once_it_holds_it(self) -> None:
        target = self.dir / "volume" / "store.cc"
        target.parent.mkdir()
        SingleFileMessagePackBackingStore(target).push(envelope("a"))
        before = target.read_bytes()
        writes = {
            "push": lambda store: store.push(envelope("b")),
            "push_all": lambda store: store.push_all([envelope("b")]),
            "delete": lambda store: store.delete("t", "a"),
        }
        missing = self.dir / "volume" / "missing.cc"
        for (name, write), link_to in [(item, to) for item in writes.items() for to in (target, missing)]:
            with self.subTest(name, dangling=link_to == missing):
                if os.path.lexists(self.path):
                    os.unlink(self.path)
                holder = start_holder([sys.executable, "-c", PY_HOLDER, str(self.path), str(HOLD_SECONDS)])
                self.addCleanup(holder.wait)
                self.addCleanup(holder.stdout.close)  # type: ignore[union-attr]
                outcome: list[BaseException | None] = []

                def run() -> None:
                    try:
                        write(SingleFileMessagePackBackingStore(self.path, lock_timeout=10))
                        outcome.append(None)
                    except BaseException as exc:  # noqa: BLE001 - the thread hands the outcome to the test
                        outcome.append(exc)

                writer = threading.Thread(target=run)
                writer.start()
                time.sleep(0.3)
                os.symlink(link_to, self.path)  # after the writer's pre-lock look, while it waits
                writer.join()
                holder.wait()
                self.assertEqual(1, len(outcome))
                self.assertIsInstance(outcome[0], StoreLinkedError)
                self.assertTrue(os.path.islink(self.path), "the link was replaced")
                self.assertEqual(before, target.read_bytes())
                self.assertFalse(missing.exists(), "the dangling target was created")
                os.unlink(self.path)  # a link is refused before the lock, so remove it to probe the writer's own lock file
                with store_write_lock(self.path, timeout=0.2):  # the lock is free
                    pass

    def test_the_lock_is_released_after_each_write(self) -> None:
        store = SingleFileMessagePackBackingStore(self.path, lock_timeout=0.2)
        for key in "abc":
            store.push(envelope(key))
        self.assertEqual(["a", "b", "c"], sorted(e.key for e in store.pull_all()))

    def test_concurrent_processes_lose_no_writes(self) -> None:
        script = (
            "import sys\n"
            "from cultcache_py import SingleFileMessagePackBackingStore as S\n"
            "from cultcache_py.backing_store import CultCacheEnvelope as E\n"
            "s = S(sys.argv[1])\n"
            "for i in range(25):\n"
            "    s.push(E(key=f'{sys.argv[2]}{i}', type='t', payload=b'\\x01', stored_at='x'))\n"
        )
        env = {**os.environ, "PYTHONPATH": str(Path(stores.__file__).parents[1])}
        procs = [subprocess.Popen([sys.executable, "-c", script, str(self.path), tag], env=env) for tag in "pqr"]
        for proc in procs:
            self.assertEqual(0, proc.wait())
        self.assertEqual(75, len(SingleFileMessagePackBackingStore(self.path).pull_all()))


class StagedWriteTests(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir, ignore_errors=True)
        self.path = self.dir / "state.cc"

    def test_the_staged_file_is_fsynced_before_the_replace(self) -> None:
        order: list[str] = []
        real_fsync, real_replace = os.fsync, os.replace
        with mock.patch("os.fsync", side_effect=lambda fd: (order.append("fsync"), real_fsync(fd))[1]), mock.patch(
            "os.replace", side_effect=lambda a, b: (order.append("replace"), real_replace(a, b))[1]
        ):
            SingleFileMessagePackBackingStore(self.path).push(envelope("a"))
        self.assertEqual("fsync", order[0])
        self.assertLess(order.index("fsync"), order.index("replace"))

    def test_the_staged_name_is_unique_per_write(self) -> None:
        names: list[str] = []
        real_replace = os.replace
        with mock.patch("os.replace", side_effect=lambda a, b: (names.append(os.fspath(a)), real_replace(a, b))[1]):
            store = SingleFileMessagePackBackingStore(self.path)
            store.push(envelope("a"))
            store.push(envelope("b"))
        self.assertEqual(2, len(set(names)))
        self.assertNotIn(os.fspath(self.path) + ".tmp", names)

    def test_a_failed_replace_leaves_the_store_and_no_staged_file(self) -> None:
        store = SingleFileMessagePackBackingStore(self.path)
        store.push(envelope("a"))
        before = self.path.read_bytes()
        with mock.patch("os.replace", side_effect=OSError("boom")), self.assertRaises(OSError):
            store.push(envelope("b"))
        self.assertEqual(before, self.path.read_bytes())
        self.assertEqual([], [p.name for p in self.dir.iterdir() if ".tmp" in p.name])


if __name__ == "__main__":
    unittest.main()
