# heimdall-consent

Typed CultCache records for Heimdall's answer collector: `NoticeAcceptance`, `Answer` and `MemberTerms`,
plus the two rules that need no store to state (`Answer::is_training_data`, `Answer::redacted`). Cut from
`docs/answer-collector-consent.md` in GameCult/Heimdall PR #6 (head d836f29); the doc's tables are the
contract. Specification only: no collector, tally or export lives here.

Entry points: `src/lib.rs`; golden vectors `tests/vectors/consent_golden.csv`, run by `tests/golden.rs`
(`cargo test`). Planned, not live: the collector, the tally, the training export.

## Choices the doc does not fix (questions for Heimdall)

1. Timestamps are unix milliseconds (`i64`), matching `origin_server_ts`. The doc says "UTC timestamp".
2. `member_id`, `experiment_id`, `event_id` are strings; `notice_version` is `u32`; `terms_version` a string. The doc names only "Heimdall member id" and "Matrix event id".
3. Key text is the parts joined with U+001F; a part that is empty or contains it is refused. Matrix ids can hold most other separators.
4. The tombstone's salted `text` hash has no row in the answer table. It is carried as `Answer.text_hash` (bytes) on the exited record. Which hash function, whose salt, and whether the tombstone is a separate record is undecided; the crate never computes the hash.
5. `Answer.notice_version` and `scope` are optional in the typed record so an answer lacking them can exist and be excluded; the table makes both required. Should such a reply be stored at all, given "is not collected"?
6. A reply under a notice with no recorded scope writes no acceptance in the vector's collector; the doc says the first reply accepts "the notice version then pinned" and does not cover a missing pin.
7. `MemberTerms` is a separate record keyed by `member_id`. The doc puts the fields on the member; the member record lives in Heimdall, not CultLib.
8. Training rule item 2 also requires the acceptance's scope to be `train` and to match the answer's experiment and member; the doc requires only that an acceptance exist for the key with `accepted_at <= received_at`.
9. The "separate store or partition" for records wider than `show` is not modelled here.
