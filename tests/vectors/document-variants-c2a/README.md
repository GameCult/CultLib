# document-variants-c2a vectors

`c2a-written-ids.msgpack` and `c2a-written-ids-directory/` are what `C2aGateWriter` writes: one record holding element ids, as a
single-file store (header v3) and as a directory store (header v5). The writer stamps every record with one fixed clock, so the
bytes are the same on every run, and `C2aGateWriter.TheCommittedVectorsAreWhatC2aWrites` compares the live output to these files
byte for byte (the directory as relative path and bytes; `*.lock` files hold no state and are left out).

Regenerate them only when C2a's format moves on purpose:
`CULTLIB_GATE_OUT=<empty dir> dotnet test tests/GameCult.Caching.Tests --filter FullyQualifiedName~C2aStamps`, then copy the single
file and the directory store (the page under `store.cc.records/` is named by its content hash) over these.

`old-reader-refusal.sh` is the gate that reads them with the pinned older readers; its header says how to run it.
