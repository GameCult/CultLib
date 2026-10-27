#!/usr/bin/env bash
# The C2a format gate: a reader from before element ids must refuse what C2a writes, and a store the 1.0.60 writer made must
# open alike at 2.0.1 and at the tree under test. Each old revision is a resolved SHA with its tag beside it, because the
# verification clone holds ancestors, not tags; an environment variable overrides each.
#
#   C#              OLD_READER_REV_CSHARP  b8fc5eb0  cultlib-unity-v2.0.1
#   TypeScript,     OLD_READER_REV_LEAF    02cf04b5  cultcache-rs-v0.3.1 (the whole-store writer fix; no cultcache-ts tag carries it)
#   Python, Rust
#   1.0.60 writer   OLD_WRITER_REV         45c2f400  cultlib-unity-v1.0.60
#
#   old-reader-refusal.sh stage csharp|typescript|python|rust|writer
#       Run in a CultLib clone. Commits the old revision plus the C2a-written vectors and that runtime's probes onto a scratch
#       ref and prints its SHA. The tree at that SHA is the old code with extra tests ("writer" is the 1.0.60 writer).
#   old-reader-refusal.sh probe csharp|typescript|python|rust|writer
#       Run at the root of a staged tree, with the runtime's toolchain on PATH. Exit status zero means every probe passed; the
#       refusal messages are printed.
#   old-reader-refusal.sh gate csharp|typescript|python|rust
#       Run in a CultLib clone at the revision under test: stage, then probe. For csharp, C2aGateWriter first writes the live
#       v3 and v5 stores into $GATE_OUT (default /tmp/gate) and the 2.0.1 probes read them beside the committed vectors; the
#       1.0.60 writer then writes a ref-list store there, 2.0.1 and the tree under test both open it, write the same documents
#       back and reread the file, and their REFLIST-SUMMARY lines (records by schema id, the list's items in order, the Items
#       slot's catalog entry, warnings, header) must be identical.
#
# Every stage asserts how many cases it PASSED, so a probe that was renamed out of its filter or a leaf test that was skipped
# fails the gate by name ('<stage>-count') instead of passing with nothing run; a C# result that is not Passed (an [Ignore]d
# case is NotExecuted) fails '<stage>-notpassed' and Python's skipped/expected-failure forms fail 'python-notpassed'. The expected counts are the constants below;
# each is confirmed against the probe's source: C#, the Passed results in the TRX the run wrote (by class); Rust, the sum of 'N passed';
# Python, 'Ran N tests'; TypeScript, node's 'pass N'. Each stage also counts the refusal lines it printed (EXPECT_REFUSED_*).
set -euo pipefail

here="tests/vectors/document-variants-c2a"
self="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
OLD_READER_REV_CSHARP="${OLD_READER_REV_CSHARP:-b8fc5eb0}"   # cultlib-unity-v2.0.1
OLD_READER_REV_LEAF="${OLD_READER_REV_LEAF:-02cf04b5}"       # cultcache-rs-v0.3.1
OLD_WRITER_REV="${OLD_WRITER_REV:-45c2f400}"                 # cultlib-unity-v1.0.60

EXPECT_C2A_WRITER=2        # C2aGateWriter: C2aStamps..., TheCommittedVectorsAreWhatC2aWrites
EXPECT_READER_GATE=10      # OldReaderGateProbe: five TestCaseSource tests, each over the committed and the live store
EXPECT_READER_REFLIST=1    # OldReaderRefListProbe
EXPECT_OLD_WRITER=1        # OldWriterRefListProbe
EXPECT_HEAD_REFLIST=1      # RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1
EXPECT_RUST=3              # tests/old_reader_v3.rs
EXPECT_PYTHON=4            # test_old_reader_v3.py
EXPECT_TYPESCRIPT=3        # old-reader-v3.test.ts

# Refusals each stage must PRINT: every probe writes one 'OLD-READER <runtime> refused <label>: ...' line from the point where it
# has observed the refusal, so a case that returned or passed early (Assert.Pass, a bare return) is Passed but prints nothing.
EXPECT_REFUSED_CSHARP=14   # OldReaderGateProbe: 7 Names calls (1+1+2+1+2 over the five tests) x 2 sources
EXPECT_REFUSED_RUST=3
EXPECT_REFUSED_PYTHON=4
EXPECT_REFUSED_TYPESCRIPT=3

