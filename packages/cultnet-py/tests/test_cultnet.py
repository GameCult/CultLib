from __future__ import annotations

import json
import socket
import threading
import time
import unittest
import hashlib
import hmac
import io
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Callable
from uuid import uuid4
from cultcache_py import CultCache, define_database_entry_type
from cultnet_py.benchmark import run_benchmark
from cultnet_py.compare_csharp import (
    DEFAULT_PARITY_THRESHOLD,
    DEFAULT_SAMPLE_COUNT,
    _compare_common_metrics,
    _median_result,
    _parity_status,
)
from cultnet_py.interop_peer import append_shard_log_put, build_state, raw_snapshot_response
from cultnet_py import (
    compute_simulation_claim_hash,
    CultNetDatabaseChange,
    CultNetRawClient,
    CultNetRawDocumentRecord,
    CultNetRawSnapshotResponse,
    CultNetSchemaCatalog,
    CultNetSchemaDescriptor,
    CultNetShardCatalog,
    CultNetShardDescriptor,
    CultNetShardLogEntry,
    CultNetShardLogResponse,
    CultNetPeerError,
    CultNetClientSecurityOptions,
    CultNetSecret,
    CultNetServerSecurityOptions,
    CultNetSimulationConsensusOptions,
    CultNetSimulationConsensusCandidate,
    CultNetSimulationObservation,
    CultNetRudpPacket,
    CultNetRudpPacketType,
    CultNetRudpSendOptions,
    CultNetRudpSession,
    CultNetRudpSessionOptions,
    CultNetRudpSocketMode,
    CultNetRudpReconnectLoop,
    CultNetRudpSocketTransportConnection,
    CultNetRudpSocketTransportOptions,
    TcpFramedTransportConnection,
    CultNetWitnessArtifactBundle,
    apply_raw_document_record,
    apply_raw_snapshot,
    apply_shard_log_response,
    create_rudp_transport_profile,
    create_tcp_framed_transport_profile,
    create_reconnect_policy,
    CultNetReconnectController,
    database_subscribe,
    database_unsubscribe,
    decode_frame,
    decode_rudp_packet,
    decode_witness_artifact_bundle_payload,
    document_delete,
    document_put_raw,
    encode_frame,
    encode_rudp_packet,
    encode_witness_artifact_bundle_payload,
    hello,
    login,
    login_success,
    parse_message,
    register,
    schema_catalog_request,
    schema_document_map,
    shard_catalog_request,
    shard_log_request,
    simulation_observation,
    snapshot_request,
    verify_session,
    wire_message_schema_catalog,
    wire_message_schema_descriptors,
    witness_artifact_bundle,
    compute_reconnect_delay_ms,
)


def bind_udp_socket() -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.settimeout(0.02)
    return sock


def pump_rudp_handshake(
    client: CultNetRudpSocketTransportConnection,
    server: CultNetRudpSocketTransportConnection,
) -> None:
    for _ in range(20):
        server.receive_once()
        client.receive_once()
        server.receive_once()
        if client.connected and server.connected:
            return
        time.sleep(0.005)
    raise AssertionError("RUDP socket handshake did not complete")


def receive_rudp_frame(transport: CultNetRudpSocketTransportConnection):
    for _ in range(20):
        frame = transport.receive_once()
        if frame is not None:
            return frame
        time.sleep(0.005)
    raise AssertionError("RUDP socket frame was not delivered")


@dataclass
class Item:
    name: str
    category: str
    value: int


