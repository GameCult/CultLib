# Pre-Cut 2 store fixtures

Bytes written by CultLib commit `0db1fe5` ("Apply Soul fixes to the store
composition docs and routing tests"), the last commit before Cut 2 changed the
MessagePack stores. They prove stores written before Cut 2 still open.

Produced from a temporary git worktree at `0db1fe5`: a throwaway NUnit fixture
in that worktree's `tests/GameCult.Caching.Tests`, referencing its own Caching
projects and generator, upserted three records into a fresh `CultCache` and
called `FlushAllBackingStores()`. The worktree was removed afterwards.

- `directory-v4/store.cc` plus `directory-v4/store.cc.records/*.msgpack`:
  `new DirectoryMessagePackBackingStore(manifest)`, format
  `cultcache.store.v4.directory-content-addressed-pages`. The empty
  `.commit.lock` lease file was not copied.
- `single-file-v1/store.msgpack`: `new SingleFileMessagePackBackingStore(file)`,
  format `cultcache.store.v1`.

Both stores hold the same records. Document types are defined identically in
`../../PreCut2FixtureDocuments.cs`:

| Key | Type (schema) | Values |
| --- | --- | --- |
| `item:anvil` | `PreCut2FixtureItem` (`fixtures.precut2.item.v1`) | `Name = "anvil"`, `Count = 3` |
| `item:bellows` | `PreCut2FixtureItem` (`fixtures.precut2.item.v1`) | `Name = "bellows"`, `Count = 42` |
| `note:origin` | `PreCut2FixtureNote` (`fixtures.precut2.note.v1`) | `Text = "written before cut 2"` |

Do not regenerate these from a newer commit; their value is that they predate
Cut 2. Tests copy them to a temp directory before opening.
