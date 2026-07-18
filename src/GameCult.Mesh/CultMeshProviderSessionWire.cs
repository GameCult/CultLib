using System;
using System.Globalization;
using GameCult.Networking;
using MessagePack;

namespace GameCult.Mesh
{
    /// <summary>
    /// Canonical CultNet service, operation, payload-schema, and status identifiers for
    /// the CultMesh provider-session control plane.
    /// </summary>
    public static class CultMeshProviderSessionWireContract
    {
        /// <summary>The CultNet service id.</summary>
        public const string ServiceId = "gamecult.mesh.provider_session";

        /// <summary>The registration operation.</summary>
        public const string RegisterOperation = "provider.register";
        /// <summary>The lease-renewal operation.</summary>
        public const string RenewOperation = "provider.renew";
        /// <summary>The publication-put operation.</summary>
        public const string PublicationPutOperation = "provider.publication.put";
        /// <summary>The publication-delete operation.</summary>
        public const string PublicationDeleteOperation = "provider.publication.delete";
        /// <summary>The command-receipt-put operation.</summary>
        public const string ReceiptPutOperation = "provider.receipt.put";
        /// <summary>The provider-withdrawal operation.</summary>
        public const string WithdrawOperation = "provider.withdraw";

        /// <summary>The registration payload schema.</summary>
        public const string RegistrationSchema = "gamecult.mesh.provider_registration.v1";
        /// <summary>The provider-lease payload schema.</summary>
        public const string LeaseSchema = "gamecult.mesh.provider_lease.v1";
        /// <summary>The lease-renewal payload schema.</summary>
        public const string LeaseRenewalSchema = "gamecult.mesh.provider_lease_renewal.v1";
        /// <summary>The publication-put payload schema.</summary>
        public const string PublicationPutSchema = "gamecult.mesh.provider_publication_put.v1";
        /// <summary>The publication-delete payload schema.</summary>
        public const string PublicationDeleteSchema = "gamecult.mesh.provider_publication_delete.v1";
        /// <summary>The broker-to-provider command document schema.</summary>
        public const string CommandSchema = "gamecult.mesh.provider_command.v1";
        /// <summary>The command-receipt-put payload schema.</summary>
        public const string ReceiptPutSchema = "gamecult.mesh.provider_receipt_put.v1";
        /// <summary>The provider-withdrawal payload schema.</summary>
        public const string WithdrawalSchema = "gamecult.mesh.provider_withdrawal.v1";
        /// <summary>The accepted-mutation response payload schema.</summary>
        public const string MutationAcceptanceSchema = "gamecult.mesh.provider_mutation_acceptance.v1";

        /// <summary>The successful application status.</summary>
        public const string OkStatus = "ok";
        /// <summary>The identity or generation conflict status.</summary>
        public const string ConflictStatus = "conflict";
        /// <summary>The expired-lease status.</summary>
        public const string ExpiredStatus = "expired";
        /// <summary>The authority-denied status.</summary>
        public const string DeniedStatus = "denied";
        /// <summary>The invalid-request status.</summary>
        public const string InvalidStatus = "invalid";
        /// <summary>The CultNet inner-payload encoding.</summary>
        public const string PayloadEncoding = "messagepack-base64";
    }

    /// <summary>
    /// Carries provider authorization evidence and a transport-owned connection
    /// generation in an RUDP Connect packet. The token is not a session id.
    /// </summary>
    [MessagePackObject]
    public sealed class CultMeshProviderConnectEvidenceWire
    {
        /// <summary>Gets or sets the fresh id minted for this physical connection.</summary>
        [Key("clientSessionId")] public string ClientSessionId { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional signed provider authorization token.</summary>
        [Key("sessionToken")] public string? SessionToken { get; set; }
    }

