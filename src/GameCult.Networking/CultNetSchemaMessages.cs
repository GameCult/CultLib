using System;
using MessagePack;

namespace GameCult.Networking
{
    /// <summary>
    /// Shared wire-contract identifiers understood by the CultNet family.
    /// </summary>
    public static class CultNetWireContracts
    {
        /// <summary>
        /// schema v0 contract identifier.
        /// </summary>
        public const string SchemaV0 = "cultnet.schema.v0";
        /// <summary>
        /// game cult networking v0 contract identifier.
        /// </summary>
        public const string GameCultNetworkingV0 = "gamecult.networking.v0";
    }

    /// <summary>
    /// Canonical schema-version strings for the modern CultNet message family.
    /// </summary>
    public static class CultNetSchemaVersions
    {
        /// <summary>
        /// hello contract identifier.
        /// </summary>
        public const string Hello = "cultnet.hello.v0";
        /// <summary>
        /// login contract identifier.
        /// </summary>
        public const string Login = "cultnet.login.v0";
        /// <summary>
        /// register contract identifier.
        /// </summary>
        public const string Register = "cultnet.register.v0";
        /// <summary>
        /// verify contract identifier.
        /// </summary>
        public const string Verify = "cultnet.verify.v0";
        /// <summary>
        /// login success contract identifier.
        /// </summary>
        public const string LoginSuccess = "cultnet.login_success.v0";
        /// <summary>
        /// error contract identifier.
        /// </summary>
        public const string Error = "cultnet.error.v0";
        /// <summary>
        /// sample change name contract identifier.
        /// </summary>
        public const string SampleChangeName = "cultnet.sample.change_name.v0";
        /// <summary>
        /// sample chat contract identifier.
        /// </summary>
        public const string SampleChat = "cultnet.sample.chat.v0";
        /// <summary>
        /// document delete contract identifier.
        /// </summary>
        public const string DocumentDelete = "cultnet.document_delete.v0";
        /// <summary>
        /// document put raw contract identifier.
        /// </summary>
        public const string DocumentPutRaw = "cultnet.document_put_raw.v0";
        /// <summary>
        /// snapshot request contract identifier.
        /// </summary>
        public const string SnapshotRequest = "cultnet.snapshot_request.v0";
        /// <summary>
        /// snapshot response raw contract identifier.
        /// </summary>
        public const string SnapshotResponseRaw = "cultnet.snapshot_response_raw.v0";
        /// <summary>
        /// schema catalog request contract identifier.
        /// </summary>
        public const string SchemaCatalogRequest = "cultnet.schema_catalog_request.v0";
        /// <summary>
        /// schema catalog response contract identifier.
        /// </summary>
        public const string SchemaCatalogResponse = "cultnet.schema_catalog_response.v0";
        /// <summary>
        /// database subscription request contract identifier.
        /// </summary>
        public const string DatabaseSubscribe = "cultnet.database_subscribe.v0";
        /// <summary>
        /// database subscription cancel contract identifier.
        /// </summary>
        public const string DatabaseUnsubscribe = "cultnet.database_unsubscribe.v0";
        /// <summary>
        /// database subscription change contract identifier.
        /// </summary>
        public const string DatabaseChangeRaw = "cultnet.database_change_raw.v0";
        /// <summary>
        /// shard catalog request contract identifier.
        /// </summary>
        public const string ShardCatalogRequest = "cultnet.shard_catalog_request.v0";
        /// <summary>
        /// shard catalog response contract identifier.
        /// </summary>
        public const string ShardCatalogResponse = "cultnet.shard_catalog_response.v0";
        /// <summary>
        /// shard mutation log request contract identifier.
        /// </summary>
        public const string ShardLogRequest = "cultnet.shard_log_request.v0";
        /// <summary>
        /// shard mutation log response contract identifier.
        /// </summary>
        public const string ShardLogResponse = "cultnet.shard_log_response.v0";
        /// <summary>
        /// simulation observation gossip contract identifier.
        /// </summary>
        public const string SimulationObservation = "cultnet.simulation_observation.v0";
        /// <summary>
        /// simulation consensus candidate gossip contract identifier.
        /// </summary>
        public const string SimulationConsensusCandidate = "cultnet.simulation_consensus_candidate.v0";
    }

