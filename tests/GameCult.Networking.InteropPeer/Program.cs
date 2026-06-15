using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;
using MessagePack;

return await ProgramMainAsync(args);

static async Task<int> ProgramMainAsync(string[] args)
{
    try
    {
        if (args.Length == 0)
        {
            throw new InvalidOperationException("Expected mode: serve | probe | dial | rudp-serve-once | rudp-dial-once");
        }

        var mode = args[0];
        var options = ParseArgs(args.Skip(1).ToArray());
        switch (mode)
        {
            case "serve":
                await ServeAsync(ParseServeConfig(options));
                return 0;
            case "probe":
                await ProbeAsync(options);
                return 0;
            case "dial":
                await DialAsync(ParseDialConfig(options));
                return 0;
            case "rudp-serve-once":
                RudpServeOnce(options);
                return 0;
            case "rudp-dial-once":
                RudpDialOnce(options);
                return 0;
            default:
                throw new InvalidOperationException($"Unknown mode {mode}");
        }
    }
    catch (Exception error)
    {
        WriteLog("fatal", new
        {
            error = error.ToString()
        });
        return 1;
    }
}

static async Task ServeAsync(ServeConfig config)
{
    var schemaRegistration = LoadSchemaRegistration(config.SchemaPath);
    var documentRegistry = new CultNetDocumentRegistry()
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropNote>(
            schemaId: schemaRegistration.SchemaId,
            payloadSerializer: SerializeInteropNotePayload,
            payloadDeserializer: DeserializeInteropNotePayload));
    RegisterCapabilityBindings(documentRegistry);

    var customSchemaRegistry = new CultNetSchemaRegistry().Register(schemaRegistration);
    var cache = new CultCache();
    var localNote = BuildInteropNote(config.RuntimeId, config.DisplayName);
    await cache.AddAsync(localNote, new CultRecordHandle<CultNetInteropNote>(new CultRecordKey(localNote.DocumentId)));

    using var udpSocket = CreateDiscoverySocket(config.DiscoveryPort, config.DiscoveryGroup);
    using var tcpListener = new TcpListener(IPAddress.Parse(config.BindHost), config.TcpPort);
    tcpListener.Start();

    var cancellationSource = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellationSource.Cancel();

    _ = RunDiscoveryServerAsync(udpSocket, config, cancellationSource.Token);
    _ = RunTcpServerAsync(tcpListener, config, cache, documentRegistry, customSchemaRegistry, cancellationSource.Token);

    WriteJsonLine(new
    {
        status = "ready",
        mode = "serve",
        runtimeId = config.RuntimeId,
        runtimeKind = config.RuntimeKind,
        tcpPort = config.TcpPort,
        discoveryPort = config.DiscoveryPort,
        discoveryGroup = config.DiscoveryGroup.ToString()
    });

    await WaitForeverAsync(cancellationSource.Token);
}

