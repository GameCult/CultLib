using System;
using LiteNetLib;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Base type for all network messages exchanged by the client and server.
    /// </summary>
    [Union(0, typeof(LoginMessage)),
     Union(1, typeof(RegisterMessage)),
     Union(2, typeof(VerifyMessage)),
     Union(3, typeof(LoginSuccessMessage)),
     Union(4, typeof(ErrorMessage)),
     Union(5, typeof(ChangeNameMessage)),
     Union(6, typeof(ChatMessage)),
     MessagePackObject]
    public abstract class Message
    {
        /// <summary>
        /// Gets or sets the peer associated with the received message.
        /// </summary>
        [IgnoreMember] public NetPeer? Peer { get; set; }
    }

    /// <summary>
    /// Base type for encrypted messages that carry a nonce.
    /// </summary>
    public class EncryptedMessage : Message
    {
        /// <summary>
        /// Nonce used for AES-GCM encryption of the message payload.
        /// </summary>
        [Key(0)] public byte[] Nonce = Array.Empty<byte>();
    }

    /// <summary>
    /// Requests authentication with an email or username and password.
    /// </summary>
    [MessagePackObject]
    public class LoginMessage : EncryptedMessage
    {
        /// <summary>
        /// Encrypted email address or username.
        /// </summary>
        [Key(1)] public byte[] Auth = Array.Empty<byte>();

        /// <summary>
        /// Encrypted password payload.
        /// </summary>
        [Key(2)] public byte[] Password = Array.Empty<byte>();
    }

    /// <summary>
    /// Requests registration of a new player account.
    /// </summary>
    [MessagePackObject]
    public class RegisterMessage : EncryptedMessage
    {
        /// <summary>
        /// Encrypted email address.
        /// </summary>
        [Key(1)] public byte[] Email = Array.Empty<byte>();

        /// <summary>
        /// Encrypted password payload.
        /// </summary>
        [Key(2)] public byte[] Password = Array.Empty<byte>();

        /// <summary>
        /// Encrypted display name payload.
        /// </summary>
        [Key(3)] public byte[] Name = Array.Empty<byte>();
    }

    /// <summary>
    /// Attempts to restore a previously issued session.
    /// </summary>
    [MessagePackObject]
    public class VerifyMessage : EncryptedMessage
    {
        /// <summary>
        /// Encrypted session identifier.
        /// </summary>
        [Key(1)] public byte[] Session = Array.Empty<byte>();
    }

    /// <summary>
    /// Indicates successful authentication or verification.
    /// </summary>
    [MessagePackObject]
    public class LoginSuccessMessage : EncryptedMessage
    {
        /// <summary>
        /// Encrypted session identifier.
        /// </summary>
        [Key(1)] public byte[] Session = Array.Empty<byte>();
    }

    /// <summary>
    /// Sends a non-success error back to the client.
    /// </summary>
    [MessagePackObject]
    public class ErrorMessage : Message
    {
        /// <summary>
        /// Human-readable error text.
        /// </summary>
        [Key(0)] public string Error = string.Empty;
    }

    /// <summary>
    /// Requests a username change for the authenticated user.
    /// </summary>
    [MessagePackObject]
    public class ChangeNameMessage : Message
    {
        /// <summary>
        /// Requested new username.
        /// </summary>
        [Key(0)] public string Name = string.Empty;
    }

    /// <summary>
    /// Carries plain chat text.
    /// </summary>
    [MessagePackObject]
    public class ChatMessage : Message
    {
        /// <summary>
        /// Chat text content.
        /// </summary>
        [Key(0)] public string Text = string.Empty;
    }
}
