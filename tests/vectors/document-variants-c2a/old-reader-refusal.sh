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
#       1.0.60 writer then writes a ref-list store there, 2.0.1 and the tree under test both open it, and their REFLIST-SUMMARY
#       lines must be identical.
set -euo pipefail

here="tests/vectors/document-variants-c2a"
self="$(cd "$(dirname "$0")" && pwd)/$(basename "$0")"
OLD_READER_REV_CSHARP="${OLD_READER_REV_CSHARP:-b8fc5eb0}"   # cultlib-unity-v2.0.1
OLD_READER_REV_LEAF="${OLD_READER_REV_LEAF:-02cf04b5}"       # cultcache-rs-v0.3.1
OLD_WRITER_REV="${OLD_WRITER_REV:-45c2f400}"                 # cultlib-unity-v1.0.60

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
    csharp) CULTLIB_ROOT="$PWD" dotnet test tests/GameCult.Caching.Tests --filter "FullyQualifiedName~OldReaderGateProbe|FullyQualifiedName~OldReaderRefListProbe" --logger "console;verbosity=detailed" ;;
    writer) dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~OldWriterRefListProbe --logger "console;verbosity=detailed" ;;
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

summary_of() { grep -o 'REFLIST-SUMMARY .*' "$1" | sort -u; }

# Every csharp stage runs, so a writer that stamps the wrong header is also caught by the 2.0.1 reader opening what it wrote;
# the gate fails if any stage failed and names them.
gate() {
  case "$1" in
    csharp)
      local out="${GATE_OUT:-/tmp/gate}" failures=""
      rm -rf "$out"; mkdir -p "$out"
      export CULTLIB_GATE_OUT="$out"
      dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~C2aGateWriter --logger "console;verbosity=detailed" || failures+=" c2a-writer"
      probe_staged writer || failures+=" 1.0.60-writer"
      probe_staged csharp 2>&1 | tee "$out/reader.log" || failures+=" 2.0.1-reader"
      CULTLIB_GATE_OLD_STORE="$out/v1-0-60-ref-list.cc" dotnet test tests/GameCult.Caching.Tests         --filter "FullyQualifiedName~RefListDriftTests.A_1_0_60_Written_Store_Opens_Like_2_0_1" --logger "console;verbosity=detailed" 2>&1 | tee "$out/head.log" || failures+=" head-ref-list"
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
    typescript|python|rust) probe_staged "$1" ;;
    *) usage ;;
  esac
}

case "${1:-}" in
  stage) stage "${2:-}" ;;
  probe) probe "${2:-}" ;;
  gate) gate "${2:-}" ;;
  *) usage ;;
esac
