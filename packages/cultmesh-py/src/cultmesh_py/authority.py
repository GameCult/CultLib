"""CultMesh authority route verification.

Port of `src/GameCult.Mesh/CultMeshAuthorityProof.cs` (the reference) and of the
route constructors in `CultMeshVerse.cs`. Rule for rule, in the same order, the
same input yields the same refusal text as `CultMeshAuthorityTrustPolicy.Verify`:
unsigned, channel protection, root lookup, validity window, signature.

Shared vectors: `contracts/cultmesh/authority-route-vectors.json` (C# written).
"""

from __future__ import annotations

import base64
import binascii
import re
import struct
from dataclasses import dataclass
from typing import Iterable, Mapping, Sequence

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import encode_dss_signature

ROUTE_DOMAIN = "gamecult.cultmesh.route-certificate.v1"
SESSION_DOMAIN = "gamecult.cultmesh.session-proof.v1"

AUTHENTICATED_REMOTE = "authenticated-remote"
LOCAL_DEVELOPMENT = "local-development"

# .NET `char.IsWhiteSpace`: Unicode Zs, Zl, Zp, U+0009-U+000D and U+0085.
# `str.strip()` is a different set (it also strips U+001C-U+001F).
_CSHARP_WHITESPACE = (
    "\u0009\u000a\u000b\u000c\u000d \u0085  "
    "           "
    "    　"
)


def trim_csharp(value: str) -> str:
    return value.strip(_CSHARP_WHITESPACE)


def is_blank_csharp(value: str | None) -> bool:
    return value is None or trim_csharp(value) == ""


def _require(value: str | None, name: str) -> str:
    if is_blank_csharp(value):
        raise ValueError(f"Value must be non-empty. (Parameter '{name}')")
    return trim_csharp(value)  # type: ignore[arg-type]


class AuthorityRouteRefusal(Exception):
    """A route or proof the consumer's trust policy refuses.

    Mirrors `CultMeshSessionException` carrying a `CultMeshSessionFailure` with
    reason `Authentication`: `reason` is always "authentication".
    """

    reason = "authentication"

    def __init__(self, message: str, endpoint: str = "") -> None:
        super().__init__(message)
        self.message = message
        self.endpoint = endpoint


def _b64_strict(value: str) -> bytes:
    # `Convert.FromBase64String`: standard alphabet, padded, whitespace ignored.
    stripped = re.sub(r"[ \t\r\n]", "", value)
    return base64.b64decode(stripped, validate=True)


@dataclass(frozen=True)
class P256PublicKey:
    key_id: str
    x: str
    y: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "key_id", _require(self.key_id, "keyId"))
        for name in ("x", "y"):
            value = _require(getattr(self, name), name)
            try:
                _b64_strict(value)
            except (binascii.Error, ValueError) as error:
                raise ValueError(f"Coordinate must be base64. (Parameter '{name}')") from error
            object.__setattr__(self, name, value)

    def verify(self, payload: bytes, signature_base64: str) -> bool:
        """IEEE P1363 (r || s) ECDSA-SHA256 verification; False on any malformation."""
        try:
            x, y = _b64_strict(self.x), _b64_strict(self.y)
            signature = _b64_strict(signature_base64)
            if len(x) != 32 or len(y) != 32 or len(signature) != 64:
                return False
            key = ec.EllipticCurvePublicNumbers(
                int.from_bytes(x, "big"), int.from_bytes(y, "big"), ec.SECP256R1()
            ).public_key()
            der = encode_dss_signature(
                int.from_bytes(signature[:32], "big"), int.from_bytes(signature[32:], "big")
            )
            key.verify(der, payload, ec.ECDSA(hashes.SHA256()))
            return True
        except (InvalidSignature, ValueError, binascii.Error):
            return False