static async Task ProbeAsync(Dictionary<string, string> options)
{
    var runtimeId = RequireArg(options, "runtime-id");
    var discoveryPort = ParseIntArg(options, "discovery-port");
    var discoveryGroup = IPAddress.Parse(RequireArg(options, "discovery-group"));
    var timeoutMs = ParseOptionalIntArg(options, "timeout-ms", 1500);
    var messageId = $"{runtimeId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
    using var timeoutSource = new CancellationTokenSource(timeoutMs);

    var probe = new DiscoveryProbeMessage
    {
        SchemaVersion = InteropPeerShared.DiscoveryProbeSchemaVersion,
        MessageId = messageId,
        RequesterRuntimeId = runtimeId
    };
    var probeBytes = MessagePackSerializer.Serialize(probe, CultNetSchemaMessageSerialization.Options);
    await socket.SendToAsync(probeBytes, SocketFlags.None, new IPEndPoint(discoveryGroup, discoveryPort));

    var found = new Dictionary<string, DiscoveryAnnounceMessage>(StringComparer.Ordinal);
    var buffer = new byte[4096];
    while (true)
    {
        try
        {
            var result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                timeoutSource.Token);
            var announce = MessagePackSerializer.Deserialize<DiscoveryAnnounceMessage>(
                new ReadOnlyMemory<byte>(buffer, 0, result.ReceivedBytes),
                CultNetSchemaMessageSerialization.Options);
            if (announce.MessageId == messageId)
            {
                found[announce.RuntimeId] = announce;
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    WriteJsonLine(new
    {
        mode = "probe",
        runtimeId,
        peers = found.Values
            .OrderBy(peer => peer.RuntimeId, StringComparer.Ordinal)
            .Select(peer => new
            {
                schemaVersion = peer.SchemaVersion,
                messageId = peer.MessageId,
                runtimeId = peer.RuntimeId,
                runtimeKind = peer.RuntimeKind,
                displayName = peer.DisplayName,
                agentId = peer.AgentId,
                tcpHost = peer.TcpHost,
                tcpPort = peer.TcpPort,
                wireContract = peer.WireContract,
                supportedDocumentTypes = peer.SupportedDocumentTypes,
                transportProfiles = peer.TransportProfiles,
                supportsSchemaCatalog = peer.SupportsSchemaCatalog
            })
            .ToArray()
    });
}

static async Task DialAsync(DialConfig config)
{
    var schemaRegistration = LoadSchemaRegistration(config.SchemaPath);
    var cache = new CultCache();
    var documentRegistry = new CultNetDocumentRegistry()
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropNote>(
            schemaId: schemaRegistration.SchemaId,
            payloadSerializer: SerializeInteropNotePayload,
            payloadDeserializer: DeserializeInteropNotePayload));
    RegisterCapabilityBindings(documentRegistry);

    using var client = new TcpClient();
    await client.ConnectAsync(config.TargetHost, config.TargetPort);
    using var stream = client.GetStream();
    var transport = new TcpFramedTransportConnection(
        stream,
        CreateTcpFramedTransportProfile(config.RuntimeId, config.TargetHost, config.TargetPort));

    await SendMessageAsync(transport, new CultNetHelloMessage
    {
        RuntimeId = config.RuntimeId,
        RuntimeKind = config.RuntimeKind,
        AgentId = config.AgentId,
        Role = "peer",
        DisplayName = config.DisplayName,
        SupportedDocumentTypes = [InteropPeerShared.InteropDocumentType],
        SupportedMutationContracts = [InteropPeerShared.InteractionContract()],
        SupportedMessageVersions = [InteropPeerShared.InteropSchemaVersion],
        SupportsSchemaCatalog = true
    });
    var remoteHello = await ExpectMessageAsync<CultNetHelloMessage>(transport, CultNetSchemaVersions.Hello);

    await SendMessageAsync(transport, new CultNetSchemaCatalogRequestMessage
    {
        MessageId = $"{config.RuntimeId}-catalog",
        IncludeSchemaJson = true,
        SchemaIds = [schemaRegistration.SchemaId],
        Kinds = ["document_payload"]
    });
    var catalog = await ExpectMessageAsync<CultNetSchemaCatalogResponseMessage>(transport, CultNetSchemaVersions.SchemaCatalogResponse);

    await SendMessageAsync(transport, new CultNetSnapshotRequestMessage
    {
        MessageId = $"{config.RuntimeId}-snapshot",
        SchemaIds = [schemaRegistration.SchemaId],
        RecordKeys = [$"note:{remoteHello.RuntimeId}"]
    });
    var snapshot = await ExpectMessageAsync<CultNetSnapshotResponseRawMessage>(transport, CultNetSchemaVersions.SnapshotResponseRaw);
    await documentRegistry.ApplyRawSnapshotResponseAsync(cache, snapshot);

    var note = cache.AllEntries
        .OfType<CultNetInteropNote>()
        .FirstOrDefault(candidate => string.Equals(candidate.AuthorRuntimeId, remoteHello.RuntimeId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Did not receive interop note from {remoteHello.RuntimeId}.");
    var hasInteropSchema = catalog.Schemas.Any(schema =>
        string.Equals(schema.SchemaId, schemaRegistration.SchemaId, StringComparison.Ordinal) &&
        string.Equals(schema.DocumentType, InteropPeerShared.InteropDocumentType, StringComparison.Ordinal));

    var mutation = await MutateRemoteNoteAsync(transport, cache, documentRegistry, config.RuntimeId, note);
    var fireReceipt = await FireRemoteWeaponAsync(transport, cache, documentRegistry, config.RuntimeId, remoteHello.RuntimeId);

    WriteJsonLine(new
    {
        mode = "dial",
        runtimeId = config.RuntimeId,
        targetHost = config.TargetHost,
        targetPort = config.TargetPort,
        remoteHello = new
        {
            schemaVersion = remoteHello.SchemaVersion,
            runtimeId = remoteHello.RuntimeId,
            runtimeKind = remoteHello.RuntimeKind,
            agentId = remoteHello.AgentId,
            displayName = remoteHello.DisplayName,
            supportedDocumentTypes = remoteHello.SupportedDocumentTypes,
            supportedMessageVersions = remoteHello.SupportedMessageVersions,
            transportProfiles = remoteHello.TransportProfiles,
            supportsSchemaCatalog = remoteHello.SupportsSchemaCatalog
        },
        hasInteropSchema,
        retrievedNote = new
        {
            schemaVersion = note.SchemaVersion,
            documentId = note.DocumentId,
            authorRuntimeId = note.AuthorRuntimeId,
            title = note.Title,
            body = note.Body,
            tags = note.Tags
        },
        mutatedNote = mutation,
        fireReceipt
    });
}

static async Task<object> MutateRemoteNoteAsync(
    TcpFramedTransportConnection transport,
    CultCache cache,
    CultNetDocumentRegistry documentRegistry,
    string runtimeId,
    CultNetInteropNote note)
{
    var intent = new CultNetInteropMutationIntent
    {
        IntentId = $"{runtimeId}-decorate",
        TargetDocumentId = note.DocumentId,
        AppendBody = $" Decorated by {runtimeId}.",
        AppendTag = $"decorated:{runtimeId}"
    };
    await SendMessageAsync(transport, documentRegistry.CreateRawDocumentPutMessage(
        $"{runtimeId}-decorate-put",
        new CultRecordHandle<CultNetInteropMutationIntent>(new CultRecordKey(intent.IntentId)),
        intent));
    await documentRegistry.ApplyRawDocumentPutMessageAsync<CultNetInteropMutationReceipt>(
        cache,
        await ExpectMessageAsync<CultNetDocumentPutRawMessage>(transport, CultNetSchemaVersions.DocumentPutRaw));
    var mutated = await documentRegistry.ApplyRawDocumentPutMessageAsync<CultNetInteropNote>(
        cache,
        await ExpectMessageAsync<CultNetDocumentPutRawMessage>(transport, CultNetSchemaVersions.DocumentPutRaw));
    return new
    {
        schemaVersion = mutated.SchemaVersion,
        documentId = mutated.DocumentId,
        authorRuntimeId = mutated.AuthorRuntimeId,
        title = mutated.Title,
        body = mutated.Body,
        tags = mutated.Tags
    };
}

static async Task<object> FireRemoteWeaponAsync(
    TcpFramedTransportConnection transport,
    CultCache cache,
    CultNetDocumentRegistry documentRegistry,
    string runtimeId,
    string remoteRuntimeId)
{
    var command = new CultNetInteropFireCommand
    {
        CommandId = $"{runtimeId}-fire",
        CharacterId = remoteRuntimeId,
        WeaponId = "interop-rifle"
    };
    await SendMessageAsync(transport, documentRegistry.CreateRawDocumentPutMessage(
        $"{runtimeId}-fire-put",
        new CultRecordHandle<CultNetInteropFireCommand>(new CultRecordKey(command.CommandId)),
        command));
    var receipt = await documentRegistry.ApplyRawDocumentPutMessageAsync<CultNetInteropFireReceipt>(
        cache,
        await ExpectMessageAsync<CultNetDocumentPutRawMessage>(transport, CultNetSchemaVersions.DocumentPutRaw));
    return new
    {
        schemaVersion = receipt.SchemaVersion,
        commandId = receipt.CommandId,
        accepted = receipt.Accepted,
        characterId = receipt.CharacterId,
        weaponId = receipt.WeaponId,
        shotsFired = receipt.ShotsFired,
        ammoRemaining = receipt.AmmoRemaining
    };
}

static async Task RunDiscoveryServerAsync(Socket socket, ServeConfig config, CancellationToken cancellationToken)
{
    var buffer = new byte[4096];
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            DiscoveryProbeMessage? probe;
            try
            {
                probe = MessagePackSerializer.Deserialize<DiscoveryProbeMessage>(
                    new ReadOnlyMemory<byte>(buffer, 0, result.ReceivedBytes),
                    CultNetSchemaMessageSerialization.Options);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(probe.SchemaVersion, InteropPeerShared.DiscoveryProbeSchemaVersion, StringComparison.Ordinal))
            {
                continue;
            }

            var announce = new DiscoveryAnnounceMessage
            {
                SchemaVersion = InteropPeerShared.DiscoveryAnnounceSchemaVersion,
                MessageId = probe.MessageId,
                RuntimeId = config.RuntimeId,
                RuntimeKind = config.RuntimeKind,
                DisplayName = config.DisplayName,
                AgentId = config.AgentId,
                TcpHost = config.AdvertiseHost,
                TcpPort = config.TcpPort,
                WireContract = CultNetWireContracts.SchemaV0,
                SupportedDocumentTypes = [InteropPeerShared.InteropDocumentType],
                TransportProfiles = [CreateTcpFramedTransportProfile(config.RuntimeId, config.AdvertiseHost, config.TcpPort)],
                SupportsSchemaCatalog = true
            };
            var announceBytes = MessagePackSerializer.Serialize(announce, CultNetSchemaMessageSerialization.Options);
            await socket.SendToAsync(announceBytes, SocketFlags.None, result.RemoteEndPoint, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception error)
        {
            WriteLog("udpError", new { runtimeId = config.RuntimeId, error = error.Message });
        }
    }
}

static async Task RunTcpServerAsync(
    TcpListener listener,
    ServeConfig config,
    CultCache cache,
    CultNetDocumentRegistry documentRegistry,
    CultNetSchemaRegistry customSchemaRegistry,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        _ = Task.Run(async () =>
        {
            using var ownedClient = client;
            try
            {
                using var stream = ownedClient.GetStream();
                var transport = new TcpFramedTransportConnection(
                    stream,
                    CreateTcpFramedTransportProfile(config.RuntimeId, config.AdvertiseHost, config.TcpPort));
                while (!cancellationToken.IsCancellationRequested)
                {
                    ICultNetSchemaMessage message;
                    try
                    {
                        message = await ReadMessageAsync(transport, cancellationToken);
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }

                    switch (message)
                    {
                        case CultNetHelloMessage:
                            await SendMessageAsync(transport, new CultNetHelloMessage
                            {
                                RuntimeId = config.RuntimeId,
                                RuntimeKind = config.RuntimeKind,
                                AgentId = config.AgentId,
                                Role = "peer",
                                DisplayName = config.DisplayName,
                                SupportedDocumentTypes = [InteropPeerShared.InteropDocumentType],
                                SupportedMutationContracts = [InteropPeerShared.InteractionContract()],
                                SupportedMessageVersions = [InteropPeerShared.InteropSchemaVersion],
                                TransportProfiles = [CreateTcpFramedTransportProfile(config.RuntimeId, config.AdvertiseHost, config.TcpPort)],
                                SupportsSchemaCatalog = true
                            }, cancellationToken);
                            break;
                        case CultNetSchemaCatalogRequestMessage catalogRequest:
                            await SendMessageAsync(transport, CreateCatalogResponse(customSchemaRegistry, catalogRequest), cancellationToken);
                            break;
                        case CultNetSnapshotRequestMessage snapshotRequest:
                            await SendMessageAsync(
                                transport,
                                documentRegistry.CreateRawSnapshotResponse(
                                    cache,
                                    snapshotRequest.MessageId,
                                    snapshotRequest,
                                    new CultNetDocumentMessageOptions
                                    {
                                        SourceRuntimeId = config.RuntimeId,
                                        SourceAgentId = config.AgentId,
                                        SourceRole = "peer",
                                        Tags = ["interop", config.RuntimeId]
                                    }),
                                cancellationToken);
                            break;
                        case CultNetDocumentPutRawMessage rawPut:
                            await HandleRawPutAsync(transport, config, cache, documentRegistry, rawPut, cancellationToken);
                            break;
                    }
                }
            }
            catch (Exception error)
            {
                WriteLog("tcpServeError", new { runtimeId = config.RuntimeId, error = error.Message });
            }
        }, cancellationToken);
    }
}

static async Task HandleRawPutAsync(
    TcpFramedTransportConnection transport,
    ServeConfig config,
    CultCache cache,
    CultNetDocumentRegistry documentRegistry,
    CultNetDocumentPutRawMessage rawPut,
    CancellationToken cancellationToken)
{
    switch (rawPut.Document.SchemaId)
    {
        case InteropPeerShared.MutationIntentSchemaId:
        {
            var intent = await documentRegistry.ApplyRawDocumentPutMessageAsync<CultNetInteropMutationIntent>(cache, rawPut);
            var note = cache.AllEntries
                .OfType<CultNetInteropNote>()
                .First(candidate => string.Equals(candidate.DocumentId, intent.TargetDocumentId, StringComparison.Ordinal));
            note.Body += intent.AppendBody;
            note.Tags = [..note.Tags, intent.AppendTag];
            await cache.AddAsync(note, new CultRecordHandle<CultNetInteropNote>(new CultRecordKey(note.DocumentId)));
            var receipt = new CultNetInteropMutationReceipt
            {
                IntentId = intent.IntentId,
                Accepted = true,
                DocumentId = note.DocumentId,
                Body = note.Body,
                Tags = note.Tags
            };
            var options = ResponseOptions(config, "mutation");
            await SendMessageAsync(transport, documentRegistry.CreateRawDocumentPutMessage(
                $"{config.RuntimeId}-mutation-receipt",
                new CultRecordHandle<CultNetInteropMutationReceipt>(new CultRecordKey(receipt.IntentId)),
                receipt,
                options), cancellationToken);
            await SendMessageAsync(transport, documentRegistry.CreateRawDocumentPutMessage(
                $"{config.RuntimeId}-mutated-note",
                new CultRecordHandle<CultNetInteropNote>(new CultRecordKey(note.DocumentId)),
                note,
                options), cancellationToken);
            break;
        }
        case InteropPeerShared.FireCommandSchemaId:
        {
            var command = await documentRegistry.ApplyRawDocumentPutMessageAsync<CultNetInteropFireCommand>(cache, rawPut);
            var receipt = new CultNetInteropFireReceipt
            {
                CommandId = command.CommandId,
                Accepted = true,
                CharacterId = command.CharacterId,
                WeaponId = command.WeaponId,
                ShotsFired = 1,
                AmmoRemaining = 29
            };
            await SendMessageAsync(transport, documentRegistry.CreateRawDocumentPutMessage(
                $"{config.RuntimeId}-fire-receipt",
                new CultRecordHandle<CultNetInteropFireReceipt>(new CultRecordKey(receipt.CommandId)),
                receipt,
                ResponseOptions(config, "side-effect")), cancellationToken);
            break;
        }
    }
}

static CultNetSchemaCatalogResponseMessage CreateCatalogResponse(
    CultNetSchemaRegistry customSchemaRegistry,
    CultNetSchemaCatalogRequestMessage request)
{
    var builtIn = CultNetSchemaRegistry.BuiltIn.CreateCatalogResponse(request).Schemas;
    var custom = customSchemaRegistry.List(
        includeSchemaJson: request.IncludeSchemaJson,
        schemaIds: request.SchemaIds,
        kinds: request.Kinds);
    var schemas = builtIn
        .Concat(custom.Where(entry => builtIn.All(candidate => !string.Equals(candidate.SchemaId, entry.SchemaId, StringComparison.Ordinal))))
        .ToArray();

    return new CultNetSchemaCatalogResponseMessage
    {
        MessageId = request.MessageId,
        Schemas = schemas
    };
}

static CultNetTransportProfile CreateTcpFramedTransportProfile(string runtimeId, string host, int port)
{
    return CultNetTransportProfiles.CreateTcpFramed(runtimeId, new TcpFramedTransportProfileOptions
    {
        TransportId = "interop-tcp",
        Host = host,
        Port = port
    });
}

static void RudpServeOnce(Dictionary<string, string> options)
{
    var bindHost = options.TryGetValue("bind-host", out var configuredBindHost) ? configuredBindHost : "127.0.0.1";
    var bindPort = options.TryGetValue("bind-port", out var configuredBindPort)
        ? int.Parse(configuredBindPort, CultureInfo.InvariantCulture)
        : 0;
    var expectedClientPayload = Ascii(options.TryGetValue("client-payload", out var configuredClientPayload)
        ? configuredClientPayload
        : "ts-csharp-client-state");
    var serverPayload = Ascii(options.TryGetValue("server-payload", out var configuredServerPayload)
        ? configuredServerPayload
        : "csharp-server-state");
    var serverExtraPayload = options.TryGetValue("server-extra-payload", out var configuredServerExtraPayload)
        ? Ascii(configuredServerExtraPayload)
        : null;
    var disconnectReason = options.TryGetValue("disconnect-reason", out var configuredDisconnectReason)
        ? Ascii(configuredDisconnectReason)
        : null;
    var maxFragmentBytes = options.TryGetValue("max-fragment-bytes", out var configuredMaxFragmentBytes)
        ? int.Parse(configuredMaxFragmentBytes, CultureInfo.InvariantCulture)
        : (int?)null;

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Parse(bindHost), bindPort));
    socket.ReceiveTimeout = 20;
    var local = (IPEndPoint)socket.LocalEndPoint!;
    using var transport = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
    {
        RuntimeId = "csharp-rudp-interop",
        Socket = socket,
        Mode = CultNetRudpSocketMode.Server,
        ConnectionId = 0x33557799,
        InitialSequence = 100,
        ResendDelayMs = 25,
        MaxFragmentBytes = maxFragmentBytes
    });

    WriteJsonLine(new
    {
        status = "ready",
        port = local.Port
    });

    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var frame = transport.ReceiveOnce();
        if (frame != null)
        {
            RequireRudpFrameBytes(frame, "schema", expectedClientPayload);
            transport.Send("schema", serverPayload);
            if (serverExtraPayload != null)
            {
                transport.Send("schema", serverExtraPayload);
            }
            if (disconnectReason != null)
            {
                transport.Disconnect(disconnectReason);
            }
            PollRudpAfterSend(transport, TimeSpan.FromMilliseconds(250));
            WriteJsonLine(new { status = "ok" });
            return;
        }

        transport.PollResends();
        Thread.Sleep(5);
    }

    throw new TimeoutException("Timed out waiting for TypeScript RUDP frame.");
}

