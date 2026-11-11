import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { isDeepStrictEqual } from "node:util";

import Ajv2020 from "ajv/dist/2020.js";
import { decode, encode } from "@msgpack/msgpack";

// contracts/cultnet/governance: the vote-API and fork-card contracts (vote-api.md, fork-card.md). The vectors are spec-authored
// (scripts/gen-governance-vectors.py); this suite re-encodes every message with this runtime and must reproduce the bytes, judges
// every negative with the committed schema, and replays the fork scenarios through a reference judge written from fork-card.md.
const DIR = join(__dirname, "..", "..", "..", "..", "contracts", "cultnet", "governance");
const read = (name: string) => JSON.parse(readFileSync(join(DIR, name), "utf8"));
const vote = read("vote-api-vectors.json");
const fork = read("fork-card-vectors.json");

const ajv = new Ajv2020({ allErrors: true, strict: false });
const voteSchema = read("vote-api.schema.json");
const forkSchema = read("fork-card.schema.json");
ajv.addSchema(voteSchema);
ajv.addSchema(forkSchema);
const check = (schema: { $id: string }, def: string, value: unknown): boolean => {
  const validate = ajv.getSchema(`${schema.$id}#/$defs/${def}`);
  assert.ok(validate, `no schema def ${def}`);
  return validate(value) as boolean;
};
const hexOf = (value: unknown): string => Buffer.from(encode(value)).toString("hex").toUpperCase();
const sha = (bytes: Buffer | string): string => createHash("sha256").update(bytes).digest("hex");

// Rules a JSON Schema cannot say: vote-api.md counts a reason in UTF-16 code units (as Bifrost does); maxLength counts code points.
const voteRuleErrors = (def: string, message: any): string[] =>
  def === "cast" && message.ballot !== null && message.ballot.reason.length > 280 ? ["reason is over 280 UTF-16 code units"] : [];