@dataclass(frozen=True)
class RouteCertificate:
    provider_key: P256PublicKey
    odin_key_id: str
    issued_at_unix_milliseconds: int
    expires_at_unix_milliseconds: int
    signature: str = ""

    def __post_init__(self) -> None:
        if is_blank_csharp(self.odin_key_id):
            raise ValueError("Odin key identity is required. (Parameter 'odinKeyId')")
        object.__setattr__(self, "odin_key_id", trim_csharp(self.odin_key_id))
        if self.issued_at_unix_milliseconds < 0:
            raise ValueError("issuedAtUnixMilliseconds must not be negative.")
        if self.expires_at_unix_milliseconds <= self.issued_at_unix_milliseconds:
            raise ValueError("expiresAtUnixMilliseconds must be after issuedAtUnixMilliseconds.")
        object.__setattr__(self, "signature", trim_csharp(self.signature or ""))


@dataclass(frozen=True)
class AuthorityRoute:
    """`CultMeshAuthorityRoute`: construction cleans every field as the C# does."""

    authority_runtime_id: str
    endpoint: str
    protocol_ids: Sequence[str] = ()
    priority: int = 0
    generation: str | None = None
    certificate: RouteCertificate | None = None

    def __post_init__(self) -> None:
        runtime_id = _require(self.authority_runtime_id, "authorityRuntimeId")
        endpoint = _require(self.endpoint, "endpoint")
        if self.priority < 0:
            raise ValueError("priority must not be negative.")
        object.__setattr__(self, "authority_runtime_id", runtime_id)
        object.__setattr__(self, "endpoint", endpoint)
        object.__setattr__(
            self,
            "protocol_ids",
            tuple(
                sorted(
                    {trim_csharp(v) for v in self.protocol_ids if not is_blank_csharp(v)},
                    key=lambda v: v.encode("utf-16-be"),  # StringComparer.Ordinal is UTF-16 code-unit order
                )
            ),
        )
        object.__setattr__(
            self,
            "generation",
            f"{runtime_id}@{endpoint}" if is_blank_csharp(self.generation) else trim_csharp(self.generation),  # type: ignore[arg-type]
        )


@dataclass(frozen=True)
class SessionOpen:
    """The fields of `CultMeshSessionOpenMessage` the session proof covers."""

    client_nonce: str | None = None
    message_id: str | None = None
    source_runtime_id: str | None = None
    verse_id: str | None = None
    authority_runtime_id: str | None = None
    protocol_id: str | None = None
    route_generation: str | None = None


@dataclass(frozen=True)
class SessionAccepted:
    client_nonce: str | None
    provider_key_id: str | None
    provider_signature: str | None


@dataclass(frozen=True)
class VerifiedRoute:
    route: AuthorityRoute
    provider_key: P256PublicKey | None
    is_local_development: bool


def canonical_fields(*values: str | None) -> bytes:
    out = bytearray()
    for value in values:
        encoded = (value or "").encode("utf-8")
        out += struct.pack(">I", len(encoded)) + encoded
    return bytes(out)


def canonical_route(verse_id: str, route: AuthorityRoute) -> bytes:
    certificate = route.certificate
    if certificate is None:
        raise ValueError("Route certificate is required.")
    return canonical_fields(
        ROUTE_DOMAIN,
        verse_id,
        route.authority_runtime_id,
        route.endpoint,
        "\u001f".join(route.protocol_ids),  # already ordinally sorted; Python sorts by code point
        str(route.priority),
        route.generation,
        certificate.provider_key.key_id,
        certificate.provider_key.x,
        certificate.provider_key.y,
        certificate.odin_key_id,
        str(certificate.issued_at_unix_milliseconds),
        str(certificate.expires_at_unix_milliseconds),
    )


def canonical_session(request: SessionOpen, endpoint: str) -> bytes:
    return canonical_fields(
        SESSION_DOMAIN,
        request.client_nonce,
        request.message_id,
        request.source_runtime_id,
        request.verse_id,
        request.authority_runtime_id,
        request.protocol_id,
        endpoint,
        request.route_generation,
    )


# `System.Uri` refuses spellings WHATWG parsers repair; a lenient parse could call
# a refused endpoint loopback or protected. Same shape rule as the TypeScript port.
_AUTHORITY = re.compile(r"^[A-Za-z][A-Za-z0-9+\-.]*://([^/?#]+)")


