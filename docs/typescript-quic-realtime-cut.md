# TypeScript QUIC Realtime Plane Cut Map

Date: 2026-09-16

Status: Imagination output for the target in
`docs/typescript-quic-realtime-target.md` (operator-accepted 2026-09-16). No
code in this document has been written. Nothing here is committed by
Imagination; the root agent commits. Q5, Q6 and Q7 were ruled the same day
(section 13, Rulings); Q5 was then re-ruled from A to C: no registry in this
campaign, registry publication parked for a later pass by choice. Cut 6,
section 5.4, the binary-resolution and dependency lines of Cut 4, and Cut 7's
ledger are remapped under C (landed 2026-09-16, same day). Q8 is ruled:
the built binaries are committed to the tree (section 11.2). No operator
question is open. **Cut 1 landed** at `e234a64` (shared verifier, tests,
C#-written vectors), `66286b9` (browser copy deleted, −143 net) and
`062a5c6` (mutation runner; twelve killed, one equivalent), then three
Soul passes and three fix batches; **closed 2026-09-16**, three residuals
landed at `c64a2da`, `6c933d0`, `fe2555d`.

**Cut 2 landed** at `6cbc73f` (the codec, 178 lines; C#-written and
TypeScript-written frame vectors, each decoded by the other side, the C#
test reading both with the reference alone) and `ae29910` (37 entries as a
third runner target; 72 of 72 across the three targets killed, one
equivalent named). Closed 2026-09-16 after Soul's pass and one fix batch.
Hands' discrepancies, kept: the
65,535-byte identity and 64 MiB payload ceilings recorded as a build rule
plus the digest of the whole encoding rather than half a megabyte of
literal text; the Body has five refusal messages where the spec listed six
(negative length and length-sum mismatch share one); three refusals added
that C#'s type system gives free, among them **a real divergence found by
the test: `TextDecoder` deletes a leading U+FEFF where
`Encoding.UTF8.GetString` keeps it, and U+FEFF is not .NET whitespace, so
such an identity passes `Validate` and reaches the wire**, closed with
`ignoreBOM`; C# sums lengths under `checked` and TS covers the overflow
with its length refusal, unreachable from either encoder. Residual 1's
exact-equality verification could not hold as written: applying the
pre-check leaves eleven stricter-than-C# spellings, all the safe direction,
pinned by name, because exact agreement needs .NET's registered-scheme
table. The no-control-byte test caught a literal U+001F the Write tool put
into its own introducing comment.

Two items for Self's ruling, both taken: **`npm run test:ts` and `build:ts`
have been broken since `8cb3b72`** (the scoped rename; Hands first
attributed it to `8dd5a45`, which only bumped a version, and Soul
corrected it) because `scripts/run-typescript-workspaces.mjs:9` names the
old unscoped `cultcache-ts`; one-word fix, lands in Cut 2's fix batch.
**The runner is `scripts/mutate-cultmesh.mjs`** since `65f2b66`'s batch
(renamed from `mutate-cultmesh-authority.mjs` once it carried a codec
target); every reference below that names the old path is history.

**Soul's pass on Cut 2** (Fable; probes `probe-cs/`, `parity.mjs`,
`symmetric.mjs` and others in the session scratchpad) held byte parity in
both directions over its own thirty-nine encode specs and thirty
hand-built byte strings: identical digests, identical refusal messages,
identical U+FFFD counts on invalid UTF-8, the byte-order mark round trip,
all four digest ceilings regenerated from their build rules on both sides,
the five decode refusals in the same order, and every symmetric two-site
mutant dead. **Cut 2 does not close as promised**, on three small findings
in the fix batch:

- **The mutant Hands called equivalent is not.** A 37-byte frame whose
  identity lengths cancel a negative payload length sums to exactly 37 and
  decodes to blank identities where C# refuses. The clause is right; nothing
  defended it. Medium.
- **The decoded payload aliases the frame when the input is a Node
  `Buffer`**, because `Buffer.prototype.slice` is a view; the test used only
  a `Uint8Array`. The same trap the repo recorded for `verifyP256`. Medium
  for the bridge, nil today.
- **Neither half of the frame parity runs in CI**; a C# codec change leaves
  everything green. The Cut 1 scar, repeated. Medium.

Recorded, not defects: the reference encoder writes delivery values its
own decoder refuses (a cast on the enum), where TypeScript refuses at
encode, the stricter side; an overflow near `int.MaxValue` is reachable
from hostile bytes and refused on both sides with different words; the
runner's sentinel detects only the last mutant by design, and the sidecar
repair names the mutation a dead run was inside.

**The Cut 2 fix batch landed** (Opus) at `65f2b66` (the aggregate scripts
reach every workspace again; breakage confirmed at `8cb3b72`), `9a4c939`
(the payload copy is `new Uint8Array(bytes.subarray(…))`, because Soul's
prescribed `Uint8Array.prototype.slice.call` returns a `Buffer` from a
`Buffer` through `@@species` and fails Soul's own assertion; three crafted
negative-length frames refused; the false equivalence note gone),
`6ed0ff7` (both halves of the frame parity in CI on both OSes) and
`7a0749b` (the eleven stricter spellings named for what they are: three
backslash, two missing-authority, four host repairs, two `URL` throws; the
same slip in `isProtectedEndpoint`'s own comment fixed). 75 of 75
mutations killed across three targets. Gap, not a workaround: `npm run
test:ts` still cannot go green on the workstation because one
`cultcache-ts` test spawns `python`, the Store alias stub here; `py -3`
has msgpack; pre-existing since `8cb3b72` and outside every cut.

**Cut 3 landed** (Opus) at `6a091ef` (the bridge: 440 → 1,168 lines of
C++, a two-role event-queue C ABI v2 with twelve exports, the five v1
exports kept as a Windows-only Unity shim) and `23dc093` (CMake for both
platforms, `scripts/build-quic-native.sh`, README). Host and target:
win32-x64 built and smoked on the workstation (MSVC 14.44, CMake 4.3.2);
linux-x64 built and smoked in the workstation's Docker `debian:13`, which
is the development loop and **not release-grade**; its release build is
Cut 6's Actions job. Fifteen-check FFI smoke 15 of 15 on both; the v1 gate
3 passed, 1 skipped by design; ten native mutations killed under two green
controls; exports 17 on Windows and exactly 12 on Linux with hidden
visibility; the deb digest and header sizes match the probe ledger;
manifests with SHA-256s beside every binary. Two bugs the old shape never
exercised, found by hanging tests and now mutation-pinned: the v1
certificate pin must be attached before `ConnectionStart` (the handshake
reaches the certificate event on a worker thread at once on loopback), and
teardown must close connection handles itself because `RegistrationClose`
blocks until every child is closed. Spec discrepancies kept: `ntdll` on the
Windows link list; the deb carries no licence text, so the script copies
the committed MIT text; the deb ships no unversioned `.so` symlink, so the
script creates one; four Linux runtime libraries are load-bearing and now
in the README. Soul in flight.

**Q9, the one fork Cut 3 hit, ruled A on 2026-09-16 (detail below).** The
pinned Windows MsQuic is the Schannel flavour, and Schannel does not
implement `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`: `ConfigurationLoadCredential`
returns `QUIC_STATUS_NOT_SUPPORTED`, so the provider role cannot open a
listener on Windows under it. The identical bridge DLL passes the full
smoke beside the OpenSSL 2.5.9 `msquic.dll` (SHA-256 `FEE9A664…`,
4,181,856 bytes). Linux is unaffected: Microsoft ships only the OpenSSL
build for Debian.
- **A. Pin OpenSSL on Windows.** One credential type on both platforms,
  provider role works, ABI unchanged. Costs: the package URL and digest in
  `build-quic-native.ps1`; the committed Windows `msquic.dll` grows from
  537 KB to 4.18 MB, so Q8's tree price becomes about 11.6 MB; the Unity
  plugin's `msquic.dll` diverges until the operator's unscheduled rebuild.
  The v1 client path works under either flavour.
- **B. Keep Schannel and convert PKCS12 to a certificate context via
  `PFXImportCertStore`.** Keeps the pin and the Unity DLL; puts about
  forty lines of Windows crypto in the shared provider path, breaking
  "runtime-neutral" and the cut's own negative grep.
- **C. Providers Linux-only.** Cheapest; kills the Windows FFI smoke the
  cut requires and the Windows provider lanes Cuts 4 and 5 need.
**Recommended: A. Ruled A, 2026-09-16** ("A"). The Windows pin moves to
`Microsoft.Native.Quic.MsQuic.OpenSSL` 2.5.9 in `build-quic-native.ps1`
with its digest; Q8's committed tree price is restated at about 11.6 MB;
the Unity plugin's `msquic.dll` stays Schannel until the operator's
rebuild, recorded in section 15, and the v1 client path is verified under
both flavours. The spec's "one credential type for Schannel and OpenSSL"
sentence in section 8 is withdrawn: the header declares the type and only
the OpenSSL provider implements it. *(Digest correction, Soul: `FEE9A664…`
is the NuGet zip, 19,179,817 bytes; the DLL is `c17c6581…`, 4,181,856
bytes. Hands' report and this map's first record had the zip's digest on
the DLL.)*

**Soul's pass on Cut 3** (Fable; `soul3-*` in the session scratchpad,
including a C consumer of the ABI that is not koffi and an ASan build of
the bridge) held the Cut 2 fixes entire, the export counts, the greps, the
clean-container Linux rebuild byte-identical to the manifest, the four
load-bearing Linux runtime libraries, the stress at 16,384 frames with
zero lost or reordered on both targets, both hang mutations dying by
timeout, and both of Q9's load-bearing claims (Schannel refuses the PKCS12
credential with not-supported; the identical bridge passes 15 of 15 beside
the OpenSSL DLL; the v1 gate passes beside either). **Cut 3 does not
close.** Found, in a fix batch (Opus):

- **A use-after-free between the host thread and MsQuic's worker** (high):
  the id lookups drop the lock and return a raw pointer, the worker frees
  the object on shutdown-complete, and the host's next send dereferences
  it. One in three release runs crashes on each target under an ordinary
  peer-driven stream teardown; three of three under ASan. The fix is an
  object kept alive across the call, not a guard.
- **Event 7 is not one per frame send**: the kind-byte send completes as
  its own event with nothing to tell it apart, six events for five frames.
- **The initiator of a shutdown never learns its connection is gone**:
  MsQuic gives it only shutdown-complete, which the bridge swallows.
- **Return codes invert by platform**: `QUIC_STATUS` is a negative HRESULT
  on Windows and a positive errno on POSIX, so a Linux host checking for a
  negative code misses every failure; the README promised negative.
- **`runtime_close` wakes a blocked poller into freed memory**; the wake
  makes the pattern look supported. Close must quiesce.
- **No host-facing header**: the struct layout and the twelve signatures
  live in the `.cpp` and the spec; Soul wrote its C consumer from the spec.
- Smaller: an unbounded per-connection trace string from the v1
  diagnostics; a listener config failure leaving a dead handle in the map;
  a stream emplaced after start; a partial listener open leaving an entry;
  a null-payload poll consuming the event; the DLL importing the VC
  runtime with the README silent.

The Q9 pin change and the manifest's stale commit line ride in the same
batch.

**The Cut 3 fix batch landed** (Opus; its commits are signed Opus 5, which
is what ran) at `9a01321` (`include/cultmesh_quic_native.h`, 305 lines:
the event struct with offsets static-asserted, every export, the id and
return-code conventions, the lifetime and threading rules; the koffi smoke
and Soul's C consumer are checked against it), `47aa7b6` (objects kept
alive across the call that names them: one `shared_ptr` per map entry, a
lookup returns a reference the caller holds for the whole MsQuic call, the
worker drops only the map's reference, the handle closes exactly once in
the destructor under the exchange guard, a stream owns its connection so
close order is structural; `runtime_close` marks closing, wakes pollers,
waits for `active_calls == 0`, and refuses any later call), `8c41896` (the
ABI gaps: one event 7 per frame send with `Canceled` distinct; event 4
uniform at completion for every path, the initiator included, published
after the id stops resolving; return codes mapped negative on every
platform with `cultmesh_quic_last_status` for the raw status, thirteen v2
exports now; a null payload with capacity refused; a stream in the map
before start; failed listeners leaving nothing) and `c729dff` (the OpenSSL
pin with both digests, `/MT` so the DLL imports no CRT, manifests with a
real commit line). Host and target: win32-x64 on the workstation, 20 of 20
smoke checks, the C consumer's seq, cancel, stress (16,384 frames, zero
lost or reordered), race 400×20 with zero crashes, closerace 20×512, the v1
gate 3 passed 1 skipped, 18 exports; linux-x64 in the workstation's Docker
`debian:13`, development loop only, the same set plus ASan clean on every
mode, 13 exports. 22 of 23 mutations killed under two green controls.
**L3 survived**: deleting the quiesce wait cannot be caught, because the
wake happens under the lock and every poller drains before teardown; right
by construction and by reading, unfalsifiable by this harness. Soul rules
whether that stands or a dev-only probe asserts the invariant. Open after
the batch: the Unity plugin's `msquic.dll` is still Schannel until the
package build runs; the Schannel dependency cache is orphaned; the v1
return-code values changed while the zero-versus-nonzero contract held.
Bridge 302,080 bytes / `f4c5f644…` (win32-x64, static CRT), 108,128 /
`61d5b96e…` (linux-x64).

**Soul's second pass on the fix batch** (Fable; `soul3b-*` in the session
scratchpad, its C consumer extended with close, re-entry and completion-race
scenarios, ThreadSanitizer and AddressSanitizer on linux-x64 in the
workstation's Docker `debian:13`). Held: the lifetime model, race 400×20 with
zero crashes on both targets and ASan clean on every mode; 16,384 of 16,384
frames under stress with none lost or reordered; one completion event per
frame with cancellation distinct; the completion event uniform at the end of
every connection, with its mutant killed on both targets; negative return
codes everywhere; the null-payload refusal; the header compiling clean as
both C and C++ under maximum warnings on two compilers with no padding and
no implicit conversion; no C runtime import; the OpenSSL pin re-downloaded
and both digests equal; the version-one gate green beside either flavour of
the library. **The cut does not close at `c729dff`.** Findings:

- **The quiesce is one line short, and it is a use-after-free.** `~CallScope`
  decrements the call count under the lock, releases it, and only then
  notifies. The closer's predicate is already true, so if that last poller is
  preempted in between, the runtime is destroyed and the notify lands on a
  freed condition variable. ThreadSanitizer caught it on the **unmutated**
  bridge, so the previous batch's model was right in shape and wrong by one
  line. Confirmed, medium, and the blocker.
- **The header oversells the return code.** It is portably negative, not
  portably meaningful: the same failure is −12032 on Windows and −1126 on
  Linux, and the Windows mask collides two distinct statuses onto −1002. The
  sign is the contract; the magnitude is diagnostic. Low, wording.
- **The previous batch's own README claims a rebuild reproduces the committed
  Unity plugin.** It does not: that plugin is the older Schannel build at
  40,448 bytes with a C runtime import, against 302,080 bytes without one
  today. Low, and exactly the stale-artifact trap. The current build does
  reproduce itself.
- **A Windows status sign-extends in diagnostics**, printing sixteen hex
  digits. Trivial.

**L3 is ruled: it becomes killable, it is not accepted as unfalsifiable.**
Soul established the survival was a harness limit rather than a proof of
correctness. Under AddressSanitizer with pollers blocked mid-copy of large
frames, deleting the quiesce wait hit use-after-free four times in
forty-eight attempts, while the release scenario never observed it. So a
dev-only assertion now aborts when the call count is non-zero after the
quiesce block, which fires deterministically with enough blocked pollers and
makes the mutation die by the existing scenario; the sanitizer run stays as
the second check. That assertion does not cover the defect above, where the
count has already reached zero. **Two mutations are honest only on the
sanitizer target:** the pop-then-copy one and the closed-handle one survive
on Windows release and kill three times of three under AddressSanitizer, so
the entries table must say which target each is honest on. A mutation whose
kill depends on the target is not killed until it runs there.

**The second fix batch landed** (Opus) at `a4753f4` (the notify moves inside
the gate), `dc9e12c` (a Windows status stops sign-extending in diagnostics),
`53d5929` (the header says the return code is portably negative, not portably
meaningful), `19d8d5c` (the readme says the committed Unity plugin is the
older build), `f4186e7` (a committed scenario runner for the runtime
lifetime) and `180917d` (four native entries, each naming the target it is
honest on).

**The discovery that outweighed the fix: there was no committed native
harness at all.** Every native mutation kill this cut had reported came from
consumers and scenarios that lived in agent scratchpads and no longer exist.
The runner at the previous head carried three package targets and nothing
touching the bridge, and two entries could not even be re-marked because
their definitions were gone. Hands built what the brief had assumed already
existed.

**Soul's third pass, 2026-09-17** (Fable). Held: the lifetime fix on both
targets, the reverted notify killed three of three by the thread sanitizer
and invisible to the address sanitizer, so its target marking is right; the
assertion killing the deleted wait deterministically on both targets and
absent from both shipped libraries and a fresh release build; two fresh
release builds byte-identical to the committed artifact; the return-code
numbers recomputed from the pinned headers; byte-exact restore on both hosts.
**Cut 3 still does not close**, and the reason is that the harness defending
the fix is weaker than the fix:

- **The committed Linux path does not run as committed.** The sanitizer
  configuration dies before the scenario starts, on the kernel's
  address-space randomisation, and the runner swallows the child's standard
  error, so a future agent sees a red control and no diagnosis. Soul reached
  four of four kills only by disabling randomisation and relaxing the
  container's seccomp profile, neither of which any committed file mentions.
  The image came from a definition that is not in the repository, and the
  documented development recipe cannot run the runner at all, since it
  installs the C++ toolchain but not the JavaScript runtime. High for the
  claim that the Linux kills are rerunnable.
- **The scenario does not test the rule it exists for.** Deleting the call
  guard from the polling entry point, so host calls are not counted at all,
  survived nine runs of nine across every configuration. The harness pins the
  order of the count and the notify, not that anything is counted.
- **The scenario passes vacuously when nothing parks.** Inverting the timeout
  comparison makes every poller return before the close begins, and the
  scenario is green on all six configurations.
- **The bounded-wait loosening is pinned by a constant, not by the rule.** At
  one millisecond it dies with as few as two calls outstanding; at fifty
  milliseconds it survives on both targets. A bounded wait is the wrong
  contract at any bound.
- **A skipped entry exits zero** with no closing summary, so a gate reads a
  host that killed nothing as green.
- **The header contradicts itself**, forbidding a call that begins after
  close starts and separately promising such a poll is refused, when that
  refusal path reads freed memory after teardown.