static void RudpDialOnce(Dictionary<string, string> options)
{
    var targetHost = RequireArg(options, "target-host");
    var targetPort = ParseIntArg(options, "target-port");

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    socket.ReceiveTimeout = 20;
    using var transport = new CultNetRudpSocketTransportConnection(new CultNetRudpSocketTransportOptions
    {
        RuntimeId = "csharp-rudp-client-interop",
        Socket = socket,
        Mode = CultNetRudpSocketMode.Client,
        RemoteEndPoint = new IPEndPoint(IPAddress.Parse(targetHost), targetPort),
        ConnectionId = 0x99775533,
        InitialSequence = 1,
        ResendDelayMs = 25
    });
    transport.Connect(Ascii("csharp-join"));

    var sent = false;
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < deadline)
    {
        var frame = transport.ReceiveOnce();
        if (frame != null)
        {
            RequireRudpFrame(frame, "schema", "ts-csharp-server-state");
            WriteJsonLine(new { status = "ok" });
            return;
        }

        transport.PollResends();
        if (transport.Connected && !sent)
        {
            transport.Send("schema", Ascii("csharp-client-state"));
            sent = true;
        }
        Thread.Sleep(5);
    }

    throw new TimeoutException("Timed out waiting for TypeScript RUDP response.");
}

