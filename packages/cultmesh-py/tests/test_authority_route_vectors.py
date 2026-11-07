"""Judges `contracts/cultmesh/authority-route-vectors*.json` in Python.

`authority-route-vectors.json` is written by the C# reference (signatures are
captured C# bytes); the `.ts-signed.json` file is signed by TypeScript over awkward
inputs. Each negative case fails when the matching check is removed from
`cultmesh_py.authority`.
"""

from __future__ import annotations

import base64
import json
import unittest
from dataclasses import replace
from pathlib import Path

from cryptography.hazmat.primitives.asymmetric import ec

from cultmesh_py.authority import (
    AUTHENTICATED_REMOTE,
    LOCAL_DEVELOPMENT,
    AuthorityRoute,
    AuthorityRouteRefusal,
    AuthorityTrustPolicy,
    P256PublicKey,
    RouteCertificate,
    SessionAccepted,
    SessionOpen,
    canonical_route,
    verify_session_proof,
)

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts" / "cultmesh"


def _key(element: dict) -> P256PublicKey:
    return P256PublicKey(element["keyId"], element["x"], element["y"])


class Vector:
    def __init__(self, name: str) -> None:
        doc = json.loads((CONTRACTS / name).read_text(encoding="utf-8"))
        self.now = doc["nowUnixMilliseconds"]
        self.root = _key(doc["odinRoot"])
        r = doc["route"]
        self.verse_id = r["verseId"]
        c = r["certificate"]
        self.certificate = RouteCertificate(
            _key(c["providerKey"]), c["odinKeyId"], c["issuedAtUnixMilliseconds"],
            c["expiresAtUnixMilliseconds"], c["signature"],
        )
        self.route = AuthorityRoute(
            r["authorityRuntimeId"], r["endpoint"], r["protocolIds"], r["priority"],
            r["generation"], self.certificate,
        )
        p = doc["sessionProof"]
        q = p["request"]
        self.request = SessionOpen(
            q["clientNonce"], q["messageId"], q["sourceRuntimeId"], q["verseId"],
            q["authorityRuntimeId"], q["protocolId"], q["routeGeneration"],
        )
        self.accepted = SessionAccepted(q["clientNonce"], p["providerKeyId"], p["providerSignature"])
        self.trust = AuthorityTrustPolicy(AUTHENTICATED_REMOTE, [self.root])

    def with_certificate(self, **changes) -> AuthorityRoute:
        return replace(self.route, certificate=replace(self.certificate, **changes))


def refusal(test: unittest.TestCase, fn) -> str:
    with test.assertRaises(AuthorityRouteRefusal) as raised:
        fn()
    test.assertEqual("authentication", raised.exception.reason)
    return raised.exception.message


