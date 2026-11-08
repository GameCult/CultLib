"""Heimdall's invariant: no client treats a reliable-transport ack as write admission. A put completes only on
its result frame (write_not_authorized, or the success reply).

The Python write path is a subscription over RUDP: `send` queues the put, `read_next` returns the result frame
and raises the peer's refusal. The server here refuses the put; its reliable ack of the put arrives (the test
waits for it) and the client still reports the refusal, never success. The transport's `receive` yields data
frames only, so the order of the ack against the refusal cannot change what `read_next` reports.
"""

from __future__ import annotations

import unittest

from cultcache_py import define_database_entry_type
from cultmesh_py import CultMesh, create_node
from cultmesh_py.server import CultMeshLocalServer
from cultnet_py import CultNetDatabaseSubscription, CultNetPeerError, create_rudp_schema_transport

from test_remote_write_authority import KEY_A, NOTE, SCHEMA_ID, put_of


class ClientWriteCompletionTests(unittest.TestCase):
    def test_a_put_the_server_refused_is_reported_refused_after_its_ack_arrived(self) -> None:
        node = create_node(runtime_id="completion-server")
        node.database.register_document(NOTE)
        server = CultMeshLocalServer(node=node, port=0, write_authority=None)
        server.start()
        try:
            subscription = CultNetDatabaseSubscription(
                host="127.0.0.1",
                port=server.port,
                timeout_seconds=4.0,
                subscription_id="completion",
                schema_ids=[SCHEMA_ID],
                include_snapshot=False,
                create_transport=lambda: create_rudp_schema_transport(
                    host="127.0.0.1", port=server.port, connection_id=server.rudp_connection_id, timeout_seconds=4.0
                ),
            )
            with subscription as sub:
                sub.send(put_of(KEY_A, "peer"))
                sub._transport.flush_reliable(2.0)  # the put's reliable ack has arrived
                with self.assertRaises(CultNetPeerError) as raised:
                    sub.read_next()
        finally:
            server.stop()
        self.assertEqual(raised.exception.response["code"], "write_not_authorized")
        self.assertIsNone(server.node.cache.get(NOTE, KEY_A))


if __name__ == "__main__":
    unittest.main()
