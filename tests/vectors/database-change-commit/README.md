# database-change-commit vectors

Written only by the C# reference, `ATwoChangeCommitIsWrittenAsTheVectorOtherRuntimesDecode` in
`tests/GameCult.Networking.Tests/CultNetReplicaCommitTests.cs`, with `CULTCACHE_WRITE_VECTORS=1`. Every other run
compares the bytes.

- `two-change-commit.msgpack`: two `cultnet.database_change_raw.v0` messages (`removed`, `note:a` and `note:b`)
  concatenated, each as the C# `[MessagePackObject]` writes it (every key present, nil where unset), both carrying
  `commitId` `9f1c0f0e4b7a4c1f9d2e` and `commitCount` 2. Read it as a msgpack stream. Python walks it in
  `packages/cultnet-py/tests/test_cultnet.py`.