    /// <summary>Requests a fenced provider lease from the provider-session broker.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderRegistrationWire
    {
        /// <summary>Gets or sets the stable provider identity.</summary>
        [Key("providerId")] public string ProviderId { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider process generation.</summary>
        [Key("serviceInstanceId")] public string ServiceInstanceId { get; set; } = string.Empty;
        /// <summary>Gets or sets the advertised endpoint identity.</summary>
        [Key("endpointId")] public string EndpointId { get; set; } = string.Empty;
        /// <summary>Gets or sets the Verse identity.</summary>
        [Key("verseId")] public string VerseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the requested lease duration in milliseconds.</summary>
        [Key("requestedLeaseDurationMs")] public int RequestedLeaseDurationMs { get; set; }
        /// <summary>Gets or sets the optional authority lease authorizing registration.</summary>
        [Key("authorityLeaseId")] public string? AuthorityLeaseId { get; set; }
    }

    /// <summary>Identifies the provider generation authorized by a broker lease.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderLeaseWire
    {
        /// <summary>Gets or sets the stable provider identity.</summary>
        [Key("providerId")] public string ProviderId { get; set; } = string.Empty;
        /// <summary>Gets or sets the authorized provider process generation.</summary>
        [Key("serviceInstanceId")] public string ServiceInstanceId { get; set; } = string.Empty;
        /// <summary>Gets or sets the authorized endpoint identity.</summary>
        [Key("endpointId")] public string EndpointId { get; set; } = string.Empty;
        /// <summary>Gets or sets the authorized Verse identity.</summary>
        [Key("verseId")] public string VerseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the unique fencing lease id.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the RFC3339 lease validity start.</summary>
        [Key("validFromUtc")] public string ValidFromUtc { get; set; } = string.Empty;
        /// <summary>Gets or sets the RFC3339 lease expiry.</summary>
        [Key("expiresAtUtc")] public string ExpiresAtUtc { get; set; } = string.Empty;
    }

    /// <summary>Requests replacement of an active provider lease.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderLeaseRenewalWire
    {
        /// <summary>Gets or sets the lease being replaced.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the requested replacement duration in milliseconds.</summary>
        [Key("requestedLeaseDurationMs")] public int RequestedLeaseDurationMs { get; set; }
    }

    /// <summary>Publishes one typed document under an active provider lease.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderPublicationPutWire
    {
        /// <summary>Gets or sets the active provider lease.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider-owned publication identity.</summary>
        [Key("publicationId")] public string PublicationId { get; set; } = string.Empty;
        /// <summary>Gets or sets the exact typed CultNet document.</summary>
        [Key("document")] public CultNetRawDocumentRecord Document { get; set; } = new CultNetRawDocumentRecord();
    }

    /// <summary>Deletes one exact provider-owned publication tuple.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderPublicationDeleteWire
    {
        /// <summary>Gets or sets the active provider lease.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider-owned publication identity.</summary>
        [Key("publicationId")] public string PublicationId { get; set; } = string.Empty;
        /// <summary>Gets or sets the exact published schema id.</summary>
        [Key("schemaId")] public string SchemaId { get; set; } = string.Empty;
        /// <summary>Gets or sets the exact published record key.</summary>
        [Key("recordKey")] public string RecordKey { get; set; } = string.Empty;
    }

    /// <summary>A retained command routed from the broker to one provider generation.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderCommandWire
    {
        /// <summary>Gets or sets the stable command identity.</summary>
        [Key("commandId")] public string CommandId { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider-defined command kind.</summary>
        [Key("commandKind")] public string CommandKind { get; set; } = string.Empty;
        /// <summary>Gets or sets the target provider identity.</summary>
        [Key("providerId")] public string ProviderId { get; set; } = string.Empty;
        /// <summary>Gets or sets the target provider process generation.</summary>
        [Key("serviceInstanceId")] public string ServiceInstanceId { get; set; } = string.Empty;
        /// <summary>Gets or sets the typed command payload value.</summary>
        [Key("payload")] public object? Payload { get; set; }
    }

    /// <summary>The exactly-once outcome of one provider command.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderCommandReceiptWire
    {
        /// <summary>Gets or sets the stable receipt identity.</summary>
        [Key("receiptId")] public string ReceiptId { get; set; } = string.Empty;
        /// <summary>Gets or sets the completed command identity.</summary>
        [Key("commandId")] public string CommandId { get; set; } = string.Empty;
        /// <summary>Gets or sets the completed command kind.</summary>
        [Key("commandKind")] public string CommandKind { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider identity.</summary>
        [Key("providerId")] public string ProviderId { get; set; } = string.Empty;
        /// <summary>Gets or sets the provider process generation.</summary>
        [Key("serviceInstanceId")] public string ServiceInstanceId { get; set; } = string.Empty;
        /// <summary>Gets or sets applied, rejected, or failed.</summary>
        [Key("state")] public string State { get; set; } = string.Empty;
        /// <summary>Gets or sets the RFC3339 completion time.</summary>
        [Key("completedAtUtc")] public string CompletedAtUtc { get; set; } = string.Empty;
        /// <summary>Gets or sets the optional typed result value.</summary>
        [Key("result")] public object? Result { get; set; }
        /// <summary>Gets or sets the optional bounded failure detail.</summary>
        [Key("error")] public string? Error { get; set; }
    }

    /// <summary>Submits a durable command receipt under an active provider lease.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderReceiptPutWire
    {
        /// <summary>Gets or sets the active provider lease.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
        /// <summary>Gets or sets the durable command receipt.</summary>
        [Key("receipt")] public CultMeshProviderCommandReceiptWire Receipt { get; set; } = new CultMeshProviderCommandReceiptWire();
    }

    /// <summary>Withdraws the provider generation identified by a lease.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderWithdrawalWire
    {
        /// <summary>Gets or sets the lease whose owned publications must be withdrawn.</summary>
        [Key("leaseId")] public string LeaseId { get; set; } = string.Empty;
    }

    /// <summary>Confirms broker acceptance of one provider-session mutation.</summary>
    [MessagePackObject]
    public sealed class CultMeshProviderMutationAcceptanceWire
    {
        /// <summary>Gets or sets the RFC3339 broker acceptance time.</summary>
        [Key("acceptedAtUtc")] public string AcceptedAtUtc { get; set; } = string.Empty;
        /// <summary>Gets or sets the accepted lease identity, when applicable.</summary>
        [Key("leaseId")] public string? LeaseId { get; set; }
        /// <summary>Gets or sets the accepted publication identity, when applicable.</summary>
        [Key("publicationId")] public string? PublicationId { get; set; }
        /// <summary>Gets or sets the accepted command identity, when applicable.</summary>
        [Key("commandId")] public string? CommandId { get; set; }
        /// <summary>Gets or sets the accepted receipt identity, when applicable.</summary>
        [Key("receiptId")] public string? ReceiptId { get; set; }
    }

    /// <summary>
    /// Encodes provider-session payloads inside the existing CultNet operation envelope.
    /// Transport acknowledgement is deliberately not treated as application acceptance.
    /// </summary>
    public static class CultMeshProviderSessionWire
    {
        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>Encodes one validated provider-session value as MessagePack base64.</summary>
        public static string EncodePayload<T>(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            Validate(value);
            return Convert.ToBase64String(MessagePackSerializer.Serialize(value, Options));
        }

        /// <summary>Decodes and validates one MessagePack-base64 provider-session value.</summary>
        public static T DecodePayload<T>(string payload)
        {
            RequireText(payload, nameof(payload));
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("Provider-session payload must be MessagePack base64.", nameof(payload), exception);
            }

            var value = MessagePackSerializer.Deserialize<T>(bytes, Options);
            if (value == null) throw new MessagePackSerializationException("Provider-session payload decoded to null.");
            Validate(value);
            return value;
        }

        /// <summary>Encodes provider RUDP Connect evidence as a named MessagePack map.</summary>
        public static byte[] EncodeConnectEvidence(CultMeshProviderConnectEvidenceWire evidence)
        {
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            Validate(evidence);
            return MessagePackSerializer.Serialize(evidence, Options);
        }

        /// <summary>Decodes and validates provider RUDP Connect evidence.</summary>
        public static CultMeshProviderConnectEvidenceWire DecodeConnectEvidence(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var evidence = MessagePackSerializer.Deserialize<CultMeshProviderConnectEvidenceWire>(payload, Options);
            if (evidence == null) throw new MessagePackSerializationException("Provider Connect evidence decoded to null.");
            Validate(evidence);
            return evidence;
        }

        /// <summary>Decodes a retained provider command carried as a raw CultNet document.</summary>
        public static CultMeshProviderCommandWire DecodeCommandDocument(CultNetRawDocumentRecord document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!string.Equals(document.SchemaId, CultMeshProviderSessionWireContract.CommandSchema, StringComparison.Ordinal))
                throw new ArgumentException($"Expected provider command schema '{CultMeshProviderSessionWireContract.CommandSchema}'.", nameof(document));
            if (!string.Equals(document.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                throw new ArgumentException("Provider command document must use MessagePack.", nameof(document));
            var command = MessagePackSerializer.Deserialize<CultMeshProviderCommandWire>(document.Payload, Options);
            if (command == null) throw new MessagePackSerializationException("Provider command decoded to null.");
            Validate(command);
            return command;
        }

        /// <summary>Creates a canonical provider-session CultNet operation request.</summary>
        public static CultNetOperationRequestMessage CreateRequest<T>(
            string messageId,
            string operation,
            string payloadSchema,
            T payload,
            string? sourceRuntimeId = null,
            string? targetRuntimeId = null)
        {
            RequireText(messageId, nameof(messageId));
            ValidateOperation(operation, payloadSchema);
            return new CultNetOperationRequestMessage
            {
                MessageId = messageId,
                ServiceId = CultMeshProviderSessionWireContract.ServiceId,
                Operation = operation,
                PayloadSchema = payloadSchema,
                PayloadEncoding = CultMeshProviderSessionWireContract.PayloadEncoding,
                Payload = EncodePayload(payload),
                SourceRuntimeId = sourceRuntimeId,
                TargetRuntimeId = targetRuntimeId
            };
        }

        /// <summary>Validates an operation request envelope and decodes its typed payload.</summary>
        public static T DecodeRequest<T>(
            CultNetOperationRequestMessage request,
            string expectedOperation,
            string expectedPayloadSchema)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            RequireEnvelope(request.ServiceId, request.Operation, request.PayloadSchema, request.PayloadEncoding,
                expectedOperation, expectedPayloadSchema);
            return DecodePayload<T>(request.Payload);
        }

        /// <summary>Creates a correlated application-level operation response.</summary>
        public static CultNetOperationResponseMessage CreateResponse<T>(
            CultNetOperationRequestMessage request,
            string status,
            string payloadSchema,
            T payload,
            string? sourceRuntimeId = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ValidateStatus(status);
            return new CultNetOperationResponseMessage
            {
                MessageId = RequireText(request.MessageId, nameof(request.MessageId)),
                ServiceId = CultMeshProviderSessionWireContract.ServiceId,
                Operation = RequireText(request.Operation, nameof(request.Operation)),
                Status = status,
                PayloadSchema = RequireText(payloadSchema, nameof(payloadSchema)),
                PayloadEncoding = CultMeshProviderSessionWireContract.PayloadEncoding,
                Payload = EncodePayload(payload),
                SourceRuntimeId = sourceRuntimeId
            };
        }

        /// <summary>Validates an operation response envelope and decodes its typed payload.</summary>
        public static T DecodeResponse<T>(
            CultNetOperationResponseMessage response,
            string expectedOperation,
            string expectedPayloadSchema)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            ValidateStatus(response.Status);
            RequireEnvelope(response.ServiceId, response.Operation, response.PayloadSchema, response.PayloadEncoding,
                expectedOperation, expectedPayloadSchema);
            return DecodePayload<T>(response.Payload);
        }

        /// <summary>Validates the invariants of a known provider-session wire value.</summary>
        public static void Validate<T>(T value)
        {
            switch (value)
            {
                case CultMeshProviderConnectEvidenceWire evidence:
                    RequireText(evidence.ClientSessionId, nameof(evidence.ClientSessionId));
                    RequireOptionalText(evidence.SessionToken, nameof(evidence.SessionToken));
                    break;
                case CultMeshProviderRegistrationWire registration:
                    RequireIdentity(registration.ProviderId, registration.ServiceInstanceId, registration.EndpointId, registration.VerseId);
                    RequirePositive(registration.RequestedLeaseDurationMs, nameof(registration.RequestedLeaseDurationMs));
                    RequireOptionalText(registration.AuthorityLeaseId, nameof(registration.AuthorityLeaseId));
                    break;
                case CultMeshProviderLeaseWire lease:
                    RequireIdentity(lease.ProviderId, lease.ServiceInstanceId, lease.EndpointId, lease.VerseId);
                    RequireText(lease.LeaseId, nameof(lease.LeaseId));
                    var validFrom = RequireTimestamp(lease.ValidFromUtc, nameof(lease.ValidFromUtc));
                    var expiresAt = RequireTimestamp(lease.ExpiresAtUtc, nameof(lease.ExpiresAtUtc));
                    if (expiresAt <= validFrom) throw new ArgumentException("Provider lease must expire after validFromUtc.");
                    break;
                case CultMeshProviderLeaseRenewalWire renewal:
                    RequireText(renewal.LeaseId, nameof(renewal.LeaseId));
                    RequirePositive(renewal.RequestedLeaseDurationMs, nameof(renewal.RequestedLeaseDurationMs));
                    break;
                case CultMeshProviderPublicationPutWire publication:
                    RequireText(publication.LeaseId, nameof(publication.LeaseId));
                    RequireText(publication.PublicationId, nameof(publication.PublicationId));
                    if (publication.Document == null) throw new ArgumentException("Provider publication document is required.");
                    RequireText(publication.Document.SchemaId, nameof(publication.Document.SchemaId));
                    RequireText(publication.Document.RecordKey, nameof(publication.Document.RecordKey));
                    if (!string.Equals(publication.Document.PayloadEncoding, "messagepack", StringComparison.Ordinal))
                        throw new ArgumentException("Provider publication document must use MessagePack.");
                    break;
                case CultMeshProviderPublicationDeleteWire deletion:
                    RequireText(deletion.LeaseId, nameof(deletion.LeaseId));
                    RequireText(deletion.PublicationId, nameof(deletion.PublicationId));
                    RequireText(deletion.SchemaId, nameof(deletion.SchemaId));
                    RequireText(deletion.RecordKey, nameof(deletion.RecordKey));
                    break;
                case CultMeshProviderCommandWire command:
                    RequireText(command.CommandId, nameof(command.CommandId));
                    RequireText(command.CommandKind, nameof(command.CommandKind));
                    RequireText(command.ProviderId, nameof(command.ProviderId));
                    RequireText(command.ServiceInstanceId, nameof(command.ServiceInstanceId));
                    break;
                case CultMeshProviderCommandReceiptWire receipt:
                    RequireText(receipt.ReceiptId, nameof(receipt.ReceiptId));
                    RequireText(receipt.CommandId, nameof(receipt.CommandId));
                    RequireText(receipt.CommandKind, nameof(receipt.CommandKind));
                    RequireText(receipt.ProviderId, nameof(receipt.ProviderId));
                    RequireText(receipt.ServiceInstanceId, nameof(receipt.ServiceInstanceId));
                    ValidateReceiptState(receipt.State);
                    RequireTimestamp(receipt.CompletedAtUtc, nameof(receipt.CompletedAtUtc));
                    RequireOptionalText(receipt.Error, nameof(receipt.Error));
                    break;
                case CultMeshProviderReceiptPutWire receiptPut:
                    RequireText(receiptPut.LeaseId, nameof(receiptPut.LeaseId));
                    if (receiptPut.Receipt == null) throw new ArgumentException("Provider receipt is required.");
                    Validate(receiptPut.Receipt);
                    break;
                case CultMeshProviderWithdrawalWire withdrawal:
                    RequireText(withdrawal.LeaseId, nameof(withdrawal.LeaseId));
                    break;
                case CultMeshProviderMutationAcceptanceWire acceptance:
                    RequireTimestamp(acceptance.AcceptedAtUtc, nameof(acceptance.AcceptedAtUtc));
                    RequireOptionalText(acceptance.LeaseId, nameof(acceptance.LeaseId));
                    RequireOptionalText(acceptance.PublicationId, nameof(acceptance.PublicationId));
                    RequireOptionalText(acceptance.CommandId, nameof(acceptance.CommandId));
                    RequireOptionalText(acceptance.ReceiptId, nameof(acceptance.ReceiptId));
                    break;
            }
        }

        private static void RequireEnvelope(
            string serviceId,
            string operation,
            string payloadSchema,
            string payloadEncoding,
            string expectedOperation,
            string expectedPayloadSchema)
        {
            if (!string.Equals(serviceId, CultMeshProviderSessionWireContract.ServiceId, StringComparison.Ordinal))
                throw new ArgumentException($"Expected provider-session service '{CultMeshProviderSessionWireContract.ServiceId}'.");
            if (!string.Equals(operation, expectedOperation, StringComparison.Ordinal))
                throw new ArgumentException($"Expected provider-session operation '{expectedOperation}'.");
            if (!string.Equals(payloadSchema, expectedPayloadSchema, StringComparison.Ordinal))
                throw new ArgumentException($"Expected provider-session payload schema '{expectedPayloadSchema}'.");
            if (!string.Equals(payloadEncoding, CultMeshProviderSessionWireContract.PayloadEncoding, StringComparison.Ordinal))
                throw new ArgumentException("Provider-session operation payload must use MessagePack base64.");
        }

        private static void ValidateOperation(string operation, string payloadSchema)
        {
            var expectedSchema = operation switch
            {
                CultMeshProviderSessionWireContract.RegisterOperation => CultMeshProviderSessionWireContract.RegistrationSchema,
                CultMeshProviderSessionWireContract.RenewOperation => CultMeshProviderSessionWireContract.LeaseRenewalSchema,
                CultMeshProviderSessionWireContract.PublicationPutOperation => CultMeshProviderSessionWireContract.PublicationPutSchema,
                CultMeshProviderSessionWireContract.PublicationDeleteOperation => CultMeshProviderSessionWireContract.PublicationDeleteSchema,
                CultMeshProviderSessionWireContract.ReceiptPutOperation => CultMeshProviderSessionWireContract.ReceiptPutSchema,
                CultMeshProviderSessionWireContract.WithdrawOperation => CultMeshProviderSessionWireContract.WithdrawalSchema,
                _ => throw new ArgumentException($"Unsupported provider-session operation '{operation}'.", nameof(operation))
            };
            if (!string.Equals(payloadSchema, expectedSchema, StringComparison.Ordinal))
                throw new ArgumentException($"Operation '{operation}' requires payload schema '{expectedSchema}'.", nameof(payloadSchema));
        }

        private static void ValidateStatus(string status)
        {
            if (status != CultMeshProviderSessionWireContract.OkStatus &&
                status != CultMeshProviderSessionWireContract.ConflictStatus &&
                status != CultMeshProviderSessionWireContract.ExpiredStatus &&
                status != CultMeshProviderSessionWireContract.DeniedStatus &&
                status != CultMeshProviderSessionWireContract.InvalidStatus)
                throw new ArgumentException($"Unsupported provider-session status '{status}'.", nameof(status));
        }

        private static void ValidateReceiptState(string state)
        {
            if (state != "applied" && state != "rejected" && state != "failed")
                throw new ArgumentException($"Unsupported provider receipt state '{state}'.", nameof(state));
        }

        private static void RequireIdentity(string providerId, string serviceInstanceId, string endpointId, string verseId)
        {
            RequireText(providerId, nameof(providerId));
            RequireText(serviceInstanceId, nameof(serviceInstanceId));
            RequireText(endpointId, nameof(endpointId));
            RequireText(verseId, nameof(verseId));
        }

        private static string RequireText(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value must be non-empty.", name);
            return value;
        }

        private static void RequireOptionalText(string? value, string name)
        {
            if (value != null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Optional value must be null or non-empty.", name);
        }

        private static void RequirePositive(int value, string name)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(name, "Value must be positive.");
        }

        private static DateTimeOffset RequireTimestamp(string value, string name)
        {
            RequireText(value, name);
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
                throw new ArgumentException("Value must be an RFC3339 timestamp.", name);
            return timestamp;
        }
    }
}