static void PollRudpAfterSend(CultNetRudpSocketTransportConnection transport, TimeSpan duration)
{
    var deadline = DateTimeOffset.UtcNow.Add(duration);
    while (DateTimeOffset.UtcNow < deadline)
    {
        _ = transport.ReceiveOnce();
        transport.PollResends();
        Thread.Sleep(5);
    }
}

static void RequireRudpFrame(CultNetTransportFrame frame, string expectedChannelId, string expectedPayload)
{
    RequireRudpFrameBytes(frame, expectedChannelId, Ascii(expectedPayload));
}

static void RequireRudpFrameBytes(CultNetTransportFrame frame, string expectedChannelId, byte[] expectedPayload)
{
    if (!string.Equals(frame.ChannelId, expectedChannelId, StringComparison.Ordinal)
        || !frame.Payload.SequenceEqual(expectedPayload))
    {
        throw new InvalidOperationException(
            $"Unexpected RUDP frame: channel={frame.ChannelId}, payload={Convert.ToHexString(frame.Payload)}.");
    }
}

static async Task SendMessageAsync<TMessage>(
    TcpFramedTransportConnection transport,
    TMessage message,
    CancellationToken cancellationToken = default)
    where TMessage : ICultNetSchemaMessage
{
    var payload = CultNetSchemaMessageSerialization.Serialize(message);
    await transport.SendAsync("schema", payload, cancellationToken);
}

