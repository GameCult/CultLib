using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// The content hash a write-receipt server binds to a (peer, messageId) dedupe key
    /// (contracts/cultnet/interop/write-receipt.md, rule 5).
    /// </summary>
    public static class CultNetWriteContentHash
    {
        /// <summary>
        /// SHA-256 of the MessagePack array <c>[operation, schemaId, recordKey, payload]</c>, <c>payload</c> a <c>bin</c>
        /// for a put and nil for a delete.
        /// </summary>
        public static byte[] Compute(string operation, string schemaId, string recordKey, byte[]? payload)
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePackWriter(buffer);
            writer.WriteArrayHeader(4);
            writer.Write(operation);
            writer.Write(schemaId);
            writer.Write(recordKey);
            if (payload == null) writer.WriteNil();
            else writer.Write(payload);
            writer.Flush();
            using var sha = SHA256.Create();
            return sha.ComputeHash(buffer.WrittenSpan.ToArray());
        }
    }

    /// <summary>
    /// Whether a peer's hello opted in to write receipts: it lists both <c>cultnet.write_admitted.v0</c> and
    /// <c>cultnet.error.v1</c>. The capability changes framing only; it never authorizes.
    /// </summary>
    internal static class CultNetWriteReceiptCapability
    {
        public static bool Declares(CultNetHelloMessage hello)
        {
            var versions = hello.SupportedMessageVersions;
            return versions != null
                   && Array.IndexOf(versions, CultNetSchemaVersions.WriteAdmitted) >= 0
                   && Array.IndexOf(versions, CultNetSchemaVersions.ErrorV1) >= 0;
        }
    }

    /// <summary>
    /// One receipt-capable peer's writes: message id to content hash, so a resend of the same content is re-receipted
    /// without being committed again and the same id with other content is refused.
    /// </summary>
    internal sealed class CultNetWriteReceiptSession
    {
        /// <summary>The most writes a peer is remembered for; a peer at the limit has new writes refused.</summary>
        internal const int MaxRememberedWrites = 1024;

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _writes = new(StringComparer.Ordinal);

        private sealed class Entry
        {
            public Entry(byte[] hash) { Hash = hash; }
            public byte[] Hash { get; }
            public bool Admitted { get; set; }
        }

        public enum Begin { New, AdmittedBefore, InFlight, DifferentContent, Full }

        /// <summary>Claims <paramref name="messageId"/> for a write with this content, or says what already holds it.</summary>
        public Begin TryBegin(string messageId, byte[] hash)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(messageId, out var entry))
                {
                    if (!CryptographicOperations.FixedTimeEquals(entry.Hash, hash)) return Begin.DifferentContent;
                    return entry.Admitted ? Begin.AdmittedBefore : Begin.InFlight;
                }

                if (_writes.Count >= MaxRememberedWrites) return Begin.Full;
                _writes[messageId] = new Entry(hash);
                return Begin.New;
            }
        }

        /// <summary>The write committed: remember it so a resend is re-receipted.</summary>
        public void Admitted(string messageId)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(messageId, out var entry)) entry.Admitted = true;
            }
        }

        /// <summary>The write was refused or unanswered: forget it so a corrected or repeated attempt is judged afresh.</summary>
        public void Abandoned(string messageId)
        {
            lock (_gate)
            {
                _writes.Remove(messageId);
            }
        }
    }

    /// <summary>
    /// The messageId of a schema frame that does not decode (write-receipt.md, rule 3: a refusal carries the write's id whenever
    /// the frame is readable enough to find it).
    /// </summary>
    internal static class CultNetReadableMessageId
    {
        /// <summary>The frame's top-level string <c>messageId</c>, or null when the bytes are not a map holding one.</summary>
        public static string? From(byte[] payload)
        {
            try
            {
                var reader = new MessagePackReader(payload);
                var entries = reader.ReadMapHeader();
                for (var index = 0; index < entries; index++)
                {
                    var key = reader.NextMessagePackType == MessagePackType.String ? reader.ReadString() : null;
                    if (key == "messageId" && reader.NextMessagePackType == MessagePackType.String)
                    {
                        var id = reader.ReadString();
                        return string.IsNullOrEmpty(id) ? null : id;
                    }

                    if (key == null) reader.Skip();
                    reader.Skip();
                }
            }
            catch (Exception)
            {
                // Not a readable map: no id.
            }

            return null;
        }
    }

    /// <summary>A resend the receipt session answered without applying it again, or refused as a reused id.</summary>
    internal sealed class CultNetWriteResendException : Exception
    {
        public CultNetWriteResendException(CultNetWriteReceiptSession.Begin outcome)
            : base("write answered from the connection's remembered writes")
        {
            Outcome = outcome;
        }

        public CultNetWriteReceiptSession.Begin Outcome { get; }
    }

    /// <summary>
    /// One write on a receipt-capable connection: its dedupe claim and its single answer (write-receipt.md, rules 1 to 5).
    /// </summary>
    internal sealed class CultNetReceiptedWrite
    {
        private readonly CultNetWriteReceiptSession _session;
        private readonly Action<ICultNetSchemaMessage> _send;
        private readonly string _operation;
        private readonly string _messageId;
        private readonly string _schemaId;
        private readonly string _recordKey;
        private readonly byte[]? _payload;
        private bool _claimed;
        private bool _answered;

        public CultNetReceiptedWrite(
            CultNetWriteReceiptSession session,
            Action<ICultNetSchemaMessage> send,
            string operation,
            string? messageId,
            string? schemaId,
            string? recordKey,
            byte[]? payload)
        {
            _session = session;
            _send = send;
            _operation = operation;
            _messageId = messageId ?? string.Empty;
            _schemaId = schemaId ?? string.Empty;
            _recordKey = recordKey ?? string.Empty;
            _payload = payload;
        }

        /// <summary>
        /// Wraps the server's authorization so the connection's remembered writes are consulted only after the actor is
        /// authorized: authorize first, then claim the id. A resend of an admitted write throws
        /// <see cref="CultNetWriteResendException"/> instead of applying it again.
        /// </summary>
        public Action<string, CultRecordKey> Guard(Action<string, CultRecordKey> authorize) => (schemaId, key) =>
        {
            authorize(schemaId, key);
            if (_messageId.Length == 0) return;
            var outcome = _session.TryBegin(_messageId, CultNetWriteContentHash.Compute(_operation, _schemaId, _recordKey, _payload));
            if (outcome == CultNetWriteReceiptSession.Begin.New)
            {
                _claimed = true;
                return;
            }

            throw new CultNetWriteResendException(outcome);
        };

        /// <summary>The write committed: remember it and send its one receipt.</summary>
        public void Admitted()
        {
            if (_answered) return;
            _answered = true;
            if (_messageId.Length > 0) _session.Admitted(_messageId);
            _send(new CultNetWriteAdmittedMessage
            {
                MessageId = _messageId,
                Operation = _operation,
                SchemaId = _schemaId,
                RecordKey = _recordKey
            });
        }

        /// <summary>The write was refused: forget its claim so a repeat is judged afresh, and send its one error.v1.</summary>
        public void Refused(CultNetErrorMessage error)
        {
            if (_answered) return;
            _answered = true;
            Release();
            _send(CultNetErrorV1Message.From(error, _messageId));
        }

        /// <summary>No answer is owed or possible (a forward the primary never answered): forget the claim, send nothing.</summary>
        public void Unanswered()
        {
            if (_answered) return;
            _answered = true;
            Release();
        }

        /// <summary>Answers a resend per rule 5.</summary>
        public void Resent(CultNetWriteResendException resend)
        {
            switch (resend.Outcome)
            {
                case CultNetWriteReceiptSession.Begin.AdmittedBefore:
                    _answered = true;
                    _send(new CultNetWriteAdmittedMessage
                    {
                        MessageId = _messageId,
                        Operation = _operation,
                        SchemaId = _schemaId,
                        RecordKey = _recordKey
                    });
                    break;
                case CultNetWriteReceiptSession.Begin.InFlight:
                    // The first attempt is still being applied and will answer this id.
                    _answered = true;
                    break;
                case CultNetWriteReceiptSession.Begin.DifferentContent:
                    Refused(CultNetErrorMessage.ForMessageInvalid("messageId", "the message id was already used for different content"));
                    break;
                default:
                    Refused(new CultNetErrorMessage { Error = "too many writes are remembered for this connection" });
                    break;
            }
        }

        /// <summary>Whether the write has had its answer (or has none owed).</summary>
        public bool Settled => _answered;

        /// <summary>A missing id cannot be answered under rule 1; refuse it with no id.</summary>
        public bool HasId => _messageId.Length > 0;

        private void Release()
        {
            if (_claimed) _session.Abandoned(_messageId);
            _claimed = false;
        }
    }
}
