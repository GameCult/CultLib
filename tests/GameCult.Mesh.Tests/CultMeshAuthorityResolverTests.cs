#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshAuthorityResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Resolve_AcceptsSignedLeaseWithExactScopeAndEpoch()
    {
        var fixture = CreateFixture();
        var decision = fixture.Resolver.Resolve(fixture.Request());

        decision.IsAuthorized.Should().BeTrue();
        decision.DenialReason.Should().Be(CultMeshAuthorityDenialReason.None);
        decision.LeaseId.Should().Be("lease:pilot");
        decision.IssuerRuntimeId.Should().Be("odin");
    }

    [TestCase(null, CultMeshAuthorityDenialReason.MissingSignature)]
    [TestCase("bad-signature", CultMeshAuthorityDenialReason.InvalidSignature)]
    public void Resolve_RejectsMissingOrInvalidSignature(string? signature, CultMeshAuthorityDenialReason expected)
    {
        var fixture = CreateFixture(signature: signature);
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(expected);
    }

    [Test]
    public void Resolve_RejectsLegacyUnsignedLeaseInsteadOfTreatingItAsEpochZero()
    {
        var catalog = new CultMeshAuthorityLeaseCatalog();
        var lease = new CultMeshAuthorityLease("legacy", "verse", "peer", new[] { "pilot" }, null, "odin", Now.AddMinutes(-1), Now.AddMinutes(1));
        catalog.Upsert(lease);
        var peer = new CultMeshPeerCard("peer", "verse", Array.Empty<string>(), new[] { "pilot" }, authorityLeaseId: "legacy");
        var resolver = new CultMeshAuthorityResolver(catalog, new SignatureVerifier(), new Revocations(), new ManualClock(Now));

        resolver.Resolve(new CultMeshAuthorityRequest(peer, "pilot", null, 0)).DenialReason
            .Should().Be(CultMeshAuthorityDenialReason.UnsupportedLeaseVersion);
#pragma warning disable CS0618
        catalog.IsAuthorized(peer, "pilot", at: Now).Should().BeFalse();
#pragma warning restore CS0618
    }

    [Test]
    public void Resolve_UsesInjectedClockForNotYetValidAndExpiry()
    {
        var fixture = CreateFixture(validFrom: Now.AddMinutes(1), expiresAt: Now.AddMinutes(2));
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.NotYetValid);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Resolver.Resolve(fixture.Request()).IsAuthorized.Should().BeTrue();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.Expired);
    }

    [Test]
    public void Resolve_RejectsRevokedLeaseAndCatalogReplacementCannotReviveIt()
    {
        var fixture = CreateFixture(revoked: true);
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.Revoked);

        fixture.Catalog.Upsert(CreateLease(signature: "valid-replacement"));
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.Revoked);
    }

    [Test]
    public void Resolve_CatalogReplacementCannotOverrideEpochPolicy()
    {
        var fixture = CreateFixture();
        fixture.Catalog.Upsert(new CultMeshAuthorityLease(
            "lease:pilot", "verse:aetheria", "peer:pilot", new[] { "pilot" }, new[] { "shard:a" }, "odin",
            Now.AddMinutes(-1), Now.AddMinutes(1), "valid-new-epoch", 8, new[] { "body:world" }));

        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.EpochMismatch);
    }

    [Test]
    public void Resolve_ClockMovementExpiresLeaseWithoutCatalogMutation()
    {
        var fixture = CreateFixture(expiresAt: Now.AddSeconds(1));
        fixture.Resolver.Resolve(fixture.Request()).IsAuthorized.Should().BeTrue();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Resolver.Resolve(fixture.Request()).DenialReason.Should().Be(CultMeshAuthorityDenialReason.Expired);
    }

    [Test]
    public void Resolve_ReportsDistinctScopeDenials()
    {
        AssertDenied(CreateFixture(peerVerse: "other").Request(), CreateFixture(peerVerse: "other").Resolver, CultMeshAuthorityDenialReason.VerseMismatch);
        AssertDenied(CreateFixture(peerId: "other").Request(), CreateFixture(peerId: "other").Resolver, CultMeshAuthorityDenialReason.PeerMismatch);
        AssertDenied(CreateFixture(requestRole: "trade").Request(), CreateFixture(requestRole: "trade").Resolver, CultMeshAuthorityDenialReason.RoleNotGranted);
        AssertDenied(CreateFixture(requestShard: "shard:b").Request(), CreateFixture(requestShard: "shard:b").Resolver, CultMeshAuthorityDenialReason.ShardNotGranted);
        AssertDenied(CreateFixture(requestEpoch: 8).Request(), CreateFixture(requestEpoch: 8).Resolver, CultMeshAuthorityDenialReason.EpochMismatch);
        AssertDenied(CreateFixture(requestScope: "body:other").Request(), CreateFixture(requestScope: "body:other").Resolver, CultMeshAuthorityDenialReason.ResourceScopeNotGranted);
    }

    [Test]
    public void Resolve_NewPeerPublicationCannotReviveExpiredAuthority()
    {
        var fixture = CreateFixture(expiresAt: Now);
        fixture.CatalogPeers.Upsert(fixture.Peer);
        fixture.CatalogPeers.FindAuthorized("verse:aetheria", "pilot", fixture.Resolver, 7, "shard:a", "body:world").Should().BeEmpty();

        fixture.CatalogPeers.Upsert(new CultMeshPeerCard("peer:pilot", "verse:aetheria", Array.Empty<string>(), new[] { "pilot" }, new[] { "shard:a" }, authorityLeaseId: "lease:pilot"));
        fixture.CatalogPeers.FindAuthorized("verse:aetheria", "pilot", fixture.Resolver, 7, "shard:a", "body:world").Should().BeEmpty();
    }

    [Test]
    public void Resolve_NewPeerPublicationCannotReviveRevokedAuthority()
    {
        var fixture = CreateFixture(revoked: true);
        fixture.CatalogPeers.Upsert(fixture.Peer);
        fixture.CatalogPeers.FindAuthorized("verse:aetheria", "pilot", fixture.Resolver, 7, "shard:a", "body:world").Should().BeEmpty();

        fixture.CatalogPeers.Upsert(new CultMeshPeerCard("peer:pilot", "verse:aetheria", Array.Empty<string>(), new[] { "pilot" }, new[] { "shard:a" }, authorityLeaseId: "lease:pilot"));
        fixture.CatalogPeers.FindAuthorized("verse:aetheria", "pilot", fixture.Resolver, 7, "shard:a", "body:world").Should().BeEmpty();
    }

    [Test]
    public void Diagnostics_CarryEvidenceIdentifiersButNeverSignatureMaterial()
    {
        var fixture = CreateFixture(signature: "super-secret-signature");
        fixture.Resolver.Resolve(fixture.Request());

        var diagnostic = fixture.Diagnostics.Snapshot().Should().ContainSingle().Subject;
        diagnostic.OperationId.Should().Be("lease:pilot");
        diagnostic.SubjectId.Should().Be("peer:pilot");
        diagnostic.SourceId.Should().Be("odin");
        string.Join("|", diagnostic.OperationId, diagnostic.SubjectId, diagnostic.SourceId, diagnostic.ReasonCode, diagnostic.Endpoint)
            .Should().NotContain("super-secret-signature");
    }

    [Test]
    public void PeerSelectionAndPrivilegedRudpHelpersUseResolverDecision()
    {
        var fixture = CreateFixture();
        fixture.CatalogPeers.Upsert(fixture.Peer);
        fixture.CatalogPeers.FirstAuthorized("verse:aetheria", "pilot", fixture.Resolver, 7, "shard:a", "body:world")
            .Should().BeSameAs(fixture.Peer);

#pragma warning disable CS0618
        fixture.CatalogPeers.FirstAuthorized("verse:aetheria", "pilot", fixture.Catalog, "shard:a", Now).Should().BeNull();
        Action legacy = () => CultMesh.CreateRudpClientForAuthorizedPeer("client", 1, fixture.CatalogPeers, fixture.Catalog, "verse:aetheria", "pilot", "shard:a", Now);
#pragma warning restore CS0618
        legacy.Should().Throw<InvalidOperationException>().WithMessage("No authorized RUDP peer*");

        Action resolved = () => CultMesh.CreateRudpClientForAuthorizedPeer("client", 1, fixture.CatalogPeers, fixture.Resolver, 7, "verse:aetheria", "pilot", "shard:a", "body:world");
        resolved.Should().Throw<InvalidOperationException>().WithMessage("Peer peer:pilot does not advertise a RUDP endpoint.");
    }

    private static void AssertDenied(CultMeshAuthorityRequest request, CultMeshAuthorityResolver resolver, CultMeshAuthorityDenialReason reason) =>
        resolver.Resolve(request).DenialReason.Should().Be(reason);

    private static Fixture CreateFixture(
        string? signature = "valid-signature", DateTimeOffset? validFrom = null, DateTimeOffset? expiresAt = null,
        bool revoked = false, string peerId = "peer:pilot", string peerVerse = "verse:aetheria",
        string requestRole = "pilot", string requestShard = "shard:a", long requestEpoch = 7, string requestScope = "body:world")
    {
        var catalog = new CultMeshAuthorityLeaseCatalog();
        catalog.Upsert(CreateLease(signature, validFrom, expiresAt));
        var clock = new ManualClock(Now);
        var revocations = new Revocations(revoked ? new[] { ("lease:pilot", 7L) } : null);
        var diagnostics = new CultMeshDiagnosticBuffer();
        var resolver = new CultMeshAuthorityResolver(catalog, new SignatureVerifier(), revocations, clock, diagnostics);
        var peer = new CultMeshPeerCard(peerId, peerVerse, Array.Empty<string>(), new[] { "pilot" }, new[] { "shard:a" }, authorityLeaseId: "lease:pilot");
        return new Fixture(catalog, new CultMeshPeerCatalog(), clock, diagnostics, resolver, peer, requestRole, requestShard, requestEpoch, requestScope);
    }

    private static CultMeshAuthorityLease CreateLease(string? signature = "valid-signature", DateTimeOffset? validFrom = null, DateTimeOffset? expiresAt = null) =>
        new("lease:pilot", "verse:aetheria", "peer:pilot", new[] { "pilot" }, new[] { "shard:a" }, "odin",
            validFrom ?? Now.AddMinutes(-1), expiresAt ?? Now.AddMinutes(1), signature, 7, new[] { "body:world" });

    private sealed record Fixture(
        CultMeshAuthorityLeaseCatalog Catalog, CultMeshPeerCatalog CatalogPeers, ManualClock Clock,
        CultMeshDiagnosticBuffer Diagnostics, CultMeshAuthorityResolver Resolver, CultMeshPeerCard Peer,
        string Role, string Shard, long Epoch, string Scope)
    {
        public CultMeshAuthorityRequest Request() => new(Peer, Role, Shard, Epoch, Scope);
    }

    private sealed class SignatureVerifier : ICultMeshAuthoritySignatureVerifier
    {
        public bool Verify(CultMeshAuthorityLease lease) => lease.Signature?.StartsWith("valid", StringComparison.Ordinal) == true;
    }

    private sealed class Revocations : ICultMeshAuthorityRevocationSource
    {
        private readonly HashSet<(string, long)> _revoked;
        public Revocations(IEnumerable<(string, long)>? revoked = null) => _revoked = revoked?.ToHashSet() ?? new();
        public bool IsRevoked(string leaseId, long authorityEpoch) => _revoked.Contains((leaseId, authorityEpoch));
    }

    private sealed class ManualClock : ICultMeshClock
    {
        public ManualClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; private set; }
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) { Advance(delay); return Task.CompletedTask; }
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
}
