# rudp-multipeer-server vectors

Written only by the C# reference, `tests/GameCult.Networking.Tests/CultNetRudpMultiPeerVectorTests.cs`, with
`CULTNET_WRITE_VECTORS=1`. Every other run replays the scenario and compares it byte for byte; a changed vector is a reviewed
commit. Other runtimes' multi-peer listeners replay the same transcript and are held to the same answers
(`packages/cultnet-py/tests/test_rudp_multipeer_server.py`).

`transcript.txt` is one listener (`connection_id`, `initial_sequence`, `max_fragment_bytes` and `accept_payload` in its first line)
serving two loopback peers, X and Y. Each line is an action or what it caused: `in <peer> <hex>` is a datagram the peer sends the
listener, `send <peer> <channel> <hex>` is a payload the application sends that peer, and the `out <peer> <hex>` lines after it are
every datagram the listener put on the wire in answer, X's before Y's. Peers are named by label because ports are ephemeral. The
scenario covers a Connect, a repeat of an accepted Connect, data and acks, a fragmented send, an unreliable `latest` send, one peer's
Disconnect and the datagram from it that follows, and the other peer's reconnect from the same endpoint.
