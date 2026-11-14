# stored-at-stamp vectors

`stamps.msgpack`: an array of `[text, parses]`. `text` (str) is a `storedAt` a peer might send; `parses` is whether it is a stamp.
Every runtime that judges a peer's `storedAt` (the raw put of cultmesh-py, and the C#, TypeScript and Rust servers) is held to
these answers, and a text that does not parse is refused `message_invalid` (field `document`) with nothing minted or stored
(`variants:ruling:raw-put-without-stamp-refused`).

A stamp is an RFC 3339 date-time: `YYYY-MM-DDThh:mm:ss`, an optional fraction of 1 to 9 digits, and `Z` or a numeric offset
`+hh:mm` / `-hh:mm` (hours 00-23, minutes 00-59). Uppercase `T` and `Z`, ASCII digits, no surrounding whitespace, a real
calendar date, seconds 00-59 (no leap second). The local year must be 1-9999 AND the UTC instant (after the offset is applied) must lie in years 0001-9999. The reference is `parse_stored_at` in
`packages/cultcache-py/src/cultcache_py/stores.py` (#172); this file was written from it and is a reviewed commit when it changes.
