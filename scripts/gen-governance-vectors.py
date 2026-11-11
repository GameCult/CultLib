#!/usr/bin/env python3
"""Writes contracts/cultnet/governance/{vote-api,fork-card}-vectors.json.

The bytes are spec-authored with msgpack-python (minimal ints, str8 only past 31 bytes, map keys in the order written here);
each runtime's conformance test re-encodes the `message` and must produce the same hex. Run: python3 scripts/gen-governance-vectors.py
Needs: pip install msgpack.
"""
import hashlib, json, pathlib, unicodedata
import msgpack

OUT = pathlib.Path(__file__).resolve().parent.parent / "contracts" / "cultnet" / "governance"
def hx(m): return msgpack.packb(m, use_bin_type=True).hex().upper()
def sha(b): return hashlib.sha256(b).hexdigest()

M1 = "5b1c2e9a-3f4d-4c8e-9a61-0d7e2f4b8c13"
M2 = "a7d3f0c2-6b15-4e29-8f3a-91c4d5e6f708"
V1 = "0c9f6a4e-7d2b-4a35-b8e1-3f5a6c7d8e90"
T0 = "2026-11-12T18:00:00Z"; T1 = "2026-11-15T18:00:00Z"; T2 = "2026-11-18T18:00:00Z"
SALT = "d41d8cd98f00b204e9800998ecf8427e5f1c0a7b3e92d6a48b15c7f0e3a9d2b6"

# ---------- vote API ----------
def commitment_bytes(motion, voter, persona, rnd, candidate, reason, salt):
    return f"gamecult.sealed_ballot.v0\n{motion}\n{voter}\n{persona}\n{rnd}\n{candidate}\n{reason}\n{salt}".encode("utf-8")

def ballot(motion=M1, rnd=1, candidate="Adopt the ledger", reason="It keeps the books honest.", salt=SALT, persona=""):
    return {"schema": "gamecult.sealed_ballot.v0", "motionId": motion, "round": rnd, "candidate": candidate,
            "reason": reason, "salt": salt, "persona": persona}
def digest(b, voter=V1):
    return sha(commitment_bytes(b["motionId"], voter, b["persona"], b["round"], b["candidate"], b["reason"], b["salt"]))

B_MEMBER = ballot()
B_PERSONA = ballot(persona="libby", candidate="Defer to round two", reason="The sealed window is too short to weigh both.")
C_MEMBER = digest(B_MEMBER); C_PERSONA = digest(B_PERSONA)

def receipt(commitment, revision=1, revealed=False, motion=M1):
    return {"schemaVersion": "gamecult.vote.receipt.v0", "motionId": motion, "voterUserAccountId": V1,
            "commitment": commitment, "revision": revision, "committedAtUtc": "2026-11-12T19:04:11.1234567Z", "revealed": revealed}
def motion(phase, your=None, mid=M1, high=False):
    return {"schemaVersion": "gamecult.vote.motion.v0", "id": mid, "title": "Ledger or lantern", "summary": "Pick one.",
            "round": 1, "priorMotionId": None, "ballotSchema": "gamecult.sealed_ballot.v0", "phase": phase,
            "candidates": ["Adopt the ledger", "Defer to round two"], "commitClosesAtUtc": T0, "closesAtUtc": T1, "highStakes": high,
            "yourVoterId": V1, "yourCommitment": your, "announcement": None}
def cast_commit(c, motion=M1): return {"schemaVersion": "gamecult.vote.cast.v0", "motionId": motion, "verb": "commit", "commitment": c, "ballot": None}
def cast_reveal(b): return {"schemaVersion": "gamecult.vote.cast.v0", "motionId": b["motionId"], "verb": "reveal", "commitment": None, "ballot": b}
def refusal(code, error, retry=None): return {"schemaVersion": "gamecult.vote.refusal.v0", "code": code, "error": error, "retryAfterSeconds": retry}
def item(i, title, phase, high=False): return {"id": i, "title": title, "round": 1, "phase": phase, "commitClosesAtUtc": T0, "closesAtUtc": T1, "highStakes": high}

valid = [
 ("motion_list_request_all", "motionListRequest", {"schemaVersion": "gamecult.vote.motion_list_request.v0", "phase": None}),
 ("motion_list_request_reveal", "motionListRequest", {"schemaVersion": "gamecult.vote.motion_list_request.v0", "phase": "Reveal"}),
 ("motion_list_two", "motionList", {"schemaVersion": "gamecult.vote.motion_list.v0", "motions": [item(M1, "Ledger or lantern", "Commit"), item(M2, "Second thoughts", "Closed", True)]}),
 ("motion_list_empty", "motionList", {"schemaVersion": "gamecult.vote.motion_list.v0", "motions": []}),
 ("motion_read_request", "motionReadRequest", {"schemaVersion": "gamecult.vote.motion_read_request.v0", "motionId": M1}),
 ("motion_commit_phase_no_receipt", "motion", motion("Commit")),
 ("motion_high_stakes_commit_phase", "motion", motion("Commit", high=True)),
 ("motion_reveal_phase_committed", "motion", motion("Reveal", receipt(C_MEMBER))),
 ("motion_closed_revealed", "motion", motion("Closed", receipt(C_MEMBER, 2, True))),
 ("cast_reveal_reason_140_astral_280_units", "cast", cast_reveal(ballot(reason="\U0001F600" * 140))),
 ("cast_commit", "cast", cast_commit(C_MEMBER)),
 ("cast_reveal_member", "cast", cast_reveal(B_MEMBER)),
 ("cast_reveal_persona", "cast", cast_reveal(B_PERSONA)),
 ("receipt_committed", "receipt", receipt(C_MEMBER)),
 ("receipt_revealed_echoes_no_ballot", "receipt", receipt(C_MEMBER, 2, True)),
]
refusals = [
 ("unauthenticated", 401, "no bearer, or one Heimdall does not know", refusal("unauthenticated", "A bearer token is required.")),
 ("reauthentication_required", 401, "a commit on a high-stakes motion with no sign-in fresh enough (Bifrost PR #37, D2); the voter signs in again at the top level and retries", refusal("reauthentication_required", "Sign in again to commit on this motion.")),
 ("forbidden", 403, "authenticated, not an Active member, or a Mind key used on a motion route it may not call", refusal("forbidden", "Only Active members may vote.")),
 ("not_found", 404, "unknown motion id, a standard (non-sealed) motion id, or reveal without a prior commit", refusal("not_found", "No such sealed-ballot motion.")),
 ("wrong_phase", 409, "commit outside Commit, reveal outside Reveal", refusal("wrong_phase", "The commit window is not open.")),
 ("contended", 409, "reveal raced the close fence; send it again", refusal("contended", "The reveal is contended; send it again.")),
 ("invalid", 400, "body fails vote-api.schema.json or an in-document rule (untrimmed candidate or reason, candidate not in the motion, round or motionId mismatch, persona wrong for the credential)", refusal("invalid", "The cast does not satisfy gamecult.vote.cast.v0.")),
 ("commitment_mismatch", 400, "reveal whose canonical bytes do not hash to the stored commitment", refusal("commitment_mismatch", "The ballot does not match the stored commitment.")),
 ("payload_too_large", 413, "body over 4096 bytes", refusal("payload_too_large", "The body exceeds 4096 bytes.")),
 ("held", 503, "the Ratatoskr voting brake is set (Bifrost PR #37, D5): no cast is read or stored while it is", refusal("held", "Voting is held.")),
 ("rate_limited", 429, "more than the per-account cast rate; Retry-After mirrors retryAfterSeconds", refusal("rate_limited", "Too many casts.", 30)),
]
def tweak(d, **kw):
    d = json.loads(json.dumps(d)); 
    for k, v in kw.items():
        if k.startswith("b_"): d["ballot"][k[2:]] = v
        else: d[k] = v
    return d