    /// <summary>
    /// Mutation authority owner identifiers advertised through CultNet mutation contracts.
    /// </summary>
    public static class CultNetMutationAuthorities
    {
        /// <summary>
        /// The document is inspectable but not writable through CultNet.
        /// </summary>
        public const string ReadOnly = "readOnly";
        /// <summary>
        /// The local human operator may submit the corresponding mutation intent.
        /// </summary>
        public const string LocalUser = "localUser";
        /// <summary>
        /// A coordinator-owned Epiphany control surface owns the mutation path.
        /// </summary>
        public const string Coordinator = "coordinator";
        /// <summary>
        /// The runtime itself may write this document directly.
        /// </summary>
        public const string Runtime = "runtime";
        /// <summary>
        /// The document should not be mutated through CultNet.
        /// </summary>
        public const string Denied = "denied";
    }

    /// <summary>
    /// Declares how a document or surface may be inspected and mutated over CultNet.
    /// </summary>
    [MessagePackObject]
    public class CultNetDocumentMutationContract
    {
        /// <summary>
        /// Gets or sets the stable document type governed by this contract.
        /// </summary>
        [Key("documentType")] public string DocumentType { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the payload schema version when the contract governs a versioned document payload.
        /// </summary>
        [Key("payloadSchemaVersion")] public string? PayloadSchemaVersion { get; set; }
        /// <summary>
        /// Gets or sets the CultNet operations this document accepts.
        /// </summary>
        [Key("operations")] public string[] Operations { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets which actor is allowed to drive those operations.
        /// </summary>
        [Key("authority")] public string Authority { get; set; } = CultNetMutationAuthorities.ReadOnly;
        /// <summary>
        /// Gets or sets the intent document types a client may submit to request mutation.
        /// </summary>
        [Key("intentDocumentTypes")] public string[]? IntentDocumentTypes { get; set; }
        /// <summary>
        /// Gets or sets the receipt document types emitted after a successful mutation path.
        /// </summary>
        [Key("receiptDocumentTypes")] public string[]? ReceiptDocumentTypes { get; set; }
        /// <summary>
        /// Gets or sets freeform notes that explain local mutation guardrails or posture.
        /// </summary>
        [Key("notes")] public string[]? Notes { get; set; }
    }

    /// <summary>
    /// Marker interface for the explicit modern CultNet schema-v0 message family.
    /// </summary>
    public interface ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version carried by this message.
        /// </summary>
        string SchemaVersion { get; set; }
    }

    /// <summary>
    /// CultNet hello message used to announce a runtime and its supported contracts.
    /// </summary>
    [MessagePackObject]
    public class CultNetHelloMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Hello;
        /// <summary>
        /// Gets or sets the runtime id.
        /// </summary>
        [Key("runtimeId")] public string RuntimeId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the runtime kind.
        /// </summary>
        [Key("runtimeKind")] public string RuntimeKind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the agent id.
        /// </summary>
        [Key("agentId")] public string? AgentId { get; set; }
        /// <summary>
        /// Gets or sets the role.
        /// </summary>
        [Key("role")] public string? Role { get; set; }
        /// <summary>
        /// Gets or sets the display name.
        /// </summary>
        [Key("displayName")] public string? DisplayName { get; set; }
        /// <summary>
        /// Gets or sets the supported document types.
        /// </summary>
        [Key("supportedDocumentTypes")] public string[]? SupportedDocumentTypes { get; set; }
        /// <summary>
        /// Gets or sets the supported mutation contracts.
        /// </summary>
        [Key("supportedMutationContracts")] public CultNetDocumentMutationContract[]? SupportedMutationContracts { get; set; }
        /// <summary>
        /// Gets or sets the supported message versions.
        /// </summary>
        [Key("supportedMessageVersions")] public string[]? SupportedMessageVersions { get; set; }
        /// <summary>
        /// Gets or sets the supports schema catalog.
        /// </summary>
        [Key("supportsSchemaCatalog")] public bool SupportsSchemaCatalog { get; set; }
    }

