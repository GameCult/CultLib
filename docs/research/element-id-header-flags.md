# Prior art: a header flag one writer sets and content another writer cannot see

Serves: `variants:question:non-csharp-writers-mark-ids` (raised in
`variants:follow_up:foreign-ids-under-v1-header`, campaign `variants`). Gathered by
Imagination, session self-2026-10-14b-eureka, 2026-10-14. Facts with pointers; the
recommendation lives in the question, not here.

The CultLib case: the store header (`cultcache.store.v1` / `v3`) says whether any record
holds an element id, so a reader older than ids refuses the store instead of rewriting
elements without them. An element id is an ordinary keyed string in the payload, so a
runtime that does not know which member is the id (cultcache-py, -ts, -rs today) cannot
tell from content whether to mark. The persistence contract
(`src/GameCult.Caching/Contracts/cultcache-persistence-format.md:424-427` at 6afa8ed9)
says those runtimes keep a `v3` header they found and do not decide.

## How established formats split the job

| System | Who sets the flag | What an implementation that does not understand the feature does | Pointer |
|---|---|---|---|
| ext4 superblock | The code that first uses a feature sets its bit in `s_feature_compat`, `_ro_compat` or `_incompat`. | Unknown `incompat` bit: refuse to mount (fsck stops too). Unknown `ro_compat` bit: mount read-only, never write. Unknown `compat` bit: read and write freely; fsck should not modify. | https://www.kernel.org/doc/html/latest/filesystems/ext4/super.html (feature fields) |
| SQLite file header | The writer's journal mode sets the write/read version bytes (offsets 18, 19); the schema format number (offset 44) is set by the writer of the newer schema features. | Read version 1-2 with write version > 2: open read-only. Read version > 2: neither read nor write. | https://www.sqlite.org/fileformat.html section 1.3.3, 1.3.10 |
| git repository format | A repository declares `core.repositoryformatversion = 1` and the `extensions.*` it uses; the tool enabling a feature writes its extension key. | Any unimplemented `extensions.*` key in a v1 repo: the operation must not proceed (read or write). | https://git-scm.com/docs/repository-version |
| Protocol Buffers | No flag. New fields are self-delimited by tag. | Parsers keep unknown fields and write them back (proto2 always; proto3 again since 3.5), so an old reader does not drop a newer writer's data on rewrite. | https://protobuf.dev/programming-guides/proto3/#unknowns |
| Avro container files | The writer embeds its full schema in the file header. | Reader resolves writer schema against its own; a writer field the reader lacks is ignored, so it is lost if the reader rewrites. | https://avro.apache.org/docs/current/specification/ ("Schema Resolution") |
| Parquet | Writer stamps `created_by` (application and version) in FileMetaData. | Readers keep a list of known-bad writer versions and distrust their metadata (parquet-mr ignores min/max statistics from writers before 1.8.0, PARQUET-251). | https://github.com/apache/parquet-format/blob/master/src/main/thrift/parquet.thrift (`created_by`); https://issues.apache.org/jira/browse/PARQUET-251 |

## Patterns, as facts

- Flag-based formats (ext4, SQLite, git) put the duty to set the flag on the writer that
  introduces the content. None of them has a writer that carries feature content while
  not knowing it is that feature: an implementation that does not understand a feature
  either does not write (ro_compat, SQLite read-only) or does not touch the store (git).
- Formats without a flag (protobuf, Avro) handle the older reader by preservation
  (protobuf keeps unknown fields) or accept loss (Avro drops unknown writer fields on
  rewrite). CultCache's C# 1.x readers skip unknown keyed slots and do not preserve
  them, which is why the `v3` header exists.
- Parquet's `created_by` is a third option: readers judge content by who wrote it and
  compensate for writers known to get a flag or statistic wrong.
- No system surveyed asks a reader to infer a feature flag from content it cannot
  recognise; where content cannot be recognised, the burden is on writers.

## Mechanism facts from this pass (probes at 6afa8ed9 on Yggdrasil)

- A C# cache that pulls the Python-written `py-store/leaf-record.cc` (v1, element ids
  `000000000021/22`) holds that record with `HoldsIds = true`, `IdsInMemoryOnly = false`;
  a C# write that adds an unrelated record copies it forward and leaves the header `v1`.
  The ids survive in C#; a pre-id reader with a type lacking the id slot would drop them.
- cultcache-py, -ts and -rs read `v3` and keep it on rewrite; none has a declaration of
  which member is an element id.