static async Task<TMessage> ExpectMessageAsync<TMessage>(
    TcpFramedTransportConnection transport,
    string expectedSchemaVersion)
    where TMessage : class, ICultNetSchemaMessage
{
    var message = await ReadMessageAsync(transport, CancellationToken.None);
    if (!string.Equals(message.SchemaVersion, expectedSchemaVersion, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected {expectedSchemaVersion} but received {message.SchemaVersion}.");
    }

    return message as TMessage
        ?? throw new InvalidOperationException($"Expected {typeof(TMessage).Name} but received {message.GetType().Name}.");
}

static async Task<ICultNetSchemaMessage> ReadMessageAsync(
    TcpFramedTransportConnection transport,
    CancellationToken cancellationToken)
{
    var frame = await transport.ReceiveAsync(cancellationToken);
    return CultNetSchemaMessageSerialization.Deserialize(frame.Payload);
}

static Socket CreateDiscoverySocket(int port, IPAddress multicastGroup)
{
    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    {
        ExclusiveAddressUse = false
    };
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Any, port));
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(multicastGroup, IPAddress.Any));
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
    return socket;
}

static CultNetSchemaRegistration LoadSchemaRegistration(string schemaPath)
{
    var schemaJson = File.ReadAllText(schemaPath);
    using var document = JsonDocument.Parse(schemaJson);
    var root = document.RootElement;
    var schemaId = root.GetProperty("$id").GetString()
        ?? throw new InvalidOperationException($"Schema {schemaPath} is missing $id.");
    var title = root.TryGetProperty("title", out var titleElement)
        ? titleElement.GetString()
        : null;

    return new CultNetSchemaRegistration
    {
        SchemaId = schemaId,
        Kind = "document_payload",
        SchemaVersion = InteropPeerShared.InteropSchemaVersion,
        DocumentType = InteropPeerShared.InteropDocumentType,
        Title = title,
        WireContracts = [CultNetWireContracts.SchemaV0],
        SchemaJson = schemaJson
    };
}