    /// <summary>
    /// CultNet login request message.
    /// </summary>
    [MessagePackObject]
    public class CultNetLoginMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Login;
        /// <summary>
        /// Gets or sets the nonce.
        /// </summary>
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the auth.
        /// </summary>
        [Key("auth")] public string Auth { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the password.
        /// </summary>
        [Key("password")] public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// CultNet registration request message.
    /// </summary>
    [MessagePackObject]
    public class CultNetRegisterMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Register;
        /// <summary>
        /// Gets or sets the nonce.
        /// </summary>
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the email.
        /// </summary>
        [Key("email")] public string Email { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the password.
        /// </summary>
        [Key("password")] public string Password { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        [Key("name")] public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// CultNet session verification request message.
    /// </summary>
    [MessagePackObject]
    public class CultNetVerifyMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Verify;
        /// <summary>
        /// Gets or sets the nonce.
        /// </summary>
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the session.
        /// </summary>
        [Key("session")] public string Session { get; set; } = string.Empty;
    }

    /// <summary>
    /// CultNet message returned after successful session verification.
    /// </summary>
    [MessagePackObject]
    public class CultNetLoginSuccessMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.LoginSuccess;
        /// <summary>
        /// Gets or sets the nonce.
        /// </summary>
        [Key("nonce")] public string Nonce { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the session.
        /// </summary>
        [Key("session")] public string Session { get; set; } = string.Empty;
    }

    /// <summary>
    /// CultNet error response message.
    /// </summary>
    [MessagePackObject]
    public class CultNetErrorMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.Error;
        /// <summary>
        /// Gets or sets the error.
        /// </summary>
        [Key("error")] public string Error { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets optional shard routing details for authority failures.
        /// </summary>
        [Key("routingHint")] public CultNetShardRoutingHint? RoutingHint { get; set; }
    }

    /// <summary>
    /// Describes where a shard write should be routed.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardRoutingHint
    {
        /// <summary>
        /// Gets or sets the shard descriptor.
        /// </summary>
        [Key("shard")] public CultNetShardDescriptorMessage? Shard { get; set; }
        /// <summary>
        /// Gets or sets a machine-readable routing reason.
        /// </summary>
        [Key("reason")] public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample CultNet message used by development clients to change a display name.
    /// </summary>
    [MessagePackObject]
    public class CultNetSampleChangeNameMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SampleChangeName;
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        [Key("name")] public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Sample CultNet chat message used by development clients.
    /// </summary>
    [MessagePackObject]
    public class CultNetSampleChatMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SampleChat;
        /// <summary>
        /// Gets or sets the text.
        /// </summary>
        [Key("text")] public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Raw document record carried over CultNet.
    /// </summary>
    [MessagePackObject]
    public class CultNetRawDocumentRecord
    {
        /// <summary>
        /// Gets or sets the schema id.
        /// </summary>
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the record key.
        /// </summary>
        [Key("recordKey")] public string RecordKey { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the stored at.
        /// </summary>
        [Key("storedAt")] public string StoredAt { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the payload encoding.
        /// </summary>
        [Key("payloadEncoding")] public string PayloadEncoding { get; set; } = "messagepack";
        /// <summary>
        /// Gets or sets the payload.
        /// </summary>
        [Key("payload")] public byte[] Payload { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the source runtime id.
        /// </summary>
        [Key("sourceRuntimeId")] public string? SourceRuntimeId { get; set; }
        /// <summary>
        /// Gets or sets the source agent id.
        /// </summary>
        [Key("sourceAgentId")] public string? SourceAgentId { get; set; }
        /// <summary>
        /// Gets or sets the source role.
        /// </summary>
        [Key("sourceRole")] public string? SourceRole { get; set; }
        /// <summary>
        /// Gets or sets the tags.
        /// </summary>
        [Key("tags")] public string[]? Tags { get; set; }
    }

    /// <summary>
    /// CultNet message requesting deletion of a document record.
    /// </summary>
    [MessagePackObject]
    public class CultNetDocumentDeleteMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DocumentDelete;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the schema id.
        /// </summary>
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the record key.
        /// </summary>
        [Key("recordKey")] public string RecordKey { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard id the sender targeted.
        /// </summary>
        [Key("shardId")] public string? ShardId { get; set; }
        /// <summary>
        /// Gets or sets the shard epoch the sender targeted.
        /// </summary>
        [Key("shardEpoch")] public long? ShardEpoch { get; set; }
    }

    /// <summary>
    /// CultNet message carrying one raw document record.
    /// </summary>
    [MessagePackObject]
    public class CultNetDocumentPutRawMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DocumentPutRaw;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the document.
        /// </summary>
        [Key("document")] public CultNetRawDocumentRecord Document { get; set; } = new CultNetRawDocumentRecord();
        /// <summary>
        /// Gets or sets the shard id the sender targeted.
        /// </summary>
        [Key("shardId")] public string? ShardId { get; set; }
        /// <summary>
        /// Gets or sets the shard epoch the sender targeted.
        /// </summary>
        [Key("shardEpoch")] public long? ShardEpoch { get; set; }
    }

    /// <summary>
    /// CultNet message requesting a document snapshot.
    /// </summary>
    [MessagePackObject]
    public class CultNetSnapshotRequestMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SnapshotRequest;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the schema ids.
        /// </summary>
        [Key("schemaIds")] public string[]? SchemaIds { get; set; }
        /// <summary>
        /// Gets or sets the record keys.
        /// </summary>
        [Key("recordKeys")] public string[]? RecordKeys { get; set; }
    }

    /// <summary>
    /// CultNet message returning raw document records from a snapshot request.
    /// </summary>
    [MessagePackObject]
    public class CultNetSnapshotResponseRawMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SnapshotResponseRaw;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the documents.
        /// </summary>
        [Key("documents")] public CultNetRawDocumentRecord[] Documents { get; set; } = Array.Empty<CultNetRawDocumentRecord>();
    }

    /// <summary>
    /// Requests a live database subscription.
    /// </summary>
    [MessagePackObject]
    public class CultNetDatabaseSubscribeMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DatabaseSubscribe;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the live subscription id.
        /// </summary>
        [Key("subscriptionId")] public string SubscriptionId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets optional schema ids to watch.
        /// </summary>
        [Key("schemaIds")] public string[]? SchemaIds { get; set; }
        /// <summary>
        /// Gets or sets optional record keys to watch.
        /// </summary>
        [Key("recordKeys")] public string[]? RecordKeys { get; set; }
        /// <summary>
        /// Gets or sets whether the server should send a matching snapshot before live changes.
        /// </summary>
        [Key("includeSnapshot")] public bool IncludeSnapshot { get; set; } = true;
    }

    /// <summary>
    /// Cancels a live database subscription.
    /// </summary>
    [MessagePackObject]
    public class CultNetDatabaseUnsubscribeMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DatabaseUnsubscribe;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the live subscription id.
        /// </summary>
        [Key("subscriptionId")] public string SubscriptionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Streams one raw document change for a live database subscription.
    /// </summary>
    [MessagePackObject]
    public class CultNetDatabaseChangeRawMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.DatabaseChangeRaw;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the live subscription id.
        /// </summary>
        [Key("subscriptionId")] public string SubscriptionId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the change kind.
        /// </summary>
        [Key("changeKind")] public string ChangeKind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the current document for added or updated changes.
        /// </summary>
        [Key("document")] public CultNetRawDocumentRecord? Document { get; set; }
        /// <summary>
        /// Gets or sets the deleted record key for removed changes.
        /// </summary>
        [Key("recordKey")] public string? RecordKey { get; set; }
        /// <summary>
        /// Gets or sets the deleted schema id for removed changes.
        /// </summary>
        [Key("schemaId")] public string? SchemaId { get; set; }
    }

    /// <summary>
    /// Describes one shard advertised by a CultNet database node.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardDescriptorMessage
    {
        /// <summary>
        /// Gets or sets the shard id.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the owner runtime id.
        /// </summary>
        [Key("ownerRuntimeId")] public string OwnerRuntimeId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard authority epoch.
        /// </summary>
        [Key("epoch")] public long Epoch { get; set; }
        /// <summary>
        /// Gets or sets whether the responding node is primary for this shard.
        /// </summary>
        [Key("isPrimary")] public bool IsPrimary { get; set; }
        /// <summary>
        /// Gets or sets schema ids governed by this shard. Empty means all schemas.
        /// </summary>
        [Key("schemaIds")] public string[] SchemaIds { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets the record-key prefix governed by this shard.
        /// </summary>
        [Key("keyPrefix")] public string? KeyPrefix { get; set; }
        /// <summary>
        /// Gets or sets endpoints that accept authoritative writes.
        /// </summary>
        [Key("primaryEndpoints")] public string[] PrimaryEndpoints { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets authoritative replica endpoints.
        /// </summary>
        [Key("replicaEndpoints")] public string[] ReplicaEndpoints { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets low-latency read replica endpoints.
        /// </summary>
        [Key("readReplicaEndpoints")] public string[] ReadReplicaEndpoints { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets an optional locality label.
        /// </summary>
        [Key("region")] public string? Region { get; set; }
    }

    /// <summary>
    /// Requests shard ownership and routing metadata.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardCatalogRequestMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.ShardCatalogRequest;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets optional schema filters.
        /// </summary>
        [Key("schemaIds")] public string[]? SchemaIds { get; set; }
        /// <summary>
        /// Gets or sets optional record-key filters.
        /// </summary>
        [Key("recordKeys")] public string[]? RecordKeys { get; set; }
    }

    /// <summary>
    /// Returns shard ownership and routing metadata.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardCatalogResponseMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.ShardCatalogResponse;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets shard descriptors.
        /// </summary>
        [Key("shards")] public CultNetShardDescriptorMessage[] Shards { get; set; } = Array.Empty<CultNetShardDescriptorMessage>();
    }

    /// <summary>
    /// Requests accepted mutation log entries for a shard.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardLogRequestMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.ShardLogRequest;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard id.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard epoch expected by the requester.
        /// </summary>
        [Key("shardEpoch")] public long? ShardEpoch { get; set; }
        /// <summary>
        /// Gets or sets the last sequence already held by the requester.
        /// </summary>
        [Key("afterSequence")] public long AfterSequence { get; set; }
        /// <summary>
        /// Gets or sets the maximum number of entries to return.
        /// </summary>
        [Key("limit")] public int? Limit { get; set; }
    }

    /// <summary>
    /// Returns accepted mutation log entries for a shard.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardLogResponseMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.ShardLogResponse;
        /// <summary>
        /// Gets or sets the request id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard id.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard epoch.
        /// </summary>
        [Key("shardEpoch")] public long ShardEpoch { get; set; }
        /// <summary>
        /// Gets or sets mutation log entries.
        /// </summary>
        [Key("entries")] public CultNetShardLogEntryMessage[] Entries { get; set; } = Array.Empty<CultNetShardLogEntryMessage>();
        /// <summary>
        /// Gets or sets whether the requester must fall back to a shard snapshot.
        /// </summary>
        [Key("resyncRequired")] public bool ResyncRequired { get; set; }
        /// <summary>
        /// Gets or sets a machine-readable resync reason.
        /// </summary>
        [Key("reason")] public string? Reason { get; set; }
        /// <summary>
        /// Gets or sets the highest sequence compacted out of the retained log.
        /// </summary>
        [Key("compactedThrough")] public long? CompactedThrough { get; set; }
    }

    /// <summary>
    /// Describes one accepted shard mutation for replica catch-up.
    /// </summary>
    [MessagePackObject]
    public class CultNetShardLogEntryMessage
    {
        /// <summary>
        /// Gets or sets the per-shard sequence.
        /// </summary>
        [Key("sequence")] public long Sequence { get; set; }
        /// <summary>
        /// Gets or sets the commit timestamp.
        /// </summary>
        [Key("committedAt")] public string CommittedAt { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the mutation kind.
        /// </summary>
        [Key("changeKind")] public string ChangeKind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets a raw put message for added/updated entries.
        /// </summary>
        [Key("put")] public CultNetDocumentPutRawMessage? Put { get; set; }
        /// <summary>
        /// Gets or sets a raw delete message for removed entries.
        /// </summary>
        [Key("delete")] public CultNetDocumentDeleteMessage? Delete { get; set; }
    }

    /// <summary>
    /// Carries one witness observation about a simulation fact.
    /// </summary>
    [MessagePackObject]
    public class CultNetSimulationObservationMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SimulationObservation;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the observation.
        /// </summary>
        [Key("observation")] public CultNetSimulationObservation Observation { get; set; } = new CultNetSimulationObservation();
    }

    /// <summary>
    /// Carries one deterministic consensus candidate derived from witness observations.
    /// </summary>
    [MessagePackObject]
    public class CultNetSimulationConsensusCandidateMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SimulationConsensusCandidate;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard id.
        /// </summary>
        [Key("shardId")] public string ShardId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the shard epoch.
        /// </summary>
        [Key("shardEpoch")] public long ShardEpoch { get; set; }
        /// <summary>
        /// Gets or sets the simulation frame.
        /// </summary>
        [Key("frame")] public long Frame { get; set; }
        /// <summary>
        /// Gets or sets the subject id.
        /// </summary>
        [Key("subjectId")] public string SubjectId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the claim kind.
        /// </summary>
        [Key("claimKind")] public string ClaimKind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the selected claim hash.
        /// </summary>
        [Key("claimHash")] public string ClaimHash { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets optional claim detail.
        /// </summary>
        [Key("claimSummary")] public string? ClaimSummary { get; set; }
        /// <summary>
        /// Gets or sets distinct supporting witnesses.
        /// </summary>
        [Key("witnessCount")] public int WitnessCount { get; set; }
        /// <summary>
        /// Gets or sets supporting witness weight.
        /// </summary>
        [Key("supportWeight")] public double SupportWeight { get; set; }
        /// <summary>
        /// Gets or sets total observed witness weight.
        /// </summary>
        [Key("totalWeight")] public double TotalWeight { get; set; }
        /// <summary>
        /// Gets or sets whether the candidate crossed quorum.
        /// </summary>
        [Key("hasQuorum")] public bool HasQuorum { get; set; }
        /// <summary>
        /// Gets or sets support divided by total observed weight.
        /// </summary>
        [Key("confidence")] public double Confidence { get; set; }

        /// <summary>
        /// Creates a wire candidate message from a local candidate.
        /// </summary>
        public static CultNetSimulationConsensusCandidateMessage FromCandidate(
            string messageId,
            CultNetSimulationConsensusCandidate candidate)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            return new CultNetSimulationConsensusCandidateMessage
            {
                MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId,
                ShardId = candidate.ShardId,
                ShardEpoch = candidate.ShardEpoch,
                Frame = candidate.Frame,
                SubjectId = candidate.SubjectId,
                ClaimKind = candidate.ClaimKind,
                ClaimHash = candidate.ClaimHash,
                ClaimSummary = candidate.ClaimSummary,
                WitnessCount = candidate.WitnessCount,
                SupportWeight = candidate.SupportWeight,
                TotalWeight = candidate.TotalWeight,
                HasQuorum = candidate.HasQuorum,
                Confidence = candidate.Confidence
            };
        }
    }

