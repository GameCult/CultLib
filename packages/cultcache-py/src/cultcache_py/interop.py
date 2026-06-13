from __future__ import annotations

import argparse
import json
from typing import Any

import msgpack

from . import CultCache, CultCacheSchemaCatalogMember, SingleFileMessagePackBackingStore, define_document_type

INTEROP_SCHEMA_VERSION = "cultcache.interop_note.v1"

interop_note_document = define_document_type(
    "cultcache.interop-note",
    encode=lambda note: [
        note["schemaVersion"],
        note["documentId"],
        note["authorRuntimeId"],
        note["title"],
        note["body"],
        note["tags"],
    ],
    decode=lambda slots: {
        "schemaVersion": slots[0],
        "documentId": slots[1],
        "authorRuntimeId": slots[2],
        "title": slots[3],
        "body": slots[4],
        "tags": slots[5] if len(slots) > 5 and slots[5] is not None else [],
    },
    name="documentId",
    payload_encoder=lambda value: msgpack.packb(value, use_bin_type=True),
    payload_decoder=lambda payload: msgpack.unpackb(payload, raw=False),
    schema_id="cultcache.interop-note",
    schema_name="cultcache.interop-note",
    schema_version=INTEROP_SCHEMA_VERSION,
    content_hash="cultcache.interop-note",
    canonical_schema_json='{"schemaName":"cultcache.interop-note","schemaVersion":"cultcache.interop_note.v1","members":[{"slot":0,"name":"SchemaVersion","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},{"slot":1,"name":"DocumentId","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":true},{"slot":2,"name":"AuthorRuntimeId","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},{"slot":3,"name":"Title","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},{"slot":4,"name":"Body","type":"System.String","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false},{"slot":5,"name":"Tags","type":"System.String[]","isReference":false,"many":false,"targetSchemaName":null,"indexAlias":null,"isName":false}]}',
    compatible_schema_ids=("cultcache.interop-note",),
    members=(
        CultCacheSchemaCatalogMember(0, "SchemaVersion", "System.String"),
        CultCacheSchemaCatalogMember(1, "DocumentId", "System.String", is_name=True),
        CultCacheSchemaCatalogMember(2, "AuthorRuntimeId", "System.String"),
        CultCacheSchemaCatalogMember(3, "Title", "System.String"),
        CultCacheSchemaCatalogMember(4, "Body", "System.String"),
        CultCacheSchemaCatalogMember(5, "Tags", "System.String[]"),
    ),
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
