# heimdall-consent

Typed CultCache records for Heimdall's answer collector: `NoticeAcceptance`, `Answer` and `MemberTerms`,
plus the rules that need no store to state (`Answer::is_training_data`, `Answer::redacted`,
`validate`). Cut from `docs/answer-collector-consent.md` in GameCult/Heimdall PR #6 at doc commit
`2364979`; the doc's tables and Representation section are the contract, and its rulings are on the PR
(comment 682). Specification only: no collector, tally, export or tombstone hashing lives here.

Entry points: `src/lib.rs`; golden vectors `tests/vectors/consent_golden.csv`, run by `tests/golden.rs`
(`cargo test`). Planned, not live: the collector, the tally, the training export.

## Where each ruling lands (doc lines at 2364979)

| Ruling | Doc | Crate |
|---|---|---|
| Timestamps are Unix ms UTC, signed 64-bit | L93-94 | `i64` on `accepted_at`, `received_at`, `terms_accepted_at` |
| Ids are non-empty opaque strings, `event_id` verbatim; versions unsigned 32-bit and positive | L95-98 | `validate()`; `notice_version: u32`; `check_sample_id` parses `u32` |
| Key parts joined with U+001F; empty part or part containing it refused | L99-100 | `key()` |
| Tombstone is the exited `Answer` with `text_hash` (32 bytes); HMAC-SHA-256 keyed by a per-member salt held by the member-side service; the collector cannot compute it | L74, L127-147, L186-187 | `Answer.text_hash`; `redacted([u8; 32])` takes the service's output and refuses an answer with no `text` (L143-144); `validate()` requires 32 bytes and `exited` |
| A reply with no established notice version or scope is stored with both absent, writes no acceptance, is excluded from every use | L82-89 | `notice_version`/`scope` optional and absent together (`validate()`); vector case `no-scope` |
| A reply's version is the one pinned at its `origin_server_ts`; changed version needs a fresh first reply | L32-34, L47, L87-89 | acceptance key includes the version; `is_training_data` accepts only the acceptance for the answer's own version; vector case `version-change` |
| `MemberTerms` is the typed shape of the member record's `terms_version`/`terms_accepted_at`, not a second store | L51-56 | `MemberTerms` doc comment; the collector does not hold it |
| Training needs acceptance `scope = train` equal to the answer's `scope`, and `accepted_at <= received_at` | L111-119 | `is_training_data` |
| One store per scope plus a store for scopeless replies | L120-125 | not modelled; the collector's layout owns it |

## Questions for Heimdall

1. May `text` be an empty string? The doc says `text` is "string, or absent" (L72) and ids are non-empty
   (L95), but says nothing of answer text. The crate accepts `Some("")` and `redacted` tombstones it.
2. The doc says a later answer to the same key replaces the earlier by "latest `received_at` wins"
   (L78-80). Equal `received_at` for two different events is unspecified. The crate holds no replace
   rule; the collector owns it.