rev = cast_reveal(B_MEMBER)
invalid = [
 ("cast_commit_carries_a_ballot", "cast", tweak(cast_commit(C_MEMBER), ballot=B_MEMBER), "a commit names exactly the commitment; a ballot beside it would put content on the wire before the reveal"),
 ("cast_reveal_carries_a_commitment", "cast", tweak(rev, commitment=C_MEMBER), "a reveal names exactly the ballot"),
 ("cast_commit_without_commitment", "cast", tweak(cast_commit(C_MEMBER), commitment=None), "commit needs its hash"),
 ("cast_verb_unknown", "cast", tweak(rev, verb="recast"), "verb is commit or reveal"),
 ("cast_commitment_uppercase", "cast", cast_commit(C_MEMBER.upper()), "commitment is lowercase hex"),
 ("cast_commitment_63_chars", "cast", cast_commit(C_MEMBER[:63]), "commitment is 64 hex characters"),
 ("cast_unknown_key", "cast", tweak(cast_commit(C_MEMBER), note="hi"), "closed document"),
 ("cast_motion_id_uppercase", "cast", cast_commit(C_MEMBER, M1.upper()), "ids are lowercase D-format uuids"),
 ("ballot_reason_empty", "cast", tweak(rev, b_reason=""), "reason is 1 to 280 characters"),
 ("ballot_reason_281", "cast", tweak(rev, b_reason="x" * 281), "reason is at most 280 characters"),
 ("ballot_reason_141_astral_282_units", "cast", tweak(rev, b_reason="\U0001F600" * 141), "reason is at most 280 UTF-16 code units (vote-api.md); the schema counts code points and accepts this, so a rule refuses it"),
 ("ballot_reason_multiline", "cast", tweak(rev, b_reason="one\ntwo"), "reason is a single line"),
 ("ballot_reason_leading_space", "cast", tweak(rev, b_reason=" because"), "the contract refuses untrimmed text; it never trims for the sender, so the hashed bytes are the sent bytes"),
 ("ballot_candidate_trailing_nbsp", "cast", tweak(rev, b_candidate="Adopt the ledger "), "NO-BREAK SPACE is White_Space; trimming is judged by the same list as write-receipt Required fields"),
 ("ballot_salt_31_hex", "cast", tweak(rev, b_salt=SALT[:31]), "salt is 32 to 128 lowercase hex characters"),
 ("ballot_salt_uppercase", "cast", tweak(rev, b_salt=SALT.upper()), "salt is lowercase hex"),
 ("ballot_schema_wrong", "cast", tweak(rev, b_schema="gamecult.sealed_ballot.v1"), "ballot schema is gamecult.sealed_ballot.v0"),
 ("ballot_persona_with_space", "cast", tweak(rev, b_persona="my mind"), "persona is printable ASCII without space, at most 64"),
 ("ballot_round_zero", "cast", tweak(rev, b_round=0), "rounds start at 1"),
 ("receipt_echoes_candidate", "receipt", dict(receipt(C_MEMBER, 2, True), candidate="Adopt the ledger"), "sealed casts never echo ballot content; the receipt is closed"),
 ("receipt_echoes_salt", "receipt", dict(receipt(C_MEMBER, 2, True), salt=SALT), "the salt never comes back before close"),
 ("receipt_revision_zero", "receipt", receipt(C_MEMBER, 0), "revision starts at 1"),
 ("receipt_time_without_zone", "receipt", dict(receipt(C_MEMBER), committedAtUtc="2026-11-12T19:04:11"), "instants are UTC with a Z"),
 ("motion_one_candidate", "motion", dict(motion("Commit"), candidates=["Only"]), "a motion has two or more candidates"),
 ("motion_phase_unknown", "motion", dict(motion("Commit"), phase="Open"), "phase is Commit, Reveal or Closed"),
 ("motion_high_stakes_not_boolean", "motion", dict(motion("Commit"), highStakes="yes"), "highStakes is a boolean, fixed at open"),
 ("motion_without_high_stakes", "motion", {k: v for k, v in motion("Commit").items() if k != "highStakes"}, "map-mode writes every key; a motion states whether it is high-stakes"),
 ("motion_list_item_without_high_stakes", "motionListItem", {k: v for k, v in item(M1, "x", "Commit").items() if k != "highStakes"}, "every list item states whether the motion is high-stakes"),
 ("motion_leaks_tally", "motion", dict(motion("Reveal"), tally=[]), "mid-vote a client sees no tallies; the document is closed"),
 ("motion_list_item_without_phase", "motionList", {"schemaVersion": "gamecult.vote.motion_list.v0", "motions": [{k: v for k, v in item(M1, "x", "Commit").items() if k != "phase"}]}, "every item names its phase"),
 ("refusal_code_unknown", "refusal", refusal("teapot", "no"), "the code set is closed"),
 ("refusal_without_error", "refusal", dict(refusal("invalid", "x"), error=""), "error is non-empty"),
]
pairs = [
 {"name": "member_commit_matches_reveal", "voterUserAccountId": V1, "commit": cast_commit(C_MEMBER), "reveal": cast_reveal(B_MEMBER), "matches": True},
 {"name": "persona_commit_matches_reveal", "voterUserAccountId": V1, "commit": cast_commit(C_PERSONA), "reveal": cast_reveal(B_PERSONA), "matches": True},
 {"name": "reveal_with_changed_reason_does_not_match", "voterUserAccountId": V1, "commit": cast_commit(C_MEMBER), "reveal": cast_reveal(ballot(reason="It keeps the books honest!")), "matches": False, "refusal": "commitment_mismatch"},
 {"name": "reveal_under_another_voter_does_not_match", "voterUserAccountId": M2, "commit": cast_commit(C_MEMBER), "reveal": cast_reveal(B_MEMBER), "matches": False, "refusal": "commitment_mismatch"},
 {"name": "persona_dropped_does_not_match", "voterUserAccountId": V1, "commit": cast_commit(C_PERSONA), "reveal": cast_reveal(ballot(persona="", candidate=B_PERSONA["candidate"], reason=B_PERSONA["reason"])), "matches": False, "refusal": "commitment_mismatch"},
]
commitments = []
for name, b, voter in [("member", B_MEMBER, V1), ("persona", B_PERSONA, V1), ("non_ascii", ballot(candidate="Café ☕", reason="naïve – but fine"), V1),
                       ("decomposed_e_is_another_ballot", ballot(candidate="Café ☕", reason="naïve – but fine"), V1)]:
    cb = commitment_bytes(b["motionId"], voter, b["persona"], b["round"], b["candidate"], b["reason"], b["salt"])
    commitments.append({"name": name, "voterUserAccountId": voter, "ballot": b, "canonicalHex": cb.hex().upper(), "sha256": sha(cb)})

