from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class CultNetClientAuthorityScope:
    owner_runtime_id: str
    schema_ids: tuple[str, ...] = ()
    key_prefix: str | None = None

    def __post_init__(self) -> None:
        if not self.owner_runtime_id.strip():
            raise ValueError("owner_runtime_id must be non-empty")

    def matches(self, runtime_id: str, schema_id: str, record_key: str) -> bool:
        return (
            self.owner_runtime_id == runtime_id
            and (not self.schema_ids or schema_id in self.schema_ids)
            and (self.key_prefix is None or record_key.startswith(self.key_prefix))
        )
