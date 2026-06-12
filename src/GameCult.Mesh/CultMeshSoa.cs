using System;
using System.Threading.Tasks;
using GameCult.Caching;
using GameCult.Networking;

namespace GameCult.Mesh
{
    /// <summary>
    /// Gameplay-facing helper for committing CultCache SoA chunks through CultMesh.
    /// </summary>
    public sealed class CultMeshSoaStore
    {
        private readonly CultNetDatabase _database;

        internal CultMeshSoaStore(CultNetDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Adds or replaces one SoA chunk at a stable mesh record key.
        /// </summary>
        public Task<CultRecordHandle<CultSoaChunkDocument>> PutChunkAsync(CultRecordKey key, CultSoaChunk chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            return _database.PutAsync(key, chunk.Document);
        }

        /// <summary>
        /// Gets one SoA chunk by record key.
        /// </summary>
        public async Task<CultSoaChunk?> GetChunkAsync(CultRecordKey key)
        {
            var document = await _database.GetAsync<CultSoaChunkDocument>(key).ConfigureAwait(false);
            return document == null ? null : CultSoaChunk.Wrap(document);
        }
    }
}
