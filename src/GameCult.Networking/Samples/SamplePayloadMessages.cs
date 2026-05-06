using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Sample authenticated payload for changing a player-visible name.
    /// </summary>
    /// <remarks>
    /// This is example application payload riding the shared networking contract,
    /// not a claim that CultNet itself is spiritually about chat lobbies forever.
    /// Keep the wire tag stable if other runtimes depend on it.
    /// </remarks>
    [MessagePackObject]
    public class ChangeNameMessage : Message
    {
        /// <summary>
        /// Requested new username.
        /// </summary>
        [Key(0)] public string Name = string.Empty;
    }

    /// <summary>
    /// Sample plain chat payload.
    /// </summary>
    /// <remarks>
    /// This exists as a dead-simple reference payload for message extension and
    /// cross-runtime compatibility tests. Real applications should define their
    /// own domain messages and keep those contracts in sync across languages.
    /// </remarks>
    [MessagePackObject]
    public class ChatMessage : Message
    {
        /// <summary>
        /// Chat text content.
        /// </summary>
        [Key(0)] public string Text = string.Empty;
    }
}
