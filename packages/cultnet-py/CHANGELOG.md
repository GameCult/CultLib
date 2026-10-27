# Changelog

## Unreleased (breaking; version 0.x, so the next minor)

- Removed `resolve_document_and_schema_id_for_raw_record` from the package exports. A raw record resolves to one local schema id, the one
  its document registers, so there is no wire id to return. Use `resolve_document_for_raw_record(documents_by_schema_id, schema_id, record)`,
  which returns the document; take the schema id from `document.catalog_entry().schema_id`.
- `apply_raw_document_record`, `apply_raw_snapshot` and `apply_shard_log_response` report and stamp the local schema id, also for a record
  that arrived under a compatible id or the schema name.
