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
type Card = Shown & { fork_id: string; leans: string | null; deciders: string[]; status: string; ruling: string | null; ruled_from: string[]; options_hash: string };
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
  if (c.status === "closed" && c.ruling === null) errors.push("closed without a ruling");
  if (c.status === "open" && c.ruled_from.length > 0) errors.push("an open card cites an answer in ruled_from");
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
  assert.equal(byName.get("heimdall_E_decomposed_equals_D"), byName.get("heimdall_D_nfc_escapes_astral"), "an NFD and an NFC spelling hash alike");
  for (const moved of ["heimdall_B_recommendation_changed", "heimdall_C_option_order_changed", "heimdall_F_option_id_changed", "heimdall_G_depends_emptied", "option_text_changed",
                       "title_changed", "context_changed", "depends_order_changed", "depends_order_changed_reversed"]) {
    assert.notEqual(byName.get(moved), base, moved);
  }
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
  hidden.fork_id = "other:question:id"; hidden.leans = "yes"; hidden.deciders = ["x"]; hidden.status = "closed"; hidden.ruled_from = ["n1"];
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

// Rules a JSON Schema cannot say: a note is at most 4000 UTF-8 bytes (maxLength counts code points and is only the looser bound).
const noteRuleErrors = (def: string, message: any): string[] => {
  const note = def === "answerRequest" ? message?.answer?.note : def === "deskAnswer" ? message?.note : null;
  return note != null && Buffer.byteLength(note, "utf8") > 4000 ? ["note is over 4000 UTF-8 bytes"] : [];
};

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
        of: outcome === "Revision" ? link : null, retracts: outcome === "Retraction" ? link : null, code: outcome === "Refused" ? code : null,
        options_hash: b.options_hash, choice: a.choice, note: a.note, answered_at: b.answered_at, received_at: s.received_at,
        claim_in_future: SECONDS(b.answered_at) - SECONDS(s.received_at) > 300_000, answerer: s.answerer, authority: s.authority, request_id: b.request_id };
      answers.push(doc);
      const stored = { schemaVersion: "gamecult.fork.answer_result.v0", answer: doc, card: code === "stale_fork" ? card : null };
      seen.set(`${s.answerer}|${b.request_id}`, { body: structuredClone(b), response: structuredClone(stored) });
      return stored;
    };
    const refuse = (code: string) => store("Refused", null, code);
    const down = s.door_down === true, reached = s.stored === true;   // the second door failed to answer; "stored" says whether Huginn had the request by then
    const unavailable = () => ({ schemaVersion: "gamecult.fork.unavailable.v0", code: "unavailable", error: "unavailable: the outcome is not confirmed, try again with the same request_id", retryAfterSeconds: s.retry_after_seconds });
    let response: unknown;
    if (s.answerer === null) response = door("unauthenticated");
    else if (s.active === false) response = door("forbidden");
    else if (hasKey(b, ["answerer", "authority"])) response = door("body_forbidden_field");
    else if (Object.keys(b).some((k) => !REQUEST_KEYS.includes(k)) || (b.answer && Object.keys(b.answer).some((k) => k !== "choice" && k !== "note"))) response = door("body_unknown_field");
    else if (!check(forkSchema, "answerRequest", b) || noteRuleErrors("answerRequest", b).length > 0 || (!b.retracted && b.answer === null)) response = door("body_invalid");
    else if (down && !reached) response = unavailable();
    else if (seen.has(`${s.answerer}|${b.request_id}`)) { const prior = seen.get(`${s.answerer}|${b.request_id}`)!; response = isDeepStrictEqual(prior.body, b) ? structuredClone(prior.response) : door("request_id_conflict"); }
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
  return { responses, record: { schemaVersion: "gamecult.fork.record.v0", fork_id: card.fork_id, options_hash: optionsHash(card), counted_answer_id: counted(), answers } };
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
    for (const s of sc.steps) if (s.response) assert.ok(check(forkSchema, "answerResult", s.response) || check(forkSchema, "refusal", s.response) || check(forkSchema, "unavailable", s.response), sc.name);
  }
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
const entryRuleErrors = (e: { card: Card; record: { fork_id: string; options_hash: string; counted_answer_id: string | null; answers: Record<string, any>[] } }): string[] => {
  const errors = cardRuleErrors(e.card);
  if (e.record.fork_id !== e.card.fork_id) errors.push("record of another fork");
  if (e.record.options_hash !== e.card.options_hash) errors.push("record under another hash");
  const counted = e.record.answers.find((a) => (a.outcome === "Counted" || a.outcome === "Revision") && a.options_hash === e.card.options_hash
    && !e.record.answers.some((r) => r.outcome === "Retraction" && r.retracts === a.answer_id));
  if ((counted?.answer_id ?? null) !== e.record.counted_answer_id) errors.push("counted_answer_id is not derived");
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

test("fork transport: requests and entries validate and encode to the vector bytes, and every negative is refused", () => {
  const t = fork.transport;
  for (const [section, def] of [["listRequest", "listRequest"], ["readRequest", "readRequest"], ["entry", "entry"]] as const) {
    for (const v of t[section].valid) {
      assert.ok(check(forkSchema, def, v.message), v.name);
      if (def === "entry") assert.deepEqual(entryRuleErrors(v.message), [], v.name);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
    for (const v of t[section].invalid) {
      assert.ok(!check(forkSchema, def, v.message) || (def === "entry" && entryRuleErrors(v.message).length > 0), `${v.name}: ${v.why}`);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
  }
  for (const v of t.listInvalid) {
    assert.equal(check(forkSchema, "list", v.message), false, `${v.name}: ${v.why}`);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
  }
});

test("fork transport: every list request replays to the vector's response, newest first, paged by the cursor", () => {
  for (const sc of fork.transport.listScenarios) {
    for (const e of sc.entries) assert.deepEqual(entryRuleErrors(e), [], sc.name);
    let previous: { next_cursor: string | null } | null = null;
    for (const step of sc.steps) {
      const response = judgeList(sc.entries, sc.ordinal, step.request);
      assert.deepEqual(response, step.response, sc.name);
      assert.ok(check(forkSchema, "list", response), sc.name);
      assert.equal(hexOf(response), step.responseHex, sc.name);
      const orders = response.forks.map((f: any) => f.card.order);
      assert.deepEqual(orders, [...orders].sort((a: number, b: number) => b - a), sc.name);
      if (previous !== null) assert.equal(step.request.cursor, previous.next_cursor, `${sc.name}: the second request follows the first page's cursor`);
      previous = response;
    }
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