# The committed 1.0.60 writer vector is pinned by hash, not by regenerating it: the 1.0.60 writer stamps wall-clock times, so two
# writes of the same documents differ and no writer stage can reproduce the bytes. A hand edit changes the hash and fails the
# gate as 1.0.60-vector; a deliberate regeneration (README) commits the new hash with the file.
OLD_WRITER_VECTOR="tests/vectors/ref-list-drift/v1-0-60-written.cc"
OLD_WRITER_VECTOR_SHA256=c05e6baa780380ea53a0dc4d0110aedb9e760d7617babb26fbb8a27f9d199a7b

usage() { echo "usage: $0 stage|probe|gate csharp|typescript|python|rust (writer for stage and probe)" >&2; exit 2; }

revision_for() {
  case "$1" in
    csharp) echo "$OLD_READER_REV_CSHARP" ;;
    typescript|python|rust) echo "$OLD_READER_REV_LEAF" ;;
    writer) echo "$OLD_WRITER_REV" ;;
    *) usage ;;
  esac
}

stage() {
  local runtime="$1" root rev old sha
  root="$(git rev-parse --show-toplevel)"
  rev="$(revision_for "$runtime")"
  old="$(mktemp -d)/old"
  git -C "$root" worktree add --detach "$old" "$rev" >/dev/null
  mkdir -p "$old/$here"
  case "$runtime" in
    csharp)
      cp "$root/$here/c2a-written-ids.msgpack" "$old/$here/"
      cp -r "$root/$here/c2a-written-ids-directory" "$old/$here/"
      cp "$root/$here/old-reader-probes/OldReaderGateProbe.cs" "$root/$here/old-reader-probes/OldReaderRefListProbe.cs" "$old/tests/GameCult.Caching.Tests/" ;;
    typescript)
      cp "$root/$here/c2a-written-ids.msgpack" "$old/$here/"
      cp "$root/$here/old-reader-probes/old-reader-v3.test.ts" "$old/packages/cultcache-ts/test/" ;;
    python)
      cp "$root/$here/c2a-written-ids.msgpack" "$old/$here/"
      cp "$root/$here/old-reader-probes/test_old_reader_v3.py" "$old/packages/cultcache-py/tests/" ;;
    rust)
      cp "$root/$here/c2a-written-ids.msgpack" "$old/$here/"
      mkdir -p "$old/packages/cultcache-rs/tests"
      cp "$root/$here/old-reader-probes/old_reader_v3.rs" "$old/packages/cultcache-rs/tests/" ;;
    writer)
      cp "$root/$here/old-writer-probes/OldWriterRefListProbe.cs" "$old/tests/GameCult.Caching.Tests/" ;;
  esac
  git -C "$old" add -A
  git -C "$old" -c user.name=old-reader-refusal -c user.email=old-reader-refusal@localhost commit -q -m "$runtime probe over $rev"
  sha="$(git -C "$old" rev-parse HEAD)"
  git -C "$root" update-ref "refs/old-reader-probe/$sha" "$sha"
  git -C "$root" worktree remove --force "$old"
  echo "$sha"
}

probe() {
  case "$1" in
    csharp) CULTLIB_ROOT="$PWD" dotnet test tests/GameCult.Caching.Tests --filter "FullyQualifiedName~OldReaderGateProbe|FullyQualifiedName~OldReaderRefListProbe" --logger "console;verbosity=detailed" --logger "trx;LogFileName=reader.trx" --results-directory "${GATE_TRX_DIR:-TestResults}" ;;
    writer) dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~OldWriterRefListProbe --logger "console;verbosity=detailed" --logger "trx;LogFileName=writer.trx" --results-directory "${GATE_TRX_DIR:-TestResults}" ;;
    typescript)
      npm install --no-audit --no-fund >/dev/null 2>&1
      cd packages/cultcache-ts
      node ../../node_modules/typescript/bin/tsc -p tsconfig.json
      node ../../node_modules/typescript/bin/tsc -p tsconfig.test.json
      node --test dist-test/test/old-reader-v3.test.js ;;
    python) PYTHONPATH=packages/cultcache-py/src python3 packages/cultcache-py/tests/test_old_reader_v3.py -v ;;
    rust) cd packages/cultcache-rs && cargo test --test old_reader_v3 -- --nocapture ;;
    *) usage ;;
  esac
}