vote = {
  "provenance": "Spec-authored with msgpack-python (scripts/gen-governance-vectors.py); every runtime re-encodes `message` and must match messagePackHex. Commitment hashes follow vote-api.md 'Commitment'.",
  "commitment": commitments,
  "valid": [{"name": n, "def": d, "message": m, "messagePackHex": hx(m)} for n, d, m in valid],
  "invalid": [{"name": n, "def": d, "message": m, "why": w, "messagePackHex": hx(m)} for n, d, m, w in invalid],
  "pairs": pairs,
  "refusals": [{"name": n, "httpStatus": s, "trigger": t, "message": m, "messagePackHex": hx(m)} for n, s, t, m in refusals],
}
(OUT / "vote-api-vectors.json").write_text(json.dumps(vote, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

# ---------- fork card ----------
# Rule source: /opt/commons/handoff/fork-desk-answer-binding-20261110.md (Heimdall), sections 1.1 to 1.5 and 1.3a. The seven hash
# vectors A to G below are that file's, with its expected digests asserted.
def jstr(x):
    out = ['"']
    for ch in x:
        o = ord(ch)
        if ch == '"': out.append('\\"')
        elif ch == "\\": out.append("\\\\")
        elif ch == "\b": out.append("\\b")
        elif ch == "\t": out.append("\\t")
        elif ch == "\n": out.append("\\n")
        elif ch == "\f": out.append("\\f")
        elif ch == "\r": out.append("\\r")
        elif o < 0x20: out.append("\\u%04x" % o)
        else: out.append(ch)
    out.append('"')
    return "".join(out)
def nfc(x): return unicodedata.normalize("NFC", x)
def canonical_json(title, context, options, recommendation, depends):
    """RFC 8785 over {context, depends, options:[{id,text}], recommendation, title} after NFC on every string; keys sorted; no numbers. (None is accepted only to check Heimdall's null digests.)"""
    opts = ",".join('{"id":%s,"text":%s}' % (jstr(nfc(o["id"])), jstr(nfc(o["text"]))) for o in options)
    dep = ",".join(jstr(nfc(d)) for d in depends)
    rec = "null" if recommendation is None else jstr(nfc(recommendation))
    return ('{"context":%s,"depends":[%s],"options":[%s],"recommendation":%s,"title":%s}' % (jstr(nfc(context)), dep, opts, rec, jstr(nfc(title)))).encode("utf-8")

OP = "e0d1c2b3-a495-4687-8879-6a5b4c3d2e1f"; ST = "11111111-2222-4333-8444-555555555555"; HM = "9f8e7d6c-5b4a-4392-8170-0f1e2d3c4b5a"
FID = "gamecult-chat:question:slot-mark-consent"
OPT = [{"id": "yes", "text": "Share it."}, {"id": "no", "text": "Keep it local."}]
DEP = ["gamecult-chat:question:file-zone-audience"]
def chash(c): return sha(canonical_json(c["title"], c["context"], c["options"], c["recommendation"], c["depends"]))
def card(**kw):
    c = {"schemaVersion": "gamecult.fork.card.v0", "fork_id": FID, "title": "Slot mark consent", "context": "Should a slot mark be shared?",
         "options": OPT, "recommendation": "no", "depends": DEP, "leans": None, "flag": None,
         "raised_in": {"kind": "CutSpec", "id": "cultlib-gaps:cut_spec:cut-slot-tolerance.r4"}, "raised_at": "2026-11-10T09:30:00Z", "deciders": [OP], "decider_count": 1,
         "status": "open", "ruling": None, "ruled_from": [], "order": 3, "options_hash": ""}
    c.update(kw)
    if "decider_count" not in kw: c["decider_count"] = len(c["deciders"])   # the decider view: the count equals the ids; a withheld card passes its own
    c["options_hash"] = chash(c); return c
CARD = card(); H = CARD["options_hash"]

hashes = []
CLAIMED = {"A": "7d95fc87d532420611fe415c1def27a3447617bb68235f46e3af130aea361c48", "B": "14b8ffbdb61106dbc06f69f3fc8e2072af7a205d766e4cc040e89733013d54dc",
           "C": "ac67b41539f869f6203ac0908819df046e4328b27795743a919bd4ab390596b5", "D": "71d5c413478ad06aaa1cbdd9e91c53f0cb4cef1dbd4c51bf3d473bccc6a5a272",
           "E": "71d5c413478ad06aaa1cbdd9e91c53f0cb4cef1dbd4c51bf3d473bccc6a5a272", "F": "fb53aa3a9f7e74029f755d774064eedd240ab90d8fc64723f97c31b3d2846d40",
           "G": "f856edce121db4121b5f1af400b1c471ebbfa6579a042aca9a34ae31c15f5c86"}
def hv(name, c, heimdall=None):
    b = canonical_json(c["title"], c["context"], c["options"], c["recommendation"], c["depends"])
    if heimdall: print("heimdall", heimdall, "same" if CLAIMED[heimdall] == sha(b) else f"DIFFERS mine={sha(b)} his={CLAIMED[heimdall]}")
    hashes.append({"name": name, "heimdall": heimdall, "title": c["title"], "context": c["context"], "options": c["options"],
                   "recommendation": c["recommendation"], "depends": c["depends"], "canonical": b.decode("utf-8"),
                   "canonicalHex": b.hex().upper(), "sha256": sha(b)})
CD = dict(title="Caf\u00e9 \u201cdesk\u201d", context="Line one\nLine two \u2014 \U0001F600", options=[{"id": "a", "text": "x"}, {"id": "b", "text": "y"}], recommendation="a", depends=[])
hv("heimdall_A_five_members", CARD, "A")
hv("heimdall_B_recommendation_changed", card(recommendation="yes"), "B")
hv("heimdall_C_option_order_changed", card(options=[OPT[1], OPT[0]]), "C")
hv("heimdall_D_nfc_escapes_astral", card(**CD), "D")
hv("heimdall_E_decomposed_equals_D", card(**dict(CD, title="Cafe\u0301 \u201cdesk\u201d")), "E")
hv("heimdall_F_option_id_changed", card(options=[{"id": "share", "text": "Share it."}, OPT[1]]), "F")
hv("heimdall_G_depends_emptied", card(depends=[]), "G")
hv("option_text_changed", card(options=[dict(OPT[0], text="Share it!"), OPT[1]]))
hv("title_changed", card(title="Slot mark consent?"))
hv("context_changed", card(context="Should slot marks be shared?"))
hv("depends_order_changed", card(depends=DEP + ["x:question:y"]))
hv("depends_order_changed_reversed", card(depends=["x:question:y"] + DEP))
hv("hidden_members_do_not_move_it", card(leans="yes", flag="provisional", deciders=[], decider_count=2, status="closed", ruling="r:1", ruled_from=["rc-1"], order=9, raised_in=None, raised_at="2027-01-02T03:04:05.123Z", fork_id="other:question:id"))
hv("control_characters_and_markup_are_escaped_minimally", card(title='q " b \\ t \t n \n bell \u0007 del \u007f', context="</script> \u2028 &"))
hv("astral_option_id_and_text", card(options=[{"id": "\U0001F600", "text": "\U0001D11E"}, OPT[1]]))
assert [h["sha256"] for h in hashes if h["heimdall"] == "D"] == [h["sha256"] for h in hashes if h["heimdall"] == "E"]
assert len({h["sha256"] for h in hashes if h["name"] in ("heimdall_A_five_members", "hidden_members_do_not_move_it")}) == 1

RID = [0]
def next_rid():
    RID[0] += 1; n = RID[0]
    return f"{n:08x}-0000-4000-8000-{n:012x}"
def body(choice=None, note=None, h=None, at="2026-11-12T19:02:00Z", fork=FID, retracts=None, answer="auto", rid=None):
    a = {"choice": choice, "note": note} if answer == "auto" else answer
    if answer == "auto" and choice is None and note is None: a = None
    return {"schemaVersion": "gamecult.fork.answer_request.v0", "fork_id": fork, "options_hash": h or H, "answer": a,
            "retracted": retracts is not None, "retracts": retracts, "answered_at": at, "request_id": rid or next_rid()}
def retraction(target, **kw): return body(retracts=target, **kw)
NOTE = "Keep it local: a slot mark is the author's call, and sharing it is a separate act."
def srv(aid, who, at, auth="Operator"): return {"answer_id": aid, "answerer": who, "authority": auth, "received_at": at}
TAIL = {"unauthenticated": "a verified session is required", "body_forbidden_field": "answerer and authority are set by the receiver", "body_unknown_field": "the body is a closed shape",
        "body_invalid": "the body does not satisfy gamecult.fork.answer_request.v0", "fork_unknown": "no such fork",
        "insufficient_scope": "the token lacks the scope this route needs", "forbidden": "only Active members may use the desk", "request_id_conflict": "this request_id was used for a different body", "request_invalid": "the request does not satisfy its document"}
DOOR = set(TAIL)
def refusal_doc(code): return {"schemaVersion": "gamecult.fork.refusal.v0", "code": code, "error": f"{code}: {TAIL[code]}"}
def unavailable_doc(retry=30): return {"schemaVersion": "gamecult.fork.unavailable.v0", "code": "unavailable", "error": "unavailable: the outcome is not confirmed, try again with the same request_id", "retryAfterSeconds": retry}
def future(a, r):
    import datetime as dt
    p = lambda x: dt.datetime.fromisoformat(x.replace("Z", "+00:00"))
    return (p(a) - p(r)).total_seconds() > 300
def da(outcome, link, code, b, server):
    a = b["answer"] or {"choice": None, "note": None}
    return {"schemaVersion": "gamecult.desk_answer.v0", "answer_id": server["answer_id"], "fork_id": b["fork_id"], "outcome": outcome,
            "of": link if outcome == "Revision" else None, "retracts": link if outcome == "Retraction" else (b["retracts"] if outcome == "Refused" else None), "code": code if outcome == "Refused" else None,
            "options_hash": b["options_hash"], "choice": a["choice"], "note": a["note"], "answered_at": b["answered_at"], "received_at": server["received_at"],
            "claim_in_future": future(b["answered_at"], server["received_at"]), "answerer": server["answerer"], "authority": server["authority"], "request_id": b["request_id"]}
def result(answer, c=None): return {"schemaVersion": "gamecult.fork.answer_result.v0", "answer": answer, "card": c, "counted_answer_id": None}   # sc() fills counted_answer_id
def record(counted, answers, h=H, fork=FID, answerer_count=None):
    # answerer_count: distinct answerers over every stored answer, refusals included; a withheld record passes the count it kept
    n = len({a["answerer"] for a in answers}) if answerer_count is None else answerer_count
    return {"schemaVersion": "gamecult.fork.record.v0", "fork_id": fork, "options_hash": h, "counted_answer_id": counted, "answerer_count": n, "answers": answers}
T = lambda m, sec=5: f"2026-11-12T19:{m:02d}:{sec:02d}Z"
def step(kind, body_=None, server=None, response=None, **kw):
    d = {"kind": kind, "body": body_, "server": server, "response": response}; d.update(kw); return d
def ok(b, server, outcome, of):
    a = da(outcome, of, None, b, server); return step("answer", b, server, result(a)), a
def rf(b, server, code, c=None):
    a = da("Refused", None, code, b, server); return step("answer", b, server, result(a, c)), a
def door(b, server, code): return step("answer", b, server, refusal_doc(code)), None
def sc(name, c, items, counted, h=None):
    # answer_result.counted_answer_id: the counted answer after the step, derived from the answers stored so far under the card's current hash
    steps, answers, cur = [], [], c["options_hash"]
    def live(): return next((a["answer_id"] for a in answers if a["outcome"] in ("Counted", "Revision") and a["options_hash"] == cur
                             and not any(r["outcome"] == "Retraction" and r["retracts"] == a["answer_id"] for r in answers)), None)
    for i in items:
        st = i[0] if isinstance(i, tuple) else i
        if isinstance(i, tuple) and i[1] is not None: answers.append(i[1])
        if st["kind"] == "card": cur = st["card"]["options_hash"]
        if st["kind"] == "answer" and (st["response"] or {}).get("schemaVersion") == "gamecult.fork.answer_result.v0":
            st = dict(st, response=dict(st["response"], counted_answer_id=live()))
        steps.append(st)
    assert live() == counted, (name, live(), counted)
    return (name, c, steps, record(counted, answers, h or c["options_hash"]))

x1 = body("no", NOTE); sx1 = srv("n1", OP, T(2))
x2 = body("yes", "On reflection, share it.", at="2026-11-12T19:06:00Z"); sx2 = srv("n2", OP, T(6))
y1 = body("yes", "Share it, the audience is settled.", at="2026-11-12T19:05:00Z"); sy1 = srv("n3", HM, T(5), auth="Steward")
z1 = body("yes", "Share, please."); sz = srv("n4", ST, T(3), auth="Member")
pick = body("no", None); words = body(None, NOTE)
STALE_H = chash(card(recommendation="yes"))
def stale_body(**kw): return body("no", NOTE, h=STALE_H, **kw)
FORKC = card(status="closed", ruling="cultlib-gaps:resolution:question.slot-mark-consent.n1")
RULED = card(status="closed", ruling="cultlib-gaps:resolution:question.slot-mark-consent.n1", ruled_from=["n1"])
Y_CARD = card(deciders=[OP, HM])
fork_edit = card(title="Slot mark consent (revised)"); H2 = fork_edit["options_hash"]
RET = lambda t: retraction(t, at="2026-11-12T19:09:00Z")
def sr(aid, who=OP, m=9, auth="Operator"): return srv(aid, who, T(m), auth)
MATRIX = [step("matrix_event", None, None, None, matrix_kind=k, choice=c) for k, c in (("m.replace", "yes"), ("m.redaction", None), ("m.reaction", "yes"))]
X1 = ok(x1, sx1, "Counted", None)
REPLAY1 = (step("answer", x1, dict(sx1, answer_id="n9"), X1[0]["response"]), None)
LOST = [(step("answer", x1, dict(sx1, door_down=True, stored=True, retry_after_seconds=30), unavailable_doc(30)), X1[1])]
STALE_BODY = stale_body(); STALE_RF = rf(STALE_BODY, sx1, "stale_fork", c=CARD)
OFFLIST = rf(x1, srv("n1", ST, T(2), "Member"), "not_a_decider")
OFFLIST_REPLAY = (step("answer", x1, srv("n9", ST, T(4), "Member"), OFFLIST[0]["response"]), None)
scen = [
 sc("operator_answers_and_is_counted", CARD, [X1], "n1"),
 sc("choice_alone_is_counted", CARD, [ok(pick, sx1, "Counted", None)], "n1"),
 sc("note_alone_is_counted_a_clear_pick", CARD, [ok(words, sx1, "Counted", None)], "n1"),
 sc("V1_answerer_in_the_body_is_refused_at_the_door_and_stores_nothing", CARD, [door(dict(x1, answerer="x"), sx1, "body_forbidden_field")], None),
 sc("V1_authority_nested_in_answer_is_refused", CARD, [door(dict(x1, answer={"choice": "no", "note": None, "authority": "Operator"}), sx1, "body_forbidden_field")], None),
 sc("V1_both_are_refused_not_stripped", CARD, [door(dict(x1, answerer=OP, authority="Operator"), sx1, "body_forbidden_field")], None),
 sc("M5_a_body_naming_deciders_is_an_unknown_key_the_decider_list_is_never_the_bodys", CARD, [door(dict(x1, deciders=[ST]), sx1, "body_unknown_field")], None),
 sc("M1_a_body_naming_a_mind_is_an_unknown_key_the_mind_is_the_routes", CARD, [door(dict(x1, mind="nibu"), sx1, "body_unknown_field")], None),
 sc("an_unknown_key_is_refused_the_body_is_closed", CARD, [door(dict(x1, mood="happy"), sx1, "body_unknown_field")], None),
 sc("an_answer_with_neither_choice_nor_note_is_invalid", CARD, [door(body(answer={"choice": None, "note": None}), sx1, "body_invalid")], None),
 sc("a_non_retraction_without_an_answer_is_invalid", CARD, [door(body(answer=None), sx1, "body_invalid")], None),
 sc("unavailable_before_huginn_stored_nothing_is_retried_into_a_Counted_answer", CARD, [step("answer", x1, dict(sx1, answer_id="n9", door_down=True, retry_after_seconds=30), unavailable_doc(30)), ok(x1, sx1, "Counted", None)], "n1"),
 sc("unavailable_after_huginn_stored_the_retry_with_the_same_request_id_returns_the_stored_outcome", CARD, LOST + [REPLAY1], "n1"),
 sc("a_retry_never_adds_a_Revision", CARD, [X1, REPLAY1], "n1"),
 sc("Q1_a_replayed_stale_fork_returns_the_CURRENT_card_and_stores_nothing_new", CARD, [STALE_RF, step("card", None, None, None, card=fork_edit),
    (step("answer", STALE_BODY, dict(sx1, answer_id="n9"), result(STALE_RF[1], fork_edit)), None)], None, H2),
 sc("V13_only_answered_at_changed_is_request_id_conflict", CARD, [X1, door(dict(x1, answered_at="2026-11-12T19:03:00Z"), dict(sx1, answer_id="n9"), "request_id_conflict")], "n1"),
 sc("V14_an_identical_body_from_another_account_gets_its_own_Counted_and_never_the_first_accounts_outcome", Y_CARD, [OFFLIST, ok(x1, srv("n2", HM, T(3), "Steward"), "Counted", None), OFFLIST_REPLAY], "n2"),
 sc("V14_an_identical_body_from_another_decider_is_its_own_Revision_and_the_first_retry_still_returns_n1", Y_CARD, [X1, ok(x1, sy1, "Revision", "n1"), (step("answer", x1, dict(sx1, answer_id="n9"), X1[0]["response"]), None)], "n1"),
 sc("V16_Q5_a_forbidden_request_binds_no_request_id", CARD, [door(x1, dict(sx1, answer_id="n9", active=False), "forbidden"), ok(x1, sx1, "Counted", None)], "n1"),
 sc("V17_Q6_a_body_refused_at_the_door_binds_no_request_id", CARD, [door(dict(x1, mood="happy"), dict(sx1, answer_id="n9"), "body_unknown_field"), ok(x1, sx1, "Counted", None)], "n1"),
 sc("V17_Q6_an_unknown_fork_binds_no_request_id", CARD, [door(dict(x1, fork_id="gamecult-chat:question:nope"), dict(sx1, answer_id="n9"), "fork_unknown"), ok(x1, sx1, "Counted", None)], "n1"),
 sc("the_same_request_id_with_another_body_is_request_id_conflict", CARD, [X1, door(dict(x1, answer={"choice": "yes", "note": None}), dict(sx1, answer_id="n9"), "request_id_conflict")], "n1"),
 sc("the_same_request_id_from_another_account_is_another_request", Y_CARD, [X1, ok(dict(y1, request_id=x1["request_id"]), sy1, "Revision", "n1")], "n1"),
 sc("a_non_active_member_is_forbidden_before_the_body_is_read", CARD, [door(dict(x1, answerer="x"), dict(sx1, active=False), "forbidden"), door(x1, dict(sx1, active=False), "forbidden"),
                                                                       door(dict(x1, mood="happy"), dict(sx1, active=False, door_down=True, retry_after_seconds=30), "forbidden")], None),
 sc("no_session_is_unauthenticated", CARD, [door(x1, srv("n1", None, T(2), None), "unauthenticated")], None),
 sc("an_unknown_fork_is_refused_at_the_door", CARD, [door(body("no", NOTE, fork="gamecult-chat:question:nope"), sx1, "fork_unknown")], None),
 sc("V2_stale_hash_is_refused_not_counted_and_stored_as_Refused", CARD, [STALE_RF], None),
 sc("V2_a_stale_answer_then_a_fresh_one_the_fresh_one_is_counted", CARD, [rf(stale_body(), sx1, "stale_fork", c=CARD), ok(x1, sx2, "Counted", None)], "n2"),
 sc("V3_an_answer_off_the_decider_list_is_Refused_not_a_decider_and_stored", CARD, [rf(z1, sz, "not_a_decider")], None),
 sc("a_non_decider_with_a_stale_hash_is_stale_first", CARD, [rf(dict(z1, options_hash=STALE_H), sz, "stale_fork", c=CARD)], None),
 sc("V4_a_closed_fork_refuses_and_stores_the_refusal", FORKC, [rf(x1, sx1, "fork_closed")], None),
 sc("V5_second_answer_from_one_decider_is_a_Revision_the_first_stays_counted", CARD, [X1, ok(x2, sx2, "Revision", "n1")], "n1"),
 sc("V5_receiver_order_decides_not_answered_at", CARD, [ok(x2, srv("n1", OP, T(2)), "Counted", None), ok(x1, srv("n2", OP, T(3)), "Revision", "n1")], "n1"),
 sc("V5_reversed_arrival_reverses_the_result", CARD, [ok(x1, srv("n1", OP, T(2)), "Counted", None), ok(x2, srv("n2", OP, T(3)), "Revision", "n1")], "n1"),
 sc("a_second_decider_answer_is_recorded_as_a_Revision", Y_CARD, [X1, ok(y1, sy1, "Revision", "n1")], "n1"),
 sc("a_claim_exactly_five_minutes_ahead_is_not_flagged", CARD, [ok(body("no", NOTE, at="2026-11-12T19:07:05Z"), sx1, "Counted", None)], "n1"),
 sc("a_claim_five_minutes_and_half_a_second_ahead_is_flagged_from_the_stored_received_at", CARD, [ok(body("no", NOTE, at="2026-11-12T19:07:05.500Z"), sx1, "Counted", None)], "n1"),
 sc("a_claim_more_than_five_minutes_ahead_is_accepted_and_flagged", CARD, [ok(body("no", NOTE, at="2026-11-12T19:09:00Z"), sx1, "Counted", None)], "n1"),
 sc("V6_a_matrix_edit_redaction_or_reaction_changes_nothing", CARD, [X1] + MATRIX, "n1"),
 sc("V7_a_choice_that_is_not_an_option_is_Refused_choice_invalid", CARD, [rf(body("maybe", None), sx1, "choice_invalid")], None),
 sc("a_changed_card_stops_the_old_answers_counting_and_a_new_answer_is_counted", CARD,
    [X1, step("card", None, None, None, card=fork_edit), ok(body("no", NOTE, h=H2), sx2, "Counted", None)], "n2", H2),
 sc("a_changed_card_leaves_no_counted_answer_until_one_arrives", CARD, [X1, step("card", None, None, None, card=fork_edit)], None, H2),
 sc("V8_retraction_with_an_answer_is_bad", CARD, [X1, rf(dict(retraction("n1"), answer={"choice": "no", "note": None}), sr("n9"), "bad_retraction")], "n1"),
 sc("V8_retraction_without_retracts_is_bad", CARD, [X1, rf(dict(retraction("n1"), retracts=None), sr("n9"), "bad_retraction")], "n1"),
 sc("V8_retracts_on_a_plain_answer_is_bad", CARD, [X1, rf(dict(x2, retracts="n1"), sr("n9"), "bad_retraction")], "n1"),
 sc("V8_retraction_naming_an_unknown_answer_is_bad", CARD, [X1, rf(retraction("n99"), sr("n9"), "bad_retraction")], "n1"),
 sc("V8_retraction_naming_a_Refused_answer_is_bad", CARD, [rf(stale_body(), sx1, "stale_fork", c=CARD), rf(retraction("n1"), sr("n9"), "bad_retraction")], None),
 sc("V8_retracting_twice_is_already_retracted", CARD, [X1, ok(RET("n1"), sr("n8"), "Retraction", "n1"), rf(RET("n1"), sr("n9", m=10), "already_retracted")], None),
 sc("V8_retraction_under_a_stale_hash_is_stale_fork", CARD, [X1, rf(retraction("n1", h=STALE_H), sr("n9"), "stale_fork", c=CARD)], "n1"),
 sc("V9_another_decider_retracts_and_the_answer_stops_counting", Y_CARD, [X1, ok(RET("n1"), sr("n7", HM, 9, "Steward"), "Retraction", "n1")], None),
 sc("V9_an_account_off_the_list_cannot_retract", CARD, [X1, rf(RET("n1"), sr("n6", ST, 9, "Member"), "not_a_decider")], "n1"),
 sc("V10a_a_ruling_that_closed_the_fork_makes_a_retraction_fork_closed", CARD, [X1, step("card", None, None, None, card=RULED), rf(RET("n1"), sr("n9"), "fork_closed")], "n1"),
 sc("V11_retracting_the_counted_answer_makes_the_next_decider_answer_counted", Y_CARD, [X1, ok(y1, sy1, "Revision", "n1"), ok(RET("n1"), sr("n8"), "Retraction", "n1")], "n3"),
 sc("V11_retracting_an_answer_that_was_not_counted_changes_nothing", Y_CARD, [X1, ok(y1, sy1, "Revision", "n1"), ok(RET("n3"), sr("n8"), "Retraction", "n3")], "n1"),
 sc("after_every_answer_is_retracted_a_fresh_answer_is_Counted_again", CARD, [X1, ok(RET("n1"), sr("n8"), "Retraction", "n1"), ok(x2, sx2, "Counted", None)], "n2"),
]
# The three illegal forms (R1) under fixed request ids (a fresh next_rid() here would move every later vector). A and C differ only in `retracted`.
IL_A = dict(retraction("n1", rid="0000a001-0000-4000-8000-00000000a001"), answer={"choice": "no", "note": None})   # a retraction carrying an answer
IL_B = dict(retraction("n1", rid="0000b001-0000-4000-8000-00000000b001"), retracts=None)                           # a retraction naming no target
IL_C = dict(body("no", None, rid="0000a001-0000-4000-8000-00000000a001"), retracts="n1")                          # a plain answer carrying a target
assert {k: v for k, v in IL_A.items() if k != "retracted"} == {k: v for k, v in IL_C.items() if k != "retracted"} and IL_A["retracted"] and not IL_C["retracted"]
def replayed(first, server): return (step("answer", first[0]["body"], dict(server, answer_id="n9"), first[0]["response"]), None)
for _n, _f in (("a_retraction_with_an_answer", IL_A), ("a_retraction_with_no_target", IL_B), ("a_plain_answer_with_a_target", IL_C)):
    _r = rf(_f, sr("n2"), "bad_retraction")
    scen.append(sc(f"R1_{_n}_is_a_stored_bad_retraction_and_a_retry_replays_it", CARD, [X1, _r, replayed(_r, sr("n2"))], "n1"))
scen += [
 sc("a_replay_returns_the_counted_answer_id_current_at_the_retry_not_the_one_stored", CARD, [X1, ok(RET("n1"), sr("n8"), "Retraction", "n1"), REPLAY1], None),
 sc("R1_retracted_true_with_an_answer_and_no_target_is_a_stored_bad_retraction", CARD, [X1, rf(dict(IL_A, retracts=None), sr("n2"), "bad_retraction")], "n1"),
 sc("R1_retracted_true_then_false_under_one_request_id_is_request_id_conflict", CARD, [X1, rf(IL_A, sr("n2"), "bad_retraction"), door(IL_C, sr("n9"), "request_id_conflict")], "n1"),
 sc("R1_retracted_false_then_true_under_one_request_id_is_request_id_conflict", CARD, [X1, rf(IL_C, sr("n2"), "bad_retraction"), door(IL_A, sr("n9"), "request_id_conflict")], "n1"),
 sc("R1_a_target_less_retraction_with_retracted_flipped_is_body_invalid_before_any_request_id_lookup", CARD, [X1, rf(IL_B, sr("n2"), "bad_retraction"), door(dict(IL_B, retracted=False), sr("n9"), "body_invalid")], "n1"),
]
REASON_CODES = ["stale_fork", "not_a_decider", "fork_closed", "choice_invalid", "bad_retraction", "already_retracted"]
HTTP = {"unauthenticated": 401, "body_forbidden_field": 400, "body_unknown_field": 400, "body_invalid": 400, "fork_unknown": 404, "insufficient_scope": 403, "forbidden": 403, "request_id_conflict": 409, "request_invalid": 400, "fork_too_large": 422,
        "stale_fork": 409, "not_a_decider": 403, "fork_closed": 409, "choice_invalid": 400, "bad_retraction": 400, "already_retracted": 409}
STORED_REFUSALS = []
for reason in REASON_CODES:
    a = da("Refused", None, reason, x1, sx1)
    STORED_REFUSALS.append({"name": reason, "httpStatus": HTTP[reason], "message": result(a, CARD if reason == "stale_fork" else None)})
# ruling citation: authority Operator must cite a Counted answer record whose answerer holds Operator authority
CNT = da("Counted", None, None, x1, sx1); REV = da("Revision", "n1", None, x2, sx2)
REF = da("Refused", None, "fork_closed", x1, srv("n5", OP, T(7))); NONOP = da("Counted", None, None, z1, sz)
def cite(authority, ans, ruling="cultlib-gaps:resolution:question.slot-mark-consent.n1"):
    return {"schemaVersion": "gamecult.fork.ruling_cite.v0", "ruling_id": ruling, "authority": authority, "answer_id": ans}
cites = [
 {"name": "operator_cites_a_counted_operator_answer", "answers": [CNT], "message": cite("Operator", "n1"), "admit": True},
 {"name": "operator_cites_nothing", "answers": [CNT], "message": cite("Operator", None), "admit": False},
 {"name": "operator_cites_a_revision", "answers": [CNT, REV], "message": cite("Operator", "n2"), "admit": False},
 {"name": "operator_cites_a_refused_answer", "answers": [REF], "message": cite("Operator", "n5"), "admit": False},
 {"name": "operator_cites_a_counted_answer_by_a_non_operator", "answers": [NONOP], "message": cite("Operator", "n4"), "admit": False},
 {"name": "operator_cites_an_answer_that_does_not_exist", "answers": [CNT], "message": cite("Operator", "n99"), "admit": False},
 {"name": "steward_needs_no_citation", "answers": [], "message": cite("Steward", None), "admit": True},
]
for cz in cites: cz["messagePackHex"] = hx(cz["message"])
# ---------- transport: list, read, auth ----------
def fcard(n, status="open", **kw):
    c = card(fork_id=f"gamecult-chat:question:q{n}", title=f"Fork {n}", order=n, status=status, **kw)
    return c
def fbody(c, choice, aid_note):
    return body(choice, aid_note, h=c["options_hash"], fork=c["fork_id"])
def with_count(e, n): return dict(e, record=dict(e["record"], answerer_count=n))
def withheld_answers(e):
    e = json.loads(json.dumps(e))
    for a in e["record"]["answers"]: a["answerer"] = None
    return e
def fentry(c, answers=(), counted=None):
    return {"schemaVersion": "gamecult.fork.entry.v0", "card": c, "record": record(counted, list(answers), c["options_hash"], c["fork_id"])}
def withheld(e):
    """The non-decider twin of a decider-view entry: ids withheld, counts kept (fork-card.md, Reader views)."""
    e = json.loads(json.dumps(e)); e["card"]["deciders"] = []
    for a in e["record"]["answers"]: a["answerer"] = None
    return e
q5 = fcard(5); q4 = fcard(4); q2 = fcard(2)
q3 = fcard(3, "closed", ruling="cultlib-gaps:resolution:question.q3.n1", ruled_from=["n1"]); q1 = fcard(1, "closed", ruling="cultlib-gaps:resolution:question.q1.n1")
a5 = da("Counted", None, None, fbody(q5, "no", NOTE), srv("n1", OP, T(2)))
a3 = da("Counted", None, None, fbody(q3, "yes", NOTE), srv("n1", OP, T(2)))
r3 = da("Refused", None, "fork_closed", fbody(q3, "no", NOTE), srv("n2", HM, T(4), "Steward"))
ENTRIES = [fentry(q5, [a5], "n1"), fentry(q4), fentry(q3, [a3, r3], "n1"), fentry(q2), fentry(q1)]
a5b = da("Revision", "n1", None, fbody(q5, "yes", NOTE), srv("n2", OP, T(4)))
TWICE = fentry(q5, [a5, a5b], "n1")                                                 # two answers, one account
q0 = fcard(0)
WENTRIES = [withheld(e) for e in ENTRIES]
MIXED = json.loads(json.dumps(WENTRIES[2])); MIXED["record"]["answers"][0]["answerer"] = OP     # one answer named among nils
ORDER0 = [fentry(fcard(3)), fentry(fcard(2)), fentry(fcard(1)), fentry(q0)]   # a Mind whose oldest card has order 0
q4b = dict(fcard(4), fork_id="gamecult-chat:question:q4b")                      # a second card on order 4 in the same Mind
def order_shared(entries):
    seen, out = {}, []
    for e in entries:
        c = e["card"]
        if c["order"] in seen: out.append(f"order {c['order']} is shared by {seen[c['order']]} and {c['fork_id']}")
        else: seen[c["order"]] = c["fork_id"]
    return out
def lreq(mind="gamecult-chat", status=None, cursor=None, limit=10): return {"schemaVersion": "gamecult.fork.list_request.v0", "mind": mind, "status": status, "cursor": cursor, "limit": limit}
def judge_list(entries, ordinal, rq):
    rows = sorted(entries, key=lambda e: -e["card"]["order"])
    want = [e for e in rows if rq["status"] is None or (e["card"]["status"] == "open") == (rq["status"] == "open")]
    if rq["cursor"] is not None: want = [e for e in want if e["card"]["order"] < int(rq["cursor"][1:])]
    page = want[:rq["limit"]]
    more = len(want) > len(page) and len(page) > 0
    return {"schemaVersion": "gamecult.fork.list.v0", "mind": rq["mind"], "stamp": f"s{ordinal}",
            "open_count": sum(e["card"]["status"] == "open" for e in rows), "recorded_count": sum(e["card"]["status"] == "closed" for e in rows),
            "forks": page, "next_cursor": f"c{page[-1]['card']['order']}" if more else None}
def lstep(entries, ordinal, rq): r = judge_list(entries, ordinal, rq); return {"request": rq, "response": r, "responseHex": hx(r)}
LISTS = [
 {"name": "newest_first_paged_open_only", "ordinal": 412, "entries": ENTRIES, "steps": [lstep(ENTRIES, 412, lreq(status="open", limit=2)), lstep(ENTRIES, 412, lreq(status="open", cursor="c4", limit=2))]},
 {"name": "all_forks_one_page", "ordinal": 412, "entries": ENTRIES, "steps": [lstep(ENTRIES, 412, lreq(limit=50))]},
 {"name": "recorded_only_paged", "ordinal": 412, "entries": ENTRIES, "steps": [lstep(ENTRIES, 412, lreq(status="recorded", limit=1)), lstep(ENTRIES, 412, lreq(status="recorded", cursor="c3", limit=1))]},
 {"name": "limit_zero_is_the_count_and_stamp_only", "ordinal": 412, "entries": ENTRIES, "steps": [lstep(ENTRIES, 412, lreq(limit=0))]},
 {"name": "order_zero_is_legal_but_no_cursor_names_it", "ordinal": 9, "entries": ORDER0, "steps": [lstep(ORDER0, 9, lreq(limit=3)), lstep(ORDER0, 9, lreq(cursor="c1", limit=3))]},
 {"name": "non_decider_page", "ordinal": 412, "entries": WENTRIES, "twin": "all_forks_one_page", "steps": [lstep(WENTRIES, 412, lreq(limit=50))]},
 {"name": "a_mind_with_no_forks", "ordinal": 7, "entries": [], "steps": [lstep([], 7, lreq(mind="nibu", limit=10))]},
]
# A page may stop short of `limit` when the next entry would pass the receiver's response budget; a list whose first
# entry alone passes it is refused `fork_too_large` (422). The budget is not in the vector: a step records what the receiver answered, and the
# judges check it against the unbudgeted page (a nonempty strict prefix, resumed below its last entry) or against the entry the page would open with.
def too_large_doc(c): return {"schemaVersion": "gamecult.fork.refusal.v0", "code": "fork_too_large", "error": f"fork_too_large: fork {c['fork_id']} (order {c['order']}) alone is past the response budget"}
def read_too_large_doc(c): return {"schemaVersion": "gamecult.fork.refusal.v0", "code": "fork_too_large", "error": f"fork_too_large: fork {c['fork_id']} alone is past the response budget"}
READ_TOO_LARGE = [{"name": "a_read_whose_entry_is_past_the_cap_is_fork_too_large_without_an_order", "ordinal": 9, "entries": ORDER0,
                   "request": {"schemaVersion": "gamecult.fork.read_request.v0", "mind": "gamecult-chat", "fork_id": ORDER0[0]["card"]["fork_id"]},
                   "refusal": read_too_large_doc(ORDER0[0]["card"]), "refusalHex": hx(read_too_large_doc(ORDER0[0]["card"])), "httpStatus": HTTP["fork_too_large"], "fork_id": ORDER0[0]["card"]["fork_id"]}]
# An answer on a stale card whose current-card re-read is past the cap: the same refusal document as the read form over a stored Refused(stale_fork), never counted.
_big = ORDER0[0]["card"]
ANSWER_TOO_LARGE = [{"name": "a_stale_answer_whose_card_reread_is_past_the_cap_is_fork_too_large_over_a_stored_stale_refusal",
                     "entries": ORDER0, "request": body("yes", NOTE, h=STALE_H, fork=_big["fork_id"], rid="0000a001-0000-4000-8000-00000000a001"),
                     "refusal": read_too_large_doc(_big), "refusalHex": hx(read_too_large_doc(_big)), "httpStatus": HTTP["fork_too_large"], "outcome": "Refused", "code": "stale_fork", "counted": False, "fork_id": _big["fork_id"]}]
def short_step(entries, ordinal, rq, keep):
    full = judge_list(entries, ordinal, rq); page = full["forks"][:keep]
    assert 0 < keep < len(full["forks"])
    r = dict(full, forks=page, next_cursor=f"c{page[-1]['card']['order']}"); return {"request": rq, "response": r, "responseHex": hx(r)}
def large_step(entries, ordinal, rq):
    c = judge_list(entries, ordinal, rq)["forks"][0]["card"]; r = too_large_doc(c)
    return {"request": rq, "refusal": r, "refusalHex": hx(r), "httpStatus": HTTP["fork_too_large"], "fork_id": c["fork_id"], "order": c["order"]}
SHORT_PAGES = [
 {"name": "a_short_page_resumes_below_its_last_entry", "ordinal": 9, "entries": ORDER0, "steps": [short_step(ORDER0, 9, lreq(limit=3), 2), lstep(ORDER0, 9, lreq(cursor="c2", limit=3))]},
 {"name": "a_first_entry_past_the_budget_is_fork_too_large_and_the_client_continues_below_its_order", "ordinal": 9, "entries": ORDER0,
  "steps": [large_step(ORDER0, 9, lreq(limit=3)), lstep(ORDER0, 9, lreq(cursor="c3", limit=3))]},
 {"name": "fork_too_large_on_order_zero_leaves_nothing_below", "ordinal": 9, "entries": ORDER0[2:],
  "steps": [short_step(ORDER0[2:], 9, lreq(limit=5), 1), large_step(ORDER0[2:], 9, lreq(cursor="c1", limit=5))]},
]
DUP_LIST = judge_list(ENTRIES + [fentry(q4b)], 412, lreq(limit=50))
assert order_shared(DUP_LIST["forks"]) == ["order 4 is shared by gamecult-chat:question:q4 and gamecult-chat:question:q4b"]
TOK = lambda aud, *sc, active=True: {"audience": aud, "scopes": list(sc), "active": active}
AUTH = [
 ("list_with_desk_read", "list", TOK("bifrost-desk", "desk.read"), "ok"), ("read_with_desk_read", "read", TOK("bifrost-desk", "desk.read"), "ok"),
 ("answer_with_desk_answer", "answer", TOK("bifrost-desk", "desk.answer"), "ok"), ("answer_with_both_scopes", "answer", TOK("bifrost-desk", "desk.read", "desk.answer"), "ok"),
 ("no_bearer_list", "list", None, "unauthenticated"), ("no_bearer_answer", "answer", None, "unauthenticated"),
 ("the_MAS_token_is_refused", "list", TOK("matrix-authentication-service", "openid", "profile"), "unauthenticated"),
 ("a_votes_token_cannot_list", "list", TOK("bifrost-votes", "votes.read"), "unauthenticated"),
 ("a_votes_token_cannot_read", "read", TOK("bifrost-votes", "votes.read"), "unauthenticated"),
 ("a_votes_token_cannot_answer", "answer", TOK("bifrost-votes", "votes.cast"), "unauthenticated"),
 ("desk_audience_with_a_votes_scope_only", "answer", TOK("bifrost-desk", "votes.cast"), "insufficient_scope"),
 ("desk_read_cannot_answer", "answer", TOK("bifrost-desk", "desk.read"), "insufficient_scope"),
 ("desk_answer_cannot_list", "list", TOK("bifrost-desk", "desk.answer"), "insufficient_scope"),
 ("a_non_active_member_cannot_list", "list", TOK("bifrost-desk", "desk.read", active=False), "forbidden"),
 ("a_non_active_member_cannot_read", "read", TOK("bifrost-desk", "desk.read", active=False), "forbidden"),
 ("a_non_active_member_cannot_answer", "answer", TOK("bifrost-desk", "desk.answer", active=False), "forbidden"),
 ("scope_is_judged_before_membership", "answer", TOK("bifrost-desk", "desk.read", active=False), "insufficient_scope"),
 ("huginn_down_list", "list", TOK("bifrost-desk", "desk.read"), "unavailable", True),
 ("huginn_down_read", "read", TOK("bifrost-desk", "desk.read"), "unavailable", True),
 ("huginn_down_answer", "answer", TOK("bifrost-desk", "desk.answer"), "unavailable", True),
 ("auth_is_judged_before_huginn_is_asked", "list", None, "unauthenticated", True),
 ("membership_is_judged_before_huginn_is_asked", "read", TOK("bifrost-desk", "desk.read", active=False), "forbidden", True),
]
# ---------- transport: the {mind} segment (Heimdall's file 4.3a, M1 to M4) ----------
import urllib.parse
MINDS = {"gamecult-chat": "huginn-gamecult-chat.sock", "nibu": "huginn-nibu.sock"}
MIND_FORKS = {"gamecult-chat": ["gamecult-chat:question:q1", "gamecult-chat:question:q2", FID], "nibu": ["nibu:question:glow-order"]}
def auth_code(route, tok):
    if tok is None or tok["audience"] != "bifrost-desk": return "unauthenticated"
    if ("desk.answer" if route == "answer" else "desk.read") not in tok["scopes"]: return "insufficient_scope"
    return "forbidden" if not tok["active"] else "ok"
def mind_route(route, seg, fork, tok):
    code = auth_code(route, tok)
    if code != "ok": return code, None, None
    try: mind = urllib.parse.unquote(seg, errors="strict")
    except UnicodeDecodeError: mind = None
    if mind not in MINDS: return "fork_unknown", None, None          # never joined into a path; no socket is opened
    if route != "list" and fork not in MIND_FORKS[mind]: return "fork_unknown", MINDS[mind], None
    return "ok", MINDS[mind], mind if route == "answer" else None
DESK_READ, DESK_ANSWER = TOK("bifrost-desk", "desk.read"), TOK("bifrost-desk", "desk.answer")
def mcase(name, route, seg, fork=None, tok=None):
    tok = None if tok == "none" else tok or (DESK_ANSWER if route == "answer" else DESK_READ)
    code, opened, stored = mind_route(route, seg, fork, tok)
    return {"name": name, "route": route, "mind": seg, "fork_id": fork, "token": tok, "expect": code, "httpStatus": 200 if code == "ok" else HTTP[code], "opened": opened, "stored_in": stored}
BAD_SEGMENTS = [("dot_dot", "../x"), ("slash", "a/b"), ("encoded_slash", "a%2fb"), ("encoded_dot_dot", "%2e%2e%2fx"), ("nul", "eureka%00"), ("four_kb", "a" * 4096),
                ("other_case", "GAMECULT-CHAT"), ("trailing_space", "nibu%20"), ("invalid_utf8", "%ff"), ("dot", "."), ("dots", ".."), ("double_encoded", "%256eibu")]
MINDCASES = [
  mcase("list_a_percent_encoded_configured_mind_is_decoded_once", "list", "%6eibu"), mcase("list_a_configured_mind", "list", "gamecult-chat"), mcase("list_another_configured_mind", "list", "nibu"),
  mcase("read_a_fork_the_mind_holds", "read", "gamecult-chat", FID), mcase("answer_a_fork_the_mind_holds_is_stored_in_that_mind", "answer", "gamecult-chat", FID),
  mcase("answer_on_the_other_mind", "answer", "nibu", "nibu:question:glow-order"),
] + [mcase(f"M1_{route}_{n}_is_unknown_and_opens_nothing", route, seg, FID if route != "list" else None) for route in ("list", "read", "answer") for n, seg in BAD_SEGMENTS] + [
  mcase("M2_read_unknown_mind_known_fork", "read", "ghost", FID), mcase("M2_read_known_mind_unknown_fork", "read", "gamecult-chat", "gamecult-chat:question:ghost"),
  mcase("M2_answer_unknown_mind_known_fork", "answer", "ghost", FID), mcase("M2_answer_known_mind_unknown_fork", "answer", "gamecult-chat", "gamecult-chat:question:ghost"),
  mcase("M2_list_unknown_mind", "list", "ghost"),
  mcase("M3_answer_on_nibu_with_a_fork_gamecult_chat_holds", "answer", "nibu", FID), mcase("M3_answer_on_gamecult_chat_with_a_fork_nibu_holds", "answer", "gamecult-chat", "nibu:question:glow-order"),
  mcase("M4_desk_read_lists_every_mind", "list", "nibu", tok=DESK_READ), mcase("M4_desk_read_reads_every_mind", "read", "nibu", "nibu:question:glow-order", tok=DESK_READ),
  mcase("M4_desk_answer_cannot_list_any_mind", "list", "gamecult-chat", tok=DESK_ANSWER), mcase("M4_desk_answer_cannot_list_the_other_mind", "list", "nibu", tok=DESK_ANSWER),
  mcase("M4_desk_read_cannot_answer_on_any_mind", "answer", "nibu", "nibu:question:glow-order", tok=DESK_READ),
  mcase("auth_comes_before_the_mind_lookup_no_bearer", "list", "ghost", tok="none"), mcase("auth_comes_before_the_mind_lookup_scope", "answer", "ghost", FID, tok=DESK_READ),
  mcase("auth_comes_before_the_mind_lookup_non_active", "read", "ghost", FID, tok=TOK("bifrost-desk", "desk.read", active=False)),
]
SAME_RESPONSE = [["M2_read_unknown_mind_known_fork", "M2_read_known_mind_unknown_fork"], ["M2_answer_unknown_mind_known_fork", "M2_answer_known_mind_unknown_fork"],
                 ["M2_list_unknown_mind", "M2_read_known_mind_unknown_fork"], ["M3_answer_on_nibu_with_a_fork_gamecult_chat_holds", "M2_answer_known_mind_unknown_fork"]]
def bad(name, why, m, **kw): return dict({"name": name, "why": why, "message": m}, **kw)
DECIDERS_64 = [f"{i:08x}-0000-4000-8000-{i:012x}" for i in range(1, 65)]   # the card's `deciders` holds at most 64 (Huginn's bound)
# body_sha256: Huginn's private "same body" digest (fork-card.md, "The mind's record"); no wire document carries it. Input per
# /opt/commons/handoff/desk-body-digest-order-20261110.md (Eureka): SHA-256 of compact serde_json UTF-8, keys in this order, null for missing,
# `retracts` the full stored key, request_id out. `illegal_retraction` and `retracted` are written only when the input has them (non-null).
DIGEST_KEYS = ["fork", "options_hash", "answered_at", "choice", "note", "retracts", "illegal_retraction", "retracted"]
def digest_bytes(i): return json.dumps({k: i[k] for k in DIGEST_KEYS if k in ("fork", "options_hash", "answered_at", "choice", "note", "retracts") or i.get(k) is not None},
                                       separators=(",", ":"), ensure_ascii=False).encode("utf-8")
DQ = {"fork": "eureka-state:question:Q1", "options_hash": "a" * 64, "answered_at": "2026-11-10T12:00:00Z"}
DT = "eureka-state:desk_answer:Q1.n1"
def dg(name, status, sha_expected, why, **kw):
    i = dict(DQ, choice=None, note=None, retracts=None, illegal_retraction=None, retracted=None); i.update(kw)
    b = digest_bytes(i); got = sha(b)
    assert got == sha_expected, (name, got)
    return {"name": name, "status": status, "why": why, "input": i, "canonical": b.decode("utf-8"), "sha256": got}
MAIN, FIX = "main_7ea11b4_must_not_move", "reproduced_on_huginn_main_f0ca504_reported_by_eureka_not_rerun_here"
BODY_DIGESTS = [
  dg("plain_answer_choice_A", MAIN, "8a4d5b2ed0b3452bfed2f3a276b2828974e6824a3edcc643cfe9cfcdda34173a", "a plain answer: choice and note as sent, retracts null; no illegal or retracted key", choice="A"),
  dg("legal_retraction", MAIN, "9f8075382556898c0bea364ed7c6deb2a86d8c78288a1f34e06da6e9bf5f58a3", "a legal retraction as Huginn main pins it: retracts is `Q1.1`, a string no door produces (the key grammar is .n<seq>); kept for the frozen-form test", retracts="eureka-state:desk_answer:Q1.1"),
  dg("illegal_i_and_iii_alike_on_main", MAIN, "69074cc60829ef5edb12358746f7549ad08da97653d59359c945cea102f53565", "main writes `illegal_retraction: true` and no `retracted`, so (i) and (iii) collide: the defect the fix removes", choice="A", retracts=DT, illegal_retraction=True),
  dg("legal_retraction_door_form", MAIN, "84576840247830b9b75754b460dc4ea52883ca1e410f9f011521e0f69b99eee1", "a legal retraction as the door produces it: the wire's n1 converted to the full stored key Q1.n1 before the digest; the golden that stands for the real form (digest unaffected by .n<seq> vs .1)", retracts=DT),
  dg("illegal_ii_on_main", MAIN, "1a36c4aba0d1c30d0736f20243ebfdb84fe84a5eabd4f69709a096a32c3cc6e3", "(ii) no answer, no target, on main", illegal_retraction=True),
  dg("illegal_i_retraction_with_an_answer_retracted_true", FIX, "935cab1a0ae34606b809d8ebca7cffa2889d20b3585d2622d32153a4d6898af1", "(i) answer present, retracted true, target Q1.n1", choice="A", retracts=DT, illegal_retraction=True, retracted=True),
  dg("illegal_iii_plain_answer_with_a_target_retracted_false", FIX, "82b24cccb4ed2535a549f95f844fcd935074c3712b5b832f0b2cbd89c71fd9a6", "(iii) the same bytes as (i) with retracted false: another body, so request_id_conflict", choice="A", retracts=DT, illegal_retraction=True, retracted=False),
  dg("illegal_i_retraction_with_an_answer_and_no_target_retracted_true", "reproduced_on_huginn_main_f0ca504_reported_by_eureka_not_rerun_here", "79eee623fd6712db6537a4867cedf54ca49ee4885416c2c8e4acec59f811b894", "(i) with the target null (the order doc: retracts set or null); Eureka re-derived it with sha256sum (order doc addendum)", choice="A", illegal_retraction=True, retracted=True),
  dg("illegal_ii_no_answer_no_target_retracted_true", FIX, "34ef86ed63b9318459965d4b85e794d2636ffc15736da866036b087c9c4a30eb", "(ii) no answer, no target, retracted true", illegal_retraction=True, retracted=True),
]
fork = {
  "provenance": "Rule source: /opt/commons/handoff/fork-desk-answer-binding-20261110.md (Heimdall), amended by the Persona briefs of 2026-11-10 (Eureka's fit check). Bytes spec-authored with msgpack-python (scripts/gen-governance-vectors.py); every runtime re-encodes `message` and must match messagePackHex. options_hash follows fork-card.md 'Options hash'; the vectors named heimdall_* are that file's, with its digests.",
  "optionsHash": hashes,
  "bodyDigests": BODY_DIGESTS,
  "card": {"valid": [{"name": "operator_fork", "message": CARD, "messagePackHex": hx(CARD)},
                     {"name": "leans_flag_closed_two_deciders", "message": card(leans="yes", flag="provisional: Self expects this to move", deciders=[OP, HM], depends=[], raised_in=None, status="closed", ruling="x:resolution:y", ruled_from=["n1"], order=0),
                      "messagePackHex": hx(card(leans="yes", flag="provisional: Self expects this to move", deciders=[OP, HM], depends=[], raised_in=None, status="closed", ruling="x:resolution:y", ruled_from=["n1"], order=0))},
                     {"name": "raised_at_nil", "message": card(raised_at=None), "messagePackHex": hx(card(raised_at=None))},
                     {"name": "depends_at_both_caps", "message": card(depends=["a"] * 7 + ["\u00e9" * 500]), "messagePackHex": hx(card(depends=["a"] * 7 + ["\u00e9" * 500]))},
                     {"name": "closed_without_a_ruling_the_question_was_withdrawn", "message": card(status="closed"), "messagePackHex": hx(card(status="closed"))},
                     {"name": "withheld_deciders", "message": card(deciders=[], decider_count=2), "messagePackHex": hx(card(deciders=[], decider_count=2))},
                     {"name": "deciders_at_the_cap_of_64", "message": card(deciders=DECIDERS_64), "messagePackHex": hx(card(deciders=DECIDERS_64))}],
           "invalid": [bad(n, w, m) for n, w, m in [
               ("depends_nine_lines", "depends holds at most 8 lines (the stored question's limit)", card(depends=["a"] * 9)),
               ("depends_line_1001_code_points", "a depends line is at most 1000 characters", card(depends=["a" * 1001])),
               ("depends_line_1001_bytes", "rule: one byte over the limit (500 two-byte characters and one more; not expressible in the schema)", card(depends=["\u00e9" * 500 + "a"])),
               ("depends_line_over_1000_bytes", "rule: a depends line is at most 1000 UTF-8 bytes (the stored Line limit; not expressible in the schema)", card(depends=["\u00e9" * 501])),
               ("one_option", "two or more options", card(options=[OPT[0]], recommendation="yes")),
               ("decider_count_zero", "a fork with no decider cannot be answered; an empty deciders list now means withheld, so the count refuses it", card(deciders=[], decider_count=0)),
               ("decider_count_missing", "map-mode writes every key: decider_count is required", {k: v for k, v in card().items() if k != "decider_count"}),
               ("decider_count_65", "decider_count is at most 64 (Bifrost's per-call list)", card(deciders=[], decider_count=65)),
               ("decider_count_string", "decider_count is an integer", card(deciders=[], decider_count="2")),
               ("decider_count_null", "decider_count is never nil: it states the count even when the ids are withheld", card(deciders=[], decider_count=None)),
               ("decider_count_fewer_than_the_ids", "rule R-card: a card that shows deciders shows all of them, so the count equals the ids (not expressible in the schema)", card(deciders=[OP, HM], decider_count=1)),
               ("decider_count_more_than_the_ids_shown", "rule R-card: a visible list must be whole; a partial list is no view (not expressible in the schema)", card(deciders=[OP, HM], decider_count=3)),
               ("null_recommendation", "every card recommends an option", dict(card(), recommendation=None)),
               ("unknown_key", "closed document", dict(card(), note="x")),
               ("option_with_a_label", "options are {id,text}; the old format's display label is not a member (fork-card.md, Old format)", card(options=[dict(OPT[0], label="Yes"), OPT[1]])),
               ("status_unknown", "status is open or closed", card(status="provisional")),
               ("options_hash_not_hex", "options_hash is 64 lowercase hex", dict(card(), options_hash="XYZ")),
               ("duplicate_option_ids", "rule: option ids are unique (not expressible in the schema)", card(options=[OPT[0], dict(OPT[1], id=OPT[0]["id"])])),
               ("recommendation_not_an_option", "rule: recommendation is an option id (not expressible in the schema)", card(recommendation="maybe")),
               ("leans_not_an_option", "rule: leans is nil or an option id (not expressible in the schema)", card(leans="maybe")),
               ("duplicate_deciders", "rule: deciders are unique (not expressible in the schema)", card(deciders=[OP, OP], decider_count=2)),
               ("ruled_from_on_an_open_card", "rule: a ruling that answers a fork closes it, so an open card cites no answer in ruled_from (not expressible in the schema)", card(ruled_from=["n1"])),
               ("open_card_names_a_ruling", "rule: only a closed card names a ruling (not expressible in the schema)", card(ruling="cultlib-gaps:resolution:question.slot-mark-consent.n1")),
               ("ruled_from_without_a_ruling", "rule: ruled_from lists answers a ruling was admitted from, so it needs the ruling (not expressible in the schema)", card(status="closed", ruled_from=["n1"])),
               ("options_hash_of_another_card", "rule: options_hash equals the hash of the shown members (not expressible in the schema)", dict(card(), options_hash=STALE_H)),
               ("raised_at_epoch_integer", "raised_at is an RFC3339 instant string or nil, not epoch milliseconds", card(raised_at=1762767000000)),
               ("raised_at_date_only", "raised_at is an instant with a time and a Z, not a date", card(raised_at="2026-11-10")),
               ("raised_at_missing", "map-mode writes every key: raised_at is required, nil when unknown", {k: v for k, v in card().items() if k != "raised_at"}),
               ("deciders_65", "deciders holds at most 64 (Huginn's bound)", card(deciders=DECIDERS_64 + ["00000041-0000-4000-8000-000000000041"], decider_count=65)),
           ]]},
  "request": {"valid": [{"name": n, "message": m, "messagePackHex": hx(m)} for n, m in
                       [("choice_and_note", x1), ("choice_alone", pick), ("note_alone_clear_pick", words), ("retraction", retraction("n1")),
                        ("note_4000_ascii_bytes", body(None, "x" * 4000)), ("note_astral_4000_bytes", body(None, "\U0001F600" * 1000)),
                        ("note_combining_4000_bytes", body(None, "e\u0301" * 1333 + "x"))]],
             "invalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
               ("answer_with_neither", "an answer object holds a choice, a note, or both", body(answer={"choice": None, "note": None})),
               ("blank_note", "a blank note is no answer", body(None, "  \u00a0 ")),
               ("request_id_missing", "map-mode writes every key; a retry needs the id", {k: v for k, v in x1.items() if k != "request_id"}),
               ("request_id_uppercase", "request_id is lowercase", dict(x1, request_id="0000000A-0000-4000-8000-00000000000B")),
               ("request_id_not_a_uuid", "request_id is a lowercase D-format UUID", dict(x1, request_id="retry-1")),
               ("note_over_4000", "notes are at most 4000 UTF-8 bytes", body(None, "x" * 4001)),
               ("note_multibyte_4001_bytes", "a note is counted in UTF-8 bytes: 1999 e-acute is 3998 bytes, plus three ASCII is 4001 (rule: not expressible in the schema, 2002 characters)", body(None, "\u00e9" * 1999 + "abc")),
               ("note_astral_4004_bytes", "1001 astral characters are 4004 bytes though only 1001 code points (rule: not expressible in the schema)", body(None, "\U0001F600" * 1001)),
               ("note_combining_4002_bytes", "e plus a combining acute is 3 bytes: 1334 of them are 4002 (rule: not expressible in the schema)", body(None, "e\u0301" * 1334)),
               ("options_hash_uppercase", "hash is lowercase hex", body("no", None, h=H.upper())),
               ("answerer_key", "answerer is server-set; the door answers body_forbidden_field", dict(x1, answerer=OP)),
               ("authority_key", "authority is server-set; the door answers body_forbidden_field", dict(x1, authority="Operator")),
               ("answer_object_with_a_key", "the answer object is closed", body(answer={"choice": "no", "note": None, "weight": 1})),
               ("answered_at_without_zone", "instants are UTC with a Z", body("no", None, at="2026-11-12T19:02:00")),
               ("retracted_not_a_boolean", "retracted is a boolean", dict(x1, retracted="no")),
               ("fork_id_empty", "fork_id names a fork", body("no", None, fork="")),
           ]]},
  "deskAnswer": {"valid": [{"name": n, "message": m, "messagePackHex": hx(m)} for n, m in
                           [("counted", CNT), ("revision", REV), ("retraction", da("Retraction", "n1", None, retraction("n1"), sr("n8"))),
                            ("withheld_answerer", dict(CNT, answerer=None)),
                            ("refused_stale", da("Refused", None, "stale_fork", stale_body(), sx1)),
                            ("refused_closed_retraction_names_its_target", da("Refused", None, "fork_closed", retraction("n1"), sr("n9")))]],
                 "invalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
                   ("counted_with_a_code", "only Refused carries a code", dict(CNT, code="fork_closed")),
                   ("refused_without_a_code", "Refused carries a reason code", dict(REF, code=None)),
                   ("refused_with_an_unknown_code", "the code set is closed", dict(REF, code="because")),
                   ("counted_naming_another_answer", "Counted names none", dict(CNT, of="n0")),
                   ("revision_naming_none", "a Revision names the counted answer", dict(REV, of=None)),
                   ("revision_that_also_retracts", "a variant carries only its own link (a retraction with no target, or a target with no retraction, cannot exist)", dict(REV, retracts="n1")),
                   ("retraction_naming_no_target", "a Retraction names the answer it retracts", dict(da("Retraction", "n1", None, retraction("n1"), sr("n8")), retracts=None)),
                   ("refused_naming_another_answer_as_of", "a Refused answer names no counted answer (`of` is a Revision's)", dict(REF, of="n1")),
                   ("refused_with_a_target_that_is_not_an_answer_id", "a Refused answer's retracts is nil or an answer id (n<seq>), never the stored key", dict(REF, retracts="cultlib-gaps:desk_answer:slot-mark-consent.n1")),
                   ("retraction_carrying_a_choice", "a Retraction carries no choice or note", dict(da("Retraction", "n1", None, retraction("n1"), sr("n8")), choice="no")),
                   ("outcome_lower_case", "outcome tags are Counted, Revision, Retraction, Refused", dict(CNT, outcome="counted")),
                   ("no_answerer", "answerer is stamped on every stored answer", dict(CNT, answerer="")),
                 ]]},
  "resultsInvalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
    ("answer_result_with_a_nil_answerer", "rule R-answer-result: the answer is the sender's own, stamped with their account; a nil answerer exists only inside a record shown to a non-decider (not expressible in the schema)", dict(result(dict(CNT, answerer=None)), counted_answer_id="n1"))]],
  "results": [{"name": r["name"], "httpStatus": r["httpStatus"], "message": r["message"], "messagePackHex": hx(r["message"])} for r in STORED_REFUSALS],
  "doorRefusals": [{"name": c, "httpStatus": HTTP[c], "message": refusal_doc(c), "messagePackHex": hx(refusal_doc(c))} for c in TAIL] + [{"name": "fork_too_large", "httpStatus": HTTP["fork_too_large"], "message": SHORT_PAGES[1]["steps"][0]["refusal"], "messagePackHex": SHORT_PAGES[1]["steps"][0]["refusalHex"]}],
  "unavailable": [{"name": "second_door_down", "httpStatus": 503, "message": unavailable_doc(30), "messagePackHex": hx(unavailable_doc(30))},
                  {"name": "second_door_down_no_hint", "httpStatus": 503, "message": unavailable_doc(None), "messagePackHex": hx(unavailable_doc(None))}],
  "rulingCites": cites,
  "transport": {
    "listRequest": {"valid": [{"name": "list_open_page", "message": lreq(status="open", limit=2), "messagePackHex": hx(lreq(status="open", limit=2))},
                              {"name": "list_next_page", "message": lreq(status="open", cursor="c4", limit=2), "messagePackHex": hx(lreq(status="open", cursor="c4", limit=2))},
                              {"name": "list_count_only", "message": lreq(limit=0), "messagePackHex": hx(lreq(limit=0))}],
                    "invalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
                      ("limit_51", "a page is at most 50 forks", lreq(limit=51)), ("limit_negative", "limit is 0 to 50", lreq(limit=-1)),
                      ("status_all", "status is open, recorded or nil", lreq(status="all")), ("cursor_malformed", "a cursor is c<N>", lreq(cursor="next")),
                      ("cursor_zero", "a cursor names a positive order", lreq(cursor="c0")), ("mind_empty", "a list names a Mind", lreq(mind="")),
                      ("unknown_key", "closed document", dict(lreq(), sort="oldest"))]]},
    "readRequest": {"valid": [{"name": "read_one", "message": {"schemaVersion": "gamecult.fork.read_request.v0", "mind": "gamecult-chat", "fork_id": FID}, "messagePackHex": hx({"schemaVersion": "gamecult.fork.read_request.v0", "mind": "gamecult-chat", "fork_id": FID})}],
                    "invalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
                      ("fork_id_empty", "a read names a fork", {"schemaVersion": "gamecult.fork.read_request.v0", "mind": "gamecult-chat", "fork_id": ""}),
                      ("mind_empty", "a read names a Mind", {"schemaVersion": "gamecult.fork.read_request.v0", "mind": "", "fork_id": FID}),
                      ("mind_missing", "map-mode writes every key; the Mind is the route's", {"schemaVersion": "gamecult.fork.read_request.v0", "fork_id": FID})]]},
    "entry": {"valid": [{"name": n, "message": e, "messagePackHex": hx(e)} for n, e in [("open_no_answers", ENTRIES[1]), ("open_counted", ENTRIES[0]), ("closed_with_ruling_and_a_refusal", ENTRIES[2])]]
                       + [dict({"name": n, "message": e, "messagePackHex": hx(e)}, twin=t) for n, e, t in [("withheld_open_counted", WENTRIES[0], "open_counted"), ("withheld_closed_with_ruling_and_a_refusal", WENTRIES[2], "closed_with_ruling_and_a_refusal"), ("withheld_no_answers", WENTRIES[1], "open_no_answers")]],
              "invalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
                ("entry_without_record", "an entry holds the card and its record", {k: v for k, v in ENTRIES[0].items() if k != "record"}),
                ("record_of_another_fork", "rule: the record names the card's fork (not expressible in the schema)", dict(ENTRIES[0], record=dict(ENTRIES[0]["record"], fork_id="gamecult-chat:question:q9"))),
                ("record_under_another_hash", "rule: the record is under the card's options_hash (not expressible in the schema)", dict(ENTRIES[0], record=dict(ENTRIES[0]["record"], options_hash=STALE_H))),
                ("counted_answer_not_derived", "rule: counted_answer_id is the first live Counted or Revision answer under the card's hash (not expressible in the schema)", dict(ENTRIES[0], record=dict(ENTRIES[0]["record"], counted_answer_id=None))),
                ("answerer_count_missing", "map-mode writes every key: answerer_count is required", dict(ENTRIES[0], record={k: v for k, v in ENTRIES[0]["record"].items() if k != "answerer_count"})),
                ("answerer_count_negative", "answerer_count is not negative", with_count(ENTRIES[0], -1)),
                ("answerer_count_null", "answerer_count is never nil: a decider and a non-decider read the same number", with_count(ENTRIES[0], None)),
                ("answerer_count_disagrees_with_the_ids", "rule R-record: in the decider view answerer_count is the number of distinct answerers (two here, not three; not expressible in the schema)", with_count(ENTRIES[2], 3)),
                ("answerer_count_counts_answers_not_people", "rule R-record: two answers by one account are one answerer (not expressible in the schema)", with_count(TWICE, 2)),
                ("withheld_record_with_a_named_answerer_among_nils", "rule R-record: a record is one reader's view, every answerer named or every answerer nil (not expressible in the schema)", MIXED),
                ("withheld_count_over_the_answers", "rule R-record: withheld answers are at most as many answerers as answers (two answers, count 3; not expressible in the schema)", with_count(WENTRIES[2], 3)),
                ("withheld_count_zero_over_answers", "rule R-record: with at least one answer the count is at least 1 (not expressible in the schema)", with_count(WENTRIES[0], 0)),
                ("withheld_count_over_no_answers", "rule R-record: with no answers the count is 0 in both views (not expressible in the schema)", with_count(WENTRIES[1], 1)),
                ("decider_card_with_withheld_answers", "rule R-entry: a card that shows deciders is the decider view, so every answerer is named (not expressible in the schema)", withheld_answers(ENTRIES[0])),
                ("withheld_card_with_named_answers", "rule R-entry, the leak: a card with deciders withheld beside an answer that still names its account (not expressible in the schema)", dict(ENTRIES[0], card=WENTRIES[0]["card"])),
                ("card_hash_wrong", "rule: the server computes options_hash and a client recomputes it; a card with another hash is no card", dict(ENTRIES[1], card=dict(ENTRIES[1]["card"], options_hash=STALE_H), record=dict(ENTRIES[1]["record"], options_hash=STALE_H)))]]},
    "listInvalid": [dict(bad(n, w, m), messagePackHex=hx(m)) for n, w, m in [
      ("next_cursor_malformed", "a cursor is c<N>", dict(LISTS[0]["steps"][0]["response"], next_cursor="more")),
      ("stamp_malformed", "a stamp is s<N>", dict(LISTS[0]["steps"][0]["response"], stamp="412")),
      ("count_negative", "counts are not negative", dict(LISTS[0]["steps"][0]["response"], open_count=-1)),
      ("a_page_mixes_views", "rule R-list: a page is one reader's view, so every entry agrees on whether the deciders are shown (not expressible in the schema)",
       dict(LISTS[1]["steps"][0]["response"], forks=[WENTRIES[0]] + LISTS[1]["steps"][0]["response"]["forks"][1:]))]]
      + [dict(bad("two_cards_share_one_order", "rule: order is a unique rank per Mind; the refusal names both fork ids (not expressible in the schema)", DUP_LIST),
              messagePackHex=hx(DUP_LIST), refusal=order_shared(DUP_LIST["forks"])[0])],
    "listScenarios": [dict({"name": l["name"], "ordinal": l["ordinal"], "entries": l["entries"], "steps": l["steps"]}, **({"twin": l["twin"]} if "twin" in l else {})) for l in LISTS],
    "listShortPages": SHORT_PAGES,
    "readTooLarge": READ_TOO_LARGE,
    "answerTooLarge": ANSWER_TOO_LARGE,
    "doorFaults": [dict(c, response=r, responseHex=hx(r)) for c, r in
                   [({"name": f"{x}_door_answers_request_id_conflict_the_client_sees_unavailable", "route": x, "doorCode": "request_id_conflict", "httpStatus": 503}, unavailable_doc(30)) for x in ("read", "list")]
                   + [({"name": "answer_door_answers_request_id_conflict_the_client_sees_it", "route": "answer", "doorCode": "request_id_conflict", "httpStatus": HTTP["request_id_conflict"]}, refusal_doc("request_id_conflict"))]],
    "mindRoutes": {"minds": MINDS, "forks": MIND_FORKS, "cases": MINDCASES, "sameResponse": SAME_RESPONSE},
    "authCases": [{"name": a[0], "route": a[1], "token": a[2], "expect": a[3], "huginn_down": len(a) > 4} for a in AUTH],
  },
  "scenarios": [{"name": n, "card": c, "optionsHash": c["options_hash"], "steps": steps, "record": r, "recordHex": hx(r)} for n, c, steps, r in scen],
}
(OUT / "fork-card-vectors.json").write_text(json.dumps(fork, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print("ok", len(vote["valid"]), len(vote["invalid"]), len(fork["scenarios"]))
