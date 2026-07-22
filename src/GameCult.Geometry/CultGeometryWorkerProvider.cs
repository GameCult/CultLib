using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Mesh;
using GameCult.Networking;

namespace GameCult.Geometry
{
    public interface ICultGeometryBuildPipeline
    {
        Task<CultGeometryBuildOutput> BuildAsync(
            CultGeometryDomainDocument domain,
            CultGeometryBuildRequest request);
    }

    /// <summary>Owns geometry worker commands and the authoritative output commit path.</summary>
    public sealed class CultGeometryWorkerProvider
    {
        public const string Owner = "GameCult.Geometry";
        public const string BuildOperationId = "gamecult.geometry.worker.build";

        private readonly string _workerId;
        private readonly CultNetDatabase _database;
        private readonly ICultGeometryBuildPipeline _pipeline;

        public CultGeometryWorkerProvider(
            string workerId,
            CultNetDatabase database,
            ICultGeometryBuildPipeline pipeline)
        {
            _workerId = string.IsNullOrWhiteSpace(workerId)
                ? throw new ArgumentException("Value must be non-empty.", nameof(workerId))
                : workerId;
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            BuildOperation = new CultMeshOperationHandle<CultGeometryBuildCommand, CultGeometryBuildReceipt>(
                BuildOperationId,
                (command, _) => BuildAsync(command));
        }

        public CultMeshOperationHandle<CultGeometryBuildCommand, CultGeometryBuildReceipt> BuildOperation { get; }

        public CultRecordKey WorkerStateKey => CultGeometryWorkerState.CreateRecordKey(_workerId);

        public async Task<CultGeometryBuildReceipt> BuildAsync(CultGeometryBuildCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.RequestKey))
                throw new ArgumentException("Request key must be non-empty.", nameof(command));

            var requestKey = new CultRecordKey(command.RequestKey);
            var request = await RequireAsync<CultGeometryBuildRequest>(requestKey).ConfigureAwait(false);
            var domain = await RequireAsync<CultGeometryDomainDocument>(new CultRecordKey(request.DomainKey)).ConfigureAwait(false);
            var output = await _pipeline.BuildAsync(domain, request).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Geometry build pipeline returned no output.");

            output.SelectedCut.RequestKey = requestKey.Value;
            var cutKey = CultGeometrySelectedCutManifest.CreateRecordKey(output.SelectedCut);
            await _database.PutAsync(cutKey, output.SelectedCut).ConfigureAwait(false);

            var artifactKeys = new string[output.Artifacts.Length];
            for (var index = 0; index < output.Artifacts.Length; index++)
            {
                var artifact = output.Artifacts[index];
                artifact.CutKey = cutKey.Value;
                artifact.SelectedCutId = output.SelectedCut.CutId;
                var artifactKey = CultGeometryChunkArtifact.CreateRecordKey(artifact);
                await _database.PutAsync(artifactKey, artifact).ConfigureAwait(false);
                artifactKeys[index] = artifactKey.Value;
            }

            var receipt = new CultGeometryBuildReceipt
            {
                RequestKey = requestKey.Value,
                SelectedCutKey = cutKey.Value,
                ArtifactKeys = artifactKeys,
                ContentHashes = artifactKeys.Select(ContentHashFromKey).ToArray()
            };
            await _database.PutAsync(WorkerStateKey, new CultGeometryWorkerState
            {
                WorkerId = _workerId,
                Phase = "completed",
                ActiveRequestKey = requestKey.Value,
                LastSelectedCutKey = cutKey.Value,
                LastArtifactKeys = artifactKeys,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                ServedPackageVersion = PackageVersion
            }).ConfigureAwait(false);
            return receipt;
        }

        public async Task<CultGeometryDevelopmentProbe> ProbeAsync()
        {
            var state = await RequireAsync<CultGeometryWorkerState>(WorkerStateKey).ConfigureAwait(false);
            var cut = await RequireAsync<CultGeometrySelectedCutManifest>(new CultRecordKey(state.LastSelectedCutKey)).ConfigureAwait(false);
            return new CultGeometryDevelopmentProbe
            {
                Owner = Owner,
                SchemaVersion = CultGeometrySchemaVersions.SelectedCut,
                SourceRecordKey = state.ActiveRequestKey,
                SelectedCutKey = state.LastSelectedCutKey,
                SelectedNodes = cut.SelectedNodes,
                ArtifactKeys = state.LastArtifactKeys,
                ContentHashes = state.LastArtifactKeys.Select(ContentHashFromKey).ToArray(),
                ServedPackageVersion = PackageVersion
            };
        }

        private async Task<T> RequireAsync<T>(CultRecordKey key) where T : class =>
            await _database.GetAsync<T>(key).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Required geometry document '{key.Value}' was not found.");

        private static string ContentHashFromKey(string key) =>
            key[(key.LastIndexOf(':') + 1)..];

        private static string PackageVersion =>
            typeof(CultGeometryWorkerProvider).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CultGeometryWorkerProvider).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
