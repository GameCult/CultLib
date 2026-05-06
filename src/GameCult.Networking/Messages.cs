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
     // Sample application payload tags preserved for cross-runtime compatibility.
     Union(5, typeof(ChangeNameMessage)),
     Union(6, typeof(ChatMessage)),
     Union(7, typeof(SchemaCatalogRequestMessage)),
     Union(8, typeof(SchemaCatalogResponseMessage)),
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
    /// Requests a catalog of schemas that the remote runtime can safely exchange.
    /// </summary>
    [MessagePackObject]
    public class SchemaCatalogRequestMessage : Message
    {
        /// <summary>
        /// Correlation identifier for the discovery request.
        /// </summary>
        [Key(0)] public string MessageId = string.Empty;

        /// <summary>
        /// Whether the remote runtime should include inline JSON schema bodies.
        /// </summary>
        [Key(1)] public bool IncludeSchemaJson;

        /// <summary>
        /// Optional schema-id filter.
        /// </summary>
        [Key(2)] public string[]? SchemaIds;

        /// <summary>
        /// Optional schema-kind filter.
        /// </summary>
        [Key(3)] public string[]? Kinds;
    }

    /// <summary>
    /// Describes one schema that the runtime knows how to exchange safely.
    /// </summary>
    [MessagePackObject]
    public class SchemaDescriptorMessage
    {
        /// <summary>
        /// Stable canonical schema identifier.
        /// </summary>
        [Key(0)] public string SchemaId = string.Empty;

        /// <summary>
        /// Discovery kind such as wire_message, document_payload, or shared_contract.
        /// </summary>
        [Key(1)] public string Kind = string.Empty;

        /// <summary>
        /// Optional runtime message schema version.
        /// </summary>
        [Key(2)] public string? SchemaVersion;

        /// <summary>
        /// Optional CultCache/CultNet document type name.
        /// </summary>
        [Key(3)] public string? DocumentType;

        /// <summary>
        /// Human-facing schema title.
        /// </summary>
        [Key(4)] public string? Title;

        /// <summary>
        /// Wire contracts that can transport this schema safely.
        /// </summary>
        [Key(5)] public string[] WireContracts = Array.Empty<string>();

        /// <summary>
        /// SHA-256 hash of the canonical schema JSON.
        /// </summary>
        [Key(6)] public string ContentHash = string.Empty;

        /// <summary>
        /// Optional canonical JSON Schema body as a string.
        /// </summary>
        [Key(7)] public string? SchemaJson;
    }

    /// <summary>
    /// Returns the remote runtime's schema catalog for safe exchange planning.
    /// </summary>
    [MessagePackObject]
    public class SchemaCatalogResponseMessage : Message
    {
        /// <summary>
        /// Correlation identifier matching the request.
        /// </summary>
        [Key(0)] public string MessageId = string.Empty;

        /// <summary>
        /// Discovered schemas available for safe exchange.
        /// </summary>
        [Key(1)] public SchemaDescriptorMessage[] Schemas = Array.Empty<SchemaDescriptorMessage>();
    }

}
