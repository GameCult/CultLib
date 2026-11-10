import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

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

test("vote API: every valid document validates, encodes to the vector bytes and decodes back", () => {
  for (const v of vote.valid) {
    assert.ok(check(voteSchema, v.def, v.message), v.name);
    assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    assert.deepEqual(decode(Buffer.from(v.messagePackHex, "hex")), v.message, v.name);
  }
});

test("vote API: every negative is refused by the schema, and its bytes are the vector's", () => {
  for (const v of vote.invalid) {
    assert.equal(check(voteSchema, v.def, v.message), false, `${v.name}: ${v.why}`);
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
  const doorCodes = (forkSchema.$defs.refusal.properties.code as { enum: string[] }).enum;
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
      assert.equal(check(forkSchema, def, v.message), false, `${v.name}: ${v.why}`);
      assert.equal(hexOf(v.message), v.messagePackHex, v.name);
    }
  }
});

const TAILS: Record<string, string> = {
  unauthenticated: "a verified session is required",
  body_forbidden_field: "answerer and authority are set by the receiver",
  body_unknown_field: "the body is a closed shape",
  body_invalid: "the body does not satisfy gamecult.fork.answer_request.v0",
  fork_unknown: "no such fork",
};
const REQUEST_KEYS = ["schemaVersion", "fork_id", "options_hash", "answer", "retracted", "retracts", "answered_at"];
const hasKey = (v: unknown, names: string[]): boolean =>
  typeof v === "object" && v !== null && Object.entries(v).some(([k, x]) => names.includes(k) || hasKey(x, names));
const SECONDS = (iso: string): number => Date.parse(iso);

// The reference judge: Huginn's half behind the second door as fork-card.md states it. Server-set fields come from the step.
function judge(start: Card, steps: { kind: string; body: Record<string, any> | null; server: Record<string, any> | null; card?: Card }[]) {
  let card = start;
  const answers: Record<string, any>[] = [];
  const responses: unknown[] = [];
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
        claim_in_future: SECONDS(b.answered_at) - SECONDS(s.received_at) > 300_000, answerer: s.answerer, authority: s.authority };
      answers.push(doc);
      return { schemaVersion: "gamecult.fork.answer_result.v0", answer: doc, card: code === "stale_fork" ? card : null };
    };
    const refuse = (code: string) => store("Refused", null, code);
    let response: unknown;
    if (s.answerer === null) response = door("unauthenticated");
    else if (hasKey(b, ["answerer", "authority"])) response = door("body_forbidden_field");
    else if (Object.keys(b).some((k) => !REQUEST_KEYS.includes(k)) || (b.answer && Object.keys(b.answer).some((k) => k !== "choice" && k !== "note"))) response = door("body_unknown_field");
    else if (!check(forkSchema, "answerRequest", b) || (!b.retracted && b.answer === null)) response = door("body_invalid");
    else if (b.fork_id !== card.fork_id) response = door("fork_unknown");
    else if (b.options_hash !== optionsHash(card)) response = refuse("stale_fork");
    else if (!card.deciders.includes(s.answerer)) response = refuse("not_a_decider");
    else if (card.status === "closed") response = refuse("fork_closed");
    else if (b.retracted || b.retracts !== null) {
      const target = answers.find((a) => a.answer_id === b.retracts && (a.outcome === "Counted" || a.outcome === "Revision") && a.options_hash === b.options_hash);
      if (!b.retracted || b.answer !== null || b.retracts === null || !target) response = refuse("bad_retraction");
      else if (answers.some((a) => a.outcome === "Retraction" && a.retracts === target.answer_id)) response = refuse("already_retracted");
      else if (card.ruled_from.includes(target.answer_id)) response = refuse("ruled");
      else response = store("Retraction", target.answer_id, null);
    } else if (b.answer.choice !== null && !card.options.some((o) => o.id === b.answer.choice)) response = refuse("choice_invalid");
    else {
      const first = counted();
      response = first === null ? store("Counted", null, null) : store("Revision", first, null);
    }
    responses.push(response);
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
    for (const s of sc.steps) if (s.response) assert.ok(check(forkSchema, "answerResult", s.response) || check(forkSchema, "refusal", s.response), sc.name);
  }
});

test("fork card: a body refused for its shape reaches Huginn never, so the record stores nothing for it", () => {
  const door = new Set(["unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown"]);
  let doorRefusals = 0;
  for (const sc of fork.scenarios) {
    for (const s of sc.steps) {
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
  for (const want of ["Counted", "Revision", "Retraction", "Refused", "unauthenticated", "body_forbidden_field", "body_unknown_field", "body_invalid", "fork_unknown", "stale_fork", "not_a_decider",
                      "fork_closed", "choice_invalid", "bad_retraction", "already_retracted", "ruled"]) {
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
