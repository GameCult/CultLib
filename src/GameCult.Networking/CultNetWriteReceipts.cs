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

    /// <summary>A SHA-256 digest held by value: the fixed-size stand-in for an id or a content hash.</summary>
    internal readonly struct CultNetDigest : IEquatable<CultNetDigest>
    {
        private readonly ulong _a, _b, _c, _d;

        private CultNetDigest(ulong a, ulong b, ulong c, ulong d) { _a = a; _b = b; _c = c; _d = d; }

        public static CultNetDigest Of(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            return new CultNetDigest(
                BitConverter.ToUInt64(hash, 0), BitConverter.ToUInt64(hash, 8),
                BitConverter.ToUInt64(hash, 16), BitConverter.ToUInt64(hash, 24));
        }

        public bool Equals(CultNetDigest other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
        public override bool Equals(object? obj) => obj is CultNetDigest other && Equals(other);
        public override int GetHashCode() => unchecked((int)_a ^ (int)(_a >> 32));
        public static bool operator ==(CultNetDigest left, CultNetDigest right) => left.Equals(right);
        public static bool operator !=(CultNetDigest left, CultNetDigest right) => !left.Equals(right);
    }

    /// <summary>
    /// What a session keeps of a refusal (write-receipt.md, What is remembered): its code, the field its details name, and its
    /// routing hint (a shard descriptor the server owns). Never the write's strings or an authority's reason: the replay is built
    /// from fixed text and the fields of the resend, which carries the same content.
    /// </summary>
    internal readonly struct CultNetRememberedRefusal
    {
        public string? Code { get; }
        public bool HasDetails { get; }
        public string? Field { get; }
        public CultNetShardRoutingHint? Hint { get; }

        public CultNetRememberedRefusal(string? code, bool hasDetails, string? field, CultNetShardRoutingHint? hint)
        {
            Code = code; HasDetails = hasDetails; Field = field; Hint = hint;
        }

        public static CultNetRememberedRefusal Of(CultNetErrorMessage error) =>
            new(error.Code, error.Details != null, error.Details?.Field, error.RoutingHint);

        private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

        /// <summary>The refusal, in fixed text, naming the write's own record key or schema id where its details do.</summary>
        public CultNetErrorMessage ToError(string schemaId, string recordKey) => new()
        {
            Error = Code switch
            {
                "unowned_schema" => "unowned_schema: no shard owns the schema and key",
                null => Hint != null ? CultNetDatabaseServer.RoutingRefusalText : CultNetWriteReceiptSession.WriteFailedText,
                _ => Code
            },
            Code = Code,
            RoutingHint = Hint,
            Details = HasDetails
                ? new CultNetErrorDetails
                {
                    Field = Field,
                    Value = Field switch { "recordKey" => NullIfEmpty(recordKey), "schemaId" => NullIfEmpty(schemaId), _ => null }
                }
                : null
        };
    }

    /// <summary>
    /// One receipt-capable peer's writes: message id to content hash and the answer given, refusals included, so a resend of the
    /// same content is answered again without being decided again and the same id with other content is refused. Ids and content
    /// hashes are held as fixed-size digests; the answer is held as its kind (and, for a refusal, its code and the field its details name),
    /// never the strings of the write.
    /// </summary>
    internal sealed class CultNetWriteReceiptSession
    {
        /// <summary>The most writes a peer is remembered for, refused ones included; a peer at the limit has new writes refused.</summary>
        internal const int MaxRememberedWrites = 1024;

        /// <summary>The most ids refused at the limit that are remembered, so their resend is judged the same way.</summary>
        internal const int MaxRememberedLimitRefusals = 1024;

        /// <summary>The longest messageId a write may carry, in UTF-8 bytes (write-receipt.md, Id size).</summary>
        internal const int MaxMessageIdBytes = 256;

        /// <summary>What a write is refused with when it failed for a reason no typed refusal names. Never an exception's own text.</summary>
        internal const string WriteFailedText = "the write could not be applied";

        private readonly object _gate = new();
        private readonly Dictionary<CultNetDigest, Entry> _writes = new();
        private readonly Dictionary<CultNetDigest, CultNetDigest> _limitRefusals = new();

        private sealed class Entry
        {
            public Entry(CultNetDigest hash) { Hash = hash; }
            public CultNetDigest Hash { get; set; }
            public bool Abandoned { get; set; }
            public bool Admitted { get; set; }
            public CultNetRememberedRefusal? Refusal { get; set; }
        }

        public enum Begin { New, ReplayAdmitted, ReplayRefusal, Unanswered, DifferentContent, Full }

        private static CultNetDigest IdDigest(string messageId) => CultNetDigest.Of(System.Text.Encoding.UTF8.GetBytes(messageId));

        /// <summary>
        /// Looks <paramref name="messageId"/> up first. An id not seen is claimed for this content (<see cref="Begin.New"/>);
        /// otherwise the outcome says what already holds it, and <paramref name="refusal"/> is the refusal to repeat.
        /// </summary>
        public Begin TryBegin(string messageId, byte[] hash, out CultNetRememberedRefusal? refusal)
        {
            var id = IdDigest(messageId);
            var content = CultNetDigest.Of(hash);
            lock (_gate)
            {
                refusal = null;
                if (_writes.TryGetValue(id, out var entry))
                {
                    if (entry.Abandoned)
                    {
                        // Not sent, so judged afresh in the slot it already holds; no slot is ever freed.
                        entry.Abandoned = false;
                        entry.Hash = content;
                        return Begin.New;
                    }

                    if (entry.Hash != content) return Begin.DifferentContent;
                    if (entry.Admitted) return Begin.ReplayAdmitted;
                    if (entry.Refusal != null)
                    {
                        refusal = entry.Refusal;
                        return Begin.ReplayRefusal;
                    }

                    // No answer yet: the first attempt is still being applied, or its primary left it unanswered for good.
                    return Begin.Unanswered;
                }

                if (_writes.Count >= MaxRememberedWrites)
                {
                    if (_limitRefusals.TryGetValue(id, out var refused)) return refused == content ? Begin.Full : Begin.DifferentContent;
                    if (_limitRefusals.Count < MaxRememberedLimitRefusals) _limitRefusals[id] = content;
                    return Begin.Full;
                }

                _writes[id] = new Entry(content);
                return Begin.New;
            }
        }

        /// <summary>The write was admitted: remember it so a resend repeats the receipt.</summary>
        public void Admitted(string messageId)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(IdDigest(messageId), out var entry)) entry.Admitted = true;
            }
        }

        /// <summary>The write was refused: remember the refusal so a resend repeats it.</summary>
        public void Refused(string messageId, CultNetRememberedRefusal refusal)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(IdDigest(messageId), out var entry)) entry.Refusal = refusal;
            }
        }

        /// <summary>
        /// The write never reached anyone who could have applied it: a repeat is judged afresh. The entry keeps its slot, so
        /// forgetting a write never makes room for an id that was refused at the limit.
        /// </summary>
        public void Abandoned(string messageId)
        {
            lock (_gate)
            {
                if (_writes.TryGetValue(IdDigest(messageId), out var entry)) entry.Abandoned = true;
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
            var outcome = _session.TryBegin(_messageId, CultNetWriteContentHash.Compute(_operation, _schemaId, _recordKey, _payload), out var refusal);
            switch (outcome)
            {
                case CultNetWriteReceiptSession.Begin.New:
                    _claimed = true;
                    return true;
                case CultNetWriteReceiptSession.Begin.ReplayAdmitted:
                    _answered = true;
                    _send(AdmittedReceipt());
                    return false;
                case CultNetWriteReceiptSession.Begin.ReplayRefusal:
                    _answered = true;
                    _send(CultNetErrorV1Message.From(refusal!.Value.ToError(_schemaId, _recordKey), _messageId));
                    return false;
                case CultNetWriteReceiptSession.Begin.Unanswered:
                    // The first attempt is still being applied and will answer this id, or its primary left it unanswered:
                    // then the claim stays, so the id is never sent again on this session, and the write stays unconfirmed.
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

        private CultNetWriteAdmittedMessage AdmittedReceipt() => new()
        {
            MessageId = _messageId,
            Operation = _operation,
            SchemaId = _schemaId,
            RecordKey = _recordKey
        };

        /// <summary>The write committed: remember the receipt and send it.</summary>
        public void Admitted()
        {
            if (_answered) return;
            _answered = true;
            if (_claimed) _session.Admitted(_messageId);
            _send(AdmittedReceipt());
        }

        /// <summary>The write was refused: remember the refusal (a resend repeats it) and send its one error.v1.</summary>
        public void Refused(CultNetErrorMessage error)
        {
            if (_answered) return;
            _answered = true;
            if (_claimed)
            {
                // A remembered refusal is answered from what is remembered, so the replay is the refusal again.
                var kept = CultNetRememberedRefusal.Of(error);
                _session.Refused(_messageId, kept);
                error = kept.ToError(_schemaId, _recordKey);
            }

            _send(CultNetErrorV1Message.From(error, _messageId));
        }

        /// <summary>
        /// A forward that may have reached its primary and is unconfirmed (silence, reset, timeout, an ambiguous send): no answer is
        /// owed or possible, and the claim stays so the id is never forwarded again on this session. Sends nothing.
        /// </summary>
        public void LeftSilent()
        {
            if (_answered) return;
            _answered = true;
        }

        /// <summary>A forward that was provably not sent (connect failed, or the send failed before any byte was written): forget the claim, send nothing.</summary>
        public void NotSent()
        {
            if (_answered) return;
            _answered = true;
            if (_claimed) _session.Abandoned(_messageId);
            _claimed = false;
        }

        /// <summary>
        /// Why the write's id cannot be used at all, before anything is remembered: it is missing (refused with no id) or longer
        /// than <see cref="CultNetWriteReceiptSession.MaxMessageIdBytes"/> UTF-8 bytes. Null when the id is usable.
        /// </summary>
        public CultNetErrorMessage? IdRefusal =>
            _messageId.Length == 0
                ? CultNetErrorMessage.ForMessageInvalid("messageId", "a write needs a messageId")
                : System.Text.Encoding.UTF8.GetByteCount(_messageId) > CultNetWriteReceiptSession.MaxMessageIdBytes
                    ? CultNetErrorMessage.ForMessageInvalid("messageId", "messageId exceeds 256 bytes")
                    : null;
    }
}
