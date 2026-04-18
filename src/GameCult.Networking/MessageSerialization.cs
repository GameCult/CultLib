using MessagePack;

namespace GameCult.Networking
{
    internal static class MessageSerialization
    {
        public static readonly MessagePackSerializerOptions Options =
            MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

        public static byte[] Serialize<T>(T message) where T : Message
        {
            return MessagePackSerializer.Serialize(message, Options);
        }

        public static T Deserialize<T>(byte[] payload)
        {
            return MessagePackSerializer.Deserialize<T>(payload, Options);
        }
    }
}
