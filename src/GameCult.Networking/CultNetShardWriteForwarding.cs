using System.Threading.Tasks;

namespace GameCult.Networking
{
    /// <summary>
    /// Forwards shard writes to an authoritative owner.
    /// </summary>
    public interface ICultNetShardWriteForwarder
    {
        /// <summary>
        /// Forwards a raw document put to the shard owner.
        /// </summary>
        Task ForwardPutAsync(CultNetShardDescriptor shard, CultNetDocumentPutRawMessage message);

        /// <summary>
        /// Forwards a raw document delete to the shard owner.
        /// </summary>
        Task ForwardDeleteAsync(CultNetShardDescriptor shard, CultNetDocumentDeleteMessage message);
    }

    /// <summary>
    /// Options controlling shard write forwarding from a database server bridge.
    /// </summary>
    public sealed class CultNetDatabaseServerOptions
    {
        /// <summary>
        /// Gets or sets whether non-primary writes may be forwarded to shard owners.
        /// </summary>
        public bool ForwardNonPrimaryWrites { get; set; }

        /// <summary>
        /// Gets or sets the forwarder used when non-primary write forwarding is enabled.
        /// </summary>
        public ICultNetShardWriteForwarder? WriteForwarder { get; set; }
    }
}
