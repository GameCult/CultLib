from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from cultcache_py import SingleFileMessagePackBackingStore, stores
from cultcache_py.backing_store import CultCacheEnvelope
from cultcache_py.stores import MOVEFILE_REPLACE_WRITE_THROUGH, ReplaceKernel, replace_store_windows


class FakeKernel(ReplaceKernel):
    def __init__(self, posix: list[int], move_ex: list[int]) -> None:
        self.posix, self.move_ex = list(posix), list(move_ex)
        self.log: list[str] = []
        self.sleeps: list[float] = []

    def rename_posix(self, staged, destination):
        self.log.append("posix")
        return self.posix.pop(0) if self.posix else 0

    def move_file_ex(self, staged, destination, flags):
        self.log.append(f"moveex:{flags}")
        return self.move_ex.pop(0) if self.move_ex else 0

    def sleep(self, seconds):
        self.sleeps.append(seconds)


def run(posix, move_ex):
    kernel = FakeKernel(posix, move_ex)
    return kernel, lambda: replace_store_windows("a", "b", kernel)


class WindowsReplaceTable(unittest.TestCase):
    def test_falls_back_to_write_through_move_where_posix_rename_is_unsupported(self):
        self.assertEqual(MOVEFILE_REPLACE_WRITE_THROUGH, 0x9)
        for unsupported in (1, 50, 87):
            kernel, go = run([unsupported], [0])
            go()
            self.assertEqual(kernel.log, ["posix", "moveex:9"])

    def test_once_on_move_the_rest_of_the_call_stays_there(self):
        kernel, go = run([87], [32, 0])
        go()
        self.assertEqual(kernel.log, ["posix", "moveex:9", "moveex:9"])

    def test_transient_codes_retry_the_same_primitive(self):
        for transient in (5, 32, 33):
            kernel, go = run([transient, transient, 0], [])
            go()
            self.assertEqual(kernel.log, ["posix", "posix", "posix"])
            self.assertEqual(kernel.sleeps, [0.02, 0.02])

    def test_a_working_posix_rename_is_the_whole_replace(self):
        kernel, go = run([0], [])
        go()
        self.assertEqual(kernel.log, ["posix"])
        self.assertEqual(kernel.sleeps, [])

    def test_unsupported_after_the_fallback_is_an_error_not_another_fallback(self):
        kernel, go = run([87], [32, 87])
        with self.assertRaisesRegex(OSError, "Win32 error 87") as caught:
            go()
        self.assertEqual(caught.exception.winerror, 87)
        self.assertEqual(kernel.log, ["posix", "moveex:9", "moveex:9"])
        for unsupported in (1, 50, 87):
            kernel, go = run([87], [unsupported])
            with self.assertRaisesRegex(OSError, f"Win32 error {unsupported}"):
                go()
            self.assertEqual(kernel.log, ["posix", "moveex:9"])

    def test_gives_up_after_25_attempts_naming_the_last_code(self):
        kernel, go = run([32] * 30, [])
        with self.assertRaisesRegex(OSError, "after 25 attempts: last Win32 error 32\\."):
            go()
        self.assertEqual(len(kernel.log), 25)

    def test_other_codes_fail_at_once(self):
        kernel, go = run([2], [])
        with self.assertRaisesRegex(OSError, "Win32 error 2"):
            go()
        self.assertEqual(len(kernel.log), 1)
        kernel, go = run([87], [3])
        with self.assertRaisesRegex(OSError, "Win32 error 3"):
            go()


class StoreWritesRouteThroughTheReplace(unittest.TestCase):
    def test_both_stores_replace_through_replace_store(self):
        seen: list[tuple[Path, Path]] = []
        real = stores._replace_store

        def spy(staged, destination):
            seen.append((Path(staged), Path(destination)))
            real(staged, destination)

        with tempfile.TemporaryDirectory() as directory, mock.patch.object(stores, "_replace_store", spy):
            envelope = CultCacheEnvelope(key="k", type="t", payload=b"\x01", stored_at="2026-11-06T00:00:00Z")
            binary = Path(directory) / "s.cc"
            SingleFileMessagePackBackingStore(binary).push_all([envelope])
            lines = Path(directory) / "s.jsonl"
            stores.JsonLinesBackingStore(lines).push_all([envelope])
            self.assertEqual([destination for _, destination in seen], [binary, lines])
            self.assertTrue(binary.read_bytes() and lines.read_text())

    @unittest.skipUnless(sys.platform == "win32", "needs a real Windows")
    def test_real_kernel_replaces_through_both_primitives(self):
        kernel = stores.WindowsReplaceKernel()
        with tempfile.TemporaryDirectory() as directory:
            for name, replace in (("posix", kernel.rename_posix), ("moveex", lambda s, d: kernel.move_file_ex(s, d, MOVEFILE_REPLACE_WRITE_THROUGH))):
                staged, store = Path(directory, name + ".tmp"), Path(directory, name)
                store.write_bytes(b"old")
                staged.write_bytes(b"new")
                self.assertEqual(replace(str(staged), str(store)), 0)
                self.assertEqual(store.read_bytes(), b"new")
            self.assertEqual(kernel.rename_posix(str(Path(directory, "missing")), str(Path(directory, "x"))), 2)


if __name__ == "__main__":
    unittest.main()
