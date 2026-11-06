# heimdall-consent

Typed CultCache records for Heimdall's answer collector: `NoticeAcceptance`, `Answer` and `MemberTerms`,
plus the rules that need no store to state (`Answer::is_training_data`, `Answer::redacted`,
`validate`). Cut from `docs/answer-collector-consent.md` in GameCult/Heimdall PR #6 at doc commit
`4e99bfa`; the doc's tables and Representation section are the contract, and its rulings are on the PR
(comments 682 and 759). Specification only: no collector, tally, export or tombstone hashing lives here.

Entry points: `src/lib.rs`; golden vectors `tests/vectors/consent_golden.csv`, run by `tests/golden.rs`
(`cargo test`). Planned, not live: the collector, the tally, the training export.

## Where each ruling lands (doc lines at 4e99bfa)

| Ruling | Doc | Crate |
|---|---|---|
| Timestamps are Unix ms UTC, signed 64-bit | L98-99; `received_at` is collector-assigned, L75 | `i64` on `accepted_at`, `received_at`, `terms_accepted_at` |
| Ids are non-empty opaque strings, `event_id` verbatim; versions unsigned 32-bit and positive | L100-103 | `validate()`; `notice_version: u32`; `check_sample_id` parses `u32` and refuses a leading zero in the version (`s@01`) so one version has one key; the doc says only "positive integer", so Heimdall may instead rule that the crate normalises |
| Key parts joined with U+001F; empty part or part containing it refused | L104-105 | `key()` |
| Tombstone is the exited `Answer` with `text_hash` (32 bytes); HMAC-SHA-256 keyed by a per-member salt held by the member-side service; the collector cannot compute it | L74, L132-152, L186-193 | `Answer.text_hash`; `redacted([u8; 32])` takes the service's output and refuses an answer with no `text` (L148-149); `validate()` requires 32 bytes and `exited` |
| A reply with no established notice version or scope is stored with both absent, writes no acceptance, is excluded from every use | L87-94 | `notice_version`/`scope` optional and absent together (`validate()`); vector case `no-scope` |
| A reply's version is the one pinned at its `origin_server_ts`; changed version needs a fresh first reply | L32-34, L47, L92-94 | acceptance key includes the version; `is_training_data` accepts only the acceptance for the answer's own version; vector case `version-change` |
| `MemberTerms` is the typed shape of the member record's `terms_version`/`terms_accepted_at`, not a second store | L51-56 | `MemberTerms` doc comment; the collector does not hold it |
| Training needs acceptance `scope = train` equal to the answer's `scope`, and `accepted_at <= received_at` | L116-123 | `is_training_data` |
| One store per scope plus a store for scopeless replies | L125-130 | not modelled; the collector's layout owns it |

Rulings of comment 759 (doc L72, L78-85; no open questions remain):

| Ruling | Doc | Crate |
|---|---|---|
| A live answer must have non-empty `text` and is rejected otherwise; empty text is valid only on an exited answer (text cleared, hash-only tombstone) | L72, L83-85 | `Answer::validate()` refuses a non-exited answer whose `text` is absent or empty; an exited answer holds no text. Vector cases `empty-live-rejected`, `empty-revoked` |
| `received_at` is assigned by the collector; a tie on it breaks by `event_id` compared bytewise, the greater wins as the later reply | L75, L78-81 | `Answer::supersedes`: `(received_at, event_id bytes)` order; the vector harness replaces a stored answer only when the incoming one supersedes it. Vector cases `tie-greater-first`, `tie-greater-last` |