# Stage a runtime's probes, then run them in a worktree of the staged commit.
probe_staged() {
  local sha tree rc=0
  sha="$(stage "$1")"
  tree="$(mktemp -d)/staged"
  git worktree add --detach "$tree" "$sha" >/dev/null
  (cd "$tree" && "$self" probe "$1") || rc=$?
  git worktree remove --force "$tree"
  return $rc
}

# A log with no summary line (a stage that failed before printing one) yields an empty file, and the gate names the failure.
summary_of() { { grep -o 'REFLIST-SUMMARY .*' "$1" || true; } | sort -u; }

# Cases a TRX records as Passed, for test classes whose name ends in $2; 0 when the run wrote no TRX. A result is tied to its class
# through its execution id, so one class cannot stand in for another. Every result that is not Passed (Skipped, NotExecuted,
# Inconclusive, Failed) is listed by trx_unpassed, and the stage fails on it by name.
trx_passed() {
  [ -f "$1" ] || { echo 0; return; }
  local flat ids
  flat="$(tr '\n' ' ' < "$1")"
  ids="$(printf %s "$flat" | grep -o "<Execution id=\"[^\"]*\" */> *<TestMethod [^>]*className=\"[^\"]*$2\"" | grep -o "Execution id=\"[^\"]*\"" | cut -d\" -f2 || true)"
  printf %s "$flat" | grep -o "<UnitTestResult [^>]*outcome=\"Passed\"" | grep -o "executionId=\"[^\"]*\"" | cut -d\" -f2 | { grep -Fxc -f <(printf %s "$ids") || true; }
}
trx_unpassed() { if [ -f "$1" ]; then tr '\n' ' ' < "$1" | grep -o "<UnitTestResult [^>]*" | grep -v "outcome=\"Passed\"" | grep -o "testName=\"[^\"]*\" [^>]*outcome=\"[A-Za-z]*\"" || true; fi; }

# Refusal lines a stage's log printed for runtime $2.
refused_lines() { grep -c "OLD-READER $2 refused " "$1" || true; }

# Python's runner says "Ran N tests" even when some were skipped or expected to fail; those forms are not passes.
python_unrun() { grep -E '(skipped|expected failures|unexpected successes)=' "$1" || true; }

# Cases a leaf runtime's own output says it passed: Rust's 'N passed' lines summed, # skips that line hides), node's 'pass N'.
leaf_cases() {
  case "$1" in
    rust) awk '/^test result:/ { n += $4 } END { print n + 0 }' "$2" ;;
    python) awk '/^Ran [0-9]+ test/ { n = $2 } /^(OK|FAILED) [(]/ { s = $0; while (match(s, /[a-z]+=[0-9]+/)) { n -= substr(s, RSTART + index(substr(s, RSTART), "=")); s = substr(s, RSTART + RLENGTH) } } END { print n + 0 }' "$2" ;;
    typescript) awk '/^[^[:alnum:]]*pass [0-9]+$/ { n = $NF } END { print n + 0 }' "$2" ;;
  esac
}