**Two rules are defended by nothing committed**: popping before copying, and
a handle closed under a live call. Both were previously reported killed on
the sanitizer target only, and neither has a definition left. The third fix
batch is told to give each a real entry or to say plainly that the rule is
undefended, because an honest gap beats a fabricated kill.

**The third fix batch landed** at `62e53df` (the development seam and a
hold-then-close scenario), `e4e09a0` (four entries, the bounded wait at 50 ms,
an exit status on skips, the child's stderr on a red control, re-execution
with randomisation disabled), `12c8bfc` (the header's self-contradiction) and
`4407098` (the committed container, the Ninja generator, the README recipe
with the seccomp requirement).

**Soul's fourth pass, 2026-09-17** (Fable, banked mid-pass when that model ran
out, resumed on Opus from the bank; the handoff paid for itself by carrying a
live harness process, a dirty tree and two leads the successor opened with).
**The verdict splits, so it is given twice.**

*The infrastructure question is now yes.* The committed container built with no
cache, then the literal README run line, gives eight killed, none surviving,
none skipped, exit zero on Linux; Windows gives six killed, two honestly
skipped, and a real exit code. A stranger can rerun both targets today. The
seccomp requirement is genuinely load-bearing and the randomisation
re-execution genuinely runs. The development seam is absent from a release
build: thirteen production symbols, no debug symbols, no assertion strings,
and every executable section byte-identical between pre- and post-change
release builds. The predecessor's harness had already exited, restoring the
tree byte-exactly, so the restore path holds.

*The rules question is still no.* Three of the four rules in the native table
are pinned. The fourth is not, and the cause is structural rather than a bad
constant:

- **F1, confirmed, high: the hold sits after the wait whose duration is the
  rule**, so every timeout mutant lands in a blind spot by construction.
  A hard-coded 100 ms survives Windows and Linux with assertions, and dies
  under the thread sanitizer only because its slowdown trips an unrelated
  condition. **A hard-coded 2000 ms survives the entire matrix on both
  targets.** A host asking for five seconds silently gets two and every
  consumer's poll loop spins at two and a half times the rate it asked for,
  forever. The committed one-millisecond entry dies only because one is less
  than the scenario's fifty-millisecond settle: constant pinning constant.
- **F2, confirmed, high: the pop-before-copy gap was wrong about itself.**
  A client opening to a closed loopback port reaches that path in about a
  millisecond with no listener, credential or connection, because the failure
  path always publishes a non-empty reason; fifteen lines. Moving the pop
  before the copy then crashes an ordinary optimised build twice out of two,
  while both committed scenarios stay green on that same binary.
- **F3, confirmed, medium: the refusal during a close has no entry** and
  survives being neutered, though the header promises it. A call entering
  behind the closer increments the in-flight count behind the wait, which can
  then never reach zero.
- F4, low: the seam misdescribes itself, claiming the held call touches
  nothing afterwards when its scope destructor takes the gate, decrements and
  notifies. The safety comes from the assertion, not the hold.
- F5, low: the bounded wait at 600 ms survives, the same shape as F1 and
  honestly labelled as a limit by Hands.
- Also: "the close wakes every blocked poll" **is** defended by committed code
  but has no entry, so the table understates coverage in one place while
  overstating it in another.

**Two scars from the pass itself.** An export made with `git archive` carried
1,459 inserted carriage returns, poisoning any byte comparison built on it;
raw blobs verified against the worktree hash are the only safe source. And the
51 bytes that differ between pre- and post-change release builds are accounted
for: 48 are four copies of the reproducibility content hash, and the last
three sit in exception metadata outside every executable section and **could
not be fully accounted for**, which Soul said rather than rounding away. The
same source rebuilt at the same path twice differs by zero bytes; built
elsewhere it differs by 69, so the figure is only meaningful at a shared path.

**One undocumented step remains** in an otherwise clean path: the README
hardcodes the container mount, so a newcomer on another checkout must edit the
line.

**The fourth fix batch landed** at `25d7de2` (the container mount taken from
the running checkout rather than one machine's path) and `a937a0b` (the
scenarios, the seam correction and the new entries).

**Hands rejected Self's diagnosis and gave a better one.** Self had said the
blind spot was that the hold sat after the wait whose duration was the rule.
The real cause is that **no committed scenario ever let a timeout finish a
poll**: both ended their waits with a close, so the timeout governed nothing
either could observe. The fix is therefore a scenario rather than a seam
recording. `polltimeout` polls an idle runtime and measures elapsed against
what was asked, **at two values with non-overlapping bands** — 200 ms in
[180,500] and 900 ms in [880,1200] — because one value cannot tell a bridge
honouring the host's timeout from one waiting on a constant that happens to
equal it, and no constant lies in both bands. No new export, no header change,
and it works in the shipped shape.

All three constants now die by that scenario: 100 ms, 600 ms and 2000 ms. The
600 ms case survives both older scenarios and dies to the new one, which is
the demonstration of what the old ones could not see; Hands kept it as a hand
check rather than carrying a third constant in the table.

Also landed: Soul's closed-port probe committed as `payloadfit`, reaching the
two-phase poll in about a millisecond, where the pop-before-copy mutant
segfaults an optimised Windows build with no sanitizer, five runs of five; an
entry for the refusal of a call racing a close; an entry for the wake, which
the table had been silently failing to claim; and the seam's self-description
corrected, since a held call does leave its scope, which takes the gate,
decrements and notifies. Release shape unchanged on both targets, with no
debug exports and no assertion strings.

**Tables now: Linux 18 killed, none surviving, none skipped, exit 0. Windows
16 killed, none surviving, two honestly skipped, exit 2.** Controls green on
both.

**A declared equivalence, handed to Soul rather than substituted.** Changing
the close's wake-all to a wake-one survives, and Hands argues it is equivalent
rather than uncovered, because the first poller out calls wake-all under the
gate from its own scope destructor. It recorded the equivalence beside the
entry and shipped the queue-conditional wake as the loosening instead. This
campaign's fourth encounter with that defect shape, and the first where the
answer was an argued equivalence rather than a swap.

Named limits: the 600 ms mutant lives only in the report; `payloadfit` and
`polltimeout` run only in the assertion configuration, so the pop-before-copy
kill rests on an unsanitized crash and an exit code rather than an
address-sanitizer configuration the harness still lacks. **Soul's fifth pass, 2026-09-17** (Opus). Both targets reproduced end to end
from a fresh clone at a different path. **Cut 3 still does not close, and for
the same underlying reason in a third costume: the scenario proves the wait is
not a constant, not that it is the host's.**

- **F1, confirmed, high: an expression defeats the two-band argument.** No
  constant sits in both bands, which is true and is not the rule. Any mapping
  that is identity at 200 and 900 passes for free, and the late tolerance is a
  flat 300 ms. Three mutants, each run through all five Windows scenarios, all
  **survived all five**: clamping the wait to at most a second, adding 150 ms,
  and scaling by six fifths. **The clamp is the one that matters**, because its
  failure is verbatim the bug this batch claims to fix — a host asking for five
  seconds gets one, forever, with nothing reporting it — and "cap the wait so
  shutdown gets noticed" is the most ordinary spelling that line will ever be
  given. The close is cheap: raise the long probe above any plausible ceiling,
  which kills the clamp and the scale, and make the tolerance proportional
  rather than flat, which kills the offset.
- **F2, confirmed, medium: a Windows clone cannot run the documented Linux
  path at all.** `.gitattributes:2` is `* text=auto` with no rule for shell
  scripts, so a default Windows clone writes the build script with carriage
  returns and the container dies in under a second complaining about the
  interpreter line. Soul hit it from a clean clone before anything else ran.
  The mount fix works; this is a second, independent blocker on the same
  documented path, and it fires for the likeliest newcomer. One line repairs
  it.
- **F3, confirmed, low: the only non-comment source change in the range is
  pinned by nothing.** Reverting the hold's new guard survived all five
  scenarios, because the timeout scenario never arms the hold and the other two
  never let a timeout expire, so nothing is ever in both states at once. The
  seam folds away in release, so nothing shipped is at risk; it is the
  recurring shape again.

**The declared equivalence holds, and its stated reason was the weaker half.**
Narrowing the close's wake to one waiter is genuinely equivalent, reproduced.
But the equivalence rests on two premises together: the closing flag is set
permanently under the gate before the notify and appears in every waiter's
predicate, so after a close begins no poller can block at all; and each exiting
call's own destructor takes the gate, decrements and wakes everyone, cascading.
Soul then narrowed **both** wakes, which also survived and **is not equivalent
by any argument**, since a notify consumed by the closer stalls the chain. It
passes on wait-queue ordering alone. The premises belong beside the entry so
the claim is checkable rather than assertable.

**Held:** Linux 18 killed and Windows 16 with two honest skips, both
reproduced; 600 ms dying to the new scenario and surviving the older two;
the closed-port probe reaching the two-phase poll and both pop-before-copy
entries dying on both targets; the release shape clean on both, with the new
local folding away. **The red-control path proved itself unasked** — a run
from a deep temporary path failed MSBuild's file tracker, and the harness
reported a red control with the child's output rather than banking eighteen
kills it had not earned.

**Two record corrections.** The source digest quoted in the previous report is
checkout-dependent, because line-ending conversion changes it; Soul's clone
restored to a different one. It is not a portable identity. And the Windows
harness needs a short checkout path or the control fails on the file tracker,
which belongs where a newcomer meets it. **Fifth fix batch in Hands.**

**The fifth fix batch landed** (Opus), `ca6c0f5..244154e`. F2: `*.sh text
eol=lf` in `.gitattributes` (`ca6c0f5`); the container line no longer picks a
shell's continuation, the Dockerfile's recipe mounts the running checkout, and
the win32-x64 short-path requirement is stated where a newcomer meets it
(`c177533`). F1: the long probe is 5000 ms and the late tolerance is
`60 + timeout/8` rather than a flat 300, which kills the clamp and the scale on
both checks and the offset on the short one; the clamp is its own entry. Stated
limit, in source beside the tolerance: **a scaling smaller than an eighth
survives** (`c682619`). F3: `holdtimeout` puts a call in both states; the
revert and the reordered-conditions loosening both die there at about 800 ms
and on nothing else (`5ab4c62`). The single-waiter equivalence now states both
premises beside the entry, and narrowing both wakes is recorded as **not yet
reached**, with why a scenario cannot force it while the first premise holds
(`b985f07`). The restore digest is printed as a restore check, not an identity
(`244154e`). Hands' numbers: win32-x64 19 killed, 2 honest skips; linux-x64 21
killed. **Soul's sixth pass dispatched 2026-09-22** (Opus).

**Soul's sixth pass, 2026-09-22** (Opus). Hands' table reproduced exactly from
fresh `autocrlf=true` clones: win32-x64 19 killed, 2 skips; linux-x64 21
killed; controls green. **Cut 3 still does not close.** Every scenario so far
has a single-threaded host.

- **N1, confirmed, medium: the wait's predicate is pinned by nothing.** Dropping
  it (`wait_for(lock, ms(t)) == no_timeout`, `cultmesh_quic_native.cpp:1179-1180`)
  survived the whole matrix on both targets. At the bridge, a second host thread
  calling `cultmesh_quic_last_error` every 20 ms made an idle `poll(1000)` return
  after 0 and 20 ms, where the real bridge stays 1000. Every `CallScope`
  destructor wakes all waiters (`:286`), so a host with a poll thread and a send
  thread spins at the rate of its other calls. The rule "a poll with nothing to
  deliver stays for its timeout" is pinned only for a host with one thread.
- **N2, confirmed, low-medium: the stated limit understates what survives.**
  Survived on both targets: `min(t,5000)`, `min(t,4990)`, `t*11/10`, `t+70`.
  `max(t,220)` survived on Linux and died on Windows. `max(t,250)` and `t+80`
  die only on `holdtimeout`'s 150 ms probe, by margins of 22 and 2 ms, so those
  kills depend on timing. "5000 is above any plausible ceiling" is wrong,
  because 5000 is itself a round ceiling, and no probe can see a floor under
  about 60 ms. Either add a small-timeout probe and a probe above 5000, or state
  the full limit: offsets to about 75 ms, floors to about 220 ms, and clamps at
  or above about 4980 ms.
- **N3, confirmed, low-medium: the LF fix does not reach an existing clone.**
  A clone made at `367c9bc` and moved to `244154e` keeps the CRLF script, since
  the blob did not change. Its status is clean and it still dies on `bash\r`.
  Nothing tells an existing clone to check its `*.sh` out again.
- **N4, confirmed, low (development seam): no hold mutant is a function of the
  input.** `(woken || timeout_ms > 1000) && HELD` survived on both targets.
  Both of Hands' hold entries only rearrange the guard's syntax.

**Held:** F2 from a fresh clone, covering every file the container runs or
parses. F3's revert and its loosening, with only `holdtimeout` failing under the
revert. The clamp entry. Both equivalence premises, checked against source, with
"narrow both wakes" recorded as not yet reached. Restores verified by hash
against `checkout-index`. Sidecar repair after a mid-mutation `taskkill`.
`polltimeout` and `holdtimeout` stayed green for five rounds under 16 burners on
8 CPUs. **Not run:** a forced red control, and Windows under a CPU burner.
**All four go to Hands as the sixth fix batch.**

**The sixth fix batch landed** (Opus), `b3d9cf7..ff72f16`. Details:

- **N3 (`b3d9cf7`):** the README gives an existing clone a one-line repair beside
  the container path. It is `git rm --cached -q scripts/build-quic-native.sh;
  git checkout HEAD -- scripts/build-quic-native.sh`. A plain checkout keeps the
  CR. The line was verified on clones made at `367c9bc` from Git Bash and from
  PowerShell 5.1.
- **N2 (`37967e8`):** the timeout probes are 15, 200 and 7300 ms. The 15 ms
  probe takes the fastest of ten tries, and every try is still checked for an
  early return. There are new entries for a round clamp, a floor and an
  offset. The limits measured on both targets are now written in source:
  - `t+25` survives and `t+40` dies;
  - `max(t,40)` survives and `max(t,50)` dies;
  - `t*106/100` survives and `t*108/100` dies;
  - `min(t,7285)` survives and `min(t,7275)` dies;
  - any shortening up to 20 ms survives.

  The "above any plausible ceiling" claim is deleted.
- **N1 (`4ffa4c9`):** `pollbusy` runs a second host thread that calls into the
  bridge while a poll is idle. Three entries sit on it: the revert, "reads
  what woke it", and "each wake restarts the whole timeout". All three die on
  `pollbusy` on both targets.
- **N4 (`ff72f16`):** `holdtimeout` makes a second held poll at 1300 ms. The
  input-derived hold mutant dies there and nowhere else.
- **Recorded as not yet reached:** the loosening that checks only `closing`.
  It breaks "an arriving event ends the wait early", a promise no scenario
  measures.

Hands' numbers:

| Target | Killed | Survived | Skipped | Control |
|---|---|---|---|---|
| win32-x64 | 26 | 0 | 2 | green |
| linux-x64 | 28 | 0 | 0 | green |

Under 16 burners on 8 CPUs, 15 of 15 rounds passed on each target. The worst
single poll was 15 ms late on Windows and 41 ms on Linux.

**The batch added one mechanism nobody specified. Self accepted it, 2026-09-22.**
On Windows the timed scenarios raise their own process priority. Without that,
Windows under load put polls up to 127 ms late. The unmodified bridge then
failed even the pre-batch tolerance: the 150 ms hold probe came back 100–115 ms
late against 78 ms allowed. The only alternative was a flat allowance of about
150 ms, which would readmit the offsets and floors this batch exists to kill.
The priority change is test-fixture only, needs no admin rights, and changes
nothing in the bridge. Accepted, and handed to Soul to attack.
**Soul's seventh pass dispatched** (Opus).

**Soul's seventh pass, 2026-09-22** (Opus). Reproduced from a fresh clone:

| Target | Killed | Survived | Skipped | Control |
|---|---|---|---|---|
| win32-x64 | 26 | 0 | 2 | green |
| linux-x64 | 28 | 0 | 0 | green |

Exit 2 on win32 is by design: it means entries were skipped
(`mutate-cultmesh.mjs:1192`). A forced red control was reported red, and the
harness ran nothing after it. Restores matched `checkout-index` by hash. N3
holds from both shells. **The priority raise is sound.** It is scoped to the
process, which exits after each scenario, and it cannot hide a lock held
across a sleep, because every thread in the process is raised equally.
**Cut 3 still does not close.**

- **S1, medium: N1 is not closed.** Two predicate mutants survive the full
  matrix on both targets:
  - **K1**, `|| ++wakes > 64`. Under a thread calling in a yield loop, an idle
    `poll(1000)` returns after 0–5 ms.
  - **K2**, `|| !runtime->error.empty()`. After one refused call, every later
    idle poll returns at 0 ms.

  `pollbusy` covers one call kind, at one pace, on a runtime with no error
  recorded.
- **S2, medium: the 15 ms probe has no early check.** The check is
  `elapsed < 15 - 20`, which cannot fail. Both `t<50 ? 0 : t` and `t/16*16`
  survive.
- **S3, low-medium: more derived waits survive.** Rounding up to a 40 ms
  quantum survives. So does any fault that only shows after a runtime's 12th
  poll, because no scenario polls a runtime more than 12 times.
- **S4, low: two stated boundaries depend on timing on Windows.** `t+25` died
  in one of two repetitions. `min(t,7275)` survived in one of two.
- **S5, low:** the hold guard `timeout_ms > 1500` survives. The stated limit
  should put the ceiling at 1300.

**Self's ruling, 2026-09-22: change the method, not the probes.** This rule has
now taken seven passes. Each fix batch pinned the functions the previous Soul
named, and the next Soul found another function that equals the identity at
every probe point. A rule about the arithmetic of a derived wait cannot
converge under wall-clock observation from outside, because there is always
another function that matches at the probes. The skill already says it: put
the observation where the rule is decided.

- **The wait.** A dev-only seam, of the same kind as the existing hold seam
  and folded away in release, records the timeout the bridge actually hands to
  its condition wait on every poll. A scenario asserts that it equals the
  host's argument **exactly**:
  - over a spread of values: 0, 1, 15, 16, 39, 40, 999, 1000, 1001, 7300,
    `INT32_MAX`, and the bridge's documented maximum if it has one;
  - on polls well past the 12th on a single runtime.

  Every clamp, floor, round, scale, offset and later-poll mutant then dies by
  equality, deterministically, on every host, with no timing tolerance. The
  wall-clock scenarios stay as proof that the recorded wait is actually waited,
  with **generous** margins. The wide boundary table goes: those limits existed
  only because the observation was indirect. Stated limits that are no longer
  true are deleted, not kept as history in source.
