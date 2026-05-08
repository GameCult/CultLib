using System;
using GameCult.Caching;
using MessagePack;

namespace GameCult.Networking
{
    [CultDocument("gamecult.player_data", "gamecult.player_data.v1")]
    public class PlayerData
    {
        [Key(0)]
        [CultIndex]
        public Guid PlayerId = Guid.NewGuid();

        [Key(1)]
        [CultIndex]
        public string Email = string.Empty;

        [Key(2)]
        public string PasswordHash = string.Empty;

        [Key(3)]
        [CultName]
        public string Username = string.Empty;
    }
}
