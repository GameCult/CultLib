from __future__ import annotations

import base64
import hashlib
import hmac
import os
import time
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Mapping
from uuid import UUID


NONCE_LENGTH = 12
TAG_LENGTH = 16


@dataclass(frozen=True)
class CultNetClientSecurityOptions:
    connection_key: str

    def __post_init__(self) -> None:
        if not self.connection_key or not self.connection_key.strip():
            raise ValueError("Connection key must be provided.")

    @staticmethod
    def development() -> "CultNetClientSecurityOptions":
        return CultNetClientSecurityOptions("gamecult-dev-connection-key")

    def encryption_key(self) -> bytes:
        return _sha256(self.connection_key.encode("utf-8"))


@dataclass(frozen=True)
class CultNetServerSecurityOptions(CultNetClientSecurityOptions):
    CONNECTION_KEY_ENVIRONMENT_VARIABLE = "GAMECULT_CONNECTION_KEY"
    SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE = "GAMECULT_SESSION_SIGNING_SECRET"

    session_signing_secret: str
    is_development: bool = False

    def __post_init__(self) -> None:
        super().__post_init__()
        if not self.session_signing_secret or not self.session_signing_secret.strip():
            raise ValueError("Session signing secret must be provided.")

    @staticmethod
    def development() -> "CultNetServerSecurityOptions":
        return CultNetServerSecurityOptions(
            "gamecult-dev-connection-key",
            "gamecult-dev-session-signing-secret",
            True,
        )

    @staticmethod
    def from_environment(
        environment: Mapping[str, str | None] | None = None,
        *,
        allow_development_defaults: bool = False,
    ) -> "CultNetServerSecurityOptions":
        source = os.environ if environment is None else environment
        connection_key = source.get(CultNetServerSecurityOptions.CONNECTION_KEY_ENVIRONMENT_VARIABLE)
        session_signing_secret = source.get(CultNetServerSecurityOptions.SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE)
        missing_connection_key = not connection_key or not connection_key.strip()
        missing_session_signing_secret = not session_signing_secret or not session_signing_secret.strip()
        if missing_connection_key and missing_session_signing_secret:
            if allow_development_defaults:
                return CultNetServerSecurityOptions.development()
            raise ValueError(
                "Server security configuration is not configured. Set "
                "GAMECULT_CONNECTION_KEY and GAMECULT_SESSION_SIGNING_SECRET, "
                "or explicitly use CultNetServerSecurityOptions.development() for local development."
            )
        if missing_connection_key or missing_session_signing_secret:
            missing = [
                name
                for name, is_missing in (
                    (CultNetServerSecurityOptions.CONNECTION_KEY_ENVIRONMENT_VARIABLE, missing_connection_key),
                    (CultNetServerSecurityOptions.SESSION_SIGNING_SECRET_ENVIRONMENT_VARIABLE, missing_session_signing_secret),
                )
                if is_missing
            ]
            raise ValueError(f"Server security configuration is partially configured. Missing: {', '.join(missing)}.")
        return CultNetServerSecurityOptions(str(connection_key), str(session_signing_secret))

    def session_signing_key(self) -> bytes:
        return _sha256(self.session_signing_secret.encode("utf-8"))

    def to_client_options(self) -> CultNetClientSecurityOptions:
        return CultNetClientSecurityOptions(self.connection_key)


@dataclass(frozen=True)
class ValidatedCultNetSessionToken:
    user_id: UUID
    expires_at_utc: datetime
    session_version: int = 0