- **The predicate.** A `pollhammer` scenario runs one host thread that calls
  every entry point which touches the gate, in a yield loop, while another
  thread holds an idle poll. It runs once on a clean runtime and once after a
  refused call has recorded an error. The poll must stay for its timeout,
  within a generous margin. K1, K2 and a "returns on any wake" revert must die
  there.
- **The seam itself** gets a revert entry and a loosening entry: a seam that
  records the argument instead of the computed wait must die. The release shape
  must still fold it away.

To Hands (Sonnet) as the seventh fix batch.

**The seventh fix batch landed** (Sonnet), `66830e9` and `b8fad13`.

What was added:

- **`waitseam`** checks the wait by equality. A dev-only seam, gated like the
  hold seam and folded away in release, records the timeout the bridge hands
  its condition wait. The scenario asserts it equals the host's argument for
  0 to `INT32_MAX` and over 40 polls on a single runtime. For
  `timeout_ms <= 0` the bridge never reaches the wait, so the scenario asserts
  the seam was left untouched. That is also what kills the loosening "records
  unconditionally". The bridge documents no maximum below `INT32_MAX`.
- **`pollhammer`** covers the predicate. It runs every gate-touching entry
  point in a yield loop, both on a clean runtime and after a recorded error.

What was removed:

- the fastest-of-ten mechanism;
- the tight tolerances, replaced by one generous 800 ms margin;
- the stated-limits table;
- **the Windows priority raise.**

Which scenario kills which mutant:

- K1, K2 and the "returns on any wake" revert die on `pollhammer`.
- Every derived wait from passes 5 to 7 dies on `waitseam`. That includes the
  quantum rounds, the small-timeout zero, doubling after the 12th poll,
  `t+25` and `min(t,7275)`. All of them are now deterministic.
- S5's `>1500` hold guard dies on `holdtimeout`, with a probe at 1600 ms.
- The seam's revert and loosening die on `waitseam`.

Harness results, with restores matching `checkout-index`:

| Target | Killed | Survived | Skipped | Control |
|---|---|---|---|---|
| win32-x64 | 39 | 0 | 2 | green |
| linux-x64 | 41 | 0 | 0 | green |

Stress: 5 rounds with 16 burners on each target, all green. Worst overshoot
was 325 ms on Windows and 26 ms on Linux.

**Scar:** the stress cleanup used `taskkill /IM node.exe /T`. That killed every
Node process on the shared host. The rule now in the Eureka skill is to kill
by PID. **Soul's eighth pass dispatched** (Opus).

**Cut 1 Soul findings, 2026-09-16.** Held: every test count, both negative
greps, all twelve mutations rerun and killed, the control catching a
truncated write, the equivalent mutant confirmed (Node 24 WebCrypto refuses
every non-64-byte P1363 signature tried, across forty keys), the separator
byte matching C#, and the TypeScript-signs-C#-verifies direction proven by
a probe over adversarial inputs, which nothing in the repo yet pins. Found:

- **The mutation runner leaves the last mutant compiled in `dist/`**, which
  is git-ignored, and the browser tests then pass against it. A stale asset
  impersonating logic, the doctrine's own trap. Medium.
- **The C# half of the vector check never runs in CI.** The only `dotnet
  test` of that project is filtered to one lease test; a C# transcript
  change would leave the committed vector self-consistent and nothing red.
  Medium.
- **`verifyP256` fails closed on Node `Buffer` inputs**: `.slice().buffer`
  on a pooled buffer is the whole pool. Internal callers allocate their own
  bytes, so it is safe today and wrong for the Cut 4 consumer. Medium.
- **The `protocolIds` sort is untested**: the vector's ids were written
  sorted, so removing the sort survives. Low-medium.
- **Four edge divergences from the C# rules**: an empty signature is
  "unsigned" in C# and "invalid" in TypeScript; C#'s loopback set is wider
  (`127.0.0.2`, and the TypeScript test pinned the narrower answer); root
  lookup and validity window check in opposite orders; duplicate root key
  ids throw in C# and take-first in TypeScript. All aligned to C# in the fix
  batch.
- **The negative grep pins names, not structure**: a local shim under
  another name passed every browser test and both greps. A guard-strength
  note, recorded.
- **A killed runner leaves the mutant in the tree**; a sidecar restore is
  added, as Epiphany's harness has.
- **`cultmesh-ts` resolves modules with `moduleResolution: "Node"`**, which
  ignores `exports`, so `cultnet-ts/authority` is unresolvable from that
  package until Cut 4 changes it; the root re-export covers it today.

**The Cut 1 fix batch landed** at `c2ffc32` (`verifyP256` copies its bytes;
empty or whitespace signature is unsigned; root lookup before the validity
window; duplicate root key ids refuse at first use; the loopback set is
C#'s, probed through the public policy: `127.0.0.0/8` in any spelling
`URL` normalises, `::1`, the IPv4-mapped `::ffff:7f00:1`, `localhost`,
`loopback`; `localhost.`, `0.0.0.0`, `::` and `::ffff:127.0.0.2` are not),
`1590145` (`scripts/sign-cultmesh-authority-vectors.mjs` writes
`contracts/cultmesh/authority-route-vectors.ts-signed.json` over adversarial
inputs and a C# test verifies it; a one-character change to the committed
signature fails that test), `cf1203a` (`cultmesh-portability.yml` runs
`dotnet test --filter AuthorityProof` on both OSes on every push; the
interop workflow was Windows-only and main-only) and `0e4f479` (the runner
rebuilds `dist/` after the final restore and asserts a sentinel from the
restored source; a sidecar of the original bytes is written first and
repaired from at the next start). Twenty mutations, twenty killed. Tests
74, 17, 10; closure smoke four tarballs.

Scars from the batch: **the runner's two multi-line anchors matched nothing
on a CRLF checkout** (the tree is `autocrlf=true`, HEAD is LF, the anchors
were `\n`), so the runner would have thrown at those entries; the earlier
"killed" claims for the transcript-order and nonce mutations came from an
LF-on-disk state. Anchors are now normalised to the file's EOL. Fixes 1 and
2 share one commit because Hands had made both before committing.
**Still open after the batch:** the browser client's local-development
short-circuit at `packages/cultmesh-browser/src/index.ts:915` tests
`!route.certificate` where the shared verifier now treats an empty
signature as unsigned, so such a route passes the verifier and is then
asked for a provider proof, where C# would not ask. **Landed** at `a801e0a`:
`isUnsignedCertificate` is exported from the shared module and used by the
verifier and the browser's session check, with a browser test for the
blank-signature loopback route, the signed loopback route that still needs
a proof, and the blank-signature remote refused in both modes. Twenty-one
mutations, twenty-one killed. Tests 74, 18, 10.

**Second Soul pass, 2026-09-16.** Held: byte copy, root before window, duplicate
root ids refusing on the unsigned path in either insertion order, the sort
pinned, the U+001F separator on both sides, 33 of 39 field tampers failing the
C# test with the survivors explained, the runner repairing itself after a
SIGKILL and leaving `dist/` free of every mutant string, all 21 anchors
matching once on both LF and CRLF copies, and every test and grep. Cut 1
closes on its stated invariants. Four edge divergences from the C# rules
remain, with one guard gap, to be fixed before Cut 4 makes a non-C# signer
real (in Hands):

- **`isLoopbackEndpoint` is not `Uri.IsLoopback`**: 15 of 90 probed
  endpoints disagree. Seven are reachable and TS-wider (a trailing dot on
  the host, `ws:` without `//`, a backslash separator, fullwidth characters,
  a tab), because `URL` normalises what C# refuses; eight are C#-wider and
  unreachable in the browser (zone ids the `URL` parser throws on,
  `file:`/`mailto:` schemes the filter drops). The doc comment claimed
  equivalence. Low: local-development only.
