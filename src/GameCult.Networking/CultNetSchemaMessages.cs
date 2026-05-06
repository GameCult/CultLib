using System;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Shared wire-contract identifiers understood by the CultNet family.
    /// </summary>
    public static class CultNetWireContracts
    {
        public const string SchemaV0 = "cultnet.schema.v0";
        public const string GameCultNetworkingV0 = "gamecult.networking.v0";
    }

    /// <summary>
    /// Canonical schema-version strings for the modern CultNet message family.
    /// </summary>
    public static class CultNetSchemaVersions
    {
        public const string Hello = "cultnet.hello.v0";
        public const string Login = "cultnet.login.v0";
        public const string Register = "cultnet.register.v0";
        public const string Verify = "cultnet.verify.v0";
        public const string LoginSuccess = "cultnet.login_success.v0";
        public const string Error = "cultnet.error.v0";
        public const string SampleChangeName = "cultnet.sample.change_name.v0";
        public const string SampleChat = "cultnet.sample.chat.v0";
        public const string DocumentDelete = "cultnet.document_delete.v0";
        public const string DocumentPutRaw = "cultnet.document_put_raw.v0";
        public const string SnapshotRequest = "cultnet.snapshot_request.v0";
        public const string SnapshotResponseRaw = "cultnet.snapshot_response_raw.v0";
        public const string SchemaCatalogRequest = "cultnet.schema_catalog_request.v0";
        public const string SchemaCatalogResponse = "cultnet.schema_catalog_response.v0";
    }

    /// <summary>
    /// Marker interface for the explicit modern CultNet schema-v0 message family.
    /// </summary>
    public interface ICultNetSchemaMessage
    {
        string SchemaVersion { get; set; }
    }

    [MessagePackObject]
    public class CultNetHelloMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Hello;
        [Key("runtimeId")] public string RuntimeId { get; set; } = string.Empty;
        [Key("runtimeKind")] public string RuntimeKind { get; set; } = string.Empty;
        [Key("agentId")] public string? AgentId { get; set; }
        [Key("role")] public string? Role { get; set; }
        [Key("displayName")] public string? DisplayName { get; set; }
        [Key("supportedDocumentTypes")] public string[]? SupportedDocumentTypes { get; set; }
        [Key("supportedMessageVersions")] public string[]? SupportedMessageVersions { get; set; }
        [Key("supportsSchemaCatalog")] public bool SupportsSchemaCatalog { get; set; }
    }

    [MessagePackObject]
    public class CultNetLoginMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Login;
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        [Key("auth")] public string Auth { get; set; } = string.Empty;
        [Key("password")] public string Password { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetRegisterMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Register;
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        [Key("email")] public string Email { get; set; } = string.Empty;
        [Key("password")] public string Password { get; set; } = string.Empty;
        [Key("name")] public string Name { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetVerifyMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Verify;
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        [Key("session")] public string Session { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetLoginSuccessMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.LoginSuccess;
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        [Key("session")] public string Session { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetErrorMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Error;
        [Key("error")] public string Error { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetSampleChangeNameMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SampleChangeName;
        [Key("name")] public string Name { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetSampleChatMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SampleChat;
        [Key("text")] public string Text { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetRawDocumentRecord
    {
        [Key("documentType")] public string DocumentType { get; set; } = string.Empty;
        [Key("documentKey")] public string DocumentKey { get; set; } = string.Empty;
        [Key("storedAt")] public string StoredAt { get; set; } = string.Empty;
        [Key("payloadSchemaVersion")] public string? PayloadSchemaVersion { get; set; }
        [Key("payloadEncoding")] public string PayloadEncoding { get; set; } = "messagepack";
        [Key("payload")] public byte[] Payload { get; set; } = Array.Empty<byte>();
        [Key("sourceRuntimeId")] public string? SourceRuntimeId { get; set; }
        [Key("sourceAgentId")] public string? SourceAgentId { get; set; }
        [Key("sourceRole")] public string? SourceRole { get; set; }
        [Key("tags")] public string[]? Tags { get; set; }
    }

    [MessagePackObject]
    public class CultNetDocumentDeleteMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DocumentDelete;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("documentType")] public string DocumentType { get; set; } = string.Empty;
        [Key("documentKey")] public string DocumentKey { get; set; } = string.Empty;
    }

    [MessagePackObject]
    public class CultNetDocumentPutRawMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DocumentPutRaw;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("document")] public CultNetRawDocumentRecord Document { get; set; } = new CultNetRawDocumentRecord();
    }

    [MessagePackObject]
    public class CultNetSnapshotRequestMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SnapshotRequest;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("documentTypes")] public string[]? DocumentTypes { get; set; }
        [Key("documentKeys")] public string[]? DocumentKeys { get; set; }
    }

    [MessagePackObject]
    public class CultNetSnapshotResponseRawMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SnapshotResponseRaw;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("documents")] public CultNetRawDocumentRecord[] Documents { get; set; } = Array.Empty<CultNetRawDocumentRecord>();
    }

    [MessagePackObject]
    public class CultNetSchemaDescriptor
    {
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        [Key("kind")] public string Kind { get; set; } = string.Empty;
        [Key("schemaVersion")] public string? SchemaVersion { get; set; }
        [Key("documentType")] public string? DocumentType { get; set; }
        [Key("title")] public string? Title { get; set; }
        [Key("wireContracts")] public string[] WireContracts { get; set; } = Array.Empty<string>();
        [Key("contentHash")] public string ContentHash { get; set; } = string.Empty;
        [Key("schemaJson")] public string? SchemaJson { get; set; }
    }

    [MessagePackObject]
    public class CultNetSchemaCatalogRequestMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SchemaCatalogRequest;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("includeSchemaJson")] public bool IncludeSchemaJson { get; set; }
        [Key("schemaIds")] public string[]? SchemaIds { get; set; }
        [Key("kinds")] public string[]? Kinds { get; set; }
    }

    [MessagePackObject]
    public class CultNetSchemaCatalogResponseMessage : ICultNetSchemaMessage
    {
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SchemaCatalogResponse;
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        [Key("schemas")] public CultNetSchemaDescriptor[] Schemas { get; set; } = Array.Empty<CultNetSchemaDescriptor>();
    }
}
