namespace GameCult.Mesh.Tests;

internal static class CultMeshTestTrust
{
    internal static CultMeshAuthorityTrustPolicy LocalPolicy { get; } =
        new(CultMeshAuthorityTrustMode.LocalDevelopment);

    internal static CultMeshSessionManagerOptions LocalSessions => new()
    {
        Trust = LocalPolicy
    };

    internal static CultMeshSessionManagerOptions LocalSessionsWithClock(ICultMeshClock clock) => new()
    {
        Clock = clock,
        Trust = LocalPolicy
    };
}
