using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

public sealed class CultMeshReliabilityDiagnosticsTests
{
    [Test]
    public void DiagnosticBuffer_IsBoundedAndCarriesServedVersion()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero));
        var diagnostics = new CultMeshDiagnosticBuffer(capacity: 2);

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            diagnostics.Emit(new CultMeshDiagnosticEvent(
                sequence,
                clock.UtcNow,
                CultMeshReliabilityOrgan.Discovery,
                CultMeshDiagnosticKind.DiscoveryObservation,
                "lookup:aetheria",
                "aetheria",
                sequence == 3 ? "degraded" : "fresh",
                schemaVersion: "gamecult.mesh.discovery_diagnostic.v1"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var snapshot = diagnostics.Snapshot();
        snapshot.Select(item => item.Sequence).Should().Equal(2, 3);
        snapshot[^1].State.Should().Be("degraded");
        snapshot[^1].LibraryVersion.Should().Be(CultMeshLibraryVersion.Current);
        snapshot[^1].LibraryVersion.Should().NotBeNullOrWhiteSpace();
        snapshot[^1].SchemaVersion.Should().Be("gamecult.mesh.discovery_diagnostic.v1");
    }

    [Test]
    public void DeterministicNetwork_ReplaysLossDuplicationReorderingLatencyAndCorruption()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero));
        var network = new CultMeshDeterministicNetwork(
            () => clock.UtcNow,
            "rudp://first:3076",
            new[]
            {
                new CultMeshFault(1, CultMeshFaultKind.Drop),
                new CultMeshFault(2, CultMeshFaultKind.Duplicate),
                new CultMeshFault(2, CultMeshFaultKind.Delay, TimeSpan.FromSeconds(2)),
                new CultMeshFault(3, CultMeshFaultKind.Reorder),
                new CultMeshFault(3, CultMeshFaultKind.Corrupt)
            });

        network.Send(Encoding.UTF8.GetBytes("lost"));
        network.Send(Encoding.UTF8.GetBytes("twice"));
        network.Send(new byte[] { 0x01, 0x02 });

        var immediate = network.ReceiveAvailable();
        immediate.Should().ContainSingle();
        immediate[0].SendOrdinal.Should().Be(3);
        immediate[0].Payload.Should().Equal(0xfe, 0x02);

        clock.Advance(TimeSpan.FromSeconds(2));
        var delayed = network.ReceiveAvailable();
        delayed.Should().HaveCount(2);
        delayed.Select(packet => Encoding.UTF8.GetString(packet.Payload)).Should().OnlyContain(value => value == "twice");
    }

    [Test]
    public void DeterministicNetwork_ReplaysPartitionEndpointRotationAndRestart()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 7, 12, 18, 0, 0, TimeSpan.Zero));
        var network = new CultMeshDeterministicNetwork(
            () => clock.UtcNow,
            "rudp://first:3076",
            new[]
            {
                new CultMeshFault(1, CultMeshFaultKind.Partition),
                new CultMeshFault(2, CultMeshFaultKind.Partition),
                new CultMeshFault(2, CultMeshFaultKind.RotateEndpoint, Endpoint: "rudp://second:3076"),
                new CultMeshFault(3, CultMeshFaultKind.Restart)
            });

        network.Send(new byte[] { 1 });
        network.Send(new byte[] { 2 });
        network.ReceiveAvailable().Should().ContainSingle().Which.Endpoint.Should().Be("rudp://second:3076");

        network.Send(new byte[] { 3 });
        network.RestartCount.Should().Be(1);
        network.Endpoint.Should().Be("rudp://second:3076");
        network.ReceiveAvailable().Should().ContainSingle().Which.Payload.Should().Equal(3);
    }

    private sealed class TestClock : ICultMeshClock
    {
        public TestClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Advance(delay);
            return Task.CompletedTask;
        }

        public void Advance(TimeSpan duration)
        {
            UtcNow += duration;
        }
    }
}
