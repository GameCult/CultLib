# Changelog

## Unreleased (breaking; version 0.x, so the next minor)

- Decodes the write receipt frames of `contracts/cultnet/interop/write-receipt.md`: `parse_message` validates `cultnet.write_admitted.v0` and `cultnet.error.v1` against their closed schemas (unknown keys, empty or non-string ids, an unknown operation or code are refused; `error.v1` requires `messageId`, string or null; `limit_reached` is `error.v1` only). Added `CULTNET_ERROR_V1_CODES`, `CULTNET_WRITE_RECEIPT_MESSAGE_VERSIONS`, `write_content_hash` and `hello_advertises_write_receipts`; `hello` takes `supported_message_versions`. `tests/test_write_receipt.py` replays the vectors' `contentHash`, `wire` and `schemaInvalid` sections. Not implemented here: the client write tracker (`scenarios`) and the server rules (`dedupe`, `blankFields`); this package has no receipt server and no write client.
- Added `message_invalid` to `CULTNET_ERROR_CODES` and `message_invalid_error(field, reason)`, byte-identical to the C# `ForMessageInvalid`. `cultmesh-py` refuses a malformed put or delete with it instead of the out-of-schema `malformed_document_put` and `malformed_document_delete`, which are removed.
- Added `CultNetRudpSocketTransportServer` (`cultnet_py.rudp_server`): one UDP socket serving many RUDP peers, one session per
  remote endpoint, ported from the C# `CultNetRudpSocketTransportServer` and held to its bytes by
  `tests/vectors/rudp-multipeer-server/transcript.txt`. The caller drives it (`try_receive_once`, `poll_resends`). Like the C#
  listener it never reaps idle peers. `CultNetRudpSession` gained `next_sequence`, `withdraw_send` and `end_with`, and
  `CultNetTransportStats` gained `send_failures`.

- Removed `resolve_document_and_schema_id_for_raw_record` from the package exports. A raw record resolves to one local schema id, the one
  its document registers, so there is no wire id to return. Use `resolve_document_for_raw_record(documents_by_schema_id, schema_id, record)`,
  which returns the document; take the schema id from `document.catalog_entry().schema_id`.
- `apply_raw_document_record`, `apply_raw_snapshot` and `apply_shard_log_response` report and stamp the local schema id, also for a record
  that arrived under a compatible id or the schema name.