class AuthorityRouteVectorTests(unittest.TestCase):
    def test_cs_written_vector_verifies_route_and_session_proof(self) -> None:
        v = Vector("authority-route-vectors.json")
        v.trust.validate(v.verse_id, v.route, v.now)
        self.assertTrue(verify_session_proof(v.request, v.accepted, v.verse_id, v.route, v.trust, v.now))

    def test_ts_signed_vector_verifies_and_tamper_is_refused(self) -> None:
        v = Vector("authority-route-vectors.ts-signed.json")
        v.trust.validate(v.verse_id, v.route, v.now)
        self.assertTrue(verify_session_proof(v.request, v.accepted, v.verse_id, v.route, v.trust, v.now))
        tampered = replace(v.route, priority=v.route.priority + 1)
        self.assertEqual(
            "The Odin route certificate signature is invalid.",
            refusal(self, lambda: v.trust.validate(v.verse_id, tampered, v.now)),
        )

    def test_canonical_route_is_length_prefixed_and_sorted(self) -> None:
        v = Vector("authority-route-vectors.json")
        data = canonical_route(v.verse_id, v.route)
        self.assertTrue(data.startswith(b"\x00\x00\x00\x26gamecult.cultmesh.route-certificate.v1\x00\x00\x00\x08aetheria"))
        self.assertIn(b"cultmesh.content.v1\x1fcultmesh.documents.v1", data)

    def test_tampered_payload_fields_are_refused(self) -> None:
        v = Vector("authority-route-vectors.json")
        invalid = "The Odin route certificate signature is invalid."
        for tampered, verse in (
            (replace(v.route, priority=8), v.verse_id),
            (replace(v.route, endpoint="wss://evil.example/mesh"), v.verse_id),
            (replace(v.route, protocol_ids=("cultmesh.documents.v1",)), v.verse_id),
            (replace(v.route, generation="other"), v.verse_id),
            (v.with_certificate(expires_at_unix_milliseconds=v.certificate.expires_at_unix_milliseconds + 1), v.verse_id),
            (v.route, "other-verse"),
        ):
            self.assertEqual(invalid, refusal(self, lambda: v.trust.validate(verse, tampered, v.now)))

    def test_wrong_root_key_is_refused(self) -> None:
        v = Vector("authority-route-vectors.json")
        numbers = ec.generate_private_key(ec.SECP256R1()).public_key().public_numbers()
        impostor = P256PublicKey(
            v.root.key_id,
            base64.b64encode(numbers.x.to_bytes(32, "big")).decode(),
            base64.b64encode(numbers.y.to_bytes(32, "big")).decode(),
        )
        trust = AuthorityTrustPolicy(AUTHENTICATED_REMOTE, [impostor])
        self.assertEqual(
            "The Odin route certificate signature is invalid.",
            refusal(self, lambda: trust.validate(v.verse_id, v.route, v.now)),
        )

    def test_untrusted_odin_key_id_is_refused(self) -> None:
        v = Vector("authority-route-vectors.json")
        trust = AuthorityTrustPolicy(AUTHENTICATED_REMOTE, [replace(v.root, key_id="someone-else")])
        self.assertEqual(
            "Odin key 'odin-vector' is not trusted by this consumer.",
            refusal(self, lambda: trust.validate(v.verse_id, v.route, v.now)),
        )

    def test_expired_and_not_yet_valid_are_refused(self) -> None:
        v = Vector("authority-route-vectors.json")
        window = "The Odin route certificate is not currently valid."
        expires = v.certificate.expires_at_unix_milliseconds
        issued = v.certificate.issued_at_unix_milliseconds
        v.trust.validate(v.verse_id, v.route, expires - 1)
        v.trust.validate(v.verse_id, v.route, issued)
        self.assertEqual(window, refusal(self, lambda: v.trust.validate(v.verse_id, v.route, expires)))
        self.assertEqual(window, refusal(self, lambda: v.trust.validate(v.verse_id, v.route, issued - 1)))

    def test_unsigned_and_unprotected_routes_are_refused(self) -> None:
        v = Vector("authority-route-vectors.json")
        unsigned = replace(v.route, certificate=None)
        self.assertEqual(
            "Remote CultMesh routes require an Odin-signed authority certificate.",
            refusal(self, lambda: v.trust.validate(v.verse_id, unsigned, v.now)),
        )
        blank = v.with_certificate(signature="  ")
        self.assertEqual(
            "Remote CultMesh routes require an Odin-signed authority certificate.",
            refusal(self, lambda: v.trust.validate(v.verse_id, blank, v.now)),
        )
        plain = replace(v.route, endpoint="ws://provider.example/mesh")
        self.assertEqual(
            "Authenticated remote CultMesh routes require TLS or QUIC channel protection.",
            refusal(self, lambda: v.trust.validate(v.verse_id, plain, v.now)),
        )

    def test_local_development_admits_only_unsigned_loopback(self) -> None:
        v = Vector("authority-route-vectors.json")
        dev = AuthorityTrustPolicy(LOCAL_DEVELOPMENT, [])
        loop = AuthorityRoute("a", "ws://127.0.0.1:9/mesh")
        dev.validate(v.verse_id, loop, v.now)
        self.assertTrue(verify_session_proof(v.request, v.accepted, v.verse_id, loop, dev, v.now))
        for endpoint in ("ws://provider.example/mesh", "ws://127.0.0.1./mesh", "ws:127.0.0.1"):
            refusal(self, lambda e=endpoint: dev.validate(v.verse_id, AuthorityRoute("a", e), v.now))
        authenticated = AuthorityTrustPolicy(AUTHENTICATED_REMOTE, [])
        refusal(self, lambda: authenticated.validate(v.verse_id, loop, v.now))

    def test_malformed_signatures_get_their_own_refusals(self) -> None:
        v = Vector("authority-route-vectors.json")
        not_base64 = v.with_certificate(signature="!!not base64!!")
        self.assertEqual(
            "The Odin route signature is not base64.",
            refusal(self, lambda: v.trust.validate(v.verse_id, not_base64, v.now)),
        )
        short = v.with_certificate(signature=base64.b64encode(b"x" * 63).decode())
        self.assertEqual(
            "The Odin route signature is not IEEE P1363 P-256.",
            refusal(self, lambda: v.trust.validate(v.verse_id, short, v.now)),
        )

    def test_high_s_signature_verifies_as_it_does_in_dotnet(self) -> None:
        v = Vector("authority-route-vectors.json")
        order = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
        raw = base64.b64decode(v.certificate.signature)
        s = int.from_bytes(raw[32:], "big")
        flipped = raw[:32] + (order - s).to_bytes(32, "big")
        high = v.with_certificate(signature=base64.b64encode(flipped).decode())
        v.trust.validate(v.verse_id, high, v.now)

    def test_session_proof_rejects_replay_wrong_key_and_forgery(self) -> None:
        v = Vector("authority-route-vectors.json")
        replayed = replace(v.request, client_nonce="replayed")
        accepted = replace(v.accepted, client_nonce="replayed")
        self.assertFalse(verify_session_proof(replayed, accepted, v.verse_id, v.route, v.trust, v.now))
        self.assertFalse(verify_session_proof(
            v.request, replace(v.accepted, provider_key_id="other"), v.verse_id, v.route, v.trust, v.now))
        self.assertFalse(verify_session_proof(
            v.request, replace(v.accepted, client_nonce="other"), v.verse_id, v.route, v.trust, v.now))
        self.assertFalse(verify_session_proof(
            v.request, replace(v.accepted, provider_signature=""), v.verse_id, v.route, v.trust, v.now))
        raw = bytearray(base64.b64decode(v.accepted.provider_signature))
        raw[0] ^= 1
        forged = replace(v.accepted, provider_signature=base64.b64encode(bytes(raw)).decode())
        self.assertFalse(verify_session_proof(v.request, forged, v.verse_id, v.route, v.trust, v.now))
        moved = replace(v.request, protocol_id="cultmesh.content.v1")
        self.assertFalse(verify_session_proof(moved, v.accepted, v.verse_id, v.route, v.trust, v.now))
        # An expired route fails closed even with a good proof.
        refusal(self, lambda: verify_session_proof(
            v.request, v.accepted, v.verse_id, v.route, v.trust, v.certificate.expires_at_unix_milliseconds))


if __name__ == "__main__":
    unittest.main()