static CultNetInteropNote BuildInteropNote(string runtimeId, string displayName)
{
    var documentId = $"note:{runtimeId}";
    return new CultNetInteropNote
    {
        SchemaVersion = InteropPeerShared.InteropSchemaVersion,
        DocumentId = documentId,
        AuthorRuntimeId = runtimeId,
        Title = $"{displayName} keeps a little note",
        Body = $"{runtimeId} can move CultNet state without begging the gods for translation.",
        Tags = [runtimeId, "interop", "cultnet"]
    };
}

static byte[] SerializeInteropNotePayload(CultNetInteropNote note)
{
    return MessagePackSerializer.Serialize(new CultNetInteropNotePayload
    {
        SchemaVersion = note.SchemaVersion,
        DocumentId = note.DocumentId,
        AuthorRuntimeId = note.AuthorRuntimeId,
        Title = note.Title,
        Body = note.Body,
        Tags = note.Tags
    }, CultNetSchemaMessageSerialization.Options);
}

static CultNetInteropNote DeserializeInteropNotePayload(byte[] payload)
{
    var decoded = MessagePackSerializer.Deserialize<CultNetInteropNotePayload>(payload, CultNetSchemaMessageSerialization.Options);
    return new CultNetInteropNote
    {
        SchemaVersion = decoded.SchemaVersion,
        DocumentId = decoded.DocumentId,
        AuthorRuntimeId = decoded.AuthorRuntimeId,
        Title = decoded.Title,
        Body = decoded.Body,
        Tags = decoded.Tags ?? Array.Empty<string>()
    };
}

static void RegisterCapabilityBindings(CultNetDocumentRegistry registry)
{
    registry
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropMutationIntent>(
            schemaId: InteropPeerShared.MutationIntentSchemaId,
            payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
            payloadDeserializer: payload => MessagePackSerializer.Deserialize<CultNetInteropMutationIntent>(payload, CultNetSchemaMessageSerialization.Options)))
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropMutationReceipt>(
            schemaId: InteropPeerShared.MutationReceiptSchemaId,
            payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
            payloadDeserializer: payload => MessagePackSerializer.Deserialize<CultNetInteropMutationReceipt>(payload, CultNetSchemaMessageSerialization.Options)))
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropFireCommand>(
            schemaId: InteropPeerShared.FireCommandSchemaId,
            payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
            payloadDeserializer: payload => MessagePackSerializer.Deserialize<CultNetInteropFireCommand>(payload, CultNetSchemaMessageSerialization.Options)))
        .Register(CultNetDocumentBinding.ForDocument<CultNetInteropFireReceipt>(
            schemaId: InteropPeerShared.FireReceiptSchemaId,
            payloadSerializer: value => MessagePackSerializer.Serialize(value, CultNetSchemaMessageSerialization.Options),
            payloadDeserializer: payload => MessagePackSerializer.Deserialize<CultNetInteropFireReceipt>(payload, CultNetSchemaMessageSerialization.Options)));
}

