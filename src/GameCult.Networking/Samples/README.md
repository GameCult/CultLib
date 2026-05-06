# Sample Payload Messages

`ChangeNameMessage` and `ChatMessage` are example application payloads.

They are kept in the base union because they are useful for smoke tests, sample
client/server code, and cross-runtime compatibility checks. They are not meant
to imply that CultNet itself is only for game chat or lobby glue.

The actual durable contract is:

- transport + framing
- encryption and verify/login/register session flow
- shared MessagePack message layout

Applications should define their own domain messages with the same discipline
used by CultCache document contracts:

- explicit field keys
- stable tag assignments
- shared definitions across runtimes
- no casual drift between language bindings

If two apps share a message contract, they should be able to share data without
translating through bespoke middleware fog. That is the point.
