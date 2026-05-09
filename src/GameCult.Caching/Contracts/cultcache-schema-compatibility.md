# CultCache Schema Compatibility

CultCache compatibility is slot-driven and explicit. The cache will not pretend
that "same-ish looking" schemas are fine just because the names feel friendly.

## Semantic identity

Two CultCache document schemas are considered the same shape when they agree on:

- `schemaName`
- `schemaVersion`
- member slots
- member persisted type names
- reference/cardinality semantics
- target schema names for references
- name/index lookup participation

The canonical fixtures currently exercised in C# are:

- `tests.named_entry`
  - schema id: `sha256:e7b97801b94190f3159012ede45b0069bb09ebf7920f7432c971bc86a0e08de8`
  - content hash: `sha256:23150930afcc1d84f0cb3012ccc2debcb9b4685f62083033bbaab0083f1e832e`
- `tests.reference_holder`
  - schema id: `sha256:bd85064961cc74565fb73e3ccbc4217cfba4dc4869e365a08bea4f704739bd8f`

See `GameCult.Caching.Tests/BackingStoreTests.cs` for the canonical fixture
documents and the expected receipts.

## Soft-migratable drift

CultCache accepts compatible drift only when the local reader can still map the
persisted slots honestly:

- persisted schema id differs, but the embedded catalog points to a compatible
  local schema id
- persisted slot is missing locally and can be ignored
- local slot is missing in persisted data and can fall back to the local
  default value

The cache emits a typed migration report with:

- exact vs compatible-drift classification
- ignored extra slots
- defaulted missing slots
- warning codes and messages

## Hard rejection

CultCache rejects persisted schemas when any slot changes in a way that would
lie about the payload:

- type change
- reference vs value change
- one vs many change
- target schema change
- name/index lookup semantics change
- missing embedded catalog entry
- no compatible local schema candidate

This is not negotiable. Better a loud refusal than quiet bit-rot.