static CultNetDocumentMessageOptions ResponseOptions(ServeConfig config, string tag)
{
    return new CultNetDocumentMessageOptions
    {
        SourceRuntimeId = config.RuntimeId,
        SourceAgentId = config.AgentId,
        SourceRole = "peer",
        Tags = [tag, config.RuntimeId]
    };
}

static async Task WaitForeverAsync(CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
    catch (OperationCanceledException)
    {
    }
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index += 2)
    {
        var token = args[index];
        if (!token.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {token}.");
        }

        parsed[token[2..]] = args[index + 1];
    }

    return parsed;
}

static string RequireArg(Dictionary<string, string> options, string name)
{
    if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required argument --{name}.");
    }

    return value;
}

static int ParseIntArg(Dictionary<string, string> options, string name)
{
    return int.Parse(RequireArg(options, name), CultureInfo.InvariantCulture);
}

static int ParseOptionalIntArg(Dictionary<string, string> options, string name, int fallback)
{
    return options.TryGetValue(name, out var value)
        ? int.Parse(value, CultureInfo.InvariantCulture)
        : fallback;
}

static ServeConfig ParseServeConfig(Dictionary<string, string> options)
{
    return new ServeConfig(
        RequireArg(options, "runtime-id"),
        RequireArg(options, "runtime-kind"),
        RequireArg(options, "display-name"),
        RequireArg(options, "agent-id"),
        options.TryGetValue("bind-host", out var bindHost) ? bindHost : "127.0.0.1",
        RequireArg(options, "advertise-host"),
        ParseIntArg(options, "tcp-port"),
        ParseIntArg(options, "discovery-port"),
        IPAddress.Parse(RequireArg(options, "discovery-group")),
        RequireArg(options, "schema-path"));
}

static DialConfig ParseDialConfig(Dictionary<string, string> options)
{
    return new DialConfig(
        RequireArg(options, "runtime-id"),
        RequireArg(options, "runtime-kind"),
        RequireArg(options, "display-name"),
        RequireArg(options, "agent-id"),
        RequireArg(options, "target-host"),
        ParseIntArg(options, "target-port"),
        RequireArg(options, "schema-path"));
}

static void WriteJsonLine(object value)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(value, InteropPeerShared.JsonOptions));
}

static byte[] Ascii(string value)
{
    return Encoding.ASCII.GetBytes(value);
}

static void WriteLog(string @event, object payload)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["event"] = @event,
        ["payload"] = payload
    }, InteropPeerShared.JsonOptions));
}

sealed record ServeConfig(
    string RuntimeId,
    string RuntimeKind,
    string DisplayName,
    string AgentId,
    string BindHost,
    string AdvertiseHost,
    int TcpPort,
    int DiscoveryPort,
    IPAddress DiscoveryGroup,
    string SchemaPath);

sealed record DialConfig(
    string RuntimeId,
    string RuntimeKind,
    string DisplayName,
    string AgentId,
    string TargetHost,
    int TargetPort,
    string SchemaPath);

static class InteropPeerShared
{
    public const string InteropDocumentType = "cultnet.interop-note";
    public const string InteropSchemaVersion = "cultnet.interop_note.v0";
    public const string MutationIntentDocumentType = "cultnet.interop-note-mutation-intent";
    public const string MutationIntentSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-intent.schema.json";
    public const string MutationIntentSchemaVersion = "cultnet.interop_note_mutation_intent.v0";
    public const string MutationReceiptDocumentType = "cultnet.interop-note-mutation-receipt";
    public const string MutationReceiptSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-receipt.schema.json";
    public const string MutationReceiptSchemaVersion = "cultnet.interop_note_mutation_receipt.v0";
    public const string FireCommandDocumentType = "cultnet.interop-fire-weapon-command";
    public const string FireCommandSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-command.schema.json";
    public const string FireCommandSchemaVersion = "cultnet.interop_fire_weapon_command.v0";
    public const string FireReceiptDocumentType = "cultnet.interop-fire-weapon-receipt";
    public const string FireReceiptSchemaId = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-receipt.schema.json";
    public const string FireReceiptSchemaVersion = "cultnet.interop_fire_weapon_receipt.v0";
    public const string DiscoveryProbeSchemaVersion = "cultnet.discovery_probe.v0";
    public const string DiscoveryAnnounceSchemaVersion = "cultnet.discovery_announce.v0";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CultNetDocumentMutationContract InteractionContract()
    {
        return new CultNetDocumentMutationContract
        {
            DocumentType = InteropDocumentType,
            PayloadSchemaVersion = InteropSchemaVersion,
            Operations = ["snapshot", "documentPut", "intentSubmit", "receiptWatch"],
            Authority = CultNetMutationAuthorities.Runtime,
            IntentDocumentTypes = [MutationIntentDocumentType, FireCommandDocumentType],
            ReceiptDocumentTypes = [MutationReceiptDocumentType, FireReceiptDocumentType],
            Notes = []
        };
    }
}

