# CultNet fork-desk contract v0 (specification)

Status: specification, drafted for Heimdall (Bifrost receiver), Huginn (the second door) and Void (Ratatoskr desk). No runtime implements it yet.
Rule source: `/opt/commons/handoff/fork-desk-answer-binding-20261110.md` (Heimdall; sections 1.1 to 1.5, 1.3a to 1.3d), amended by the Persona briefs of 2026-11-10 (Eureka's fit check). Where this page differs from that file it says so under "Differences from the binding file".
Format source: the fork desk artifact in `/opt/commons/handoff/fork-desk-original/` (ruling `eureka-substrate:ruling:fork-desk-nibu-format`).
Files here: `fork-card.schema.json` (one schema, a `$defs` entry per document), `fork-card-vectors.json`. Conformance: `tests/GameCult.Networking.Tests/GovernanceVectorTests.cs` (C#) and `packages/cultnet-ts/test/governance-vectors.test.ts` (TypeScript), both generated against vectors written by `scripts/gen-governance-vectors.py`. Each suite computes every hash with its own code and replays every scenario through a reference judge written from this page.

All documents are CultNet map-mode: every key written, nil where absent, closed schemas. Field names are snake_case as in the binding file and the mind's records; `schemaVersion` keeps CultNet's spelling (open choice 1). The bytes of every document are pinned in the vectors as `messagePackHex`.

## Documents

| schema id | what | who writes it |
|---|---|---|
| `gamecult.fork.card.v0` | the fork as shown | the publisher (a Mind's card projection) |
| `gamecult.fork.answer_request.v0` | the answer a decider sends | the client; Bifrost forwards it unchanged to Huginn's second door |
| `gamecult.desk_answer.v0` | the stored answer with one tagged outcome | Huginn only |
| `gamecult.fork.answer_result.v0` | what the sender gets back for a stored answer | Bifrost, from the stored `desk_answer` |
| `gamecult.fork.refusal.v0` | a body refused at the door; nothing stored | Bifrost |
| `gamecult.fork.record.v0` | a fork's answers plus the derived counted answer | a projection of the stored `desk_answer`s |
| `gamecult.fork.ruling_cite.v0` | the citation a ruling carries | the Mind admitting the ruling |

### card

`{schemaVersion, fork_id, title, context, options[{id, text}], recommendation, depends[], leans, flag, raised_in, deciders[], status, ruling, ruled_from[], order, options_hash}`.

- `fork_id`: the mind's `question` key, `<campaign>:question:<label>`.
- **Hashed (the shown members):** `title`, `context`, `options` (each `{id, text}`, in shown order), `recommendation` (an option id, **never nil**: the stored `question` requires `recommended`), `depends` (strings, each the `campaign:kind:label` rendering of a stored reference, in shown order, empty when none).
- **Not hashed:** `fork_id` (bound beside the hash), `leans` (an option id or nil), `flag`, `raised_in`, `deciders`, `status` (`open` or `closed`), `ruling` (the ruling id when closed), `ruled_from` (answer ids a ruling has been admitted from; non-empty only on a closed card), `order`, and `options_hash` itself.
- **`deciders` is read-only data.** The list is set outside the Mind, through Bifrost's admin path; the Mind reads it and the card projection copies it. A question's own `audience` may only narrow it. A Mind never writes it, so a Mind cannot add itself and answer its own fork.
- Rules a schema cannot say, each with a negative vector: option ids are unique; `recommendation` and `leans` name an option; deciders are unique; a closed card names its `ruling`; an open card's `ruled_from` is empty (a ruling that answers a fork closes it); `options_hash` equals the hash of the shown members.

### Options hash (canonical bytes)

`options_hash` = lowercase hex SHA-256 of the UTF-8 bytes of the following, byte for byte:

1. Build an object with exactly the five members `title`, `context`, `options`, `recommendation`, `depends`. Each option is an object with exactly `id` and `text`.
2. **NFC-normalise every string, object keys included.** No trimming, no newline rewriting, no case folding. A string with a lone surrogate is not a card (it has no UTF-8). Normalisation stability makes NFC stable across Unicode versions for already-assigned code points; a runtime's own normaliser is trusted and the vectors include a decomposed pair and astral code points (open choice 2).
3. Serialise as RFC 8785 (JCS): no whitespace; object keys sorted by UTF-16 code unit, which for these ASCII keys is `context, depends, options, recommendation, title` and, inside an option, `id, text`; array order is as shown (option order and `depends` order are part of the hash).
4. Strings are written in double quotes; only `"` and `\` are backslash-escaped, U+0008, U+0009, U+000A, U+000C, U+000D as `\b \t \n \f \r`, every other code point below U+0020 as `\u00xx` in lowercase hex; everything else is written raw as UTF-8 (U+007F, U+2028, `<`, `&` and astral code points included).
5. **Numbers and nulls:** the member set contains none. There is no number to canonicalise (a number anywhere in the five members is a defect) and `recommendation` is never nil, so no nil is hashed.

`fork_id` is not in the hash and is bound beside it in its own field, so one hash cannot be replayed under another fork. The publisher computes the hash and stores it in the card; the client recomputes it from what it rendered and refuses to send on a mismatch; the receiver recomputes from its stored fork, never from the client.

Vectors `optionsHash`: the seven of the binding file (`heimdall_A..G`), computed by the Python generator, by the C# suite and by the TypeScript suite independently, plus text, title, context and `depends`-order mutations, a case showing the unshown members do not move the hash, escapes and markup, and astral ids. Each suite also mutates every shown member, an option id included, and asserts the hash changes.

### answer_request

`{schemaVersion, fork_id, options_hash, answer, retracted, retracts, answered_at}`. `answer` is `{choice, note}` (at least one non-nil) or nil on a retraction. `choice` is an option id; `note` is the person's words, verbatim, 1 to 4000 characters (code points, as JSON Schema counts them), not blank. `answered_at` is the client's claim. **A request never carries `answerer` or `authority`**: both are stamped by Huginn (`answerer` from the verified subject the receiver passes; `authority` from the door's configuration). A body that names either key at any depth is refused whole, never stripped (`body_forbidden_field`); any other unknown key is `body_unknown_field`. The request type Bifrost sends over the second door is this document; it has no field for either key, so a client cannot supply them by construction, and Bifrost's scan is the first line, not the only one. How Huginn receives the verified subject is the door's authenticated context, not a body field (open choice 3). In map-mode `retracted` is always written (the binding file's optional `retracted` becomes `false`).

### desk_answer (the stored kind)

`{schemaVersion, answer_id, fork_id, outcome, of, retracts, code, options_hash, choice, note, answered_at, received_at, claim_in_future, answerer, authority}`, key `<campaign>:desk_answer:<question-label>.n<seq>`; `answer_id` is `n<seq>`, assigned by Huginn per question, and **an answer is named by its answer id** everywhere (`retracts`, `of`, `ruled_from`).

The stamped `authority` field of a stored answer is an **attribution**: the role the verified account holds (`Operator`, `Steward`, `Member`), copied from Bifrost's state. A stored answer confers no authority. Authority exists only where a ruling cites a counted answer ("Operator authority" below); the field keeps the binding file's spelling (open choice 8). One tagged `outcome`, each variant carrying only its own link:

- `Counted`: the answer that counts. `of`, `retracts`, `code` nil.
- `Revision`: a later passing answer, kept, not counted while the first stands. `of` names the counted answer at the time.
- `Retraction`: the stored form of `retracted: true` plus `retracts`. `retracts` names the answer retracted; `choice` and `note` nil. The illegal pair (a retraction with no target, or a target with no retraction) cannot be written.
- `Refused`: any refusal from steps 4 to 7 or R1 and R2, with its `code`, stored too. `choice` and `note` keep what the body carried.

`received_at` is Huginn's admission time; "first" means first by admission order (the `n<seq>` ordinal), never by `answered_at`. `claim_in_future` is true when `answered_at` is more than five minutes after `received_at`: accepted but flagged. **Not stored, and no answer id:** a body refused at step 1 or 2 (Bifrost; it never reaches Huginn), and a body naming an unknown fork (step 3; Huginn answers it from its stored forks, but with no question there is nothing to key a record under). Each gets a `refusal` and is dropped. A malformed body is therefore never stored, and neither is an unknown fork. Every refusal from step 4 on is stored.

### Counting rules (in order; the first failure refuses with that code)

| step | rule | refusal | stored |
|---|---|---|---|
| 1 | verified session or bearer | `unauthenticated` (401) | no |
| 2 | closed body; no `answerer`/`authority` at any depth; type-correct; not (no `answer` and not a retraction) | `body_forbidden_field`, `body_unknown_field`, `body_invalid` (400) | no |
| 3 | `fork_id` is a stored fork | `fork_unknown` (404) | no |
| 4 | `options_hash` equals the stored fork's hash | `stale_fork` (409) | **yes**; the result carries the current card |
| 5 | the stamped `answerer` is on `deciders` | `not_a_decider` (403) | yes |
| 6 | the fork is open (`status: open`) | `fork_closed` (409) | yes |
| 7 | `choice`, if present, is an option id | `choice_invalid` (400) | yes |
| 8 | first passing answer for `(fork_id, options_hash)` is `Counted`; later passing answers are `Revision`s naming the counted one | | yes |
| 9 | a Matrix edit, redaction or reaction changes nothing | | no event is an answer |

**Retraction** (`retracted: true`, `retracts: <answer id>`, `answer: nil`, `options_hash` still carried, so step 4 applies) passes steps 1 to 6, then:

- R1 form: it carries no `answer`, it names a `Counted` or `Revision` answer of this fork under this `options_hash`; else `bad_retraction` (400). A retraction naming an answer already retracted is `already_retracted` (409). `retracts` on a body that is not a retraction is `bad_retraction`.
- R2 who: the original answerer or any other decider; step 5 already requires a decider.
- R3 is dropped and its label stays vacant, so R4 keeps its name in the binding file. A ruling that answers a fork closes it (Eureka and Heimdall, 2026-11-10), so a ruling cannot cite an answer on an open fork (that is the stale-click gap, and a card with `ruled_from` set and `status: open` is invalid), and a retraction after a ruling meets step 6: refused `fork_closed`, stored. There is no `ruled` code (vector `V10a_a_ruling_that_closed_the_fork_makes_a_retraction_fork_closed`).
- R4 counted answer: retracting the counted answer removes it from the count and the next passing answer in admission order counts again. Retracting an answer that was not counted changes nothing. After every answer is retracted, a fresh answer is `Counted`. Records stay append-only.

**The counted answer is derived**, not stored: the first `Counted` or `Revision` answer under the card's current `options_hash` that no `Retraction` names. When the card changes, answers under the old hash stop counting and the next answer under the new hash is `Counted` (vectors `a_changed_card_...`). The `record` projection carries it as `counted_answer_id`.

### Operator authority

A ruling may carry `authority: Operator` only if it cites (`ruling_cite.answer_id`) a stored `desk_answer` whose outcome is `Counted` and whose stamped attribution (the answer's `authority` field) is `Operator` (or a stamped relay of her words from a mapped door, which this contract does not model). The citation is what gives a ruling authority: an attribution on an answer is a fact about the account, and authority exists only where a ruling cites a counted answer. The cited answer id is what `ruled_from` lists on the closed card. Negative vectors (`rulingCites`): no citation, a `Revision`, a `Refused` answer, a Counted answer by a non-operator, an answer that does not exist. Other authorities need no citation.

## Old format, the mind's record, and what maps to what

The old desk (`index.html`) read `questions/<id>` and wrote `answers/<id>` in a shared store. The mind's `question` record is the source in the new design; the card is its projection.

| old desk | mind `question` record | card |
|---|---|---|
| `qid`, `id` | key `campaign:question:label` | `fork_id` |
| `title` | `title` | `title` |
| `body` | `question` (the long text) | `context` |
| `depends`: a free string | `depends`: a list of references | `depends`: list of `campaign:kind:label` strings |
| `options[{key, label, text}]` | `options[{label, text}]` | `options[{id, text}]` |
| option `key` | option `label` (a slug, the id) | option `id` |
| option `label` (a human title) | none | **dropped**: not stored, not hashed; shown text is `text` |
| `rec` (a key) | `recommended` (a label) | `recommendation` (an id), never nil |
| `lean` ("Self leans" mark) | none | `leans` (an id or nil), not hashed |
| `flag` (the provisional line) | none | `flag` (a string or nil), not hashed |
| `status` open or closed, `ruling` | derived from the resolution (status is never written) | `status`, `ruling`, both a projection |
| `order` | none (admission ordinal) | `order` |
| none | `raised_in`, `asked_on` | `raised_in`; `asked_on` is not carried |
| none | none | `deciders` (outside the Mind), `ruled_from`, `options_hash` |
| answer `{choice: key or null, note, question, updatedAt}`, one mutable document per question | none | `answer_request` then an append-only `desk_answer` |
| `note`: her words verbatim | | `answer.note`, verbatim |
| "Clear pick": `choice` back to null so the note stands alone | | an `answer` with `choice` nil and `note` set, as the first answer or as a `Revision` |
| "Changing an answer is fine until it's admitted as a ruling" | | a later answer is a `Revision`; `Retraction` withdraws one; neither changes who counted first, and a ruling that answers the fork closes it, after which no answer or retraction is taken (`fork_closed`) |

Differences that need Void's choice: the old page **overwrites** one answer per question and the new store **appends**, so the booth cannot show "your answer" as one mutable field (it shows the latest answer by the person, plus its history); the old page needs no identity and the new one stamps `answerer` from a verified session; the old `label` has nowhere to live unless the card's `text` carries it.

## Transport note for Void: Matrix event versus how the mind carries questions

How a mind carries a question today: a typed `question` record in Huginn (`huginn view`, `huginn query`); the Persona relays it to the operator in chat, and the operator's reply comes back as chat text that Self reads and admits as a `ruling` in her words. Identity is whoever the chat attributes the message to; nothing binds an answer to the question text she saw.

Option A, **a Matrix event in the Mind's room with the answer as a reply.** Gains: it sits where people already are, needs no sign-in beyond Matrix, and the thread shows the exchange. Loses: the sender is an mxid, not a Bifrost account, so `answerer` and `authority` have no verified source; an edit or redaction changes the visible message but not the decision (binding rule 9), so the thread and the record can disagree; there is nowhere to carry `options_hash`, so a stale card cannot be refused; and a reply is free text, so `choice` is lost.

Option B, **Bifrost receives the answer and Huginn stores it** (the design in this page; Ratatoskr PR #24 also withdraws the Matrix carrier). Gains: a verified account, a bound hash, refusals that are stored and visible, one counted answer, retractions. Loses: the booth needs Bifrost sign-in, which does not reach chat yet (Bifrost PR #9 and #37 design it), so until then the desk is read-only; and the exchange is no longer in the room.

**Recommendation: B, with Matrix as presentation only.** The Mind may post a card as a listing and a link into the desk; no Matrix event is ever an answer (rule 9). Confidence: high on the rule (it is Heimdall's and Eureka's already), moderate on the interim, where read-only until sign-in is a real cost; if the operator needs to answer before sign-in reaches chat, the honest stopgap is the old path (chat text, Self admits a ruling in her words) rather than a Matrix reply the record cannot bind.

## Differences from the binding file

- Spelling: every field is as the binding file names it (`fork_id`, `options_hash`, `answered_at`, `retracted`, `retracts`, `answer.choice`, `answer.note`, `recommendation`, `depends`, option `id`), except that `schemaVersion` keeps CultNet's camelCase and `answer_id` replaces the earlier `receipt id` (Persona brief).
- Map-mode writes every key: `retracted` is always present; nothing is "optional".
- `Refused` stores every refusal from steps 4 to 7 and R1 and R2 (his section 1.3b); only steps 1 and 2 and `fork_unknown` are not stored. The binding file does not say whether `fork_unknown` is stored; it cannot be keyed without a question, so it is not (open choice 4).
- The binding file's `choice_valid` has no named refusal code; this page names it `choice_invalid`. `fork_known` is `fork_unknown`. A type-invalid body is `body_invalid`.
- `ruled` (his R3 and V10) is dropped: a ruling that answers a fork closes it, so the case is `fork_closed` (V4, V10a). His file drops it too.
- The recommendation is never nil. The binding file's earlier vectors D and E used a nil recommendation; its updated D and E use `"a"`, as here.
- Hash vectors: A, B, C, D, E, F and G of the binding file are reproduced digest for digest by the Python generator, the C# suite and the TypeScript suite (the generator prints `same` for each; none differs).

## Open choices

1. Case of field names: snake_case for the fork documents (as the binding file) beside camelCase for the vote documents (as Bifrost's serializer). One convention would be tidier.
2. NFC across runtimes relies on each runtime's normaliser; a pinned table, as `write-receipt.md` does for letters, would remove the reliance. The risk is only for code points assigned after a runtime's Unicode version.
3. How Huginn learns the verified subject: the second door's authenticated context (the peer credential names Bifrost the service, not the member), so the subject must travel beside the request. This contract has no envelope for it.
4. Whether `fork_unknown` and shape refusals should leave an audit record somewhere other than a `desk_answer`.
5. Whether `leans` and `flag`, which the booth shows, should join the hash. The binding file's five members do not include them; a changed lean would not make an answer stale.
6. What the person sees of a retracted counted answer when the next answer counts: this contract only says the count moves.
7. `received_at` is not in the binding file's stored fields; it is kept for `claim_in_future` and could be dropped if the ordinal alone decides order.
8. The stamped `authority` field is an attribution, not an authority (above); renaming it `attribution` would say so, at the price of a new key the door must also refuse in a body.
9. Which scope and audience a fork answer needs from Bifrost PR #37: its scopes (`votes.read`, `votes.cast`) and audience (`bifrost-votes`) name votes, and this page says only "verified session or bearer".