- **`trim()` is not `char.IsWhiteSpace`** at U+0085 (C# whitespace, TS not)
  and U+FEFF (TS trims, C# does not), so a signature of either is
  "unsigned" on one side and "signed" on the other; both are carriable on
  the rendezvous wire. Low, fail-open only where an absent certificate is
  already accepted.
- **C# cleans the route before the transcript and TS transcribes raw**:
  duplicate or padded protocol ids, padded generation and padded key ids
  verify in C# and refuse in TS, and an absent protocol list gets a default
  in TS and `""` in C#. Latent while every signer is C#; live at the first
  TS signer. Low-medium.
- **Two C# refusals collapse into one TS message** (not base64; not P1363).
  Low.
- **A local `=== ""` shim beside the shared call in the browser survives
  every test**, because no browser test uses a whitespace signature; the
  name grep is silent by construction. Low-medium.
- **No workflow runs the `cultnet-ts` tests on Linux**, and the interop
  workflow is Windows-only and main-only; on a `codex/**` push only the C#
  half of the vector check runs. Low.
- Recorded, not a defect: the TS-signed vector is not reproducible
  byte-for-byte (random `k`); the C# test pins that the committed bytes
  verify under current rules, and the TS test pins that the committed file
  verifies under the current module.

**The edge-alignment batch landed** (Opus) at `e67e695` (C#'s whitespace
set through three named helpers; the two signature refusals split to C#'s
texts), `c4c2330` (route fields cleaned before the transcript as the C#
constructors clean them: trim, drop blanks, distinct, sort; generation and
key ids trimmed; a null or empty protocol list transcribes as `""`, and the
TS default that wrote bytes the reference never writes is gone; `verseId`
deliberately raw, because `CanonicalRoute` passes it raw), `803ee12` (a
raw-endpoint pre-check refusing the spellings WHATWG `URL` repairs and
`System.Uri` does not; loopback disagreements 15 → 6, all six documented
unreachable), `dfe582e` (the browser's local reading of "unsigned" killed
by a whitespace-signature session test; the runner mutates the browser
target too, with per-target sidecar, control, sentinel and post-restore
assertion) and `0dfef0e` (the TypeScript authority tests in CI on both
operating systems). Soul's probes rerun: 88 endpoints, zero reachable
disagreements; 34 code points, zero; five signature shapes, byte-identical
messages; fifteen field tampers, zero mismatches against C#'s answers.
Tests 77, 18, 79, 10; closure smoke four tarballs; 33 shared and one
browser mutation killed.

Two scars from the batch, both recorded because they are the kind a
postmortem should carry: **the runner's sidecar bit twice**, a throw inside
the mutation loop leaving it behind so the next run "repaired" from stale
bytes and silently reverted two fixes Hands had just made (the sidecar is
now removed in the `finally` immediately after the verified restore); and
**a literal NUL byte was committed inside a regex** at `803ee12`, which made
git classify the file as binary, stop normalising its line endings, and
diff the whole file as a rewrite, escaped at `dfe582e`. Residuals named,
not guessed at: `isProtectedEndpoint` did not get the pre-check and no
probe covers protected-scheme spellings; a negative priority throws in C#
and transcribes in TS, unreachable while only C# signs; two browser
mutations survive because the verifier refuses those routes first.

**Third Soul pass, closing.** Held: all 34 whitespace code points in the
browser path too, with C#'s `VerifySessionProof` answer as the
expectation; the three signature refusals byte-identical; cleaning parity
in both directions with fresh keys (nine of nine each way, a padded
`verseId` refused cross-wise, C# confirmed not to trim it); 88 loopback
spellings with only the documented unreachable disagreements; the runner's
control writing through the same path as its mutations; CI's job, matrix,
triggers and setup order as promised; every suite, the closure smoke, both
greps. **Cut 1 is closed; Cut 2 may start.** Three residuals go with Cut 2's
Hands pass:

- **`isProtectedEndpoint` lacks the raw-shape pre-check** that
  `isLoopbackEndpoint` got, so eleven spellings C# refuses as unparseable
  are accepted and dialled after repair by `URL`. A trusted signer would
  have to emit a malformed endpoint, so security holds and the parity
  invariant does not. Five lines. Low-medium.
- **A literal U+001F byte sits in the separator string** in source and
  twice in the tests, where C# spells it ``; git does not treat it as
  binary, but any editor that strips C0 controls changes every signature
  with no visible diff. The NUL scar one rank lower.
- **The sidecar footgun is narrowed, not removed**: a SIGKILL still leaves
  it, and an edit made before the next run is overwritten by the repair.
  Repair must refuse when the file matches neither the sidecar nor the
  mutant.

Two corrections to this map's own record of the batch: **the NUL scar was
overstated**, since the byte sat past git's binary sniff and no whole-file
rewrite exists in history, only the normalisation path saw it and the blob
was LF-only anyway; and **CI was not green throughout the batch**, two of
its commits failing an unrelated Eve checkpoint job before the authority
steps existed, green from `0dfef0e`.

Seen by Hands, recorded for section 15: `packages/cultnet-ts/src/generated/swarm-contracts.generated.ts`
is rewritten with LF by every build while the tree is `autocrlf=true`, so
it shows modified with zero content diff after any build; the generator's
line endings and the repo's normalisation disagree, and every pass discards
the churn by hand.

Anchor: repo `F:\Projects\CultLib`, branch `main`, HEAD
`8d8ad568aa280bac34bf123503a9e38c175eb558`. The three commits since the
original anchor `f2cd2eb4` (`bf9f6f7`, `3c29fc9`, `8d8ad56`) touch only this
document `(probe: git log --stat)`, so every `file:line` below, read against
`f2cd2eb4`, is still exact at `8d8ad568`. If HEAD moves, re-anchor before
cutting.

How to read this document: section 1 prices the largest liability first, as
the target requires, because if that price had turned out to dominate the
campaign, the cut order below would have been wrong. Section 2 is the probe
ledger; every mechanism claim in this map carries `(probe)` when it was
established by running something, or `(source read)` when it was established
by reading a file at a named line. Nothing is claimed from a package name or a
README. Section 3 is the consumer audit. Section 4 names rejected options and
why. Section 5 is the authority map for every ownership change. Sections 6-12
are the cuts. Section 13 holds the operator questions; section 14 the
subtraction estimate; section 15 what could not be assigned.

Two corrections to the target, recorded here rather than silently absorbed:

- The target cites `src/GameCult.Mesh.Quic/CultMeshRealtimeTransports.cs`.
  That file is `src/GameCult.Mesh.Quic/CultMeshQuicRealtimeTransport.cs`
  (674 lines). `CultMeshRealtimeTransports.cs` exists, but in
  `src/GameCult.Mesh/`, and holds the transport-neutral contracts
  (`CultMeshRealtimeDelivery` at :8-13, `CultMeshRealtimeFrame` at :19-44,
  `ICultMeshRealtimeTransport` at :46-52,
  `ICultMeshRealtimeTransportConnector` at :55-63) `(source read)`.
- This map's first issue attributed the phrase "shipped through a registry"
  to the target. The target contains no such phrase and no word "registry"
  `(probe: rg)`; the reading was the map's, and it is withdrawn. What is
  true: today only `@gamecult/cultcache-ts` has a registry publish job
  (`.github/workflows/publish-packages.yml:20-24`, `:40-76`); `cultnet-ts`,
  `cultmesh-ts` and `cultmesh-browser` have none, and every external consumer
  reaches them by `file:` path (section 3). Q5 was ruled C on that Body: no
  registry in this campaign, publication parked.

## 1. The largest liability, priced first

The target's biggest new surface is CultLib owning native MsQuic bindings for
Node on two platforms. The price depends on three things: what Node itself
offers, what the repo already owns, and how a native artifact reaches a
consumer host without a toolchain. Each was probed.

### 1.1 What Node offers

- Node `v24.15.0` on Starfire has no `node:quic`, with or without
  `--experimental-quic` (`ERR_UNKNOWN_BUILTIN_MODULE`), and its internal
  binding table has no `quic` entry (`No such binding: quic` under
  `--expose-internals`). `process.versions` reports `openssl 3.5.5`, no
  `ngtcp2`, no `nghttp3`, `napi 10` `(probe)`. Yggdrasil runs Node `24.14.1`
  from NodeSource (`F:\Projects\gamecult-ops\inventory.md:660`) `(source read)`,
  so the same absence holds on the deploy host.
- Conclusion: there is no first-party QUIC in this Node line. Anything that
  works is native.

### 1.2 What the repo already owns

- `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp` (440 lines) is a
  CultLib-owned C ABI over MsQuic: `cultmesh_quic_open/state/poll/error/close`
  (`:309-440`). It negotiates ALPN `cultmesh-state-v1` (`:32`, `:341-344`),
  uses deferred certificate validation and answers the pin decision through
  `ConnectionCertificateValidationComplete` (`:239-263`), reassembles the
  1-byte-kind plus 4-byte-LE-length QUIC stream framing (`:123-157`), and
  queues whole encoded frames for a polling host (`:400-418`). It is Windows
  desktop only by construction: `<windows.h>`, `<wincrypt.h>`, `<bcrypt.h>`
  (`:1-3`), `CryptHashCertificate2` for the pin (`:184-200`),
  `__declspec` exports (`:18-22`), and the CMake build refuses non-Windows
  (`CMakeLists.txt:4-6`) `(source read)`.
- The bridge is consumed by the Unity connector through `DllImport` of library
  name `gamecult_mesh_quic_native`
  (`src/GameCult.Mesh.Quic.Native/CultMeshNativeQuicRealtimeTransport.cs:259-277`),
  which polls every 2 ms by default (`:14`, `:167-203`) `(source read)`. The
  built DLL and `msquic.dll` 2.5.9 are committed at
  `unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/` (last rebuilt in
  `a0813c6`, 2026-09-14) `(probe: git log)`.
- The Windows build is scripted and pinned: `scripts/build-quic-native.ps1`
  downloads `Microsoft.Native.Quic.MsQuic.Schannel` `2.5.9` by URL, checks its
  SHA-256 (`:13-14`, `:27-41`), and builds with CMake `-A x64` (`:48-52`).
  The last build on this machine used Visual Studio 17 2022 Build Tools, MSVC
  14.44 (`artifacts/quic-native-build/x64/Release/CMakeCache.txt`) `(probe)`.
  `cl.exe` is not on PATH but Build Tools are installed, so the existing
  script works here as-is.
- `tests/GameCult.Mesh.Quic.Native.Tests/GameCult.Mesh.Quic.Native.Tests.csproj:20-29`
  builds that bridge before the test build and copies it beside the test
  output `(source read)`. `scripts/build-unity-package.ps1:80-82`, `:143-147`
  builds it again for the Unity package `(source read)`.

Conclusion: CultLib already owns the MsQuic body for one platform, one role
(client), and one host (Unity). The cost of the target is extending that body
to a second role (provider), a second platform (Linux x64), and a second host
(Node), not creating a body from nothing.

### 1.3 How Node loads it

The decisive probe: from Node 24.15.0, with `koffi@3.3.0` installed into the
scratchpad, `koffi.load` loaded the committed `msquic.dll` then
`gamecult_mesh_quic_native.dll`, bound the five v1 exports by C prototype,
called `cultmesh_quic_open("127.0.0.1", 9, <64 zero hex>)` which returned `0`
with a non-null handle, observed `cultmesh_quic_state` move from `Connecting`
to `Failed` after 34 ms, read the bridge's structured error string through
`cultmesh_quic_error` ("shut down by the transport (status=0xFFFFFFFF800704D0,
error=0x1, msquic=F:\...\msquic.dll, events=event=1)"), got `-2` from
`cultmesh_quic_poll` on a failed client, and closed cleanly `(probe:
scratchpad/koffi-probe/probe.js)`. MsQuic ran its worker threads inside the
Node process and the poll model crossed the boundary without any callback
into JS.

What that proves and what it does not:

- Proves: the existing CultLib C ABI is directly loadable and drivable from
  the Node line in question with no addon build, no Node headers, no MSVC at
  install time, and no per-Node-version artifact. The `.async` call form
  exists on bound functions (`typeof state.async === "function"`), which is
  the mechanism for a blocking wait off the JS thread `(probe)`.
- Does not prove: a QUIC handshake succeeded (port 9 was intentionally dead);
  that the provider role works (the bridge has none); that Linux works (the
  bridge is Windows-only). Those are cut deliverables, not probe results.

koffi facts used by the design, from its shipped documentation, not from
memory: JS callbacks always run on the main thread and calls from other
threads are queued to it, with an explicit deadlock warning if the main thread
blocks on a secondary thread (`node_modules/koffi/doc/callbacks.md:166-176`);
asynchronous calls run on a worker pool sized to the core count with a default
cap of 256 queued calls (`doc/misc.md:26`, `:37`); prebuilt binaries ship for
Windows x64 and Linux/glibc x64 among others, Node >= 16, no compiler needed
(`doc/index.md:17-38`) `(source read)`. Package weight: 1,713,562 bytes
unpacked, 87 files, MIT `(probe: npm view)`. The design below uses no JS
callbacks at all; the only crossing is one blocking `next_event` wait per
runtime issued through `.async`, so the deadlock class in `callbacks.md:176`
cannot occur.

### 1.4 How it reaches Linux

- `libmsquic` is published by Microsoft for Debian 13 (trixie) at
  `packages.microsoft.com/debian/13/prod`, versions including `2.5.9`
  (`pool/main/libm/libmsquic/libmsquic_2.5.9_amd64.deb`, 2,906,420 bytes,
  SHA-256 `1baa61ade0b7b4a99f6dcb6b00d9aedb12b5566d00918a325be7425e878e51ba`,
  `Depends: libssl3t64, libnuma1, libxdp1, libnl-route-3-200`) `(probe)`.
  Yggdrasil is Debian 13 (`gamecult-ops/inventory.md:356`) `(source read)`.
  The same `2.5.9` pinned for Windows exists for the deploy host.
- The NuGet packages carry Windows binaries only: `Microsoft.Native.Quic.MsQuic.OpenSSL`
  2.5.9 contains `bin/{x64,x86,arm64}/msquic.dll`, `lib/*/msquic.lib`,
  and headers `msquic.h`, `msquicp.h`, `msquic_winuser.h`; no `.so`, no
  `msquic_posix.h` `(probe: nupkg listing)`. The Linux build needs
  `msquic.h`, `msquic_posix.h`, `quic_sal_stub.h` from the msquic source
  tree at tag `v2.5.9`; all three are fetchable at
  `raw.githubusercontent.com/microsoft/msquic/v2.5.9/src/inc/` (HTTP 200,
  79,280 / 17,909 / 4,731 bytes) `(probe)`. GitHub releases `v2.5.9` carry only
  `*_test.zip` bundles; `v2.5.11`, `v2.6.1` carry no assets `(probe)`, so the
  deb is the only distribution-grade Linux runtime for this version.
- Build host for Linux: Docker Desktop on Starfire runs a `linux/amd64` engine
  `(probe: docker version)`, so a `debian:13` container is a Linux build host
  for the development loop. The release artifact must come from a Linux host
  the deploy path trusts: Yggdrasil itself, or a CI runner. Q6.
- Windows toolchain: MSVC Build Tools 2022 present; CMake 4.3.2; also a MinGW
  gcc from StrawberryPerl on PATH `(probe)`. The map keeps MSVC for Windows
  because the committed Unity DLL is built with MSVC `/Brepro` for
  reproducibility (`CMakeLists.txt:17-19`) `(source read)`; mixing toolchains
  for one DLL is a liability with no capability behind it.

### 1.5 Price verdict

The binding does not dominate the campaign. The C body exists; the Node load
path is proven with one 1.7 MB MIT dependency and zero addon tooling; the
Linux runtime is a pinned distro package on the exact target OS; the Windows
build is already scripted. The real cost is the C++ rewrite from a
client-only, Windows-only, per-client poll bridge to a runtime-neutral,
two-role event bridge (section 8), estimated at roughly 1,000 lines of C++
replacing 440, plus a Linux build script and CI job. That is one cut, not the
campaign.

What would have changed this verdict: if the bridge had needed JS callbacks
from MsQuic worker threads (it does not; the wait/event model avoids them), or
if `libmsquic` had not been available for Debian 13 at the pinned version (it
is), or if the FFI load had failed on Node 24 (it did not).

## 2. Probe ledger

| # | Claim | Method | Evidence |
| --- | --- | --- | --- |
| P1 | Node 24.15.0 has no `node:quic` and no internal quic binding | probe | `require('node:quic')` -> `ERR_UNKNOWN_BUILTIN_MODULE` with and without `--experimental-quic`; `--expose-internals` -> `No such binding: quic`; `process.versions.ngtcp2 === undefined` |
| P2 | The committed CultLib MsQuic bridge loads and runs from Node 24 via koffi | probe | `scratchpad/koffi-probe/probe.js`: open rc 0, state 2 after 34 ms, structured error string, poll -2, clean close |
| P3 | koffi ships Windows x64 and Linux glibc x64 prebuilds, needs no compiler, Node >= 16 | source read | `koffi/doc/index.md:17-38` |
| P4 | koffi async calls run on a worker pool; JS callbacks only on the main thread | source read | `koffi/doc/misc.md:26,37`; `koffi/doc/callbacks.md:166-176` |
| P5 | koffi is 1.7 MB unpacked, MIT | probe | `npm view koffi dist.unpackedSize dist.fileCount`; `package.json:27` |
| P6 | MSVC Build Tools 2022 (14.44) and CMake 4.3.2 are present; the last bridge build used them | probe | `artifacts/quic-native-build/x64/Release/CMakeCache.txt`; `cmake --version` |
| P7 | `libmsquic 2.5.9` exists for Debian 13 amd64 with its SHA-256 and dependency set | probe | `packages.microsoft.com/debian/13/prod/dists/trixie/main/binary-amd64/Packages` |
| P8 | Yggdrasil is Debian 13 with Node 24.14.1 from NodeSource | source read | `gamecult-ops/inventory.md:356`, `:660` |
| P9 | MsQuic NuGet OpenSSL 2.5.9 carries no Linux binary or posix header | probe | nupkg listing |
| P10 | MsQuic POSIX headers are fetchable at tag v2.5.9 | probe | HTTP 200 for `msquic.h`, `msquic_posix.h`, `quic_sal_stub.h`, `msquic_winuser.h` |
| P11 | The pinned `msquic.h` has `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`, deferred/indicated certificate flags, `QUIC_SEND_FLAG_FIN`, `QUIC_STREAM_EVENT_SEND_COMPLETE`, API version 2 | source read | `artifacts/dependencies/msquic-schannel-2.5.9/package/build/native/include/msquic.h:123,132-133,142,246,394-398,1290,1530,1850` |
| P12 | Docker Desktop on Starfire runs a linux/amd64 engine | probe | `docker version --format '{{.Server.Os}}/{{.Server.Arch}}'` -> `linux/amd64` |
| P13 | `openssl` 3.5.5 CLI is available on Starfire (mingw) | probe | `which openssl` |
| P14 | npm names `cultmesh-ts`, `cultnet-ts`, `cultmesh-browser`, `@gamecult/cultmesh-ts`, `@gamecult/cultnet-ts`, `@gamecult/cultmesh-quic-native` are all unclaimed | probe | `npm view <name> version` returned nothing for each |
| P15 | Only `@gamecult/cultcache-ts` and the three Python packages have publish jobs | source read | `.github/workflows/publish-packages.yml:20-24,40-76` |
| P16 | Every external TS consumer uses `file:` paths; none uses a registry version | probe (Explore agent over `F:\Projects`) | section 3 |
| P17 | The StreamPixels deploy path extracts a CultLib tarball beside the app and runs `pnpm install --frozen-lockfile` and `pnpm build` on Yggdrasil | source read | `gamecult-ops/scripts/deploy-streampixels-preview.sh:21,95-103` |
| P18 | The Unity native connector refuses any endpoint without a 64-hex `cert-sha256` pin | source read | `CultMeshNativeQuicRealtimeTransport.cs:103-105` |
| P19 | C# `IsProtected` accepts `wss`, `https`, or any scheme containing `quic`; the browser verifier accepts only `wss` | source read | `CultMeshAuthorityProof.cs:156-162`; `cultmesh-browser/src/index.ts:965` |
| P20 | C# and browser canonical route transcripts are field-for-field identical | source read | `CultMeshAuthorityProof.cs:275-292` vs `cultmesh-browser/src/index.ts:979-996` |

## 3. Consumer audit

Method: every `package.json` under `F:\Projects` (excluding `node_modules`,
`dist`, `dist-test`, `.git`, `bin`, `obj`, `Library`, `Temp`, and CultLib
itself and its worktree copies) checked for the four package names, plus a
grep of every `*.ts/*.tsx/*.mts/*.cts/*.js/*.mjs/*.cjs` import or require of
them, with symbols read from the import lines. Vendored copies inside
`Heimdall/vendor/CultLib`, `VoidBot/vendor/cultcache-ts`, and
`gamecult-ops/.artifacts/*` were classified as library-internal, not
consumers.

Result:

| Package | Repos | Source files | Symbols actually imported (union) |
| --- | ---: | ---: | --- |
| `cultnet-ts` | 11 (StreamPixels, Heimdall, AetheriaEve, AetheriaEve-cultlib-admission, VoidBot, Stonks, Vili, weksa, Sai, EvePlugins, Bifrost) | 28 | `CultNetRudpSession`, `decodeRudpPacket`, `encodeRudpPacket`, `encodeCultNetMessageForWire`, `parseCultNetMessage`, `invokeCultNetOperation`, `startCultNetOperationServer`, `defineCultNetDocumentBinding`, `CultNetDocumentRegistry`, runtime-presence helpers, and message/packet types |
| `cultnet-ts/contracts` | 0 | 0 | one esbuild alias only (`AetheriaEve-cultlib-admission/scripts/verify-aetheria-browser-provider.mjs:48`); `cultmesh-browser` is its only importer and is inside CultLib |
| `cultmesh-ts` | 7 (StreamPixels, Heimdall, AetheriaEve and its worktree, Stonks, weksa, Odin and its worktree) | 10 | `CultMesh`, `cultMeshRectFromBounds`, `cultMeshViewportRequest`, and types (`CultMeshRudpEndpoint`, `CultMeshDocumentCatalog`, `CultMeshViewportRequest`, diagnostics types) |
| `@gamecult/cultcache-ts` | 1 (Heimdall) | 5 | `SingleFileMessagePackBackingStore`, `defineDocumentType`, `CultCacheEnvelope` |
| `cultcache-ts` (old unscoped name) | 8 | 16 | `CultCache`, `SingleFileMessagePackBackingStore`, `defineDocumentRegistry`, `defineDocumentType`, `/inspection` |
| `cultmesh-browser` | 1 (AetheriaEve and its worktree) | 1 | `CultMeshBrowserClient`, `CultMeshBrowserOdinRendezvous`, `decodeCultNetPayload` |

StreamPixels specifically: `apps/service/package.json:25-27` declares
`cultcache-ts`, `cultmesh-ts`, `cultnet-ts` as `file:../../../CultLib/packages/<pkg>`;
`apps/service/src/verse-state.ts:4-6` imports `defineDocumentType`,
`CultMesh`, `defineCultNetDocumentBinding`; `apps/service/src/idunn-rudp-health.ts:2-7`
imports `encodeCultNetMessageForWire`, `encodeRudpPacket`, and two types.
Nothing else in StreamPixels imports these packages. No TS/JS file anywhere
under `F:\Projects` references `cultmesh-state+quic` or `CultMeshRealtime`.

Sizing consequences:

- Nothing this migration adds has a consumer yet. The QUIC surface is new
  API; no existing export is reshaped, so no external repo changes are
  required by the CultLib cuts. StreamPixels adoption is consumer work after
  release (target, "Not in this migration").
- The `verifyAuthorityRoute` family in `cultmesh-browser` is module-private
  (not exported at `cultmesh-browser/src/index.ts:955`, `:979`, `:998`,
  `:1012`, `:1028`), so moving it (Q2) changes no external import. The three
  symbols AetheriaEve imports stay.
- `cultnet-ts/contracts` has no external consumer; adding a sibling subpath
  export for the shared verifier follows an existing, internally consumed
  pattern rather than inventing a new one.
- The `cultcache-ts` naming split (8 repos on the old unscoped name, Heimdall
  on `@gamecult/`) is pre-existing and not touched here, but it is the
  precedent Q5 has to reckon with.

## 4. Rejected options

- **Node built-in QUIC.** Absent in Node 24 (P1). Not a choice.
- **`@matrixai/quic` 2.0.9.** A Rust/quiche-based QUIC stack with its own
  stream, certificate and event semantics `(probe: npm view description)`.
  Rejected because the target requires the bytes on the wire, the ALPN, the
  stream-kind byte, the deferred certificate pin decision, and the close/abort
  codes to match `GameCult.Mesh.Quic` and the Unity MsQuic connector; two QUIC
  stacks would have to be proven equivalent at every one of those points, and
  CultLib would own neither. Semantically wrong for this campaign, not merely
  unfashionable.
- **`@fails-components/webtransport` 1.6.8.** libquiche behind a WebTransport
  (HTTP/3) session model `(probe)`. Wrong layer: the realtime plane is raw
  QUIC with ALPN `cultmesh-state-v1`, not HTTP/3.
- **`node-quic` 0.1.3.** A wrapper around an abandoned pure-JS QUIC `(probe)`.
  Not TLS 1.3 MsQuic; rejected on the same grounds.
- **A Node-API addon (node-addon-api + node-gyp/cmake-js) wrapping MsQuic
  directly.** Viable, but it creates a second C++ owner of MsQuic callback
  semantics beside the Unity bridge, requires Node headers and MSVC on every
  Windows build host and a Node-ABI artifact per platform, and buys nothing the
  C ABI plus FFI does not (P2). Rejected as duplicate authority. If koffi were
  ever removed, the cheapest replacement is a thin N-API shim over the same C
  ABI, not a second MsQuic body.
- **A separate .NET provider process or a WebSocket shortcut.** Already ruled
  out by the operator (target, line 15-17).
- **JS callbacks from MsQuic worker threads.** koffi supports them by queuing
  to the main thread (P4), but it turns every MsQuic event into a
  cross-thread hop and opens the documented deadlock class. Rejected in favour
  of the event-queue/wait model the existing bridge already uses.
- **Building the Windows DLL with the MinGW gcc on PATH.** Present (P6) but the
  committed Unity artifact is MSVC `/Brepro`; two toolchains for one DLL is a
  reproducibility liability. Rejected.
- **Building the Linux artifact on Starfire and shipping it.** Cross-building
  is explicitly forbidden by the target; a Docker `debian:13` container on
  Starfire is a Linux host and is fine for the development loop, but the
  release artifact comes from the builder the operator names in Q6.
- **Keeping `cert-sha256` verification in C.** The v2 ABI hands the DER
  certificate to the host and waits for the decision, which is the "caller
  hook" the target demands and removes the Windows-only
  `CryptHashCertificate2` from the portable path. The v1 entrypoint keeps its
  in-C pin check only as the Unity compatibility shim (section 8).

## 5. Authority maps

### 5.1 Native MsQuic bridge (`native/GameCult.Mesh.Quic.Native`)

- Owner: `cultmesh_quic_native.cpp` owns MsQuic registration, configuration,
  listener, connection and stream handles, TLS credential loading, the
  1-byte-kind + 4-byte-LE-length stream framing, and a single event queue per
  runtime.
- Inputs: host/port, PKCS12 bytes and password for providers, the host's
  certificate decision for clients, encoded frame bytes to send.
- Outputs: events (`listener_new_connection`, `connection_connected`,
  `connection_certificate_received` with DER payload, `connection_shutdown`
  with code/status/text, `stream_started` with kind, `stream_frame` with the
  whole encoded frame, `stream_send_complete`, `stream_shutdown`); the bound
  listener port; structured error text.
- Derived state: none that the host reads as truth. Connection counts, remote
  addresses and MsQuic status codes are diagnostics.
- Forbidden writers: the bridge never decodes a CultMesh frame, never decides
  delivery semantics, never coalesces, never accepts a certificate on its own
  in the v2 path, never reconnects. The v1 shim is the only place a pin is
  compared in C, for the Unity contract only.
- Shared paths: the Unity managed connector (v1 exports) and the Node binding
  (v2 exports) use the same library, the same MsQuic runtime, the same
  framing code.
- Deletion line: the per-client `Client` struct with its own registration
  (`:51-63`, `:316-393`), Windows-only includes and export macros (`:1-3`,
  `:18-22`), `CertificateMatchesPin` as the sole trust path (`:184-200`).

### 5.2 TypeScript QUIC realtime plane (`packages/cultmesh-ts`)

- Owner: `src/realtime-quic.ts` owns provider and consumer semantics: which
  stream kind carries which delivery mode, the per-peer keyed coalescing
  outbox for `LatestOnly`, the single ordered stream for `ReliableOrdered`,
  `Unreliable` failing closed, the generation filter on receive, and the
  certificate pin decision on the consumer.
- Inputs: bridge events; `CultMeshRealtimeFrame` objects from application
  code; an advertised `CultMeshTransportCandidate`-shaped route from the
  session manager; the consumer's trust policy.
- Outputs: encoded frames to the bridge; decoded frames to the application;
  the advertised endpoint string for a provider (with `cert-sha256`).
- Derived state: connection health, connected peer count, stream ids.
- Forbidden writers: application code never opens a stream, picks an
  endpoint, or writes a reconnect loop (target). `src/realtime-wire.ts` never
  touches a socket. `src/realtime-quic-native.ts` (the koffi binding) never
  interprets frame bytes.
- Shared paths: provider broadcast and consumer send both go through
  `sendFrame` on the same transport object; the coalescing outbox is the only
  path for `LatestOnly` on a provider, whether the caller is a test, a tick, or
  a reconnect.
- Deletion line: none inside `cultmesh-ts` (there is no prior QUIC code); the
  RUDP provider transport at `src/provider-rudp-transport.ts` is untouched.

### 5.3 Shared TypeScript route verification (Q2)

- Owner after the cut: `packages/cultnet-ts/src/cultmesh-authority.ts`,
  published as subpath `cultnet-ts/authority`. It is the only TypeScript
  implementation of: Odin root lookup, validity window, canonical route
  transcript, canonical session transcript, P-256/P1363 verification,
  loopback detection, and the protected-scheme rule.
- Inputs: a route view (Verse, authority runtime, endpoint, protocol ids,
  priority, generation, certificate), a trust policy (`mode`, `odinRoots`,
  `now`), a session-open request and an accepted message.
- Outputs: resolution or a thrown `Error` whose message is the C# reference's
  for the same refusal. *(First written as "the same message texts the
  browser package emits today (its tests pin them)"; Soul found the browser
  tests pin two messages only, and the channel-protection text was false for
  QUIC. Corrected 2026-09-16.)*
- Derived state: none.
- Forbidden writers: `cultmesh-browser` must not retain or regrow
  `verifyAuthorityRoute`, `canonicalRoute`, `canonicalSession`,
  `canonicalFields`, `verifyP256`, or `isLoopbackEndpoint`; `cultmesh-ts` must
  not implement any of them; neither transport package may depend on the
  other. Negative greps in section 6 pin this.
- Shared paths: the browser WebSocket client and the Node QUIC consumer verify
  the same route bytes through the same function.
- Deletion line: `cultmesh-browser/src/index.ts:955-1072` and `:797-808`.
- Behavioural note, not a silent change: the protected-scheme rule becomes the
  C# rule (`wss`, `https`, scheme containing `quic`;
  `CultMeshAuthorityProof.cs:156-162`) instead of `wss`-only
  (`cultmesh-browser/src/index.ts:965`). The browser client still refuses
  non-`ws(s)` route endpoints at `:537-540`, so browser behaviour is
  unchanged; the shared module simply stops being wrong for QUIC routes.

### 5.4 Build and packaging

- Owner: `scripts/build-quic-native.ps1` (Windows x64) and the new
  `scripts/build-quic-native.sh` (Linux x64) own producing the bridge
  artifact; the new `.github/workflows/quic-native.yml` owns producing the
  release-grade artifact for both platforms (Q6 A) and attaching it to a
  GitHub Release; the committed tree `packages/cultmesh-ts/native/` owns
  delivery to consumers (Q5 C): whatever carries the package directory (a
  `git archive`, an `npm pack` tarball, a pnpm `file:` copy) carries the
  binaries with it, and `packages/cultmesh-ts/native/MANIFEST.txt` plus
  `SHA256SUMS` own provenance (MsQuic version, build host, digest per file).
- Inputs: pinned MsQuic 2.5.9 (Schannel NuGet on Windows; `libmsquic` deb on
  Linux) with SHA-256 checks; the bridge source; the pinned `debian:13` image
  digest for the Linux builder.
- Outputs: `gamecult_mesh_quic_native.dll` + `msquic.dll` (win32-x64),
  `libgamecult_mesh_quic_native.so` + `libmsquic.so.2` (linux-x64), the MsQuic
  license, and the two provenance files.
- Derived state: `artifacts/` (git-ignored, `.gitignore:66`) is the
  development-loop build output and never delivery; the Unity plugin copies at
  `unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/` remain the Unity
  package's own body (section 8, operator-only rebuild), not a source for
  Node.
- Forbidden writers: no `postinstall` compile or download step in any
  package; a consumer host never needs CMake, a compiler, or registry access
  for the binding; no workflow commits binaries to `main` (the operator
  commits from a Release asset whose digest the manifest records, Q8);
  `realtime-quic-native.ts` never searches `node_modules` for a platform
  package.
- Deletion line: the platform packages
  `packages/cultmesh-quic-native/{win32-x64,linux-x64}` and
  `optionalDependencies` from the ruling-A map are never created; the Windows
  script is kept and the Unity package script keeps calling it.
- Parked seam (registry publication, later pass): the same `quic-native.yml`
  artifacts are the payload a publish job would wrap; the reattachment point
  is a `cultmesh-ts` job shaped like `publish-packages.yml:39-78` that depends
  on the two build jobs, plus `optionalDependencies` in
  `packages/cultmesh-ts/package.json` and a `node_modules` branch in the
  resolver. None of that exists under C.

## 6. Cut 1 (subtraction): one TypeScript route verifier

Purpose: land Q2 before any QUIC code exists, so the Node consumer has one
owner to call and the browser package has nothing left to grow.

Deletes (exact):

- `packages/cultmesh-browser/src/index.ts:955-977` `verifyAuthorityRoute`
  (23 lines), `:979-996` `canonicalRoute` (18), `:998-1010` `canonicalSession`
  (13), `:1012-1026` `canonicalFields` (15), `:1028-1058` `verifyP256` (31),
  `:1065-1072` `isLoopbackEndpoint` (8), `:797-803` `bytesToBase64` (7),
  `:805-808` `base64ToBytes` (4). Total 119 lines deleted from the browser
  package. `randomNonce` (`:1060-1063`) stays: it is browser-client behaviour,
  not verification.

Keeps and moves:

- The deleted bodies move verbatim into
  `packages/cultnet-ts/src/cultmesh-authority.ts` as exported functions, with
  one edit: the protected-scheme test in `verifyAuthorityRoute` becomes
  `isProtectedEndpoint(endpoint)` implementing the C# rule (P19). Types
  `CultMeshBrowserIdentity`, `CultMeshBrowserRoute`,
  `CultMeshBrowserP256PublicKey`, `CultMeshBrowserRouteCertificate`,
  `CultMeshBrowserAuthorityTrustMode`, `CultMeshBrowserAuthorityTrustPolicy`
  (`cultmesh-browser/src/index.ts:19-53`) move too, renamed without the
  `Browser` infix (`CultMeshAuthorityIdentity`, `CultMeshAuthorityRouteView`,
  `CultMeshP256PublicKey`, `CultMeshAuthorityRouteCertificate`,
  `CultMeshAuthorityTrustMode`, `CultMeshAuthorityTrustPolicy`).
  `cultmesh-browser` re-exports them under the old names as type aliases.
- `validateSessionAcceptance` (`:926-953`) stays in the browser package; it
  compares WebSocket handshake fields. Its crypto lines (`:944-951`) are
  replaced by a call to the new `verifyProviderSessionProof(request, endpoint,
  providerKey, signature)` exported from the shared module.

Adds:

- `packages/cultnet-ts/src/cultmesh-authority.ts` (about 190 lines: the moved
  119 plus types and `isProtectedEndpoint`). Browser-safe by construction: it
  may use only `globalThis.crypto.subtle`, `TextEncoder`, `atob`, `btoa`,
  `URL`, `DataView`, `Uint8Array`. No `node:` import.
- `packages/cultnet-ts/package.json:7-18` gains an `"./authority"` export
  beside `"./contracts"` (`:12-16`), same three-key shape.
- `packages/cultnet-ts/src/index.ts` re-exports the module's public names.
- `packages/cultnet-ts/test/cultmesh-authority.test.ts` (about 150 lines):
  signs a route with `node:crypto` `generateKeyPairSync("ec", { namedCurve:
  "P-256" })` and verifies through the shared module; one negative per rule
  (expired, unsigned remote, wrong Odin root, mutated endpoint, wrong signature
  length); and asserts `isProtectedEndpoint` accepts `wss://`, `https://`,
  `cultmesh-state+quic://` and rejects `ws://`, `cultnet+tcp://`.
- A cross-runtime vector: `tests/GameCult.Mesh.Tests/CultMeshAuthorityProofTests.cs`
  gains a test that calls `CultMeshAuthorityProof.CreateSignedRoute`
  (`src/GameCult.Mesh/CultMeshAuthorityProof.cs:221`) with a fixed test key
  and writes `contracts/cultmesh/authority-route-vectors.json` (route fields,
  Odin public key, signature) when `CULTMESH_WRITE_VECTORS=1`, and otherwise
  asserts the committed file still verifies. The TS test verifies the same
  file through the shared module. This is a shared-vector check, not
  cross-process interop; it proves the transcript bytes, which is what Q2
  needs.

Per-file changes:

- `packages/cultmesh-browser/src/index.ts:1-17` add
  `import { verifyAuthorityRoute, verifyProviderSessionProof, isLoopbackEndpoint, type ... } from "cultnet-ts/authority";`
  (the package already imports `cultnet-ts/contracts` at `:2-17`, so
  resolution is proven). `:536` and `:914` call the imported function
  unchanged. `:944-951` call `verifyProviderSessionProof`. `:961` and `:965`
  are gone with the function.
- `packages/cultmesh-browser/tsconfig.json` unchanged (`moduleResolution:
  "Bundler"` already resolves the `exports` map).

Verification:

- `npm run test --workspace packages/cultnet-ts` (build + unit; pins the
  shared verifier and the vector file).
- `npm run test --workspace packages/cultmesh-browser` (all 17 existing tests
  at `test/cultmesh-browser.test.ts:29-472` must pass unchanged; they pin the
  error strings and the refusal order, including "rejects a mutated or expired
  signed route before opening a provider socket" at `:239-258`).
- `dotnet test tests/GameCult.Mesh.Tests --filter FullyQualifiedName~AuthorityProof`
  (vector generation/verification; focused, one project).
- `node scripts/test-typescript-package-closure.mjs` (the packed
  `cultmesh-browser` tarball must resolve `cultnet-ts/authority` from the
  registry-shaped install at `scripts/test-typescript-package-closure.mjs:63-72`).
- Negative greps (must return nothing):
  `rg -n "crypto\.subtle\.(verify|importKey)|function (verifyAuthorityRoute|canonicalRoute|canonicalSession|canonicalFields|verifyP256|isLoopbackEndpoint|isProtectedEndpoint|isUnsignedCertificate)" packages/cultmesh-browser/src packages/cultmesh-ts/src`
  and `rg -n "cultmesh-browser" packages/cultmesh-ts/package.json packages/cultnet-ts/package.json`.
  *(The name list grew by two after the fix batch: `isProtectedEndpoint`,
  written fresh as the C# rule, and `isUnsignedCertificate`, the shared
  reading of "unsigned" the browser's session check now uses. Soul noted
  the grep pins names, not structure; a shim under another name passes it.)*
- Operator-only: none.

## 7. Cut 2 (behaviour, pure TypeScript): the realtime frame codec

Purpose: byte parity with `CultMeshRealtimeWireProtocol` before any transport
exists, proven in both directions with shared vectors.

Deletes: none.

Adds:

- `packages/cultmesh-ts/src/realtime-wire.ts` (about 130 lines). Exports
  `CULTMESH_REALTIME_ALPN = "cultmesh-state-v1"`,
  `CULTMESH_REALTIME_CONNECTION_CLOSE_CODE = 0x43554c54n`,
  `CULTMESH_REALTIME_STREAM_ABORT_CODE = 0x53544154n`,
  `CULTMESH_REALTIME_RELIABLE_STREAM = 1`, `CULTMESH_REALTIME_LATEST_ONLY_STREAM = 2`,
  `CULTMESH_REALTIME_MAX_PAYLOAD_BYTES = 64 * 1024 * 1024`,
  `CULTMESH_REALTIME_MAX_ENCODED_FRAME_BYTES` (= max payload + 37 + 3 * 65535,
  matching `CultMeshRealtimeWireProtocol.cs:20`), type
  `CultMeshRealtimeDelivery = "reliable-ordered" | "latest-only" | "unreliable"`
  with wire bytes 0/1/2 in that order (`CultMeshRealtimeTransports.cs:8-13`),
  interface `CultMeshRealtimeFrame { channelId; schemaId; bodyId;
  producerEpoch: bigint; sequence: bigint; delivery; payload: Uint8Array }`,
  `encodeRealtimeFrame(frame): Uint8Array`, `decodeRealtimeFrame(bytes):
  CultMeshRealtimeFrame`. Layout exactly `CultMeshRealtimeWireProtocol.cs:37-54`
  (magic `0x31545343` LE u32, delivery u8, epoch i64 LE, sequence i64 LE,
  three u16 LE lengths, payload i32 LE, header size i32 LE = 37, version u16
  LE = 1, then UTF-8 identities, then payload). Decode rejections match
  `:60-76` one for one: truncated header, wrong magic, unsupported version or
  header size, delivery out of range, negative payload length, length sum
  mismatch. Epoch and sequence are `bigint` because i64 exceeds the safe
  integer range; validation mirrors `CultMeshRealtimeTransports.cs:29-39`
  (non-empty identities, non-negative epoch/sequence).
- `contracts/cultmesh/realtime-frame-vectors.json`: written by a new C# test
  `tests/GameCult.Mesh.Tests/CultMeshRealtimeWireProtocolTests.cs` under
  `CULTMESH_WRITE_VECTORS=1` and otherwise asserted against. Cases: one frame
  per delivery mode with small payload; empty payload; identities at exactly
  65,535 UTF-8 bytes (multi-byte characters included so byte length and
  character length differ); epoch and sequence at `long.MaxValue`; and a
  64 MiB-payload case recorded as `{ sha256OfEncoding, encodedLength }` rather
  than bytes. Malformed cases: wrong magic, version 2, header size 36,
  delivery 3, payload length -1, length sum off by one, each with the C#
  exception message. There is no existing C# test for this codec
  (`rg CultMeshRealtimeWireProtocol tests` returns nothing `(probe)`), so this
  is also the first direct C# codec test.
- `packages/cultmesh-ts/test/realtime-wire.test.ts` (about 120 lines): every
  vector decodes to the recorded fields and re-encodes to the recorded bytes;
  every malformed vector throws; the 64 MiB case is built in-process and its
  SHA-256 compared; TypeScript-produced bytes for the same fields equal the
  C#-produced bytes (this direction is what "TypeScript encodings decode in
  C#" means when the vector file is the medium; the cross-process direction is
  cut 5).
- `packages/cultmesh-ts/src/index.ts:37-40` gains `export * from "./realtime-wire";`.

Verification:

- `dotnet test tests/GameCult.Mesh.Tests --filter FullyQualifiedName~RealtimeWireProtocol`.
- `npm run test --workspace packages/cultmesh-ts`.
- Negative grep: `rg -n "0x31545343|cultmesh-state-v1" packages --glob '!**/dist*/**'`
  must hit only `realtime-wire.ts` and its test in TypeScript (one codec).
- Operator-only: none.

## 8. Cut 3 (native, subtraction then behaviour): the runtime-neutral bridge

Purpose: one MsQuic body for Unity and Node, two roles, two platforms, no
callbacks across the host boundary.

Deletes first (in `native/GameCult.Mesh.Quic.Native/cultmesh_quic_native.cpp`):

- `:1-3` Windows includes; `:18-22` `__declspec` macros; `:51-63` per-client
  `Client` with its own `api/registration/configuration`; `:184-200`
  `CertificateMatchesPin` as the trust path; `:288-305` `CloseHandles`;
  `:309-393` `cultmesh_quic_open` as the primary API. About 200 of 440 lines
  are removed or rewritten; the framing consumer (`:123-157`), stream callback
  (`:159-182`), trace/error helpers (`:65-86`, `:202-221`) are kept in spirit
  and re-homed on the runtime.
- `CMakeLists.txt:4-6` (Windows-only fatal) and `:7-9` (`MSQUIC_ROOT`
  mandatory) are replaced by a platform branch.

Adds (C ABI v2, all `extern "C"`, exported with a portable macro
`CULTMESH_API` that is `__declspec(dllexport)` on MSVC and
`__attribute__((visibility("default")))` elsewhere):

```
int32_t cultmesh_quic_runtime_open(const char* app_name, void** out_runtime);
void    cultmesh_quic_runtime_close(void* runtime);
int32_t cultmesh_quic_listener_open(void* runtime, const char* host, uint16_t port,
            const uint8_t* pkcs12, int32_t pkcs12_len, const char* password,
            uint64_t* out_listener_id, uint16_t* out_bound_port);
void    cultmesh_quic_listener_close(void* runtime, uint64_t listener_id);
int32_t cultmesh_quic_connection_open(void* runtime, const char* host, uint16_t port,
            uint64_t* out_connection_id);
int32_t cultmesh_quic_connection_certificate_complete(void* runtime, uint64_t connection_id,
            int32_t accept);
void    cultmesh_quic_connection_shutdown(void* runtime, uint64_t connection_id, uint64_t code);
int32_t cultmesh_quic_stream_open(void* runtime, uint64_t connection_id, uint8_t kind,
            uint64_t* out_stream_id);
int32_t cultmesh_quic_stream_send_frame(void* runtime, uint64_t stream_id,
            const uint8_t* encoded_frame, int32_t length, int32_t fin);
void    cultmesh_quic_stream_shutdown(void* runtime, uint64_t stream_id, uint64_t code);
int32_t cultmesh_quic_next_event(void* runtime, int32_t timeout_ms,
            cultmesh_quic_event* out_event, uint8_t* payload, int32_t payload_capacity,
            int32_t* out_required);
int32_t cultmesh_quic_last_error(void* runtime, char* destination, int32_t capacity);
```

- `cultmesh_quic_event` is a fixed 64-byte struct: `uint32 type; uint32
  stream_kind; uint64 listener_id; uint64 connection_id; uint64 stream_id;
  uint64 code; int32 status; int32 payload_length; uint8 reserved[16]`.
  Types: 1 `listener_new_connection`, 2 `connection_connected`, 3
  `connection_certificate_received` (payload = DER), 4 `connection_shutdown`
  (payload = UTF-8 reason), 5 `stream_started` (inbound, `stream_kind` set
  from the first byte), 6 `stream_frame` (payload = one whole encoded frame,
  length prefix stripped), 7 `stream_send_complete`, 8 `stream_shutdown`, 9
  `listener_stopped`. `next_event` returns 0 on timeout, 1 with the event
  filled and payload copied, 2 when `payload_capacity` is too small (event
  stays at the head; `out_required` set), negative on error. This is the v1
  two-phase poll convention (`:400-418`) applied to a queue of typed events.
- Client connections use `QUIC_CREDENTIAL_TYPE_NONE` with
  `INDICATE_CERTIFICATE_RECEIVED | DEFER_CERTIFICATE_VALIDATION |
  USE_PORTABLE_CERTIFICATES` (already at `:358-364`), return
  `QUIC_STATUS_PENDING` from the certificate event, enqueue event 3, and call
  `ConnectionCertificateValidationComplete` only when the host calls
  `certificate_complete` (P11: `msquic.h:132-133,142,1290`). Timeout: the
  bridge sets a 10 s handshake idle; if the host never answers, MsQuic's
  handshake timeout closes the connection and event 4 is emitted.
- Listeners load `QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12`
  (`msquic.h:123,394-398`), one credential type for Schannel and OpenSSL; the
  provider's advertised pin is the SHA-256 of the DER certificate computed on
  the TypeScript side from the same PKCS12 with `node:crypto` (no hashing in
  C).
- Settings mirror the C# server/connector: `PeerUnidiStreamCount 1024`
  (`CultMeshQuicRealtimeTransport.cs:77`, `:202`), idle timeout 30 s and
  keep-alive 5 s as in the existing bridge (`:335-338`).
- `stream_send_frame` copies the bytes, prepends the 4-byte LE length, calls
  `StreamSend` (with `QUIC_SEND_FLAG_FIN` when `fin`), and emits event 7 on
  `QUIC_STREAM_EVENT_SEND_COMPLETE`. `stream_open` writes the kind byte as the
  first send. Framing therefore lives in C on both directions, exactly where
  the v1 bridge already keeps it (`:123-157`), and `CultMeshQuicRealtimeProtocol`
  keeps it in C# (`CultMeshQuicRealtimeTransport.cs:635-655`).
- The v1 exports `cultmesh_quic_open/state/poll/error/close` are kept, Windows
  only (`#ifdef _WIN32`), implemented on top of a private runtime with the pin
  check retained from `:184-200`. External contract protected: the Unity
  package `DllImport` at `CultMeshNativeQuicRealtimeTransport.cs:259-277` and
  `CanConnect`'s Windows gate at `:39-41`. They delegate; they decide nothing
  new.
- `CMakeLists.txt`: Windows branch unchanged in effect (MSVC, `/Brepro`,
  `MSQUIC_ROOT` from the NuGet layout, links `msquic crypt32 bcrypt`); Linux
  branch links `msquic` from `MSQUIC_LIB_DIR` (the extracted deb's
  `usr/lib/x86_64-linux-gnu`) with headers from `MSQUIC_INCLUDE_DIR`, `-fvisibility=hidden`,
  output `libgamecult_mesh_quic_native.so`, `RPATH $ORIGIN` so the sibling
  `libmsquic.so.2` resolves.
- `scripts/build-quic-native.sh` (about 70 lines): fetches
  `libmsquic_2.5.9_amd64.deb` from the packages.microsoft.com pool URL in P7,
  verifies its SHA-256, extracts with `dpkg-deb -x`, fetches the three headers
  at tag `v2.5.9` (P10) and verifies their SHA-256 (record the hashes at first
  run into the script, the same way `build-quic-native.ps1:14` pins the
  NuGet), configures CMake with Ninja, and copies `libgamecult_mesh_quic_native.so`,
  `libmsquic.so.2`, and the MsQuic LICENSE into `artifacts/quic-native/linux-x64`.
- `native/GameCult.Mesh.Quic.Native/README.md` rewritten to describe both
  roles, both platforms, the v1/v2 split, and the "no host callbacks" rule.

Per-file changes outside `native/`:

- `scripts/build-quic-native.ps1:54-58` output path becomes
  `artifacts/quic-native/win32-x64` by default (parameter default at `:4`);
  callers `build-unity-package.ps1:80-82` and the Native.Tests csproj `:20-22`
  pass explicit `-OutputDirectory` today, so they are unaffected.
- No change to `src/GameCult.Mesh.Quic.Native/*.cs`. The Unity managed
  connector keeps polling v1.

Verification:

- Windows: `powershell -File scripts/build-quic-native.ps1` then
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests` (the csproj rebuilds the
  bridge itself at `:20-22`; all four tests at
  `CultMeshNativeQuicRealtimeTransportTests.cs:15-141` must pass, including the
  wrong-pin refusal at `:79-99` and the departed-client test at `:101-141`).
  This is the regression gate for the v1 shim.
- Windows FFI smoke, before any TypeScript transport exists:
  `scratchpad`-style script (to be committed as
  `packages/cultmesh-ts/test/realtime-quic-native.test.ts` in cut 4) that
  opens a runtime, a listener with a PKCS12 generated by `openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 ... | openssl pkcs12 -export`
  (P13), a client connection to it, answers event 3 with accept, sees event 2
  on both sides, opens a latest-only stream, sends one frame with `fin`, and
  reads event 6 on the server side with the identical bytes.
- Linux, development loop: build the committed image
  `scripts/quic-native-linux-dev.Dockerfile`, pinned to the same `debian:13`
  digest as the release workflow, and follow the recipe in the bridge's own
  README. The image carries the compiler, Ninja, Node, `setarch` and the
  library's runtime dependencies, and the sanitizer configurations need the
  container's default seccomp profile relaxed so randomisation can be
  disabled. The README and the Dockerfile header own that recipe; this map
  does not restate it. This is a Linux build on a Linux host; it is not the
  release artifact (Q6). *(The earlier one-line `docker run` recipe here
  installed no JavaScript runtime and could not run the mutation runner at
  all, and said nothing about randomisation or seccomp, which is how the
  Linux path came to be red for everyone but the agent that had found the
  flags by hand.)*
- Build economy: the only builds are the native CMake target and
  `tests/GameCult.Mesh.Quic.Native.Tests` (which pulls `GameCult.Mesh.Quic`,
  `GameCult.Mesh.Quic.Native`, `GameCult.Mesh`, `GameCult.Networking`,
  `GameCult.Caching` by project reference). Before the first `dotnet build`,
  take `Get-ChildItem -Recurse F:\Projects\CultLib\bin,F:\Projects\CultLib\obj,F:\Projects\CultLib\artifacts | Select FullName,Length`
  to a scratch file and diff afterwards; restore or delete anything outside
  those five projects' outputs.
- Negative greps: `rg -n "CryptHashCertificate2|wincrypt" native/` must hit
  only inside the `#ifdef _WIN32` v1 block; `rg -n "windows.h" native/` only
  inside `#ifdef _WIN32`.
- Operator-only: rebuilding and re-committing the Unity plugin DLL
  (`unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/gamecult_mesh_quic_native.dll`)
  and cutting a Unity package version through `scripts/build-unity-package.ps1`
  is a release action; the map does not schedule it. Until it happens, Unity
  keeps the old DLL, which is fine because v1 exports are unchanged.

## 9. Cut 4 (behaviour): Node binding, consumer, session manager, trust

Purpose: a TypeScript consumer that connects by advertised candidate, verifies
the Odin route through the cut-1 module, pins the TLS certificate, and
receives frames from the C# managed provider in a separate process.

Deletes: none.

Adds:

- `packages/cultmesh-ts/src/realtime-quic-native.ts` (about 260 lines): the
  koffi binding. Resolves the binary directory as `CULTMESH_QUIC_NATIVE_DIR`
  when set (the development loop and CI point it at
  `artifacts/quic-native/<platform>` straight from a build), otherwise
  `path.join(__dirname, "..", "native", `${process.platform}-${process.arch}`)`,
  the committed tree cut 6 lands (Q5 C; `__dirname` is `dist/` at runtime, so
  the path holds in the repo, in an `npm pack` tarball, and in a pnpm `file:`
  copy alike). No `node_modules` lookup, no platform package, no registry.
  When neither directory holds `msquic` and the bridge, throw one error naming
  both paths tried and the env var; until cut 6 lands, that is the error every
  caller outside the test suite gets. Loads `msquic` first, then the bridge, by absolute path
  (the probe showed the dependent library must be resolvable before the
  bridge loads). Declares the twelve v2 prototypes. Exposes
  `CultMeshQuicNativeRuntime` with a single event pump: a loop that awaits
  `nextEvent.async(runtime, 250, ...)` and dispatches typed events to
  registered listeners/connections by id. One pump per process-wide runtime;
  reference-counted open/close. No koffi callbacks anywhere.
- `packages/cultmesh-ts/src/realtime-quic.ts` (about 650 lines across cuts 4
  and 5). Cut 4 lands: `CultMeshQuicRealtimeTransport` (implements
  `sendFrame`, `receiveFrame`, `dispose`, `transportId = "msquic-realtime"`,
  `endpoint`; the `isVerifiedFor(verseId, authorityRuntimeId, protocolId,
  routeGeneration)` check mirroring `CultMeshQuicRealtimeTransport.cs:352-361`),
  `CultMeshQuicRealtimeConnector` (`connectorId`, `priority`, `canConnect(candidate)`,
  `connect(candidate, target, signal)`), and the receive path: stream kind
  vs frame delivery consistency (`:519-524`), and the per-`(channel, body)`
  generation filter that drops older `(producerEpoch, sequence)` (`:538-560`).
  Certificate decision on event 3: caller `validateProviderCertificate(target,
  der)` if supplied, else SHA-256 of DER equals the endpoint's `cert-sha256`
  (`:111-119`, case-insensitive hex); otherwise reject. Endpoint parsing:
  scheme `cultmesh-state+quic`, host, port, optional pin (`:98-109`).
- `CultMeshQuicRealtimeSessionManager` in the same file: takes an
  `ICultMeshRealtimeLookupSource`-shaped port `{ resolve(target): Promise<CultMeshVerseDescriptorMessage[]> }`,
  a trust policy from `cultnet-ts/authority`, and connectors. Binds
  candidates from `authorityRoutes` whose `protocolIds` include
  `cultmesh.realtime_state.v1` (`CultMeshSessions.cs:77`) and whose
  `authorityRuntimeId` matches; verifies each with `verifyAuthorityRoute`
  before any connector is asked; orders by connector priority then route
  priority; races at most `maxRacedCandidates` per tier (`:833-880`); requires
  `isVerifiedFor` after connect (`:885-896`); keys sessions by
  `verseId + "\u001f" + authorityRuntimeId` (`:46`); marks offline on transport
  failure. Ships with `CultMeshStaticRealtimeLookupSource` (an array of
  `CultMeshVerseDescriptorMessage` from `cultnet-ts/contracts:350-369`). No
  Odin WebSocket rendezvous in Node in this migration (Q7).
- `CultMesh` facade (`packages/cultmesh-ts/src/index.ts:4700`) gains statics
  beside the RUDP ones at `:6008-6186`: `parseQuicRealtimeEndpoint`,
  `createQuicRealtimeConnector(options)`, `createQuicRealtimeSessionManager(options)`,
  and in cut 5 `createQuicRealtimeProvider(options)`.
- `packages/cultmesh-ts/package.json:44-48` gains `"koffi": "3.3.0"` (exact).
  No `optionalDependencies`: under Q5 C the binaries are files of this
  package, not packages of their own. `koffi` is required lazily inside
  `realtime-quic-native.ts` so consumers that never touch QUIC (AetheriaEve
  Electron, Odin, Stonks, weksa) load nothing native. `koffi` itself is a
  registry dependency like `@msgpack/msgpack` (`:45`); every consumer host
  already installs from the registry (`deploy-streampixels-preview.sh:102`),
  so this adds no new kind of fetch.
- `packages/cultmesh-ts/test/realtime-quic-native.test.ts` (about 150 lines):
  the cut-3 FFI smoke, now against the runtime class.
- `packages/cultmesh-ts/test/realtime-quic-consumer.test.ts` (about 250
  lines): TypeScript consumer against a TypeScript listener opened through
  the raw runtime (no provider semantics yet): connect with correct pin;
  refuse wrong pin (`cert-sha256` of 64 zeros, mirroring
  `CultMeshNativeQuicRealtimeTransportTests.cs:79-99`); refuse missing
  certificate when no validator; caller validator observed with the target
  identity; generation filter drops a late older generation (mirrors
  `CultMeshQuicRealtimeTransportTests.cs:111-140`).
- Trust negatives, each a separate test with its own mutation, in the same
  file, through the session manager with a static lookup source and a signed
  route built with `node:crypto`: (a) expired certificate (`now` past
  `expiresAt`); (b) unsigned remote route (certificate absent, mode
  `authenticated-remote`); (c) wrong Odin root (trust has a different
  `keyId`); (d) `cert-sha256` pin mismatch (route verifies, TLS pin fails, so
  the connector rejects before `isVerifiedFor`); (e) replayed nonce. On the
  QUIC plane there is no `session_open` nonce exchange; the route's
  freshness is its generation plus the TLS handshake. The replay test
  therefore mutates `generation` on the connected transport's target and
  asserts `isVerifiedFor` fails and the session manager disposes the
  transport (`CultMeshSessions.cs:885-896`). If the operator wants a nonce on
  QUIC, that is a protocol addition on both runtimes and belongs to a later
  pipeline; this map does not invent it.
- Interop lane, C# managed provider to TypeScript consumer, separate
  processes: `tests/GameCult.Networking.InteropPeer/Program.cs:27-49` gains
  mode `quic-realtime-serve` (`--port`, `--frames N`, `--delivery
  latest-only|reliable-ordered`, `--interval-ms`): creates a self-signed
  certificate exactly as `CultMeshQuicRealtimeTransportTests.cs:203-228`,
  starts `CultMeshQuicRealtimeServer`, prints one JSON line
  `{ endpoint: "cultmesh-state+quic://127.0.0.1:<port>?cert-sha256=<HEX>" }`
  to stdout, then broadcasts `N` frames with increasing sequence and exits
  after the last `send_complete` or on stdin close.
  `GameCult.Networking.InteropPeer.csproj:9-11` gains a `ProjectReference` to
  `src/GameCult.Mesh.Quic/GameCult.Mesh.Quic.csproj`.
  `packages/cultnet-ts/test/interop/cultnet-interop.test.ts` gains
  `test("CultMesh QUIC realtime: C# managed provider and TypeScript consumer", ...)`
  using `spawnServeProcess` (`:2491`) for the C# peer and `runJsonCommand`
  (`:2557`) for a new TypeScript peer script
  `packages/cultmesh-ts/test/interop/cultmesh-quic-peer.ts` (`dial --endpoint
  --expect N`) that prints the received frames as JSON (fields plus payload
  hex). The harness drives processes; it does not import `cultmesh-ts`
  (dependency direction: `cultmesh-ts` depends on `cultnet-ts`, so the reverse
  import is forbidden). The peer script path follows the `tsPeerScript`
  pattern at `:41`.

Verification:

- `npm run test --workspace packages/cultmesh-ts` with
  `CULTMESH_QUIC_NATIVE_DIR=artifacts/quic-native/win32-x64`.
- `npm run test:interop --workspace packages/cultnet-ts` filtered with
  `--test-name-pattern "QUIC realtime"`; it builds the C# interop peer through
  the existing `csharpInteropPeerBuild` path (`:45-58`, `:71`).
- Negative greps: `rg -n "new Function|koffi\.register|\.async\(" packages/cultmesh-ts/src`
  must show `.async(` only on `nextEvent` in `realtime-quic-native.ts` and no
  `koffi.register` (no callbacks);
  `rg -n "verifyP256|canonicalRoute" packages/cultmesh-ts/src` must return
  nothing.
- Operator-only: none.

## 10. Cut 5 (behaviour): TypeScript provider and the coalescing outbox

Purpose: the StreamPixels role. A TypeScript listener that broadcasts with an
explicit delivery mode and cannot be backpressured by a slow consumer, proven
against both C# connectors in separate processes.

Deletes: none.

Adds (in `packages/cultmesh-ts/src/realtime-quic.ts`):

- `CultMeshQuicRealtimeProvider.listen({ host, port, serverCertificate:
  { pkcs12, password }, handshakeTimeoutMs })`: opens a listener, computes
  `advertisedEndpoint` (`cultmesh-state+quic://<host>:<boundPort>?cert-sha256=<HEX>`,
  uppercase, because the Unity connector requires the pin, P18), accepts
  connections into a peer set, exposes `connectionCount`, `broadcast(frame)`,
  `receive()` for client-originated frames, `dispose()`.
- Delivery semantics, mirroring `CultMeshQuicRealtimeTransport.cs`:
  `reliable-ordered` opens one unidirectional stream of kind 1 per peer on
  first use and serialises writes behind a per-peer promise chain; `broadcast`
  resolves when every peer's `send_complete` arrives or the peer is evicted
  (`:216-235`, `:237-255`). `latest-only` enqueues into a per-peer keyed
  outbox (`Map<"channel\u001fbody", frame>` plus a ready queue) and returns
  synchronously (`:363-367`, `:572-618`); each peer's pump opens a fresh kind-2
  stream per frame, sends with `fin`, and only dequeues the next frame after
  `send_complete`, so a stalled peer accumulates at most one pending frame per
  `(channel, body)` and never blocks `broadcast`. `unreliable` throws
  `NotSupported` with the same sentence as `:373-375` adjusted for the
  runtime.
- Peer eviction on send failure or `connection_shutdown`; no reconnect logic
  on the provider.

Interop lanes (all separate processes; all in
`packages/cultnet-ts/test/interop/cultnet-interop.test.ts`):

- TypeScript provider to C# native connector (the Unity one): the harness
  starts `cultmesh-quic-peer.ts serve --frames 5 --delivery latest-only`,
  reads its advertised endpoint from stdout, then runs
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests --filter FullyQualifiedName~NativeConnectorReceivesFromExternalManagedProvider`
  with `CULTMESH_NATIVE_EXTERNAL_ENDPOINT=<endpoint>` (the existing test at
  `CultMeshNativeQuicRealtimeTransportTests.cs:15-33` is reused unchanged: it
  already asserts a frame arrives with non-empty schema, body and payload).
  Rename the test to `NativeConnectorReceivesFromExternalProvider` since the
  provider is no longer necessarily managed; the env var name stays.
- TypeScript provider to C# managed connector: add
  `ManagedConnectorReceivesFromExternalProvider` to
  `tests/GameCult.Mesh.Quic.Tests/CultMeshQuicRealtimeTransportTests.cs`,
  gated by the same `CULTMESH_NATIVE_EXTERNAL_ENDPOINT`, using
  `CultMeshQuicRealtimeTransportConnector` with the pin from the endpoint
  (`:111-119`) and no validator, asserting frame fields and payload bytes
  equal what the TypeScript peer prints. The harness runs it the same way.
- `LatestOnly` under a stalled consumer: `cultmesh-quic-peer.ts serve
  --frames 200 --delivery latest-only --interval-ms 1` while the harness dials
  twice with the TypeScript consumer, pauses one consumer's pump for 500 ms
  (a test hook on the runtime that stops calling `next_event`), and asserts:
  the provider's 200 `broadcast` calls complete within a bounded time
  regardless of the pause; the healthy consumer reaches sequence 199; the
  paused consumer, once resumed, converges on 199 with fewer than 200 frames
  received. This is the TypeScript form of
  `CultMeshNativeQuicRealtimeTransportTests.cs:101-141` plus the convergence
  assertion from `CultMeshQuicRealtimeTransportTests.cs:111-140`.
- Golden bytes across processes: the C# managed connector lane above prints
  the received frame's re-encoding (via `CultMeshRealtimeWireProtocol.EncodeFrame`)
  as hex; the harness compares it with the TypeScript peer's `encodeRealtimeFrame`
  output for the same frame. That closes "TypeScript encodings decode in C#"
  by process, not by shared file.

Verification:

- `npm run test --workspace packages/cultmesh-ts` (provider unit tests with
  the TypeScript consumer in-process but separate connections: reliable
  ordering in both directions mirrors `CultMeshQuicRealtimeTransportTests.cs:71-110`;
  latest-only coalescing; unreliable fails closed).
- `npm run test:interop --workspace packages/cultnet-ts --test-name-pattern "QUIC realtime"`.
- `dotnet test tests/GameCult.Mesh.Quic.Tests` and
  `dotnet test tests/GameCult.Mesh.Quic.Native.Tests` without the env var
  (the new tests `Assert.Ignore`, the existing ones still pass).
- Negative greps: `rg -n "setInterval|setTimeout" packages/cultmesh-ts/src/realtime-quic.ts`
  must show no polling of the bridge (the pump is the only wait);
  `rg -n "reconnect" packages/cultmesh-ts/src/realtime-quic.ts` must return
  nothing on the provider side.
- Operator-only: none.

## 11. Cut 6 (packaging and release, remapped under Q5 C)

Purpose: a consumer host with no toolchain and no registry access for the
binding gets it by receiving the `cultmesh-ts` package directory, which is how
every consumer already receives the package (P16, P17). Registry publication
is parked, not ruled out; the seam it reattaches to is named at the end of
this section.

### 11.1 Body facts this cut stands on

- The deploy tarball is a `git archive`. The Idunn actuator on Starfire runs
  `git -C CultLib archive --format=tar --output=<tar> origin/main packages/cultnet-ts packages/cultcache-ts`
  (`F:\Projects\Odin\scripts\deploy-yggdrasil-streampixels.ps1:59`), uploads
  it by `sftp` (`:127`), and `deploy-streampixels-preview.sh:95-99` extracts it
  into `/srv/streampixels/CultLib` after deleting only those two package
  directories (`:97`) `(source read)`. Two consequences: only committed files
  ride (a `git archive` cannot carry untracked or ignored files), and
  `packages/cultmesh-ts` is not in the archive today at all, although
  StreamPixels declares it (`apps/service/package.json:26`). Adding it to the
  archive list and to `:97` is deploy work in Odin and gamecult-ops, outside
  this campaign (section 16); the Yggdrasil smoke below therefore ships its
  own archive of the same shape rather than waiting on that change.
- pnpm materialises a `file:` directory dependency as a copy of the
  package's `files` entries only: with `pnpm 10.33.0`, `node_modules/cultmesh-ts`
  held `README.md`, `dist`, `package.json` and nothing else (no `src`, no
  `test`) `(probe: scratch consumer with file: overrides)`. `files` is the
  gate, so `native` must be listed in it.
- The MsQuic binaries the koffi probe loaded (P2) are the Unity plugin copies
  at `unity/org.gamecult.cultlib/Runtime/Plugins/x86_64/` (`probe.js:4-7`),
  committed in plain git (`git ls-files`; `.gitattributes` is `* text=auto`
  only, no LFS), `gamecult_mesh_quic_native.dll` 40,448 bytes and
  `msquic.dll` 536,928 bytes `(probe)`. Committing binaries beside the code
  that loads them is therefore existing practice in this repo, not a new one.
- The Linux runtime is one file: `libmsquic.so.2 -> libmsquic.so.2.5.9`,
  7,375,872 bytes, inside the pinned deb (P7); the deb also carries
  `libmsquic.lttng.so.2.5.9` and `datapath_raw_xdp_kern.o`, neither needed
  `(probe: dpkg-deb -c in debian:13)`. The repo pack is 31.68 MiB today
  `(probe: git count-objects)`.
- `debian:13` resolved to digest
  `sha256:f324c7ff54321e8d9c588493a20244965938ce0aa50bbd1022d38010e9ffc4b1`
  on 2026-09-16 and carries `g++ 4:14.2.0-1` `(probe: docker pull; apt-cache
  policy)`. The builder is pinned by that digest, not by the moving tag.
- `GameCult/CultLib` is public `(probe: gh repo view)` and already carries two
  GitHub Releases with binary assets, `cultlib-unity-v1.0.3`
  (`org.gamecult.cultlib-1.0.3.tgz`) and `gamecult-geometry-v0.1.0` (a crate
  and nupkgs) `(probe: gh release view)`; nothing in `scripts/`, `docs/` or
  `.github/` creates them, so they were cut by hand `(source read: rg)`.
  Release assets on a public repo download with plain `curl`, no token.
- The npm registry is reachable from Starfire: `npm ping` answers `PONG`,
  `npm view koffi` is HTTP 200, and `npm view @gamecult/cultcache-ts` is a
  404 because it was never published `(probe)`. This confirms the corrected
  premise in section 13: C is deferral, not necessity.
- `.gitignore` ignores `artifacts/` (`:66`) and `node_modules`; it has no rule
  for `*.so` or `*.dll` `(source read)`, so nothing blocks the commit path.
- The root `package-lock.json` is committed `(probe: git ls-files)`, so
  `npm ci` is the real install path (as `publish-packages.yml:55` uses it);
  `cultnet-interop.yml:95` uses `npm install --no-package-lock` instead, a
  pre-existing inconsistency this cut does not touch.

### 11.2 Q8, the fork C surfaces (operator decision, recommended option first)

Where do the built binaries live so that the `git archive` above carries
them to the host?

- **Option 1, commit them.** `packages/cultmesh-ts/native/{win32-x64,linux-x64}/`
  hold the four binaries, committed by the operator from the Release assets
  the workflow attaches, with `MANIFEST.txt` and `SHA256SUMS` beside them.
  Price: about 8.0 MB in the tree (7,375,872 + 536,928 + 40,448 + the bridge
  `.so`, on the order of 60 KB); pack growth roughly the compressed size,
  about 3 MB, and that only when the MsQuic pin moves, because
  `libmsquic.so.2` and `msquic.dll` are copies of Microsoft's files and
  change with the pin, not with the bridge. A bridge rebuild adds tens of KB.
  A CI job checks `sha256sum -c SHA256SUMS` on every push, so the manifest
  and the bytes cannot drift. Deploy path change required: none beyond the
  `packages/cultmesh-ts` archive-list fix already owed. Human step: download,
  verify, commit (operator-only, section 11.7).
- **Option 2, fetch them.** `native/<platform>/` is git-ignored; the tree
  commits only `MANIFEST.txt`, `SHA256SUMS` and a `scripts/fetch-quic-native.mjs`
  (about 80 lines) that downloads the Release assets and verifies digests.
  Price: zero binary bytes in git, but every carrier of the package directory
  becomes incomplete until the fetch runs: a fresh clone, every worktree,
  Heimdall's vendored copy, `npm pack` (a tarball without binaries looks
  valid), and the actuator, which must run the fetch and then `tar --append`
  the ignored files after `git archive` (cross-repo edit in Odin), or the
  deploy script must fetch from GitHub on Yggdrasil at deploy time (cross-repo
  edit in gamecult-ops, plus GitHub egress the StreamPixels path does not
  have today). It is the "tarball with a `.so` in it by hand" failure the
  original Q5 text warned about, moved one step left.

**Recommended: Option 1.** The tarball is a `git archive`, so committed is the
only state that rides it without a second mechanism; the repo already commits
the same `msquic.dll` for Unity; and the growth is bounded by the MsQuic pin,
not by bridge iteration. Everything below is written for Option 1; Option 2
changes only 11.4's manifest paragraph, the closure smoke, and the operator
steps, and is not mapped further unless chosen.

**Ruled 2026-09-16: Option 1.** Operator: "Commit them, yeah." Option 2 is
history.

### 11.3 Deletes

None in the tree. The ruling-A surfaces (`packages/cultmesh-quic-native/*`,
`optionalDependencies`, `cultnet-ts`/`cultmesh-ts` publish jobs, new tag
prefixes in `publish-packages.yml:20-24`) are never created; that is the
subtraction, and it is an estimate change, not a diff (section 11.8).

The existing `@gamecult/cultcache-ts` publish job
(`.github/workflows/publish-packages.yml:39-78`) stays. It is out of this
campaign's scope: it publishes a package this migration does not touch, its
only consumer today reaches it by `file:` (`F:\Projects\Heimdall\package.json:22`
`(source read)`), and it is the shape the parked registry pass will copy for
`cultnet-ts` and `cultmesh-ts`. Whether it should run before then is a
question for that pass.

### 11.4 Keeps and moves

- `scripts/build-quic-native.ps1` and the Unity plugin binaries are untouched
  (5.4 deletion line). `scripts/build-quic-native.sh` is the cut-3 script,
  unchanged here.
- The MsQuic license text already committed at
  `unity/org.gamecult.cultlib/Third Party Notices/MSQUIC-LICENSE.txt` is
  copied, not referenced, to `packages/cultmesh-ts/native/MSQUIC-LICENSE.txt`,
  because the package directory must be self-contained once copied out of the
  repo (11.1, pnpm). Same MIT text for both platforms.

### 11.5 Adds

- `packages/cultmesh-ts/native/win32-x64/gamecult_mesh_quic_native.dll`,
  `packages/cultmesh-ts/native/win32-x64/msquic.dll`,
  `packages/cultmesh-ts/native/linux-x64/libgamecult_mesh_quic_native.so`,
  `packages/cultmesh-ts/native/linux-x64/libmsquic.so.2` (the real file, not
  the symlink; `RPATH $ORIGIN` from cut 3 resolves it beside the bridge).
  Directory names are exactly `${process.platform}-${process.arch}` so the
  cut-4 resolver needs no table.
- `packages/cultmesh-ts/native/SHA256SUMS`: `sha256sum` format, one line per
  binary, relative paths, generated by the workflow and committed verbatim.
- `packages/cultmesh-ts/native/MANIFEST.txt` (about 20 lines, `key=value`):
  `msquic.version=2.5.9`; `msquic.windows.source` = the NuGet URL at
  `build-quic-native.ps1:30` with its SHA-256 from `:14`;
  `msquic.linux.source` = the deb pool URL from P7 with its SHA-256;
  `bridge.commit` = the CultLib commit the bridge was built from;
  `build.workflow_run` = the Actions run URL; `build.release` = the Release
  tag; `build.win32-x64.host` = runner image plus MSVC version as printed by
  the job; `build.linux-x64.host` = `ubuntu-latest` plus the `debian:13`
  digest plus `g++` version as printed; and the date. Every value is copied
  from the run log by the operator, then the CI check below keeps the file
  and the bytes agreeing. The binding never reads this file; it exists for
  readers and for the digest check.
- `.github/workflows/quic-native.yml` (about 120 lines), four jobs:
  - `build-win32-x64` on `windows-latest`: checkout, run
    `scripts/build-quic-native.ps1 -OutputDirectory artifacts/quic-native/win32-x64`,
    print `cl.exe` version and `Get-FileHash` of both files, upload
    `actions/upload-artifact` named `quic-native-win32-x64`.
  - `build-linux-x64` on `ubuntu-latest` with
    `container: debian:13@sha256:f324c7ff54321e8d9c588493a20244965938ce0aa50bbd1022d38010e9ffc4b1`
    (Q6 A; the digest is the pin, the tag is the label): `apt-get install -y
    cmake ninja-build g++ curl ca-certificates dpkg`, run
    `scripts/build-quic-native.sh`, print `g++ --version` and `sha256sum`,
    upload `quic-native-linux-x64`. No Node in this job; loading is proven by
    the interop job below, on the same image.
  - `release`, `needs` both, `if: startsWith(github.ref, 'refs/tags/cultmesh-quic-native-v')`,
    `permissions: contents: write`: download both artifacts, write
    `SHA256SUMS` and a draft `MANIFEST.txt` with every value the jobs printed,
    and run `gh release create "$GITHUB_REF_NAME" --verify-tag` with the two
    per-platform `.tar.gz` files, `SHA256SUMS` and `MANIFEST.txt` as assets
    (`GH_TOKEN: ${{ github.token }}`; `gh` is preinstalled on hosted runners,
    no third-party release action). The release notes are the manifest.
  - `verify-committed` on `push` and `pull_request`, `ubuntu-latest`, one
    step: `cd packages/cultmesh-ts/native && sha256sum -c SHA256SUMS`. This is
    the check that the tree's bytes are the bytes the manifest names.
  - Triggers: `push` to `main` and `pull_request` run `verify-committed`
    only; `workflow_dispatch` runs the two builds (dry run, uploads
    artifacts, no release); a `cultmesh-quic-native-v*` tag runs all four.
    Only `release` has write permission.
- `packages/cultmesh-ts/package.json:14-16`: `"files": ["dist", "native"]`.
  No version bump: under C nothing keys on the version; provenance keys on
  the commit in `MANIFEST.txt`.
- `packages/cultmesh-ts/src/realtime-quic-native.ts` (cut 4): the runtime
  class gains a readonly `nativeDirectory` (the absolute directory it loaded
  from). Diagnostic only; the closure smoke asserts on it.
- `scripts/test-typescript-package-closure.mjs`:
  - `:39-41`: for `cultmesh-ts`, assert the packed file list contains all
    four binaries, `native/SHA256SUMS`, `native/MANIFEST.txt` and
    `native/MSQUIC-LICENSE.txt`, on every host (the tarball carries both
    platforms; the host only decides which loads).
  - `:59-70`: the runtime smoke adds
    `assert.equal(typeof mesh.CultMesh.createQuicRealtimeConnector, "function")`,
    the same for `createQuicRealtimeProvider`, then opens a
    `CultMeshQuicNativeRuntime`, asserts
    `runtime.nativeDirectory` ends with `node_modules/cultmesh-ts/native/<platform>`
    (path separators normalised), and closes it. The child at `:70` runs with
    `CULTMESH_QUIC_NATIVE_DIR` deleted from its env so the default path is the
    one exercised. The point: the binaries load from the installed copy of the
    package, not from a repo path or a registry.
- `.github/workflows/cultnet-interop.yml`:
  - After the C# RUDP step (`:127-128`): a step building the Windows bridge
    (`scripts/build-quic-native.ps1 -OutputDirectory artifacts/quic-native/win32-x64`)
    and a step running the `QUIC realtime` lanes with
    `CULTMESH_QUIC_NATIVE_DIR` set to that directory, so CI proves the lanes
    against a fresh build, then a second run of
    `node scripts/test-typescript-package-closure.mjs` proves the committed
    Windows binaries load without the env var.
  - A second job `interop-quic-linux` on `ubuntu-latest` with the same pinned
    `debian:13` container: `apt-get install` the build tools plus `libicu`
    (dotnet's runtime need), `actions/setup-dotnet@v4` 10.0.x and
    `actions/setup-node@v4` 24 (both work inside a container), build the
    bridge with `scripts/build-quic-native.sh`, `npm ci`, run only the
    TypeScript-provider-to-managed-connector and TypeScript-to-TypeScript
    lanes (section 15: the native-connector lane is Windows by design), then
    the closure script without the env var. The container is not optional:
    the artifact is linked against Debian 13's glibc 2.41 and `ubuntu-latest`
    (24.04) carries glibc 2.39 `(probe: ldd --version in both images)`, so a
    symbol version the bridge or `libmsquic` references can be absent on the
    bare runner; run the lanes on the image the artifact targets rather than
    discover that per symbol.
- `README.md` of `packages/cultmesh-ts` gains a "QUIC Realtime Plane" section
  after "RUDP Helpers" (`README.md:332`) with provider and consumer examples
  matching the C# README (`src/GameCult.Mesh.Quic/README.md:13-62`), and a
  "Native binaries" paragraph: where they live, that a `file:` dependency or
  an `npm pack` tarball carries them, the `CULTMESH_QUIC_NATIVE_DIR`
  override, the two provenance files, Windows x64 and Linux x64 (Debian 13
  glibc) only, and the four `libmsquic` runtime dependencies (P7) a Linux host
  must already have.

### 11.6 Per-file changes

- `packages/cultmesh-ts/package.json:14-16`, `files` gains `"native"`.
- `packages/cultmesh-ts/src/realtime-quic-native.ts` (cut 4 file), one
  readonly field `nativeDirectory` set at load.
- `scripts/test-typescript-package-closure.mjs:39-41`, `:59-70` as above;
  `:9` unchanged (no new packages).
- `.github/workflows/cultnet-interop.yml`, two steps after `:128`, one job
  after `:140`.
- `.github/workflows/quic-native.yml`, new.
- `.github/workflows/publish-packages.yml`, unchanged.
- `packages/cultmesh-ts/native/` (seven files), new, four of them binary and
  operator-committed (11.7).
- `packages/cultmesh-ts/README.md`, one section after `:332`.

### 11.7 Verification

- `node scripts/test-typescript-package-closure.mjs` on Windows, with
  `CULTMESH_QUIC_NATIVE_DIR` unset in the shell. Pins: the tarball carries
  both platforms and the provenance files; the installed copy loads its own
  binaries.
- `cd packages/cultmesh-ts/native && sha256sum -c SHA256SUMS` (Git Bash on
  Windows). Pins: committed bytes equal the manifest.
- `git check-ignore -v packages/cultmesh-ts/native/linux-x64/libmsquic.so.2`
  must exit 1 (not ignored), and
  `git ls-files packages/cultmesh-ts/native | wc -l` must print 7. Pins: the
  binaries are tracked, so a `git archive` carries them.
- `npm pack packages/cultmesh-ts --dry-run --json | node -p "JSON.parse(require('fs').readFileSync(0,'utf8'))[0].files.map(f=>f.path).filter(p=>p.startsWith('native/')).join('\n')"`
  must list seven `native/` paths; the closure script already asserts this,
  this is the one-liner for a reader.
- `workflow_dispatch` of `quic-native.yml`: both build jobs green, artifacts
  present, no release created. A tag push `cultmesh-quic-native-v<n>`
  creates the Release with four assets. Pins: Q6 A, the builder is a Linux
  host on the deploy OS and the artifact has a public, digest-named home.
- Negative greps (must return nothing):
  `rg -n "optionalDependencies|cultmesh-quic-native-(win32|linux)|@gamecult/cultmesh-quic-native" packages scripts .github --glob '!**/node_modules/**' --glob '!**/dist*/**'`
  (no platform packages exist);
  `rg -n "node_modules|require\.resolve" packages/cultmesh-ts/src/realtime-quic-native.ts`
  (the resolver never searches for a package);
  `rg -n "\"(pre|post)install\"" packages/*/package.json` (no install-time
  compile or download);
  `rg -n "NPM_TOKEN|npm publish|registry-url" .github/workflows/quic-native.yml`
  (the native workflow publishes to no registry).
- Operator-only, in order:
  1. After the tag build, download the Release assets, compare every digest
     with the run log and with `SHA256SUMS`, place the binaries under
     `packages/cultmesh-ts/native/<platform>/`, fill `MANIFEST.txt` from the
     log, commit. This is the only path bytes take into the tree; no workflow
     writes to `main`.
  2. Yggdrasil smoke, which installs from what C ships (the archived tree)
     and nothing else. On Starfire, at the commit from step 1:
     `git -C F:\Projects\CultLib archive --format=tar --output=$env:TEMP\cultlib-quic-smoke.tar <commit>`
     (the actuator's own mechanism, `deploy-yggdrasil-streampixels.ps1:51,59`,
     whole tree), then `sftp` it to `gamecultadmin@yggdrasil.gamecult.org:/home/gamecultadmin/`.
     On Yggdrasil:
     `mkdir -p /tmp/cultmesh-quic-smoke && tar -xf ~/cultlib-quic-smoke.tar -C /tmp/cultmesh-quic-smoke && cd /tmp/cultmesh-quic-smoke`;
     `(cd packages/cultmesh-ts/native && sha256sum -c SHA256SUMS)` (the bytes
     that arrived are the bytes the manifest names);
     `ldd packages/cultmesh-ts/native/linux-x64/libmsquic.so.2 | grep "not found"`
     must print nothing. If it prints, the missing ones are among
     `libnuma1`, `libxdp1`, `libnl-route-3-200` (P7; `libssl3t64` is base):
     under C nothing installs them because the deb itself is never installed
     on the host, so `sudo apt-get install` them once and record it in
     `gamecult-ops/inventory.md` beside the Node line (`:660`). Then
     `npm ci` (registry access from Yggdrasil is the deploy path's own
     assumption, `deploy-streampixels-preview.sh:102`) and
     `node scripts/test-typescript-package-closure.mjs` with the env var
     unset: this is the same smoke CI runs, now on Node 24.14.1 (P8) on the
     deploy host, loading `node_modules/cultmesh-ts/native/linux-x64`.
     Then the handshake: `npm run test --workspace packages/cultmesh-ts`
     (builds `dist-test` and runs the cut-4/5 suites on the host), then a
     throwaway PKCS12
     (`openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj /CN=127.0.0.1 -days 2 -keyout k.pem -out c.pem && openssl pkcs12 -export -inkey k.pem -in c.pem -passout pass:smoke -out smoke.p12`),
     `node packages/cultmesh-ts/dist-test/test/interop/cultmesh-quic-peer.js serve --frames 5 --delivery latest-only --pkcs12 smoke.p12 --password smoke`
     in one shell (it prints the advertised endpoint), and
     `node packages/cultmesh-ts/dist-test/test/interop/cultmesh-quic-peer.js dial --endpoint <printed> --expect 5`
     in a second, which must print five frames. If cut 5 landed `serve` with
     a generated certificate instead of `--pkcs12/--password` flags, add the
     flags in this cut; the smoke needs a certificate it can name. Finish
     with `rm -rf /tmp/cultmesh-quic-smoke ~/cultlib-quic-smoke.tar`. This is
     the only place the Linux artifact is proven on the deploy host, and only
     the operator holds the key.
  3. Nothing here touches `/srv/streampixels`; StreamPixels adoption
     (archive list, `:97`, its stale lockfile) is consumer work (section 15,
     section 16).

### 11.8 Subtraction estimate delta against section 14

Row 6 of the table read `0 removed, 60 (manifests) + 130 (workflow) + 30
(closure) + 60 (README) = +280` with `+2 optional platform packages` and `+3
publish jobs` in the dependencies line. Under C:

- Removed: 0 (the `cultcache-ts` job stays, 11.3).
- Added text: about 120 (`quic-native.yml`) + 40 (interop workflow) + 30
  (closure) + 70 (README) + 25 (`MANIFEST.txt`, `SHA256SUMS`) + 1
  (`package.json`) = about +285, the same order as before; the 60 lines of
  platform-package manifests are gone and the release/verify jobs take their
  place.
- Added binary: four files, about 8.0 MB in the tree under Option 1, 0 under
  Option 2. Section 14 has no column for this; it is the real price of C and
  is stated here so it is not hidden in a line count.
- Dependencies line becomes: +0 optional platform packages (was +2); +0
  publish jobs (was +3); +1 workflow file with four jobs (two builds, one
  release, one digest check) and +1 job in the interop workflow (was "+1 CI
  job"). Runtime dependency unchanged: `koffi` only.
- Campaign total: about +3,900 source lines, unchanged; plus 8.0 MB of
  committed binaries.

### 11.9 Parked seam: registry publication (later pass)

Not in this campaign, by the operator's choice (section 13). When it is
picked up, it reattaches here without remapping the rest:

- `publish-packages.yml` gains `cultnet-ts` and `cultmesh-ts` jobs shaped like
  `:39-78`, tag prefixes at `:20-24`, dispatch options at `:30-33`; the
  `cultmesh-ts` job `needs` the two `quic-native.yml` build jobs (or the
  workflow is merged) and publishes the artifacts they already produce as
  `@gamecult/cultmesh-quic-native-{win32-x64,linux-x64}` platform packages.
- `packages/cultmesh-ts/package.json` gains `optionalDependencies` on those
  two names; `realtime-quic-native.ts` gains a third resolution branch after
  the env var and before the committed `native/` directory, or the committed
  directory is retired in the same pass.
- Naming (unscoped `cultnet-ts`/`cultmesh-ts` versus `@gamecult/*`) is the
  original Q5 A/B fork, still open for that pass; P14 recorded all names
  unclaimed on 2026-09-16.
- Its Body precondition is an `NPM_TOKEN` repository secret minted on
  npmjs.com, which the operator can now reach (section 13).

## 12. Cut 7 (documentation and ledgers)

- `docs/runtime-parity-scope.md:38` TypeScript row: add "CultMesh QUIC
  realtime provider and consumer over CultLib-owned MsQuic bindings (Windows
  x64, Linux x64), byte-parity with C#, cross-process interop lanes" to
  "Claimed parity"; add "QUIC datagrams (`Unreliable`), browser QUIC" to "Not
  claimed".
- `src/GameCult.Mesh/docs/transport-planes.md:35-46`: name the TypeScript
  provider/consumer beside the .NET and Unity bodies; `:122-125`: state that
  `CULTMESH_NATIVE_EXTERNAL_ENDPOINT` now accepts any provider and is driven
  by the TypeScript interop harness.
- `docs/cultnet-transport-parity.md`: no change. It maps RUDP/TCP/LiteNetLib
  ownership; the realtime plane is a CultMesh plane, not a CultNet transport
  profile, and pretending otherwise would put QUIC into a document whose cut
  line says "Do not put UDP packet mechanics into CultMesh" the other way
  round.
- `docs/parked-features.md`: one entry, "Node Odin WebSocket rendezvous",
  pointing at `cultmesh-browser/src/index.ts:84-191` as the browser owner and
  the `ICultMeshRealtimeLookupSource` port as the seam (Q7).
- `docs/typescript-quic-realtime-target.md:5-7`: status line updated to point
  at this map.
- Under Q5 C, the delivery story every document tells is the one in section
  11: the binding ships inside `packages/cultmesh-ts/native/`, provenance
  lives in `MANIFEST.txt` and `SHA256SUMS`, and consumers keep `file:` paths.
  `native/GameCult.Mesh.Quic.Native/README.md` (cut 3) and
  `packages/cultmesh-ts/README.md` (cut 6) both point at `MANIFEST.txt` as the
  provenance owner rather than restating versions or digests. The parity row
  above says "bindings shipped in the `cultmesh-ts` package tree", not
  "published". Registry publication is described in exactly one place, as
  parked: section 11.9 of this map.

Verification: `rg -n "CultMeshRealtimeTransports.cs" docs src/GameCult.Mesh/docs`
returns only correct paths;
`rg -n -i "registry|npm publish|platform package|optionalDependencies" packages/cultmesh-ts/README.md native/GameCult.Mesh.Quic.Native/README.md docs/runtime-parity-scope.md src/GameCult.Mesh/docs/transport-planes.md`
returns nothing (the only permitted mentions are this map's sections 11.9 and
13 and the target's history).

## 13. Operator questions

The four rulings in the target (Q1-Q4) are closed and not reopened. These are
the forks the probes surfaced.

- **Q5. Registry names.** The binding cannot ship as prebuilt platform
  packages without `cultmesh-ts` itself being on a registry, and today every
  consumer uses `file:` paths and only `@gamecult/cultcache-ts` is published
  (P15, P16). All candidate names are unclaimed (P14). A: publish `cultnet-ts`
  and `cultmesh-ts` under their existing unscoped names (no import changes in
  any of the 11 consuming repos) and put only the new native platform packages
  under `@gamecult/cultmesh-quic-native-{win32-x64,linux-x64}`. B: scope
  everything as `@gamecult/*` now and take the import rename across consumers
  as a separate consumer task. C: no registry; ship the binaries inside the
  CultLib tarball the deploy script already extracts on Yggdrasil
  (`deploy-streampixels-preview.sh:95-99`), keeping `file:` consumption.
  **Recommended: A.** It reshapes nothing consumers use, matches the
  `cultcache-ts` precedent of scoping only what is new, and gives StreamPixels
  a versioned artifact instead of a tarball with a `.so` in it. C is the
  cheapest but leaves the deploy path owning a native binary by hand, which is
  the failure the target's "shipped through a registry" phrase was written to
  avoid.
- **Q6. Linux release builder.** A: GitHub Actions `ubuntu-latest` with
  `container: debian:13` (same OS as Yggdrasil, P8), artifact published by the
  workflow, verified on Yggdrasil by the operator smoke in cut 6. B: build on
  Yggdrasil in the deploy script (installs CMake and g++ on a live public
  host; every deploy compiles). C: build on Starfire's Docker `debian:13`
  engine (P12) and publish from the workstation. **Recommended: A.** It is a
  Linux host, it is reproducible from a tag, and it keeps compilers off
  Yggdrasil. C is kept for the development loop only.
- **Q7. Node-side Odin rendezvous.** The TypeScript consumer needs a route
  source. The browser package has an Odin WebSocket rendezvous
  (`cultmesh-browser/src/index.ts:84-191`); Node 24 has a global `WebSocket`,
  so it would run, but `cultmesh-ts` may not depend on `cultmesh-browser`
  (Q2's spirit), and the first consumer of the Node QUIC path is the interop
  harness, not a product. A: ship the lookup-source port plus a static source
  now, park the Node Odin rendezvous with a note. B: move the Odin rendezvous
  into `cultnet-ts` beside the verifier in this migration so Node and browser
  share it. **Recommended: A.** B is the right end state but is a second
  ownership move in one pipeline; it should follow once a Node consumer that
  discovers through Odin exists.

Not asked, because the target already decided them: datagrams stay out (Q4);
no TLS on TCP planes (Q1); Windows x64 and Linux x64 only (Q3); the verifier
moves to a shared browser-safe module (Q2, section 6 names the module).

### Rulings, 2026-09-16

The operator took all three recommendations the same day the map was written.
Each is now a standing ruling; the options above are history.

- **Q5 ruled A, then re-ruled C the same day.** A had `cultnet-ts` and
  `cultmesh-ts` publishing under their unscoped names with only the two native
  platform packages scoped. The re-ruling was first stated as necessity ("we
  don't even have npm access and cannot get it because our entire subnet is
  blocked"); the operator corrected that Body fact the same day after
  reaching npm signup: "apparently that was true yesterday." The registry is
  reachable from Starfire (`npm ping` answers, section 11.1 `(probe)`), and an
  `NPM_TOKEN` can be minted. **C still stands for this campaign, by choice:
  no registry.** In the operator's words, registry publication is "still for
  a later pass". Consumers keep `file:` paths. The native binaries ship inside
  the `cultmesh-ts` package tree that the deploy path's `git archive` carries,
  and Q6's Actions build attaches both platform artifacts to a GitHub Release
  instead of publishing packages. Registry publication of `cultnet-ts`,
  `cultmesh-ts` and the native binaries is parked, not ruled out; section 11.9
  names the seam it reattaches to, and the original A/B naming fork stays
  open for that pass. Cut 6 is remapped under C (section 11); section 5.4,
  Cut 4's binary resolution and dependency lines, and Cut 7's ledger follow
  it. The one fork C surfaces, whether the binaries are committed or fetched
  from the Release, is Q8 in section 11.2.
- **Q8 ruled Option 1.** The four native binaries are committed at
  `packages/cultmesh-ts/native/<platform>/` with their manifest and digests,
  so they ride the `git archive` the deploy path already builds. Operator's
  words: "Commit them, yeah."
- **Q6 ruled A.** The Linux release artifact is built by GitHub Actions on
  `ubuntu-latest` inside `container: debian:13`, published by the workflow,
  and verified by the operator smoke on Yggdrasil in cut 6. The Docker
  `debian:13` engine on the workstation is the development loop only; nothing
  it produces is a release artifact. No compiler is installed on Yggdrasil.
- **Q7 ruled A.** Cut 4 ships the `ICultMeshRealtimeLookupSource` port and a
  static source. The Node Odin WebSocket rendezvous is parked with a note in
  cut 7's ledger; it moves into `cultnet-ts` beside the verifier in a later
  pipeline, once a Node consumer that discovers through Odin exists.

## 14. Subtraction estimate

Both sides of the number, by cut, in source lines (tests included, generated
`dist*` excluded, vector JSON excluded):

| Cut | Removed or replaced | Added | Net |
| --- | ---: | ---: | ---: |
| 1 trust module | 119 (browser) | 190 (module) + 150 (tests) + 40 (C# vector test) | +261 |
| 2 codec | 0 | 130 + 120 (TS test) + 120 (C# test) | +370 |
| 3 bridge | ~200 rewritten of 440 C++; 24 CMake | ~1,000 C++ (replacing 440), 60 CMake, 70 sh, 40 README | +~700 net C++/build |
| 4 consumer | 0 | 260 (binding) + 400 (consumer/session manager) + 400 (tests) + 200 (C# interop mode) + 150 (harness) + 120 (peer script) | +1,530 |
| 5 provider | 0 | 250 (provider) + 300 (tests) + 150 (harness) + 40 (C# tests) | +740 |
| 6 packaging | 0 | 60 (package manifests) + 130 (workflow) + 30 (closure smoke) + 60 (README) | +280 |
| 7 docs | ~10 | ~40 | +30 |
| Total | ~350 | ~4,260 | about +3,900 |

Dependencies: +1 runtime dependency on `cultmesh-ts` (`koffi`, 1.7 MB, MIT,
loaded lazily); +0 optional platform packages; +0 build-time Node
dependencies (no node-gyp, no node-addon-api, no cmake-js). Targets: +0 .NET
projects (the C# interop peer gains one project reference); +1 CMake platform
branch; +1 shell build script; +1 workflow file with four jobs; +1 job in
the interop workflow; and, under Q8 option 1, about 8.0 MB of committed
binaries. *(This line first read "+2 optional platform packages; +3 publish
jobs; +1 CI job" under ruling A; section 11.8 carries the delta.)*

Why the number is positive and still acceptable: the target orders a new
capability (a QUIC realtime plane in a runtime that has none), and every line
above either is that capability, proves it against the C# reference, or ships
it. The subtraction that was available (one verifier instead of two, one
MsQuic body instead of two, one framing implementation per language instead
of one per host) is taken in cuts 1 and 3. Anything that would have shrunk
the number further (a third-party QUIC stack, a callback-driven addon, a
tarball with a `.so`) was rejected in section 4 for reasons that are not
about line count.

## 15. Not assignable to a cut

- **The Unity package release** carrying the rebuilt bridge DLL. The v1 ABI
  is preserved, so no cut requires it, but the committed DLL will lag the
  source until the operator runs `scripts/build-unity-package.ps1` and
  commits. Recorded here so nobody reads the stale DLL as evidence the
  rewrite did not happen.
- **StreamPixels certificate provisioning** (which PKCS12, where it lives on
  Yggdrasil, how its pin reaches the Odin route). Consumer work by the target.
- **A nonce on the QUIC plane.** The target lists "a replayed nonce" among
  trust negatives, but the QUIC plane has no nonce exchange in C# either
  (`CultMeshQuicRealtimeTransport.cs` opens no session message; trust is the
  Odin route plus the TLS pin, `transport-planes.md:71-73`). Cut 4 tests the
  replay class that does exist (route generation). Adding a nonce is a
  cross-runtime protocol change and needs its own target.
- **Linux coverage of the C# native connector.** `CanConnect` is Windows-only
  (`CultMeshNativeQuicRealtimeTransport.cs:40`); the Linux interop job in cut 6
  therefore runs the TypeScript-provider-to-managed-connector lane and the
  TypeScript-to-TypeScript lanes only. The native-connector lane is Windows
  by design, matching where Unity runs.
- **The `cultcache-ts` naming split** across consumers (section 3). Out of
  scope. Q5 C touches no package name and publishes nothing, so it makes the
  split neither better nor worse; the split becomes the parked registry
  pass's problem (section 11.9), where the A/B naming fork is still open.
  One consequence to carry there: StreamPixels' lockfile resolves
  `cultmesh-ts`'s cache dependency as unscoped `cultcache-ts`
  (`F:\Projects\StreamPixels\pnpm-lock.yaml:2561-2564` `(source read)`) while
  `packages/cultmesh-ts/package.json:46` now names `@gamecult/cultcache-ts`,
  so a `pnpm install --frozen-lockfile` against the current tree would refuse
  before any QUIC code is reached. Consumer work, recorded so the Yggdrasil
  smoke in section 11.7 is not mistaken for StreamPixels adoption.

## 16. Reminder of what a wrong cut looks like

If a cut touches `src/GameCult.Mesh/CultMeshTcp*.cs`, adds TLS to the TCP
schema or content connectors, adds WebTransport to `cultmesh-browser`, adds
QUIC to `cultnet-py`, `cultnet-rs`, or `cultmesh-kotlin`, or edits anything
under `F:\Projects\StreamPixels`, it has left the target's "Not in this
migration" list and is wrong. Stop and re-read this map.
