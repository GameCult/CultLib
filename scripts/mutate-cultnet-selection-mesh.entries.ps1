# Entries for scripts/mutate-dotnet.ps1: CultNet typed selection, Cut 1, commit 2d - the CultMesh
# collapse (docs/cultnet-selection-cut.md, sections 4/7/11). Targets: src/GameCult.Mesh/CultMesh.cs
# (ReadDocumentFromSnapshotResponse's one exact read) and src/GameCult.Mesh/CultMeshSnapshots.cs
# (ResolveDefaultSelection's "no schema filter when keys are given" rule). Test project:
# tests/GameCult.Mesh.Tests.
#
# Every entry below carries its own File, so this runs as one invocation with both -Target paths.
#
# Two of the three loosening entries here SURVIVED against the current fixtures (recorded, not
# swapped for an easier mutant): MESH-DEFAULT-Loosening and MESH-KEY-Loosening. Both are genuine
# weaker readings of the rule they sit beside; no test in tests/GameCult.Mesh.Tests exercises an
# explicit empty (non-null) recordKeys array or a record-key case variation, so nothing currently
# distinguishes the loosened behavior from the correct one. That is a fixture gap in
# CultMeshStreamingTests.cs, not evidence the mutant is unreachable in principle.

@(
    @{
        Id     = 'MESH-DEFAULT-Revert'
        Rule   = 'ResolveDefaultSelection: an explicit non-empty recordKeys with no schemaIds means no schema filter, not the document type''s own schema.'
        Mutant = 'revert'
        File   = 'src/GameCult.Mesh/CultMeshSnapshots.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.SnapshotEndpoint_ProvidesTypedHandlesAndSchemaAliases'
        Old    = 'var schemas = schemaIds != null
                ? Clean(schemaIds)
                : (recordKeys is { Count: > 0 } ? null : new[] { descriptor.SchemaId });'
        New    = 'var schemas = schemaIds != null ? Clean(schemaIds) : new[] { descriptor.SchemaId };'
    },
    @{
        Id     = 'MESH-DEFAULT-Loosening'
        Rule   = 'ResolveDefaultSelection: the no-schema-filter branch triggers on a non-empty recordKeys, not merely a non-null one.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Mesh/CultMeshSnapshots.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.SnapshotEndpoint_ProvidesTypedHandlesAndSchemaAliases'
        Old    = 'recordKeys is { Count: > 0 } ? null : new[] { descriptor.SchemaId }'
        New    = 'recordKeys != null ? null : new[] { descriptor.SchemaId }'
    },
    @{
        Id     = 'MESH-KEY-Revert'
        Rule   = 'ReadDocumentFromSnapshotResponse: a candidate must name this recordKey - the one exact read never returns a record under the wrong key.'
        Mutant = 'revert'
        File   = 'src/GameCult.Mesh/CultMesh.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments'
        Old    = 'if (!string.Equals(candidate.RecordKey, recordKey, StringComparison.Ordinal))
                    continue;'
        New    = ''
    },
    @{
        Id     = 'MESH-KEY-Loosening'
        Rule   = 'ReadDocumentFromSnapshotResponse: the recordKey match is exact (ordinal), not case-insensitive.'
        Mutant = 'loosening'
        File   = 'src/GameCult.Mesh/CultMesh.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments'
        Old    = 'if (!string.Equals(candidate.RecordKey, recordKey, StringComparison.Ordinal))'
        New    = 'if (!string.Equals(candidate.RecordKey, recordKey, StringComparison.OrdinalIgnoreCase))'
    },
    @{
        Id     = 'MESH-FOREIGN-Revert'
        Rule   = 'ReadDocumentFromSnapshotResponse: a foreign/runtime-generated schema id at the right recordKey still matches when its payload decodes as TDocument.'
        Mutant = 'revert'
        File   = 'src/GameCult.Mesh/CultMesh.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments'
        Old    = 'if (string.Equals(candidate.SchemaId, schemaId, StringComparison.Ordinal) ||
                    CultNetSchemaAliasMatching.Matches(candidate.SchemaId, descriptor) ||
                    TryDecodeAsMessagePack<TDocument>(candidate, out _))'
        New    = 'if (string.Equals(candidate.SchemaId, schemaId, StringComparison.Ordinal) ||
                    CultNetSchemaAliasMatching.Matches(candidate.SchemaId, descriptor))'
    }
)
