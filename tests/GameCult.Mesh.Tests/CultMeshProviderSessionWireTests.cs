using System;
using System.Collections.Generic;
using GameCult.Networking;
using MessagePack;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

[TestFixture]
public sealed class CultMeshProviderSessionWireTests
{
    private const string RegistrationGolden = "hqpwcm92aWRlcklkqGFldGhlcmlhsXNlcnZpY2VJbnN0YW5jZUlkq2FldGhlcmlhLTQyqmVuZHBvaW50SWSvYWV0aGVyaWEtcHVibGljp3ZlcnNlSWSmcHVibGljuHJlcXVlc3RlZExlYXNlRHVyYXRpb25Nc811MLBhdXRob3JpdHlMZWFzZUlkq2F1dGhvcml0eS03";
    private const string ConnectEvidenceGolden = "gq9jbGllbnRTZXNzaW9uSWSyYWV0aGVyaWEtY2xpZW50LTQyrHNlc3Npb25Ub2tlbrJvZGluLXNlc3Npb24tdG9rZW4=";
    private const string TokenlessConnectEvidenceGolden = "gq9jbGllbnRTZXNzaW9uSWSyYWV0aGVyaWEtY2xpZW50LTQyrHNlc3Npb25Ub2tlbsA=";

    [Test]
    public void ConnectEvidence_SeparatesTransportGenerationFromAuthorityToken()
    {
        var encoded = CultMeshProviderSessionWire.EncodeConnectEvidence(
            new CultMeshProviderConnectEvidenceWire
            {
                ClientSessionId = "aetheria-client-42",
                SessionToken = "odin-session-token"
            });
        var decoded = CultMeshProviderSessionWire.DecodeConnectEvidence(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToBase64String(encoded), Is.EqualTo(ConnectEvidenceGolden),
                "C#, TypeScript, and Rust must emit the same Connect evidence map");
            Assert.That(decoded.ClientSessionId, Is.EqualTo("aetheria-client-42"));
            Assert.That(decoded.SessionToken, Is.EqualTo("odin-session-token"));
            Assert.That(
                Convert.ToBase64String(CultMeshProviderSessionWire.EncodeConnectEvidence(
                    new CultMeshProviderConnectEvidenceWire { ClientSessionId = "aetheria-client-42" })),
                Is.EqualTo(TokenlessConnectEvidenceGolden));
        });
    }

    [Test]
    public void Registration_RoundTrips_InsideCanonicalCultNetOperationEnvelope()
    {
        var registration = new CultMeshProviderRegistrationWire
        {
            ProviderId = "aetheria",
            ServiceInstanceId = "aetheria-42",
            EndpointId = "aetheria-public",
            VerseId = "public",
            RequestedLeaseDurationMs = 30_000,
            AuthorityLeaseId = "authority-7"
        };

        var request = CultMeshProviderSessionWire.CreateRequest(
            "register-1",
            CultMeshProviderSessionWireContract.RegisterOperation,
            CultMeshProviderSessionWireContract.RegistrationSchema,
            registration,
            sourceRuntimeId: "aetheria-42",
            targetRuntimeId: "odin");
        var envelopeBytes = CultNetSchemaMessageSerialization.Serialize(request);
        var decodedEnvelope = (CultNetOperationRequestMessage)CultNetSchemaMessageSerialization.Deserialize(envelopeBytes);
        var decoded = CultMeshProviderSessionWire.DecodeRequest<CultMeshProviderRegistrationWire>(
            decodedEnvelope,
            CultMeshProviderSessionWireContract.RegisterOperation,
            CultMeshProviderSessionWireContract.RegistrationSchema);

        Assert.Multiple(() =>
        {
            Assert.That(CultMeshProviderSessionWire.EncodePayload(registration), Is.EqualTo(RegistrationGolden),
                "C# and TypeScript must emit the same canonical registration payload");
            Assert.That(decodedEnvelope.ServiceId, Is.EqualTo("gamecult.mesh.provider_session"));
            Assert.That(decodedEnvelope.PayloadEncoding, Is.EqualTo("messagepack-base64"));
            Assert.That(decoded.ProviderId, Is.EqualTo("aetheria"));
            Assert.That(decoded.ServiceInstanceId, Is.EqualTo("aetheria-42"));
            Assert.That(decoded.EndpointId, Is.EqualTo("aetheria-public"));
            Assert.That(decoded.VerseId, Is.EqualTo("public"));
            Assert.That(decoded.RequestedLeaseDurationMs, Is.EqualTo(30_000));
            Assert.That(decoded.AuthorityLeaseId, Is.EqualTo("authority-7"));
        });
    }

    [Test]
    public void PublicationPut_RoundTrips_ExactRawDocumentTuple()
    {
        var publication = new CultMeshProviderPublicationPutWire
        {
            LeaseId = "lease-1",
            PublicationId = "surface:pilot",
            Document = new CultNetRawDocumentRecord
            {
                SchemaId = "gamecult.eve.surface.v1",
                RecordKey = "eve:surface:pilot",
                StoredAt = "2026-07-14T12:00:00Z",
                PayloadEncoding = "messagepack",
                Payload = [0x81, 0xA2, 0x69, 0x64, 0x01],
                SourceRuntimeId = "aetheria-42"
            }
        };

        var encoded = CultMeshProviderSessionWire.EncodePayload(publication);
        var decoded = CultMeshProviderSessionWire.DecodePayload<CultMeshProviderPublicationPutWire>(encoded);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.LeaseId, Is.EqualTo("lease-1"));
            Assert.That(decoded.PublicationId, Is.EqualTo("surface:pilot"));
            Assert.That(decoded.Document.SchemaId, Is.EqualTo("gamecult.eve.surface.v1"));
            Assert.That(decoded.Document.RecordKey, Is.EqualTo("eve:surface:pilot"));
            Assert.That(decoded.Document.Payload, Is.EqualTo(publication.Document.Payload));
        });
    }

    [Test]
    public void CommandAndReceipt_UseCamelCaseMessagePackMaps()
    {
        var command = new CultMeshProviderCommandWire
        {
            CommandId = "cmd-7",
            CommandKind = "pilot.thrust",
            ProviderId = "aetheria",
            ServiceInstanceId = "aetheria-42",
            Payload = new Dictionary<string, object> { ["axis"] = 0.75 }
        };
        var receiptPut = new CultMeshProviderReceiptPutWire
        {
            LeaseId = "lease-1",
            Receipt = new CultMeshProviderCommandReceiptWire
            {
                ReceiptId = "receipt-7",
                CommandId = command.CommandId,
                CommandKind = command.CommandKind,
                ProviderId = command.ProviderId,
                ServiceInstanceId = command.ServiceInstanceId,
                State = "applied",
                CompletedAtUtc = "2026-07-14T12:01:02.345Z",
                Result = new Dictionary<string, object> { ["accepted"] = true }
            }
        };

        var encodedCommand = CultMeshProviderSessionWire.EncodePayload(command);
        var encodedReceipt = CultMeshProviderSessionWire.EncodePayload(receiptPut);
        var decodedCommand = CultMeshProviderSessionWire.DecodePayload<CultMeshProviderCommandWire>(encodedCommand);
        var decodedReceipt = CultMeshProviderSessionWire.DecodePayload<CultMeshProviderReceiptPutWire>(encodedReceipt);
        var decodedCommandDocument = CultMeshProviderSessionWire.DecodeCommandDocument(new CultNetRawDocumentRecord
        {
            SchemaId = CultMeshProviderSessionWireContract.CommandSchema,
            RecordKey = "provider-command:aetheria:aetheria-42:cmd-7",
            PayloadEncoding = "messagepack",
            Payload = Convert.FromBase64String(encodedCommand)
        });
        var commandJson = MessagePackSerializer.ConvertToJson(Convert.FromBase64String(encodedCommand));
        var receiptJson = MessagePackSerializer.ConvertToJson(Convert.FromBase64String(encodedReceipt));

        Assert.Multiple(() =>
        {
            Assert.That(commandJson, Does.Contain("\"commandId\""));
            Assert.That(commandJson, Does.Contain("\"commandKind\""));
            Assert.That(commandJson, Does.Contain("\"serviceInstanceId\""));
            Assert.That(commandJson, Does.Contain("\"payload\""));
            Assert.That(decodedCommand.CommandKind, Is.EqualTo("pilot.thrust"));
            Assert.That(decodedCommand.Payload, Is.Not.Null);
            Assert.That(decodedCommandDocument.CommandId, Is.EqualTo("cmd-7"));
            Assert.That(receiptJson, Does.Contain("\"completedAtUtc\""));
            Assert.That(receiptJson, Does.Contain("\"receiptId\""));
            Assert.That(receiptJson, Does.Contain("\"result\""));
            Assert.That(decodedReceipt.Receipt.State, Is.EqualTo("applied"));
            Assert.That(decodedReceipt.Receipt.Result, Is.Not.Null);
        });
    }

    [Test]
    public void LeaseResponse_RoundTrips_WithApplicationAcceptance()
    {
        var request = CultMeshProviderSessionWire.CreateRequest(
            "register-1",
            CultMeshProviderSessionWireContract.RegisterOperation,
            CultMeshProviderSessionWireContract.RegistrationSchema,
            new CultMeshProviderRegistrationWire
            {
                ProviderId = "aetheria",
                ServiceInstanceId = "aetheria-42",
                EndpointId = "public",
                VerseId = "verse",
                RequestedLeaseDurationMs = 10_000
            });
        var response = CultMeshProviderSessionWire.CreateResponse(
            request,
            CultMeshProviderSessionWireContract.OkStatus,
            CultMeshProviderSessionWireContract.LeaseSchema,
            new CultMeshProviderLeaseWire
            {
                ProviderId = "aetheria",
                ServiceInstanceId = "aetheria-42",
                EndpointId = "public",
                VerseId = "verse",
                LeaseId = "lease-2",
                ValidFromUtc = "2026-07-14T12:00:00Z",
                ExpiresAtUtc = "2026-07-14T12:00:10Z"
            },
            sourceRuntimeId: "odin");

        var lease = CultMeshProviderSessionWire.DecodeResponse<CultMeshProviderLeaseWire>(
            response,
            CultMeshProviderSessionWireContract.RegisterOperation,
            CultMeshProviderSessionWireContract.LeaseSchema);

        Assert.That(response.Status, Is.EqualTo("ok"));
        Assert.That(lease.LeaseId, Is.EqualTo("lease-2"));
        Assert.That(lease.ExpiresAtUtc, Is.EqualTo("2026-07-14T12:00:10Z"));
    }

    [Test]
    public void Validation_RejectsSplitAuthorityAndMalformedLifecycleState()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => CultMeshProviderSessionWire.CreateRequest(
                    "bad-schema",
                    CultMeshProviderSessionWireContract.RegisterOperation,
                    CultMeshProviderSessionWireContract.PublicationPutSchema,
                    new CultMeshProviderRegistrationWire()),
                Throws.ArgumentException);
            Assert.That(
                () => CultMeshProviderSessionWire.EncodePayload(new CultMeshProviderLeaseWire
                {
                    ProviderId = "provider",
                    ServiceInstanceId = "instance",
                    EndpointId = "endpoint",
                    VerseId = "verse",
                    LeaseId = "lease",
                    ValidFromUtc = "2026-07-14T12:00:10Z",
                    ExpiresAtUtc = "2026-07-14T12:00:00Z"
                }),
                Throws.ArgumentException);
            Assert.That(
                () => CultMeshProviderSessionWire.EncodePayload(new CultMeshProviderCommandReceiptWire
                {
                    ReceiptId = "receipt",
                    CommandId = "command",
                    CommandKind = "kind",
                    ProviderId = "provider",
                    ServiceInstanceId = "instance",
                    State = "maybe",
                    CompletedAtUtc = "2026-07-14T12:00:00Z"
                }),
                Throws.ArgumentException);
        });
    }
}
