from __future__ import annotations

import base64
import json
import os
import threading
from pathlib import Path
from typing import Any

from .backing_store import CultCacheEnvelope


class JsonLinesBackingStore:
    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self._lock = threading.RLock()

    def pull_all(self) -> list[CultCacheEnvelope]:
        with self._lock:
            if not self.path.exists():
                return []
            envelopes: list[CultCacheEnvelope] = []
            for line_number, line in enumerate(self.path.read_text(encoding="utf-8").splitlines(), start=1):
                if not line.strip():
                    continue
                raw = json.loads(line)
                try:
                    payload = base64.b64decode(raw["payload"])
                    envelopes.append(
                        CultCacheEnvelope(
                            key=raw["key"],
                            type=raw["type"],
                            payload=payload,
                            stored_at=raw.get("stored_at", raw.get("storedAt")),
                        )
                    )
                except KeyError as exc:
                    raise ValueError(f"Malformed CultCache JSONL envelope at {self.path}:{line_number}: missing {exc}") from exc
            return envelopes

    def push(self, envelope: CultCacheEnvelope) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            for envelope in envelopes:
                existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def delete(self, type: str, key: str) -> None:
        with self._lock:
            existing = [item for item in self.pull_all() if not (item.type == type and item.key == key)]
            self._replace_all(existing)

    def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        lines = []
        for envelope in sorted(envelopes, key=lambda item: (item.type, item.key)):
            lines.append(json.dumps({
                "key": envelope.key,
                "type": envelope.type,
                "payload": base64.b64encode(envelope.payload).decode("ascii"),
                "stored_at": envelope.stored_at,
            }, ensure_ascii=False, sort_keys=True))
        temp.write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
        temp.replace(self.path)


class SingleFileMessagePackBackingStore:
    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = Path(path)
        self._lock = threading.RLock()

    def pull_all(self) -> list[CultCacheEnvelope]:
        msgpack = self._msgpack()
        with self._lock:
            if not self.path.exists():
                return []
            raw_items = msgpack.unpackb(self.path.read_bytes(), raw=False)
            return [
                CultCacheEnvelope(
                    key=item["key"],
                    type=item["type"],
                    payload=item["payload"],
                    stored_at=item.get("storedAt", item.get("stored_at")),
                )
                for item in raw_items
            ]

    def push(self, envelope: CultCacheEnvelope) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def push_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        with self._lock:
            existing = {(item.type, item.key): item for item in self.pull_all()}
            for envelope in envelopes:
                existing[(envelope.type, envelope.key)] = envelope
            self._replace_all(list(existing.values()))

    def delete(self, type: str, key: str) -> None:
        with self._lock:
            existing = [item for item in self.pull_all() if not (item.type == type and item.key == key)]
            self._replace_all(existing)

    def _replace_all(self, envelopes: list[CultCacheEnvelope]) -> None:
        msgpack = self._msgpack()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix(self.path.suffix + ".tmp")
        raw_items: list[dict[str, Any]] = [
            {
                "key": envelope.key,
                "type": envelope.type,
                "payload": envelope.payload,
                "storedAt": envelope.stored_at,
            }
            for envelope in sorted(envelopes, key=lambda item: (item.type, item.key))
        ]
        temp.write_bytes(msgpack.packb(raw_items, use_bin_type=True))
        temp.replace(self.path)

    @staticmethod
    def _msgpack() -> Any:
        try:
            import msgpack  # type: ignore
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "SingleFileMessagePackBackingStore requires the optional 'msgpack' dependency. "
                "Install with: python -m pip install cultcache-py[msgpack]"
            ) from exc
        return msgpack