    /// <summary>
    /// Describes one schema exposed through the CultNet schema catalog.
    /// </summary>
    [MessagePackObject]
    public class CultNetSchemaDescriptor
    {
        /// <summary>
        /// Gets or sets the schema id.
        /// </summary>
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the kind.
        /// </summary>
        [Key("kind")] public string Kind { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string? SchemaVersion { get; set; }
        /// <summary>
        /// Gets or sets the document type.
        /// </summary>
        [Key("documentType")] public string? DocumentType { get; set; }
        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        [Key("title")] public string? Title { get; set; }
        /// <summary>
        /// Gets or sets the wire contracts.
        /// </summary>
        [Key("wireContracts")] public string[] WireContracts { get; set; } = Array.Empty<string>();
        /// <summary>
        /// Gets or sets the content hash.
        /// </summary>
        [Key("contentHash")] public string ContentHash { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the schema json.
        /// </summary>
        [Key("schemaJson")] public string? SchemaJson { get; set; }
    }

    /// <summary>
    /// CultNet message requesting schema catalog entries.
    /// </summary>
    [MessagePackObject]
    public class CultNetSchemaCatalogRequestMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SchemaCatalogRequest;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the include schema json.
        /// </summary>
        [Key("includeSchemaJson")] public bool IncludeSchemaJson { get; set; }
        /// <summary>
        /// Gets or sets the schema ids.
        /// </summary>
        [Key("schemaIds")] public string[]? SchemaIds { get; set; }
        /// <summary>
        /// Gets or sets the kinds.
        /// </summary>
        [Key("kinds")] public string[]? Kinds { get; set; }
    }

    /// <summary>
    /// CultNet message returning schema catalog entries.
    /// </summary>
    [MessagePackObject]
    public class CultNetSchemaCatalogResponseMessage : ICultNetSchemaMessage
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        [Key("schemaVersion")] public string SchemaVersion { get; set; } = CultNetSchemaVersions.SchemaCatalogResponse;
        /// <summary>
        /// Gets or sets the message id.
        /// </summary>
        [Key("messageId")] public string MessageId { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the schemas.
        /// </summary>
        [Key("schemas")] public CultNetSchemaDescriptor[] Schemas { get; set; } = Array.Empty<CultNetSchemaDescriptor>();
    }
}
