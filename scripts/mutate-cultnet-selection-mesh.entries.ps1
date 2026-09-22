# Entries for scripts/mutate-dotnet.ps1: CultNet typed selection, Cut 1, commit 2 fix batch
# (docs/cultnet-selection-cut.md, sections 4/7/11). Targets: src/GameCult.Mesh/CultMesh.cs
# (ReadDocumentFromSnapshotResponse's one exact read) and src/GameCult.Mesh/CultMeshSnapshots.cs
# (ResolveDefaultSelection's "no schema filter when keys are given" rule). Test project:
# tests/GameCult.Mesh.Tests.
#
# Every entry below carries its own File, so this runs as one invocation with both -Target paths.
#
# There are two loosening entries here (MESH-DEFAULT-Loosening and MESH-KEY-Loosening); the header
# above this line previously miscounted them as "two of three" (S2-9/S2-10). Both were genuine
# weaker readings of the rule they sit beside and both SURVIVED against the pre-fix-batch fixtures,
# because nothing in tests/GameCult.Mesh.Tests exercised an explicit empty (non-null) recordKeys
# array or a record-key case variation. CultMeshStreamingTests.cs now carries
# FetchDocumentsAsync_WithExplicitEmptyRecordKeys_StillDefaultsToOwnSchema (MESH-DEFAULT) and the
# case-variant-key block appended to DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments
# (MESH-KEY); both mutants below now die.

@(
    @{
        Id     = 'MESH-DEFAULT-Revert'
        Rule   = 'ResolveDefaultSelection: an explicit non-empty recordKeys with no schemaIds means no schema filter, not the document type''s own schema.'
        Mutant = 'revert'
        File   = 'src/GameCult.Mesh/CultMeshSnapshots.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.SnapshotEndpoint_ProvidesTypedHandlesAndSchemaAliases'
        Old    = 'var cleanedKeys = Clean(recordKeys);
            var schemas = schemaIds != null
                ? Clean(schemaIds)
                : (cleanedKeys != null ? null : new[] { descriptor.SchemaId });'
        New    = 'var cleanedKeys = Clean(recordKeys);
            var schemas = schemaIds != null ? Clean(schemaIds) : new[] { descriptor.SchemaId };'
    },
    @{
        Id     = 'MESH-DEFAULT-Loosening'
        Rule   = 'ResolveDefaultSelection: the no-schema-filter branch triggers on a non-empty (post-cleaning) recordKeys, not merely a non-null one - an explicit empty recordKeys array still defaults to the document type''s own schema (docs/cultnet-selection-cut.md, S2-7/rule 1''s v0 lowering).'
        Mutant = 'loosening'
        File   = 'src/GameCult.Mesh/CultMeshSnapshots.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.FetchDocumentsAsync_WithExplicitEmptyRecordKeys_StillDefaultsToOwnSchema'
        Old    = 'cleanedKeys != null ? null : new[] { descriptor.SchemaId }'
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
        Rule   = 'ReadDocumentFromSnapshotResponse: a foreign/runtime-generated schema id at the right recordKey still matches when its payload''s embedded schema (read by the registry''s shared TryReadSchemaVersion) alias-matches the caller''s own descriptor.'
        Mutant = 'revert'
        File   = 'src/GameCult.Mesh/CultMesh.cs'
        Killer = 'FullyQualifiedName=GameCult.Mesh.Tests.CultMeshStreamingTests.DocumentHandle_ReadsRemotePeerSnapshotsAsTypedDocuments'
        Old    = 'else if (byPayload == null &&
                         CultNetDocumentRegistry.TryReadSchemaVersion(candidate.Payload) is { } payloadSchemaVersion &&
                         CultNetSchemaAliasMatching.Matches(payloadSchemaVersion, descriptor))
                    byPayload = candidate;'
        New    = ''
    }
)
