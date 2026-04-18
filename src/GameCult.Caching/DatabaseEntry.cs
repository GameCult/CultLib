using System;

namespace GameCult.Caching
{
    /// <summary>
    /// Interface for database entries that have a human-readable name.
    /// </summary>
    public interface INamedEntry
    {
        /// <summary>
        /// Human-readable name of the entry.
        /// </summary>
        string EntryName { get; set; }
    }

    /// <summary>
    /// Base abstract class for all database entries in the cache.
    /// For optional MessagePack integration, define concrete subclasses with [MessagePackObject] and [Key(n)] on fields.
    /// The ID field can be serialized via a custom formatter in MessagePack stores.
    /// </summary>
    public abstract class DatabaseEntry
    {
        /// <summary>
        /// Unique identifier for the entry. Handled by custom formatters in MessagePack integration.
        /// </summary>
        public Guid ID = Guid.NewGuid();

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return ID.GetHashCode();
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            if (obj is DatabaseEntry entry) return entry.ID == ID;
            return false;
        }
    }

    /// <summary>
    /// Generic link to a <see cref="DatabaseEntry"/> in the cache.
    /// Value is computed from the cache; serializes only LinkID.
    /// </summary>
    public class DatabaseLink<T> : DatabaseLinkBase where T : DatabaseEntry
    {
        /// <summary>
        /// Computed value from the cache (not serialized; regenerated post-deserialize).
        /// </summary>
        public T? Value => Cache?.Get<T>(LinkID);
    }

    /// <summary>
    /// Base for database links. LinkID is serialized directly; Value is computed in subclasses.
    /// </summary>
    public class DatabaseLinkBase
    {
        /// <summary>
        /// ID of the linked entry.
        /// </summary>
        public Guid LinkID;

        /// <summary>
        /// Static reference to the cache (not serialized).
        /// </summary>
        public static CultCache? Cache;
    }
}