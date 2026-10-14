# Python MessagePack default payload: history and probes (2026-10-11)

Serves `cultlib-gaps:ruling:py-msgpack-default` and `cultlib-gaps:cut_spec:cut-py-msgpack-default.r1`.

## How a JSON payload default got into .cc

1. Ghostlight `2531b42` (2026-05-06), the first consumer: "The JSONL store is deliberate bootstrap plumbing", msgpack optional.
2. CultLib `b214daab` (2026-05-24) squashed in `python/cultcache-py` from GameCult/cultcache-py@9b4bbb8a with
   `define_document_type` already defaulting to `_json_payload_encoder`; msgpack was an optional extra and the README
   promised a standard-library-only bootstrap. JSON was the default because it was the only codec without msgpack.
3. CultLib `a1ef58cb` (2026-06-13) made msgpack a hard dependency but kept the JSON default and the optional-import
   guards, so the default outlived its reason.
4. Earlier history is unreachable (GameCult/cultcache-py no longer exists; CultLib holds only the squash). voidbot
   `search_history` found no discussion of the choice.

## Probes

- `probe1.py`, `probe2.py`: no runtime records a payload's format in the store, so a reader cannot tell JSON from
  MessagePack except by trying; a one-byte JSON payload `0`-`9` is valid MessagePack (48-57); a MessagePack map with
  a non-str/bytes key packs but `unpackb` refuses it.
- `rmpprobe/`: `rmp_serde::from_slice` reads a JSON object's bytes as `Number(123)`, which is why Brokkr's daemon must
  read exactly one MessagePack value with a trailing-bytes check.
