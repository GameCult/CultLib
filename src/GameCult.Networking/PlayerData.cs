using System;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Basic player account document used by the sample networking stack.
    /// </summary>
    [CultDocument("gamecult.player_data", "gamecult.player_data.v2")]
    public class PlayerData
    {
        /// <summary>
        /// Unique player identifier.
        /// </summary>
        [Key(0)]
        [CultIndex]
        public Guid PlayerId = Guid.NewGuid();

        /// <summary>
        /// Player email address.
        /// </summary>
        [Key(1)]
        [CultIndex]
        public string Email = string.Empty;

        /// <summary>
        /// Stored password hash.
        /// </summary>
        [Key(2)]
        public string PasswordHash = string.Empty;

        /// <summary>
        /// Player display name.
        /// </summary>
        [Key(3)]
        [CultName]
        public string Username = string.Empty;

        /// <summary>
        /// Monotonic session version used to supersede older issued tokens.
        /// </summary>
        [Key(4)]
        public long SessionVersion;
    }
}