def _split_endpoint(value: str) -> tuple[str, str] | None:
    """Return (scheme, lowercase host) or None where System.Uri would refuse."""
    raw = re.sub(r"^[\x00-\x20]+|[\x00-\x20]+$", "", value)
    if "\\" in raw:
        return None
    shape = _AUTHORITY.match(raw)
    if not shape:
        return None
    authority = shape.group(1)
    after_user = authority[authority.rfind("@") + 1 :]
    if after_user.startswith("["):
        host = after_user[: after_user.find("]") + 1]
    else:
        host = after_user.split(":")[0]
    if not host or re.search(r"[^!-~]", host) or host.endswith("."):
        return None
    return raw.split("://", 1)[0].lower(), host.strip("[]").lower()


def is_loopback_endpoint(endpoint: str) -> bool:
    parts = _split_endpoint(endpoint)
    if parts is None:
        return False
    host = parts[1]
    return host in ("localhost", "loopback", "::1", "::ffff:127.0.0.1") or bool(
        re.fullmatch(r"127\.\d{1,3}\.\d{1,3}\.\d{1,3}", host)
    )


def is_protected_endpoint(endpoint: str) -> bool:
    parts = _split_endpoint(endpoint)
    return parts is not None and (parts[0] in ("wss", "https") or "quic" in parts[0])


class AuthorityTrustPolicy:
    """Consumer-owned policy. Fails closed unless a route is valid under it."""

    def __init__(self, mode: str, odin_roots: Iterable[P256PublicKey] = ()) -> None:
        if mode not in (AUTHENTICATED_REMOTE, LOCAL_DEVELOPMENT):
            raise ValueError(f"Unknown trust mode '{mode}'.")
        self.mode = mode
        self._roots: dict[str, P256PublicKey] = {}
        for root in odin_roots:
            if root.key_id in self._roots:
                raise ValueError(f"Odin root key id '{root.key_id}' is listed more than once in the trust policy.")
            self._roots[root.key_id] = root

    def validate(self, verse_id: str, route: AuthorityRoute, now_unix_milliseconds: int) -> None:
        self.verify(verse_id, route, now_unix_milliseconds)

    def verify(self, verse_id: str, route: AuthorityRoute, now_unix_milliseconds: int) -> VerifiedRoute:
        local = self.mode == LOCAL_DEVELOPMENT and is_loopback_endpoint(route.endpoint)

        def refuse(message: str) -> AuthorityRouteRefusal:
            return AuthorityRouteRefusal(message, route.endpoint)

        certificate = route.certificate
        if certificate is None or is_blank_csharp(certificate.signature):
            if local:
                return VerifiedRoute(route, None, True)
            raise refuse("Remote CultMesh routes require an Odin-signed authority certificate.")
        if not is_protected_endpoint(route.endpoint) and not local:
            raise refuse("Authenticated remote CultMesh routes require TLS or QUIC channel protection.")
        root = self._roots.get(certificate.odin_key_id)
        if root is None:
            raise refuse(f"Odin key '{certificate.odin_key_id}' is not trusted by this consumer.")
        if not (certificate.issued_at_unix_milliseconds <= now_unix_milliseconds < certificate.expires_at_unix_milliseconds):
            raise refuse("The Odin route certificate is not currently valid.")
        try:
            signature = _b64_strict(certificate.signature)
        except (binascii.Error, ValueError):
            raise refuse("The Odin route signature is not base64.") from None
        if len(signature) != 64:
            raise refuse("The Odin route signature is not IEEE P1363 P-256.")
        if not root.verify(canonical_route(verse_id, route), certificate.signature):
            raise refuse("The Odin route certificate signature is invalid.")
        return VerifiedRoute(route, certificate.provider_key, False)


def verify_session_proof(
    request: SessionOpen,
    response: SessionAccepted,
    verse_id: str,
    route: AuthorityRoute,
    trust: AuthorityTrustPolicy,
    now_unix_milliseconds: int,
) -> bool:
    """Verifies the Odin route certificate (raising) and the provider nonce proof."""
    verified = trust.verify(verse_id, route, now_unix_milliseconds)
    if verified.is_local_development:
        return True
    key = verified.provider_key
    if (
        key is None
        or response.provider_key_id != key.key_id
        or response.client_nonce != request.client_nonce
        or is_blank_csharp(response.provider_signature)
    ):
        return False
    return key.verify(canonical_session(request, route.endpoint), response.provider_signature)  # type: ignore[arg-type]
