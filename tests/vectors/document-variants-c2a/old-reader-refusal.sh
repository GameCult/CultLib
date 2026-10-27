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
# Every stage asserts how many cases it ran, so a probe that was renamed out of its filter or a leaf test that was skipped
# fails the gate by name ('<stage>-count') instead of passing with nothing run. The expected counts are the constants below;
# each is confirmed against the probe's source: C#, the TRX the run wrote (cases by class); Rust, the sum of 'N passed';
# Python, 'Ran N tests'; TypeScript, node's 'pass N'.
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

# Cases a TRX records for test classes whose name ends in $2 (every class when $2 is empty); 0 when the run wrote no TRX.
trx_cases() { if [ -f "$1" ]; then grep -o "<TestMethod [^>]*className=\"[^\"]*$2\"" "$1" | wc -l; else echo 0; fi; }

# Cases a leaf runtime's own output says it ran.
leaf_cases() {
  case "$1" in
    rust) awk '/^test result:/ { n += $4 } END { print n + 0 }' "$2" ;;
    python) awk '/^Ran [0-9]+ test/ { n = $2 } END { print n + 0 }' "$2" ;;
    typescript) awk '/^[^[:alnum:]]*pass [0-9]+$/ { n = $NF } END { print n + 0 }' "$2" ;;
  esac
}

# Every csharp stage runs, so a writer that stamps the wrong header is also caught by the 2.0.1 reader opening what it wrote;
# the gate fails if any stage failed and names them.
gate() {
  local failures=""
  # A stage that ran a number of cases other than the one pinned fails as '<stage>-count'.
  expect() { if [ "$2" != "$3" ]; then echo "COUNT $1: expected $2 case(s), ran $3"; failures+=" $1-count"; fi; }
  case "$1" in
    csharp)
      local out="${GATE_OUT:-/tmp/gate}" trx
      rm -rf "$out"; mkdir -p "$out"
      export CULTLIB_GATE_OUT="$out" GATE_TRX_DIR="$out/trx"
      trx="$GATE_TRX_DIR"
      dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~C2aGateWriter --logger "console;verbosity=detailed" --logger "trx;LogFileName=c2a-writer.trx" --results-directory "$trx" || failures+=" c2a-writer"
      expect c2a-writer "$EXPECT_C2A_WRITER" "$(trx_cases "$trx/c2a-writer.trx" C2aGateWriter)"
      probe_staged writer || failures+=" 1.0.60-writer"
      expect 1.0.60-writer "$EXPECT_OLD_WRITER" "$(trx_cases "$trx/writer.trx" OldWriterRefListProbe)"
      probe_staged csharp 2>&1 | tee "$out/reader.log" || failures+=" 2.0.1-reader"
      expect 2.0.1-reader "$EXPECT_READER_GATE+$EXPECT_READER_REFLIST" "$(trx_cases "$trx/reader.trx" OldReaderGateProbe)+$(trx_cases "$trx/reader.trx" OldReaderRefListProbe)"
      CULTLIB_GATE_OLD_STORE="$out/v1-0-60-ref-list.cc" dotnet test tests/GameCult.Caching.Tests         --filter "FullyQualifiedName~RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1" --logger "console;verbosity=detailed" --logger "trx;LogFileName=head.trx" --results-directory "$trx" 2>&1 | tee "$out/head.log" || failures+=" head-ref-list"
      expect head-ref-list "$EXPECT_HEAD_REFLIST" "$(trx_cases "$trx/head.trx" RefListDriftTests)"
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
      local log expected
      log="$(mktemp)"
      case "$1" in rust) expected="$EXPECT_RUST" ;; python) expected="$EXPECT_PYTHON" ;; typescript) expected="$EXPECT_TYPESCRIPT" ;; esac
      probe_staged "$1" 2>&1 | tee "$log" || failures+=" $1"
      expect "$1" "$expected" "$(leaf_cases "$1" "$log")"
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