[CultDocument(InteropPeerShared.InteropDocumentType, InteropPeerShared.InteropSchemaVersion)]
public sealed class CultNetInteropNote
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.InteropSchemaVersion;
    [Key(1)] [CultName] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
    [Key(3)] public string Title { get; set; } = string.Empty;
    [Key(4)] public string Body { get; set; } = string.Empty;
    [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
}

[MessagePackObject]
public sealed class CultNetInteropNotePayload
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.InteropSchemaVersion;
    [Key(1)] public string DocumentId { get; set; } = string.Empty;
    [Key(2)] public string AuthorRuntimeId { get; set; } = string.Empty;
    [Key(3)] public string Title { get; set; } = string.Empty;
    [Key(4)] public string Body { get; set; } = string.Empty;
    [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
}

[CultDocument(InteropPeerShared.MutationIntentDocumentType, InteropPeerShared.MutationIntentSchemaVersion)]
[MessagePackObject]
public sealed class CultNetInteropMutationIntent
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.MutationIntentSchemaVersion;
    [Key(1)] [CultName] public string IntentId { get; set; } = string.Empty;
    [Key(2)] public string TargetDocumentId { get; set; } = string.Empty;
    [Key(3)] public string AppendBody { get; set; } = string.Empty;
    [Key(4)] public string AppendTag { get; set; } = string.Empty;
}

[CultDocument(InteropPeerShared.MutationReceiptDocumentType, InteropPeerShared.MutationReceiptSchemaVersion)]
[MessagePackObject]
public sealed class CultNetInteropMutationReceipt
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.MutationReceiptSchemaVersion;
    [Key(1)] [CultName] public string IntentId { get; set; } = string.Empty;
    [Key(2)] public bool Accepted { get; set; }
    [Key(3)] public string DocumentId { get; set; } = string.Empty;
    [Key(4)] public string Body { get; set; } = string.Empty;
    [Key(5)] public string[] Tags { get; set; } = Array.Empty<string>();
}

[CultDocument(InteropPeerShared.FireCommandDocumentType, InteropPeerShared.FireCommandSchemaVersion)]
[MessagePackObject]
public sealed class CultNetInteropFireCommand
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.FireCommandSchemaVersion;
    [Key(1)] [CultName] public string CommandId { get; set; } = string.Empty;
    [Key(2)] public string CharacterId { get; set; } = string.Empty;
    [Key(3)] public string WeaponId { get; set; } = string.Empty;
}

[CultDocument(InteropPeerShared.FireReceiptDocumentType, InteropPeerShared.FireReceiptSchemaVersion)]
[MessagePackObject]
public sealed class CultNetInteropFireReceipt
{
    [Key(0)] public string SchemaVersion { get; set; } = InteropPeerShared.FireReceiptSchemaVersion;
    [Key(1)] [CultName] public string CommandId { get; set; } = string.Empty;
    [Key(2)] public bool Accepted { get; set; }
    [Key(3)] public string CharacterId { get; set; } = string.Empty;
    [Key(4)] public string WeaponId { get; set; } = string.Empty;
    [Key(5)] public int ShotsFired { get; set; }
    [Key(6)] public int AmmoRemaining { get; set; }
}

[MessagePackObject]
public sealed class DiscoveryProbeMessage
{
    [Key("schemaVersion")] public string SchemaVersion { get; set; } = InteropPeerShared.DiscoveryProbeSchemaVersion;
    [Key("messageId")] public string MessageId { get; set; } = string.Empty;
    [Key("requesterRuntimeId")] public string RequesterRuntimeId { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class DiscoveryAnnounceMessage
{
    [Key("schemaVersion")] public string SchemaVersion { get; set; } = InteropPeerShared.DiscoveryAnnounceSchemaVersion;
    [Key("messageId")] public string MessageId { get; set; } = string.Empty;
    [Key("runtimeId")] public string RuntimeId { get; set; } = string.Empty;
    [Key("runtimeKind")] public string RuntimeKind { get; set; } = string.Empty;
    [Key("displayName")] public string DisplayName { get; set; } = string.Empty;
    [Key("agentId")] public string? AgentId { get; set; }
    [Key("tcpHost")] public string TcpHost { get; set; } = string.Empty;
    [Key("tcpPort")] public int TcpPort { get; set; }
    [Key("wireContract")] public string WireContract { get; set; } = CultNetWireContracts.SchemaV0;
    [Key("supportedDocumentTypes")] public string[] SupportedDocumentTypes { get; set; } = Array.Empty<string>();
    [Key("transportProfiles")] public CultNetTransportProfile[]? TransportProfiles { get; set; }
    [Key("supportsSchemaCatalog")] public bool SupportsSchemaCatalog { get; set; }
}