class CultNetSecret:
    @staticmethod
    def new_nonce() -> bytes:
        return os.urandom(NONCE_LENGTH)

    @staticmethod
    def encrypt_string(
        value: str | None,
        nonce: bytes,
        options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
    ) -> bytes | None:
        if not value:
            return None
        return CultNetSecret.encrypt_bytes(value.encode("utf-8"), nonce, options)

    @staticmethod
    def decrypt_string(
        encrypted: bytes | None,
        nonce: bytes | None,
        options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
    ) -> str | None:
        if encrypted is None or nonce is None:
            return None
        return CultNetSecret.decrypt_bytes(encrypted, nonce, options).decode("utf-8")

    @staticmethod
    def encrypt_bytes(
        value: bytes,
        nonce: bytes,
        options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
    ) -> bytes:
        aesgcm = _create_aesgcm(options.encryption_key())
        ciphertext_and_tag = aesgcm.encrypt(_validate_nonce(nonce), value, None)
        ciphertext = ciphertext_and_tag[:-TAG_LENGTH]
        tag = ciphertext_and_tag[-TAG_LENGTH:]
        return tag + ciphertext

    @staticmethod
    def decrypt_bytes(
        encrypted: bytes,
        nonce: bytes,
        options: CultNetClientSecurityOptions | CultNetServerSecurityOptions,
    ) -> bytes:
        if len(encrypted) < TAG_LENGTH:
            raise ValueError("Invalid encrypted data.")
        aesgcm = _create_aesgcm(options.encryption_key())
        tag = encrypted[:TAG_LENGTH]
        ciphertext = encrypted[TAG_LENGTH:]
        return aesgcm.decrypt(_validate_nonce(nonce), ciphertext + tag, None)

    @staticmethod
    def create_session_token(
        user_id: str | UUID,
        expires_at_utc: datetime | int | float,
        options: CultNetServerSecurityOptions,
        *,
        session_version: int = 0,
    ) -> str:
        parsed_user_id = _parse_user_id(user_id)
        expires_at_seconds = _expires_at_seconds(expires_at_utc)
        payload = f"{parsed_user_id.hex}|{expires_at_seconds}|{int(session_version)}".encode("utf-8")
        signature = hmac.new(options.session_signing_key(), payload, hashlib.sha256).digest()
        return f"{CultNetSecret.to_base64url(payload)}.{CultNetSecret.to_base64url(signature)}"

    @staticmethod
    def try_validate_session_token(
        token: str | None,
        options: CultNetServerSecurityOptions,
        *,
        now_utc: datetime | int | float | None = None,
    ) -> ValidatedCultNetSessionToken | None:
        if not token or not token.strip():
            return None
        parts = token.split(".")
        if len(parts) != 2:
            return None
        try:
            payload = CultNetSecret.from_base64url(parts[0])
            signature = CultNetSecret.from_base64url(parts[1])
        except ValueError:
            return None
        expected = hmac.new(options.session_signing_key(), payload, hashlib.sha256).digest()
        if not hmac.compare_digest(signature, expected):
            return None
        try:
            decoded = payload.decode("utf-8")
            payload_parts = decoded.split("|")
            if len(payload_parts) not in (2, 3):
                return None
            user_id = UUID(payload_parts[0])
            expires_at_seconds = int(payload_parts[1])
            session_version = int(payload_parts[2]) if len(payload_parts) == 3 else 0
        except (ValueError, UnicodeDecodeError):
            return None
        if expires_at_seconds <= _now_seconds(now_utc):
            return None
        return ValidatedCultNetSessionToken(
            user_id=user_id,
            expires_at_utc=datetime.fromtimestamp(expires_at_seconds, UTC),
            session_version=session_version,
        )

    @staticmethod
    def to_base64url(value: bytes) -> str:
        return base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")

    @staticmethod
    def from_base64url(value: str) -> bytes:
        try:
            return base64.urlsafe_b64decode(value + "=" * ((4 - len(value) % 4) % 4))
        except Exception as exc:
            raise ValueError("Invalid base64url data.") from exc


def _sha256(value: bytes) -> bytes:
    return hashlib.sha256(value).digest()


def _validate_nonce(nonce: bytes) -> bytes:
    if len(nonce) != NONCE_LENGTH:
        raise ValueError("Invalid nonce.")
    return nonce


def _parse_user_id(user_id: str | UUID) -> UUID:
    return user_id if isinstance(user_id, UUID) else UUID(str(user_id))


def _expires_at_seconds(expires_at_utc: datetime | int | float) -> int:
    if isinstance(expires_at_utc, datetime):
        value = expires_at_utc
        if value.tzinfo is None:
            value = value.replace(tzinfo=UTC)
        return int(value.timestamp())
    return int(expires_at_utc)


def _now_seconds(now_utc: datetime | int | float | None) -> int:
    if now_utc is None:
        return int(time.time())
    return _expires_at_seconds(now_utc)


def _create_aesgcm(key: bytes) -> object:
    try:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    except ModuleNotFoundError as exc:
        raise ImportError(
            "AES-GCM encryption helpers require the optional 'cryptography' dependency."
        ) from exc
    return AESGCM(key)
