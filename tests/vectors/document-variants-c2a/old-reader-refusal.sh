#!/usr/bin/env bash
# Proves that a reader from before element ids refuses the v3 header, per runtime. The old reader is the code at the C0
# merge (e382bb4), which accepts only cultcache.store.v1; OLD_READER_REV overrides it (the C1 merge, for the C# reader that
# reads v2).
#
#   old-reader-refusal.sh stage
#       Run in a CultLib clone. Commits the old revision plus the v3 vector and the probes in old-reader-probes/ onto a
#       scratch ref and prints its SHA. The tree at that SHA is the old reader with one extra test.
#   old-reader-refusal.sh probe csharp|typescript|python|rust
#       Run at the root of the staged tree (a verification job given that SHA), with the runtime's toolchain on PATH.
#       Exit status zero means the old reader refused the header by name; the refusal message is printed.
set -euo pipefail
case "${1:-}" in
  stage)
    root="$(git rev-parse --show-toplevel)"
    here="tests/vectors/document-variants-c2a"
    old="$(mktemp -d)/old"
    git -C "$root" worktree add --detach "$old" "${OLD_READER_REV:-e382bb4}" >/dev/null
    mkdir -p "$old/$here"
    cp "$root/$here/v3-base.msgpack" "$old/$here/"
    cp "$root/$here/old-reader-probes/OldReaderV3Probe.cs" "$old/tests/GameCult.Caching.Tests/"
    cp "$root/$here/old-reader-probes/old-reader-v3.test.ts" "$old/packages/cultcache-ts/test/"
    cp "$root/$here/old-reader-probes/test_old_reader_v3.py" "$old/packages/cultcache-py/tests/"
    mkdir -p "$old/packages/cultcache-rs/tests"
    cp "$root/$here/old-reader-probes/old_reader_v3.rs" "$old/packages/cultcache-rs/tests/"
    git -C "$old" add -A
    git -C "$old" commit -q -m "old-reader probe over ${OLD_READER_REV:-e382bb4}"
    sha="$(git -C "$old" rev-parse HEAD)"
    git -C "$root" update-ref "refs/old-reader-probe/$sha" "$sha"
    git -C "$root" worktree remove --force "$old"
    echo "$sha" ;;
  probe)
    case "${2:-}" in
      csharp) CULTLIB_ROOT="$PWD" dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~OldReaderV3Probe --logger "console;verbosity=detailed" ;;
      typescript)
        npm install --no-audit --no-fund >/dev/null 2>&1
        cd packages/cultcache-ts
        node ../../node_modules/typescript/bin/tsc -p tsconfig.json
        node ../../node_modules/typescript/bin/tsc -p tsconfig.test.json
        node --test dist-test/test/old-reader-v3.test.js ;;
      python) PYTHONPATH=packages/cultcache-py/src python3 packages/cultcache-py/tests/test_old_reader_v3.py -v ;;
      rust) cd packages/cultcache-rs && cargo test --test old_reader_v3 -- --nocapture ;;
      *) echo "usage: $0 probe csharp|typescript|python|rust" >&2; exit 2 ;;
    esac ;;
  *) echo "usage: $0 stage | probe <runtime>" >&2; exit 2 ;;
esac