class CultNetTests(unittest.TestCase):
    def test_benchmark_reports_core_hot_path_metrics(self) -> None:
        result = run_benchmark(8)
        metric_names = {metric["name"] for metric in result["metrics"]}
        self.assertEqual(result["records"], 8)
        self.assertEqual(result["wireMessageCount"], 8)
        self.assertEqual(metric_names, {
            "database_entry_encode",
            "database_entry_decode",
            "cultnet_frame_parse",
            "raw_snapshot_apply",
            "cache_upsert",
            "cache_get",
        })
        self.assertTrue(all(metric["opsPerSecond"] > 0 for metric in result["metrics"]))

    def test_compare_csharp_median_result_summarizes_samples(self) -> None:
        self.assertEqual(DEFAULT_SAMPLE_COUNT, 3)
        self.assertEqual(DEFAULT_PARITY_THRESHOLD, 0.90)
        samples = [
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 30.0, "opsPerSecond": 100.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 60.0, "opsPerSecond": 50.0},
                ],
            },
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 10.0, "opsPerSecond": 300.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 20.0, "opsPerSecond": 150.0},
                ],
            },
            {
                "runtime": "python",
                "records": 3,
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 20.0, "opsPerSecond": 200.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 40.0, "opsPerSecond": 75.0},
                ],
            },
        ]

        summarized = _median_result(samples)

        self.assertEqual(summarized["sampleCount"], 3)
        self.assertEqual(summarized["metrics"][0]["name"], "cache_get")
        self.assertEqual(summarized["metrics"][0]["elapsedMs"], 20.0)
        self.assertEqual(summarized["metrics"][0]["opsPerSecond"], 200.0)
        self.assertEqual(summarized["metrics"][1]["opsPerSecond"], 75.0)

    def test_compare_csharp_marks_threshold_status_per_metric(self) -> None:
        python_result = {
            "metrics": [
                {"name": "cache_get", "operations": 3, "elapsedMs": 2.0, "opsPerSecond": 50.0},
                {"name": "cache_upsert", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 95.0},
            ],
        }
        csharp_result = {
            "metrics": [
                {"name": "cache_get", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 100.0},
                {"name": "cache_upsert", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 100.0},
            ],
        }

        comparison = _compare_common_metrics(python_result, csharp_result, parity_threshold=0.90)
        by_name = {item["name"]: item for item in comparison}

        self.assertFalse(by_name["cache_get"]["meetsParityThreshold"])
        self.assertTrue(by_name["cache_upsert"]["meetsParityThreshold"])
        self.assertEqual(_parity_status(comparison, "ok"), "below-threshold")
        self.assertEqual(_parity_status([by_name["cache_upsert"]], "ok"), "meets-threshold")
        self.assertEqual(_parity_status(comparison, "failed"), "unknown")

    def test_compare_csharp_marks_meets_threshold_when_all_common_metrics_pass(self) -> None:
        comparison = _compare_common_metrics(
            {
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 95.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 120.0},
                ],
            },
            {
                "metrics": [
                    {"name": "cache_get", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 100.0},
                    {"name": "cache_upsert", "operations": 3, "elapsedMs": 1.0, "opsPerSecond": 100.0},
                ],
            },
            parity_threshold=0.90,
        )

        self.assertEqual(_parity_status(comparison, "ok"), "meets-threshold")

    def test_cultnet_security_options_match_cultlib_defaults_and_environment_rules(self) -> None:
        client = CultNetClientSecurityOptions.development()
        server = CultNetServerSecurityOptions.development()

        self.assertEqual(client.connection_key, "gamecult-dev-connection-key")
        self.assertEqual(server.connection_key, "gamecult-dev-connection-key")
        self.assertTrue(server.is_development)
        self.assertEqual(server.to_client_options().connection_key, client.connection_key)
        self.assertEqual(client.encryption_key(), hashlib.sha256(b"gamecult-dev-connection-key").digest())
        self.assertEqual(server.session_signing_key(), hashlib.sha256(b"gamecult-dev-session-signing-secret").digest())
        self.assertEqual(
            CultNetServerSecurityOptions.from_environment({}, allow_development_defaults=True),
            server,
        )
        with self.assertRaisesRegex(ValueError, "not configured"):
            CultNetServerSecurityOptions.from_environment({})
        with self.assertRaisesRegex(ValueError, "GAMECULT_SESSION_SIGNING_SECRET"):
            CultNetServerSecurityOptions.from_environment({"GAMECULT_CONNECTION_KEY": "key"})
        configured = CultNetServerSecurityOptions.from_environment({
            "GAMECULT_CONNECTION_KEY": "prod-key",
            "GAMECULT_SESSION_SIGNING_SECRET": "prod-secret",
        })
        self.assertEqual(configured.connection_key, "prod-key")
        self.assertFalse(configured.is_development)

    def test_cultnet_secret_helpers_validate_csharp_versioned_session_tokens(self) -> None:
        security = CultNetServerSecurityOptions("connection-key", "session-secret")
        user_id = uuid4()
        expires = datetime(2035, 1, 2, 3, 4, 5, tzinfo=UTC)

        token = CultNetSecret.create_session_token(user_id, expires, security, session_version=7)
        payload_b64, signature_b64 = token.split(".")
        self.assertEqual(
            CultNetSecret.from_base64url(payload_b64).decode("utf-8"),
            f"{user_id.hex}|{int(expires.timestamp())}|7",
        )
        self.assertEqual(
            CultNetSecret.from_base64url(signature_b64),
            hmac.new(
                hashlib.sha256(b"session-secret").digest(),
                CultNetSecret.from_base64url(payload_b64),
                hashlib.sha256,
            ).digest(),
        )

        validated = CultNetSecret.try_validate_session_token(
            token,
            security,
            now_utc=datetime(2035, 1, 2, 3, 4, 4, tzinfo=UTC),
        )
        self.assertIsNotNone(validated)
        assert validated is not None
        self.assertEqual(validated.user_id, user_id)
        self.assertEqual(validated.expires_at_utc, expires)
        self.assertEqual(validated.session_version, 7)

        tampered_payload = CultNetSecret.to_base64url(CultNetSecret.from_base64url(payload_b64) + b"tamper")
        tampered = f"{tampered_payload}.{signature_b64}"
        self.assertIsNone(CultNetSecret.try_validate_session_token(tampered, security))
        self.assertIsNone(CultNetSecret.try_validate_session_token(token, security, now_utc=expires))

    def test_cultnet_secret_helpers_validate_legacy_two_field_session_tokens(self) -> None:
        security = CultNetServerSecurityOptions.development()
        user_id = uuid4()
        expires_at_seconds = int(datetime(2036, 1, 1, tzinfo=UTC).timestamp())
        payload = f"{user_id.hex}|{expires_at_seconds}".encode("utf-8")
        signature = hmac.new(security.session_signing_key(), payload, hashlib.sha256).digest()
        token = f"{CultNetSecret.to_base64url(payload)}.{CultNetSecret.to_base64url(signature)}"

        validated = CultNetSecret.try_validate_session_token(
            token,
            security,
            now_utc=datetime(2035, 12, 31, tzinfo=UTC),
        )

        self.assertIsNotNone(validated)
        assert validated is not None
        self.assertEqual(validated.user_id, user_id)
        self.assertEqual(validated.session_version, 0)

    def test_cultnet_secret_encryption_helpers_are_optional_without_crypto_dependency(self) -> None:
        nonce = b"123456789012"
        security = CultNetServerSecurityOptions.development()
        try:
            encrypted = CultNetSecret.encrypt_string("hello", nonce, security.to_client_options())
        except ImportError as exc:
            self.assertIn("cryptography", str(exc))
            return
        self.assertEqual(CultNetSecret.decrypt_string(encrypted, nonce, security), "hello")

    def test_cultnet_schema_message_frame_round_trip(self) -> None:
        message = hello(
            runtime_id="python-test",
            supported_schema_versions=["cultnet.hello.v0"],
            transport_profiles=[
                {
                    "schemaVersion": "cultnet.transport_profile.v0",
                    "runtimeId": "python-test",
                    "transports": [
                        {
                            "transportId": "test",
                            "protocol": "tcp_framed",
                            "channels": [{"channelId": "schema", "delivery": "reliable", "ordering": "ordered"}],
                        }
                    ],
                }
            ],
        )
        parsed = parse_message(decode_frame(encode_frame(message.to_bytes())))
        self.assertEqual(parsed.schema_version, "cultnet.hello.v0")
        self.assertEqual(parsed.body["runtimeId"], "python-test")
        self.assertEqual(parsed.body["transportProfiles"][0]["transports"][0]["protocol"], "tcp_framed")

    def test_cultnet_tcp_framed_transport_carries_schema_payloads_with_stats(self) -> None:
        payload = b"cultnet-payload"
        output = io.BytesIO()
        sender = TcpFramedTransportConnection(
            output,
            profile=create_tcp_framed_transport_profile("sender", transport_id="test-tcp"),
        )
        sender.send("schema", payload)
        self.assertEqual(sender.stats.frames_sent, 1)
        self.assertEqual(sender.stats.bytes_sent, len(payload) + 4)
        with self.assertRaisesRegex(ValueError, "only supports the schema channel"):
            sender.send("unreliable", b"")

        receiver = TcpFramedTransportConnection(
            io.BytesIO(output.getvalue()),
            profile=create_tcp_framed_transport_profile("receiver", transport_id="test-tcp"),
        )
        frame = receiver.receive()
        self.assertEqual(frame.channel_id, "schema")
        self.assertEqual(frame.payload, payload)
        self.assertEqual(receiver.stats.frames_received, 1)
        self.assertEqual(receiver.stats.bytes_received, len(payload) + 4)
        self.assertEqual(receiver.profile["transports"][0]["protocol"], "tcp_framed")

    def test_cultnet_raw_client_uses_schema_transport_factory(self) -> None:
        import msgpack  # type: ignore

        sent_messages: list[dict[str, object]] = []
        test_case = self

        class FakeSchemaTransport:
            profile = create_tcp_framed_transport_profile("fake-client")

            def __enter__(self) -> "FakeSchemaTransport":
                return self

            def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
                self.close()

            def send(self, channel_id: str, payload: bytes) -> None:
                test_case.assertEqual(channel_id, "schema")
                sent_messages.append(msgpack.unpackb(payload, raw=False))

            def receive(self) -> object:
                response = {
                    "schemaVersion": "cultnet.schema_catalog_response.v0",
                    "messageId": "transport-factory",
                    "schemas": [],
                }
                return type("Frame", (), {"channel_id": "schema", "payload": msgpack.packb(response, use_bin_type=True)})()

            def close(self) -> None:
                pass

        client = CultNetRawClient(
            "unused.example.test",
            1,
            create_transport=FakeSchemaTransport,
        )

        response = client.fetch_schema_catalog(message_id="transport-factory")

        self.assertEqual(response["schemaVersion"], "cultnet.schema_catalog_response.v0")
        self.assertEqual(sent_messages[0]["schemaVersion"], "cultnet.schema_catalog_request.v0")
        self.assertEqual(sent_messages[0]["messageId"], "transport-factory")

    def test_cultnet_rudp_packet_codec_uses_deterministic_reliable_ordered_fixture(self) -> None:
        encoded = encode_rudp_packet(
            CultNetRudpPacket(
                packet_type=CultNetRudpPacketType.DATA,
                connection_id=0x01020304,
                sequence=0x0000002A,
                ack=0x00000029,
                ack_mask=0x80000001,
                channel_id="schema",
                reliable=True,
                ordered=True,
                fragment_id=7,
                fragment_index=1,
                fragment_count=3,
                payload=b"hello",
            )
        )

        self.assertEqual(
            encoded.hex(),
            "434e523000030b2a010203040000002a0000002980000001000700010003000000050600736368656d6168656c6c6f",
        )

        decoded = decode_rudp_packet(encoded)
        self.assertEqual(decoded.packet_type, CultNetRudpPacketType.DATA)
        self.assertEqual(decoded.connection_id, 0x01020304)
        self.assertEqual(decoded.sequence, 0x0000002A)
        self.assertEqual(decoded.ack, 0x00000029)
        self.assertEqual(decoded.ack_mask, 0x80000001)
        self.assertEqual(decoded.channel_id, "schema")
        self.assertTrue(decoded.reliable)
        self.assertTrue(decoded.ordered)
        self.assertFalse(decoded.sequenced)
        self.assertEqual(decoded.fragment_id, 7)
        self.assertEqual(decoded.fragment_index, 1)
        self.assertEqual(decoded.fragment_count, 3)
        self.assertEqual(decoded.payload, b"hello")

    def test_cultnet_rudp_transport_profile_advertises_state_and_realtime_channels(self) -> None:
        profile = create_rudp_transport_profile(
            "python-rudp",
            transport_id="public-rudp",
            host="127.0.0.1",
            port=7777,
            max_payload_bytes=1200,
            max_fragment_bytes=1000,
        )

        self.assertEqual(profile["transports"][0]["protocol"], "rudp")
        self.assertEqual(profile["transports"][0]["reconnectPolicy"]["schemaVersion"], "cultnet.reconnect_policy.v0")
        self.assertEqual(profile["transports"][0]["reconnectPolicy"]["baseDelayMs"], 1_000)
        self.assertEqual(
            [
                (channel["channelId"], channel["delivery"], channel["ordering"])
                for channel in profile["transports"][0]["channels"]
            ],
            [
                ("schema", "reliable", "ordered"),
                ("latest", "unreliable", "sequenced"),
                ("realtime", "unreliable", "unordered"),
            ],
        )

    def test_cultnet_reconnect_policy_exposes_portable_delay_contract(self) -> None:
        policy = create_reconnect_policy(policy_id="rudp-default", max_attempts=8)

        self.assertEqual(policy.schema_version, "cultnet.reconnect_policy.v0")
        self.assertEqual(policy.policy_id, "rudp-default")
        self.assertEqual(policy.max_attempts, 8)
        self.assertEqual(policy.to_wire()["policyId"], "rudp-default")
        self.assertEqual(policy.to_wire()["maxAttempts"], 8)
        self.assertEqual(compute_reconnect_delay_ms(policy, 1), 1_000)
        self.assertEqual(compute_reconnect_delay_ms(policy, 3, 17), 4_017)
        self.assertEqual(compute_reconnect_delay_ms(policy, 9, 999), 30_250)
        self.assertEqual(compute_reconnect_delay_ms(policy, 0, -5), 1_000)

    def test_cultnet_reconnect_controller_schedules_attempts_and_reset(self) -> None:
        controller = CultNetReconnectController(create_reconnect_policy(max_attempts=2))

        first = controller.record_failure(10_000)
        self.assertEqual(first.attempt, 1)
        self.assertTrue(first.should_retry)
        self.assertEqual(first.delay_ms, 1_000)
        self.assertEqual(first.next_attempt_at_ms, 11_000)
        self.assertFalse(first.exhausted)
        self.assertFalse(controller.can_attempt(10_999))
        self.assertTrue(controller.can_attempt(11_000))

        second = controller.record_failure(11_000, 17)
        self.assertEqual(second.attempt, 2)
        self.assertEqual(second.delay_ms, 2_017)
        self.assertEqual(second.next_attempt_at_ms, 13_017)
        self.assertTrue(second.should_retry)

        exhausted = controller.record_failure(13_017)
        self.assertEqual(exhausted.attempt, 2)
        self.assertFalse(exhausted.should_retry)
        self.assertEqual(exhausted.delay_ms, 0)
        self.assertIsNone(exhausted.next_attempt_at_ms)
        self.assertTrue(exhausted.exhausted)
        self.assertFalse(controller.can_attempt(99_000))

        controller.reset()
        self.assertEqual(controller.attempt, 0)
        self.assertIsNone(controller.next_attempt_at_ms)
        self.assertFalse(controller.exhausted)
        self.assertTrue(controller.can_attempt(99_000))

    def test_cultnet_rudp_reconnect_loop_consumes_shared_controller(self) -> None:
        class FakeRudpReconnectTransport:
            def __init__(self) -> None:
                self.connect_calls = 0
                self.close_calls = 0
                self.last_payload: bytes | None = None

            def connect(self, payload: bytes = b"") -> None:
                self.connect_calls += 1
                self.last_payload = bytes(payload)

            def close(self) -> None:
                self.close_calls += 1

        now_ms = 10_000
        captured_callback: list[Callable[[], None]] = []
        transports: list[FakeRudpReconnectTransport] = []

        def create_transport() -> FakeRudpReconnectTransport:
            transport = FakeRudpReconnectTransport()
            transports.append(transport)
            return transport

        def scheduler(delay_ms: int, callback: Callable[[], None]) -> Callable[[], None]:
            self.assertEqual(delay_ms, 1_017)
            captured_callback[:] = [callback]

            def cancel() -> None:
                captured_callback.clear()

            return cancel

        loop = CultNetRudpReconnectLoop(
            create_transport,
            reconnect_policy=create_reconnect_policy(max_attempts=2),
            connect_payload=b"join",
            now_ms=lambda: now_ms,
            jitter_ms=lambda: 17,
            scheduler=scheduler,
        )

        first = loop.start()
        self.assertEqual(first.connect_calls, 1)
        self.assertEqual(first.last_payload, b"join")
        self.assertIs(loop.transport, first)

        decision = loop.handle_closed()
        self.assertIsNotNone(decision)
        self.assertEqual(decision.attempt, 1)
        self.assertTrue(decision.should_retry)
        self.assertEqual(decision.delay_ms, 1_017)
        self.assertEqual(decision.next_attempt_at_ms, 11_017)
        self.assertEqual(loop.reconnect_controller.attempt, 1)
        self.assertEqual(loop.reconnect_controller.next_attempt_at_ms, 11_017)
        self.assertEqual(len(captured_callback), 1)

        now_ms = 11_017
        captured_callback[0]()
        self.assertEqual(len(transports), 2)
        self.assertEqual(transports[1].connect_calls, 1)
        self.assertIs(loop.transport, transports[1])

        loop.mark_connected()
        self.assertEqual(loop.reconnect_controller.attempt, 0)

        loop.stop()
        self.assertEqual(transports[1].close_calls, 1)
        self.assertIsNone(loop.transport)

    def test_cultnet_rudp_session_handshake_acks_reliable_connect_and_accept_packets(self) -> None:
        client = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=0x0A0B0C0D, initial_sequence=1, resend_delay_ms=50)
        )
        server = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=0x0A0B0C0D, initial_sequence=100, resend_delay_ms=50)
        )

        connect = client.create_connect(0, b"join")
        self.assertEqual(connect.packet_type, CultNetRudpPacketType.CONNECT)
        self.assertEqual(connect.sequence, 1)
        self.assertEqual(client.pending_reliable_sequences, (1,))

        accept = server.accept_connect(connect, 10, b"ok")
        self.assertEqual(accept.packet_type, CultNetRudpPacketType.ACCEPT)
        self.assertEqual(accept.ack, 1)
        self.assertTrue(server.connected)
        self.assertEqual(server.pending_reliable_sequences, (100,))

        client.receive(accept, 20)
        self.assertTrue(client.connected)
        self.assertEqual(client.pending_reliable_sequences, ())

        ack = client.create_ack()
        self.assertEqual(ack.ack, 100)
        server.receive(ack, 30)
        self.assertEqual(server.pending_reliable_sequences, ())

    def test_cultnet_rudp_session_computes_ack_masks_and_clears_pending_reliable_packets(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=7, initial_sequence=10, resend_delay_ms=100))
        receiver = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=7, initial_sequence=200, resend_delay_ms=100)
        )
        sender.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 7, 1, 0, 0, "control")
        )
        receiver.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 7, 2, 0, 0, "control")
        )

        first = sender.send(
            "schema",
            b"first",
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=0),
        )
        second = sender.send(
            "schema",
            b"second",
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=0),
        )
        third = sender.send(
            "schema",
            b"third",
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=0),
        )
        self.assertEqual(sender.pending_reliable_sequences, (10, 11, 12))

        receiver.receive(first)
        receiver.receive(third)
        ack_with_gap = receiver.create_ack()
        self.assertEqual(ack_with_gap.ack, 12)
        self.assertEqual(ack_with_gap.ack_mask, 0b10 | (1 << 9))
        sender.receive(ack_with_gap)
        self.assertEqual(sender.pending_reliable_sequences, (11,))

        receiver.receive(second)
        full_ack = receiver.create_ack()
        self.assertEqual(full_ack.ack, 12)
        self.assertEqual(full_ack.ack_mask, 0b11 | (1 << 9))
        sender.receive(full_ack)
        self.assertEqual(sender.pending_reliable_sequences, ())

    def test_cultnet_rudp_session_schedules_reliable_resends_until_acked(self) -> None:
        session = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=99, initial_sequence=1, resend_delay_ms=100))
        session.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 99, 50, 0, 0, "control")
        )
        sent = session.send(
            "schema",
            b"payload",
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=10),
        )

        self.assertEqual(session.due_resends(90), ())
        self.assertEqual(tuple(packet.sequence for packet in session.due_resends(110)), (sent.sequence,))
        self.assertEqual(session.due_resends(150), ())

        session.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACK, 99, 51, sent.sequence, 0, "control")
        )
        self.assertEqual(session.due_resends(250), ())

    def test_cultnet_rudp_session_pings_and_detects_receive_timeout(self) -> None:
        client = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=101, initial_sequence=1))
        server = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=101, initial_sequence=100))
        connect = client.create_connect(0, b"join")
        accept = server.accept_connect(connect, 10)
        client.receive(accept, 20)

        ping = client.create_ping(b"pulse")
        ping_result = server.receive(ping, 30)
        self.assertIsNotNone(ping_result.reply)
        self.assertEqual(ping_result.reply.packet_type, CultNetRudpPacketType.PONG)
        self.assertEqual(ping_result.reply.payload, b"pulse")

        pong_result = client.receive(ping_result.reply, 40)
        self.assertTrue(pong_result.pong)
        self.assertEqual(pong_result.pong_payload, b"pulse")
        self.assertFalse(client.check_timeout(90, 50))
        self.assertTrue(client.check_timeout(91, 50))
        self.assertFalse(client.connected)

    def test_cultnet_rudp_session_bounds_pending_reliable_packets_before_enqueue(self) -> None:
        session = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=102, initial_sequence=1, max_pending_reliable_packets=2)
        )
        session.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 102, 50, 0, 0, "control"))
        session.send("schema", b"first", CultNetRudpSendOptions(reliable=True, ordered=True))
        session.send("schema", b"second", CultNetRudpSendOptions(reliable=True, ordered=True))
        with self.assertRaisesRegex(ValueError, "reliable send queue is full"):
            session.send("schema", b"third", CultNetRudpSendOptions(reliable=True, ordered=True))
        self.assertEqual(session.pending_reliable_sequences, (1, 2))

        fragmented = CultNetRudpSession(
            CultNetRudpSessionOptions(connection_id=103, initial_sequence=1, max_pending_reliable_packets=3)
        )
        fragmented.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 103, 50, 0, 0, "control"))
        with self.assertRaisesRegex(ValueError, "reliable send queue is full"):
            fragmented.send_many(
                "schema",
                b"fragment-me",
                CultNetRudpSendOptions(reliable=True, ordered=True),
                max_fragment_bytes=3,
            )
        self.assertEqual(fragmented.pending_reliable_sequences, ())

    def test_cultnet_rudp_session_suppresses_duplicates_and_delivers_reliable_ordered_payloads(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=123, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=123, initial_sequence=100))
        sender.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 123, 90, 0, 0, "control")
        )
        receiver.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 123, 91, 0, 0, "control")
        )

        first = sender.send("schema", b"first", CultNetRudpSendOptions(reliable=True, ordered=True))
        second = sender.send("schema", b"second", CultNetRudpSendOptions(reliable=True, ordered=True))
        third = sender.send("schema", b"third", CultNetRudpSendOptions(reliable=True, ordered=True))

        self.assertEqual([frame.payload.decode("utf-8") for frame in receiver.receive(first).delivered], ["first"])
        self.assertEqual(receiver.receive(third).delivered, ())
        self.assertEqual(receiver.receive(first).delivered, ())
        self.assertEqual(
            [frame.payload.decode("utf-8") for frame in receiver.receive(second).delivered],
            ["second", "third"],
        )

    def test_cultnet_rudp_lossy_packets_cannot_create_reliable_ordered_gaps(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=198, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=198, initial_sequence=100))
        sender.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 198, 90, 0, 0, "control"))
        receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 198, 91, 0, 0, "control"))

        realtime = sender.send("realtime", b"discarded realtime", CultNetRudpSendOptions())
        latest = sender.send("latest", b"discarded latest state", CultNetRudpSendOptions(sequenced=True))
        schema = sender.send("schema", b"committed response", CultNetRudpSendOptions(reliable=True, ordered=True))

        self.assertEqual(realtime.sequence, 0)
        self.assertEqual(latest.sequence, 1)
        self.assertEqual(
            [frame.payload.decode("utf-8") for frame in receiver.receive(schema).delivered],
            ["committed response"],
        )

    def test_cultnet_rudp_unreliable_sequenced_delivery_is_scoped_to_its_channel(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=197, initial_sequence=50))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=197, initial_sequence=100))
        sender.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 197, 90, 0, 0, "control"))
        receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 197, 91, 0, 0, "control"))

        older = sender.send("latest", b"older", CultNetRudpSendOptions(sequenced=True))
        newer = sender.send("latest", b"newer", CultNetRudpSendOptions(sequenced=True))

        self.assertEqual(older.sequence, 1)
        self.assertEqual(newer.sequence, 2)
        self.assertEqual(
            [frame.payload.decode("utf-8") for frame in receiver.receive(newer).delivered],
            ["newer"],
        )
        self.assertEqual(receiver.receive(older).delivered, ())

    def test_cultnet_rudp_rejects_unreliable_ordered_delivery(self) -> None:
        session = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=196, initial_sequence=1))
        session.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 196, 50, 0, 0, "control"))

        with self.assertRaises(ValueError) as raised:
            session.send(
                "schema",
                b"cannot order what will not retransmit",
                CultNetRudpSendOptions(ordered=True),
            )
        self.assertIn("ordered delivery requires reliability", str(raised.exception))

    def test_cultnet_rudp_session_skips_control_packets_while_ordering_schema_payloads(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=124, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=124, initial_sequence=100))
        sender.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 124, 90, 0, 0, "control")
        )
        receiver.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 124, 91, 0, 0, "control")
        )

        first = sender.send("schema", b"first", CultNetRudpSendOptions(reliable=True, ordered=True))
        control = sender.create_ack()
        second = sender.send("schema", b"second", CultNetRudpSendOptions(reliable=True, ordered=True))

        self.assertEqual([frame.payload.decode("utf-8") for frame in receiver.receive(first).delivered], ["first"])
        self.assertEqual(receiver.receive(control).delivered, ())
        self.assertEqual([frame.payload.decode("utf-8") for frame in receiver.receive(second).delivered], ["second"])

    def test_cultnet_rudp_session_fragments_and_reassembles_reliable_ordered_payloads(self) -> None:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=456, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=456, initial_sequence=100))
        sender.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 456, 90, 0, 0, "control")
        )
        receiver.receive(
            CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 456, 91, 0, 0, "control")
        )

        packets = sender.send_many(
            "schema",
            b"fragment-me-please",
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=10),
            max_fragment_bytes=5,
        )
        self.assertEqual(len(packets), 4)
        self.assertEqual(tuple(packet.fragment_count for packet in packets), (4, 4, 4, 4))
        self.assertEqual(tuple(packet.fragment_index for packet in packets), (0, 1, 2, 3))
        self.assertTrue(all(packet.fragment_id == packets[0].fragment_id for packet in packets))

        self.assertEqual(receiver.receive(packets[0]).delivered, ())
        self.assertEqual(receiver.receive(packets[1]).delivered, ())
        self.assertEqual(receiver.receive(packets[2]).delivered, ())
        delivered = receiver.receive(packets[3]).delivered
        self.assertEqual(len(delivered), 1)
        self.assertEqual(delivered[0].payload, b"fragment-me-please")
        self.assertEqual(delivered[0].sequence, packets[0].sequence)

    def test_cultnet_rudp_session_advances_large_fragment_sets_through_bounded_window(self) -> None:
        connection_id = 457
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=connection_id, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=connection_id, initial_sequence=100))
        sender.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, connection_id, 0, 0, 0, "control"))
        receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, connection_id, 0, 0, 0, "control"))

        fragment_count = CultNetRudpSession.RELIABLE_SEND_WINDOW_PACKETS + 17
        payload = bytes(index % 251 for index in range(fragment_count * 8))
        wire = list(sender.send_many(
            "schema",
            payload,
            CultNetRudpSendOptions(reliable=True, ordered=True, now_ms=1),
            max_fragment_bytes=8,
        ))
        self.assertEqual(len(wire), CultNetRudpSession.RELIABLE_SEND_WINDOW_PACKETS)
        oldest_sequence = wire[0].sequence
        self.assertEqual(len(sender.pending_reliable_sequences), len(wire))
        self.assertEqual(sender.queued_reliable_packet_count, 17)

        delivered = []
        while wire:
            admitted = len(wire)
            for _ in range(admitted):
                packet = wire.pop(0)
                delivered.extend(receiver.receive(packet, 2).delivered)
            acknowledged = sender.receive(receiver.create_ack(), 3)
            wire.extend(acknowledged.ready_to_send)

        self.assertEqual(sender.outstanding_reliable_packet_count, 0)
        self.assertEqual(len(delivered), 1)
        self.assertEqual(delivered[0].payload, payload)
        old_ack = receiver.create_ack_for_received(oldest_sequence)
        self.assertEqual(old_ack.ack, oldest_sequence)
        self.assertEqual(old_ack.ack_mask, 0)

    def test_cultnet_rudp_socket_transport_handshakes_and_carries_reliable_ordered_schema_frames(self) -> None:
        server_socket = bind_udp_socket()
        client_socket = bind_udp_socket()
        connection_id = 0x10203040
        server = CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id="python-rudp-server",
                socket=server_socket,
                mode=CultNetRudpSocketMode.SERVER,
                connection_id=connection_id,
                initial_sequence=100,
                resend_delay_ms=25,
            )
        )
        client = CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id="python-rudp-client",
                socket=client_socket,
                mode=CultNetRudpSocketMode.CLIENT,
                remote_addr=server_socket.getsockname(),
                connection_id=connection_id,
                initial_sequence=1,
                resend_delay_ms=25,
            )
        )

        try:
            client.connect(b"join")
            pump_rudp_handshake(client, server)
            self.assertTrue(client.connected)
            self.assertTrue(server.connected)

            client.send("schema", b"client-state")
            server_frame = receive_rudp_frame(server)
            self.assertEqual(server_frame.channel_id, "schema")
            self.assertEqual(server_frame.payload, b"client-state")

            server.send("schema", b"server-state")
            client_frame = receive_rudp_frame(client)
            self.assertEqual(client_frame.channel_id, "schema")
            self.assertEqual(client_frame.payload, b"server-state")
            self.assertEqual(server.profile["transports"][0]["protocol"], "rudp")
            self.assertEqual(client.stats.frames_sent, 1)
            self.assertEqual(server.stats.frames_received, 1)
        finally:
            client.close()
            server.close()

    def test_cultnet_rudp_socket_transport_carries_fragmented_reliable_ordered_schema_frames(self) -> None:
        server_socket = bind_udp_socket()
        client_socket = bind_udp_socket()
        connection_id = 0x10203041
        server = CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id="python-rudp-fragment-server",
                socket=server_socket,
                mode=CultNetRudpSocketMode.SERVER,
                connection_id=connection_id,
                initial_sequence=100,
                resend_delay_ms=25,
                max_fragment_bytes=8,
            )
        )
        client = CultNetRudpSocketTransportConnection(
            CultNetRudpSocketTransportOptions(
                runtime_id="python-rudp-fragment-client",
                socket=client_socket,
                mode=CultNetRudpSocketMode.CLIENT,
                remote_addr=server_socket.getsockname(),
                connection_id=connection_id,
                initial_sequence=1,
                resend_delay_ms=25,
                max_fragment_bytes=8,
            )
        )

        try:
            payload = b"this-schema-frame-is-larger-than-one-rudp-fragment"
            client.connect(b"join")
            pump_rudp_handshake(client, server)
            client.send("schema", payload)
            server_frame = receive_rudp_frame(server)
            self.assertEqual(server_frame.channel_id, "schema")
            self.assertEqual(server_frame.payload, payload)
            self.assertEqual(client.stats.frames_sent, 1)
            self.assertEqual(server.stats.frames_received, 1)
        finally:
            client.close()
            server.close()

    def test_cultnet_database_subscription_helpers_match_schema_v0_shape(self) -> None:
        subscribe = database_subscribe(
            message_id="sub-message",
            subscription_id="sub-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
            include_snapshot=False,
        ).to_wire()
        self.assertEqual(subscribe["schemaVersion"], "cultnet.database_subscribe.v0")
        self.assertEqual(subscribe["messageId"], "sub-message")
        self.assertEqual(subscribe["subscriptionId"], "sub-1")
        self.assertEqual(subscribe["schemaIds"], ["schema-a"])
        self.assertEqual(subscribe["recordKeys"], ["record-a"])
        self.assertFalse(subscribe["includeSnapshot"])

        unsubscribe = database_unsubscribe(message_id="unsub-message", subscription_id="sub-1").to_wire()
        self.assertEqual(unsubscribe["schemaVersion"], "cultnet.database_unsubscribe.v0")
        self.assertEqual(unsubscribe["messageId"], "unsub-message")
        self.assertEqual(unsubscribe["subscriptionId"], "sub-1")

    def test_cultnet_database_change_parses_put_and_delete_shapes(self) -> None:
        put_change = CultNetDatabaseChange.from_wire({
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": "change-put",
            "subscriptionId": "sub-1",
            "changeKind": "added",
            "document": {
                "schemaId": "schema-note",
                "recordKey": "note:1",
                "payload": b"payload",
            },
        })
        self.assertEqual(put_change.schema_id, "schema-note")
        self.assertEqual(put_change.record_key, "note:1")
        self.assertIsInstance(put_change.raw_document, CultNetRawDocumentRecord)
        self.assertEqual(put_change.raw_document.payload, b"payload")
        self.assertEqual(put_change.to_wire()["document"]["recordKey"], "note:1")

        delete_change = CultNetDatabaseChange.from_wire({
            "schemaVersion": "cultnet.database_change_raw.v0",
            "messageId": "change-delete",
            "subscriptionId": "sub-1",
            "changeKind": "removed",
            "schemaId": "schema-note",
            "recordKey": "note:1",
        })
        self.assertIsNone(delete_change.document)
        self.assertIsNone(delete_change.raw_document)
        self.assertEqual(delete_change.schema_id, "schema-note")
        self.assertEqual(delete_change.to_wire()["recordKey"], "note:1")

    def test_cultnet_raw_put_helper_carries_message_id(self) -> None:
        put = document_put_raw(
            message_id="put-1",
            key="record-a",
            schema_id="schema-a",
            stored_at="2026-06-13T00:00:00Z",
            payload=b"payload",
            source_runtime_id="python-test",
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(put["schemaVersion"], "cultnet.document_put_raw.v0")
        self.assertEqual(put["messageId"], "put-1")
        self.assertEqual(put["document"]["recordKey"], "record-a")
        self.assertEqual(put["document"]["sourceRuntimeId"], "python-test")
        self.assertEqual(put["shardId"], "interop")
        self.assertEqual(put["shardEpoch"], 1)

    def test_cultnet_catalog_and_snapshot_helpers_accept_filters(self) -> None:
        catalog = schema_catalog_request(
            message_id="catalog-1",
            include_schema_json=True,
            schema_ids=["schema-a"],
            kinds=["wire_message"],
        ).to_wire()
        self.assertEqual(catalog["schemaVersion"], "cultnet.schema_catalog_request.v0")
        self.assertEqual(catalog["messageId"], "catalog-1")
        self.assertTrue(catalog["includeSchemaJson"])
        self.assertEqual(catalog["schemaIds"], ["schema-a"])
        self.assertEqual(catalog["kinds"], ["wire_message"])

        snapshot = snapshot_request(
            message_id="snapshot-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(snapshot["schemaVersion"], "cultnet.snapshot_request.v0")
        self.assertEqual(snapshot["messageId"], "snapshot-1")
        self.assertEqual(snapshot["schemaIds"], ["schema-a"])
        self.assertEqual(snapshot["recordKeys"], ["record-a"])
        self.assertEqual(snapshot["shardId"], "interop")
        self.assertEqual(snapshot["shardEpoch"], 1)

    def test_cultnet_raw_snapshot_response_filters_and_roundtrips_records(self) -> None:
        response = CultNetRawSnapshotResponse.from_wire({
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": "snapshot-1",
            "shardId": "notes",
            "shardEpoch": 3,
            "shardLogSequence": 8,
            "documents": [
                CultNetRawDocumentRecord(
                    schema_id="schema-note",
                    record_key="note:1",
                    stored_at="2026-06-14T00:00:00Z",
                    payload=b"note",
                    source_runtime_id="python-test",
                    tags=("snapshot",),
                ).to_wire(),
                {
                    "schemaId": "schema-fact",
                    "recordKey": "fact:1",
                    "payloadEncoding": "messagepack",
                    "payload": b"fact",
                },
            ],
        })
        self.assertEqual(response.shard_id, "notes")
        self.assertEqual(response.shard_epoch, 3)
        self.assertEqual(response.shard_log_sequence, 8)
        self.assertEqual(response.documents[0].source_runtime_id, "python-test")
        self.assertEqual(response.filter(schema_ids=["schema-note"])[0].record_key, "note:1")
        self.assertEqual(response.filter(record_keys=["fact:1"])[0].schema_id, "schema-fact")
        self.assertEqual(response.to_wire()["documents"][0]["tags"], ["snapshot"])

    def test_cultnet_raw_client_fetches_schema_snapshot_and_shard_reads(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        received_versions: list[str] = []
        ready = threading.Event()
        server_error: list[BaseException] = []
        port_holder: list[int] = []

        def serve_requests() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(4)
                    ready.set()
                    for _ in range(4):
                        connection, _ = server.accept()
                        with connection:
                            stream = connection.makefile("rwb")
                            request = msgpack.unpackb(read_frame(stream), raw=False)
                            received_versions.append(request["schemaVersion"])
                            if request["schemaVersion"] == "cultnet.schema_catalog_request.v0":
                                self.assertEqual(request["kinds"], ["wire_message"])
                                response = {"schemaVersion": "cultnet.schema_catalog_response.v0", "messageId": request["messageId"], "schemas": []}
                            elif request["schemaVersion"] == "cultnet.snapshot_request.v0":
                                self.assertEqual(request["shardId"], "interop")
                                response = {"schemaVersion": "cultnet.snapshot_response_raw.v0", "messageId": request["messageId"], "documents": []}
                            elif request["schemaVersion"] == "cultnet.shard_catalog_request.v0":
                                response = {
                                    "schemaVersion": "cultnet.shard_catalog_response.v0",
                                    "messageId": request["messageId"],
                                    "shards": [{
                                        "shardId": "interop",
                                        "ownerRuntimeId": "socket-test",
                                        "epoch": 1,
                                        "schemaIds": request["schemaIds"],
                                        "keyPrefix": "record-",
                                        "primaryEndpoints": ["cultnet://127.0.0.1:1"],
                                    }],
                                }
                            elif request["schemaVersion"] == "cultnet.shard_log_request.v0":
                                self.assertEqual(request["afterSequence"], 7)
                                response = {
                                    "schemaVersion": "cultnet.shard_log_response.v0",
                                    "messageId": request["messageId"],
                                    "shardId": request["shardId"],
                                    "shardEpoch": request["shardEpoch"],
                                    "entries": [],
                                    "resyncRequired": False,
                                }
                            else:
                                raise AssertionError(f"unexpected request {request['schemaVersion']}")
                            write_frame(stream, msgpack.packb(response, use_bin_type=True))
                            stream.flush()
            except BaseException as error:
                server_error.append(error)
                ready.set()

        thread = threading.Thread(target=serve_requests, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        self.assertEqual(client.fetch_schema_catalog(kinds=["wire_message"])["schemaVersion"], "cultnet.schema_catalog_response.v0")
        typed_snapshot = client.fetch_snapshot_response(shard_id="interop", shard_epoch=1)
        self.assertEqual(typed_snapshot.shard_id, None)
        self.assertEqual(typed_snapshot.documents, ())
        shard_catalog = CultNetShardCatalog()
        synced_shards = client.sync_shard_catalog(shard_catalog, schema_ids=["schema-a"])
        self.assertEqual(synced_shards[0].shard_id, "interop")
        self.assertEqual(shard_catalog.get("interop"), synced_shards[0])
        shard_log = client.fetch_shard_log_response(shard_id="interop", shard_epoch=1, after_sequence=7)
        self.assertEqual(shard_log.shard_id, "interop")
        self.assertFalse(shard_log.resync_required)
        self.assertEqual(shard_log.last_sequence, 0)

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertEqual(received_versions, [
            "cultnet.schema_catalog_request.v0",
            "cultnet.snapshot_request.v0",
            "cultnet.shard_catalog_request.v0",
            "cultnet.shard_log_request.v0",
        ])

    def test_cultnet_raw_client_raises_typed_peer_error_response(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        ready = threading.Event()
        port_holder: list[int] = []

        def serve_error() -> None:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                server.bind(("127.0.0.1", 0))
                port_holder.append(server.getsockname()[1])
                server.listen(1)
                ready.set()
                connection, _ = server.accept()
                with connection:
                    stream = connection.makefile("rwb")
                    read_frame(stream)
                    write_frame(stream, msgpack.packb({
                        "schemaVersion": "cultnet.error.v0",
                        "messageId": "error-1",
                        "error": "Snapshot document limit exceeded",
                    }, use_bin_type=True))
                    stream.flush()

        thread = threading.Thread(target=serve_error, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        with self.assertRaisesRegex(CultNetPeerError, "Snapshot document limit exceeded") as raised:
            client.fetch_snapshot()

        self.assertEqual(raised.exception.peer_error, "Snapshot document limit exceeded")
        self.assertEqual(raised.exception.response["schemaVersion"], "cultnet.error.v0")
        thread.join(2.0)

    def test_cultnet_database_subscription_reads_snapshot_and_change(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        document = define_database_entry_type(
            "sub.item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        schema_id = document.catalog_entry().schema_id
        ready = threading.Event()
        server_error: list[BaseException] = []
        port_holder: list[int] = []
        received_versions: list[str] = []

        def serve_subscription() -> None:
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                    server.bind(("127.0.0.1", 0))
                    port_holder.append(server.getsockname()[1])
                    server.listen(1)
                    ready.set()
                    connection, _ = server.accept()
                    with connection:
                        stream = connection.makefile("rwb")
                        subscribe = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(subscribe["schemaVersion"])
                        self.assertEqual(subscribe["subscriptionId"], "sub-1")
                        snapshot = {
                            "schemaVersion": "cultnet.snapshot_response_raw.v0",
                            "messageId": subscribe["messageId"],
                            "documents": [],
                        }
                        write_frame(stream, msgpack.packb(snapshot, use_bin_type=True))
                        stream.flush()

                        put = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(put["schemaVersion"])
                        change = {
                            "schemaVersion": "cultnet.database_change_raw.v0",
                            "messageId": "change-1",
                            "subscriptionId": "sub-1",
                            "changeKind": "added",
                            "document": put["document"],
                        }
                        write_frame(stream, msgpack.packb(change, use_bin_type=True))
                        stream.flush()

                        unsubscribe = msgpack.unpackb(read_frame(stream), raw=False)
                        received_versions.append(unsubscribe["schemaVersion"])
            except BaseException as error:
                server_error.append(error)
                ready.set()

        thread = threading.Thread(target=serve_subscription, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))
        self.assertFalse(server_error)

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        put = document_put_raw(
            message_id="put-sub",
            key="item:sub",
            schema_id=schema_id,
            stored_at="2026-06-14T00:00:00Z",
            payload=document.encode_payload(Item("orb", "gear", 8)),
        )
        with client.subscribe_database(subscription_id="sub-1", schema_ids=[schema_id]) as subscription:
            snapshot = subscription.read_next_snapshot_response()
            subscription.send(put)
            change = subscription.read_next_change()

        thread.join(2.0)
        self.assertFalse(server_error)
        self.assertIsInstance(snapshot, CultNetRawSnapshotResponse)
        self.assertEqual(snapshot.message_id, "cultnet-python-subscribe")
        self.assertEqual(snapshot.to_wire()["schemaVersion"], "cultnet.snapshot_response_raw.v0")
        self.assertEqual(change.change_kind, "added")
        self.assertEqual(change.record_key, "item:sub")
        self.assertIsInstance(change.raw_document, CultNetRawDocumentRecord)
        self.assertEqual(change.raw_document.record_key, "item:sub")
        self.assertEqual(received_versions, [
            "cultnet.database_subscribe.v0",
            "cultnet.document_put_raw.v0",
            "cultnet.database_unsubscribe.v0",
        ])

    def test_cultnet_database_subscription_raises_typed_peer_error(self) -> None:
        import msgpack  # type: ignore
        from cultnet_py import read_frame, write_frame

        ready = threading.Event()
        port_holder: list[int] = []

        def serve_subscription_error() -> None:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
                server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                server.bind(("127.0.0.1", 0))
                port_holder.append(server.getsockname()[1])
                server.listen(1)
                ready.set()
                connection, _ = server.accept()
                with connection:
                    stream = connection.makefile("rwb")
                    read_frame(stream)
                    write_frame(stream, msgpack.packb({
                        "schemaVersion": "cultnet.error.v0",
                        "messageId": "subscription-error",
                        "error": "subscription rejected",
                    }, use_bin_type=True))
                    stream.flush()

        thread = threading.Thread(target=serve_subscription_error, daemon=True)
        thread.start()
        self.assertTrue(ready.wait(2.0))

        client = CultNetRawClient("127.0.0.1", port_holder[0], timeout_seconds=2.0)
        with client.subscribe_database(subscription_id="sub-error") as subscription:
            with self.assertRaisesRegex(CultNetPeerError, "subscription rejected") as raised:
                subscription.read_next()
        self.assertEqual(raised.exception.response["messageId"], "subscription-error")
        thread.join(2.0)

    def test_cultnet_replication_helpers_apply_raw_snapshot_and_shard_log(self) -> None:
        document = define_database_entry_type(
            "replica.item",
            [
                ("name", 0),
                ("category", 1),
                ("value", 2, 0),
            ],
            cls=Item,
        )
        cache = CultCache()
        cache.register_document_type(document)
        schema_id = document.catalog_entry().schema_id

        snapshot = {
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": "snapshot-1",
            "documents": [
                {
                    "schemaId": schema_id,
                    "recordKey": "item:1",
                    "storedAt": "2026-06-13T00:00:00Z",
                    "payloadEncoding": "messagepack",
                    "payload": document.encode_payload(Item("sword", "gear", 3)),
                }
            ],
        }
        applied_snapshot = apply_raw_snapshot(cache, [document], snapshot)
        self.assertEqual(applied_snapshot[0].record_key, "item:1")
        self.assertEqual(cache.get_required(document, "item:1").value, 3)
        typed_snapshot = CultNetRawSnapshotResponse.from_wire(snapshot)
        direct_applied = apply_raw_document_record(cache, schema_document_map([document]), typed_snapshot.documents[0])
        self.assertEqual(direct_applied.schema_id, schema_id)
        self.assertEqual(direct_applied.record_key, "item:1")
        typed_snapshot_applied = apply_raw_snapshot(cache, [document], typed_snapshot)
        self.assertEqual(typed_snapshot_applied[0].record_key, "item:1")

        shard_log = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-1",
            "shardId": "interop",
            "shardEpoch": 1,
            "resyncRequired": False,
            "entries": [
                {
                    "sequence": 0,
                    "changeKind": "updated",
                    "put": {
                        "schemaVersion": "cultnet.document_put_raw.v0",
                        "messageId": "put-unknown",
                        "document": {
                            "schemaId": "replica.unknown.v1",
                            "recordKey": "unknown:1",
                            "storedAt": "2026-06-13T00:00:01Z",
                            "payloadEncoding": "messagepack",
                            "payload": document.encode_payload(Item("ignored", "unknown", 0)),
                        },
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
                {
                    "sequence": 1,
                    "changeKind": "updated",
                    "put": {
                        "schemaVersion": "cultnet.document_put_raw.v0",
                        "messageId": "put-1",
                        "document": {
                            "schemaId": schema_id,
                            "recordKey": "item:1",
                            "storedAt": "2026-06-13T00:00:01Z",
                            "payloadEncoding": "messagepack",
                            "payload": document.encode_payload(Item("sword", "gear", 12)),
                        },
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
                {
                    "sequence": 2,
                    "changeKind": "removed",
                    "delete": {
                        "schemaVersion": "cultnet.document_delete.v0",
                        "messageId": "delete-1",
                        "schemaId": schema_id,
                        "recordKey": "item:1",
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                },
            ],
        }
        applied_log = apply_shard_log_response(cache, [document], shard_log)
        self.assertEqual([change.change_kind for change in applied_log], ["updated", "removed"])
        self.assertIsNone(cache.get(document, "item:1"))

        cache.put(document, "item:1", Item("sword", "gear", 3))
        typed_shard_log = CultNetShardLogResponse.from_wire({
            **shard_log,
            "entries": shard_log["entries"][1:],
        })
        typed_applied_log = apply_shard_log_response(cache, [document], typed_shard_log)
        self.assertEqual([change.change_kind for change in typed_applied_log], ["updated", "removed"])
        self.assertIsNone(cache.get(document, "item:1"))

    def test_cultnet_replication_helpers_recover_schema_stamped_raw_records(self) -> None:
        document = define_database_entry_type(
            "replica.runtime-policy",
            [
                ("schema_version", 0),
                ("name", 1),
                ("value", 2),
            ],
            schema_id="replica.runtime-policy.current",
            schema_name="replica.runtime_policy",
            schema_version="replica.runtime_policy.v1",
        )
        cache = CultCache()
        cache.register_document_type(document)
        stale_schema_id = "sha256:stale-replica-runtime-policy"
        payload = document.encode_payload({
            "schema_version": "replica.runtime_policy.v1",
            "name": "policy",
            "value": "still synced",
        })
        record = {
            "schemaId": stale_schema_id,
            "recordKey": "policy:1",
            "storedAt": "2026-06-25T00:00:00Z",
            "payloadEncoding": "messagepack",
            "payload": payload,
        }
        snapshot = {
            "schemaVersion": "cultnet.snapshot_response_raw.v0",
            "messageId": "snapshot-stale",
            "documents": [record],
        }

        applied_snapshot = apply_raw_snapshot(cache, [document], snapshot)
        local_schema_id = document.catalog_entry().schema_id
        self.assertEqual(applied_snapshot[0].schema_id, local_schema_id)
        self.assertEqual(cache.get_required(document, "policy:1")["value"], "still synced")
        self.assertEqual(cache.get_required_envelope(document, "policy:1").schema_id, local_schema_id)
        direct_applied = apply_raw_document_record(cache, schema_document_map([document]), record)
        self.assertEqual(direct_applied.schema_id, local_schema_id)
        self.assertEqual(cache.get_required_envelope(document, "policy:1").schema_id, local_schema_id)

        shard_log = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-stale",
            "shardId": "interop",
            "shardEpoch": 1,
            "resyncRequired": False,
            "entries": [
                {
                    "sequence": 0,
                    "changeKind": "updated",
                    "put": {
                        "schemaVersion": "cultnet.document_put_raw.v0",
                        "messageId": "put-stale",
                        "document": {
                            **record,
                            "payload": document.encode_payload({
                                "schema_version": "replica.runtime_policy.v1",
                                "name": "policy",
                                "value": "updated through log",
                            }),
                        },
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                }
            ],
        }

        applied_log = apply_shard_log_response(cache, [document], shard_log)
        self.assertEqual(applied_log[0].schema_id, local_schema_id)
        self.assertEqual(cache.get_required(document, "policy:1")["value"], "updated through log")
        self.assertEqual(cache.get_required_envelope(document, "policy:1").schema_id, local_schema_id)

        delete_log = {
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-delete-alias",
            "shardId": "interop",
            "shardEpoch": 1,
            "resyncRequired": False,
            "entries": [
                {
                    "sequence": 1,
                    "changeKind": "removed",
                    "delete": {
                        "schemaVersion": "cultnet.document_delete.v0",
                        "messageId": "delete-version-alias",
                        "schemaId": "replica.runtime_policy.v1",
                        "recordKey": "policy:1",
                        "shardId": "interop",
                        "shardEpoch": 1,
                    },
                }
            ],
        }
        applied_delete = apply_shard_log_response(cache, [document], delete_log)
        self.assertEqual(applied_delete[0].schema_id, local_schema_id)
        self.assertEqual(applied_delete[0].change_kind, "removed")
        self.assertIsNone(cache.get(document, "policy:1"))

    def test_cultnet_document_delete_helper_matches_schema_v0_shape(self) -> None:
        message = document_delete(
            message_id="delete-1",
            schema_id="schema-a",
            record_key="record-a",
            shard_id="interop",
            shard_epoch=1,
        ).to_wire()
        self.assertEqual(message["schemaVersion"], "cultnet.document_delete.v0")
        self.assertEqual(message["messageId"], "delete-1")
        self.assertEqual(message["schemaId"], "schema-a")
        self.assertEqual(message["recordKey"], "record-a")
        self.assertEqual(message["shardId"], "interop")
        self.assertEqual(message["shardEpoch"], 1)

    def test_cultnet_auth_session_helpers_match_schema_v0_shape(self) -> None:
        self.assertEqual(
            login(nonce="nonce", auth="encrypted-auth", password="encrypted-password").to_wire(),
            {
                "schemaVersion": "cultnet.login.v0",
                "nonce": "nonce",
                "auth": "encrypted-auth",
                "password": "encrypted-password",
            },
        )
        self.assertEqual(
            register(
                nonce="nonce",
                email="encrypted-email",
                password="encrypted-password",
                name="encrypted-name",
            ).to_wire(),
            {
                "schemaVersion": "cultnet.register.v0",
                "nonce": "nonce",
                "email": "encrypted-email",
                "password": "encrypted-password",
                "name": "encrypted-name",
            },
        )
        self.assertEqual(
            verify_session(nonce="nonce", session="encrypted-session").to_wire(),
            {
                "schemaVersion": "cultnet.verify.v0",
                "nonce": "nonce",
                "session": "encrypted-session",
            },
        )
        self.assertEqual(
            login_success(nonce="nonce", session="encrypted-session").to_wire(),
            {
                "schemaVersion": "cultnet.login_success.v0",
                "nonce": "nonce",
                "session": "encrypted-session",
            },
        )

    def test_cultnet_wire_catalog_describes_python_handled_messages(self) -> None:
        descriptors = wire_message_schema_descriptors(include_schema_json=True)
        by_version = {descriptor["schemaVersion"]: descriptor for descriptor in descriptors}
        for schema_version in [
            "cultnet.error.v0",
            "cultnet.login.v0",
            "cultnet.register.v0",
            "cultnet.verify.v0",
            "cultnet.login_success.v0",
            "cultnet.transport_profile.v0",
            "cultnet.document_delete.v0",
            "cultnet.database_change_raw.v0",
            "cultnet.shard_log_response.v0",
            "cultnet.simulation_consensus_candidate.v0",
            "cultmesh.peer_exchange_response.v0",
        ]:
            self.assertIn(schema_version, by_version)
            expected_kind = "shared_contract" if schema_version == "cultnet.transport_profile.v0" else "wire_message"
            self.assertEqual(by_version[schema_version]["kind"], expected_kind)
            self.assertIn("cultnet.schema.v0", by_version[schema_version]["wireContracts"])
            self.assertIn(schema_version, by_version[schema_version]["schemaJson"])
            self.assertEqual(len(by_version[schema_version]["contentHash"]), 64)

        delete_schema = json.loads(by_version["cultnet.document_delete.v0"]["schemaJson"])
        self.assertEqual(
            delete_schema["required"],
            ["schemaVersion", "messageId", "schemaId", "recordKey"],
        )
        self.assertEqual(delete_schema["properties"]["recordKey"]["type"], "string")

        error_schema = json.loads(by_version["cultnet.error.v0"]["schemaJson"])
        self.assertEqual(error_schema["required"], ["schemaVersion", "error"])
        self.assertEqual(error_schema["properties"]["error"]["type"], "string")
        self.assertIn("details", error_schema["properties"])

        login_schema = json.loads(by_version["cultnet.login.v0"]["schemaJson"])
        self.assertEqual(login_schema["required"], ["schemaVersion", "nonce", "auth", "password"])
        self.assertEqual(login_schema["properties"]["auth"]["type"], "string")

        register_schema = json.loads(by_version["cultnet.register.v0"]["schemaJson"])
        self.assertEqual(register_schema["required"], ["schemaVersion", "nonce", "email", "password", "name"])
        self.assertEqual(register_schema["properties"]["email"]["type"], "string")

        verify_schema = json.loads(by_version["cultnet.verify.v0"]["schemaJson"])
        self.assertEqual(verify_schema["required"], ["schemaVersion", "nonce", "session"])
        self.assertEqual(verify_schema["properties"]["session"]["type"], "string")

        success_schema = json.loads(by_version["cultnet.login_success.v0"]["schemaJson"])
        self.assertEqual(success_schema["required"], ["schemaVersion", "nonce", "session"])

        transport_profile_schema = json.loads(by_version["cultnet.transport_profile.v0"]["schemaJson"])
        self.assertEqual(transport_profile_schema["required"], ["schemaVersion", "runtimeId", "transports"])
        transport = transport_profile_schema["properties"]["transports"]["items"]
        self.assertEqual(transport["properties"]["protocol"]["enum"], ["tcp_framed", "litenetlib", "websocket", "rudp"])
        self.assertEqual(
            transport["properties"]["reconnectPolicy"]["properties"]["schemaVersion"]["const"],
            "cultnet.reconnect_policy.v0",
        )
        channel = transport["properties"]["channels"]["items"]
        self.assertEqual(channel["properties"]["ordering"]["enum"], ["ordered", "unordered", "sequenced"])

        change_schema = json.loads(by_version["cultnet.database_change_raw.v0"]["schemaJson"])
        self.assertEqual(change_schema["properties"]["changeKind"]["enum"], ["added", "updated", "removed"])
        self.assertIn("document", change_schema["properties"])
        self.assertIn("schemaId", change_schema["properties"])
        self.assertIn("recordKey", change_schema["properties"])

        log_schema = json.loads(by_version["cultnet.shard_log_response.v0"]["schemaJson"])
        self.assertIn("entries", log_schema["required"])
        self.assertIn("resyncRequired", log_schema["required"])
        self.assertIn("compactedThrough", log_schema["properties"])

        hello_schema = json.loads(by_version["cultnet.hello.v0"]["schemaJson"])
        mutation_contract = hello_schema["properties"]["supportedMutationContracts"]["items"]
        self.assertEqual(mutation_contract["type"], "object")
        self.assertIn("operations", mutation_contract["properties"])
        self.assertEqual(
            hello_schema["properties"]["transportProfiles"]["items"]["$ref"],
            "https://github.com/GameCult/cultnet-ts/contracts/cultnet.transport-profile.schema.json",
        )

        consensus_schema = json.loads(by_version["cultnet.simulation_consensus_candidate.v0"]["schemaJson"])
        for required_field in ["witnessCount", "supportWeight", "totalWeight", "hasQuorum", "confidence"]:
            self.assertIn(required_field, consensus_schema["required"])

    def test_cultnet_schema_catalog_applies_filters_and_responses(self) -> None:
        catalog = wire_message_schema_catalog(include_schema_json=True)
        descriptor = catalog.get("https://github.com/GameCult/cultnet-ts/contracts/cultnet.document-put-raw.schema.json")
        self.assertIsNotNone(descriptor)
        assert descriptor is not None
        self.assertEqual(descriptor.kind, "wire_message")
        self.assertEqual(descriptor.schema_version, "cultnet.document_put_raw.v0")
        self.assertIsNotNone(descriptor.schema_json)

        filtered = catalog.list(kinds=["wire_message"], schema_ids=[descriptor.schema_id])
        self.assertEqual(filtered, [descriptor])

        response = catalog.create_response(
            message_id="catalog-response",
            include_schema_json=False,
            schema_ids=[descriptor.schema_id],
            kinds=["wire_message"],
        )
        self.assertEqual(response["schemaVersion"], "cultnet.schema_catalog_response.v0")
        self.assertNotIn("schemaJson", response["schemas"][0])

        remote = CultNetSchemaCatalog()
        applied = remote.apply_response({
            "schemaVersion": "cultnet.schema_catalog_response.v0",
            "messageId": "remote",
            "schemas": [
                CultNetSchemaDescriptor(
                    schema_id="schema:custom",
                    kind="shared_contract",
                    schema_version="custom.v0",
                    wire_contracts=("cultnet.schema.v0",),
                    content_hash="hash",
                    schema_json="{}",
                ).to_wire()
            ],
        })
        self.assertEqual(applied[0].schema_id, "schema:custom")
        self.assertEqual(remote.get("schema:custom"), applied[0])

    def test_cultnet_shard_helpers_match_schema_v0_shape(self) -> None:
        catalog = shard_catalog_request(
            message_id="catalog-1",
            schema_ids=["schema-a"],
            record_keys=["record-a"],
        ).to_wire()
        self.assertEqual(catalog["schemaVersion"], "cultnet.shard_catalog_request.v0")
        self.assertEqual(catalog["messageId"], "catalog-1")
        self.assertEqual(catalog["schemaIds"], ["schema-a"])
        self.assertEqual(catalog["recordKeys"], ["record-a"])

        log = shard_log_request(
            message_id="log-1",
            shard_id="interop",
            shard_epoch=7,
            after_sequence=3,
            limit=2,
        ).to_wire()
        self.assertEqual(log["schemaVersion"], "cultnet.shard_log_request.v0")
        self.assertEqual(log["messageId"], "log-1")
        self.assertEqual(log["shardId"], "interop")
        self.assertEqual(log["shardEpoch"], 7)
        self.assertEqual(log["afterSequence"], 3)
        self.assertEqual(log["limit"], 2)

    def test_cultnet_shard_catalog_applies_filters_and_responses(self) -> None:
        catalog = CultNetShardCatalog()
        primary = catalog.upsert(CultNetShardDescriptor(
            shard_id="notes",
            owner_runtime_id="python-runtime",
            epoch=2,
            is_primary=True,
            schema_ids=("schema-note.v1",),
            key_prefix="note:",
            primary_endpoints=("cultnet://127.0.0.1:3075",),
            read_replica_endpoints=("cultnet://127.0.0.1:3075",),
            region="local",
        ))
        catalog.upsert({
            "shardId": "facts",
            "ownerRuntimeId": "python-runtime",
            "epoch": 1,
            "schemaIds": ["schema-fact"],
            "keyPrefix": "fact:",
            "primaryEndpoints": ["cultnet://127.0.0.1:3076"],
        })

        self.assertTrue(primary.serves(schema_id="schema-note.v1", record_key="note:1"))
        self.assertTrue(primary.serves(schema_id="schema-note", record_key="note:1"))
        self.assertFalse(primary.serves(schema_id="schema-note", record_key="fact:1"))
        self.assertEqual(catalog.list(schema_ids=["schema-note.v1"]), [primary])
        self.assertEqual(catalog.list(schema_ids=["schema-note"]), [primary])
        self.assertEqual(catalog.list(record_keys=["note:1"]), [primary])

        response = catalog.create_response(
            message_id="shards",
            schema_ids=["schema-note"],
            record_keys=["note:1"],
        )
        self.assertEqual(response["schemaVersion"], "cultnet.shard_catalog_response.v0")
        self.assertEqual(response["shards"][0]["shardId"], "notes")
        self.assertEqual(response["shards"][0]["ownerRuntimeId"], "python-runtime")

        remote = CultNetShardCatalog()
        applied = remote.apply_response(response)
        self.assertEqual(applied[0].shard_id, "notes")
        self.assertEqual(remote.get("notes"), applied[0])

    def test_cultnet_shard_log_response_tracks_cursor_and_resync_state(self) -> None:
        response = CultNetShardLogResponse.from_wire({
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log",
            "shardId": "notes",
            "shardEpoch": 2,
            "entries": [
                CultNetShardLogEntry(
                    sequence=2,
                    change_kind="updated",
                    put={"schemaVersion": "cultnet.document_put_raw.v0", "messageId": "put", "document": {"schemaId": "schema-note", "recordKey": "note:1", "payload": b"p"}},
                    committed_at="2026-06-14T00:00:00Z",
                ).to_wire(),
                {"sequence": 3, "changeKind": "removed", "delete": {"schemaId": "schema-note", "recordKey": "note:1"}},
            ],
            "resyncRequired": False,
        })
        self.assertEqual(response.last_sequence, 3)
        self.assertFalse(response.resync_required)
        self.assertEqual(response.entries[0].put["document"]["recordKey"], "note:1")
        self.assertIsInstance(response.entries[0].raw_document, CultNetRawDocumentRecord)
        self.assertEqual(response.entries[0].raw_document.record_key, "note:1")
        self.assertEqual(response.entries[1].delete_schema_id, "schema-note")
        self.assertEqual(response.entries[1].delete_record_key, "note:1")
        self.assertEqual(response.to_wire()["entries"][1]["delete"]["recordKey"], "note:1")
        self.assertIs(response.require_usable(), response)

        resync = CultNetShardLogResponse.from_wire({
            "schemaVersion": "cultnet.shard_log_response.v0",
            "messageId": "log-resync",
            "shardId": "notes",
            "shardEpoch": 2,
            "entries": [],
            "resyncRequired": True,
            "reason": "compacted",
            "compactedThrough": 12,
        })
        self.assertEqual(resync.last_sequence, 12)
        with self.assertRaisesRegex(ValueError, "compacted"):
            resync.require_usable()

    def test_cultnet_simulation_observation_helper_matches_schema_v0_shape(self) -> None:
        claim_hash = compute_simulation_claim_hash("frame:42", "subject:player-1", "hit")
        self.assertEqual(len(claim_hash), 64)
        message = simulation_observation(
            message_id="obs-1",
            witness_runtime_id="python-test",
            shard_id="interop",
            shard_epoch=1,
            frame=42,
            subject_id="player-1",
            claim_kind="hit",
            claim_hash=claim_hash,
            claim_summary="player-1 hit target-a",
            observed_at="2026-06-13T00:00:02Z",
        ).to_wire()
        self.assertEqual(message["schemaVersion"], "cultnet.simulation_observation.v0")
        self.assertEqual(message["messageId"], "obs-1")
        self.assertEqual(message["observation"]["witnessRuntimeId"], "python-test")
        self.assertEqual(message["observation"]["claimHash"], claim_hash)
        self.assertEqual(message["observation"]["weight"], 1.0)
        observation = CultNetSimulationObservation.from_wire(message)
        self.assertEqual(observation.witness_runtime_id, "python-test")
        self.assertEqual(observation.claim_summary, "player-1 hit target-a")
        self.assertEqual(observation.to_message_wire(message_id="obs-2")["observation"], message["observation"])

    def test_cultnet_witness_artifact_bundle_uses_csharp_slot_order(self) -> None:
        import msgpack  # type: ignore

        bundle = witness_artifact_bundle(
            bundle_id="bundle-1",
            witness_kind="interop-proof",
            captured_at="2026-06-13T00:00:03Z",
            subject={"documentType": "cultnet.interop-note", "subjectId": "note:python"},
            contracts=[{"role": "payload", "schemaId": "schema-a"}],
            artifacts=[{"role": "log", "uri": "cultcache://bundle-1/log", "mediaType": "text/plain"}],
            timing_witnesses=[{"stage": "roundtrip", "startedAt": "2026-06-13T00:00:03Z", "completedAt": "2026-06-13T00:00:04Z", "latencyMs": 1.0}],
            provenance={"pipelineId": "interop", "runId": "run-1", "runtimeId": "python-test"},
        )
        payload = encode_witness_artifact_bundle_payload(bundle)
        decoded = decode_witness_artifact_bundle_payload(payload)
        typed = CultNetWitnessArtifactBundle.from_wire(bundle)
        typed_payload = typed.to_payload()
        typed_decoded = CultNetWitnessArtifactBundle.from_payload(typed_payload)
        slots = msgpack.unpackb(payload, raw=False)

        self.assertEqual(payload, typed_payload)
        self.assertEqual(slots[0], "bundle-1")
        self.assertEqual(slots[1], "interop-proof")
        self.assertEqual(slots[2], "2026-06-13T00:00:03Z")
        self.assertEqual(slots[3]["subjectId"], "note:python")
        self.assertEqual(slots[4][0]["schemaId"], "schema-a")
        self.assertEqual(slots[5][0]["uri"], "cultcache://bundle-1/log")
        self.assertEqual(slots[6][0]["latencyMs"], 1.0)
        self.assertEqual(slots[7]["runtimeId"], "python-test")
        self.assertEqual(decoded["bundleId"], "bundle-1")
        self.assertEqual(decoded["witnessKind"], "interop-proof")
        self.assertEqual(decoded["subject"]["subjectId"], "note:python")
        self.assertEqual(decoded["contracts"][0]["schemaId"], "schema-a")
        self.assertEqual(decoded["artifacts"][0]["mediaType"], "text/plain")
        self.assertEqual(decoded["provenance"]["runtimeId"], "python-test")
        self.assertEqual(typed.bundle_id, "bundle-1")
        self.assertEqual(typed_decoded.to_wire(), bundle)

    def test_python_interop_peer_filters_shard_snapshot_by_logged_membership(self) -> None:
        runtime_id = f"python-interop-test-{uuid4().hex}"
        state = build_state(
            runtime_id=runtime_id,
            runtime_kind="python",
            display_name="Python Interop Test",
            agent_id="python-interop-test-agent",
            schema_path=str(Path("packages/cultnet-ts/integration/contracts/cultnet.interop-note.schema.json")),
        )
        binding = state.bindings["note"]
        logged_value = {
            "schemaVersion": "cultnet.interop_note.v0",
            "documentId": "note:logged",
            "authorRuntimeId": runtime_id,
            "title": "Logged",
            "body": "This record belongs to the shard log.",
            "tags": ["interop", "logged"],
        }
        unlogged_value = {
            **logged_value,
            "documentId": "note:unlogged",
            "title": "Unlogged",
            "body": "This record is in cache but not in the shard log.",
        }
        state.cache.put(binding.document, "note:logged", logged_value)
        state.cache.put(binding.document, "note:unlogged", unlogged_value)
        logged_record = {
            "schemaId": state.note_schema_id,
            "recordKey": "note:logged",
            "storedAt": "2026-06-14T00:00:00Z",
            "payloadEncoding": "messagepack",
            "payload": binding.document.encode_payload(logged_value),
        }
        append_shard_log_put(
            state,
            {"messageId": "logged-put"},
            logged_record,
        )

        response = raw_snapshot_response(
            state,
            {
                "schemaVersion": "cultnet.snapshot_request.v0",
                "messageId": "interop-shard-snapshot",
                "schemaIds": [state.note_schema_id],
                "shardId": state.shard_id,
            },
        )

        self.assertEqual(response["shardId"], state.shard_id)
        self.assertEqual(response["shardLogSequence"], 1)
        self.assertEqual([record["recordKey"] for record in response["documents"]], ["note:logged"])

    def test_cultnet_simulation_consensus_dedupes_witnesses_and_requires_quorum(self) -> None:
        from cultnet_py import CultNetSimulationConsensus

        consensus = CultNetSimulationConsensus(
            CultNetSimulationConsensusOptions(minimum_witnesses=2, quorum_ratio=1.0)
        )
        claim_hash = compute_simulation_claim_hash("hit", "alice", "bob", "frame:100")
        observations = [
            {
                "witnessRuntimeId": "watcher-1",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": claim_hash,
                "claimSummary": "alice shot bob first",
                "weight": 1.0,
            },
            {
                "witnessRuntimeId": "watcher-1",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": "stale-duplicate",
                "weight": 0.5,
            },
            {
                "witnessRuntimeId": "watcher-2",
                "shardId": "arena",
                "shardEpoch": 4,
                "frame": 100,
                "subjectId": "bob",
                "claimKind": "hit",
                "claimHash": claim_hash,
                "weight": 1.0,
            },
        ]

        candidates = consensus.build_candidates(observations)
        typed_candidates = consensus.build_candidate_objects([
            CultNetSimulationObservation.from_wire(observation)
            for observation in observations
        ])

        self.assertEqual(len(candidates), 1)
        self.assertEqual(candidates[0]["claimHash"], claim_hash)
        self.assertEqual(candidates[0]["witnessCount"], 2)
        self.assertEqual(candidates[0]["supportWeight"], 2.0)
        self.assertTrue(candidates[0]["hasQuorum"])
        self.assertEqual(len(typed_candidates), 1)
        self.assertIsInstance(typed_candidates[0], CultNetSimulationConsensusCandidate)
        self.assertEqual(typed_candidates[0].claim_hash, claim_hash)
        self.assertEqual(typed_candidates[0].to_wire(), candidates[0])
        self.assertEqual(CultNetSimulationConsensusCandidate.from_wire(candidates[0]), typed_candidates[0])


class CultNetRudpFragmentBoundTests(unittest.TestCase):
    def _pair(self) -> tuple[CultNetRudpSession, CultNetRudpSession]:
        sender = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=311, initial_sequence=1))
        receiver = CultNetRudpSession(CultNetRudpSessionOptions(connection_id=311, initial_sequence=100))
        sender.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 311, 90, 0, 0, "control"))
        receiver.receive(CultNetRudpPacket(CultNetRudpPacketType.ACCEPT, 311, 91, 0, 0, "control"))
        return sender, receiver

    def test_stranded_fragment_sets_are_bounded_and_evicted_oldest_first(self) -> None:
        sender, receiver = self._pair()
        receiver._max_pending_fragment_sets = 4

        # Eight payloads each lose their last fragment; the map must not grow past four.
        stranded = []
        for fill in range(1, 9):
            packets = sender.send_many("media", bytes([fill]) * 2500, max_fragment_bytes=1000)
            self.assertEqual(len(packets), 3)
            for packet in packets[:2]:
                self.assertEqual(receiver.receive(packet).delivered, ())
            stranded.append(packets[2])
        self.assertEqual(len(receiver._fragment_buffers), 4)
        self.assertEqual(receiver.fragment_sets_evicted, 4)

        # A complete payload still lands.
        delivered = []
        for packet in sender.send_many("media", bytes([9]) * 2500, max_fragment_bytes=1000):
            delivered.extend(receiver.receive(packet).delivered)
        self.assertEqual([frame.payload for frame in delivered], [bytes([9]) * 2500])
        self.assertEqual(receiver.fragment_sets_evicted, 5)

        # Oldest-first: set 8 still pends and completes late; set 1 was evicted.
        self.assertEqual(receiver.receive(stranded[7]).delivered[0].payload, bytes([8]) * 2500)
        self.assertEqual(receiver.receive(stranded[0]).delivered, ())


if __name__ == "__main__":
    unittest.main()
