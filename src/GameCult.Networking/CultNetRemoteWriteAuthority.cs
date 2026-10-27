using System;
using GameCult.Caching;

namespace GameCult.Networking
{
    /// <summary>
    /// Decides whether a connected peer may put or delete a record in the database a
    /// <see cref="CultNetDatabaseServer"/> serves. Transport admission (the connection key, a completed handshake) is
    /// never a grant; this is the one owner of that decision.
    /// </summary>
    public interface ICultNetRemoteWriteAuthority
    {
        /// <summary>
        /// Decides one peer write. Called once per received put or delete, after the schema is resolved and before the
        /// shard is, so a refusal applies, publishes, logs and forwards nothing.
        /// </summary>
        CultNetRemoteWriteDecision Decide(CultNetRemoteWriteRequest request);
    }

    /// <summary>
    /// The kind of write a peer asked for.
    /// </summary>
    public enum CultNetRemoteWriteOperation
    {
        /// <summary>Put a record.</summary>
        Put,
        /// <summary>Delete a record.</summary>
        Delete
    }

    /// <summary>
    /// One peer write, as the authority sees it.
    /// </summary>
    public sealed class CultNetRemoteWriteRequest
    {
        /// <summary>
        /// Creates a request.
        /// </summary>
        public CultNetRemoteWriteRequest(
            CultNetServerPeer peer,
            CultNetRemoteWriteOperation operation,
            string schemaId,
            CultRecordKey recordKey,
            string? shardId,
            long? shardEpoch)
        {
            Peer = peer;
            Operation = operation;
            SchemaId = schemaId;
            RecordKey = recordKey;
            ShardId = shardId;
            ShardEpoch = shardEpoch;
        }

        /// <summary>Gets the peer connection the message arrived on (endpoint and transport only).</summary>
        public CultNetServerPeer Peer { get; }

        /// <summary>Gets the operation.</summary>
        public CultNetRemoteWriteOperation Operation { get; }

        /// <summary>Gets the canonical schema id the database resolved for the write, never the alias the peer sent.</summary>
        public string SchemaId { get; }

        /// <summary>Gets the record key.</summary>
        public CultRecordKey RecordKey { get; }

        /// <summary>Gets the shard id as the peer sent it.</summary>
        public string? ShardId { get; }

        /// <summary>Gets the shard epoch as the peer sent it.</summary>
        public long? ShardEpoch { get; }
    }

    /// <summary>
    /// An authority's answer to one <see cref="CultNetRemoteWriteRequest"/>.
    /// </summary>
    public sealed class CultNetRemoteWriteDecision
    {
        private CultNetRemoteWriteDecision(bool isGranted, string? reason)
        {
            IsGranted = isGranted;
            Reason = reason;
        }

        /// <summary>Gets whether the write may proceed.</summary>
        public bool IsGranted { get; }

        /// <summary>Gets the refusal reason, when there is one.</summary>
        public string? Reason { get; }

        /// <summary>Grants the write.</summary>
        public static CultNetRemoteWriteDecision Grant() => new(true, null);

        /// <summary>Refuses the write.</summary>
        public static CultNetRemoteWriteDecision Refuse(string? reason = null) => new(false, reason);
    }

    // Thrown by the server's authorize callback through the database's remote apply when the authority refuses; the
    // server's handlers are its one catcher.
    internal sealed class CultNetRemoteWriteRefusedException : Exception
    {
        public CultNetRemoteWriteRefusedException(CultNetRemoteWriteRequest request, string? reason)
            : base(reason ?? "the write was not authorized")
        {
            Request = request;
            Reason = reason;
        }

        public CultNetRemoteWriteRequest Request { get; }

        public string? Reason { get; }
    }
}