test("vote API: every valid document validates, encodes to the vector bytes and decodes back", () => {
  for (const v of vote.valid) {
    assert.ok(check(voteSchema, v.def, v.message), v.name);
    assert.deepEqual(voteRuleErrors(v.def, v.message), [], v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    assert.deepEqual(decode(Buffer.from(v.messagePackHex, "hex")), v.message, v.name);
  }
});

test("vote API: every negative is refused by the schema, and its bytes are the vector's", () => {
  for (const v of vote.invalid) {
    assert.ok(!check(voteSchema, v.def, v.message) || voteRuleErrors(v.def, v.message).length > 0, `${v.name}: ${v.why}`);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
});

test("vote API: the commitment is SHA-256 of the canonical bytes", () => {
  for (const c of vote.commitment) {
    const b = c.ballot;
    const bytes = Buffer.from(`gamecult.sealed_ballot.v0\n${b.motionId}\n${c.voterUserAccountId}\n${b.persona}\n${b.round}\n${b.candidate}\n${b.reason}\n${b.salt}`, "utf8");
    assert.equal(bytes.toString("hex").toUpperCase(), c.canonicalHex, c.name);
    assert.equal(sha(bytes), c.sha256, c.name);
  }
});

test("vote API: a reveal matches its commit exactly when the ballot hashes to the commitment", () => {
  for (const p of vote.pairs) {
    const b = p.reveal.ballot;
    const digest = sha(Buffer.from(`gamecult.sealed_ballot.v0\n${b.motionId}\n${p.voterUserAccountId}\n${b.persona}\n${b.round}\n${b.candidate}\n${b.reason}\n${b.salt}`, "utf8"));
    assert.equal(digest === p.commit.commitment, p.matches, p.name);
    assert.ok(check(voteSchema, "cast", p.commit) && check(voteSchema, "cast", p.reveal), p.name);
  }
});

test("vote API: each refusal code has a vector, a status and the vector bytes", () => {
  const codes = (voteSchema.$defs.refusal.properties.code as { enum: string[] }).enum;
  assert.deepEqual(vote.refusals.map((r: { message: { code: string } }) => r.message.code).sort(), [...codes].sort());
  for (const r of vote.refusals) {
    assert.ok(check(voteSchema, "refusal", r.message), r.name);
    assert.equal(hexOf(r.message), r.messagePackHex, r.name);
    assert.ok(r.httpStatus >= 400, r.name);
  }
  assert.ok(vote.refusals.find((r: { name: string }) => r.name === "rate_limited").message.retryAfterSeconds >= 1);
});

// ---- fork card (rule source: /opt/commons/handoff/fork-desk-answer-binding-20261110.md) ----
const jstr = (s: string): string => {
  let out = '"';
  for (const ch of s) {
    const o = ch.codePointAt(0)!;
    out += ch === '"' ? '\\"' : ch === "\\" ? "\\\\" : o === 8 ? "\\b" : o === 9 ? "\\t" : o === 10 ? "\\n" : o === 12 ? "\\f" : o === 13 ? "\\r"
      : o < 0x20 ? "\\u" + o.toString(16).padStart(4, "0") : ch;
  }
  return out + '"';
};
type Option = { id: string; text: string };
type Shown = { title: string; context: string; options: Option[]; recommendation: string; depends: string[] };
type Card = Shown & { fork_id: string; leans: string | null; raised_at: string | null; deciders: string[]; decider_count: number; status: string; ruling: string | null; ruled_from: string[]; options_hash: string };
// RFC 8785 over the five shown members, every string NFC-normalised first; keys sorted; no numbers in the member set.
const nfc = (s: string): string => s.normalize("NFC");
const canonical = (c: Shown): Buffer =>
  Buffer.from(`{"context":${jstr(nfc(c.context))},"depends":[${c.depends.map((d) => jstr(nfc(d))).join(",")}],"options":[${c.options.map((o) => `{"id":${jstr(nfc(o.id))},"text":${jstr(nfc(o.text))}}`).join(",")}],"recommendation":${jstr(nfc(c.recommendation))},"title":${jstr(nfc(c.title))}}`, "utf8");
const optionsHash = (c: Shown): string => sha(canonical(c));

const cardRuleErrors = (c: Card): string[] => {
  const ids = c.options.map((o) => o.id);
  const errors: string[] = [];
  if (new Set(ids).size !== ids.length) errors.push("duplicate option ids");
  if (!ids.includes(c.recommendation)) errors.push("recommendation is not an option");
  if (c.leans !== null && !ids.includes(c.leans)) errors.push("leans is not an option");
  if (new Set(c.deciders).size !== c.deciders.length) errors.push("duplicate deciders");
  if (c.deciders.length > 0 && c.deciders.length !== c.decider_count) errors.push("R-card: shown deciders are not exactly decider_count ids");
  if (c.depends.some((d) => Buffer.byteLength(d, "utf8") > 1000)) errors.push("a depends line is over 1000 UTF-8 bytes");
  if (c.status === "open" && c.ruling !== null) errors.push("an open card names a ruling");
  if (c.ruling === null && c.ruled_from.length > 0) errors.push("ruled_from without a ruling");
  if (c.options_hash !== optionsHash(c)) errors.push("options_hash is not the hash of the shown members");
  return errors;
};

test("fork card: options hash is SHA-256 of the canonical JSON, computed here, and every shown member moves it", () => {
  for (const h of fork.optionsHash) {
    const bytes = canonical(h);
    assert.equal(bytes.toString("utf8"), h.canonical, h.name);
    assert.equal(bytes.toString("hex").toUpperCase(), h.canonicalHex, h.name);
    assert.equal(sha(bytes), h.sha256, h.name);
  }
  const byName = new Map<string, string>(fork.optionsHash.map((h: { name: string; sha256: string }) => [h.name, h.sha256]));
  const base = byName.get("heimdall_A_five_members")!;
  assert.equal(byName.get("hidden_members_do_not_move_it"), base);
  const card0 = fork.card.valid[0].message as Card;
  for (const raised_at of [null, "2027-01-02T03:04:05.123Z"]) {
    const stamped: Card = { ...card0, raised_at };
    assert.equal(optionsHash(stamped), card0.options_hash, "raised_at stays outside the hash");
  }
  for (const [deciders, decider_count] of [[[], 2], [card0.deciders, 64]] as const) {
    const shown: Card = { ...card0, deciders: [...deciders], decider_count };
    assert.equal(optionsHash(shown), card0.options_hash, "decider_count and the emptiness of deciders stay outside the hash");
  }
  assert.equal(byName.get("heimdall_E_decomposed_equals_D"), byName.get("heimdall_D_nfc_escapes_astral"), "an NFD and an NFC spelling hash alike");
  for (const moved of ["heimdall_B_recommendation_changed", "heimdall_C_option_order_changed", "heimdall_F_option_id_changed", "heimdall_G_depends_emptied", "option_text_changed",
                       "title_changed", "context_changed", "depends_order_changed", "depends_order_changed_reversed"]) {
    assert.notEqual(byName.get(moved), base, moved);
  }
});

test("fork card: an answer result without counted_answer_id is refused by the schema", () => {
  const result = structuredClone(fork.scenarios.flatMap((sc: any) => sc.steps).find((st: any) => st.response?.answer).response);
  assert.ok(check(forkSchema, "answerResult", result));
  delete result.counted_answer_id;
  assert.ok(!check(forkSchema, "answerResult", result));
});

// body_sha256 is Huginn's private "same body" digest, not a wire field: compact JSON, fixed key order, null for missing, the two illegal-form keys only when present.
test("fork card: the body digest is SHA-256 of the compact JSON in the stated key order, and the fixed forms separate retracted true from false", () => {
  const byName = new Map<string, any>(fork.bodyDigests.map((d: { name: string }) => [d.name, d]));
  for (const d of fork.bodyDigests) {
    const o: Record<string, unknown> = {};
    for (const key of ["fork", "options_hash", "answered_at", "choice", "note", "retracts", "illegal_retraction", "retracted"]) {
      if ((key === "illegal_retraction" || key === "retracted") && d.input[key] === null) continue;
      o[key] = d.input[key];
    }
    const json = JSON.stringify(o);
    assert.equal(json, d.canonical, d.name);
    assert.equal(sha(Buffer.from(json, "utf8")), d.sha256, d.name);
  }
  assert.ok(byName.get("plain_answer_choice_A").sha256.startsWith("8a4d5b2e"));
  assert.ok(byName.get("legal_retraction").sha256.startsWith("9f807538"));
  assert.ok(byName.get("legal_retraction_door_form").sha256.startsWith("84576840"));
  assert.ok(byName.get("legal_retraction_door_form").input.retracts.endsWith(".n1"), "the door's form: the wire n<seq> converted to the full key");
  assert.ok(byName.get("illegal_i_retraction_with_an_answer_retracted_true").sha256.startsWith("935cab1a"));
  assert.ok(byName.get("illegal_iii_plain_answer_with_a_target_retracted_false").sha256.startsWith("82b24ccc"));
  assert.ok(byName.get("illegal_ii_no_answer_no_target_retracted_true").sha256.startsWith("34ef86ed"));
  assert.equal(new Set(fork.bodyDigests.map((d: { sha256: string }) => d.sha256)).size, fork.bodyDigests.length, "no two vectors share a digest");
});

test("fork card: mutating any shown member, an option id included, changes the hash", () => {
  const card = fork.card.valid[0].message as Card;
  const base = optionsHash(card);
  const mutations: ((c: Card) => void)[] = [
    (c) => { c.title += "x"; }, (c) => { c.context += "x"; }, (c) => { c.options[0].id += "x"; }, (c) => { c.options[0].text += "x"; },
    (c) => { c.options.reverse(); }, (c) => { c.recommendation = c.options[0].id; }, (c) => { c.depends = [...c.depends, "a:b:c"]; }, (c) => { c.depends = []; },
  ];
  for (const [i, mutate] of mutations.entries()) {
    const copy = structuredClone(card);
    mutate(copy);
    assert.notEqual(optionsHash(copy), base, `mutation ${i}`);
  }
  const hidden = structuredClone(card);
  hidden.fork_id = "other:question:id"; hidden.leans = "yes"; hidden.deciders = []; hidden.decider_count = 7; hidden.status = "closed"; hidden.ruled_from = ["n1"];
  assert.equal(optionsHash(hidden), base, "fork_id and the unshown members stay outside the hash");
});

test("fork card: documents validate and encode to the vector bytes", () => {
  for (const v of fork.card.valid) {
    assert.ok(check(forkSchema, "card", v.message), v.name);
    assert.deepEqual(cardRuleErrors(v.message), [], v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
  for (const [section, def] of [["request", "answerRequest"], ["deskAnswer", "deskAnswer"]] as const) {
    for (const v of fork[section].valid) {
      assert.ok(check(forkSchema, def, v.message), v.name);
      assert.deepEqual(noteRuleErrors(def, v.message), [], v.name);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
      assert.deepEqual(decode(Buffer.from(v.messagePackHex, "hex")), v.message, v.name);
    }
  }
  const resultCodes = (forkSchema.$defs.deskAnswerRefused.properties.code as { enum: string[] }).enum;
  assert.deepEqual(fork.results.map((r: { name: string }) => r.name).sort(), [...resultCodes].sort());
  for (const v of fork.results) {
    assert.ok(check(forkSchema, "answerResult", v.message), v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    assert.equal(v.message.card !== null, v.name === "stale_fork", "only a stale refusal shows the new fork");
    assert.deepEqual(resultRuleErrors(v.message), [], v.name);
  }
  for (const v of fork.resultsInvalid) {
    assert.ok(!check(forkSchema, "answerResult", v.message) || resultRuleErrors(v.message).length > 0, `${v.name}: ${v.why}`);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
  for (const v of fork.unavailable) {
    assert.ok(check(forkSchema, "unavailable", v.message), v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    assert.equal(v.httpStatus, 503, v.name);
  }
  const doorCodes = (forkSchema.$defs.refusal.properties.code as { enum: string[] }).enum;
  assert.ok(!doorCodes.includes("unavailable"), "unavailable is kept apart from the refusal set");
  assert.deepEqual(fork.doorRefusals.map((r: { name: string }) => r.name).sort(), [...doorCodes].sort());
  for (const v of fork.doorRefusals) {
    assert.ok(check(forkSchema, "refusal", v.message), v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
  assert.equal(fork.doorRefusals.find((r: { name: string }) => r.name === "fork_too_large").httpStatus, 422);
});

test("fork card: every negative is refused by the schema or a stated rule", () => {
  for (const v of fork.card.invalid) {
    assert.ok(!check(forkSchema, "card", v.message) || cardRuleErrors(v.message).length > 0, `${v.name}: ${v.why}`);
  }
  for (const [section, def] of [["request", "answerRequest"], ["deskAnswer", "deskAnswer"]] as const) {
    for (const v of fork[section].invalid) {
      assert.ok(!check(forkSchema, def, v.message) || noteRuleErrors(def, v.message).length > 0, `${v.name}: ${v.why}`);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
  }
});

// R-answer-result: the answer in an answer_result is the sender's own, stamped with their account, never nil (nil exists only in a record shown to a non-decider).
const resultRuleErrors = (r: { answer: { answerer: string | null } }): string[] => (r.answer.answerer === null ? ["R-answer-result: the answerer is nil"] : []);

// Rules a JSON Schema cannot say: a note is at most 4000 UTF-8 bytes (maxLength counts code points and is only the looser bound).
const noteRuleErrors = (def: string, message: any): string[] => {
  const note = def === "answerRequest" ? message?.answer?.note : def === "deskAnswer" ? message?.note : null;
  return note != null && Buffer.byteLength(note, "utf8") > 4000 ? ["note is over 4000 UTF-8 bytes"] : [];
};

const OP_ID = "e0d1c2b3-a495-4687-8879-6a5b4c3d2e1f";
const TAILS: Record<string, string> = {
  unauthenticated: "a verified session is required",
  body_forbidden_field: "answerer and authority are set by the receiver",
  body_unknown_field: "the body is a closed shape",
  body_invalid: "the body does not satisfy gamecult.fork.answer_request.v0",
  fork_unknown: "no such fork",
  forbidden: "only Active members may use the desk",
  request_id_conflict: "this request_id was used for a different body",
};
const REQUEST_KEYS = ["schemaVersion", "fork_id", "options_hash", "answer", "retracted", "retracts", "answered_at", "request_id"];
const hasKey = (v: unknown, names: string[]): boolean =>
  typeof v === "object" && v !== null && Object.entries(v).some(([k, x]) => names.includes(k) || hasKey(x, names));
const SECONDS = (iso: string): number => Date.parse(iso);

// The reference judge: Huginn's half behind the second door as fork-card.md states it. Server-set fields come from the step.
function judge(start: Card, steps: { kind: string; body: Record<string, any> | null; server: Record<string, any> | null; card?: Card }[]) {
  let card = start;
  const answers: Record<string, any>[] = [];
  const responses: unknown[] = [];
  const seen = new Map<string, { body: Record<string, any>; response: unknown }>();   // (answerer, request_id) -> the first stored outcome
  const counted = (): string | null => {
    const hash = optionsHash(card);
    const live = answers.find((a) => (a.outcome === "Counted" || a.outcome === "Revision") && a.options_hash === hash
      && !answers.some((r) => r.outcome === "Retraction" && r.retracts === a.answer_id));
    return live ? live.answer_id : null;
  };
  for (const step of steps) {
    if (step.kind === "matrix_event") { responses.push(null); continue; }       // rule 9: Matrix never changes a decision
    if (step.kind === "card") { card = step.card!; responses.push(null); continue; }
    const s = step.server!, b = step.body!;
    const door = (code: string) => ({ schemaVersion: "gamecult.fork.refusal.v0", code, error: `${code}: ${TAILS[code]}` });
    const store = (outcome: string, link: string | null, code: string | null) => {
      const a = b.answer ?? { choice: null, note: null };
      const doc = { schemaVersion: "gamecult.desk_answer.v0", answer_id: s.answer_id, fork_id: b.fork_id, outcome,
        of: outcome === "Revision" ? link : null, retracts: outcome === "Retraction" ? link : outcome === "Refused" ? b.retracts : null, code: outcome === "Refused" ? code : null,
        options_hash: b.options_hash, choice: a.choice, note: a.note, answered_at: b.answered_at, received_at: s.received_at,
        claim_in_future: SECONDS(b.answered_at) - SECONDS(s.received_at) > 300_000, answerer: s.answerer, authority: s.authority, request_id: b.request_id };
      answers.push(doc);
      const stored = { schemaVersion: "gamecult.fork.answer_result.v0", answer: doc, card: code === "stale_fork" ? card : null, counted_answer_id: counted() };
      seen.set(`${s.answerer}|${b.request_id}`, { body: structuredClone(b), response: structuredClone(stored) });
      return stored;
    };
    const refuse = (code: string) => store("Refused", null, code);
    const down = s.door_down === true, reached = s.stored === true;   // the second door failed to answer; "stored" says whether Huginn had the request by then
    const unavailable = () => ({ schemaVersion: "gamecult.fork.unavailable.v0", code: "unavailable", error: "unavailable: the outcome is not confirmed, try again with the same request_id", retryAfterSeconds: s.retry_after_seconds });
    // a stored desk_answer has no card, so a replayed stale_fork carries the CURRENT card (fork-card.md, request_id)
    const replay = (stored: any) => { const copy = structuredClone(stored); if (copy.answer.code === "stale_fork") copy.card = structuredClone(card); copy.counted_answer_id = counted(); return copy; };
    let response: unknown;
    if (s.answerer === null) response = door("unauthenticated");
    else if (s.active === false) response = door("forbidden");
    else if (hasKey(b, ["answerer", "authority"])) response = door("body_forbidden_field");
    else if (Object.keys(b).some((k) => !REQUEST_KEYS.includes(k)) || (b.answer && Object.keys(b.answer).some((k) => k !== "choice" && k !== "note"))) response = door("body_unknown_field");
    else if (!check(forkSchema, "answerRequest", b) || noteRuleErrors("answerRequest", b).length > 0 || (!b.retracted && b.answer === null)) response = door("body_invalid");
    else if (down && !reached) response = unavailable();
    else if (seen.has(`${s.answerer}|${b.request_id}`)) { const prior = seen.get(`${s.answerer}|${b.request_id}`)!; response = isDeepStrictEqual(prior.body, b) ? replay(prior.response) : door("request_id_conflict"); }
    else if (b.fork_id !== card.fork_id) response = door("fork_unknown");
    else if (b.options_hash !== optionsHash(card)) response = refuse("stale_fork");
    else if (!card.deciders.includes(s.answerer)) response = refuse("not_a_decider");
    else if (card.status === "closed") response = refuse("fork_closed");
    else if (b.retracted || b.retracts !== null) {
      const target = answers.find((a) => a.answer_id === b.retracts && (a.outcome === "Counted" || a.outcome === "Revision") && a.options_hash === b.options_hash);
      if (!b.retracted || b.answer !== null || b.retracts === null || !target) response = refuse("bad_retraction");
      else if (answers.some((a) => a.outcome === "Retraction" && a.retracts === target.answer_id)) response = refuse("already_retracted");
      else response = store("Retraction", target.answer_id, null);
    } else if (b.answer.choice !== null && !card.options.some((o) => o.id === b.answer.choice)) response = refuse("choice_invalid");
    else {
      const first = counted();
      response = first === null ? store("Counted", null, null) : store("Revision", first, null);
    }
    responses.push(down && reached ? unavailable() : response);
  }
  return { responses, record: { schemaVersion: "gamecult.fork.record.v0", fork_id: card.fork_id, options_hash: optionsHash(card), counted_answer_id: counted(), answerer_count: new Set(answers.map((a) => a.answerer)).size, answers } };
}

test("fork card: every scenario replays to the vector's responses and record, bytes included", () => {
  for (const sc of fork.scenarios) {
    assert.equal(optionsHash(sc.card), sc.optionsHash, sc.name);
    assert.deepEqual(cardRuleErrors(sc.card), [], sc.name);
    const { responses, record } = judge(sc.card, sc.steps);
    assert.deepEqual(responses, sc.steps.map((s: { response: unknown }) => s.response), sc.name);
    assert.deepEqual(record, sc.record, sc.name);
    assert.ok(check(forkSchema, "record", record), sc.name);
    assert.equal(hexOf(record), sc.recordHex, sc.name);
    for (const s of sc.steps) if (s.response?.answer) assert.deepEqual(resultRuleErrors(s.response), [], sc.name);
    for (const s of sc.steps) if (s.response) assert.ok(check(forkSchema, "answerResult", s.response) || check(forkSchema, "refusal", s.response) || check(forkSchema, "unavailable", s.response), sc.name);
  }
});

test("fork card: a request id belongs to its account; another account's identical body gets its own answer, never the first account's outcome", () => {
  let crossed = 0;
  for (const sc of fork.scenarios) {
    const first = new Map<string, { answerer: string; result: any }>();   // request_id -> the account that first got a stored result and what it got
    for (const s of sc.steps) {
      if (s.kind !== "answer" || !s.response?.answer) continue;
      assert.equal(s.response.answer.answerer, s.server.answerer, `${sc.name}: a result is stamped with the account that asked`);
      const prior = first.get(s.body.request_id);
      if (!prior) { first.set(s.body.request_id, { answerer: s.server.answerer, result: s.response }); continue; }
      if (prior.answerer === s.server.answerer) continue;
      crossed++;
      assert.notEqual(s.response.answer.answer_id, prior.result.answer.answer_id, `${sc.name}: its own answer id`);
      assert.notDeepEqual(s.response, prior.result, `${sc.name}: the first account's outcome is never returned`);
    }
  }
  assert.ok(crossed >= 3, "the vectors carry the cross-account cases");
});

test("fork card: a body refused at the door or naming an unknown fork is never stored", () => {
  const door = new Set(["unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "forbidden", "request_id_conflict"]);
  let doorRefusals = 0;
  for (const sc of fork.scenarios) {
    for (const s of sc.steps) {
      // unavailable promises nothing: the outcome is not confirmed, so the answer is stored exactly when Huginn had it
      if (s.response?.code === "unavailable") assert.equal(sc.record.answers.some((a: { answer_id: string }) => a.answer_id === s.server.answer_id), s.server.stored === true, `${sc.name} unavailable`);
      if (s.response && door.has(s.response.code)) {
        doorRefusals++;
        assert.ok(!sc.record.answers.some((a: { answer_id: string }) => a.answer_id === s.server.answer_id), `${sc.name} stored a door refusal`);
      }
    }
  }
  assert.ok(doorRefusals >= 8);
});

test("fork card: the scenarios cover every outcome and every refusal code", () => {
  const seen = new Set<string>();
  for (const sc of fork.scenarios) {
    for (const s of sc.steps) if (s.response) seen.add(s.response.code ?? s.response.answer.code ?? s.response.answer.outcome);
    for (const a of sc.record.answers) seen.add(a.outcome);
  }
  for (const want of ["Counted", "Revision", "Retraction", "Refused", "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "unavailable", "forbidden", "request_id_conflict", "stale_fork", "not_a_decider",
                      "fork_closed", "choice_invalid", "bad_retraction", "already_retracted"]) {
    assert.ok(seen.has(want), want);
  }
});

// A ruling may carry authority Operator only if it cites a Counted desk_answer stamped Operator (binding file 1.3d).
const admitRuling = (cite: { authority: string; answer_id: string | null }, answers: { answer_id: string; outcome: string; authority: string }[]): boolean => {
  if (cite.authority !== "Operator") return true;
  const cited = answers.find((a) => a.answer_id === cite.answer_id);
  return cited !== undefined && cited.outcome === "Counted" && cited.authority === "Operator";
};

test("fork card: operator authority needs a cited Counted answer record", () => {
  for (const c of fork.rulingCites) {
    assert.ok(check(forkSchema, "rulingCite", c.message), c.name);
    assert.equal(hexOf(c.message), c.messagePackHex, c.name);
    assert.equal(admitRuling(c.message, c.answers), c.admit, c.name);
  }
});

// ---- transport: list, read, auth (fork-card.md "Transport") ----
type Entry = { card: Card; record: { fork_id: string; options_hash: string; counted_answer_id: string | null; answerer_count: number; answers: Record<string, any>[] } };
const entryRuleErrors = (e: Entry): string[] => {
  const errors = cardRuleErrors(e.card);
  if (e.record.fork_id !== e.card.fork_id) errors.push("record of another fork");
  if (e.record.options_hash !== e.card.options_hash) errors.push("record under another hash");
  const counted = e.record.answers.find((a) => (a.outcome === "Counted" || a.outcome === "Revision") && a.options_hash === e.card.options_hash
    && !e.record.answers.some((r) => r.outcome === "Retraction" && r.retracts === a.answer_id));
  if ((counted?.answer_id ?? null) !== e.record.counted_answer_id) errors.push("counted_answer_id is not derived");
  // R-record: named answerers are counted by distinct id, nil ones by a bound
  const named = e.record.answers.filter((a) => a.answerer !== null).length, total = e.record.answers.length, n = e.record.answerer_count;
  if (total === 0 && n !== 0) errors.push("R-record: no answers and a count other than 0");
  else if (named === total && total > 0 && n !== new Set(e.record.answers.map((a) => a.answerer)).size) errors.push("R-record: answerer_count is not the distinct answerers");
  else if (named === 0 && total > 0 && (n < 1 || n > total)) errors.push("R-record: a withheld count outside 1..answers");
  // R-entry: the card's view and the record's view agree, so a withheld card never rides beside a named account
  if (e.card.deciders.length > 0 && named < total) errors.push("R-entry: a decider-view card with withheld answerers");
  if (e.card.deciders.length === 0 && named > 0) errors.push("R-entry: a withheld card beside a named answerer");
  return errors;
};
// R-list: all entries of one page are one reader's view
const viewRuleErrors = (forks: { card: Card }[]): string[] => (new Set(forks.map((f) => f.card.deciders.length > 0)).size > 1 ? ["R-list: a page mixes views"] : []);
// The non-decider twin of a decider-view entry: ids withheld, counts kept.
const withheld = (e: Entry): Entry => ({ ...e, card: { ...e.card, deciders: [] }, record: { ...e.record, answers: e.record.answers.map((a) => ({ ...a, answerer: null })) } });
const idsOf = (e: Entry): string[] => [...e.card.deciders, ...e.record.answers.map((a) => a.answerer as string)];
const bytesHaveNoId = (hex: string, ids: string[], name: string): void => {
  const bytes = Buffer.from(hex, "hex");
  assert.ok(ids.length > 0, name);
  for (const id of ids) assert.ok(!bytes.includes(Buffer.from(id, "utf8")), `${name}: the withheld bytes carry ${id}`);
};

// Order is a unique rank per Mind: the refusal names both fork ids, the first holder and the second.
const orderErrors = (cards: { fork_id: string; order: number }[]): string[] => {
  const holder = new Map<number, string>();
  const errors: string[] = [];
  for (const c of cards) {
    if (holder.has(c.order)) errors.push(`order ${c.order} is shared by ${holder.get(c.order)} and ${c.fork_id}`);
    else holder.set(c.order, c.fork_id);
  }
  return errors;
};

// The reference list judge: newest first by card order, status filter, cursor c<N> = cards below order N, a page of limit, counts over the whole set.
const judgeList = (entries: { card: Card }[], ordinal: number, rq: { mind: string; status: string | null; cursor: string | null; limit: number }) => {
  const rows = [...entries].sort((a, b) => (b.card as any).order - (a.card as any).order);
  let want = rows.filter((e) => rq.status === null || (e.card.status === "open") === (rq.status === "open"));
  if (rq.cursor !== null) want = want.filter((e) => (e.card as any).order < Number(rq.cursor!.slice(1)));
  const page = want.slice(0, rq.limit);
  const more = want.length > page.length && page.length > 0;
  return { schemaVersion: "gamecult.fork.list.v0", mind: rq.mind, stamp: `s${ordinal}`, open_count: rows.filter((e) => e.card.status === "open").length,
    recorded_count: rows.filter((e) => e.card.status === "closed").length, forks: page, next_cursor: more ? `c${(page[page.length - 1].card as any).order}` : null };
};

// Bearer, audience, scope, in that order: a votes.* token fails at the audience and a desk token without the route's scope fails at the scope.
const authJudge = (route: string, token: { audience: string; scopes: string[]; active: boolean } | null, huginnDown: boolean): string => {
  if (token === null || token.audience !== "bifrost-desk") return "unauthenticated";
  if (!token.scopes.includes(route === "answer" ? "desk.answer" : "desk.read")) return "insufficient_scope";
  if (!token.active) return "forbidden";
  return huginnDown ? "unavailable" : "ok";
};

// The {mind} segment is decoded once and compared, exactly, only with the configured list; nothing is opened from it (fork-card.md, "The {mind} segment").
type MindResult = { code: string; opened: string | null; storedIn: string | null };
const mindRoute = (config: any, route: string, segment: string, fork: string | null, token: any): MindResult => {
  const auth = authJudge(route, token, false);
  if (auth !== "ok") return { code: auth, opened: null, storedIn: null };
  let mind: string;
  try { mind = decodeURIComponent(segment); } catch { return { code: "fork_unknown", opened: null, storedIn: null }; }
  if (!Object.prototype.hasOwnProperty.call(config.minds, mind)) return { code: "fork_unknown", opened: null, storedIn: null };
  const opened: string = config.minds[mind];
  if (route !== "list" && !config.forks[mind].includes(fork)) return { code: "fork_unknown", opened, storedIn: null };
  return { code: "ok", opened, storedIn: route === "answer" ? mind : null };
};

test("fork transport: the mind segment is only compared with the configured list; an unknown mind is the unknown fork refusal", () => {
  const config = fork.transport.mindRoutes;
  const refusals = new Map<string, any>(fork.doorRefusals.map((r: { name: string }) => [r.name, r]));
  const byName = new Map<string, MindResult>();
  for (const c of config.cases) {
    const got = mindRoute(config, c.route, c.mind, c.fork_id, c.token);
    byName.set(c.name, got);
    assert.equal(got.code, c.expect, c.name);
    assert.equal(got.opened, c.opened, `${c.name}: the socket opened`);
    assert.equal(got.storedIn, c.stored_in, `${c.name}: where an answer was stored`);
    assert.equal(c.httpStatus, got.code === "ok" ? 200 : refusals.get(got.code).httpStatus, c.name);
    if (got.code === "fork_unknown") {
      const error: string = refusals.get("fork_unknown").message.error;
      assert.ok(!error.includes(c.mind) && !(c.fork_id !== null && error.includes(c.fork_id)), `${c.name}: the refusal names neither the Mind nor the fork`);
    }
  }
  for (const [a, b] of config.sameResponse) {
    const x = byName.get(a)!, y = byName.get(b)!;
    assert.equal(x.code, y.code);
    const built = (code: string) => hexOf({ schemaVersion: "gamecult.fork.refusal.v0", code, error: `${code}: ${TAILS[code]}` });
    assert.equal(built(x.code), built(y.code), `${a} and ${b} answer with the same bytes, built from the spec's rule`);
    assert.equal(built(x.code), refusals.get(x.code).messagePackHex, a);
  }
  const all = [...byName.values()];
  assert.ok(all.filter((v) => v.code === "fork_unknown" && v.opened === null).length >= 39, "every bad segment opens nothing");
  assert.equal(new Set(all.filter((v) => v.storedIn !== null).map((v) => v.storedIn)).size, 2, "an answer is stored in the route's Mind only, and both Minds take one");
});

test("fork transport: requests and entries validate and encode to the vector bytes, and every negative is refused", () => {
  const t = fork.transport;
  for (const [section, def] of [["listRequest", "listRequest"], ["readRequest", "readRequest"], ["entry", "entry"]] as const) {
    for (const v of t[section].valid) {
      assert.ok(check(forkSchema, def, v.message), v.name);
      if (def === "entry") assert.deepEqual(entryRuleErrors(v.message), [], v.name);
      if (def === "entry" && v.twin !== undefined) {
        const twin = t.entry.valid.find((x: any) => x.name === v.twin);
        assert.deepEqual(v.message, withheld(twin.message), `${v.name}: the twin of ${v.twin}`);
        assert.equal(v.message.record.answerer_count, twin.message.record.answerer_count, "a decider and a non-decider read the same count");
        bytesHaveNoId(v.messagePackHex, idsOf(twin.message), v.name);
        assert.ok(twin.messagePackHex.includes(Buffer.from(idsOf(twin.message)[0], "utf8").toString("hex").toUpperCase()), "the decider view does carry the ids");
      }
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
    for (const v of t[section].invalid) {
      assert.ok(!check(forkSchema, def, v.message) || (def === "entry" && entryRuleErrors(v.message).length > 0), `${v.name}: ${v.why}`);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
  }
  for (const v of t.listInvalid) {
    const refused = !check(forkSchema, "list", v.message) || orderErrors(v.message.forks.map((f: any) => f.card)).length > 0 || viewRuleErrors(v.message.forks).length > 0;
    assert.ok(refused, `${v.name}: ${v.why}`);
    if (v.refusal !== undefined) assert.deepEqual(orderErrors(v.message.forks.map((f: any) => f.card)), [v.refusal], v.name);
    if (v.name === "a_page_mixes_views") assert.equal(viewRuleErrors(v.message.forks).length, 1, v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
});

test("fork transport: every list request replays to the vector's response, newest first, paged by the cursor", () => {
  for (const sc of fork.transport.listScenarios) {
    for (const e of sc.entries) assert.deepEqual(entryRuleErrors(e), [], sc.name);
    assert.deepEqual(orderErrors(sc.entries.map((e: any) => e.card)), [], sc.name);
    if (sc.twin !== undefined) {
      // the non-decider page is the decider page with the ids withheld: counts, order, cursor and stamp equal, byte for byte but the withheld members
      const twin = fork.transport.listScenarios.find((x: any) => x.name === sc.twin);
      assert.deepEqual(sc.entries, twin.entries.map(withheld), sc.name);
      assert.equal(sc.steps.length, twin.steps.length);
      sc.steps.forEach((step: any, i: number) => {
        const other = twin.steps[i].response;
        assert.deepEqual(step.response, { ...other, forks: other.forks.map(withheld) }, sc.name);
        assert.ok(twin.entries.length > 0 && step.response.forks.length > 0);
        bytesHaveNoId(step.responseHex, twin.entries.flatMap(idsOf), sc.name);
      });
      assert.ok(twin.steps[0].responseHex.includes(Buffer.from(OP_ID, "utf8").toString("hex").toUpperCase()), "the decider page does carry the ids");
    }
    let previous: { next_cursor: string | null } | null = null;
    for (const step of sc.steps) {
      const response = judgeList(sc.entries, sc.ordinal, step.request);
      assert.deepEqual(response, step.response, sc.name);
      assert.ok(check(forkSchema, "list", response), sc.name);
      assert.equal(hexOf(response), step.responseHex, sc.name);
      assert.deepEqual(viewRuleErrors(response.forks), [], sc.name);
      const orders = response.forks.map((f: any) => f.card.order);
      assert.deepEqual(orders, [...orders].sort((a: number, b: number) => b - a), sc.name);
      if (previous !== null) assert.equal(step.request.cursor, previous.next_cursor, `${sc.name}: the second request follows the first page's cursor`);
      previous = response;
    }
  }
});

test("fork transport: order 0 is legal, no cursor names it, and the page ending at order 1 hands on to it", () => {
  const sc = fork.transport.listScenarios.find((x: any) => x.name === "order_zero_is_legal_but_no_cursor_names_it");
  assert.deepEqual(sc.steps.map((s: any) => s.response.next_cursor), ["c1", null]);
  assert.deepEqual(sc.steps[1].response.forks.map((f: any) => f.card.order), [0]);
  assert.equal(check(forkSchema, "cursor", "c0"), false);
});

test("fork transport: a page may stop short of limit, and a first entry past the budget is fork_too_large (422) continued below its order", () => {
  const sc = fork.transport.listShortPages;
  assert.deepEqual(sc.map((x: any) => x.name), ["a_short_page_resumes_below_its_last_entry", "a_first_entry_past_the_budget_is_fork_too_large_and_the_client_continues_below_its_order", "fork_too_large_on_order_zero_leaves_nothing_below"]);
  assert.ok((forkSchema.$defs.refusal.properties.code as { enum: string[] }).enum.includes("fork_too_large"));
  const covered: number[] = [];
  for (const s of sc) {
    let last: string | null | undefined = undefined;
    s.steps.forEach((step: any, i: number) => {
      if (last !== undefined) assert.equal(step.request.cursor, last, `${s.name}: the step follows the cursor or the refused order`);
      const full = judgeList(s.entries, s.ordinal, step.request);
      if (step.response !== undefined) {
        const r = step.response as any, n = r.forks.length;
        assert.ok(check(forkSchema, "list", r), s.name);
        assert.equal(hexOf(r), step.responseHex, s.name);
        assert.deepEqual(r.forks, full.forks.slice(0, n), `${s.name}: a prefix of the unbudgeted page`);
        assert.ok(n >= 1, s.name);
        assert.equal(r.next_cursor, full.next_cursor !== null || n < full.forks.length ? `c${r.forks[n - 1].card.order}` : null, s.name);
        assert.equal(r.stamp, full.stamp); assert.equal(r.open_count, full.open_count); assert.equal(r.recorded_count, full.recorded_count);
        covered.push(...r.forks.map((f: any) => f.card.order));
        last = r.next_cursor;
      } else {
        const r = step.refusal, first = full.forks[0].card as any;
        assert.ok(check(forkSchema, "refusal", r), s.name);
        assert.equal(hexOf(r), step.refusalHex, s.name);
        assert.equal(r.code, "fork_too_large");
        assert.equal(step.httpStatus, 422);
        assert.equal(step.fork_id, first.fork_id); assert.equal(step.order, first.order);
        assert.ok(r.error.includes(first.fork_id) && new RegExp(`\\b${first.order}\\b`).test(r.error.replace(first.fork_id, "")), `${s.name}: the text names the fork id and the order`);
        assert.ok(step.request.limit >= 1, "limit 0 never refuses");
        if (first.order >= 1) { assert.ok(i + 1 < s.steps.length, s.name); last = `c${first.order}`; assert.ok(check(forkSchema, "cursor", last)); }
        else { assert.equal(i, s.steps.length - 1, `${s.name}: order 0 is the end`); assert.equal(check(forkSchema, "cursor", "c0"), false); last = undefined; }
      }
    });
  }
  assert.deepEqual(sc[0].steps.map((x: any) => x.response.next_cursor), ["c2", null]);
  assert.deepEqual(sc[0].steps.map((x: any) => x.response.forks.length), [2, 2]);
  assert.deepEqual([sc[1].steps[0].order, sc[2].steps[1].order], [3, 0]);
  assert.deepEqual(covered, [3, 2, 1, 0, 2, 1, 0, 1]);
});

test("fork transport: a read whose entry is past the cap is fork_too_large (422), named by fork id alone", () => {
  const doorStatus = fork.doorRefusals.find((r: any) => r.name === "fork_too_large").httpStatus;
  assert.equal(fork.transport.readTooLarge.length, 1);
  for (const c of fork.transport.readTooLarge) {
    const held = c.entries.find((e: any) => e.card.fork_id === c.request.fork_id);
    assert.ok(held !== undefined && check(forkSchema, "readRequest", c.request), c.name);
    assert.ok(check(forkSchema, "refusal", c.refusal), c.name);
    assert.equal(c.refusal.code, "fork_too_large");
    assert.ok(c.refusal.error.includes(held.card.fork_id), "the text names the fork id");
    assert.equal(hexOf(c.refusal), c.refusalHex, c.name);
    assert.equal(c.httpStatus, 422); assert.equal(c.httpStatus, doorStatus);
  }
});

test("fork transport: a stale answer whose card re-read is past the cap is the read-form fork_too_large (422), over a stored Refused(stale_fork)", () => {
  assert.equal(fork.transport.answerTooLarge.length, 1);
  const read = fork.transport.readTooLarge[0];
  for (const c of fork.transport.answerTooLarge) {
    const held = c.entries.find((e: any) => e.card.fork_id === c.request.fork_id);
    assert.ok(held !== undefined && check(forkSchema, "answerRequest", c.request), c.name);
    assert.notEqual(c.request.options_hash, held.card.options_hash, "the card moved: the answer is stale");
    assert.ok(check(forkSchema, "refusal", c.refusal), c.name);
    assert.equal(c.refusal.code, "fork_too_large");
    assert.ok(c.refusal.error.includes(held.card.fork_id), "the text names the fork id");
    assert.equal(hexOf(c.refusal), c.refusalHex, c.name);
    assert.equal(c.refusal.code, read.refusal.code, "the same refusal as the read form");
    assert.equal(c.httpStatus, 422); assert.equal(c.httpStatus, fork.doorRefusals.find((r: any) => r.name === "fork_too_large").httpStatus);
    assert.equal(c.outcome, "Refused"); assert.equal(c.code, "stale_fork"); assert.equal(c.counted, false);
  }
});

test("fork transport: a door request_id_conflict is a fault on read and list (503 unavailable) and a refusal only on the answer", () => {
  assert.deepEqual(fork.transport.doorFaults.map((c: any) => c.route), ["read", "list", "answer"]);
  for (const c of fork.transport.doorFaults) {
    const source = c.route === "answer" ? fork.doorRefusals.find((r: any) => r.name === c.doorCode) : fork.unavailable[0];
    assert.equal(c.responseHex, source.messagePackHex, c.name);
    assert.equal(hexOf(c.response), c.responseHex, c.name);
    assert.equal(c.httpStatus, source.httpStatus, c.name);
    assert.ok(check(forkSchema, c.route === "answer" ? "refusal" : "unavailable", c.response), c.name);
  }
});

test("fork transport: auth is bearer, then audience, then scope, and a votes token touches nothing", () => {
  const codes = (forkSchema.$defs.refusal.properties.code as { enum: string[] }).enum;
  const seen = new Set<string>();
  for (const c of fork.transport.authCases) {
    const got = authJudge(c.route, c.token, c.huginn_down);
    assert.equal(got, c.expect, c.name);
    if (got !== "ok" && got !== "unavailable") assert.ok(codes.includes(got), c.name);
    seen.add(`${c.route}:${got}`);
  }
  for (const want of ["list:insufficient_scope", "answer:insufficient_scope", "read:forbidden", "list:unavailable", "read:unavailable", "answer:unavailable", "read:unauthenticated", "answer:ok"]) assert.ok(seen.has(want), want);
});
