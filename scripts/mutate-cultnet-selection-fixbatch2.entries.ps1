# Entries for scripts/mutate-dotnet.ps1: CultNet typed selection, Cut 1, commit 2 fix batch
# (docs/cultnet-selection-cut.md, Self's rulings 2026-09-22). Targets the dead-identity-code deletion
# in src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs (S2-2): Reconcile must still walk
# its diff when the request stays authorized (not gated on DemandActive changing), and its add loop
# must still fire. Test project: tests/GameCult.Networking.Tests.

@(
    @{
        Id     = 'NET-RECONCILE-EarlyReturn-Loosening'
        Rule   = 'Reconcile walks the delivered/added diff whenever it runs; it must not early-return merely because request-level authorization (and so DemandActive) has not changed - per-record authorization can still have moved.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.DatabaseSubscriptionServer_Reconcile_FlipsPerRecordAuthorizationWithoutChangingRequestAuthority'
        Old    = 'if (!_projections.TryGetValue(key, out var projection)) return;
            var authorized = _authorizeRequest?.Invoke(request, key.Peer) != false;'
        New    = 'if (!_projections.TryGetValue(key, out var projection)) return;
            var authorized = _authorizeRequest?.Invoke(request, key.Peer) != false;
            if (projection.DemandActive == authorized) return;'
    },
    @{
        Id     = 'NET-RECONCILE-AddLoop-Revert'
        Rule   = 'Reconcile''s add loop sends an upsert (added: true) for every key in the new snapshot that was not already delivered.'
        Mutant = 'revert'
        File   = 'src/GameCult.Networking/CultNetDatabaseSubscriptionServer.cs'
        Killer = 'FullyQualifiedName=GameCult.Networking.Tests.NetworkingTests.DatabaseSubscriptionServer_Reconcile_FlipsPerRecordAuthorizationWithoutChangingRequestAuthority'
        Old    = 'foreach (var current in next.BySourceRecordKey)
            {
                if (!projection.DeliveredBySourceRecordKey.ContainsKey(current.Key))
                    SendUpsert(key.Peer, key.Id, current.Value, added: true);
            }'
        New    = ''
    }
)
