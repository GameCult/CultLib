using System;
using GameCult.Networking;
using NUnit.Framework;

namespace GameCult.Mesh.Tests;

// R-F (docs/cultnet-selection-cut.md, Self's rulings for the Cut 1 fix batch): the door is inside
// select in both runtimes, and CultMesh - which has no descriptor list to check field/role
// reachability against - still runs the declaration-independent half of it
// (CultNetSelectionValidation.ValidateShape) before its own v0 transport refusals, through
// CultMeshSnapshots.EnsureV0Compatible. Keys=[]/[""] is refused there, not silently lowered to
// "every key" the way v0's own cleaning would.
public sealed class CultMeshSnapshotOptionsTests
{
    [Test]
    public void FetchSnapshotAsync_RefusesAnEmptyKeysListBeforeTouchingTheClient()
    {
        var options = new CultMeshSnapshotRequestOptions
        {
            Selection = new CultNetSelection { Keys = Array.Empty<string>() }
        };

        var ex = Assert.ThrowsAsync<CultNetSelectionInvalidException>(() =>
            CultMesh.FetchSnapshotAsync(
                createClient: () => throw new InvalidOperationException(
                    "EnsureV0Compatible must refuse before a client is ever created."),
                endpoint: "mesh-door-test-endpoint",
                options: options));

        Assert.That(ex!.Field, Is.EqualTo("keys"));
    }

    [Test]
    public void FetchSnapshotAsync_RefusesABlankOnlyKeysEntryBeforeTouchingTheClient()
    {
        var options = new CultMeshSnapshotRequestOptions
        {
            Selection = new CultNetSelection { Keys = new[] { "   " } }
        };

        var ex = Assert.ThrowsAsync<CultNetSelectionInvalidException>(() =>
            CultMesh.FetchSnapshotAsync(
                createClient: () => throw new InvalidOperationException(
                    "EnsureV0Compatible must refuse before a client is ever created."),
                endpoint: "mesh-door-test-endpoint",
                options: options));

        Assert.That(ex!.Field, Is.EqualTo("keys"));
    }
}
