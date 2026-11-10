"""A server asked for any port finds a TCP+UDP pair; one asked for a port gets that port or an honest failure."""

from __future__ import annotations

import errno
import socket
import threading
import unittest
from unittest.mock import patch

from cultmesh_py import create_node
from cultmesh_py.server import CultMeshLocalServer


def server(**kwargs) -> CultMeshLocalServer:
    return CultMeshLocalServer(node=create_node(runtime_id="bind-test"), host="127.0.0.1", **kwargs)


class ServerBindTests(unittest.TestCase):
    def test_port_zero_retries_with_a_fresh_port_when_another_process_holds_the_udp_number(self) -> None:
        real_bind = socket.socket.bind
        refused: list[int] = []

        def bind(sock: socket.socket, address) -> None:
            # The first two UDP binds find the number taken, as a UDP listener of another process would.
            if sock.type == socket.SOCK_DGRAM and len(refused) < 2:
                refused.append(address[1])
                raise OSError(errno.EADDRINUSE, "Address already in use")
            real_bind(sock, address)

        with patch.object(socket.socket, "bind", bind):
            started = server().start()
        try:
            self.assertEqual(len(refused), 2)
            self.assertNotIn(started.port, refused[:1], "the retry used a fresh TCP port")
            probe = socket.create_connection(("127.0.0.1", started.port), timeout=2.0)
            probe.close()
        finally:
            started.stop()

    def test_an_explicit_port_whose_udp_number_is_taken_fails_and_leaves_nothing_running(self) -> None:
        holder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        holder.bind(("127.0.0.1", 0))
        port = holder.getsockname()[1]
        free = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            free.bind(("127.0.0.1", port))  # the TCP number is free: only the UDP one is taken
        finally:
            free.close()
        threads_before = threading.active_count()
        try:
            failing = server(port=port)
            with self.assertRaises(OSError) as raised:
                failing.start()
            self.assertEqual(raised.exception.errno, errno.EADDRINUSE)
            self.assertIsNone(failing._socket)
            self.assertEqual(threading.active_count(), threads_before, "no accept thread was left running")
            # The TCP number was released, not left listening.
            free = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            try:
                free.bind(("127.0.0.1", port))
            finally:
                free.close()
        finally:
            holder.close()


if __name__ == "__main__":
    unittest.main()
