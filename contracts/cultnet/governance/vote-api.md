# CultNet vote API v0 (specification)

Status: specification, drafted for Heimdall (Bifrost) and a Ratatoskr voting module. No server implements it yet.
Files here: `vote-api.schema.json` (one schema, a `$defs` entry per document), `vote-api-vectors.json`. Conformance suites: `tests/GameCult.Networking.Tests/GovernanceVectorTests.cs` (C#) and `packages/cultnet-ts/test/governance-vectors.test.ts` (TypeScript). The vectors are written by `scripts/gen-governance-vectors.py`.
Facts it rests on: `/opt/commons/handoff/bifrost-browser-voting-facts.md` (Bifrost `416f11a`; `416f11a..276f611` changes four files: a Standard-close concurrency fix in `MotionGovernanceService.cs`, two test files and `docs/deploy-main-to-live.md`; none touches the sealed API, so the facts hold at `276f611`) and Bifrost PR #37 (`docs/ratatoskr-voting-auth.md`, see "PR #37" below).

## Scope

**Which API covers which.** This contract covers **sealed-ballot motions only** (`gamecult.sealed_ballot.v0`). **Standard motions are not covered here.** Bifrost PR #37 also serves them (`PUT /api/votes/motions/{id}/ballot`, a receipt `{motionId, choice, revision, castAtUtc, receiptId}`), and that API is #37's own: this page defines no document for it, and a Standard motion id sent to any exchange below is answered `not_found`. Today Standard motions have no JSON read or cast API at all, no Mind path, and no receipt (open choice 1). The contract names four exchanges and one refusal document:

| exchange | request | response |
|---|---|---|
| motion list | `gamecult.vote.motion_list_request.v0` | `gamecult.vote.motion_list.v0` |
| motion read | `gamecult.vote.motion_read_request.v0` | `gamecult.vote.motion.v0` |
| cast (commit or reveal) | `gamecult.vote.cast.v0` | `gamecult.vote.receipt.v0` |
| any refusal | | `gamecult.vote.refusal.v0` |

Every document is CultNet map-mode: all keys are written, nil where absent, and the schema is closed (`additionalProperties: false`). Instants are RFC 3339 UTC with a `Z` and up to seven fractional digits. Ids are lowercase `D`-format UUIDs. A motion states `highStakes`, a boolean fixed when the motion opens and never changed after (Bifrost PR #37, D2): it is the property that makes a commit demand a fresh sign-in (`reauthentication_required`). The bytes of every document are pinned by `messagePackHex` in the vectors (shortest integers, shortest strings, keys in the order the schema lists them); JSON with the same keys is the xenos-boundary form of the same documents (open choice 4: the wire format).

## Documents

- **motion_list_request**: `{schemaVersion, phase}`; `phase` is `Commit`, `Reveal`, `Closed` or nil for all.
- **motion_list**: `{schemaVersion, motions: [{id, title, round, phase, commitClosesAtUtc, closesAtUtc, highStakes}]}`. **Heimdall must add this endpoint**: Bifrost has no list route for sealed motions today, only fetch-by-id. No pagination in v0 (open choice 2).
- **motion_read_request**: `{schemaVersion, motionId}`.
- **motion**: Bifrost's `SealedMotionState` with its names: `{schemaVersion, id, title, summary, round, priorMotionId, ballotSchema, phase, candidates (two or more), commitClosesAtUtc, closesAtUtc, highStakes, yourVoterId, yourCommitment (a receipt or nil), announcement}`. It never carries a tally, a turnout, or another voter's commitment or ballot (a `tally` key is a negative vector). `yourVoterId` is the voter's `UserAccount.Id`, the `voterUserAccountId` of the commitment.
- **cast**: `{schemaVersion, motionId, verb, commitment, ballot}` with exactly one of two shapes: `verb: "commit"` with a 64-hex `commitment` and `ballot: nil`, or `verb: "reveal"` with `commitment: nil` and a `ballot`. One document for both verbs; today's two routes (`/commit`, `/reveal`) may stay, taking this document as the body, with a `verb` that must match the route (open choice 3).
- **ballot** (inside a reveal): `{schema: "gamecult.sealed_ballot.v0", motionId, round, candidate, reason, salt, persona}`. `candidate` and `reason` are single-line and **already trimmed** (no Unicode White_Space at either end, using the White_Space list of `write-receipt.md`); the contract refuses an untrimmed value rather than trimming it, so the bytes hashed are the bytes sent. **This is a behaviour change Bifrost must make:** it trims `candidate` and `reason` silently on reveal today (facts file section 3c, `SealedBallotService.cs:154, 385-387`), so an untrimmed reveal is accepted and hashed trimmed; under this contract it is `invalid` (open choice 5). `reason` is 1 to 280 characters counted in UTF-16 code units, as Bifrost counts. A JSON Schema `maxLength` counts code points, so the schema alone accepts up to 280 astral characters; both suites add the rule (vectors `cast_reveal_reason_140_astral_280_units` valid, `ballot_reason_141_astral_282_units` refused). `salt` is 32 to 128 lowercase hex. `persona` is `""` for a member and a printable-ASCII, space-free id of at most 64 characters for a Mind.
- **receipt**: Bifrost's `SealedBallotReceipt`: `{schemaVersion, motionId, voterUserAccountId, commitment, revision (>= 1), committedAtUtc, revealed}`. The receipt for a reveal has `revealed: true` and **echoes nothing of the ballot** (no candidate, reason or salt; negative vectors for each).
- **refusal**: `{schemaVersion, code, error, retryAfterSeconds}`; the `code` set is closed:

| code | HTTP | trigger |
|---|---|---|
| `unauthenticated` | 401 | no bearer, or one Heimdall does not know (the API must answer this, not redirect to a sign-in page) |
| `reauthentication_required` | 401 | a commit on a `highStakes` motion with no sign-in fresh enough (#37 D2: `prompt=login`, `max_age`); the voter signs in again at the top level and retries |
| `forbidden` | 403 | authenticated but not an Active member, or a Mind key used on a route it may not call |
| `not_found` | 404 | unknown motion id, a Standard motion id, or a reveal with no prior commit |
| `wrong_phase` | 409 | commit outside Commit, reveal outside Reveal |
| `contended` | 409 | a reveal raced the close fence: send it again |
| `invalid` | 400 | the body fails the schema or an in-document rule (untrimmed text, candidate not in the motion, `round` or `motionId` mismatch, `persona` wrong for the credential) |
| `commitment_mismatch` | 400 | a reveal whose canonical bytes do not hash to the stored commitment |
| `payload_too_large` | 413 | body over 4096 bytes |
| `rate_limited` | 429 | cast rate exceeded, per account; `retryAfterSeconds >= 1`, mirrored in `Retry-After` |
| `held` | 503 | the Ratatoskr voting brake is set (#37 D5): nothing is read or stored while it is |

Each code has a vector (`refusals`), and every failure of the schema has a negative vector (`invalid`).

**Not on Bifrost's member API yet.** The **413 body cap of 4096 bytes** and the **429 per-account cast limit** are not on Bifrost's member sealed API today (facts file sections 3c and 5): both exist only on the Mind intake route (4096 bytes; 30 requests a minute per source address). #37 proposes 30 casts per 10 minutes per account for the member routes (its D6). `reauthentication_required` and `held` are #37 proposals (D2, D5) and exist nowhere in code. All four are spec text until Heimdall builds them.

**Two refusals Bifrost must split first.** Bifrost returns a reveal whose bytes do not hash to the stored commitment as a plain `400` `Invalid` ("The ballot does not match the stored commitment."), the same status and error type as every other bad body, so `commitment_mismatch` and `invalid` are one today. It returns a contended reveal (a race with the close fence, `SealedBallotService.cs:186-189`) as `WrongPhase` `409`, the same type as a reveal outside the Reveal phase, so `contended` and `wrong_phase` are one today. A client cannot tell "send it again" from "too late", or "wrong ballot" from "malformed body", until Bifrost splits each pair.

## Commitment

`commitment = lowercase hex SHA-256 of the UTF-8 bytes of`

    gamecult.sealed_ballot.v0 \n {motionId} \n {voterUserAccountId} \n {persona} \n {round} \n {candidate} \n {reason} \n {salt}

with `\n` U+000A, ids in `D` format, `round` in decimal, `persona` empty for a member. This is Bifrost's formula as the code has it (`SealedBallotService.cs:154, 385-387`); `docs/sealed-ballot-motion.md:22-23` omits `{persona}` and is stale. The vectors give the canonical bytes (`canonicalHex`) and the digest for a member, a Mind, non-ASCII text, and a decomposed-versus-precomposed pair (no normalisation: they are different ballots). `pairs` pins what a reveal must match: the right ballot matches; a changed reason, another voter id, or a dropped persona does not, and the answer is `commitment_mismatch`.

## Receipts the voter can check later

A cast returns the receipt. The voter checks it later in two places that must agree byte for byte with it: `motion.yourCommitment` on a motion read (a commit receipt is present from the commit; the revision counts re-commits), and, after close, the `commitment` the result lists for them. The contract adds no receipt id or signature: the receipt is the commitment, its revision and the time (open choice 6).

## Personas

A Persona votes only on sealed motions, through a Mind key (Bifrost binds an Ed25519 public key 1:1 to an Active member account). The ballot's `persona` is the bound persona id and the commitment covers it, so a Mind's ballot cannot be replayed as a member's (`pairs`: dropping the persona does not match). The Mind-intake route today takes a signed `MindEnvelope` anonymously; this contract does not model that envelope, nonce or signature (open choice 7). Standard motions have no Mind path.

## Authentication

On the wire the credential is an opaque `Authorization: Bearer` header. The token route, claims, scopes and audience belong to Heimdall and Bifrost and are not designed here. A request without a usable bearer is answered `unauthenticated` as a typed refusal.

**Auth per route, now and after #37.** Routes are Bifrost's; this contract fixes the documents, not the paths (open choice 3).

| exchange | today (`/api/sealed-motions`, facts file 3c) | after #37 (`/api/votes`) |
|---|---|---|
| motion list | no route | bearer, scope `votes.read` |
| motion read | session cookie + Active member | bearer, scope `votes.read` |
| commit, reveal | session cookie + Active member | bearer, scope `votes.cast`, audience `bifrost-votes`; a high-stakes commit also needs a fresh sign-in |
| Mind cast | anonymous; the Ed25519 signature on a `MindEnvelope` is the credential | unchanged: Minds never use `/api/votes` (#37) |
| open a motion, Mind-key registry | session cookie + moderator | unchanged, cookie only |

Today a member route has **no bearer scheme**: it accepts the host-only `__Host-bifrost_session` cookie and answers an unauthenticated call with a redirect to the sign-in page, not a 401. After #37 `/api/votes` is bearer-only and answers a typed 401. The wire header stays an opaque bearer either way; only Bifrost knows which of the two it is checking.

## PR #37 (Bifrost `docs/ratatoskr-voting-auth.md`, head `447a7cb`)

Checked 2026-11-10 by an Eyes pass over the PR and the schema; conflicts for Heimdall:

1. **Standard motions.** The PR serves them (`PUT /api/votes/motions/{id}/ballot {choice, comment?}`, a receipt `{motionId, choice, revision, castAtUtc, receiptId}`); the contract is sealed-only and answers `not_found` for a Standard id.
2. **Verb model.** The PR keeps `POST .../commit` and `.../reveal` and adds `GET .../result`; the contract has one `cast` document and no result document.
3. **List and read as GET.** The PR names GET routes; the contract's request documents carry a `phase` filter and a `motionId`. The mapping (query string and path) is undefined until Heimdall fixes the routes.
4. **Refusals.** The PR lists 401/403/404/409/429 with `WWW-Authenticate`; the contract adds 400 (`invalid`, `commitment_mismatch`) and 413, and a typed body.
5. **Minds.** The PR says Minds never use `/api/votes` and keeps the anonymous signed `/mind` route; the contract carries `persona` in the ballot and a `forbidden` case for a Mind key on a route it may not call. The PR's token is never a Persona's.
6. **Authority.** The contract's `unauthenticated` says a bearer "Heimdall does not know"; the PR makes Bifrost the OIDC issuer (a second client, scopes `votes.read` and `votes.cast`, audience `bifrost-votes`) and notes that Bifrost OIDC is not live (its D7).
7. **Rate limits.** The PR keys per account (30 casts per 10 minutes); the contract now says per account too, and neither exists on the member API yet (above).
8. **Wire format.** The PR's errors are JSON; the contract's vectors are MessagePack. Nothing settles the media type.
9. **The PR's seven open decisions** (consent for `votes.cast`, re-authentication, no refresh token, grant revocation, a Ratatoskr brake, rate numbers, MAS rollout order) assume nothing the contract contradicts. D2 (re-authentication at cast) is answered yes for sealed commit in this contract: `reauthentication_required`, with `highStakes` fixed at open. D5 (a brake) is `held`.

## Open choices for Heimdall

1. Standard motions in v0 or a v1 (the PR already serves them).
2. Pagination and filters on the list.
3. One `cast` route or the two existing ones; `verb` redundancy.
4. Wire format: MessagePack documents with a JSON export, or JSON only (the PR is JSON).
5. Refuse untrimmed `candidate` and `reason` (this contract) or keep trimming server-side.
6. Whether a receipt should carry an id or a server signature so a voter can prove it to a third party.
7. The Mind path: keep the signed envelope on its own route, or take Mind keys as a bearer credential.
8. `reason` length in UTF-16 code units (as Bifrost) or code points.
9. Whether the result document (`GET .../result`) joins this contract.
