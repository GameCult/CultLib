# Changelog

## Unreleased

- Needs the cultcache-ts release that ships with this change (its manifest range moves with that release): the Idunn runtime authority reader calls `readSingleFileStore`, which 0.14.x does not export. An absent authority file is now a plain `Error` without a code.
