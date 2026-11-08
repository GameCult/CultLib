using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
            return SHA256.HashData(buffer.WrittenSpan);
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
}
