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
    /// One receipt-capable peer's writes: message id to content hash and the answer given, refusals included, so a resend of the
    /// same content is answered again without being decided again and the same id with other content is refused.
    /// </summary>
    internal sealed class CultNetWriteReceiptSession
    {
        /// <summary>The most writes a peer is remembered for, refused ones included; a peer at the limit has new writes refused.</summary>
        internal const int MaxRememberedWrites = 1024;

        /// <summary>What a write is refused with when it failed for a reason no typed refusal names. Never an exception's own text.</summary>
        internal const string WriteFailedText = "the write could not be applied";

        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _writes = new(StringComparer.Ordinal);

        private sealed class Entry
        {
            public Entry(byte[] hash) { Hash = hash; }
            public byte[] Hash { get; }
            public bool Silent { get; set; }
            public ICultNetSchemaMessage? Answer { get; set; }
        }

        public enum Begin { New, Replay, InFlight, Silent, DifferentContent, Full }

        /// <summary>
        /// Looks <paramref name="messageId"/> up first. An id not seen is claimed for this content (<see cref="Begin.New"/>);
        /// otherwise the outcome says what already holds it, and <paramref name="answer"/> is the answer to replay.
        /// </summary>
        public Begin TryBegin(string messageId, byte[] hash, out ICultNetSchemaMessage? answer)
        {
            lock (_gate)
            {
                answer = null;
                if (_writes.TryGetValue(messageId, out var entry))
                {
                    if (!CryptographicOperations.FixedTimeEquals(entry.Hash, hash)) return Begin.DifferentContent;
                    if (entry.Answer != null)
                    {
                        answer = entry.Answer;
                        return Begin.Replay;
                    }

                    return entry.Silent ? Begin.Silent : Begin.InFlight;
                }

                if (_writes.Count >= MaxRememberedWrites) return Begin.Full;
                _writes[messageId] = new Entry(hash);
                return Begin.New;
            }
        }

        /// <summary>The write was answered, admitted or refused: remember the answer so a resend repeats it.</summary>
        public void Answered(string messageId, ICultNetSchemaMessage answer)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(messageId, out var entry)) entry.Answer = answer;
            }
        }

        /// <summary>
        /// The primary left a forward unanswered: it may have applied it, so the id is never forwarded again on this session and
        /// a resend is answered with nothing (the client settles it unconfirmed).
        /// </summary>
        public void LeftSilent(string messageId)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(messageId, out var entry)) entry.Silent = true;
            }
        }

        /// <summary>The write never reached anyone who could have applied it: forget it so a repeat is judged afresh.</summary>
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
        /// Looks the write's id up before anything else is decided (rule 5). True when the id is new and the write should go on
        /// to authorization and the commit; false when it was answered here (a replay, a reused id, a full table) or is owed no
        /// further answer (still in flight, or left silent by its primary).
        /// </summary>
        public bool Begin()
        {
            var outcome = _session.TryBegin(_messageId, CultNetWriteContentHash.Compute(_operation, _schemaId, _recordKey, _payload), out var answer);
            switch (outcome)
            {
                case CultNetWriteReceiptSession.Begin.New:
                    _claimed = true;
                    return true;
                case CultNetWriteReceiptSession.Begin.Replay:
                    _answered = true;
                    _send(answer!);
                    return false;
                case CultNetWriteReceiptSession.Begin.InFlight:
                    // The first attempt is still being applied and will answer this id.
                    _answered = true;
                    return false;
                case CultNetWriteReceiptSession.Begin.Silent:
                    // The first attempt went to a primary that never answered; this id is never sent again, and stays unanswered.
                    _answered = true;
                    return false;
                case CultNetWriteReceiptSession.Begin.DifferentContent:
                    Refused(CultNetErrorMessage.ForMessageInvalid("messageId", "the message id was already used for different content"));
                    return false;
                default:
                    _answered = true;
                    _send(CultNetErrorV1Message.LimitReached(_messageId));
                    return false;
            }
        }

        /// <summary>The write committed: remember the receipt and send it.</summary>
        public void Admitted()
        {
            if (_answered) return;
            _answered = true;
            var receipt = new CultNetWriteAdmittedMessage
            {
                MessageId = _messageId,
                Operation = _operation,
                SchemaId = _schemaId,
                RecordKey = _recordKey
            };
            if (_claimed) _session.Answered(_messageId, receipt);
            _send(receipt);
        }

        /// <summary>The write was refused: remember the refusal (a resend repeats it) and send its one error.v1.</summary>
        public void Refused(CultNetErrorMessage error)
        {
            if (_answered) return;
            _answered = true;
            var refusal = CultNetErrorV1Message.From(error, _messageId);
            if (_claimed) _session.Answered(_messageId, refusal);
            _send(refusal);
        }

        /// <summary>
        /// A forward its primary left unanswered: no answer is owed or possible, and the id is never forwarded again on this
        /// session. Sends nothing.
        /// </summary>
        public void LeftSilent()
        {
            if (_answered) return;
            _answered = true;
            if (_claimed) _session.LeftSilent(_messageId);
        }

        /// <summary>A forward that never reached a primary that could have applied it: forget the claim, send nothing.</summary>
        public void Unanswered()
        {
            if (_answered) return;
            _answered = true;
            if (_claimed) _session.Abandoned(_messageId);
            _claimed = false;
        }

        /// <summary>A missing id cannot be answered under rule 1; refuse it with no id.</summary>
        public bool HasId => _messageId.Length > 0;
    }
}