# Every csharp stage runs, so a writer that stamps the wrong header is also caught by the 2.0.1 reader opening what it wrote;
# the gate fails if any stage failed and names them.
gate() {
  local failures=""
  # A stage that ran a number of cases other than the one pinned fails as '<stage>-count'.
  expect() { if [ "$2" = "$3" ]; then echo "COUNT $1: $3 passed, as expected"; else echo "COUNT $1: expected $2 passed case(s), got $3"; failures+=" $1-count"; fi; }
  # A stage whose probes printed another number of refusal lines than pinned fails as '<stage>-refused-count', however many cases Passed.
  refused() { local n; n="$(refused_lines "$3" "$1")"; if [ "$n" = "$2" ]; then echo "REFUSED $1: $n refusal lines, as expected"; else echo "REFUSED $1: expected $2 refusal line(s), got $n"; failures+=" $4-refused-count"; fi; }
  # A TRX result that is not Passed (an [Ignore]d case is NotExecuted) fails its stage by name even when the count is met.
  unpassed() { local rows; rows="$(trx_unpassed "$2")"; if [ -n "$rows" ]; then echo "NOT PASSED in $1: $rows"; failures+=" $1-notpassed"; fi; }
  case "$1" in
    csharp)
      local out="${GATE_OUT:-/tmp/gate}" trx
      if [ "$(sha256sum "$OLD_WRITER_VECTOR" | cut -d" " -f1)" = "$OLD_WRITER_VECTOR_SHA256" ]; then echo "VECTOR 1.0.60-vector: sha256 as recorded"; else echo "VECTOR 1.0.60-vector: sha256 differs from the one recorded in this script"; failures+=" 1.0.60-vector"; fi
      rm -rf "$out"; mkdir -p "$out"
      export CULTLIB_GATE_OUT="$out" GATE_TRX_DIR="$out/trx"
      trx="$GATE_TRX_DIR"
      dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~C2aGateWriter --logger "console;verbosity=detailed" --logger "trx;LogFileName=c2a-writer.trx" --results-directory "$trx" || failures+=" c2a-writer"
      expect c2a-writer "$EXPECT_C2A_WRITER" "$(trx_passed "$trx/c2a-writer.trx" C2aGateWriter)"
      unpassed c2a-writer "$trx/c2a-writer.trx"
      probe_staged writer || failures+=" 1.0.60-writer"
      expect 1.0.60-writer "$EXPECT_OLD_WRITER" "$(trx_passed "$trx/writer.trx" OldWriterRefListProbe)"
      unpassed 1.0.60-writer "$trx/writer.trx"
      probe_staged csharp 2>&1 | tee "$out/reader.log" || failures+=" 2.0.1-reader"
      expect 2.0.1-reader "$EXPECT_READER_GATE+$EXPECT_READER_REFLIST" "$(trx_passed "$trx/reader.trx" OldReaderGateProbe)+$(trx_passed "$trx/reader.trx" OldReaderRefListProbe)"
      unpassed 2.0.1-reader "$trx/reader.trx"
      refused csharp "$EXPECT_REFUSED_CSHARP" "$out/reader.log" 2.0.1-reader
      CULTLIB_GATE_OLD_STORE="$out/v1-0-60-ref-list.cc" dotnet test tests/GameCult.Caching.Tests         --filter "FullyQualifiedName~RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1" --logger "console;verbosity=detailed" --logger "trx;LogFileName=head.trx" --results-directory "$trx" 2>&1 | tee "$out/head.log" || failures+=" head-ref-list"
      expect head-ref-list "$EXPECT_HEAD_REFLIST" "$(trx_passed "$trx/head.trx" RefListDriftTests)"
      unpassed head-ref-list "$trx/head.trx"
      summary_of "$out/reader.log" > "$out/reader.summary"
      summary_of "$out/head.log" > "$out/head.summary"
      cat "$out/reader.summary" "$out/head.summary"
      if [ "$(wc -l < "$out/reader.summary")" -eq 1 ] && [ "$(wc -l < "$out/head.summary")" -eq 1 ] && diff "$out/reader.summary" "$out/head.summary"; then
        echo "GATE csharp: the 2.0.1 and head REFLIST-SUMMARY lines are identical"
      else
        failures+=" summary-diff"
      fi
      if [ -n "$failures" ]; then echo "GATE csharp FAILED:$failures"; return 1; fi
      echo "GATE csharp ok" ;;
    typescript|python|rust)
      local log expected refusals
      log="$(mktemp)"
      case "$1" in rust) expected="$EXPECT_RUST" ;; python) expected="$EXPECT_PYTHON" ;; typescript) expected="$EXPECT_TYPESCRIPT" ;; esac
      case "$1" in rust) refusals="$EXPECT_REFUSED_RUST" ;; python) refusals="$EXPECT_REFUSED_PYTHON" ;; typescript) refusals="$EXPECT_REFUSED_TYPESCRIPT" ;; esac
      probe_staged "$1" 2>&1 | tee "$log" || failures+=" $1"
      expect "$1" "$expected" "$(leaf_cases "$1" "$log")"
      refused "$1" "$refusals" "$log" "$1"
      if [ "$1" = python ] && [ -n "$(python_unrun "$log")" ]; then echo "NOT PASSED in python: $(python_unrun "$log")"; failures+=" python-notpassed"; fi
      if [ -n "$failures" ]; then echo "GATE $1 FAILED:$failures"; return 1; fi
      echo "GATE $1 ok" ;;
    *) usage ;;
  esac
}

case "${1:-}" in
  stage) stage "${2:-}" ;;
  probe) probe "${2:-}" ;;
  gate) gate "${2:-}" ;;
  *) usage ;;
esac
