using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using GameCult.Caching;
using GameCult.Caching.MessagePack;
using GameCult.Networking;
using MessagePack;
using NUnit.Framework;
using R3;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshStreamingTests
{
    [Test]
    public async Task TypedOperationHandle_CarriesAuthorityAndRouteContext()
    {
        var handle = new CultMeshOperationHandle<MeshMoveRequest, CultMeshOperationReceipt>(
            "aetheria.entity.pilot.move",
            (request, context) => Task.FromResult(new CultMeshOperationReceipt(
                "aetheria.entity.pilot.move",
                accepted: request.EntityId == 7 &&
                          context.Claims.Count == 1 &&
                          context.Claims[0].Role == "pilot-control" &&
                          context.RouteHint.Kind == CultMeshLocalityKind.InProcess,
                context.RouteHint)));

        var receipt = await handle.InvokeAsync(
            new MeshMoveRequest(7, 1, -1),
            CultMeshOperationContext
                .ForRuntime("unity-raven")
                .WithClaim(new CultMeshAuthorityClaim("pilot-control", shardId: "zone:local-rts"))
                .WithRoute(new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "co-located daemon"))
                .WithIdempotencyKey("move-7-001"));

        receipt.Accepted.Should().BeTrue();
        receipt.Route.Kind.Should().Be(CultMeshLocalityKind.InProcess);

        var diagnostic = CultMesh.DescribeOperationHandle(handle);
        diagnostic.OperationId.Should().Be("aetheria.entity.pilot.move");
    }

    [Test]
    public void ContextBuilders_MakeTypedHandleCallsReadLikeVerseOperations()
    {
        var operationContext = CultMesh.OperationContextFor("unity-raven")
            .Claim("pilot-control", shardId: "zone:local-rts", leaseId: "lease:raven")
            .Route(CultMeshLocalityKind.SharedMemory, "co-located daemon")
            .Idempotency("move-7-001")
            .Build();

        operationContext.RuntimeId.Should().Be("unity-raven");
        operationContext.Claims.Should().ContainSingle();
        operationContext.Claims[0].Role.Should().Be("pilot-control");
        operationContext.Claims[0].ShardId.Should().Be("zone:local-rts");
        operationContext.Claims[0].LeaseId.Should().Be("lease:raven");
        operationContext.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        operationContext.IdempotencyKey.Should().Be("move-7-001");

        var queryContext = CultMesh.QueryContextFor("browser-starfire")
            .Route(CultMeshLocalityKind.Wasm, "browser-local projection")
            .Build();

        queryContext.RuntimeId.Should().Be("browser-starfire");
        queryContext.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Wasm);
        queryContext.RouteHint.Description.Should().Be("browser-local projection");
    }

    [Test]
    public async Task VerseContext_LetsGeneratedDomainSugarUseSharedTypedContexts()
    {
        var verse = await CultMesh.ConnectVerseAsync(
            "starbridge",
            "unity-raven",
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located Verse"),
            new[] { new CultMeshAuthorityClaim("pilot-control", shardId: "zone:raven", leaseId: "lease:raven") });
        var commandVerse = verse.WithRoute(new CultMeshRouteHint(CultMeshLocalityKind.Network, "remote command route"));

        var aetheria = commandVerse.Use(context => new MeshAetheriaDomain(context));
        var receipt = await aetheria.Entity(7).Pilot.MoveAsync(new MeshVec2(1, 0), "move:raven:1");
        var viewport = await aetheria.Zone("zone:raven").Objects.VisibleWithinAsync(new MeshViewportRequest(-16, 16));

        verse.VerseId.Should().Be("starbridge");
        verse.RuntimeId.Should().Be("unity-raven");
        verse.QueryContext().RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        commandVerse.OperationContext("move:raven:2").RouteHint.Kind.Should().Be(CultMeshLocalityKind.Network);
        commandVerse.OperationContext("move:raven:2").IdempotencyKey.Should().Be("move:raven:2");
        receipt.OperationId.Should().Be("aetheria.entity.pilot.move");
        receipt.Accepted.Should().BeTrue();
        receipt.Route.Kind.Should().Be(CultMeshLocalityKind.Network);
        viewport.Should().Equal("unity-raven:zone:raven:-16:16:Network");
    }

    [Test]
    public async Task QuerySurfaceAndStatePointer_ExposeTypedReactiveState()
    {
        var query = new CultMeshQuerySurface<MeshViewportRequest, string[]>(
            "aetheria.zone.objects.visible",
            (request, context) => Task.FromResult(new[] { $"{context.RuntimeId}:{request.MinX}:{request.MaxX}" }));

        var queryResult = await query.ExecuteAsync(
            new MeshViewportRequest(-8, 8),
            CultMeshQueryContext.ForRuntime("browser-starfire"));

        queryResult.Should().Equal("browser-starfire:-8:8");

        var queryDiagnostic = CultMesh.DescribeQuerySurface(query);
        queryDiagnostic.QueryId.Should().Be("aetheria.zone.objects.visible");
        queryDiagnostic.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Automatic);
        queryDiagnostic.Sources.Should().BeEmpty();

        var subject = new Subject<string>();
        var pointer = CultMesh.StatePointer(
            "aetheria.selection.current",
            () => Task.FromResult("entity:station:0"),
            () => subject,
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located selection cache"),
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.selection.current.v1",
                    schemaId: "gamecult.aetheria.selection.v1")
            });

        string observed = null!;
        using var subscription = pointer.Watch().Subscribe(value => observed = value);

        (await pointer.ResolveAsync()).Should().Be("entity:station:0");
        subject.OnNext("entity:pawn:7");
        observed.Should().Be("entity:pawn:7");

        var pointerDiagnostic = CultMesh.DescribeStatePointer(pointer);
        pointerDiagnostic.PointerId.Should().Be("aetheria.selection.current");
        pointerDiagnostic.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        pointerDiagnostic.Sources.Should().ContainSingle().Which.SchemaId.Should().Be("gamecult.aetheria.selection.v1");
    }

    [Test]
    public async Task StatePointer_CanBindToVerseContextForUiAndTools()
    {
        var subject = new Subject<string>();
        var pointer = CultMesh.StatePointer(
            "aetheria.daemon.frame.latest",
            context => Task.FromResult($"{context.RuntimeId}:{context.RouteHint.Kind}"),
            context => subject.Select(value => $"{context.RuntimeId}:{value}:{context.RouteHint.Kind}"),
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located daemon frame"),
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.frame.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_frame.v1")
            });

        var verse = CultMesh.Verse(
            "aetheria.local",
            "bifrost-tool",
            new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "tool bridge"));
        var bound = verse.BindStatePointer(pointer);

        (await bound.ResolveAsync()).Should().Be("bifrost-tool:Ipc");

        string observed = null!;
        using var subscription = bound.Watch().Subscribe(value => observed = value);
        subject.OnNext("frame:12");

        bound.PointerId.Should().Be("aetheria.daemon.frame.latest");
        bound.Sources.Should().ContainSingle().Which.SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
        observed.Should().Be("bifrost-tool:frame:12:Ipc");
    }

    [Test]
    public async Task MutableStatePointer_HoistsReadWatchReplaceDocumentHandles()
    {
        var subject = new Subject<string>();
        var stored = "frame:0";
        var observedContexts = new List<string>();
        var pointer = CultMesh.MutableStatePointer(
            "aetheria.daemon.frame.latest",
            context =>
            {
                observedContexts.Add($"read:{context.RuntimeId}:{context.RouteHint.Kind}");
                return Task.FromResult<string?>(stored);
            },
            context => subject.Select(value => $"{context.RuntimeId}:{value}:{context.RouteHint.Kind}"),
            (context, value) =>
            {
                observedContexts.Add($"replace:{context.RuntimeId}:{context.RouteHint.Kind}");
                stored = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located daemon frame"),
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.frame.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_frame.v1")
            });

        var verse = CultMesh.Verse(
            "aetheria.local",
            "unity-raven",
            new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "tool bridge"));
        var bound = verse.BindMutableStatePointer(pointer);

        string observed = null!;
        using var subscription = bound.Watch().Subscribe(value => observed = value);

        (await bound.ReadAsync()).Should().Be("frame:0");
        await bound.ReplaceAsync("frame:1");

        stored.Should().Be("frame:1");
        observed.Should().Be("unity-raven:frame:1:Ipc");
        observedContexts.Should().Equal("read:unity-raven:Ipc", "replace:unity-raven:Ipc");

        var diagnostic = CultMesh.DescribeStatePointer(pointer);
        diagnostic.PointerId.Should().Be("aetheria.daemon.frame.latest");
        diagnostic.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);

        var binding = CultMesh.StateBinding("frame", pointer);
        binding.PointerId.Should().Be("aetheria.daemon.frame.latest");
        binding.SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
    }

    [Test]
    public void StateBindingDescriptor_BindsUiPropsToTypedStatePointers()
    {
        var pointer = CultMesh.StatePointer(
            "aetheria.selection.current",
            () => Task.FromResult("entity:station:0"),
            () => new Subject<string>(),
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located selection cache"),
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.selection.current.v1",
                    schemaId: "gamecult.aetheria.selection.v1")
            });

        var binding = CultMesh.StateBinding("value", pointer);
        var explicitBinding = CultMesh.StateBinding(
            "label",
            "aetheria.selection.label",
            sourceId: "daemon:aetheria.selection.label.v1",
            schemaId: "gamecult.aetheria.selection_label.v1",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "tool bridge"));

        binding.TargetProp.Should().Be("value");
        binding.PointerId.Should().Be("aetheria.selection.current");
        binding.SourceId.Should().Be("daemon:aetheria.selection.current.v1");
        binding.SchemaId.Should().Be("gamecult.aetheria.selection.v1");
        binding.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        binding.RouteHint.Description.Should().Be("co-located selection cache");

        explicitBinding.TargetProp.Should().Be("label");
        explicitBinding.PointerId.Should().Be("aetheria.selection.label");
        explicitBinding.SourceId.Should().Be("daemon:aetheria.selection.label.v1");
        explicitBinding.SchemaId.Should().Be("gamecult.aetheria.selection_label.v1");
        explicitBinding.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Ipc);
    }

    [Test]
    public void StateBindingRecord_FlattensAndRehydratesPointerBindingFields()
    {
        var binding = CultMesh.StateBinding(
            "status",
            "aetheria.current.status",
            sourceId: "daemon:aetheria.frame.latest.v1",
            schemaId: "gamecult.aetheria.daemon_frame.v1",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located frame slab"));

        var record = CultMesh.StateBindingRecord(binding);
        record.TargetProp.Should().Be("status");
        record.PointerId.Should().Be("aetheria.current.status");
        record.SourceId.Should().Be("daemon:aetheria.frame.latest.v1");
        record.SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
        record.RouteKind.Should().Be(nameof(CultMeshLocalityKind.SharedMemory));
        record.RouteDescription.Should().Be("co-located frame slab");

        var fromTypescript = CultMesh.StateBindingRecord(
            "value",
            "aetheria.selection.current",
            "daemon:aetheria.selection.current.v1",
            "gamecult.aetheria.selection.v1",
            "shared-memory",
            "browser-adjacent cache").ToBinding();

        fromTypescript.TargetProp.Should().Be("value");
        fromTypescript.PointerId.Should().Be("aetheria.selection.current");
        fromTypescript.SourceId.Should().Be("daemon:aetheria.selection.current.v1");
        fromTypescript.SchemaId.Should().Be("gamecult.aetheria.selection.v1");
        fromTypescript.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        fromTypescript.RouteHint.Description.Should().Be("browser-adjacent cache");
    }

    [Test]
    public void OperationBindingDescriptor_BindsUiCommandsToTypedOperations()
    {
        var operation = new CultMeshOperationHandle<MeshMoveRequest, CultMeshOperationReceipt>(
            "aetheria.entity.pilot.move",
            (_request, _context) => Task.FromResult(new CultMeshOperationReceipt("aetheria.entity.pilot.move", true)));

        var binding = CultMesh.OperationBinding(
            operation,
            label: "Move",
            schemaId: "gamecult.aetheria.pilot_move.v1",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, "remote Verse peer"));
        var explicitBinding = CultMesh.OperationBinding(
            "aetheria.surface.refresh",
            label: "Refresh");

        binding.OperationId.Should().Be("aetheria.entity.pilot.move");
        binding.Label.Should().Be("Move");
        binding.SchemaId.Should().Be("gamecult.aetheria.pilot_move.v1");
        binding.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Network);
        binding.RouteHint.Description.Should().Be("remote Verse peer");

        explicitBinding.OperationId.Should().Be("aetheria.surface.refresh");
        explicitBinding.Label.Should().Be("Refresh");
        explicitBinding.SchemaId.Should().Be("");
        explicitBinding.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Automatic);
    }

    [Test]
    public void OperationBindingRecord_FlattensAndRehydratesSurfaceCommandFields()
    {
        var binding = CultMesh.OperationBinding(
            "aetheria.entity.pilot.move",
            label: "Move",
            schemaId: "gamecult.aetheria.pilot_move.v1",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, "remote Verse peer"));

        var record = CultMesh.OperationBindingRecord(binding);
        record.OperationId.Should().Be("aetheria.entity.pilot.move");
        record.Label.Should().Be("Move");
        record.SchemaId.Should().Be("gamecult.aetheria.pilot_move.v1");
        record.RouteKind.Should().Be(nameof(CultMeshLocalityKind.Network));
        record.RouteDescription.Should().Be("remote Verse peer");

        var fromTypescript = CultMesh.OperationBindingRecord(
            "aetheria.surface.refresh",
            "Refresh",
            "gamecult.aetheria.refresh.v1",
            "in-process",
            "embedded UI runtime").ToBinding();

        fromTypescript.OperationId.Should().Be("aetheria.surface.refresh");
        fromTypescript.Label.Should().Be("Refresh");
        fromTypescript.SchemaId.Should().Be("gamecult.aetheria.refresh.v1");
        fromTypescript.RouteHint.Kind.Should().Be(CultMeshLocalityKind.InProcess);
        fromTypescript.RouteHint.Description.Should().Be("embedded UI runtime");
    }

    [Test]
    public void OperationInvocationDescriptor_CarriesTypedOperationIdentityThroughUiRequests()
    {
        var binding = CultMesh.OperationBinding(
            "aetheria.entity.pilot.move",
            label: "Move",
            schemaId: "gamecult.aetheria.pilot_move.v1",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "local Verse node"));

        var invocation = CultMesh.OperationInvocation(binding, idempotencyKey: "move:42");
        var explicitInvocation = CultMesh.OperationInvocation(
            "aetheria.surface.refresh",
            routeHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, "remote Verse peer"));

        invocation.OperationId.Should().Be("aetheria.entity.pilot.move");
        invocation.SchemaId.Should().Be("gamecult.aetheria.pilot_move.v1");
        invocation.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Ipc);
        invocation.RouteHint.Description.Should().Be("local Verse node");
        invocation.IdempotencyKey.Should().Be("move:42");

        explicitInvocation.OperationId.Should().Be("aetheria.surface.refresh");
        explicitInvocation.SchemaId.Should().Be("");
        explicitInvocation.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Network);
        explicitInvocation.IdempotencyKey.Should().BeNull();
    }

    [Test]
    public void OperationPayload_ReadsCommonSurfaceScalarFields()
    {
        var payload = CultMesh.OperationPayload(
            ("value", "42.5"),
            ("tierIndex", "3"),
            ("enabled", "on"),
            ("name", "Starfire"));
        var updated = payload.With("enabled", "false");

        payload.GetString("name").Should().Be("Starfire");
        payload.GetString("missing", "fallback").Should().Be("fallback");
        payload.GetInt32("tierIndex", -1).Should().Be(3);
        payload.GetInt32("missing", -1).Should().Be(-1);
        payload.GetDouble("value", -1).Should().Be(42.5);
        payload.GetBoolean("enabled").Should().BeTrue();
        updated.GetBoolean("enabled", true).Should().BeFalse();
        updated.GetString("name").Should().Be("Starfire");
        payload.GetBoolean("missing", true).Should().BeTrue();
        payload.Should().ContainKey("value");
        payload.Count.Should().Be(4);
    }

    [Test]
    public void OperationInvocationRecord_FlattensAndRehydratesTransportFields()
    {
        var invocation = CultMesh.OperationInvocation(
            "aetheria.entity.pilot.move",
            "gamecult.aetheria.pilot_move.v1",
            new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "local Verse node"),
            "move:42");

        var record = CultMesh.OperationInvocationRecord(invocation);
        var restored = record.ToInvocation(
            fallbackOperationId: "fallback.operation",
            fallbackSchemaId: "fallback.schema",
            fallbackRouteHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, "fallback route"));
        var fallback = CultMesh.OperationInvocationRecord(
                operationId: "",
                schemaId: "",
                routeKind: "not-a-route",
                routeDescription: "",
                idempotencyKey: "")
            .ToInvocation(
                fallbackOperationId: "fallback.operation",
                fallbackSchemaId: "fallback.schema",
                fallbackRouteHint: new CultMeshRouteHint(CultMeshLocalityKind.Network, "fallback route"),
                fallbackIdempotencyKey: "fallback-key");
        var payloadFields = CultMesh.OperationPayload(("value", "Raven")).ToDictionary();

        record.OperationId.Should().Be("aetheria.entity.pilot.move");
        record.SchemaId.Should().Be("gamecult.aetheria.pilot_move.v1");
        record.RouteKind.Should().Be("Ipc");
        record.RouteDescription.Should().Be("local Verse node");
        record.IdempotencyKey.Should().Be("move:42");
        restored.OperationId.Should().Be("aetheria.entity.pilot.move");
        restored.SchemaId.Should().Be("gamecult.aetheria.pilot_move.v1");
        restored.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Ipc);
        restored.IdempotencyKey.Should().Be("move:42");
        fallback.OperationId.Should().Be("fallback.operation");
        fallback.SchemaId.Should().Be("fallback.schema");
        fallback.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Network);
        fallback.RouteHint.Description.Should().Be("fallback route");
        fallback.IdempotencyKey.Should().Be("fallback-key");
        payloadFields["value"].Should().Be("Raven");
    }

    [Test]
    public void RouteRecord_FlattensAndParsesCrossRuntimeRouteKinds()
    {
        var record = CultMesh.RouteRecord(new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located slab"));
        var restored = record.ToRoute();
        var fromTypescript = CultMesh.RouteRecord("in-process", "").ToRoute(
            new CultMeshRouteHint(CultMeshLocalityKind.Network, "fallback route"));
        var fallback = CultMesh.RouteRecord("not-real", "").ToRoute(
            new CultMeshRouteHint(CultMeshLocalityKind.Ipc, "fallback route"));

        record.Kind.Should().Be("SharedMemory");
        record.Description.Should().Be("co-located slab");
        restored.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        restored.Description.Should().Be("co-located slab");
        fromTypescript.Kind.Should().Be(CultMeshLocalityKind.InProcess);
        fromTypescript.Description.Should().Be("fallback route");
        fallback.Kind.Should().Be(CultMeshLocalityKind.Ipc);
        fallback.Description.Should().Be("fallback route");
    }

    [Test]
    public void StateRefResolver_ComposesNamedSurfaceResolution()
    {
        var route = new CultMeshRouteHint(CultMeshLocalityKind.InProcess, "embedded renderer");
        var daemon = CultMesh.StateRefResolver(
            "aetheria.daemon.refs",
            (stateRef, context) => stateRef == "aetheria.daemon/frame/id"
                ? $"{context.RuntimeId}:{context.RouteHint.Kind}:42"
                : "",
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.frame.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_frame.v1")
            },
            route);
        var itemStats = CultMesh.StateRefResolver(
            "aetheria.item_stats.refs",
            stateRef => stateRef == "aetheria.item_stats/laser/damage" ? "12.5" : "");
        var resolver = daemon.Or(itemStats);

        resolver.Resolve(
                "aetheria.daemon/frame/id",
                CultMesh.QueryContextFor("unity-raven")
                    .Route(CultMeshLocalityKind.Network, "remote peer")
                    .Build())
            .Should()
            .Be("unity-raven:Network:42");
        resolver.Resolve("aetheria.item_stats/laser/damage").Should().Be("12.5");
        resolver.TryResolve("missing", out var missing).Should().BeFalse();
        missing.Should().Be("");
        resolver.AsFunc()("aetheria.item_stats/laser/damage").Should().Be("12.5");

        var diagnostic = CultMesh.DescribeStateRefResolver(resolver);
        diagnostic.ResolverId.Should().Be("aetheria.daemon.refs|aetheria.item_stats.refs");
        diagnostic.RouteHint.Kind.Should().Be(CultMeshLocalityKind.InProcess);
        diagnostic.Sources.Should().ContainSingle();
        diagnostic.Sources[0].SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
    }

    [Test]
    public async Task ProjectionRecipe_NamesSourcesAndCanBecomeQuerySurface()
    {
        var recipe = CultMesh.ProjectionRecipe<MeshViewportRequest, string[]>(
            "aetheria.zone.objects.visible",
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.frame.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_frame.v1",
                    description: "latest daemon frame"),
                CultMesh.ProjectionSource(
                    "daemon:aetheria.authority.policy.v1",
                    schemaId: "gamecult.aetheria.authority_policy.v1")
            },
            (request, context) => Task.FromResult(new[]
            {
                $"{context.RuntimeId}:{request.MinX}:{request.MaxX}:{context.RouteHint.Kind}"
            }),
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located frame slab"));

        recipe.Sources.Should().HaveCount(2);
        recipe.Sources[0].SourceId.Should().Be("daemon:aetheria.frame.latest.v1");
        recipe.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);

        var recipeDiagnostic = CultMesh.DescribeProjectionRecipe(recipe);
        recipeDiagnostic.ProjectionId.Should().Be("aetheria.zone.objects.visible");
        recipeDiagnostic.RouteHint.Description.Should().Be("co-located frame slab");
        recipeDiagnostic.Sources.Should().HaveCount(2);
        recipeDiagnostic.Sources[0].SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
        recipeDiagnostic.Sources.Should().NotBeSameAs(recipe.Sources);

        var result = await recipe.ProjectAsync(
            new MeshViewportRequest(-16, 16),
            CultMesh.QueryContextFor("browser-starfire")
                .Route(CultMeshLocalityKind.Wasm, "browser-local projection")
                .Build());

        result.Should().Equal("browser-starfire:-16:16:Wasm");

        var query = recipe.AsQuerySurface();
        var queryResult = await query.ExecuteAsync(
            new MeshViewportRequest(-4, 4),
            "unity-raven");

        query.QueryId.Should().Be(recipe.ProjectionId);
        query.Sources.Should().HaveCount(2);
        query.Sources[0].SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
        query.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        queryResult.Should().Equal("unity-raven:-4:4:SharedMemory");

        var queryDiagnostic = CultMesh.DescribeQuerySurface(query);
        queryDiagnostic.QueryId.Should().Be("aetheria.zone.objects.visible");
        queryDiagnostic.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        queryDiagnostic.Sources.Should().HaveCount(2);
        queryDiagnostic.Sources.Should().NotBeSameAs(query.Sources);
    }

    [Test]
    public async Task LiveFeed_DescribesAndWatchesCoherentClientSnapshots()
    {
        var subject = new Subject<string>();
        var feed = CultMesh.LiveFeed<MeshViewportRequest, string>(
            "aetheria.rts.viewport.feed",
            (request, context) => Task.FromResult(
                $"{context.RuntimeId}:{request.MinX}:{request.MaxX}:{context.RouteHint.Kind}"),
            (_request, context) => subject.Select(value => $"{context.RuntimeId}:{value}:{context.RouteHint.Kind}"),
            new[]
            {
                CultMesh.ProjectionSource(
                    "daemon:aetheria.frame.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_frame.v1"),
                CultMesh.ProjectionSource(
                    "daemon:aetheria.health.latest.v1",
                    schemaId: "gamecult.aetheria.daemon_health.v1")
            },
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located RTS cache"));

        feed.FeedId.Should().Be("aetheria.rts.viewport.feed");
        feed.Sources.Should().HaveCount(2);
        feed.Sources[0].SchemaId.Should().Be("gamecult.aetheria.daemon_frame.v1");
        feed.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);

        var diagnostic = CultMesh.DescribeLiveFeed(feed);
        diagnostic.FeedId.Should().Be("aetheria.rts.viewport.feed");
        diagnostic.RouteHint.Description.Should().Be("co-located RTS cache");
        diagnostic.Sources.Should().HaveCount(2);
        diagnostic.Sources[1].SourceId.Should().Be("daemon:aetheria.health.latest.v1");
        diagnostic.Sources.Should().NotBeSameAs(feed.Sources);

        var inherited = await feed.SnapshotAsync(
            new MeshViewportRequest(-32, 32),
            "browser-starfire");

        inherited.Should().Be("browser-starfire:-32:32:SharedMemory");

        var explicitRoute = await feed.SnapshotAsync(
            new MeshViewportRequest(-32, 32),
            CultMesh.QueryContextFor("unity-raven")
                .Route(CultMeshLocalityKind.InProcess, "embedded Verse")
                .Build());

        explicitRoute.Should().Be("unity-raven:-32:32:InProcess");

        string observed = null!;
        using var subscription = feed
            .Watch(new MeshViewportRequest(-1, 1), CultMeshQueryContext.ForRuntime("browser-starfire"))
            .Subscribe(value => observed = value);

        subject.OnNext("frame:42");
        observed.Should().Be("browser-starfire:frame:42:SharedMemory");
    }

    [Test]
    public async Task DocumentHandle_ExposesTypedSnapshotsAndSameSchemaAliases()
    {
        var subject = new Subject<MeshNoteDocument>();
        var route = new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located document slab");
        var sources = new[]
        {
            CultMesh.ProjectionSource(
                "daemon:mesh.note.current",
                schemaId: "tests.mesh_note.v1")
        };
        var context = CultMesh.Verse("starbridge", "unity-pilot", route).Context;
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "primary",
            Revision = 1
        };

        var handle = CultMesh.Document(
            "mesh.note.current",
            context,
            _ => Task.FromResult(current),
            _ => subject,
            sources,
            route);

        handle.DocumentId.Should().Be("mesh.note.current");
        handle.DocumentType.Should().Be(typeof(MeshNoteDocument));
        handle.SchemaName.Should().Be("tests.mesh_note");
        handle.SchemaVersion.Should().Be("tests.mesh_note.v1");
        handle.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        handle.Sources.Should().ContainSingle().Which.SourceId.Should().Be("daemon:mesh.note.current");

        var snapshot = await handle.LatestAsync();
        snapshot.Text.Should().Be("primary");
        handle.Latest().Text.Should().Be("primary");

        var alias = handle.AsSchemaAlias<MeshNoteAliasDocument>();
        var aliasSnapshot = await alias.LatestAsync();
        alias.Latest().Text.Should().Be("primary");
        alias.DocumentId.Should().Be(handle.DocumentId);
        alias.SchemaName.Should().Be(handle.SchemaName);
        alias.SchemaVersion.Should().Be(handle.SchemaVersion);
        aliasSnapshot.Text.Should().Be("primary");
        aliasSnapshot.Revision.Should().Be(1);

        MeshNoteAliasDocument observed = null!;
        using var subscription = alias.Watch(value => observed = value);
        subject.OnNext(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "reactive",
            Revision = 2
        });

        observed.Text.Should().Be("reactive");
        observed.Revision.Should().Be(2);
    }

    [Test]
    public void DocumentHandle_RejectsAliasTypesWithDifferentSchemaIdentity()
    {
        var handle = CultMesh.Document(
            "mesh.note.current",
            CultMesh.Verse("starbridge", "unity-pilot"),
            _ => Task.FromResult(new MeshNoteDocument
            {
                Schema = "tests.mesh_note.v1",
                Text = "primary",
                Revision = 1
            }),
            _ => new Subject<MeshNoteDocument>());

        Action act = () => handle.AsSchemaAlias<MeshOtherDocument>();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*tests.mesh_other*tests.mesh_note*");
    }

    [Test]
    public async Task DocumentCatalog_IndexesHandlesByTypeAndSchemaAlias()
    {
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "catalog-primary",
            Revision = 3
        };
        var handle = CultMesh.Document(
            "mesh.note.current",
            CultMesh.Verse("starbridge", "unity-pilot"),
            _ => Task.FromResult(current),
            _ => new Subject<MeshNoteDocument>());
        var catalog = CultMesh.Documents(handle);

        catalog.Documents.Should().ContainSingle().Which.Should().BeSameAs(handle);
        catalog.TryGetDocument<MeshNoteDocument>(out var exact).Should().BeTrue();
        exact.Should().BeSameAs(handle);
        catalog.TryGetDocumentBySchema("tests.mesh_note.v1", out var byVersion).Should().BeTrue();
        byVersion.Should().BeSameAs(handle);
        catalog.DocumentBySchema("tests.mesh_note").Should().BeSameAs(handle);

        catalog.TryGetDocument<MeshNoteAliasDocument>(out var alias).Should().BeTrue();
        alias.DocumentId.Should().Be(handle.DocumentId);
        alias.SchemaName.Should().Be(handle.SchemaName);
        alias.SchemaVersion.Should().Be(handle.SchemaVersion);

        var aliasSnapshot = await catalog.LatestAsync<MeshNoteAliasDocument>();
        aliasSnapshot.Text.Should().Be("catalog-primary");
        aliasSnapshot.Revision.Should().Be(3);
        catalog.Latest<MeshNoteAliasDocument>().Text.Should().Be("catalog-primary");
    }

    [Test]
    public async Task DocumentHelpers_ProjectLatestSnapshotsFromMultipleHandles()
    {
        var primary = CultMesh.Document(
            "mesh.note.primary",
            CultMesh.Verse("starbridge", "unity-pilot"),
            _ => Task.FromResult(new MeshNoteDocument
            {
                Schema = "tests.mesh_note.v1",
                Text = "alpha",
                Revision = 3
            }),
            _ => new Subject<MeshNoteDocument>());
        var secondary = CultMesh.Document(
            "mesh.note.secondary",
            CultMesh.Verse("starbridge", "unity-pilot"),
            _ => Task.FromResult(new MeshOtherDocument
            {
                Schema = "tests.mesh_other.v1",
                Text = "beta"
            }),
            _ => new Subject<MeshOtherDocument>());

        var projected = await CultMesh.LatestAsync(
            primary,
            secondary,
            (first, second) => $"{first.Text}:{first.Revision}:{second.Text}");

        projected.Should().Be("alpha:3:beta");
    }

    [Test]
    public async Task DocumentHandle_ReadsWatchesAndReplacesCultCacheRecords()
    {
        var cache = new CultCache();
        var key = new CultRecordKey("mesh-note:cache");
        await cache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "cache-initial",
            Revision = 1
        }, new CultRecordHandle<MeshNoteDocument>(key));

        var handle = CultMesh.Document<MeshNoteDocument>(
            cache,
            key,
            CultMesh.Verse("starbridge", "unity-pilot"));
        var alias = handle.AsSchemaAlias<MeshNoteAliasDocument>();

        handle.CanReplace.Should().BeTrue();
        alias.CanReplace.Should().BeTrue();
        handle.CanSubmitPrediction.Should().BeFalse();
        alias.CanSubmitPrediction.Should().BeFalse();
        handle.DocumentId.Should().Be(key.Value);
        handle.Sources.Should().ContainSingle().Which.SchemaId.Should().Be(handle.SchemaId);

        var snapshot = await alias.LatestAsync();
        snapshot.Text.Should().Be("cache-initial");

        MeshNoteAliasDocument observed = null!;
        using var subscription = alias.Watch(value => observed = value);
        await alias.ReplaceAsync(new MeshNoteAliasDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "cache-replaced",
            Revision = 2
        });

        cache.Get<MeshNoteDocument>(key)!.Text.Should().Be("cache-replaced");
        observed.Text.Should().Be("cache-replaced");
        observed.Revision.Should().Be(2);

        var catalog = CultMesh.Documents(handle);
        catalog.CanReplace<MeshNoteAliasDocument>().Should().BeTrue();
        await catalog.ReplaceAsync(new MeshNoteAliasDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "cache-catalog-replaced",
            Revision = 3
        });
        cache.Get<MeshNoteDocument>(key)!.Text.Should().Be("cache-catalog-replaced");

        Func<Task> act = () => alias.SubmitPredictionAsync(new MeshNoteAliasDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "cache-predicted",
            Revision = 4
        });
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task DocumentHandle_SubmitsPredictionsThroughCultNetDatabase()
    {
        var cache = new CultCache();
        var schemaId = CultDocumentRegistry.Shared.GetRequired<MeshNoteDocument>().SchemaId;
        var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
        {
            RuntimeId = "pilot-a",
            Shards = new[]
            {
                new CultNetShardDescriptor(
                    "pilot-inputs",
                    "server",
                    epoch: 1,
                    isPrimary: false,
                    schemaIds: new[] { schemaId },
                    keyPrefix: "input:")
            },
            ClientAuthorityScopes = new[]
            {
                new CultNetClientAuthorityScope(
                    "pilot-a",
                    schemaIds: new[] { schemaId },
                    keyPrefix: "input:pilot-a")
            }
        });
        var key = new CultRecordKey("input:pilot-a:thermal");

        var handle = CultMesh.Document<MeshNoteDocument>(
            database,
            key,
            CultMesh.Verse("starbridge", "pilot-a"));
        var alias = handle.AsSchemaAlias<MeshNoteAliasDocument>();

        handle.CanReplace.Should().BeTrue();
        alias.CanSubmitPrediction.Should().BeTrue();
        alias.CanSet.Should().BeTrue();
        var catalog = CultMesh.Documents(handle);
        catalog.CanSubmitPrediction<MeshNoteAliasDocument>().Should().BeTrue();
        catalog.CanSet<MeshNoteAliasDocument>().Should().BeTrue();

        MeshNoteDocument observed = null!;
        using var subscription = handle.Watch(value => observed = value);
        await catalog.SetAsync(new MeshNoteAliasDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "predicted-thermal",
            Revision = 5
        });

        cache.Get<MeshNoteDocument>(key)!.Text.Should().Be("predicted-thermal");
        observed.Text.Should().Be("predicted-thermal");
        observed.Revision.Should().Be(5);
        (await handle.LatestAsync()).Text.Should().Be("predicted-thermal");

        var updated = await alias.UpdateAsync(value => new MeshNoteAliasDocument
        {
            Schema = value.Schema,
            Text = "updated-as-prediction",
            Revision = value.Revision + 1
        });
        updated.Text.Should().Be("updated-as-prediction");
        cache.Get<MeshNoteDocument>(key)!.Revision.Should().Be(6);
    }

    [Test]
    public async Task ReactiveDocument_CoalescesLocalEditsIntoOnePrediction()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var predictions = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            value =>
            {
                predictions.Add(value);
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });
        var catalog = CultMesh.Documents(handle);

        using var reactive = await catalog.ReactiveAsync<MeshNoteAliasDocument>(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });

        reactive.Current.Text.Should().Be("initial");
        reactive.Update(document =>
        {
            document.Text = "first-local-edit";
            document.Revision++;
        });
        reactive.Update(document =>
        {
            document.Text = "second-local-edit";
            document.Revision++;
        });

        predictions.Should().BeEmpty();

        await reactive.FlushAsync();

        predictions.Should().ContainSingle();
        predictions[0].Text.Should().Be("second-local-edit");
        predictions[0].Revision.Should().Be(3);
        current.Text.Should().Be("second-local-edit");
    }

    [Test]
    public async Task ReactiveDocument_TracksCanonicalSnapshotDuringDirtyPrediction()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var handle = CultMesh.Document(
            "mesh.note.reconciliation",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });

        reactive.Update(document =>
        {
            document.Text = "local-prediction";
            document.Revision = 2;
        });

        subject.OnNext(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "canonical-correction",
            Revision = 7
        });

        reactive.Current.Text.Should().Be("local-prediction");
        reactive.Reconciliation.Should().NotBeNull();
        reactive.Reconciliation!.Canonical.Text.Should().Be("canonical-correction");
        reactive.Reconciliation.Predicted.Text.Should().Be("local-prediction");
    }

    [Test]
    public async Task ReactiveDocument_AutoFlushesOnceAfterDebounceWindow()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var predictions = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.debounce",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            value =>
            {
                predictions.Add(value);
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMilliseconds(25)
            });

        reactive.Update(document =>
        {
            document.Text = "coalesced-one";
            document.Revision++;
        });
        reactive.Update(document =>
        {
            document.Text = "coalesced-two";
            document.Revision++;
        });

        await WaitForAsync(() => predictions.Count == 1);

        predictions[0].Text.Should().Be("coalesced-two");
        predictions[0].Revision.Should().Be(3);
        reactive.IsDirty.Should().BeFalse();
    }

    [Test]
    public async Task ReactiveDocument_UsesReplacementWhenPredictionIsUnavailable()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var replacements = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.replace",
            CultMesh.Verse("starbridge", "daemon"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                replacements.Add(value);
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });

        reactive.Update(document =>
        {
            document.Text = "authoritative-replace";
            document.Revision = 10;
        });
        await reactive.FlushAsync();

        replacements.Should().ContainSingle();
        replacements[0].Text.Should().Be("authoritative-replace");
        current.Revision.Should().Be(10);
    }

    [Test]
    public async Task ReactiveDocument_ReadOnlyHandleRejectsFlush()
    {
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var handle = CultMesh.Document(
            "mesh.note.reactive.readonly",
            CultMesh.Verse("starbridge", "observer"),
            _ => Task.FromResult(current),
            _ => new Subject<MeshNoteDocument>());

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });
        reactive.Update(document => document.Text = "not-allowed");

        Func<Task> act = () => reactive.FlushAsync();

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task ReactiveDocument_DisposeSuppressesScheduledFlush()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var predictions = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.dispose",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            value =>
            {
                predictions.Add(value);
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMilliseconds(25)
            });
        reactive.Update(document => document.Text = "disposed-before-flush");
        reactive.Dispose();

        await Task.Delay(100);

        predictions.Should().BeEmpty();
    }

    [Test]
    public async Task ReactiveDocument_CanAdoptCanonicalSnapshotWhileDirty()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var handle = CultMesh.Document(
            "mesh.note.reactive.adopt-canonical",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1),
                ReplaceDirtyCurrentOnCanonicalSnapshot = true
            });
        reactive.Update(document => document.Text = "local-prediction");

        subject.OnNext(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "canonical-wins",
            Revision = 9
        });

        reactive.Current.Text.Should().Be("canonical-wins");
        reactive.Current.Revision.Should().Be(9);
        reactive.Reconciliation.Should().BeNull();
    }

    [Test]
    public async Task ReactiveDocument_RefreshClearsDirtyLocalState()
    {
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var replacements = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.refresh",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => new Subject<MeshNoteDocument>(),
            value =>
            {
                replacements.Add(value);
                current = value;
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });
        reactive.Update(document => document.Text = "local-dirty");
        current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "fresh-canonical",
            Revision = 5
        };

        await reactive.RefreshAsync();
        await reactive.FlushAsync();

        reactive.Current.Text.Should().Be("fresh-canonical");
        reactive.IsDirty.Should().BeFalse();
        replacements.Should().BeEmpty();
    }

    [Test]
    public async Task ReactiveDocument_SyncsSameSchemaAliasesAcrossRuntimeHandles()
    {
        var cache = new CultCache();
        var schemaId = CultDocumentRegistry.Shared.GetRequired<MeshNoteDocument>().SchemaId;
        var database = new CultNetDatabase(cache, new CultNetDatabaseOptions
        {
            RuntimeId = "pilot-a",
            Shards = new[]
            {
                new CultNetShardDescriptor(
                    "pilot-inputs",
                    "server",
                    epoch: 1,
                    isPrimary: false,
                    schemaIds: new[] { schemaId },
                    keyPrefix: "input:")
            },
            ClientAuthorityScopes = new[]
            {
                new CultNetClientAuthorityScope(
                    "pilot-a",
                    schemaIds: new[] { schemaId },
                    keyPrefix: "input:pilot-a")
            }
        });
        var key = new CultRecordKey("input:pilot-a:reactive");
        await cache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        }, new CultRecordHandle<MeshNoteDocument>(key));

        var pilotHandle = CultMesh.Document<MeshNoteDocument>(
            database,
            key,
            CultMesh.Verse("starbridge", "pilot-a"));
        var commanderHandle = CultMesh.Document<MeshNoteDocument>(
            database,
            key,
            CultMesh.Verse("starbridge", "commander-rts"));
        var observedByCommander = new List<MeshNoteAliasDocument>();
        using var commanderSubscription = commanderHandle
            .AsSchemaAlias<MeshNoteAliasDocument>()
            .Watch(observedByCommander.Add);
        using var pilotReactive = await CultMesh
            .Documents(pilotHandle)
            .ReactiveAsync<MeshNoteAliasDocument>(
                new CultMeshReactiveDocumentOptions
                {
                    FlushDelay = TimeSpan.FromMinutes(1)
                });

        pilotReactive.Update(document =>
        {
            document.Text = "pilot-predicted";
            document.Revision = 2;
        });
        await pilotReactive.FlushAsync();

        await WaitForAsync(() => observedByCommander.Any(document => document.Text == "pilot-predicted"));

        commanderHandle.Context.RuntimeId.Should().Be("commander-rts");
        observedByCommander.Last().Revision.Should().Be(2);
        (await commanderHandle.AsSchemaAlias<MeshNoteAliasDocument>().LatestAsync()).Text.Should().Be("pilot-predicted");
    }

    [Test]
    public async Task ReactiveDocument_MarkDirtyFlushesDirectMemberEdits()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var predictions = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.mark-dirty",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            value =>
            {
                predictions.Add(value);
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });

        reactive.Current.Text = "direct-member-edit";
        reactive.Current.Revision = 12;
        reactive.MarkDirty();
        await reactive.FlushAsync();

        predictions.Should().ContainSingle();
        predictions[0].Text.Should().Be("direct-member-edit");
        predictions[0].Revision.Should().Be(12);
    }

    [Test]
    public async Task ReactiveDocument_CleanFlushDoesNotWrite()
    {
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var writes = 0;
        var handle = CultMesh.Document(
            "mesh.note.reactive.clean-flush",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => new Subject<MeshNoteDocument>(),
            value =>
            {
                writes++;
                current = value;
                return Task.CompletedTask;
            });

        using var reactive = await handle.ReactiveAsync();

        await reactive.FlushAsync();

        writes.Should().Be(0);
        reactive.IsDirty.Should().BeFalse();
    }

    [Test]
    public async Task ReactiveDocument_FlushesAgainWhenEditedDuringInFlightFlush()
    {
        var subject = new Subject<MeshNoteDocument>();
        var current = new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "initial",
            Revision = 1
        };
        var allowFirstWrite = new TaskCompletionSource<bool>();
        var firstWriteStarted = new TaskCompletionSource<bool>();
        var predictions = new List<MeshNoteDocument>();
        var handle = CultMesh.Document(
            "mesh.note.reactive.in-flight",
            CultMesh.Verse("starbridge", "pilot-a"),
            _ => Task.FromResult(current),
            _ => subject,
            value =>
            {
                current = value;
                subject.OnNext(value);
                return Task.CompletedTask;
            },
            async value =>
            {
                predictions.Add(value);
                if (predictions.Count == 1)
                {
                    firstWriteStarted.SetResult(true);
                    await allowFirstWrite.Task.ConfigureAwait(false);
                }

                current = value;
                subject.OnNext(value);
            });

        using var reactive = await handle.ReactiveAsync(
            new CultMeshReactiveDocumentOptions
            {
                FlushDelay = TimeSpan.FromMinutes(1)
            });
        reactive.Update(document =>
        {
            document.Text = "first";
            document.Revision = 2;
        });
        var flush = reactive.FlushAsync();
        await firstWriteStarted.Task;

        reactive.Update(document =>
        {
            document.Text = "second";
            document.Revision = 3;
        });
        allowFirstWrite.SetResult(true);
        await flush;

        predictions.Should().HaveCount(2);
        predictions[0].Text.Should().Be("first");
        predictions[1].Text.Should().Be("second");
        current.Revision.Should().Be(3);
    }

    [Test]
    public async Task DocumentHandle_ReadsSchemaPublicationsFromSingleFileStores()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "publication.ccmp");
        var key = new CultRecordKey("mesh-note:publication");

        var writerCache = new CultCache();
        writerCache.AddBackingStore(new SingleFileMessagePackBackingStore(filePath));
        await writerCache.UpsertAsync(typeof(MeshPublicationNoteDocument), new MeshPublicationNoteDocument
        {
            Schema = "tests.mesh_publication_note.v1",
            Text = "published",
            Revision = 1
        }, key);
        writerCache.FlushAllBackingStores();
        File.Exists(filePath).Should().BeTrue();

        var directReadCache = new CultCache();
        directReadCache.AddBackingStore(new SingleFileMessagePackBackingStore(filePath));
        await directReadCache.PullAllBackingStoresAsync();
        directReadCache.Get<MeshPublicationNoteDocument>(key)!.Text.Should().Be("published");

        var handle = CultMesh.DocumentFromPublication<MeshPublicationNoteDocument>(
            CultMeshDocumentPublicationSource.SingleFile(filePath),
            key,
            CultMesh.Verse("starbridge", "unity-pilot"),
            new CultMeshStoreDocumentOptions
            {
                DocumentId = "daemon:tests.mesh_note.latest",
                SourceId = "daemon:tests.mesh_note.latest.v1",
                PollInterval = TimeSpan.FromMilliseconds(10)
            });
        var observed = new List<MeshPublicationNoteDocument>();
        using var subscription = handle.Watch(value => observed.Add(value));

        (await handle.LatestAsync()).Text.Should().Be("published");
        handle.CanReplace.Should().BeFalse();
        handle.CanSubmitPrediction.Should().BeFalse();
        handle.DocumentId.Should().Be("daemon:tests.mesh_note.latest");
        handle.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        handle.Sources.Should().ContainSingle().Which.SourceId.Should().Be("daemon:tests.mesh_note.latest.v1");

        var republisher = new CultCache();
        republisher.AddBackingStore(new SingleFileMessagePackBackingStore(filePath));
        await republisher.UpsertAsync(typeof(MeshPublicationNoteDocument), new MeshPublicationNoteDocument
        {
            Schema = "tests.mesh_publication_note.v1",
            Text = "republished",
            Revision = 2
        }, key);
        republisher.FlushAllBackingStores();

        await WaitForAsync(() => observed.Any(value => value.Text == "republished"));
        (await handle.LatestAsync()).Revision.Should().Be(2);
    }

    [Test]
    public async Task SingleFileDocumentHelpers_RoundTripTypedDocuments()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "single-document.ccmp");
        var key = new CultRecordKey("mesh-note:single-document");

        CultMesh.WriteSingleFileDocument(
            filePath,
            key,
            new MeshPublicationNoteDocument
            {
                Schema = "tests.mesh_publication_note.v1",
                Text = "published-directly",
                Revision = 3
            },
            storedAt: "2026-06-27T12:00:00.0000000Z");

        var document = CultMesh.ReadSingleFileDocument<MeshPublicationNoteDocument>(filePath, key);

        document.Text.Should().Be("published-directly");
        document.Revision.Should().Be(3);

        var cache = new CultCache();
        cache.AddBackingStore(new SingleFileMessagePackBackingStore(filePath));
        await cache.PullAllBackingStoresAsync();
        cache.Get<MeshPublicationNoteDocument>(key)!.Text.Should().Be("published-directly");
    }

    [Test]
    public void SingleFileDocumentHelpers_ReadLegacySingleDocumentSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "legacy-single-document.ccmp");
        var key = new CultRecordKey("mesh-note:legacy-single-document");
        var descriptor = CultDocumentRegistry.Shared.GetRequired<MeshPublicationNoteDocument>();
        var payload = CultDocumentMessagePackSerialization.Serialize(new MeshPublicationNoteDocument
        {
            Schema = "tests.mesh_publication_note.v1",
            Text = "legacy-published",
            Revision = 4
        });

        File.WriteAllBytes(filePath, WriteLegacySingleDocumentSnapshot(
            key.Value,
            descriptor.SchemaId,
            descriptor.SchemaName,
            descriptor.SchemaVersion,
            "2026-06-27T12:00:00.0000000Z",
            payload));

        var document = CultMesh.ReadSingleFileDocument<MeshPublicationNoteDocument>(filePath, key);

        document.Text.Should().Be("legacy-published");
        document.Revision.Should().Be(4);
    }

    [Test]
    public async Task DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments()
    {
        var cache = new CultCache();
        var key = new CultRecordKey("mesh-note:remote");
        await cache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "remote-snapshot",
            Revision = 9
        }, new CultRecordHandle<MeshNoteDocument>(key));
        var registry = new CultNetDocumentRegistry(CultDocumentRegistry.Shared)
            .Register(CultNetDocumentBinding.ForDocument<MeshNoteDocument>(CultDocumentRegistry.Shared));
        var requests = new List<CultNetSnapshotRequestMessage>();

        var handle = CultMesh.DocumentFromPublication<MeshNoteDocument>(
            CultMeshDocumentPublicationSource.PeerSnapshot(
                () => new MeshSnapshotSchemaClient(request =>
                {
                    requests.Add(request);
                    return registry.CreateRawSnapshotResponse(cache, request.MessageId, request);
                }),
                "cultnet://snapshot.test:3075"),
            key,
            CultMesh.Verse("starbridge", "unity-pilot"),
            peerOptions: new CultMeshPeerSnapshotDocumentOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
                MessageIdPrefix = "mesh-test-snapshot"
            });
        var alias = handle.AsSchemaAlias<MeshNoteAliasDocument>();
        var catalog = CultMesh.Documents(handle);

        var snapshot = await alias.LatestAsync();

        snapshot.Text.Should().Be("remote-snapshot");
        snapshot.Revision.Should().Be(9);
        handle.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Network);
        handle.Sources.Should().ContainSingle().Which.SourceId.Should().Be(key.Value);
        requests.Should().ContainSingle();
        requests[0].SchemaIds.Should().ContainSingle().Which
            .Should().Be(CultDocumentRegistry.Shared.GetRequired<MeshNoteDocument>().SchemaId);
        requests[0].RecordKeys.Should().ContainSingle().Which.Should().Be(key.Value);
        (await catalog.LatestAsync<MeshNoteAliasDocument>()).Text.Should().Be("remote-snapshot");

        var aliasSchemaHandle = CultMesh.DocumentFromPeerSnapshot<MeshNoteDocument>(
            _ => Task.FromResult(new CultNetSnapshotResponseRawMessage
            {
                MessageId = "alias-schema",
                Documents = new[]
                {
                    new CultNetRawDocumentRecord
                    {
                        SchemaId = "remote.generated.mesh_note_alias.v99",
                        RecordKey = key.Value,
                        StoredAt = DateTimeOffset.UtcNow.ToString("O"),
                        PayloadEncoding = "messagepack",
                        Payload = CultDocumentMessagePackSerialization.SerializeUntyped(
                            new MeshNoteDocument
                            {
                                Schema = "tests.mesh_note.v1",
                                Text = "record-key-fallback",
                                Revision = 10
                            },
                            typeof(MeshNoteDocument))
                    }
                }
            }),
            key.Value,
            CultMesh.Verse("starbridge", "unity-pilot"));

        (await aliasSchemaHandle.LatestAsync()).Text.Should().Be("record-key-fallback");
    }

    [Test]
    public async Task SnapshotHelpers_FetchApplyAndDecodeScopedSnapshots()
    {
        var sourceCache = new CultCache();
        var targetCache = new CultCache();
        var registry = new CultNetDocumentRegistry(CultDocumentRegistry.Shared);
        registry.Register(CultNetDocumentBinding.ForDocument<MeshPublicationNoteDocument>(sourceCache.Registry));
        var key = new CultRecordKey("mesh-note:snapshot-helper");
        await sourceCache.UpsertAsync(new MeshPublicationNoteDocument
        {
            Schema = "tests.mesh_publication_note.v1",
            Text = "snapshot-helper",
            Revision = 11
        }, new CultRecordHandle<MeshPublicationNoteDocument>(key));
        var requests = new List<CultNetSnapshotRequestMessage>();
        var endpoint = "cultnet://snapshot-helper.test:3075";
        var options = new CultMeshSnapshotRequestOptions
        {
            RecordKeys = new[] { key.Value },
            ShardId = "primary",
            ShardEpoch = 7,
            MessageIdPrefix = "mesh-test-scoped-snapshot",
            CreateClient = () => new MeshSnapshotSchemaClient(request =>
            {
                requests.Add(request);
                return registry.CreateRawSnapshotResponse(sourceCache, request.MessageId, request);
            })
        };

        var snapshot = await CultMesh.FetchSnapshotAsync(endpoint, options);
        var decoded = await CultMesh.FetchSnapshotDocumentsAsync<MeshPublicationNoteDocument>(
            endpoint,
            options,
            registry);
        using var node = await CultMesh.CreateNodeAsync(
            Path.Combine(Path.GetTempPath(), $"cultmesh-snapshot-helper-{Guid.NewGuid():N}.ccmp"),
            new CultMeshNodeOptions
            {
                StartServer = false,
                CacheOptions = new CultCacheOpenOptions
                {
                    Registry = targetCache.Registry,
                    PullOnOpen = false
                },
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    DocumentRegistry = registry
                }
            });
        var applied = await CultMesh.ApplySnapshotAsync(node, endpoint, options);

        snapshot.Documents.Should().ContainSingle().Which.RecordKey.Should().Be(key.Value);
        decoded.Should().ContainSingle().Which.Text.Should().Be("snapshot-helper");
        applied.Should().ContainSingle();
        node.Cache.Get<MeshPublicationNoteDocument>(key)!.Revision.Should().Be(11);
        requests.Should().HaveCount(3);
        requests.Should().OnlyContain(request =>
            request.RecordKeys!.SequenceEqual(new[] { key.Value }) &&
            request.ShardId == "primary" &&
            request.ShardEpoch == 7 &&
            request.MessageId.StartsWith("mesh-test-scoped-snapshot:", StringComparison.Ordinal));
    }

    [Test]
    public async Task DocumentRegistryHelpers_CreateTypedNetworkRegistriesForAliases()
    {
        var sourceCache = new CultCache();
        var cacheRegistry = CultMesh.CreateCultCacheDocumentRegistry(
            typeof(MeshNoteDocument),
            typeof(MeshNoteAliasDocument));
        var networkRegistry = CultMesh.CreateCultNetDocumentRegistry(
            new[] { typeof(MeshNoteDocument), typeof(MeshNoteAliasDocument) },
            cacheRegistry);
        var key = new CultRecordKey("mesh-note:registry-helper");
        await sourceCache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "registry-helper",
            Revision = 23
        }, new CultRecordHandle<MeshNoteDocument>(key));
        var endpoint = "cultnet://registry-helper.test:3077";

        var documents = await CultMesh.FetchSnapshotDocumentsAsync<MeshNoteAliasDocument>(
            endpoint,
            new CultMeshSnapshotRequestOptions
            {
                RecordKeys = new[] { key.Value },
                CreateClient = () => new MeshSnapshotSchemaClient(request =>
                    networkRegistry.CreateRawSnapshotResponse(sourceCache, request.MessageId, request))
            },
            networkRegistry);

        var schemaId = cacheRegistry.GetRequired<MeshNoteDocument>().SchemaId;
        cacheRegistry.GetRequired<MeshNoteAliasDocument>().SchemaId.Should().Be(schemaId);
        networkRegistry.GetByDocumentType(typeof(MeshNoteDocument)).Should().NotBeNull();
        networkRegistry.GetByDocumentType(typeof(MeshNoteAliasDocument)).Should().NotBeNull();
        networkRegistry.GetBySchemaId(schemaId)!.DocumentType.Should().Be(typeof(MeshNoteDocument));
        documents.Should().ContainSingle().Which.Text.Should().Be("registry-helper");
    }

    [Test]
    public async Task SnapshotEndpoint_ProvidesTypedHandlesAndSchemaAliases()
    {
        var sourceCache = new CultCache();
        var registry = new CultNetDocumentRegistry(CultDocumentRegistry.Shared);
        registry.Register(CultNetDocumentBinding.ForDocument<MeshNoteDocument>(sourceCache.Registry));
        var key = new CultRecordKey("mesh-note:snapshot-endpoint");
        await sourceCache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "snapshot-endpoint",
            Revision = 19
        }, new CultRecordHandle<MeshNoteDocument>(key));
        var requests = new List<CultNetSnapshotRequestMessage>();
        var endpoint = "cultnet://snapshot-endpoint.test:3076";
        var surface = CultMesh.SnapshotEndpoint(
            endpoint,
            new CultMeshSnapshotEndpointOptions
            {
                Context = CultMesh.Verse("starbridge", "browser-starfire").Context,
                DocumentRegistry = registry,
                Request = new CultMeshSnapshotRequestOptions
                {
                    ShardId = "primary",
                    ShardEpoch = 9,
                    CreateClient = () => new MeshSnapshotSchemaClient(request =>
                    {
                        requests.Add(request);
                        return registry.CreateRawSnapshotResponse(sourceCache, request.MessageId, request);
                    })
                }
            });

        var fetchedAlias = await surface.FetchDocumentAsync<MeshNoteAliasDocument>(key.Value);
        using var node = await CultMesh.CreateNodeAsync(
            Path.Combine(Path.GetTempPath(), $"cultmesh-snapshot-endpoint-{Guid.NewGuid():N}.ccmp"),
            new CultMeshNodeOptions
            {
                StartServer = false,
                CacheOptions = new CultCacheOpenOptions
                {
                    Registry = sourceCache.Registry,
                    PullOnOpen = false
                },
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    DocumentRegistry = registry
                }
            });
        var syncedAlias = await surface.SyncDocumentAsync<MeshNoteAliasDocument>(node, key.Value);
        var aliasHandle = surface.Document<MeshNoteAliasDocument>(key.Value);
        var aliasLatest = await aliasHandle.LatestAsync();
        var catalog = surface.Documents(
            CultMesh.SnapshotDocument<MeshNoteDocument>(key.Value, "daemon:mesh-note:snapshot-endpoint"));
        var catalogAlias = await catalog.LatestAsync<MeshNoteAliasDocument>();
        await sourceCache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "snapshot-endpoint-synced",
            Revision = 20
        }, new CultRecordHandle<MeshNoteDocument>(key));
        var syncedEndpoint = surface.SyncTo(node);
        var syncedHandle = syncedEndpoint.Document<MeshNoteAliasDocument>(key.Value);
        var syncedLatest = await syncedHandle.LatestAsync();
        await sourceCache.UpsertAsync(new MeshNoteDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "snapshot-endpoint-catalog-synced",
            Revision = 21
        }, new CultRecordHandle<MeshNoteDocument>(key));
        var syncedCatalog = syncedEndpoint.Documents(
            CultMesh.SnapshotDocument<MeshNoteDocument>(key.Value));
        var syncedCatalogAlias = await syncedCatalog.LatestAsync<MeshNoteAliasDocument>();

        fetchedAlias.Text.Should().Be("snapshot-endpoint");
        syncedAlias.Text.Should().Be("snapshot-endpoint");
        aliasLatest.Revision.Should().Be(19);
        catalog.Document<MeshNoteDocument>().DocumentId.Should().Be("daemon:mesh-note:snapshot-endpoint");
        catalogAlias.Text.Should().Be("snapshot-endpoint");
        syncedLatest.Text.Should().Be("snapshot-endpoint-synced");
        node.Cache.Get<MeshNoteDocument>(key)!.Revision.Should().Be(21);
        syncedCatalogAlias.Text.Should().Be("snapshot-endpoint-catalog-synced");
        requests.Should().HaveCount(6);
        requests.Should().OnlyContain(request =>
            request.SchemaIds!.SequenceEqual(new[]
            {
                CultDocumentRegistry.Shared.GetRequired<MeshNoteAliasDocument>().SchemaId
            }) &&
            request.RecordKeys!.SequenceEqual(new[] { key.Value }) &&
            request.ShardId == "primary" &&
            request.ShardEpoch == 9 &&
            request.MessageId.StartsWith("cultmesh:browser-starfire:snapshot:", StringComparison.Ordinal));
    }

    [Test]
    public async Task CollectionHandle_ReadsAndWatchesDatabaseCollectionsByIndex()
    {
        var cache = new CultCache();
        var database = new CultNetDatabase(cache);
        var teamId = Guid.NewGuid().ToString("D");
        var collection = CultMesh.CollectionByIndex<MeshIndexedPlayer>(
            database,
            "TeamId",
            teamId);
        var changes = new List<CultMeshCollectionChange<MeshIndexedPlayer>>();
        using var subscription = collection.WatchChanges(change => changes.Add(change));

        await database.PutAsync(
            new CultRecordKey("player:one"),
            new MeshIndexedPlayer
            {
                Name = "One",
                TeamId = teamId,
                Score = 10
            });
        await database.PutAsync(
            new CultRecordKey("player:two"),
            new MeshIndexedPlayer
            {
                Name = "Two",
                TeamId = Guid.NewGuid().ToString("D"),
                Score = 20
            });

        collection.CollectionId.Should().Contain(teamId);
        collection.SchemaName.Should().Be("tests.mesh_indexed_player");
        collection.RouteHint.Kind.Should().Be(CultMeshLocalityKind.Automatic);
        collection.Sources.Should().ContainSingle().Which.SchemaId.Should().Be(collection.SchemaId);

        var snapshot = await collection.LatestAsync();
        snapshot.Should().ContainSingle().Which.Name.Should().Be("One");
        changes.Should().ContainSingle();
        changes[0].Kind.Should().Be(CultMeshCollectionChangeKind.Added);
        changes[0].Document!.Name.Should().Be("One");
    }

    [Test]
    public async Task CollectionHandle_SupportsSameSchemaAliases()
    {
        var cache = new CultCache();
        await cache.UpsertAsync(
            new MeshNoteDocument
            {
                Schema = "tests.mesh_note.v1",
                Text = "collection-alias",
                Revision = 4
            },
            new CultRecordHandle<MeshNoteDocument>(new CultRecordKey("mesh-note:collection")));

        var collection = CultMesh.Collection<MeshNoteDocument>(cache);
        var alias = collection.AsSchemaAlias<MeshNoteAliasDocument>();

        alias.CollectionId.Should().Be(collection.CollectionId);
        alias.SchemaName.Should().Be(collection.SchemaName);

        var snapshot = await alias.LatestAsync();
        snapshot.Should().ContainSingle().Which.Text.Should().Be("collection-alias");
    }

    [Test]
    public async Task DocumentHandle_ReplacesThroughCultMeshNodeDatabase()
    {
        using var directory = new TemporaryDirectory();
        using var node = await CultMesh.CreateNodeAsync(
            Path.Combine(directory.Path, "mesh-node.cc"),
            new CultMeshNodeOptions
            {
                DatabaseOptions = new CultNetDatabaseOptions
                {
                    RuntimeId = "node-runtime",
                    Shards = new[]
                    {
                        new CultNetShardDescriptor(
                            "mesh",
                            "node-runtime",
                            epoch: 1,
                            isPrimary: true,
                            schemaIds: new[] { CultDocumentRegistry.Shared.GetRequired<MeshNoteDocument>().SchemaId })
                    }
                },
                StartServer = false
            });
        var key = new CultRecordKey("mesh-note:node");

        var handle = CultMesh.Document<MeshNoteDocument>(
            node,
            key,
            CultMesh.Verse("starbridge", "unity-pilot"));
        var alias = handle.AsSchemaAlias<MeshNoteAliasDocument>();

        MeshNoteDocument observed = null!;
        using var subscription = handle.Watch(value => observed = value);
        await alias.ReplaceAsync(new MeshNoteAliasDocument
        {
            Schema = "tests.mesh_note.v1",
            Text = "node-replaced",
            Revision = 3
        });

        var snapshot = await handle.LatestAsync();
        snapshot.Text.Should().Be("node-replaced");
        observed.Text.Should().Be("node-replaced");
        observed.Revision.Should().Be(3);
    }

    [Test]
    public void SurfaceCatalog_DescribesTypedRuntimeSurfaces()
    {
        var route = new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "co-located frame slab");
        var sources = new[]
        {
            CultMesh.ProjectionSource(
                "daemon:aetheria.frame.latest.v1",
                schemaId: "gamecult.aetheria.daemon_frame.v1")
        };
        var query = new CultMeshQuerySurface<MeshViewportRequest, string[]>(
            "aetheria.zone.objects.visible",
            (_request, _context) => Task.FromResult(Array.Empty<string>()),
            sources: sources,
            routeHint: route);
        var feed = CultMesh.LiveFeed<MeshViewportRequest, string>(
            "aetheria.rts.viewport.feed",
            (_request, _context) => Task.FromResult("frame"),
            sources: sources,
            routeHint: route);
        var operation = new CultMeshOperationHandle<MeshMoveRequest, CultMeshOperationReceipt>(
            "aetheria.entity.pilot.move",
            (_request, _context) => Task.FromResult(new CultMeshOperationReceipt("aetheria.entity.pilot.move", true)));
        var pointer = CultMesh.StatePointer(
            "aetheria.selection.current",
            async () => "entity:ship:1",
            () => new Subject<string>(),
            route,
            sources);
        var document = CultMesh.Document(
            "daemon:aetheria.frame.latest.v1",
            CultMesh.Verse("aetheria.local", "unity-raven").Context,
            _context => Task.FromResult(new MeshNoteDocument { Schema = "frame", Text = "current", Revision = 1 }),
            _context => new Subject<MeshNoteDocument>(),
            sources,
            route);
        var collection = new CultMeshCollectionHandle<MeshNoteDocument>(
            "daemon:aetheria.contacts.v1",
            () => Task.FromResult<IReadOnlyList<MeshNoteDocument>>(Array.Empty<MeshNoteDocument>()),
            () => new Subject<CultMeshCollectionChange<MeshNoteDocument>>(),
            sources,
            route);
        var nativeView = new CultMeshNativeSliceViewDescriptor(
            "aetheria.zone.render",
            "gamecult.aetheria.render_body.v1",
            rowCount: 128,
            new[] { CultMeshNativeSliceColumn.For<MeshVec2>("position") },
            route);

        var catalog = CultMesh.DescribeSurfaceCatalog(
            "gamecult.aetheria.rts.surfaces.v1",
            new[]
            {
                CultMesh.DescribeSurface(query),
                CultMesh.DescribeSurface(feed),
                CultMesh.DescribeSurface(operation),
                CultMesh.DescribeSurface(document),
                CultMesh.DescribeSurface(collection),
                CultMesh.DescribeSurface(pointer),
                CultMesh.DescribeSurface(nativeView)
            });

        catalog.CatalogId.Should().Be("gamecult.aetheria.rts.surfaces.v1");
        catalog.Surfaces.Should().HaveCount(7);
        catalog.Surfaces[0].Kind.Should().Be(CultMeshSurfaceKind.Query);
        catalog.Surfaces[0].SurfaceId.Should().Be("aetheria.zone.objects.visible");
        catalog.Surfaces[0].RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        catalog.Surfaces[0].Sources.Should().HaveCount(1);
        catalog.Find("aetheria.rts.viewport.feed")!.Kind.Should().Be(CultMeshSurfaceKind.LiveFeed);
        catalog.Find("aetheria.entity.pilot.move")!.Kind.Should().Be(CultMeshSurfaceKind.Operation);
        catalog.Find("daemon:aetheria.frame.latest.v1")!.Kind.Should().Be(CultMeshSurfaceKind.Document);
        catalog.Find("daemon:aetheria.contacts.v1")!.Kind.Should().Be(CultMeshSurfaceKind.Collection);
        catalog.Find("aetheria.selection.current")!.Kind.Should().Be(CultMeshSurfaceKind.StatePointer);
        catalog.Find("aetheria.selection.current")!.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        catalog.Find("aetheria.selection.current")!.Sources.Should().ContainSingle();
        catalog.Find("aetheria.zone.render")!.Kind.Should().Be(CultMeshSurfaceKind.NativeSliceView);
        catalog.Find("aetheria.zone.render")!.RouteHint.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        catalog.Find("missing").Should().BeNull();
        catalog.FindByKind(CultMeshSurfaceKind.Operation)
            .Should()
            .ContainSingle()
            .Which
            .SurfaceId
            .Should()
            .Be("aetheria.entity.pilot.move");
        catalog.FindByKind(CultMeshSurfaceKind.Query).Should().ContainSingle();

        var index = CultMesh.DescribeSurfaceCatalogIndex(catalog);
        index.CatalogId.Should().Be(catalog.CatalogId);
        index.Queries.Should().ContainSingle().Which.SurfaceId.Should().Be("aetheria.zone.objects.visible");
        index.LiveFeeds.Should().ContainSingle().Which.SurfaceId.Should().Be("aetheria.rts.viewport.feed");
        index.Operations.Should().ContainSingle().Which.SurfaceId.Should().Be("aetheria.entity.pilot.move");
        index.Documents.Should().ContainSingle().Which.SurfaceId.Should().Be("daemon:aetheria.frame.latest.v1");
        index.Collections.Should().ContainSingle().Which.SurfaceId.Should().Be("daemon:aetheria.contacts.v1");
        index.StatePointers.Should().ContainSingle().Which.SurfaceId.Should().Be("aetheria.selection.current");
        index.NativeSliceViews.Should().ContainSingle().Which.SurfaceId.Should().Be("aetheria.zone.render");
        index.ProjectionRecipes.Should().BeEmpty();
    }

    [Test]
    public async Task PollingQueryWatcher_TurnsSnapshotsIntoReactiveFeed()
    {
        var frameId = 0;
        var observed = new List<int>();
        var feed = CultMesh.LiveFeed<MeshViewportRequest, int>(
            "aetheria.rts.viewport.feed",
            (_request, _context) => Task.FromResult(frameId),
            CultMesh.PollingQueryWatcher<MeshViewportRequest, int>(
                (_request, _context) => Task.FromResult(frameId),
                new CultMeshPollingWatchOptions<int>(TimeSpan.FromMilliseconds(5))));

        using (feed
            .Watch(new MeshViewportRequest(-1, 1), CultMeshQueryContext.ForRuntime("browser-starfire"))
            .Subscribe(value => observed.Add(value)))
        {
            await Task.Delay(35);
            frameId = 1;
            await Task.Delay(35);
            frameId = 1;
            await Task.Delay(25);
            frameId = 2;
            await Task.Delay(35);
        }

        frameId = 3;
        await Task.Delay(25);

        observed.Should().Equal(0, 1, 2);
    }

    [Test]
    public void NativeSliceDescriptor_DescribesSharedColumnsWithoutOpeningTransport()
    {
        var descriptor = new CultMeshNativeSliceViewDescriptor(
            "aetheria.zone.render",
            "gamecult.aetheria.render_body.v1",
            rowCount: 128,
            new[]
            {
                CultMeshNativeSliceColumn.For<MeshVec2>("position"),
                CultMeshNativeSliceColumn.For<MeshVec2>("velocity")
            },
            new CultMeshRouteHint(CultMeshLocalityKind.SharedMemory, "CultCache slab"),
            nativeHandle: "cultcache-slab:aetheria-zone-render");

        descriptor.Route.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        descriptor.Columns.Should().HaveCount(2);
        descriptor.Columns[0].Name.Should().Be("position");
        descriptor.DenseRowStrideBytes.Should().Be(16);
        descriptor.FindColumn("velocity")!.ElementSizeBytes.Should().Be(8);

        var diagnostic = CultMesh.DescribeNativeSliceView(descriptor);
        diagnostic.ViewId.Should().Be("aetheria.zone.render");
        diagnostic.SchemaId.Should().Be("gamecult.aetheria.render_body.v1");
        diagnostic.RowCount.Should().Be(128);
        diagnostic.Route.Kind.Should().Be(CultMeshLocalityKind.SharedMemory);
        diagnostic.NativeHandle.Should().Be("cultcache-slab:aetheria-zone-render");
        diagnostic.DenseRowStrideBytes.Should().Be(16);
        diagnostic.Columns.Should().HaveCount(2);
        diagnostic.Columns.Should().NotBeSameAs(descriptor.Columns);
    }

    [Test]
    public async Task ManagedDocument_Commits_Through_MeshDatabase_And_Watches_Networked_Updates()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"cultmesh-managed-{Guid.NewGuid():N}.ccmp");

        try
        {
            using var node = await CultMesh.CreateNodeAsync(
                filePath,
                new CultMeshNodeOptions { StartServer = false });
            var key = new CultRecordKey("player:alice");
            var document = node.Database.Document<MeshManagedPlayer>(key);
            MeshManagedPlayer observed = null!;
            using var subscription = document.Watch().Subscribe(value => observed = value);

            await document.ReplaceAsync(new MeshManagedPlayer
            {
                Name = "alice",
                PositionX = 4,
                Health = 100
            });
            await node.Database.PutAsync(key, new MeshManagedPlayer
            {
                Name = "alice",
                PositionX = 8,
                Health = 75
            });

            document.Value.Should().NotBeNull();
            document.Value!.Health.Should().Be(75);
            observed.Should().NotBeNull();
            observed!.PositionX.Should().Be(8);
            node.Cache.Soa<MeshManagedPlayer>().Column<int>(nameof(MeshManagedPlayer.Health)).Span.ToArray()
                .Should()
                .Equal(75);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Test]
    public void NegotiatesGpuTextureStreamsWithoutForcingCopies()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        var stream = new CultMeshStreamDescriptor(
            "mimir:kiyo-pro:rgba",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Video,
            new CultMeshStreamClock("mimir:clock", "kiyo-pro", sampleRate: 90_000, confidence: 0.92d),
            new[]
            {
                CultMeshStreamBodyTransport.SharedD3D12Texture,
                CultMeshStreamBodyTransport.SharedMemory,
                CultMeshStreamBodyTransport.CultCachePage
            },
            video: new CultMeshVideoStreamFormat(1920, 1080, "rgba8", framesPerSecond: 60),
            maxInFlightFrames: 4);

        catalog.Declare(stream);

        var negotiation = catalog.Negotiate(
            stream.StreamId,
            new CultMeshStreamConsumerProfile(
                "fensalir",
                "mimir-live",
                new[] { CultMeshStreamBodyTransport.SharedD3D12Texture, CultMeshStreamBodyTransport.CultCachePage },
                acceptedKinds: new[] { CultMeshStreamKind.Video },
                canImportGpuHandles: true,
                maxInFlightFrames: 2));

        negotiation.Transport.Should().Be(CultMeshStreamBodyTransport.SharedD3D12Texture);
        negotiation.CopyBudget.Should().Be(CultMeshStreamCopyBudget.ZeroCopyTarget);
        negotiation.MaxInFlightFrames.Should().Be(2);

        var handle = new CultMeshStreamFrameHandle(
            stream.StreamId,
            sequence: 42,
            timestampNs: 123_456_789,
            CultMeshStreamBodyTransport.SharedD3D12Texture,
            nativeHandle: "shared-handle:0xfeedbeef",
            fenceHandle: "fence:0x1234",
            fenceValue: 7);

        catalog.PublishFrame(handle);

        catalog.LatestFrame(stream.StreamId).Should().BeSameAs(handle);
    }

    [Test]
    public void NegotiationRejectsStreamsWithoutACommonTransport()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        catalog.Declare(new CultMeshStreamDescriptor(
            "mimir:kiyo-pro:gpu-only",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Video,
            new CultMeshStreamClock("mimir:clock", "kiyo-pro"),
            new[] { CultMeshStreamBodyTransport.SharedD3D12Texture }));

        var consumer = new CultMeshStreamConsumerProfile(
            "cpu-recorder",
            "mimir-live",
            new[] { CultMeshStreamBodyTransport.SharedMemory },
            acceptedKinds: new[] { CultMeshStreamKind.Video });

        Action act = () => catalog.Negotiate("mimir:kiyo-pro:gpu-only", consumer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Stream and consumer have no compatible body transport.");
    }

    [Test]
    public void SharedMemoryRingPublishesWritableSlotsWithoutInternalCopies()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 2, slotByteLength: 16);

        ring.TryAcquireWriteSlot(out var write).Should().BeTrue();
        ReadOnlySpan<byte> seed = stackalloc byte[] { 1, 2, 3, 4 };
        seed.CopyTo(write.Span);

        var handle = ring.CommitWriteSlot(write, timestampNs: 99, byteLength: 4);
        catalog.PublishFrame(handle);

        ring.TryAcquireLatestRead(out var read).Should().BeTrue();
        using (read)
        {
            read.Handle.Sequence.Should().Be(0);
            read.Handle.UnavoidableCopyCount.Should().Be(0);
            read.Span.ToArray().Should().Equal(1, 2, 3, 4);
        }

        var stats = ring.Stats();
        stats.PublishedFrames.Should().Be(1);
        stats.UnavoidableCopyCount.Should().Be(0);
        catalog.LatestFrame("mimir:leap:depth")!.ResourceKey.Should().Be("mimir:leap:depth:slot:0");
    }

    [Test]
    public void SharedMemoryRingDoesNotOverwriteSlotsHeldByReaders()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 1, slotByteLength: 8);

        ring.TryAcquireWriteSlot(out var firstWrite).Should().BeTrue();
        firstWrite.Span[0] = 11;
        ring.CommitWriteSlot(firstWrite, timestampNs: 1, byteLength: 1);

        ring.TryAcquireLatestRead(out var read).Should().BeTrue();

        ring.TryAcquireWriteSlot(out _).Should().BeFalse();
        ring.Stats().BlockedWrites.Should().Be(1);

        read.Dispose();

        ring.TryAcquireWriteSlot(out var secondWrite).Should().BeTrue();
        secondWrite.Span[0] = 12;
        ring.CommitWriteSlot(secondWrite, timestampNs: 2, byteLength: 1);

        var stats = ring.Stats();
        stats.PublishedFrames.Should().Be(2);
        stats.DroppedFrames.Should().Be(1);
        stats.LatestSequence.Should().Be(1);
    }

    [Test]
    public void CopyPublishMarksFallbackCopiesExplicitly()
    {
        var catalog = CatalogWithByteStream();
        using var ring = catalog.CreateSharedMemoryRing("mimir:leap:depth", slotCount: 2, slotByteLength: 8);

        ring.TryPublishCopy(stackalloc byte[] { 5, 6, 7 }, timestampNs: 10, durationNs: 2, out var handle)
            .Should()
            .BeTrue();

        handle.UnavoidableCopyCount.Should().Be(1);
        ring.Stats().UnavoidableCopyCount.Should().Be(1);
    }

    private static byte[] WriteLegacySingleDocumentSnapshot(
        string key,
        string schemaId,
        string schemaName,
        string schemaVersion,
        string storedAt,
        byte[] payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);

        writer.WriteArrayHeader(3);
        writer.Write("cultcache.store.v1");
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(7);
        writer.Write(schemaId);
        writer.Write(schemaName);
        writer.Write(schemaVersion);
        writer.Write("Legacy Aetheria single document");
        writer.Write(storedAt);
        writer.Write("legacy-hash");
        writer.WriteArrayHeader(0);
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(4);
        writer.Write(key);
        writer.Write(schemaId);
        writer.Write(storedAt);
        writer.Write(payload);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static CultMeshStreamCatalog CatalogWithByteStream()
    {
        var catalog = CultMesh.CreateStreamCatalog();
        catalog.Declare(new CultMeshStreamDescriptor(
            "mimir:leap:depth",
            "mimir-live",
            "starfire",
            CultMeshStreamKind.Tensor,
            new CultMeshStreamClock("mimir:clock", "leap", confidence: 0.8d),
            new[] { CultMeshStreamBodyTransport.SharedMemory, CultMeshStreamBodyTransport.CultCachePage }));
        return catalog;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        predicate().Should().BeTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cultmesh-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [CultDocument("tests.mesh_managed_player", "tests.mesh_managed_player.v1")]
    private sealed class MeshManagedPlayer
    {
        [MessagePack.Key(0)]
        public string Name = string.Empty;

        [MessagePack.Key(1)]
        public float PositionX;

        [MessagePack.Key(2)]
        public int Health;
    }

    [CultDocument("tests.mesh_indexed_player", "tests.mesh_indexed_player.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class MeshIndexedPlayer
    {
        [Key(0)]
        public string Name { get; set; } = string.Empty;

        [Key(1)]
        [CultIndex]
        public string TeamId { get; set; } = string.Empty;

        [Key(2)]
        public int Score { get; set; }
    }

    [CultDocument("tests.mesh_note", "tests.mesh_note.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class MeshNoteDocument
    {
        [Key(0)]
        public string Schema { get; set; } = string.Empty;

        [Key(1)]
        public string Text { get; set; } = string.Empty;

        [Key(2)]
        public int Revision { get; set; }
    }

    [CultDocument("tests.mesh_note", "tests.mesh_note.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class MeshNoteAliasDocument
    {
        [Key(0)]
        public string Schema { get; set; } = string.Empty;

        [Key(1)]
        public string Text { get; set; } = string.Empty;

        [Key(2)]
        public int Revision { get; set; }
    }

    [CultDocument("tests.mesh_publication_note", "tests.mesh_publication_note.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class MeshPublicationNoteDocument
    {
        [Key(0)]
        public string Schema { get; set; } = string.Empty;

        [Key(1)]
        public string Text { get; set; } = string.Empty;

        [Key(2)]
        public int Revision { get; set; }
    }

    [CultDocument("tests.mesh_other", "tests.mesh_other.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class MeshOtherDocument
    {
        [Key(0)]
        public string Schema { get; set; } = string.Empty;

        [Key(1)]
        public string Text { get; set; } = string.Empty;
    }
}

public readonly struct MeshMoveRequest
{
    public MeshMoveRequest(int entityId, float x, float y)
    {
        EntityId = entityId;
        X = x;
        Y = y;
    }

    public int EntityId { get; }
    public float X { get; }
    public float Y { get; }
}

public readonly struct MeshViewportRequest
{
    public MeshViewportRequest(float minX, float maxX)
    {
        MinX = minX;
        MaxX = maxX;
    }

    public float MinX { get; }
    public float MaxX { get; }
}

public readonly struct MeshVec2
{
    public MeshVec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }
}

public sealed class MeshAetheriaDomain
{
    private readonly CultMeshVerseContext _context;

    public MeshAetheriaDomain(CultMeshVerseContext context)
    {
        _context = context;
    }

    public MeshEntityFacade Entity(int entityId)
    {
        return new MeshEntityFacade(_context, entityId);
    }

    public MeshZoneFacade Zone(string zoneId)
    {
        return new MeshZoneFacade(_context, zoneId);
    }
}

public sealed class MeshEntityFacade
{
    public MeshEntityFacade(CultMeshVerseContext context, int entityId)
    {
        Pilot = new MeshPilotFacade(context, entityId);
    }

    public MeshPilotFacade Pilot { get; }
}

public sealed class MeshPilotFacade
{
    private readonly int _entityId;
    private readonly CultMeshBoundOperationHandle<MeshMoveRequest, CultMeshOperationReceipt> _move;

    public MeshPilotFacade(CultMeshVerseContext context, int entityId)
    {
        _entityId = entityId;
        _move = CultMesh.BindOperation(
            context,
            new CultMeshOperationHandle<MeshMoveRequest, CultMeshOperationReceipt>(
            "aetheria.entity.pilot.move",
            (request, context) => Task.FromResult(new CultMeshOperationReceipt(
                "aetheria.entity.pilot.move",
                accepted: request.EntityId == _entityId &&
                          Math.Abs(request.X) > 0 &&
                          context.Claims.Any(claim => claim.Role == "pilot-control"),
                context.RouteHint))));
    }

    public Task<CultMeshOperationReceipt> MoveAsync(MeshVec2 direction, string idempotencyKey)
    {
        return _move.InvokeAsync(
            new MeshMoveRequest(_entityId, direction.X, direction.Y),
            idempotencyKey);
    }
}

public sealed class MeshZoneFacade
{
    public MeshZoneFacade(CultMeshVerseContext context, string zoneId)
    {
        Objects = new MeshObjectsFacade(context, zoneId);
    }

    public MeshObjectsFacade Objects { get; }
}

public sealed class MeshObjectsFacade
{
    private readonly string _zoneId;
    private readonly CultMeshBoundQuerySurface<MeshViewportRequest, string[]> _visibleObjects;

    public MeshObjectsFacade(CultMeshVerseContext context, string zoneId)
    {
        _zoneId = zoneId;
        _visibleObjects = CultMesh.BindQuery(
            context,
            new CultMeshQuerySurface<MeshViewportRequest, string[]>(
            "aetheria.zone.objects.visible",
            (parameters, context) => Task.FromResult(new[]
            {
                $"{context.RuntimeId}:{_zoneId}:{parameters.MinX}:{parameters.MaxX}:{context.RouteHint.Kind}"
            })));
    }

    public Task<string[]> VisibleWithinAsync(MeshViewportRequest request)
    {
        return _visibleObjects.ExecuteAsync(request);
    }
}

internal sealed class MeshSnapshotSchemaClient : ICultNetSchemaClient
{
    private readonly Func<CultNetSnapshotRequestMessage, CultNetSnapshotResponseRawMessage> _respond;
    private readonly List<Action<CultNetSnapshotResponseRawMessage>> _snapshotHandlers = new();
    private readonly List<Action<CultNetErrorMessage>> _errorHandlers = new();

    public MeshSnapshotSchemaClient(
        Func<CultNetSnapshotRequestMessage, CultNetSnapshotResponseRawMessage> respond)
    {
        _respond = respond;
    }

    public bool Connected { get; private set; }

    public void Connect(string host, int port)
    {
        Connected = true;
    }

    public void SendCultNet<T>(T message) where T : ICultNetSchemaMessage
    {
        if (message is not CultNetSnapshotRequestMessage request)
        {
            foreach (var handler in _errorHandlers)
            {
                handler(new CultNetErrorMessage { Error = $"Unexpected message {typeof(T).Name}." });
            }

            return;
        }

        var response = _respond(request);
        foreach (var handler in _snapshotHandlers)
        {
            handler(response);
        }
    }

    public void OnCultNet<T>(Action<T> callback) where T : ICultNetSchemaMessage
    {
        if (typeof(T) == typeof(CultNetSnapshotResponseRawMessage))
        {
            _snapshotHandlers.Add(message => callback((T)(object)message));
        }
        else if (typeof(T) == typeof(CultNetErrorMessage))
        {
            _errorHandlers.Add(message => callback((T)(object)message));
        }
    }

    public void Dispose()
    {
    }
}
