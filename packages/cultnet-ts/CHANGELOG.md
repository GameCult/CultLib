# Changelog

## Unreleased

- Depends on `@gamecult/cultcache-ts ^0.15.0`: the Idunn runtime authority reader calls `readSingleFileStore`, which 0.14.x does not export. An absent authority file is now a plain `Error` without a code.
