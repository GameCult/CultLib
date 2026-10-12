from __future__ import annotations

import argparse
import json
from typing import Any

from . import CultCache, SingleFileMessagePackBackingStore, database_entry_field, define_database_entry_type, leaves as t

INTEROP_SCHEMA_VERSION = "cultcache.interop_note.v1"

interop_note_document = define_database_entry_type(
    "cultcache.interop-note",
    [
        database_entry_field("schemaVersion", 0, codec=t.nullable(t.string)),
        database_entry_field("documentId", 1, codec=t.nullable(t.string)),
        database_entry_field("authorRuntimeId", 2, codec=t.nullable(t.string)),
        database_entry_field("title", 3, codec=t.nullable(t.string)),
        database_entry_field("body", 4, codec=t.nullable(t.string)),
        database_entry_field("tags", 5, default=list, codec=t.nullable(t.array(t.string))),
    ],
    name="documentId",
    schema_id="cultcache.interop-note",
    schema_name="cultcache.interop-note",
    schema_version=INTEROP_SCHEMA_VERSION,
    content_hash="cultcache.interop-note",
    compatible_schema_ids=("cultcache.interop-note",),
)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="cultcache-py-interop")
    parser.add_argument("mode", choices=("write", "read"))
    parser.add_argument("--file", required=True)
    parser.add_argument("--runtime-id")
    args = parser.parse_args(argv)

    if args.mode == "write":
        if not args.runtime_id:
            parser.error("write requires --runtime-id")
        note = write_note(args.file, args.runtime_id)
    else:
        note = read_note(args.file)
    print(json.dumps(note, separators=(",", ":")))
    return 0


def write_note(file: str, runtime_id: str) -> dict[str, Any]:
    cache = build_cache(file)
    cache.pull_all_backing_stores()
    note = {
        "schemaVersion": INTEROP_SCHEMA_VERSION,
        "documentId": f"note:{runtime_id}",
        "authorRuntimeId": runtime_id,
        "title": f"{runtime_id} wrote a CultCache note",
        "body": "The v1 store format is the contract.",
        "tags": [runtime_id, "python", "interop"],
    }
    cache.put(interop_note_document, note["documentId"], note)
    return note


def read_note(file: str) -> dict[str, Any]:
    cache = build_cache(file)
    cache.pull_all_backing_stores()
    notes = cache.get_all(interop_note_document)
    if not notes:
        raise RuntimeError("No cultcache.interop-note records found.")
    return notes[0]


def build_cache(file: str) -> CultCache:
    return (
        CultCache.builder()
        .register_document_type(interop_note_document)
        .add_generic_store(SingleFileMessagePackBackingStore(file))
        .build()
    )


if __name__ == "__main__":
    raise SystemExit(main())
