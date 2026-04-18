using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Player data stored in the database.
    /// For MessagePack serialization, uses [MessagePackObject] and [Key(n)] attributes.
    /// </summary>
    public class PlayerData : DatabaseEntry, INamedEntry
    {

        /// <summary>
        /// Email address of the player.
        /// </summary>
        [Key(1)]
        public string Email;

        /// <summary>
        /// Password hash of the player.
        /// </summary>
        [Key(2)]
        public string PasswordHash;

        /// <summary>
        /// Username of the player.
        /// </summary>
        [Key(3)]
        public string Username;

        /// <inheritdoc/>
        [IgnoreMember]
        public string EntryName
        {
            get => Username;
            set => Username = value;
        }
    }
}